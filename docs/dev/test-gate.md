# The test gate (hooks)

Detail behind the short rules of [`CLAUDE.md`](../../CLAUDE.md). Both hooks run `.claude/hooks/test-gate.ps1`; the suites
are described in [`StandaloneTests/README.md`](../../StandaloneTests/README.md).

## After every edit (Claude Code hook)

`.claude/settings.json` runs `test-gate.ps1 post` after a tool call: `StandaloneTests/run-all.ps1` if the engine or test
sources changed, and a compile check of the Unity code **without the editor** (`StandaloneTests/unity-compile-check.ps1`,
~10 s) if any C# under `Assets` changed. When it reports a failure, fix it before continuing (don't pile more edits on a
broken tree). The compile check works while someone else has the editor open.
- It costs ~0.5 s when nothing changed (one directory walk before the lock); `run-all.ps1` builds the engine once and
  runs the suites in parallel (~9 s).
- A failure is reported once per fingerprint; the following calls stay quiet until the sources change, and a change
  re-runs the check. Silence after a failure is therefore not a pass: re-run `run-all.ps1` if in doubt.
- It never waits for a pre-commit run and waits 2 minutes at most for another post run, then skips (the next call
  checks again).

## In CI

`.github/workflows/standalone-tests.yml` runs `StandaloneTests/run-all.ps1` on GitHub Actions for every push and pull
request on `main`. The EditMode tests need Unity and run only locally.

## Before every commit (git `pre-commit`)

`.githooks/` — enable once per clone with `git config core.hooksPath .githooks`; it applies to console, GitHub Desktop
and Claude commits.
1. The standalone tests must pass (in parallel).
2. Then the Unity EditMode tests: a **full run in the open editor** (`SimuLeanTestRunner.Start("")`, no filter) that
   passed with no skipped test counts while nothing it covers changed since it started (a working-tree fingerprint taken
   at its start; `Start` refreshes the assets first, so an edited script is compiled before it is tested). So with the
   editor open, run the full suite there just before committing, after your last edit; otherwise they run in batch mode
   (a few minutes) when the editor is **closed**.
   - After an engine change the run first builds the external runner of the new engine version
     (`Library/SimuLeanRunner/<fingerprint>`, ~40 s) — the batch run, and `SimuLeanTestRunner.Start` in the open editor
     when the run includes `ExternalRunnerTests` (a full run always does): `Status()` says `"phase": "building runner"`
     meanwhile, the tests start once it is built, and a runner that cannot be built fails the run (so one full run after
     an engine change is enough); a release's prebuilt runner (`SimuLeanRunner/<rid>/`, not in this
     repository) is used only when built from the same engine sources.
   - With the editor open and no such run the commit goes ahead with a warning and the EditMode tests stay pending.
   - The EditMode verdict is cached on the git index; when code (`.cs` / `.asmdef`, baselines, manifest) has unstaged
     changes or untracked scripts the pass is not cached (the tests saw code that is not committed).
   - In a linked git worktree the EditMode tests are skipped (no imported Library): run them in the main checkout after
     merging.

Never use `--no-verify` or `SIMULEAN_SKIP_UNITY_TESTS=1` unless the user explicitly asks.

## When the EditMode tests stay pending

The editor is open and a full in-editor run is not possible (MCP down, a scene of the user's modified, …). Don't
silently move on:
- tell the user in chat that the batch EditMode tests are pending and ask them to close Unity (save their scenes first —
  never close or save the editor yourself);
- wait for it in the background (e.g. `Monitor` until `Temp/UnityLockfile` is no longer locked, ~15 min at most), and
  meanwhile go on with work that does not need the editor;
- once the editor is closed, run `powershell -File .claude/hooks/test-gate.ps1 precommit` (it runs the batch EditMode
  tests and caches the verdict), report the result, and tell the user they can reopen Unity;
- if the user is away (no close within the wait), leave the tests pending, say so in the end-of-task summary, and keep
  working; they run on the next commit made with the editor closed.

Running them in the open editor through the MCP: [`unity-mcp.md` — *EditMode tests in the open editor*](unity-mcp.md#editmode-tests-in-the-open-editor).
