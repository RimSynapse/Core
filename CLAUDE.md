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

### What goes on the board

Every open issue in the **active and next milestone** goes on the board. Older-milestone
and unmilestoned issues may go on it but are not required to. Closed items stay on the
board in **Done** rather than being archived — the board's job is to show that work
shipped, and an archived card cannot do that.

## The release train — what rides it, and what does not

Not every issue belongs to a release. Four categories, carried as labels in every repo,
and they are treated differently:

| Label | On the train? | Rule |
|---|---|---|
| `release-target` | **yes** | The feature work the milestone exists to deliver. Milestones are scoped around these and nothing else. |
| `harness` | no | Test and build tooling. Worked **opportunistically** — ideally in the release it is discovered in, otherwise pushed to the next. If one grows too expensive or starts holding a release, push it again. |
| `process` | no | Build, CI and release process. Same rules as `harness`. |
| `documentation` | **gate** | Not scheduled as features. Every outstanding docs issue is **evaluated and closed, committed and pushed, before a release goes out** — docs are part of the final review gate, not a milestone item. |
| `qol` | no | Backlog. Opportunistic, and below `harness` in priority. Carries no milestone. |

A `harness` or `process` issue sitting on a milestone means "try to get this done by
then", not "this release is defined by it". Pushing one to the next milestone is a normal
outcome, not a slip — the failure is letting it block a release, or letting it fall out of
sight entirely.

### Priority order

Steady state:

1. **Harness blockers** — a harness defect that makes results untrustworthy outranks
   everything, because everything else is measured with it.
2. **Release targets**
3. **Harness discoveries** — newly found tooling defects that are not blocking
4. **Process**
5. **QoL**

While cutting a release, the order changes:

1. **Process** — the gates, the version bumps, the tag and merge
2. **Documentation** — evaluate and close every outstanding docs issue first
3. Everything else

The reason harness sits at both the top and the middle: a harness issue that makes a
run lie is a blocker, and a harness issue that is merely annoying is not. Judge by whether
it can produce a false green, not by which component it lives in.

## Release gates — which run in CI, which are yours to run

Three gates exist, and each was written after the defect it catches had already shipped.
Two now run in CI (`.github/workflows/release-gates.yml`, in every mod repo) on any PR
and any push to `development` touching `About/` or `Learning/`:

| Gate | Where it runs | What it catches |
|---|---|---|
| `verify-metadata.ps1` | **CI** | the three version locations disagreeing; `steam_description.txt` over Steam's silent 8000-character cap |
| `sync-wiki.ps1 -WhatIf` | **CI** | `Learning/` docs not published to the repo wiki |
| `verify-binaries.ps1` | **manual, pre-tag** | a committed DLL that does not match a fresh build of its own source |

`verify-binaries.ps1` cannot run on a hosted runner: it needs a build against RimWorld's
reference assemblies, which are not available there. **Run it by hand before every tag.**

**Two things CI does not catch, so do not read a green run as more than it is:**

- `verify-metadata.ps1` checks that the three version locations agree with *each other*,
  not that they agree with the source tree. A repo has passed the gate while declaring
  `0.6.2` over a 0.7 source tree — internally consistent, and wrong. A version bump is
  still a human decision at cut time.
- `sync-wiki.ps1` **skips** a repo whose wiki has never been initialised, and a skip is a
  pass. NVIDIA-Tool is skipped today. GitHub only creates the wiki repo after the first
  page is made in the web UI, so an uninitialised wiki is invisible to the gate rather
  than caught by it.

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
- Producers log all three branches: registered / member missing / Core absent. The
  producer-side pattern is documented in `docs/COMPANION_MODS.md` §13 (the former
  in-org reference implementation migrated out with the territory mod).

Deprecating a slot follows the binary-compatibility rule above: keep the old member as
a forwarding shim for one release and mark it `[Obsolete]` — see
`SynapseCoreWorldComponent.GetPopulationDensityDelegate`, which
`SynapseCoreProviders.PopulationDensity` still falls back to.

`Tests/run-tests.sh` covers this surface without a game.

## Build and test loop

The harness lives in `rimworld-claude-dev-tools` (`harness/*.ps1`; loaded globally via Claude
settings, local copy at `C:\github\rimworld-claude-dev-tools` — this was forked out of the now-deprecated
`Repo-MCP`); the in-game suite is the `TestRunner` repo (loads last, only active with `-synapse-test`).

