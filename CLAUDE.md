# CLAUDE.md

Guidance for Claude Code (and other AI assistants) working in this repository.

**Building or studying a model with SimuLean** (not changing SimuLean itself)? Read [`AGENTS.md`](AGENTS.md) and
[`docs/agents/`](docs/agents/workflow.md) instead; this file is for work on the engine, the Unity layer and the tools.

## Version control

- Commit a change once it is complete and its tests pass, with a conventional message (`feat(…)`, `fix(…)`,
  `docs(…)`). The git pre-commit gate (below) must pass — never `--no-verify` / `SIMULEAN_SKIP_UNITY_TESTS=1`
  unless the user explicitly asks.
- Stage the files of the change by name, never `git add -A` or a folder; never commit `UserSettings/`, `.vscode/` or
  someone's own edits to the example scenes. A modified tracked scene / prefab / material: check its diff first.
- Push, amend, rebase, reset or any other history-altering command only when the user asks for it.
- You may freely edit files in the working tree and run read-only git commands (`git status`, `git diff`, `git log`).

## Project

**SimuLean** — a C# discrete-event simulation (DES) engine, its **UnitySimuLean** Unity integration layer, and an
**optimisation** module (pluggable algorithms in the engine, `IOptimizationAlgorithm`). Target editor: **Unity 6.6
(6000.6.4f1)**. The engine (`Assets/SimuLean.Net`) is its own assembly with **no Unity reference** (`SimuLean.Net.asmdef`;
no third-party numerics); everything else compiles into Unity's `Assembly-CSharp` / `Assembly-CSharp-Editor`.

## Architecture documentation — read what you touch, keep it in sync

- **Before a change, orient with [`docs/project-overview.md`](docs/project-overview.md) and read the sections of
  [`docs/architecture.md`](docs/architecture.md) that cover what you will touch** (its contents list; the table below).
  `architecture.md` is the authoritative description and wins over the overview on any conflict.
- **When you change the architecture, update `architecture.md` in the same change** and say what changed and why — it is
  part of the definition of done. That is: an `Element` type, `IVElement`, `ILink` or distribution added / removed /
  renamed; event scheduling or `SimClock` / `AdvanceClock`; the link layer or the statistics mechanism; the extract →
  serialize → rebuild → run pipeline or the `SimulationConfig` DTOs; a Unity wrapper (`Unity*`), the `SElement` binding
  (`Bind` / `OnBound` / `OnRunStarted` / `ClearViews`), `SceneModelBuilder` or the `UnitySimClock` loop; the optimisation
  core or the threading / dispatcher model; a new subsystem, dependency or assembly boundary; any item of its "Known
  caveats" (§8) — reconcile that list too.
- Bump its "Last verified against the codebase" date when you confirm it matches the code; keep that line to the date and
  one short clause (the history is in git).

## Layout

- `Assets/SimuLean.Net/` — core engine (namespace `SimuLean`).
- `Assets/SimuLean.Unity/` — Unity integration (namespace `SimuLean.Unity`).
- `Assets/SimuLean.Optimization/` — the Unity side of optimisation (`Optimizer` component, scene specs); the algorithms
  are in the engine (`SimuLean.Net/Optimization`).
- `Assets/SimuLean.Experimentation/` — batch experiment runner.
- `Assets/Docs/` — feature-level documentation; `docs/dev/` — the development procedures behind this file.
- `Tools/Blender/` (script-built 3D models), `Tools/FloorPlan/` (CAD drawing → floor-plan texture), `Tools/Runner/` (the
  out-of-editor runner `simulean`) — each with a README.

## Where to read for X

| Topic | Read |
|---|---|
| Calendar, stops / failures / shifts (`Element.Stop`, `ScheduleWork`, generators) | `docs/architecture.md` §3.1, §3.12; `Assets/Docs/Downtime_And_Calendar.md` |
| Signals and reactions (`SimContext.Signals`, `Reaction`, `UnityReaction`) | `docs/architecture.md` §3.21; `Assets/Docs/Signals_And_Reactions.md` |
| Length-limited lanes (`LengthLimitedQueue`) | `docs/architecture.md` §3.13 |
| Result tables / JSON (`ResultTable`, `MiniJson`) | `docs/architecture.md` §3.8 |
| Run results / statistic names (`RunResults`, `ResultMetrics`) | `docs/architecture.md` §3.11 |
| Paced clock, camera, HUD, element inspector, floor plan | `docs/architecture.md` §4.3, §4.7.2, §4.9; `Assets/Docs/Optional_Runtime_Tools.md` |
| Lists (push / pull, priority queues) | `docs/architecture.md` §3.16 |
| Regression net: reference models, baselines, `run-all.ps1`, EditMode tests | `docs/architecture.md` §3.15; `StandaloneTests/README.md`; [`docs/dev/test-gate.md`](docs/dev/test-gate.md) |
| Building / crane kit and 3D models | `docs/architecture.md` §4.7.1, §4.8; `Tools/Blender/README.md` |
| Driving the open editor (Unity MCP), EditMode tests there | [`docs/dev/unity-mcp.md`](docs/dev/unity-mcp.md) |

