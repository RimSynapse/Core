# Agent, Scripting and Tools — Mod Builder Guide

This is the guide for companion mods building on RimSynapse Core's agent and tool foundation (the 0.6 surface). Everything here also exists on GitHub as `docs/COMPANION_MODS.md` — the two are kept in sync.

---

## 1. Where the agent sits

The agent is the **exception path, not the mainline**. The primary pipeline is programmed: your mod intercepts game functions with Harmony hooks, assembles curated context for that specific situation, and prompts the LLM for a specific outcome. Prompts stay small *by construction* because you chose what matters.

The agent, and its tool discovery, exist for the rest:

- **Situations nothing was programmed for** — the Direct Action Console is the manual form of this.
- **Escalation** — when your programmed path hits an outcome it does not know how to handle, hand what you know to the agent instead of logging a warning and dropping the interaction (see section 7).

Design your features as hooks first. Reach for the agent when reality diverges.

---

## 2. Registering game tools

`SynapseToolRegistry` is the shared directory of everything the LLM can do in the game. Register during startup (your Mod constructor or a StaticConstructorOnStartup):

- `SynapseToolRegistry.RegisterTool(name, description, parametersSchema, handler)` — with optional `isDebug` and `keywords` parameters.
- `SynapseToolRegistry.RegisterTool(name, description, parametersSchema, handler, isDebug, keywords, isMutating)` — the overload that also flags the tool as **mutating** (it changes game state).

Details that matter:

- **name** — snake_case, prefixed by intent: `get_` / `search_` / `list_` for read-only queries. Read-only prefixes make your tool eligible for pre-seeding (section 8), which can save the agent a whole turn.
- **parametersSchema** — a JSON-Schema-shaped object: a dictionary with `type: "object"` and a `properties` dictionary describing each argument. The schema is shown to the model by `describe_tool` and used to *warn* about undeclared script arguments, so keep it accurate.
- **handler** — takes the arguments as a JSON string, returns a JSON string. The contract:
  - Return valid JSON even for empty arguments.
  - **Never throw.** Report failure as an error payload: `{"error": "reason"}`. `ExecuteTool` never throws either — callers rely on that.
  - The whole-registry test suite executes every non-debug tool with empty and malformed arguments; your handler must survive both.
- **keywords** — feed the tool search index. The agent's first prompt only names the tools a request is *likely* to need (chosen by index search); everything else is found by searching from within the conversation. Good keywords are how your tool gets found.
- **isMutating** — flag anything that changes game state. Autonomous runs (escalations, background evaluations) are barred from mutating tools unless the player enables *Allow autonomous mutations* in settings. Player-initiated runs may always mutate. An unflagged mutator is a hole in that gate.

Also available: `IsToolRegistered(name)`, `ExecuteTool(name, argsJson)` and `ExecuteTool(name, argsJson, allowMutating)`.

**Binary compatibility warning**: your DLL binds to Core's *exact* method signatures. Core never alters an existing public signature (new capability arrives as new overloads), and your mod should follow the same rule for any API it exposes — appending an optional parameter to an existing public method silently removes the old signature and breaks every assembly bound to it.

---

## 3. The script step schema

The model drives multi-step work by emitting a script: a `scriptName` plus a list of `steps`. Each step is `{ "type": ..., "arguments": {...} }`. The schema is declared in `SynapseScriptValidator` and published to the model automatically — companion mods only need to know the semantics:

- **A step type is a tool name.** Any registered tool can be a step; its arguments follow the tool's schema. Unknown tool names are reported and skipped at execution.
- **call_tool** — runs a tool named in its arguments: `tool`, `arguments`, optional `resultKey`. Use it when a tool's name collides with a step keyword.
- **wait_until** — pauses the script until a condition holds: `condition`, `pawnName`, `timeoutTicks` (default 3000). On timeout the script continues to the next step.
- **resultKey** — any tool step may include it; the result is stored and surfaced in the completion log the agent sees on its next turn.

Validation is deliberately asymmetric:

- **Structural steps are strict.** Unknown fields on `wait_until` or `call_tool` reject the whole script before anything executes, naming the step number and field. The rejection flows to the caller through the normal log/finish chain, so an agent run sees the errors as feedback and can correct the shape.
- **Tool-step arguments only warn.** An argument not in the tool's declared schema logs a warning but does not block — registered schemas are not all complete, and tools already answer bad input with error payloads.
- **Legacy aliases** (equip_item, damage_self, clear_queue and friends) are rewritten up front and each rewrite is logged. New content should use the declared schema.

---

## 4. Custom wait conditions

`SynapseScriptRunner.RegisterWaitCondition(name, evaluator)` adds a condition your mod owns. The evaluator receives the resolved pawn and the step's arguments and returns a bool. Registered conditions automatically appear in the step schema the model is shown — no prompt edits needed.

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
- **Autonomous runs** (constructor flag) are for anything the player did not directly initiate. They respect the autonomous-mutation gate.
- **The run inspector**: `SynapseAgentRunLog` records every run — the plan each turn emitted, each executed action with error payloads marked, the outcome fed back, and budget usage. Players see it in the Script Debugger's *Agent Runs* view. Your escalations and scripts show up there automatically; keep your log lines meaningful because they are what the player reads when diagnosing.

---

## 7. Escalation: when reality diverges

When your programmed path gets a response it cannot apply — a missing field, an outcome that matches no branch — call:

- `SynapseAgentEscalation.Escalate(context)` with a `SynapseEscalationContext`: `Origin` (your system and call site, e.g. "Psychology.CeremonyRecord"), `Expectation` (what you were trying to produce), `Observation` (what actually happened — large payloads are abbreviated automatically), and optional `SuggestedGoal`.

It returns true if an agent run started, false if refused — and it **never throws**, so the call is safe on any failure path. Keep your original warning log either way; escalation is an addition, not a replacement.

Guardrails you should design around: escalation is a **default-off** player setting, rate-limited by a cooldown and a per-session cap, and refused outright on the Minimal capability tier. Treat a refused escalation as the normal case — your fallback path must remain correct without the agent.

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
- Every behavior change lands with an in-game TestRunner case (`<Repo>_<CaseName>`); the suite must end with 0 blocking entries. See the TestRunner repo for case-writing rules.

---

## 10. Publishing your own in-game docs

Everything you are reading is a plain markdown file. Any mod that ships a `Learning/` folder of `.md` files gets them injected into RimWorld's Learning Helper automatically at startup — headings, bullets, bold/italic and blockquotes render; code fences and images do not. Keep a matching copy in your repo so the in-game docs and GitHub never drift.
