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

**SimuLean** — a C# discrete-event simulation (DES) engine, its **UnitySimuLean**
Unity integration layer, and an **optimisation** module (pluggable algorithms in the engine, `IOptimizationAlgorithm`).
Target editor: **Unity 6.6 (6000.6.4f1)**. The engine (`Assets/SimuLean.Net`) is its own assembly with
**no Unity reference** (`SimuLean.Net.asmdef`; no third-party numerics: own special functions, D1); everything else compiles into
Unity's `Assembly-CSharp` / `Assembly-CSharp-Editor`.

## ⚠️ Architecture documentation — read first, keep in sync

- **Before making any change, read [`docs/architecture.md`](docs/architecture.md).**
  It is the authoritative description of the software architecture (layers,
  element taxonomy, event/link mechanics, headless & serialization pipeline,
  optimisation module, threading model, and known caveats). Use it to orient yourself before
  editing.
- **Whenever you change the project architecture, update
  `docs/architecture.md` in the same change**, and explain what changed and why.
  Treat the doc as part of the definition of done — a PR that alters architecture
  without updating it is incomplete.

  Update the doc when you, for example:
  - add, remove, or rename an `Element` type, `IVElement`, `ILink`, or distribution;
  - change how events are scheduled or how `SimClock`/`AdvanceClock` works;
  - change the link layer or the statistics side-effect mechanism;
  - alter the extract → serialize → rebuild → run (headless) pipeline or the
    `SimulationConfig` DTOs;
  - add/rename a Unity wrapper (`Unity*`) or change the `SElement` binding
    (`Bind`/`OnBound`/`OnRunStarted`/`ClearViews`), `SceneModelBuilder` or the `UnitySimClock` driving loop;
  - change the optimisation core (search space / candidates, the simulation objective, an algorithm's loop) or the
    threading / dispatcher model;
  - introduce a new subsystem, external dependency, or assembly boundary;
  - resolve or introduce any of the items in the doc's "Known caveats" section (§8).

  When you touch that section's subject matter, reconcile the list too. Also bump
  the "Last verified against the codebase" date at the top of the doc when you
  confirm it still matches reality.

- For a fast orientation, [`docs/project-overview.md`](docs/project-overview.md)
  summarizes the layout and key mechanics. Keep it roughly consistent with the
  architecture doc, but `architecture.md` wins on any conflict.

## Layout (see `docs/project-overview.md` for detail)

- `Assets/SimuLean.Net/` — core engine (namespace `SimuLean`).
- `Assets/SimuLean.Unity/` — Unity integration (namespace `SimuLean.Unity`).
- `Assets/SimuLean.Optimization/` — the Unity side of optimisation (`Optimizer` component, scene specs); the
  algorithms are in the engine (`SimuLean.Net/Optimization`).
- `Assets/SimuLean.Experimentation/` — batch experiment runner.
- Root `*.md` guides and `Assets/Docs/` — feature-level documentation.
- `Tools/Blender/` (script-built 3D models), `Tools/FloorPlan/` (CAD drawing → floor-plan
  texture) — authoring tools outside `Assets/`, each with a README.

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
| Regression net: reference models, baselines, `run-all.ps1`, EditMode tests | `docs/architecture.md` §3.15; `StandaloneTests/README.md` |
| Building / crane kit and 3D models | `docs/architecture.md` §4.7.1, §4.8; `Tools/Blender/README.md` |

## Conventions & gotchas

- **Statistics have two sources, nothing else**:
  - **item flow** (input / output, content, stay times of elements) is driven only by the `Link` layer
    (`OnEntry`/`OnExit`) — route any new item-movement path through a `Link` or stats will be wrong;
  - **resources and lists** (utilisation, time per state, tasks, usage per station; list content,
    waits, field values) come from their own state tracker / statistics objects, updated where the
    state changes (`SetState`, push / pull). Never compute either kind twice or from another layer.
  - **reading them for results** (reports, exports, metrics, baselines, the scene's headless run) goes only through
    `RunResults.Capture` and the `ResultMetrics` names (architecture §3.11) — add a new statistic there, not in a report.
- **One build path.** Unity components never build engine objects: a scene run extracts the scene
  (`SceneExtractor`) and the engine handlers build the model (`SceneModelBuilder` → `HeadlessModelFactory`), with
  each component as the view of its element, then bind it (`OnBound`). New element parameters go in the extractor
  and the handler only — the scene uses them automatically.
- **Headless runs** rely on `HeadlessVElement`. The engine cannot use Unity at all (the
  compiler enforces it): log with `SimLog`, and put anything that reads Unity components in the Unity
  layer. A new element type needs an engine `<Type>Handler` (build) **and** a Unity `<Type>Extractor`
  (`SimuLean.Unity/Serialization/Extractors`), registered in both registries (architecture §3.5).
- **Every property is parameterisable.** The optimiser / Experimenter catalog walks every config key:
  numbers, bools and number arrays are offered by their type; a **text or list key must be declared** in the
  handler's `Schema` (`ParameterSchema`: `Choice`, `Sampler`, `NumberList`, `Numbers`, `Properties` or `Structural`;
  team / downtime properties in `ParameterSchema.Team` / `.Downtime`). The gate fails on an undecided key
  (`ParameterCatalogTests`, `optimizationtests`) — decide it, never list parameters by hand (architecture §3.19).
- **Random numbers: one stream per element, keyed by name.** Every sampler must get
  its stream from the model: `clock.Context.AssignRandomStream(sampler, elementName, purpose)` /
  `CreateRandomStream(owner, purpose)` (engine), `ElementBuildHelpers.BuildRandomProcess` /
  `SamplerSpec.Decode(spec, clock, owner, purpose)` (handlers). Unity wrappers never build samplers (one build path:
  the handlers build them from the extracted config), so the scene and a headless run draw the same numbers.
  Never draw from `RandomEngineManager.GlobalEngine`, `System.Random` or `UnityEngine.Random` in model
  code (a distribution left without a stream warns at its first draw). Runs are reproducible for a model seed (`new SimClock(seed)`, `SimulationConfig.UseFixedSeed`,
  `UnitySimClock` *Fixed* mode); unseeded runs draw a fresh seed and record it (`SimContext.Seed`).
  Changing the stream derivation changes every seeded result: regenerate the reference baselines.
- **Timed work goes through `Element.ScheduleWork`**, not `SimClock.ScheduleEvent`,
  so downtime (`Element.Stop`) can pause it; keep `ScheduleEvent(event, delay)` — its second argument is a **delay
  from now**, not a time — for events that are not an element's work (arrivals, timetables). Downtime blocking is enforced in `GeneralLink` and
  the output strategies (`CanAccept`) — don't bypass them.
- **`AdvanceClock(t)` leaves the clock at `t`** (not at the last event), so end-of-run
  statistics include the final interval.
- **Scene set-up vs. optional components.** A modelling scene comes
  ready: the editor-only `SimuLeanSceneSetup` creates the `UnitySimClock` with its default views
  (`SimRuntimeHud`, `ElementInspectorPanel`, `SimVisualDetail`) and a `SimCameraController` on the
  main camera — in new scenes, once per project on first load, when the clock is created from the
  library or added by hand, and from *Tools ▸ SimuLean ▸ Set Up Scene* (switch:
  *Auto Set-Up New Scenes*, `ProjectSettings/SimuLeanSettings.json`). Only that editor set-up adds
  components; never add anything at run time (`RuntimeInitializeOnLoadMethod`) and keep the other
  optional pieces (`FloorPlanUnderlay`, the building kit, the 3D models, the paced / max-speed
  clock mode) opt-in. New `UnitySimClock` options default to the current behaviour. Views,
  inspector contributors (`IElementInspectorContributor`) and other observers must stay read-only
  (no events scheduled, no items moved) so runs are unchanged.
- **Unity object lookup / identity:** never call `FindObjectOfType`, `FindObjectsOfType`,
  `FindFirstObjectByType`, `FindObjectsByType`, `FindAnyObjectByType`, `GetInstanceID` or
  `GetEntityId` directly — use `SimuLean.Unity.UnityCompat` (`FindAny<T>()`, `FindAll<T>()`
  (unsorted), `FindAll<T>(includeInactive)`, `ObjectId(obj)`), the single place to adapt when
  Unity changes these APIs again (architecture §7). The project targets Unity 6.6 only (no
  2021.3 / older-6.x guards); compile-check in 6000.6.4f1.
- **Register what you add in the SimuLean window (Library tab):** every new model-element wrapper (`SElement`)
  carries `[SimuLeanElement(SimuLeanModules.X, "Name", Description = …)]` (an EditMode test fails
  otherwise); every new non-model scene component meant to be created / edited by the user (pools,
  lists, generators, reports…) carries `[SimuLeanTool(SimuLeanToolCategories.X, "Name", …)]`
  (architecture §4.6.1). The SimuLean window is generated from them (and the `GameObject/SimuLean/…` menu items)
  — never hard-code entries. Sector content belongs to a pack (`SimuLeanPacks`: by the element's module or the preset's
  menu folder, else `Pack = …` / `[SimuLeanPresetPack]`); general content stays in Core.
- **The `SimuLeanModelPostprocessor` only handles library folders** under
  `Art/Models/` that contain `<Folder>_Albedo.png`; keep hand-made models in folders
  without such a file (or they get the library import settings).
- **No case-specific or confidential content** (customer names, plant data, drawings,
  calibrated parameters) in this shared repository; case studies live in their own
  repositories and use the engine through its public API.

## Working through the Unity MCP

- **Modelling guide for agents: [`AGENTS.md`](AGENTS.md) and [`docs/agents/`](docs/agents/)** — the workflow, the command
  reference (generated: after adding or changing a command run `python Tools/Docs/command_reference.py`; `run-all.ps1`
  fails when it is stale) and the error catalogue. Keep them in step with the commands.
- **One Unity MCP server per project** — the project's `.mcp.json` (`unity mcp`, the `com.unity.pipeline` server);
  don't drive the same editor from two servers.
- **Build and study models with the SimuLean commands, not `eval`** (`Editor/Pipeline/SimuLeanCommands.cs`):
  `simulean_types` (what can be placed; with `type`, its fields and catalog keys), `simulean_params`,
  `simulean_add_element` (also the Visualization / Analysis tools), `simulean_connect` (between two transport nodes: a
  track), `simulean_set` (a field by name / path, objects by GameObject name or asset path `Assets/…`, a list item's
  field as `actions[0].kind` — a reaction's actions),
  `simulean_add_team`, `simulean_load_table`, `simulean_place_model` (a library 3D model as decoration),
  `simulean_record_shot` / `simulean_record_start` / `simulean_record_status` (a demo video: shots framing elements, then a
  take in Play that ends as an MP4 — never place cameras or encode frames by hand), `simulean_run_headless` (per scenario the `metrics` asked and the model
  block — pass `metrics` to keep the answer small, `detail=full` adds every element's KPIs; `set` = catalog overrides
  `Owner.key=v1|v2;…`, the `Study` path grammar of architecture §3.5). A study that may last more than ~60 s:
  `simulean_run_start` (same arguments) then `simulean_run_status wait=50` — a long synchronous run times the MCP out and
  blocks every following call; every `_status` takes `wait` (never sleep in `eval`). Optimise with
  `simulean_optimize_start` / `simulean_optimize_status`, find stalls and deadlocks with `simulean_diagnose`. Build in bulk
  with `simulean_apply` (a JSON list of edits, all or nothing) and `simulean_repeat` (a group copied N times); run a
  component's action with `simulean_invoke` (mark a new one `[SimuLeanAction]`) — never loops or reflection in `eval`.
  See the model with `simulean_capture` (a framed PNG) instead of placing cameras. Results: `simulean_results`
  (list / read / save / export / chart); data: `simulean_query`, `simulean_fit`. Undo a mistake with `simulean_disconnect`, `simulean_remove` (references to the object
  cleared and listed) or `simulean_rename`, not the generic editor tools. `simulean_model` reads the whole model in one call (by name, parameters under the keys
  a study varies) and `simulean_validate` builds it without running. Every answer carries `errors` and
  `warnings` (what was logged while the command ran) — read them; a study is checked before it runs. Elements are
  named by GameObject name (unique: a duplicate name is refused) or by full hierarchy path. In code, `HeadlessModelFactory.Find(name)` finds an element by display name.
- **EditMode tests:** `simulean_test` (filter) then `simulean_test_status` until `completed`, or `eval`
  `return SimuLean.Unity.Editor.SimuLeanTestRunner.Start("<filter>");` / `.Status()`. **Never the MCP's `run_tests`.**
  The first call after a recompile may return a network error: retry.
- **Captures:** use a scene camera (`capture_game_view camera=…`); the Scene View image is stale while the editor is
  unfocused.
- **Hierarchy handles** of the generic tools need the full path (elements sit under organiser groups).
- **A blocked editor:** read the port and token from `Library/Pipeline/.unity-pipeline-port`, check `GET /api/dialog`
  (Bearer token) for a modal dialog, and request a domain reload / recompile (`recompile`, `refresh_unity`).
- **Known upstream issue:** the `batch` tool's `params` (client schema: string; server: object) — use parallel single
  calls instead.
- Leave no modified scene open; save only the scenes you create.

## Test gate (hooks)

Both run `.claude/hooks/test-gate.ps1`:
- **After every edit** (Claude Code hook, `.claude/settings.json`): runs `StandaloneTests/run-all.ps1`
  if the engine or test sources changed, and compile-checks the Unity code **without the editor**
  (`StandaloneTests/unity-compile-check.ps1`, ~10 s) if any C# under `Assets` changed. When it reports
  a failure, fix it before continuing (don't pile more edits on a broken tree).
- **Before every commit** (git `pre-commit`, `.githooks/` — enable once per clone with
  `git config core.hooksPath .githooks`; applies to console, GitHub Desktop and Claude commits): the
  standalone tests must pass (they run in parallel, ~11 s), then the Unity EditMode tests: a **full run in the open
  editor** (`SimuLeanTestRunner.Start("")`, no filter) that passed with no skipped test counts while nothing it covers
  changed since it started (a working-tree fingerprint taken at its start; `Start` refreshes the assets first, so an
  edited script is compiled before it is tested) — so with the editor open, run the full suite there just before
  committing, after your last edit; otherwise they run in batch mode (a few minutes) when the editor is **closed**.
  With the editor open and no such run the commit goes ahead with a warning and the EditMode tests stay pending. The
  EditMode verdict is cached on the git index; when code (`.cs` / `.asmdef`, baselines, manifest) has unstaged
  changes or untracked scripts the pass is not cached (the tests saw code that is not committed). Never use `--no-verify` or `SIMULEAN_SKIP_UNITY_TESTS=1` unless the user explicitly asks.
The compile check works while someone else has the editor open; the EditMode tests still need Unity.

**When the EditMode tests stay pending** — the editor is open and a full in-editor run is not
possible (MCP down, a scene of the user's modified, …). Don't silently move on:
- tell the user in chat that the batch EditMode tests are pending and ask them to close Unity (save their scenes
  first — never close or save the editor yourself);
- wait for it in the background (e.g. `Monitor` until `Temp/UnityLockfile` is no longer locked, ~15 min at most),
  and meanwhile go on with work that does not need the editor;
- once the editor is closed, run `powershell -File .claude/hooks/test-gate.ps1 precommit` (it runs the batch
  EditMode tests and caches the verdict), report the result, and tell the user they can reopen Unity;
- if the user is away (no close within the wait), leave the tests pending, say so in the end-of-task summary, and
  keep working; they run on the next commit made with the editor closed.

**Running EditMode tests in the open editor through the Unity MCP.** Never use the MCP's
`run_tests`. If an open scene has unsaved changes, Unity's Test Runner shows the modal "Scene(s) Have Been Modified"
dialog. That dialog stalls the editor's main thread, and so every MCP command, until a human answers it; a cancelled
run also leaves the MCP's synchronous `run_tests` waiting forever.
- Use `eval` with `return SimuLean.Unity.Editor.SimuLeanTestRunner.Start("<name filter>");`. It refuses to start
  while a scene is modified and lists the scenes; nothing blocks.
- Then poll `return SimuLean.Unity.Editor.SimuLeanTestRunner.Status();` until `"status":"completed"`.
  The results are also in `Temp/simulean_test_results.json` (the last full run also in `…_full.json`, which the gate
  reads). A `blocked: compiling …` answer means `Start` found edited scripts: call it again once the editor is idle.
- If it says `blocked`: save the scenes you created; never save or discard a user's scene without asking.
- Keep your own scenes saved after every edit.
- Editor tooling must not dirty a scene just by opening it (e.g. the inspector panel's element auto-collection
  is not marked dirty).
- If the MCP stops answering:
  - check `GET http://127.0.0.1:<port>/api/dialog`, with the port and the bearer token from
    `Library/Pipeline/.unity-pipeline-port`;
  - a stuck queue clears with a domain reload. The *MCP for Unity* server on `127.0.0.1:8080` can request
    one with `refresh_unity` (`mode: force`, `compile: request`).

## Build / dependencies

- ExcelDataReader (+ `System.*` deps) is committed in `Assets/Plugins/SimuLeanDependencies/` and restored with
  `RestoreNuGetPackages.ps1` (bundled `nuget.exe` + `Assets/packages.config`); Excel export is the engine's own
  `XlsxWriter` (no EPPlus).
- Optimisation uses no external library (algorithms in `SimuLean.Net/Optimization`); Unity packages are in
  `Packages/manifest.json`.