## Conventions & gotchas

- **Statistics have two sources, nothing else:**
  - **item flow** (input / output, content, stay times) comes only from the `Link` layer (`OnEntry` / `OnExit`) — route
    any new item-movement path through a `Link` or stats will be wrong;
  - **resources and lists** (utilisation, time per state, tasks, usage per station; list content, waits, field values)
    come from their own state tracker / statistics objects, updated where the state changes (`SetState`, push / pull).
    Never compute either kind twice or from another layer;
  - **reading them** (reports, exports, metrics, baselines, the scene's headless run) goes only through
    `RunResults.Capture` and the `ResultMetrics` names (architecture §3.11) — add a new statistic there, not in a report.
- **One build path.** Unity components never build engine objects: a scene run extracts the scene (`SceneExtractor`), the
  engine handlers build the model (`SceneModelBuilder` → `HeadlessModelFactory`) with each component as the view of its
  element, then bind it (`OnBound`). New element parameters go in the extractor and the handler only.
- **The engine cannot use Unity** (the compiler enforces it; headless runs rely on `HeadlessVElement`): log with `SimLog`,
  put anything that reads Unity components in the Unity layer. A new element type needs an engine `<Type>Handler` **and**
  a Unity `<Type>Extractor` (`SimuLean.Unity/Serialization/Extractors`), registered in both registries (architecture §3.5).
- **Every property is parameterisable.** The optimiser / Experimenter catalog walks every config key: numbers, bools and
  number arrays by their type; a **text or list key must be declared** through its setting's `[Setting]` / `ParameterSchema`
  kind (`Choice`, `Sampler`, `NumberList`, `Numbers`, `Properties`, `Expression`, `Structural`; team / downtime settings
  by the `[Setting]` of `ResourceTeamConfig` / `DowntimeSettings`). The gate fails on an undecided key
  (`ParameterCatalogTests`, `optimizationtests`) — decide it, never list parameters by hand (architecture §3.19).
- **Random numbers: one stream per element, keyed by name.** Every sampler gets its stream from the
  model: `clock.Context.AssignRandomStream(sampler, elementName, purpose)` / `CreateRandomStream(owner, purpose)` (engine),
  `ElementBuildHelpers.BuildRandomProcess` / `SamplerOf` / `SamplerSpec.Decode(spec, clock, owner, purpose)` (handlers).
  Unity wrappers never build samplers. Never draw from `RandomEngineManager.GlobalEngine`, `System.Random` or
  `UnityEngine.Random` in model code. Runs are reproducible for a model seed (`new SimClock(seed)`,
  `SimulationConfig.UseFixedSeed`, `UnitySimClock` *Fixed* mode); unseeded runs record their seed (`SimContext.Seed`).
  Anything that changes how many numbers a stream draws, or in what order, changes every seeded result: regenerate the
  reference baselines on purpose, never as a side effect.
- **Timed work goes through `Element.ScheduleWork`**, not `SimClock.ScheduleEvent`, so downtime (`Element.Stop`) can pause
  it; keep `ScheduleEvent(event, delay)` — the second argument is a **delay from now** — for events that are not an
  element's work (arrivals, timetables). Downtime blocking is enforced in `GeneralLink` and the output strategies
  (`CanAccept`) — don't bypass them.
- **`AdvanceClock(t)` leaves the clock at `t`** (not at the last event), so end-of-run statistics include the final interval.
- **Scene set-up vs. optional components.** Only the editor-only `SimuLeanSceneSetup` adds components (the `UnitySimClock`
  with `SimRuntimeHud`, `ElementInspectorPanel`, `SimVisualDetail`; a `SimCameraController`; a 100 × 100 m `SceneFloor`
  when there is no ground) — in new scenes, on first load, when the clock is created, and from *Tools ▸ SimuLean ▸ Set Up
  Scene* (switch *Auto Set-Up New Scenes*, `ProjectSettings/SimuLeanSettings.json`). Never add anything at run time
  (`RuntimeInitializeOnLoadMethod`); keep the other optional pieces (`FloorPlanUnderlay`, building kit, 3D models, paced /
  max-speed clock) opt-in; new `UnitySimClock` options default to the current behaviour. Views, inspector contributors
  (`IElementInspectorContributor`) and other observers stay read-only (no events scheduled, no items moved).
- **Unity object lookup / identity:** never call `FindObjectOfType`, `FindObjectsOfType`, `FindFirstObjectByType`,
  `FindObjectsByType`, `FindAnyObjectByType`, `GetInstanceID` or `GetEntityId` directly — use `SimuLean.Unity.UnityCompat`
  (`FindAny<T>()`, `FindAll<T>()` (unsorted), `FindAll<T>(includeInactive)`, `ObjectId(obj)`), the one place to adapt
  when Unity changes these APIs (architecture §7). Unity 6.6 only: no guards for older versions.
- **Register what you add in the SimuLean window (Library tab):** a new model-element wrapper (`SElement`) carries
  `[SimuLeanElement(SimuLeanModules.X, "Name", Description = …)]` (an EditMode test fails otherwise); a new non-model scene
  component the user creates / edits (pools, lists, generators, reports…) carries `[SimuLeanTool(SimuLeanToolCategories.X,
  "Name", …)]` (architecture §4.6.1). The window and the `GameObject/SimuLean/…` menu are generated from them — never
  hard-code entries. Sector content belongs to a pack (`SimuLeanPacks`; `Pack = …` / `[SimuLeanPresetPack]`); general
  content stays in Core.
- **The `SimuLeanModelPostprocessor` only handles library folders** under `Art/Models/` that contain
  `<Folder>_Albedo.png`; keep hand-made models in folders without such a file.
- **Comments say why, not when.** No dates, review / battery / round ids or phase names in code comments or test names
  (the history lives in git); name tests after the behaviour they check.
- **No case-specific or confidential content** (customer names, plant data, drawings, calibrated parameters) in this
  shared repository; case studies live in their own repositories and use the engine through its public API.

## Working through the Unity MCP

Detail and the full command list: [`docs/dev/unity-mcp.md`](docs/dev/unity-mcp.md).
- **Build and study models with the `simulean_*` commands, never `eval`** (loops, reflection or C# to build, run or
  inspect a model); a new command argument needs a concept in `Optimization/Vocabulary.cs`. After adding or changing a
  command run `python Tools/Docs/command_reference.py` (`run-all.ps1` fails when `docs/agents/commands.md` is stale), and
  keep `AGENTS.md` / `docs/agents/` in step.
- **One Unity MCP server per project** (`.mcp.json`); don't drive the same editor from two servers.
- **Long jobs:** `simulean_run_start` / `simulean_optimize_start`, then `_status wait=50` — a long synchronous run times
  the MCP out and blocks every following call. Never sleep in `eval`.
- **EditMode tests:** `simulean_test` / `simulean_test_status`, or `eval`
  `SimuLean.Unity.Editor.SimuLeanTestRunner.Start("<filter>")` / `.Status()`. **Never the MCP's `run_tests`** (a modified
  scene opens a modal dialog that blocks the editor). The first call after a recompile may fail: retry.
