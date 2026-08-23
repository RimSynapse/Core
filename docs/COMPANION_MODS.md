# RimSynapse — Companion Mod Builder Guide

> **Purpose:** the reference for building companion mods on RimSynapse Core's agent and tool foundation (the 0.6 surface).
> This document mirrors the in-game wiki page **Agent Scripting and Tools** (`Learning/Agent_Scripting_and_Tools.md`, injected into RimWorld's Learning Helper) — keep the two in sync when editing either.

---

## The ecosystem

```
Core                      ← LLM client, request queue, tool registry, script runner,
│                           agent loop, capability tiers, context/event tracking
│
├── Psychology            ← pawn personality, weighted memories, evaluations
│   └── Conversations     ← in-game dialogue UI (requires Core + Psychology)
│
├── Factions              ← faction motivations, diplomacy, population density, map modes
├── WorldNews             ← planetary news feed and world events
├── Local-Text-to-Speech  ← on-device speech synthesis (registers the TextToSpeech provider)
└── NVIDIA-Tool           ← GPU/VRAM stats feeding Core's hardware awareness
```

Storyteller mechanisms are embedded in Core — the separate RimSynapse-StoryTeller repo is deprecated. In-game test cases live in each repo's `Source.Tests/`, run by the dev-tools toolkit's bridge mod under `-synapse-test`; the build/launch/log harness is the `rimworld-claude-dev-tools` repo. From 0.10 no repo commits built DLLs — every release ships an installable zip asset, with `Assemblies/CHECKSUMS.sha256` as the tracked record.

---

## 1. Where the agent sits

The agent is the **exception path, not the mainline**. The primary pipeline is programmed: your mod intercepts game functions with Harmony hooks, assembles curated context for that specific situation, and prompts the LLM for a specific outcome. Prompts stay small *by construction* because you chose what matters.

The agent, and its tool discovery, exist for the rest:

- **Situations nothing was programmed for** — the Direct Action Console is the manual form of this.
- **Escalation** — when your programmed path hits an outcome it does not know how to handle, hand what you know to the agent instead of logging a warning and dropping the interaction (see section 7).

Design your features as hooks first. Reach for the agent when reality diverges.

---

## 2. Registering game tools

`SynapseToolRegistry` is the shared directory of everything the LLM can do in the game. Register during startup (your Mod constructor or a `[StaticConstructorOnStartup]`):

```csharp
SynapseToolRegistry.RegisterTool(
    "get_herd_status",
    "Returns the colony's animal herds: species, counts, health and training.",
    new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["species"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Optional species filter, e.g. 'Muffalo'."
            }
        }
    },
    argsJson => BuildHerdReportJson(argsJson),
    isDebug: false,
    keywords: new List<string> { "animals", "herd", "livestock", "pets" });

// Anything that CHANGES game state uses the overload with the isMutating flag:
SynapseToolRegistry.RegisterTool(
    "cull_herd", "...", schema, handler,
    isDebug: false, keywords: null, isMutating: true);
```

Details that matter:

- **name** — snake_case, prefixed by intent: `get_` / `search_` / `list_` for read-only queries. Read-only prefixes make your tool eligible for pre-seeding (section 8), which can save the agent a whole turn.
- **parametersSchema** — a JSON-Schema-shaped object (`type: "object"` with a `properties` dictionary). It is shown to the model by `describe_tool` and used to *warn* about undeclared script arguments, so keep it accurate.
- **handler** — takes the arguments as a JSON string, returns a JSON string. The contract:
  - Return valid JSON even for empty arguments.
  - **Never throw.** Report failure as an error payload: `{"error": "reason"}`. `ExecuteTool` never throws either — callers rely on that.
  - The whole-registry test suite executes every non-debug tool with empty and malformed arguments; your handler must survive both.
- **keywords** — feed the tool search index. The agent's first prompt only names the tools a request is *likely* to need (chosen by index search); everything else is found by searching from within the conversation (`list_available_tools`, `describe_tool`, `execute_game_tool`). Good keywords are how your tool gets found.
- **isMutating** — flag anything that changes game state. Autonomous runs (escalations, background evaluations) are barred from mutating tools unless the player enables *Allow autonomous mutations* in settings. Player-initiated runs may always mutate. An unflagged mutator is a hole in that gate.

Also available: `IsToolRegistered(name)`, `ExecuteTool(name, argsJson)` and `ExecuteTool(name, argsJson, allowMutating)`.

> **Binary compatibility warning**: your DLL binds to Core's *exact* method signatures. Core never alters an existing public signature (new capability arrives as new overloads — `RegisterTool`, `StartScript` and `ExecuteTool` all follow this pattern), and your mod should follow the same rule for any API it exposes. Appending an optional parameter to an existing public method silently removes the old signature from the assembly and breaks every DLL bound to it — this has happened; the `Core_AllModsInstantiated` sentinel test exists because of it.

---

## 3. The script step schema

The model drives multi-step work by emitting a script: a `scriptName` plus a list of `steps`. Each step is `{ "type": ..., "arguments": {...} }`. The schema is declared in `SynapseScriptValidator` and published to the model automatically — companion mods only need to know the semantics:

- **A step type is a tool name.** Any registered tool can be a step; its arguments follow the tool's schema. Unknown tool names are reported and skipped at execution.
- **call_tool** — runs a tool named in its arguments: `tool`, `arguments`, optional `resultKey`. Use it when a tool's name collides with a step keyword.
- **wait_until** — pauses the script until a condition holds: `condition`, `pawnName`, `timeoutTicks` (default 3000). On timeout the script continues to the next step.
- **resultKey** — any tool step may include it; the result is stored and surfaced in the completion log the agent sees on its next turn.

```json
{
  "scriptName": "Fetch and equip",
  "steps": [
    { "type": "possess_colonist",
      "arguments": { "pawnName": "Dole", "action": "equip", "targetItemName": "shotgun" } },
    { "type": "wait_until",
      "arguments": { "condition": "has_weapon", "pawnName": "Dole", "timeoutTicks": 6000 } },
    { "type": "call_tool",
      "arguments": { "tool": "get_colonists_profile", "arguments": {}, "resultKey": "after" } }
  ]
}
```

Validation is deliberately asymmetric:

- **Structural steps are strict.** Unknown fields on `wait_until` or `call_tool` reject the whole script before anything executes, naming the step number and field. The rejection flows to the caller through the normal log/finish chain, so an agent run sees the errors as feedback and can correct the shape.
- **Tool-step arguments only warn.** An argument not in the tool's declared schema logs a warning but does not block — registered schemas are not all complete, and tools already answer bad input with error payloads.
- **Legacy aliases** (`equip_item`, `damage_self`, `clear_queue` and friends) are rewritten up front and each rewrite is logged. New content should use the declared schema.

---

## 4. Custom wait conditions

```csharp
SynapseScriptRunner.RegisterWaitCondition("herd_gathered",
    (pawn, args) => HerdIsGatheredNear(pawn, args));
```

The evaluator receives the resolved pawn and the step's arguments and returns a bool. Registered conditions automatically appear in the step schema the model is shown — no prompt edits needed.

Built-in conditions: `has_weapon`, `has_ranged_weapon`, `has_any_weapon`, `reached_cell`, `pawn_downed`.

---

## 5. Scripts at runtime

- `SynapseScriptRunner.StartScript(script, logCallback, onFinished)` — plus an overload adding `allowMutatingTools` for gated runs.
- `AbortScript(scriptName)` — removes the script; `onFinished` still runs so an agent chain resolves instead of waiting forever.
- `GetActiveScriptStates()` — read-only view of every active script: name, step, wait condition, remaining ticks. This is what the debugger renders.

**Persistence**: active scripts survive a save/reload. The script itself resumes — at the same step, with its wait timeout re-anchored so it never expires instantly. The **agent chain does not resume**: `onFinished` is a closure and cannot be serialised, and a resumed agent would be reasoning from a conversation about a world the load replaced. A restored script logs through the standard logger, states once at restore that its agent chain was interrupted, and finishes without a continuation. Build your `onFinished` logic accordingly: it is guaranteed to run in the session that started the script, not across a load.

---

## 6. The agent loop, budgets and the inspector

`SynapseLlmPlanner` drives the plan→execute→observe loop. What mod builders should know:

- **Budgets**: every run is bounded by the *Agent max turns* (default 8) and *Agent max requests per run* (default 12) settings, and `Cancel()` stops a run at its next loop entry.
- **Autonomous runs** (constructor flag `isAutonomous`) are for anything the player did not directly initiate. They respect the autonomous-mutation gate.
- **The run inspector**: `SynapseAgentRunLog` records every run — the plan each turn emitted, each executed action with error payloads marked, the outcome fed back, and budget usage. Players see it in the Script Debugger's *Agent Runs* view. Your escalations and scripts show up there automatically; keep your log lines meaningful because they are what the player reads when diagnosing.

---

## 7. Escalation: when reality diverges

When your programmed path gets a response it cannot apply — a missing field, an outcome that matches no branch — hand it to the agent:

```csharp
bool started = SynapseAgentEscalation.Escalate(new SynapseEscalationContext
{
    Origin = "Psychology.CeremonyRecord",
    Expectation = "a ceremony record with an overallRecord narrative",
    Observation = rawResponseExcerpt,           // large payloads are abbreviated automatically
    SuggestedGoal = "salvage a one-line ceremony record from the response",
});
```

`Escalate` returns true if an agent run started, false if refused — and it **never throws**, so the call is safe on any failure path. Keep your original warning log either way; escalation is an addition, not a replacement.

Guardrails you should design around: escalation is a **default-off** player setting, rate-limited by a cooldown (default 120 s) and a per-session cap (default 10), and refused outright on the Minimal capability tier. Treat a refused escalation as the normal case — your fallback path must remain correct without the agent.

---

## 8. Performance: tiers, budgets and handles

Compatibility is the maximum concern; speed is second. Core measures and adapts — your job is to route through the right primitives:

- **Capability tiers** (Minimal / Standard / Rich) are chosen from *measured* total-response latency, starting low and promoting on evidence. Ask `SynapseTierController.GetOperatingPoint(eventType)` for your work class's current prompt-token cap and governing constraint rather than sizing prompts yourself. The 2k-context floor governs everything: never assume all tools or all context fit in a prompt.
- **Work classes**: foreground interactions (conversations) target under 500 ms; background batches are deadline-bound. Cloud providers are cost-governed by default (the player can override with the experimental *Ignore token costs* toggle).
- **Big payloads travel as handles**: `SynapseResultStore.AbbreviateIfLarge(content)` turns oversized results into an excerpt plus a `res_N` handle the model can retrieve later with the `get_stored_result` tool. Use it for anything you feed back to the model that can grow.
- **Opportunistic tasks**: register non-urgent background generation with `SynapseClient.RegisterOpportunisticTask(mod, taskId, callback, config)` instead of firing requests on your own schedule. Core dispatches when the queue is quiet, informed by a per-in-game-hour demand forecast, and stands down when foreground work arrives.
- **Deadline batches**: for work that must finish inside an in-game window (nightly reviews), use `SynapseBatchPlanner` — it sizes per-item context to fit the window, then coalesces, then cuts passes, and always yields to foreground.

---

## 9. Logging and testing conventions

The log is machine-read by the test harness, so format matters:

- Handled warnings must not look like thrown exceptions: log `[ExceptionTypeName] message`, never `ExceptionTypeName: message`.
- Tool failures are error payloads, never exceptions; never log an error payload under a `[Result]` prefix.
- Use `SynapseLogger.Message/Warning(msg, category)`; agent and performance lines use the `performance` category.
- Every behavior change lands with an in-game test case (`<Repo>_<CaseName>`) in the repo's `Source.Tests/`; the suite must end with 0 blocking entries. Case-writing rules live in the dev-tools repo's `modding-knowledge/07-in-game-tests.md`.

The development loop (harness in the `rimworld-claude-dev-tools` repo):

```powershell
.\harness\build.ps1              # all mods, dependency order: Core -> companions -> Factions
.\harness\launch.ps1 -Test       # rotates Player.log, runs the in-game suite, self-terminates
.\harness\readlog.ps1            # classifies the log; exit 1 on blocking entries or FAILed cases
```

Work lands on `development` via PRs; `main` only via release promotion. Versioning is `0.<iteration>.<minor>`.

---

## 10. Publishing your own in-game docs

Any mod that ships a `Learning/` folder of `.md` files gets them injected into RimWorld's Learning Helper automatically at startup — headings, bullets, bold/italic and blockquotes render; code fences and images do not (keep the in-game copies fence-free). Keep a matching copy in your repo so the in-game docs and GitHub never drift — exactly as this document mirrors `Learning/Agent_Scripting_and_Tools.md`.

## 11. Tuning the memory model (0.7.1)

Memory weight is a single **0.0–1.0** scale; short-term vs long-term is emergent (a memory consolidates to long-term once its relational salience or reference count crosses a threshold), not a `memoryType` list. Per-type behaviour is data-driven via `SynapseMemoryClassDef` (`Core/Defs/MemoryClasses/`), so you can retune or add memory classes by XPath without recompiling:

- `memoryType` — the type this class tunes (matched case-insensitively; falls back to sane defaults for unknown types).
- `baseWeight` — suggested 0–1 weight for a fresh memory of this class.
- `decayRate` — per-day decay in the daily maintenance pass (before the global `memoryDecayMultiplier`).
- `bornLongTerm` — true = never decays (backstory, defining events).
- `consolidationContribution` — how strongly a memory of this class boosts a neighbour's salience (0–1 × its weight), which is what lets, e.g., a death promote linked chit-chat.

When you create memories, populate `subjectPawnIds` with the pawn a memory is *about* so the salience graph can link it. Balance thresholds (consolidation, reference count, decay, trait-shift pressure) are exposed as mod settings and mirrored into Core statics, so players can retune without XML.

## 12. The 0.9 storyteller and memory surfaces

New extension points a companion mod can build against (all inert under a vanilla storyteller):

*   **Contribute a storytelling verb**: `SynapseToolVocabulary.Add(SynapseToolVocabulary.StorytellerScope, "your_tool_name")`
    after registering the tool. Verbs outside the scope are refused at the executor no matter
    what the model asks for. Never grant a verb that edits pawns or objects by fiat.
*   **Subscribe to incident lifecycle** (`SynapseIncidentLifecycle`): reflection-friendly,
    primitive-only payloads — `OnIncidentStarted(kind, region, magnitude, origin, leadTimeTicks)`
    and `OnIncidentResolved(kind, region, outcome)`. Resolve the type with
    `GenTypes.GetTypeInAnyAssembly("RimSynapse.SynapseIncidentLifecycle")` so your mod builds
    with Core absent. Resolution is deduped per incident instance.
*   **Query world history** (`SynapseCoreWorldComponent`): `QueryWorldHistory(region, kind, sinceTick)`,
    `OpenThreads()`, `WorldHistoryContextBlock()` — the canonical, save-backed record the
    storyteller reasons over. Do not build your own parallel store; view over this one.
*   **Record memories about pawns** (`SynapseCorePawnComp`): always use
    `AddMemoryAbout(pawn(s), summary, type, weight, ...)` or key `subjectPawnIds` with
    `MemoryPawnId(pawn)` (`GetUniqueLoadID`). Never push a hand-rolled `WeightedMemory` onto
    `.memories` and never link pawns via `ThingID` tags — those memories are invisible to
    relational consolidation.
*   **Report in-process VRAM** (`SynapseClient.Gpu`): if your mod loads a model into VRAM inside
    RimWorld's process, `UpsertConsumer(modId, label, vramMb, resident)` on load and
    `RemoveConsumer(modId)` on dispose gives it its own line in GPU monitors instead of
    inflating "System".

## 13. Provider slots: owning a question Core asks (0.10)

`SynapseCoreProviders` is the registry for capabilities Core does not own: each slot has
exactly one authoritative answerer, is pulled rather than pushed, and returns a value (the
opposite of the broadcast hooks above). Every slot is a public static property registered by
reflection so a producer builds and runs with Core absent, and every slot documents its
unregistered value — consumers call the accessor, never the slot.

```csharp
var t = GenTypes.GetTypeInAnyAssembly("RimSynapse.SynapseCoreProviders");
var slot = t?.GetProperty("TextToSpeech", BindingFlags.Public | BindingFlags.Static);
slot?.SetValue(null, (Func<string, string, Action<byte[]>, bool>)MyEngine.Speak);
```

Current slots:

*   **`PopulationDensity`** (`Func<int, int>`): dwellings on a world tile. Unregistered: 0.
    Consumers call `PopulationDensityAt(tile)`.
*   **`Residency`** (`Func<Pawn, bool>`): whether a pawn lives in a generated dwelling.
    Unregistered: false. Consumers call `IsResident(pawn)`.
*   **`TextToSpeech`** (`Func<string, string, Action<byte[]>, bool>`, 0.9.1): synthesise a
    line as spoken audio — `(text, voiceHint, onPcm) => accepted`. Owned by Local Text to
    Speech. Return quickly (synthesise on your own worker, never the calling thread) and
    deliver 16-bit mono 24 kHz PCM to `onPcm`; Core routes it to playback, and `onPcm` is
    safe to call from any thread. `voiceHint` is advisory (a Kokoro voice id or file path).
    Unregistered: **no-op** — `SynapseSpeech.TrySpeak(text, voiceHint)` returns false and
    nothing plays. A throwing provider is contained and logged; the line just goes unspoken.
    Consumers call `SynapseSpeech.TrySpeak`, which hands your provider the request off the
    caller's thread. Core's own call sites (storyteller chat replies, letter reactions) sit
    behind default-off mod settings.
