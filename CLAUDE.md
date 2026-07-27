# RimSynapse Core — Engineering Rules

Core is the API surface for an ecosystem: every companion mod's DLL binds against
`RimSynapseCore.dll`. Rules here exist because violating them has already broken
things once.

## Binary compatibility (the rule that broke three mods)

Companion DLLs bind to **exact method signatures**. Appending an optional parameter
to a public method **removes the old signature from the assembly** — Psychology,
Conversations and Factions all failed to instantiate when `RegisterTool` gained an
optional `isMutating` parameter, and the test suite stayed green because dead mods
run no tests.

- **Never** append optional parameters to, reorder, or otherwise alter an existing
  public method signature. Add a **separate overload** (see `RegisterTool`,
  `StartScript`, `ExecuteTool` — all follow this pattern now).
- The same applies to removing/renaming public types, fields, and enum members.
- The guard: `Core_AllModsInstantiated` in the TestRunner fails if any mod dies at
  startup. Run the full suite after touching any public surface.

## Build and test loop

The harness lives in the `Repo-MCP` repo (`harness/*.ps1`); the in-game suite is the
`TestRunner` repo (loads last, only active with `-synapse-test`).

```powershell
.\harness\build.ps1              # all mods, dependency order: Core -> Regions -> companions -> Factions
.\harness\launch.ps1 -Test       # rotates Player.log, runs the TestRunner, self-terminates on SUMMARY
.\harness\readlog.ps1            # classifies the log; exit 1 on blocking entries or FAILed cases
```

- **Every behavior change gets a TestRunner case** (`<Repo>_<CaseName>`). The suite
  must end `0 blocking`.
- Under `-quicktest` the LLM is **mocked** in `SynapseClient` (canned responses,
  keyword-branched). Tool tests are deterministic; live-LLM behavior is not covered.
- A `build.ps1 -Repo X` rebuilds only X and its deps — after public-surface changes
  in Core, always do a **full** build so companions recompile against it.

## Log conventions (the classifier reads these)

- Handled warnings must not look like thrown exceptions: log the type as
  `[JsonSerializationException] message`, never `JsonSerializationException: message`.
- Tool handlers report failure as an `{"error": ...}` payload, never by throwing;
  callers must not log error payloads under a `[Result]` prefix.
- Test output goes through `Log.Message` (never `Log.Error` — it double-counts as a
  blocking entry). Format: `[SYNAPSE-TEST] PASS|FAIL <case> | <detail>`.
- `SynapseLogger.Message(msg, category)` — performance/tier lines use category
  `"performance"` and a `[Tier]`/`[Agent]` prefix.

## Agent & performance architecture (0.6+)

- **The agent is the exception path.** The mainline is Harmony-hook pipelines with
  curated context. Tool search (`SynapseToolIndex`), `list_available_tools` /
  `describe_tool` serve discovery for unprogrammed situations.
- **Prompts are budgeted, never unbounded.** `SynapseTierController` picks
  Minimal/Standard/Rich from *measured* latency (start low, promote on evidence;
  demote immediately). Per-class operating points implement the objective switch:
  latency-governed locally, cost-governed on metered cloud backends (all cloud
  providers are metered by default; `ignoreTokenCosts` is the experimental escape).
- **Big payloads travel as handles**: `SynapseResultStore` + `get_stored_result`.
  History compacts via `SynapseAgentHistory`; the latest exchange stays verbatim.
- **Mutating tools are flagged** (`GameTool.isMutating`, manifest in
  `EnsureInitialized`). Autonomous runs are gated by `allowAutonomousMutations`.
  `execute_game_tool` must stay flagged — it can launder any mutation.
- Never assume all registered tools fit in a prompt; the 2k-window floor governs.

## Release gate

Binaries have two sources of truth, and both have drifted before. Before tagging:

```powershell
.\harness\verify-metadata.ps1          # version + changelog agree in all three places
.\harness\verify-binaries.ps1 -Build   # rebuild, then check every shipped DLL
.\harness\release-manifest.ps1         # regenerate Core's Assemblies/CHECKSUMS.sha256
```

**Every mod states its version in three independent places** and they drift silently:
`About.xml <modVersion>` (read by mod managers), `About.xml <description>` (the in-game
mod list blurb), and `About/steam_description.txt` (the Workshop page). v0.6.0 shipped
with the first bumped and the second still reading v0.5.2 with no 0.6.0 notes — the
embedded description is easy to forget precisely because it duplicates the Workshop copy.
`verify-metadata.ps1` fails when they disagree, when the description's changelog has no
entry for the current version, or when About.xml stops being well-formed.

- Companion repos **track** their DLLs (a cloned companion repo is a playable mod). The
  hazard is source landing without a rebuild — Psychology shipped a source-only escalation
  feature this way. `verify-binaries.ps1 -Build` fails when a committed DLL differs from a
  fresh build of its own source.
- Core does **not** track its DLLs (they attach to Releases), so its record is
  `Assemblies/CHECKSUMS.sha256` — version, commit, and SHA256 per file. Regenerate it in
  the release commit, attach the same DLLs as Release assets, and re-run
  `verify-binaries.ps1` immediately before a Workshop upload to confirm the files on disk
  are byte-for-byte the released build.

## Branch discipline

- Work lands on `development` via PRs; `main` only via release promotion PRs.
- Versioning is `0.<iteration>.<minor>`; never plan or tag anything `1.0`.
- Companion repos commit their built `Assemblies/*.dll`; Core does not (its DLLs
  attach to GitHub Releases). `Source/obj/` is never tracked.
