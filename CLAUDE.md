# RimSynapse Core — Engineering Rules

Core is the API surface for an ecosystem: every companion mod's DLL binds against
`RimSynapseCore.dll`. Rules here exist because violating them has already broken
things once.

## GitHub first: if it is not in git, it does not exist

**Every discovered item and every work action is captured in GitHub before the turn
ends.** A chat transcript is not a record: the maintainer cannot review it, a future
session cannot see it, and two people working from it are working from different
source material. GitHub issues are the human-readable shared state — the repo is the
system of record for *work*, exactly as it is for code.

This is a **capture-first** discipline, not a bookkeeping step at the end:

1. **A discovery becomes an issue the moment it is found.** Anything surfaced while
   doing something else — a latent bug, a stale artifact, a missing guard, a process
   gap, an assumption that turned out wrong — gets an issue immediately. If it is
   fixed in the same change, the PR closes it; if it is not, the issue stands on its
   own. "I'll mention it in the summary" is how findings get lost.
2. **Work that needs a plan gets an issue before it gets code**, using the standing
   template: goal, milestone, scope/design, acceptance criteria, test plan.
3. **The board is not optional.** Issues belong on the org project board
   (`https://github.com/orgs/RimSynapse/projects/2`) and must move through
   Backlog → In progress → Testing → Done as the work does. A board showing 0 Done
   while a 13-child epic has shipped is worse than no board — it actively misleads.
4. **Milestones carry the release, the board carries the state.** When they disagree,
   one of them is lying; reconcile rather than picking a favourite.
5. **Process fixes count as work.** Guards, scripts and rules earned the hard way
   (`verify-binaries`, `verify-metadata`, `sync-wiki`) are themselves issues and PRs,
   not silent additions.

### Working with the board

The board is **org-level**: `https://github.com/orgs/RimSynapse/projects/2`, one project
named "RimSynapse" (node `PVT_kwDOEfI01s4Bdlhx`). It is not a per-repo project — when a
picker offers two entries both called "RimSynapse", check `data-id`: they have been
observed to be the *same* project rendered twice, not a repo/org pair.

**Prefer the CLI.** It needs a scope the default token lacks:

```bash
gh auth refresh -s read:project,project
```

This is **interactive** — it prints a one-time code and waits for a browser
authorisation, so the maintainer must run it; it cannot be done from a tool call.
Verify it actually landed with `gh api -i user` and read the `X-Oauth-Scopes` header,
**not** `gh auth status` alone. Approving the app in GitHub settings does *not* update
the stored token; only completing `gh auth refresh` does.

The trap: without the scope, `gh project` errors *but*
`gh issue view --json projectItems` returns an **empty list rather than an error** — so
every issue reads as "not on the board". Never conclude anything about board membership
from a token that cannot see projects.

**Browser fallback** (works today, no scope needed — the maintainer's Chrome is signed
in). Two routes, and the second is far faster:

- *Per issue:* open the issue, click **Edit Projects** in the sidebar, click the
  "RimSynapse" option, then press **Escape**. Do not dismiss with a synthetic
  `document.body.click()` — that discards the selection. The sidebar text may not
  refresh immediately; reload and look for `Projects | RimSynapse | Status | Backlog`.
- *Bulk (preferred):* open the board, click **Add item** at the bottom of the Backlog
  column once, then repeatedly `type` a full issue URL and press `Return`. The input
  stays focused between adds, so ten issues are ten type/Enter pairs with no extra
  clicks and no navigation.

**Verifying the board is unreliable if done naively** — two independent reasons:

1. The default view is **filtered** (`iteration:@current or status:Backlog`), so counts
   are filtered counts, not board totals.
2. Columns are **virtualised**: only the visible window of cards exists in the DOM, so a
   card that is present will read as missing. Scroll the column and accumulate text
   across scroll positions before concluding anything is absent — a scan without
   scrolling reported 5 of 11 items missing that were all in fact there.

Real mouse actions (`computer` clicks/keys) work on these React controls; synthetic
`.click()` from injected JS frequently does not.

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

## Extension surfaces: who owns a mechanic

**The mod that introduces a mechanic owns its state and its logic.** Core does not
store other mods' data — it brokers access to it. A consumer asks Core and gets a
documented answer whether or not anybody registered. The test of whether this is
being followed: **a producer mod must build and run with Core absent.**

Core brokers two kinds of extension. They are not interchangeable, and picking the
wrong one is how a fifth undocumented convention gets invented:

| | Broadcast hook | Provider |
|---|---|---|
| Subscribers | many | exactly one authority |
| Direction | push | pull |
| Returns | nothing | a value |
| Where | events on `SynapseCoreContext`, `SynapseLetterContextHook`, `ContextAssembler` | `SynapseCoreProviders` |
| Use when | several mods may each contribute something | one mod owns the question |

Rules for provider slots:

- Every slot is a **public static property**, nothing more exotic. Producers can only
  reach it by reflection (`GenTypes.GetTypeInAnyAssembly` → `GetProperty` → `SetValue`),
  because they must not reference Core's assembly. A slot whose registration needs a
  Core type in its signature defeats the point.
- Every slot **documents its unregistered value**, and consumers call the accessor
  (`PopulationDensityAt`, `IsResident`) rather than null-checking the slot. Consumers
  inventing their own fallbacks is how two callers end up disagreeing about what
  "nobody answered" means.
- Registration is logged; so is the first use of an unregistered slot, **once**. These
  are read from storyteller weighting passes, so a per-call log line is a performance
  bug as well as noise.
- A throwing provider is contained and logged — it must not take its caller down.
- Producers log all three branches: registered / member missing / Core absent. See
  `RegionsAndTerritoriesMod.TryRegisterPopulationDelegate`, which is the reference
  implementation.

Deprecating a slot follows the binary-compatibility rule above: keep the old member as
a forwarding shim for one release and mark it `[Obsolete]` — see
`SynapseCoreWorldComponent.GetPopulationDensityDelegate`, which
`SynapseCoreProviders.PopulationDensity` still falls back to.

`Tests/run-tests.sh` covers this surface without a game.

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

## Release flow

Run these in order. Every step exists because skipping it has shipped a defect.

1. **Changelog into both description copies.** Add the release entry to
   `About/steam_description.txt` **and** the `<description>` block inside `About/About.xml`,
   and bump the version line in each plus `<modVersion>`. These are separate copies of the
   same text — updating one and not the other is exactly how v0.6.0 shipped showing v0.5.2
   in the in-game mod list. Update the roadmap in both when it changes.
2. **Check the docs captured the change.** Anything a player or mod builder needs to know
   belongs in `Learning/*.md` (injected into RimWorld's Learning Helper) and, for builder
   docs, in `docs/COMPANION_MODS.md` — these mirror each other and must stay identical.
3. **Push the GitHub wiki** with `.\harness\sync-wiki.ps1` (`-WhatIf` to preview). The
   wiki is a separate git repo (`<repo>.wiki.git`) whose source is `Learning/`; nothing
   syncs it automatically, so the "Official Wiki" link in every description goes stale
   on its own. The script also deletes pages whose source file is gone, so renamed docs
   stop being served under their old titles.
4. **Run the gate** (below), fixing anything it reports.
5. **Commit and push to `development`**, then open and merge the release-promotion PR into
   `main` (use a merge commit — a release should keep its history).
6. **Tag** `vX.Y.Z` on `main` and publish the GitHub Release; attach Core's DLLs and
   `CHECKSUMS.sha256`.
7. **Update the live Steam Workshop description for every mod** from
   `About/steam_description.txt`. The local file is only the source of truth — it changes
   nothing on the Workshop until it is pasted into each item's page. Needs a
   Steam-authenticated browser: the in-app browser is not signed in, so this requires the
   Claude in Chrome extension to be connected (`list_connected_browsers` returning empty
   means it is not). Edit URL is
   `https://steamcommunity.com/sharedfiles/itemedittext/?id=<PublishedFileId>`.

   Mechanics that cost an hour to work out the first time:
   - **8000 characters, enforced silently.** Over the cap, Steam accepts the paste, Save
     looks like it works, and the live page keeps the old text. `verify-metadata.ps1`
     now fails on this, but check it before wondering why a save "did nothing".
   - Set `textarea[name="description"].value`, fire an `input` event, then call
     `ValidateForm()` — the Save control is `<a href="javascript:ValidateForm()">` and a
     synthetic `.click()` on it does nothing. Wrap it in `setTimeout(...,50)` so the
     navigation does not time out the CDP evaluate.
   - Verify by SHA-256: hash the textarea value and the local file (both normalised to
     `\n`, trailing whitespace stripped) and compare. Length alone is not proof.
   - Fetching the file from raw.githubusercontent.com inside the page is blocked by
     Steam's CSP, and the sandbox cannot reach the clipboard — the text has to be
     inlined into the injected script.

   | Mod | Workshop ID |
   |---|---|
   | Core | 3760829776 |
   | Psychology | 3760830041 |
   | Conversations | 3768363934 |
   | Factions | 3767279097 |
   | WorldNews | 3768365293 |
   | Regions-and-Territories | 3768364266 |
   | NVIDIA-Tool | 3760830285 |
   | AuraAlgorithm | 3768364958 |
8. **Tell the user to upload the new builds** through the in-game uploader (Core first —
   companions declare a dependency on it). This is the one step that cannot be automated:
   there is no reliable CLI path, so the release is not actually live until they do it.
   Re-run `verify-binaries.ps1` immediately beforehand, since the uploader takes whatever
   is on disk. Per-mod Change Notes text lives in `About/steam_change_notes.txt`.

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