```powershell
.\harness\build.ps1              # all mods, dependency order: Core -> companions -> Factions
.\harness\launch.ps1 -Test       # rotates Player.log, runs the TestRunner, self-terminates on SUMMARY
.\harness\readlog.ps1            # classifies the log; exit 1 on blocking entries or FAILed cases
```

- **Every behavior change gets a TestRunner case** (`<Repo>_<CaseName>`). The suite
  must end `0 blocking`.
- Under `-quicktest` the LLM is **mocked** in `SynapseClient` (canned responses,
  keyword-branched). Tool tests are deterministic; live-LLM behavior is not covered.
- A `build.ps1 -Repo X` rebuilds only X and its deps — after public-surface changes
  in Core, always do a **full** build so companions recompile against it.

## The in-game tool bridge (file IPC)

`list_game_tools` and `execute_game_tool` reach a running game through **files**, not a
socket. Both sides resolve the directory independently, and if they disagree the only
symptom is a ten-second timeout — so the contract is written down here.

**Game side** — `SynapseGameComponent.ScriptingDir`, in priority order:

1. `%RIMSYNAPSE_ROOT%\Core`, if the env var is set and that directory exists
2. the Core mod's own `Content.RootDir` (whatever folder RimWorld loaded Core from)
3. `GenFilePaths.ConfigFolderPath` — the fallback, and note this follows
   `-savedatafolder`

**MCP side** — `rimworld-claude-dev-tools/server/src/tools/gameIpc.ts`: `workspaceRoot()\Core`, where
`workspaceRoot()` also honours `RIMSYNAPSE_ROOT`.

They line up because both prefer `RIMSYNAPSE_ROOT`, which the manifest passes to the MCP
server and the game inherits when the server spawns it. They stop lining up if that var is
unset on one side, or if RimWorld loads Core from somewhere unexpected — the Workshop copy
rather than the local one, say.

**Do not infer which directory is in play — read it.** Core logs it once at first
resolution:

```
[RimSynapse] Tool bridge polling directory: C:\github\rimsynapse\Core (resolved via RIMSYNAPSE_ROOT).
```

Files exchanged in that directory: `tool_input.json` / `tool_output.json`,
`script_input.json` / `script_output.log`, `game_state_request.json` / `game_state.json`,
`storyteller_input.txt` / `storyteller_output.log`. All are runtime scratch and gitignored;
none are source.

**The poll runs from `GameComponentUpdate`**, so it only fires once a `Game` exists and
only while the main thread is actually updating. A request written and never consumed means
the game is not polling — it is pre-game, or hung — not that the paths are wrong. That
distinction cost three investigations; check `Responding` on the process before suspecting
the path.

## Log conventions (the classifier reads these)

- Handled warnings must not look like thrown exceptions: log the type as
  `[JsonSerializationException] message`, never `JsonSerializationException: message`.
- Tool handlers report failure as an `{"error": ...}` payload, never by throwing;
  callers must not log error payloads under a `[Result]` prefix.
- Test output goes through `Log.Message` (never `Log.Error` — it double-counts as a
  blocking entry). Format: `[SYNAPSE-TEST] PASS|FAIL <case> | <detail>`.
