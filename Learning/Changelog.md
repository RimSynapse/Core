# Changelog

Full version history for RimSynapse - Core. The mod page and Workshop description show only the latest release; every earlier version is recorded here.

## v0.9.1 - Fixes and the Speech Surface
- FIX - Ambient dialogue no longer leaks raw job defNames: a labelless job such as `GotoWander` fell through to its raw defName and surfaced in Conversations dialogue as "the gotowander they've been busy with". Jobs are now humanized through a label -> clean reportString -> spaced-camelCase fallback that never emits a raw defName.
- SAFETY - The mutating-tool manifest is complete: nine previously-unflagged state-changing tools are now flagged as mutating, so an autonomous run cannot invoke them unless `allowAutonomousMutations` is set.
- NEW (for modders) - TextToSpeech provider surface: a Core provider slot plus the `SynapseSpeech` accessor that a Local-TTS mod binds to by reflection, with a documented unregistered fallback (see the Local TTS / Voicebox guide).
- Internal - The in-game test suite moved in-repo from the retired TestRunner, the release-gates workflow checks the harness out of the dev-tools fork, and every GitHub release now carries a RimSort-installable zip asset plus a per-file `CHECKSUMS.sha256` manifest.

## v0.9.0 - The Aura Storyteller Engine
- NEW - LLM-driven incident selection: when a RimSynapse storyteller (Aura) is active, the AI chooses WHICH eligible incident fires on the game's own deterministic cadence - the model never rolls timing, only picks what. Backend offline or budget spent falls back to the vanilla weighted roll for that beat, so the colony is always fully playable.
- NEW - Storyteller tool vocabulary: the storyteller runs on a curated allowlist of storytelling verbs enforced at the executor boundary; god-mode and diagnostic tools are structurally unreachable, and consequence verbs clamp to the difficulty budget.
- NEW - Difficulty and personality context: the difficulty (including a custom threat-scale slider) is injected as a hard points ceiling plus a mood mandate, and the storyteller's personality profile rides every storyteller and chat prompt.
- NEW - Player-storyteller chat window (migrated from Conversations): a floating chat with Aura, storyteller-gated, save-backed, with optional voice - works with or without Conversations installed.
- NEW - Two agents, two scopes: the Chat agent holds zero consequence tools (a jailbroken chat can be rude, not dangerous) and player messages never trigger storyteller turns; only a typed mood signal - never your words - reaches the storyteller, on its own schedule.
- NEW - World history store: save-backed record of regional incidents and outcomes; unresolved events stay open as threads the storyteller calls back to later. Bounded so saves stay sane.
- NEW - Regional-incident lifecycle hook: Core broadcasts incident start and first-level resolution (primitive payloads, reflection-subscribable) for Storyteller, WorldNews, Factions and Regions to react on their own terms.
- NEW - Memory-linkage framework: one canonical pawn-id scheme (AddMemoryAbout / MemoryPawnId) so chit-chat about a pawn actually consolidates with later events about them - the "chit-chat about someone who then dies" promotion now fires on real data.
- NEW - GpuStats in-process consumers channel: a mod loading a model into VRAM inside RimWorld's process (e.g. Local TTS) can register its footprint for its own line in the NVIDIA Tool's VRAM breakdown.

## v0.7.1 - Weight-driven memory and evaluation
- NEW - Memories now live on a single 0-1 importance scale, and short-term vs long-term is emergent: a memory becomes long-term once its relational salience - its links to other significant memories and people - crosses a threshold, not from a hardcoded type list.
- NEW - Relational consolidation: idle chit-chat fades within a day or two, but chit-chat about someone who then dies is pulled up into long-term memory the day the death lands.
- NEW - Data-driven memory classes (`Defs/MemoryClasses`): per-type base weight, decay and consolidation are tunable by XPath without recompiling.
- Changed: context now surfaces long-term and high-salience memories first, then fills with recent ones, instead of a flat top-5-by-weight. Surfacing a memory counts as a reference.
- Fixed: reinforcing a strong memory no longer crushed it down to 1.0 (a weight-scale mismatch); reinforcement now only ever raises a memory.
- Added the `TraitPressure` data model and per-comp store behind Psychology's gradual, multi-day personality shifts. Legacy saves rescale weights to 0-1 exactly once and migrate silently.

## v0.7.0 - Regions and Territories Compatibility
- NEW - Load-order guard: a mod loaded before something it declares it must load after now reports one clear error at startup, instead of silently losing every type in its assembly and appearing installed while doing nothing.
- Changed: every mod now targets .NET Framework 4.8, so any RimSynapse mod can reference any other. Previously a net472 mod could not, and the reference was dropped with only a build warning.
- Changed: the metadata and wiki release gates now run automatically on every pull request, rather than when someone remembers to run them.

## v0.6.1
- Fixed: the in-game mod list showed v0.5.2 with no 0.6.0 notes; version and changelog now agree everywhere.
- Roadmap updated: 0.7 is Regions and Territories compatibility (groundwork for Factions, which will require Empire).

## v0.6.0 - Agent and Tool Foundation
- NEW: Multi-step plans that call game tools, wait on game conditions, see the result and continue.
- NEW: Plans survive saving and loading, resuming at the right step without instantly timing out.
- NEW: Turn limits, a per-run request budget and a stop button.
- NEW: Autonomous actions cannot change your colony unless you enable "Allow autonomous mutations".
- NEW: Scripted features escalate to the agent when they hit something they were never taught to handle (off by default).
- NEW: Agent Runs inspector in the Script Debugger - what it decided, ran, observed and spent, plus cancel.
- NEW: Capability tiers chosen from measured response times; designed down to a 2k context window.
- NEW: Conversations prioritised (sub-500ms target); background work fits the in-game night and yields to you.
- NEW: Cloud APIs budgeted by token cost, with an experimental toggle to ignore cost.
- Fixed: mistyped tools looked like successes and malformed plans were dropped silently.
- Fixed: restored a public constructor signature so mods built against 0.5.x keep working.
- Compatible with 0.5.x saves; companion mods verified against their published builds.

## v0.5.2
- Fixed: Core failed to load entirely when a Harmony patch bound the wrong parameter name.
- Fixed: wiki guides in the Learning Helper threw an exception every frame.
- Fixed: developer scripting paths were hardcoded to one machine and now follow your install.
- Licence changed to PolyForm Noncommercial 1.0.0.

## v0.5.1
- API-level thinking controls, historical records and letter archival, resident life support.

## v0.5.0
- Regions and Territories module, expanded faction and world systems.

## v0.4.0
- New Features! (Multi-provider LLM balancing, Image Generation Framework, and LLM Capabilities filtering).

## v0.3.0
- Embedded Aura the storyteller into core.
- Ecosystem Expansion (Introduced Chat, Factions, and WorldNews companion modules).

## v0.2.1
- Bug fixes and stability improvements.

## v0.2.0
- Storyteller Update (Added the AI Storyteller module, now merged into Core).

## v0.1.0
- Initial Release (Core framework, NVIDIA Tool, and Psychology module).