- **Captures:** `simulean_capture`, not cameras or `capture_game_view` into `Assets`. **A blocked editor:** check
  `/api/dialog` and request a recompile — see the detail doc.
- Read `errors` / `warnings` in every answer. Leave no modified scene open; save only the scenes you create; never save or
  discard a scene of the user's.

## Test gate (hooks)

Detail: [`docs/dev/test-gate.md`](docs/dev/test-gate.md). Both hooks run `.claude/hooks/test-gate.ps1`.
- **After an edit** (Claude Code hook): the standalone tests if engine / test sources changed, and the Unity compile check
  without the editor if C# under `Assets` changed. When it reports a failure, fix it before continuing.
- **Before a commit** (git `pre-commit`, enable once per clone with `git config core.hooksPath .githooks`): the standalone
  tests, then the EditMode tests — with the editor open, a **full in-editor run after your last edit**
  (`SimuLeanTestRunner.Start("")`); with it closed, a batch run.
- If the EditMode tests cannot run (editor open, MCP down, a user scene modified), don't move on silently: tell the user,
  ask them to close Unity, and follow the pending procedure in the detail doc.

## Build / dependencies

- ExcelDataReader (+ `System.*` deps) is committed in `Assets/Plugins/SimuLeanDependencies/` and restored with
  `RestoreNuGetPackages.ps1` (bundled `nuget.exe` + `Assets/packages.config`); Excel export is the engine's own
  `XlsxWriter`.
- Optimisation uses no external library (algorithms in `SimuLean.Net/Optimization`); Unity packages are in
  `Packages/manifest.json`.
