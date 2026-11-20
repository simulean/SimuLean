# SimuLean — Software Architecture

> **Status:** Living document. This file is the single source of truth for the
> software architecture of the SimuLean / UnitySimuLean solution.
> **Any change to the project architecture must be reflected here** (see
> [CLAUDE.md](../CLAUDE.md)). When you add/remove/rename an element type, change
> the event or link mechanics, alter the headless/serialization pipeline, or add
> a subsystem, update the relevant section and the diagrams below.

Last verified against the codebase: **2026-11-20** (text settings: the catalog's text kind, study / Experimenter / Optimizer).

> **Companion documents:** [`project-overview.md`](project-overview.md) (fast orientation),
> [`api/index.html`](api/index.html) — a generated, searchable API reference for every public type and member of
> `Assets/SimuLean.Net/`, rebuilt with `python docs/api/generate_api_docs.py` (**re-run it whenever you change the
> core's public surface**) — and the feature guides in `Assets/Docs/`.

---

## 1. What this project is

SimuLean is a **discrete-event simulation (DES) engine** for manufacturing /
logistics systems, written in C#, together with **UnitySimuLean**, a Unity
integration layer that lets you build a model visually in a Unity scene and watch
items flow through it in 3D. On top of that sits an **optimisation module** —
pluggable algorithms (`IOptimizationAlgorithm`: `RandomSearch`, `GeneticOptimizer`, engine §3.19) with a Unity
front-end (§5) — that tunes model parameters by running the simulation headlessly thousands of times.

Three questions the architecture answers:

1. **How does the simulation run?** A next-event time-advance loop over an event
   heap (`SimClock`), driving a graph of `Element` nodes connected by `Link`s.
2. **How does Unity fit in?** Each core `Element` has an optional visual twin
   (`IVElement`); Unity `MonoBehaviour` wrappers implement that interface to spawn
   and move GameObjects. The engine itself has no dependency on the Unity view.
3. **How does optimization work?** A Unity scene model is *extracted* into a
   serializable config, *rebuilt* as a pure-.NET object graph, and run
   *headlessly* on background threads by a pluggable optimiser (engine `SimuLean.Optimization`, §3.19, §5).

## 2. Layered architecture

The solution is organized in **logical layers by namespace/folder**. Since 2026-10-06 the boundary
between the engine and Unity is **enforced by the compiler**:

| Assembly | Folder | Definition |
|---|---|---|
| `SimuLean.Net` (the engine) | `Assets/SimuLean.Net` | `SimuLean.Net.asmdef`: **`noEngineReferences: true`** (no UnityEngine / UnityEditor), no assembly references, `overrideReferences` with the precompiled DLLs it uses (System.Memory, System.Buffers, System.Runtime.CompilerServices.Unsafe, System.Numerics.Vectors; no Excel reader, no numerics library), auto-referenced |
| `SimuLean.UserLogic` (choice rules) | `Assets/SimuLean.UserLogic` | `SimuLean.UserLogic.asmdef`: **`noEngineReferences: true`**, references `SimuLean.Net` only, auto-referenced. The modeller's (or an agent's) choice rules (§3.25); the engine finds them by reflection. The standalone tests and the external runner compile this folder with the engine (`StandaloneTests/Engine.props`) |
| `Assembly-CSharp` / `-Editor` | everything else (`SimuLean.Unity`, `SimuLean.Optimization`, `SimuLean.Experimentation`; `Editor` folders → editor assembly) | Unity's default assemblies; they see the engine automatically |

Consequences: the engine cannot use Unity types (logging goes through **`SimLog`**,
`SimClock/SimLog.cs` — sinks default to the console, the Unity layer routes them to `Debug.Log*` via
`SimLogUnityBridge`, installed by the editor on load and by `UnitySimClock.Awake`); the serialization
handlers are split (§3.4: build half in the engine, extraction half `IElementExtractor` in
`SimuLean.Unity/Serialization`); `UnityModelExtractor` and the frame `Timer` moved to the Unity layer.
The rest of the layering (Unity / GA / experiments) is still a convention inside `Assembly-CSharp`.

```
┌──────────────────────────────────────────────────────────────────────┐
│  Optimization   Assets/SimuLean.Optimization/ (+ engine §3.19)        │
│  pluggable algorithms that drive headless runs on worker threads      │
└───────────────┬──────────────────────────────────────────────────────┘
                │ builds & runs headless models, reads metrics
┌───────────────▼──────────────────────────────────────────────────────┐
│  Unity Integration            Assets/SimuLean.Unity/ (SimuLean.Unity)  │
│  MonoBehaviour wrappers (SElement + IVElement), UnitySimClock driver,   │
│  custom Editors, batch Experimenter, stats display                    │
└───────────────┬──────────────────────────────────────────────────────┘
                │ IVElement interface  +  serialization bridge
┌───────────────▼──────────────────────────────────────────────────────┐
│  Core DES Engine              Assets/SimuLean.Net/   (SimuLean)        │
│  SimClock + event heap · Element graph · Link layer · Items ·         │
│  stochastic value providers · statistics · headless factory           │
└──────────────────────────────────────────────────────────────────────┘
```

### Namespace / folder map

| Layer | Folder | Main namespace(s) |
|---|---|---|
| Core engine | `Assets/SimuLean.Net/` | `SimuLean`, `SimuLean.Collections`, `.Data`, `.Downtime`, `.Expressions`, `.Headless`, `.Logic`, `.Naval`, `.Optimization`, `.Resources`, `.Results(.Charts)`, `.Serialization`, `.Statistics`, `.Stochastics(.Streams/.Distributions/.Fitting)`, `.Storage`, `.Testing`, `.Transport` |
| Unity integration | `Assets/SimuLean.Unity/` (`Core/`, `Elements/`, `Config/`, `Serialization/`, `Resources/`, `Transport/`, `Lists/`, `Data/`, `Downtime/`, `Naval/`, `Charts/`, `Inspector/`, `Visuals/`, `Art/`, `Prefabs/`, `Editor/`) | `SimuLean.Unity`, `SimuLean.Unity.Naval`, `SimuLean.Unity.Editor(.Naval/.Tests)`; the extractors are in `SimuLean.Serialization` |
| Optimisation (Unity front-end) | `Assets/SimuLean.Optimization/` (`Setup/`, `Evaluator/`, `Editor/`) | `SimuLean.Optimization`, `.Evaluator`, `.Editor` (the algorithms: engine `SimuLean.Optimization`, §3.19) |
| Experimentation | `Assets/SimuLean.Experimentation/` (`Controller/`, `Core/`, `Editor/`) | `SimuLean.Experimentation.Controller` / `.Core` / `.Editor` |

**Namespace dependencies inside the engine** are pinned by `architecturelint` (`namespace-deps`,
`StandaloneTests/architecturelint/namespaces.txt`): a top namespace may use another only in a listed direction, so no new
cycle appears unnoticed. The list is the graph as it is, with its remaining two-way pairs (the stations of the root
namespace use `SimuLean.Resources`, whose dispatchers serve elements; `SimContext` keeps the model's teams by name).
Inverted so far: the element core no longer uses `SimuLean.Headless` (an element built without a view gets the engine's
silent `NoView`), `SetupMatrix.From` gets the decoder of its time cells from the builder instead of calling
`Serialization.SamplerSpec`, and `ElementSettings.Refine` writes through `Serialization.ISettingsSchema` (implemented by
`Optimization.ParameterSchema`). A re-thrown error keeps the one it wraps as its inner exception (the message unchanged).

> **Engine ↔ Unity coupling — resolved 2026-10-06:** the engine is its own assembly without any Unity
> reference (`SimuLean.Net.asmdef`, see above); the standalone test suites compile it with the plain .NET
> SDK and no stubs.

---

## 3. Core DES engine (`Assets/SimuLean.Net/`)

### 3.1 Simulation clock and events

- **`SimClock`** (`SimClock/SimClock.cs`) owns `simTime` and a
  `DoubleMinBinaryHeap` of `(time, seq, EventHandle)` entries.
  - `SimClock()` / `SimClock(int? seed)` — every clock owns a **`SimContext`**
    (see below); pass a seed for reproducible runs.
  - `ScheduleEvent(evt, dt)` inserts at absolute time `simTime + dt` and
    **returns an `EventHandle`** whose `Cancel()` removes the event before it
    fires (reschedule = cancel + schedule anew).
  - `AdvanceClock(time)` repeatedly pops the minimum-time live event, advances
    `simTime` to it, and calls `evt.Execute()` until the heap head exceeds
    `time` or the heap empties. Classic **next-event time advance**.
    It then **leaves the clock at `time`** (it used to stay at the
    last event), so time-weighted statistics and open state intervals are measured
    up to the requested horizon — end-of-run KPIs no longer drop the final
    interval. Returns false when no events remain.
  - `Context`, `GetSimulationTime()`, `GetDateTime()` (calendar date of now), `PendingEventCount`, `IsStopped` /
    `StopTime`. There is no `Reset`: a clock serves one model and one run — a new run builds its model on a new
    `SimClock` (`UnitySimClock.NewModelClock`).
  - `RunStatus` / `RunEndReason` (`SimClock/RunStatus.cs`: `Horizon`, `StopAtCount`, `Empty`, `Deadlock`, `Stalled`,
    `Finished`; end / stall / deadlock / empty / finish times, `Waiting`) is the state `SimContext.Run` that the run
    monitor fills (§3.5). `IBackgroundEvent` / `BackgroundActionEvent` are in `SimClock/ActionEvent.cs`.
  - `SimTime` (`SimClock/SimTime.cs`): the tolerance of time comparisons — `SimTime.Epsilon` (1e-9, absolute) and
    `SimTime.ToleranceAt(t)` (relative to the size of `t`); lengths, quantities and chart axes keep their own.
  - **Run control.** `Stop()` ends the run (an end condition): `AdvanceClock` returns at
    once and never moves the clock again, so statistics end at `StopTime`. **Background events** (`IBackgroundEvent`,
    `BackgroundActionEvent`: shift / calendar ticks, the next calendar-time MTBF failure, a timetable or periodic stop's
    start, the warm-up reset, the run monitor's "no progress" check) never move items; the heap counts the others (`ForegroundEventCount`) and the clock raises **`Idle`** when none is
    left (after an event, and when `AdvanceClock` starts) — the run monitor (§3.5) tells an empty model from a stall or
    a deadlock. A stop's end, paused work and arrivals are foreground events (an AfterCurrent stop's end is scheduled
    when it takes effect, §3.12: until then the work in hand it waits for is the pending foreground event).
- **`SimCalendar`** (`SimClock/SimCalendar.cs`) — maps sim time to
  dates: `date = Start + simTime · SecondsPerUnit`. Immutable; owned by
  `SimContext.Calendar` (default `SimCalendar.Default` = 2000-01-01 00:00, seconds;
  `IsDefault` tells consumers that no calendar was configured).
  `ToDateTime`/`ToSimTime`/`TryParseSimTime`/`Format`, `FromSeconds`/`ToSeconds`,
  day helpers; `TryParseDate` accepts ISO and day-first `dd/MM/yyyy [HH:mm[:ss]]`
  (invariant culture). The engine still simulates in plain numbers — the calendar
  converts date-stamped input (order books, downtime traces, shifts) at load time
  and formats results. Configured by `SimulationConfig.StartDateTime`/
  `TimeUnitSeconds` (headless) or the `UnitySimClock` *Calendar* fields. Without a start
  date the time unit still applies: `SimCalendar.Undated(secondsPerUnit)` (dates from 2000-01-01 at that unit,
  `IsDefault` true = views show model time) — it used to fall back to `Default` (seconds), so with a 1 h unit
  t = 46 showed `0:00:46` instead of 2 January 22:00 (`bugfixtests` #36, EditMode `TimeUnitsTests`).
- **`SimContext`** (`SimClock/SimContext.cs`) — per-model context owned by the
  clock: the **element registry** (replaces the former process-wide static list,
  so parallel headless models never share state), the model `Seed` (a fresh
  `NewRandomSeed()` when the clock is built without one — read it to reproduce the run;
  `Reseed(seed)` sets the seed of the run's clock before its model is built, as the Unity scene clock does), a
  thread-safe root `IRandomEngine` (`RandomEngine`; model elements use their own streams), and
  **`CreateRandomStream(owner, purpose)`** — *one stream per stochastic source*,
  see §3.6 *Seeding model*. Also `Clock`, `Lists` / `GetList(name)` (the model's `SimList`s, §3.16),
`Data` (the run's read-only data-table snapshot, `ModelData`, §3.8), `Run` (`RunStatus`, §3.5), `Elements`, `GetItemCount()` (total items
  currently held across all elements — renamed from `GetInventory()`, since it is a
  raw item count, not necessarily warehousing "inventory"), and a per-model
  **`ModelParameters`** dictionary read by output-routing strategies (per model: no process-wide
  static).
  `ModelParameters` is a `Dictionary<string, ParameterValue>` — the value is a typed
  `ParameterValue` (see below), not a boxed `object`.
- **`ParameterValue`** (`SimClock/ParameterValue.cs`) — a `readonly struct` typed
  parameter value: one of int / double / bool / string, tagged by a `ParameterKind` enum (`None` = `default` /
  `ParameterValue.None`, `IsNone`). Numeric kinds share a single `double` field (bool = 0/1) and
  never box; only the string kind carries a reference. Implicit conversions build a
  value (`ParameterValue p = 3; = 2.5; = true; = "round_robin";`); `As*` accessors
  throw on kind mismatch and `TryGet*` do not. Replaced the former boxed
  `object` in the parameter bag so numeric/boolean routing parameters allocate
  nothing. `ParameterizedRoutingStrategy` reads a **string-kind** value to pick a
  routing mode (preserving the old `value as string` semantics: non-string values
  fall through to the default strategy).
- **`EventHandle`** (`SimClock/EventHandle.cs`) — cancellation handle;
  **lazy deletion**: cancelled entries stay in the heap, are skipped/purged at
  the top, and the heap rebuilds itself when more than half its entries are
  dead (bounds memory in cancel-heavy models: breakdowns, preemption,
  reneging).
- **`IEvent`** (`SimClock/IEvent.cs`) is the single scheduling contract:
  `void Execute()`. Everything schedulable implements it. One-line events need no class of their own:
  `ActionEvent` runs an action (keep one instance to fire it repeatedly without allocating) and
  `SimClock.Schedule(action, delay)` schedules a one-off; timed work of an element still goes through
  `Element.ScheduleWork` so downtime can pause it.
- **`DoubleMinBinaryHeap`** (`SimClock/DoubleBinaryHeap.cs`) — min-heap keyed by
  `(time, seq)`: the monotone sequence number gives **deterministic FIFO
  ordering of same-time events** (required for reproducible runs).

Two self-scheduling idioms:

1. **An element schedules itself.** e.g. `ScheduleSource.Start()` →
   `ScheduleNext()` → `simClock.ScheduleEvent(this, dt)`; its own `Execute()`
   emits items then re-schedules the next arrival. `InterArrivalSource` and `RateTableSource` follow the
   same pattern (arrivals use `ScheduleEvent`: absolute-time events).
2. **A server schedules its job.** A workstation's `StartProcessing` samples a
   delay and `ScheduleWork(serverProcess, delay)` (pausable by downtime, §3.12). When it fires,
   `ServerProcess.Execute()` calls back `IWorkStation.CompleteServerProcess(this)`,
   which forwards the finished item downstream.

### 3.2 The element graph

**`Element`** (`SimElements/Element.cs`) is the abstract base of every model node.
It holds:
- an `input`/`output` `ILink` pair,
- the shared `SimClock`,
- a `IVElement vElement` visual adapter (**defaults to the silent `NoView`** when
  none is supplied — this is what makes headless runs transparent, see §3.5),
- an `ElementStateTracker` (time-in-state accounting), driven exclusively through
  `SetState(ElementState)` — the **single point** that updates state statistics.
  `CurrentState` is a typed **`ElementState`** token (no longer a raw `string`);
  `GetTimeInState`/`GetStateRatio` take an `ElementState`.
- an `ElementStatsCollector` (per-element metrics),
- registration into its clock's **`SimContext`** registry (the former static
  `ArrayList` + `GetElements`/`GetInventory` statics were removed — use
  `simClock.Context.Elements` / `Context.GetItemCount()`).
- its input / output strategies (`SetInputStrategy` / `ValidateInput`, `SetOutputStrategy` / `GetOutputStrategy`;
  none = admit all / a shared `FirstAvailableStrategy`), its `Location` / `WorkLocations` (network nodes, §3.9,
  §3.17), `ResolveInputFor(origin)` (a Combiner maps a link to one of its ports) and `SendHeldItem(item)` (hands
  over a list-reserved item; default false → `Unblock`, §3.16);
- protected helpers `SendOut(item)` (false without an output link — an unconnected element keeps its items) and
  `NotifyInput()`; statistics accessors `GetStatsInputCount` … `GetStatsStayTimeMin`, `GetProcessingRatio`,
  `GetStateSnapshot`, `GetStateLog`.

Abstract contract every subclass implements:
`GetQueueLength()`, `GetFreeCapacity()`, `CheckAvailability(Item)`,
`Receive(Item)`, `Unblock()`, plus `virtual Start()` (resets tracker + stats).
Every built-in `Start()` override now resets its statistics too (several
did not, so a restarted model accumulated the previous run's counts). Warm-up:
`virtual ResetStatistics()` discards the statistics gathered so far (link counters, stay
times, content history, time per state) **without touching the model** — items, pending
work, stops and the current state carry on; content continues from its current level
(`StatTimeWeightedVariable.Restart`). `Element.ScheduleWarmup(clock, participants, t)` runs it
at time `t` on every **`IWarmupStatistics`** given — elements, resources (they are elements), downtime generators
(stop count; downtime of a stop running at `t` counted from `t`), resource teams (`Served`) — and on the model's lists;
`SimulationConfig.WarmupTime` does this in headless runs for all of them (§3.5). A resource task
running at `t` counts its usage from `t`; the element `StopCount` restarts too. State ratios (`GetStateRatio`) are
measured since the last tracker reset.

`Element` is `partial`: `SimElements/ElementDowntime.cs` adds the
**interruption API every element shares** (design in §3.12): `Stop(StopRequest)`
→ `StopToken` / `Resume(token)` (overlapping stops stack), `ScheduleWork(evt, dt)`
→ `WorkHandle` (pausable work), `IsDown`/`IsInputBlocked`/`IsOutputBlocked`/
`IsWorkPaused`, `ActiveStops`, `StopCount`, `UnderlyingState`, `CanAccept(item, origin)`
(= not input-blocked **and** `CheckAvailability` **and** the input strategy admits the item
from that origin — *2026-10-06*, see §3.4; the output strategies use it), and
C# events `Stopped`, `Resumed`, `StateChanged(element, old, new)`, `ItemEntered`,
`ItemExited` (the last two raised by the link layer through `ElementStatsCollector` —
cheap, statically wired publisher hooks).
`SetState` records the element's own state as `UnderlyingState` and only reaches the
tracker when no stop masks it. Without stops every element behaves exactly as before.

**Element taxonomy** (all `: Element` unless noted):

| Group | Classes | Role |
|---|---|---|
| **Sources** | `InterArrivalSource`, `ScheduleSource`, `RateTableSource` (file `Source.cs`, on `SourceBase`: creation labels, model size, demand factor, `ArrivalsOnStop` and lost arrivals, the pending arrival paused and resumed — each source says when its items arrive), `ReleaseSource` | Create items: by inter-arrival times, data-table driven, by a **rate per period** (a non-stationary Poisson process, `RatePeriod` start / rate / labels, piecewise constant, rate 0 before the first start, the last rate to the end; each next arrival is the exact inversion of the cumulative rate — a unit exponential from the element's stream `rateTable`, walked across the periods (`TimeAfter`, `Mass`), never one period's rate carried past its end; rate 0 for good schedules nothing more, so a stop-when-empty run ends at the last exit; one item per arrival, BLOCKED and the next drawn after it left as `InterArrivalSource`; Pause keeps the remaining expected arrivals; `DemandFactor` multiplies the rates), or **released on demand by control logic** (`ReleaseSource.Enqueue` — FIFO, pushed when downstream accepts, retried on `Unblock`; a finite downstream buffer acts as a kanban). `ScheduleSource` creates each row's items with **that row's** name and labels (it used to read the next row) |
| **Servers / workstations** | `MultiServer`; `Combiner` (n assemblies in parallel); single-job station `SingleJobStation` → `PickingStation`; `AssemblyStation`; `Delay` (no capacity — every item waits its own delay at once, drawn on entry, as `ScheduleWork`; finished items that cannot leave wait in finishing order, BLOCKED when nothing else runs) | Process/transform items over sampled delays |
| **Queues / buffers** | `ItemsQueue` (2026-11-02: `Gated` — items leave against release credits, `ReleaseItems(n)`; it replaced the `GateQueue` element), `CombinerInput`, `LengthLimitedQueue` (§3.13) | Hold items; gate/validate admission; `LengthLimitedQueue` = accumulation lane(s) limited by the total item length |
| **Line supply** (§3.23; Manufacturing pack) | `CallOffPoint` (+ an `OutboundPort` per call-off family), `LineSideUnpacker` (a `Separator`, + empties `OutboundPort`), `PacedLine` (+ `LineStationPort` per station, optional call-off `OutboundPort`) | JIS supply of an assembly line: the reporting point calls off parts per passing vehicle (families: quantity, condition, per-position reference choice tables); the line-side unpacker empties the carriers piece by piece (pull-driven) and returns the empty ones; the paced line indexes every takt, fits parts from its station ports and stops (STARVED) when a part is missing |
| **Sequencing / JIS kitting** (§3.22; Manufacturing pack) | `SequencingStation` (+ports `SequencingOrderPort` `orders`, `SequencingPiecePort` `pieces`) | Fills carriers (wagons, totes) with one piece per call-off order in the order sequence, lot by lot: lot accumulation, operator per carrier or per piece (oldest-order rule), pieces pulled on demand by reference from a list (or fed by links), gaps, stock-outs, carrier labels |
| **Separating** | `Separator` (Core; + port `empties`, an `OutboundPort`), `LineSideUnpacker` (Manufacturing, §3.23) | the **inverse of the Combiner**: holds `Capacity` items and after `ProcessTime` either **unpacks** each — a container (pallet, tote, a Combiner's batch; its `subItems` are the contents): the contents leave together in `Fifo` / `Lifo` order, the empty container through its **empties port** when wired, else through the output after them — or **splits** it into `SplitQuantity` items (itself + copies with its type and labels; `ItemSplit`). Statistics: Unpack opts in to `CountSubItemsOnEntry` (input = containers + pieces); Split records each copy entering through a `DirectLink` from an internal generator (input = items + copies); own metrics `released`, `carriers_emptied` / `split_copies` (§3.11). States IDLE / PROCESSING / SETUP (change) / BLOCKED (pieces ready, downstream refuses). A subclass specialises the release through protected settings (one by one with a piece time, a change time, label copy, empties keeping their place): the `LineSideUnpacker` |
| **Stacking / palletising** (§3.14) | `Stacker` | Builds stacks of items of any type at N positions (rule: first not full / round-robin / label index / key; capacity by count and optionally height) and releases each full stack as **one compound item** (`subItems` = the stacked items) |
| **Sinks** | `Sink` | Consume items and count them; `KeepItems` keeps them (`Kept`, at rest: `IKeepsItems`) — a finished-goods area, a launched ship |
| **Healthcare** | `SimuLean.Healthcare.Agenda` | Appointment book: books each patient on entry into the first free slot of its weekly sessions (`WeeklyShiftPattern` cut into `SlotLength` slots, the model's holidays + its own) at least `LeadTime` ahead, holds it (at home) and releases it at its appointment (an absolute-time event; late if the output is busy). The first `ReservedPerSession` slots of a session are for priority patients (`PriorityLabel` ≠ 0; `Overflow` Ordinary / WaitReserved); a `NoShow` probability expression (the agenda's stream) stamps `no_show`; labels `booked_at`, `appt_time`, `lead_time`. Slots generated lazily a week at a time (look-ahead 3660 days from now; the statistics — slots, booking horizon — only read, they generate nothing; a cancelled extra slot is removed and its session's overbooking given back); statistics of its own (slots offered / booked / reserved unused, no-shows, cancellations, overbooked, lead times, booking horizon — `RunResults`). Later parts (same day): **templates of later periods** (`AddTemplate(from, sessions, slot)`: config `templates` records `date | sessions | slot minutes` or a data table `templateTable` with `From`, `Sessions`, `SlotMinutes`; each day uses the last template from that date), **cancellations** (`Cancel` probability expression drawn at the booking: at a random time before the appointment the slot is freed and the patient books again, label `rescheduled`; the lead time counts from the first booking), **overbooking** (`Overflow = Overbook`: the next session's free reserved slot, else an extra slot squeezed at its last slot's time, at most `OverbookPerSession`) |
| **Naval** | `SimuLean.Naval.ErectionDock` | Slipway / building dock: erects ship blocks in sequence or by precedence on `Capacity` fronts with process resources, keeps the hull, launches the complete ship (§3.20) |
| **Resources** (§3.17) | `Resource`, `TaskExecutor` (namespace `SimuLean.Resources`) | Operators (and next vehicles) that do tasks for elements — their process or the transport of their items — handed out by a `ResourceDispatcher`; elements, but not part of the item flow |
| **Transport network** | `TransportNetwork` (not an element; namespace `SimuLean.Transport`) | The model's network: elements stand at its nodes (`Element.Location`), vehicles are resources travelling on it (§3.9, §3.17) |
| **Storage** (§3.10) | `StorageSystem` (namespace `SimuLean.Storage`; scene `UnityStorageSystem` / `UnityStorageRack`) | Racks / floor lanes with location policies; moves by its transport team |
| **Warehouse supply** (library pack *Warehousing*) | `PickFace` (+ port `empties`), `BaseStockReplenisher` | **Pick face**: one SKU's picking location — up to `Positions` unit loads (compound items; label `sku`), the front one open; hands out **single pieces** (through a link while downstream accepts, or advertised to a list and pulled by query); the emptied load leaves through its empties port and the next opens; a kanban (`ReorderPoint`, `ReorderQuantity`: it accepts loads from its input only while it has ordered them, e.g. a pull from a published storage stock); `StartFull` creates its loads at the start; STARVED when it has no piece (stock-outs counted). **Base-stock replenisher**: a source of unit loads that orders truckloads (`TruckloadSize`) of exactly the consumed mix — consumption = retrievals of the storages it observes (`StorageSystem.Retrieved`, by default those it delivers to) or `NotifyConsumed` — arriving after `LeadTime` (own stream), unloaded only within `UnloadingWindows` (an internal `ShiftDowntime`, OFF_SHIFT with the output blocked), one load at a time or carried by its transport team; `InitialLoads` per SKU stored at time 0 through a `DirectLink`. Both report their counters through `IElementMetrics` (§3.11); build in `ReplenishmentBuilders` over the typed `PickFaceSettings` / `ReplenisherSettings` (a replenisher's SKUs are records `skus[i]` — `sku`, `piecesPerLoad`, `initialLoads`, `ReplenisherSkuSettings` —, not `sku|pieces|initial` texts) |
| **Kitting / assembly stations** | `PickingStation` (+child ports `SkuStockPort`, `ReturnsPort` — both in `PickingStation.cs`), `AssemblyStation` (+`SkuStockPort`) | Picking: per-container pick list (SKU→qty) resolved via a `CompositePickListSource` chain (a model data table `pickTable` → `TablePickListSource`; then container labels → static); picks into the container as sub-items, then batch-returns non-picked stock through the returns port — unless `returnLeftovers` is off (a goods-to-person station whose bins keep their stock between containers). Assembly: precedence network of `AssemblyTaskSpec` tasks (materials by SKU from a shared buffer, per-task duration sampler), up to N concurrent workers, dispatch = first-ready-first, ties by priority then declaration order |

**Auxiliary types (not `Element`s):**
- Interfaces: `IWorkStation` (`StartProcessing` / `CompleteServerProcess`), `IResourceUser`, `IKeepsItems`,
  `IWarmupStatistics`, `ILink`, `IOutputStrategy`, `IVElement`, `IEvent` / `IBackgroundEvent`.
- `ServerProcess : IEvent` — one processing slot: the item it holds, its delay sampler
  (label expressions get the item) and, since 2026-10-06, its **own `ElementStateTracker`**
  (`GetState` / `SetState` / `GetStateRatio` / `GetTimeInState`, in `ElementState` terms);
  calls back `CompleteServerProcess` on completion. `MultiServer` exposes its slots
  (`Slots`, `GetSlotStateRatio`) and `GetUtilization()` (mean per-slot PROCESSING ratio, also a GA
  metric), and **derives its element state from the slots** (PROCESSING if any slot processes,
  else WAITING_FOR_RESOURCE, else BLOCKED, else IDLE) — one source for both levels.
- Shared operators: every processing station (`MultiServer`, `Combiner`, `PickingStation`,
  `AssemblyStation`, `Stacker`, `ErectionDock`) is an `IResourceUser` — its `Resources` (`StationResources`) ask a
  resource team for its process (§3.17). The former `OperatorPool` / `IOperatorStation` were **removed**
; a team with `ItemLeaves` release reproduces them exactly.
- `Item` — the token flowing through the model.
- `InputStrategy` (+ `DefaultStrategy`, `SingleLabelStrategy`, `MultiLabelStrategy`,
  `OriginNameInputStrategy`, `MaxQueueInputStrategy`, `ConditionStrategy` §3.18) — item admission. **One
  question**: `Admits(target, item, origin)` (origin null when the item does not come
  through a link), asked by the link layer before every hand-over through `Element.ValidateInput` /
  `CanAccept`. The former `IsValid(item)` / `IsValid(item, origin)` and `Element.ValidateItem` are gone:
  an element's `CheckAvailability` answers capacity and state only (no admission test inside it — the
  duplicate check without the receiving element made conditions on `element.x` refuse every item).
  `CanAccept` = not input-blocked ∧ `CheckAvailability` ∧ `ValidateInput`. `UpdateStrategy(item)` remains for
  the Combiner pull modes ("auto-update" takes the value from the main item).
- `ElementState` (interned `readonly struct` state token: predefined core states
  IDLE/RECEIVING/PROCESSING/BLOCKED/TRAVELING/WAITING_FOR_RESOURCE, SETUP and the
  downtime states STOPPED/BREAKDOWN/SCHEDULED_DOWN/OFF_SHIFT, STARVED (material
  missing — distinct from IDLE and BLOCKED), plus
  subsystem-specific states via `ElementState.Of(name)`), `ElementStateTracker`
  (time-in-state accounting, keyed by `ElementState`), `StateLogEntry` — the single
  state vocabulary — also used by the per-slot trackers (the former `State` enum was removed
  2026-10-06).
- `HeadlessVElement` — the no-op visual adapter (optionally logging through `SimLog`); the duplicate
  `NullVElement` was removed 2026-10-07.

**Interface-implementation matrix (workstation-type elements):**
- `MultiServer : Element, IWorkStation, IResourceUser` (N slots)
- `SingleJobStation : Element, IWorkStation, IResourceUser` (abstract) — one job at a time that gathers
  what it needs: `Receive` → `RECEIVING` + `OnJobArrived` (subclass gathers, then `StartJob`) → resources
  (`WAITING_FOR_RESOURCE`) → `PROCESSING` (`OnProcessingStarted`) → sent, or `BLOCKED` until `Unblock` → slot freed,
  resources released (`OnJobLeaving` / `OnJobLeft`). Owns the slot (`ActiveProcess`), `Resources`, the stop shift of
  the load time, `GetUtilization()` (time share PROCESSING) and `GetMeanServiceTime()`.
- `Combiner : Element, IWorkStation, IResourceUser` *(was a `SingleJobStation`)* — **`Capacity`**
  assemblies in parallel (config `capacity`, default 1), each an **`Assembly`**: its job slot (`ServerProcess`, index =
  the assembly), its main item, its components per input (`PartsOf`), its requirements (updated by its own main item
  with `updateRequirements`) and its **own matching** — a copy of the pull mode (`InputStrategy.Copy`), updated by its
  main item (`SingleLabel` with no value = "the component of my main item's key"; the key a number or a text — a
  number matches numeric labels, a text text labels, 2026-11-04: it read numeric labels only, so a text key admitted
  nothing; a main item without the key warns). A **`CombinerInput`** is now a port:
  it admits an item when some assembly is gathering (RECEIVING, or WAITING_FOR_RESOURCE: the components of its next job),
  has room on that input and admits it (`PortStrategy`, checked by the links), and gives it to the first such
  assembly in slot order; the components wait in their assembly. After each component the port asks upstream again
  while some assembly still has room on it, not only when a set is complete. The cycle per assembly is the single-job one
  (RECEIVING → resources → PROCESSING → sent or BLOCKED, finished assemblies leave in completion order); the
  element's state is the assembly's with one, the busiest (PROCESSING > WAITING_FOR_RESOURCE > BLOCKED > RECEIVING >
  IDLE) with several; `GetUtilization` = PROCESSING share, or the mean of the assemblies (as a Multi-Server); with
  several, each assembly is a `slot` in the results. One assembly is exactly the former combiner (every baseline
  identical). `IArrivalListener` was removed. **2026-10-26 (`bugfixtests` #82):** a combiner stop that
  refuses items also closes its ports (`CombinerInput.CheckAvailability` false while `owner.IsInputBlocked`; they took
  parts, and completed sets, during a stop) and `OnResumed` asks them upstream again; a main item without its
  `updateRequirements` label needs the configured number (it kept the previous job's); the handler's default
  `requirements` is the scene's `{2}`. **Load height:** when a set is assembled the main item gets
  `HeightLabel` (key `heightLabel`, default `height`, empty = off) = its own height + the layers of its components
  (`Combiner.LoadHeight`: `PartsPerLayer` (key `partsPerLayer`) parts a layer in order, or 0 = packed on the main
  item's `length` × `width` by `TopPacking` — the one packing function the view's `UnityCombiner.PackOnTop` lays the
  parts out with too —, also turned a quarter — the lower of the two, as the view; a
  walled container — label `loadFloor`, its inside floor, from its model's `LoadSocket` well below its top
  (`ItemVisuals.LoadFloor`, written with the model size as `modelLoadFloor`) — takes the parts inside, on its
  floor and in `Combiner.ContainerInside` (86 %) of its footprint: its own height, or its contents' top when they stand out
  of it; before, a tote with a carton in it counted as tote + carton); not set when unknown (a part without a height, missing
  footprints) — so a palletizer's load can be checked by a storage with physical dimensions (§3.10). Standalone
  `stackertests`.
  - `PickingStation : SingleJobStation` — gathers the pick list from its `SkuStockPort`; returns the rest on leaving
    (`ReturnLeftovers`, default on; off keeps it in the stock port).
- `AssemblyStation : Element, IResourceUser` (one event per task, up to `workerCount` tasks at once: a precedence
  network, not one process per job — kept apart from `SingleJobStation`).

**Setups (changeovers, 2026-11-04 / 05; `SimElements/StationSetup.cs`, `SetupMatrix.cs`).** `MultiServer`, `Combiner`,
`SingleJobStation` (→ `PickingStation`) and, since 2026-11-05, `AssemblyStation`, `Stacker` and `Naval.ErectionDock` are
**`ISetupStation`**s: their `Setup` (`StationSetup`, null = none — the default, every model and
baseline unchanged) runs between the job's arrival (the Combiner and the single-job stations: once the set / pick is
gathered, `StartJob`) and the request of its process resources:
- **When** (`SetupWhen`): `None`, `EachItem`, or `LabelChange` — only when the item's value of `Label` (text; a number as
  its invariant text; no label = a value of its own) differs from the **previous item set up or processed at the same slot**
  (a Multi-Server's server, a Combiner's assembly — `ServerProcess.setupKey`); the slot's first item pays one when
  `First` (default true: the station does not know how it was left). The value is recorded when the job's setup is decided.
- **How long**: `Time`, any sampler (expressions read the item and the station), drawn when the setup starts; its own
  random stream (owner = the element, purpose `setup`), so the process draws are unchanged.
- **Setup matrix** (sequence-dependent times, 2026-11-05; `SetupMatrix`, key `setup.matrix` = the name of a model data
  table): columns `from`, `to`, `time`, a row per pair of `Label`'s values; a `time` cell is a number (constant) or a
  distribution's text `Type~p1~p2` (`SamplerSpec`), each such cell with its own stream (owner = the element, purpose
  `setup.matrix[from>to]` — the setup time keeps `setup`, so adding a matrix moves no other draw). `*` = any value; an
  empty `from` = the slot's first setup (no previous value; also a previous item without the label), an empty `to` = an
  item without the label. Lookup, most specific first: (from, to), (from, *), (*, to), (*, *); no row = `Time`. The pair
  is the slot's previous value and the item's (`StationSetup.Needed` records both — `ServerProcess.setupFrom` /
  `setupKey` —, also under EachItem, where an A → A row is a cleaning between two jobs of one type). Built in pass 1a
  from the model's snapshot (`SimContext.Data`): a missing table, a missing column, an empty / negative / unknown time or
  a pair given twice fail the build naming the table and the row. The key is structural (`ParameterSchema.Common`): the
  table's content is varied as `data:<table>` (§3.19), as a source's `dataTable`.
- **Who**: no `Resources` = the machine alone (even when its process needs an operator); a crew of its own = a
  `StationResources` with `Task = TaskType.Setup` (requests and tasks of type Setup: its members travel to the station as
  for a process and hold in **SETUP** — a `HoldUntil(setupEnded, SETUP)` like the process's hold in PROCESSING —, released
  when the setup ends; then the process resources are asked); or **`KeepForProcess`**: the process's use is asked once
  before the setup (`StationResources.AcquireWithSetup` → `ResourceJob.WithSetup`), its members hold in SETUP until
  `StationResources.SetupEnded`, then in PROCESSING — no new request between setup and process (the process use's
  requirements and priority apply; the build accepts it only when the setup team is the process team, and only on the
  stations that ask their process resources once per job — `StationSetup.CanKeepForProcess`: Multi-Server, single-job
  stations, Erection Dock; with *Before The Part* (`acquireBeforeEntry`, §3.17, Multi-Server) the members secured before the part set up
  at once, no request after the entry; on the Assembly Station (one request per task) and the Stacker (one per cycle) it is ignored
  with a warning and the crew is its own).
- **What triggers it on the newer stations**: the **Assembly Station** sets up **per job** (an order), when
  the job arrives and before its first task — the tasks wait (`AssemblyStation.InSetup`; a material arriving meanwhile
  is kept); a setup of a single task is a task of the precedence network already, so none is added per task. The
  **Stacker** (a palletiser's gripper / layer-pattern change) sets up **per item**, once a position can take it and before
  its operator or crane is asked; the item waits at the pick point (SETUP), the previous value is the stacker's (one
  robot). The **Erection Dock** prepares a **front** (staging, jigs, the berth's supports) before a block, the previous
  block tracked per front like a Multi-Server's slot (e.g. `setup.label = blockType`). The Assembly Station and the
  Stacker track the value in a slot of their own (a `ServerProcess` without a station), so `StationSetup` is shared as
  is. Still none: `SequencingStation` (a sequence of its own), `Separator` / `LineSideUnpacker` (a change time of their
  own), `PacedLine`, `Delay` (no job slots to change over).
- **Grouping to avoid setups (`ItemsQueue.GroupBySetup`, `GroupLimit`, keys `groupBySetup` / `groupLimit`)**:
  the receiver says what it is set up for — `ISetupStation.IsSetUpFor(item)`: a LabelChange rule and the slot that takes
  the next item (a Multi-Server's first idle slot, a Combiner's first idle assembly, the free single-job station /
  assembly station / pick point, the dock's first free front) last had the item's value; read-only. The queue that feeds
  it, in `Unblock` before its normal release, offers the oldest held item (within `offerScan`, 0 = all) that a
  destination of its output `GeneralLink` is set up for and could take now (`CanAccept`); the output strategy still picks
  the receiver. An item that leaves ahead of the oldest counts an **overtake**; after `GroupLimit` overtakes in a row
  (0 = no limit) the normal release (FIFO / AnyAccepted) offers the oldest first, and the count restarts whenever the
  oldest leaves — so no item waits behind more than `GroupLimit` later ones (anti-starvation). With grouping on, a new
  arrival joins the waiting items instead of passing them. Why the queue: the release already lives there
  (`offerOrder` / `offerScan`, §3.6), the receiver keeps deciding what it takes (`CanAccept`), and the station needs
  no knowledge of its upstream; a list's puller orders by its own query and a carrier link by its requests, so grouping
  applies behind an ordinary link only. EachItem stations are never "set up for" an item (no gain from grouping).
- **Timing and stops**: the station keeps the setup time (`Element.ScheduleWork` of a `StationSetup.SetupRun` event), so a
  stop pauses and shifts it; a stopped station's Setup request waits as a Process one (`ResourceDispatcher`). The slot (and
  the element: PROCESSING > SETUP > WAITING_FOR_RESOURCE > BLOCKED > RECEIVING > IDLE) is WAITING_FOR_RESOURCE while the
  crew comes and **SETUP** while it sets up; utilisation stays the PROCESSING share. A setup crew can be preempted (§3.17 *Preemption*):
  the setup's `WorkHandle` is the job's work (`ProcessStarted`), the stations' `JobPaused` resume SETUP (`ServerProcess.InSetup`).
  Views: `ServerProcess.InSetup`, `SetupStart`, `SetupDuration` (shifted by pauses).
- **Statistics** (its own counters): `Count`, `TotalTime` (sampled durations of the setups completed), `MeanTime`; warm-up
  resets them; results `setups`, `setup_time`, `setup_time_avg` (§3.11) only for stations with a rule; the time setting up is
  `state:SETUP`.
- Config (the `setup` settings, built by `ResourceBuild.SetupOf` in the factory's pass 1a): `setup.when`, `setup.label`, `setup.setupFirst`,
  `setup.time` (a distribution, `SamplerSettings`), `setup.keepForProcess`, and the crew `setup.setupBy.team`, `setup.setupBy.requirements`, `setup.setupBy.priority`, `setup.setupBy.preemption`,
  `setup.setupBy.preemptable`. An unknown rule, a LabelChange without a label or a setup on a type that does not set up fail the
  build; `setup.keepForProcess` with another team is ignored with a warning. Unity: `StationSetupConfig` (`setup` field of
  `UnityMultiServer`, `UnityCombiner`, `UnityPickingStation`, `UnityAssemblyStation`, `UnityStacker`, `UnityErectionDock` —
  `ISetupUser`; crew `SetupResourceUse`; `matrix` a `UnityDataTable`, exported by its `TableName`), the inspector's
  **Setup** section (the matrix field, a warning for Keep For Process where it is ignored); the Queue's *Group by setup* /
  *Most in a row*. `simulean_load_table` loads a station's matrix from a file too (a table `<station> setup matrix`).
  Tests: `StandaloneTests/setuptests`, reference models `setup_changeover` and `setup_matrix_grouping`, EditMode
  `SetupSceneTests`; library example `Assets/Scenes/LibraryExamples/Setup_Changeover.unity` (a press, three types, a
  matrix from `Setup_Changeover/changeovers.csv`, a setter, a grouping buffer; its model card); guide
  `Assets/Docs/Setups_And_Changeovers.md`.
- Self-scheduling elements (`: … , IEvent`): the sources (`SourceBase`).

### 3.3 The `Item` token and the property model (`SimEntity`, `PropertyBag`)

**`SimEntity`** (`SimElements/PropertyBag.cs`) is the base of every simulation object —
`Item`, `Element` (so every element and resource) — with a `Name` (element name / item type; the only name accessor — `Element.GetName()` and `Item.GetTypeName()` were
removed 2026-11-04, and `IWorkStation` exposes `string Name { get; }`) and user
**`Properties`** (a `PropertyBag`, created on first use: an entity without properties costs nothing; `HasProperties`
checks without creating it). Universal attributes stay typed members of the classes, never property names.

**`PropertyBag`**: one dictionary per kind, each allocated on its first write — **double** (`SetDouble` /
`TryGetDouble` / `GetDouble`: the hot path, no boxing or allocation, verified by `propertytests`), **string**,
**entity reference** (`SetRef` / `GetRef`, runtime only: serialized as the referent's name and resolved when the
model is rebuilt) and **array**. A name has one kind at a time (setting another kind replaces it).
`SetParsed` (number when the text parses, dot or comma; else string; empty ignored), `KindOf`, `Has`, `Remove`, `Names`,
`Count`, `Get` (boxed), `GetText` (numbers as round-trip `"R"` text, references by name, arrays comma-separated),
`CopyFrom`, `Set(name, ExprValue)` — **the one writer of an evaluated value** (number → double, text → string, none →
removed; used by `LabelAssignments`, the reactions' *Set Item Label* / *Set Element Property* and the hand-over's undo,
2026-11-04) — and `View` — a live, read-only `IReadOnlyDictionary<string, object>` (no copy).

`Item` (`SimElements/Item.cs`) carries `creationTime`, string `type`, numeric `myId`, `priority`,
an opaque `vItem` (the visual handle — a GameObject in Unity, `null` headless; setting one raises the static
`Item.VisualAttached`, which the Unity item views use) and a `subItems` list (assemblies / batches). Its **labels are its properties**: `SetLabelValue(label, string)` (parsed),
`SetLabelValue(label, double)`, `SetLabelText` (never parsed), `GetLabelValue`, `GetLabelText`, `GetLabelOrDefault`,
`HasLabel`, `CopyLabelsFrom` keep their names; `Labels` returns the bag's view (no copy; it used to build a new
dictionary per call). Labels are how per-item data (deadlines, inspection flags, processing-time drivers) travels
with the item and feeds label-expression delays, admission strategies and lists. **Terminology: an item
has labels, an element and a resource member have properties** — the same `PropertyBag` underneath. A member's values
(skills, attributes) describe the resource, not something it carries, so they are properties: `Resource.SetProperty` /
`GetProperty` / `GetPropertyText` / `PropertyNames` (named after labels before); a member's configured
properties (`ResourceMemberConfig.Properties`, the team's field `members[i].properties`; scenes migrated) are
`name = expression` records evaluated once when it is built, `LabelAssignments.ApplyTo` — bare names read the member's
own properties, an unquoted text that reads nothing fails the build; a study's `team:T.members[skill=weld]`
(`TeamPropertyMembersParameter`) matches a member whose record is the constant `skill = 'weld'`; and **elements have
properties** (`Element.Properties`, the scene's `SElementProperties`). `GetBatchRequirement` (unused) was removed with the old dictionaries.

**Serialization.** **`PropertyRecords`** encodes a bag as typed records
`PropertySettings` (`name`, `kind` = Number / Text / Reference / Numbers / Texts, `value`, `text` — a reference's target
name —, `numbers`, `texts`) in the element setting `properties[i]`; each number (array) is the catalog parameter
`properties.<name>` (role `Properties` on the list; the record's fields are structural). `HeadlessModelFactory` applies them after creating every element, then resolves references
**by element name in a second pass**; an unknown name leaves the reference **null with a `SimLog` warning** (never a
silent zero) — the one caveat of the property model. Bad records (no name, a NaN number, an unknown kind) fail loudly.
Unity: **`SElementProperties`** (was `SElementLabels`, renamed 2026-11-05 with its editor `SElementPropertiesEditor`; same
script GUID, so scene references survive; its list field `properties`, its API `GetValue` / `SetValue` /
`Has` / `Remove` / `GetProperties` / `Rebuild`) is the element's user properties in the scene — `ElementProperty` (was
the label pair) = a number, or a text, or a reference to another `SElement`; `Records()` exports them and the factory fills the engine element's properties
(references resolved by name, as for any config); during a run its get /
set read and write the element's properties. **Typed consumers (step 5):** multi-label admission compares typed
(no boxing), pick lists, the sink's deadline and the expression error log iterate `Properties.Names`; the other
readers already used the typed `GetLabelValue` / `GetLabelText`.

### 3.4 The link layer (`SimLink/`)

**`ILink`** is an *interface*: `bool SendItem(Item, Element source)` and
`bool NotifyAvailable(Element source)`. Each element holds one output link and **its input links**:
with one input link it holds it directly; when a second is added (`SetInput`, e.g. several upstream
elements each with its own link into one queue, or a list plus connections) it gets an
**`InputFanIn`** (`SimLink/InputFanIn.cs`) whose `NotifyAvailable` asks every input link in turn —
round robin, starting after the one served last — until one sends.

- **`ILink`** — the contract (`SendItem`, `NotifyAvailable`). Implementations: `GeneralLink` (connections; static
  `GeneralLink.CreateLink(origin(s), destinations)`, which resolves each destination's port through
  `Element.ResolveInputFor`; the former 1→1 `SimpleLink` was removed), `InputFanIn` (several input links of one
  element), `ListLink` (push to / pull from a list, §3.16), `ResourceTransportLink` (items carried by a transport
  team, §3.17) and `WalkLink` (items that walk by themselves, §3.17).
- **`GeneralLink`** — many→many. Holds `origins`/`outputs` lists; routes each item to the output its source's
  `IOutputStrategy` picks (one reused `OutputStrategyContext` carrying the model's `ModelParameters`), then fires
  `OnExit` / `OnEntry`. `NotifyAvailable` walks origins calling `NotifyUnblock()` (→ `Unblock()`, or deferred while
  that origin is sending) until one succeeds. **Fair start:** with several origins the link
  keeps a FIFO turn for each origin waiting to send (taken at its first refused `SendItem`, given up when it sends, or
  when an offer finds it with nothing to send); a freed place is offered to the longest-waiting origin first, then the
  next while the destination refuses their items (its input strategy still decides — a user gives a line priority
  there), then the origins not waiting in connection order. Before, it always started at the first origin, which could
  starve the others. One origin: unchanged; the reference models' results did not change.
  **Downtime enforcement lives here:** `SendItem` refuses when the
  source is output-blocked or the chosen target is input-blocked, and
  `NotifyAvailable` skips output-blocked origins — every element honours stops
  without element-specific code.
  **Admission before choosing.** The strategies test
  `CanAccept(item, source)`, which includes the destination's input strategy with the
  origin (`ValidateInput` → `InputStrategy.Admits`), so a destination that would refuse
  the item (origin filter, `MaxQueueInputStrategy`) is skipped instead of making the item
  wait; `SendItem` re-checks the chosen destination for custom strategies.
  **Re-entrancy guard.** While a source is inside `SendItem` (the
  destination's `Receive` runs), a `NotifyAvailable` that reaches it does **not** call its
  `Unblock` re-entrantly — its pending item (head of queue, `lastItem`, BLOCKED slot) is
  still recorded as pending and would be sent twice (seen with `CombinerInput`, which
  notifies upstream from inside `Receive`). The call is deferred to a zero-delay event at the
  same simulation time (`Element.NotifyUnblock`), when the source has updated its own
  state. One mechanism for every element instead of per-element "remove before send".
  `ParameterizedRoutingStrategy` keeps one strategy instance per mode (round robin used to
  restart at output 0 on every call).
- **Routing strategies** (`SimLink/OutputStrategy.cs`): `FirstAvailableStrategy` (default), `RoundRobinStrategy`,
  `ShortestQueueStrategy` (ties in turn, 2026-11-04: the first tied output after the one chosen last, in connection
  order — it gave every tie to the first, two equal aisles got 90 % / 10 %; the first decision is unchanged and no
  reference baseline has a tie), `MostAvailableCapacityStrategy`, `ParameterizedRoutingStrategy`, `PriorityRoutingStrategy`
  (priority > 0 → first available, else shortest queue), `ExpressionRoutingStrategy` (§3.18; replaced
  `LabelRoutingStrategy`), **`ProbabilityRoutingStrategy`** (type `Probability`, weights
  `outputStrategy.weights`, a `double[]` in connection order (a text `w1|w2|…` written to it is read as the
  numbers — `SettingsPaths.Convert` reads any number array from `a|b|c`, `a;b` or `a,b`) — a catalog vector, so studies
  can vary the shares;
  drawn once per item from the source's stream `name/routing` and kept until the item leaves, `IRoutingMemory.Forget`
  called by every link once the item has left: a busy choice is waited for, not redrawn; it is an `IRuleRouting`, so a
  walk or a transport team draws over every destination too), **`BestScoreRoutingStrategy`**
  (1a, 2026-11-07: type `BestScore`, `outputStrategy.scoreExpression` evaluated once per output in the
  context `score` — `out.x` the output scored, `item.x`, `element.x` the router, model and table reads, no random
  functions; the highest score wins, ties to the first in connection order, a missing value scores 0;
  `outputStrategy.whenBestFull` = `Skip` (default: only the outputs that can take the item now are scored) or `Wait`
  (`BestScoreWaitRoutingStrategy`, an `IRuleRouting`: every output that admits the item is scored and the item waits
  for the best, scored again at every offer); an output is kept out by its own admission) and `DelegateOutputStrategy` (a C# lambda
  `(outputs, item, source) → index`, for code-built models). All test `CanAccept(item, source)`, so a stopped
  destination is skipped.
- **Labels set on hand-over** (`SimElements/LabelAssignments.cs`): any element may have
  `EntryLabels` / `ExitLabels` (config `entryLabels` / `exitLabels`, the settings paths; Unity `SElement.entryLabels`
  / `exitLabels`, inspector *Labels on Entry / Exit*): `name = expression` records over the item and the element
  (`reps = (reps ?? 0) + 1`, `t_test = now()`, `valid = rand() < 0.9`). Every link hands items over through
  **`ItemHandOver.Receive(source, target, item)`** (GeneralLink, ListLink, ResourceTransportLink): the source's exit
  labels and the target's entry labels are set **before** the target's `Receive` (its process time and admission
  downstream read them) and undone if it refuses the item; a carried item gets its exit labels when loaded
  (`ItemHandOver.Exit`). An element's own routing is decided before its exit labels: route on entry labels. Statistics
  stay in the links (`OnEntry` / `OnExit`). Random functions in a label draw from **that label's own stream** (owner =
  the element, purpose `exitLabel:NAME` / `entryLabel:NAME`; 2026-10-26, as `choice(...)`
  labels did) — and so does **every other random use of an element** (`Element.RandomStreamFor(purpose)`): an
  expression process / transport / setup time (`ExpressionSampler` takes its stream like any sampler), the routing
  expression (`routing`), the admission condition (`admission`), an agenda's no-shows / cancellations (`noShow`,
  `cancel`), a call-off family's condition and the sequencing station's rule. A change in one random use leaves the draws
  of the others as they were: scenarios that differ only there stay comparable (common random numbers; `bugfixtests`
  #87, #88). `Element.ExpressionRandom` (`name/expr`) is only the fallback of an expression evaluated with no stream.
- `Element.SetInput` never replaces an input link: a second one turns the input into an `InputFanIn` (its origins are
  woken in turn); `ClearInput()` removes them all (rewiring).

> **Design note:** statistics are a *side effect of item movement across links* —
> only the Link calls `OnExit`/`OnEntry`. No element updates its own throughput
> counters directly. If you add a new way to move items, route it through a Link
> or the stats will silently miss it.

### 3.5 Headless execution & serialization (`Headless/`, `Serialization/`)

The engine can run **without Unity GameObjects**, which is the foundation for fast
batch runs and GA optimization.

- **`IVElement`** (`SimElements/IVElement.cs`) is the visual callback interface:
  `ReportState(string)`, `object GenerateItem(int type)`, `LoadItem(Item)`,
  `UnloadItem(Item)`.
- **`HeadlessVElement`** (`Headless/HeadlessVElement.cs`) is the no-op implementation (`Headless/` holds only it;
  `HeadlessModelFactory` and `RunMonitor` live in `Serialization/` with namespace `SimuLean.Headless`). `Element`'s
  constructor defaults to the engine's own silent `NoView` (the same behaviour) when no view is passed, so the same
  `Element` classes run identically with or without Unity.
- **Serialization DTOs** (`Serialization/SimulationConfig.cs`):
  `SimulationConfig` → `Elements` + `Connections` + `MaxSimulationTime`, plus `UseFixedSeed` / `Seed`, `WarmupTime`,
  the calendar (`StartDateTime`, `TimeUnitSeconds`), the end condition (`StopAt`, `StopAtCount`, `StopWhenEmpty`),
  `Downtimes`, `Reactions` (§3.21), `Network`, `TravelMatrices`, `WalkGrids`, `ResourceTeams`, `Lists`, `Tables` + `DatabasePath` (§3.8),
  and `Clone()` (a reflection deep copy, `ConfigCloner.DeepClone`: new fields are copied automatically).
  **As JSON without Unity: `ConfigJson`** — the exchange format of the out-of-editor runner:
  an envelope `{"format": 1, "kind": "model", "model": {…}}` (a newer `format` is refused: `ConfigJson.FormatVersion`),
  the DTOs written by reflection like `ConfigCloner` (a new field travels automatically; on reading each value takes
  its member's declared type, an unknown member fails naming it). **Format 2:** an element
  carries its typed `Settings` (below) as an object under their path keys (`{"capacity": 4, "delayStrategy":
  {"strategyType": "Uniform", "uniformMin": 1}, "tasks": [{"id": "a", …}]}`; settings not given are left out), read as the
  settings class of its `Type` — an unknown setting fails naming the element, the key and the closest valid one; a file of
  format 1 (parameter bags) is refused ("export the model again"). A model has no parameter bag (downtimes,
  reactions, teams and connections are typed) and no `object` value at all: the
  model, the study and the batch are typed through and through, so the untyped `$type` tags are gone and a member declared
  `object`, an interface or an abstract class (other than typed settings) is refused when written or read — an exact
  round trip (`runnertests`: every reference model gives the same JSON and the same results; EditMode
  `ConfigJsonSceneTests`: every example and test fixture scene). A MultiLabel admission's pairs are records too (`multiLabelPairs[i]`:
  `label`, `value`; `LabelValueSettings`), on an input strategy and a Combiner's component admission.
  **The study as data: `StudySpec`** (`Optimization/StudySpec.cs`) — the inputs of a study
  (`Horizon` — −1, the default, = the model's; any other value must be > 0 —,
  replications, seed, `Factors`, `Metrics`, explicit `Scenarios`, `Where`, `Derived`, confidence, warm-up,
  `Detail`, threads) read in the engine (`StudyInputs`, `StudyOptions`, moved from the editor), so the editor commands
  (`simulean_run_headless` / `_start` / `_validate`, `simulean_diagnose`) and the out-of-editor runner read them the same
  way — the shorthand `name=value;…` split at each part's first `=` outside `[…]` (`StudyInputs.Pairs`: a level or a
  derived expression may hold `=`, `Exit.tallies=tis=age`, `late=wait>=60`; a label filter keeps its own,
  `team:W.members[skill=weld]=1|2`) —: `ApplyTo(study)` (a model's study, or a designed experiment's), `Build(model, …)` (checked, `Study.Check`), and
  the answer both return, `Settings(study)` + `Answer(resultSet, full, warnings, settings, model, compareTo)` (plain
  dictionaries: per scenario the runs, `firstError` only when a run failed, `endReasons` counted per reason
  (`EndReasonCounts`, `{"Horizon": 20}`) with `deadlocked` / `stalled` = the runs that did when any did,
  `{mean, halfWidth, n}` per metric — with `full` also `min` / `max` over two runs or more (`IntervalOf(…, range)`; +25 %
  on a metrics-only answer, so not there) —, the model block; the compact answer (not `full`) gives its numbers to
  `CompactDigits` (6) significant digits and the model block only when a run deadlocked, stalled or ended on no progress;
  `full` keeps every digit, the model block, the KPIs and `perRun` (each run's seed, replication, end reason and metric
  values, or its error) — every datum of the compact answer is in the full one, and the exports are unchanged;
  with `CompareTo` (a scenario's name, or its 1-based position in the plan, checked by `ApplyTo` before any run:
  `ScenarioNamed`) every other scenario carries `paired: {to, metrics: {m: {diff, halfWidth, n}}}` — the mean and
  half-width of its runs minus the reference's run of the same seed (`PairedOf`: common random numbers, so the noise
  shared by a seed cancels); given the study's model, one
  `warnings` note per Queue left at the default capacity 100 that a run filled — `content_max` = 100, scenarios whose
  factors set its capacity not counted —, `DefaultCapacityNotes`: there is no unlimited Queue and the default caps
  silently; no number changes; one note per factor whose every level gave identical metric means within each group of
  scenarios that agree on the other factors — with common random numbers a factor that never binds gives identical runs —,
  `IdenticalLevelNotes`, an `Element.capacity` factor adding the element's largest `content_max` when it stayed below the
  smallest level; each scenario's `parameters` echoed typed, `TypedLevel`: `true` / `false` as bools, finite numbers as
  numbers, any other level as its text). `Build` adds `StudyOptions.Warnings` to the check's: a `derived` metric that reads
  no metric and no factor is the same constant in every run (a warning, not an error: a constant can be a reference
  value). **Answer size (`Optimization/AnswerPages.cs`):** an MCP client keeps an answer up to ~50 KB as it shows it
  (indented: `AnswerPages.SizeOf`) and spills a larger one to a file the agent may not read, so `AnswerPages.Study(answer,
  top, sort, offset, more, maxChars)` makes a study's answer readable without losing anything: within `MaxChars` (40000)
  and without paging arguments it is returned as it is; above, `scenarios` gives way to `table` (the first
  `DefaultTop` = 25 by the first metric ascending, each metric's mean and half-width) and `page` (`truncated`, the total,
  the sort, `note` — how to read the rest: the pages, `detail=full`, the export — and `next`, the exact call of the first
  page); with `top` / `offset` / `sort` (a metric, `-metric` or `metric desc` descending; a run without a value last)
  `scenarios` is that page, whole as the answer gives it (fewer than `top` when they would not fit), and `page.next` the
  following one — walking the pages from offset 0 gives every scenario once (`runnertests` `TestAnswerPages`, EditMode
  `ALargeStudyAnswerIsATableAndWalkingItsPagesGivesTheWholeAnswer`). The editor applies it to `simulean_run_headless`,
  `simulean_run_status` and `simulean_results action=read` (`SimuLeanCommands.MaxAnswer`, the same limit as the
  `simulean_types` listings and the `simulean_vocabulary` index); the runner writes its answer whole. As a file, the runner's
  input: `{"format": 1, "kind": "study", "model": {…}, "study": {…}}` (`StudySpec.Serialize` / `Deserialize`; a model
  file alone is a one-run study). Tests: `runnertests`.
  **The runner `simulean` (`Tools/Runner`, README there)** — a console program compiled from the
  engine sources (no Unity): `version`, `run FILE [--out] [--threads] [--quiet]`, `validate FILE`; JSON out with the
  exchange `format`, the `kind` (`version` / `results` / `validation` / `error`) and the **engine fingerprint**
  (`EngineFingerprint.OfSources`: SHA-256 of the engine's `*.cs`, ordinal by relative path, carriage returns removed, 16
  hex digits; computed at build time by `Tools/Runner/Fingerprint.targets` into `EngineBuild.Fingerprint` — `runnertests`
  checks that both agree). The engine's log goes to the standard error and into the answer. `RunnerCli` holds the
  commands (the tests call it in-process). Exit codes 0 / 2 (bad input) / 1. Built on .NET 10 with the engine at
  Unity's C# version (`Engine.props`: `net10.0`, C# language version 9.0); `Tools/Runner/publish.ps1` makes the self-contained
  single files per OS (not trimmed nor AOT: reflection).
  **The editor's studies for the runner: `StudyBatch`** (`Optimization/StudyBatch.cs`) — a study
  with its scenarios applied (`Study.Batch()`: per scenario its model with the levels applied by the study's
  parameters, whatever they are — catalog keys, team members, tables read with the editor's reader —, its replications;
  the metrics and run settings). **`Study.Run` runs through it**, so a study in the editor and one sent to the runner
  (`{"format": 1, "kind": "batch", …}`) are the same code on the same data; a scenario whose levels cannot be applied
  keeps the error for its runs. The runner answers a batch with the whole result set: **`ResultSetJson`**
  (`Results/ResultSetJson.cs`; scenarios, runs, metrics, every object's statistics, run info, series, timeline, jobs,
  samples, extra tables, candidates — what `ResultStore` keeps, plus the time unit and the samples; non-finite numbers
  as null → NaN). `ConfigJson` also writes dates (ISO), key / value pairs and non-finite doubles (`"NaN"`).
  `ElementConfig` = `Id`, `Type` (the engine type name, `MultiServer`), `Name` and **typed `Settings`**: one class per
  element type deriving from `ElementSettings` (`Serialization/ElementSettingsTypes.cs`: `QueueSettings`,
  `MultiServerSettings`, `AssemblyStationSettings`…; `ElementSettings.For(type)` / `TypeFor`), whose public fields are the
  settings — the shared ones on the base (`location`, `workNodes`, `properties`, labels, tallies, `outputStrategy`,
  `inputStrategy`, `processBy`, `transportBy`, `walkBy`, `setup`, `reserveExit`, `pullMaxWait` — **per type**:
  each settings class declares the groups it has with `[SettingGroupsOf(SettingGroups…)]` (default `Flow` =
  routing, admission, transport, walk, pull; a Source has no admission / pull, a Sink no routing / transport / walk, a
  Milk Run only its transport; `processBy` on the resource stations, `setup` on the setup stations, `reserveExit` on the
  Multi-Server, Combiner and Picking Station); `SettingsPaths.Members` leaves the others out, so writing one fails naming
  the groups the type has, the JSON reader refuses it and the catalog does not offer it (the factory's "has process
  resources but its type does not use them" and the queue's `reserveExit` checks became dead and were removed; the scene
  exports a group only for a type that has it)), groups as classes of their own
  (`SamplerSettings` for a flat sampler, `ProcessUseSettings`, `SetupSettings`…) and lists of records as `List<T>` of a
  `SettingsGroup` (`AssemblyTaskSettings`, `MaterialSettings`). A setting's **path** is its field name with the first
  letter in lower case, nested by dots and indexed by `[i]` (`capacity`, `delayStrategy.constantValue`,
  `tasks[1].requirements`, `tasks[0].materials[0].qty`): `SettingsPaths` (reflection, build time / editor only) reads,
  writes, lists (`Values`, the settings given) and declares (`DeclaredPaths`) them. `ElementConfig.Set(path, value)`
  converts the value to the setting's type and **fails for a path the type does not declare, naming the element, the path and
  the closest valid ones** (for element configs: a misspelt or outdated key does not fall back to a default);
  `Get<T>(path, default)` / `Has` / `TryGet` / `Remove` read a setting (a path the type does not have reads as not given, so
  code shared by every type — the factory, `ResourceBuild` — reads its keys on any element). Setting `Type` keeps the
  settings the new type also has. **Defaults:** a field starts at the build's default (a Queue's `capacity` 100, a use's `preemption`
  None, `setup.label` "type"), options are enums (`processBy.preemption`, `.acquire`, `.release`, `setup.when`, a
  Source's `arrivalsOnStop`, a Queue's `offerOrder`… — an unknown name fails when it is written, naming the options), and
  writing null puts the default back (`SettingsPaths.SetValue`); a field stays nullable only where "not given" means
  something of its own — a group that switches a feature on (`processBy`, `transportBy`, `setup`, `walkBy`,
  `outputStrategy`, `inputStrategy`, a flat sampler), a flat sampler's distribution fields, a task's own use
  (`tasks[i].processBy`, which takes the station's when absent), a model size, and a text whose absence is "none". The
  handlers, `ResourceBuild` and the factory read the typed members (`config.SettingsAs<QueueSettings>().Capacity`,
  `ec.Settings.ProcessBy`), not paths; the key-constant classes `ElementConfigKeys`, `ResourceUseKeys` and `SetupKeys` are
  gone (a path is written as its text where code sets one; a downtime's paths are the `DowntimeKeys` constants).
  `ElementConfig.Values()` lists every setting (defaults included); `SettingsPaths.Values(group, all: false)` the ones
  that differ from the defaults. No seeded result moved. Downtimes, teams and reactions are typed the same way (their settings
  read and written by path through `IConfigValues`, one implementation: `ConfigValues`); a connection has typed members (`Port`, `Input`, `Query`)
  and no bag (`BagConfig` is gone). The Unity extractors fill the settings (`ExtractorHelpers.SamplerOf` a `SamplerSettings`, the
  Assembly Station's extractor its typed tasks); the catalog reads them through a bag over `ElementConfig.Values()` (§3.19).
  **A key is the field path.** Every parameter key is the path of the scene field that holds it — the
  path `simulean_set` writes and `simulean_types` lists under `fields` — so an agent learns one name per setting:
  `capacity`, `mode` (a source's), `delayStrategy.strategyType` + `delayStrategy.constantValue` (a *flat sampler*: the
  distribution field's type and the chosen distribution's own fields, `ElementBuildHelpers.BuildRandomProcess(config,
  field)` / `ExtractorHelpers.ExtractRandomProcessConfig`; two samplers of one element no longer collide),
  `interArrivalDistribution.exponentialRate`, `outputStrategy.strategyType` / `.parameterKey` / `.routingExpression` /
  `.weights`, `inputStrategy.*`, `processBy.team` / `.requirements` / `.priority` / `.preemption` / `.preemptable` /
  `.release` / `.releaseAt` / `.holdFor`, `transportBy.*` (`.travelTime`, `.loadTime`, `.unloadTime`), `walkBy.*`,
  `setup.when` / `.label` / `.setupFirst` / `.time` / `.keepForProcess` / `.setupBy.*`, `gated`, `tasks`,
  `locationPolicy`, `positionCount`; a downtime's `stateName`, `timeToFailure`, `periodicPeriod`, `repairBy.*`, `rows`; a
  reaction's `reactionEnabled`, `timing`, `actions[0].value`; a team's `walkGrid`. Those constants are gone (the typed settings classes are the declaration). A distribution of an
  element is a `SamplerSettings` group (`setup.time`, `processBy.holdFor`, `cycleTime`, `tasks[i].duration`,
  `legs[i].time`…: its `strategyType` and the chosen distribution's fields; a text `Type~p1~p2` written to it is read
  strictly by `SamplerSpec.Parse`, the input form of studies and `simulean_set`); a downtime's (`timeToFailure`) is still a
  compact text. Their numbers are catalog parameters named by the distribution's fields (`setup.time.uniformMax`, §3.19). Keys with no field behind them
  are named as the field of the component that holds them, else in clear camelCase: the derived `modelWidth` /
  `modelHeight` / `modelLength`, `tableDatabase`, `arrivalsTable`, `inputOrder`, `pickups`,
  `refX` / `refY` / `refZ`, `properties` (the `SElementProperties` component's list), the team's `members` (its count),
  `loadTime` / `unloadTime` (its members'), the connections' `Port` / `Input` / `Query` (typed members of `ConnectionConfig`, not element keys).
  The random streams keep their purposes (`delay`, `interArrival`, `transportLoad`… — `ElementBuildHelpers.StreamPurpose`,
  the purposes `ResourceBuild` passes), so the renaming moved no seeded result (every reference baseline identical). No alias
  of the old names is read: configs, scenes, tests and docs were migrated once. **An Assembly Station's tasks are typed
  records:** `tasks[i]` with `id`, `predecessors` (a list of ids), `materials[j]` (`sku`,
  `qty`), `priority`, `duration` (a distribution) and its own `requirements` — the former aggregated keys `tasks` (`id|…`
  records) and `tasks.requirements` (`taskId=…` records) are gone; each task's settings have their own path. **A task's
  resource use:** `tasks[i].processBy` (`TaskUseSettings`: `team`, `requirements[j]`, `priority`,
  `preemption`, `preemptable`, `acquire`, `maxWait`, `onTimeout`); what a task does not set is the station's process use.
  **Requirements are typed records** on every element use — `processBy.requirements[j]`, `transportBy.requirements[j]`,
  `setup.setupBy.requirements[j]`, `tasks[i].processBy.requirements[j]` (`RequirementSettings`: `count`, `query`, `label`,
  `team`; the count is a catalog number, the rest structural, by their `[Setting]`); a downtime's
  `repairBy.requirements[j]` too (`RepairCrewSettings`). **Spelling of public names:**
  identifiers and statistic / state names are US English (`utilization`, `Optimization`), the prose British;
  hence the states `TRAVELING`, `TRAVELING_EMPTY` / `TRAVELING_LOADED`, `ChartData.Normalize`,
  `MilkRun.DistanceTraveled`, `MilkRunPhase.Traveling`, the view `TravelingGantryRig`. **Overloaded names** (now split): `release*` named an offer order, a resource
  release and a gate, and `onStop` a source's arrivals and a team's hand-over. Now `offerOrder` / `offerScan` (enum
  `OfferOrder`, `ItemsQueue` / `MultiServer.OfferOrder` / `.OfferScan`), `gated` (a Queue released on command),
  `arrivalsOnStop` (a Source) and `jobOnMemberStop` (a team); scenes and prefabs migrated. `processBy.release` is kept:
  under `processBy` it is unambiguous and it pairs with `processBy.releaseAt`.
  `ConnectionConfig` = `SourceId`, `TargetId`, `ConnectionType` (`General` (= `Simple`) / `StockInput` /
  `PortInput` / `PortOutput` / `ReturnsOutput` / `PushToList` / `PullFromList`; any other type fails the build —
  2026-10-09) and the typed `Port` / `Input` / `Query` (list and port connections).
  No type (a hand-written / JSON config) and `Simple` are `General`, decided in one place,
  `ConnectionConfig.EffectiveType`, which the factory, `ModelDescription` and `ModelLogic` all read (the two
  descriptions tested `== "General"` and listed no next element for an untyped connection the factory wired; scene
  configs always carry `General`, so scene runs and descriptions are unchanged).
- **Named ports.** An element with several inputs / outputs of its own (`IPortedElement`:
  `Ports`, `GetPort(name)`) creates child **port elements** with their own links: `PortInput` connects upstream → the
  target's port named by `port`, `PortOutput` the source's port → downstream (grouped under `owner/port`, so a port's
  outputs share one link like any source's), and a `PullFromList` with a text `port` makes that port the puller. An
  unknown port fails the build naming it. `OutboundPort` is the outbound kind (items wait FIFO and leave when
  downstream accepts; unwired = discarded and counted); the owner hands it items through a `DirectLink` (a private
  one-to-one link that records the exit and the entry, so statistics stay link-driven) and a buffer whose items its
  owner consumes leaves through `ConsumeLink` (exit recorded, handed to nobody). Ports are started and reset by their
  owner; the results report each as an element `owner/port` (group = the owner, §3.11).
- **`SceneExtractor`** (`SimuLean.Unity/Serialization/SceneExtractor.cs`, static) walks the scene's components and
  produces the `SimulationConfig` (`Extract(root, sceneRun)` → `SceneExtraction`; `ExtractElement` for one element in
  an editor), pulling each element's Inspector-configured parameters into the bag through its `<Type>Extractor`.
  Schedule data is captured as the **pre-loaded `arrivalsTable`**, not a file name, so headless runs need no file
  I/O. `UnityModelExtractor` is an optional component around it (model root, JSON export).
  **Build order:** every list of the config (elements, tables, lists, teams, downtimes,
  reactions…) is in **hierarchy order** — `GetComponentsInChildren` under a root, `UnityCompat.FindAllInHierarchyOrder`
  without one (by loaded scene, then depth first as the Hierarchy window shows it, then the component's position on its
  GameObject). The factory builds in config order and `SimClock` breaks ties between simultaneous events in insertion
  order, so the build order shows in seeded results; Unity's object lookup is unsorted and changed after a save, and a
  scene run and a headless run of the same model could then differ replication by replication. Moving an object in the
  Hierarchy changes the order (and may change seeded results); renaming it does not.
- **`HeadlessModelFactory`** (`Serialization/HeadlessModelFactory.cs`, pure .NET)
  rebuilds a live object graph from a `SimulationConfig` (`BuildModel`). Element
  creation is delegated to a per-type **`IElementHandler` registry**
  (`ElementHandlerRegistry.CreateDefault()` — no switch statement); wiring uses
  `GeneralLink` (one shared link per source — every connection that feeds the source's output, `General` targets and
  `StockInput` stock ports alike; a transport team makes it a `ResourceTransportLink`) plus combiner input ports,
  followed by a handler `Wire` pass. `BuildModelCore` is the passes in order, one method each: `ResetBuild`,
  `SetUpCalendar`, the data tables, `BuildLocations`, `CreateElements` (1), `ApplyProperties`, `BuildLists`,
  `BuildResources` (1a), `PrepareWiring` (1b), `WireConnections` (2), `ApplyWaitLimits` (2b), `WireHandlers` (3),
  `BuildDowntimes` (4), `BuildReactions` (5). The factory branches on no element type: what only some types do is a
  **build hook** of their handler (`internal virtual` on `ElementHandlerBase`, defaults those of most types) —
  `ConfigureTransport` (a storage's moves, a milk run's trains; the milk run has no setup / process resources),
  `ConfigureProcessUse` (an assembly station's task uses), `JobsAcquireBeforeEntry` / `JobsReroute` (the Multi-Server),
  `PrepareWiring` (a combiner's input order), `AppliesInputStrategy` / `InputPort` (the combiner's pull mode and
  numbered inputs), `StockPort` (picking / assembly stations), `ReturnsPort` (picking), `ListPushesByTransport` (false
  for a storage, whose retrieval carries the item). A source's table labels for the expression check come from
  `SourceBase.TableLabelWrites`.
- **Handlers are split by assembly.** Each element type has an engine
  **`IElementHandler`** (`Serialization/Handlers/<Type>Handler.cs`, naval `Naval/ErectionDockHandler.cs`; base
  `ElementHandlerBase`: `TypeName`, `Build`, `Wire`; the catalog's schema comes from the type's settings class, §3.19) and a
  Unity **`IElementExtractor`** (`SimuLean.Unity/Serialization/Extractors/Unity<Type>Extractor.cs`, base
  `ElementExtractorBase`: `ExtractParameters`, `ExtractConnections`) with the same `TypeName`, registered in
  `ElementHandlerRegistry.CreateDefault()` and `ElementExtractorRegistry.CreateDefault()` (in `IElementExtractor.cs`); the
  shared extraction helpers are `ExtractorHelpers` (Unity layer). A new element type needs both. **Extraction rules
  headless:** the decisions extraction takes from a wrapper's settings are pure functions of plain values in
  `Serialization/Extractors/ExtractionRules.cs`, a file without Unity references (with the enums they read:
  `StorageRackStyle`, `RackSkuMix`, `RackAccessOverride`, `TeamTravel`, `PhysicsEstimate`) that the standalone suite
  `extractiontests` compiles with the engine: a rack style's lane semantics (`ExtractionRules.RackSemantics`, called by
  `UnityStorageRack.Rebuild`), a resource team's travel, home node and member names (used by
  `UnityResourceTeamExtractor.Extract`, which builds the `ResourceTeamConfig` — the wrapper has no `ToConfig`), and a
  Combiner's input order and slots (`ExtractionRules.InputOrder` / `InputSlots`, behind `UnityCombiner.ResolveInputOrder`
  / `ResolveInputSlots`). Wrappers and extractors gather the values from the scene and call them. **Engine type names
:** `ElementConfig.Type` is the engine's name of the type — `Source`, `Queue`,
  `LengthLimitedQueue`, `Stacker`, `MultiServer`, `Delay`, `Agenda`, `Combiner`, `Sink`, `Separator`, `LineSideUnpacker`,
  `PickFace`, `BaseStockReplenisher`, `MilkRun`, `CallOffPoint`, `PacedLine`, `SequencingStation`, `StorageSystem`,
  `PickingStation`, `AssemblyStation`, `ErectionDock` —, not the Unity wrapper's class name (`UnitySource`…, which a config
  carried before); each handler is named after it (`QueueHandler`). The extractor registry is keyed by the wrapper component
  (`IElementExtractor.ComponentName`, default `Unity` + `TypeName`; `RegisterAlias("UnityStorageRack", …)`: the rack
  subclass extracts as a `StorageSystem`), and `SceneExtractor` writes the extractor's `TypeName`
  (`ElementExtractorRegistry.EngineTypeOf`). The MCP commands still name components (`simulean_add_element type=UnityQueue`
  or the Library name); a model description (`simulean_model`) and a JSON model file carry the engine names. No old name is
  read: configs, tests, reference models and docs were migrated (scenes are extracted, so they needed nothing).
- **Build order (`BuildModel`).** Calendar → data tables (`DataResolver.Build` → `SimContext.Data`, §3.8) → locations
  (`Network`, travel matrices, walk grids = `ModelSpace`, traffic control) → pass 1: elements (+ `location` /
  `workNodes`), user properties (references by name), lists (`SimList` + `ListLink`), resource teams and each
  element's process / transport uses (`ResourceBuild`), Combiner `inputOrder` → pass 2: connections, one link per
  source or port (`GeneralLink`, or `ResourceTransportLink` with a transport team; `StockInput` / `PortInput` targets and
  `PortOutput` sources join that grouping); `ReturnsOutput` / `PushToList` / `PullFromList` wired on their own → pass 3: handler `Wire` → pass 4: downtime generators (`DowntimeBuilder`). The
  factory exposes `Teams`, `Executors`, `Lists` / `ListLinks`, `DowntimeGenerators` / `DowntimesById`, `Network`,
  `Space`, `Monitor`, `Jobs`, `GetElement(id)` and `Find(name)` (display name; ambiguous names fail); its `viewOf`
  constructor argument gives each element its view (scene runs, `SceneModelBuilder`).
- **Kitting / assembly serialization.** Handlers
  `PickingStationHandler` / `AssemblyStationHandler` are registered in
  `CreateDefault()`. They add two connection types — `"StockInput"` (feed → the station's
  `SkuStockPort`, a destination of the feed's single output link: one feed into several stations' stock, or into a
  stock and its next elements, keeps every connection) and `"ReturnsOutput"` (picking `ReturnsPort` → destination) —
  while job/container input and picked/assembled output use the standard
  `nextElements` general connections. Assembly precedence networks serialize as a
  `List<string>` of `"id|preds|sku:qty,…|priority|durationSpec"` records, each
  duration rebuilt through `ElementBuildHelpers.BuildRandomProcess`.
- **Network & storage serialization.**
  `StorageSystemHandler` is registered in `CreateDefault()`; its build logic is engine-pure in
  `Serialization/TransportStorageBuilders.cs` (`TransportStorageConfigCodec.BuildNetwork` + `TransportStorageBuilders`).
  - **Typed records (they were texts `id|x|y|z` / `from|to|length|bidir|type`):** `NetworkConfig.Nodes`
    (`NetworkNodeConfig`: `Id`, `X` / `Y` / `Z`) and `Tracks` (`NetworkTrackConfig`: `From`, `To`, `Length` (&lt; 0 = the
    straight distance), `Bidirectional`, `Type`), deep-cloned by `SimulationConfig.Clone()`. A storage's lanes are typed records: `lanes[i]`
    (`StorageLaneSettings`: `id`, `x` / `y` / `z`, `capacity`, `access`, `sku` (enums), `node`, `slotWidth` / `slotHeight` /
    `slotLength`) in `StorageSystemSettings`.
  - **One network per model:** `SimulationConfig.Network` (`NetworkConfig`: nodes, tracks), built once by
    `HeadlessModelFactory` (`Network`) before the elements are placed. An element's node is its parameter
    `location` (`HeadlessModelFactory.LocationKey`) → `Element.Location`; a location that is neither a network node nor in
    a travel matrix fails the build (likewise each `workNodes` entry, `HeadlessModelFactory.WorkNodesKey`). Its
    optional `workNodes` → `Element.WorkLocations`, where its walking operators work (§3.17).
  - Vehicles are members of resource teams with `Travel = "Network"` (§3.17); a storage's moves use its own
    Transport use (the `processBy` / `transportBy` settings). The former `"TransportSystem"` / `"TransportDestination"` connections,
    the transfer-point and transport-system handlers are gone (no compatibility).
- **Calendar & downtime.** `SimulationConfig` gains `StartDateTime`,
  `TimeUnitSeconds` (`CreateCalendar()`) and `List<DowntimeConfig> Downtimes`
  (`Id`, `Kind` = Timetable | MtbfMttr | Shift | Periodic, `TargetIds`, flat parameter bag;
  intervals as `"targetId|start|duration|state|mode|code|reason"` records, MTBF/MTTR
  as compact sampler specs). `HeadlessModelFactory.BuildModel` sets the calendar
  first, then (pass 4) builds the generators through the engine-pure
  **`DowntimeBuilder`**; **`StartModel()`** resets the lists, transport links and traffic, starts every element, then
  every resource and team, registers the lists' first pulls, starts every downtime generator, schedules the warm-up
  reset (elements, resources, generators, teams; `SimulationConfig.WarmupTime > 0`, §3.2) and installs the
  `RunMonitor`. `SamplerSpec` is the text form of a distribution
  (`Type~p1~p2`, incl. `ExponentialMean~m`): `Parse` → `SamplerSettings` (*2026-10-06*: a null/empty text, unknown type or
  missing / unparseable parameter throws `FormatException` — only StudentT's clamp and Beta's bounds are optional),
  `Format`, `Decode` (a downtime's times, a setup matrix's cells); `ElementBuildHelpers` throws for an unknown
  random-process type instead of returning `Constant(5)`; `RandomProcessConfig.ToSettings()` gives a Unity
  distribution's `SamplerSettings`; `ElementBuildHelpers` moved to its own engine-pure file
  (`Serialization/ElementBuildHelpers.cs`), so the Unity-coupled registry file now
  only holds the handler registry (the extraction helpers moved to the Unity layer). `SceneExtractor`
  exports the clock calendar and every `UnityDowntime` (database timetables are
  resolved into the config, so headless runs need no file I/O); `SimulationConfig.Clone()` copies both.
- **Length-limited lane.** `UnityLengthLimitedQueueExtractor` / `LengthLimitedQueueHandler`
  (registered in both registries); the build is the engine-pure `LengthLimitedQueueBuilder` over `LengthLimitedQueueSettings` (`laneLengths`
  a `double[]` — texts written to it are converted, a decimal comma accepted; a length that is not a positive number
  fails the build —, `gap`, `defaultItemLength`, `speed`,
  `lengthLabel`, `maxItems` (≤ 0 = unlimited), `acceptOversizeOnEmptyLane` and the shared strategy settings).
- **Stacker.** `UnityStackerExtractor` / `StackerHandler` (registered in both registries); the
  build is the engine-pure `StackerBuilder` over `StackerSettings` (`positionCount`, `capacity`,
  optional `positionCapacities` an `int[]` (≤ 0 = the stack capacity), `maxHeight`,
  `heightLabel`, `defaultItemHeight`, `acceptOversizeOnEmpty`, `selectionRule` (enum name),
  `positionLabel`, `keyLabel`, `releasePartialWhenBlocked`, `cycleTime` as a distribution
  (`SamplerSettings`) — absent = instantaneous — rebuilt with its own per-model random stream,
  `stackType`, `copyLabelsFromFirst`, `pickNode` (the node where carriers pick up full stacks) and `positionNodes` (a
  node per position, `List<string>`), plus the shared strategy settings).
- **Reproducibility:** `SimulationConfig` carries `UseFixedSeed` + `Seed` (experiment
  replications pass their own seed instead, §4.4); when
  set, the headless run's `SimClock(seed)` derives every distribution's random
  stream deterministically (handlers pass the clock into
  `ElementBuildHelpers.BuildRandomProcess(config, prefix, clock)`, which keys the sampler's stream
  by the element name (`config.Name`) and the purpose — `Src/interArrival`, `Lathe/delay` (`ElementBuildHelpers.StreamPurpose`: the
  keys' names before 2026-11-05, so renaming the keys moved no stream; likewise the purposes `ResourceBuild` passes); distribution
  settings built by `ElementBuildHelpers.SamplerOf(settings, clock, owner, purpose, path)`: stacker `cycleTime`,
  assembly `task:<id>`; a downtime's texts by `SamplerSpec.Decode(spec, clock, owner, purpose)`, purposes `ttf` / `ttr` / `firstFailure` (keys `timeToFailure` / `timeToRepair` / `firstFailure`) of
  the generator `<config id>:<target>`).
  Without `UseFixedSeed` the clock draws a fresh seed (`SimContext.Seed`, recorded in every run's results as
  `RunResults.Info.Seed`). A scene run builds through the same factory (`SceneModelBuilder` → `HeadlessModelFactory`,
  one build path since 2026-10-07), so a scene run and a headless run of the same model with the same seed draw the
  same numbers per element (§3.6).

**Extract → serialize → rebuild → run** is the reusable pipeline: author once in
Unity, snapshot to a config, run the identical logic anywhere. Consumers: every scene run (`SceneModelBuilder`,
§4.1), the clock's *Run Headless* button (`HeadlessSceneRun`, §4.3), `Study` / the Experimenter (§4.4), the
optimisation objective (`SimulationObjective`, §3.19, §5), `RunData` (§3.6) and the regression net
(`ReferenceModelRunner`, §3.15).

**Diagnosis — `ModelDiagnosis` (`Serialization/ModelDiagnosis.cs`).** The state of a built model at
the current time, read-only: the run monitor's verdict, the elements holding items, stopped or busy (with
`RunMonitor.WaitsFor`, the stall report's description, at any time — items that timed out are said to wait for the
timeout target, wired outside the connections; a BLOCKED element's items: "no receiver has room (full,
refusing or stopped): waits for …"), each team's pending requests and members, the lists.
**Options by name:** every enum key of a config is read by `ConfigParameters.Enum` — any case,
empty = the default, an unknown name fails the build naming the owner, the key and the options (a downtime's `mode`,
`basis`, `overlap`, an interval's or a code rule's mode were ignored silently; `processBy.release` was case-sensitive).
**Downtime settings:** a downtime config holds typed settings
(`DowntimeConfig.Settings`, `DowntimeSettings`: one class for every kind — the common `enabled` (default true; off, the
factory builds its generators, so its settings are checked and its statistics read 0, but `StartModel` never starts them:
a study switches a downtime with `downtime:Id.enabled=true|false`; Unity field `downtimeEnabled`, as `Behaviour.enabled`
is taken) / `stateName` / `mode` (absent = the kind's: `DowntimeBuilder.DefaultStateName` / `DefaultMode` — MtbfMttr
BREAKDOWN, Timetable STOPPED, Shift OFF_SHIFT + AfterCurrent, Periodic SCHEDULED_DOWN; Immediate but for a Shift) /
`blockInput` / `blockOutput`, MTBF `timeToFailure` / `timeToRepair` / `firstFailure` (distributions, `SamplerSettings`;
absent = ExponentialMean 3600 / 300) / `basis` / `busyStates` / `failureCode`, shift, periodic, timetable `rows[i]`
(`DowntimeIntervalSettings`: `target`, `start` — a model time or a date —, `duration`, `state`, `mode`, `code`, `reason`;
the scene extraction leaves a row's `state` / `mode` absent unless the row gives its own, so the downtime's `stateName` /
`mode` — and a study factor on them — reach every row: the extraction used to copy them into each row, and a factor
`downtime:Id.mode` then changed nothing),
the data-table mapping with `codeRules[i]` (`DowntimeCodeRuleSettings`) and `targetKeys[i]` (`DowntimeTargetKeySettings`),
and the crew `repairBy` (`RepairCrewSettings`, not preemptable by default)); `DowntimeConfig` reads and writes them by path
like an element (`IConfigValues`), so a misspelt key (`ttf`, the old `first`) or an unknown option fails when written,
naming the downtime and the closest paths; the JSON reader refuses an unknown setting. The catalog is
`ParameterSchema.Of(DowntimeSettings)` (`ParameterSchema.Downtime`), its distributions offered by their fields
(`timeToFailure.exponentialMean`) as an element's. **Element configs are checked by their typed settings** (`ElementConfig.Set`, the JSON
reader): an unknown key fails naming the element, the key and the closest valid one; writing it found dead keys in three
test configs and one reference model (`separator_split`'s source had `meanValue`, ignored: its Exponential used the default
rate 0.2 — now written explicitly, the baseline unchanged). `Study.Diagnose(time, seed, all)` runs the study's one scenario to a time, or until nothing can happen
(`StopWhenEmpty`), and returns it (`simulean_diagnose`, §4.11). The run monitor's *stalled* means "no event left that
can move items"; a model whose items have not moved for thousands of hours while shifts or reactions keep scheduling
events is not stalled by that rule. The diagnosis therefore also reports **progress**: the last item entry
/ exit of any element (`ElementStatsCollector.LastEntryTime` / `LastExitTime`), the items in the model, the oldest
pending request, the longest task in progress (`TaskSequence.StartTime`), and `ModelDiagnosis.Notes` — facts, not a
verdict, when one of them reaches a quarter of the run (`NoProgressShare`): a blocking can be what the design does or an
error of the model's logic, and telling them apart is the modeller's call. The run monitor's rule, the end of a run and
every result are unchanged.

**Run control — `RunMonitor` (`Serialization/RunMonitor.cs`).** Installed by
`HeadlessModelFactory.StartModel` (so scene runs, which build through the factory, and headless runs alike) from the
config's **end condition**: `StopAt` (element ids or names, **any** elements — sinks, or the buffers that hold the
finished product; fix 2026-10-08) + `StopAtCount` — the clock stops when those elements have received
the count between them, counted from the warm-up (the monitor is an `IWarmupStatistics` in the factory's warm-up list;
it counted from time 0 — 2026-10-26, `bugfixtests` #84) — and `StopWhenEmpty` (stop when nothing is left to do). On the clock's `Idle` (no foreground
event) it sorts the items held outside the sinks: **at rest** in a buffer (queue, lane) without outputs (the finished
product), **surplus** — ready to leave while every element downstream is empty or holds surplus itself, so nothing will
ever take them (an unlimited source blocked behind a full rack whose consumers are done; fix 2026-10-08) —
**starved** — components in a Combiner with no main item whose main-input supply is exhausted (`Combiner.MainOrigin`
and everything upstream of it hold nothing; at `Idle` no source creates more), e.g. the kit a cell took for its next
job while it waited for its welder (fix 2026-10-08; a main input fed by a list is an unknown supply) —
or **waiting**. A Combiner holding components only is reported waiting for its main input (upstream), not its outputs. No item at all → **empty** (`RunStatus.EmptyTime`); none waiting → **finished** (`FinishTime`, not a
stall; `StopWhenEmpty` ends with `RunEndReason.Empty` / `Finished`); items waiting → the run
**stalled** (`Stalled`, logged: a kit that will never be complete); stalled with an item ready to leave that is not
surplus — an element `BLOCKED` or `WAITING_FOR_RESOURCE`, or a buffer with outputs, behind an element holding items —
→ **deadlock** (`Deadlocked`, warned once, naming each waiting element, its state, its items and the elements it waits
for, from the connections — and after them each **full buffer at rest** (no outputs, no room left), which waits for
nothing but holds its feeder up: `TotesBack: full (100/100), no outputs`, `RunMonitor.FullAtRest`; the stall and "no
progress" reports name them too). **What each one waits for (fix 2026-10-09):** a Combiner gathering (its main
item here, not `BLOCKED`) waits for the sources of the component inputs some assembly still lacks — the element wired to
the input (`Combiner.InputOrigin(k)`) or the list the port pulls from (`PullFromList` with `port`) — not for the elements
after it; holding components only, for its main input (element or list); an element pushing to a list waits for that
list's pullers (`list yardList (pulled by bayA, bayB)`). **The others name only a real wait:** their pending resource requests, by team (`team Operators (1 for Process, waiting 12.5)`; the run monitor
gets the model's teams), or `a resource` when `WAITING_FOR_RESOURCE` without one; their downstream elements only when
`BLOCKED` or a buffer (queue, lane) whose items are ready to leave; a station processing its items waits for nothing (it
was said to wait for the element after it). A Combiner's missing components are those of an assembly still gathering
(`Assembly.Gathering`), so a Combiner processing waits for nothing. An unpacking Separator says its carriers apart from their pieces (`RunMonitor.HeldText`:
`2 carrier(s) (capacity 2), 3 piece(s)`). A member's task in the diagnosis is its type, then its name when it says more
(`ModelDiagnosis.TaskName`: `Process`, not `Process Process`). Tests: bugfixtests #48, *The diagnosis names the real wait*
and EditMode `DeadlockAttributionTests` (`ReferenceModels.YardListDeadlock`: the yard as a list of capacity 1, two
Combiners with SingleLabel). A sink downstream, a stopped element or outputs to a list only are obstacles (never
surplus). **An element under a stop does not excuse a stall:** every stop's end
— a generator's repair or next shift, a reaction's *stop for a time*, a clock signal — is a pending foreground event,
and at `Idle` none is left, so a stop still active then never ends (it used to suppress the report: a repair waiting
for the operator its Immediate-stopped machine holds was reported neither as a stall nor as a deadlock); the stopped
element is listed with its stop state (`M2: BREAKDOWN, 1 item(s) → …`; `bugfixtests` #79; no baseline changed). **A night off shift is
the exception:** a shift's next window is a *background* calendar tick, so at a shift end `Idle` fires with
only that tick pending; while an element is under a stop whose end is such a pending tick
(`DowntimeGenerator.StopEndPending`, true for `ShiftDowntime` off shift) the monitor reports nothing and judges again at
the next `Idle` (it used to report a deadlock at the first shift end and, with `StopWhenEmpty`, end the run there;
`bugfixtests` #81). The monitor gets the generators from the factory.
`StopWhenEmpty` also ends the run on a
stall / deadlock. `RunMonitor.IsTerminal(e)` (no outgoing connection) gives the results their **makespan**: the last
entry into a terminal element (sinks and end-of-line buffers / stations, not sources) — so a model without sinks has
one. **`IKeepsItems`** (`KeptCount`, `LastKeptTime`: a sink with `KeepItems`, an erection dock's hull):
kept items are at rest (not counted as held / waiting), the makespan includes the last time an item came to rest (an
erection ends after the block arrives), and such an element downstream is an obstacle, not a reason for surplus (a dock
refusing blocks whose predecessor never comes is reported). Read-only: it moves nothing and schedules nothing but the
no-progress check below. The state is `SimContext.Run` (`RunStatus`).

**Generators that always have a next event.** A calendar-time MTBF generator's next failure and a
timetable / periodic downtime's next start are background events (they only stop an element), so `Idle` — and
the stall / deadlock check — runs beside them: a deadlock beside such a generator is reported where it forms (at the
`Idle` after it), and with `StopWhenEmpty` the run ends there (`bugfixtests` #89; no reference baseline or end reason
moved — they were background events already). A stop already running keeps its end as
a foreground event (a repair's end can free things); a busy-time MTBF arms only while the element works. **Periodic
signals and reactions stay foreground** (a reaction can release items), so a model stalled beside one never becomes
idle. For them the end condition **`StopWhenNoProgressFor`** (model time, 0 = off): the monitor schedules one background
check T after the last item movement — the latest `ElementStatsCollector.LastEntryTime` / `LastExitTime` of any element
and named port (`RunMonitor.LastMovement`, which the diagnosis' `progress` reads too) — and, when it fires, reschedules
itself T after a later movement; if nothing moved for T while items wait (the same waiting set as at `Idle`: not at rest,
starved or surplus) the run ends with **`RunEndReason.NoProgress`** (`RunStatus.NoProgress`, `NoProgressTime` = the last
movement + T, `Waiting` = who waits for what; a warning names them). It is a verdict of its own, not the `Idle` rule's
stall / deadlock (`Stalled` / `Deadlocked` stay false). An element off shift with its next window pending
(`StopEndPending`, the `Idle` rule's exception) is a night, not a lack of progress: checked again T later. No per-event
cost (one event per T of quiet at most); option off = no event, unchanged results (`bugfixtests` #90). Reference models `end_sink_count`,
`end_when_empty`, `stall_combiner`, `deadlock_dead_end`, `finish_with_surplus`, `end_at_storage`, `combiner_starved` (the 2026-10-08 fix
only added keys: `finish_time`, and the makespan of `deadlock_dead_end`'s end buffer); `bugfixtests` #38, #42, #43.

**Scriptable experiments — `Study` (`Optimization/Study.cs`).** **Path grammar:**
`[scope:]Owner.key` — the scope is the text before a colon that comes before the first dot; the owner is the **longest
name of that scope in the model** followed by a dot (elements, teams by name or id, downtimes, reactions, data tables,
lists), so an owner whose name holds dots or spaces is found whole (`Line 1.Buffer.capacity` → `Line 1.Buffer` /
`capacity`), and the key is the rest, dots and all (`Srv.delayStrategy.constantValue` → `Srv` /
`delayStrategy.constantValue`); without a model or a known name the owner ends at the first dot (`Study.Split`; tests:
`agentinterfacetests`). Factors by catalog path
(`Yard.capacity`, `Srv.delayStrategy.constantValue`, `team:T.members`, `downtime:id.key`, `reaction:Name.key` — §3.21), a
declared text setting by its texts (`Station.processBy.priority`, `team:T.requestOrder`: `TextSettingParameter`, §3.19) plus two parameters: **`TeamPropertyMembersParameter`**
(`team:T.members[skill=welding]`: the members carrying a label; extra ones copy the last of them) and
**`SourceTableParameter`** (`Source.table` — the one factor name; `Source.fileName`, the config key it writes, is
refused as a factor naming `Source.table`, 2026-11-04 — or `Source.tableSource`, a `sql:` / `naval:` spec read at build time: a schedule
source's arrivals table read from a file per level — a push / a pull plan; the loader is `TableReader` in the engine,
the Unity reader with Excel in a scene; the run's config gets the table and the `fileName`); **`DataTableParameter`**
(`data:Table.file` / `data:Table.sql`, a data table's source per level — a file read with the
study's loader into the snapshot, or a SELECT left to the run's build — so every consumer of the table sees the
variant; an unknown table is invalid, naming the tables there are); scenarios explicit
(`Scenario(name, reps, values)`) or every combination of the levels (named `factor=level · …`); metrics by path
(`model.makespan`, `Sink.input`, `team:T.utilization`, `list:L.wait_avg`); `Replications` with common random numbers
(seed `BaseSeed + r − 1`), `RunTime`, `SeriesInterval`, `Threads`; `Run(token, progress)` → a `ResultSet` (§3.11; a
cancelled study keeps the runs done); `Validate()`, `Apply(config, scenario)`; **`Check()`** → a
`StudyCheck` (errors, warnings) on the built model without running it: the syntax, then each scenario's levels applied,
its model built and started (never advanced) and every metric resolved on the results it would give
(`MetricCatalog.Problem`: an unknown or ambiguous target, an unknown statistic, a state no element has — a run reads a
misspelt `state:` as 0 —, each with "did you mean", `Serialization/NameSuggestions.cs`); a factor on a flat sampler key
of a distribution the element does not use (`ModelParameterCatalog.InactiveSamplerKey`) is an error saying so when the
key is not in the config (a scene exports the chosen distribution's keys only), a warning when it is (it changes
nothing). Names resolve
uniquely: a factor's element / team (`ModelParameterCatalog.ElementNamed` / `TeamNamed`) and a metric's object
(`RunResults.FindUnique`) fail on two with one name instead of taking the first. The Experimenter component builds and
runs one (`Experimenter.BuildStudy`, §4.4); `SceneStudy.Of(root)` (Unity) makes one of the open scene for scripts and the
MCP. Tests: `resultstests`, EditMode `RunControlSceneTests` (an experiment in small, in one call).

### 3.6 Stochastics (`Stochastics/`, namespace `SimuLean.Stochastics`; was `SimValues/` until 2026-10-07)

Randomness, distributions, probability and distribution fitting in one module:

- **`Streams/`** (`SimuLean.Stochastics.Streams`) — `IRandomEngine` (thread-safe, seedable: `NextDouble`, `NextInt`,
  `NextStandardNormal`, `SetSeed`), `DefaultRandomEngine` (locked
  `System.Random`; default seed is Guid-derived so engines created in the same
  millisecond stay uncorrelated), `RandomEngineManager` (static global engine —
  only the fallback for samplers built without a model stream, e.g. ad-hoc code; no model element
  uses it any more), `IRandomStreamUser` (`SetRandomEngine` — implemented by both distribution bases),
  `UnassignedStreamEngine`.
- **`Distributions/`** (`SimuLean.Stochastics.Distributions`; the sampling contracts `IRandomSampler` /
  `IIntegerProvider` and `ConstantRandomProcess` stay in `SimuLean`) — the bases `ContinuousDistributionBase` /
  `DiscreteDistributionBase` (`Sample()`, parameter validation, engine injection via constructor or
  `SetRandomEngine()`; a discrete distribution is both an `IIntegerProvider` and an `IRandomSampler`, so elements use
  it directly — the `PoissonStrategy` / `BinomialStrategy` / `DiscreteUniformStrategy` wrappers were removed) and a full catalog: Uniform, Normal, Exponential, Triangular, Gamma, Weibull, LogNormal, Beta, ChiSquare,
  StudentT, F, Poisson, Binomial, IntegerUniform, ConstantRandomProcess. `BoxMuller` is the one normal transform
  (`Radius` / `Angle` / `Standard` / `Normal(mean, sd, u1, u2)`): each caller — the stream engine's spare pair, the
  expression functions `normal` / `lognormal`, a choice rule's `Normal`, a routing's replayed draws — draws its own
  uniforms and passes them in, so the sequences of draws are its own. The expression functions `exponential` /
  `triangular` use `ExponentialDistribution.FromUniform` / `TriangularDistribution.FromUniform`, the distributions'
  own inverse transforms.
- **`Probability/`** — the static class `SimuLean.Stochastics.Probability` (namespace `SimuLean.Stochastics`): CDFs, quantiles (`…Quantile`, the inverse of the CDF),
  probability masses, and Γ, ln Γ, B, the regularized incomplete gamma / beta and their inverses, erf / erfc —
  SimuLean's own implementation (§7; it replaced ALGLIB and the former `DistributionUtils`). The distribution fitting added
  digamma / trigamma and the p-values of the Kolmogorov–Smirnov (Stephens' approximation of the Kolmogorov series)
  and Anderson–Darling (Marsaglia & Marsaglia 2004, with the finite-n correction) statistics.
- **`Fitting/`** (`SimuLean.Stochastics.Fitting`) — turns data into a catalog distribution.
  `FitSample` (the values sorted, the summary — n, mean, sd, min / max, CV, skewness —, `ParseNumbers` for text
  pasted from a spreadsheet, decimal commas included); `FittedDistribution` (family, parameters with names, CDF,
  density / mass, mean, `ToSpec()` = the `Type~p1~p2` sampler spec the elements read); `DistributionFitter`:
  maximum likelihood in closed form (Exponential, Normal, LogNormal, Poisson), Newton maximum likelihood (Gamma from
  Minka's start with digamma / trigamma; Weibull on data scaled to its maximum), the unbiased range for Uniform /
  Triangular endpoints (the sample extremes would sit on CDF 0 / 1), moments (Triangular mode, Beta on the range
  widened by one gap, Binomial), the range (discrete uniform); `Evaluate` — KS D (just before / at each value for
  discrete families), A², χ² over equal-width classes (one per value for discrete) merged to ≥ 5 expected,
  log-likelihood, AIC; `FitAll(sample, criterion)` → a list of `FitResult` (`FitCriterion`), ranks by A² (default) or D, or — for integer data — by the χ²
  p-value (KS / AD assume a continuous distribution), and gives each a verdict (p ≥ 0.10 good, ≥ 0.01 doubtful, else
  rejected; p-values of a fully specified distribution, so optimistic with estimated parameters). Families the data
  cannot have are skipped. `RunData.Collect(config, element, quantity, time, seed)` runs a model headless with the
  element's raw samples on and returns its stay times or the gaps between its arrivals / departures ("times of a
  run"); the samples come from `ElementStatsCollector.RecordSamples()` (`StaySamples`, `EntryTimes`, `ExitTimes`;
  null — off — unless asked, so a normal run keeps only the summaries). Tests: `fittingtests` (scipy reference fits,
  §3.15).
- **Seeding model — one random stream per element.** Every stochastic
  source draws from its own `DefaultRandomEngine` created by
  `SimContext.CreateRandomStream(owner, purpose)` / `AssignRandomStream(sampler, owner, purpose)`.
  A distribution built without one gets an `UnassignedStreamEngine`: it draws from
  `RandomEngineManager.GlobalEngine` and warns once per distribution type (`SimLog`) that the draws are not
  reproducible; no model sampler does (every suite runs without the warning).
  Its seed is `SimContext.DeriveStreamSeed(modelSeed, "owner/purpose")`: a 64-bit FNV-1a hash of
  the key's UTF-16 code units (stable across processes and runtimes — **not**
  `string.GetHashCode`, which .NET randomises per process) mixed with the model seed by the
  SplitMix64 finaliser and folded to a non-negative int. The **key, not the creation order**, decides
  the numbers, so adding, removing or reordering other elements does not change what an element
  draws (common random numbers for the GA); a key requested again since the last (re)seed (the slots
  of a `MultiServer`, two elements with the same name) gets `key#1`, `key#2`… in request order.
  Deterministic samplers (constants, label expressions) consume no key. Owners / purposes in use:
  sources `interArrival`; MultiServer `delay` (one per slot); Delay `delay` (one); Combiner / PickingStation / ErectionDock `delay` (one,
  shared by their slots);
  Stacker `cycleTime`; AssemblyStation `task:<id>`; an element's resource uses `processBy.holdFor`, `transportBy.loadTime`,
  `transportBy.travelTime`, `transportBy.unloadTime`; MTBF/MTTR downtime `<config id>:<target>` with
  `ttf` / `ttr` / `firstFailure`; StorageSystem `locationPolicy` (purposes, not keys). Owner = the engine element name, which is the
  GameObject name in the scene and `ElementConfig.Name` headless, so both paths share the streams.
  No shared mutable RNG state across models: parallel models (headless experiments, GA) each own
  their `SimContext`. Pinned derivation values are checked by `StandaloneTests/seedtests` — changing
  the hash changes every seeded result (and the reference baselines).

### 3.7 Statistics (`Statistics/`)

- **`StatisticsCollector`** (abstract) → **`ElementStatsCollector`** (one per
  `Element`). Driven by the link layer's `OnEntry`/`OnExit` hooks: tracks input
  total, output total, current content, peak content, and per-item stay time
  (`_entryTimes` dictionary); an item that leaves inside the very `Receive` that brought it (exit before entry at one
  instant) is recorded as a zero stay; `LastEntryTime` / `LastExitTime` (absolute, kept by the warm-up) give the
  makespan (§3.5). Results read it through `RunResults` (§3.11), never directly.
  **Compound exits (opt-in):** `CountSubItemsOnExit` (default `false`). When an
  element sets it (only `Stacker` does) and an item leaving through the link was never tracked as
  having entered that element, each of its `subItems` that did enter is recorded as leaving with it
  (output, content, stay time) and the compound itself is not counted. The hook is still the link's
  `OnExit`, so the Link-only rule holds; every other element behaves exactly as before.
  **Compound entries** (opt-in): `CountSubItemsOnEntry` — an item entering through the link also records each of its `subItems`
  as entering (input, content, entry time), so an element that takes compounds apart (a separator unpacking) lets the contents
  leave one by one through the link with exact stay times while the emptied compound leaves on its own; still the
  link's `OnEntry`.
  **Generators hold no content:** `TracksContent` (default `true`). The sources
  (`InterArrivalSource`, `ScheduleSource`, `ReleaseSource`) set it to `false` in
  their constructors: their items are created inside and leave through the link without ever
  entering, so the `-1` per exit drove content current / peak / average negative. With the flag off
  `OnEntry`/`OnExit` skip the content variable (it stays 0); input / output counts and stay times are
  unchanged. Their `Start()` calls `base.Start()` (state tracker and statistics reset; before 2026-10-05 they skipped
  it and never reset their statistics). Covered by `StandaloneTests/sourcestatstests`.
  **Sinks keep no entry times:** `TracksStay` (default `true`); a `Sink` sets it to `false` — nothing
  leaves it, so the entry time it kept for every item received only grew with the run.
- **`StatVariable`** (abstract) heads a **family of three statistic types** —
  pick by what the number means:
  - **`StatLevelVariable`** — event counters (input/output totals); no mean (a
    count divided by its updates means nothing).
  - **`StatTally`** — independent observations (stay times, list / request waits,
    field values on push / pull): mean, sample variance and standard deviation
    (Welford, one pass).
  - **`StatTimeWeightedVariable`** — levels integrated over simulation time
    (`Update(delta, simTime)`); its average is the **true time-weighted
    average**. Used for `VarContent`, so average WIP is now correct
    (`ContentAvgTimeWeighted`, result metric `content_avg`). A run's reset opens the averaging window at the run's
    start (`Reset(startTime)`: elements, lists, team request queues), so the time before the first change counts at
    level 0 — before 2026-10-09 the window opened at the first update. `GetIntegral(now)`
    (read-only) returns ∫value·dt so an observer can average any
    interval as Δ∫/Δt without touching the variable (element inspector history).
- **`ElementStateTracker`** accumulates time-in-state per element and keeps a
  chronological `Log` of `StateLogEntry`. It is keyed by the typed `ElementState`
  token and is fed only through `Element.SetState`. `GetAllAccumulated` still
  returns a `Dictionary<string,double>` keyed by the stable `ElementState.Name`
  strings, so report / CSV / GA consumers are unchanged. `KeepLog` (`Element.KeepStateLog`,
  default on — the timeline and the inspector read the log) is turned off by `HeadlessModelFactory.KeepStateLogs = false`,
  which `SimulationObjective` (studies, the optimiser) sets for runs that record no timeline; `Version` counts transitions
  (a resource caches its `Utilization` per time and version); `ElementState` keeps its name's hash.

### 3.8 Project data — SQLite (`Data/`, namespace `SimuLean.Data`)

"global tables" backed by a real per-project SQLite database:

- **`ProjectDatabase`** — thread-safe connection (single handle + lock, safe for
  parallel headless reads); `Query(sql, params)` → rows as name→value
  dictionaries with `?` parameter binding, `Execute`, `TableExists`; `QueryTable` (columns, named even
  with no row), `ListTables`, `ImportTable(name, columns, replace)`. Callers supply the path; the Unity layer uses
  the project setting `databasePath` (default `<project>/SimuLeanData/simulean.db`, `ResultsOutput.DatabasePath`).
- **`XlsxWriter`** (moved from the optimisation module) — a minimal .xlsx writer on
  `System.IO.Compression` (no external library): sheets (names made valid and unique), bold headers, string / number
  cells (NaN / infinity left empty): `XlsxWriter.AddSheet(name)` → `XlsxSheet` (`AddHeaderRow`, `AddRow`,
  `AddTable(ResultTable)`), `Save(path)`. Used by `ResultExport` (§3.11).
- **`SqliteInterop`** — minimal dual-provider P/Invoke layer (UTF-16 entry
  points). Tries the canonical `sqlite3` native library first (drop a
  `sqlite3.dll` into `Assets/Plugins` to use a specific build), otherwise falls
  back to **`winsqlite3.dll`, which ships with Windows 10/11** — so no download,
  no `Mono.Data.Sqlite`, and no API-compatibility-level change is needed.
- **`ProjectDatabaseRegistry`** — process-wide cache of open connections by path.
- **Result tables.** `ProjectDatabase.ExecuteBatch(sql, rows)` runs one
  prepared statement per row inside a single transaction (`sqlite3_reset` /
  `sqlite3_clear_bindings` added to `SqliteInterop`; non-primitive parameters are bound with
  the invariant culture). **`ResultTable`** is an in-memory table (KPIs, event logs)
  filled during a run and written in one transaction by `WriteTo(db)` (drop + create
  with inferred column types; with `replace: false` it appends, adding the columns the table lacks and inserting by
  column name) or exported with `ToCsv()` — each parallel model owns
  its tables, only the final write touches the shared connection. **`MiniJson`** is a
  dependency-free JSON writer/reader (Unity has no `System.Text.Json`) for exchanging
  data with external scripts.
- **Model data tables.** Named tables of the model, as the global
  tables: `SimulationConfig.Tables` (`DataTableConfig`: `Name`, `Source` — `DataSourceKind` Inline / File / Sql —,
  `File` / `Sheet` / `Sql`, and `Columns`, the **snapshot**) and `SimulationConfig.DatabasePath` (the project database
  SQL tables read). The rule: a table is read **once, at the run's start**, never during the run — the Unity layer
  takes the snapshot when it extracts the scene; a config written in code may leave `Columns` null and
  `HeadlessModelFactory.BuildModel` reads the source then (`DataResolver.Build`, right after the calendar: CSV /
  `.data` through `TableReader`, SQL through `ProjectDatabase.QueryTable` — columns named even with no row —; Excel
  only in Unity, a clear error otherwise). At run time `SimContext.Data` (`ModelData`) gives a **`ModelTable`** by name
  (an unknown name lists the tables there are): `RowCount`, `Columns`, `Text` / `Number(row, column)` (invariant, a
  decimal comma accepted), `Find(keyColumn, key)` (indexed on first use), `Require(columns…)` — every error names the
  table, the row and the column. Read-only, so parallel runs share nothing. Expressions read the tables too:
  `SimContext.Data` is exposed through `IDataScope.Data` (element scopes, list-field formulas — `SimList` `EntryScope`). Tests: `datatests` (standalone), EditMode
  `DataTableSceneTests`. Consumers take tables by name: a Schedule Source's arrivals, a downtime's stops and a Picking
  Station's pick lists (below).
- **Schedule Source from a data table.** Source config key `dataTable` (Structural: a
  reference) = a table name; `SourceHandler` takes its snapshot from `SimContext.Data` (an unknown table names the
  source and the tables there are). Precedence: an `arrivalsTable` given in the config (a study's `Owner.table`, which also drops `dataTable` / `tableSource`) > the naval `tableSource` > `dataTable` > a
  `fileName` key (hand-written configs; read at build; with a `dataTable` too the build fails, naming both — one would
  be ignored). A study varies the arrivals as `data:<table>.file` / `.sql`
  The source's own table fields (`fileName` / `sheetName` / `readFileAtRun`) and their migrator `DataMigrator`
  were removed on 2026-10-11: a Schedule source reads a data
  table, a `tableSource`, or rows set from code (`SetArrivalsTable`, tests). Tests: `datatests`, EditMode
  `ScheduleFileTests` (read at run / stale copy / `data:` study through a file data table).
- **Downtime timetable from a data table.** A `Timetable` downtime config may carry
  `dataTable` (`DowntimeBuilder.DataTableKey`) with its mapping — `targetColumn` / `startColumn` / `endColumn` /
  `durationColumn` / `codeColumn` / `reasonColumn`, `durationScale`, `codeRules[i]` (`code`, `state`, `mode`, `factor`,
  `ignore`), `targetKeys[i]` (`key`, `target`: the target column's value per target) — typed downtime settings
  (the factors and the scale are catalog numbers). `DowntimeBuilder` builds the stops from the run's snapshot (`SimContext.Data`, through
  `DowntimeTable.Build`), added to the typed rows' intervals; skipped rows are warned, an unknown table names the
  downtime and the table. So a study's `data:<table>` variants reach the stops too. Unity: `UnityDowntime.dataTable`
  with the mapping fields (the former own query `useDatabase` / `databasePath` / `sql` was removed 2026-10-11).
- **Pick lists from a data table.** Picking Station config key `pickTable` (Structural): the
  handler puts a **`TablePickListSource`** (`Data/`) first in the chain — the table indexed **once** at build (per
  `keyColumn` value, numeric keys written as labels are: `7.0` = label 7; or one shared list without a key column;
  lines of a SKU add up; each container gets its own copy), never queried during the run (the former
  `SqlitePickListSource`, which queried the database per container, and the station's `useSqliteTable` /
  `databasePath` / `tableName` were removed 2026-10-11). Unity: `UnityPickingStation.pickTable` with
  `skuColumn` / `qtyColumn` / `keyColumn` / `containerKeyLabel`. The Fit tab (§4.6.1) also fits a column of a scene data table.
- **`TableSources`** (`Data/TableSources.cs`) — tables a Schedule source reads **when its
  model is built** instead of a copy in the scene, so a scene run, a headless run and a Study read the same current data:
  `sql:<SELECT …>` (the project database) or `naval:<part>[;interval=h][;start=h][;lead=h]:<table, SELECT … or file>` (a
  part generated from a block breakdown, §3.20). The spec is the source's catalog key **`tableSource`** (`File` kind: a
  Study varies it like a file — ship variants as scenarios), with **`tableDatabase`** the database path (the Unity layer
  resolves the default `<project>/SimuLeanData/simulean.db`); `SourceHandler` reads it before the arrivals table, a
  table that cannot be read fails the build naming the source.
- **`IPickListSource`** (core, `SimElements/PickListSources.cs`) — the chainable
  pick-list abstraction: `TablePickListSource` (`pickTable`) → `LabelPickListSource` (`pick_<SKU>` labels,
  `useContainerLabels`) → `StaticPickListSource` (`staticPickList[i]`: `sku`, `qty` — typed records,
  `PickListEntrySettings`), combined by `CompositePickListSource` (first
  non-empty wins).

### 3.9 Transport network (`Transport/`, namespace `SimuLean.Transport`)

Vehicles are **resources**: members of a resource team whose travel
model is the network (§3.17), handed out by its dispatcher like operators, and asked for through an
element's *Transport* use (`ResourceTransportLink`) or a storage's moves. What remains here is the
geometry:

- **`TransportNetwork`** — nodes (`TransportNode`: id + abstract x/y/z) and typed edges (`Track`:
  `TravelPath` / `Conveyor` / `Crane`, per-edge length auto-euclidean or manual, uni/bidirectional,
  optional shared `TrackProfile`). Deterministic Dijkstra (`GetRoute` / `GetDistance`, ties by node-id
  ordinal, cached per query, mode-filtered by track type). Engine-side and Unity-free — the Unity layer
  reads Transforms once at build time. Conveyors are not implemented (the network can define `Conveyor`
  tracks; vehicle routing filters them out).
- **Elements stand at nodes** (`Element.Location`, optional): spatial carriers travel empty to the origin's
  location, load, travel to the destination's and unload.
- **Removed 2026-10-06 (no compatibility):** `TransferPoint` (any placed element is now a pick-up / drop-off
  point), `TransportVehicle` (→ a spatial `TaskExecutor`), `VehicleTransportSystem` (→ a team: dispatcher +
  members; nearest-vehicle dispatch = resource order `distance`), `DelayTransportSystem` (→ abstract
  transport with a defined travel time) and `ITransportSystem` / `MoveRequest`. The `transport` reference
  model kept every flow statistic through the migration (only the vehicle's statistics moved to `res:`).

### 3.10 Storage subsystem (`Storage/`, namespace `SimuLean.Storage`)


The subsystem, its `UnityStorageSystem` wrapper and its serialization handlers (see §3.5
and §4.2):

- **The stock list** (replaced the separate `EntityList` query engine) — a private `SimList`
  (§3.16) per storage with one entry per stored item; `StoredItem` (`Storage/StoredItem.cs`) records where each
  item sits. Queries are list queries: fields `sku` (the item type), `depth` (live, 0 = at the face),
  `storedTime`, every item label (FEFO = `OrderBy("expiry")`) and the list built-ins; FIFO by default. Private: it
  is not a model list (no metrics, no `ListLink`), so every retrieval goes through `RequestRetrieval`, which
  re-homes blockers.
- **`StorageLane`** — one parametric lane model: `AccessDiscipline`
  (Direct/Lifo/Fifo) × depth × `SkuConstraint` (SingleSku/Mixed) × coordinate
  (+ optional transport `NodeId`). `GetBlockers()` returns the units in front
  of a target (empty for Direct). **`StoragePresets`** maps the industry
  types: Selective, MultiDeep, DriveIn, PushBack, FloorBlock, FlowRack.
  In Unity, `UnityStorageRack` generates these lanes from a style; its `CartonFlowRack` style gives
  FlowRack lanes (FIFO, one SKU) for carton live storage. The engine is unchanged (visuals: §4.7.3). The lanes are
  `B{bay}_S{slot}_L{level}` (floor: `B{bay}_R{row}`), `levels` counting the ground level; `Rebuild` regenerates them
  from the layout and keeps a lane's own `node` / `slotSize` by its id. They follow the layout: a field the
  rack declares itself set by `simulean_set` / `simulean_apply` rebuilds them at once (`SimuLeanCommands.RebuildRack`),
  and `UnityStorageRack.LayoutMismatch` makes the extractor warn about lanes left from an older layout (an inspector
  edit without *Rebuild*).
- **`ILocationPolicy` / `IRelocationPolicy`** + `LocationPolicyBase` —
  put-away and blocker-re-homing policies, mirroring the
  InputStrategy/IOutputStrategy injection pattern. Defaults: FirstAvailable,
  ClosestSpot, Random (its own stream: `SimContext.CreateRandomStream(<storage name>, "locationPolicy")` through
  `LocationContext.RandomStream`, created once),
  DedicatedBySku (with fallback).
- **`StorageSystem : Element`** — link-driven `Receive` put-away;
  `RequestRetrieval(ListQuery, onRetrieved)` peeks the best stock entry,
  computes blockers, plans all relocations atomically up-front (fails whole
  if any blocker has no home), then executes moves — chained as transport
  requests to its **`Transport`** team (`StationResources`: travel to the lane's
  node, load, travel, unload; retrieved items go to the storage's own
  `Location`) when a team + node ids are set (real durations), instantaneous
  otherwise. **A lane without a node is at the storage's `Location`** (`StorageSystem.NodeOf`): its moves take the
  team's travel to the rack and the load / unload times; with neither a lane node nor a location the build warns that the
  moves are instantaneous. Delivery goes through the
  output Link so statistics fire normally: a retrieved item waits at the outbound point, still in the storage, until
  the output takes it (`Unblock` — in order into a plain link, every item offered to a carrier or a list —, or
  `SendHeldItem` when a carrier loads it); the callback runs when it leaves (it used to
  leave anyway, without its exit, and a transport team could not take it). Overlapping retrievals never fetch the same
  item (items being fetched are excluded; a relocated blocker is in no lane while on its way), a lane being emptied
  takes no put-away, and the places planned for relocated blockers are **reserved** (`StorageLane.Reserved`, counted
  in `FreeCapacity`). Counters: `TotalRelocations`, `TotalRetrievals`. v1 simplifications documented
  in the class: sequential (not concurrent) moves; destinations planned up-front, not revised mid-flight; a
  retrieval whose target sits behind another retrieval's target in the same lane is refused.
- **Published stock**: when the storage pushes to a model list
  (its output is a `ListLink`, `PublishedList`) every stored item is advertised there (`IAdvertisingLink`), so a storage
  is no longer an end store in a flow model. A pull that reserves one makes the storage retrieve it
  (`SendHeldItem` → blockers re-homed, moves by its `Transport` team) and **deliver it at the puller's location**
  (not at its own); at the end of the move the item leaves through the list link to that puller. The factory gives no
  link transport to a storage that pushes to a list (its own moves carry the item). A pull that cannot start yet (its
  lane being dug, no room to re-home a blocker) waits (`WaitingPullCount`) and is retried when a retrieval ends or the
  output frees. A programmatic `RequestRetrieval` skips items reserved by a puller, withdraws the target from the list
  and hands it to its callback only (the list is not a destination). `Retrieved` is raised at the end of every
  retrieval (the consumption hook of replenishment); `MoveLoadTime` / `MoveUnloadTime` take the element's transport
  load / unload samplers (`ResourceBuild.ApplyStorageMoveTimes`) instead of the member's own times.
- **Put-away by the Transport team** (`PutAwayByTransport`; scene default on, key `putAwayByTransport`):
  a load taken in (`Receive`) whose lane has a node, with a team and a storage `Location`, is not placed at once: its
  place is **reserved**, it waits at the storage's location — at most `InboundCapacity` loads (key `inboundCapacity`,
  default 1; the upstream holds the next, notified when a member loads one) — and a member carries it there (the same
  `Move`: travel, load, travel, unload). It is in the storage (`GetQueueLength`, `PutAwaysInProgress`; a record in no
  lane, as a relocated blocker) but joins the stock — retrievable, published to the list — when it is placed. A lane
  that is not `Direct` and expects a load (`Reserved` > 0) takes no retrieval meanwhile (the load lands at its face;
  waiting pulls are retried at placement). `StoreAtOnce` places loads at once while set: the
  `BaseStockReplenisher` sets it around its **initial stock** (in the racks when the run starts). Before 2026-10-29
  put-away was always instant and an AS/RS crane moved only relocations and retrievals.
- **Pass-through** (`ReleaseAfterDwell` + `MinimumDwellTime`, 2026-11-02; off by default — a rack's minimum
  dwell time; keys `releaseAfterDwell`, `minimumDwellTime`): with an output connection (a plain or a carrier's link, not a list),
  every load that joins the stock (stored at once or placed by a put-away) is due after the dwell (`SimClock.Schedule`,
  not element work: a stop does not pause it; events of an earlier run are ignored); due loads are released oldest
  first, **one at a time** — a release starts only when none is on its way and the outbound point is empty — as a
  retrieval of that load (its blockers re-homed, moved by the team, a lane awaiting a load skipped until it is placed)
  delivered to the outbound point (`Deliver`), where it waits until the receiver takes it. Off, the storage is a store:
  connected to a sink it fills up and nothing leaves (by design; acceptance scene 2026-11-02).
- **Physical dimensions** (`PhysicalDimensions`, 2026-10-29; off by default — the option to use the loads'
  physical size or not; key `physicalDimensions`): each `StorageLane` has the clear room of one position
  (`SlotWidth` across, `SlotHeight` above the support, `SlotLength` along the lane; 0 = no limit; the lane's
  `slotWidth` / `slotHeight` / `slotLength`), and a lane takes a load (put-away and
  re-homed blockers: `Fits`) only if its size labels fit — `LengthLabel` / `WidthLabel` / `HeightLabel` (`length`,
  `width`, `height`: the convention of the length-limited lane and the stacker); a missing label is not checked (the
  `Stacker` labels its stacks and the `Combiner` its loads with their `height`, 2026-10-30, §3.14 / §3.2; the parts'
  own sizes still come from the model's labels). One
  load per position (no packing along the lane by length). In Unity a `StorageLaneDefinition` has `slotSize`
  (`defaultSlotSize` for child lanes) and `UnityStorageRack.SlotRoom(level)` gives a rack's from its layout (the slot
  width × the pitch along the lane, under the next level's beam, the top level and floor cells open above; a unit
  block: the case pitch; through `SlotSizeOf`, so nothing to rebuild). EditMode `StoragePhysicalDimensionsTests`.

Verified in-repo by `resourcetests` (moves by the transport team, a retrieved item waiting at the outbound point (#5),
overlapping retrievals / reserved destinations / a lane being emptied (#6), an AS/RS moved by an axes crane),
`warehousetests` (published stock: a pull that digs, a waiting pull, the storage's move times, programmatic retrieval; the
user example `warehouse_to_line` — scene `Assets/Scenes/LibraryExamples/Warehouse_ToLine.unity`, built by the editor tool
`WarehouseToLineExample`, EditMode `WarehouseToLineExampleTests`; the example builders put a floor under their scene
with the editor tool `ExampleFloor`, also *Tools ▸ SimuLean ▸ Developer ▸ Add Example Floor to the Open Scene*),
`bugfixtests` #35 (stock queries) and the reference models `end_at_storage` and `storage_stock_list_pull`. The original 2026-07 suites (78 + 27
assertions) were kept outside the repository.

### 3.11 Run results (`Results/`, namespace `SimuLean.Results`)

**One snapshot of a run, one vocabulary.** `RunResults.Capture(HeadlessModelFactory, modelName)` reads a built and run
model once, at its clock's time: a **`ResultObject`** per element (`ResultScope.Element`), server slot (`Slot`,
group = the element: a MultiServer's slots, a Combiner's parallel assemblies (`Capacity` > 1) and an Erection Dock's
fronts, `<id>/slot<i>` / `<id>/front<i>`), named port (`Element`, `<id>/<port>`, group = its owner, §3.5),
team member (`Resource`, group = its team; id = its name, or `team/member` when two teams have a
member of that name —, the build warns; one place, `RunResults.ResourceIds`, used by the results, the
timeline and the time series), team (`Team`), list (`List`), downtime generator
(`Downtime`, group = its target — `<id>@<target>` when one downtime has several targets), reaction (`Reaction`, `fired` / `not_met`, §3.21) and the space's traffic control (`Traffic`), each with an ordered set of statistics,
plus a **`RunInfo`** (model, seed, end time, warm-up, observed time, end reason, who waited, and `Warnings`: the model's own
warnings of the run, `SimContext.ModelWarnings`, carried in the result set's JSON so that a run outside the editor brings
them back). An element (or port) with counters of its own
implements **`IElementMetrics`** (`ReportMetrics(set)`, names from `ResultMetrics`): the Separator's `released`,
`carriers_emptied` / `split_copies`, the line-side unpacker's also `pieces_avg` / `pieces_min` / `pieces_max`; the paced line's `index_count`, `empty_pitches`,
`starved_time`, `line_stops`, `line_stop_time`, `fitted`, `mismatches` and the call-off point's `call_offs`; the sequencing
station's `carriers`, `pieces`, `gaps`, `stockouts`, `stockout_time`, `order_wait_avg` / `max`, `lot_wait_avg`,
`orders_waiting_avg`; the pick face's `pieces`, `stockouts`, `loads_received`,
`loads_ordered` (and `carriers_emptied`); the replenisher's `trucks_ordered` / `trucks_received` / `trucks_unloaded`,
`loads_released`, `lead_time_avg` / `lead_time_max` (the Agenda's names), `unload_time_avg`, `consumed`, `backlog`. The
names are **`ResultMetrics`**: element `input`,
`output`, `content` (at the end), `content_avg` (time-weighted), `content_max`, `stay_avg` / `std` / `min` / `max` /
`count`; a Queue's (`ItemsQueue`) **`stay_p90`** (`ResultMetrics.QueueStay`: the 90th percentile, type 7 as a tally's,
of its stays — only a queue keeps them, `ElementStatsCollector.KeepStayValues` → `StayValues`, a `TallySamples`, 8 bytes per
item that left); a Sink's **time in system** `tis_avg` / `tis_std` / `tis_min` / `tis_max` / `tis_p90`
(`ResultMetrics.TimeInSystem`: each item's age as it enters, now − its creation time, the tally `age`; recorded by the
link layer in `ElementStatsCollector.OnEntry` once `RecordTimeInSystem` is on — the Sink's constructor — into
`TimeInSystem`, kept for the percentile; reset with the other stays, warm-up included; 0 before the first item, as
`stay_*`; a Sink's stays are 0 since nothing leaves it). Both are read by `ElementMetrics.TryRead` and absent on other
elements (a study metric on another element is refused, saying whose it is), `utilization` (MultiServer, Combiner, Erection Dock: their own `GetUtilization()`, the mean over slots /
assemblies / fronts; a Queue (`ItemsQueue`): the share **not empty**, PROCESSING + BLOCKED, 2026-10-26; otherwise the
PROCESSING share), `stops`; resource
`utilization`, `tasks`, `state:X`; team `members`, `utilization` (mean of members), `state:X` (the mean over
its members of their share of each state — they add up to 1 and the busy ones to the team's utilization), `served`
(requests served: a job that gave up on its max wait and asked again counts again), `tasks` (completed tasks: a task
withdrawn by a GiveUp is not one), `request_wait_avg` / `std`, `requests_waiting_avg`; **preemption** (only for a team `ResourceDispatcher.Preemptive` — some use of it has a
preemption mode — so other models' results keep their shape): resource and team `preemptions` (tasks taken by requests of
higher priority) and `interruptions` (also by the member's own Immediate stop), a station using the team `interruptions`
(of its jobs, taken by priority or interrupted by a member's Immediate stop — the latter only when the job's use has a preemption
mode, §3.17: a breakdown of a member working for a use without one does not interrupt it, so with operator breakdowns and
preemption by one station only, `team:T.preemptions` can equal the preemptable station's `interruptions`, by design —
over every use of the station: its process, its setup crew and an Assembly Station's per-task uses;
present when any of their teams can preempt), `interrupted_time` (its jobs' processes paused for a lost resource; a pause still open counts up to the capture, as a
running stop in `downtime_total`, 2026-11-04: it was left out until it ended) and `interrupted_time_avg` (=
`interrupted_time` / `interruptions`, the time paused per interruption counted; 0 without interruptions); **setups**
(only for a station with a setup rule, §3.2 *Setups*): `setups` (completed), `setup_time` (their sampled
durations summed) and `setup_time_avg` — a study reads them as 0 for a station without a rule (`ResultMetrics.Setup`,
`simulean_vocabulary`'s `setup`); list `pushed`, `pulled`, `content`, `content_avg`, `content_max`, `wait_avg` / `std`,
`pending_pulls_avg`; downtime `stops`, `downtime_total` (the time the target **actually stood**: each stop from when it
took effect — an AfterCurrent stop once the work in hand is done — to its end, overlapping stops of one generator once, a
stop still running up to the capture; it equals the target's `state:<stateName>` time when nothing else stops it;
`DowntimeGenerator.TotalDowntime`), `downtime_scheduled` (the stop time **as scheduled**: each stop's planned length from
when it was due — a row's / periodic duration, a time to repair, a shift's off-shift window, with a crew wait + repair —,
overlapping stops each; `ScheduledDowntime`; equal to `downtime_total` for Immediate stops that do not overlap. 2026-11-17:
`downtime_total` counted each stop from its scheduled start, so an AfterCurrent stop counted the time the job in hand
still ran — 20 for a stop the machine never stood; baseline `shift_downtime` 7680.0008 → 7673.1687, the jobs in hand at
each shift end) and, only for a downtime with a
repair crew (§3.12), `repairs` (completed), `repair_waits` (crews arrived = repairs started), `repair_wait_avg` (stop start → crew
there, its travel included; over the crews arrived) and `repair_time_avg` (crew there → end of the repair, a pause for a taken
crew member included; over the repairs completed); traffic `waits`, `wait_total`, `deadlocks_resolved`; and by
prefix `state:NAME` (share of time, states entered only) and `field:F:push_avg` / `pull_avg` / `pull_max` (list fields).
**Item statistics:** an element's `tallies` (`name = expression` records over the entering
item and the element, config `tallies` / `tallySplit`, Unity *Item Statistics* / *Split By*) are recorded by
`ElementStatsCollector.OnEntry` — the link layer, like the other item-flow statistics — into
`Statistics/ItemTallies` (values kept for the percentiles, reset with the collector and at the warm-up) and reported as
`tally:NAME:mean / std / min / max / p50 / p90 / count`, per value of the split label as `tally:NAME[value]:…`
(`ResultMetrics.Tally`; only tallies with values — a study metric on a tally the element defines but that got no value reads `count` 0 and NaN otherwise, and `Study.Check` refuses a tally the element does not define or an unknown statistic; a study asks for one as `Exit.tally:time_in_system:p90` — the
`Study` path's scope prefix is only a colon before the first dot). Typical: `time_in_system = age` at a sink, `to_decision =
t_decision - created` (milestones set by entry labels, §3.4), `within_target = age <= 720` (its mean is the share).
**Utilisation = the share of the observed time spent actually working** (PROCESSING); time under an effective stop
(an Immediate stop at once, an AfterCurrent one once the work in progress is done) is accounted in the stop's state,
never PROCESSING — for the element and, since 2026-10-13, for each slot too (a Multi-Server /
multi-assembly Combiner slot whose job was paused by an Immediate stop stayed PROCESSING, so `utilization` included the
repair time while the element's `state:PROCESSING` did not; `downtime_mtbf` 0.888 → 0.790, now equal to the element's
share; the slot rows of `downtime_mtbf` / `shift_downtime` gained `state:BREAKDOWN` / `state:OFF_SHIFT`). The slots
keep their own state for the station's logic (`ServerProcess.GetState`); only their statistics show the stop
(`Element.OnStopShown` → `ServerProcess.ShowStop`).
**`utilization_onshift`** (element): the utilisation divided by the share of time not `OFF_SHIFT`, for elements that
were off shift part of the run. **Agenda** objects add `slots_offered`, `slots_booked`,
`slots_reserved_unused`, `no_shows`, `cancellations`, `overbooked`, `lead_time_avg` / `p90` / `max`, `lead_time_priority_avg` and `booking_horizon`
(time from the end of the run to the first free ordinary slot a new referral could take) — the agenda's own counters.
It is **the only place results read the engine's statistics**: `MetricCatalog` (optimiser / Experimenter metrics,
§3.19), the reference baselines (§3.15), the result sets and their exports (below) and the scene's headless run
(§4.3) all use it. Views: `ToTable(scope)` (wide, a column per statistic) and `ToLongTable()` — the
canonical long format `scope, name, type, group, metric, value` (`ResultTable`, §3.8: CSV / SQLite).
**Across replications:** `StatSummary.Of(values, confidence)` — n, mean, sample std (Welford), min, max and
the confidence interval of the mean `mean ± t(1−α/2, n−1)·s/√n` (`Probability.StudentTQuantile`; no interval with one
value); `ResultSummary.Of(runs, confidence)` summarises every statistic of every object of a list of `RunResults`
(`SummaryObject`: the values per replication and their `StatSummary`; a state or list field missing in a replication
counts as 0 there). `SimulationObjective` keeps each replication's metric values (`Evaluation.MetricValues`, the mean
stays in `Metrics`) and `Replicate(candidate, runs)` returns the `Evaluation` and adds every replication's
`RunResults` to `runs` (seed order). A failed run is a `ResultRun` with its `Error` and no metrics.

**Run control in the results.** Element statistics `last_entry` / `last_exit` (absolute times, from the link
layer: `ElementStatsCollector.LastEntryTime` / `LastExitTime`; absent until the first one); a **`model`** object
(`ResultScope.Model`): `end_time`, `makespan` (the last entry into a terminal element: a sink or an end-of-line buffer /
station — 2026-10-08, was sinks only; an element that keeps its items, `IKeepsItems` (the Erection Dock), counts its
`LastKeptTime`; without a run monitor only sinks count), `deadlocked` / `deadlock_time`, `stalled` / `stall_time`, `empty_time`,
`finish_time` (nothing left to do), `stopped` (an end condition met), `no_progress_time` (only when the
"no progress" end condition ended the run); `RunInfo.EndReason` (Horizon,
StopAtCount — was `SinkCount` until 2026-10-08 —, Empty, Deadlock, Stalled, Finished, NoProgress), `Deadlocked` and `Waiting` (who waited for what); the `runs` table gains `end_reason` and `deadlocked`.
`MetricScope.Model` makes them metrics (`model.makespan`; a time never reached reads NaN).

**Time series:** `SeriesRecorder(model, interval).RunTo(end)` advances the clock interval by interval
(`AdvanceClock` runs every event up to each boundary, so the run is identical to one call — checked) and keeps, per
interval, the change of the cumulative statistics: elements `input` / `output` (items in the interval), `content` (at
the end), `content_avg` (from the content integral), `state:X` (share of the interval); resources `utilization`,
`state:X`; teams the same, the mean over their members, named as the results name the team (its name, not
its id); lists `pushed`, `pulled`, `content`, `content_avg`. An element has no `utilization` series
(its results' `utilization` is a station's mean slot utilisation, not a state share; its
`state:PROCESSING` series is the per-interval share). With a warm-up the series start at its end.
`TimeSeries` (scope, name, metric, interval ends, values) go to `RunResults.Series` (`AddTo`; `ToSeriesTable`).
`SimulationObjective.SeriesInterval` records them for the runs handed to an inspector (`Run`, `Replicate`) only —
plain evaluations never do.

**Result sets and exports:** a **`ResultSet`** gathers what an experiment, an optimisation or a run produced:
`ResultScenario`s (name, parameter values as text), `ResultRun`s (scenario, replication, seed, elapsed time, the named
metrics, the run's `RunResults`), metadata (name, source, model, date, confidence, settings) and extra tables (an
optimiser's `history`). Its tables are **the stable format** (`ResultSet.FormatVersion` = `simulean-results/1`), all
long and keyed by run / scenario: `metadata(key, value)`, `scenarios(scenario, parameter, value)`, `runs(run,
scenario, replication, seed, end_time, warmup, elapsed_s, end_reason, deadlocked)`, `metrics(run, scenario, replication, metric, value)`,
`metric_summary(scenario, metric, n, mean, std, min, max, ci_low, ci_high)`, `results(run, scenario, replication,
scope, name, type, group, metric, value)`, `summary(scenario, scope, name, type, group, metric, n, mean, std, min, max,
ci_low, ci_high)`, `series(run, scenario, replication, scope, name, metric, time, value)`, `timeline(run, scenario, replication, scope,
name, state, start, end)`, `jobs(run, scenario, replication, item, element, start, end)` (below),
`candidates(candidate, iteration, fitness, error, <parameters…>, <metrics…>)` (below) (empty tables left out;
missing numbers are empty cells, never NaN). **`ResultExport.Write(set, folder, ResultFormats, db)`** writes them:
`Csv` (a folder, one file per table), `Json` (`{format, name, tables: {table: [rows as objects]}}`, `MiniJson`),
`Sqlite` (appended to the project database with a leading `set` column — re-exporting a set replaces its rows; extra
tables as `<source>_<table>`) and `Excel` (`XlsxWriter`, §3.8 — readable sheets: *Summary* (metrics per scenario with
n, mean, std, CI), *Scenarios* (parameters, metric means ± half-width), one sheet per kind of object (*Elements*,
*Server slots*, *Resources*, *Teams*, *Lists*, *Downtimes*, *Traffic*: the mean of each statistic per scenario, ± the
half-width with several replications), *Runs*, then the long tables (*Statistics* = `summary`, *Run statistics* = `results`, *Series*), the extra
tables and *Metadata*; no native charts). The Unity side writes through `ResultsOutput` (§4.4). Tests:
`StandaloneTests/resultstests`.

**Results store:** **`ResultStore(ProjectDatabase)`** keeps result sets in the
project database for good, in its own `rs_*` tables (the `Sqlite` export above is untouched and stays a flat export):
`rs_meta` (`schema_version` = `ResultStore.SchemaVersion`, 2 — `rs_timeline` / `rs_jobs`; a version-1
database gets the new tables when opened; a database with a newer version is refused), the catalog
`rs_sets` (integer `id`, name, kind = the set's source, model, created, confidence), then per set `rs_set_meta`,
`rs_metric_names`, `rs_scenarios` / `rs_parameters`, `rs_runs` (scenario, replication, seed, elapsed, the run info, or
none for a failed run) / `rs_run_metrics`, `rs_objects` (scope, id, name, type, group per run) / `rs_stats`,
`rs_series` (one row per point), `rs_extra` (an extra table as `MiniJson`) and `rs_candidates` /
`rs_candidate_values`, with indexes by set and scenario / object name / metric. `Save(set, kind)` → id (a failed save
deletes its rows), `List(model)` (newest first, with scenario / run / candidate counts), `Load(id)` (the same
`ResultSet`, so the same tables), `Rename`, `Delete` (every row of the set), and queries across sets:
`Statistic(scope, object, metric, sets)` and `Metric(name, sets)` → `ResultTable`s with `result_set_id, result_set, scenario,
replication, …`. NaN / infinity are stored as NULL and read back as NaN. **Candidates:** `OptimizationRun` records
every simulated candidate (`OptimizationResult.Candidates`: iteration, the variables' values as text, its
`Evaluation`; cache hits are not repeated) and `CopyCandidatesTo(set)` gives them to `ResultSet.Candidates`
(`ResultCandidate`: iteration, parameters, fitness, metric means, error) — the `candidates` table; the Optimizer's
result set carries them. `ResultSet.StoreId` is the set's id once saved or loaded (0 = not stored; not a table value).
**`ResultSet.Compare(sets)`** makes one set of several to compare them: every scenario becomes `set · scenario`
(parameters and runs with their results kept), the metric names joined, source `comparison`. **Saved on request:**
`ResultsOutput.Keep(set, formats, projectRoot)` = `Export` + `Save` to the store when the project setting
`storeResults` is on (off by default) — used at the end of the Experimenter, the Optimizer and
`SceneStudy.RunAndPublish`; the MCP studies (`simulean_run_headless` / `simulean_run_start`) are only published, never
stored; a store failure is only logged. Any set (a single run, Play or *Run Headless*, too) is kept by hand: *Save* on the clock's run panel or in the Results tab
(`ResultsOutput.Save`, once per set). Nothing is deleted on its own. `ResultsOutput.Store(projectRoot)` opens the store
of the project database (`StoreOverride`: tests keep their sets elsewhere); `ResultsOutput.Stored` is raised on a save.
Tests: `resultstests` (round trip, list, rename, delete, cross-set queries, schema version, compare),
`optimizationtests` (candidates), EditMode `PipelineCommandsTests` (the MCP studies stored).

**Charts (`Results/Charts/`, namespace `SimuLean.Results.Charts`) — the one chart path.** Every
chart of SimuLean is an engine `ChartData` drawn by the engine `ChartRenderer`, so the Results tab, the element
inspector, the dashboards and the exported PNG draw alike, Unity-free and testable:
- **`ChartQuery`** (serialisable: what to plot) — `Kind` (`Bar` mean ± CI, `Box` quartiles, `Histogram`, `Scatter` X
  vs Y per run, `Line` recorded series, `States` share of time stacked), `Y` / `X` = `ChartValue` (`Source` Metric /
  Statistic / Parameter, `Name`, `Scope`, `Objects` = names or `*` patterns separated by `;`), `GroupBy` (Scenario /
  Object / Run: the categories; the other dimension, when several, makes the series), `Scenarios` filter, `Bins`,
  `Confidence`, `ValueLabels`.
- **`ChartBuilder.Build(query, tables | set)`** reads only the long tables (`metrics`, `results`, `scenarios`, `runs`,
  `series`), so a live set, a stored set and a `.json` export give the same chart; a name not found gives an empty
  chart whose `Message` lists what exists. Quartiles as numpy's linear default; histogram bins by Sturges' rule.
- **`ChartData`** — categories, `ChartSeries` (name, colour or palette, per-category `Values` / `Low` / `High`,
  `Boxes`, `Points`, a `Note`), axis titles, `Unit`, sparse column labels (`XStart` / `XCenter` / `XEnd`), `Normalize`.
  Two kinds come only from data built in code (live views): `Stacked` (columns, to 100 % when normalised) and `Area`
  (first series filled, the others as lines).
- **`ChartRenderer`** lays a chart out once on an **`IChartCanvas`** (rectangles, rounded-top bars, lines, polylines,
  circles, text with a size / weight / alignment, turned 90° when needed; `Measure`, `Ascent`) — the modern style
: semibold title left, subtitle (`ChartData.Subtitle`, filled by the builder: "mean ± 95 % confidence
  interval…"), legend as round swatches under them, light grid without a frame, zero line, bars with rounded tops and
  thin whiskers, sizes in points × `scale` (pixels per point: the editor's, the HUD's; `AutoScale` = 1 at 640 × 400),
  Y ticks fitted to the plot height. `Render(data, w, h, theme, scale)` → **`ChartImage`** through **`RasterCanvas`**
  (2× supersampled, then `Downsample`; RGBA top-down; `ToPng` with its own zlib / CRC writer). `RenderPlot` draws only
  the plot box (grid at the quarters, top = `NiceMax`) for views that write labels in their own font. Themes `Light`,
  `Dark`, `Panel`. **SVG:** `ToSvg(data, w, h, theme, scale)` draws the same layout on **`SvgCanvas`** — vector
  shapes (`rect`, rounded-top bars as `path`, `line`, `polyline`, `circle`) and real `<text>` in Inter (fallbacks Segoe
  UI / Helvetica / Arial; measured with the same metrics, so the layout matches where Inter is installed);
  `Save(data, path, w, h, theme)` writes `.svg` or `.png` (2 pixels per point) by the extension. The Results tab and
  the chart assets have *PNG…* and *SVG…* (sizes in points; `ChartDefinition.exportSize`, default 800 × 500).
- **Text: `TrueTypeFont`** — the engine reads and rasterises TrueType fonts itself (`cmap` 4 / 12, `head`, `hhea`,
  `hmtx`, `maxp`, `loca`, `glyf` simple and composite glyphs; exact-area coverage of the flattened quadratic outlines;
  glyphs cached per size and quarter pixel; no hinting / kerning). **`ChartFonts`** gives Inter Regular / SemiBold
  (SIL OFL, `Assets/SimuLean.Unity/Resources/SimuLean/Fonts/*.bytes` + `OFL.txt`): through a host `Provider` (Unity:
  `ChartTextures.UseResourceFonts`, `Resources`, so player builds have them) or on disk (`SIMULEAN_FONTS`, or that
  folder found walking up from the working / application folder — editor, standalone tests). The former 5×7 bitmap
  font was removed.
- **Raw samples.** An element whose collector records samples
  (`ElementStatsCollector.RecordSamples`, off by default) has them in `RunResults.Samples` (`ElementSamples`: stay,
  entry and exit times, by element name): the Fit tab's *times of a run* (`RunData`) reads them there, not from the
  collector.
- **Timeline and Gantt.** Opt-in: `RunResults.Capture(model, name, timeline: true)` /
  `AddTimeline` adds `RunResults.Timeline` — every state interval (`TimelineEntry`: scope, name, state, start, end) of
  the elements and resources, read from their state logs (`ElementStateTracker.Log`, the one source of the time per
  state: the intervals add up to the `state:X` statistics, tested), consecutive equal states merged, at most
  `MaxTimelineRows` per run. Switches: `UnitySimClock.recordTimeline` (Play and *Run Headless*), `Study.RecordTimeline`
  / `SimulationObjective.RecordTimeline`, the Experimenter's and the Optimizer's *Record Timeline* (default off); the
  dashboard asks `CaptureLive(timeline)` when it shows a Gantt. The `timeline` table (`run, scenario, replication, scope,
  name, state, start, end`) goes to every export (Excel sheet *Timeline*) and the results store (`rs_timeline`, schema
  **2**: a version-1 database gets the table when opened). **`ChartKind.Gantt`**: one run (the first scenario kept,
  `ChartQuery.Replication`), a row per object (`Y.Scope`, `Y.Objects`), the window `From` / `To`, the states' colours;
  `ChartData.Segments` (`GanttSegment`); the renderer draws a row label column, time ticks, alternate row shading, and
  when a row has more intervals than pixels each pixel column takes its dominant state (so 10⁴–10⁶ intervals stay
  fast and the SVG small). The Results tab shows a *Timeline* section when the set has one.
  **Job Gantt:** with the timeline on, a **`JobRecorder`** (`JobRecorder.Attach(model)` before the model starts;
  `HeadlessModelFactory.Jobs`) listens to the link layer's `ItemEntered` / `ItemExited` (item flow has one source) and
  keeps every stay of an item in an element (`JobEntry`: item, element, start, end; items numbered by first arrival —
  their ids are not unique —, compared by reference; a pass-through, reported exit-then-entry at one instant, is a
  0-length stay and dropped, as the statistics do; sinks are left out). `RunResults.Jobs` → the `jobs` table (*Jobs*
  sheet; `rs_jobs` in the store, schema 2). **`ChartKind.JobGantt`**: a row per item (by first arrival), a colour per
  element in flow order (mean position along the routes), one run, time window; the Results tab's *Timeline* offers
  *jobs* beside the state kinds. Tests: one stay per entry (= the input statistic), stays never overlap.
- **`ChartPalette`** — the series colours (a Tableau-10-like palette readable on light and dark) and **the** state
  colours / order (`InspectorPalette` reads them); RECEIVING is khaki (a light green was hard to tell from
  PROCESSING's).
Unity: **`ChartDefinition`** (ScriptableObject, *Create ▸ SimuLean ▸ Chart* or the Results tab's *New chart*, saved
under `Assets/SimuLeanCharts/`) holds a `ChartQuery`, its source (`ShownResults` or `StoredSets` by id — several are
compared through `ResultSet.Compare`) and the export size; `ChartTextures.ToTexture` turns an image into a texture
(rows flipped). Its inspector (`ChartDefinitionEditor`) shows only the fields the kind uses, a ▾ beside every name
listing what the results have, and a preview. Tests: `resultstests` (every kind, messages, the `.json` path, the
stacked / area plots, PNG re-read with CRC / zlib), EditMode `ChartDefinitionTests`.

### 3.12 Downtime subsystem (`SimElements/ElementDowntime.cs`, `Downtime/`)

One interruption mechanism serves failures, planned stops and shift calendars
and applies to **any element**.

- **Element side (the mechanism).** `Element.Stop(StopRequest)` returns a
  `StopToken`; `Resume(token)` ends it. A `StopRequest` carries the accounted
  `State` (default STOPPED), a `StopMode` — `Immediate` (pre-empt: pending work is
  paused now and resumed with its remaining time) or `AfterCurrent` (finish work in
  progress, refuse new items; the stop **takes effect** — its state visible — when no work is left —
  the pending `ScheduleWork` jobs, or what an element adds through `HasWorkInProgress` (a resource's current task,
  also while it is held in a process or walks open-ended) —, typical for shift ends; the
  element stops as a whole, so with several slots when the last job in hand ends) — plus `BlockInput`/`BlockOutput` (output default: blocked
  for Immediate, allowed for AfterCurrent), `Code`, `Reason`, `Source`. Stops may
  overlap (counted); the most recent effective stop's state is accounted, and the
  element's own state returns when all end. On resume the element retries its
  blocked output (`Unblock`) and wakes its input (`NotifyAvailable`) — *2026-10-06*:
  repeatedly while upstream keeps handing items over and the element has room (one
  notification used to refill a single slot). The `StopToken` **snapshots** State, Mode,
  BlockInput and BlockOutput when the stop starts (`token.Mode`, `token.BlocksInput`, …),
  so editing a reused `StopRequest` cannot unbalance the block counters. Cancelling a
  `WorkHandle` refreshes the masking like a completion (a pending AfterCurrent stop shows). **Effective start**
: `StopToken.EffectiveStart` / `IsEffective` — an Immediate stop's start, an AfterCurrent stop's the moment
  the element had no work in hand left (also under another stop: then when the paused work has resumed and ended); a stop
  ended before that never took effect. Once effective a stop stays shown. `StopToken.WhenEffective(action)` runs an action
  then (at once if it has): what the generators and a reaction's "stop for a time" time a stop's length from.
- **Pausable work.** Elements schedule processing/setup/travel delays with
  `ScheduleWork(evt, dt)` instead of `SimClock.ScheduleEvent` (a station's setup too, 2026-11-04: an Immediate stop during a
  setup pauses it and shifts its end, §3.2 *Setups*); the returned
  `WorkHandle` is tracked by the element, cancelled on pause (lazy heap deletion) and
  rescheduled with the remaining time on resume. **One work alone can be held** (for preemption): `WorkHandle.Hold` /
  `Release(restart)` pause just that work (a station's job that lost a resource to preemption, not the whole element),
  its remaining time kept — or, with `restart`, its whole `Duration` again; an owner's stop and a hold add up (it runs
  when neither holds it; `IsHeld`). Stations with slots (`MultiServer`, `Combiner`, `ErectionDock`)
  pass the effective stop state to their slots (`OnStopShown` → `ServerProcess.ShowStop`), so per-slot statistics and
  the utilisation leave out stopped time like the element's (`bugfixtests` #78). Retrofitted: `MultiServer` (+ load
  time shifted by the part of the pause each job spent loaded — `PauseShiftFor`; *2026-10-06*
  also in `Combiner`, `SingleJobStation` / `PickingStation` and the naval `ErectionDock`, views only), `Combiner`,
  `SingleJobStation` (`PickingStation`), `AssemblyStation`, `Stacker` (built on it), the resources'
  `TaskExecutor` (every task step and travel leg, traffic segments included) and the `ErectionDock`. Arrival events (sources, timetables)
  intentionally keep absolute times. **New elements with timed work should use
  `ScheduleWork`**, or stops will not pause them.
- **Generators (`Downtime/`, namespace `SimuLean.Downtime`, engine-pure).**
  `DowntimeGenerator` (target element, state, mode, blocking, counters, events) with
  `TimetableDowntime` (replays intervals at absolute times, one pending event,
  `OverlapPolicy` Allow/Serialize/Merge, a late start clips running intervals;
  *2026-10-06*: `DowntimeInterval.Mode` is nullable and falls back to the generator's
  `Mode`, as State does; `CancelPending` also cancels the end events of running stops),
  `MtbfMttrDowntime` (any `IRandomSampler` for time-to-failure/repair, optional first
  failure; `DowntimeBasis.CalendarTime` from the end of the last repair, or
  `BusyTime` counted only in `BusyStates` via `StateChanged`), and `ShiftDowntime`
  (a `WeeklyShiftPattern` — `"Mon-Fri 06:00-14:00,14:00-22:00; Sat 06:00-14:00"`,
  overnight windows, holidays — its own plus **the model's** (`SimulationConfig.Holidays` → `SimContext.Holidays` /
  `IsHoliday`, Unity `UnitySimClock` *Holidays*; 2026-10-09: one working calendar for every shift and
  agenda; a bad date fails the build) — on the model calendar; AfterCurrent/OFF_SHIFT by
  default; the end of a working window is applied a hair early — `ShiftDowntime.CloseTolerance`, the larger of 10 ms
  of calendar time and 1e-9 of the change time — so a job that ends at the closing time up to rounding (the drift of
  summed slot times, a slot length typed in a `float` field) is the session's last: twelve 20-minute slots in 4 h, not
  thirteen, 2026-10-09, `bugfixtests` #50), and `PeriodicDowntime` (*2026-10-13*: a repeating planned
  stop — every `Period` for `Duration`, the first at `First`, optionally only stops that start before `Until`; absolute
  times like a timetable, one start event pending at a time (the next stop is scheduled when one starts; the k-th start
  is `First + k·Period`, no summed drift), a late start skips finished stops and clips the running one; SCHEDULED_DOWN /
  Immediate by default; `Period > 0`, `0 < Duration <= Period` (unless `AllowOverlap`: stops stack), `First >= 0`,
  `Until > First`, else the build fails naming the downtime). **AfterCurrent delays a stop, never cuts it** (
  owner's decision): a timetable row, a periodic stop, an MTBF failure (its time to repair) and a reaction's stop for a
  time start their length when the stop takes effect — a row 30+20 on a machine busy 0..100 stops it 100..120 (before:
  never, the stop ended at 50 while the job ran); with a repair crew the crew is called then. A stop due while an earlier
  one of the same generator is delayed: with `OverlapPolicy.Allow` (or a periodic `AllowOverlap`) it is applied at its
  time and both take effect together (they overlap, counted once in `downtime_total`); with `Serialize` / `Merge` (or a
  periodic stop without overlap) it is queued (`DowntimeGenerator.QueueBehindDelayed` / `BeginOrQueue`) and begins when
  that one ends — before its resume, so the element takes no work in between — for its whole length. **A shift is not
  delayed:** its stops end at the calendar's next window, so the return stays the calendar's and the stop is shorter by
  the job that overran the shift end (`downtime_total` < `downtime_scheduled`); a fixed-hour calendar written as timetable
  rows, by contrast, slides (each return late by that day's overrun). `DowntimeTable` + `DowntimeTableMapping` turn table rows (project
  database or any rows; start as number or date; end or duration; per-code state,
  mode, duration factor, ignored codes) into intervals grouped by target. Typed
  `DateTime`/`DateTimeOffset` cells are converted directly (*2026-10-06*: going through
  invariant text produced MM/dd, which the dd/MM-first parser swapped).
- **Repair crews (`Downtime/DowntimeRepair.cs`).** A generator's optional **`Repair`** (`DowntimeRepair`:
  MTBF / MTTR, timetable, periodic — a shift has none, the build fails if one is given) is an ordinary `StationResources`
  use of a team (requirements all-or-nothing, priority default 1, preemption mode, preemptable — **false** by default
  for a repair), with `Host` = the repair (`IInterruptibleStation`) and `ServeWhileDown` (its requests are served while the
  requester is stopped; `ResourceRequest.ServeWhileDown`, read by the dispatcher's stopped-requester rule). On a stop the
  element shows the new state **`WAITING_FOR_REPAIR`** and the crew is requested — when the stop takes effect (an
  AfterCurrent stop: after the work in hand) — (a Process request of the stopped
  element: the crew's usage by requester shows the repaired elements); spatial members travel to its work point /
  location like a station's operators; when all are there the repair time is drawn and starts (MTBF: the time to repair;
  timetable / periodic: the interval's remaining length), the stop's state switches to the downtime's own
  (`Element.SetStopState(token, state)`: one stop, counted once) and the crew holds PROCESSING; the repair's time is work
  of no element (`WorkHandle.Detached`: the element's stop must not pause it; a taken crew member holds it). At its end the
  crew is released, then the stop ends. Stop = wait + travel + repair; the MTBF calendar basis counts the next failure from
  the end of the repair. With a mode a repair takes members from work of strictly lower priority (§3.17 *Preemption*); a preemptable repair
  whose member is taken pauses (the element back to WAITING_FOR_REPAIR) and goes on — Resume: its remaining time,
  Restart: its whole time. Without a crew a downtime is unchanged (every baseline kept). Statistics (`DowntimeRepair`:
  `Repairs`, `CrewsArrived`, `WaitAvg`, `RepairTimeAvg`, warm-up reset; §3.11 `repairs`, `repair_waits`, `repair_wait_avg`,
  `repair_time_avg`, only
  with a crew). **A team as target:** a `DowntimeConfig` target id may be a resource team's — one generator per member
  (named `<id>:<member>`, its random streams keyed by that name); a member's Immediate stop uses the preemption interruption (only
  work whose use has a preemption mode; the team's `JobOnMemberStop`). Config: `repairBy.team` / `repairBy.requirements`
  / `repairBy.priority` (structural) / `repairBy.preemption` (choice) / `repairBy.preemptable` (bool), built by
  `ResourceBuild.RepairUse`. Unity: `UnityDowntime.repairBy` (`RepairResourceUse`, the resource-use drawer) and
  `teamTargets`. Reference model `repair_crew`; `resourcetests` (Repair crews: …), EditMode
  `RepairCrewSceneTests`. Views: none new — the crew walks to the element and works there like an operator; lamps red
  (`MachineStateView.LampFor`), a darker red in the charts (`ChartPalette`).
- **Serialization:** `DowntimeConfig` + `DowntimeBuilder` (§3.5). Kind `Periodic`: `periodicPeriod`, `periodicDuration`, `periodicFirst`
  (default 0), optional `periodicUntil` (absent = no end), `allowOverlap`, `periodicCode` — numbers offered as parameters, labelled in
  `ParameterSchema.Downtime` (§3.19).
- **Unity.** `UnityDowntime` (§4.2) configures one generator per target element (and per member of a target team);
  the model builds and starts them (`DowntimesById`) and binds them to the component (`UnityDowntime.Bind`).
- Generators implement `IWarmupStatistics`: the warm-up resets their counters (`bugfixtests` #52).
- Verified in-repo by `componenttests` (shift pattern, `ShiftDowntime`), `bugfixtests` (#8–#14, #20, #26, #50, #52,
  #55, #58: typed dates, mutated requests, timetable mode / cancel, progress shift, shift slots, warm-up reset, resume
  releases, unreadable records; the AfterCurrent delay: a job ending before / after the scheduled end, several jobs,
  a stop due while one is delayed per overlap policy, an MTBF failure), stop tests in `resourcetests` (an AfterCurrent
  stop calls its crew after the job) / `signaltests` (a reaction's AfterCurrent stop for a time) / `stackertests` / `listtests` and the reference models
  `downtime_mtbf` / `shift_downtime`; `componenttests` (`PeriodicDowntime`: times, pause / resume, warm-up, late start,
  validation, JSON round trip) and the EditMode `PeriodicDowntimeSceneTests` (scene → `Periodic` config → headless run);
  the original 76-assertion suite was kept outside this repository.

### 3.13 Length-limited accumulation lane (`SimElements/LengthLimitedQueue.cs`)

`LengthLimitedQueue : Element` — one or more parallel single-file lanes (roller /
chain conveyor buffers, floor or gravity lanes) whose capacity is a **length**, not an
item count. Generalised from an industrial application (packages of different lengths
on roller lanes).

- **Item length:** `ItemLength` delegate → numeric label `LengthLabel` (default
  `"length"`) → `DefaultItemLength`; each item also reserves `Gap` to the one in front.
- **Entry:** first lane (lowest index) where used length + gap + item length fits;
  `AcceptOversizeOnEmptyLane` (default true) lets an empty lane take any single item
  so oversize items cannot deadlock; `MaxItems` adds an optional count limit.
  `CheckAvailability` = fits (length and count); the input strategy is checked by the links (`CanAccept`), as for
  every element.
- **Exit:** global FIFO (items are assigned to lanes in arrival order, so the oldest
  item is always at a lane front). A lone item passes straight through when the
  output accepts (like `ItemsQueue`). `Unblock` **drains while the output accepts**
  (not one item per call), so the lane empties after a stop or behind an always-free
  sink.
- **Statistics:** only through the element's links (Link-layer rule); states IDLE /
  PROCESSING (holding items) / BLOCKED (head refused).
- **Conveying:** `Speed` (config `speed`, default 0 = no transit time, as before). With a
  speed each item travels its lane's length ÷ speed (the element's work: `ScheduleWork`, paused by stops), then may
  leave from the front of its lane — the lane fronts that reached the end in order of arrival there (per-lane FIFO, so
  lanes of different lengths do not wait for each other); no pass-through; a list origin advertises an item when it
  arrives at the end. Views: `InTransit`, `DistanceToExit` (the travelling item's remaining distance, pauses not
  subtracted); `UnityLengthLimitedQueue` repositions every frame while conveying. `componenttests`.
- **Views:** `Items`, `LaneOf`, `LengthOf`, `OffsetFromExit` (for drawing items end
  to end), `UsedLength`, `FreeLength`, `CountOnLane`. `GetFreeCapacity` is an estimate
  in units of `DefaultItemLength + Gap` (capacity-based output strategies).
- **Unity / headless:** `UnityLengthLimitedQueue` (§4.2) and
  `LengthLimitedQueueHandler` → `LengthLimitedQueueBuilder` (§3.5).
- Verified by `componenttests` (lane length / gap / item lengths; lanes, oversize, count limit; pass-through, FIFO
  exit, `Unblock` drains, `Start` resets) and the reference model `length_limited_lanes`.

### 3.14 Stacker (`SimElements/Stacker.cs`)

`Stacker : Element` — a stacker / palletiser that receives items **of any type** and builds
stacks at N positions, then releases each finished stack as one compound item. Generalised from
a pick-and-place board stacker of an industrial application; nothing depends on the item type.

- **Flow.** One **pick point** (capacity 1; `CheckAvailability` = pick point free and the input
  strategy accepts). An item waits there until a position can take it; then a **pick-and-place
  cycle** (`CycleTime` sampler, an expression gets the item and the stacker; null = instantaneous)
  moves it onto the stack. The cycle runs through `ScheduleWork`, so stops pause it (§3.12). The
  pick point frees when the cycle starts (the next item can arrive while the current one is
  placed); one cycle at a time. Placement always runs as its own same-time event, never inside
  the link's `SendItem`, so an item's entry is recorded before any stack can leave.
- **Positions.** `StackPosition` views (items bottom first, `Height`, `Key`, `StartedAt`,
  `IsComplete`, `Compound`, `CompletionReason`). Capacity = `Capacity` items (per-position
  overrides `StackPosition.Capacity` / `StackPosition.MaxHeight`) and optionally `MaxHeight` using the item height
  (`ItemHeight` delegate → numeric label `HeightLabel` → `DefaultItemHeight`; 0 = unknown, does
  not count). `AcceptOversizeOnEmpty` (default true) lets an empty position take any single item.
- **Position choice.** `StackSelectionRule`: `FirstNotFull` (default), `RoundRobin`,
  `ByLabelIndex` (numeric label `PositionLabel`, 0-based; missing / out of range → first not
  full), `ByKey` (stack per key = text of `KeyLabel`, or the item type; a new key takes an empty
  position). `PositionSelector` (`(item, stacker) → index`, −1 = wait) overrides the rule.
- **Completion.** A stack completes when it reaches its capacity (`full`), when another item as
  high as the one just placed would exceed the height limit (`height`), on `ReleaseStack(i)` /
  `ReleaseAll()` (`forced`; deferred to after the placement when the running cycle targets that
  position) or — with `ReleasePartialWhenBlocked` (default) — when the waiting item cannot be
  placed because open stacks are in its way (its own stack too high for it, or every position
  holds another key): the blocking stack closes (the oldest when several qualify; `blocked`), so
  the stacker cannot deadlock.
- **Release (reuses the batching convention).** The complete stack becomes a new `Item` of type
  `StackType` (default `"Stack"`) with the stacked items as `subItems` (`Item.AddItem`, as the
  combiner / picking / assembly stations do), labels of the first item copied
  (`CopyLabelsFromFirst`) plus `stackPosition`, `stackCount`, `stackHeight`, `stackKey`,
  `stackReason`, and — when the stack's height is known (> 0) — its own height in `HeightLabel` (before,
  the stack carried its first item's copied `height`). It is sent through the **output link**; refused stacks wait in completion order
  and are retried on `Unblock`. The position is free once its stack has left.
- **Statistics (Link layer only).** Items enter through the input link; the compound leaves
  through the output link and the stacker opts in to `CountSubItemsOnExit` (§3.7), so input =
  output = stacked items, content and stay times are exact; downstream counts the compound as one
  item. States: PROCESSING (cycle), BLOCKED (a complete stack cannot leave or the waiting item
  cannot be placed), RECEIVING (stacks forming), IDLE. Counters `ItemsStacked`,
  `StacksReleased`, `PartialStacksReleased`.
- **Views.** C# events `ItemArrived`, `CycleStarted(StackerCycle)` (item, position, level,
  start, duration, heights), `ItemPlaced`, `StackCompleted` (compound built, before it is sent —
  the place to give it a visual), `StackReleased`; `IVElement.LoadItem` at the pick point and
  `ReportState("Cycle" / "Placed" / "StackComplete" / "StackReleased")`.
- **Unity / headless:** `UnityStacker` (§4.2), `GantryStackerView` (§4.10) and
  `StackerHandler` → `StackerBuilder` (§3.5). Engine-pure.
- Verified by the in-repo dotnet suite `StandaloneTests/stackertests` (89 assertions: the four
  rules + selector, count and height capacity, oversize, blocked / forced / partial release,
  pipelined cycle timing and stay times, blocked output order, pause by a stop, Link statistics
  balance, builder round trip, headless factory wiring, opt-in default).

---

### 3.15 Regression net (`Testing/`, namespace `SimuLean.Testing`)

**`ReferenceModels`** defines small single-purpose models as `SimulationConfig`s
built with the extractor's DTOs and handler type names (42 on 2026-10-09: the original eleven — basic line, 3-slot
server, round-robin routing, shared operator team, combiner, picking, assembly, stacker, transport, MTBF downtime,
warm-up —; lists `list_*` (3); resources `resource_*` (12: shared team, skill query, transport, count two, request
priority, fleet nearest, walk matrix / grid / calibrated, robot cell, gantry stacker, AGV traffic);
`length_limited_lanes`, `shift_downtime`; run control `end_sink_count`, `end_when_empty`, `stall_combiner`,
`deadlock_dead_end`, `finish_with_surplus`, `end_at_storage`, `combiner_starved`; shipyard / naval `yard_*` (4),
`erection_sequence`, `erection_precedence`, `naval_block_process`), each with a fixed seed and horizon. **`ReferenceModelRunner`** runs one through
`HeadlessModelFactory`, collects its statistics (`RunResults`, §3.11 — keyed by element id, or `scope:id` such as
`team:operators`, `resource:op1`, `slot:ms/slot0`, `list:orders`, `downtime:fail`, `traffic` and `model`) and compares
them with a JSON baseline (`MiniJson`; structural changes always fail, numbers within the relative tolerance
`DefaultTolerance` = 1e-6, scaled by max(1, |baseline|); a `state:X` statistic appearing or vanishing is compared as 0;
a missing baseline fails — it is written only with `--update`). The baselines leave out
`ReferenceModelRunner.NotInBaselines` — a queue's `stay_p90` and a sink's `tis_*`: summaries of item times the run
already records, which would only add a key to nearly every baseline; `resultstests` checks them against a tally
`tis = age` and the queue's raw stays. Both are engine-pure, so the
same definitions and baselines serve:

- `StandaloneTests/referencemodels` — the dotnet runner (also checks determinism; `--update`
  rewrites `baselines/*.json`); `StandaloneTests/run-all.ps1` runs it and every unit suite (also
  run automatically by the Claude Code **test gate** hook after every edit that changes the engine
  or test sources, and by the git **pre-commit** hook (`.githooks/`, `core.hooksPath`), which also
  checks the EditMode suite: a full passing run (no skipped test) in the open editor whose working-tree fingerprint
  still matches, else a Unity batch-mode run when the editor is closed; the suites run in
  parallel —
  `.claude/hooks/test-gate.ps1`, see `StandaloneTests/README.md`); the suites (`run-all.ps1`, `-Serial` one by one):
  `referencemodels`, `bugfixtests`, `stackertests`, `sourcestatstests`, `listtests`, `seedtests`, `resourcetests`,
  `propertytests`, `expressiontests`, `optimizationtests`, `probabilitytests`, `componenttests`, `fittingtests`,
  `resultstests`, `navaltests`, `datatests`, `signaltests`, `extractiontests` (the Unity layer's `ExtractionRules`,
  §3.5), `agentinterfacetests` (study checks, unique
  lookups, `ModelDescription`; every statistic a "Statistics …" / "Metric paths …" sentence of `docs/agents/**` cites is one the reference models' runs report — for the element type the paragraph names, `RunResults.Capture` — and the validate info on member breakdowns), `docsymboltests` (every back-ticked code name of `CLAUDE.md`,
  `docs/architecture.md` and `docs/project-overview.md` exists in the code — types / members, snake_case statistic and
  command names, file paths; a name quoted as history in its sentence is skipped, the other exceptions are in
  `allowlist.txt` with their reason), `architecturelint` (conventions of `CLAUDE.md` as text rules over
  the C# sources, comments and strings left out — `ScheduleEvent(` only where allow-listed (element work goes through
  `ScheduleWork`), no global / unseeded random source, no direct `Find*` / `GetEntityId` outside `UnityCompat`, no
  `RuntimeInitializeOnLoadMethod`, no Unity namespace in the engine, every Library element with a registered extractor
  and handler, no date / review / phase / tracker tag in a comment of the engine, the Unity layer, the optimisation or
  the experimentation code, nor a test class / method named after one (`comment-history`: comments say why, not when;
  tests are named after the behaviour they check), and the engine's namespaces depending on each other only in the directions of `namespaces.txt`
  (`namespace-deps`, §2); existing exceptions in `allowlist.txt` with their reason, each rule self-tested); they include
  `seedtests` (per-element streams — same seed identical, an unrelated branch leaves the
  existing one unchanged, order independence, parallel = sequential, pinned stream-seed values);
- `SimuLean.Unity/Editor/Testing/SimuLeanTestRunner` runs the EditMode suite inside an **open**
  editor (MCP `eval`, or *Tools ▸ SimuLean ▸ Checks ▸ Run EditMode Tests (Safe)*). It does not start while a scene
  has unsaved changes, because the Test Runner's save dialog would block the editor and the MCP. It runs
  asynchronously and writes `Temp/simulean_test_results.json` (a full run also `…_full.json`, with the gate's
  working-tree fingerprint `treeFp`); it refreshes the asset database first, so it never tests scripts the unfocused
  editor has not compiled (see `CLAUDE.md`, Test gate). A run that includes `ExternalRunnerTests` (every full run)
  first makes sure the external runner of this engine version exists (`ExternalRunner.EnsureBuilt`, the build a study's
  first use starts; status `running`, `phase` `building runner`) and fails when it cannot be built, so after an engine
  change one full run passes with nothing skipped. Scene tests open sample scenes through `IsolatedScene`
  (a temporary copy when the scene is open), so they never skip; a test that builds a model gets a fresh additive scene
  from `TestScene` (a root object, closed unsaved; the `SceneTestFixture` base gives each test of a fixture one, active,
  with its undo operations reverted), waits for a run on its task (`HeadlessSceneRun.Completion`,
  `SimuLeanCommands.WaitForBackgroundRun`), not in a sleep loop, and tells a model that runs only in the scene by its
  type (`SceneNotHeadlessException`), not its message. Related rule: editor
  tooling must not dirty a scene merely by opening it, so `ElementInspectorPanelSync` does not mark its
  auto-collected list dirty. **Tests leave the open scenes untouched**: a root object a
  test leaves in an open scene fails that test (and is removed); after the run, once Unity's Test Runner has put the
  editor's scenes back (it runs the suite in an untitled scene), a scene that came back with other root objects than
  it had — a default *Main Camera*, not even marked modified — is reloaded from its file, and one that came back
  modified fails the run. The scenes come back in steps (the camera arrived more than a second after the paths
  matched, in a non-empty scene too), so the check waits until their root objects have stayed the same for 3 s (30 s
  at most) before it compares them. Tests that place library entries switch
  `ModelHierarchy.AutoOrganize` off (its organiser groups outlive the element), and an undoable delete in a test is
  reverted with its undo group (`Undo.RevertAllDownToGroup`) so a later Undo cannot restore it into the user's scene;
- `SimuLean.Unity/Editor/Tests/ReferenceModelEditModeTests.cs` — Unity Test Framework EditMode
  tests: the same baselines with the Unity-compiled engine, and the sample scenes `Assembly_Kitting` and
  `ListsDemo` extracted and run headless (`SampleSceneRunsHeadless`; `ChoiceLogicDemo` has its own `ChoiceLogicDemoTests`).
  `ExampleResultTests` runs `Assembly_Kitting`, `Setup_Changeover`, `Workshop_Resources`,
  `Hospital_Emergency` and `Shipyard_Library` headless with a fixed seed (`SceneStudy`): no error, deadlock or stall,
  every station processed items and every sink received some, and the numbers of the scene's card hold (the seeded
  ones, or exact ones where every time is constant); `Factory_3DModels`, a gallery, gets the smoke check only. `SimuLeanTestRunner` also offers
  `ModifiedScenes()` and `OpenNeutral()` (opens `Assets/Scenes/Neutral.unity` before a branch switch, `CLAUDE.md`).

To make that possible the element handlers compile outside Unity: since 2026-10-06 the extraction half
lives in the Unity layer (`IElementExtractor`) and the engine assembly has no Unity reference, so
`Engine.props` compiles all of `SimuLean.Net` with no stubs. Not built yet: a scene generator
(`SimulationConfig` → Unity scene, the inverse of the extractor).

### 3.16 Lists (`Collections/SimList.cs` + one file per list type, `SimLink/ListLink.cs`)

Model lists. **`SimList`** (on `ModelList<T>`) holds
**entries**: items advertised by the element that holds them (the item does **not** move) or any
value (orders, tokens, messages, with push-time fields). **Queries** (`ListQuery`: `Where(field, op,
value)` / custom predicate, `OrderBy(field, desc)`, list default order, then FIFO) read fields
through one access layer: item labels (numeric or text), value fields, built-ins `age`,
`pushTime`, the entry's item and origin as `item.x` / `origin.x` (no `itemPriority`, `owner`, `type`), and named calculated fields (`DefineField`). `TryPeek` / `TryPull` / `Pull` (back-order if nothing
matches, **delivered by a zero-delay event**, cancellable through `ListPull`; a pull cancelled
after its match puts the entry back as it was and is not counted; a query's own order replaces the
list's default order).
Entries without the order field (or with a non-numeric value) rank as **0**, so ordering never fails
on a missing label. Statistics: time-weighted content and pending pulls (a cancelled pull stops
counting), waiting time, pushes / pulls; lists register in
`SimContext.Lists` and the warm-up resets them. `ListQuerySpec` / `ListCondition` are the serializable
form (text `field op value;…|orderBy[ desc]`). `ModelList.Pull` now returns a cancellable `BackOrder`.
**A puller's max wait** (key `pullMaxWait`, `ListLink.SetPullLimit`): from its pull to a match, KeepWaiting only
— counted (`timeouts:list_pull`) and signalled (`WaitTimedOut`); a push never waits (a list has no capacity). §3.17
*Max-wait timers*.

**General list extensions for consumers:** values may implement
**`IListFieldSource`** (fields read live from the value — resolved after the list's aliases / formulas
and built-ins, before push-time fields); `Ordered(query)` returns the matching entries best first
(stable: ties stay FIFO) so a consumer can look past the best one; `Take(entry)` pulls a given entry
(counted, waiting time recorded); `ListQuery.Then(other)` combines two queries (filters of both,
orders appended). A `ListQuerySpec` order may hold **several keys** with their own directions (`|urgent desc,
distance`: the first decides, ties by the next, then FIFO). **Expressions in queries and orders
(1b):** a condition that is not `field op value` (`prio * 2 > 3`, `has(item.code)`) is an
expression, true when not 0 (kept as `expression != 0`; a text that does not parse fails the decode), and an order key
that is not a field name (`ListQuerySpec.IsExpression`: `max(prio, 1) * -1`) an expression; the text is split at `|`,
`;` and `,` only outside parentheses and quotes (`||` is not the bar). In a **list of items** they are calculated fields
named by their text (`SimList.DefineExpressionKeys`, at build: the default order and every pull query; the list's field
context, stream `<list>/field:key:TEXT`); team requests, members and milk-run requests refuse them (`ListFields.Check`:
their fields have no expression scope yet).

**List fields.** `SimList.DefineFields(ListFieldSpec…)`: each field has a name, a kind —
`Label` (an item label, optionally under another name), `BuiltIn` (`age`, `pushTime`, `item.type`,
`item.priority`, `item.id`, `origin.name`, any `item.x` / `origin.x`) or `Expression` (a formula over other fields) — and a `monitor` flag. Fields are used by
name in queries and orders (aliases resolve in `GetNumber` / `GetText`; formulas are calculated fields).
Formulas use **the common expression syntax** (§3.18; bare names = the entry's fields, `item.x` = its item's
properties); a missing field counts as 0; bad formulas, duplicate / empty names,
unknown built-ins and self references fail at definition, cycles at evaluation. Monitored fields record
`ListFieldStats` (values on push and on pull; reset with the list and by the warm-up); the reference
runner exports them as `field:<name>:push_avg` / `:pull_avg` / `:pull_max` (`ResultMetrics.Field`). Config: `ListConfig.Fields`.

**Flow integration — `ListLink : Link`** (one per list): the output link of the elements that
**push** and the input link of those that **pull**.
- Push: the list handles **every distinct item of every origin, whatever the element**:
  `SendItem` advertises an item once and returns false, so the origin keeps it exactly as with a full
  destination (it shows BLOCKED); elements that hold several outgoing items also advertise the ones
  behind the first (`ListLink.Advertise`, idempotent) through **`IAdvertisingLink`** (any output
  link that wants every held item — a list, or a link that lets its puller choose; the holders test the interface, not
  `ListLink`, and items behind it wait for a puller, never BLOCKED by a refusal): `ItemsQueue`, `LengthLimitedQueue`,
  `ReleaseSource`, `Delay`, `Agenda`; `ListLink.ReservedFor(item)` (the puller a matched item is going to — an origin
  whose hand-over takes time uses it) and `Withdraw(item)` (an advertised, unreserved item taken off the list); `MultiServer` / `ScheduleSource` offer each finished / created item anyway. An
  origin offering an already-advertised item again (it retries, e.g. after a stop) re-evaluates the
  pending pulls (`RefreshPulls`). Entries of a stopped origin stay listed but are not matched until
  it resumes; reserved items wait for it too.
- Pull: a puller with free capacity registers one pull (its query + its input strategy with the
  origin); on a match the item is reserved for it and, after the current event, the origin hands over
  **that very item** through **`Element.SendHeldItem(item)`** — overridden by every element that
  holds several outgoing items (`ItemsQueue`, `LengthLimitedQueue`, `MultiServer` (finished slots),
  `ScheduleSource` (backlog: at each arrival it is offered first and new items queue behind it, unless the output is a
  list — 2026-10-26, `bugfixtests` #85), `ReleaseSource`, `StorageSystem` (retrieved items at its outbound point), `ErectionDock`
  (the finished ship)); the default returns false and the link falls back to
  `NotifyUnblock` (single-item elements resend their item). The transfer is `Receive`, then
  `OnExit` / `OnEntry` — statistics stay link-driven. (`IListOrigin` was removed.) After a transfer the puller
  asks again if it still has room. The item leaves the list's bookkeeping (entry, reservation, carrier) **before** it
  enters the puller, so a puller that also pushes to the same list (a welding queue the robot chooses from, beside the
  beds) advertises it again as it receives it (it recursed or was lost, `listtests`, `bugfixtests` #80). Only entries the puller can take **now** match (its
  `CheckAvailability` and input strategy), and `NotifyAvailable` from the puller re-registers its
  pull so entries already listed are re-evaluated; a failed transfer re-advertises the item and the
  puller asks again after the current event. `ListLink.Start` (after the elements' `Start`) registers
  the first pulls; `Reset` forgets advertisements, reservations and pending pulls.
- **Combiner inputs** pull per port: the main input (`GetInputPort(0)`, the combiner) or a
  component input (`GetInputPort(k)`, a `CombinerInput` that accepts only while a main item is being
  assembled — the readiness filter above keeps it from matching too early).
- **Transport on list transfers.** A holder with a transport team (`transportBy`) carries the
  items the list hands to a puller — the holder's transport: `ListLink.SetTransport`
  (built by the factory for a `PushToList` origin with `transportBy.team`); the reserved item is not transferred at once:
  `TransferCarried` requests a carrier (`TransportMoves`, below), the item leaves the holder when loaded (`OnExit`) and
  enters the puller at the delivery (`OnEntry`, `DeliverCarried`); the reservation keeps the puller from pulling
  another item meanwhile, and items being carried are not resent. A carrier waiting at a full puller delivers on its
  `NotifyAvailable`. A Combiner's component input (the port a list delivers to) is where its Combiner is
  (`CombinerInput.Location` → the owner's; `Element.Location` is virtual). Reference models `yard_kitting_list` (abstract crane), `yard_list_axes`
  (an overhead crane on axes, out-of-order blocks; `bugfixtests` #44).
- A pulling element must not also be fed by ordinary links (one input link per element); a pushing
  element's ordinary outputs are replaced by the list.

Unity: **`UnitySimList`** (`[SimuLeanTool]`, category Lists: name, **fields** and a default order chosen from them —
**none by default**: a new list has no fields and pulls first pushed, first out; it had a
`prio` field and ordered by it, which pallets and totes do not carry — a run-time warning; `Warehouse_ToLine`'s list
migrated) built by the model factory
(`SimulationConfig.Lists` → `HeadlessModelFactory.ListLinks`, one build path) and bound to the component by
`SceneModelBuilder` (`UnitySimList.Bind(ListLink)`; `List` / `Link` are null outside a run); its
inspector edits the fields one per line (`ListFieldSpecDrawer`: name · kind · label / built-in / formula
with live validation · monitor), lists the pushers / pullers and, in Play mode, the live entries with
one column per field, the statistics and the monitored fields' values. Pull queries are edited with a
**query builder** (`ListQuerySpecDrawer`: rows *field ▾ / operator ▾ / value*, *order by ▾* + highest
first; the fields offered are those of the list the query reads — sibling `pullFromList` or the
combiner input's `list` — plus "(other…)" for any label). Every
`SElement` has one connection choice per direction: **`sendTo`** = `Elements` (`nextElements`) |
`List` (`pushToList`), and **`receiveFrom`** = `Connections` | `List` (`pullFromList` + `pullQuery`);
`PushList` / `PullList` return the list only when that mode is selected, and only they are used by
the extractor and the list inspector (a stale list reference in the other mode is
ignored). Every element inspector draws them through **`SElementEditorHelper.DrawConnections`**
(only the fields of the chosen option; the Stacker / Storage Rack editors draw it and then the rest
with `DrawPropertiesExcluding(ConnectionFields)`). Sources and the combiner hide *Receive From*.
**Routing and admission are common to every element**: `SElement.outputStrategy`
(routing) and `SElement.inputStrategy` (admission), drawn
by the same helper next to *Send To* / *Receive From* (the shared routing asset `OutputStrategyAsset`,
unused by every scene, was removed with its *Create* menu entry) and exported once by `SceneExtractor` (`HasOutput` false for sinks, `HasInput` false for sources and the
combiner, whose inputs use its pull mode). The fields kept their names, so scenes needed no migration.
`UnityDowntimeEditor` shows only the chosen kind's section (and the database options only when used).
**`UnityCombiner.listInputs`** (`ListInput`: input index 0 = main / k = component k−1, list,
query) feeds inputs from lists; connections fill the remaining inputs in order
(`ResolveInputSlots` over `ExtractionRules.InputSlots`: null = list-fed; the extractor writes it as the combiner's `inputOrder`, from which the factory
builds the engine `InputOrderProvider`), the inspector shows the
feeder of every input, and connections + lists must equal requirements + 1 (`FedInputCount`).
Sources give every item the labels of **`createLabels`** — `name = expression` records like every label a model sets
(entry / exit labels, a member's properties; `LabelAssignments`, streams of purpose `createLabel`; text
constants quoted, `sku = '007'`; default `prio = 0`, so list ordering works out of the box; engine
`InterArrivalSource` / `ScheduleSource.CreateLabels`, set at creation after the item's id and type and before the
table columns, which override them of the same name). **Item size from the model:** after the labels and the
columns, `Item.SetSizeWhereMissing(ModelSize)` sets `width` / `height` / `length` on the sides still unlabelled — the
size of the source's `itemPrefab` (`UnitySource.sizeFromModel`, default on; measured by the extractor from the meshes,
keys `modelWidth` / `modelHeight` / `modelLength`, `modelWidth` …), so the storage's physical
dimensions, a stacker's height limit and a combiner's load height work in a scene with no labels written, the same
inside and outside Unity. The same holds for the unit loads other elements create: the
`BaseStockReplenisher`'s and the `PickFace`'s loads get `LoadModelSize` (their `loadPrefab`), and the pick face gives
`PieceModelSize` (its `piecePrefab`, turned by `pieceRotation`) to the pieces of **every** load it opens, received ones
included — how it draws them; `sizeFromModel` on both wrappers (default on), keys `modelWidth` … and
`pieceModelWidth` … (`ReplenishmentBuilders.EncodeModelSize` / `DecodeModelSize`, shared with the source's extractor;
the prefab is measured by `ItemVisuals.ModelSize`). A label always wins. Labels drawn per item (a case mix) are the source's **exit labels**
(`dx = pick(0.6, 'diagnosis', 0.4, 'control')`, §3.4 *Labels set on hand-over*).
Headless: `SimulationConfig.Lists` (`ListConfig`) and `PushToList` (element → list id) /
`PullFromList` (list id → element, parameter `query`, and `port` for a combiner input) connections;
the combiner's `inputOrder` holds `""` for list-fed inputs; sources export `createLabels` as
`name = expression` records. All exported by the extractor and wired by `HeadlessModelFactory`; the
reference runner records `list:<id>` statistics. Tests: `StandaloneTests/listtests`, reference models
`list_priority_dispatch` / `list_filtered_pull` / `list_combiner`, EditMode `ListSceneTests` (incl.
combiner list input). Demo scene: `Assets/Scenes/ListsDemo.unity` (two queues → list `Work` → two
servers by priority; combiner whose component input pulls from list `Parts`), also run headless by
`ReferenceModelEditModeTests.SampleSceneRunsHeadless`.

### 3.17 Resources (`Resources/`)

Everything that does work for tasks — operators now, vehicles / AGVs next, later gantry cranes, AS/RS,
robots — shares one model (namespace `SimuLean.Resources`):

- **`Resource : Element`** — identity, **properties** (numeric / text: skills, attributes; an item has labels), `Mode` (**Abstract** = capacity only, no position, travel takes no time; **Spatial** = on
  the network, `HomeNode` / `Location`; **`ReturnHome`** (team `ResourceTeamConfig.ReturnHome`, default
  off): a `TaskExecutor` walks back to its home after each task — an internal travel task, busy time, not counted in
  `TasksDone` or the usage — and becomes available only there; otherwise a free member waits where its last task
  ended, `bugfixtests` #86), `Capacity` (items carried), `CurrentTask`, `IsAvailable` (no task,
  not stopped, not finishing before a stop), `IdleSince`, `BecameAvailable` event; **properties** via `SetProperty` /
  `GetProperty` / `GetPropertyText` / `PropertyNames` (stored in the element's `Properties`); `DistanceTo(location)` (0 when
  abstract). It is an element for its
  identity, states, statistics and **downtime** (`Element.Stop`: `Immediate` pauses the task's timed
  steps, `AfterCurrent` = finish the task, then the break / shift), but **not part of the item flow** (no
  links; `Receive` refuses). Statistics come from its state tracker and counters, not from links (the
  two-source statistics rule): `Utilization` = time in `ResourceStates.Busy` (PROCESSING, SETUP,
  TRAVELING_EMPTY / LOADED, LOADING, UNLOADING, HELD, WAITING, TRAFFIC_WAIT), `TasksDone`, `UsageByRequester` (time per
  requesting element); warm-up resets them. **`WAITING_DEPARTURE`** is assigned but
  **not busy**: a vehicle reserved by its task with nothing to do yet (a milk-run train waiting for departure) — it
  keeps its task, so it is not available, but the wait is not utilisation.
- **`TaskExecutor : Resource`** runs one **`TaskSequence`** at a time (`Execute`; throws when the executor is not
  available or the task already ran): steps `Process` (PROCESSING), `Setup` (SETUP), `Delay` (WAITING), `Travel` (instant when
  abstract; `TravelTime(from, to)` when spatial — TRAVELING_EMPTY / LOADED by what it carries — a spatial
  executor without a travel model fails loudly), `Load` / `Unload` (LOADING / UNLOADING, `LoadTime` /
  `UnloadTime` defaults, item callbacks, `Carried`), `Callback` (instant) and **`Hold`** (HELD until
  `Continue()` — e.g. an operator kept by a station until its item leaves) / **`HoldUntil(done, state)`** (the same,
  skipped when `done` is already true at the step's start), and **`Expand(addSteps)`** (when
  reached, the steps `addSteps` adds are inserted right after it — "load the next ready carrier, then look again"
  decided at that moment; a milk run's tour). Durations are functions
  evaluated when their step starts; timed steps use `ScheduleWork`. `TaskSequence` carries `Type`
  (`TaskType`: Process / Setup / Transport / Other — it decides when its resources are released),
  `Requester`, `Priority`, start / end times and `Completed`; `TaskStarted` / `TaskFinished` on the executor. Also
  `CurrentStep`, `StepStartTime` / `StepEndTime`, and `RecordMoves` / `Moves` (`MoveRecord`, calibration input; on for
  `ExternalTravel` teams). An AfterCurrent stop waits for the current task (`HasWorkInProgress`).
  `TaskSequence.Priority` (default 1), `Preemptable` (default true), `WasInterrupted` and `Aborted` serve preemption (below).
- **`ResourceDispatcher`** (the "ordenador") hands its members out through two ordinary `SimList`s:
  free resources (`Resources`, values = the executors, which give their fields live: `idleTime`,
  `utilization`, `tasks`, `capacity`, `distance`, `name`, `location`, every property) and pending
  **`ResourceRequest`**s (`Requests`; fields `priority`, `requestTime`, `count`, `origin.name` / `origin.x` (the requester), `location`, `taskType`,
  `skill` (the first requirement's label), `oldestItemTime` (the time of the oldest item the request works for,
  `ResourceRequest.OldestItemTime`, given by `StationResources.Acquire(…, oldestItemTime)`; NaN = the request time),
  plus the list's `age` and user fields). The priority rule is two list orders —
  `RequestOrder` (which request first) and `ResourceOrder` (which free resource) — **null = FIFO if
  available** (requests by arrival, resources longest idle first). **Named request orders** (1d,
  2026-11-07): a team's `namedOrders[i]` (`NamedOrderSettings`: `name`, `query` — the record a milk run's named orders
  use too) and `requestOrder` a `ChoiceFrom` over them: a name picks one (`ResourceBuild.NamedOrder`; a plain word that
  names none fails the build naming them), a query text written in place still works; the catalog offers
  `team:T.requestOrder` as a choice of the names, so a study compares dispatch rules. A request lists
  **`ResourceRequirement`**s (count + filter query, e.g. `skill == painting`; several = "operator and
  tool"), all from that dispatcher, served **all-or-nothing**; requests that cannot be served yet do not
  block the ones behind (big requests can wait longer — no reservation yet). On assignment the request's
  `OnAssigned` gives each resource its task; a resource left without one goes back to the list. **A stopped
  station's process request waits**: while its requester `IsDown` (off shift, a failure, an
  AfterCurrent stop in effect) a `Process` request is skipped, so the job that cannot start does not keep a shared
  resource from the others; `StationResources` retries the dispatcher on the station's `Resumed`
  (`ResourceDispatcher.Retry`). Work already running when a stop begins keeps its resources unless its use has a
  preemption mode (below); transport requests are not held back, nor a repair crew's (`ResourceRequest.ServeWhileDown`,
  §3.12 *Repair crews*). `bugfixtests` #45.
  Dispatching runs on every request and every `BecameAvailable`, re-entrant-safe (zero-time tasks loop,
  not recurse). The lists keep their statistics (request wait = waiting for resources). API: `Request(request)`,
  `Cancel(request)`, `Retry()`, `Members`, `Served`; a `ResourceRequest` has `State` (`RequestState`: Pending /
  Assigned / Cancelled), `RequestTime`, `AssignTime`, `Assigned`, `Location`.
- **Stations (`IResourceUser` = `StationResources Resources`, `StationResources`).** A station's `Resources` =
  dispatcher + requirements + **`Release`** (`ProcessRelease`: `EndOfProcess`, default; `ItemLeaves` = kept, HELD,
  while the station is BLOCKED; **`AtElement`** = the resource goes with the item, HELD, until
  it enters `ReleaseAt` (config `processBy.releaseAt`, an element name or id; Unity `ProcessResourceUse.releaseAt`) — an
  equipment pool: a team of devices without travel lent to patients to take home and returned at the device
  desk; the station is free as soon as the item leaves it; an item that never gets there keeps them) + request `Priority` (`Func<Item,double>`, an expression over the item and the station,
  `ResourceBuild.PriorityOf`). On a job the station calls `Acquire(station, item, onReady)` → `ResourceJob` (slot
  WAITING_FOR_RESOURCE until assigned; then `onReady` starts processing — once, after every resource has its task),
  `ProcessEnded(job)`, `ItemLeft(job)`, `Cancel(job)` (a job still pending). **The station keeps its
  process time** (its own stops pause it; KISS); its resources run a task of [`Travel` to the station / work point,
  spatial only +] `HoldUntil(processEnded, PROCESSING)` [+ `HoldUntil(released, HELD)` with `ItemLeaves`] and are
  continued at the release points — or, with **`HoldFor`** (a sampler, catalog key `processBy.holdFor`), a
  `Process` step of their own time, after which they are free while the item stays (painting then curing, a crane's
  lift then the joint welding); `Next` continues only an executor still on that job's task (a released one may hold
  another job) — so their statistics show the processing and held time per station. An operator
  breakdown does not interrupt a running process unless the use has a preemption mode (**Preemption**, below). Every
  station uses it:
  `MultiServer` and `Combiner` (per job),
  `PickingStation` (per pick cycle), `AssemblyStation` (one request per task; with `ItemLeaves` only the
  last task's resources wait for the job to leave), `Stacker` (per cycle, released at the cycle's end), `ErectionDock`
  (per erection front), `SequencingStation` (per carrier or per piece); `PickingStation` gets it through its base
  `SingleJobStation`. Work points are taken
  per job (with `HoldFor` a resource may already work there for a later job).
  **Setup crews (§3.2 *Setups*):** a `StationResources` has a `Task` (`TaskType.Process` by default;
  `Setup` for a station's setup crew: requests and tasks of type Setup, members holding in SETUP, a stopped station's
  Setup request waiting as a Process one); `AcquireWithSetup` / `SetupEnded` let a process use set the station up first
  and stay for the process (`ResourceJob.WithSetup`: a `HoldUntil(setupEnded, SETUP)` before the process hold). Resource
  views need nothing new (SETUP is a busy state; the members stand at the station as for a process).
  With `ItemLeaves` and a FIFO dispatcher each reproduced the former `OperatorPool` exactly (verified
  before the pool was removed; `operator_pool` and `resource_shared_team` kept their baseline values
  through the migration).
- **Preemption (`Resources/Preemption.cs`).** A request's **`Priority`** defaults to **1** (larger = more
  important; `StationResources.Priority` null = 1, Unity `ResourceUse.priority` "1") and its **`Preempt`**
  (`PreemptMode`) to `None` — wait for a free member (no preemption). With `Resume` / `Restart` / `Abort`, a
  request that finds no free member meeting a requirement takes one from a task of **strictly lower** priority
  (`ResourceDispatcher.TryPreempt`, run only then and only over that team's members; requirement filters read a busy
  member through a probe entry): free members first, then the victim whose task has the lowest priority, ties the one that
  started it last; **all or nothing** — nobody is taken unless the whole request can then be served, and it is served
  directly (the freed members are never listed, so an older request cannot take them). Equal priorities never preempt, so
  models that set none behave exactly as before (every reference baseline unchanged). Which tasks can be taken
  (`TaskExecutor.CanBePreemptedFor`): a `Preemptable` task of an executor not stopped, at a step that can stop — holding at
  a station, a timed process / setup / delay, or an **empty** move that is not open-ended (a loaded carrier always
  delivers; loading / unloading are never cut) — and its owner (`ITaskInterruption`) agrees:
  - **a station's job** (`StationResources`, `Preemptable` / config `processBy.preemptable`): while its resource walks to the
    station, or while it works there during the station's process — when the station can pause that job's work alone
    (`IInterruptibleStation`, every station that uses resources since 2026-11-04: `MultiServer`, `Combiner`,
    `SingleJobStation` / `PickingStation`, `AssemblyStation`, `ErectionDock`, `Stacker`, `SequencingStation`; they give the
    job its timed work through `StationResources.ProcessStarted` → `ResourceJob.Work`). The job's work is held
    (`WorkHandle.Hold`), the station shows WAITING_FOR_RESOURCE for it (`JobPaused`), its work point is freed, and it asks
    again for the resource of that slot (the requirement it met, count 1) with its own priority and mode; the member that
    takes over travels to the station and the work goes on — `Resume`: the remaining time, `Restart`: the whole time
    (`WorkHandle.Release(restart)`; the slot's `loadTime` shifted or reset for the views). A member lost while walking to
    the station (process not started) is simply replaced. Per station (*preemption everywhere*):
    - `AssemblyStation`: **one request per task** — taking a task's operator pauses only that task's work
      (`ProcessStarted(job, work)` per task); the task asks again with the station's priority and mode (the use is the
      station's: `processBy.preemption` / `processBy.preemptable` / `processBy.priority`, the Process block; no per-task fields — an
      assembly task's `Priority` is its tie-break among ready tasks, not a resource priority); the other tasks go on, and
      the station shows WAITING_FOR_RESOURCE only while none of its tasks works.
    - `ErectionDock`: per front, as the Multi-Server's slots. `Stacker`: the pick-and-place cycle (cycle by time; the
      crane mode is a carrier's move, below). `SequencingStation`: the operator's job (the carrier's, or the piece's) gives
      each timed step it works (prepare, pick, place, gap, close) — the current step is held; `Restart` starts that step
      again, not the whole carrier; between steps (waiting for an order or a piece) the operator cannot be taken.
    - **Its own time** (`HoldFor`): the member's own `Process` step can be taken (by priority, or by its Immediate stop
      when the use has a mode); the one taking over travels there and works what was left (`Resume`, from
      `TaskExecutor.InterruptedRemaining`) or the whole time again (`Restart`); the station's process goes on meanwhile
      (the item stays for it). `WaitForSame`: the step is paused by the member's stop and goes on when it resumes
      (`Restart`: starts again, `TaskExecutor.RestartStep`). The time without a member counts as the job's interruption.
    - **HELD after the process** (`ItemLeaves`): the member keeping the job while its item waits to leave can be taken
      (or handed over at its stop); the job asks for another member **to replace it** — the item must not stay without
      its holder; there is no time to resume. If the item leaves before the replacement arrives, the request is withdrawn
      (or the replacement is free at once). `AtElement` (the resource travels with the item) is never taken.
  - **a carrier's move** (`TransportMoves`, `transportBy.preemptable`; also the storage moves and a stacker's crane cycle,
    `EmptyLegMove`): a move of one carrier on its way, empty, to the pick-up (a timed or an open-ended move); the move asks
    for a carrier again (the stacker's cycle is undone: the item waits at the pick point).
  - **a joint move of several carriers** (a transport request for more than one resource: `CarrierGroup`, used by
    `TransportMoves` — the transport and list links —, the storage moves and a stacker's crane): every carrier travels,
    empty, to the pick-up and waits there, HELD, until the whole group is there and none is stopped; then the lead loads
    and carries, the others accompany it (TRAVELING_LOADED) and end at the destination with it. **While gathering** a
    carrier can be taken (by priority, or handed over at its Immediate stop when the use has a mode): only that one is
    released, the others wait at the pick-up, and the move asks for a replacement of that carrier (the requirement it met,
    the move's priority and mode); the move starts once the group is complete again. **Loaded together**: never taken by
    priority; an Immediate stop of one of them **pauses the whole group** (the lead's timed work held,
    `TaskExecutor.HoldForGroup` → `Element.HoldAllWork` — not a stop: no state, no count —, its leg frozen,
    `TaskExecutor.IsGroupHeld` for the physics view) until it resumes: no hand-over with the load on. Before 2026-11-04 the
    helpers only held (TRAVELING_LOADED) from the assignment, did not move and a helper's stop changed nothing; no
    reference model had a joint move, no baseline moved. A replacement that never comes leaves its request pending — the
    dispatcher lists it, so the stall / deadlock detection (§3.5, `ModelDiagnosis`, *Stop When No Progress For*) reports
    it instead of a silent hang. A milk-run train with a crew pauses the same way when a crew member stops
    (`CarrierGroup.Together`).
  **A loaded carrier is never interrupted by priority** (it delivers first; loading and unloading are never cut); its
  own Immediate stop pauses the leg as before.
  - **a task without an owner** (built by user code): only `Abort`, only without an item and not a transport task; it is
    cancelled (`TaskSequence.Aborted`). `Abort` never takes a station's job or a carrier's move.
  An interrupted empty move goes on to the **next node of its route** (the network has no positions mid-track;
  `TaskExecutor.DrainToNextNode`: the leg truncated there, `TravelStarted` raised again; a route with no node in between
  — straight, matrix, abstract travel time — ends where it was going); the next task starts there. An **open-ended**
  empty move drains too: under traffic (`IDrivenTravel.EndAtNextNode`, `TrafficMover`) it ends at the end of
  the segment it is on, or at once at the node where it waits for one (its wait withdrawn); a move driven from outside
  (Unity physics) ends where it was going, at its `Arrive`. The interrupted task
  never completes (`WasInterrupted`; not in `TasksDone`, its time so far in the usage), `TaskInterrupted` is raised.
  **A member's Immediate stop** (breakdown, shift) interrupts a station's job the same way — only when the job's use has a
  preemption mode (else the station keeps its process time while the member is stopped): the team's
  **`ResourceStopPolicy`** (`TaskExecutor.OnStop`, `ResourceTeamConfig.JobOnMemberStop`, Unity `UnityResourceTeam.jobOnMemberStop`):
  `HandOver` (default: the job asks again, any member meeting the requirement takes it) or `WaitForSame` (the job's
  process paused until that member resumes); `Restart` jobs start again, others resume. Statistics (counters, the
  two-source rule): `TaskExecutor.Preemptions` / `Interruptions`, `ResourceDispatcher.PreemptiveServes`,
  `StationResources.Interruptions` / `InterruptedTime`, `ResourceJob.Interruptions` / `InterruptedTime` (§3.11 names).
  The station's `InterruptedTime` is the pauses ended plus the ones still open up to the current time
  (`InterruptedTimeAt(t)`): it keeps the jobs paused now (a short list, added at `Pause`, removed at `Back`) and sums
  them when read — no event per pause, the same at a live capture in Play, at the horizon or at the end. Warm-up
  (`ResourceDispatcher.ResetStatistics`): the time ended is forgotten, a pause open then counts from the warm-up and as
  one interruption of the new period (so `interrupted_time_avg` stays the time per interruption counted). A job's own
  `ResourceJob.InterruptedTime` counts its pauses ended, whole.
  Config: `processBy.preemption` / `transportBy.preemption` (catalog choices), `processBy.preemptable` / `transportBy.preemptable` (bools);
  team `jobOnMemberStop` (choice). Unity: *Preemption* and *Preemptable* in an element's Process / Transport block, *On Stop* on the
  team. Reference models `resource_preemption`, `resource_preemption_everywhere` (an assembly station, a
  painter's own time and an urgent station sharing two operators); `resourcetests` (Preemption: …), `preemptiontests`
  (every other element and mechanism), EditMode `ResourceSceneTests.PreemptionFieldsExtractAndBuild` /
  `PreemptionFieldsOfAssemblyAndDockExtractAndBuild`. Views: none new (an interrupted operator walks to its next task).
- **A use's team is its switch and its default team.** Every use (`processBy`,
  `setup.setupBy`, `transportBy`, `repairBy`, an assembly task's) is active when its `team` is set **or** a requirement
  names a team (`ResourceBuild.IsActive`; Unity `ResourceUse.IsUsed`, `EffectiveTeam` = the team or the first requirement's).
  A requirement without a team takes the use's; with the use's team empty such a requirement fails the build
  (`ResourceBuild.Use`: "`processBy.requirements[1]` names no team and `processBy.team` is empty: set …"); the first
  requirement's team stands for the use (`StationResources.Dispatcher`, as an assembly task without a team already did).
  It was silently ignored — no resources at all. Requirements with no team anywhere are no use; the extraction warns
  (`SceneExtractor.WarnIdleRequirements`). Test `primitivetests`, EditMode `AgentModelCommandsTests`.
- **Resources from several teams, in order.** A requirement may name **its own
  team** (`ResourceRequirement.Team`, null = the use's; record `count#query#label#team`, the team's id — Unity
  `ResourceRequirementSpec.team`) and a use says how its requirements are taken (`StationResources.Order`, `AcquireOrder`;
  keys `processBy.acquire`, `setup.setupBy.acquire`, `repairBy.acquire`, catalog choices; Unity `ResourceUse.acquire`):
  **`AllAtOnce`** (default — granted only when every requirement can be met at the same moment, across teams; nothing is
  held meanwhile) or **`InOrder`** (one by one in list order; the members granted wait, **HELD** at the station, while the
  next is waited for — a crane, then a driver of another pool). A use with one team and `AllAtOnce` keeps the single
  request of before (every baseline unchanged). Otherwise the job is built from **parts**
  (`StationResources.UsesParts`): `InOrder` = one request per requirement, the next asked when the one before is granted;
  `AllAtOnce` over several teams = one part per team, each pending in its own dispatcher's list and tied by a
  `JointRequest` — a dispatcher that could serve its part plans every part (`ResourceDispatcher.Plan`: free members, or
  victims when the parts have a preemption mode) and serves all (`Commit`, the other dispatchers suspended meanwhile) only
  when every one can be served now. Each granted member travels to the station (spatial) and holds there
  (`HoldUntil(all parts there, HELD)`); when the last part is there the job starts as a one-request job does. The parts
  carry the use's priority and preemption mode (each part preempts within its team); a member **held waiting** for the
  other parts can be taken by work of higher priority (the job asks again for that requirement, from its team) but is
  not interrupted by its own stop. Release modes, `HoldFor`, setups (`KeepForProcess`) and the statistics are those of a
  one-request job: each team's request waits are its own list's, utilisation counts the time HELD. **Hold-and-wait:**
  two uses that take the same teams in opposite orders can deadlock (each holds what the other waits for); the pending
  parts stay in the dispatchers' lists, so the Idle deadlock check / *Stop When No Progress For* (§3.5) end the run and
  `simulean_diagnose` names the waiting stations and requests — order the requirements the same way everywhere, or use
  `AllAtOnce`. Not for transport uses (`transportBy`: a move of several carriers gathers one team; another team fails the
  build) nor the milk run. The **Assembly Station's tasks** may have requirements of their own (`AssemblyTaskSpec.Resources`;
  each task's `tasks[i].processBy.requirements` (typed records), Unity `AssemblyTaskConfig.requirements`,
  `ResourceBuild.AssemblyTaskUses`): such a task asks them instead of the station's requirements, with the station's
  Process block for the rest (team, priority, preemption, release, order). Without a station process team a
  task names its own team and its use takes the Process block's priority, preemption, preemptable, acquisition order and
  max wait when given; **per-task settings:** a task's `tasks[i].processBy` sets its own team, priority,
  preemption, preemptable, acquisition order, max wait and action over the station's (Unity: the task's *Resource Settings*,
  `TaskResourceSettings`, off = the station's; KeepWaiting / GiveUp only); a task with only settings of its own uses the
  station's team and requirements; in Unity the Process block then shows only these settings
  (`UnityAssemblyStation.TasksUseOwnTeams`) and the extractor writes them with no team key
  (`ProcessResourceUse.ExportTaskSettings`), so the catalog offers them. Tests `primitivetests`, EditMode
  `ResourcePrimitivesSceneTests`.
- **Room reserved downstream before a job.** A station option
  **`ReserveExit`** (`Element.ReserveExit`, key `reserveExit`, Unity `reserveExit` on the Multi-Server, Combiner and
  Picking Station; default off — every baseline unchanged): a job starts (before its setup, its resources and its process)
  only when a place at its next destination can be **reserved** — promised in the destination's ledger
  (*Room promised on the way*, below) and marked reserved (`Element.ReserveRoom`, `RoomReserved`); until then the job
  waits, its item inside, the slot **BLOCKED** and no resource asked, and it tries again whenever the downstream notifies
  (`Unblock`: a place freed, or a promise released by another owner). The place is held until the item enters it, so
  the item never waits blocked in the station after its process (a "bed keeps its node until a downstream bed is free").
  **The destination:** the output link's destinations (`GeneralLink.Outputs`, `ResourceTransportLink.Destinations`,
  `WalkLink.Destinations`); a **routing by rule** (`IRuleRouting`: expression, probability, code) is evaluated at the
  start and its choice kept — the job waits for room *there*, never redirected (its random draws are kept per item,
  §3.18); any other strategy chooses among the destinations with room beyond the promised ones. The item is then sent to
  the reserved destination (`Element.ReservedExitOf`): a general link skips the routing and releases the reservation when
  the item enters; a carrier or walk link takes the **same promise over** (a promise is per item: counted once, never
  twice) and releases it at the delivery. A reserved place is honoured by **every** link, a general one too:
  `Element.CanAccept` refuses other items while the free room is reserved (`RoomReservedForOthers`, only when something is
  reserved). Several slots reserve one place each. Not with a list or milk-run output (no destination to reserve: the job
  starts as before); other element types fail the build with the key. Tests `primitivetests`, EditMode
  `ResourcePrimitivesSceneTests`.
- **Process resources secured before the part.** A Process-block option **`AcquireBeforeEntry`**
  (`StationResources.AcquireBeforeEntry`, key `processBy.acquireBeforeEntry`, Unity `ProcessResourceUse.acquireBeforeEntry`
  — *Before The Part*; default off), **on the Multi-Server only** (any other type, `onTimeout = Reroute` — the wait has no
  item — and a station that pulls from a list — a list offers only what the puller takes now — fail the build with the key;
  the inspector offers it only on the Multi-Server). Two steps:
  1. **The request.** Admission checks have **no side effects**: `Element.CanAccept`, `CheckAvailability` and
     `GeneralLink.CouldTake` only read `StationResources.ReadyForEntry` (a grant is there). A link that could not hand an
     item over (`GeneralLink.TrySend` with no destination chosen, a carrier / walk link with no destination or a refused
     delivery, a milk-run delivery) tells the destinations (`Element.OfferForEntry`: the one a routing rule names, else
     the first in order) through **`Element.PrepareForEntry(item, origin)`**. The Multi-Server checks its admission
     **first** — not input-blocked, its input strategy, no place promised to others, a free slot, with `ReserveExit` an exit place
     it could reserve (`CouldReserveExit`) — and only then asks for one job's resources (`StationResources.RequestAhead`,
     priority and max wait from that item): one request at a time per station, at most one grant per free slot; when
     granted its input is told (`Element.NotifyInputLater`). An item routed elsewhere asks nothing.
  2. **The entry.** An item enters only once the resources are there (walked to the station when spatial; the station is
     never WAITING_FOR_RESOURCE with a part). **The grant is for the next item that enters** (`TakeForEntry(item)`), the
     offered one or another: taking it **ends the wait and its max wait** (`ResourceJob.ItemIn`), so a GiveUp can never
     leave a station processing without its operator. The members wait **HELD** at the station
     (`ResourceJob.BeforeEntry`), not preemptable and not interrupted by their stop, until the work starts
     (`StationResources.Entered`, `ResourceJob.WorkStarted`): then they set up (with `setup.keepForProcess`, no new
     request) or wait **HELD** while a setup crew of its own (or the machine alone) sets up, until the process starts
     (`ResourceJob.ProcessBegun`, set by `ProcessStarted`) — never PROCESSING during another crew's setup. The setup crew's
     work is its own job's (`StationResources.SetupStarted(job, slot, work)`), so it can be preempted as without `AcquireBeforeEntry`.
     An item that never comes keeps the grant — give the station a steady feed, or a max wait (GiveUp drops
     the grant; the next offer asks again).
  **Downtime:** a stopped station's request waits (as every process request). Tests `primitivetests` (refusals),
  `maxwaittests`, EditMode `ResourcePrimitivesSceneTests`; reference models **`resource_primitives`**
  (several teams in order, `ReserveExit`, `AcquireBeforeEntry`: a cutting station that secures its operator and reserves the buffer after it, a lift that takes a
  crane and then a driver of another team) and **`max_wait`**.
- **Max-wait timers (`SimElements/WaitLimit.cs`, `ElementWaitLimits.cs`).** Any wait of the places below may
  carry a **`WaitLimit`**: the max wait — a number, or an expression over the item and the element evaluated **when the
  wait starts** (its random functions draw from the element's stream named after the key, `Element.RandomStreamFor`), ≤ 0
  or empty = no limit — and a **`TimeoutAction`**: `KeepWaiting` (the default: only record it), `GiveUp` (cancel the wait
  and release everything held for it), `Reroute` (items only: the item leaves by the element's timeout target). The timer
  is a `WaitTimer`, an ordinary `SimClock` event (not the element's work: a stop does not pause the time waited;
  architecture-lint allow-listed), cancelled when the wait ends normally, on a reset (each owner's `Start` / reset) and
  when the job is withdrawn; a preemption of a member held for a waiting job neither restarts nor doubles it (one wait,
  one timer). **Every timeout**, whatever the action, is counted on the element (`Element.RecordTimeout`, `Timeouts`,
  `TimeoutsOf(WaitKind)`, reset at `Start` and the warm-up) and raises `Element.WaitTimedOut` — the signal
  `SignalKind.WaitTimedOut` (§3.21). `WaitKind`: `Resource`, `Queue`, `ListPull` — spelt `resource`, `queue`, `list_pull`
  in the metrics and in `signal.kind` (`WaitLimit.KindName`). Defaults off: nothing is scheduled
  without a limit, every baseline unchanged.
  - **Resource requests** (`StationResources.MaxWait`; keys `processBy.maxWait` / `processBy.onTimeout`,
    `setup.setupBy.maxWait` / `.onTimeout`, `repairBy.maxWait` / `.onTimeout`; an Assembly Station's task uses share the
    station's): the wait runs from the request until **every requirement is granted** (walking excluded), and with
    `acquireBeforeEntry` until an item is taken in for the grant (`TakeForEntry`). **GiveUp** withdraws the job's assignment (`Assignment.Withdraw`): its
    pending requests / parts and the take-overs asked are cancelled, the members already granted (held in order, or
    secured before the part) go free — at once when they hold at the station, on arrival if they are walking (the
    withdrawn assignment's steps pass) — and their work points are freed. Then **a job with its item** (or its task / cycle /
    repair) **asks again** at once: a new request on the same `ResourceJob` (`ResourceJob.Request` replaced), behind the
    requests made meanwhile, with a new timer — so a crane held while a driver is waited for goes to the station that
    needed it, and an in-order hold-and-wait breaks. **A job secured before its part** (`AcquireBeforeEntry`) is dropped (`DropAhead`) and
    the station's input told: the station asks again at the next offer — the operator HELD for an item that does not come
    is released. **Reroute** (process use of a **Multi-Server** only, `ITimeoutRerouting`; key
    `processBy.timeoutTarget`, an element name or id; any other station or a crew's wait fails the build): the members go
    free as for GiveUp and the item leaves unprocessed by the timeout target; while the target cannot take it the slot
    waits BLOCKED (`MultiServer.RetryReroutes` in `Unblock`); a place reserved downstream (`ReserveExit`) is given back
    (`Element.DropExitReservation`). A job secured before its part has no item yet: Reroute is refused at the build. **Not for transport uses** (a
    carrier request belongs to a link's move, with its promised room and joint moves; an item waiting for its carrier in a
    queue is covered by the queue's own max wait, KeepWaiting) nor the milk run (its `readyMaxWait` / `loadTimeout` are
    its own).
  - **Queue items — reneging** (`ItemsQueue.MaxWait`; keys `maxWait`, `onTimeout`, `timeoutTarget` of the Queue): an item's
    wait runs from its entry until it leaves (a pass-through has none). **Reroute** — and **GiveUp**, which for an item is
    the same: it holds nothing, and it is never destroyed (a Sink as the target counts the lost items, statistics through
    the link) — sends it to the **timeout target** through a link of its own (`Element.SetTimeoutTarget`: a `GeneralLink`
    with its own `Routing`, not among the Next Elements, so the normal routing never sends there; the target's input
    fan-in notifies it) — link statistics and downtime blocking as any send (a stopped queue or target holds it until
    the resume). **A full target**: the item keeps its place in the line and leaves by whichever comes first, its normal
    turn or the target's room (`Reneging`: those waiting). Needs plain connections as the output: behind a list or
    carriers only KeepWaiting (the build refuses Reroute / GiveUp). Not on the `LengthLimitedQueue` (a lane releases its
    head only: an item cannot leave from the middle) nor the Agenda's patients (their wait is the booking; drop-outs are
    the agenda's own cancellation rule).
  - **List pulls** (`ListLink.SetPullLimit`; key `pullMaxWait` of the puller): from its pull until an entry matches (a
    pull re-registered does not restart it; a puller that stops pulling ends it). KeepWaiting only — a puller holds
    nothing and has no item to send elsewhere; use the signal for anything else. **List pushes do not wait** (a list has
    no capacity), so they have no limit.
  - **Statistics** (§3.11): `timeouts` and `timeouts:<kind>` (`resource`, `queue`, `list_pull`) of each element with a limit
    (`Element.DeclareWaitLimit`, set at the build), only then — the other results keep their shape. **Build**:
    `ResourceBuild.ApplyMaxWait` (uses), `HeadlessModelFactory.WaitLimits` (pass 2b, after the links: queue, puller, the
    Multi-Server's target). **Catalog** (§3.19): the max waits are `ParameterSchema.Expression` (a number while constant),
    the actions choices (Reroute only in the Multi-Server's schema — a crew and any other station offer KeepWaiting /
    GiveUp; the inspector flags a Reroute or a *Before The Part* where the build refuses it), the targets
    structural. **Unity** (key = field path): `WaitingResourceUse.maxWait` / `onTimeout` / `acquire` (the Process,
    Setup and Repair uses; not the Transport use, which no longer carries fields it ignores) in
    the Process, Setup and Repair blocks (*Max Wait*, *On Timeout* — shown once a max wait is set; not for Transport),
    `ProcessResourceUse.timeoutTarget` (*Timeout Target*, a process Reroute), the Queue's `maxWait` / `onTimeout` /
    `timeoutTarget` (`UnityQueueExtractor` writes the target's id in its reference pass) and `SElement.pullMaxWait` (the
    list block's *Max Wait*, exported only when the element pulls from a list); the use's and the Queue's max wait and
    action are exported also at their defaults (`0` = none, `KeepWaiting`). The
    reaction inspector offers the signal (*a wait reaches its max wait*). Guide `Assets/Docs/Resource_Primitives.md` §4.
    Tests `maxwaittests`, EditMode `MaxWaitSceneTests`; reference model **`max_wait`**
    (queue reneging to a Sink, a cutting station that secures its operator before the part and gives it up after 4 for the
    packing line that shares it, packing recording its waits).
- **Transport by resources (abstract) — `ResourceTransportLink`.** The output link of an element whose items
  are carried by resources (operators now, vehicles in B-II) to its next elements. A move takes **load +
  travel + unload**, defined separately: `LoadTime` / `UnloadTime` per item (null = the resource's own
  `TaskExecutor.LoadTime` / `UnloadTime`, so each kind of resource can load at its own pace) and
  `TravelTime` (item, destination; null = 0 for abstract carriers, the carrier's travel model for spatial ones; set,
  it overrides the model's time and the carrier still ends at the destination); spatial carriers first travel empty to
  the origin's `Location` (origin and destination must have one). The moves themselves are `TransportMoves` (below),
  shared with the list link. An offered item gets a destination with room —
  the origin's output strategy among the destinations that can take it, **counting the items already on
  their way** to each, by any link (*Room promised on the way*, below) — and a Transport request; the origin keeps it (refused send → BLOCKED) until the
  assigned resource loads it: then the origin hands it over (`SendHeldItem`, after the current event if the
  load happened inside the origin's own send) and it leaves (`OnExit`); the resource travels
  (TRAVELING_LOADED), unloads and delivers (`Receive`, `OnEntry`) — statistics stay link-driven; if the
  destination was filled meanwhile by another element, the resource waits HELD until it has room (the
  destination's input fan-in notifies this link). Released at the delivery; after each delivery the origin
  is asked to send again. Requests needing several resources: they gather at the pick-up, the first carries, the others
  accompany it (`CarrierGroup`, *Preemption* above).
  **Every held item gets its own carrier**: a holder releasing in order (`ItemsQueue` FIFO,
  `LengthLimitedQueue`, `Delay`) passes over the items whose carrier is already requested
  (`Element.AwaitsCarrier`, `ResourceTransportLink.IsRequested`) and offers the next, until one is refused (no
  destination with room) — it offered only its head, so a queue as transport origin moved one carrier at a time.
  The holders that release by slot (`MultiServer`) or scan (`StorageSystem`, *any accepted*) already did.
- **Items that walk by themselves — `WalkLink`** (`SimLink/WalkLink.cs`). The item is a
  *mobile entity* (a patient — an entity that moves on paths by itself; a people module makes
  the person a TaskExecuter instead, which would take the pathway logic out of the item flow). Decision: the patient
  stays an item (agendas, routing, labels, link statistics, lists, studies unchanged) and moves with the
  **resources' travel machinery**: the link plans each walk with an `ITravelModel` (`NetworkTravel` on the model's
  network — the default —, `GridTravel` on a walk grid, `MatrixTravel`; `Plan(null, from, to)`) and exposes it as a
  `TravelLeg` (`ItemWalk.Leg`: route, path, start / end), the same animation contract as a resource's move. The
  destination is chosen as `ResourceTransportLink` does (output strategy among the destinations that can take it,
  counting the items on their way by any link); the item leaves at once (`ItemHandOver.Exit`, `OnExit`), walks from the origin's
  `Location` to the destination's in the planned time (model time, `SimClock.Schedule`), and enters
  (`ItemHandOver.Receive(null, …)`, `OnEntry`); a destination without room on arrival makes it wait at its door
  (`ItemWalk.Waiting`) until its input notifies the link (the first to arrive enters first). `WaitAtDoor`
  (`walkBy.waitAtDoor`, Unity `walkBy.waitAtDoor`; set by an agenda that `ArriveOnTime`): items set off even to a full
  destination and wait at its door (patients going to their appointment). A routing by rule (`IRuleRouting`: the
  expression and delegate strategies, `Route` = the named output whether it has room or not) chooses over **all** the
  destinations: the item goes there (or to its door) or waits — never to another one because the named one is full
  (`ResourceTransportLink` had the same flaw — the index applied to the destinations with room — and
  chooses the same way now, baselines unchanged). Two elements without a location, or at the same one, hand
  over at once (a general link). No resource, no capacity on the way, not paused by stops; the walk is in neither
  element (a sink's `age` includes it). Config: `walkBy.speed` (m/s, physical: × the calendar's seconds
  per unit, as a physical team), `WalkTravel` (Network / Grid / Matrix), `WalkGrid` / `walkBy.matrix`; built by
  `ResourceBuild.WalkTravel` (the team travel builder with a team config made from the keys); a transport team wins
  (warning). `HeadlessModelFactory.WalkLinks` lists them for the views; `WalkLink.Arrived` (an event) tells
  them a walk ended — for views only, nothing feeds back. Unity: `SElement.walkBy` (`ItemWalkUse`:
  speed, route, grid, matrix; extracted in `SceneExtractor.ExtractOwn`). Reference model `walking_patients`;
  `resourcetests` (route and time, room counted, waiting at the door, same place, config and units).
- **Headless configuration.** `SimulationConfig.ResourceTeams` (`ResourceTeamConfig`, typed settings:
  a `SettingsGroup` read and written by path like an element's — `id`, `name`, `members[i]` (`ResourceMemberConfig`: name,
  `properties` (`name = expression` records), load / unload time) and the request / resource orders as `ListQuerySpec`
  text; travel: `travel` (`TravelKind`: `Abstract`, `Network` (+ `traffic`), `Matrix`, `Grid`, `Straight`, `Axes`,
  `External`, `Calibrated` — the last two below), `units` (`ResourceUnits`), `speed`, `homeNode`, `matrix`, `walkGrid`,
  `axisSpeed` / `axisAccel` (`double[]` x, y, z) / `axesSequential`, `calibrationBase` (a `TravelKind`) / `calibrationFit`
  (`double[]` A, B, C) / `calibrationPairs[i]` (`CalibrationPairSettings`) / `calibrationMinSamples`; an unknown option fails
  when written); an
  element's use is in its parameters (the `processBy` / `transportBy` settings): `processBy.team`,
  `processBy.requirements` (`count#query#label`), `processBy.release`, `processBy.priority` (an expression over the item and
  the element; a bare name = the item's label), `processBy.holdFor` (a distribution) for its process, and
  `processBy.acquire` (`AllAtOnce` / `InOrder`; a requirement's fourth field = its team), `processBy.acquireBeforeEntry`
  and the element's `reserveExit`, and
  `transportBy.team`, `transportBy.requirements`, `transportBy.priority` (expression), `transportBy.loadTime` / `transportBy.travelTime` /
  `transportBy.unloadTime` (distributions, own random streams) for the transport of its items; a station's setup crew
  `setup.setupBy.team`, `setup.setupBy.requirements`, `setup.setupBy.priority`, `setup.setupBy.preemption`, `setup.setupBy.preemptable` (the `setup` settings, §3.2 *Setups*,
  `ResourceBuild.SetupOf`). `ResourceBuild` builds them;
  `HeadlessModelFactory` builds the teams, sets each station's `Resources`, creates a
  `ResourceTransportLink` instead of the `GeneralLink` for elements with a transport team, starts the
  members after the elements and then the dispatchers (`Teams`, `Executors`). The reference runner
  records `resource:<member>` (`utilization`, `tasks`, `state:*` ratios) and `team:<id>` (`members`, `served`,
  `tasks`, `utilization`, `state:*` (members' mean), `request_wait_avg` / `_std`, `requests_waiting_avg`).
  Reference models: `operator_pool` (a team of one shared by two servers), `resource_shared_team`,
  `resource_skill_query`, `resource_transport`, `resource_count_two`, `resource_request_priority`, `resource_preemption`,
  `resource_preemption_everywhere`.
- **Unity (§4.2, §4.3).** `UnityResourceTeam` (tool *Resources ▸ Resource Team*) holds the members
  (`ResourceMemberSpec`: name, `properties`, load / unload time, spawn point; *Add N* in the inspector), the request /
  resource orders (order part of a `ListQuerySpec`) and the travel (`TeamTravel`: Abstract / Network / Matrix / Axes /
  Grid / UnityPhysics / **Straight** — the default since 2026-10-29 —, with `physicsEstimate` and `calibration` for physics); its inspector shows each member's state, utilisation
  and tasks in Play mode. **A team is always seen**: spatial members move (below); an Abstract team's
  members stand at their spawn point (side by side at the team's) — `showMembers` off hides them. Every `SElement` has a **Resources** section with two blocks, each assigned or
  not: **Process** (`processBy`, `ProcessResourceUse`: team, requirements, priority expression, release, optional own
  time `holdForOwnTime` / `holdFor` → `processBy.holdFor` — shown only on `IProcessResourceUser` stations) and
  **Transport** (`transportBy`, `TransportResourceUse`: team, requirements, priority expression, travel time and optional load / unload overrides — the factory then
  creates a `ResourceTransportLink` instead of the `GeneralLink`). `SceneExtractor` exports the teams
  (`ResourceTeamConfig`, id = name + object id) and both uses as the `processBy` / `transportBy` settings parameters.
- **Travel.** A spatial `TaskExecutor` travels with its
  **`ITravelModel`** — **asynchronous by design**: `Plan(executor, from, to)` gives route, distance and
  duration, and the executor waits that long (`ScheduleWork`, so stops pause it) — or, for an **open-ended**
  plan (duration NaN: congestion, Unity physics), until someone calls
  **`TaskExecutor.Arrive()`**. Models: `NetworkTravel` (shortest route on a `TransportNetwork` ÷ speed;
  unreachable → exception) and `FunctionTravel` (a time function); a step with a defined duration (abstract
  transport) skips the model. The move is exposed as **`CurrentLeg`** (`TravelLeg`: from, to, route node
  ids, distance, start / end time, carried items) + `TravelStarted` — the animation contract. Every element
  has an optional **`Location`** (a network node); a resource's is where it is now. "Nearest resource"
  rules: a request carries a `Location` (the pick-up) and the resource field **`distance`** is measured to
  it (`Resource.DistanceTo`, set by the dispatcher while matching), so a resource order `distance` picks the
  nearest free vehicle. Teams: `ResourceTeamConfig.Travel = TravelKind.Network`, `Speed`, `HomeNode` → spatial
  members with `NetworkTravel` on the model's network. A spatial team without `HomeNode` starts at the network's first
  node — the first whose id has no `:` (the scene's automatic points are skipped), else the first
  (`ResourceBuild.DefaultHome`); `Study.Check` says
  which under `StudyCheck.Info`; a home that is no location fails naming `homeNode`. No scene or baseline had a spatial
  team without a home (it could not build), so no result changed. Reference models: `transport`, `resource_fleet_nearest`.
- **Units of a team.** `ResourceTeamConfig.Units` (`ResourceUnits`; a `Choice` of the team
  schema): `Model` (the default) = speeds in distance per model time unit, accelerations per unit², load / unload times in
  model units (the behaviour so far); `Physical` = m/s, m/s² and seconds, converted at build by
  `ResourceBuild.InModelUnits(tc, calendar.SecondsPerUnit)` (a copy: the config and the catalog keep the values as
  entered; a travel calibration is measured in model time and is not converted). `UnityResourceTeam.units` (default
  Model, so scenes are unchanged); its inspector shows the unit of every speed and what one time unit is; the
  physics-travel view gets the converted speed (`SimSpeed`). Tests: `bugfixtests` #37, EditMode `TimeUnitsTests`.
- **Matrix travel, stops mid-way, walking operators.** `DistanceMatrix` (model data:
  `TravelMatrixConfig.Entries`, typed `TravelMatrixEntryConfig` records `From` / `To` / `Distance`, symmetric
  option; `SimulationConfig.TravelMatrices`) + `MatrixTravel(matrix, speed)` —
  O(1) per move, straight route; a missing pair fails loudly. A team chooses `Travel = "Matrix"` + `Matrix` id.
  A model's locations (`ModelSpace`) are the network's nodes plus the matrices' locations; an element's
  `location` must be one of them. A **stop mid-way** (Immediate) pauses the leg's timed work and freezes it
  (`TravelLeg.PausedAt`); the resume shifts its start / end times, so `TravelLeg.Fraction(now)` continues where
  it stopped. **Spatial operators walk to the station** they are assigned to for a process (if it has a
  location; `StationResources`): the process starts when every walker has arrived, and the request's
  location is the station (nearest-operator rules); abstract operators are unchanged. **Work points:** a station may list **`workNodes`** (`Element.WorkLocations`, config `workNodes`,
  `HeadlessModelFactory.WorkNodesKey`, validated as locations, `ParameterSchema` structural) — the walkers go to a free
  work point (`StationResources.WorkPointFor`, one walker per point while it works there, freed when the job releases
  them; more walkers than points share the station's location), so welders stand beside the block, not on it; the
  station's `location` stays where items are picked / delivered (`bugfixtests` #40). Reference model:
  `resource_walk_matrix`. Unity: `UnityTravelMatrix` tool (nodes + entries; fill from straight lines or
  network routes), team travel *Matrix*; views place matrix travellers at their nodes and move them straight.
- **Straight-line travel — the default of a scene team.** **`StraightTravel`** (`Resources/Travel.cs`): between the
  coordinates of two points of the model's network (tracks unused) in a straight line at a speed, no obstacles and no
  collisions — distance = the Euclidean distance, time = distance ÷ speed, route `[from, to]` (views go straight), as a
  mobile resource. Team `Travel = "Straight"` (`ModelParameters`: `Speed`, `ReturnHome`). In a scene it is
  the **default** (`UnityResourceTeam.travel = Straight`; the enum value was appended, so saved scenes keep theirs): the
  extractor (`SceneExtractor.AddStraightTravelPoints`, only when a team travels straight) gives every element without a
  `location` a point **`pos:<id>`** where a person works at it (`UnityResourceTeam.AutoPoint` = `StationModel.StaffPosition`:
  its staff socket, else in front of it (−Z, the model frame), half its depth + 0.6 m, on its floor), a team without a home node the
  point **`home:<teamId>`** at its own position, and a robot team (`MembersAreArms`: a `RobotArmRig` or A1…A6 in the member
  prefab) a work node **`work:<id>`** at the station's work socket — so a team placed from the library is seen, walks /
  drives to the stations it serves and its moves take their time, with no network to draw. `Abstract` stays for logic
  models (no positions, no travel time: e.g. a case whose resources are only capacity); a team that must go round
  obstacles uses `Grid` or the network. The library team presets (`LibraryPresets.Team(cmd, name, kind)`) set Straight, `Physical` units and
  a realistic speed (walking 1.2 m/s, pushing a load 1.0, AGV 1.0, forklift 2.5, tugger 2.0, robot tool 1.5; naval cranes 0.5–1.0,
  SPMT 0.8) — all read from **`LibraryPresets.MemberKinds`**, the one table of member kinds (name, aliases,
  member prefab, speed, arm), also used by `simulean_add_team` (§4.7.2); robots get a `Home` node 1.4 m above and 1 m in front of the base (the tool's rest point) and an optional
  `RobotCellFence` (§4.7). Tests: `resourcetests` (Straight), EditMode `ResourceSceneTests.AStraightTeamGoes…`,
  `LibraryPresetsTests` (teams, robot fence). **A storage moved by a straight team** (`UnityStorageSystem.AutoLaneNodes`:
  its Transport team travels straight) gives each lane without a node the point **`lane:<storage>:<lane>`** at the lane's
  own position (`AutoLaneNodeId`, in `ResolveLanes`; the extractor adds the points), so the crane / forklift goes to each
  slot — an AS/RS crane to its height — with no nodes to place (an abstract team keeps the instant moves). The **AS/RS**
  of the library: a rack whose Transport is the *Stacker Cranes* team preset (2 m/s), placed in the aisle along the rack
  face (its +Z along the aisle); its stock published to a list that a picker pulls from; the crane also puts away
  (from the rack's location, see *Put-away by the Transport team*). EditMode `ResourceSceneTests.AStorageMovedByAStraightTeam…`.
- **Kinematic resources.** **`AxisTravel`**: between the coordinates of two points (nodes
  of the model's network, tracks unused), each axis x / y / z moves with a trapezoidal profile (max speed,
  acceleration; 0 = instant; `AxisTime`), together (slowest axis) or one after the other (`AxesSequential`); an
  axis with speed 0 cannot move; its `Distance` is the travel time. Team `Travel = TravelKind.Axes`, `AxisSpeed` /
  `AxisAccel` (`double[]` x, y, z; a study level may write them as `x|y|z`). The kinds are compositions, not new elements: **AS/RS** = a `StorageSystem` whose
  Transport team is a crane on axes (lanes at points, its I/O point = its location); **robot / gantry between
  stations** = an element's Transport by an axes team; **gantry crane on a `Stacker`** = crane mode: with a
  `PickLocation` and a location per `StackPosition`, and an axes team as its process resources, the Stacker
  asks for the crane, chooses the target when it is assigned (a crane with nothing to place goes back) and
  gives it travel to the pick point → load (the pick point frees) → travel to the position → unload (the
  item is placed); `CycleTime` is not used, `StackerCycle.Duration` is the expected time (views). Headless:
  stacker parameters `pickNode`, `positionNodes`. Unity: team travel *Axes* (axis speed / acceleration
  as `Vector3`, Unity y = vertical), stacker `pickNode` / `positionNodes` + *Create crane nodes* button;
  every `UnityTransportNode` of the model is exported as a point (network nodes first). Reference models:
  `resource_robot_cell`, `resource_gantry_stacker`.
- **Walkable area + own A*.** **`WalkGrid`** (engine data: cells on the x / z plane, free or
  blocked; `FromRows` / `ToRows` with `.` / `#`) finds paths itself — **A* on 8 neighbours** (octile heuristic,
  binary heap, a diagonal step needs both orthogonal neighbours free: no corner cutting) **+ line-of-sight
  smoothing** — so paths are computed headless and per GA candidate (the layout may be optimised).
  **`GridTravel(grid, points, speed)`**: locations (points of the network) go to the nearest free cell; time =
  path length ÷ speed; paths cached per pair for the run; no path → +∞ distance, planning fails loudly. The
  path's points travel on the leg (`TravelPlan.Path` / `TravelLeg.Path`, `WayPoint`) and views follow them.
  Config: `SimulationConfig.WalkGrids` (`WalkGridConfig`: origin, cell size, floor y, rows), team
  `Travel = "Grid"` + `Grid` id. Unity: **`UnityWalkGrid`** tool (rectangle centred on its transform, cell size;
  *Bake* from colliders — `Physics.CheckBox` above the floor with an obstacle mask, height and clearance — or
  from the NavMesh — `NavMesh.SamplePosition`; NavMesh is used only to bake, never at run time), team travel
  *Grid*, and `UnityTravelMatrix` *Walk grid (A*)* fill (the fixed-layout cache). Reference model:
  `resource_walk_grid`.
- **Resource views.** The team gives every spatial member a view (scene only,
  read-only): a gantry in the scene (`craneRigs`, e.g. a gantry stacker's own) or a member prefab with a crane rig →
  **`CraneResourceView`** on its **`ICraneRig`** (a `GantryRig` is added to a prefab with `Bridge` and `Head` nodes and no
  rig of its own; fitted to the team's reach points — home + locations of the
  elements using it + a crane-mode stacker's nodes — runway along the longer side); a member prefab with a 6-axis
  arm (a `RobotArmRig`, or the nodes `A1` and `A6`) → **`RobotArmView`**; a level-luffing crane
  (a `LevelLuffingCraneView`, or the nodes `Slew` and `Jib`) → **`LevelLuffingCraneView`**; anything
  else → **`UnityVehicleAnimator`** (order: a scene `craneRigs` gantry; Unity Physics travel → `PhysicsTravelMember`
  only; an `ICraneRig` in the prefab, else a `GantryRig` added on `Bridge` + `Head`; `Slew` + `Jib`; `A1` + `A6`; no
  member prefab = a box placeholder — only for a team built by code or an old scene: every editor path sets a prefab,
  §4.7.2; an *Abstract* team without one shows nothing, in the run and in the edit-mode preview). Engine support: `TaskExecutor.CurrentStep`, `StepStartTime` / `StepEndTime`
  (shifted by stops like the leg), `AxisTravel.Speed(i)` / `Accel(i)` / static `AxisPosition(d, v, a, t)` (same profile as the times).
  - `GantryRig` (`Visuals/`) = the gantry model's mechanics (Bridge along the frame Z, Trolley along X, Head up /
    down; `Fit` / `FitAround` resize the portal: posts, runways, end beams, saddles move, girders stretch;
    `SetHead` clamps to the travel limits) — **shared by `GantryStackerView`** (time-mode stacker cycles, logic
    unchanged; it stands aside in crane mode) **and `CraneResourceView`** (each horizontal axis follows its own
    `AxisTravel` profile; load / unload = head down to the item's top / its rest height, grip / release in the
    middle, head up; the item hangs from the head between grip and release).
  - **`ICraneRig`** (`Visuals/`) = what `CraneResourceView` drives: `HasHead`, `RestCup`, `Plan`,
    `CupWorld`, `PlanOf` / `HeightOf`, `SetHead(plan, cup)`, `FitAround(points, margin)`. Implemented by `GantryRig`, by
    **`TravelingGantryRig`** (`Visuals/`: a gantry whose `Bridge` carries its legs `Leg_W` / `Leg_E` on ground rails
    `Rail_W` / `Rail_E`, `Trolley` along the girder, `Head` on `Ropes` (scaled with the drop), a load's top at
    `HookSocket`; `Fit` moves the legs / rails and stretches the girder / rails; `reachAcross` / `reachAlong` per model —
    the `GoliathCrane` and `WeldingGantry` members) and by **`UnityOverheadCrane`** (the hall crane member: trolley = plan
    x, bridge = plan z, the hook socket = the cup; `FitAround` turns the runway along the longer side and sizes span,
    runway and rail height, then rebuilds — the `HallCrane` prefab holds only the component and is built when fitted).
  - **`AxisLegPath`** (`Visuals/`) = where a member is along its leg: with `AxisTravel` every axis x / y / z
    on its own profile (together, or x → y → z when sequential), else a straight line — used by `CraneResourceView`
    (plan only) and `RobotArmView` (all three axes).
  - **`RobotArmRig`** (`Visuals/`) = the 6-axis robot model's mechanics (`Art/Models/IndustrialRobot`):
    joints `A1`…`A6` found by name, link lengths measured from their rest offsets, **analytic IK** `SetTcp(point,
    toolDir = down)`: wrist centre = target − tool direction × (flange + `toolLength`); A1 = its heading; A2 / A3 by the
    law of cosines in the arm plane (elbow up); A4 / A5 / A6 = the remaining wrist rotation split as roll · pitch · roll,
    the wrist flip nearest the current pose. Solved in the frame of the model root, so **`mount` = Floor / Ceiling** (the
    root turned 180° about its forward axis; the member transform is the mount point) uses the same solution.
    Out-of-reach targets are pulled onto the reachable shell (one warning: move the robot closer); joints are clamped
    to their ranges. **Tools**: `RobotArmRig.tool` (a prefab, e.g. `Art/Models/RobotTools`
    `Gripper` / `VacuumGripper`) is mounted on `ToolSocket` at run start (a tool already under the socket is used as
    it is); its `TCP` node sets the tool length the IK places. **`RobotArmView`** = the robot resource's view: the tool centre point follows the leg on the
    team's axis profiles (`AxisLegPath`, tool down), goes down to the item's top / its rest height and back within a
    load / unload (grip / release in the middle), and the item hangs from the tool between grip and release,
    turning with the tool; a gripper's `Finger_L` / `Finger_R` close on the item's width while it is held. The
    robot base is the member transform (its spawn point, else the team's position: place it where the arm reaches the points; the
    engine and headless runs never see it). EditMode test: `RobotArmRigTests`.
  - **`LevelLuffingCraneView`** (`Visuals/`) = the view of a level-luffing portal crane member
    (`Art/Models/LevelLuffingCrane`). Its team uses **Axes** travel, and the axes move
    the **hook point**. The view turns that point into the crane's own motions:
    - **travel** along the rails, which are the member's local Z from its spawn point (the member's `spawnPoint`, else the team's transform); the
      crane stays abreast of the hook, or moves far enough to keep the hook beyond the minimum reach;
    - **slew** (`Slew` about Y) to face the hook;
    - **luff** to the boom angle whose jib tip is at that reach. The four-bar linkage `Linkage(theta)`, which matches
      the Blender script, sets the `Jib` and `Tie` rotations, so the tip stays almost level;
    - the **rope drop** to the hook height (`Ropes` scaled in Y, `HookBlock` under it): `hookTravelHeight` while
      travelling; within a load / unload, down to the load (top + `slingLength`) and back, with the grip / release
      in the middle. The item hangs on its slings under `HookSocket` and turns with the jib.
    The linkage dimensions are serialized fields that must match the model; an out-of-reach point stops the crane
    at its limit with one warning. EditMode test: `LevelLuffingCraneViewTests` (tip level within 1 m, reach
    inversion, hook on targets, tie joined to the jib tail).
  - `UnityVehicleAnimator` also drives forklift parts by name (`MastInner` / `Carriage` lift: low to pick up,
    raised while carrying, lowered to put down within the load / unload times; the load rides on `LoadSocket`;
    `Wheel_*` spin with the distance) and follows walk-grid paths.
  - Member prefabs (`Prefabs/ResourceMembers/`): `GantryCrane` (gantry model + rig + crane view), `Forklift`
    (forklift model + animator), `OperatorSphere` (placeholder), `AGV` / `Operator` (animator),
    `IndustrialRobot` (robot model + `RobotArmRig` + `RobotArmView`); `LevelLuffingCrane` (crane model +
    `LevelLuffingCraneView`). **People are one element:** `Operator` (`UnityVehicleAnimator` +
    **`OperatorAppearance`**) whose role is a field — `look` names an entry of the **`PeopleLooks`** catalog
    (`Visuals/PeopleLooks.cs`, asset `Prefabs/ResourceMembers/PeopleLooks.asset`: Operator, Doctor, Nurse, Patient,
    Fitter, Welder, Painter, Outfitter — male / female models and the default hard hat; `gender` Male / Female / **Mixed**,
    alternating per member through `ForMember`; looks are found case-insensitively); the appearance builds the
    look's models as children when enabled (not saved in the editor: opening a scene or prefab dirties nothing) and
    rebuilds them when `look` changes (inspector list). `Doctor`, `Nurse`, `Fitter`, `Welder`, `Painter`, `Outfitter`
    are **variants of `Operator`** that only set `look` (and gender / hat); the library offers only the *Operators*
    team, a role is chosen as the team's member prefab or on the appearance. **People clips:** one clip set for every look (they share the MPFB rig), keyed by
    `Tools/Blender/assets/people_animations.py` and exported as the takes of `Art/Models/Operator/People_Animations.fbx`
    (Generic); *Tools ▸ SimuLean ▸ Developer ▸ Build People Animations* (library maintenance: it rewrites the clips and the operator prefab) (`Editor/Tools/PeopleAnimationBuilder.cs`) turns them into
    looping `Operator/Animations/*.anim` (bone rotations only, paths `Root/Torso/…` below each look's armature; the
    pelvis position kept in `SitIdle` / `LieOnBed`), `Operator/People.controller` (one state per clip) and puts a
    **`PersonAnimator`** (`Visuals/PersonAnimator.cs`, read-only) on `Operator.prefab` (every role variant) and the
    `Patient` item. In Play it adds an Animator to the shown look's armature and cross-fades by `ClipFor`: walking
    (`Walk` / `CarryWalk`, or `PushWalk` for a porter — `pushes`, set on the porter prefabs — played at speed / 1.4 m
    per cycle), a load / unload step (`HandOver`), working at a station (a Process / Hold step, not moving; the work by
    the station model's name, `WorkAt`: `Examine` at a consultation / bed, `MachineTend` at a CNC / inspection /
    palletizer / robot cell, `Weld`, `Paint`, else `WorkDesk`; the station's item is not "carried"),
    carrying (`CarryIdle`), else `Idle` / `Idle2` (per instance, out of phase); items take `pose` from the layouts
    (`ItemLayout` seats → `SitIdle`, placed on the seat; a person's pivot height is 0 — `ItemVisuals.PivotHeight`: its posed bounds would lift it differently every frame; a Multi-Server whose item point is a `PatientSocket` →
    `LieOnBed`). **Item walk view**: an item the model places somewhere else (a layout, a station's slot) walks there from where it is shown — straight, `Walk` clip, at least 1.4 m/s and at most `maxWalkSeconds` (1.5 s real) — instead of jumping; moves under 0.6 m (a queue shuffling) or beyond `maxWalkDistance` (40 m), and an item shown again after being hidden, are taken at once; it then takes the pose it was given (a view: the model's times are unchanged; `walkBetweenPlaces` off restores the jump). *(the element's named positions)* A Multi-Server whose model has an `EntrySocket` (a box's door) makes its new items walk in **through it** (`PersonAnimator.WalkVia`) and those leaving walk out through it (`LeaveVia` → the `ItemWalkView` track starts by the door): no person crosses a box's walls; the walk ends at the element's location node (put it at the door). `PersonAnimator` runs after the layouts (`DefaultExecutionOrder(100)`): no walk clip shown with the person already at its place. A resource member with a `PersonAnimator` at a station — working, loading or unloading — stands at the station's staff position (`StationModel.StaffPosition`: a `StaffSocket` / `StaffSocket_1` / `OperatorSocket` of the element or its model, else in front of it — the side its −Z faces (the model frame, §4.6.1), half its depth + 0.6 m), not at its location node, which may be inside the machine; its walks to and from the station are drawn to and from that position (`UnityVehicleAnimator.CacheRoute`: a machine's replaces the node; a box with an `EntrySocket`, whose node is at the door, adds the way in from the door), so nobody walks through a machine or a box wall. The station of a node: the element located there that processes (a buffer sharing the node gives way). A view: the travel times are the model's. The default Source / Sink prefabs were scaled to the stations' size (×0.65). It freezes while the clock is paused and turns `UnityVehicleAnimator.ProceduralLimbs` off — the
    procedural swing stays the fallback (porters, models without clips). `BedPorter` /
    `StretcherPorter` / `WheelchairPorter` (the carrier at the root, a porter with look Nurse at its `DriverSocket`,
    the `OperatorAppearance` on the `Porter` child — the team looks it up with `GetComponentInChildren` — and a
    `LoadSocket` under the carrier's `PatientSocket`, so the patient rides on the bed; the bed's `Lift` rises while
    carrying), `PalletTruckOperator` (truck + a `Driver` with look Operator walking ahead of the tiller), `IndustrialRobot_Gripper` /
    `IndustrialRobot_Vacuum` (variants that only set `RobotArmRig.tool`; presets *Robots (Gripper)*, *(Vacuum)*,
    *(Welding)*), `IndustrialRobot_Welder`
    (variant with the `WeldingTorch`). **Wheels**: `UnityVehicleAnimator.FindWheels` spins every
    active node named `Wheel` / `Wheel_…` (not inside another wheel) with its radius read from its meshes (half their
    height) — the forklift, AGV, pallet truck, wheelchair and the SPMT's 28 bogies alike; `wheelRadius` is only the
    fallback for a wheel without a mesh. Naval members (`Prefabs/ResourceMembers`, presets *Shipbuilding ▸ Teams*):
    `SPMT` (vehicle view, `Lift` = the deck, stroke 0.35 m), `GoliathCrane`, `WeldingGantry` (`TravelingGantryRig`),
    `HallCrane` (`UnityOverheadCrane`), plus the existing `LevelLuffingCrane` and `IndustrialRobot_Welder`.
- **Unity-physics travel + calibration to 1D.** Engine: every executor can log its moves
  (`RecordMoves`, `Moves`: `MoveRecord` from / to / loaded / start / end / estimate distance; stops excluded — open-ended
  legs are shifted by stops too). **`ExternalTravel(estimate)`**: open-ended plans (duration NaN) ended by
  `TaskExecutor.Arrive()`; the estimate model (grid / matrix / network) gives distances. **`TravelCalibrationFit.Fit`**:
  per (from, to, loaded) mean / sd / n + least squares `time = A + B · distance + C · loaded` (R², residual sd).
  **`CalibratedTravel`**: the pair's mean when seen ≥ `MinSamples` times, else the general model on the estimate's
  distance (so it still holds when the layout changes); deterministic. Config: team `Travel = TravelKind.Calibrated` +
  `CalibrationBase` (estimate kind, with the team's Grid / Matrix fields), `CalibrationFit` (`double[]` A, B, C),
  `CalibrationPairs` (`CalibrationPairSettings` records: from, to, loaded, mean, sd, n — the asset stores the same
  records), `CalibrationMinSamples`. Unity: team travel **Unity Physics**
  (scene only) — each member gets a **`PhysicsTravelMember`**: on an open-ended move it sends its
  **`IPhysicsMover`** (default **`NavMeshMover`** = `NavMeshAgent`; or the prefab's own controller) to the node at
  speed × clock time scale, calls `Arrive()` when it gets there, halts while the member is stopped or the clock
  paused, ends overlong moves (time-out factor × estimate) with a warning; maximum-speed clock mode is turned off.
  **`RuntimeNavMesh`** (tool) bakes a NavMesh on start from colliders (core API, no package). **`UnityTravelCalibration`**
  (tool): *Fit from this run* writes the team's **`TravelCalibrationAsset`** (ScriptableObject: survives Play mode);
  *Compare with headless* runs the extracted model with the calibration for the time simulated and lists sinks'
  throughput and members' utilisation side by side; the inspector reports coverage of pairs, R², residual sd. The
  extractor exports a physics team as `Calibrated`, and **refuses** (clear exception) when it has no calibration —
  so experiments / the optimiser never run a physics model silently (EditMode `PhysicsTeamNeedsCalibrationForHeadless`). Reference model: `resource_walk_calibrated`
  (its example scene was removed 2026-10-30: a development tool, not a demo).
- **Traffic on the network.** **`TrafficControl`** (one per model run, shared by
  every team with `Traffic` on the network): a track holds at most `SegmentCapacity` vehicles (either direction — they
  cannot pass). **`TrafficNetworkTravel`** (an **`IDrivenTravel`**: the plan is open-ended and the executor calls
  `Begin` once the leg exists) moves the vehicle segment by segment (`ScheduleWork`, so stops pause it): it reserves
  the next segment before entering, waits at the node while it is full (state **`TRAFFIC_WAIT`**, busy; it keeps the
  segment it came from), enters released segments in arrival order, and ends with `Arrive()`. The leg exposes
  `SegmentIndex` / `SegmentStartTime` / `SegmentEndTime` for views. **Deadlocks** (a cycle of waits) are detected
  when a vehicle starts waiting, by following the wait chain; the vehicle closing the cycle is let through and it is
  counted (`DeadlocksResolved`) — many of them mean the layout needs passing places or one-way tracks. Statistics:
  `Waits` (waits longer than zero), `TotalWait`, captured by `RunResults` as the `traffic` object (`ResultScope.Traffic`);
  the reference runner records them as `traffic`. A segment's time uses the length on the track types the route was
  planned on; finished moves are not kept (#45). Config:
  `ResourceTeamConfig.Traffic`, `NetworkConfig.SegmentCapacity`. Unity: team *Traffic* (network travel), network
  *Segment Capacity*, traffic line in the team inspector, animator segment by segment. Reference model:
  `resource_agv_traffic`. Still open: grid-cell reservations (free movement), multi-load vehicles, priority / traffic
  rules (one-way, right of way), deadlock avoidance instead of resolution.
- **Milk runs — multi-load vehicles.** **`MilkRun : Element, ILink, IElementMetrics`** (`Resources/MilkRun.cs`):
  a tugger train — or any vehicle carrying several carriers at once — run by a member of its **Transport** team
  (`StationResources`; several `Trains` = several members, each one tour at a time). Not part of the item flow (it receives
  nothing); it is **the output link of every origin and an input link of every destination** it serves, so movement stays
  link-driven: an origin offers a carrier (`SendItem`, refused → the origin keeps it; a queue behind the milk run is waiting for a puller, so it holds — PROCESSING, not BLOCKED — and offers only its head,
  the next one is offered after each hand-over), the train loads it (`Load` step; then the origin hands it over:
  `SendHeldItem` → `OnExit`), unloads it at its stop and delivers it (`Receive` + `OnEntry`), waiting HELD while the
  destination is full (`NotifyAvailable` delivers and continues it). Room at a destination = free capacity − carriers on
  their way (reserved, on board, waiting).
  - **Fixed tours.** `MilkRunStop`s (name, location, empties origins, own empties destination / filter), `MilkRunFlow`s
    (origin at the depot → destination at a stop, filter expression over item + origin, `MaxPerTour`, own load / unload
    time), `MilkRunTourType`s (flows + stops in order) and a `Pattern` (cyclic, e.g. A, B, B, or `OnDemand` = the first
    type of the pattern, from the one after the last tour, that is ready). **Departure** (`MilkRunDeparture`): `Ready`
    (`ReadyRule` Any / All flows of the tour have a ready carrier whose destination has room; a tour without flows: an
    empty ready at one of its stops; **`ReadyMaxWait`** > 0, 2026-10-08 — key `readyMaxWait`, Unity `readyMaxWait`: once the
    train has waited that long at the depot for the due type it leaves as soon as any flow is ready, of the due type or of
    the next type of the pattern that has one, so a train never waits for a flow that cannot become ready, e.g. a full
    line side while the line is stopped by another part; **per flow** (key `flowReadiness` =
    `flow|minPerTour|optional`, Unity `FlowSpec.minPerTour` / `optional`): `MinPerTour` n ≥ 2 = the flow is ready only with
    n carriers at its origin — the one it offers and the ones queued behind it, which origins advertise
    (`IAdvertisingLink.Advertise`, counted whatever the `OriginAccess`) — and room for n at its destination, i.e. a full
    load (a train never leaves with one wagon of two); an `Optional` flow neither makes a tour ready nor holds it back
    (its carriers go if ready when the tour leaves); defaults keep the previous rule), `Schedule` (every `DepartureInterval` from `FirstDeparture`, or a `Timetable`
    repeated every `TimetableCycle`; leaves with whatever is ready), `ScheduleWhenReady`, `ReadyOrSchedule`. A missed
    slot (train still on tour) makes it leave as soon as it is back. Loading: flow by flow in turn, one carrier at a time,
    while flow maximum, destination room and `Capacity` (total, and `CapacityByType` by item type or `TypeLabel`) allow.
    At each stop: unload its carriers, then collect empties while they fit; back at the depot, unload the empties.
  - **The tour is one task** of the member: `TaskSequence` of Travel / Load / Unload / Hold steps, completed as it goes
    with the new **`TaskSequence.Expand(addSteps)`** step (the steps it adds are inserted right after it when it is
    reached — "load the next ready carrier, then look again"). So utilisation, time per state (`WAITING_DEPARTURE` at the
    depot while the departure rule is not met — the member stays reserved for the train but the wait is **not busy** —,
    LOADING / UNLOADING, TRAVELING_LOADED /
    EMPTY, HELD at a full destination) and downtime (`Element.Stop` pauses the timed steps) work as for any resource.
    When a tour ends the member is released and the train asks for it again.
  - **Legs**: a leg time (`SetLegTime(from, to)`, `SymmetricLegs`; stop names, the depot by its name), else the member's
    travel model to the stop's location (spatial teams: network / matrix / grid / axes; a stop without location takes its
    first destination's, the depot its first origin's), else `DefaultLegTime` (the Transport block's travel time), else
    none (abstract). Per-carrier times: the flow's, else the Transport block's load / unload, else the member's; empties:
    `EmptiesLoadTime` / `EmptiesUnloadTime`.
  - **Dynamic tours** (`TourMode = Dynamic`, e.g. a rail shuttle): origins at any stop (`MilkRunStop.Pickups`,
    the depot included), flows to any stop, the destination of a carrier = the flows of its origin that accept it and have
    room, chosen by the origin's output strategy (its route). Open transport requests are entries of the **`Requests`**
    `SimList` (`MilkRunRequest`: `kind` pickup / delivery, `stop`, `distance` from the
    deciding train — travel model, or the constant leg time —, `onBoard`, `originFill`, `item.x` (the carrier), `origin.x`,
    `destination.x` (`origin.name`, `origin.content`…), `age`; 2026-11-06: one name per value, §3.18);
    the next stop = the stop of the first actionable request in **`RequestOrder`** (a list query: FIFO by default,
    `|distance` = nearest stop, `|origin.content desc` = longest queue, filters by kind / destination / type). Policies
    (`MilkRunDynamicPolicy`): `Opportunistic` (at each stop unload what is due, then load what fits) and
    `FillThenDeliver` (visit origins until full or nothing more to pick up, then deliver everything); **`LoadTimeout`** =
    wait at a loading stop for more carriers until full or T after the arrival; capacity 1 = the reference single-load
    shuttle. One task per busy period (it ends when the train is empty and no request is actionable); the train then
    waits empty for its next request in `WAITING_DEPARTURE` (not busy), while the waits inside a trip — `WaitOnBoard`
    (carriers on board, nothing actionable), `LoadTimeout` / `FillTimeout` waits at a stop — are `WAITING` (busy: the
    train is mid-trip). Pick-up waits are the `Requests` list's wait statistics.
    *(rail-shuttle extensions, generic; tests in `milkruntests`, reference model `milk_run_shuttle_line`)*:
    - **`Assignment`** (`MilkRunAssignment`): `AtStop` (above; the request only picks the stop) or `BeforeTrip` — the chosen
      request is assigned at once (a pick-up is reserved before the trip, so it already counts for its destination's room)
      and the train serves only that carrier, then decides again ("pull one request, travel, load / unload it").
    - **`DeliveryOrder`** (list query; null = `RequestOrder`): the order once the train delivers (fill-then-deliver's
      delivery phase; the opportunistic train while it carries something — deliveries and pick-ups that fit mixed, e.g.
      `|distance` = nearest stop). `|pushTime` = loading order (a delivery request is pushed when its carrier is reserved).
    - **`FillTimeout`** (fill-then-deliver): 0 = deliver when nothing more can be picked up (default, as before); T > 0 =
      wait (WAITING, where it is) for more until full or T after the first carrier of the cycle was loaded; < 0 = wait
      until full. A train with carriers on board and nothing actionable waits inside its task (`WaitOnBoard`).
    - **`OriginAccess`** (`MilkRunOriginAccess`): `Head` (default: a queue's first carrier) or `Any` — every carrier an
      origin holds is a request (free access, each pushed when it entered); origins advertise the ones behind their head
      through **`IAdvertisingLink`** (`SimLink/IAdvertisingLink.cs`, implemented by `ListLink` and `MilkRun`; used by
      `ItemsQueue`, `LengthLimitedQueue`, `ReleaseSource`).
    - Opportunistic stops are served **one carrier at a time**: unload what is due, load one, look again — a carrier
      loaded for the stop it is at (a feed queue at the same stop) is unloaded before the next is loaded.
    - Request field **`origin.x`** = a property or built-in of the origin element (e.g. `|origin.priority desc`).
    - *(urgency rules)* Request fields **`originFill`** (origin content / capacity, 0…1) and **`origin.free`**
      (its free capacity), pick-ups only (NaN for deliveries); setting **`requestFields`** (records
      `requestFields[i]`: `name`, `formula` — `MilkRunRequestFieldSettings`) defines calculated fields of `Requests`
      (`SimList.DefineFields`, Expression kind, not monitored) usable in the orders, e.g. `urgent=originFill >= 0.8` with
      the named order `|urgent desc, distance` = nearest stop with priority to near-full origins.
      Engine / headless only: the Unity wrapper has no field for it yet (a scene sets it through `ConfigureModel`). Test `milkruntests` (*Urgency rule*).
    - **Named orders** (`namedOrders[i]`: `name`, `query`): `requestOrder` / `deliveryOrder` may name one; then they are
      parameters (choices) of the optimiser / Experimenter — `ParameterSchema.ChoiceFrom(key, optionsKey)` (§3.19), a
      choice whose options are the names of another setting's records (`ParameterSchema.RecordNames`: typed records with
      a `name`, or `name=…` texts).
  - **Statistics** (`ReportMetrics`, `ResultMetrics`): `tours`, `tours:TYPE`, `tour_time_avg` / `max` (assignment →
    empties unloaded = the four stages), `stage_depot_avg` (waiting + loading), `stage_outbound_avg` (to the end of the
    last stop: deliveries and empties pick-up), `stage_return_avg`, `stage_empties_avg`, `departure_wait_avg` / `total`,
    `delivered`, `delivered:FLOW`, `empties_returned`; dynamic: `request_wait_avg`, `load_avg` (carriers on board,
    time-weighted) and `distance` (travel-model distances, or the constant legs' values). The element's state: PROCESSING while
    a train is on tour (its `utilization` = share of time on tour), `WAITING_DEPARTURE`, IDLE.
  - **Config** (`MilkRunHandler` over the typed `MilkRunSettings`; element references are config ids or
    names): `depotName`, `depotNode`, typed records `stops[i]` (`name`, `location`, `emptiesOrigins`,
    `emptiesDestination`, `emptiesFilter`), `flows[i]` (`name`, `origin`, `stop`, `destination`, `maxPerTour`,
    `loadTime`, `unloadTime`, `filter`), `flowReadiness[i]` (`flow`, `minPerTour`, `optional`), `tourTypes[i]` (`name`,
    `flows`, `stops`), `capacityByType[i]` (`type`, `capacity`), `legs[i]` (`from`, `to`, `time`), `pickups[i]` (`stop`,
    `origins`), `namedOrders[i]`, `requestFields[i]`; `pattern` (a list of tour type names, `A, B, B`), the enums `patternMode`, `departure`,
    `readyRule`, `tourMode`, `policy`, `assignment`, `originAccess`; `departureInterval`, `firstDeparture`, `timetable`
    (a `double[]`), `timetableCycle`, `capacity`, `typeLabel`, `trains`, `emptiesDestination`, `emptiesLoadTime`,
    `emptiesUnloadTime`, `symmetricLegs`, `loadTimeout`, `requestOrder`, `deliveryOrder`, `fillTimeout`. Each record type
    has an `Of(…)` for code. The team and default times are the element's Transport use settings. `HeadlessModelFactory` gives the milk run its Transport use (pass 1a); the handler's
    `Wire` resolves the references and connects the links (`MilkRun.Connect`; a flow origin connected elsewhere is warned
    about and re-linked). An empties origin that empties carriers itself (a `Separator`: its output hands out the
    pieces) or already sends its items on is a build error naming the pattern — the element's `emptiesDestination` to a
    Queue at the stop, the Queue in `emptiesOrigins` (`MilkRunHandler.CheckEmptiesOrigin`).
  - Unity: **`UnityMilkRun`** (element, *Transport* module, Core pack; §4.2) and its view **`MilkRunTrainView`** (§4.2). View:
    a leg's `LegSpec.via` points (view only, not extracted; reversed for the way back of symmetric legs, corners rounded by
    `viaCornerRadius`) make an abstract train's drawn tractor follow aisles instead of the straight line between the stops
    (`UnityMilkRun.LegPath`, `PolylinePath`); the leg time is unchanged. A rail shuttle can be drawn as a procedural
    **`UnityTransferCar`** (`TransferCarMesh`), and the vehicle animator's `keepHeading` keeps a rail vehicle's heading.
    Reference models `milk_run_two_tours` (hand-checked tour ends 41, 77, 122, 186, 227) and `milk_run_shuttle_line`;
    tests `StandaloneTests/milkruntests` (fixed and dynamic tours, hand-computed), EditMode `MilkRunSceneTests`,
    `TransferCarTests`. The scene extractor's third pass (`IElementReferenceExtractor`) writes the parameters that name
    other elements once every element has its id.
- **Test fixture scenes** (`Assets/SimuLean.Unity/Editor/Tests/Scenes/`, one per resource kind, kept out of
  `Assets/Scenes` since 2026-11-09 so the Examples tab does not list them; all run headless in the EditMode test
  `ExampleSceneRunsHeadless`, and the whole-scene scans `ConfigJsonSceneTests` / `ParameterCatalogTests` see them
  with the examples, `IsolatedScene.SceneFolders`): `Resources_Operators` (abstract team shared by two stations),
  `Resources_OperatorsWalking` (matrix travel, process + transport), `Resources_OperatorsWalkingGrid`
  (operators as spheres on a walk grid around walls), `Resources_AGVs` (fleet on a network, nearest AGV),
  `Resources_RobotCell` (axes with acceleration; the `IndustrialRobot` member on the floor at (0, 0, 0.2) since
  2026-10-07 — the points are unchanged, so the results are too), `Resources_GantryStacker` (the stacker's own gantry driven by the
  crane resource), `Shipyard_BlockAssembly` (generic ship block assembly: plate cutting → fitting → sub-block
  welding → inspection, block assembly of a jig + 3 sub-blocks → block welding → painting; forklift on a matrix,
  two gantry cranes on axes, transporters on a road network, workers with skills on a walk grid; minutes).
- Tests: `StandaloneTests/resourcetests`.

**Matching + transport (the receiver / transport split: the receiver
decides *which* item it takes, the holder *whether* it is carried).**
- **Offer order** (`OfferOrder`, config `offerOrder` / `offerScan`, `ParameterSchema` choice) of `ItemsQueue` and
  `MultiServer`: `FIFO` (default, unchanged: only the first item is offered) or **`AnyAccepted`** — `Unblock` offers
  each held item, oldest first (up to `offerScan`, 0 = all), through `SendHeldItem` until a receiver accepts one;
  a new arrival may leave at once. The receiver chooses with its input strategy (a Combiner's pull mode); the output
  strategies already pick among the receivers that `CanAccept` the item. With a transport (`ResourceTransportLink`)
  every acceptable item gets its own transport request. A queue can also **group by the receiver's setup**
  (`groupBySetup` / `groupLimit`, §3.2 *Setups*): the items a station is set up for go first, at most
  `groupLimit` in a row ahead of the oldest. **Offer by order keys** (1b): a Queue's
  `offerSort` (`ItemsQueue.OfferSort`, an **`ItemOrder`**) — order keys in the list-order grammar (`priority desc, due`,
  `item.prio * 10 + age desc`), each an expression in the context `offerSort` (bare = an item label or built-in,
  `element.x` = the queue, model and table reads, no random), read live at every offer; best first, ties by arrival.
  FIFO offers only the best item (a new arrival that ranks first is offered at once, so it is not stuck behind a refused
  head), AnyAccepted offers them best first (the first `offerScan`), carriers are requested in that order; the receiver
  still decides. Grouping by setup and reneging keep their own order. Empty = arrival order (the default).
- **Queue state:** an `ItemsQueue` is IDLE when empty, **BLOCKED** while none of its items
  can leave now — behind a `GeneralLink`, no destination could take the head (any of the first 64 with `AnyAccepted`),
  probed with `GeneralLink.CouldTake` (each destination's `CanAccept`, no routing run, no side effects) at every
  arrival, departure and refusal; behind carriers or another link, from a refused offer until an item leaves —, and
  PROCESSING otherwise; items advertised in a list wait for a puller and are not blocked. It showed PROCESSING after
  every arrival (≈ 99 % behind a busy station: BLOCKED understated). 32 baselines regenerated (the PROCESSING /
  BLOCKED split; `utilization` = not empty, unchanged except for queues that feed carriers); `bugfixtests` #83.
- **`TransportMoves`** (`Resources/TransportMoves.cs`) — the move of one element's items by its transport team, shared
  by `ResourceTransportLink` (direct links) and `ListLink` (list transfers): the request, the carrier's task (empty
  travel to the origin, load — the origin hands the item over and the link sees `TakeLoading` —, loaded travel,
  unload, deliver through the link's callback, or wait HELD until `DeliverWaiting`). The direct link keeps its
  destination choice and the room promised to items on their way; an item stays "requested" until it is actually
  handed over (a deferred hand-over, e.g. zero load time, must not get a second carrier). A carrier
  that finishes loading at a **stopped** origin (output blocked) waits HELD (`WaitingForOrigin`) and takes the item when
  the stop ends; a run reset forgets such waits.
- **Room promised on the way — one ledger per receiving element** (`Element.cs`,
  2026-11-05). A load that is sent before it can enter — carried by a team, walking, on a milk run, or
  matched by a list (carried or not) — holds a place at its receiver from the moment its move is decided until it enters:
  `Element.PromiseRoom(item)` / `ReleaseRoom(item, by)`, read as `RoomPromised`, `FreeCapacityBeyondPromises()` and
  `HasRoomBeyondPromises(asker)`. A station with `ReserveExit` (above) promises its item's place before the job
  starts — a *reserved* promise (`ReserveRoom`), which a general link honours too — and the link that moves the item later
  takes the same promise over. Before, each `ResourceTransportLink` / `WalkLink` / `MilkRun` counted its own loads
  per destination, so several links into one element each promised its last place: loads were parked HELD at a full
  destination and a team that also worked downstream could deadlock (hidden by fixed paired routes
  and oversized yards). Who promises and releases: `ResourceTransportLink` (at the request — before the carrier leaves
  the origin — to the delivery), `WalkLink` (from the departure to the entry, also with `WaitAtDoor`), `MilkRun`
  (`Reserve` → `Deliver`; its departure rules and loading read `RoomPromised`), `ListLink` (from the match to the
  transfer or the carried delivery; a pull is registered and matched only while a place is left after the promises; a
  refused transfer lists the item again and releases it). The joint moves (`CarrierGroup`) are moves of those links.
  `MostAvailableCapacityStrategy` ranks by the places not promised. Not promises: a `GeneralLink` hands over at once
  (it checks `CanAccept`; a destination it fills while a carrier is on its way still makes that carrier wait HELD), and
  a storage's own put-away / relocations reserve lane positions inside the storage (`StorageLane.Reserve`).
  **Releases.** A promise ends at the entry, or when the load is listed again (a refused list transfer); a preempted
  empty move asks again for a carrier and keeps its one promise (same item, same receiver: no double count); Abort never
  takes a transport (`TaskExecutor`: a transport task is not abortable), and a run reset forgets the promises
  (`ForgetPromise`, from each owner's `Reset` / `Start`). When an asker was refused only because the free room was
  promised (`HasRoomBeyondPromises` remembers it), a release by **another** owner notifies the element's input (after the
  current event), so every upstream tries again; an owner's own release keeps its own notification (the origin asked
  to send again), so a model with one link into each receiver runs exactly as before — every baseline unchanged.
  `resourcetests` (*room on the way per destination*, *promise on preemption*, *transport and lists share the room*),
  `milkruntests` (*two milk runs*).
- Reference models `yard_kitting` (direct, Any Accepted: two bays by key, out-of-order blocks, one crane; FIFO deadlocks
  — `bugfixtests` #39), `yard_kitting_list`, `yard_kitting_capacity` (one Combiner, 2 assemblies); EditMode
  `MatchingTransportSceneTests` (the scene's extraction gives the reference result). Pattern guide:
  `Assets/Docs/Kitting_By_Key.md`.

### 3.18 Expressions — one syntax for every decision (`Expressions/`, namespace `SimuLean.Expressions`; one vocabulary 2026-11-06)

Wherever a value of the model drives a decision the same syntax is used (user reference: `Assets/Docs/Expressions.md`,
which has the table of contexts). **`Expression.Parse(text, context,
owner, field)`** compiles the text once (recursive descent; `FormatException` with the position) into a tree evaluated many
times **without allocating** (`expressiontests`). Values are **`ExprValue`** (number or text, plus *missing*).

- **Contexts.** **`ExpressionContext`** declares, per place an expression is written, the roots it serves (with what each
  means there), what a bare name reads (`Bare`: `item`, `entry`, `element`, or the metric / factor names of a fitness or
  study), and the function families (random, model, data); `ExpressionContext.All` is the one table (`simulean_vocabulary`
  shows it). Parsing refuses a root or a function the context does not serve, an unknown function or root (with the
  closest names; names are case-sensitive, functions and roots lower-case — `MAX(…)` / `Item.x` get "did you mean"), and
  `true` / `false` used as a root. `Expression.Parse(text)` (no context) = `ExpressionContext.Any`, for code with its own
  scope. **`Expression.ParseCondition`** parses a condition (admission, call-off, sequencing *applies*, milk-run filters,
  reaction conditions and *only if*, a study's `where`) and also refuses a constant text.
- **Grammar.** Numbers, texts `'…'` / `"…"` (a doubled quote escapes), `true` / `false` (1 / 0); `root.x`,
  `root['any name']` for the roots `item`, `element`, `origin`, `destination`, `entry`, `signal`, `out` (one meaning each,
  no aliases; `out` — a routing score's output — is a root only before `.` / `[`, so a label named `out`
  stays a label); a bare name = the context's default; `+ - * / %`, comparisons (two texts: `==` / `!=` exact, `<` …
  ordinal unless both read as numbers; a missing value equals `''`; text vs number compares as numbers), `&& || !`,
  `?:`, `a ?? b` (b when a is missing). Functions (`FunctionArities`): math (`min max abs floor ceil round sqrt pow if has
  now`, `ln exp clamp`; `has(x)` takes any expression), random (`rand uniform
  exponential(rate) normal triangular(min, mode, max) lognormal(mu, sigma) pick`; each takes its sampler's parameters —
  `exponential` the rate, `lognormal` the mean and sd of ln x — and draws from the use's stream), model (`content free get stat team list count`), tables (`table cell rows sum`).
- **Finite numbers, numeric conditions.** Every number an expression computes or reads is checked where
  it is produced — each arithmetic result, each function's result, each reference read, each literal (parse) — so no NaN
  or infinity flows on: `10 / 0`, `0 / 0`, `x % 0`, `sqrt(-1)`, `pow(-1, 0.5)`, an overflow, a label holding NaN throw. A
  condition (`?:`, `&&`, `||`, `!`, `if`, `Condition`) is a number; a text throws (`ExprValue.AsBool`; a constant one fails
  the parse). `Expression.Evaluate` / `Number` / `Condition` turn any failure of the evaluation (these, a computed name the
  model lacks, a missing stream / model / table) into an **`ExpressionException`** (an `InvalidOperationException`; `Time`,
  `Expression`) whose message names the time, the owner, the field and the text; it stops the run (the event loop does not
  catch it; a reaction wraps it naming the reaction). The checks are one comparison per value (no allocation). A live
  statistic that is NaN (no value yet) reads as missing; a table cell `NaN` / `Infinity` is a text. Before, `Number()`
  turned NaN into 0, `÷ 0` gave 0, `sqrt` of a negative 0, `Condition` treated NaN as false and a text as true when not
  empty — no reference model relied on it (baselines unchanged).
- **Arithmetic takes numbers; random parameters are valid.** `+ - * / %`, unary `-` and the numeric
  arguments of the math and random functions (`Expression.NumericArgument`; `pick`: its weights) refuse a text: a text
  known when parsed (a constant, or a choice of constants — `Node.Values`) fails the parse with the position; a text read
  at run time throws (an `ExpressionException`). Comparisons keep their rules (a text that reads as a number compares as a
  number). The random functions check their parameters (`Expression.RandomProblem`: `uniform` min ≤ max, `exponential`
  rate > 0, `normal` / `lognormal` sd ≥ 0, `triangular` min ≤ mode ≤ max, `pick` weights ≥ 0; also `clamp` lo ≤ hi): constant ones (`Node.IsConstant`: literals, operators and math functions
  over them) when parsed — plus `pick` with every weight 0 —, computed ones at the draw (a computed `pick` whose weights are
  all 0 stays missing). Before, a text counted as its number or 0 and an invalid parameter drew 0 (`exponential`) or a
  meaningless value; no reference model relied on it (baselines unchanged).
- **Scopes** (**`IExpressionScope`**, plus `IDataScope` / `IRandomScope` / `IModelScope` — the latter gives the
  `SimContext`): **`EntityScope`** (item, element, `Origin`; bare = the item's label; built-ins `item.id / type / priority /
  created / age`, `element.name / state / content / free` — `state` is the current state's name), the list entry scope in
  `SimList` (bare / `entry.x` = the entry's field; `item.x`, `origin.x`, `destination.x` through the entry's field source;
  each calculated field draws from `<list>/field:NAME` and reads the model), `Reaction`'s scope (signal root; stream
  `reaction:<name>/expr`), a member's `ObjectScope`, the optimiser's metric scope and the study's factor / metric scope.
- **Live statistics, one reader.** `stat('M1', 'output')`, `team('T', …)`, `list('L', …)` read through
  **`ElementMetrics` / `TeamMetrics` / `ListMetrics`** (`Results/LiveMetrics.cs`), the readers `RunResults.Capture` fills the
  results with — one path for the values (CLAUDE.md statistics rule), names = `ResultMetrics`; plus the live counts
  `team: free / busy / waiting`, `list: pending`. `count(X, field, value)` counts the items an element holds by the link
  layer (`ElementStatsCollector.ItemsInside`) or a list's entries. All read-only.
- **Build-time check.** While `HeadlessModelFactory.BuildModel` builds, every expression parsed with an owner is
  collected (**`ExpressionCheck`**, thread-static); once the model is built, `ExpressionCheck.Problems` resolves the
  constant names it reads — elements (`content / free / get / stat / count`), teams, lists, tables and columns, element
  properties (`element.p` of its owner, `get('X', 'p')`, `origin.p`: a built-in, a declared property or one a reaction's
  SetElementProperty sets) and statistic names — and the build fails listing each with owner, field and "did you mean". The
  resolved targets are kept in the call nodes (one immutable record per model; a constant name not resolved at build — an
  expression parsed outside a build — is resolved at its first evaluation), so a constant name is never looked up again. A
  name computed at run time keeps the lookup: `SimContext.FindElement` / `FindTeam` / `GetList` are dictionary reads under
  the context's lock (uncontended in a run: one thread per model; kept because the Unity views read the context from the
  main thread; `GetList` is indexed). Unity validate, the scene run and the
  runner's validate get it through the one build path.
- **Names computed from labels.** `ExpressionCheck.Problems` also takes the model's **`LabelWrites`**: per item
  label, the values its writes give when they are known at build — a label record (`LabelAssignments`) or a reaction's
  SetItemLabel value whose `Expression.WritesLabel` is set, analysed by `StaticValues` (constants, the branches of `?:` /
  `if` / `pick` / `??`, a constant table's column), a reaction's number, a `ScheduleSource` arrivals-table column
  (`HeadlessModelFactory`). When every write of a label is known, each value given to a function that names an object
  (`content free get stat count team list table cell rows sum`) through that label (`content(item.dest)`) must name one of
  the right kind, or the build fails naming the writer. A computed write, or a label set only by element code (not seen),
  leaves the name to the run-time check.
- **Run-time warnings.** Item labels and list entry fields are dynamic: the first read of one that is missing warns once
  per reference (time, owner, field, expression, name) through **`SimContext.Warn`** (`ModelWarnings`, also `SimLog`),
  unless the read is inside `has()` or left of `??`. A routing value that names no output warns once per value
  (`ExpressionRoutingStrategy.Unrouted`); `RunMonitor` / `ModelDiagnosis` report it, and the diagnosis lists the model's
  warnings. `RunResults.Capture` keeps them in `RunInfo.Warnings`, and a study's answer (`StudySpec.Answer`:
  `simulean_run_headless`, `simulean_run_status`) lists them under `warnings` (`StudySpec.ModelWarningNotes`): one line per
  cause over the runs — the same text up to its numbers (time, item id) in several runs is its first text with "(and in k
  more run(s) of this answer, at other times)", the k-th of a kind in one run another cause —, the first 10, then a count.
  The command's log capture (`AgentReply`) words its copy of a `[SimuLean] t=…` warning the same way, so the two merge.
- **Uses** (context, owner field): times (`ExpressionSampler`, sampler type `Expression` — an empty text fails the build,
  no hidden default), request priorities (`ResourceBuild.PriorityOf`, stream `<station>/priority:<key>`), routing
  (`ExpressionRoutingStrategy`; random draws kept per item until it leaves, `IRoutingMemory`), routing scores
  (`BestScoreRoutingStrategy`, context `score`, `EntityScope.Out` = the output scored, `out.p` checked against every
  element at build), admission
  (`ConditionStrategy`; `origin` = the sender), labels and member properties (`LabelAssignments`; `true` / `false` are not
  names), tallies (`ItemTallies`), max waits (`WaitLimit`), the agenda, the call-off, the sequencing station, milk-run
  filters (`element` = the milk run, `origin` = the carrier's origin; streams `<milk run>/filter:<flow>`,
  `empties:<stop>`), list and milk-run request fields, reactions (`ReactionBuilder`: the context by signal kind), the
  optimiser's fitness and the study's `where` / `derived`.
- **Unity.** Every expression field carries **`SimuLeanExpressionAttribute`** (context key, `Records` for `name = expr`); the
  shared drawer **`ExpressionField`** checks it on edit with the same parser and the scene's element names, and
  `simulean_set` / `simulean_apply` refuse a text that does not parse (EditMode `ExpressionFieldTests`).
- **List queries** (`ListQuerySpec`, §3.16) use the expression's quoting (`kind == 'pickup'`), operators with or without
  spaces and `desc` / `asc` in any case; their fields are the entries' own plus `item.x` / `origin.x` / `destination.x`
  (**`ListFields`**: the fixed fields of team requests, members and milk-run requests); an unknown one fails the build, a
  label missing from an item entry warns once per list and field.
- **Data tables.** `table('t', key, 'col'[, 'keyCol'])`, `cell('t', row, 'col')`, `rows('t'[, 'col', value])`,
  `sum('t', 'col'[, 'keyCol', key])` over `SimContext.Data` (§3.8); cells that parse are numbers, else texts; no row / an
  empty cell = missing; a constant table / column name is checked when the model is built.

### 3.19 Optimisation core — pluggable algorithms (`Optimization/`, namespace `SimuLean.Optimization`)

Engine-pure (no Unity, no external library), so optimisation runs anywhere the engine runs and is tested standalone
(`optimizationtests`).
- **`SearchSpace`** of **`DecisionVariable`s** — `Integer` / `Real` in [min, max] (one, or a vector of `Length`),
  `Choice` of `Options`, `Permutation` of n items, `Binary` vector of n decisions — and **`Candidate`** (the values of
  each variable: `Number`, `Values`, `Vector`, `Option`; `Text` / `SetText` read and write any kind as text — numbers,
  the option, `true` / `false`, values separated by commas; `Key` identifies the point).
- **`IObjective.Evaluate(candidate)` → `Evaluation`** (`Fitness`, maximised; `Metrics` (means); `MetricValues` (each
  replication); `Error`; `Evaluation.Failed(error)` = −∞). Called from several
  threads at once.
- **`IOptimizationAlgorithm`** (`Name`, `Run(OptimizationRun)`) — the plug-in point: a genetic algorithm, a library
  adapter, an own metaheuristic. The **`OptimizationRun`** gives every algorithm the same services: a seeded random
  engine (same seed + same objective = same search, also in parallel), `Evaluate(batch)` in parallel up to
  `Parallelism` with a **cache** (a point seen before is not simulated again), the budget (`MaxEvaluations`,
  `MaxSeconds`), cancellation, the best candidate and the history (`EndIteration` after each iteration, progress
  callback). Helpers: `RandomCandidate`, `RandomInt`, `RandomPermutation`. A NaN
  fitness counts as −∞ (with an error note), never as the best; the time limit and a cancellation also stop a batch
  being evaluated (its remaining candidates stay null). The `Optimizer` component runs the search and the re-run of
  the best in the background (no re-run when every candidate failed), and disabling it cancels the search (as the
  `Experimenter`'s headless runs).
- **`OptimizationEngine.Run(space, objective, algorithm, settings, progress, cancellation)` → `OptimizationResult`**
  (`Algorithm`, `Best` / `BestEvaluation`, `Evaluations`, `Seconds`, `StopReason`: `finished` / `evaluation budget` /
  `time limit` / `cancelled`, `History`, and `Candidates`: every simulated candidate, `EvaluatedCandidate`, copied to a
  result set by `CopyCandidatesTo`). `Candidate.Text` is exact ("R"; `DisplayText` rounds for display).
- Built-in algorithms: **`RandomSearch`** (baseline) and **`GeneticOptimizer`** (mixed spaces: tournament selection,
  elitism, uniform / blend crossover and step / Gaussian mutation for numbers — per component for vectors —, a
  parent's option and a different option for choices, order crossover and swap for permutations, uniform crossover
  and bit flips for binary vectors). A library (GeneticSharp or another metaheuristics
  library) is connected the same way: one class implementing `IOptimizationAlgorithm`.

**The model as a problem.** Everything is expressed on the `SimulationConfig`, so the same optimisation runs
for a scene, a headless model or a model written in code:
- **Every property is a parameter.** **`ModelParameterCatalog`** (`ForElement`, `ForTeam`,
  `ForDowntime`, `For(model, scope, owner)`) walks **every key** of an element / downtime config and every setting
  of a `ResourceTeamConfig` (its typed settings by path, the members' records left out; plus `members` and the members'
  `LoadTime` / `UnloadTime`) — so the scene exports every
  option also at its default (`reserveExit`, `offerOrder` / `offerScan`, the Queue's release,
  grouping and max-wait keys, a use's `acquire` / `maxWait` / `onTimeout`, `processBy.acquireBeforeEntry` on the
  Multi-Server, `setup.when = None` with its label / first / keep, a team's `units` / `jobOnMemberStop`), and a study varies it
  from there; results are unchanged — and offers it as a
  **`ParameterInfo`** (key, label, `VariableKind`, length, options, `ValueEncoding`, current value, suggested range):
  - **by the value's type**, with nothing to declare: `int` → integer, `double` → real, `bool` → yes / no, `int[]` /
    `double[]` → vector;
  - **by the `ParameterSchema`** for texts and lists — **for elements generated by reflection from the settings classes
    (the catalog by reflection):** each setting's `[Setting]` attribute (`SettingAttribute`: `Role` —
    `SettingRole.Structural`, `Choice` with `Options` (an enum) or `Choices`, `ChoiceFrom` with `OptionsFrom`, `SamplerType`,
    `Sampler`, `NumberList`, `Properties`, `File`, `Expression`; an enum-typed field is a choice by itself, a number
    array (`double[]` / `int[]`) a vector —,
    `Label`, `Units`, `Description`, `Min` / `Max`, `Integer`) is its one declaration; `ParameterSchema.Of(settingsType)`
    walks `SettingsPaths.DeclaredMembers` (a list of records once as `tasks[].field`; `Find` looks an indexed path up by
    that pattern) and lets the type refine it (`ElementSettings.Refine`: the Multi-Server's `processBy.onTimeout` with
    Reroute). The handlers' `Schema` and `IElementHandler.Schema` are gone; `ParameterSchema.Common` is the schema of the
    base `ElementSettings`. `ParameterSchema.Undecided(type)` lists a declared text / list setting with no role, and
    `optimizationtests` checks it is empty for every element type — whatever a model sets. Covered there (routing / admission types, the distribution of the flat sampler keys,
    `processBy.release`, transport times, user properties, structural references; a station's setup, 2026-11-04: `setup.when`
    a choice, `setup.time` a sampler, `setup.setupFirst` / `setup.keepForProcess` / `setup.setupBy.preemptable` bools, `setup.setupBy.preemption` a choice,
    `setup.setupBy.priority` an expression, `setup.label` / `setup.setupBy.team` / `setup.setupBy.requirements` structural) and the shared `Team` / `Downtime`
    schemas: `Choice` (a text that is one of options — enums by `Choice<TEnum>`; `ChoiceFrom(key, optionsKey)`:
    the options are the names of another key's `name=…` records, e.g. a milk run's named request orders), `Sampler` (each number of a
    downtime's `Type~p1~p2` text: `key.field`, the number's distribution field, e.g. `timeToFailure.exponentialMean`; an
    element's distribution is a `SamplerSettings` group, so its fields are keys of their own, `setup.time.uniformMax`, and
    its `strategyType` a choice), `NumberList` (`"a|b|c"`),
    `Properties` (user property records: each number / number array `properties.name`), `File` (a data file — a
    schedule source's `fileName`: varied by a study with file names as levels, not offered to the
    numeric optimisers), `Expression` (an expression that is a number parameter while it is a constant —
    the resource uses' `processBy.priority` / `transportBy.priority` / `setup.setupBy.priority`, a downtime's `repairBy.priority`; a level is written as
    the expression's text; one that reads the item is a **text setting**, below), `Structural` (names, references,
    tables, other expressions, coordinates: not a parameter);
    `SamplerSettings` declares a flat sampler (its `field.strategyType` choice, `field.expression` structural, the
    labels of the numbers; `delayStrategy` and `interArrivalDistribution` in `Common`);
    `Describe(key, label, min, max)` only refines the label / suggested range of any key; schemas merge with `With`
    (the handler's wins over `Common`). The integer fields of a distribution (Binomial n, degrees of freedom, discrete
    bounds) are integer variables.
  - **what the current settings use**: an element's distribution keys only for its chosen
    distribution, labelled by the setting's label ("Service time" for `delayStrategy.…`, "Inter-arrival time — Uniform min" for
    `interArrivalDistribution.…`, "Setup time — Uniform max"); a factor on the distribution itself
    (`transportBy.loadTime`) stands for its number when it has one; a team's travel properties only for its travel mode (`speed` / `traffic` for *Network*,
    `axisSpeed` / `axisAccel` / `axesSequential` for *Axes*…). **Team keys are setting paths** (the one key
    vocabulary): the `ResourceTeamConfig` field names with a lower-case first letter (`team:T.speed`,
    `homeNode`, `jobOnMemberStop`, `returnHome`, `axisSpeed`), plus `members`, `loadTime` / `unloadTime` (the members');
    `ParameterSchema.Team` is generated from the class's `[Setting]` attributes (`ParameterSchema.Of`). The factor / metric scope of a path is parsed from the enums
    `ParameterScope` (`element`, `team`, `downtime`, `reaction`, `data`) and `MetricScope` (`Study.TryScope`,
    `ScopeNames` for the error text), not from string switches. `ConfigParameter.Of` resolves against every key
    (`ForAll`), so a saved parameter keeps working after its owner's settings change. **A sampler key by its own name**
: `Rack.transportBy.loadTime` — the config key `simulean_params` lists — stands for
    `transportBy.loadTime.constantValue` when its spec has one number (`Constant~8`); with more numbers the error names the
    parts (`transportBy.travelTime.uniformMin`, `.uniformMax`), and
    a transport load / unload key the element does not have (its carriers' own times) points to the team's
    `loadTime` / `unloadTime` or `transportBy.overrideLoadTimes`; another unknown key suggests the nearest.
  **`Undecided(model)`** lists the keys neither rule decides; `optimizationtests` (every reference model) and the
  EditMode `ParameterCatalogTests` (every library element type with its defaults, each downtime kind, a team, user
  properties, every sample scene) require it to be empty — **a new element type, key or property must be decided**
  (offered or declared) to pass the gate. `ScheduleRows` gives a source's table length for sequence / per-row
  decisions. (Replaced the handlers' hand-written `Optimizable` lists.)
- **`ModelParameter`**s bind a decision variable to the config and apply a candidate to a copy: **`ConfigParameter`**
  (`Of(model, scope, owner, key, min?, max?, name?)`: null bounds = the suggested range, the default name is
  `owner.key`; any catalog parameter of an element, team or downtime, written
  back the way the config stores it — int / double / arrays, bool, the option's text, the number inside its sampler
  spec, `a|b|c`, a list of texts, a user property record, the team's member count — extra members copy the last one),
  **`OptionsParameter`** (a `ConfigParameter` searched over a list of values only — a `Choice` of the values
  as given, each written through `ConfigParameter.ApplyText`; `Study.OptionsOf`, the optimiser's `a|b|c`; a numeric key
  given options is never searched between them),
  `ScheduleSequenceParameter` (the jobs of a schedule table in the candidate's order; the time slots — column `Time`
  by default — stay in place), `ScheduleBinaryParameter` (one 0 / 1 per row, written to a column the model reads);
  the `Study` parameters `TeamPropertyMembersParameter`, `SourceTableParameter` and `DataTableParameter`
  (`Optimization/Study.cs`, §3.5) are `ModelParameter`s too;
  **`TextSettingParameter`** (a declared text setting varied by its texts: a `Choice` of the texts as given, each written
  as is through the owner's bag; the run's build checks it). **Text settings:** `ModelParameterCatalog.IsTextSetting(model,
  scope, owner, key)` — a `string` setting whose schema rule is `Expression` (a resource use's `processBy.priority`) or a
  `ChoiceFrom` (a team's / milk run's `requestOrder`) of the owner or one of its groups — never a `Structural` key (names,
  references: no study or optimiser varies them), a record's field (`actions[0].value`), a fixed-option choice or a file. The catalog lists them as a **text kind** (`ValueEncoding.Text`,
  `ParameterInfo.IsText`; `Kind` stays `Choice` with no options): an expression that reads the item, an order written in
  place (a `ChoiceFrom` whose value is no record's name), and — unset — those of a group the owner has (a station's
  `processBy.priority` once it has a process team) and a team's own; with the current text, **no range and no options**:
  the texts to try are always the user's. `ParameterInfo.Variable` and `ConfigParameter.Of` refuse one
  (`TextSettingParameter.NeedsTexts`: "give the texts to try"), so no numeric search varies it; a constant priority and a
  named order's name keep their number / choice entry. `TextSettingParameter.Applies` (a text setting the catalog lists
  as text, or not at all) decides it for every builder: `Study.Ranged` (also when a level is not a value the number /
  choice entry accepts: a constant priority given `item.prio`), `ParameterSpec` (§5.1) and the Experimenter (§4.4).
  Any other key keeps the "has no parameter" error and its did-you-mean; `Undecided` and the "every key decided" tests
  are unchanged (the text kind is listed only by the catalog's own lists). A study factor, a scenario and an
  optimiser's options `a|b` (`Study.OptionsOf`) name them with their texts — in JSON when a text holds `|` or `;`
  (`{"Station.processBy.priority": "table('T', item.k, 'p')", "team:Ops.requestOrder": "|priority desc"}` — an order
  written in place is `condition|order`). `simulean_params` / `simulean_types` answer them under `textFactors`
  (`{key: {kind: "text", now}}`).
- **`ModelMetric`** (name, element / team / list, statistic) read by **`MetricCatalog`** from the run's `RunResults`
  (§3.11; the lists are `ResultMetrics`): element `input`, `output`,
  `content` (at the end) / `content_avg` / `content_max`, `stay_avg` / `stay_std` / `stay_min` / `stay_max` /
  `stay_count`, `utilization`, `stops`, `last_entry` / `last_exit`, `state:NAME`,
  `member:NAME` (any public number of the engine element: a property or a method without parameters, e.g.
  `member:GetMeanServiceTime`, `member:CompletedTaskCount` — what the removed property registry read by reflection),
  `property:NAME` (`ResultMetrics.PropertyPrefix`: the element's user property at the end of the
  run, read from the run model like `member:` — a number; `Study.Check` refuses a property the element does not have at the
  build and no reaction writes, with a did-you-mean, and a text property); team `members`, `utilization`, `state:NAME` (members' mean; 0 if never entered),
  `served`, `tasks`, `request_wait_avg` / `std`, `requests_waiting_avg`; list `pushed`, `pulled`, `content` / `content_avg` /
  `max`, `wait_avg` / `std`, `pending_pulls_avg`, `field:F:…`; model (`MetricScope.Model`, no target) `end_time`,
  `makespan`, `deadlocked`, `deadlock_time`, `stalled`, `stall_time`, `empty_time`, `finish_time`, `stopped`,
  `no_progress_time` (a time never reached reads NaN, so the fitness is −∞); reaction (`MetricScope.Reaction`, §3.21)
  `fired`, `not_met`; **downtime** (`MetricScope.Downtime`, `downtime:Id.stat`) `stops`, `downtime_total`,
  `downtime_scheduled` and,
  with a repair crew (§3.12), `repairs`, `repair_waits`, `repair_wait_avg`, `repair_time_avg` (`MetricCatalog.DowntimeStatistics`) — the
  config id the catalog's `downtime:Id.key` factors use; a downtime on several targets (elements, a team's members) has
  one generator each, named `Id@target` (`downtime:machineFail@m1.repairs`); the bare id reads all its targets together
  (`stops`, `downtime_total`, `downtime_scheduled`, `repairs`, `repair_waits` summed; `repair_wait_avg` over every crew arrived — each
  target's mean weighted by its `repair_waits` — and `repair_time_avg` over every repair completed — weighted by its
  `repairs`: a repair open at the end has its wait in the target's mean but not its time; weighting the wait by `repairs`
  was wrong then, NaN with one crew arrived and none finished), and a repair statistic of a downtime without a crew is an error saying so. The preemption statistics
  are read through the team (`team:T.preemptions`, `interruptions`) and the station (`M.interruptions`,
  `interrupted_time`, `interrupted_time_avg` — every element that uses resources for its work, summed over its uses
  (process, setup crew, per-task uses), including the
  assembly station (its tasks), the erection dock, the stacker and the sequencing station; a `HoldFor` job counts the
  time its own time stood without a member, a HELD job the time without its holder); where no use of the team has a preemption mode they are absent from the
  results and read **0** (nothing was interrupted; a study may switch the mode between scenarios). The same for an
  element's `timeouts` / `timeouts:<kind>` without a max wait (a study may vary `maxWait` to 0). A `state:NAME`
  of an element or a team is checked against the known states (a misspelt team state read 0). No `resource:` scope:
  a member's statistics stay read through its team. `SimulationObjective.Run(candidate, seed, inspect)` hands `inspect`
  the run's `RunResults`.
- **`SimulationObjective : IObjective`** — clones the model per evaluation (`SimulationConfig.Clone()`, a deep copy by
  reflection, `ConfigCloner`: no field can be forgotten), applies the parameters, runs `Replications` headless runs
  with the seeds `BaseSeed`, +1, … (the same for every candidate: common random numbers), averages the metrics and
  evaluates the **fitness expression** (the common syntax, §3.18; metric names and the names of the numeric
  parameters are its variables, e.g. `throughput - 120 * operators` with a team-size parameter `operators`). A model error is an evaluation error naming its cause.
  `RunTime` (0 = the config's `MaxSimulationTime`), `Replicate(candidate, runs)` (every replication with its
  `RunResults`, used for the Optimizer's best), `SeriesInterval` / `RecordTimeline` (inspected runs only),
  `BuildSpace()`, `ConfigFor(candidate)`. A metric and a parameter cannot share a name; a fitness that names an unknown
  variable fails at construction.

### 3.20 Naval library — block breakdown (`Naval/`, namespace `SimuLean.Naval`)

The shipbuilding module. **`BlockBreakdown`** is a ship's product structure, one row per
**sub-block**: `block`, `sub` (required), `kind`, `turn`, `length` / `breadth` / `depth` / `weight`,
any `t_<stage>` work content, the block-level `erect_seq`, `erect_after` (predecessors, separated by spaces / commas /
semicolons / `|`) and `hull_x / y / z` (written once per block or repeated equal); other columns are kept as labels.
- Read by **`Load(source, databasePath)`**: a table (default `block_breakdown`) or a `SELECT` / `WITH` query of the
  project database (§3.8, decision of 2026-10-08:
  SQL is the source of data) or a `.csv` / `.data` file; `FromTable` / `FromRows`; **`Save(db, table)`** writes it back
  (TEXT columns; the Unity import of Excel / CSV goes through it).
- **`Validate()`**: errors (duplicated sub-blocks, non-numeric cells, block-level values differing between rows,
  unknown / self / cyclic predecessors, an `erect_seq` given twice or missing, a sequence that breaks the precedence)
  and warnings. **`ErectionOrder()`**: by `erect_seq`, else precedence levels (**`ErectionLevels()`**: 0 = no
  predecessor), else block number.
- **`Generate(part, ReleaseOptions)`** — the tables a Schedule source reads (`Name`, `Time`, `Q` + labels), in erection
  order, released every `BlockInterval` from `Start`: `SubBlocks` (one kit per sub-block, every column), `Blocks` (one
  order per block: `block`, **`nsub`** — the component count of a block-assembly Combiner with `updateRequirements` —,
  `weight`, `erect_seq`, `erect_after`, `hull_*`, `level`), `Erection` (one token per block); sub-block kits
  `SubBlockLead` before their block. A source reads a part through `TableSources`
  (`naval:<part>[;interval=h][;start=h][;lead=h]:<source>`, parts `subblocks` / `blocks` / `erection` and aliases, built
  by `TableSources.NavalSpec`), so the three parts always agree. Unity: `UnityBlockBreakdown` (§4.6.1, tool category `Data`). Tests: standalone `navaltests`, EditMode
  `BlockBreakdownTests`.

**`ErectionDock`** (`Naval/ErectionDock.cs`; handler `ErectionDockHandler` in
`Naval/ErectionDockHandler.cs`, wrapper
`UnityErectionDock`, library module **Shipbuilding**, presets *Slipway* / *Building Dock*):
- `IWorkStation`, `IResourceUser`, `IKeepsItems`, `IInterruptibleStation` (a front's crew can be taken by a
  request of higher priority or handed over at a member's stop — the front waits, WAITING_FOR_RESOURCE, and its erection
  goes on (Resume) or starts again (Restart), §3.17 *Preemption*); `Capacity` (`capacity`) erection fronts (one `ServerProcess` each;
  per-front results `<dock>/front0…` when more than one); the erection time is its `delayStrategy` sampler (an expression reads the block's labels).
- **Admission** (`CheckAvailability` and `Receive`, so every link type agrees): a free front and an erectable block —
  `Sequence`: its `erect_seq` = blocks started + 1; `Precedence`: every block of its `erect_after` text (`"4 5"`) erected.
  A refused block stays upstream (a yard releasing any accepted item / a list lets others pass); the dock calls
  `NotifyInput` after every erection.
- An erected block **stays** (the hull; its resources are released at once, `processBy.holdFor` limits them further).
  With `ExpectedBlocks`, the complete hull leaves as one **ship** item (the blocks as sub-items,
  `CountSubItemsOnExit`: each counted as leaving; its visual from `GenerateItem`); kept BLOCKED while the output refuses
  it; then the next hull starts. Labels and the ship name are parameters (`blockLabel`, `sequenceLabel`,
  `predecessorsLabel`, `shipName`, `erectionRule` (`Sequence` / `Precedence`), `expectedBlocks`, `capacity`).
- Reference models `erection_sequence` (out-of-order arrivals, crane for its lift only, ship launched to a sink that
  keeps it) and `erection_precedence` (two fronts, a crew of two per erection, the hull kept: makespan = last erection).

**Presets** — `Editor/Naval/NavalPresets.cs`, *GameObject ▸ SimuLean ▸ Shipbuilding*: the shop stations
on existing elements (no new engine type): *Steel Stockyard* (`UnityStorageRack`, floor storage), *Profile Rack*,
*Sub-Block Yard*, *Block Yard* (Queue, `AnyAccepted`), *Plate Cutting*, *Sub-Assembly Cell* (fitters by `skill`),
*Turning Station*, *Outfitting Station*, *Blasting & Painting Hall* (MultiServer; the painters held 6 h by `holdFor`),
*Sub-Block Welding* (Combiner, routing on `item.turn`), *Block Assembly Bay* (Combiner, SingleLabel `block`,
`updateRequirements` from `nsub`), *Slipway* / *Building Dock*, *Launch* (Sink, keeps the ship). The times are
expressions with a default (`has(t_sba) ? t_sba : 6`), so a breakdown column drives them. Two composites place connected
elements under one parent: *Panel Line* (4 stations in line) and *Block Process* — the flow of the reference model
**`naval_block_process`** (`ReferenceModels.NavalBlockProcess`, on the generic `ReferenceModels.SampleHull()`) with its
teams and a `UnityBlockBreakdown` on the project database's `sample_hull` table (written from the sample hull only when
missing — by the preset, and by `SampleHullOnOpen` when a scene that reads it is opened, since the database is not in
the repository). Teams (*Shipbuilding ▸ Teams*; the *Hall Crane* is a Core team): *Goliath Crane*, *Level-Luffing Crane*, *SPMT Transporters*,
*Welding Gantry*, *Welding Robots*. The block workspace model stands in for the station models still to come. Tests: standalone `navaltests`
(the block process), EditMode `BlockBreakdownTests` (every preset visible with its icon, the composite runs headless, an opened scene gets the missing sample hull).

### 3.21 Signals and reactions (`Signals/`, namespace `SimuLean.Signals`)

"When an element signals, if a condition, do actions".
- **Signals** are the hook points elements already raise: `ItemEntered` / `ItemExited` (the link layer's entry / exit,
  `ElementStatsCollector` → `Element.RaiseItemEntered`), `ProcessEnded` (`Element.ProcessEnded`, raised by the Multi-Server,
  single-job station, Combiner and assembly station when they finish an item, before sending it on), `StateChanged`,
  `Stopped`, `Resumed`; and the clock's: `RunStarted`, `Every` (`period`, `firstTime`), `AtTime` (`atTime`) — a reaction's own
  `Tick` events, scheduled by `Reaction.Start` (`StartModel`), with no element (`SignalKinds.IsTime` / `HasItem` /
  `HasState`). A signal is a `readonly struct SignalArgs` (kind, element, item, from / to state, time) passed by reference
  to an `ISignalSink`.
- **`SimContext.Signals`** (`SignalHub`) is the model's signal table: subscribers register once, when the model is built,
  for a signal of an element; the hub hooks that element's C# event only then (one `Channel` per element and signal),
  so a model without subscribers pays nothing (the reference models' event counts and times are unchanged). Delivery
  (`SignalDelivery`): **`NextEvent`** (default) — a pooled zero-delay `SimClock` event after the one
  that raised it (deterministic in the heap's order; the subscriber never runs inside another element's step) — or
  **`Immediate`** (inside the raising step; sees the state before anything else moves). **Order** (defined 2026-10-12): the subscribers of one signal run in the order they subscribed — the reactions' build order (in a
  scene, their order in the Hierarchy, §3.5) —: the
  immediate ones inside the step, the next-event ones as zero-delay events, which `SimClock` runs in (time,
  insertion) order. A *Next Event* stop at `ProcessEnded` arrives after the station took its next item; *Immediate*
  stops it first. `Clear()` unhooks everything when a model is built again on the same clock.
- **`Reaction`** (an `ISignalSink`): its sources, signal, optional state filter (state changes to that state; stops /
  resumes in that state), optional condition (an expression, §3.18, over the signal's item and element; data tables,
  other elements, and random functions from the reaction's own stream `reaction:<name>/expr`, so it does not shift the
  element's draws) and its actions in order (`ReactionAction`, `ReactionActionKind`): **SetItemLabel** (item signals
  only), **SetElementProperty** (`Element.Properties`; routing and admission read it with `get('E', 'p')` /
  `element.p`), **Stop** (a `StopRequest`: state, mode; for a duration — a resume event — or until a resume),
  **Resume** (ends the stops *any* reaction started on the element **in its group** — kept with their group in
  `SignalHub.ReactionStops` —, never a downtime's), **PushToList** (a value to a model list), **Release** (2026-10-26; 2026-11-02 `ItemsQueue.ReleaseItems(n)`, the value =
  n, empty = 1; the target, or the element that signalled, must be a Queue released on command (`Gated`), checked at build;
  orders every period, kanban one per item that leaves — it acted on the former Gate Queue element), **Retry**
  (`Element.WakeInput()` of the target or the signalling element — its waiting senders offer again,
  for an admission over something other than a property), **SetItemLook** (item signals only:
  `Item.SetLook(ItemLook)` — a colour (`LookColor`: `#RRGGBB`, `#RRGGBBAA`, `#RGB` or a name, parsed at build) and / or a
  scale factor of the visual, settings `actions[i].color` / `actions[i].scale`, both Structural; in the scene
  (`UnityReaction.ActionSpec`) a fully transparent colour (the default) keeps the item's own, any other is set (no separate
  switch). **View-only:** the look
  is kept on `Item.Look`, outside `Properties` (so no admission wake-up, no label, no statistic), schedules nothing and
  draws nothing; `Item.LookChanged` (static, raised only when the item has a visual — never headless) is the Unity
  layer's hook: `ItemViews` applies it with `ItemLooks` — a `MaterialPropertyBlock` tint (`_BaseColor` / `_Color`) of the
  visual's renderers except the visuals of its sub-items and label texts, and its own scale times the factor — and again
  in `ItemViews.Bind` on a new visual. A model's results are identical with or without it (`signaltests`). A prefab /
  model change is not offered: an engine setting cannot name a Unity prefab without a registry). **Admission wake-up (1e):** after the build,
  `SimContext.WatchAdmissions` maps each element property name an admission condition (`ConditionStrategy`) reads —
  `Expression.ElementPropertiesRead`: `element.p`, `origin.p`, `get(…, 'p')`; a computed name = every name — to those
  elements, and hooks every element's `PropertyBag.Changed` (null for items: one null check per write); a write of a
  watched name calls `WakeInput` on them: one zero-delay event at the current time per element (deduplicated), after the
  writing event, which asks upstream (`NotifyInput`) while the element has room — as a freed place does. Exact and cheap:
  unwatched writes cost a dictionary miss, and nothing polls. A model whose admissions read no property is unchanged. **Groups**: `ReactionAction.Group` (empty = default) tags a stop; a resume ends only its group's, so independent
  controls of one element (orders gating it, an incident) do not end each other's stops. **Only if**:
  `ReactionAction.Condition`, an expression checked when the action's turn comes (after the actions before it). Stop / Resume also act on **members of a resource team**
  (`ReactionAction.Team`, `Count`, 0 = all): stop takes the last members still working, resume calls back
  that group stopped in that order (`SignalHub.StoppedMembers`, member + group; a stop takes members no group stopped); a resource honours the stop like a downtime's
  (`Resource.IsAvailable`; *After Current* finishes the task first). A value is a plain number or an expression (`ReactionValue`).
  Counters for the views: `Fired`, `Skipped` (condition false), `LastFired`, `LastItem`, `LastSource`; `Reset()` at
  `StartModel` (also `IWarmupStatistics`: the counts restart at the warm-up). A failing action throws `ReactionException`
  naming the reaction, the signal and the time (the run stops). **Results:** `ResultScope.Reaction` (`fired`, `not_met`,
  `ResultMetrics.Reaction`; the exports' *Reactions* sheet), metrics `reaction:Name.fired` (`MetricScope.Reaction`).
- **Config and build** (one build path): `SimulationConfig.Reactions` — `ReactionConfig` (`Id`, `Sources` element ids,
  and typed settings, `ReactionSettings`, read and written by path — the paths of the component's
  fields: `reactionEnabled`, `signal` (`SignalKind`), `state`, `condition`, `timing` (`SignalDelivery`), clock signals'
  `period` / `firstTime` / `atTime`, and the records `actions[i]` (`ReactionActionSettings`, one record for every kind:
  `kind`, `target`, `name`, `value` — a plain number or an expression's text, the builder reads a number as a number —,
  `onlyIf`, `state`, `mode`, `duration`, `team` / `count`, `group`); an unknown path or option fails when written;
  `AddAction` appends a record). A new action kind (e.g. Set Item Look) is a `ReactionActionKind` value with its case in
  the builder and in `Reaction`, and a field on the record for anything no kind has yet. `ReactionBuilder` builds each reaction after the elements and downtimes (pass 5 of `HeadlessModelFactory`,
  `ReactionsById`) and fails the build naming the reaction and the field (unknown element / list / signal / action, an
  expression that does not parse, an item label on a signal without an item, a negative duration, two reactions of one
  name).
- **Parameters**: `ParameterScope.Reaction`, `ParameterSchema.Reaction` — `reactionEnabled`, the clock's
  `period` / `firstTime` / `atTime` and each action's `actions[i].value` (while a number) / `actions[i].duration` /
  `actions[i].count` are catalog parameters (the schema of `ReactionSettings`, declared once as `actions[].value`…); signal, actions, modes, names, expressions are the
  reaction's design (structural). A study: `reaction:Name.reactionEnabled=true|false`, `reaction:Name.actions[0].value=…`.
- Tests: standalone `signaltests` (each action, delivery, state filter, determinism, build / run errors, catalog and
  studies, no subscribers).
- **`WaitTimedOut`**: an element's wait reached its max wait (§3.17 *Max-wait timers*), whatever its action —
  the item if any (`HasItem`; an item label may be set on it), and in the reaction's expressions the root **`signal`**
  (`Expression.Roots`): `signal.kind` (`resource`, `queue`, `list_pull`, as the metrics spell it), `signal.action`, `signal.waited`, and for every signal
  `signal.time` (`SignalArgs.WaitKind` / `WaitAction` / `Waited`). Raised before the action (an Immediate reaction sees the
  item still in the queue).

### 3.22 Sequencing station — JIS kitting into carriers (`SimElements/SequencingStation.cs`, Manufacturing pack)

`SequencingStation : Element, IResourceUser, IPortedElement, IElementMetrics` fills **carriers** (wagons, totes) with one
**piece** per call-off **order**, in the order sequence (just-in-sequence supply of a line).
- **Inputs.** Main input: empty carriers, one at a time (`CheckAvailability` = no carrier; slots = numeric carrier label
  `CapacityLabel`, default `capacity`, else `CarrierCapacity`). Port **`orders`** (`SequencingOrderPort`): orders wait FIFO
  (= vehicle sequence; capacity `orderCapacity`, −1 unbounded). Port **`pieces`** (`SequencingPiecePort`): with capacity 0
  (default) it takes pieces **on demand** — only while a slot needs one and only one whose key (label `PieceField`, default
  `sku`; empty / `type` = the item type) equals the order's reference (label `ReferenceLabel`, default `reference`). The
  filter is the port's `CheckAvailability`, so the same port works with a **list pull** (`ListLink`: the list's query + the
  filter; setting a demand calls `NotifyInput`, which re-registers the pull — no change to the list layer) or a plain link
  (a sequenced feed). Capacity > 0 / −1 makes it a stock buffer fed by links, drawn by reference. *Why not `SkuStockPort`:*
  it keys by item type and has no demand filter, so a list puller would pull any piece; the new port is small and keeps
  the list as the stock (one source of truth shared by stations, with the list's statistics).
- **Lot accumulation.** A carrier starts when it is present and at least `LotSize` orders wait (≤ 0 = the carrier
  capacity; "orders to group"); with `OldestOrderMaxWait` > 0 (key `oldestOrderMaxWait`) also once the oldest order has waited that long.
  `WaitForFullCarrier` (default) makes a slot wait for its order; off, the carrier closes when no order waits.
- **Operators** (`Resources`, the standard the `processBy` / `transportBy` settings process use). `OperatorScope` **Carrier** (default): one
  request per carrier, held from preparation to close (`EndOfProcess`) or until the carrier leaves (`ItemLeaves`);
  **Piece**: one request per piece (take → place). Each request carries `ResourceRequest.OldestItemTime` = the lot's
  oldest order time (Piece: the order's), so a team `RequestOrder` **`|oldestItemTime`** reproduces "the operator next
  serves the family whose accumulated lot has the oldest order" (§3.17). Skills etc. are the usual requirements.
- **Timeline** (all through `ScheduleWork`): `PrepareTime` (item = carrier) → per slot i: the next order; if it needs a
  piece (`Applies` expression over the order, else reference non-empty) the piece is taken — **WAITING_FOR_MATERIAL**
  until one arrives; a wait longer than zero is a **stock-out** (a list hand-over is immediate and is not one) — then
  `PickTime` + `ExtraTime` and `PlaceTime` (item = the piece, which has received the order's labels it lacks, e.g.
  `vehicle`, `callOffTime`); the piece becomes a sub-item of the carrier with label `slot` = i; otherwise `GapTime`
  (item = the order) leaves the slot empty → `CloseTime` (carrier) → the carrier gets `firstVehicle`, `lastVehicle`
  (order label `VehicleLabel`), `pieces`, `gaps`, `closeTime` and leaves through the output link (**BLOCKED** while
  refused). States: IDLE (no carrier, no orders), **WAITING** (one of them missing), WAITING_FOR_RESOURCE, PROCESSING,
  WAITING_FOR_MATERIAL, BLOCKED.
- **Statistics.** Carriers through the station's links. An order is *served* when its piece is placed or its gap ends;
  it then leaves its port through the port's output link (`PortOutput`, e.g. a sink) or, unwired, through the
  **`ConsumeLink`**; a placed piece leaves the piece port through the `ConsumeLink` (it travels on as a sub-item) — both
  ports balance, and an order's stay in its port = its wait in the station. Own counters (`IElementMetrics`, names in
  `ResultMetrics`): `carriers`, `pieces`, `gaps`, `stockouts` / `stockout_time` (one under way included),
  `order_wait_avg` / `_max` (call-off time label `OrderTimeLabel`, default `callOffTime`, else the arrival → served),
  `lot_wait_avg` (oldest order of the lot → carrier start), `orders_waiting_avg`; the ports appear as `<id>/orders`,
  `<id>/pieces` (§3.11).
- **Views.** Events `CarrierStarted`, `PieceTaken(…, slot, duration)`, `PiecePlaced`, `GapLeft`, `OrderServed`,
  `CarrierClosed`, `CarrierLeft`; `IVElement.LoadItem(carrier)`.
- **Headless.** `SequencingStationHandler` / build `SequencingStationBuilder` over `SequencingStationSettings` (`carrierCapacity`,
  `capacityLabel`, `lotSize`, `oldestOrderMaxWait`, `waitForFullCarrier`, `referenceLabel`, `pieceField`, `orderTimeLabel`,
  `vehicleLabel`, `applies`, `copyOrderLabels`, `operatorScope`, `orderCapacity`, `pieceStock`, and the samplers
  `prepareTime`, `pickTime`, `extraTime`, `placeTime`, `gapTime`, `closeTime` as distributions — absent = 0 — with
  streams keyed by the element name and the setting). Wiring: `PortInput` (port `orders` / `pieces`), `PullFromList` with a
  **named** `port` (`pieces`: the factory resolves a non-numeric `port` through `IPortedElement.GetPort`), `PortOutput`
  (port `orders`) — implemented in `HeadlessModelFactory` with this element.
- Tests: `StandaloneTests/sequencingtests` (hand-computed timeline, stock-out, buffer by link, blocked + `ItemLeaves`,
  max wait, oldest-order vs FIFO with a shared operator, operator per piece, expressions, factory + builder round trip);
  reference model `sequencing_station_lot` (two families, one operator, `|oldestItemTime`, a stock-out window — hand
  check in its doc comment); EditMode `SequencingStationSceneTests`.

### 3.23 Line supply — call-off point, line-side unpacker, paced line (`SimElements/CallOff.cs`, `LineSideUnpacker.cs` (on `Separator.cs`), `PacedLine.cs`; the call-off point and the paced line in the Manufacturing pack)

Generic elements for the just-in-sequence supply of an assembly line (reporting point → sequencing → delivery →
line-side unpacking → fitting); case models use them through their public API.

- **`CallOffFamily`** — one definition used by both the call-off point and the line stations (one code path): name,
  order type, quantity q (positions 1..q), applicability condition (expression on the vehicle, §3.18), reference
  label and **reference choice tables** (one per position, or one for all). For a vehicle it applies to, `Emit`
  creates q orders in position order (the vehicle's labels + `callOffTime`, `family`, `position`, the drawn reference)
  and sends them through its port; the reference of position k is also written on the vehicle as
  `<family>.p<k>.<referenceLabel>`. The tables draw from one stream per family: owner = the element, purpose =
  `<family>.ref` (§3.6). **Optional positions**: a table value **`-`** (`CallOffFamily.NoPart`, e.g.
  `C1:0.4|C2:0.3|-:0.3` for a cover fitted on 70 % of the vehicles) = no part for that position: the order is still
  emitted, without a reference label (a sequencing station leaves the slot empty, §3.22), the vehicle label is `-`, and
  `PartsFor(vehicle)` / `NeedsPart(vehicle, k)` tell a line station which positions it fits.
- **`DiscreteChoice`** (`Stochastics/Distributions`) — a choice among text values with weights, table
  `value:weight|…` (normalised; decimal comma accepted), inverse transform on one `NextDouble` per draw,
  `IRandomStreamUser`.
- **`CallOffPoint : Element, IPortedElement`** — pass-through reporting point: a vehicle arriving gets its sequence
  label (`vehicle`, when it has none), every family emits its orders (port = family name), then the vehicle goes on;
  refused, it is held (BLOCKED, one at a time). Model: source → call-off point → vehicle buffer → paced line, so the
  requisition time = buffer time + positions × takt.
- **`OutboundPort : Element`** — FIFO outbound port with its own output link (unwired = discarded, counted). Items
  handed over by the owner through a `DirectLink` (content exact) or created inside (`Dispatch`; a generator: no
  content, like a source). Used for empties and call-off orders.
- **`LineSideUnpacker : Separator`** (Manufacturing pack; a specialisation of the Core `Separator`, the inverse of
  the Combiner) — receives carriers (compound
  items) up to `Capacity`, releases the contents one by one (FIFO / LIFO of `subItems`) when downstream accepts (one
  piece prepared at a time; retried on `Unblock`), optional `PieceTime` per piece and `ChangeTime` per carrier
  (`ScheduleWork`; SETUP state; not before the run's first carrier unless `ChangeTimeFirst`, setting `changeTimeFirst`,
  the Separator's protected `ChangeBeforeFirst`); the emptied carrier leaves through port `empties` (else through the output after its
  pieces) and, with `EmptiesUseCapacity` (default), keeps its place until it has left the port. Optional copy of carrier
  labels onto the pieces. **Statistics:** as the Separator's (input = carriers + pieces, output = pieces + carriers,
  content = carriers + pieces still in them); counters `released`, `carriers_emptied`; **pieces held** (`PiecesHeld`, a
  time-weighted level of the pieces in the carriers + the ready one, carriers not counted — the line-side stock) as
  `pieces_avg` / `pieces_min` / `pieces_max`. The warm-up reset restarts the counters, the level and the empties port.
- **`PacedLine : Element, IPortedElement`** — N positions, takt sampler (an index every takt through `ScheduleWork`:
  breaks and shifts pause it; no index while stopped). A vehicle enters position 0 whenever it is free; at an index
  the last vehicle leaves (refused = BLOCKED), the others move on, an empty position 0 moving on counts an empty
  pitch. **Stations** (`LineStation`: name, position 1..N−1, family, `MatchLabel`, port capacity, optional call-off
  position) each own a **`LineStationPort`** (FIFO buffer with its own input link; output = `ConsumeLink`).
  **Starvation:** before indexing, every station whose position will receive a vehicle needing its part must hold the
  parts the vehicle needs (q, minus its no-part positions); otherwise the line is **STARVED** (new predefined `ElementState`), each missing station counts a line stop
  and its port shows STARVED until its parts are there (`line_stop_time` = port STARVED time); the line indexes as soon
  as the last part arrives (checked in a same-time event) and the next takt starts then. **Fitting:** parts leave the
  port through the consume link and become sub-items of the vehicle (or are dropped); a part whose match label (and,
  for q > 1, its `position`) or whose reference differs from the vehicle's is fitted and counted as a mismatch. The
  warm-up reset restarts the line's counters (`index_count`, `empty_pitches`, `line_stops`), the stations'
  (`line_stops`, `fitted`, `mismatches`) and the station / call-off ports' statistics.
- **Serialization:** `FlowBuilders` (builds), handlers `CallOffPointHandler`, `LineSideUnpackerHandler`, `SeparatorHandler`,
  `PacedLineHandler`; typed settings `CallOffPointSettings`, `SeparatorSettings`, `LineSideUnpackerSettings`,
  `PacedLineSettings`: families and stations are typed records — `families[i]` / `stations[i].family`
  (`CallOffFamilySettings`: `name`, `orderType`, `quantity`, `condition`, `referenceLabel`, `referenceTables[i].choices[j]` — `ReferenceTableSettings`: `reference`, `weight` (a catalog parameter); the inspector's text `R1:0.3|R2:0.7` is read by `ReferenceTableSettings.Parse` —) and
  `stations[i]` (`LineStationSettings`: `name`, `position`, `matchLabel`, `portCapacity` — optimisable, was the parallel
  list `stationCapacities` —, `callOffPosition`, `family`); `takt`, `processTime`, `pieceTime`, `changeTime`,
  `splitQuantity` (distributions); `mode`, `unpackOrder` (enums). The Unity wrappers write their settings into the
  element's (`Fill`).
  Ports: `PortInput` to `<station>`; `PortOutput` from `<family>`, `empties`, `<station>.callOff`.
- **Tests:** `StandaloneTests/linesupplytests` (hand-computed times), reference models `line_side_unpacker`, `separator_split`, `combiner_separator`,
  `paced_line_starvation` (starved time = 151 stops × 0.2), `call_off_buffer_line`; EditMode `LineSupplySceneTests`.

### 3.24 Logic viewer — the decision sites of a model (`Logic/ModelLogic.cs`, namespace `SimuLean.Logic`)

A read-only report of **every decision of a model**: where items go, in which order they are released,
what an element takes, what a job asks of a team and in which order a team serves, list orders and queries, reactions —
each with what decides it, what it reads and what it writes —, and the admission wake-up map. Built **in the engine from
the canonical model** (`ModelLogic.Of(SimulationConfig)`), so a scene (extracted) and a headless model show the same; it
parses expressions without an owner (nothing is collected by `ExpressionCheck`), builds nothing and schedules nothing —
results are unchanged (`logictests`: every reference model's `ConfigJson` is the same after it).

- **Declared on the setting.** What a setting decides is part of its one declaration (`SettingAttribute`, §3.19):
  `Logic` (a `LogicSite`: `Routing`, `Admission`, `Order`, `Dispatch`, `Resources`, `Placement`, `Replenishment`, `Wait`,
  `Labels`, `Reaction`; `None` = decides nothing; `Unset` = not declared), `Form` (a `LogicForm`: `Value`, `Expression`,
  `OrderKeys`, `Query`, `Label`, `Assignments`), `Context` (the `ExpressionContext` key its text is written in),
  `LogicDefault` (shown at its default too, with this text: a Queue's `offerOrder` FIFO, a routing's FirstAvailable, a
  team's "first come, first served" / "longest idle first"), `LogicDefaultValue` (the values that mean the default when
  written — the Unity extractors write every value: a max wait `0`, `KeepWaiting`, an admission `Default`, a priority `1`,
  a location policy `FirstAvailable`; compared as numbers or as text ignoring case, `LogicDefault` too when it is a value;
  the choices the builder parses itself — a routing's or an admission's `strategyType`, an `onTimeout` — are first read
  as the builder reads them, `Serialization/BuilderChoices.cs` and `WaitLimit.TryActionOf`, which the builder calls too:
  `strategyType = "1"` is ShortestQueue, `"shortestqueue"` FirstAvailable, `onTimeout = "giveup"` GiveUp; the
  sentences, the wake-up map and `ChoiceRules.RulesOf` read them the same way)
  and `LogicWhen` (shown — and read — only when it applies: `key=v1|v2` a sibling has one of the values, `key!=v1|v2` none
  of them, an empty value = not given, conditions separated by `;`: `parameterKey` only under `strategyType=Parameterized`,
  `scoreExpression` under BestScore, an admission's label / condition under its mode, `onTimeout` under `maxWait!=|0`,
  `timeoutTarget` under a max wait and a leaving action, `releaseAt` under `release=AtElement`, `groupLimit` under
  `groupBySetup`, `offerScan` under AnyAccepted, a Combiner's component labels under their pull mode, a milk run's
  dynamic settings under `tourMode=Dynamic` and its fixed ones under `Fixed`; `ModelLogic.Applies`; 2026-11-09 — a stale
  `parameterKey = routing_mode` was listed, and counted as a read, on every element). The report walks each element's / team's / downtime's settings
  (`SettingsPaths.Members`): a declared leaf becomes a `DecisionSite` (a list of records declared as a whole is shown as
  its records), an undeclared group or record list is walked into. **Coverage:** `ModelLogic.Undeclared(type)` lists the
  settings of a type that look like decisions (an enum, an expression / choice / sampler-type role, or a name such as
  `…Condition`, `…Order`, `…Priority`, `…Filter`, `…Query`) and declare no `Logic`; `logictests` fails on any over
  `ModelLogic.SettingsTypes()` (every element type's settings, `ResourceTeamConfig`, `ReactionSettings`,
  `DowntimeSettings` and the groups / records inside). A new decision setting is therefore described where it is
  declared, and a forgotten one fails the gate.
- **What decides, reads, writes.** A `DecisionSite` has `Site`, `Key` (the setting path, or `pull:List`, `push:List`,
  `signal`, `condition`, `actions[i]`, `fields.<name>`), `Value` (the value, the expression / query text; a named order
  as `name: query`; a record list as `field=value, …`), `IsDefault`, `Context`, `Reads` (`Expression.Reads()`: `item.x`
  — a bare name under the context's bare root —, `element.p`, `out.p`, `content('B')`, `get('M', 'p')`, `table('T')`,
  `now()`, `random`; a query's fields and order keys), `Writes` (a label record's `item.x`; a reaction action's item
  label, element property, state of an element / team members, list, released items, retried admission) and `ViewOnly`
  (Set Item Look: listed, never a write), `Problem` (a text that does not parse). Elements add their list pulls / pushes
  (`PullFromList` / `PushToList` connections) and drop a default routing with fewer than two outputs; teams list the uses
  that ask them (`UsedBy`, `ModelLogic.TeamUses`: every `…team` setting of the elements — process, its requirements' teams,
  transport, setup crew, assembly tasks — and of the downtimes' repair crews); lists their order, calculated fields and who pushes / pulls;
  reactions their signal (state filter, times, sources, timing), condition and actions; downtimes only their repair crew.
- **Decisions in plain language** (`Logic/LogicSentences.cs`). `ModelLogic.Of` groups each owner's sites into
  `LogicOwner.Decisions`: the sites that decide one thing together — a group's settings (`outputStrategy.*`, `processBy.*`,
  `tasks[i].*`), an element's top-level ones of one site (`offerOrder`, `offerSort`, `offerRule`, `gated`…), each label
  record list, list field, pull and push alone, a reaction whole. A `Decision` is a sentence — `Verb` (`Route`, `Push`,
  `Admit`, `Pull`, `Release order`, `Order`, `Field`, `Rule` when a choice rule decides it, `Wait`, `Resources`,
  `Dispatch`, `Place`, `Reorder`, `Labels`, `When` for a reaction) then `Text`: `Route best score: … · if best is full:
  skip`, `Admit when element.open == 1 · woken by Flex Cell Shift`, `Release order prio desc, age`, `Rule
  ShortestQueueRule (FreeWeight = 0.5) picks the next element`, `Dispatch requests by priority desc · members longest
  idle first`, `When every 1800: set …` — with `IsDefault` (every site at its default), the union of its sites' `Reads` /
  `Writes`, `ViewOnly`, `Rule`, `Problem`, the raw `Settings` (`key = value`, the non-default ones) and `Links`: the model
  objects it names (`DecisionSite.Refs`: an element of `content('B')` / `get('M', …)`, a list pushed to or pulled from, a
  team, a reaction's targets, a timeout target; the reactions that wake an admission, `LogicOwner.WokenByReactions`).
  Ids written in values (a requirement's team, a timeout target, a repair crew) are given as names. A requester's priority
  (`processBy.priority`, `transportBy.priority`, a task's) is a decision whenever its team can tell the requests apart by it —
  the team's request order (or the named order it chooses) reads `priority`, or its requesters ask with different
  priorities — then every requester shows it, the default 1 included (`ModelLogic.RequestPriorities`). A
  requirement of another team (`processBy.requirements[1].team`) asks with its use's priority — a requirement has none of
  its own — so it is reported under the use's key (not as a spurious `requirements[1].priority = 1`). A
  station's **setup** is one decision `Setup …` (`SetupSettings` sites `when`, `label`, `setupFirst`, `time`,
  `matrix`, `keepForProcess` with its crew: "when item.type changes · time from matrix 'M' (pairs it lacks: …) · crew: team
  setters"); and when a use of a team has a preemption mode, every preemptable use of that team whose priority is lower
  says `preemptable by <owner>` (`ModelLogic.Preemptors`; "(if its priority is higher)" when a priority is an expression).
  Reaction
  actions are named by their path (`reaction 'Open' actions[0]`). `LogicOwner.HasOwnLogic`:
  a non-default decision, a wake-up or a note.
- **Run-time counters** (`Logic/DecisionCounters.cs`, `Logic/LogicLive.cs`). Read-only counters kept where
  each decision is made, never changing a result (no random draw, no event, no allocation per event beyond a counter or a
  last-value store): `Element.Decisions` (`DecisionCounters`, made at the element's first counted decision) — items handed
  over per output by its routing (`GeneralLink` when the link routes by the element's own strategy,
  `ResourceTransportLink` at delivery), its last choice and, for a BestScore / Rule routing, the scores of that choice
  (`BeginScores` / `Score` / `EndScores` in the strategies' `Best`, two fixed buffers swapped), the times a waiting
  routing held an item for its full best output (`HeldForBest`), the item of its last choice (`LastItem`, 2026-11-10:
  its labels say why), its admission's entries (`Entered`: one per item entered, counted at the link layer's entry
  `ElementStatsCollector.OnEntry` → `Element.CountAdmittedEntry`, not for the default admission) and its evaluations
  (`Checks` / `Refused`, `Element.ValidateInput`; each offer and each check by a sender's routing counts), its wake-ups
  (`Element.WakeInput`), a Queue's releases — the items its order chose after they waited (`ItemsQueue.Take`) and, apart, those that passed straight through on arrival (`PassedThrough`, `ItemsQueue.Receive`; `Left` is both) — and the last item released; a choice rule's `Decisions`,
  `Errors`, `LastScore` and `LastItem` (`RuleSiteBase`); a team's `Served`; a reaction's `Fired` / `Skipped` (its condition false).
  `ModelLogic.Live(report, model, only)` attaches them to a report built from the same config
  (`HeadlessModelFactory.Config`, the model it built last): `Decision.Live` (data: `routed` per output, `last`,
  `lastScores`, `lastSkipped` (the outputs the last scored choice did not score — full, refusing or stopped; also in
  the text as "; skipped A (full, refusing or stopped)"), `lastItem`, `heldForBest`, `entered`, `checks`, `refused`, `wakeUps`, `left`, `released`, `passedThrough`,
  `lastReleased`, `lastReleasedItem`, `served`, `fired`, `notMet`, `ruleCalls`, `ruleErrors`, `lastScore`, `lastScoreItem`;
  an item as `{id, name, labels}` with its first `LogicLive.LabelsShown` = 4 labels, read when the counters are read) and
  `Decision.LiveText` (`routed Cell A 50, Cell B 50, Flex Cell 20 · last → Cell B (10 vs -2, -1) for item 120 (P)
  {family=2, prio=3}`; an admission `entered 20 · refused 180 of 226 checks · woken 6`). `ToData` carries `live`; `Marks` append the text; `ModelLogic.Mark(diagnosis, config, run)`
  gives `simulean_diagnose` the counters of the run it diagnosed. The counters cover the whole run (not reset at the
  warm-up; a reaction's own `Fired` / `Skipped` are). `logictests` proves a run read every 100 time units has the same
  results (`RunResults`) as the run not read, and the reference baselines are unchanged.
- **Wake-ups and notes.** For each element whose admission is a Condition, `WakeUp` records each property its condition
  reads (`Expression.ElementPropertiesRead`, the same rule as `SimContext.WatchAdmissions`, 1e; a computed name = any
  property) and the reaction actions that write it; the owner's `WokenBy` says so. `Notes`: a property no reaction writes
  (no write wakes the condition), a condition over a model read (`content(…)`, `now()`…) with or without a `Retry` action
  targeting the element (without one the note says how it is woken: "it is checked again when 'X' frees a place; if what
  it reads changes elsewhere, add a Retry action targeting it" — not an error).
- **Readers.** `LogicReport.ToData(only)` (dictionaries and lists: `elements`, `teams`, `lists`, `reactions`, `downtimes`,
  `wakeUps`, `notes`; each owner's `decisions` as `Decision.ToData` — `site`, `say` (the sentence), and for a decision of
  its own `settings`, `reads`, `writes`, `viewOnly`, `rule`, `problem`; a default one only `site`, `say`, `default` —; an
  owner without decisions is left out, except a downtime — every downtime is listed with its `targets`, a call-away
  without a repair crew has no decision; `only` = one owner, an unknown name suggests) is the answer of `simulean_model
  section=logic` (§4.11). The editor window *Tools ▸ SimuLean ▸ Model Logic* (`ModelLogicWindow`, IMGUI, also the *Logic*
  button of the SimuLean window's Model tab) draws the same `LogicReport`: by default only the real decisions (not
  default), one line each — a chip coloured by site (rule decisions their own colour; light and dark theme colours) and
  the sentence, its raw settings and reads / writes in the tooltip —, the elements without a decision of their own in one
  dimmed line of links; *All* shows every decision with its raw settings and the wake-up map. Every name a decision links
  (`Decision.Links`) and the dimmed names are links: a click selects and pings the object and scrolls the window to it.
  A search, *Refresh*, *Select*; `VisibleLines()` gives the lines of the current view (tests). In Play it shows the running
  model's decisions (`UnitySimClock.Model.Config`) with their counters under each line (`ShowLive`, read every
  `LiveInterval` = 0.5 s from `Update`, never every frame); after Play the last run's counters stay until *Refresh*. The
  Play inspector panel shows the selected element's non-default decisions with their counters (`DecisionInspectorContributor`,
  built into `ElementInspectorPanel`'s contributors: a code contributor, read-only, the report made once per run). A window, not a scene
  component, so no `[SimuLeanTool]`. `ModelLogic.Mark` gives `Study.Diagnose` (`simulean_diagnose`) a `decisions` list on
  each element holding items (`LogicReport.Marks`: its own non-default routing / order / pull / wait decisions, the
  admission of each next element, as sentences, their notes).
- **Tests:** `StandaloneTests/logictests` (coverage, each kind of site, reactions, wake-ups and notes, the data form, the
  diagnosis marks, every reference model; `LogicView.cs`: each routing strategy / admission mode / wait / milk-run tour
  mode shows and reads only its settings, every `LogicWhen` names settings of its type, values written at their default
  are defaults, the sentences, links and default view of the `ChoiceLogicDemo` model); EditMode `ModelLogicTests` (the
  section on `Workshop_Advanced` and `ReactionsDemo`, the window's report, the window's default view of
  `ChoiceLogicDemo` without the noise lines) and `ChoiceLogicDemoTests` (the counters of a headless run in the report and
  the window); `logictests` also checks the counters of a run of the `ChoiceLogicDemo` model and that reading them changes
  nothing.
- **Item labels read that nothing sets (`Logic/ModelLogic.Labels.cs`).** A label value is
  an expression, so `type = A` reads the label `A`: every item got an empty label and only a run-time warning said so.
  `ModelLogic.UnsetLabelReads(model, report, owner)` flags each `item.X` a site reads when no writer can set `X`: the
  report's `Writes` (label assignments — create / entry / exit labels, a task's labels — and reactions' Set Item Label),
  the labels engine elements set by their own logic (`EngineLabels`: a pick face's `sku`, a call-off's `family` /
  `position` / `callOffTime`, a stacker's `stack*`, a sequencing station's `slot` / `pieces`…, an agenda's `appt_time`…,
  an erection dock's `ship` / `blocks`), the item built-ins, and any text of a setting that is not an expression (a
  schedule table's columns, a reference / type / sequence label: a configured name is never flagged). A model reading a
  file at run (a `SettingRole.File` setting) flags nothing. `Study.Check` adds them as warnings (`simulean_validate`);
  `simulean_set` of an expression field warns for its owner, `simulean_apply` once after its last edit. Test: bugfixtests
  *A label read that nothing sets* (also: no reference model is flagged), EditMode `AgentBulkCommandsTests.AnUnquotedLabelTextIsFlagged`.

### 3.25 Choice rules — one decision in C# (`Rules/`, namespace `SimuLean.Rules`)

The one script hook: a **choice rule** scores the candidates of one
decision site; the engine does the argmax, the ties, eligibility and waiting, exactly as for the declarative form the site
already has. Guide: `Assets/Docs/Choice_Rules.md`.

- **Contract** (`Rules/ChoiceContract.cs`): `IChoiceRule` and one interface per site — `IRoutingRule.Score(in ChoiceContext,
  in ElementView output)`, `IReleaseRule.Score(in ChoiceContext, in ItemView item)`, `IDispatchRule.Score(in ChoiceContext,
  in RequestView request)`; the highest score wins, ties keep the candidates' order, NaN stops the run. `ChoiceContext` is a
  `readonly struct` made per decision: `Site`, `Now`, `Owner`, `Element` (the decider), `Item` (routing), `Candidates`,
  reads by name `Content` / `Free` / `Get` / `Stat` / `Team` / `List` / `ElementNamed` (the expression functions' readers:
  `EntityScope.OfElement`, `ElementMetrics` / `TeamMetrics` / `ListMetrics`), and `Random` (`RuleRandom`). The views
  (`ElementView`, `ItemView`, `RequestView`) only read. `[ChoiceRule(Label, Why, Reads)]` describes a rule;
  `[RuleParameter(Label, Min, Max)]` marks its parameters. `ChoiceRuleException` (t=, owner, rule, message) wraps any
  exception of a rule, as `ExpressionException` does for expressions.
- **Random numbers:** `RuleRandom` draws from `clock.Context.CreateRandomStream(owner, "rule:<site>")`, created at the first
  draw (a rule that never draws takes no key), and records the draws **per decision** — per (item, output) for routing,
  per held item for release, per request for dispatch — replaying them when that candidate is scored again (a waiting item
  re-offered), until it leaves (`IRoutingMemory.Forget`, or the subject no longer among the candidates). Seeded runs are
  reproducible.
- **Sites** (`Rules/RuleSites.cs`, base `RuleSiteBase`: the instance, owner, stream, `Decisions` counter and error wrapping):
  `RuleRoutingStrategy` / `RuleWaitRoutingStrategy` (mirror `BestScoreRoutingStrategy` / `BestScoreWaitRoutingStrategy`,
  `whenBestFull` Skip / Wait; the Wait one is an `IRuleRouting`), `RuleItemOrder` (an `IItemOrder` beside `ItemOrder`: a
  Queue's `OfferSort` is now the interface), `RuleRequestOrder` (`ResourceDispatcher.RequestRule`, tried instead of
  `RequestOrder`: best first, ties in request order, a request that cannot be served yet does not block the others).
- **Settings:** routing `outputStrategy.strategyType = Rule` + `outputStrategy.rule`; release a Queue's `offerRule` (not with
  `offerSort`); dispatch a team's request order `rule:<Type>` (written in place or as a named order's query, so
  `requestOrder` stays a choice of named orders). The parameters are the owner's `rule` records (`ElementSettings.Rule`,
  `ResourceTeamConfig.Rule`: `PropertySettings`, `SettingRole.Properties`): catalog keys `<Element>.rule.<Field>` /
  `team:T.rule.<Field>` for numbers and yes / no (the catalog bags complete them from the rules' defaults,
  `ChoiceRules.Complete`, so every parameter is offered); a routing and a release rule of one Queue share a name. The rule
  type is `SettingRole.Rule`: a choice among the rules of the setting's site.
- **Registry** (`Rules/ChoiceRules.cs`): every `IChoiceRule` class of the loaded assemblies that reference the engine
  (cached; `Refresh`). `Resolve` (missing → did you mean, two of a name, wrong site, no parameterless constructor),
  `Create` (parameters from the records: unknown name with suggestions, kind, range; a record of the owner's other rule is
  skipped), `ContractProblems` (static mutable fields, `System.Random` / thread / stream / Unity fields, bad parameter
  fields — the build refuses them), `Parameters`, `SourceFile` / `SourceHash` (`Assets/SimuLean.UserLogic` under the current
  directory, or `SourceFolder`).
- **Logic viewer:** `LogicForm.Rule`; a site decided by a rule shows `rule: <Type> (source <hash>)`, its declared reads and
  `parameters` (`rule.<Field> = value`); `DecisionSite.Rule` names it. Rules write nothing.
- **Outside the editor:** `Engine.props` compiles `Assets/SimuLean.UserLogic` into every standalone suite and the runner;
  `EngineFingerprint.OfSources(engine, userLogic)` and `Fingerprint.targets` hash those sources after the engine's
  (`UserLogic/<path>`), so `ExternalRunner` never uses a runner built before a rule changed.
- **Unity:** `OutputStrategyType.Rule` (`rule`, `ruleParameters`), `UnityQueue.offerRule` / `ruleParameters`,
  `UnityResourceTeam.requestOrderName = rule:<Type>` / `ruleParameters` (`RuleParameterValue`: name, value text → a number or
  text record). MCP (`Editor/Pipeline/SimuLeanRuleCommands.cs`): `simulean_rule_new` (template per site, refused without
  `why`), `simulean_rule_check` (found, contract, parameters, hash; with `object`: builds the scene model and runs it twice
  with one seed, counting the decisions), `simulean_rule_show` (site, why, reads, parameters with catalog keys, file, hash,
  users from `ModelLogic`).
- **Examples:** `Assets/SimuLean.UserLogic/Examples` — `ShortestQueueRule`, `EarliestDueDateRule`, `HighestPriorityRule`;
  each declares the primitive it equals with its parameters at their initial values (`[ChoiceRule(Equivalent = …)]`).
  **Example scene** `Assets/Scenes/ChoiceLogicDemo.unity` (with an example card): Orders labelled `family` /
  `prio` → *Router* (BestScore `(out.family == item.family ? 10 : 0) - out.content` over the cells' property `family`) →
  Cell A / Cell B / *Flex Cell* (admission `element.open == 1`, flipped every 1800 s by the reaction *Flex Cell Shift*: the
  wake-up) → *Pack Queue* (`offerSort = prio desc, age`, routing `Rule` = `ShortestQueueRule`, `FreeWeight = 0.5`) → Packer 1
  / Packer 2 → Shipped; *Colour By Family* colours the items (Set Item Look). EditMode `ChoiceLogicDemoTests` (runs
  headless, every station used; its logic section shows each site).
- **Guide and card rules:** the escalation ladder field → expression → query / order → reaction → rule
  (reference: `docs/agents/topics/logic.md` *The escalation ladder*) is on the report: every `DecisionSite` has its `Rung`
  (`ModelLogic.Rungs`; from the setting's `LogicForm`: Value / Label → field, Expression / OrderKeys / Assignments →
  expression, Query and a named order → query, a reaction's sites → reaction, a rule → rule). `LogicReport.Decisions()`
  counts one decision per owner and site kind that is not the default, at its highest rung (a BestScore routing is one
  expression decision); `RuleSites(rule)` / `RuleUse` give the uses of a rule (`simulean_rule_show`'s `usedBy`);
  `Scripts()` is the model card's `scripts` (`simulean_card`, the open model only: the rules with owner, setting, label,
  `Why`, reads, parameters with catalog keys and source hash; `decisions`; `declarative` / `scripted`; a `summary`);
  `RuleWarnings(varied)` warns on a rule without a `Why` and on a rule used with every parameter at its initial value when
  it declares an `Equivalent` primitive (quiet when a study varies one of its parameters). `Study.Check` adds them as
  warnings, so `simulean_validate` and every MCP run show them; nothing else changes.
- **Tests:** `choicetests` (each site equal to the primitive it mirrors, catalog and a study's value, per-decision draws and
  seeds, build and run errors, the viewer, the round trip; the card's scripts, the rungs and the rule warnings); EditMode
  `ChoiceRuleTests`. No reference baseline changed.

## 4. Unity integration layer (`Assets/SimuLean.Unity/`)

### 4.1 The wrapper pattern: `SElement` + `IVElement` — one build path

**`SElement`** (`Core/SElement.cs`) is an `abstract MonoBehaviour` — the base of every element component. It holds the
connections (`nextElements`, or `sendTo` / `receiveFrom` a list: `pushToList`, `pullFromList` with `pullQuery`), the
routing and admission (`outputStrategy` / `inputStrategy`), the location node and `workNodes`, and the resources (`processBy`, `transportBy`). **It does not build anything**: a component is the *description* of an
element (exported by its extractor) and its *view*.

**One build path.** (The extractor takes the model's own clock — under the root, else in the root's scene, else the
active scene's — not any open scene's, 2026-10-07.) A run in the scene builds its model exactly as a headless run does:
1. `SceneExtractor.Extract(root, sceneRun: true)` (`Serialization/SceneExtractor.cs`) turns the scene into a
   `SimulationConfig` plus the id of every component (`SceneExtraction`: elements, lists, teams, downtimes). In a scene
   run, teams that move with Unity physics are exported with **external travel** (`Travel = External`, the estimate
   model as `CalibrationBase`; the physics times the moves and the engine records them); headless they use their
   calibration.
2. `SceneModelBuilder.Build(clock, bound, root, configure)` (`Core/SceneModelBuilder.cs`) does step 1 itself, applies
   `configure` to the extracted config (`UnitySimClock` passes `ConfigureModel`), then runs the **engine factory**
   (`HeadlessModelFactory`, §3.5) with a view provider: each element is built with its component as `IVElement`.
3. The components are **bound** to what was built: `SElement.Bind(element)` → `OnBound(element)` (each wrapper keeps
   its typed engine element and prepares its views), `UnityResourceTeam.Bind(dispatcher, points)`,
   `UnitySimList.Bind(listLink)`, `UnityDowntime.Bind(generators)`, `UnityTransportNetwork.Traffic`.
4. `HeadlessModelFactory.StartModel()` resets the list links, transport links and traffic, then starts elements →
   resources → teams → lists → downtimes → warm-up and creates the `RunMonitor`; then `UnitySimClock` calls
   `OnRunStarted()` on every element component and every `UnityResourceTeam` (member views).

Hooks of a component: `GetElement()` (the engine element of the current run), `OnBound`, `OnRunStarted`, `ClearViews`
(remove the views of the finished run — the next run builds a new model on a new clock; every element that positions
item views clears them, and `UnityResourceTeam.ClearViews` removes its member views and the items they carry, on
Restart and Stop). A source's next run starts from the table the component holds, never
the previous run's (#34). The former
`InitializeSim` / `ConnectSim` / `ApplyResources` / `StartSim` / `RestartSim` and the Unity-side factories
(`RandomProcessConfig.CreateRandomProcess`, `InputStrategyConfig.CreateInputStrategy`, `OutputStrategyConfig` /
`OutputStrategyAsset.CreateStrategy`, the wrappers' own builders) were removed: the scene and headless cannot diverge
any more. The EditMode test `SceneRunTests` checks that a scene
run gives exactly the headless results with the same seed.

Concrete wrappers implement `IVElement` **explicitly** so the visual API stays private. GameObjects are the visual
twins of core `Item`s: created in `GenerateItem`, moved in `LoadItem` / `ReportState`, destroyed in `UnloadItem`.
Smooth in-process motion is a view concern: `UnityMultiServer` and `UnityCombiner` lerp the item in `LateUpdate` (every
frame after the clock; their work point measured once per run) from
`ServerProcess.loadTime` / `lastDelay` against `UnitySimClock.SmoothSimTime`, and `BlockTurnView` (`LateUpdate`) rolls
a turned block the same way; the other wrappers place item views only in `LoadItem` / `ReportState`.

### 4.2 Wrapper → core mapping

| Unity wrapper | Core `Element` | Notes |
|---|---|---|
| `UnitySource` | `ScheduleSource` / `InterArrivalSource` (`mode`) | RateTable: `ratePeriods` (`RatePeriodSpec` start, rate, labels) inline, or the same data table / table source with the columns `Start`, `Rate` (`Name`, labels; `RateTables.PeriodsOf`); the extractor writes `ratePeriods` or the rows. Schedule: the arrivals are a **data table** (`dataTable`, a `UnityDataTable`; inspector: *New* creates one named after the source; `simulean_load_table` creates / reuses it; a file table's *Read file at run* / stale-copy warning, live on the `UnityDataTable`); the source's own table file was removed 2026-10-11 (rows set from code with `SetArrivalsTable` are still exported as `arrivalsTable`, `UnitySource.TableForRun`). Configs run without the scene carry their table, as before; or `tableSource` (+ `tableDatabase`): an `sql:` / `naval:` spec read at build (`TableSources`, §3.8 / §3.20); inter-arrival: a distribution; item labels `assignLabels` (label = expression); **`demandFactor`** (default 1, always extracted so the catalog offers it: `Source.demandFactor=1|1.2` runs demand scenarios) — the inter-arrival rate times the factor, or each table row's quantity times it with the fractions carried from row to row (deterministic; the total is the table's times the factor); ≤ 0 fails the build. `arrivalsOnStop` (`SourceStopPolicy` Hold / Pause / Lose, a catalog Choice; §8 item 2): what the arrivals do while the source is stopped; with Lose the source reports `lost_arrivals` (`IElementMetrics`). View: the components of a new item's view that implement **`IItemViewInit`** are told the item's id (`OnItemCreated`), e.g. **`MaterialVariants`** (Visuals: one of several shared materials per item, weighted, deterministic by id — paint colours of car bodies) |
| `UnityQueue` | `ItemsQueue` | FIFO; *Release on command* (`gated`, the key too since 2026-11-05; 2026-11-02: the former `UnityGateQueue`, removed with its prefab, handler and extractor; `ReleaseItems(n)` in Play). Item layout: *Grid* (default) — centred on the queue's footprint (its mesh, in its axes, or `gridArea`), spacing from the item size (`separation` 0 = auto), rows across the flow from the exit side (+X) first and each row from the front (−Z), items facing +X (the model frame), layers on top when full — *Stack* (one column upwards), *Seats* (the queue's `Seat` nodes in hierarchy order, e.g. waiting chairs, the items facing the queue's front, −Z; then the grid) or *Line* (a single file, the head at the item position, the others behind along −X facing it, +X — the model frame, it ran along −Z; with `lineLength` the line folds back beside itself, 1.5 steps aside, as a serpentine — `ItemLayout.LineOffset`); the layouts live in **`ItemLayout`** (`Visuals/ItemLayout.cs`, shared by every holding element, today `UnityQueue` and `UnityDelay`); `showFootprint` hides the queue's own pad (the footprint still sizes the grid); no fixed sizes. `offerOrder` / `offerScan` (also on the Multi-Server) choose which waiting item leaves first; `groupBySetup` / `groupLimit` offer first the items the receiving station is set up for (§3.2 *Setups*) |
| `UnityDelay` | `Delay` | *Processors ▸ Delay*: every item waits its own delay at once (`delayStrategy`, an expression reads the item) — a stay at home, a therapy, a curing time — whatever how many wait (a huge-capacity Multi-Server was the workaround). Items drawn with the queue's layouts (`ItemLayout`); an `UnityElementLabel` can hide them behind a count |
| `UnityAgenda` | `Agenda` | *Healthcare ▸ Agenda* (module `SimuLeanModules.Healthcare`): `sessions` (weekly pattern text), `slotMinutes` / `leadDays` (calendar minutes / days, converted with the model's time unit by the handler), `reservedPerSession`, `priorityLabel`, `overflow`, `noShow` (expression), `holidays`; the waiting patients drawn with the queue's layouts (`ItemLayout`) or hidden behind an `UnityElementLabel`. The visit station after it needs no shift |
| `UnityMultiServer` | `MultiServer` | N parallel servers (the element's state from per-state slot counters and an indexed work list, O(1) per event whatever the capacity —, 2026-10-08; `GetItems()` lists every held item — waiting for its resources, in process, blocked — from an indexed list of the waiting slots), selectable delay strategy, optional process / transport resources. Item layout: one slot per server (`ServerProcess.Index`) in a row along the element's X (more than 8 slots: a square-ish grid, `UnityMultiServer.SlotCell` — 253 slots stretched 100 m), centred on `inItemPosition` (else the top of the footprint, `ItemVisuals`), spaced by the item size (`separation` 0 = auto); with `outItemPosition` each item moves from its slot towards it (same offset) while processed |
| `UnityCombiner` | `Combiner` | Joins main + component streams. every upstream element connects through its own `nextElements`; the combiner maps connection 0 to the main input and the next ones to component inputs 0..N−1 (`Combiner.ResolveInputFor`, called by `GeneralLink.CreateLink`; order = optional `inputOrder` list, then hierarchy order; exported as the `inputOrder` parameter; the editor's Connect tool and `simulean_connect` append the new upstream to `inputOrder` — `UnityCombiner.NoteConnected` — so the first element connected is the main input, as in the engine: a pallet supply placed after the cartons' conveyor became a carton carrying 12 pallets). The former `myInputs` wiring (component sources listed on the combiner, `CombinerInput` connection type) was **removed** with no compatibility path — the sample scenes were migrated. Components can also be pulled from lists (`listInputs`). *Visuals:* the main item waits on the work point (`itemPosition`, `Combiner.prefab`'s `ItemPoint` on the work zone; else the top of the footprint, `ItemVisuals`), the components behind it, one column per input, spaced by their size — with `capacity` > 1 each assembly has its own work point, side by side along the combiner's right axis (`assemblySpacing`, 0 = from the items' size; set it to the pitch of the assembly beds when the model has one per assembly, with an explicit `itemPosition` at their centre — the automatic work point is the top of the largest single part); the waiting components stand behind the main item, one column per input (`componentLayout` **Behind**, default) or pile on the work point (*OnWorkPoint*: parts laid on the assembly bed; `UnityCombiner.PartOffset`, EditMode `CombinerLayoutTests`) or — *OnLoad* (appended to the enum so saved scenes keep their value) — each goes straight to its final place in the main item's load (the slots Attach ends with, `UnityCombiner.LayOnLoad`: what the main item carried in, then each input's parts in arrival order; the parts still to come keep their places with the size of their input's last one, the count from `requirements` / the `updateLabels`), so the load grows at the station as on a palletizer; before the main item is there they wait Behind, and at the process start nothing travels (view only, no engine change; the Palletizer and Tote Packer presets); at the processing start (`UnloadItem`) `visualMode` decides: **Attach** (default) — the components become children of the main item in a grid on its top and travel with it; *MainOnly* — they are destroyed; *NewItem* — a `combinedItemPrefab` instance replaces the main item and becomes `vItem` (none set → Attach). *Assembly view:* `componentEntries` — a `Transform` per component input where its parts pile up while they wait (none = the layout above); Attach packs the components on the main item's top in rows, then layers, each with its own size (`PackOnTop`; a `LoadSocket` marks where a container's load rests; the parts are turned a quarter when that packs them lower or, as low, further inside the top — a carton across a tote went through its walls) and, with `animateAssembly` (default), they travel there one after another during the process time (all in place when the process ends — `ProcessEnded`, in a fast run the item left in the same frame with a part still outside and the next station measured it wider; `MoveProgress`: the first 85 %, `MovePose`: straight and eased, was lifted in an arc; driven by `SmoothSimTime` in `LateUpdate`, view only); without component entries the parts wait on the station model's `PartsSocket` (the library assembly bench's parts shelf — behind the main item they stood inside the bench's back panel). The view records each component's input when it arrives (`LoadItem`) and lays the assembly out at the process start, since an assembly can start in the instant its main item arrives. Example: `Assets/Scenes/LibraryExamples/Assembly_Kitting.unity` (`CombinerAssemblyExample`, EditMode `CombinerAssemblyExampleTests`) |
| `UnityPickingStation` | `PickingStation` | Containers arrive through the upstream `nextElements`, stock from `stockSources` into the SKU stock port (`stockCapacity`); pick list from a `pickTable` data table, the container's labels or the static list; surplus goes to `returnsDestination` |
| *Setup section* of `UnityMultiServer`, `UnityCombiner`, `UnityPickingStation` | `StationSetup` (§3.2 *Setups*) | `setup` field (`StationSetupConfig`, `ISetupUser`): `when` (`SetupWhen`), `label`, `setup.setupFirst`, `time` (`RandomProcessConfig`, as the process time), `setupBy` (`SetupResourceUse`: team, requirements, priority, preemption, preemptable — `ResourceUseDrawer`), `keepForProcess`; inspector section *Setup* (`SElementEditorHelper.DrawSetup`, only the fields the rule uses; a warning when *Keep For Process* has another team than the process). Exported by the three extractors (`Export`: nothing with rule None) and `SceneExtractor` (the crew's team id, `ExportCrew`); built by the engine only. Views: the state lamp is green and the parts move while SETUP (`MachineStateView`), charts colour SETUP yellow (`ChartPalette`) |
| `UnityAssemblyStation` | `AssemblyStation` | A precedence network of `tasks` (id, predecessors, materials by SKU, duration, priority), `workerCount` tasks at a time, materials from `materialSources` into a shared stock (`stockCapacity`) |
| `UnityErectionDock` | `ErectionDock` (§3.20) | `capacity` fronts, `erectionRule`, duration, `expectedBlocks`, block / sequence / predecessor labels, `shipName`; view: a block hangs `hoistHeight` over its hull place (`positionLabels`) and settles when erected; the launched ship gathers the hull's blocks and travels on |
| `UnitySink` | `Sink` | Terminal; `GetNumberItems()` / input count, `expectedItems` (progress); `keepItems` keeps the received items in the scene, laid out by `positionLabels` (else a row along +X), removed in `ClearViews` |
| `UnityResourceTeam` | `ResourceDispatcher` + `TaskExecutor`s | Plain `MonoBehaviour` (not an `SElement`): a team of resources and its dispatch orders (§3.17); extracted by `SceneExtractor` (`UnityResourceTeamExtractor`), built by the model factory and bound (`Bind(dispatcher, points)`); member views created in `OnRunStarted`, removed in `ClearViews` (an Abstract team's members stand still at their spawn points). Travel `TeamTravel`: Abstract / Network / Matrix / Axes / Grid / UnityPhysics (+ `physicsEstimate`) / Straight (default, §3.17). Elements use it through their `processBy` / `transportBy` fields |
| `UnitySimList` (`Lists/UnitySimList.cs`) | `SimList` + `ListLink` (§3.16) | Plain `MonoBehaviour` (library tool, group *Lists*): name, fields, default order; built by the model factory, bound with `Bind(ListLink)`; elements push / pull through `pushToList` / `pullFromList` + `pullQuery`; the inspector shows the live content |
| `UnityDataTable` | a `ModelTable` of `SimContext.Data` (§3.8) | Library tool, group *Data* (`SimuLeanToolCategories.Data`): a named table — rows typed as CSV in the inspector (Inline), a file (CSV / `.data` / Excel through `TableFiles`; *Load* keeps a copy, *Read file at run* re-reads it at every run start, else a warning when the file's content differs from the copy — a SHA-256 of its bytes without carriage returns, not its write time, which a git checkout changes) or an SQL query on the project database (run at every run start). `SceneExtractor` adds each one's snapshot (`Snapshot`) to `SimulationConfig.Tables` and sets `DatabasePath` |
| `UnityTransportNode` / `UnityTransportNetwork` / `UnityVehicleAnimator` | `TransportNetwork` | Plain `MonoBehaviour`s (not `SElement`s): scene node marker; the model's network (one per model, exported as `SimulationConfig.Network`), authoring + build + arrow gizmos + explicit *Place Elements On Closest Node* tool (fills each element's `location`); per-member view that interpolates along `TaskExecutor.CurrentLeg`'s route from sim time and stands at its node otherwise, carried items riding on it — **Unity animates, the engine decides the times**. `UnityTransferPoint` / `UnityTransportSystem` were removed 2026-10-06: every `SElement` has a `location` node, and a fleet is a `UnityResourceTeam` with *Network* travel (home node, speed, member prefab) |
| `UnityStorageSystem` | `StorageSystem` | Lanes from an Inspector list (`StorageLaneKind` presets or custom access/SKU rule, per-lane anchor Transform + optional network node) or auto-generated one-per-child-Transform; location/relocation policy dropdowns (+ SKU→lane map for DedicatedBySku); timed moves by its Transport team (resources section) to / from its `location`; put-away via upstream `nextElements`, retrievals programmatic (`RequestRetrievalBySku` or core API), delivered through its own `nextElements` link, or pulled from the model list it pushes to (delivered at the puller); stored items stacked above their lane anchor by default (`protected virtual GetStoredItemPosition` — the hook subclasses override for exact location views) |
| `UnityStorageRack` | `StorageSystem` (via `UnityStorageSystem`) | Parametric storage element **library**: `StorageRackStyle` ∈ ConventionalRack / FloorStorage / DriveInRack / PushBackRack, created preconfigured from *GameObject → SimuLean → Storage*. Counts (bays, slots/bay, levels, depth, stack height) + physical pitches (bay width, slot width, level height, slot depth); `Rebuild()` (Inspector button / context menu) regenerates one marker-tagged child anchor per lane plus slot visuals (optional **unscaled** slot prefab per position, else generated shelf/floor plates) and rewrites the inherited lane list — so behaviour, extraction (extractor registry alias `"UnityStorageRack"` → a `StorageSystem` config) and headless runs are unchanged. `[SelectionBase]` makes clicking any slot select the rack root. Overrides `GetStoredItemPosition` / `PlaceStoredItem` for the item location view: rack styles place units along lane depth, floor storage stacks upward (oldest on the ground). Style `CartonFlowRack` (FIFO single-SKU lanes on inclined shelves); without a slot prefab the visuals are generated procedurally and items rest on their support (§4.7.3) |
| `UnityItem` | — | Debug MonoBehaviour on item GameObjects (`Core/UnityItem.cs`, namespace `SimuLean.Unity`) |
| `UnityReaction` | `Reaction` | Plain `MonoBehaviour` (Library ▸ *Logic*, `SimuLeanToolCategories.Logic`, its own icon `Editor/Icons/Reaction.png`): *When* `sources` `signal` (`state` filter; clock signals: `period`, `firstTime`, `atTime`, no sources) *if* `condition` *do* `actions` (`ActionSpec`: kind, target element or `onTeam` + `team` + `count`, name, list, value — a plain number or an expression —, stop state / mode / duration), `reactionEnabled`, `timing`. Connection lines (`SceneConnections`: `ReactionSource` element → reaction, `ReactionTarget` reaction → element / team, group `Logic`, purple, dotted; the Connect tool adds a source); a team's inspector lists its reactions. Exported by `SceneExtractor` (`ToConfig`, `ReactionIds`) as a `ReactionConfig`, built by the engine, bound in Play (`Bind`, `Bound`). `UnityReactionEditor` reads as its sentence (`Sentence`), shows only each action's fields, checks it (no elements, expressions that do not parse, a label without an item…) and, in Play, its counts; the Play inspector panel (`ElementInspectorReport`) lists the reactions of the selected element from `SimContext.SignalsIfAny`. User guide: `Assets/Docs/Signals_And_Reactions.md` |
| `UnityDowntime` | `DowntimeGenerator`s (one per target) | Plain `MonoBehaviour` (not an `SElement`): kind MtbfMttr / Timetable / Shift / Periodic (`periodicFirst`, `periodicPeriod`, `periodicDuration`, `useUntil` + `periodicUntil`, `allowOverlap`, `periodicCode`; the inspector flags invalid settings), target list (+ optional keys for timetable matching; `teamTargets`, resource teams — a generator per member), *Repair by* (`repairBy`, `RepairResourceUse`: team, requirements, priority, preemption, preemptable — off by default; drawn by `ResourceUseDrawer`; not for shifts), on / off (`downtimeEnabled` → `enabled`), stop behaviour (state, mode, blocking — 2026-11-17: `stateName` default empty and
`mode` default *Kind Default* (`UnityDowntime.DowntimeMode`, whose Immediate / AfterCurrent keep `StopMode`'s values, so saved
scenes read as before) take the kind's state and mode, exported resolved (`EffectiveStateName` / `EffectiveMode`: the model view
shows them, a study can vary `mode`); a component used to export BREAKDOWN + Immediate for every kind, a shift calendar
included; scenes that saved BREAKDOWN keep it; the inspector clears a state left at the former kind's default when the kind
changes), MTBF/MTTR distributions (`RandomProcessConfig.ToSettings()`), shift pattern + holidays, inline timetable rows and/or a data table with column mapping and per-code rules. Exported by `SceneExtractor` (`ToConfig`) as a `DowntimeConfig`, built by the model factory (`Bind(generators)`) and started by `StartModel`; the rows can come from a data table (`dataTable`) |
| `UnityCallOffPoint` (Manufacturing pack) | `CallOffPoint` | Families as `CallOffFamilyDef` (name, order type, quantity, condition, reference tables, destination → `PortOutput`); the view only places the passing vehicle |
| `UnityPacedLine` (Manufacturing pack) | `PacedLine` | Positions, takt, inline stations (`LineStationDef`: position index, match label, port capacity, feeders → `PortInput`, family, optional call-off at the line). View: pitches along local +X (`pitchLength`; the model frame — it was +Z) or, 2026-10-08, along an optional **`path`** (ordered waypoint transforms, corners rounded with `cornerRadius`; geometry in `PolylinePath`, `PoseAt(s)` / `PitchPose(i)`), so a long line can be folded into a serpentine; vehicles face along the line and move smoothly along it (arc length) during `indexMotionFraction` of the takt after each index; parts wait beside their station (`partsSideOffset` to the right of the travel: a straight line's −Z, its front) and become children of the vehicle when fitted — **view hook:** a station's `vehicleSocket` names a node of the vehicle's view (e.g. `ConsoleSocket` of the `CarBody` model) where the fitted parts are shown, and its `fittedPrefab` replaces their own views by one model per station and vehicle (`ShowFitted`, named `Fitted_<station>`). View-only fields: not extracted, runs unchanged. EditMode `PacedLinePathTests` |
| `UnitySequencingStation` (Manufacturing pack) | `SequencingStation` | Orders from `orderSources` (port `orders`), pieces from `pieceList` (list + query, pulled on demand by reference) or `pieceSources`, served orders to `servedOrdersTo`, carriers through the usual connections; times as `RandomProcessConfig`s (constant 0 = none); `operatorScope`. View: the carrier at `carrierPoint`; a placed piece goes into the carrier model's child `Slot_i` (any depth) or onto a grid on its top (`gridColumns`, `slotSpacing`), parented to the carrier; a taken piece waits at `pickPoint`; waiting orders line up at `orderPoint`; optional `stateIndicator` renderer in the state colour (`InspectorPalette`). Spatial operators walk to the station's `location` (team views). Extractor `UnitySequencingStationExtractor` |
| `UnityMilkRun` | `MilkRun` | Trains = its transport team's members; fixed tours (stops with empties, flows origin → stop → destination, tour types, pattern, departure rule, legs with optional `via` points for the view) or dynamic tours (pick-ups at any stop, request / delivery orders, named orders, policy, assignment, origin access, fill time-out). View `MilkRunTrainView` (tractor and trailers, or carriers on board). Extracted by `UnityMilkRunExtractor` (its references in the third pass, `IElementReferenceExtractor`), built by `MilkRunHandler`; library *Transport ▸ Milk Run* |
| `UnityPickFace` (pack Warehousing) | `PickFace` | SKU, positions, reorder point / quantity, initial fill (`startFull`, pieces per load, load / piece types), `emptiesDestination` (→ `PortOutput` from port `empties`), `sizeFromModel` (the load / piece prefab's size → `modelWidth` … / `pieceModelWidth` …, §3.16). View (read-only): loads at slots along local +X, pieces from a prefab or `UnitLoadVisuals` boxes, in a grid of the piece prefab's own size (else `pieceSize`); a load received with another look (e.g. a replenisher's full pallet) is opened with its `loadPrefab` and its pieces laid on it. Extracted by `UnityPickFaceExtractor` (`UnityWarehouseExtractors.cs`), built by `PickFaceHandler` (`ReplenishmentBuilders`); library *Storage ▸ Pick Face* |
| `UnityBaseStockReplenisher` (pack Warehousing) | `BaseStockReplenisher` | SKU table inline or from a CSV / Excel file anywhere (`skuTableFile`, read at extraction: the config carries the rows), truckload size, lead time, unloading windows, observed storages, initial-stock target, load / piece types, `sizeFromModel` (the load prefab's size → `modelWidth` …); its transport block carries the loads. View: the loads waiting at the dock. Extracted by `UnityBaseStockReplenisherExtractor`, built by `BaseStockReplenisherHandler` (whose `Wire` resolves the observed storages and the target by name); library *Sources ▸ Base-Stock Replenisher* |
| `UnitySeparator` | `Separator` | Mode (Unpack / Split), capacity, unpack order, split quantity, optional process time (`RandomProcessConfig`), `emptiesDestination` (→ a `PortOutput` connection from port `empties`; Unpack). View (read-only), the Combiner's in reverse: the items held at slots along local +X (`firstSlot` + i × `slotStep`), a container's contents children of its GameObject; during the process time they travel one after another to `piecesPosition` (`animateSeparation`, `ProcessStarted`; `UnityCombiner.MoveProgress` / `MovePose`); a split copy a copy of the original's GameObject. Extracted by `UnitySeparatorExtractor`, built by `SeparatorHandler` (codec `FlowBuilders`); library *Processors ▸ Separator* (Core); EditMode `SeparatorSceneTests` |
| `UnityLineSideUnpacker` | `LineSideUnpacker` | Carriers held, unpack order, carrier labels copied, empties keep their place, optional piece / change times (`changeTimeFirst`: the change time before the first carrier too), `emptiesDestination` (→ a `PortOutput` connection from port `empties`). View: carriers at slots, the pieces children of their carrier while inside it. Extracted by `UnityLineSideUnpackerExtractor`, built by `LineSideUnpackerHandler`; library *Assembly & Kitting ▸ Line-Side Unpacker* (Manufacturing pack) |
| `UnityStacker` | `Stacker` | Positions from `positionAnchors` or a generated row (`firstPosition` + i × `positionStep`), `stackBaseHeight`, pick point (`pickPoint` / `pickOffset`), all engine settings (rule, labels, capacities, height, cycle time as a `RandomProcessConfig`). Visual: item GameObjects go to the pick point on arrival and snap to their slot (`SlotWorldPosition`, stacked by item height or `itemThickness`) when placed — unless an `IStackerAnimator` (`GantryStackerView`, §4.10) is still animating them; on `StackCompleted` a new parent GameObject is created at the stack base, the items are re-parented and it becomes the compound's `vItem`, so downstream elements move the whole stack. Crane positions `pickNode` / `positionNodes`. Extracted by `UnityStackerExtractor`, built by `StackerHandler`; inspector with live stacks and *GameObject → SimuLean → Stacking → Stacker / Gantry Stacker* |
| `UnityLengthLimitedQueue` | `LengthLimitedQueue` | Lane lengths list, gap, default length, length label, max items, oversize rule, input/output strategies. Local +Z of `laneOrigin` = flow direction, origin = lane exit; items drawn end to end backwards along −Z with their real lengths, lanes offset by `laneSpacing` along +X; lanes drawn as gizmos. Extracted by `UnityLengthLimitedQueueExtractor`, built by `LengthLimitedQueueHandler` |

**Scene placement.**
- **Layout by bounds:** `ItemVisuals.PivotHeight(go)` (pivot height above the bottom of its renderers) /
  `TopAbovePivot(go)`; the Queue, Multi-Server and Combiner layouts put the pivot that high above the work point, so an
  item's bottom rests on it whatever the model's pivot (a centred pivot gives half the height: the former layout, the
  example scenes unchanged; library models with the pivot at the base no longer float).
- **Output look per station:** `UnityMultiServer.outputItemPrefab` — when processing starts (`MultiServer` calls
  `IVElement.UnloadItem`) the item's visual is replaced by an instance of the prefab (`ItemVisuals.ReplaceLook`); views
  only.
- **Item views — looks from labels.** `Item.vItem` is a property: assigning a visual raises the
  static engine event **`Item.VisualAttached`** (the engine never subscribes; headless runs have no visuals). The Unity
  layer's **`ItemViews`** (`Visuals/Items/`, installed by `UnitySimClock.Awake`) binds every **`IItemView`** on the
  visual to its item — wherever the visual comes from (a source, a station's output look, a combiner's new item, an
  erection dock's ship). Views: **`ItemLabelView`** (a label — the block number — as world-space text over the item,
  turned to the camera; `#id` / `#name`), **`ParametricBlockLook`** (the model of the item's `kind` scaled to its
  `length` × `breadth` × `depth`, a block without them from its sub-blocks; child `Look`, length along local X,
  breadth along Z), **`LookByLabel`** (a label value → prefab map; binds the chosen look's own views). Station view
  **`BlockTurnView`** (on a MultiServer: the processed item rolls 180° about its long horizontal axis with the process
  time and leaves turned). All read-only. Item prefabs `Prefabs/Items/Shipbuilding/` (`SteelPlate`, `ProfileBundle`,
  `PlateKit`, `SubBlock`, `Block`, `BlockOrder`), used by the naval presets (sub-assembly output = SubBlock, block
  bays = NewItem Block, turning = BlockTurnView). EditMode `ItemLooksTests`, standalone `navaltests` (the hook).
- **Per-member spawn points:** `ResourceMemberSpec.spawnPoint` (`UnityResourceTeam.SpawnPointOf(member)`, else the
  team's transform), e.g. two robots of one team in their own cells; axis rails run from it.
- **Work nodes:** `SElement.workNodes` (transport nodes) → config `workNodes` (`SceneExtractor`), see §3.17.
- EditMode `PlacementTests`.

### 4.3 `UnitySimClock` — driving the engine from Unity

Singleton owning `public SimClock clock` (a new one on every Restart / Stop, `NewModelClock`) and the `Elements` of the
scene.
*Calendar* fields `startDateTime` / `timeUnitSeconds` (`CreateCalendar()`,
set on the clock's context before the model is built; empty = default calendar, the
inspector then shows the calendar date live and the end date), `maxTime` is now a
`double` (a year in seconds no longer loses precision; `SimEvents.OnSimStart` takes a
`double`; serialized scenes keep their value); downtimes are built and started by the model (§4.1).
`Update()` is the pump:

- **First frame:** calendar, run seed, then **`SceneModelBuilder.Build`** (scene extraction → engine factory →
  components bound, §4.1) and `StartModel()`; `OnRunStarted()` on the elements and teams. Every run builds a new
  model on a new `SimClock`.
- **Subsequent frames:**
  `clock.AdvanceClock(min((Time.time - timer.PastTime + Time.deltaTime) * timeScale, maxTime))` —
  real elapsed Unity time × `timeScale` becomes simulation time, **capped at `maxTime`**
  (with the former default `timeScale` of 10⁷ the first frame used to run far past the
  end, generating items without bound and freezing the editor). **Default `timeScale` = 1** (real
  time; was 10⁷) — scenes keep the value they store.
- Stops at `maxTime` (or rolls to the next `Experimenter` scenario).

`timeScale` is sim-seconds per real-second. Lifecycle wires through the
`SimEvents` event bus — a plain C# class owned by the clock (`UnitySimClock.SimEvents`, no asset): `OnSimStart(double)`,
`OnSimStop`, `OnSimResume`, `OnSimFinish`, `OnExperimentStart`, `OnExperimentFinish`, `OnMonthPassed`. A model that
cannot be built (an extraction or handler error) leaves the clock not running and logs the error once.

**Optional driving features (defaults = the legacy behaviour above).**
They live in the inspector foldout *Optional runtime features* of `UnitySimClockEditor`:

- `autoStartOnPlay` (**default `true`** since 2026-10-06; was `false`) makes `Start()` call
  `RunSimulation()` on Play without the *Run* button. Scenes whose clock is driven by an
  Experimenter / Optimizer (`1WS_Experimenter_Scene`, `1WS_Optimizer_Scene`) store `false`.
- The `TimeScale` **property** changes the speed while running: it re-anchors
  `timer.PastTime` so the target time `(Time.time − PastTime) × timeScale` stays
  continuous (no jump). (The former `SetTimeScale(int)`, which also throttled the frame rate, was
  removed 2026-10-07.)
- **Paced mode (large speed-ups).** `frameBudgetMs` (default 0 = legacy loop,
  unchanged) > 0, or `RunAsFastAsPossible = true` (runtime property; serialized field
  `runAsFastAsPossible`, default off, shown under *Simulation Controls* in Edit and Play mode
  since 2026-10-06 — it was a Play-only toggle inside the foldout: "maximum speed",
  target = `maxTime`, default budget 40 ms),
  switches `Update()` to `AdvancePaced()`: double-precision anchors
  (`Time.realtimeSinceStartupAsDouble`, sim time), target = anchor + elapsed ×
  `timeScale`, advanced in slices (900 sim units with *Run As Fast As Possible*, else `max(1, timeScale × 0.004)`)
  until the wall-clock budget of the frame is spent; if the target is not reached the
  anchor restarts at the reached time (lag dropped, the view never freezes catching
  up). `EffectiveSpeed` reports the achieved sim-units per real second; `TimeScale`,
  pause / resume and budget changes re-anchor (and re-sync the legacy float timer when
  leaving paced mode); `SmoothSimTime` returns the clock time in paced mode.
- `escapeQuits` (default `true`) lets applications keep Esc for their own menu;
  `UnitySimClock.ConsumeEscape()` lets an optional panel that closes on Esc (and runs
  earlier in the frame) keep the clock from also quitting.
Run settings for parity with headless: the seed (below) and
`warmupTime` (default 0; scheduled by `StartModel`, as headless). **End condition**: `stopAt` (a list of
elements; 2026-10-08, was one sink) + `stopAtCount`, `stopWhenEmpty` (defaults off) → `SimulationConfig.StopAt` (the
element ids) / `StopAtCount` / `StopWhenEmpty`, and `stopWhenNoProgressFor` (*Stop When No Progress
For*, model time, 0 = off) → `StopWhenNoProgressFor` (§3.5); `Update` finishes the run when the clock is stopped (`clock.IsStopped`) as at max time, and
*Run Headless* stops there too. The extractor
exports `maxTime`, the seed and `warmupTime` into `SimulationConfig`.

**Seed.** `seedMode` (`SeedMode.RandomEachRun`, the default = the
previous behaviour, or `Fixed`) + `seed` (the fixed value). Before every run (Run / Restart, the
`!simStarted` branch of `Update`) `ApplyRunSeed()` resolves the run's seed — `seed` when Fixed,
otherwise `SimContext.NewRandomSeed()` — calls `clock.Context.Reseed(runSeed)` on the run's new clock and records it in `LastRunSeed` (logged), *before*
the model is built (the handlers create the samplers with per-element streams, §3.6, as headless). API for views: `SeedMode`, `Seed`, `LastRunSeed` /
`HasLastRunSeed`, `NextRunSeed` (null = random), `UseFixedSeed(v)`, `KeepLastRunSeed()`,
`DrawNewSeed()` (fixes a new random seed so it is visible before running), `UseRandomSeedEachRun()`.
The inspector (*Random Numbers*) shows the mode, the fixed seed, the seed of the last run with
**Keep** / **New**; the HUD has the same controls (§4.9). Changes made through this API during Play
mode set `SeedSettingsChangedInPlayMode`, and the editor-only `SeedPlayModePersistence`
(`[InitializeOnLoad]`, `SessionState`) re-applies them with Undo to the clock of the same scene
when Play mode ends (Unity would otherwise revert them). Extraction: Fixed → `UseFixedSeed`
+ `Seed`; Random each run → `UseFixedSeed = false` (the headless run draws its own seed) with the
scene's last run seed recorded in `Seed`, so switching `UseFixedSeed` on replays that run headless.

**Run Headless (results only) — `HeadlessSceneRun` (`Core/HeadlessSceneRun.cs`).** A
separate inspector button, next to the scene run and kept apart from *Run As Fast As Possible*
(which still drives the scene and its views). It runs the §3.5 pipeline on the open scene once:
extraction on the main thread (`SceneExtractor.Extract`, with the scene's `UnityModelExtractor` model root when there
is one; nothing is added to the scene, so Edit mode does not dirty it; clock `maxTime`, warm-up, seed and calendar come
with it), then `HeadlessModelFactory`
`BuildModel` / `StartModel` and `AdvanceClock` to `maxTime` on a worker thread (`Task.Run`), in
200 slices only for progress and cancel. Works in Edit and Play mode, never touches the scene or
the `UnitySimClock` state. The result is the run's `RunResults` (§3.11; model = the scene's name): the inspector
shows the elements (in, out, average content, average stay, state shares) with *Copy CSV* (every statistic in the
long format, `ToLongTable`), *Export*, *Save* (results store) and *Show in Results*; the run is published as the last
results (`ResultsOutput.Publish`); `Start(SimulationConfig)` runs an already
extracted config (EditMode test `HeadlessSceneRunTests`). In *Random each run* mode the headless
run uses a fresh seed (the extracted config does not fix one). A domain reload (script recompile, entering Play mode) drops a run in progress.

### 4.4 Batch experiments — `Experimenter`

**Outside the editor: `ExternalRunner`** (`Core/ExternalRunner.cs`, runtime assembly). The
Experimenter, `SceneStudy.RunAndPublish`, the MCP's `simulean_run_headless` / `_start`, the **Optimizer** (and
`simulean_optimize_start`: `Optimizer.Setup.Executor`; each generation is one batch, `BatchedObjective` /
`IBatchObjective`: `SimulationObjective.BatchFor` / `EvaluationFrom` give the same evaluations, so the same search —
the time limit is then checked between generations) and the clock's **Run Headless** (`HeadlessSceneRun`: a one-run
batch; an unseeded model draws its seed as a clock does; the progress bar then jumps to the end) run their work through an
`ExternalRunner.Executor` decided on the main thread (`Prepare()`): the study's batch (`Study.Batch()`, §3.5: the
scenarios applied in the editor, tables read with its reader) is written as JSON, run by the runner `simulean` (§3.5,
`Tools/Runner`) in a separate .NET process — **kept alive for the editor's session** (`simulean serve`, 2026-10-27: batches
by id over its standard input, one at a time — both streams UTF-8 on both sides, or the console's code page garbled every non-ASCII name; .NET starts and compiles once, not per batch — an optimisation sends one
per generation; closed when the editor reloads its scripts or quits, started again if it died) —, and the whole result
set comes back (`ResultSetJson`); progress and the
engine's log arrive on its standard error (the log into the editor's, so the answers report it); cancelling creates the
runner's cancel file (the runs done come back). The runner is first looked for as shipped with the package
(`SimuLeanRunner/<rid>/simulean[.exe]` at the project root, built by the release: no wait), else in
`Library/SimuLeanRunner/<engine fingerprint>/`. When it is missing and the project's engine is the release's own
(**`RunnerRelease`**: the release's version and engine fingerprint, which the export checks against the exported tree), the
release's prebuilt runner for this machine (win-x64, osx-arm64, linux-x64) is **downloaded** in the background from the
GitHub release (`SimuLeanRunner-<version>-<rid>.zip`, built by `Tools/Runner/publish.ps1 -Version`) into
`SimuLeanRunner/<rid>/` (switch `SimuLeanProjectSettings.downloadRunner`, *Download the runner*, on by default); otherwise,
or when the download fails, a project with its sources (`Tools/Runner`) and a .NET 10 SDK builds it in the background on
first use (`dotnet publish`, self-contained); its fingerprint must be the editor's (`EngineFingerprint.OfSources` of
`Assets/SimuLean.Net`, checked once per session — a stale runner is deleted and rebuilt). Until then, when switched off
(`SimuLeanProjectSettings.runOutsideEditor`, *Run outside the editor*, on by default), in batch mode, during the EditMode
tests (`ExternalRunner.Suspended`, set by `SimuLeanTestRunner`: their results must not depend on whether the runner is
built) or when the runner fails, the study runs in the editor, with the reason in the executor's `Note`; every result set
records where it ran (metadata `ran_in`; the MCP answers' settings `ranIn`). **Same results (measured):** the event
sequences are the same in Mono and on .NET (every run's metrics and every statistic bit for bit, EditMode
`ExternalRunnerTests` on six reference models); a value derived by floating-point arithmetic may differ in its last bit
(one time-series point of `operator_pool`, 1 ulp). **Speed (measured 2026-10-27):** 40 runs of `operator_pool` (8 threads)
take 28.7 s in the editor and 1.9 s in the runner (process start and JSON both ways included). Models that need Unity's physics are not extracted for headless runs
at all — they run only in the scene (decided 2026-10-27): the extraction's message says how to calibrate the travel, and
the Experimenter adds that its in-scene mode runs such a model. **A large model (400 elements, 45 reactions, a
year):** one run 2.3 s in the editor, 1.1 s by the runner (≈ 0.2 s per run once warm, the
rest is the process start, the model build and the JIT); 20 runs on 8 threads 22.9 s / 6.4 s — the runner's runs scale
poorly with threads (4.4 s on 1 thread, 3.1 s on 8) and the result set of 20 runs is 17 MB of JSON, read slowly by Mono:
the second is solved: the runs come back compact — statistic names once per run, every number list the
  base64 of its doubles (exact) — and one per line as soon as each ends (`simulean run … --stream`), decoded on workers while
  the next ones run (20 runs: 6.4 → 3.6 s); and the runner uses the server garbage collector (a run allocated ≈ 140 MB: with
  the workstation one parallel runs stopped each other; ≈ 53 MB without the state log) and is published
  ready-to-run: 20 runs 2.55 s from the editor.
  The runner's folder in `Library` is named by the engine fingerprint and a hash of the runner's own sources
  (`Tools/Runner`), so a change to the runner alone (protocol, collector) also gets it rebuilt.

Built on the optimisation model (§3.19, §5): the same `ParameterSpec` /
`MetricSpec` lists as the Optimizer and `ExperimentScenario` rows (name, replications, one value per parameter **as
text** — a number, an option, `true` / `false`, values separated by commas for vectors and sequences —, read by
`Candidate.SetText`; any kind of parameter). The range of a number is the scenarios' values; a **text setting**
(§3.19: a priority expression, a request order written in place) is a choice among the scenarios' texts
(`ParameterSpec.ToTextParameter`), each row's value a text field. `Validate()` builds
every scenario without running it; the inspector draws each value by its kind (popup, toggle, number / text). Scenario
names are unique (`Study.Plan` refuses two with one name; a new row gets the first unused "Scenario k"); the set's
metadata keeps the study's `base_seed`, `cancelled`, `failed_runs` and `first_error`; removing a
parameter removes its value in every scenario; an error during an in-scene run ends the experiment and gives the clock
its settings back. Two modes:
- **Headless** (`headlessMode`, default): the scene is extracted once; the objective's parameter ranges are the
  scenarios' values; every (scenario, replication) is a `SimulationObjective.Run(candidate, seed, inspect)` in
  `Parallel.For` (`threads`, 0 = `Study.DefaultThreads`: half the logical processors — about one per physical core —, 1 to 16; the inspectors show the value on that computer), in Edit or Play mode, cancellable. Replication r uses the seed
  `randomSeed + r − 1` in every scenario (**common random numbers**; `randomSeed` 0 = a new base each time). The
  component's `warmupTime` goes to `SimulationConfig.WarmupTime`. With `exportElementStats` each run keeps its
  `RunResults` (`ResultRun.Results`, §3.11; the Excel report has the sheets Summary, Scenarios, one per scope, Runs,
  Statistics, Run statistics, Series, Timeline, Jobs and Metadata). `warmupTime` is applied only when > 0.
- The headless runs go through a **`Study`** (§3.5): `BuildStudy(config)` turns the component's parameters,
  scenarios (with their replications), metrics and settings into one — scripts can add factors to it — and the in-scene
  mode applies a scenario with `Study.Apply`. A run that fails (a level the model cannot be built with, a metric it
  cannot read) is kept as a `ResultRun` with its `Error` and no metrics; the other runs carry on, and the set's
  metadata gives `failed_runs` and `first_error` (one failure aborted the study and lost
  every completed run).
- **In the scene** (Play mode): every (scenario, replication) runs visibly, one after another, replication r with the
  clock fixed to `randomSeed + r − 1` — the same seeds as headless, so both modes give the same results (checked
  2026-10-07); the clock's seed settings are restored at the end. The Experimenter's `recordTimeline` switches the
  clock's on for the experiment (the job recorder is attached at the build) and restores it. `UnitySimClock.ConfigureModel` applies the
  scenario's values to the extracted config before the build (`SceneModelBuilder.Build(…, configure)` — one build
  path, nothing set on components); at `OnSimFinish` the metrics are read from the run's model
  (`UnitySimClock.Model`, `MetricCatalog`) and the next run starts (`IExperimenter.NextScenario`).
  Progress (`status`, run counts) and results are in memory only (`[NonSerialized]`; the report keeps them): a run
  leaves the scene unchanged.

Without scenarios (and parameters) the model runs as it is ("Baseline"). Results: each run is a
`ResultRun` (`Results`: scenario, replication, seed, metrics, its `RunResults` when `exportElementStats` is on, time
series every `seriesInterval` in headless mode); at the end `BuildResultSet()` makes the `ResultSet` (§3.11: the
scenarios with their parameter values, the runs, the settings) and **`ResultsOutput.Export`** (`SimuLean.Unity`) writes
it through **`ResultsOutput.Keep`** (export in `exportFormats` — `ResultFormats` flags, default Excel; none = no
export — and store it when `storeResults`) to the results folder
(the project settings' folder, default `SimuLeanResults/`, git-ignored; SQLite: `SimuLeanData/simulean.db`) as
`Experiment_<name>_<time>`. The former
`ExperimentResult`, `ExperimentReport` and `ExperimentReportExporter` are gone. `ExperimenterEditor` uses the Optimizer's `SpecGUI` (parameters without ranges) and draws the
scenario rows. The former reflection on component fields, `ScenarioEvaluator` and the editor thread dispatchers
(`SimuLean.Unity.Threading`) are gone. Scenes: `1WS_Experimenter_Scene`, `MM1` migrated.

### 4.5 Stats display

- An element's live statistics are the **Statistics** section of its inspector (Play only): the same KPIs
  as the run-time element panel (`ElementInspectorReport.Build`, no charts). The former `SElementStatsDisplay`
  component was removed 2026-10-07 (prefabs and sample scenes migrated; *Tools ▸ SimuLean ▸ Checks ▸ Remove Missing
  Scripts* cleans other scenes).
- **Results of a run.** `SimulationStatsReport` (a text report on `OnSimFinish`) was removed: a finished
  Play-mode run is captured as a `RunResults` before `OnSimFinish` (read-only) and published as the last results
  (`ResultsOutput.Publish`, the *Results* tab), and written in the clock's `exportOnFinish` formats (none by default);
  the clock's *Run Headless* has *Export* (the default formats of the settings), *Save* (the results store, §3.11) and *Show in Results*, and records
  time series every `seriesInterval` (`HeadlessSceneRun.Start(seriesInterval)`, `SeriesRecorder` in whole intervals
  over the progress slices). **A Play run records them too** when `seriesInterval` > 0 (default 0 =
  unchanged): the clock advances through `SeriesRecorder.RunTo(t, final: false)` (`AdvanceClock` at each interval boundary, then
  up to the frame's target — the same events run, tested frame by frame in `resultstests`), the
  last, shorter interval is closed in `PublishRun` and the series go with the run's results. `CaptureLive()` gives the
  run going on as `RunResults` (statistics now + a copy of the series so far), read-only, for views.
- **Utilisation** shown by the inspector panel and the element inspector uses the results' definition
  (`ResultMetrics.Utilization`: the mean slot utilisation of a Multi-Server, Combiner or Erection Dock, else the
  PROCESSING share; was the share of "busy" states).

### 4.6 Custom editors (`Editor/`)

**Inspector standard (`Editor/Inspectors/Framework/`; 2026-10-11: the header icon is
the element's prefab / Library preview, the Flow section shows the next elements as a numbered list with ✕ / + Add, the
routing and *Receive From*, the rest — routing asset, labels on exit / entry, admission, item statistics — in an
*Advanced* foldout that counts what is set; time fields show the clock's unit, `SimuLeanGUI.TimeUnit`).** `SElementInspector` is the base of the
element inspectors: a header (name, type · module from `[SimuLeanElement]`, the state badge in Play, the description)
with the problems found and a button that fixes them (`InspectorIssue`; shared checks: sends nowhere, no list chosen,
nothing feeds it, carried items without a location node; plus the element's own `CollectIssues`), then the sections
**Parameters → Flow → Resources → Reactions → Visual → Statistics** (collapsible, state per session; the Flow,
Resources and Reactions headers summarise their content; Statistics only in Play). *Reactions* (`ReactionsSection`)
lists the scene's `UnityReaction`s that listen to the element or act on it — each a link with its sentence —
and **+ Reaction** creates one listening to it. An element inspector implements `DrawParameters` (and
`DrawVisual`, `CollectIssues`); Flow / Resources come from `SElementEditorHelper.DrawFlow` / `DrawResourceFields`.
`SimuLeanGUI` holds the shared pieces (sections, header, issue boxes, read-only rows, number fields shown rounded —
`2.0999999642` reads `2.1` — and applied on Enter). `RandomProcessConfigDrawer` draws a distribution in two lines
(type + mean, then its parameters side by side); routing / admission (`OutputStrategyConfigDrawer`,
`InputStrategyConfigDrawer`) are one line with the rule's description as its tooltip, then only the fields the rule
needs (an expression shows its error). Every element follows it: Multi-Server, Source, Queue, Sink,
Combiner (its inputs in Flow), Stacker and Storage Rack (their fields through `DrawRemaining`, which splits scene
references / layout fields into Visual), and `GenericElementInspector` (fallback for any other `SElement`).
**No field left out:** after `DrawParameters` / `DrawVisual` the base draws every serialized field
not drawn by the shared sections nor declared by the inspector (`DrawnFields`, drawn by it; `HiddenFields`, hidden on
purpose) through `DrawRemaining` — unless the section called `DrawRemaining` itself at the place it chose. An inspector
that declares fields must declare every one: the EditMode test `InspectorCoverageTests` fails on a field neither declared
nor shared (and on a declared name that is not a field), so a new field is placed deliberately.
Element-specific hooks: `DrawFlowExtra`, `DrawPlayExtra`, `CollectIssues`. The `SElementConnectionGizmo` component is
hidden from the inspector (its switch is *Draw connections* in Visual).

`UnityMultiServerEditor` & `UnityCombinerEditor`
(conditional inspectors that hide irrelevant delay-strategy fields);
`UnitySourceEditor` (mode, edit-time data load / preview);
`UnityResourceTeamEditor` (members, *Add N*, live member state / utilisation / tasks; while editing,
`Tools/TeamMemberPreviews` shows each member's prefab at its spawn point (else, for a team that travels, its home node,
where the run starts it) — a `DontSave` child `MembersPreview`, removed in
Play, the team a `[SelectionBase]`; a crane member — `UnityResourceTeam.CraneRigOf`: its `ICraneRig`, else the
library gantry — is fitted around the team's `ReachPoints` as the run fits it, runway along their longer side, and is
rebuilt when one of them moves) and `ResourceUseDrawer` (an element's Process / Transport blocks, drawn by `SElementEditorHelper.DrawResources` after the connections; the query builder offers request / member fields); `UnitySimClockEditor` (live sim-time
readout); `UnityStorageRackEditor` (layout summary + *Rebuild Storage
Locations* button, and the *GameObject → SimuLean → Storage* menu that creates the
preconfigured conventional / floor / drive-in / push-back elements);
`UnityRollerConveyorEditor` (length/roller summary + *Rebuild
Conveyor* button, and the *GameObject → SimuLean → Transport → Roller Conveyor*
menu); `Tools/SimuLeanModelPostprocessor` (import conventions for the
script-built 3D model library — see §4.8; it only touches folders that hold their own
`<Folder>_Albedo.png`, so the older hand-made models keep their import settings);
`ModularKitEditors` (one inspector for every `ModularAssetBehaviour` —
summary + *Rebuild* button, play-mode crane test buttons — and the
*GameObject → SimuLean → Building* menu: Wall, Steel Column, Steel Beam (IPE), Crane
Runway, Overhead Crane, and a complete *Workshop (24 × 18 m with crane)* — see §4.7.1);
`UnitySimClockEditor` gained the calendar fields and the *Optional runtime features*
foldout (§4.3). `UnityStackerEditor` (live stacks / pick point / waiting stacks in
Play mode, *Release all stacks now*, and *GameObject → SimuLean → Stacking → Stacker* and
*→ Gantry Stacker*, the latter adding the `GantryStacker` model and a `GantryStackerView`).
Other inspectors: `UnityQueueEditor` (with *Release on command* and the Play-mode Release button), `UnitySinkEditor` (on `SElementInspector`);
`UnitySimListEditor` (fields, pushers / pullers; in Play the live content, pending pulls, statistics) with `ListDrawers`
(one-line list field, the pull query builder); `UnityDataTableEditor` (source, *Load*, inline CSV rows, preview);
`OperatorAppearanceEditor` (the look as a `PeopleLooks` list); `SElementPropertiesEditor`; `ElementInspectorPanelEditor`
(+ `ElementInspectorPanelSync`); transport `UnityWalkGridEditor` (bake, free / blocked count), `UnityTravelMatrixEditor`
(fill straight-line / network routes), `UnityTravelCalibrationEditor` (fit from the run, scene vs headless). Checks
(*Tools ▸ SimuLean ▸ Checks*, also buttons in Settings): *Run EditMode Tests
(Safe)* (`SimuLeanTestRunner`), *Remove Missing Scripts* (`MissingScriptsCheck`), *Camera Controller Check*.
`SimLogEditorBridge` routes `SimLog` to the console after every domain reload.

**Connection editing — the SimuLean Connect tool** (`Editor/Connections/`). One place
edits every connection of a model; it replaced the global C / D / V hotkeys of `SimuLeanConnectionTool`, the
per-element drawing of `SElementConnectionGizmo` (now only its *showConnections* switch) and the *Connect* mode of
`UnityTransportNetworkEditor`:
- `SceneConnections` (runtime, `Core/SceneConnections.cs`) — what exists, read-only (`All()`: flow `nextElements`,
  push / pull lists, combiner list inputs, element locations, network edges, team home nodes, downtime targets and
  downtime team targets — group Downtimes —, every use of a team — group Resources: process (with its
  requirements' teams and an Assembly Station's task requirements), setup crew (`SetupCrew`), transport and a downtime's
  repair crew, one connection per team (index −1 = the use's team, `i` = requirement `i`, `SceneConnections.UseRequirements`) —, a milk run's flows (origin →
  train → destination) and empties (a stop's empties origins → train → the empties' destination) — group Flow),
  with the kinds / groups / colours / labels; shared with the HUD's run-time view (§4.9).
- `ConnectionModel` (editor) — the same through `SceneConnections`, plus what can connect two objects
  (`OptionsFor`, kind inferred from the endpoint types; undirected kinds work in either drag direction; same scene
  only; already-existing connections refused with a reason; a flow needs an output at the start and
  `SElement.TakesConnections` at the end — an input, or a Combiner, whose inputs are its incoming connections although
  it has no input strategy: the tool refused it until 2026-10-13) and the edits (`Remove`, `Reverse`), all Undo-recorded and
  writing the same fields the inspectors show — the inspector lists stay the source of truth.
- `SimuLeanConnectTool` — an `EditorTool` (Tools overlay, shortcut *Shift+C*, `[Shortcut("SimuLean/Connect Tool")]`)
  that owns the Scene view's clicks while active (`AddDefaultControl`): drag to connect (a menu when more than one kind
  fits, Shift = one-way track), click a line + Delete to remove, right-click a line for delete / reverse / select,
  click an object to select it, Esc to cancel / leave. Picking needs no colliders (drawn markers for lists, teams,
  nodes and downtimes, then `HandleUtility.PickGameObject` on renderers). `SimuLeanConnectOverlay` (transient, shown
  with the tool) holds the legend, group filters and the hint line.
- `ConnectionDrawing` — the only drawer of connections (colour per group; dashed for resources / locations /
  downtimes; arrows for directed kinds); draws when the tool is off too, following the group filters, *Draw
  connections* and *Only of the selection* (EditorPrefs). The network group is off by default (the network draws its
  own edges). The SimuLean window's *Model* tab lists every connection by kind through `ConnectionModel`.

**Modelling in the Scene view** (editor-only, per-user switches in EditorPrefs, nothing changes a run):
- `SimuLeanPlaceTool` (`Editor/SceneTools/`, an `EditorTool`, *Shift+P*) — move and turn in one tool, on the floor: drag
  a selected object to slide the selection over its ground plane (`Plane(up, handle)`; height kept), drag the ring
  (`Handles.Disc` about the vertical) to turn it about the handle; snapping `MoveStep` = 0.5 m (the first object lands
  on the grid, the others keep their offsets) and `AngleStep` = 15°, off with *Ctrl* or the *Snap* switch; a click on
  anything else is Unity's (no default control); right-click (`ContextClick`) a selected object for turn ±90° / 180°,
  Connect…, Duplicate, Delete, Snap, Fit Floor. *Auto* (on): when the settled selection (`ElementSelection.Settled`)
  is only SimuLean objects and Unity's Move / Rotate / Transform tool is active, the Place tool takes over, and Move
  comes back when the selection is something else. Library drops land on the grid too (`SimuLeanWindow.OnGrid`).
- `SimuLeanSceneOverlay` (`[Overlay(SceneView, "SimuLean", true)]`, a `ToolbarOverlay`) — Place, Connect, Validate
  (`SimuLeanCommands.Validate`: the answer as a notification, errors / warnings in the Console), Floor (Fit Floor to
  Model), the switches Snap, Links (`ConnectionModel.ShowWhenIdle`) and Parts (`ElementSelection.EditParts`) — each
  follows its setting however it is changed (`ToggleFollow`: read every 250 ms while shown; the Tools menu toggled
  Edit Element Parts without the bar knowing) — and the ⋯ menu (Set Up Scene, Fit Floor, Organize Hierarchy,
  Modelling Layout). Library maintenance (rebuilding the people animations and the example scenes, the example floor)
  is under *Tools ▸ SimuLean ▸ Developer*.
- `SimuLeanMainToolbar` (`Editor/Toolbar/`, `[MainToolbarElement]`, next to Play) — *Game / Scene* brings the other
  view to the front (un-maximizing a maximized Game view; the label follows `EditorWindow.windowFocusChanged`) and the
  *Maximize* toggle plays with the Game view maximized (set on `EnteredPlayMode`, restored on exit).
- `SimuLeanLayout` — *Tools ▸ SimuLean ▸ Modelling Layout*: Unity's `Default.wlt` loaded (after a confirmation) and the
  SimuLean window docked in front of the Project browser.
- `HierarchyBadges` (`Editor/Hierarchy/`, `hierarchyWindowItemByEntityIdOnGUI`) — each SimuLean object's library name
  in grey at the right of its row and, in Play, a dot with the element's `CurrentState` (green working, red blocked,
  orange stopped / down, blue waiting for a resource, grey idle), repainted twice a second; *Tools ▸ SimuLean ▸
  Hierarchy Badges* hides them.

**Automatic scene hierarchy** (`Editor/Hierarchy/ModelHierarchy.cs`): a root `SimuLean Model`
with fixed groups by function — `Data` (data tables, the block breakdown), `Flow` (every element), `Resources`, `Lists`,
`Transport`, `Downtimes`, `Logic` (reactions), `Experiments`, `Runtime` (clock, views and analysis tools). The group comes from the library attributes (`[SimuLeanElement]` → Flow,
`[SimuLeanTool]`'s category; `OnMainCamera` tools and cameras stay put), so a new type needs nothing. An
`ObjectChangeEvents` hook places whatever is created at the scene root with such a component (library, *Set Up
Scene*, a prefab dragged in…; *Tools ▸ SimuLean ▸ Auto Organize Hierarchy*, per user, default on); *Tools ▸ SimuLean ▸
Organize Hierarchy* tidies the open scenes. Only scene-root objects move (what the user nests stays), with their world
placement kept; groups sit at the origin. Nothing in the build path depends on the hierarchy — except the combiner's
input order of connections not in `inputOrder` (hierarchy order); the sample scenes all set it, and connecting with the
Connect tool or `simulean_connect` fills it in connection order.

**Selecting an element, not its parts** (`Editor/Hierarchy/ElementSelection.cs`): `SElement` is a
`[SelectionBase]`, and a `Selection.selectionChanged` hook replaces any selected part of an element (its model, pads,
work points, a rack's slot anchors…) — clicked in the Scene view or the Hierarchy — by its owner, the nearest object
up the hierarchy with an element or a library tool (`[SimuLeanElement]` / `[SimuLeanTool]`); objects outside any
element are left alone, and nothing is redirected in Play mode. *Tools ▸ SimuLean ▸ Edit Element Parts* (EditorPrefs,
per user, off) turns it off to edit the parts. Before, a click went to the mesh deep in the element and it was easy
to take the element apart by mistake.

#### 4.6.1 The SimuLean window and the element / tool registry

*Tools ▸ SimuLean ▸ Open SimuLean* (`Editor/Window/SimuLeanWindow*.cs`, Ctrl+Shift+L) is the one entry point of the
modelling interface (it replaced the Library, Toolbox and Optimizer windows). It is generated from two metadata
attributes (`Core/SimuLeanLibraryAttributes.cs`, found with `TypeCache`) and the `GameObject/SimuLean/…` menu items:

- **`[SimuLeanElement(module, name)]`** on every model-element wrapper (`SElement`), grouped by module
  (`SimuLeanModules`); optional `Prefab` under `Prefabs/Elements`, otherwise a bare GameObject with the component.
- **`[SimuLeanTool(category, name)]`** on scene components that are not model elements (`SimuLeanToolCategories`:
  Simulation — clock; Resources — resource teams; Lists; Downtime; Transport — network, nodes, travel matrix, walk
  grid, runtime NavMesh; Analysis — dashboard (`UnityDashboard`), travel calibration; Experiments — Experimenter,
  Optimizer, model extractor; Visualization — camera controller, floor plan, visual detail, runtime HUD, element
  inspector panel, element label (`UnityElementLabel`, §4.9), speed profile and recorder (`SimSpeedProfile`,
  `SimRecorder`, §4.9), floor zone (`UnityFloorZone`, §4.9), pathway arrows (`UnityPathwayArrows`, §4.9); **Data** — *Data Table* (`UnityDataTable`) and the naval library's
  **`UnityBlockBreakdown`**, its own Library group and hierarchy group). `SimuLeanElementAttribute.Icon` / `SimuLeanToolAttribute.Icon`: the tile
  of an element or tool without a prefab (a model / prefab preview or a texture). `Singleton` = one per scene; `OnMainCamera` = added to the main camera.
  `UnityBlockBreakdown` (`Naval/`, inspector `Editor/Naval/`): the breakdown's table / query and database, the release
  (interval, start, lead) and the three sources it feeds; *Import Excel / CSV…* (into the table), *Check* (errors,
  warnings, summary), *Apply to sources* (Schedule mode, `tableSource = naval:<part>:<table>`, a preview copy loaded).
  `UnitySource.tableSource` / `tableDatabase`: a table read at run start (`TableForRun`) instead of the file / copy.

**The model frame.** One orientation for every element view and every station / element model of the
library: **flow along local +X** (in on −X, out on +X) and **the front** — operator side, control panel, stack light,
doors, screens — **towards local −Z**, so an element placed unturned (`SimuLeanWindow.Place`, `simulean_add_element`)
faces the starting view (it looks along +Z) with its flow from left to right; items are laid along +X, *behind* is +Z.
The models export in it: the Blender stations (CNC, Assembly Bench, Inspection Station, Paint Booth, Palletizer, Weld
Positioner, Unpacking Station, Picking Station, authored with their front on −Y) turn 180° at export (`simulean_kit.export_fbx(turn)`,
`station_parts.STATION_TURN`; nodes keep identity rotations), the hand-made models are turned on import
(`Editor/Tools/HandMadeModelFrame`: Source / Sink stations −90°, the assembly table and the classic workstation 180°).
Carriers, people, cranes and the hospital bed keep the vehicle frame (front = travel = +Z: they move along their
forward). The hospital rooms and furniture follow the frame since 2026-11-03: the reception desk (public side), the
consultation box (curtain / corridor side) and the hospital door (the building's inside, where patients appear —
its `ItemSocket`) export turned 180° like the stations, so a healthcare preset placed unturned shows its front and the
composites (`HealthcarePresets`) lay their elements along +X unturned; a queue's **Seats** layout seats its people
facing its front, −Z (`ItemLayout`), and the Waiting Room preset turns its chair strip +90° under the queue (the modular
kits — building, fence, hospital wall, waiting chairs — keep the kit frame, tiling along +Z, the run of
`UnityModularStrip`; a wall has no front). A straight **Paced Line** runs along its +X (it ran along +Z), its line side
to the right of the flow, −Z (`UnityPacedLine.PoseAt`; vehicles keep the vehicle frame, their +Z along the travel). The
load units are unchanged. The views follow it: a lane's items run along −X to the
exit at its origin, lanes towards −Z (`UnityLengthLimitedQueue`; its rollers and floor marking turned onto it by
`FitTo`), a Combiner's waiting components stand behind (+Z), a Separator's pieces behind the work point, a Stacker's row
along X with the pick point on −X (the gantry model turned −90° under it: its runway is its own Z), a sink's kept row
behind it; items lying on a flow axis turn by `ItemVisuals.AlongFlow` (their +Z along the flow); the staff position
(`StationModel.StaffPosition`) is in front, −Z. This replaced the turn the Library gave elements and presets when placed (an
attribute's place yaw, removed). Scenes and prefabs saved before it were migrated so they look the same
(`Editor/Tools/ModelFrameMigration`, one `Step` per change, each run once over the scenes saved before it — *Tools ▸
SimuLean ▸ Developer ▸ Migrate Scenes to the Model Frame* (`Stations`) and *… Hospital Furniture and Paced
Line* (`Hospital`, the three hospital models, Seats-layout queues with seats 180°, Paced Lines +90°), *Queue Lines* (`QueueLines`, Line-layout queues −90°, so their lines stay where they were);
`MigrateScene` does one scene):
every object whose content turned gets the opposite turn on its own rotation and its children are turned with it, and
the element fields that are offsets in its own axes (a Separator's slots, a Sequencing Station's offsets, a Stacker's
positions) are turned. What the frame changes on a migrated scene: a station's slots / assemblies now count from its
left as seen from its front (they counted from the right), and the staff point of an element without a staff socket and
not turned 180° (queues, sources, sinks, lanes, racks) is on its −Z side — a straight-travel team's distances to them
change. An element without a prefab shows `SimuLeanElementAttribute.Model`
(a library model under `Art/Models/`, a `StationModel` child as the presets'): the Separator the `UnpackingStation`
(a decanting station: the container on its roller section — the view's `itemPosition`, bound to the model's
`WorkSocket`, `firstSlot` an offset from it; containers lie along the flow, `ItemVisuals.AlongFlow` — and the pieces
packed on its worktop, the model's `PiecesSocket`, in `piecesArea` by `UnityCombiner.PackOnTop`), the Picking Station
the `PickingStation` (a pick-to-light workstation: the order container on `itemPosition` = its `WorkSocket`, along the
flow; the stock in its bins `StockSocket_0..11`, one SKU per bin in the order the SKUs first arrive, packed in
`binSize`; the picked items travel into the container during the pick time and ride on it — the stock read through the
read-only `SkuStockPort.Stored`), the Sequencing Station a work table, the Delay, Stacker and Line-Side Unpacker a buffer position, the Pick Face a buffer box, the
Call-Off Point a post — they were placed as empty objects. `InLibrary = false` leaves an element out of the Library
(still a type of the commands): the Length-Limited Lane, placed through its presets **Conveyor** (with speed) and
**Accumulation Lane** (no speed; its outline painted on the floor by `UnityLaneMarking`, which follows the lane's
length like the conveyor's rollers). A preset is registered for Undo once complete (`LibraryPresets.Select` and the
other preset builders), so undoing it removes it whole — registered first, its later parts were left dangling.

**Variants.** A preset that is another look of one element carries `[SimuLeanVariantOf(type)]` and is
drawn folded under that element's tile (its ▾ n opens them in place, marked by a side bar; `SimuLeanWindow.FoldVariants`,
per window session; a search shows every match on its own): the CNC Machine, Paint Booth, Inspection
Station and Weld Positioner under the Multi-Server; the Assembly Bench, Palletizer and Tote Packer under the Combiner;
the Stacker with Gantry (an automatic stacker with its own gantry — the same Stacker, its cycle animated; a hall crane
that stacks is the Stacker's Crane mode with a crane team) under the Stacker; the racks under the Storage Rack; the Tugger
Train under the Milk Run; the resource teams in families (`[SimuLeanVariantOf(typeof(UnityResourceTeam), "<family>")]`: its first preset by
menu priority is the tile) — People (Operators ▾ the porters), Vehicles (Forklifts ▾ Pallet Trucks, SPMT
Transporters), Cranes (Hall Crane ▾ Stacker Cranes, Goliath and Level-Luffing Cranes), Robots (Gripper ▾ Vacuum,
Welding, Welding Gantry, Welding Robots); the AGV Fleet and the Resource Team stay tiles of their own; the Healthcare pack likewise — Waiting (Waiting Room ▾ Waiting Line, Waiting List), Rooms
(Consultation Room ▾ Triage, Diagnostic Test), Away (Home Test ▾ Treatment at Home, Report), Pathways (Outpatient Clinic ▾
Home Diagnostic Test, Follow-up Loop), Decor (its pieces under the first); Patient Arrivals, Discharge, Clinic Agenda,
Reception Desk, Equipment Pool and the dashboard on their own; the Shipbuilding pack — Steel (Steel Stockyard ▾ Profile Rack), Workshops
(Plate Cutting ▾ Sub-Assembly Cell, Turning, Outfitting, Blasting & Painting), Assembly (Block Assembly Bay ▾ Sub-Block
Welding), Yards (Block Yard ▾ Sub-Block Yard), the Slipway and the Building Dock under the Erection Dock element,
Templates (Block Process ▾ Panel Line); its icons are rendered by `Editor/Tools/LibraryIconRenderer` (each station with
the work it handles, front three-quarters, transparent: `Editor/Icons/Shipbuilding/`, *Tools ▸ SimuLean ▸ Developer ▸
Render Shipbuilding Icons*) instead of the bare model previews, which several presets shared; the Accumulation Lane under the Conveyor (the Length-Limited Lane is out of the Library: its
first variant draws it). A variant of a sector pack folds under its Core element while the pack is shown. Within a
section the tiles follow `Entry.SectionOrder` (an element's or tool's `Order`, a preset's menu priority): Source, Sink ·
Queue, Conveyor, Milk Run · Multi-Server, Combiner, Separator, Delay, Assembly Station, Picking Station, Stacker ·
Storage Rack, Storage System · the resources — Operators, Forklifts, Hall Crane, AGV Fleet, the robots, Resource Team —
then the paths. The variants come with sensible defaults (`LibraryPresets.Takes` /
`Assembles` / `Packer`: a typical time in seconds of the scene clock's unit — CNC 300 s, Paint Booth
900 s, Inspection 30 s, Weld Positioner 240 s, Assembly Bench 120 s, Palletizer 60 s, Tote Packer 30 s).

**Packs.** Every Library entry has a pack (`SimuLeanPacks`): **Core** — the general elements, resources,
transport, storage, the building kit and the model tools — is always shown; **Manufacturing**, **Warehousing**,
**Healthcare** and **Shipbuilding** are sector libraries shown or hidden by a row of switches at the top of the Library
tab (the project's choice, `SimuLeanProjectSettings.shownPacks` in `ProjectSettings/SimuLeanSettings.json`; default
Manufacturing and Warehousing). An element's pack is its module's (`SimuLeanPacks.OfModule`) unless
`[SimuLeanElement(Pack = …)]` says otherwise; a tool's is Core unless `[SimuLeanTool(Pack = …)]` (the block breakdown:
Shipbuilding); a preset's is its menu folder's (`SimuLeanPacks.OfPresetFolder`) unless `[SimuLeanPresetPack]` (the
hospital porters among the teams). A pack that a component of an open scene belongs to is shown while it is there
(`SimuLeanWindow.ScenePacks`, looked up every 2 s; nothing written), and a search names the matches of hidden packs with
a *Show* button. Hiding only filters the window: the types still build and run, the `GameObject/SimuLean/…` menus
keep every preset, and `simulean_types` lists every entry with its `pack`. The Library also has an **Elements | Tools**
switch: Tools holds the Lists, Downtimes, Logic and Data tool groups (`Entry.IsTool`). Elements draws core in five
sections (`SimuLeanWindow.CoreSections`, `SectionOf`: by the element's module, the preset's menu folder or the tool's
group) — *Arrivals & exits*, *Queues & conveyors*, *Stations*, *Storage*, *Resources & paths* (every team preset, the
empty Resource Team, a crane team being a team like any other, and the transport network, nodes, travel matrix, walk
grid and NavMesh) —, then one block per shown pack. **Visuals**: a preset that is decoration only — the
`Building/` kit, a pack's `…/Decor/` folder, or `[SimuLeanVisual]` (the safety fence) — has `Entry.Visual`; the
Library lists visuals only when the *Visuals* switch is on (`SimuLeanProjectSettings.showVisuals`, off by default):
core's in a *Visuals* section, each pack's at the end of its block. The **Conveyor** preset (`Queues/`) is a
length-limited lane (the element that simulates it) with a child `UnityRollerConveyor` (visual) that follows the
lane's length and spacing (`UnityRollerConveyor.FitTo`, from the lane's `OnValidate` in the editor); it conveys
: `speed` = `UnityRollerConveyorEditor.DefaultSpeed` 0.3 m/s in the clock's time unit, so an item takes the
length ÷ the speed to reach the end (before, speed 0: an accumulation lane, items did not travel). Each switch keeps
its own scroll position. Tests: `LibraryPacksTests`.

Tabs (Models, Library, Model, Data, Fit, Experiments, Results, Settings — callers name them, `TabIndex("Fit")`, and
`OnGUI` dispatches by name), by what a thing is for:
- **Models** (`SimuLeanWindow.Models.cs`) — model management, all through the editor service
  `Editor/Models/SimuLeanModels.cs` (also the commands', §4.11, so people and agents behave the same). A **model** is a
  folder `<modelsFolder>/<Name>/` (project setting `modelsFolder`, default `Assets/SimuLeanModels`, under `Assets`; git-ignored in this repository, whose examples stay in `Assets/Scenes`;
  Settings ▸ Models) holding its scene `<Name>.unity`, its **model card** *ModelCard.asset* (`SimuLeanModelCard`,
  `Core/`, a `ScriptableObject`: display name, description, author, scene path, `copiedFrom`, created / modified, and
  the notes for agents `represents`, `kpis`, `parameters`, `assumptions`; pure metadata, nothing reads it at run time),
  its thumbnail *Thumbnail.png* and its own data. The tab lists the models recent first (thumbnail, name, description,
  modified, *copy of …*; a search box) with **New model** (a name; the folder, a new scene set up as any new scene —
  `SimuLeanSceneSetup.SetUpScene` — saved at once, the card, opened), *Open*, *Duplicate* (the whole folder, the scene
  renamed, a free numbered name proposed), *Rename* (folder, scene and card; the scene may be open, not unsaved),
  *Card* (selects the card asset: its inspector edits it) and *Delete* (a confirmation, then
  `AssetDatabase.MoveAssetToTrash`: the OS trash, never a hard delete). **Examples** are the scenes under
  `Assets/Scenes` except `Neutral.unity`, by folder (*Basics* for the root ones, *Library Examples*,
  *Validation*; the resource test fixtures live in `Assets/SimuLean.Unity/Editor/Tests/Scenes/` since 2026-11-09, so they
  are not listed); an example is never opened itself: *Open* copies it the first time into a new model
  `<modelsFolder>/<Example>/` — with the assets of its own folders beside it (`<Example>/`, `<Example>_*/`, e.g.
  `Factory_3DModels_Charts`), every GUID of an original replaced by its copy's in the copied text assets
  (`RemapReferences`; the project serialises as text) — whose card's `copiedFrom` is the example; once a copy exists it
  offers *Open my copy* / *New copy* (numbered, `Name_2`). **An example's card** is
  `<Example>/ModelCard.asset` in the example's own folder (`SimuLeanModels.ExampleCardPath`; the examples share their
  folder, so it cannot sit beside the scene): `CardOf(scene)` — the model's card beside the scene, else the example's —
  gives it to `simulean_card` and `simulean_model`'s `info` while the example is open, the tab shows its display name and
  description in the example's row (`Example.Card`; `simulean_models list` too), and a copy takes its text (description
  and the notes for agents) into the new model's own card (the copied `<Example>/ModelCard.asset` removed), with
  `copiedFrom` filled. The save hook and the thumbnail only look beside the scene, so saving an example touches neither. Since
  2026-11-09 every example has a card (description, what it represents, KPIs with their seeded or theoretical values,
  parameters, assumptions); `Factory_3DModels`'s says it is a gallery of the 3D models, not a study model. **The card's inspector**
  (`SimuLeanModelCardEditor`) draws the card's fields, then a read-only *Scripts / Logic* section: the
  card's `scripts` of `simulean_card` (`SimuLeanCommands.CardScripts` → `LogicReport.Scripts`, §3.25 — the summary, the
  decisions per rung, each choice rule with owner, setting, label, Why, reads, parameters with their catalog keys and
  source hash) and *Open Model Logic*; computed only while the card's `scenePath` is the open scene (else a note: open the
  model), once per open scene and on *Refresh* (marked stale on a hierarchy change), never per repaint; drawing it
  dirties neither the card nor the scene (EditMode `ModelCardEditorTests`). Whatever opens a scene asks first with Unity's save prompt
  (`SaveCurrentModifiedScenesIfUserWantsTo`); the service never prompts. **On save** (`EditorSceneManager.sceneSaved`,
  not in batch mode) a scene with a card beside it gets its `modified` date and, on the next editor tick when it is the
  active scene, a 320 × 180 thumbnail (its model framed and drawn — or the whole scene — by `ModelLayout.OfModel`,
  `SimuLeanCommands.Frame` / `RenderPng`: the code of `simulean_capture`). Opening a model never dirties it. **Results per model**:
  `ResultsOutput.Folder(projectRoot, model)` — `<resultsFolder>/<Name>/` when the result set's model (the scene's name)
  is one of the project's models, else the results folder; the exports (`ResultsOutput.Export`) and the Results tab's
  *Folder* use it (the `.gitignore` stays at the top of the results folder). EditMode `SimuLeanModelsTests` (under a
  temporary models folder, `SimuLeanModels.ModelsFolderOverride`).
- **Library** — what is placed to build a model, as tiles colour-coded like the connections (elements = flow cyan,
  resources orange, lists purple, transport green, downtimes red, presets grey) with the prefab preview or initials
  and the description as tooltip: **click** places at the Scene view's centre, **drag** places where dropped. Groups:
  Data (`UnityDataTable`, the block breakdown), Elements (by module), Resources, Lists, Transport, Downtimes
  (the tools of those categories) and **Presets** (every
  `GameObject/SimuLean/…` menu item — racks, conveyor, stackers, building kit, the library's ready-made objects
  of `LibraryPresets` (Manufacturing / Teams), `HealthcarePresets` (Healthcare) and `NavalPresets` (Shipbuilding) —; `Place` calls the menu method directly (through
  `EditorApplication.ExecuteMenuItem` a `GameObject ▸ …` item opened the Hierarchy's rename of the new object, which,
  committed later, renamed another row, e.g. the scene's light; `ExecuteMenuItem` stays the fallback). A preset method
  selects its object as its last step (`Place` takes the selection as the created object). A preset's tile icon is
  **`[SimuLeanPresetIcon(assetPath)]`** on its menu method: the asset preview of a model / prefab, or a
  texture as it is (`Editor/Icons/`, e.g. the procedural roller conveyor's); `LibraryPresetsTests` requires one on every
  preset. What is created lands in its hierarchy group (`ModelHierarchy`).
- **Library presets** (`Editor/LibraryPresets.cs`) — the library's objects: each is an element
  with its default behaviour and a model, visible in the scene as soon as it is placed. *Manufacturing*: CNC Machine,
  Paint Booth, Inspection Station, Weld Positioner (MultiServer), Assembly Bench, Palletizer (Combiner: 12
  cartons 400 × 300 × 250, 2 layers of 3 × 2 on a Euro pallet, *OnLoad*), Tote Packer (Combiner: 4 `SmallCarton` 255 ×
  170 × 120, one 2 × 2 layer in a tote 600 × 400, *OnLoad*; on the assembly table),
  Safety Fence (`UnityModularStrip` of the `SafetyFence` kit). *Healthcare* (`Editor/Healthcare/HealthcarePresets.cs`,
  2026-10-09): Patient Arrivals (was *Referrals*; element *Arrivals*: Source of
  `Prefabs/Items/Patient`, exponential inter-arrival, the **hospital door** crossed along the flow — `StationModel.modelYaw`
  −90°, its inside to +X; the Discharge door +90° — `Art/Models/HospitalDoor`, automatic sliding glass doors whose leaves `Door_L` / `Door_R` a **`SlidingDoorView`** (`Visuals/SlidingDoorView.cs`, read-only: the element's `ItemEntered` / `ItemExited` events) opens as patients pass — and, with `openWhenNear` (2.5 m), when a shown person comes near: a door with no element,
  e.g. a building's entrance, opens for walking patients (`PersonAnimator.AnyVisibleNear`); patients appear at its `ItemSocket`), Clinic Agenda (`UnityAgenda`,
  virtual look, a hidden-items count), Waiting List (at home) (Queue, virtual look *At home*, count), Waiting Room (Queue in the
  **Seats** layout over a `WaitingChairs` strip, one seat per place: each patient takes the first free seat and keeps it until it leaves (`ItemLayout.SeatsTaken`: the others do not move up when one goes), sits with the `SitIdle` clip, those beyond the seats stand; the queue inspector shows seats vs places and *Fit Chairs to Capacity* rebuilds the strip, `UnityQueueEditor.FitChairs`), Waiting Line (Queue, *Line* layout along +X, no pad, its outline painted on the floor by a `UnityLaneMarking` fitted to its `lineLength`),
  Reception Desk (MultiServer, 2 places), Consultation Room (MultiServer, consultation box), Home Test and Treatment at
  Home (`UnityDelay`, virtual look), Report (not face to face) (MultiServer, virtual look), Triage (MultiServer; entry label `triage = pick(0.1, 1, 0.3, 2, 0.6, 3)`), Diagnostic Test (hospital) (MultiServer with its own equipment pool of 2 as the process team), Discharge (Sink with the
  item statistic `time_in_system = age`, the hospital door), Equipment Pool (a team of 3 devices without views; staff are the Operators
  team with the Doctor / Nurse look, not presets); composites
  **Outpatient Clinic** (referrals → agenda → waiting room → consultation room with a doctor → discharge) and **Home
  Diagnostic Test** (device desk lending a device of the pool until the device return — `AtElement` — → home test →
  return; `valid = rand() < 0.9` on entry to the desk, invalid tests back to the desk's line, valid ones to the report) and **Follow-up Loop** (referrals → treatment at home → review line → review — `reviews = (reviews ?? 0) + 1`, `continue = rand() < 0.6` — → back to the treatment or discharge).
  **Healthcare Dashboard** (a `UnityDashboard`, top right, with the library charts `Charts/Healthcare/*.asset` created once by
  `HealthcarePresets.HealthcareCharts`: patients waiting per list, time in system P90 at the discharges, agenda slots
  booked, lead time to the appointment — elements found by the presets' names). EditMode test `HealthcarePresetsTests`
  (the composites run headless; the dashboard's charts build from a clinic run). *Teams*: a
  `UnityResourceTeam` with its member prefab — Operators, Bed / Stretcher / Wheelchair Porters, AGV Fleet, Forklifts, Pallet Trucks, Robots (Gripper / Vacuum / Welding), and the *Token Pool* (1f: three abstract members, no prefab — a team as a semaphore, Core) — a doctor, nurse or fitter team is an Operators
  team whose member prefab (`Doctor`, `Nurse`, … variants of `Operator`) or `OperatorAppearance.look` gives the role.
  **Member kinds:** every team preset names a kind of `LibraryPresets.MemberKinds` (operator, doctor, nurse,
  fitter, welder, painter, outfitter, the porters, agv, forklift, pallet truck, tugger — the `TowTractor` model —,
  stacker / hall / goliath / level-luffing crane, spmt, welding gantry, robot / vacuum / welding robot; aliases and
  plurals accepted by `KindOf`), the one place that maps a kind to its member prefab and speed (`ApplyKind`: look +
  speed + `Physical` units + an arm's `Home` node and fence; `ApplyLook`: the prefab only). A team created **without a
  kind** never gets a box: the plain *Resource Team* placed by the window's `Place` and `simulean_add_team` /
  `simulean_apply add_team` without `kind` take **`DefaultKind(travel)`**'s look — *Network* → AGV (vehicles on the
  network), *Axes* → hall crane, any other travel (Straight, Grid, Matrix, Unity physics, Abstract) → operator — and keep
  the team's default speed; `simulean_add_team` first reads a kind from the team's name (`TryKindOf`: *AGVs*,
  *Forklifts*…). Presets that want no look set it explicitly (Token Pool, the healthcare Equipment Pool). Before this,
  `simulean_add_team` placed the plain Resource Team with no prefab and its members were boxes. Old scenes keep their
  saved prefab (none = a box at run time); the example `Setup_Changeover` was migrated to the operator. EditMode test:
  `AgentInterfaceCommandsTests.AddTeamGivesTheMembersTheirKindsModelNotABox`.
  Stations get a **`StationModel`** and a
  **`MachineStateView`** (§4.7). EditMode test: `LibraryPresetsTests` (placed through `Place`: element + visible model
  bound to the item position + state view; model switching; seats; teams; porters; containers; lamp rules; no other
  object renamed).
- **Deleted elements**: `ConnectionCleaner` removes the empty Next Elements entries a
  destroyed GameObject leaves (undoable, logged), so index-based routing stays right; the extraction warns about one.
  It cleans inside the `ObjectChangeEvents.changesPublished` callback (a `delayCall` alone never ran while
  the editor was in the background driven by the MCP), with the `delayCall` kept as a fallback; EditMode
  `ConnectionCleanerTests.AnUndoableDeleteIsCleanedByTheHook` goes through the real event.
- **Model** — the open scenes' SimuLean objects by hierarchy group (counts, select, remove, a ⚠ count from
  `ElementChecks` per element) and every connection (`ConnectionModel`, select / remove, the Connect tool button).
- **Data** — the project database as the hub of the model's data: its path (the project setting
  `databasePath`, default `SimuLeanData/simulean.db`; `ResultsOutput.DatabasePath` is the one place it is resolved —
  data tables, downtime timetables, Picking Station pick lists, the Fit tab and SQLite exports all use it), its tables
  (`ProjectDatabase.ListTables`; preview; *Use in scene* adds a `UnityDataTable` reading it by SQL), **import** of a
  CSV / `.data` / Excel file into a table (`ProjectDatabase.ImportTable`: REAL columns when every non-empty cell is a
  number, else TEXT, empty = NULL; replace or append; safe identifiers only), an SQL console (`QueryTable`), and the
  scene's data tables (select, load, load all). EditMode `DataTableSceneTests`; `datatests`.
- **Fit** — distribution fitting: data from pasted numbers, a column of a scene data table, a CSV / Excel / `.data` file (`TableFiles`), an
  SQL query on the project database (`ProjectDatabaseRegistry`, the project database by default) or the
  times of a headless run of the active scene (`RunData`); the summary, a histogram (bars as densities) with the
  selected candidate's density, the candidates ranked by Anderson–Darling or Kolmogorov–Smirnov (χ² for count data)
  with their statistics, p-values, AIC and verdict colour; **Insert** writes the chosen distribution into the
  `RandomProcessConfig` the tab was opened for (type and parameters, Undo) — every distribution field has a
  right-click *Fit from data…* (`FitFieldMenu`, `EditorApplication.contextualPropertyMenu`) —, **Copy spec** puts its
  `Type~p1~p2` on the clipboard.
- **Experiments** — each Experimenter / Optimizer with ▶ Run / ■ Stop, status, a summary and the best result
  (set-up stays in their inspectors), then the Analysis / Experiments tools (create / select).
- **Results** — first a **Summary** (per element in, out, average content and stay,
  utilisation — only what applies: a source's out, a sink's in, no utilisation for flow-only types, `ResultMetrics.IsFlowOnly`;
  per team members, utilisation, waits; per list; mean ± CI with replications; times in the run's unit,
  `RunInfo.TimeUnitSeconds` / metadata `time_unit_seconds`), then charts of a result set (saved charts with nothing to
  draw for the shown results are hidden, sources / queues / sinks left out of the time per state): the last one of the session (`ResultsOutput.Last`, followed while
  *Latest* is on; the Experimenter, the Optimizer, a headless run and a finished Play run publish theirs — also with
  no export format) or a `.json` export opened from disk. It reads only the set's long tables, so both draw the same:
  *Metrics by scenario* (bars, mean ± the t-based confidence interval), *Statistics by object* (one statistic of every
  object of a kind, per scenario), *Time per state* (stacked shares, the inspector's state colours) and *Over time*
  (the series, one line per run; indexed once per set) and *Timeline* (states or items over time, one run) — each an
  engine chart (`ChartQuery` → `ChartRenderer`, §3.11) rendered at the screen's pixel size, cached until the results,
  the choice or the size change, with *PNG…* and *SVG…*; then
  *Charts*: every `ChartDefinition` asset drawn for the shown results (or its stored sets), *Edit*, *PNG…*, *New chart*.
  *Export* (the default formats), *Save* (keeps the shown set in
  the results store, §3.11 — *Saved #id* once stored), *Stored* (`SimuLeanWindow.Stored.cs`: the store's
  sets newest first with kind, model, date and counts — *Open*, *Rename*, *Delete* with a confirmation, tick several and
  *Compare ticked*, shown through `ResultSet.Compare`), *Folder*.
- **Settings** — *Set Up Scene*, *Organize Hierarchy*, **Models** (project: the models folder, under `Assets`), the switches *Set up new scenes automatically* (project) and
  *Put new objects in their hierarchy group* (user), the clock (select / create), the run-time views as on / off
  switches (singletons; added to the clock's object, the camera controller to the camera; HUD and panel wired), the
  floor plan (add / select), **Results** (project: the results folder — relative to the project or absolute —, the
  confidence level of the intervals, the formats of an export made by hand, *Store results* = `storeResults`, *Run outside the editor* =
  `runOutsideEditor`, §4.4), *Reimport 3D Model Library* and every
  `Tools/SimuLean/Checks/…` item. The project settings are one file, `ProjectSettings/SimuLeanSettings.json`
  (`SimuLeanProjectSettings`, runtime assembly: also read by `ResultsOutput` at run time in the editor).

The attributes are pure metadata (nothing happens at run time). The *Tools ▸ SimuLean* menu keeps only *Open
SimuLean*, *Set Up Scene*, *Checks* and *Reimport 3D Model Library*.

**Scene set-up** (`Editor/Tools/SimuLeanSceneSetup.cs`): a modelling scene comes ready —
one `UnitySimClock` with its default runtime views on the same object (`SimRuntimeHud`,
`ElementInspectorPanel`, `SimVisualDetail`) and a `SimCameraController` on the main camera, wired to
each other, and (`Editor/Tools/SceneFloor.cs`) a **floor** of 100 × 100 m at the origin when the scene has
no ground (a `SimuLeanFloor`, a root called *Floor* / *Ground*, or a flat renderer of 20 m or more at ground level):
a quad with no collider whose `SimuLeanFloor` (`Visuals/`, `[ExecuteAlways]`) sets the grid texture's tiling and
offset per renderer (`MaterialPropertyBlock`) so the 1 m lines, heavier every `gridCell` = 5 m, sit on world metres
at any size (material `Art/Floor/FloorGrid.mat`). *Tools ▸ SimuLean ▸ Fit Floor to Model* resizes it to everything
drawn plus a 5 m margin, on whole cells, at least 20 m. The examples keep their own floors (`ExampleFloor`). It runs from *Tools ▸ SimuLean ▸ Set Up Scene*, when the clock is created from the
library or added by hand (`ObjectFactory.componentWasAdded`), automatically in every new scene
(`EditorSceneManager.newSceneCreated`) and once per project, on the first load, in the open scene;
the automatic part is the project setting *Set up new scenes automatically* (SimuLean window ▸ Settings)
(`ProjectSettings/SimuLeanSettings.json`). Editor-only and never in batch mode; idempotent
(existing components are kept and only wired). With several scenes open it works on the scene it sets up (its own
clock, camera and views; it used to find another scene's clock), and every reference stays inside that
scene: a view's clock / camera found in another scene is rewired to its own. The HUD and the
inspector panel also fill their clock / camera references in `Reset`, from their own scene
(`UnityCompat.FindInScene<T>(scene)`; `FindAny` answered the open scene's when a model was made beside it,
`simulean_models action=new open=false`, and Unity dropped those cross-scene references on save). **`ElementInspectorPanel.elements`** (*Inspectable Elements*; only
they open the panel when clicked) is kept filled with every `SElement` of the panel's scene
(`autoCollectElements`, default on): in the editor by `ElementInspectorPanelSync` on every hierarchy
change (it also drops deleted elements), and at `Awake` for elements created at run time. Elements
the user removes by hand, or empties with *Clear*, are remembered as exclusions and never re-added
(*Add All Scene Elements* forgets them); adding one back by hand lifts its exclusion. The
`contributors` list is optional and manual (it was auto-collected, which also deleted
the empty slot a user was adding).
`LibraryRegistryTests` (EditMode) fails if a concrete `SElement` lacks `[SimuLeanElement]`, if a
module / category is unknown, if a prefab path is missing, or if a tool is not a `Component` /
is a model element. `UnityCompat.FindAll(Type, includeInactive)` is the non-generic lookup the
window uses. The former hard-coded six-element list is gone.

### 4.7 Procedural visuals (`Visuals/`)

- **`StationModel`** (`Visuals/StationModel.cs`) — the 3D model of a station element, chosen from a list of
  options (e.g. a MultiServer as the CNC machine or the classic workstation) or the user's own model (`custom`), as
  the object's shape. The chosen model is the child `Model`, placed in edit mode (`[ExecuteAlways]`, rebuilt
  from `OnValidate` through `delayCall`; editor only), keeping the model asset's own root rotation / scale. Its work
  socket (`WorkSocket`, `PalletSocket`, `PatientSocket`, `ServiceSocket_1`, `ItemSocket`) is bound to the element's
  `inItemPosition` / `itemPosition` when that field is empty or pointed into the former model. View only.
  `look` = *Model* (default), **Virtual** (a step done elsewhere or not face to face — a
  test at home, a report written later: no model, a translucent floor marker `VirtualMarker` of `virtualSize` with an
  optional `virtualCaption`, built on enable with `HideFlags.DontSave` by `FloorMarker`, never saved) or **None** (nothing drawn). Virtual
  and None hide the element's own renderers (switched back on only if the look switched them off).
- **`MachineStateView`** (`Visuals/MachineStateView.cs`) — read-only state view of a library station model:
  reads `Element.CurrentState` of its `SElement` every frame. The stack light / status panel (`Beacon` › `Lamp_Green` /
  `Lamp_Amber` / `Lamp_Red`) is lit through a `MaterialPropertyBlock` (shared atlas material): green PROCESSING /
  SETUP, red STOPPED / BREAKDOWN / SCHEDULED_DOWN / OFF_SHIFT, amber otherwise (`LampFor`), each lamp emitting its own
  colour (`LampColors`; a white emission washed the green out). Parts move by node name
  while the element works (`Spindle` / `Turntable` spin, `Head` / `Table` / `Tilt` / `Screwdriver` cycle, `Lights`
  flash), doors (`Door_L/R` slide, `Door_In/Out` roll up) and the `Curtain` are open while it waits and closed while
  it works, the palletizer's `Slew` › `Lift` cycle — one pick-and-place per part laid (a combiner's requirements), timed
  by the process in simulation time (**`Element.CurrentWork`**, the running work unit's start and due time, read-only;
  `WorkHandle.Duration` / `StartTime`; 2026-10-29: was a fixed 8 s loop of screen time); the other motions advance on
  screen time only while the simulation clock does. Before a
  run the baked look (all lamps lit) shows.
- **Reject pusher** (`MachineStateView`): a model with `Pusher` and `RejectSocket` (the inspection station)
  strokes the pusher towards the bin (0.72 m, `PusherStroke`; 0.8 s of screen time) each time an item leaves the element
  for any next element but the first — its exit, then that element's entry, from the link layer's `ItemExited` /
  `ItemEntered` (read-only subscriptions, renewed when the run's model changes).
- **`StackerCraneView`** (`Visuals/StackerCraneView.cs`) — a stacker-crane member (a model with `Carriage` and
  `Forks` and no forklift `Mast`, `IsStackerCrane`; chosen by `UnityResourceTeam`): the aisle is the line the crane was
  placed on (its +Z); the point the member is at along its leg (`AxisLegPath`) is projected on it — the mast runs so the
  pallet centre (0.95 m ahead) faces the point, the carriage rises to its height (fork top 0.6 m above the carriage, up to
  `MaxLift`). **The crane fits the rack it serves** (`fitMastToRack`, on by default; the serialized
  `maxLift = 5.8` constant is gone): at `Bind` the highest node of its network within 2 m of the aisle sets the lift
  (seat − 0.6 m; never below the model's 5.8 m) and the base mesh is stretched above the control cabinet (y > 2.3 m
  scaled along Y, the top carriage moved up; copies of the meshes, the model's kept and restored) — a 10-level rack
  of 1.4 m levels (top seat 12.6 m) was served with the carriage stuck at 5.8 m; a rack within the model leaves it as
  built (EditMode `StackerCraneViewTests`). During a load / unload the forks reach out sideways to the point (≤ 1.35 m, its side), the carriage lifts the
  load off / sets it down half-way and the forks come back; a carried item rides on `LoadSocket`. Node contract:
  `Tools/Blender/assets/asrs.py`.
- **`RobotCellFence`** (`Visuals/RobotCellFence.cs`) — on the robot team presets, off by default: switched
  on, a square of the `SafetyFence/` panels round the team's position at the arm's `reach` + `clearance` (four
  `UnityModularStrip`s under the child `Safety Fence`, a door in the middle of the front side), built in the editor
  (`Rebuild`, also a `[SimuLeanAction]`), removed when switched off. Visual only.

Purely-visual, parametric mesh generators — **not** `SElement`s, not part of the
engine, serialization, or headless runs. They follow the same "never scale — set
parameters and `Rebuild()`" contract as `UnityStorageRack`.

- **`UnityRollerConveyor`** — a roller conveyor built as **one
  combined mesh** sized to a `length` parameter. The roller count adapts automatically
  (`rollers = round(length / rollerPitch)`, then evenly distributed so the last
  roller lands exactly at the end), so a conveyor can be drawn at any length with
  correctly-spaced, **never-deformed** rollers — the fix for "scaling an FBX
  ovalises the rollers". Local **+Z is the run direction**, +X width, surface at
  `bedHeight`. Two sub-meshes (0 = frame+legs, 1 = rollers) → `frameMaterial` /
  `rollerMaterial` (+ `bedMaterial` at LOD1). Drive it from a "draw a conveyor" tool by
  setting `length` and calling `Rebuild()` at runtime. **Turning rollers:** a roller ring has a seam column
  (U = the arc length, 0 … 2πr, without folding back) and the default roller material is marked
  (`RollerConveyorMesh.CreateRollerTexture`, two marks per turn); in Play, under a conveying lane (a
  `UnityLengthLimitedQueue` with a speed, not stopped), `LateUpdate` scrolls the roller sub-mesh's texture (a property
  block on material 1) by the distance conveyed in simulation time, so the rollers turn with the flow — the frame and the
  single mesh are unchanged. Read-only (reads the clock and the lane's state).
  The geometry moved to the static generator **`RollerConveyorMesh`**
  (`Visuals/RollerConveyorMesh.cs`; plain-data `RollerConveyorSpec`), reusable by scene builders
  without the component: **LOD0** = folded C-channel side frames (`RollerFrameProfile.CChannel`,
  default; `Box` = the former solid bar), smooth-shaded lathed rollers with tapered ends and
  hexagonal axle stubs, square-tube legs with foot plates, cross ties and diagonal braces, an
  optional drive (`RollerDriveSide`: chain guard + gear motor); **LOD1** = the rollers merged into
  one striped bed quad (≈ 5 % of the LOD0 triangles), under a `LODGroup` with a fixed reference
  size (`LodReferenceSize` = 3 m, so it switches by roller size on screen, not run length;
  `SetupLodGroup`). Planar UVs in metres; rollers at `(i + 0.5)·pitch`, so sections joined end
  to end continue the lattice (`legsAtStart/End` avoid double stands). Default materials are
  created at run time with **code-generated textures** (`CreatePaintTexture` — tileable paint
  grain; `CreateBedTexture` — roller stripes for LOD1; `CreateMaterial` / `ApplyLook` for URP/Lit
  or Standard, GPU instancing on) — no texture or material assets are needed. The component's
  former fields are unchanged (API backward compatible); new fields: `rollerSides`,
  `rollerStubs`, `frameProfile`, `frameThickness`, `frameTopGap`, `legSize`, `legFeet`,
  `crossBraces`, `diagonalBraceEvery`, `driveSide`, `generateLod`, `lodTransition`, `bedMaterial`,
  plus `ToSpec()`. Note: an existing conveyor rebuilt with the defaults now shows the C-channel
  frame (set `frameProfile = Box` for the old look).

#### 4.7.1 Modular building & overhead-crane kit (`Visuals/Building/`)

Components that **assemble workshop layouts from the kit FBXs** in
`Art/Models/IndustrialBuilding/` and `Art/Models/OverheadCrane/` (see §4.8). Same
contract as above: set size parameters and `Rebuild()`; never scale the GameObject.
Only the tiling pieces stretch (by ≤ a few % to hit the requested length);
doors, end trucks, trolley, hook block and sign keep their aspect ratio. **Optional:**
nothing is added to existing scenes; use the *GameObject → SimuLean → Building* menu
or *Add Component*.

- **`ModularAssetBehaviour`** (abstract, `[SelectionBase]`) — base of the builders:
  `Rebuild()`, `GetSummary()`, `combine` + `chunkLength`. `Reset()` auto-fills the
  `pieces` fields from the kit and builds, so *Add Component* gives a working default.
  Everything a build produces lives under one child `Generated`, destroyed and
  recreated by each rebuild.
- **`ModularKit`** (static helpers) — `Load(kit, piece)` (AssetDatabase, editor
  only; returns `null` in a player, so kit references must already be serialized in
  the component), `ResolvePieces` (fills each public `GameObject` field whose
  **name equals the piece FBX name**), `Place`, `Strip` (cap at the start,
  `round(L/seg)` segments stretched to fit, optional centre piece, far cap rotated
  180° about Y; runs along local +Z), and **`Combine`**: merges the static piece
  instances into chunks of `chunkLength` m along one axis — per chunk one renderer per
  LOD level, one submesh per material, UInt32 indices past 65 k verts, and a
  `LODGroup` with the §4.8 policy (25 % / 7 % / culled below 1 %, no shadows from
  LOD2). Combined meshes are named `SimuLeanCombined*` and are saved **in the scene**.
- **`UnityModularWall`** — one straight wall on a **2 m × 1 m grid**: 1 m concrete
  plinth, `rows` × 1 m sandwich-panel rows (`windowRows` → ribbon windows,
  `darkRows` → dark fascia), coping on top, optional corner trims at the END.
  Origin = bottom of the interior cladding face, +X along the wall, exterior faces
  −Z; close a building by chaining walls counter-clockwise, each turned −90° about
  Y. `openings`: sectional door (2 cells × plinth+3 rows) or personnel door
  (1 cell × plinth+2 rows), each optionally with the SimuLean sign above it.
  Doors stay live (not combined) and get a `UnityIndustrialDoor`.
- **`UnityIndustrialDoor`** — animates a door's `Leaf` node from its serialized
  rest pose: sectional rises 3.9 m, personnel swings 90°. `Open/Close/Toggle/SetOpen`,
  `IsOpen/IsClosed/IsMoving`, `openAmount` scrubbable in the inspector. Plain
  MonoBehaviour — a `IVElement` or script calls it.
- **`UnitySteelColumn`** — HEB column: base plate + shaft (whole segments stretched
  to `height`) + cap, optional **crane bracket** (seat at `railHeight − 0.455`,
  runway axis 0.50 m in front of the column, +Z) and a yellow/black guard.
- **`UnityModularStrip`** — generic segment/cap/centre strip along +Z, used for the
  IPE roof beam (`Beam_Segment`/`Beam_End`) and crane runways
  (`Runway_Segment`/`Runway_EndStop`).
- **`UnityOverheadCrane`** — a double-runway bridge crane: optional two runways
  (±span/2, at `railHeight`, along +Z) standing on the building kit's steel columns (`buildColumns`, on by default: the
  `UnitySteelColumn` pieces, the bracket under each rail, the axis 0.5 m outside it, at most `MaxColumnSpacing` = 8 m
  apart; off when a hall's own columns carry the runways; `FitAround` / `ClearColumnRows` move each runway outwards
  until its columns stand at least 1 m clear of every element's view and floor zone along it — a column never stands
  in a work station), a `Bridge` transform holding two fixed-shape
  **end trucks** and the **extensible box girder** (segments + centred logo panel + end
  plates, combined), and the fixed-shape **trolley** (hoist, drum, ropes, hook block,
  pendant). Pose: `bridgePosition`, `trolleyPosition`, `hookDrop`. `SetPose` jumps,
  `MoveTo`/`MoveHookTo(world)` drive at `bridgeSpeed`/`trolleySpeed`/`hoistSpeed` in
  `Update` and raise `Arrived`; `HookSocket` is the attach point for a carried load.
  **Visual only**; as a team member it is an `ICraneRig` that `CraneResourceView`
  poses from the engine (§4.2).

#### 4.7.2 Floor-plan underlay (`Visuals/FloorPlanUnderlay.cs`)

**Optional** component that lays a CAD drawing on the floor under the model:
`Tools/FloorPlan/make_floor_texture.py` (see its README) converts DWG → DXF with
LibreDWG `dwg2dxf` (not bundled), reads per-layer polylines with ezdxf (INSERTs
exploded, layer-0 inheritance, curves flattened), maps layers to **configurable** style
groups (glob patterns in a JSON config, drop patterns, widths in metres, grey
intensities, optional label layers) and renders one grey channel (line coverage) plus
a placement JSON (`rect_m` = drawing metres covered). `FloorPlanUnderlay` builds one
quad at run time (shader `SimuLean/FloorPlanUnderlay`, built-in pipeline: colour with
alpha = R × opacity, no depth write, *Ignore Raycast* layer, no collider), placed by
`drawingOrigin` (drawing point at the GameObject origin; drawing +X/+Y → local +X/+Z)
and the GameObject transform; `toggleKey` (default P, `None` disables),
`visibleAtStart`, `Visible`. No texture → it disables itself. Purely visual: no clock
events. Drawings are often confidential — keep them and their outputs out of shared
repos (`Tools/FloorPlan/work/` is git-ignored).

#### 4.7.3 Storage racks (`Visuals/Storage/`)

The visuals of `UnityStorageRack` (§4.2) are built like the roller conveyor: **static generators with plain-data
specs**, combined meshes, code-generated materials, never scaled. The geometry uses the rack's own lane formula, so the
racks and the lanes cannot drift apart. Rack axes: +X along the row, +Y up, +Z into the rack; the access / **pick face
is z = 0**. Lane (b, s, l) is anchored at `(b · bayWidth + (s + 0.5) · slotWidth, LevelSurfaceY(l), 0)`. The load
surface `LevelSurfaceY(l)` is `l · levelHeight` with a ground level (the default), else `firstLevelHeight + l · levelHeight`.

- **`PalletRackMesh`** / **`PalletRackSpec`**: selective racking, and drive-in / push-back with
  `RackSupportType.Rails`. Frame f stands at `x = f · bayWidth − gap / 2`, where gap = `bayWidth − slotsPerBay ·
  slotWidth` is the room for the upright. The default bay pitch is 2.8 m: 2.7 m clear for 3 Europallets plus a 0.1 m
  upright. The 1.1 m frame is centred in the lane, so 1.2 m loads overhang it by 0.05 m at the front and the back.
  Deep lanes get intermediate uprights every ≤ `maxFrameSpan`. Parts:
  - open-profile uprights with lips (punched-slot texture, 0.1 × 0.07 m), base plates with anchor bolts;
  - Z or zig-zag frame bracing (`RackBracingPattern`);
  - safety-orange **step beams whose top is the load surface**, with connectors; level 0 on the floor (no beam);
  - `RackDeckType` None / SupportBars / WireMesh;
  - row spacers for back-to-back rows (`rowSpacerLength`, set on one of the two racks);
  - yellow / black upright protectors and end-of-aisle guards;
  - location labels on the beams and a load sign on the end frames.
- **`CartonFlowRackMesh`** / **`CartonFlowRackSpec`**: carton live storage (`StorageRackStyle.CartonFlowRack`, engine
  FlowRack lanes: FIFO, one SKU). Shelves rise by `slopeDegrees` (default 6°) from the pick face to the loading side.
  The shelf surface of level l passes through its anchors, and box k sits `AlongSlope(k) = frontStop + (k + 0.5) ·
  boxPitch` from the anchor along the slope (front stop 0.04 m), tilted with the shelf. Parts: upright frames as above
  (0.08 × 0.06 m); orange front and rear shelf beams (plus a middle one on long shelves); two wheel-bed strips per lane;
  lane guide rails; a front stop plate with the lane label strip; an optional flat wire overstock deck on top. Menu
  preset: 2 bays × 4 lanes × 4 levels × 4 boxes, 0.45 m lanes for 0.6 × 0.4 m boxes, 0.45 m shelf pitch from 0.35 m,
  2.47 m shelves, about 2.1 m high.
- **`FloorMarkingMesh`**: floor storage gets a yellow painted rectangle per ground cell. Each is inset, so neighbouring
  lanes show two lines. One sub-mesh.
- **Shared code** (`StorageMeshBuilder.cs`, `StorageRackVisuals.cs`):
  - `StorageMeshBuilder` (internal): boxes, members, extruded profiles and quads, one triangle list per material slot.
    `RackFrameParts` builds the upright, base plate and bracing for both racks.
  - `StorageRackSlots`: the sub-mesh / material slots 0 Frame, 1 Beam, 2 Steel (galvanised), 3 Safety, 4 Label,
    5 Deck, 6 Track. Every rack mesh takes the same seven-material array.
  - `StorageRackVisuals`: the default materials (`RollerConveyorMesh.CreateMaterial`: URP/Lit or Standard, GPU
    instancing on), with code-generated textures (punched upright, safety stripes, label, wire deck, plus the
    conveyor's paint grain and roller-bed stripes for the wheel beds), and `CreateLodChunk`.
- **LOD and chunks.** `UnityStorageRack.Rebuild` creates one child `RackVisual` with one chunk per `baysPerChunk` bays
  (default 8). Each chunk has a `LODGroup` (fixed reference size 6 m; LOD1 below 20 %, LOD2 below 6 %, culled below
  0.8 %; LOD2 casts no shadows) over the children `LOD0..2`, one renderer each with the seven materials.
  - LOD0: everything.
  - LOD1: box uprights and beams, only the end bracing horizontals, decks and guards as boxes, no bolts, connectors or
    labels. On a carton flow rack each shelf is one striped sloped quad.
  - LOD2: uprights and beams / rails (carton flow: shelf quads) only.

  Reference counts (`StorageRackVisualsTests`, budget ≤ 60 k at LOD0): a 10-bay × 5-level double row with wire decks,
  row spacers, guards, protectors and labels has 14.6 k / 3.0 k / 1.4 k triangles at LOD0 / 1 / 2. A carton flow rack
  of 4 bays × 4 lanes × 5 levels has 4.2 k / 0.9 k / 0.25 k.
- **Component.** New `UnityStorageRack` fields default to the new look; existing scenes keep their values and their
  old children until rebuilt. The fields:
  - layout: `groundLevel`, `firstLevelHeight`;
  - structure: `frameDepth`, `uprightWidth`, `uprightDepth`, `beamHeight`, `uprightExtension`, `bracingPattern`;
  - accessories: `deckType`, `rowSpacerLength`, `uprightProtectors`, `endOfAisleGuards`, `locationLabels`;
  - carton flow: `flowSlopeDegrees`, `overstockDeck`;
  - build: `proceduralVisuals` (off = the former thin plates), `baysPerChunk`, `generateLods`, eight material overrides;
  - item view: `restItemsOnSurface`.

  `slotPrefab` still overrides everything: one unscaled prefab per position, tilted on a flow rack. The default
  `bayWidth` changed from 2.7 to 2.8 (pitch = clear width + upright).
- **Item location view.** `UnityStorageSystem.PlaceStoredItem` is a new virtual (default: `GetStoredItemPosition`).
  The rack uses it to rest each view on its support. It measures the view's pivot height above its lowest rendered point
  once (cached per view) and puts the pivot that high above the support point. The support point is the beam top
  centred on the slot, the shelf along the slope, or the floor plus `stackPitch` per stacked unit. A view without
  renderers (or with `restItemsOnSurface` off) gets its pivot `itemVerticalOffset` above the support. Public helpers
  for tools and tests: `LevelSurfaceY`, `LocalAnchorPosition`, `LocalSurfaceOffset`, `GetLoadSurfacePoint`,
  `PlaceItemView`, `ToPalletSpec`, `ToCartonSpec`. Since 2026-10-08 every stored view is also **turned to the rack**
  (rack rotation × `itemYaw` about the vertical, × the shelf tilt on a flow rack); it used to keep the rotation it
  arrived with (e.g. a forklift's heading). `itemYaw` 90 puts a load's long side (its local Z) along the row, as on a
  drawing's floor cells; floor storage takes `markingInset` (e.g. 0.05 for 1.0 × 0.8 m cells on a 1.1 × 0.9 m pitch).
- **Pick module (conventional racks).** `pickModule` (`RackPickModule`) uses the **bottom zone of the same
  frames** for picking; the storage (pallet) levels start above it: the first storage level is raised to at least
  `PickZoneTop` (`EffectiveFirstLevelHeight`, no ground level). The storage lanes are unchanged `StorageSystem` lanes
  (only on the storage levels); the pick positions are **anchors only**, children `Pick_B{b}_S{s}_L{l}`
  (`PickAnchor`, `PickLocalPosition`, `PickLocalRotation`, `PickUnitOffset`; marker level −1 − l), for other elements'
  views, e.g. pick faces.
  - `CartonFlow`: `pickLevels` sloped carton-flow shelves (`pickFirstShelf`, `pickLevelHeight`, `pickSlopeDegrees`,
    `pickDepth` boxes of `pickPitch`), `pickLanesPerBay` lanes sharing the bay's slot band (`PickLaneWidth`), pick face
    on the aisle side (z = 0), shelves spanning the lane depth. They are generated by `CartonFlowRackMesh` with
    `CartonFlowRackSpec.frames` off, appended to the pallet rack's mesh (`PalletRackSpec.cartonModule`): **no second set
    of frames**. Anchor = the lane's shelf at the face, tilted with it (box k at `PickUnitOffset(k)` along its local +Z).
    The first storage beam is at least `pickClearance` (0.45 m) + a beam above the top shelf's rear (≈ 1.8–2.0 m for two
    shelves).
  - `PalletFloor`: a floor pick position per slot and `pickLevels − 1` reserve beams at `pickLevelHeight` steps over it
    (`PalletRackSpec.extraBeamLevels`), storage above.

  EditMode `StorageRackPickModuleTests` (anchors, lanes only above, beam height, rack + shelves = combined triangles, no
  duplicate frames, budget). Three bays × three storage levels with a two-shelf module: 2.6 k / 0.5 k / 0.2 k triangles at LOD0 / 1 / 2.
- **Units in a lane.** `unitsPerLane` (0 = `depth`) sets the lane capacity of a single-deep rack (several
  units = LIFO by default); `unitGrid` (across × deep per layer) and `unitPitch` draw them as a **block on the position**
  (e.g. cases on a pallet place), first stored at the bottom. `laneAccess` (`RackAccessOverride`) overrides the access
  (Direct = any unit, no relocations; Fifo; Lifo).

#### 4.7.4 Signs (`Visuals/UnitySign.cs`)

**`UnitySign`** (Library tool, *Visualization*): a label board whose size follows its text (`DisplayText` wraps at spaces
within `maxWidth`), text on both faces (the back one not mirrored), leaning back by `tilt` for views from above, held by
`SignMount` Hanging (rods to a ceiling-height bracket at `mountHeight`), Post (floor post) or Wall (flat on a wall, front
= local −Z); `SignImportance` Secondary = 60 % text. `Build` regenerates its children. The text uses the shader
**`SimuLean/Sign Text`** (`Visuals/Shaders/SignText.shader`; `UnitySign.UseSignText` for any `TextMesh`): depth-tested
and back-face culled — the built-in text shader draws text over everything.

**`SignLayout.Resolve`** places a set of signs (Primary first): each takes the first candidate — its own position, then
raised in steps, then shifted along its board and towards / away from its reader (wall signs along their wall) — where
board and holder clear every obstacle (`CollectObstacles`: world bounds of the scene's renderers, flat floor pieces and
ground-sized objects excluded; the wall behind a wall sign ignored) and every sign placed before (gap, and never right
above / below one in plan). With `Options.viewpoints` (e.g. the camera presets) a candidate must also be **readable**
from one of them: board centre unobstructed, facing the camera, text at least `minAngularSize`; failing that it takes the
first free place and is listed in `SignLayout.unreadable`. View only.

### 4.8 3D asset library (`Art/Models/`) and the Blender pipeline

Detailed equipment models are **generated by Python scripts run headless in
Blender** from `Tools/Blender/` (outside `Assets/`, so Unity never compiles or imports
them). Full methodology, flags and budgets:
[`Tools/Blender/README.md`](../Tools/Blender/README.md). Like §4.7 these are purely
visual — no engine, serialization or headless dependency; a `IVElement` or animation
script drives them. They are used by the library presets (`StationModel`, §4.7), the member and item prefabs
(`Prefabs/ResourceMembers/`, `Prefabs/Items/`) and the `Shipyard_Library` sample scene.

- **Output contract.** `Art/Models/<Name>/<Name>.fbx` + one baked PBR set
  `<Name>_{Albedo,MetallicSmoothness,Normal,Occlusion,Emission}.png` (one shared
  atlas and material per asset; colour variants are extra texture sets on the same
  mesh). Every animated part is a bare Transform (e.g. `Mast`, `MastInner`,
  `Carriage`, `Wheel_FL`, `Steer_RL`, `LoadSocket`) whose meshes are children
  `<Name>_LOD0/1/2` at identity — **animate the named Transform, never a `_LODn`
  mesh** (far LODs merge parts into `Body`). Pivots/axes per node are documented in
  each asset script's docstring.
- **Assets:** `Forklift/` (counterbalance forklift; LOD0 ≈ 22 k tris / 11 renderers,
  LOD1 ≈ 6 k / 10, LOD2 ≈ 1.8 k / 4 — sized for dozens of vehicles), `RailShuttle/`
  (rail-guided transfer car with a single-lane roller deck 6.0 × 2.3 m, sockets
  `Deck` › `Slot_Center` / `Slot_Front` / `Slot_Rear` and a `Beacon` node, plus
  `Rail_1m`, a 1 m tiling rail segment), and the modular kits `IndustrialBuilding/`
  (wall panels, window/dark rows, plinth, coping, corners, sectional & personnel
  doors, SimuLean sign, HEB column parts + crane bracket + guard, IPE beam) and
  `OverheadCrane/` (runway segment/end stop, girder segment/logo/end, end truck,
  trolley). A kit folder holds **one FBX per piece** that all share the folder's
  single texture set and material `<Folder>.mat`. `GantryStacker/`
  (`assets/gantry_stacker.py`): a portal pick-and-place gantry (four posts, two runway girders,
  a bridge travelling along the runway = local Z, a trolley along the bridge = local X, a vacuum
  head moving up / down; nodes `Bridge` › `Girder`, `Saddle_W/E`, `Trolley` › `Head` (pivot =
  cup lips, 2.30 m), plus `Leg_*`, `Runway_W/E`, `EndBeam_S/N` so a scene can move the posts
  and stretch the girders); authored 4 × 12 m, library palette (steel-blue portal, yellow
  bridge); LOD0 ≈ 6.5 k tris / 14 renderers, LOD1 ≈ 2.4 k, LOD2 ≈ 1.0 k. Animated by
  `GantryStackerView` (§4.10). `AGV/` (`assets/agv.py`: a warehouse
  drive unit, 1.05 × 0.80 m, turntable top 0.362 m — reworked the same day from a 0.30 m low-profile AGV —,
  nodes `Lift` (the turntable) › `LoadSocket`, `Wheel_L` / `Wheel_R`) and `Operator/` (`assets/operator.py`; reworked the
  same day into a realistic worker generated with MPFB 2 / MakeHuman — CC0 assets, the GPL add-on is not in
  the repo — in two looks of the same height and joint names: `Operator.fbx` (male) and
  `Operator_Female.fbx` (a kit piece on the folder material), each one skinned mesh on an armature whose
  driven bones are `Torso`, `Leg_L/R`, `Shin_L/R`, `Arm_L/R`, `Forearm_L/R`, `CarrySocket`, `Helmet`;
  RGBA albedo → Cutout material), as member prefabs `Prefabs/ResourceMembers/AGV.prefab` /
  `Operator.prefab` (**`OperatorAppearance`** with look Operator: gender Male / Female / Mixed — members
  alternate, the team calls `ForMember(i)` — and the hard hat), animated by `UnityVehicleAnimator` (plate up
  while carrying, wheels spin; legs and arms swing with the distance walked, knees bend while a foot swings
  forward, arms forward with the elbows bent while carrying; joints turn relative to their rest rotation
  about the model's lateral axis, so skinned and rigid models animate alike). `Forklift/` was re-exported upright
  (it predated the `export_fbx` fix); that run baked an all-black atlas (OptiX listed but its kernels do
  not compile on the development GPU, and Cycles then bakes black without an error), so it was re-baked
  the same day: `setup_cycles_gpu` now keeps the first GPU backend whose probe bake is non-black (else
  the CPU) and `bake_texture_set` refuses an all-black albedo / AO. `IndustrialRobot/`
  (`assets/industrial_robot.py`): a generic 6-axis articulated robot (~2.7 m reach, orange arm, graphite pedestal),
  one renderer per axis; chain `Base`, `A1` (yaw, local Y) › `A2` (shoulder, X) › `A3` (elbow, X) › `A4` (forearm
  roll, Z) › `A5` (wrist pitch, X) › `A6` (flange roll, Z) › `Flange` › `ToolSocket`, identity rotations in the zero
  pose (upper arm vertical, forearm along +Z); pivots and link lengths in the script docstring. One model for floor
  and ceiling mounting; driven by `RobotArmRig` / `RobotArmView` (§3, Resource views); member prefab
  `Prefabs/ResourceMembers/IndustrialRobot.prefab`. `RobotTools/` (`assets/robot_tools.py`, a kit): `Gripper`
  (jaws `Finger_L` / `Finger_R` along local X, `TCP` = palm), `VacuumGripper` (8 cups, `TCP` = cup lips) and
  `WeldingTorch` (`TCP` = wire tip), mounted by `RobotArmRig.tool`. Station models of the library
  presets (`LibraryPresets`, `NavalPresets`; `StationModel` + `MachineStateView`, §4.7): `WeldPositioner/` (`Tilt` › `Turntable` › `WorkSocket`) and the process stations `PaintBooth/`
  (`Door_In` / `Door_Out` roll-up shutters, `WorkSocket`), `Palletizer/` (`Slew` › `Lift` › `Reach` ›
  `LoadSocket`, `PickSocket`, `PalletSocket`) and `InspectionStation/` (`Lights`, `Pusher`, `WorkSocket`,
  `RejectSocket`), `CncMachine/` (`Door_L` / `Door_R`, `Table` › `WorkSocket`, `Head` › `Spindle`) and
  `AssemblyBench/` (`Screwdriver`, `WorkSocket`, `PartsSocket`) — the last two as alternatives to the hand-made
  `WorkstationProcessor` / `AssemblyWorkstation` models of the MultiServer / Combiner / AssemblyStation prefabs;
  `UnpackingStation/` (`assets/unpacking_station.py`: roller section, worktop, empties chute, waste cage;
  `Stop`, `WorkSocket`, `PiecesSocket`, `EmptiesSocket`, `StaffSocket` — the Separator's model) and `PickingStation/`
  (`assets/picking_station.py`: worktop with the tote's place, 3 × 4 bins with pick modules; `PickLights`,
  `WorkSocket`, `StockSocket_0..11`, `StaffSocket` — the Picking Station's model); each station carries a stack light `Beacon` › `Lamp_Green` / `Lamp_Amber` / `Lamp_Red`
  (`Tools/Blender/assets/station_parts.py`), driven by `MachineStateView` (§4.7) when placed from the library
  (`LibraryPresets`). Hospital library: `HospitalBed/`, `Stretcher/` (`Lift` › `Backrest`, `PatientSocket`, `DriverSocket`),
  `Wheelchair/` (`Wheel_L` / `Wheel_R` r 0.30 m, `PatientSocket`, `DriverSocket`), `ReceptionDesk/`
  (`ServiceSocket_n`, `StaffSocket_n`), `ConsultationBox/` (`Curtain`, `PatientSocket`, `StaffSocket`,
  `VisitorSocket`, `EntrySocket`) — both with a wall status panel using the same `Beacon` › `Lamp_*` nodes — and
  the `WaitingChairs/` kit (`Seat_Row` / `Seat_Table` segments, `Seat_End` cap, a `Seat` socket per seat).
  `HospitalPeople/` (`assets/hospital_people.py`, on `operator.py`'s MPFB pipeline): `Doctor`, `Nurse`, `Patient`
  and `*_Female`, skinned on the operator's bone contract: the `Doctor` / `Nurse` member prefabs and the `Patient`
  item (`Prefabs/Items/Patient`, `OperatorAppearance` Mixed alternating by instance). **Item looks**
  (`Prefabs/Items/`): `EuroPallet`, `Tote`, `RollCage` are containers — the Combiner's Attach lays the
  components on a container's `LoadSocket` (`ItemVisuals.LoadSocket`; a socket below the model's top is the floor of a walled container — tote, roll cage — and the parts are packed inside its walls, 86 % of the outer footprint; the parts are turned a quarter when that packs them lower) — and `Patient`. `LoadUnits/` (kit: `RollCage`, `LoadSocket`; the item looks `EuroPallet` / `Tote` are
  the `WarehouseLoadUnits/` models `EuroPallet` / `SmallLoadCarrier_600x400x280` since 2026-10-26 — lighter, one renderer
  per LOD — with the GUIDs of the former `LoadUnits/` pallet and tote, so the prefabs kept their reference; the item
  prefabs carry the `LoadSocket` (deck top / inside floor) and the tote's `StackSocket` (rim) themselves), `PalletTruck/` (hand pallet
  truck: `Lift` › `LoadSocket`, `Handle`, `Wheel_L/R` r 0.10 m — the AGV's names, so `UnityVehicleAnimator` drives
  it — and `DriverSocket`; member `PalletTruckOperator`), `SafetyFence/` (the *Safety Fence* preset; kit: `Fence_Panel` 1.0 m segment, `Fence_End` cap, `Fence_Door` with a
  `Door` node) and `ASRS/` (kit: `Rack_Bay` 1.4 m segment with `Slot_1..5`, `Rack_End` cap, `Aisle_Rail` segment,
  `StackerCrane` with `Carriage` › `Forks` › `LoadSocket`; `Rack_Bay` is the *Profile Rack* station model and a preset
  icon; `StackerCrane` is the member of the *Stacker Cranes* team preset (`Prefabs/ResourceMembers/StackerCrane.prefab`,
  driven by `StackerCraneView`)). `ShipBlock/` (`assets/ship_block.py`): two static hull blocks
  sharing one atlas, `ShipBlockCurved` (12 × 16 × 5 m bilge / side block) and `ShipBlockFlat`
  (12 × 16 × 1.6 m double-bottom panel); pivot at the base centre, nodes `Lug_1..4` (sling
  attach points) and `LiftPoint` (crane hook); LOD0 ≈ 3.4 k / 2.6 k tris, one renderer. No number is
  painted any more (the block number is the item's label, `ItemLabelView`); `ShipItems/` (`assets/ship_items.py`, a
  kit): `SteelPlate`, `PlateKit` (a sub-block's cut plates and profiles on dunnage), `ProfileBundle`, `LoadSocket` on
  top of each.
  `SPMT/` (`assets/spmt.py`: 14 × 5 m deck on 7 axle lines × 4 bogies, nodes `Body`,
  `Wheel_<line>_<bogie>` (twin tyres, r 0.36 m), `Lift` (deck, rods, power pack) › `LoadSocket`; ≈ 63 k tris LOD0),
  `GoliathCrane/` and `WeldingGantry/` (`assets/goliath_crane.py --variant goliath|welding`: span 40 m / 12 m, nodes
  `Rail_W` / `Rail_E`, `Bridge` › `Leg_W` / `Leg_E` / `Girder` / `Trolley` › `Ropes`, `Head` › `HookSocket`).
  `ShipyardPeople/` (`assets/shipyard_people.py`, on `operator.py`'s MPFB pipeline, one atlas): `Fitter`, `Welder`,
  `Painter`, `Outfitter` and `*_Female` on the operator's bone contract, their head gear (hat, welding visor,
  respirator) as `<Stem>Hat_LOD0/1` under the `Helmet` bone; looks of the `PeopleLooks` catalog, the member prefabs of
  the same names are variants of `Operator` (the *Block Process* composite's crews use them, members labelled with
  their `skill`). `operator.orient_outward` turns the faces of a garment lofted round the torso (vest, belt) outwards —
  an open loft cannot be recalculated, and engines cull back faces (the vest showed its dark inner back in Unity;
  the operator was re-exported with it). Sample scene `Scenes/LibraryExamples/Shipyard_Library.unity`:
  the *Block Process* composite and the yard's resources.
  `BlockWorkspace/` (`assets/block_workspace.py`): `BlockWorkspace` (a 16 × 20 m floor pad with bay
  markings and 8 block stools; node `BlockSocket` at the stool top, where a ShipBlock root goes;
  LOD0 ≈ 4.1 k tris) and `BlockScaffold` (a 2.5 m scaffold bay with its deck at 2.0 m; ≈ 1.0 k
  tris), sharing one atlas. `LevelLuffingCrane/` (`assets/level_luffing_crane.py`): a double-link
  level-luffing portal crane with travel on rails along local Z. Its nodes are
  `Slew` › `Boom` › `Jib`, `Slew` › `Tie`, and `Ropes` / `HookBlock` (also under `Slew`), plus the
  sockets `JibTip`, `TieEnd` and `HookSocket`. The four-bar linkage (`linkage(theta)` in the script;
  formulas in `Tools/Blender/README.md`) keeps the hook level over a 17–35 m reach. LOD0 ≈ 9.8 k tris
  on 7 renderers; member prefab `Prefabs/ResourceMembers/LevelLuffingCrane`, animated by `LevelLuffingCraneView`
  (§3, Resource views). The blocks are the `Block` / `SubBlock` item looks (`Prefabs/Items/Shipbuilding/`), the
  workspace is the station model of the naval presets and erection docks; `BlockScaffold`, `RailShuttle/` and the ASRS
  `StackerCrane` are not used yet. `WarehouseLoadUnits/`
  (`assets/load_units.py`, a kit for intralogistics / automotive supply, generic; its own atlas — the older
  `LoadUnits/` kit keeps the Euro pallet, roll cage and tote): `EuroPallet`
  (1200 × 800 × 144), `IndustrialPallet` (1200 × 1000), four KLT-type `SmallLoadCarrier_<L>x<W>x<H>`
  (300x200x147, 400x300x147, 400x300x280, 600x400x280), three `CardboardCarton_<L>x<W>x<H>`,
  `LargeLoadCarrier` (GLT type, 1200 × 1000 × 975), `MetalMeshBoxPallet` (1240 × 835 × 970),
  `PalletCollar` (pallet + 3 collars) and the one-piece loads `PalletLoad_Cartons` /
  `PalletLoad_SmallLoadCarriers`. Built for thousands of instances: **one renderer per LOD**, one material
  (`WarehouseLoadUnits.mat`, plus the colour set `WarehouseLoadUnits_Blue_*` → `WarehouseLoadUnits_Blue.mat`: blue carriers on the same
  UVs), LOD0 ≤ 1.5 k tris (pallets < 1 k), LOD1 76–204, LOD2 12–28. Pivot at the bottom centre, long side
  along local +Z; sockets `TopSocket` (stacking) and, on containers, `ContentSocket` (inside floor). LOD1 / LOD2
  are hand-made **proxies** baked selected-to-active from LOD0 through the finished atlas
  (`simulean_kit.transfer_to_proxies`, new): a 12-triangle box still shows plank gaps, ribs and handle slots.
  `TuggerTrain/` (`assets/tugger_train.py`, a kit): the stand-on electric `TowTractor` (`Steer_FL/FR` ›
  `Wheel_FL/FR`, `Wheel_RL/RR`, `Beacon`, `DriverSocket` for a standing Operator, `Hitch_Rear`), the wagons
  `PlatformTrolley` (`LoadSocket`), `ConsoleWagon` (`Slot_0..8`), `SteeringWheelWagon` (`Slot_0..35`) and
  `ToteTrolley` (`Slot_0..5`, for `SmallLoadCarrier_600x400x280` totes), each with castors `Castor_FL/FR` ›
  `Wheel_FL/FR`, `Wheel_RL/RR`, a yawing `Drawbar` › `Hitch_Front` and a `Hitch_Rear` (all hitches 0.30 m
  high: a wagon's `Hitch_Front` goes on the `Hitch_Rear` of the unit in front), and the sequenced parts
  `CentralConsole` and `SteeringWheel` (proxy LODs). `TruckTrailer/` (`assets/truck_trailer.py`): a 13.6 m
  curtain-side `SemiTrailer` (one curtain open; `Deck`, `KingPin`, `Axle_1..3`) and a cab-over
  `TractorUnit` (`Steer_FL/FR` › `Wheel_FL/FR`, `Axle_R`, `FifthWheel`). `CarBody/` (`assets/car_body.py`):
  a generic compact SUV body in the trim state (painted, doors on, no glass yet so the cabin is visible,
  empty arches; sockets `ConsoleSocket`, `SteeringSocket`, `DoorSocket_L/R`, `Wheel_FL/FR/RL/RR`) with the
  option piece `CarWheel` and paint colour sets `CarBody_Red_*` / `CarBody_Blue_*` (→ `CarBody_Red.mat`,
  `CarBody_Blue.mat`; proxy LODs). `AssemblyLineKit/` (`assets/assembly_line.py`): `AssemblySkid`
  (`BodySocket`), the tiling `SlatConveyor_6m` segment, `WagonParkingFrame` (`Bay_0/1`), `WorkstationPost`
  (`Lamp`, `StackLight`), `AndonBoard`, `ToolBalancer` (`Cable`, `Tool` › `ToolSocket`), `LineLight` and the
  tiling `FloorTape_1m` / `FloorTape_Hazard_1m`. Models only (no views yet), except the tow tractor: the *Warehousing ▸ Tugger Train* preset draws a milk run with it. **Hand-made models** (no `<Folder>_Albedo.png`, so the postprocessor leaves them
  alone): `SourceStation`, `SinkStation`, `BufferPosition`, `CartonItem`, `WorkstationProcessor`,
  `AssemblyWorkstation` — the looks of the element prefabs `Source`, `Sink`, `Queue`, `Item`,
  `Multiserver`, `AssemblyStation` / `Combiner` — plus the unused `AGV_Mouse` and `BufferBox`, and their `Materials/`.
  Shared Blender modules: `simulean_kit.py` (scene, bake, LODs, `export_fbx`), `simulean_materials.py` (procedural
  source materials baked into the atlas), `simulean_modular.py` (kits: many FBX pieces on one atlas),
  `assets/station_parts.py` / `hospital_parts.py` (shared parts and palettes), `assets/workshop_preview.py` (building +
  crane kits together); each script also saves `Tools/Blender/build/<Asset>.blend` (git-ignored), from which
  `export_external.py --asset <Name>` writes files for other tools.
- **`SimuLeanModelPostprocessor`** (`Editor/Tools/`, an `AssetPostprocessor` scoped
  to library folders of `Assets/SimuLean.Unity/Art/Models/` — those with their own
  `<Folder>_Albedo.png`): sets texture import types; creates `<Name>.mat` (URP Lit, metallic workflow,
  **GPU instancing on**; *Cutout* when the albedo has an alpha channel) in `OnPostprocessAllAssets` and reimports the model once if
  the material did not exist yet; imports models with `materialImportMode = None`
  and assigns the shared material in `OnPostprocessModel`; kit pieces (FBX not named
  after the folder) fall back to `<Folder>.mat` and import **readable** so
  `ModularKit.Combine` can merge them; replaces Unity's per-node auto-LODGroups with
  **one `LODGroup` on the model root** (LOD0 > 25 %, LOD1 > 7 %, LOD2 > 1 % screen
  height, culled below; LOD2 casts no shadows). Menu: *Tools → SimuLean → Reimport 3D
  Model Library*.
- **FBX export** (`simulean_kit.export_fbx`): the kit converts Blender to Unity axes
  itself and exports with `bake_space_transform` **off** (that option
  mis-transformed nodes two or more levels below the root).
- **Gotcha:** the root `.gitignore` rule `[Ll]ibrary/` is unanchored — never name an
  asset folder `Library`. Preview renders (`Tools/Blender/previews/`) are git-ignored.

### 4.9 Optional runtime tools: camera, HUD, element inspector

Reusable play-mode / player tools. *Since 2026-10-06* the camera controller, the HUD, the element
inspector panel and the visual-detail setting are part of the default modelling scene (the editor
scene set-up, §4.6.1, adds and wires them with the clock); remove them if you prefer your own
camera or UI. The sample scenes predate this and do not all have them. All use the legacy Input Manager API (`activeInputHandler: 0`)
and IMGUI.

- **`SimCameraController`** (`Core/Camera/`) — map-like navigation camera
. Its pose is a focus point kept on the ground plane
  (`groundHeight`) at the screen centre, a distance, a pitch (clamped `minPitch`–`maxPitch`,
  no roll) and a yaw. Left- or middle-drag grab-pans: the grabbed ground point (or a
  collider hit, `anchorOnColliders` / `anchorMask`) stays exactly under the cursor at any
  zoom; a left click without drag (`dragThreshold`) still selects, a drag never does
  (`leftDragPans` = false restores middle-drag-only panning). Right-drag orbits about the
  focus (`orbitSpeed` °/px). The wheel zooms towards the point under the cursor by
  `zoomStep` of the distance per notch, approaching `minHeight` / `minDistance`
  geometrically and capped at `maxDistance`. WASD / Q/E speeds are fractions of the camera
  height (`panSpeed`, `verticalSpeed`, clamped to `minKeySpeed`–`maxKeySpeed`, Shift ×
  `fastMultiplier`). Orbit, zoom, keys and preset transitions move a target pose that the
  displayed pose follows with a critically damped spring (`smoothTime`, `transitionTime`)
  in `Time.unscaledDeltaTime` clamped to `maxFrameTime`; grab panning moves the displayed
  pose directly (no lag). Mouse drags starting over a registered HUD rect, or while an
  IMGUI control holds the mouse, are ignored. Rays are built in camera space (`ViewRay`)
  because `Camera.ScreenPointToRay` loses ≈ 0.5 px of precision ~900 m from the origin.
  Public operations `Orbit`, `ZoomAt`, `BeginPan`/`DragPan`/`EndPan`, `MoveKeys`, `Step`,
  `SnapToTarget`, `SyncFromTransform` (plus `CameraHeight`, `KeySpeed`, `Panning`,
  `Transitioning`) drive it without the Input Manager;
  `Editor/Tools/SimCameraControllerCheck` (menu *Tools ▸ SimuLean ▸ Checks ▸ Camera Controller
  Check*, also a button in the SimuLean window ▸ Settings, or batch `-executeMethod SimuLean.Unity.Editor.SimCameraControllerCheck.Run`,
  exit code 0/1, log prefix `[CamCheck]`) measures pan anchoring, zoom ratio, key speed,
  frame-rate independence, orbit centre and preset reproduction with them. Also
  click-select, F frame, 1–9 `CameraPreset` views (a preset's pivot may be off the ground:
  the camera reproduces the same pose), `resetKey` (default R), `presetKeys` toggle. The
  previous public API (`CameraPreset`, `GoTo`, `ResetView`, `Frame`, `Select`, `Pick`,
  `Pivot`, `Selected`, `SelectionChanged`, the speed/limit fields) is unchanged; the
  `panSpeed` / `verticalSpeed` defaults now mean fractions of the camera height (0.8 / 0.5)
  instead of the pivot distance. Picking (`SimCameraController.PickObject`, static)
  **prefers simulation elements**: the nearest collider hit that belongs to an element, else the
  nearest element renderer whose bounds the ray crosses, else the nearest collider / renderer — so a
  floor or wall with a collider never hides the (collider-less) element prefabs.
- **`SimRuntimeHud`** (`Core/Hud/`) — calendar date (only when the clock has a start date), sim time in the model's
  unit, run/pause, ×0.5/×2 and speed presets ×1 ×60 … (via `TimeScale`), the elements' names over the model (`showNames`,
  key N; on for up to 40 elements; a name under a panel — this frame's or the last one's registered rects,
  `UnderPanel` — is not drawn), the help shown by itself only the first time (`PlayerPrefs`), the **model seed** (a field with the
  next run's seed — or, in random mode, the last one — where typing a value fixes it, a *random*
  toggle, **Keep** (fix the last run's seed) and **New** (draw and fix a new one), the last / next
  seed line and a **Restart** button, so the seed is chosen before each Run / Restart; shortcuts are
  ignored while the field has keyboard focus), a views dropdown, the selected element (resolved up
  the hierarchy from an `SElement` or any component exposing a public `Element`
  field/property — `SimRuntimeHud.FindElement`; hidden while the element inspector panel is open, which
  shows it in full) and a key legend (Space, +/−, H; a *Hide* / *Help* button too). The clock panel
  collapses to its first rows (−/+, `startCollapsed`).
  `RegisterScreenRect`/`IsPointerOverHud` let other IMGUI panels block camera zoom /
  picking under them. **Layout:** the run-time panels (HUD and element inspector)
  are laid out on a 1600 × 900 reference screen and scaled as a whole to the Game view
  (`SimRuntimeHud.ReferenceScale` = min of the width and height ratios; `uiScale` multiplies the HUD's),
  so their arrangement — clock top-left, help top-right, inspector docked right below it — is the same
  at every size and they never overlap (before, fixed pixel sizes made them overlap in a small Game view).
  **Connections over the Game view** (opt-in): `showConnections` (off by default; key C or the
  *Connections* button) draws `SceneConnections.All()` as camera-facing GL bands with arrows when a camera has
  rendered (`RenderPipelineManager.endCameraRendering` in the Universal pipeline, `OnRenderObject` in the built-in one;
  game cameras only, on top of the geometry), filtered by group (`showFlow` / `showLists` on, the others off);
  read-only, rescanned once a second. The drawing is `ConnectionOverlay` (`Core/Hud/ConnectionOverlay.cs`, 2026-11-10:
  the bands, arrow heads and the discs of objects without a mesh), which `simulean_capture` draws its pictures with too.
- **`ElementInspectorPanel`** (`Inspector/`) —
  click an element (camera picking, or `SimCameraController.PickObject` from the main camera
  without a controller) to open a docked, draggable panel with tabs **KPIs** (state,
  active stops, content, free capacity / occupied length, input / output and rates,
  time-weighted WIP avg / max, stay times, utilisation (the results' definition, §4.5), stops,
  time per state), **Charts** (state split per period — per calendar day when the
  clock has a calendar —, cumulative split, content mean / max over the run and
  recent buckets) and **Detail** (type, scene object, capacity / lane set-up,
  strategies, calendar). Built by `ElementInspectorReport` from the Link-layer
  `ElementStatsCollector`, the state tracker / log and `ElementLiveHistory` (content
  integral per bucket via `StatTimeWeightedVariable.GetIntegral`, sampled once per
  rendered frame); charts are engine `ChartData` (`Stacked` to 100 % per period, `Area` for content, `Bar` drawn
  as the panel's list), the plot rasterised by `ChartRenderer.RenderPlot` (theme `Panel`, §3.11) at the panel's pixel
  size (≤ 1 Hz) and the labels written by IMGUI (the former `ChartSpec` / `ChartTexture` were removed). **Read-only and
  cheap:** KPIs rebuilt at `refreshHz` (default 4 Hz), charts at 1 Hz, nothing
  scheduled on the clock, so results are unchanged. **Extension hook:**
  `IElementInspectorContributor` (`Supports`, `AddKpis`, `AddDetail`, `AddCharts`;
  base class `ElementInspectorContributor`) — found as components on the selected
  object's hierarchy, in the panel's optional `contributors` list, or via
  `ElementInspectorPanel.RegisterContributor`; contributors must stay read-only. Only the
  elements listed in `elements` (auto-collected, see §4.6.1 scene set-up) are inspectable. `closeKey`
  (default Esc) calls `UnitySimClock.ConsumeEscape()` so closing the panel does not
  quit; `[DefaultExecutionOrder(-100)]`.
- **Dashboard** (`Charts/UnityDashboard.cs`; tool *Analysis ▸ Dashboard*, opt-in): a panel of
  `ChartDefinition`s over the scene in Play (`corner`, `chartSize` in reference points, `columns`, `toggleKey`, a header
  button to collapse). Every `refreshSeconds` of real time it takes `UnitySimClock.CaptureLive()` (a copy of the run, ≈ 2 ms)
  and, **on a worker thread**, turns it into a single-run set and builds each chart from it (`ShownResults`
  = the run going on; `StoredSets` read once, on the main thread), drawn by `ChartRenderer` (theme `Panel`, text
  ×round(UI scale)) — the same charts as the Results tab and the PNG; the frame uploads the images when the job is done
  (one refresh at a time; `Refresh()` does it all at once, for tests). Engine code only on the thread (the fonts' glyph
  cache is locked); a long run's Gantt took ≈ 360 ms per refresh on the main thread in Debug and stalled the scene. Each chart
  draws into its own kept `ChartBuffers` (the supersampled image and the result, reused while the size holds) and the
  upload writes the rows straight into the texture: a new ≈ 2.9 MB image per chart and second set off the collector,
  whose pauses were the remaining stutter (the element inspector's plots keep theirs too). Read-only (reads the
  statistics, schedules nothing); registers its screen rect with the HUD so the camera ignores clicks on it. Charts
  over time need the clock's `seriesInterval`.
- **Element Label** (`Visuals/UnityElementLabel.cs`, 2026-10-09; tool *Visualization ▸ Element
  Label*, opt-in): a `TextMesh` over an element — `text` with `{n}` (the element's content from its statistics,
  `GetStatsContentCurrent`) and `{name}` — or, without an element on or above it, a zone label. `facing` Camera
  (parallel to the view of `SimRecorder.ViewCamera`, upright on screen; was turned to the camera's position, so labels
  below a high camera spun round — 2026-10-12, as `ItemLabelView`) or Floor. `hideItems` switches the renderers of the items the
  element holds off (queues, Multi-Servers, lanes): e.g. patients waiting at home shown as a number. A
  shared count per item keeps an item hidden when it moves between two hidden elements whatever the update order. The
  text object is `DontSave`, built in Play. Read-only (renderers only; no event, no item moved).
- **Floor Zone** (`Visuals/UnityFloorZone.cs`, 2026-10-09; tool *Visualization ▸ Floor Zone*, presets
  *Healthcare ▸ Decor ▸ …*): a coloured, labelled area on the floor (`size` in metres, `color`, `label`,
  `letterHeight`) — decoration, nothing in the model. Drawn by **`FloorMarker`** (`Visuals/FloorMarker.cs`: a translucent
  slab with a 5 cm border and a caption at its front edge, sized in metres whatever the parent's scale, every object
  `DontSave`), the same drawing as `StationModel`'s virtual look. The *Healthcare ▸ Decor* presets also place library
  models without an element (Hospital Bed, Stretcher, Wheelchair, Side Table) and the **hospital wing kit**
  (`Art/Models/HospitalWall`, `Tools/Blender/assets/hospital_wall.py`: cut-away 1.6 m partitions in 1 m pieces —
  `HWall_Panel`, `HWall_Glass`, `HWall_Door` with its `Door` leaf, `HWall_End`) as `UnityModularStrip` runs: Partition Wall,
  Glass Partition, Wall with Door (the door as the strip's centre piece) and Room Walls (four runs around 6 × 4 m, the
  door in the front one; `HealthcarePresets.Wall` / `RoomWalls`).
- **Item walk view** (`Visuals/ItemWalkView.cs`): the clock, after each advance, places every item of a
  `WalkLink` along its leg by the simulation time — from where it stood (on the floor), through the route's nodes or
  the grid's path, skipping a first node it already stands beyond — as `UnityVehicleAnimator` does for a member; the
  drawn position catches up with the model's within ~0.25 s (a walk may start part-way through a frame). A person
  plays its walk (`PersonAnimator.DriveWalk`: the model places it, its own straight `WalkToPlace` is skipped). A sink
  that takes an item still drawn behind its walk lets it reach the door first (`ItemWalkView.DestroyWhenArrived`).
  *(patients through the walls)* An item leaving an element whose items a label hides
  (`UnityElementLabel.HidesItemsOf`: a patient at home while the study is corrected) sets off from the route's first
  node, not from where its hidden view stood. A walk that ended in the model while drawn behind it — or never drawn,
  shorter than a frame (`WalkLink.Arrived`) — hands the rest of its way to the person (`PersonAnimator.FinishWalk`):
  its next walk to a place goes along the route's nodes, then the station's entry, whatever the length, instead of
  straight to the station.
  `AnyWalking` makes a `SimSpeedProfile` slow down. Read only. An `Agenda` with **`ArriveOnTime`** (`arriveOnTime`,
  2026-10-09) releases each patient its walk early (`WalkLink.LongestWalk`: the planned walk to its destinations, the
  longest when several), so that it arrives at its slot; its link waits at the door (a room still busy with the
  previous patient frees as it arrives). Without it a walking patient arrives its walk late.
- **Pathway Arrows** (`Visuals/UnityPathwayArrows.cs`, 2026-10-09; tool *Visualization ▸ Pathway
  Arrows*): floor chevrons every `spacing` m along each connection (Next Elements) between the elements under `root`
  (empty = the scene), `clearance` m free around each element — the pathway drawn from the model; a connection whose
  items walk on the network follows the walked route between the two locations (none when they share it), each
  direction 0.35 m to its right, one set per route; `FloorMarker.Chevron`,
  `DontSave`, rebuilt on enable / validate. The healthcare composites carry one (a child *Pathway Arrows*), and their
  doors face the flow. **Door signs:** the healthcare rooms (Reception, Consultation Room, Triage, Diagnostic Test)
  carry an `UnityElementLabel` with `{name}` over them.
- **Speed Profile** (`Visuals/SimSpeedProfile.cs`, 2026-10-09; tool *Visualization ▸ Speed Profile*,
  opt-in): sets `UnitySimClock.TimeScale` every frame from the model calendar — `openSpeed` within `openHours` (a
  `WeeklyShiftPattern`, e.g. clinic hours), `closedSpeed` otherwise — and `walkingSpeed` while a resource view moves
  along a leg (`UnityVehicleAnimator.Executor.CurrentLeg`) or an item walks (`ItemWalkView.AnyWalking`), at most
  `maxSlowSeconds` of real time per walk. **The user's speed wins**: a clock speed other than the one it
  set last (the HUD's ×2 / ×0.5 / presets, the camera's + / − keys) puts it in `Manual` — it stops setting the speed —
  until `ResumeAuto` (the HUD shows *auto* / an *Auto* button while a profile is active, `SimSpeedProfile.Active`). The
  setter re-anchors the clock, so the run is unchanged at any speed.
- **Recorder** (`Visuals/SimRecorder.cs` — settings, shot data, state —, with `SimRecorder.Path.cs` (shots, fly-path),
  `SimRecorder.Captions.cs` and `SimRecorder.Take.cs` (approach, capture, encoding) as partial files; 2026-10-09; shots as
  data, fly-paths, captions and Unity's encoder the same day; tool *Visualization ▸ Recorder*, opt-in): in Play, drives
  the clock to `startAt` (a model date; the paced loop at about the remaining gap per real second, `SimRecorder.Approaching`
  makes a Speed Profile wait; a date after the run's end, a finished run or 30 s without progress fail the take), then,
  with the paced loop off for the take (the legacy loop follows game time) and the clock's speed and pacing
  given back afterwards, renders its `shots` through its own `DontSave` camera into a `RenderTexture` at `fps` with
  `Time.captureFramerate` (each frame = 1/fps of playback). A **shot** (`SimRecorder.Shot`) has a framing — `Fixed`
  (position / rotation, taken from the Scene view), `Pose` (a placed `Transform`), `Element` (a `target` seen from
  `distance` / `elevation` / `azimuth` above `lookHeight`: follows the layout) or `Orbit` (the same, turning
  `orbitDegrees` during the shot) — `seconds`, an optional field of view, a caption `title` / `subtitle`, and `glide`
  (`glideSeconds`, 0 = the whole shot; the rest holds or orbits). **Fly-path** (`PoseAt(t)`): a glide follows a uniform
  Catmull–Rom curve from where the previous shot ended to the shot's start pose, its neighbours as control points where
  the camera flows on (a whole-shot glide of a non-orbit shot followed by another glide: linear progress, no stop),
  eased in / out where it starts or stops (`Ease`); rotation and field of view are slerped / lerped. **Captions**
  (`captions`, `BurnCaptions`) are burnt into the read-back pixels with the engine's TrueType rasteriser and Inter
  (`ChartFonts`, §4.5): the shot's title band bottom left (fading over 0.4 s where the title changes), the model clock
  top right (`clockFormat`, `culture`), the `liveValues` lines top left; placeholders (`FillTemplate`) `{time}`,
  `{shot}`, `{content:X}` / `{input:X}` / `{output:X}` (X = an element's GameObject name; the element's live counters,
  read only). **Output** (`output`): `Mp4` — Unity's own H.264 encoder (`UnityEditor.Media.MediaEncoder` with
  `VideoTrackEncoderAttributes`: High profile, a key frame per second, 2 B-frames, the take's `targetBitRate`; set by the
  editor as `SimRecorder.UnityVideoWriter`; nothing to install; a player build falls back to ffmpeg) — `Ffmpeg` (JPG
  frames + ffmpeg, H.264 yuv420p, `-preset slow`) or `Frames`. **Quality** (`quality`, 2026-10-09: the plain
  `VideoBitrateMode.High` gave ~3.6 Mbps at 720p and detailed scenes pixelated in motion): `Standard` / `High` (default) /
  `Maximum` = 0.12 / 0.3 / 0.6 bit per pixel per frame (`BitRate`: 3.3 / 8.3 / 16.6 Mbps at 1280×720, 30 fps; at least
  1 Mbps) or ffmpeg crf 23 / 18 / 14; `bitrateMbps` > 0 sets the MP4 bitrate directly; the frame size is rounded down to even. Each take goes to
  `outputFolder/<scene>_<timestamp>` (`video.mp4`, `frames.csv`: frame, model time, shot); a take stopped early closes
  the video so far. Element labels face its camera while it records. A view: no event, no item moved.
  **Editor** (`Editor/Recorder/`): `SimRecorderEditor` (buttons *Add shot from view*, *Frame selection* — the selected
  element at the Scene view's angle and distance — per-shot *View* / *Set*, and a fly-path preview slider; a property
  drawer shows only the fields of a shot's framing; the path is drawn as a gizmo) and **`RecorderSession`**
  (`[InitializeOnLoad]`): the MP4 writer, and takes started from the MCP — `SimRecorder.Configure` applies a take's
  settings (start date, length by `ScaleTo`, fps, size, quality / bitrate) in Play without changing the scene, a Recorder left disabled
  is enabled in memory for the take and disabled again after it, `SimRecorder.Finished` ends Play, and the status
  lives in `SessionState` (it survives the domain reloads of entering / leaving Play). MCP:
  `simulean_record_shot`, `simulean_record_start`, `simulean_record_status` (§4.11).

### 4.10 Visual detail setting and machine views

**Visual only** — views read element events /
state and never change the model, so a run gives the same results in every mode.

- **`SimVisualDetail`** (`Visuals/SimVisualDetail.cs`) — scene-wide animation level
  `VisualDetailMode` = `Auto` (default: detailed while `UnitySimClock.TimeScale` ≤
  `autoThreshold`, default ×60, and not at maximum speed; fast otherwise), `Detailed`, `Fast`.
  Views implement **`IVisualDetailView`** (`SetDetailed(bool)`, `FastRefresh()` — called at
  `fastRefreshHz` in fast mode) and `Register` / `Unregister` themselves; the manager pushes
  mode changes (`DetailChanged` event). Without a manager in the scene the views stay detailed (nothing is
  created at run time — the scene set-up adds it). Controls: inspector, `Mode` / `Cycle()` (for a HUD),
  optional `cycleKey` (default none) and the command line
  `-visual-detail auto|detailed|fast`, `-visual-detail-threshold N`.
- **`GantryStackerView`** (`Visuals/GantryStackerView.cs`, `IVisualDetailView` +
  **`IStackerAnimator`**) — animates a `UnityStacker` with the `GantryStacker` model (nodes
  found by name under `gantryRoot`): per `Stacker.CycleStarted` one cycle — travel to the pick
  point, head down, pick, up, travel, down onto the stack top, release, up — on **simulation
  time** (the element's cycle time, or `fallbackCycle` when placement is instantaneous; clamped,
  shortened while cycles queue, frozen while the element's work is paused, snapped beyond
  `maxLag`). The stacker skips snapping items the view is still animating
  (`IsAnimating(item)`), and calls `Flush(position)` when a stack completes so the stack is
  grouped with every item on its slot. Fast mode: the gantry parks, the component disables
  itself and the stacker snaps items as they are placed. Without gantry nodes the item flies the
  same path on its own.

---

### 4.11 SimuLean commands for the Unity MCP

`Editor/Pipeline/SimuLeanCommands.cs` registers static `[CliCommand]` methods (`com.unity.pipeline`, tag `simulean`)
that the MCP exposes as tools, so an agent builds and studies a model without `eval` C#:

**One key vocabulary (no aliases):** `object` (`objects` for a list) addresses a SimuLean object by name or
path in every command (`simulean_params`, `_model`, `_set`, `_load_table`, `_capture`, `_record_shot`, `_remove`,
`_rename`, `_invoke`, and `simulean_apply`'s ops); `newName` is a rename's new name, `model` a project model, `asset` a
library 3D asset; `factors` the catalog overrides / decision variables (validate, run_headless / run_start, diagnose,
optimize_start; `StudySpec.Factors`); `resultSet` a stored result set; `horizon` the run length (arguments and answers);
`until` a diagnosis' stop time; `warmup` and `noProgress` (arguments and answers); omitted or `-1` = "use the model's
(or the component's) value" everywhere; `team` names a resource's team in answers; `xAxis` / `yAxis` a chart's axes. Answers use the same words: the object a command acted on is `object` (`simulean_params`, `_set`, `simulean_apply`'s brief results), a stored set is `resultSet` (`ResultSetJson.Key`: the result-set file envelope `{"format", "kind": "resultset", "resultSet": {…}}` and the runner's answer) and `result_set` / `result_set_id` in data tables (SQLite / CSV exports, the store's cross-set queries: tables keep snake_case columns). No alias reads the old `set` / `element` keys: older result-set files must be regenerated.
`from` / `to`, `name` (a new object), `root`, `metrics`, `wait` / `cancel` are unchanged. `simulean_vocabulary` answers
the whole vocabulary from the code. A chart axis is written as the study writes it (`ChartValue.Parse`): a named
metric of the set, one of its factor paths, else a statistic path `[scope:]OBJECTS.statistic` whose objects may be several
(`Srv;Buf.utilization`) or `*` patterns (`*.utilization`, `team:*.utilization`), `model.makespan`; the scope any
`ResultScope` (a chart may read a member: `resource:*.utilization`). The old `metric:` / `stat:` / `param:` prefixes are gone.

| Command | Does |
|---|---|
| `simulean_types [type detail fields]` | The Library's entries (`SimuLeanWindow.CollectEntries`: elements, model-group tools, presets) and the tools of the window's other tabs (Visualization, Analysis…: labels, pathway arrows, speed profile, recorder, dashboard); with a type, its serialized fields and its config (catalog) keys, from a hidden throwaway instance — of the entry's prefab when it has one, so the values are those the Library places. **Compact by default (`SimuLeanFieldsView.cs`):** the type's own fields; the groups declared by `SElement` (Next Elements, routing, admission, labels, lists, resources, transport, walking) counted under `shared`, not listed; a distribution as one entry, its text `Type~p1~p2`; `distributions` gives every distribution's keys once (one line per type, `SamplerSpec.Fields`), `listItems` the fields of each listed list's items from the item type (`actions[i]`, `flows[i]`, `processBy.requirements[i]`, nested `tourTypes[i].stops[j]`). `detail=full` lists every field (a distribution's active type only); `fields=processBy,…` the fields under those paths. `type` matches the Library name, the component type, the component type without `Unity` or the engine type (`ElementExtractorRegistry.EngineTypeOf`) in any case, without spaces / hyphens / underscores (`NormalisedEntry`, unambiguous only); a wrong name answers a short did-you-mean, not the whole Library; `type=A,B,C` answers `types` (each its own answer or `error`). A field marked `[SimuLeanHelp]` (`Core/SimuLeanHelpAttribute.cs`) is listed under `help` by its path (a list item's as `stops[i].emptiesOrigins`). Without a type the list is the **Core pack's entries without decoration** (`Entry.Visual`; hidden entries left out), the other packs and the Core decoration one line each (count and names) under `packs`; `pack=` a pack, a group or a module lists those (decoration included), `pack=all` every entry; a preset carries `variantOf` (the type it places, `[SimuLeanVariantOf]`: Pallet Rack → Storage Rack). In a `pack=` listing each preset that places one element or one team says `places` — its type and key defaults (`Multi-Server · capacity 2 · delayStrategy Constant~5`, `Resource Team · members 1 · travel Straight · speed 2.5 (Physical)`; `PresetSummaries`): the preset placed once per editor session in a temporary additive scene, closed unsaved (not in Play, nor beside an untitled scene); an empty object slot of a list reads `(none)` (the Combiner's `inputOrder` read `[, ]`); `[SimuLeanHelp]` on the Combiner's `inputOrder` / `requirements` and the Source's `mode` / `ratePeriods`. With `detail=full` or `fields=` (`simulean_types` and `simulean_params`), `tips` gives the `[Tooltip]` of each field listed (and of the listed lists' item fields) that has no `[SimuLeanHelp]` — not a distribution's own keys (`TipOf`); not in a compact answer, which it would double. `[SimuLeanHelp]` on the Pick Face's `reorderPoint` / `reorderQuantity` / `startFull` / `emptiesDestination`. `type=A,B fields=X`: a type with no field under `X` answers `fields: []` and a `note`, not an error. A multi-type answer past ~40 KB (`MaxAnswer`, measured indented as the client shows it, `AnswerSize`; above ~50 KB an agent's tool spills it to a file) gives the first types in full and the rest by name and description, with a `note` naming them ("ask them one at a time or with fields="); a listing (`pack=all`, a pack, a group) past it gives its first rows whole and the others by name, pack and group, with a `note` (`Listing`: "pack=<pack> or pack=<group> lists them whole"). |
| `simulean_params object [detail fields]` | An object's fields (path, type, value) and its catalog keys / values (scene extraction); compact as `simulean_types`, the shared groups listed where they differ from a fresh component's. A list of records prints each item readably — a rule parameter `name = value`, a team member by its name (the generated `<Team>_<n>` when empty) and its properties, any other record as its fields set away from a fresh item's (`name` / `id` / `laneId` / `sku` / `kind` first, at most six; 20 items, then the count) — not its type name. `fields` filters the catalog keys too (`CatalogUnder`: a key equal to a path or continuing it with `.` or `[`; `fields=lanes` gave ~130 per-lane keys). A catalog value that is a team's, list's or element's internal id (`Setter_576743:2304`) is shown by its name (`CatalogOf`). |
| `simulean_add_element type [name x y z parent]` | Places an entry exactly as the Library does (`SimuLeanWindow.Place`, now returning the object and taking an optional parent); a tool of another tab gets its own object with the component, in its hierarchy group (`ModelHierarchy.Place`; a singleton already in the scene is returned). The answer gives its `size` (the footprint [x, z] in metres, `ModelLayout.SizeOf` — the world bounds of its own renderers, not those of an element nested in it; none without a renderer). A List, Downtime or Reaction (`ModelLayout.IsMarkerTool`: no footprint, drawn as a marker) placed without a position (x, y, z all 0) goes beside the model — a row behind it (+Z, 3 m beyond the elements' far edge, from their left edge, `ToolSpacing` 2 m apart, skipping spots another such tool holds; `ModelLayout.FreeSpotForTool`) — and the answer says `placedAt` (`simulean_apply`: `placed`). Other tools keep the position given: a transport node, sign or floor zone mean their place; data, study and analysis tools are not drawn. |
| `simulean_connect from to` | Adds `to` to the Next Elements of `from` |
| `simulean_place_model [asset name x y z rotY scale parent]` | A library 3D model as decoration: a model folder (`HospitalBed`), a kit file (`WaitingChairs/Seat_Table`) or an asset path, under a root `Decor` group by default, keeping the import's root rotation / scale; no model = the list of library models (`instantiate_prefab` refuses `.fbx`) |
| `simulean_disconnect from to` / `simulean_remove object` / `simulean_rename object newName` | The inverse commands (`Editor/Pipeline/SimuLeanEditCommands.cs`): `to` leaves the Next Elements of `from` (a warning when later outputs move up one index; two transport nodes: their track); an element / team / list / table / tool deleted with its children, every object reference to them in the other SimuLean components cleared and listed (`referencesCleared`; list entries removed, fields set to none; the referencing objects recorded whole first, so the one undo step brings the references back too); a rename checked as a new name (`EnsureFreeName`), the team / list / table name field updated when set. Both warn about texts (expressions, queries, labels) that still name the old object |
| `simulean_reorder object next` | The Next Elements of an element in a new order (`SimuLeanEditCommands.Reorder`; the order routing by index reads): `next` lists every one of them once, `;`-separated; undoable; also a `simulean_apply` op (`reorder`) |
| `simulean_move object x z` | Moves an element, a team or a tool (a SimuLean object) to world x / z, its height kept, with its children (a rack's lanes, a team's member previews; `SimuLeanEditCommands.MoveOf`, one undo step; also a `simulean_apply` op, `move`, both coordinates required). Answers `from`, `at`, `size`, the footprints it now `overlaps` (`ModelLayout.Overlaps`) and what the place changes in a run (`MoveEffects`): the warning "travel distances changed" for an element without a location node when the model has a Straight team (its auto point is its place, `SceneExtractor.AddStraightTravelPoints`), a Straight team without a home node (its members start there) and a transport node whose tracks of automatic length change (before → after, m); a `note` when a location node, a home node or a tool's place does not move with it. `simulean_set x` points to it (`FieldHint`). In `simulean_apply`, an `add_team` without a position is placed after the whole list (`ModelLayout.FreeSpotForTeam(except)`, answered under `teamsPlaced`), so it does not stand where the list adds an element later; the `overlaps` cover the objects moved too. |
| `simulean_set object key value` | A serialized field by name / path (`SerializedObject`: numbers, bools, strings, enums by name, object fields by GameObject name or by asset path — `Assets/…/Doctor.prefab`, the asset or its prefab's component of the field's type —, lists comma-separated, vectors (`x,y,z`, `x,y`), colours) — undoable; a field of an inactive distribution answers a `warning`; `properties.NAME` (the key a study varies) is the element's user property (`SElementProperties`, added when missing; the property created when missing; a number, or else a text with a warning; quotes around the name dropped, from the case-2 v2 run, where the agent wrote the property list through the generic tools). **Since 2026-11-09:** a distribution field takes its text (`delayStrategy = Triangular~0.6~1~1.6`, `ExponentialMean~5` stored as `Exponential` with rate 1 / mean, a number = a constant; `SimuLeanDistributionText.cs`, `SamplerSpec.Parse`) and a wrong text's error lists every type's keys; setting a rule (`outputStrategy.rule`, `offerRule`, a team's `requestOrderName = rule:X`) fills its `ruleParameters` with the `[RuleParameter]` fields missing there at their initial values and lists them (`ruleParameters` in the answer), and `ruleParameters` takes `Name = value, …` text (an unknown name warns with "did you mean"; `SimuLeanRuleParameters.cs`); errors name list items as `actions[0]`, never `.Array.data[0]`; an expression field whose owner reads an item label nothing sets warns (`ModelLogic.UnsetLabelReads`, §3.24). A list query field (`pullQuery`, a team's `requestOrder`) takes its text `sku == 'A'; qty > 0 | due desc` (`ListQuerySpec.Decode`); a field declared by `UnityStorageRack` itself (its layout) rebuilds the rack's lanes at once with an undo record (`RebuildRack`; the answer's `rebuilt`), inside `simulean_apply` once after its last edit. A rule parameter list that changes nothing warns — set while its rule is empty (the extractor drops it), or naming a parameter its rule lacks, with "did you mean `outputStrategy.ruleParameters`" when the name is the Queue's other rule's (a Queue has `ruleParameters` for `offerRule` and `outputStrategy.ruleParameters` for its routing rule); `RuleParameterChecks.Problems` (`Config/RuleParameterValue.cs`), after a set of a list or a rule key, in `simulean_apply` once after its last edit (a rule set after its parameters is fine); the scene extraction logs the same for elements (`simulean_validate`; a team's list without a dispatch rule is already a build error). Setting a value turns on the switch that says it is used (`SwitchOn`: `useX` beside `X` — `usePieceTime`, `useChangeTime`, `useLeadTime` —, `overrideLoadTimes` for a use's `loadTime` / `unloadTime`, `overrideEmptiesTimes` for a milk run's empties times), reported as `alsoSet`: an explicit `transportBy.loadTime = 0` is then exported and silences the storage's zero-handling warning. In place of a list index, an item id or a pattern (`lanes[B0_S0_L0].node`, `lanes[B0_*].node`, `lanes[B0_*\|B1_*]`, `lanes[*]`; `*` any text, `?` one character): `SetSelected` matches each item by its `laneId` / `id` / `name` / `sku` (`ItemIdFields`) and sets each as by its index; the answer gives `matched` and `items`; no match is an error listing the ids. A rack's `rebuilt` lists its 0-based lane ids (`IdRange`). `value=""` clears the field (an empty text or list, no object reference, as `apply`'s `set` with `""`): the MCP server refuses an empty *required* argument, so `value` is optional with the default `SimuLeanCommands.NoValue`, which the command refuses as missing ("value is required"); a JSON `null` reads as missing too. |
| `simulean_add_team name [members travel … parent]` | A resource team with n members. Without a position (x, y, z all 0) it stands in front of the model (`ModelLayout.FreeSpotForTeam`: 3 m before the nearest footprint, towards −Z) and says so (`placedAt`); the answer gives `at` and the `overlaps` of its members; an unknown `kind` gets a did-you-mean (walking roles such as setters / mechanics: `operator`). The answer's `speedFrom` says where `speed` / `units` came from — the `kind`, the team's name read as a kind, or the default (1 distance unit per model time unit, Model units). |
| `simulean_load_table object file [sheet readAtRun]` | A table with `Start` and `Rate` and no `Time` (or a source already in RateTable mode) puts the Source in **RateTable** mode (its inline periods cleared) and answers one line per period and what happens after the last; a schedule's `arrivals` are counted from `Q` (it said "N rows, one arrival each"). Puts a Source in Schedule mode with a `UnityDataTable` (named after the source, created or reused, in its hierarchy group) reading the file (CSV / `.data` / Excel; absolute or relative to the project or `Assets/`), loaded into the project database; `readAtRun` = read the file at each run start (§4.2); one undo group, reverted when the table cannot be loaded. The answer says how the rows were read — a setup matrix one line per row (`SetupMatrix.Describe`: `(first setup) → any: 10`, the first 12) with the `lookup` rule (`SetupMatrix.LookupText`); a schedule its arrivals, first / last `Time`, `Q` and label columns, and `notes` for a column that differs from `Time` / `Name` / `Q` only by case (those names are case-sensitive). |
| `simulean_model [object parameters root]` | The `clock` block also with `object` (an object's numbers are in its time unit). The scene's model in one answer: `ModelDescription.Of` (engine, `Optimization/ModelDescription.cs`) over the extracted `SimulationConfig` — the clock (time unit, horizon, warm-up, start, end condition, seed), every element (type without the `Unity` prefix, `next` in connection order, parameters under the catalog keys a study varies), the other connections (lists, ports), teams (members with labels, catalog parameters; members by their built names, an unnamed one `<Team>_<n>`,
`ResourceBuild.MemberName`), lists, downtimes, reactions, data tables — all by name; each team and downtime with its
`metrics` (`path` — `team:T.<statistic>` / `downtime:Id.<statistic>` —, `statistics` once —
`MetricCatalog.TeamMetricStatistics` and `state:<STATE>`, `ResultMetrics.Downtime` plus `ResultMetrics.Repair` with a
repair crew —, a team's `eachMember` (`ResultMetrics.Resource`: a run's `resources` block, not a study metric) and, for a
downtime with several generators, `eachTarget` `downtime:Id@<target>.<statistic>` with its `targets` — a team target's
members —, as `RunResults` names them); each element also `at` [x, z] and `size` [x, z] (2026-11-10:
`ModelLayout.AddFootprints`, after its name and type: where it stands and its footprint, so an agent spaces neighbours by
their real size — a Queue's pad is 3 m wide); `object` = one object, `parameters=false` = the structure only; `section=logic` answers instead the logic report of §3.24 (`ModelLogic.Of(...).ToData(object)`: every decision as one sentence with its raw settings, what it reads / writes; a setting its strategy does not use left out, a default decision one short line; the wake-ups and notes; in Play each decision's run-time counters `live`, `ModelLogic.Live` over `UnitySimClock.Model`). Read-only. Every id in a value is a name (a team, a list, an element; the scene's automatic points `pos:` / `work:` / `home:` as `X (its position)`); a team shows `travel`, `homeNode`, `requestOrder`, `resourceOrder`, `namedOrders`; a downtime its `repairBy.*` texts (the crew's team), its `stateName` and, a Timetable, its `rows` (`start+duration [target] [state] [mode] [code ...]`, the first `LongList` and "… n more" unless `full`; `TimetableRows`); `object` = a team, downtime, list… answers no empty `elements` list; `detail=compact` (the default: empty texts and automatic points left out, a list of `ModelDescription.LongList` items or more — a storage's `lanes` always — summarised as `lanes` = "N items" and one `lanes[*].field` line each) or `full`. `network` — `ModelDescription.NetworkOf`: the nodes `id (x, z)` with the elements located there, the tracks `A – B: length` (`→` one-way; the straight distance when no length), the teams on it (Network, Straight; home, speed, units); compact leaves out the scene's automatic points (counted) and caps each list at `NetworkShown` (40); `object=network`, the network GameObject's name or one of its nodes answers it alone. |
| `simulean_vocabulary [root section]` | The one key vocabulary as data (`Editor/Pipeline/SimuLeanVocabularyCommands.cs` → engine `Optimization/Vocabulary.cs`, which the runner and the tests can read). The whole (~80 KB) is over what an agent's tool reads: by default an index (`Vocabulary.Index`: each section's name, what it holds — `Vocabulary.Sections` —, its size, the parts of one over `MaxAnswer`, and how to ask), `section=` one or several sections, a part (`arguments.study`, `expressions.functions`) or `all` (`Vocabulary.Section`: the answer keeps the vocabulary's shape, so the sections together are the whole — `runnertests` `TestVocabularySections`, EditMode `TheVocabularyAnswersAnIndexAndItsSectionsTogetherAreTheWhole`). The sections: every command argument under its concept with the commands that take it (reflection over `[CliCommand]` / `[CliArg]`; EditMode `EveryCommandArgumentHasOneConceptOfTheVocabulary` fails on an argument no concept covers, or a retired name), the retired names, the factor / metric / result scopes (from `ParameterScope`, `MetricScope`, `ResultScope`), the path grammar with one example per form, the statistics per scope (`ResultMetrics.Of`, `Repair`, `Preemption`, `TallyStatistics`), the state names (every public static `ElementState` field of the engine, plus those the scene's built model registers), the expression roots / built-ins / functions with arities (`Expression.Roots`, `EntityScope.ItemBuiltIns` / `ElementBuiltIns`, `Expression.FunctionArities`), the option enums and the catalog keys of the common / team / downtime / reaction schemas with their rule (`ParameterSchema.Declarations`) |
| `simulean_validate [factors metrics root horizon replications seed]` | Extract + build without running: `Study.Check` (§3.5) on the scene's model with the optional factors / metrics (`horizon` / `replications` / `seed` only shape the echoed `settings`); `ok`, the counts (elements, connections, teams, scenarios, metrics) and the errors / warnings a run would meet (logged by the extractor and the build too; a choice rule without a `Why` or used where its declared primitive would do, §3.25; an item label read that nothing sets — "did you mean the text 'A'?", §3.24). The answer opens with the effective run `settings` (`StudySpec.Settings`: time unit, horizon, warm-up, end condition, seeds; the calendar `start` when the model has one) and carries `info` (`StudyCheck.Info`: defaults the build chose — a spatial team's home node; a station that uses a team whose members a downtime stops, with `processBy.preemption = None` — the breakdown will not interrupt its job, `Study.MemberStopsNotInterrupting`; each LogNormal / Gamma / Weibull distribution by its mean, `Study.ShapeScaleMeans`, beside the exponentials'). Since 2026-11-10 `info`
also names every two elements whose footprints overlap on the floor by more than `ModelLayout.OverlapTolerance` (0.1 m)
in both directions — the teams' members (their previews) included, two of one team not compared — information, not an error: a run does not care, a picture does (an element nested in another is not
compared). `info` lists each exponential distribution of the elements, teams and downtimes by its mean (`Study.ExponentialMeans`: `Src.interArrivalDistribution mean 5 (rate 0.2)`; `Exponential~x` is a rate, `ExponentialMean~x` a mean, and a scene stores the mean as a rate). |
| `simulean_run_headless [horizon replications seed factors metrics root detail top sort offset]` | A `SceneStudy` (§3.19): `factors` = catalog overrides `Owner.key=v1|v2;…` (one scenario per level combination), `metrics` = `name=path;…` — both also as JSON (`StudyInputs.FactorsOf` / `MetricsOf`, in the engine; `{"Buffer.capacity": [2, 4]}` or `[{"path": …, "values": […]}]`, `{"out": "Sink.input"}` or `[{"name": …, "path": …}]`; levels as numbers, texts or booleans, so a value may hold `;` `=` `|`; string arguments, as the client schema of object arguments is broken upstream); returns per scenario the named metrics and the model's statistics, plus the elements' and resources' KPIs with `detail=full` — the default without `metrics`; with `metrics` the default is `detail=metrics`; an answer over `MaxAnswer` is a table and its pages (`top` / `sort` / `offset`: `AnswerPages.Study`, §3.5 *Answer size*), read from the last set by `simulean_results action=read` without a second run (`lastStudy`: the set with its warnings, settings, model and reference scenario, so the pages are the run's own answer). The answer opens with the effective `settings` (time unit, horizon run, warm-up, end condition, replications, seeds, confidence; explicit scenarios with their own `replications` are echoed as they run: one count when they agree, else the largest plus `replicationsPerScenario`); each named metric is `{mean, halfWidth, n}` over the completed runs (the half-width of the `ResultSet.Confidence` interval, null with one value; NaN values — a time not reached — left out of n); with `detail=full` also `min` / `max` over two runs or more; `compareTo` = a scenario (name or 1-based position) the others are compared with on common random numbers — each other scenario's `paired` gives per metric `{diff, halfWidth, n}` of its runs minus the reference's of the same seed (`StudySpec.PairedOf`); a Queue left at the default capacity 100 that a run filled is a `warnings` note (`StudySpec.DefaultCapacityNotes`), as are the model's own run-time warnings (`StudySpec.ModelWarningNotes`, §3.18), the model block and KPIs are means (seeds `seed`, `seed + 1`, …; one thread, deterministic); NaN → null. The scene is not changed; the result set is published to the Results tab, never stored (*Save* there keeps it). **Studies (`SimuLean.Net/Optimization/StudyOptions.cs`; also `simulean_run_start`, `simulean_validate`):** `scenarios` = explicit scenarios as JSON (their paths become the factors, own `replications`; every scenario gives every factor any of them gives — the error names each one missing with the model's value, `Study.CurrentLevel`); `where` = a condition over the factors (each path with every character that is not a letter or digit as `_`: `Buf1_capacity`) keeping the combinations that meet it, as explicit scenarios (`Study.FactorLabels`); `derived` = `name=expression;…` computed per completed run from the metrics before it and the study's factors at the run's scenario levels (named as in `where`; a metric may not share a factor's name) (the expression language, §3.18), reported like them; `confidence`; `warmup`; `noProgress` (the end condition `StopWhenNoProgressFor` for the study's runs, −1 = the
model's, 0 = off; `StudyOptions.NoProgress`, `StudySpec.NoProgress` in a runner study file); a metric path with `[*]` (`Exit.tally:tis[*]:mean`) is checked unsplit and expanded after the runs into one metric per label value (`name[value]`); `experimenter` = a scene Experimenter's study (`Experimenter.PrepareStudy`: its factors, scenarios, metrics and settings; `horizon`, `replications` ≠ 1, `seed` ≠ 1 and `metrics` override / add) |
| `simulean_run_start …` / `simulean_run_status [cancel wait top sort offset]` | The same study in the background: the scene is read on the main thread, the runs go on a worker (all cores) and the call returns at once; the status gives runs done / in all, then the same answer as `simulean_run_headless` (or the error; the worker keeps the answer whole and each status call pages it with its own `top` / `sort` / `offset`); one run at a time; `cancel` keeps the runs done; status `none` / `running` / `completed` / `failed`. A long study no longer blocks the editor nor times the MCP out. `wait`: see below |
| `simulean_optimize_start [factors metrics fitness optimizer algorithm algorithmSettings maxEvaluations maxSeconds replications horizon seed threads root]` / `simulean_optimize_status [cancel wait]` | An optimisation in the background (`Editor/Pipeline/SimuLeanOptimizeCommands.cs`): the search given in the call — `parameters` in the factor grammar with a range (`Buffer.capacity=1..10`, `team:Welders.members=1..4`; `Study.ParameterOf`; JSON `[lo, hi]` too) or options (`a|b|c`: `Study.OptionsOf` → `OptionsParameter`, searched over those values only — numbers too; before 2026-10-20 a numeric key given options was searched over the range between them, and a property as a real), a whole-number range of a real key answering a warning that suggests the options (`Study.RangeNote`), `metrics` as `simulean_run_headless` (`Study.MetricOf`; checked on the built model, `Study.Check`), `fitness` maximised (the only metric when omitted) — or a scene `Optimizer`'s setup (`Optimizer.Prepare`, the same `Optimizer.Setup` its inspector button runs), the other arguments overriding its settings; `algorithm` by name (`genetic`, `random` or any `AlgorithmSettings` type, `TypeCache`) with `algorithmSettings` `name=value;…` into its fields. Runs on a worker (`Optimizer.Setup.Run`: the search, then the best re-run for every replication); the status gives the iteration, candidates and best so far, then the best candidate (fitness, values, each metric `{mean, halfWidth, n}`), the stop reason and the failed candidates. The scene is not changed; the result set is published, never stored. One search at a time |
| `simulean_diagnose [until seed factors all root noProgress]` | One replication of the scene's model run headless (`Editor/Pipeline/SimuLeanDiagnoseCommands.cs` → `Study.Diagnose`) to `until` — or, omitted (-1), with `StopWhenEmpty` until nothing can happen (stall, deadlock, empty; at most the horizon) — and its state (`ModelDiagnosis.Of`, engine): the run monitor's verdict and `waiting` list, each element holding items, stopped or not idle (state, items, what it waits for — `RunMonitor.WaitsFor`; sinks and idle empty elements only with `all`), each team's pending requests (requester, task, count, label, time waiting) and members (state, task, for whom, step, `taskSince`, carried), each list's entries; `progress` (the last item entry / exit of any element, `noMovementFor`, the items in the model, the oldest pending request, the longest task in progress) and `notes` (`ModelDiagnosis.Notes`: facts, no verdict — no item moved, a request waited or a task lasted for a quarter of the run or more while the run monitor sees no stall because events such as shifts or reactions are still scheduled; whether the blocking is the design's or the logic's is the modeller's call; 2026-10-20, from the case-2 v2 run; and, verdict or not, each **circular wait** — `RunMonitor.CircularWaits`: a strongly connected set of the "waits for" graph `BlockedBy` (an element holding items ready to leave — BLOCKED, or a buffer with outputs — → each element downstream that holds items with no room left), one cycle each, named "Circular wait: Lathe Buffer → Lathe → Check Buffer → Check → Lathe Buffer" (`ModelDiagnosis.CircularWaitNote`); diagnose only, the run monitor's warning is unchanged); `factors` = one level per factor (a what-if); `noProgress` = the end condition `StopWhenNoProgressFor` for this run (a stall beside a periodic signal or reaction ends with `endReason` NoProgress — the answer also carries `noProgress` / `noProgressTime`); each element holding items carries `decisions` (`ModelLogic.Mark`, §3.24: the decision sites where its items wait); an unpacking Separator (a Line-Side Unpacker) also `carriers` (`Separator.PlacesUsed`, against `capacity`) and `pieces` apart (its `items`, the content, counts both). Read-only, synchronous. A decision's item labels leave out the size labels of the item's 3D model (`LogicLive.SizeLabels`: width, height, length, loadFloor; also in `simulean_model section=logic`); the data form gives them apart as `size`. |
| `simulean_record_shot [shot framing object distance elevation azimuth orbit lookHeight position rotation pose fromView seconds glide glideSeconds fov title subtitle index remove recorder]` | Adds or changes a shot of the scene's `SimRecorder` (placed when there is none, §4.9): by name, empty arguments keep the shot's values (a single space clears a text); a target makes an element shot; `fromView` takes the Scene view's camera (a fixed pose, or with a target its angle and distance); `index` inserts / moves; without `shot` lists the shots (start, length, camera position). Undoable |
| `simulean_record_start [scene startAt seconds fps width height exitPlay recorder quality bitrateMbps]` / `simulean_record_status [cancel]` | Records a take in Play (`RecorderSession`, §4.9): opens the scene (modified scenes must be saved), the take's settings apply to it only (`seconds` stretches the shots), Play ends with the take; status `starting` / `approaching` / `recording` (frames / in all) / `completed` (the MP4's path) / `failed` / `stopped`; `cancel` ends Play keeping the video so far |
| `simulean_apply operations` | A JSON list of edits in one call (`Editor/Pipeline/SimuLeanBulkCommands.cs`): `add`, `set`, `connect`, `disconnect`, `remove`, `rename`, `add_team`, `invoke`, `repeat`, each run by its command; all or nothing — **every operation is tried**, then on any error every edit is undone (`Undo.RevertAllDownToGroup`) and the error names every bad operation (a later one may fail because of an earlier one) —, one undo step (`Undo.CollapseUndoOperations`); a short result per operation; `validateOnly=true` checks the list the same way and undoes it (an `invoke` is not run, only its object checked); the label check of `simulean_set` runs once after the last operation, over the objects set. The answer is a summary — `applied`, `operations` (count per kind), `created` (the names placed), `rebuilt` (racks whose layout changed: their layout summary and their built extent
`Built extent W × D m`), every warning — and `detail=full` gives `results`, one per operation. It also gives
`sizes` (the footprint of each object created) and `overlaps` (the footprints that overlap one of them, as in
`simulean_validate`'s `info`). |
| `simulean_repeat group [count offset names connectFrom connectTo]` | Copies a group of objects (with their children) N times, each moved by `offset` and its model objects (elements, teams, lists, tables) renamed by `names` (`{name}_{i}`, checked unique); every object reference inside the group is remapped to the copy's own members (`SerializedObject`, the hierarchies paired object by object), references outside it kept; `connectFrom 'Outside>Member;…'` / `connectTo 'Member>Outside;…'` connect each copy. One undo step |
| `simulean_invoke object [action]` | Runs a parameterless action of the object's components — marked `[SimuLeanAction("…")]` (`Core/SimuLeanLibraryAttributes.cs`: `UnityTravelMatrix.FillStraightLine`, `UnityFloorZone.Rebuild`, `UnityPathwayArrows.Rebuild`, `StationModel.Rebuild`, `UnityWalkGrid.Bake`) or `[ContextMenu]` —, by method name or menu label; no action = the list with descriptions. Undoable |
| `simulean_capture [objects elevation azimuth distance fov shot width height path connections]` | A PNG of the scene (`Editor/Pipeline/SimuLeanCaptureCommands.cs`): the named objects — or the whole model — framed from an elevation and azimuth (`SimRecorder.Around`), or a Recorder shot's framing (`SimRecorder.Framing`); rendered by a temporary hidden camera with the main camera's settings into `Recordings/Captures/` (git-ignored) or `path`; returns the path, what was framed and drawn, and the camera. The scene is not changed. The whole model is `ModelLayout.OfModel` — every element's own renderers (else its position), the transport nodes, each team's members (their edit-mode previews, `TeamMemberPreviews.EnsureAll` first: a scene opened in the same tick has none yet) and home node, the lists, downtimes and reactions; never the floor (`SimuLeanFloor`), the decoration of `simulean_place_model` or the analysis tools (they show results, not the layout). `Frame` puts the camera at the closest distance at which every box corner and point fits both the vertical and the horizontal field of view with an 8 % margin, and centres the picture on what it shows (four passes; it fitted the bounding sphere's radius to the vertical field only, so the model filled about a third of the picture); the whole model is framed at least 8 m across when it is smaller in both directions (named objects 2 m). Over the render, `ConnectionOverlay` (GL, no depth test) draws the connections of the `connections` groups (default `flow,lists,network`; `all`, `none`) and a disc for each object without a mesh, its name as a temporary `TextMesh` (`HideAndDontSave`, destroyed after the render). The model thumbnails use the same framing and drawing. EditMode `CaptureFramesTheModelWithItsNodesAndTeamsNotTheFloor`. A Network team's preview stands at its `homeNode` in every order of the edits; `TeamMemberPreviews` also rebuilds a preview that is not the one it built (restored by an undo / redo or a reload) and when the travel changes. Tools placed without a position skip transport nodes and teams too (`FreeSpotForTool`). |
| `simulean_results [action resultSet formats kind yAxis xAxis groupBy title path name detail top sort offset]` | Result sets (`Editor/Pipeline/SimuLeanResultsCommands.cs`): `list` (the last published set, `ResultsOutput.Last`, and the stored ones), `read` (the run answer's scenarios and metrics, plus the metadata; the last study's as its command answered it; `detail` metrics / full — empty = as the study answered, a stored set: metrics —, paged by `top` / `sort` / `offset`), `save` (`ResultsOutput.Save`, optionally renamed), `export` (`ResultsOutput.Export`: csv / excel / sqlite / json), `chart` (`ChartBuilder.Build` + `ChartRenderer.Save`, PNG or SVG by the extension, `Recordings/Charts/` by default; `yAxis` / `xAxis` written as in a study (a named metric, a factor, a statistic path `[scope:]OBJECTS.statistic`: `ChartValue.Parse`); an empty chart is an error naming the set's metrics), `delete` (`DeleteResults`: `path=` a file or folder that `export`, `chart`, `simulean_capture` or a `simulean_record_start` take wrote in this editor session — noted by `AgentWrote` in a `SessionState` list, kept across domain reloads like `AgentScenes`; one inside a folder noted is one too — and that lies inside the results folder or `Recordings/`, never the project database, moved to the OS trash with no dialog (`Trash`: Windows `SHFileOperation` with undo, macOS `~/.Trash`; elsewhere, or when it fails, nothing is deleted; called through the replaceable `MoveToTrash`, which the EditMode test swaps for a temporary folder); or `resultSet=` a stored set that `save` stored in this session (`AgentSaved`), removed from the store (`ResultStore.Delete`); anything else is refused, naming why). `resultSet` = `last`, a stored id or name |
| `simulean_query [sql import table sheet append maxRows]` | The project database (`Editor/Pipeline/SimuLeanDataCommands.cs`): no argument = its tables and row counts; `sql` = a statement's rows (at most `maxRows`, 200 in all, with the count); `import` = a CSV / Excel file into a table (`TableFiles.Read` + `ProjectDatabase.ImportTable`, replacing it unless `append`) |
| `simulean_fit [values sql file column sheet criterion top into pick]` | Distribution fitting: the data from `values`, a query's or a file's column; `DistributionFitter.FitAll` (AD or KS) ranked, each with its parameters, its `mean` and `std` (`FittedDistribution.Mean` / `StdDev`: what the parameters mean in the data's units — a LogNormal's mu / sigma are of ln x), sampler spec, p-values, AIC and verdict; `into = Element.field` writes the `pick`-th fit into a distribution field as the Fit tab's *Insert* does (undoable) |
| `simulean_models [action model newName example open]` | Model management (`Editor/Pipeline/SimuLeanModelCommands.cs` → `SimuLeanModels`, §4.6.1): `list` (the models folder, the open scene, the models recent first, the examples with their copies), `new`, `open`, `duplicate`, `rename`, `copy_example` (an example copied into a new model, warned when copies exist). What would replace the open scene refuses while a scene has unsaved changes (no save prompt for an agent; `open=false` makes the model without opening it). `action=new` takes `timeUnit` (`s`, `min`, `h`, `d` or seconds per unit; `TimeUnitSeconds`) and `horizon`, set on the new scene's clock before it is saved (`SimuLeanModels.Create(configure)`, `SetClock`); the answer echoes them as `settings` (the defaults in words: `warmup` "none (0: statistics from t = 0)", `seed` "a new one each run in Play …; headless runs and studies use seeds 1..N"). `action=save` saves the open scene only when it is one of the project's models (`SimuLeanModels.ModelOfScene`: never an example, the neutral scene or another scene) **and** these commands created, copied or opened it in this editor session (`AgentScenes`, a `SessionState` list kept across domain reloads: the folder alone does not tell whose the changes are — a user's own model edited by hand lives there too); otherwise it refuses and says to ask the user. A clean scene answers `saved = false`. `action=new` also takes `seed` (the clock's Fixed seed mode with it, `UseFixedSeed`) and `warmup` (`SetClockRun`), echoed in `settings`. `action=new` uses an existing folder of that name that holds no model (a scene or a card) — data put there first — and imports it if Unity has not yet; a folder that holds one is a taken name (`SimuLeanModels.CheckNewName` / `MakeModelFolder`; before, a folder on disk that Unity had not imported made `CreateFolder` leave a `Name 1` folder behind). `simulean_set` / `apply` set a team's size by a number (`members = 3`: `SimuLeanCommands.ResizeMembers`, the new members copies of the last, numbered on). |
| `simulean_card [field value model]` | The model card of the open scene (or of `model`): no field = every field; `field` + `value` sets one of `displayName`, `description`, `author`, `represents`, `kpis`, `parameters`, `assumptions` (the others are kept by SimuLean), saved at once — the answer gives the text it replaced as `previous` (the card keeps no history); `scripts` = the open model's choice rules and its decisions by rung (§3.25, `LogicReport.Scripts`). `simulean_model` answers the open scene's card as its `info` block (the whole model only) |
| `simulean_test [filter]` / `simulean_test_status [wait]` | `SimuLeanTestRunner` (never the MCP's `run_tests`) |

**Agent documentation.** [`AGENTS.md`](../AGENTS.md) (set-up and rules) and `docs/agents/`: `workflow.md`, `errors.md` and
`commands.md`, generated from the `[CliCommand]` / `[CliArg]` attributes by `Tools/Docs/command_reference.py` (the
`commandreference` step of `run-all.ps1` fails when it is stale). **Core and topic modules:** `workflow.md` is the short core (~10 KB: the loop, the vocabulary, the build format, the run settings and
time unit, the metrics grammar, the retry traps, an index), and `docs/agents/topics/` holds the modules read on demand —
`resources.md`, `warehouse.md`, `logic.md` (with the escalation ladder), `setup-and-data.md` — each with the exact keys
in recipe form; the skill and `AGENTS.md` read the core, then only the modules a task needs. `docsymboltests` checks the modules too.

**Waiting for long jobs.** The `_status` commands of runs, optimisations, tests and takes take
`wait` (seconds, at most 55): `JobWait.Until` (`Editor/Pipeline/JobWait.cs`) answers as soon as the job is no longer
going on (status not `running` / `starting` / `approaching` / `recording`) or when the wait is over. The command returns
a task: the status is read on the main thread at each editor tick (every 0.25 s, `EditorApplication.update`) while the
pipeline server awaits the task off it, so the editor and the job go on; a domain reload ends the wait with the status
then. While a call waits, the server's other commands queue behind it (one command at a time). It replaces the agents'
sleep-and-poll through `eval`. A run
answer also gives, per scenario, `waiting` — who waited for what in the first replication that stalled or deadlocked.

**Answers.** Every command answers a JSON object with its fields
plus **`errors`** and **`warnings`**: `AgentReply.Of` (`Editor/Pipeline/AgentReply.cs`) captures what is logged while
the command runs (`Application.logMessageReceivedThreaded`: the engine's `SimLog` through the editor bridge, the
extractor, the editor; worker threads included), messages only, each once, at most 20 of each. **Attribution
:** a capture is entered as the logging scope of its work (`LogCapture.Enter`, an `AsyncLocal` that flows into
the tasks and parallel loops it starts); a message logged in a scope goes to that capture and the ones it was entered in,
one logged outside any scope to every command capture alive and to a background job only from a worker thread — before,
every capture took every message, so a background run's warnings appeared in a command polled meanwhile and the run's
answer collected the main thread's logs. The background run and the optimisation enter their capture detached around
their `Task.Run`. A message logged with an object (`Debug.LogWarning(m, context)`) gets its name in front when it does
not say it (`NamingLogHandler`, a pass-through `ILogHandler`; main thread only). What stays unattributed: a command that fails
throws as before, its message followed by the warnings logged until then. Lists are fields (`simulean_types` →
`types`). A study is **checked before it runs** (`Study.Check`, §3.5): an error refuses the run with every problem in
the message ("did you mean"), the warnings go into the answer. A metric's error whose path reads like an expression
(`+ * / ( ) ^`) adds that a metric is one path and `derived` combines them (`Study.ExpressionHint`); a level written as a
range (`1..10`) is refused with "a study lists its levels; lo..hi is simulean_optimize_start's" (`Study.RangeHint`); a
metric constant or meaningless by its element's type is a warning (`Study.FlowOnlyMetricNote`: a Source's `stay_*` /
`content*` — always 0, the time held at a blocked source is its `state:BLOCKED` —, a Sink's `stay_*` / `utilization` /
`content_avg` — its time in system is `tis_avg`). A run answer is `{settings, scenarios, errors, warnings}` (`Settings(study)`: nothing
the run used is a silent default); each
scenario has `runs {ok, failed}` and `firstError` — a failed run is never averaged in nor reported as a null metric —
and `errors` names each scenario with failed runs. The background run captures the log for its whole length.
**Edits and their follow-ups.** `simulean_set` applies one field (`SimuLeanCommands.SetField`, a typed answer — a
distribution's `value` as its text `Triangular~3~4~6`, `DistributionText`; a refused `sendTo` / `receiveFrom` naming a list
says the mode is `List` and the list goes in `pushToList` / `pullFromList`, `ListModeHint`; a `rule.X` key points to the
site's `ruleParameters`, `FieldHint`), then
runs the post-set hooks of the edited component (`SimuLeanPostSetHooks`: a rack's layout field rebuilds its lanes,
`RebuildRack`; a lane's `laneLengths` / `laneSpacing` refits its rollers or floor marking,
`UnityLengthLimitedQueue.FitLaneViews` — what the inspector's `OnValidate` calls; a rule site checks its parameter lists, `RuleParameterChecks.Problems`; an element adds its flow warnings;
an expression field checks the labels it reads, `LabelWarnings`) — a new follow-up of an edit is a hook, not a type test.
`simulean_apply` passes an explicit `EditSession` to its `set` operations: the rebuilds and the rule / label checks wait
for its last edit (the rebuilds only when the list is kept). Each operation answers as its command does
(`AgentReply.Run`, the typed form of `AgentReply.Of`) and `simulean_apply` reads those answers as JSON objects, without a
round-trip through text.
`simulean_connect` checks what Next Elements allows: `from` must have an output, `to` an input **or be a Combiner**
(`SElement.TakesConnections`, the Connect tool's rule too: the Combiner's `HasInput` is false — no input strategy — but its inputs are its incoming
connections; the command refused it before), a repeated connection is a warning, and an element that pushes to a list is
switched to its Next Elements as the editor's Connect tool does (warned); `simulean_set nextElements` warns about the
same cases. **Unique names**: `simulean_add_element name=` and `simulean_add_team` refuse a name another
SimuLean object of the open scenes has (`EnsureFreeName`, proposing a free one; edit-time previews left out), and a
Library name — unique among its siblings only — is made unique in the scene (`Queue (2)`); with the engine's unique
lookups (§3.5) a factor or a metric can no longer take the first of two objects silently. **Atomic edits and parse
errors**: `simulean_add_team` checks the name, `members` ≥ 1 and `travel` (options and "did you
mean") before placing anything, in an undo group reverted on failure (as `simulean_load_table`); `simulean_set`
parse errors name the field, its type, the value and an example (`Bad`), unknown fields / objects / Library entries
and enum values get "did you mean". EditMode
`AgentInterfaceCommandsTests`.

The setters write the **scene** (fields, as the inspector), a study varies the **config** (catalog keys) — the one-build-path
rule: the extractor maps one to the other. Objects are found by unique GameObject name or full hierarchy path. In the
engine, `HeadlessModelFactory.Find(name)` finds a built element by display name (element ids are
`Type_Name_instanceId`; an id works too; an ambiguous name fails) — `bugfixtests` #41. EditMode `PipelineCommandsTests`
builds a model through the commands and runs it. MCP usage notes: `CLAUDE.md` (*Working through the Unity MCP*).
`InspectorCapture.Capture(go, path)` (`Editor/Pipeline/InspectorCapture.cs`) renders an object's SimuLean
inspectors in an off-screen window and saves a PNG (through `eval`): documentation screenshots, where the docked Inspector cannot
be captured. Read-only.

## 5. Optimisation in Unity (`Assets/SimuLean.Optimization/`)

The algorithms, the search space and the simulation objective are in the
engine (§3.19); this module only turns the scene's choices into that problem and shows the run.

### 5.1 Scene specifications (`Setup/ModelSpecs.cs`, namespace `SimuLean.Optimization`)

- **`ParameterSpec`** — a parameter of the scene: `target` (`Element`, `Team`, `Downtime` — any of their catalog
  parameters —, `ScheduleSequence`, `ScheduleBinary` + column), the scene object (`SElement` / `UnityResourceTeam` /
  `UnityDowntime`), the key (`Options()`: the catalog of that object) and the range of a number (each value of a
  vector). `ToModelParameter(config)` binds it to the extracted config (`ConfigParameter.Of`, by element / team name,
  downtime id); `Info()` gives the catalog entry. `SceneExtractor.ExtractElement(se)` extracts one element the way
  the scene extraction does (its extractor, user properties, routing, admission, use of resources) for the editor.
  `name` labels it in the results (empty = `owner.key`). A **text setting** (§3.19, `IsText(config)`: the catalog's text
  kind) has no range: its `texts` are the texts to try — the Optimizer's choice among them (`ToTextParameter`; none is
  an error) —, the inspector shows its current text ("text, now …") and the list; a structural key is never offered.
- **`MetricSpec`** — a statistic (`MetricCatalog`) of the model, an element, a team or a list, under a name that is a variable
  of the fitness; `ToModelMetric()`.
- **`AlgorithmSettings`** (abstract, `[SerializeReference]`) — **the Unity plug-in point**: the inspector lists
  every subclass (`TypeCache`), each creates its `IOptimizationAlgorithm` (`Create()`) and describes itself for the
  report (`Describe()`, its public fields). Built in: `GeneticAlgorithmSettings`, `RandomSearchSettings`. A new
  metaheuristic — own or a library adapter — is an `IOptimizationAlgorithm` in the engine plus a settings subclass
  here; nothing to register.

### 5.2 `Optimizer` component (`Evaluator/Optimizer.cs`)

Fields: `modelRoot`, `parameters`, `metrics`, `fitness` (expression, maximised; metric names and the numeric
parameters' names are its variables, e.g. `throughput - 60 * servers`), `algorithm`, `replications` (common random
numbers), `runTime`, `seed`, `maxEvaluations`, `maxSeconds`, `threads`, `exportFormats`, `seriesInterval`, `recordTimeline` (the best's re-run keeps every state
interval, for the Gantt charts); results `status`,
`bestFitness`, `bestValues`, `bestMetrics` — in memory only (`[NonSerialized]`, shown by the inspector, kept by the
report; 2026-10-07: they were saved with the scene, leaving stale results and dirty scenes). Result sets are named
`<Algorithm>_Report_<time>` with the algorithm's name in PascalCase (`GeneticAlgorithm_Report_…`). `Validate()` builds the problem without running it (error text or null).
`StartOptimization()` extracts the scene once on the main thread (`SceneExtractor.Extract(modelRoot).Config`), builds
the `SimulationObjective` and runs `OptimizationEngine.Run`, then builds the result set (re-running the best), in one
`Task.Run` — in Edit or Play mode; `OnDisable` (leaving Play, deleting the component) cancels (#29); worker threads only
touch cloned configs and headless models. The progress callback writes `status`; `CancelOptimization()` cancels. The
results (`Optimizer.BuildResultSet` → `ResultsOutput.Keep`: export in `exportFormats` + the results store)
are a `ResultSet`
(§3.11): the best solution as scenario `best` (its parameter values) re-run for every replication
(`SimulationObjective.Replicate`, with time series every `seriesInterval`) — metrics, statistics, summaries with
t-based intervals —, the history per iteration (`history`: iteration, evaluations, best fitness, elapsed) and the
algorithm, fitness, stop reason, evaluations and hyperparameters (`algorithm.<name>`) in the metadata, and the
evaluated candidates (`CopyCandidatesTo`). A best that failed (error or −∞) is not re-run. The former
`OptimizationReport`, `OptimizationReportExporter`, `SimpleXlsxWriter` and `RunResultsSheet` are gone.

Editor: `OptimizerEditor` + `SpecGUI` (shared parameter / metric lists with catalog pop-ups and a *Suggest* range
button, algorithm type pop-up with its settings); the SimuLean window's *Experiments* tab runs / stops it and shows its
status and best result. Test: `OptimizerSceneTests` (scene specs → objective → the
optimiser finds the exhaustive optimum; validation).

### 5.3 Removed

The previous GeneticSharp stack — `GenericOptimizationController`, `GenericParameterEvaluator`, `OptimizationConfiguration`
(`SimulationParameter`, `PerformanceMeasure`), `DataDictTransformer`, the chromosome / crossover / mutation classes,
`GenericOptimizer`, `SimulationMetrics`, the `SimuLeanPropertyRegistry`, the wrappers' `IParameterizable`
descriptors, their drawers and the GeneticSharp DLLs / package — was replaced by §3.19 and §5.1–5.2 and removed with
the Experimenter's port (§4.4). GeneticSharp drew from a global random engine (runs not reproducible); if wanted, it
returns as an `IOptimizationAlgorithm` adapter (§5.1).

---

## 6. End-to-end flow of an item

1. A source's scheduled `Execute` creates an `Item`
   (id + labels + `vElement.GenerateItem`) and calls `output.SendItem(item, this)`.
2. The `Link` invokes the downstream `Receive`; on success it records
   `OnExit`/`OnEntry` statistics.
3. A queue stores the item or passes it straight through.
4. A workstation's `Receive` binds the item to an idle `ServerProcess`, optionally
   waits for its resources (`StationResources.Acquire`), then `ScheduleWork(process, delay)`.
5. When the timer fires, `ServerProcess.Execute()` → `CompleteServerProcess` pushes
   the item downstream and calls `input.NotifyAvailable`, which walks upstream via
   `Unblock()` so blocked elements release held items.
6. Combiners/assemblers accumulate several items into one composite
   (`Item.AddItem`) before forwarding.
7. The item terminates at a `Sink` (counted).
8. Throughout, each element records IDLE/RECEIVING/PROCESSING/BLOCKED/
   WAITING_FOR_RESOURCE intervals via `SetState` → `ElementStateTracker`, and every
   link crossing updates `ElementStatsCollector`.

---

## 7. External dependencies

- **Unity 6.6 (6000.6.4f1)** (see `ProjectSettings/ProjectVersion.txt`; moved from 2021.3.45f2 on
  2026-10-06, no older editor is supported). UGUI 2.6 (includes TextMeshPro), Timeline 6.6, AI Navigation 2.0,
  Test Framework 1.8 (EditMode tests), the Terrain modules, the Unity Pipeline package `com.unity.pipeline` (editor MCP
  bridge; the `unity mcp` server of `.mcp.json`, §4.11) (`Packages/manifest.json`): every package comes from Unity's
  registry, so opening the project needs no git.
- **Universal Render Pipeline 17.6** (`com.unity.render-pipelines.universal`): one pipeline asset for every quality
  level, `Assets/SimuLean.Unity/Rendering/SimuLean_URP.asset` (+ its renderer, the global settings and the default
  volume profile), shadows to 150 m in 4 cascades, MSAA 4×, HDR. Every material is URP Lit (the library's, made by
  `SimuLeanModelPostprocessor`; the run-time ones of signs and conveyors); the two own shaders (`SimuLean/FloorPlanUnderlay`,
  `SimuLean/Sign Text`) are unlit with no light mode, so they draw in either pipeline.
- **Input System 1.20** (`com.unity.inputsystem`), the project's active input handling: the run-time tools read the
  keyboard and mouse only through **`SimInput`** (`Core/SimInput.cs`, the one place, as `UnityCompat` is for object
  lookup; their keys stay `KeyCode` fields, mapped there).
- No optimisation library: the algorithms are in the engine (§3.19); GeneticSharp was removed (§5.3).
- No numerics library: `Stochastics/Probability/Probability.cs` — ln Γ / Γ (Lanczos), regularized
  incomplete gamma (series / Lentz continued fraction) and beta (Lentz), erf / erfc, the normal quantile (Acklam +
  Halley), bracketed Newton / Halley inverses, and the χ², t, F, Poisson and binomial CDFs / quantiles on them —
  replaced ALGLIB (GPL, 30 MB). Checked against ALGLIB 4.07's values on a grid, tails included
  (`StandaloneTests/probabilitytests`, 3621 reference values kept in `reference.tsv`, + round trips + published
  tail values); where they disagree ALGLIB was the less accurate (erfc / Φ beyond |x| ≈ 4, flushed to 0 from
  erfc(10); inverse incomplete beta for large a). ALGLIB's χ² / F inverses invert the *complemented* CDF, so
  the former `DistributionUtils.ChiSquareInvCDF` / `FInvCDF` returned the upper-tail point: `Probability.ChiSquareQuantile`
  / `FQuantile` are CDF quantiles
  (samplers draw the same distribution; a given u maps to the other side).
- **ExcelDataReader 3.7.0** (+ `System.*` deps), committed in `Assets/Plugins/SimuLeanDependencies/` (listed in
  `Assets/packages.config`; `RestoreNuGetPackages.ps1` / `nuget.exe` restore them), used by the Unity layer only:
  `SimuLean.Unity.TableFiles` reads `.xlsx` / `.xls` sheets; `.csv` (RFC 4180 quoting) and `.data`
  (whitespace) are read by the engine's `SimuLean.Data.TableReader`. Elements receive a table, never a file
  (`ScheduleSource(name, clock, arrivalsTable)`). Excel *export* uses the engine's own `XlsxWriter` (§3.8), no
  library (no EPPlus).
- **`SimuLean.Unity.UnityCompat`** (`SimuLean.Unity/Core/UnityCompat.cs`): every
  object lookup / identity call in the code base goes through it — `FindAny<T>()`
  (→ `FindAnyObjectByType`), `FindAll<T>()` / `FindAll<T>(includeInactive)` (→ `FindObjectsByType`;
  **results are unsorted**, unlike the former `FindObjectsOfType`), `FindAllInHierarchyOrder<T>()` (sorted by scene,
  hierarchy and component position: what model building uses, §3.5) and `ObjectId(obj)`
  (→ `GetEntityId()`, as text). Since 2026-10-06 it targets 6.6 only (the 2021.3 / 6.3 `#if`
  guards were removed); it stays the one place to adapt when Unity changes these APIs again.
- **SQLite** — a `sqlite3` native library when present (e.g. in `Assets/Plugins`), else Windows 10/11's
  `winsqlite3.dll` (§3.8).
- **Blender 5.x** *(authoring only, optional)* — regenerates the 3D model library
  from `Tools/Blender/` scripts (§4.8). Not needed to open or run the Unity project.
- **Python 3.10+ with ezdxf / numpy / pillow, and LibreDWG `dwg2dxf`** *(authoring only,
  optional, not bundled)* — the floor-plan texture pipeline `Tools/FloorPlan/` (§4.7.2).
- No sample data workbooks are kept in the repository: application data lives in its own repositories, model tables in the
  project database (`SimuLeanData/simulean.db`, git-ignored).

---

## 8. Known caveats

Open limitations to keep in mind (resolve or document as the code evolves).

1. **Assemblies.** The engine (`SimuLean.Net`) and the choice rules (`SimuLean.UserLogic`, §3.25) are their own assemblies (the compiler keeps Unity out of both); the rule contract is enforced by the read-only context and a reflection scan of fields, not by a scan of the calls a rule makes. The Unity layer, optimisation and experiments still share `Assembly-CSharp`.
2. **Downtime coverage.** `ItemsQueue` / `StorageSystem` (no timed work) are not paused by a stop: they refuse items
   in and out. A source chooses what its arrivals do (`arrivalsOnStop`, `SourceStopPolicy`): **Hold** (the default:
   its output is blocked — a time-between-arrivals source holds one item and stops its clock, a schedule source keeps its
   timetable and piles the rows up), **Pause** (the pending arrival and a schedule's remaining timetable shift by the
   stop; the first stop cancels the arrival event, the last resume re-schedules the time left) or **Lose** (arrivals
   during the stop are dropped and counted, `lost_arrivals`).
   The end of a stop calls `Unblock` until it sends nothing, so every waiting item the receivers take leaves (queues,
   Multi-Servers, Combiners); an element's `Unblock` must return true only when an item left **or a job waiting
   for its exit took the freed place** (`ReserveExit`: `GeneralLink.Offer` stops offering the place then). A stop freezes and shifts a resource's current
   leg / step (`TaskExecutor.OnStopped` / `OnResumed`), so its views pause with it. Repair crews: a downtime's *Repair by* (§3.12).
   An AfterCurrent stop is delayed by the work in hand, not cut (§3.12): a long job at a timetable row shifts the row's
   return (a fixed-hour calendar written as rows slides by each day's overrun — use a Shift, whose return stays the
   calendar's); rows that wait for the same job overlap unless the timetable serialises them.
3. **Queue pass-through order.** When `ItemsQueue` / `LengthLimitedQueue` forward an item in the same call that
   receives it, the link records the exit before the entry: the content dips to −1 for zero time (time-weighted
   averages are unaffected). The stay time is right: `ElementStatsCollector` pairs the exit with the entry that
   follows at the same instant and records a zero stay (fixed 2026-10-07).
4. **Compound-exit statistics are opt-in.** `Stacker` and `ErectionDock` (naval) set
   `ElementStatsCollector.CountSubItemsOnExit`; components
   drawn from `CombinerInput` / `SkuStockPort` ports into a composite never leave those ports through a link, so the
   ports' output / content statistics keep counting them. Downstream of a batching element the compound is one item.
5. **`Stacker` scope.** One pick-and-place cycle at a time; positions are not transport resources (no travel time
   between them other than the cycle time); `ByLabelIndex` falls back to first-not-full; with
   `AcceptOversizeOnEmpty = false` an item higher than every limit waits forever (BLOCKED).
6. **Station setups (§3.2 *Setups*).** Setups are modelled by the Multi-Server, the Combiner, the single-job stations, the Assembly Station (per
   job), the Stacker (per cycle) and the Erection Dock (per front), with a setup matrix and grouping by setup in a queue;
   `SequencingStation`, `Separator` / `LineSideUnpacker` (their own change time), `PacedLine` and `Delay` have none. Keep
   For Process is ignored on the Assembly Station and the Stacker (their resources are asked per task / cycle). Grouping
   works behind an ordinary link only (a list's puller can order by a query on the label instead).
7. **Config files on disk.** A `SimulationConfig` is written and read by `ConfigJson` (an exact round trip, the
   runner's model file; the *Model Extractor* tool's export too). In format 2 the settings of elements, downtimes, teams and reactions are typed and connections
   have typed members (§3.5); a network's nodes / tracks and a matrix's entries are typed records.
8. **Parameterisation (§3.19).** Choosing another distribution starts it with its default parameters (they are not
   tuned in the same search); assembly task durations (inside the task records) and a team's travel mode are
   structural. The catalog test covers each element type with its default settings, the reference models and the
   sample scenes: a key written only in a rarely used mode is caught when it appears there.
9. **Modular building / crane kit (§4.7.1)** is lightly exercised: `ModularKit.Combine` needs readable meshes,
   combined meshes are serialized into the scene, `ModularKit.Load` works only in the editor. The hall crane member
   (`HallCrane`) relies on it: it is built when its team fits it, so it builds in the editor only.
10. **Item visual hook is a static event (§4.2).** `Item.VisualAttached` is static: every item of every
    model raises it when it gets a visual. Headless runs (studies, the optimiser's parallel workers) assign no visuals,
    so they never raise it, and `ItemViews` ignores anything that is not a `GameObject`; a future engine view that
    assigns visuals off the main thread would need a per-model hook instead.
11. **Naval library (§3.20, §4.2, §4.8) — open items**: the block workspace and
    the 4.6 m paint booth stand in for the station models (a paint hall at block scale: resize the booth or a dedicated
    model later); the decor kits and
    sample scene and the naval results — item trace, KPIs, block Gantt — are not built; the `sample_hull` table is
    written into the project database when it is missing (by the preset and when a scene that reads it is opened).
13. **Resources under stops — design limitations.**
    - *Downtimes use resources.* A downtime's *Repair by* requests a team's members for each
      repair (§3.12 *Repair crews*); the failure-token loop with reactions is no longer needed.
    - *Pre-emption only on request.* Requests with a preemption mode take members from work of lower
      priority, and a member's breakdown interrupts work whose use has a mode (§3.17, *Preemption*). Without a mode
      (the default) work keeps its resources when a stop begins: with an **Immediate** stop the paused machine keeps its
      operator, so a repair needing that operator waits forever — a resource deadlock (since 2026-10-13 reported as one,
      §3.5); give the repair a higher priority and a mode, or model such a failure **AfterCurrent**. Since 2026-11-04 every
      station's job, a resource's own time, HELD after the process, joint moves while gathering and open-ended empty moves
      are preemptable (§3.17). **Still not preemptable, by design:** a loaded carrier (it delivers first; loading and
      unloading are never cut) and a joint move once loaded (a carrier's stop pauses the group instead); a resource that
      travels with its item (`AtElement`: it cannot be handed over away from the station); a sequencing station's operator
      between two timed steps (waiting for an order or a piece: no work to hold); a stacker's
      operator granted while its cycle cannot start yet (no position free); a milk-run train (a tour is one standing task — its cargo is picked
      and dropped along the route, a hand-over mid-tour has no meaning; its requests carry no mode); a walking item
      (`WalkLink`: no resource); and Abort never takes a station's job or a carrier's move (they have an item).
    - *A station takes its part before it has its operators — by default.* Receiving the item comes first, then
      `WAITING_FOR_RESOURCE` with the item inside; the buffer in front of a station sharing a scarce operator therefore
      stays empty and the wait is in the station's state share (`state:WAITING_FOR_RESOURCE`). Since 2026-11-05 the
      Process block's *Before The Part* (`processBy.acquireBeforeEntry`, §3.17) makes the Multi-Server secure its
      resources first, the part waiting upstream; it is off by default. Only the Multi-Server (the
      Combiner gathered its components and the Picking Station its stock between the grant and the work).
    - *Hold-and-wait with requirements taken in order.* `AcquireOrder.InOrder` keeps the members granted
      while the next requirement is waited for; two uses taking the same teams in opposite orders can deadlock. It is
      reported (pending parts in the dispatchers' lists: the Idle deadlock check, *Stop When No Progress For*,
      `simulean_diagnose`), not avoided; `AllAtOnce` (the default) never holds and waits. Transport uses take one team.
    - *A periodic signal or reaction hides a stall from the `Idle` check.* Its ticks are foreground
      events (a reaction can release items), so a model stalled beside one never becomes idle; the end condition
      `StopWhenNoProgressFor` ends such a run as `NoProgress` (§3.5). Calendar-time MTBF failures and timetable /
      periodic stop starts are background events: the check runs beside them (it was noted here as hidden
      too — wrongly, `bugfixtests` #89).
14. **Max-wait timers — where they do not apply (§3.17).** Transport uses and the milk run (a move's request
    belongs to its link, with its promised room and joint moves — an item waiting for a carrier is covered only by its
    queue's KeepWaiting limit); the `LengthLimitedQueue` (no exit from the middle of a lane); the Agenda (the wait is the
    booking); list pushes (lists have no capacity) and Reroute / GiveUp of a list pull (it holds nothing). Reroute of a
    job's item only on the Multi-Server. A GiveUp job asks again at once: with a constant limit and nobody else asking it
    repeats every max wait (counted each time) until its resources come.

---

## 9. Where to look (quick index)

| Concern | Start here |
|---|---|
| Clock / event loop | `Assets/SimuLean.Net/SimClock/SimClock.cs`, `IEvent.cs`, `DoubleBinaryHeap.cs` |
| Element base & contract | `Assets/SimuLean.Net/SimElements/Element.cs` |
| Item token | `Assets/SimuLean.Net/SimElements/Item.cs` |
| Links & stats side-effects | `Assets/SimuLean.Net/SimLink/ILink.cs`, `GeneralLink.cs`, `InputFanIn.cs`, `ListLink.cs` |
| Servers / workstations | `MultiServer.cs`, `SingleJobStation.cs`, `Combiner.cs`, `PickingStation.cs`, `ServerProcess.cs`, `Delay.cs` |
| Distributions / delays / probability | `Assets/SimuLean.Net/Stochastics/` |
| Statistics | `Assets/SimuLean.Net/Statistics/` |
| Headless run / serialization | `Assets/SimuLean.Net/Headless/`, `Serialization/` |
| Unity binding, one build path | `Assets/SimuLean.Unity/Core/SElement.cs`, `UnitySimClock.cs`, `SceneModelBuilder.cs`, `Serialization/SceneExtractor.cs` |
| Batch experiments | `Assets/SimuLean.Experimentation/Controller/Experimenter.cs` |
| Optimisation, parameters of the model | `Assets/SimuLean.Net/Optimization/` (algorithms, objective, `ParameterSchema`, catalog), `Assets/SimuLean.Optimization/Evaluator/Optimizer.cs`, `Setup/ModelSpecs.cs` |
| Any core type or method | [`docs/api/index.html`](api/index.html) — generated API reference (search with `/`) |
| Calendar / dates | `Assets/SimuLean.Net/SimClock/SimCalendar.cs` |
| Downtime (stops, MTBF/MTTR, shifts, timetables) | `SimElements/ElementDowntime.cs`, `Assets/SimuLean.Net/Downtime/`, `Serialization/DowntimeBuilder.cs`, `SimuLean.Unity/Downtime/UnityDowntime.cs`, guide `Assets/Docs/Downtime_And_Calendar.md` |
| Order release on demand | `SimElements/ReleaseSource.cs` |
| Length-limited lanes / conveyor buffers | `SimElements/LengthLimitedQueue.cs`, `Serialization/LengthLimitedQueueBuilder.cs`, `SimuLean.Unity/Elements/UnityLengthLimitedQueue.cs` |
| KPI / log tables in SQLite, JSON exchange | `Assets/SimuLean.Net/Data/ResultTable.cs`, `ProjectDatabase.ExecuteBatch`, `Data/MiniJson.cs` |
| Optional runtime tools (paced clock, camera, HUD, inspector, dashboard) | `Core/UnitySimClock.cs`, `Core/Camera/`, `Core/Hud/`, `SimuLean.Unity/Inspector/`, `SimuLean.Unity/Charts/UnityDashboard.cs`, guide `Assets/Docs/Optional_Runtime_Tools.md` |
| SimuLean window tabs, Library presets | `SimuLean.Unity/Editor/Window/SimuLeanWindow.*.cs`, `Editor/LibraryPresets.cs`, `Editor/Naval/NavalPresets.cs`, §4.6.1 |
| Inspector standard, Connect tool, hierarchy | `Editor/Inspectors/Framework/` (`SElementInspector`, `SimuLeanGUI`, `ElementChecks`), `Editor/Connections/`, `Core/SceneConnections.cs`, `Editor/Hierarchy/ModelHierarchy.cs` |
| Logic viewer (decision sites) | `Assets/SimuLean.Net/Logic/ModelLogic.cs`, `SimuLean.Unity/Editor/Window/ModelLogicWindow.cs`, §3.24 |
| MCP commands (agents) | `SimuLean.Unity/Editor/Pipeline/SimuLeanCommands.cs`, `SimuLeanTestRunner`, §4.11 |
| Results, charts, results store | `Assets/SimuLean.Net/Results/` (`Charts/ChartQuery.cs`, `ChartRenderer.cs`, `SvgCanvas.cs`), `SimuLean.Unity/Core/ResultsOutput.cs`, `Charts/ChartDefinition.cs` |
| Studies, distribution fitting | `Assets/SimuLean.Net/Optimization/Study.cs`, `SimuLean.Unity/Core/SceneStudy.cs`, `Assets/SimuLean.Net/Stochastics/Fitting/` |
| Resources / transport / storage | `Assets/SimuLean.Net/Resources/`, `Transport/`, `Storage/`, `SimuLean.Unity/Elements/UnityResourceTeam.cs`, `SimuLean.Unity/Transport/` |
| Data tables, project database | `Assets/SimuLean.Net/Data/` (`ProjectDatabase.cs`, `TableReader.cs`), `SimuLean.Unity/Data/UnityDataTable.cs`, `Serialization/TableFiles.cs` |
| Naval library | `Assets/SimuLean.Net/Naval/`, `SimuLean.Unity/Naval/`, `Editor/Naval/` |
| Station models / state lamps | `SimuLean.Unity/Visuals/StationModel.cs`, `Visuals/MachineStateView.cs` |
| Floor-plan underlay | `SimuLean.Unity/Visuals/FloorPlanUnderlay.cs`, `Tools/FloorPlan/README.md` |
| 3D models / asset pipeline | `Tools/Blender/README.md`, `Assets/SimuLean.Unity/Editor/Tools/SimuLeanModelPostprocessor.cs` |
| Workshop walls, columns, overhead crane | `Assets/SimuLean.Unity/Visuals/Building/`, `Editor/ModularKitEditors.cs` |
| Stacker / palletiser (stacks → compound items) | `SimElements/Stacker.cs`, `Serialization/StackerBuilder.cs`, `SimuLean.Unity/Elements/UnityStacker.cs`, `StandaloneTests/stackertests` |
| Visual detail (Auto / Detailed / Fast), gantry animation | `SimuLean.Unity/Visuals/SimVisualDetail.cs`, `Visuals/GantryStackerView.cs`, model `Art/Models/GantryStacker/` |
| Roller conveyor geometry | `SimuLean.Unity/Visuals/RollerConveyorMesh.cs`, `Visuals/UnityRollerConveyor.cs` |
| Lists (push / pull, priority queues, back-orders) | `Assets/SimuLean.Net/Collections/SimList.cs`, `SimLink/ListLink.cs`, `SimuLean.Unity/Lists/UnitySimList.cs`, §3.16 |
| Element library / tool registry (`[SimuLeanElement]`, `[SimuLeanTool]`) | `SimuLean.Unity/Core/SimuLeanLibraryAttributes.cs`, `Editor/Window/SimuLeanWindow*.cs`, §4.6.1 |
| Regression net (reference models, baselines, runner, EditMode tests) | `Assets/SimuLean.Net/Testing/`, `StandaloneTests/referencemodels/`, `StandaloneTests/run-all.ps1`, `SimuLean.Unity/Editor/Tests/` |
| "What changed recently?" | `git log` |

Feature guides: `Assets/Docs/` (headless runs and experiments, downtime and calendar, expressions, optional runtime
tools).
