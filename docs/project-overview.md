# SimuLean — Project Overview

A quick orientation for anyone (human or Claude Code) starting work in this
repository. For the detailed software architecture, read
[architecture.md](architecture.md) — it is the authoritative reference and must be
kept in sync with the code. For type-level lookup, open the generated [API reference](api/index.html)
(`python docs/api/generate_api_docs.py` regenerates it).

## What it is

**SimuLean** is a C# **discrete-event simulation (DES) engine** for
manufacturing/logistics systems. **UnitySimuLean** integrates it with Unity so a
model can be built visually in a scene and animated in 3D. An **optimisation** module (pluggable algorithms:
random search, a genetic algorithm, any `IOptimizationAlgorithm`) tunes model parameters by running the simulation
headlessly (no GameObjects) many times on background threads.

## Repository layout

```
Assets/
  SimuLean.Net/        Core DES engine (namespace SimuLean) — no Unity view needed
    SimClock/          Event heap + clock (next-event advance), SimContext, EventHandle
    SimElements/       Element graph: sources, servers, queues, sinks, operators, Item
    SimLink/           Link layer (SimpleLink / GeneralLink) — also drives statistics
    Stochastics/       Streams/ (random engines, per-element streams), Distributions/ (catalog, strategies), Probability/
    Statistics/        Per-element metrics (counter / observation / time-weighted family)
    Headless/          No-op IVElement for GameObject-free runs
    Serialization/     Extract scene → SimulationConfig → handler registry rebuild
    Collections/       ModelList (push/pull + back-orders), SimList (lists; also the storage stock)
    Transport/         The model's network + Dijkstra; vehicles are resources (architecture §3.9, §3.17)
    Storage/           Parametric lanes, location/relocation policies, StorageSystem (§3.10)
    Data/              SQLite-backed project tables, ResultTable, MiniJson (§3.8)
    Downtime/          Stop generators: timetable, MTBF/MTTR, weekly shifts (§3.12)
  SimuLean.Unity/      Unity integration (namespace SimuLean.Unity)
    Core/              SElement base, UnitySimClock (drives the engine per frame)
    Elements/ Agents/  MonoBehaviour wrappers implementing IVElement
    Transport/         Network/node/transfer-point/vehicle wrappers + animator
    Config/ Editor/    Inspector config types & custom editors
    Downtime/          UnityDowntime (stops / failures / shifts on scene elements)
    Inspector/         Optional runtime element inspector (KPIs / charts / detail, §4.9)
    Art/Models/        Script-built 3D models (FBX + baked PBR atlas, LOD0-2; architecture §4.8)
    Visuals/           Parametric visuals: roller conveyor (RollerConveyorMesh, LOD0/LOD1); Building/ =
                       walls, columns, overhead crane (§4.7.1); FloorPlanUnderlay (§4.7.2);
                       SimVisualDetail + GantryStackerView (Auto/Detailed/Fast machine animation, §4.10)
  SimuLean.Optimization/   Optimizer component (algorithms in the engine, pluggable)
  SimuLean.Experimentation/ Batch design-of-experiments runner
  Scenes/              Example scenes (lines, lists, reactions, resources, library examples, Validation/MM1)
  Docs/                Feature guides (headless runs & experiments, downtime & calendar, expressions,
                       optional runtime tools)
docs/                  ← this documentation folder (architecture.md, this file, guide/, agents/, api/)
Tools/Blender/         Headless Blender scripts that generate Art/Models (see its README)
StandaloneTests/       dotnet harness for the engine outside Unity (Engine.props + stackertests)
Tools/FloorPlan/       CAD drawing (DWG/DXF) -> floor-plan texture for FloorPlanUnderlay (see its README)
```

## The three layers in one sentence each

1. **Core engine** — a `SimClock` pops timed `IEvent` off a min-heap; each event
   is an `Element` (or `ServerProcess`) that moves `Item`s along `Link`s and
   reschedules itself.
2. **Unity layer** — each scene `MonoBehaviour` (`SElement`) lazily creates its
   core `Element` and implements `IVElement` so the engine can spawn/move/destroy
   the item GameObjects; `UnitySimClock` advances the clock each frame.
3. **Optimisation layer** — the scene is extracted to a serializable `SimulationConfig`,
   rebuilt headlessly, and run thousands of times by a pluggable optimiser (genetic algorithm, random search, any
   `IOptimizationAlgorithm`) that varies parameters and reads back metrics.

## Key mechanics to know before editing

- **Item-flow statistics are a side effect of `Link` transfers.** Only the links (`GeneralLink`,
  `ListLink`) call `StatsCollector.OnExit`/`OnEntry`. Move items through a Link, or stats will miss
  them. Resources and lists keep their own statistics (state tracker, list stats).
