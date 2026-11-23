# Contributing to SimuLean

Thank you for helping. This page is what you need to change SimuLean itself — the engine, the Unity layer or the
tools. To build a model *with* SimuLean, start with the [README](README.md) instead.

## Set-up

| What | Needed for |
|---|---|
| **.NET 10 SDK** | the standalone test suites (no Unity) and the runner `simulean` |
| **Unity 6.6** (the version in `ProjectSettings/ProjectVersion.txt`) | the Unity layer and its EditMode tests |
| **PowerShell** — Windows PowerShell, or [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell) (`pwsh`) on macOS / Linux | the test scripts and the git hook |
| **Python 3** (optional) | the check that `docs/agents/commands.md` is current |

SimuLean is developed and tested on Windows; the engine and its suites are plain .NET.

## Tests

- **Standalone suites** — the engine's tests and the reference models against their baselines, without Unity (about a
  minute the first time):

  ```bash
  pwsh -File StandaloneTests/run-all.ps1
  ```

  (`powershell -File …` on Windows.) What each suite checks: [`StandaloneTests/README.md`](StandaloneTests/README.md).
- **Unity EditMode tests** — open the project in Unity, then *Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All*.
- **Git hook** — enable once per clone; every commit then runs the standalone suites and the EditMode tests (in batch
  mode when the editor is closed):

  ```bash
  git config core.hooksPath .githooks
  ```

  Without PowerShell the hook skips the tests and says so. Details:
  [`docs/dev/test-gate.md`](docs/dev/test-gate.md).

A change that alters seeded results on purpose regenerates the reference baselines
(`dotnet run --project StandaloneTests/referencemodels -- --update`) and says why in the commit; a baseline never
changes as a side effect. A bug fix comes with a test named after the behaviour it checks.

## Code rules

The full list, with the reasons, is in [`CLAUDE.md`](CLAUDE.md) and [`docs/architecture.md`](docs/architecture.md). The
ones a contribution most often meets:

- **The engine has no Unity reference.** `Assets/SimuLean.Net` is its own assembly; the compiler enforces it. Log with
  `SimLog`; anything that reads Unity components belongs in the Unity layer.
- **One build path.** Unity components never build engine objects: the scene is extracted, the engine handlers build
  the model, then each component is bound as its element's view. A new element type needs an engine handler and a Unity
  extractor, both registered.
- **Statistics only where they belong.** Item flow (input / output, content, stay times) comes only from the `Link`
  layer; resources and lists keep their own statistics where their state changes. Reports, exports and metrics read
  them only through `RunResults` and the `ResultMetrics` names — a new statistic is added there.
- **Random numbers come from the model**: one stream per element, keyed by name, so runs are reproducible for a seed.
  Never `System.Random` or `UnityEngine.Random` in model code.
- **Every property is parameterisable**: a new text or list setting declares its kind, or the parameter-catalog tests
  fail.
- **Comments say why, not when**: no dates, review or phase ids in code comments or test names — the history is in git.
- **No project-specific content**: no customer names, plant data, drawings or calibrated parameters. Models of a real
  system live in their own repositories and use SimuLean through its public API.
- **Keep the docs in step**: a change to the architecture updates `docs/architecture.md` in the same change.

## Commits and pull requests

- Conventional commit messages: `feat(engine): …`, `fix(unity): …`, `docs(…): …`, `test(…): …`. Say what changed for
  the user of SimuLean.
- One topic per pull request; the standalone suites must pass (CI runs them on every pull request) and the EditMode
  tests should pass locally.
- Commit only your change: not your own scenes, `UserSettings/` or editor settings.

## AI agents

- An agent **changing SimuLean** (engine, Unity layer, tools) follows [`CLAUDE.md`](CLAUDE.md).
- An agent **building or studying a model** follows [`AGENTS.md`](AGENTS.md) and [`docs/agents/`](docs/agents/workflow.md).

## License

By contributing you agree that your contribution is released under the project's [GNU GPLv3](LICENSE).
