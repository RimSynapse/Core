# Changelog

Full version history for RimSynapse - Core. The mod page and Workshop description show only the latest release; every earlier version is recorded here.

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