- **One build path.** A scene run extracts the scene (`SceneExtractor`) and the engine factory builds the model,
  as headless; each component is the view of its engine element and is bound to it (`SElement.Bind` → `OnBound`).
- **Headless = no view.** `Element` defaults to a `HeadlessVElement`; a headless run builds the same config without
  components (the scene run passes the components as views).
- **`timeScale`** on `UnitySimClock` converts real seconds → sim seconds each frame.
- **Path 0 kernel guarantees:** each model owns a `SimContext`
  (element registry + seeded random streams — no cross-model static state);
  `ScheduleEvent` returns a cancellable `EventHandle`; same-time events fire in
  deterministic FIFO order; content/WIP averages are time-weighted
  (`content_avg`). Set `SimulationConfig.UseFixedSeed`/`Seed` for
  reproducible headless runs (`UnitySimClock` seed mode *Fixed* in the scene). Every element draws
  from its own random stream keyed by its name, so adding elements does not change the
  numbers of the others and scene / headless runs with the same seed draw the same numbers.
- **Downtime & calendar:** any element can be stopped/resumed
  (`Element.Stop`/`Resume`); schedule processing delays with `Element.ScheduleWork`
  (not `SimClock.ScheduleEvent`) so stops pause them. Generators: timetable, MTBF/MTTR,
  shift calendar (`Downtime/`, `UnityDowntime`). `SimContext.Calendar` maps sim time
  to dates. `AdvanceClock(t)` leaves the clock at `t`. Items carry numeric **and** text
  labels. New elements: `ReleaseSource` (release on demand) and `LengthLimitedQueue`
  (lanes limited by item length).
- **Stacker:** `Stacker` builds stacks of any items at N positions (first not
  full / round-robin / label index / key; capacity by count and optional height label) and
  releases each stack as one compound item (`subItems`); its statistics stay Link-driven through
  the opt-in `ElementStatsCollector.CountSubItemsOnExit`. Unity: `UnityStacker` (+ menu
  *GameObject → SimuLean → Stacking*), animated by `GantryStackerView` with the `GantryStacker`
  model; `SimVisualDetail` switches machine animations Auto / Detailed / Fast by sim speed.
- **Optional Unity tools:** paced / maximum-speed mode of `UnitySimClock`,
  `SimCameraController`, `SimRuntimeHud`, `ElementInspectorPanel`, `FloorPlanUnderlay`,
  the building kit and the 3D models are **opt-in**: nothing is added to scenes and the
  clock defaults keep the legacy behaviour. Guide: `Assets/Docs/Optional_Runtime_Tools.md`.
  Since 2026-10-05 `SimCameraController` navigates FlexSim-style (left / middle-drag grab pan on
  the ground, right-drag orbit about the screen centre, zoom to cursor, key speed proportional to
  height; editor check `SimCameraControllerCheck`).

## Environment & build

- **Unity 6.6 (6000.6.4f1)** (per `ProjectSettings/ProjectVersion.txt`).
- **NuGet** packages (EPPlus, ExcelDataReader) restored via
  `RestoreNuGetPackages.ps1` (uses the bundled `nuget.exe` + `packages.config`).
- Optimisation needs no external library: the algorithms are in the engine and pluggable (`IOptimizationAlgorithm`).
- The engine is its own assembly with no Unity reference (`SimuLean.Net.asmdef`, no third-party numerics);
  the Unity layer, the optimiser component and experiments compile into Unity's `Assembly-CSharp`.

## Running things

- **Interactive sim:** open a scene under `Assets/Scenes/` and press Play; the
  `UnitySimClock` GameObject drives it.
- **Batch experiments:** add an `Experimenter` (library: Experiments): parameters and metrics as in the Optimizer,
  one row of values per scenario, replications; headless (background) or in the scene; Excel report.
- **Optimization:** add an `Optimizer` (library: Experiments), choose parameters and metrics from the catalog
  pop-ups, write the fitness, pick the algorithm and press *Optimise* (Edit or Play mode).

## Further reading

- [architecture.md](architecture.md) — full architecture, diagrams, caveats.
- Feature guides: `Assets/Docs/Headless_And_Experiments.md`, `Assets/Docs/Downtime_And_Calendar.md`,
  `Assets/Docs/Signals_And_Reactions.md` (reactions: "when an element signals, if…, do…"),
  `Assets/Docs/Expressions.md`, `Assets/Docs/Optional_Runtime_Tools.md`.
- The modelling guide [guide/](guide/README.md) and the agents' documentation [agents/](agents/workflow.md).
- `Tools/Blender/README.md` (3D asset pipeline), `Tools/FloorPlan/README.md` (floor plans).
