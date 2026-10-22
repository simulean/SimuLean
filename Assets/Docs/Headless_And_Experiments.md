# Headless runs, experiments and optimisation

How a SimuLean model runs without the scene, and how the Experimenter and the Optimizer use that. Architecture
details: `docs/architecture.md` §3.5 (pipeline), §3.19 (optimisation core, parameters), §4.4 (Experimenter), §5
(Optimizer).

## One model, two ways to run it

A scene is **extracted** into a `SimulationConfig` (`SceneExtractor.Extract(root)`), which the engine **builds**
(`HeadlessModelFactory.BuildModel`) and **runs** (`StartModel`, `clock.AdvanceClock(time)`). A Play-mode run does
exactly the same — with the scene's components as the views of the engine elements (`SceneModelBuilder`) — so a
headless run gives the same results as the scene for the same seed.

```csharp
SimulationConfig config = SceneExtractor.Extract(root).Config;   // main thread (reads Unity components)
var clock = new SimClock(seed);
var model = new HeadlessModelFactory(clock);
model.BuildModel(config);
model.StartModel();
clock.AdvanceClock(1000);                                         // any thread: no Unity
```

- **Seeds.** `new SimClock(seed)` makes a run reproducible; each element draws its own stream (keyed by its name), so
  adding an element does not change the others' numbers. The scene uses the clock's *Fixed* / *Random each run* mode.
- **No files at run time.** Schedule tables, timetables and calendars are copied into the config.
- **A model written in code** (no scene) builds the same way: see `Assets/SimuLean.Net/Testing/ReferenceModels.cs`.
- **Quick run from the editor:** *Run Headless (results only)* on the Simulation Clock. *Export* writes it, *Show in Results* opens
  the charts (SimuLean window ▸ Results: metrics per scenario with confidence intervals, a statistic per object, the
  time per state, the series over time — set *Series Interval* on the clock to record them).

## Experimenter (library: Experiments)

Scenarios × replications of the scene.

1. **Parameters** — pick an element, a resource team or a downtime, then *what*: the list shows every parameter it
   has (numbers, options, yes / no, vectors, the numbers of its distributions, user properties…), with its kind and
   current value. A source's schedule can also be a parameter (order of the rows, a 0 / 1 column).
2. **Scenarios** — one row each: a value per parameter (a number, an option from the list, a tick, or values
   separated by commas), and the replications.
3. **Metrics** — a name and a statistic of an element, a team or a list (`input`, `stay_avg`, `utilization`,
   `state:NAME`, `member:NAME`…).
4. **Run** — *Headless* (background, in parallel, Edit or Play mode; replication r uses the seed base + r − 1 in every
   scenario, so scenarios compare on the same random numbers) or *in the scene* (Play mode, each scenario once,
   visibly). The results are exported to the results folder (SimuLean window ▸ Settings ▸ Results; default `SimuLeanResults/`)
   in the chosen *Export Formats* (Excel by default;
   CSV, JSON, or SQLite into the project database): every run, the summary per scenario with confidence intervals, every
   statistic of elements, resources, teams and lists, and time series when *Series Interval* is set.

## Optimizer (library: Experiments)

The same parameters (with a range for numbers) and metrics, plus:

- **Fitness** — an expression to maximise over the metric names and the numeric parameters' names, e.g.
  `throughput - 60 * servers` (negate to minimise).
- **Algorithm** — genetic algorithm or random search; any `AlgorithmSettings` subclass appears in the list.
- **Budget** — replications per candidate (common random numbers), run time, seed, max evaluations / seconds,
  threads. *Optimise* runs in the background; the best values and metrics are the result, and the
  best solution re-run for every replication is exported like an experiment (with the history per iteration).

## Adding things

- **A new algorithm** (own or a library adapter): an `IOptimizationAlgorithm` in the engine (it gets the seeded
  random engine, cached parallel evaluation, budget and history from `OptimizationRun`) and an `AlgorithmSettings`
  subclass in `Assets/SimuLean.Optimization/Setup` — nothing to register.
- **A new element type or config key**: numbers, bools and number arrays are parameters automatically; declare every
  text or list key in the handler's `Schema` (`ParameterSchema`: option, sampler spec, numbers, user properties or
  structural). The gate fails on an undecided key.
