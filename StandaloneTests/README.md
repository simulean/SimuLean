# Standalone engine tests

Console projects that compile the engine sources of `Assets/SimuLean.Net` with the .NET SDK
(no Unity). `Engine.props` includes every engine-pure file by glob (the Unity extractor is
excluded; the element handlers' extraction half is `#if UNITY_5_3_OR_NEWER`, so their build half and
`ElementHandlerRegistry.CreateDefault()` are the real ones; `UnityEngine.Debug` is stubbed in `Shared/`),
so new engine files are picked up without editing any project file.

| Folder | What it checks |
|---|---|
| `referencemodels` | **Regression net.** Builds every model of `SimuLean.Testing.ReferenceModels` with the real `HeadlessModelFactory` and handlers, checks it is deterministic and compares per-element statistics with `baselines/<model>.json` (relative tolerance 1e-6; structural differences always fail). `-- --update` rewrites the baselines (a missing baseline is a failure otherwise), `-- --model <name>` runs one |
| `listtests` | Lists: `SimList` queries / orders / calculated fields / back-orders (zero-delay delivery, cancel) / statistics; `ListLink` push / pull: priority across queues and inside one queue, filtered pulls without head-of-line blocking, input strategy, puller downtime, link statistics (35 assertions) |
| `stackertests` | `Stacker`: selection rules, count / height capacity, compound release, Link statistics, downtime, builder + headless factory (89 assertions) |
| `sourcestatstests` | Sources (`InterArrivalSource`, `ReleaseSource`): content current / peak / average stay 0, output counted, `Start` resets statistics (17 assertions) |
| `optimizationtests` | The optimisation core (`SimuLean.Optimization`, R-5): search spaces and candidates, the run services (cache, budget, parallel = sequential, failures, cancellation, history), random search, the genetic algorithm on a mixed space (optimum, same seed = same search) a plug-in algorithm written in the test; R-5b: `SimulationConfig.Clone` on every reference model, the parameter catalog, element / team / schedule parameters, the simulation objective (metrics, fitness expression, replications) and a team-size optimisation that matches the exhaustive search; numeric parameters as fitness variables; single runs with a given seed (the Experimenter's replications); `content`, `stay_min`, `member:NAME` metrics; the catalog of every config key (no key of any reference model
undecided), every value encoding written back (choice, bool, vector, sampler number, number list, list of numbers,
user properties), choices and vectors in the GA and random search (150 assertions) | `dotnet run` |
| `expressiontests` | The common expression syntax (`SimuLean.Expressions`): precedence, comparisons, logic, conditional, functions, errors with positions; item / element properties, bare names, texts, built-ins, `has()`, `now()`; uses in process times, priorities, routing, admission and list formulas; allocation-free numeric evaluation (52 assertions) |
| `propertytests` | Property model: `PropertyBag` kinds (double, string, reference, array), one kind per name, parsed text, text of every kind, live view, copy, allocation-free numeric path; `SimEntity` on items, elements and resources; property records round trip, factory resolving references by name (37 assertions) |
| `resourcetests` | Resource core: `TaskExecutor` steps and states, utilisation and usage per requester, `Hold` / `Continue`, Immediate vs AfterCurrent stops, availability and `BecameAvailable`, abstract vs spatial travel with load / unload, labels, warm-up ; `ResourceDispatcher`: FIFO if available, request priority, skill query + resource order, all-or-nothing without head-of-line blocking, stopped / unused resources, zero-time tasks, cancel, request waits; stations: a shared team's processing / held time match the stations, `EndOfProcess` release, request priority from the item; Combiner / PickingStation / AssemblyStation / Stacker with a team of one; transport by resources: load / travel / unload with link statistics, room on the way counted, HELD at a full destination; network travel (route, times, open-ended moves ended by `Arrive`), nearest vehicle (`distance`), spatial transport between placed elements, storage moved by its team, distance matrix, stops mid-way, operators walking to the station, axis travel (trapezoid, together / sequential), AS/RS crane, gantry stacker, walk grid A* (walls, corners, smoothing), grid travel, move logs, external (physics) moves, calibration fit and calibrated travel, traffic (follow, head-on deadlock, capacity) (192 assertions) |
| `componenttests` | Components without a suite before R-11: `LengthLimitedQueue` (lane length with gaps, item length by rule / label / default, several lanes, oversize items, count limit, pass-through, FIFO exit, `Unblock` draining), `GateQueue` (held until released, spare releases as credits), `WeeklyShiftPattern` (parse, working time, next change, merged and overnight windows, day ranges, holidays, malformed text) and `ShiftDowntime` in a model, `ResultTable` CSV (invariant culture, quoting) and `ProjectDatabase` (write, query, parameters, batch, reopen; skipped without a SQLite library) (72 assertions). Reference models `length_limited_lanes` and `shift_downtime` added with it |
| `probabilitytests` | `SimuLean.Stochastics.Probability` (D1): 3621 values of ALGLIB 4.07 (`reference.tsv`, generated once before ALGLIB was removed) for ln Γ, Γ, incomplete gamma / beta and their inverses, erf / erfc, normal, χ², t, F, Poisson, binomial — relative error ≤ 1e-11 / 1e-13 (≤ 1e-9 for inverses), ours accepted where it is the more accurate; published tail values; round trips CDF(inverse(p)) = p down to 1e-300; quantiles are CDF inverses (4429 assertions) |
| `resultstests` | Run results: `RunResults` (run info, statistics, long / wide tables), `StatSummary` (mean, std, min / max, t-based CI against scipy, coverage on normal samples), `ResultSummary` across replications (missing states as 0), the objective's per-replication metric values and `Replicate`; time series (`SeriesRecorder`: the run unchanged, intervals add up to the run, warm-up); `ResultSet` tables and the Excel / CSV / JSON / SQLite exports (`ResultExport`); `Study` (factorial scenarios over catalog paths, members by label, source tables from files, model metrics such as makespan / deadlocked) |
| `datatests` | Model data: `ModelTable` / `ModelData` (typed access, key lookup, required columns, errors that name the table, row and column), the sources — inline, CSV file read at build when there is no snapshot, SQL on the project database (`QueryTable`, columns kept with no row) —, the snapshot rule (no I/O once taken) and the config copy |
| `signaltests` | Signals and reactions: each action (item label, element property, stop for a time / stop and resume by two reactions, list push), next-event and immediate delivery, the state filter, determinism for a seed, build and run errors that name the reaction, the catalog parameters and studies (`reaction:Name.key`), no subscriptions without reactions; process ended, the clock's signals (run started, every, at a time), team members sent off and called back, the firings in the results, studies' metrics and the warm-up |
| `agentinterfacetests` | The engine side of the agent interface: "did you mean" suggestions; `Study.Check` on the built model before any run — unknown / ambiguous targets, statistics and states (a misspelt state read 0), a factor on a key of a distribution the element does not use (warning), a level the model cannot be built with; factors and metrics that fail on two objects with one name; `ModelDescription` (`simulean_model`) on every reference model |
| `docsymboltests` | Doc-symbol check: every back-ticked code name in `CLAUDE.md`, `docs/architecture.md` and `docs/project-overview.md` exists in the code (types and members, snake_case statistic / command names, file paths); a name quoted as history in its sentence (removed, replaced, renamed…) is skipped; other exceptions in `docsymboltests/allowlist.txt`, each with its reason; a self-test checks it still catches the stale names a review found |
| `architecturelint` | Architecture lint: the conventions of `CLAUDE.md` as text rules over the C# sources under `Assets` (comments and strings left out): `SimClock.ScheduleEvent(` only in allow-listed files (element work goes through `Element.ScheduleWork`), no global / unseeded random source in model code, no direct `Find*ObjectOfType` / `FindObjectsByType` / `GetInstanceID` / `GetEntityId` outside `UnityCompat`, no `RuntimeInitializeOnLoadMethod`, no `using UnityEngine` / `UnityEditor` in the engine, every `[SimuLeanElement]` with a registered extractor and handler. Exceptions in `architecturelint/allowlist.txt` (`rule path # reason`); a self-test per rule |
| `fittingtests` | Distribution fitting: `SimuLean.Stochastics.Fitting` against scipy 1.17 on fixed samples (`reference.tsv`, `make_reference.py`) — digamma / trigamma (1e-10), maximum-likelihood fits (closed forms 1e-10; Gamma / Weibull Newton vs scipy's optimiser 1e-4), KS D and A² of each fit; KS / AD p-values at their classical critical points; ranking (AD, KS; χ² for count data), verdicts; every fit's spec decodes and samples its mean; numbers pasted from a spreadsheet (114 assertions) |
| `navaltests` | Naval library N1: the block breakdown (`SimuLean.Naval.BlockBreakdown`) — reading columns and block-level data, validation (duplicates, unknown / self / cyclic predecessors, sequence vs precedence, numbers), erection order and levels, the generated parts (sub-block kits, block orders with `nsub`, erection plan; release interval / lead), SQL round trip (table and `SELECT`), `TableSources` specs (`sql:` / `naval:`), a model whose sources read the breakdown parts when built (kits → yard → block bays → sink, a ship variant by query, a missing table failing the build), a CSV breakdown; N2: the erection dock in sequence (ship launched and kept, crane held for its lift only — `processHoldFor`) and by precedence (fronts, crew, hull kept, makespan = last erection), a missing predecessor reported, a sink keeping items, resources for their own time; N3: the block process of the presets (`naval_block_process`: kits → sub-assembly → turning / yard → block bays → outfitting → painting → slipway → launch) ; N5: assigning an item's visual raises `Item.VisualAttached` (73 assertions; the SQL part is skipped without a SQLite library) |
| `seedtests` | Seeding: pinned stream-seed derivation, same seed → identical results, an unrelated branch added before / after the model leaves its statistics unchanged, element order irrelevant, parallel runs equal sequential ones, unseeded runs record a reproducing seed, `Reseed`, duplicate keys, discrete strategies seeded (30 assertions) |
| `bugfixtests` | Regression tests of the fixed bugs: routing admission / MaxQueue, operator holding, re-entrant source → combiner input, round robin, stats reset, stops (snapshots, resume pulls, cancel, timetable), dates, state ratios, warm-up, sampler specs, batch mode, progress shift, combiner connections, resources on assembly / stacker; #22–#29: admission conditions reading the element, schedule tables in any culture, combiner pull mode auto-update, gate release, stops on sinks, elements without links, model errors failing the build, config parameter conversion, Combiner utilisation as a single-job station (93 assertions); benchmark findings (#77–#79): a queue-fed Combiner input with a requirement ≥ 2, slot utilisation without stopped time, a deadlock beside a stop nothing will end. Build with `-p:DefineConstants=OLDAPI` to run the subset that compiles against an older engine |

Run every suite with one command (exit code 1 on any failure):

```bash
powershell -ExecutionPolicy Bypass -File StandaloneTests/run-all.ps1
```

The suites run **in parallel** (each compiles the engine sources in its own project, so their builds share no files),
their output printed in suite order: ~11 s after an engine change instead of ~32 s one by one; `-Serial`
runs them one by one.

or one suite: `dotnet run --project StandaloneTests/referencemodels/referencemodels.csproj`.
The projects target `net8.0` with `RollForward=Major` (set in `Engine.props`), so any .NET SDK
from 8 on builds and runs them.

The Unity side of the net is the EditMode suite
`Assets/SimuLean.Unity/Editor/Tests/ReferenceModelEditModeTests.cs` (same models and baselines with
the Unity-compiled engine, plus the sample scenes extracted and run headless), with the scene tests next to it
(`SceneRunTests`: a scene run equals the headless run; `OptimizerSceneTests`: scene parameters / metrics become an
optimisation problem; `ExperimenterSceneTests`: scenarios × replications with their values and seeds; `ParameterCatalogTests`: every key of
every library element type, downtime kind, team, user property and sample scene is decided; resources, lists, library registry). Batch mode, editor closed:

```bash
Unity.exe -batchmode -runTests -projectPath . -testPlatform EditMode -testResults results.xml
```

When a model's behaviour changes on purpose, rerun `referencemodels -- --update` and commit the
baseline diff together with the change (reviewing the numbers is the point).

Add a suite by creating a folder with a console csproj (`EnableDefaultCompileItems`
false) that imports `..\Engine.props` and compiles its own `Program.cs`; the transport, storage,
serialization and downtime suites referred to in `docs/architecture.md` follow the same layout.
Add it to `$suites` in `run-all.ps1` so the test gate runs it too.

**Unity compile check without the editor:** `powershell -ExecutionPolicy Bypass -File
StandaloneTests/unity-compile-check.ps1` compiles every C# file under `Assets` with Unity's split
(`Editor` folders → editor assembly, asmdef folders → their own assembly) against the references of the
IDE project files Unity generates (`Assembly-CSharp*.csproj`), building in `%TEMP%` — it works while the
editor is open (or closed) and catches compile errors in Unity-only code in ~10 s.

**Test gate (`.claude/hooks/test-gate.ps1`).** As a Claude Code hook (`.claude/settings.json`), after
every Edit / Write / Bash / PowerShell tool call the gate runs the compile check above when C# under
`Assets` changed, and fingerprints the sources these suites
compile (`Assets/SimuLean.Net`, `StandaloneTests`); if they changed since the
last run it runs `run-all.ps1` and, on failure, reports the failing suites / compiler errors back
at once (exit 2), so a faulty bulk-edit script is caught on the step that broke things. As the git
**pre-commit** hook (`.githooks/pre-commit`; enable once per clone with
`git config core.hooksPath .githooks`) it fails any commit — console, GitHub Desktop or Claude —
unless the standalone suites pass, and then checks the **Unity EditMode suite**: a **full run in the open
editor** (`SimuLeanTestRunner.Start("")`, results in `Temp/simulean_test_results_full.json` with `full` and the
working-tree fingerprint `treeFp` taken by `test-gate.ps1 treefp` at its start) that completed with no failure and no
skipped test counts while **nothing it covers changed since it started** (edits, deletions, renames, new files; a full
run with failures blocks the commit; `Start` refreshes the assets first, so it never tests stale assemblies); otherwise the
suite runs in batch mode when the editor is closed (Unity locks the project). With the editor open and no such run the
commit is allowed with a warning and the EditMode run stays pending until a commit is made with the editor closed, or
`powershell -File .claude/hooks/test-gate.ps1 precommit` is run with it closed. The EditMode verdict is fingerprinted on
the git **index** (what is committed), so unstaged work in progress in the tree does not invalidate it;
when code (`.cs` / `.asmdef`, baselines, manifest) has unstaged changes or untracked scripts, a pass is not cached
(the tests saw code that is not committed). A batch run that produces no results is never cached, and the gate's lock
(20 min) and batch run (45 min) time out.
`SIMULEAN_SKIP_UNITY_TESTS=1` skips the Unity step and `git commit --no-verify` the whole hook. Requirements: Windows
PowerShell, .NET SDK 8+, Unity 6000.6.4f1 in the Hub's default folder (or `UNITY_EDITOR_PATH`).
Unchanged sources cost nothing (the last passing / failing fingerprints are kept in
`.claude/hooks/.state/`, git-ignored, with the EditMode results XML and log).

## Performance benchmark (`perfbench`, not in the gate)

`dotnet run -c Release --project StandaloneTests/perfbench -- ref | scale [family] | study [reps] | mem <spec> |
verify <baselines dir> | profile <model | scale:family:n>`: build and run times, events per
second, bytes per event and collections of the reference models; scaling families (`line`, `arrival`, `overload`,
`team`, `teamprio`, `teamdist`, `list`, `traffic`, `notraffic`); a `Study` against the number of threads; retained
memory; allocation samples by type. Set `DOTNET_TieredCompilation=0` for steady-state numbers. Run it by hand before
and after a change on the hot path; `verify` checks the reference baselines from the benchmark's build. Uses
`SimClock.EventsExecuted`.
