# Changelog — RimSynapse Core

Versioning is `0.<iteration>.<minor>`. Dates are release dates.

---

## 0.6.1 — Mod listing metadata

Metadata-only. The code, and the assemblies, are identical to 0.6.0.

- **FIXED — the in-game mod list showed v0.5.2 with no 0.6.0 notes.** A mod states its
  version in three independent places: `About.xml <modVersion>`, the description embedded
  in `About.xml` (what RimWorld's mod list displays), and the Workshop description. The
  0.6.0 release updated the first and third but not the second, so the mod list kept
  showing the old version and changelog. All three now agree across every mod, and
  `verify-metadata.ps1` fails a release when they do not.
- **Roadmap updated.** 0.7 is now *Regions and Territories Compatibility* — making the
  territory layer work cleanly alongside other major world and faction mods, which is the
  groundwork the Factions work depends on (Factions will build on the Empire mod). Logic
  Externalization moves to 0.8 and Emergent Systems and Content to 0.9.

---

## 0.6.0 — Agent and Tool Foundation

The whole 0.6 milestone: an in-game agent that plans and carries out multi-step work itself — composing scripts that call registered tools, waiting on game conditions, observing what happened, and continuing — instead of firing one-shot tool calls. Every prompt it builds is budgeted, and the whole thing is designed to stay usable on a 2k context window.

**Compatibility:** no save format changes, and no companion mod needs rebuilding. Saves from 0.5.x load unchanged. See *Compatibility* below for detail.

### The agent

- **Multi-step scripts are a declared schema, not a convention.** `wait_until` and `call_tool` are validated strictly — an unknown field rejects the script before anything runs, naming the step and the field — while tool-step arguments only warn, so older content keeps working. Legacy step aliases are still accepted and each rewrite is logged.
- **Tool steps are explicit and their failures are visible.** A mistyped tool name used to be logged as an ordinary result, so a script that did nothing read as a success. Unknown tools are now reported and skipped, and error payloads are no longer printed as output.
- **Scripts survive a save and reload.** A script waiting on a game condition resumes at the same step with its timeout re-anchored, rather than being silently stranded. The agent conversation attached to it does not resume — it is closed out and says so, because its reasoning describes a world the load replaced.
- **Runs have budgets and a stop button.** Turn limit, per-run request budget, and cancellation, all configurable in settings.
- **Mutating tools are gated.** Tools that change game state are flagged; runs the player did not start cannot call them unless *Allow autonomous mutations* is enabled.
- **Programmed features can escalate to the agent.** When a scripted path hits an outcome it does not know how to handle, it hands what it knows to the agent instead of dropping the interaction. Default-off, rate-limited, and refused on the lowest capability tier.
- **A run inspector.** The Script Debugger gained an *Agent Runs* view: each run's turns with the plan it emitted, the steps it executed with errors marked, the outcome it observed, budget usage, and a cancel button.

### Performance and scaling

- **Hardware-adaptive capability tiers.** Core measures actual end-to-end response latency and picks Minimal / Standard / Rich from evidence — starting low, promoting only when the measurements justify it, demoting immediately when they do not. Detected context window is a ceiling; measurement sets the operating point.
- **Per-work-class budgets.** Conversations target sub-500 ms. Background batches are deadline-bound: nightly work sizes itself to finish inside the in-game night, shrinking per-item context and then cutting passes, and always yields to anything in the foreground.
- **Cost governance for cloud backends.** Every cloud provider is treated as metered by default and governed by token cost instead of latency; an experimental *Ignore token costs* toggle switches it back to latency-scaling. Local backends are never treated as metered.
- **Two-stage tool discovery.** The first prompt names only the tools a request is likely to need, chosen by a search index; everything else is reachable by searching from inside the conversation. A vague request now produces the *smallest* prompt instead of the largest.
- **Pre-seeded observations.** Obviously-relevant read-only tools run before the first LLM call and their results are inlined, so simple requests can finish in one turn instead of two. Turns are the latency on slow hardware.
- **Big results travel as handles.** Oversized tool output becomes an excerpt plus a retrievable handle rather than flooding the context window.
- **Quiet-hour forecasting.** Background work learns which in-game hours are typically quiet and prefers them, standing down when foreground activity arrives.

### Documentation

- New mod-builder guide, published identically to the in-game Learning Helper wiki and to `docs/COMPANION_MODS.md` on GitHub.
- "MCP" terminology retired from player- and modder-facing docs: Core's engine is native tool-calling. The Repo-MCP developer tooling repo is unaffected.

### Fixed

- **Restored a public constructor signature.** `SynapseLlmPlanner`'s three-argument constructor had gained an optional parameter during development, which removes the original signature from the assembly and would break any third-party mod compiled against 0.5.x. It is now a separate overload, and a reflection test guards the published surface so this class of regression fails the test suite instead of reaching players.

### Compatibility

- **Saves:** 0.5.x saves load unchanged. Running scripts are new save data; a save without them restores nothing.
- **Companion mods:** verified empirically — the actual 0.5.2 release binaries of Psychology, Conversations, Factions, WorldNews, Regions and Territories and the NVIDIA Tool were run against this Core, with every mod instantiating cleanly and the full 84-case in-game suite passing with zero blocking log entries.
- **Third-party mods built against 0.5.x:** no published Core signature was removed (see *Fixed* above).

---

## 0.5.2

- **FIXED — Core failed to load:** a Harmony patch bound the wrong parameter name on the game's knowledge database, which threw while patching and stopped RimSynapse Core initialising at all.
- **FIXED — Learning Helper crash:** wiki guides added to RimWorld's Learning Helper threw an exception every frame. Guide names are now valid identifiers and duplicates are skipped.
- **FIXED — Developer scripting paths:** the developer file channel pointed at one machine's hard-coded drive, so it silently did nothing anywhere else. It now follows your install.
- **Licence:** now PolyForm Noncommercial 1.0.0. Free to use, modify and share for any noncommercial purpose.

---

## 0.5.1

- **NEW — API-level thinking controls:** support for OpenAI/LM Studio reasoning and thinking-token configuration in mod options.
- **NEW — Historical records and letter archival:** automatically generates "Colony Established" and "Colony Named" ceremony event records and sends letter alerts.
- **NEW — Stele links stored in Core:** moved Stele Link monument bindings and colony ceremony logs to `SynapseCoreWorldComponent` for unified tracking.
- **NEW — Resident life support:** resident pawns cook simple meals in their inventories near heat sources if low on food.
- **Pawn-level job tracking:** relocated job and location tracking to per-pawn `SynapseCorePawnComp` (`CompTickRare`) to reduce CPU cost.
- **Idle backstory memory queueing:** resident pawns are flagged high-priority, queueing backstory generation in background cycles.

---

## 0.5.0

- **Multi-provider LLM balancing:** enhanced multi-provider load balancing and capabilities filtering.
- **Full RimWorld 1.6 and DLC compatibility:** deep optimisations to support active storytellers across all official expansions.