- **`Log.Error` is now classifiable by level.** `Patches/Patch_Log_Error.cs` prefixes
  every `Verse.Log.Error(string)` with `[SYNAPSE-LOGERROR]`, and `readlog.ps1` keys on
  that token (Repo-MCP#17) — a `Log.Error` from any mod, in any wording, is caught. Two
  consequences: (1) the marker exempts `[SYNAPSE-TEST]` output and is idempotent, so keep
  it that way if you touch `Patch_Log_Error.Mark`; (2) this made previously-invisible
  errors visible, so a first run after a Core rebuild can surface a real error that older
  "0 blocking" runs hid — triage it, do not re-hide it in the classifier. It surfaced
  183 world-gen `Faction.OfPlayer` calls in a companion on its first outing. Errors logged
  before `harmony.PatchAll()` are unmarked; the load-order check runs first by design and
  carries its own token.
- **Querying `Faction.OfPlayer` when it can be null spams a `Log.Error` each time** —
  vanilla `FactionManager.OfPlayer` logs "Could not find player faction." on null. Use
  `Faction.OfPlayerSilentFail` on any path that can run during world generation or before
  a game exists; `?.` does not help, since the getter logs before the null-conditional.
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
6. **Tag** `vX.Y.Z` on `main` and publish the GitHub Release; attach Core's DLLs,
   `CHECKSUMS.sha256`, and the RimSort-installable payload zip —
   `harness\package-release.ps1 -Repo <mod> -Tag vX.Y.Z -Upload` (every repo, every
   release: RimSort installs from GitHub only via a `.zip` release *asset*, not the
   auto-generated source archive — #109).
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
.\harness\verify-branches.ps1          # nothing finished is sitting outside development
.\harness\verify-metadata.ps1          # version + changelog agree in all three places
.\harness\verify-binaries.ps1 -Build   # rebuild, then check every shipped DLL
.\harness\release-manifest.ps1 -Repo X # regenerate the repo's Assemblies/CHECKSUMS.sha256
```

`verify-branches.ps1` runs **first**, because everything after it verifies the wrong
tree if a finished fix is sitting on a branch. It answers two questions, and the second
is the one that matters: is a pull request open, and does any branch carry commits
`development` lacks? A branch needs no PR to be lost. Psychology's counseling report path
stayed hardcoded to `d:\github\rimsynapse\...` on `development` — failing on every other
machine, silently, because the write is inside a `try` — while the fix sat finished on an
unmerged branch. There were zero open PRs the entire time.

**It reports; it never merges.** Commit counts lie after a squash merge: Core's
`fix/wiki-concept-knowledge-rebind` showed 7 commits "not in development" while every one
of its changes was already there by another route, with `development` since moved past
it. The report names the files each branch would touch so that pending-versus-superseded
call is cheap to make. Branches already merged to `main` are release records and are not
reported.

**Every mod states its version in three independent places** and they drift silently:
`About.xml <modVersion>` (read by mod managers), `About.xml <description>` (the in-game
mod list blurb), and `About/steam_description.txt` (the Workshop page). v0.6.0 shipped
with the first bumped and the second still reading v0.5.2 with no 0.6.0 notes — the
embedded description is easy to forget precisely because it duplicates the Workshop copy.
`verify-metadata.ps1` fails when they disagree, when the description's changelog has no
entry for the current version, or when About.xml stops being well-formed.

- **No repo tracks built DLLs** (decided 2026-08-23, #110, effective with the 0.10 wave —
  supersedes #95's track-everything decision): binaries ship in each release's
  RimSort-installable zip asset, which `package-release.ps1` builds from the tag's tree,
  injecting locally built DLLs only after each one's SHA256 matches the tag's committed
  `Assemblies/CHECKSUMS.sha256`. Every repo tracks that manifest; regenerate it in the
  release commit with `release-manifest.ps1 -Repo <mod>`. The hazard is unchanged —
  source landing without a rebuild — and so is the gate: `verify-binaries.ps1 -Build`
  fails when a disk DLL differs from a fresh build of its own source, and untracked DLLs
  verify against the manifest. Builds are deterministic (a fresh build reproduced the
  v0.9.0 checksums byte-for-byte), so any release is reproducible from its tag.
- The accepted cost: a plain clone is **not** playable without a build — install from
  the release's zip asset instead. Re-run `verify-binaries.ps1` immediately before a
  Workshop upload to confirm the files on disk are byte-for-byte the released build.

## Branch discipline

- **Developing on this machine, commit directly to `development`.** It is a
  single-maintainer project: a PR nobody reviews is a queue, not a gate. Five accumulated
  in one session behind a permission the assistant did not have, which delayed the work
  without improving it. Use a branch only when the change genuinely needs isolating — a
  risky refactor, or two approaches to compare side by side — not by default.
- Work can still arrive on a branch from elsewhere: the gaming PC, an older session, a
  branch opened for isolation and then forgotten. `verify-branches.ps1` in the release
  gate is what finds it, because nothing else will.
- The discipline the PR was standing in for still applies, and it is the important part:
  **build, run the full suite, and confirm `0 blocking` before pushing.** Multi-repo
  changes must land in dependency order (Core → companions → Factions) so a
  mid-sequence build is never broken.
- `main` only via release promotion PRs — that gate is real, because it marks a release.
- Versioning is `0.<iteration>.<minor>`; never plan or tag anything `1.0`.
- Built `Assemblies/*.dll` are never committed (from 0.10 — #110); the tracked record is
  `Assemblies/CHECKSUMS.sha256`. `Source/obj/` is never tracked.
