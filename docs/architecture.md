# SimuLean — Software Architecture

> **Status:** Living document. This file is the single source of truth for the
> software architecture of the SimuLean / UnitySimuLean solution.
> **Any change to the project architecture must be reflected here** (see
> [CLAUDE.md](../CLAUDE.md)). When you add/remove/rename an element type, change
> the event or link mechanics, alter the headless/serialization pipeline, or add
> a subsystem, update the relevant section and the diagrams below.

Last verified against the codebase: **2026-10-21** (§4.6.1 library packs).

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
| `SimuLean.Net` (the engine) | `Assets/SimuLean.Net` | `SimuLean.Net.asmdef`: **`noEngineReferences: true`** (no UnityEngine / UnityEditor), no assembly references, `overrideReferences` with the precompiled DLLs it uses (System.Memory, System.Buffers, System.Runtime.CompilerServices.Unsafe, System.Numerics.Vectors; no Excel reader since R-10e, no numerics library since D1), auto-referenced |
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
| Core engine | `Assets/SimuLean.Net/` | `SimuLean`, `SimuLean.Collections`, `.Data`, `.Downtime`, `.Expressions`, `.Headless`, `.Naval`, `.Optimization`, `.Resources`, `.Results(.Charts)`, `.Serialization`, `.Statistics`, `.Stochastics(.Streams/.Distributions/.Fitting)`, `.Storage`, `.Testing`, `.Transport` |
| Unity integration | `Assets/SimuLean.Unity/` (`Core/`, `Elements/`, `Config/`, `Serialization/`, `Resources/`, `Transport/`, `Lists/`, `Data/`, `Downtime/`, `Naval/`, `Charts/`, `Inspector/`, `Visuals/`, `Art/`, `Prefabs/`, `Editor/`) | `SimuLean.Unity`, `SimuLean.Unity.Naval`, `SimuLean.Unity.Editor(.Naval/.Tests)`; the extractors are in `SimuLean.Serialization` |
| Optimisation (Unity front-end) | `Assets/SimuLean.Optimization/` (`Setup/`, `Evaluator/`, `Editor/`) | `SimuLean.Optimization`, `.Evaluator`, `.Editor` (the algorithms: engine `SimuLean.Optimization`, §3.19) |
| Experimentation | `Assets/SimuLean.Experimentation/` (`Controller/`, `Core/`, `Editor/`) | `SimuLean.Experimentation.Controller` / `.Core` / `.Editor` |

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
    interval (see §8.1). Returns false when no events remain.
  - `Context`, `GetSimulationTime()`, `GetDateTime()` (calendar date of now), `PendingEventCount`, `IsStopped` /
    `StopTime`. There is no `Reset`: a clock serves one model and one run — a new run builds its model on a new
    `SimClock` (`UnitySimClock.NewModelClock`).
  - `RunStatus` / `RunEndReason` (`SimClock/RunStatus.cs`: `Horizon`, `StopAtCount`, `Empty`, `Deadlock`, `Stalled`,
    `Finished`; end / stall / deadlock / empty / finish times, `Waiting`) is the state `SimContext.Run` that the run
    monitor fills (§3.5). `IBackgroundEvent` / `BackgroundActionEvent` are in `SimClock/ActionEvent.cs`.
  - **Run control.** `Stop()` ends the run (an end condition): `AdvanceClock` returns at
    once and never moves the clock again, so statistics end at `StopTime`. **Background events** (`IBackgroundEvent`,
    `BackgroundActionEvent`: shift / calendar ticks, the next MTBF failure, a timetable stop's start, the warm-up reset)
    never move items; the heap counts the others (`ForegroundEventCount`) and the clock raises **`Idle`** when none is
    left (after an event, and when `AdvanceClock` starts) — the run monitor (§3.5) tells an empty model from a stall or
    a deadlock. A stop's end, paused work and arrivals are foreground events.
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
  **`ModelParameters`** dictionary read by output-routing strategies (Task 0-E moved
  this here from the former process-wide static `GeneralLink.GlobalModelParameters`).
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
  `SimClock.Schedule(action, delay)` schedules a one-off (R-10f); timed work of an element still goes through
  `Element.ScheduleWork` so downtime can pause it.
- **`DoubleMinBinaryHeap`** (`SimClock/DoubleBinaryHeap.cs`) — min-heap keyed by
  `(time, seq)`: the monotone sequence number gives **deterministic FIFO
  ordering of same-time events** (required for reproducible runs).

Two self-scheduling idioms:

1. **An element schedules itself.** e.g. `ScheduleSource.Start()` →
   `ScheduleNext()` → `simClock.ScheduleEvent(this, dt)`; its own `Execute()`
   emits items then re-schedules the next arrival. `InterArrivalSource` follows the
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
- a `IVElement vElement` visual adapter (**defaults to a `HeadlessVElement`** when
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
| **Sources** | `InterArrivalSource`, `ScheduleSource` (file `Source.cs`), `ReleaseSource` | Create items: by inter-arrival times, data-table driven, or **released on demand by control logic** (`ReleaseSource.Enqueue` — FIFO, pushed when downstream accepts, retried on `Unblock`; a finite downstream buffer acts as a kanban). `ScheduleSource` creates each row's items with **that row's** name and labels (it used to read the next row) |
| **Servers / workstations** | `MultiServer`; `Combiner` (n assemblies in parallel); single-job station `SingleJobStation` → `PickingStation` (R-6); `AssemblyStation`; `Delay` (no capacity — every item waits its own delay at once, drawn on entry, as `ScheduleWork`; finished items that cannot leave wait in finishing order, BLOCKED when nothing else runs) | Process/transform items over sampled delays |
| **Queues / buffers** | `ItemsQueue`, `GateQueue` (releases on demand: `Release(n)`), `CombinerInput`, `LengthLimitedQueue` (§3.13) | Hold items; gate/validate admission; `LengthLimitedQueue` = accumulation lane(s) limited by the total item length |
| **Stacking / palletising** (§3.14) | `Stacker` | Builds stacks of items of any type at N positions (rule: first not full / round-robin / label index / key; capacity by count and optionally height) and releases each full stack as **one compound item** (`subItems` = the stacked items) |
| **Sinks** | `Sink` | Consume items and count them; `KeepItems` keeps them (`Kept`, at rest: `IKeepsItems`) — a finished-goods area, a launched ship |
| **Healthcare** (I1) | `SimuLean.Healthcare.Agenda` | Appointment book: books each patient on entry into the first free slot of its weekly sessions (`WeeklyShiftPattern` cut into `SlotLength` slots, the model's holidays + its own) at least `LeadTime` ahead, holds it (at home) and releases it at its appointment (an absolute-time event; late if the output is busy). The first `ReservedPerSession` slots of a session are for priority patients (`PriorityLabel` ≠ 0; `Overflow` Ordinary / WaitReserved); a `NoShow` probability expression (the agenda's stream) stamps `no_show`; labels `booked_at`, `appt_time`, `lead_time`. Slots generated lazily a week at a time (look-ahead 3660 days from now; the statistics — slots, booking horizon — only read, they generate nothing: review2 #4, #7; a cancelled extra slot is removed and its session's overbooking given back, #6); statistics of its own (slots offered / booked / reserved unused, no-shows, cancellations, overbooked, lead times, booking horizon — `RunResults`). Later parts (same day): **templates of later periods** (`AddTemplate(from, sessions, slot)`: config `templates` records `date | sessions | slot minutes` or a data table `templateTable` with `From`, `Sessions`, `SlotMinutes`; each day uses the last template from that date), **cancellations** (`Cancel` probability expression drawn at the booking: at a random time before the appointment the slot is freed and the patient books again, label `rescheduled`; the lead time counts from the first booking), **overbooking** (`Overflow = Overbook`: the next session's free reserved slot, else an extra slot squeezed at its last slot's time, at most `OverbookPerSession`) |
| **Naval** (N2) | `SimuLean.Naval.ErectionDock` | Slipway / building dock: erects ship blocks in sequence or by precedence on `Capacity` fronts with process resources, keeps the hull, launches the complete ship (§3.20) |
| **Resources** (§3.17) | `Resource`, `TaskExecutor` (namespace `SimuLean.Resources`) | Operators (and next vehicles) that do tasks for elements — their process or the transport of their items — handed out by a `ResourceDispatcher`; elements, but not part of the item flow |
| **Transport network** | `TransportNetwork` (not an element; namespace `SimuLean.Transport`) | The model's network: elements stand at its nodes (`Element.Location`), vehicles are resources travelling on it (§3.9, §3.17) |
| **Storage** (§3.10) | `StorageSystem` (namespace `SimuLean.Storage`; scene `UnityStorageSystem` / `UnityStorageRack`) | Racks / floor lanes with location policies; moves by its transport team |
| **Kitting / assembly stations** | `PickingStation` (+child ports `SkuStockPort`, `ReturnsPort` — both in `PickingStation.cs`), `AssemblyStation` (+`SkuStockPort`) | Picking: per-container pick list (SKU→qty) resolved via a `CompositePickListSource` chain (a model data table `pickTable` → `TablePickListSource`; then container labels → static); picks into the container as sub-items, then batch-returns non-picked stock through the returns port. Assembly: precedence network of `AssemblyTaskSpec` tasks (materials by SKU from a shared buffer, per-task duration sampler), up to N concurrent workers, dispatch = first-ready-first, ties by priority then declaration order |

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
  duplicate check without the receiving element made conditions on `element.x` refuse every item, B1).
  `CanAccept` = not input-blocked ∧ `CheckAvailability` ∧ `ValidateInput`. `UpdateStrategy(item)` remains for
  the Combiner pull modes ("auto-update" takes the value from the main item).
- `ElementState` (interned `readonly struct` state token: predefined core states
  IDLE/RECEIVING/PROCESSING/BLOCKED/TRAVELLING/WAITING_FOR_RESOURCE, SETUP and the
  downtime states STOPPED/BREAKDOWN/SCHEDULED_DOWN/OFF_SHIFT, plus
  subsystem-specific states via `ElementState.Of(name)`), `ElementStateTracker`
  (time-in-state accounting, keyed by `ElementState`), `StateLogEntry` — the single
  state vocabulary — also used by the per-slot trackers (the former `State` enum was removed
  2026-10-06).
- `HeadlessVElement` — the no-op visual adapter (optionally logging through `SimLog`); the duplicate
  `NullVElement` was removed 2026-10-07.

**Interface-implementation matrix (workstation-type elements):**
- `MultiServer : Element, IWorkStation, IResourceUser` (N slots)
- `SingleJobStation : Element, IWorkStation, IResourceUser` (abstract, R-6 2026-10-07) — one job at a time that gathers
  what it needs: `Receive` → `RECEIVING` + `OnJobArrived` (subclass gathers, then `StartJob`) → resources
  (`WAITING_FOR_RESOURCE`) → `PROCESSING` (`OnProcessingStarted`) → sent, or `BLOCKED` until `Unblock` → slot freed,
  resources released (`OnJobLeaving` / `OnJobLeft`). Owns the slot (`ActiveProcess`), `Resources`, the stop shift of
  the load time, `GetUtilization()` (time share PROCESSING) and `GetMeanServiceTime()`.
- `Combiner : Element, IWorkStation, IResourceUser` *(was a `SingleJobStation`)* — **`Capacity`**
  assemblies in parallel (config `capacity`, default 1), each an **`Assembly`**: its job slot (`ServerProcess`, index =
  the assembly), its main item, its components per input (`PartsOf`), its requirements (updated by its own main item
  with `updateRequirements`) and its **own matching** — a copy of the pull mode (`InputStrategy.Copy`), updated by its
  main item (`SingleLabel` with no value = "the component of my main item's key"). A **`CombinerInput`** is now a port:
  it admits an item when some assembly is gathering (RECEIVING, or WAITING_FOR_RESOURCE: the components of its next job),
  has room on that input and admits it (`PortStrategy`, checked by the links), and gives it to the first such
  assembly in slot order; the components wait in their assembly. After each component the port asks upstream again
  while some assembly still has room on it, not only when a set is complete (fix 2026-10-13, benchmark finding C1: a
  FIFO queue offers its waiting items only when asked, so a requirement ≥ 2 fed by a queue took one component and
  stalled silently; `bugfixtests` #77; no baseline changed). The cycle per assembly is the single-job one
  (RECEIVING → resources → PROCESSING → sent or BLOCKED, finished assemblies leave in completion order); the
  element's state is the assembly's with one, the busiest (PROCESSING > WAITING_FOR_RESOURCE > BLOCKED > RECEIVING >
  IDLE) with several; `GetUtilization` = PROCESSING share, or the mean of the assemblies (as a Multi-Server); with
  several, each assembly is a `slot` in the results. One assembly is exactly the former combiner (every baseline
  identical). `IArrivalListener` was removed.
  - `PickingStation : SingleJobStation` — gathers the pick list from its `SkuStockPort`; returns the rest on leaving.
- `AssemblyStation : Element, IResourceUser` (one event per task, up to `workerCount` tasks at once: a precedence
  network, not one process per job — kept apart from `SingleJobStation`).
- Self-scheduling elements (`: … , IEvent`): `InterArrivalSource`, `ScheduleSource`.

### 3.3 The `Item` token and the property model (`SimEntity`, `PropertyBag`)

**`SimEntity`** (`SimElements/PropertyBag.cs`) is the base of every simulation object —
`Item`, `Element` (so every element and resource) — with a `Name` (element name / item type) and user
**`Properties`** (a `PropertyBag`, created on first use: an entity without properties costs nothing; `HasProperties`
checks without creating it). Universal attributes stay typed members of the classes, never property names.

**`PropertyBag`**: one dictionary per kind, each allocated on its first write — **double** (`SetDouble` /
`TryGetDouble` / `GetDouble`: the hot path, no boxing or allocation, verified by `propertytests`), **string**,
**entity reference** (`SetRef` / `GetRef`, runtime only: serialized as the referent's name and resolved when the
model is rebuilt) and **array**. A name has one kind at a time (setting another kind replaces it).
`SetParsed` (number when the text parses, dot or comma; else string; empty ignored), `KindOf`, `Has`, `Remove`, `Names`,
`Count`, `Get` (boxed), `GetText` (numbers as round-trip `"R"` text, references by name, arrays comma-separated),
`CopyFrom`, and `View` — a live, read-only `IReadOnlyDictionary<string, object>` (no copy).

`Item` (`SimElements/Item.cs`) carries `creationTime`, string `type`, numeric `myId`, `priority`,
an opaque `vItem` (the visual handle — a GameObject in Unity, `null` headless; setting one raises the static
`Item.VisualAttached`, which the Unity item views use, N5) and a `subItems` list (assemblies / batches). Its **labels are its properties**: `SetLabelValue(label, string)` (parsed),
`SetLabelValue(label, double)`, `SetLabelText` (never parsed), `GetLabelValue`, `GetLabelText`, `GetLabelOrDefault`,
`HasLabel`, `CopyLabelsFrom` keep their names; `Labels` returns the bag's view (no copy; it used to build a new
dictionary per call). Labels are how per-item data (deadlines, inspection flags, processing-time drivers) travels
with the item and feeds label-expression delays, admission strategies and lists. Resources' labels (skills,
attributes) are their properties too (`Resource.SetLabelValue` / `GetLabelText` / `LabelNames`), and **elements have
properties** (`Element.Properties`). `GetBatchRequirement` (unused) was removed with the old dictionaries.

**Serialization.** **`PropertyRecords`** encodes a bag as flat records `kind|name|value` — `d` (number,
round-trip), `s` (text, may contain '|'), `ref` (the referenced entity's name), `arr-d` / `arr-s` — in the element
parameter `properties`. `HeadlessModelFactory` applies them after creating every element, then resolves references
**by element name in a second pass**; an unknown name leaves the reference **null with a `SimLog` warning** (never a
silent zero) — the one caveat of the property model. Bad records (a non-number for `d`, an unknown kind) fail loudly.
Unity: **`SElementLabels`** is the element's user properties in the scene — `LabelPair` = a number, or a text, or a
reference to another `SElement`; `Records()` exports them and the factory fills the engine element's properties
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
round robin, starting after the one served last — until one sends (§8.1).

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
  `ShortestQueueStrategy`, `MostAvailableCapacityStrategy`, `ParameterizedRoutingStrategy`, `PriorityRoutingStrategy`
  (priority > 0 → first available, else shortest queue), `ExpressionRoutingStrategy` (§3.18; replaced
  `LabelRoutingStrategy`), **`ProbabilityRoutingStrategy`** (type `Probability`, weights
  `outputStrategy_weights` = `w1|w2|…` in connection order — a catalog `NumberList`, so studies can vary the shares;
  drawn once per item from the source's stream `name/routing` and kept until the item leaves, `IRoutingMemory.Forget`
  called by every link once the item has left: a busy choice is waited for, not redrawn; it is an `IRuleRouting`, so a
  walk or a transport team draws over every destination too — review2 #1 / #2) and `DelegateOutputStrategy` (a C# lambda
  `(outputs, item, source) → index`, for code-built models). All test `CanAccept(item, source)`, so a stopped
  destination is skipped.
- **Labels set on hand-over** (`SimElements/LabelAssignments.cs`): any element may have
  `EntryLabels` / `ExitLabels` (config `entryLabels` / `exitLabels`, `ElementConfigKeys`; Unity `SElement.entryLabels`
  / `exitLabels`, inspector *Labels on Entry / Exit*): `name = expression` records over the item and the element
  (`reps = reps + 1`, `t_test = now()`, `valid = rand() < 0.9`). Every link hands items over through
  **`ItemHandOver.Receive(source, target, item)`** (GeneralLink, ListLink, ResourceTransportLink): the source's exit
  labels and the target's entry labels are set **before** the target's `Receive` (its process time and admission
  downstream read them) and undone if it refuses the item; a carried item gets its exit labels when loaded
  (`ItemHandOver.Exit`). An element's own routing is decided before its exit labels: route on entry labels. Statistics
  stay in the links (`OnEntry` / `OnExit`).
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
  constructor defaults to `HeadlessVElement` when no view is passed, so the same
  `Element` classes run identically with or without Unity.
- **Serialization DTOs** (`Serialization/SimulationConfig.cs`):
  `SimulationConfig` → `Elements` + `Connections` + `MaxSimulationTime`, plus `UseFixedSeed` / `Seed`, `WarmupTime`,
  the calendar (`StartDateTime`, `TimeUnitSeconds`), the end condition (`StopAt`, `StopAtCount`, `StopWhenEmpty`),
  `Downtimes`, `Reactions` (§3.21), `Network`, `TravelMatrices`, `WalkGrids`, `ResourceTeams`, `Lists`, `Tables` + `DatabasePath` (§3.8),
  and `Clone()` (a reflection deep copy, `ConfigCloner.DeepClone`: new fields are copied automatically).
  `ElementConfig` = `Id`, `Type` (string tag), `Name`, and a `Dictionary<string,object> Parameters` bag.
  `ConnectionConfig` = `SourceId`, `TargetId`, `ConnectionType` (`General` (= `Simple`) / `StockInput` /
  `ReturnsOutput` / `PushToList` / `PullFromList`; any other type fails the build) and a
  `Parameters` bag (`port`, `query` for list connections).
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
  followed by a handler `Wire` pass.
- **Handlers are split by assembly.** Each element type has an engine
  **`IElementHandler`** (`Serialization/Handlers/Unity<Type>Handler.cs`, naval `Naval/ErectionDockHandler.cs`; base
  `ElementHandlerBase`: `TypeName`, `Build`, `Wire`, `Schema` — the `ParameterSchema` of its config keys, §3.19) and a
  Unity **`IElementExtractor`** (`SimuLean.Unity/Serialization/Extractors/Unity<Type>Extractor.cs`, base
  `ElementExtractorBase`: `ExtractParameters`, `ExtractConnections`) with the same `TypeName` (= the wrapper class
  name), registered in `ElementHandlerRegistry.CreateDefault()` and `ElementExtractorRegistry.CreateDefault()` (in
  `IElementExtractor.cs`; aliases via `RegisterAlias`, e.g. `UnityStorageRack`); the shared extraction helpers are
  `ExtractorHelpers` (Unity layer). A new element type needs both. Registered types: Source, Queue,
  LengthLimitedQueue, Stacker, GateQueue, MultiServer, Delay, Agenda, Combiner, Sink, StorageSystem (+ alias), PickingStation,
  AssemblyStation, ErectionDock.
- **Build order (`BuildModel`).** Calendar → data tables (`DataResolver.Build` → `SimContext.Data`, §3.8) → locations
  (`Network`, travel matrices, walk grids = `ModelSpace`, traffic control) → pass 1: elements (+ `location` /
  `workNodes`), user properties (references by name), lists (`SimList` + `ListLink`), resource teams and each
  element's process / transport uses (`ResourceBuild`), Combiner `inputOrder` → pass 2: connections, one link per
  source (`GeneralLink`, or `ResourceTransportLink` with a transport team); `ReturnsOutput` / `PushToList` /
  `PullFromList` wired on their own → pass 3: handler `Wire` → pass 4: downtime generators (`DowntimeBuilder`). The
  factory exposes `Teams`, `Executors`, `Lists` / `ListLinks`, `DowntimeGenerators` / `DowntimesById`, `Network`,
  `Space`, `Monitor`, `Jobs`, `GetElement(id)` and `Find(name)` (display name; ambiguous names fail); its `viewOf`
  constructor argument gives each element its view (scene runs, `SceneModelBuilder`).
- **Kitting / assembly serialization (Task 0-D).** Handlers
  `UnityPickingStationHandler` / `UnityAssemblyStationHandler` are registered in
  `CreateDefault()`. They add two connection types — `"StockInput"` (feed → the station's
  `SkuStockPort`, a destination of the feed's single output link: one feed into several stations' stock, or into a
  stock and its next elements, keeps every connection) and `"ReturnsOutput"` (picking `ReturnsPort` → destination) —
  while job/container input and picked/assembled output use the standard
  `nextElements` general connections. Assembly precedence networks serialize as a
  `List<string>` of `"id|preds|sku:qty,…|priority|durationSpec"` records, each
  duration rebuilt through `ElementBuildHelpers.BuildRandomProcess`. See §8.1.
- **Network & storage serialization.**
  `UnityStorageSystemHandler` is registered in `CreateDefault()`; its build logic is engine-pure in
  `Serialization/TransportStorageBuilders.cs` (`TransportStorageConfigCodec` + `TransportStorageBuilders`).
  - **Flat-list encoding:** nodes `"id|x|y|z"`, tracks `"from|to|length|bidir|type"`, lanes
    `"laneId|x|y|z|cap|access|sku|nodeId"` — `List<string>`, JSON-friendly and deep-cloned by `SimulationConfig.Clone()`.
  - **One network per model:** `SimulationConfig.Network` (`NetworkConfig`: nodes, tracks), built once by
    `HeadlessModelFactory` (`Network`) before the elements are placed. An element's node is its parameter
    `location` (`HeadlessModelFactory.LocationKey`) → `Element.Location`; a location that is neither a network node nor in
    a travel matrix fails the build (likewise each `workNodes` entry, `HeadlessModelFactory.WorkNodesKey`). Its
    optional `workNodes` → `Element.WorkLocations`, where its walking operators work (§3.17).
  - Vehicles are members of resource teams with `Travel = "Network"` (§3.17); a storage's moves use its own
    Transport use (`ResourceUseKeys`). The former `"TransportSystem"` / `"TransportDestination"` connections,
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
  `RunMonitor`. `SamplerSpec` (`Type~p1~p2`, incl.
  `ExponentialMean~m`; *2026-10-06*: a null/empty spec, unknown type or missing /
  unparseable parameter throws `FormatException` — only StudentT's clamp and Beta's bounds are
  optional — and `ElementBuildHelpers` throws for an unknown random-process type instead of
  returning `Constant(5)`) is the shared compact sampler codec (moved out of the
  assembly-station handler; `RandomProcessConfig.ToSamplerSpec()` encodes a Unity
  distribution); `ElementBuildHelpers` moved to its own engine-pure file
  (`Serialization/ElementBuildHelpers.cs`), so the Unity-coupled registry file now
  only holds the handler registry (the extraction helpers moved to the Unity layer). `SceneExtractor`
  exports the clock calendar and every `UnityDowntime` (database timetables are
  resolved into the config, so headless runs need no file I/O); `SimulationConfig.Clone()` copies both.
- **Length-limited lane.** `UnityLengthLimitedQueueExtractor` / `UnityLengthLimitedQueueHandler`
  (registered in both registries); the codec and build are the engine-pure `LengthLimitedQueueBuilder` (`laneLengths`
  as a `List<string>` of numbers, a decimal comma accepted, or a JSON list; a length that is not a positive number
  fails the build plus `gap`, `defaultItemLength`,
  `lengthLabel`, `maxItems` (≤ 0 = unlimited), `acceptOversize` and the standard
  strategy keys; reads are tolerant of int/float/double/string values).
- **Stacker.** `UnityStackerExtractor` / `UnityStackerHandler` (registered in both registries); codec and
  build are the engine-pure `StackerBuilder` (`positions`, `capacity`,
  optional `positionCapacities` as a `List<string>` of invariant integers, `maxHeight`,
  `heightLabel`, `defaultItemHeight`, `acceptOversize`, `selectionRule` (enum name),
  `positionLabel`, `keyLabel`, `releasePartialWhenBlocked`, `cycleTime` as a compact
  `SamplerSpec` string — empty = instantaneous — rebuilt with its own per-model random stream,
  `stackType`, `copyLabels`, `pickLocation` (the node where carriers pick up full stacks) and `positionLocations` (a
  node per position, `List<string>`), plus the standard strategy keys; tolerant reads).
- **Reproducibility:** `SimulationConfig` carries `UseFixedSeed` + `Seed` (experiment
  replications pass their own seed instead, §4.4); when
  set, the headless run's `SimClock(seed)` derives every distribution's random
  stream deterministically (handlers pass the clock into
  `ElementBuildHelpers.BuildRandomProcess(config, prefix, clock)`, which keys the sampler's stream
  by the element name (`config.Name`) and the prefix — `Src/interArrival`, `Lathe/delay`; sampler
  specs decoded with `SamplerSpec.Decode(spec, clock, owner, purpose)`: stacker `cycleTime`,
  assembly `task:<id>`, downtime `ttf` / `ttr` / `firstFailure` of the generator `<config id>:<target>`).
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
`RunMonitor.WaitsFor`, the stall report's description, at any time), each team's pending requests and members, the
lists. `Study.Diagnose(time, seed, all)` runs the study's one scenario to a time, or until nothing can happen
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
the count between them — and `StopWhenEmpty` (stop when nothing is left to do). On the clock's `Idle` (no foreground
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
for, from the connections). **What each one waits for (fix 2026-10-09):** a Combiner gathering (its main
item here, not `BLOCKED`) waits for the sources of the component inputs some assembly still lacks — the element wired to
the input (`Combiner.InputOrigin(k)`) or the list the port pulls from (`PullFromList` with `port`) — not for the elements
after it; holding components only, for its main input (element or list); an element pushing to a list waits for that
list's pullers (`list yardList (pulled by bayA, bayB)`); the others for their downstream elements. Tests: bugfixtests #48
and EditMode `DeadlockAttributionTests` (`ReferenceModels.YardListDeadlock`: the yard as a list of capacity 1, two
Combiners with SingleLabel). A sink downstream, a stopped element or outputs to a list only are obstacles (never
surplus). **An element under a stop does not excuse a stall (fix 2026-10-13, benchmark finding C4):** every stop's end
— a generator's repair or next shift, a reaction's *stop for a time*, a clock signal — is a pending foreground event,
and at `Idle` none is left, so a stop still active then never ends (it used to suppress the report: a repair waiting
for the operator its Immediate-stopped machine holds was reported neither as a stall nor as a deadlock); the stopped
element is listed with its stop state (`M2: BREAKDOWN, 1 item(s) → …`; `bugfixtests` #79; no baseline changed).
`StopWhenEmpty` also ends the run on a
stall / deadlock. `RunMonitor.IsTerminal(e)` (no outgoing connection) gives the results their **makespan**: the last
entry into a terminal element (sinks and end-of-line buffers / stations, not sources) — so a model without sinks has
one. **`IKeepsItems`** (`KeptCount`, `LastKeptTime`: a sink with `KeepItems`, an erection dock's hull):
kept items are at rest (not counted as held / waiting), the makespan includes the last time an item came to rest (an
erection ends after the block arrives), and such an element downstream is an obstacle, not a reason for surplus (a dock
refusing blocks whose predecessor never comes is reported). Read-only: it schedules nothing. The state is `SimContext.Run` (`RunStatus`). Reference models `end_sink_count`,
`end_when_empty`, `stall_combiner`, `deadlock_dead_end`, `finish_with_surplus`, `end_at_storage`, `combiner_starved` (the 2026-10-08 fix
only added keys: `finish_time`, and the makespan of `deadlock_dead_end`'s end buffer); `bugfixtests` #38, #42, #43.

**Scriptable experiments — `Study` (`Optimization/Study.cs`).** Factors by catalog path
(`Yard.capacity`, `Srv.delay~1`, `team:T.members`, `downtime:id.key`, `reaction:Name.key` — §3.21) plus two parameters: **`TeamLabelMembersParameter`**
(`team:T.members[skill=welding]`: the members carrying a label; extra ones copy the last of them) and
**`SourceTableParameter`** (`Source.table`, or the catalog key `Source.fileName` — follow-up #29, 2026-10-08 — or
`Source.tableSource`, a `sql:` / `naval:` spec read at build time: a schedule
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
  it directly — the `PoissonStrategy` / `BinomialStrategy` / `DiscreteUniformStrategy` wrappers were removed in
  R-12) and a full catalog: Uniform, Normal, Exponential, Triangular, Gamma, Weibull, LogNormal, Beta, ChiSquare,
  StudentT, F, Poisson, Binomial, IntegerUniform, ConstantRandomProcess.
- **`Probability/`** — the static class `SimuLean.Stochastics.Probability` (namespace `SimuLean.Stochastics`): CDFs, quantiles (`…Quantile`, the inverse of the CDF),
  probability masses, and Γ, ln Γ, B, the regularized incomplete gamma / beta and their inverses, erf / erfc —
  SimuLean's own implementation (D1, §7; it replaced ALGLIB and the former `DistributionUtils`). The distribution fitting added
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
  element's raw samples on and returns its stay times or the gaps between its arrivals / departures (F2: "times of a
  run"); the samples come from `ElementStatsCollector.RecordSamples()` (`StaySamples`, `EntryTimes`, `ExitTimes`;
  null — off — unless asked, so a normal run keeps only the summaries). Tests: `fittingtests` (scipy reference fits,
  §3.15).
- **Seeding model — one random stream per element.** Every stochastic
  source draws from its own `DefaultRandomEngine` created by
  `SimContext.CreateRandomStream(owner, purpose)` / `AssignRandomStream(sampler, owner, purpose)`.
  A distribution built without one gets an `UnassignedStreamEngine`: it draws from
  `RandomEngineManager.GlobalEngine` and warns once per distribution type (`SimLog`) that the draws are not
  reproducible (R-10d); no model sampler does (every suite runs without the warning).
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
  Stacker `cycleTime`; AssemblyStation `task:<id>`; an element's resource uses `processHoldFor`, `transportLoad`,
  `transportTravel`, `transportUnload`; MTBF/MTTR downtime `<config id>:<target>` with
  `ttf` / `ttr` / `firstFailure`; StorageSystem `locationPolicy`. Owner = the engine element name, which is the
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
  **Generators hold no content:** `TracksContent` (default `true`). The sources
  (`InterArrivalSource`, `ScheduleSource`, `ReleaseSource`) set it to `false` in
  their constructors: their items are created inside and leave through the link without ever
  entering, so the `-1` per exit drove content current / peak / average negative. With the flag off
  `OnEntry`/`OnExit` skip the content variable (it stays 0); input / output counts and stay times are
  unchanged. Their `Start()` calls `base.Start()` (state tracker and statistics reset; before 2026-10-05 they skipped
  it and never reset their statistics). Covered by `StandaloneTests/sourcestatstests`.
  **Sinks keep no entry times:** `TracksStay` (default `true`); a `Sink` sets it to `false` — nothing
  leaves it, so the entry time it kept for every item received only grew with the run (review2 #19).
- **`StatVariable`** (abstract) heads a **family of three statistic types** —
  pick by what the number means:
  - **`StatLevelVariable`** — event counters (input/output totals); no mean (a
    count divided by its updates means nothing — R-10c).
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
  strings, so report / CSV / GA consumers are unchanged. *(review2 #15 / #19)* `KeepLog` (`Element.KeepStateLog`,
  default on — the timeline and the inspector read the log) is turned off by `HeadlessModelFactory.KeepStateLogs = false`,
  which `SimulationObjective` (studies, the optimiser) sets for runs that record no timeline; `Version` counts transitions
  (a resource caches its `Utilization` per time and version); `ElementState` keeps its name's hash.

### 3.8 Project data — SQLite (`Data/`, namespace `SimuLean.Data`)

FlexSim-style "global tables" backed by a real per-project SQLite database:

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
- **Model data tables.** Named tables of the model, as FlexSim's global
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
  reference) = a table name; `UnitySourceHandler` takes its snapshot from `SimContext.Data` (an unknown table names the
  source and the tables there are). Precedence: an `arrivalsTable` given in the config (a study's `Owner.table` /
  `Owner.fileName`, which also drop `dataTable` / `tableSource`) > the naval `tableSource` > `dataTable` > a
  `fileName` key (hand-written configs; read at build; with a `dataTable` too the build fails, naming both — one would
  be ignored). A study varies the arrivals as `data:<table>.file` / `.sql`
  (D5). The source's own table fields (`fileName` / `sheetName` / `readFileAtRun`) and their migrator `DataMigrator`
  were removed on 2026-10-11 once every branch's scenes were migrated: a Schedule source reads a data
  table, a `tableSource`, or rows set from code (`SetArrivalsTable`, tests). Tests: `datatests`, EditMode
  `ScheduleFileTests` (read at run / stale copy / `data:` study through a file data table).
- **Downtime timetable from a data table.** A `Timetable` downtime config may carry
  `dataTable` (`DowntimeBuilder.DataTableKey`) with its mapping — `targetColumn` / `startColumn` / `endColumn` /
  `durationColumn` / `codeColumn` / `reasonColumn`, `durationScale`, `codeRules` (`code|state|mode|factor|ignore`),
  `targetKeys` (`key|elementId`: the target column's value per target) — declared in `ParameterSchema.Downtime`
  (Structural). `DowntimeBuilder` builds the stops from the run's snapshot (`SimContext.Data`, through
  `DowntimeTable.Build`), added to the typed rows' intervals; skipped rows are warned, an unknown table names the
  downtime and the table. So a study's `data:<table>` variants reach the stops too. Unity: `UnityDowntime.dataTable`
  with the mapping fields (the former own query `useDatabase` / `databasePath` / `sql` was removed 2026-10-11).
- **Pick lists from a data table.** Picking Station config key `pickTable` (Structural): the
  handler puts a **`TablePickListSource`** (`Data/`) first in the chain — the table indexed **once** at build (per
  `keyColumn` value, numeric keys written as labels are: `7.0` = label 7; or one shared list without a key column;
  lines of a SKU add up; each container gets its own copy), never queried during the run (the former
  `SqlitePickListSource`, which queried the database per container, and the station's `useSqliteTable` /
  `databasePath` / `tableName` were removed 2026-10-11). Unity: `UnityPickingStation.pickTable` with
  `skuColumn` / `qtyColumn` / `keyColumn` / `containerKeyLabel`. The Fit tab (§4.6.1) also fits a column of a scene data table (D3d).
- **`TableSources`** (`Data/TableSources.cs`) — tables a Schedule source reads **when its
  model is built** instead of a copy in the scene, so a scene run, a headless run and a Study read the same current data:
  `sql:<SELECT …>` (the project database) or `naval:<part>[;interval=h][;start=h][;lead=h]:<table, SELECT … or file>` (a
  part generated from a block breakdown, §3.20). The spec is the source's catalog key **`tableSource`** (`File` kind: a
  Study varies it like a file — ship variants as scenarios), with **`tableDatabase`** the database path (the Unity layer
  resolves the default `<project>/SimuLeanData/simulean.db`); `UnitySourceHandler` reads it before the arrivals table, a
  table that cannot be read fails the build naming the source.
- **`IPickListSource`** (core, `SimElements/PickListSources.cs`) — the chainable
  pick-list abstraction: `TablePickListSource` (`pickTable`) → `LabelPickListSource` (`pick_<SKU>` labels,
  `useContainerLabels`) → `StaticPickListSource` (`staticPickList`), combined by `CompositePickListSource` (first
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


S5 (the `UnityStorageSystem` wrapper and the serialization handlers, see §3.5
and §4.2) landed 2026-07-17 in the same increment as transport P4:

- **The stock list** (A7; replaced the separate `EntityList` query engine) — a private `SimList`
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
  otherwise. Delivery goes through the
  output Link so statistics fire normally: a retrieved item waits at the outbound point, still in the storage, until
  the output takes it (`Unblock` — in order into a plain link, every item offered to a carrier or a list —, or
  `SendHeldItem` when a carrier loads it); the callback runs when it leaves (it used to
  leave anyway, without its exit, and a transport team could not take it). Overlapping retrievals never fetch the same
  item (items being fetched are excluded; a relocated blocker is in no lane while on its way), a lane being emptied
  takes no put-away, and the places planned for relocated blockers are **reserved** (`StorageLane.Reserved`, counted
  in `FreeCapacity`). Counters: `TotalRelocations`, `TotalRetrievals`. v1 simplifications documented
  in the class: sequential (not concurrent) moves; destinations planned up-front, not revised mid-flight; a
  retrieval whose target sits behind another retrieval's target in the same lane is refused.

Verified in-repo by `resourcetests` (moves by the transport team, a retrieved item waiting at the outbound point (#5),
overlapping retrievals / reserved destinations / a lane being emptied (#6), an AS/RS moved by an axes crane),
`bugfixtests` #35 (stock queries) and the reference model `end_at_storage`. The original 2026-07 suites (78 + 27
assertions) were kept outside the repository.

### 3.11 Run results (`Results/`, namespace `SimuLean.Results`)

**One snapshot of a run, one vocabulary.** `RunResults.Capture(HeadlessModelFactory, modelName)` reads a built and run
model once, at its clock's time: a **`ResultObject`** per element (`ResultScope.Element`), server slot (`Slot`,
group = the element: a MultiServer's slots, a Combiner's parallel assemblies (`Capacity` > 1) and an Erection Dock's
fronts, `<id>/slot<i>` / `<id>/front<i>`), team member (`Resource`, group = its team; id = its name, or `team/member` when two teams have a
member of that name —, the build warns; one place, `RunResults.ResourceIds`, used by the results, the
timeline and the time series), team (`Team`), list (`List`), downtime generator
(`Downtime`, group = its target — `<id>@<target>` when one downtime has several targets), reaction (`Reaction`, `fired` / `not_met`, §3.21) and the space's traffic control (`Traffic`), each with an ordered set of statistics,
plus a **`RunInfo`** (model, seed, end time, warm-up, observed time). The names are **`ResultMetrics`**: element `input`,
`output`, `content` (at the end), `content_avg` (time-weighted), `content_max`, `stay_avg` / `std` / `min` / `max` /
`count`, `utilization` (MultiServer, Combiner, Erection Dock: their own `GetUtilization()`, the mean over slots /
assemblies / fronts; otherwise the PROCESSING share), `stops`; resource
`utilization`, `tasks`; team `members`, `utilization` (mean of members), `served`, `tasks`, `request_wait_avg` / `std`,
`requests_waiting_avg`; list `pushed`, `pulled`, `content`, `content_avg`, `content_max`, `wait_avg` / `std`,
`pending_pulls_avg`; downtime `stops`, `downtime_total`; traffic `waits`, `wait_total`, `deadlocks_resolved`; and by
prefix `state:NAME` (share of time, states entered only) and `field:F:push_avg` / `pull_avg` / `pull_max` (list fields).
**Item statistics:** an element's `tallies` (`name = expression` records over the entering
item and the element, config `tallies` / `tallySplit`, Unity *Item Statistics* / *Split By*) are recorded by
`ElementStatsCollector.OnEntry` — the link layer, like the other item-flow statistics — into
`Statistics/ItemTallies` (values kept for the percentiles, reset with the collector and at the warm-up) and reported as
`tally:NAME:mean / std / min / max / p50 / p90 / count`, per value of the split label as `tally:NAME[value]:…`
(`ResultMetrics.Tally`; only tallies with values; a study asks for one as `Exit.tally:time_in_system:p90` — the
`Study` path's scope prefix is only a colon before the first dot). Typical: `time_in_system = age` at a sink, `to_decision =
t_decision - created` (milestones set by entry labels, §3.4), `within_target = age <= 720` (its mean is the share).
**Utilisation = the share of the observed time spent actually working** (PROCESSING); time under an effective stop
(an Immediate stop at once, an AfterCurrent one once the work in progress is done) is accounted in the stop's state,
never PROCESSING — for the element and, since 2026-10-13, for each slot too (benchmark finding C2: a Multi-Server /
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
**Across replications (E2):** `StatSummary.Of(values, confidence)` — n, mean, sample std (Welford), min, max and
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
`finish_time` (nothing left to do), `stopped` (an end condition met); `RunInfo.EndReason` (Horizon,
StopAtCount — was `SinkCount` until 2026-10-08, follow-up #30 —, Empty, Deadlock, Stalled, Finished), `Deadlocked` and `Waiting` (who waited for what); the `runs` table gains `end_reason` and `deadlocked`.
`MetricScope.Model` makes them metrics (`model.makespan`; a time never reached reads NaN).

**Time series (E3):** `SeriesRecorder(model, interval).RunTo(end)` advances the clock interval by interval
(`AdvanceClock` runs every event up to each boundary, so the run is identical to one call — checked) and keeps, per
interval, the change of the cumulative statistics: elements `input` / `output` (items in the interval), `content` (at
the end), `content_avg` (from the content integral), `state:X` (share of the interval); resources `utilization`,
`state:X`; lists `pushed`, `pulled`, `content`, `content_avg`. With a warm-up the series start at its end.
`TimeSeries` (scope, name, metric, interval ends, values) go to `RunResults.Series` (`AddTo`; `ToSeriesTable`).
`SimulationObjective.SeriesInterval` records them for the runs handed to an inspector (`Run`, `Replicate`) only —
plain evaluations never do.

**Result sets and exports (E3):** a **`ResultSet`** gathers what an experiment, an optimisation or a run produced:
`ResultScenario`s (name, parameter values as text), `ResultRun`s (scenario, replication, seed, elapsed time, the named
metrics, the run's `RunResults`), metadata (name, source, model, date, confidence, settings) and extra tables (an
optimiser's `history`). Its tables are **the stable format** (`ResultSet.FormatVersion` = `simulean-results/1`), all
long and keyed by run / scenario: `metadata(key, value)`, `scenarios(scenario, parameter, value)`, `runs(run,
scenario, replication, seed, end_time, warmup, elapsed_s, end_reason, deadlocked)`, `metrics(run, scenario, replication, metric, value)`,
`metric_summary(scenario, metric, n, mean, std, min, max, ci_low, ci_high)`, `results(run, scenario, replication,
scope, name, type, group, metric, value)`, `summary(scenario, scope, name, type, group, metric, n, mean, std, min, max,
ci_low, ci_high)`, `series(run, scenario, replication, scope, name, metric, time, value)`, `timeline(run, scenario, replication, scope,
name, state, start, end)`, `jobs(run, scenario, replication, item, element, start, end)` (G3c–d, below),
`candidates(candidate, iteration, fitness, error, <parameters…>, <metrics…>)` (G1a, below) (empty tables left out;
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
`rs_meta` (`schema_version` = `ResultStore.SchemaVersion`, 2 — G3c–d: `rs_timeline` / `rs_jobs`; a version-1
database gets the new tables when opened; a database with a newer version is refused), the catalog
`rs_sets` (integer `id`, name, kind = the set's source, model, created, confidence), then per set `rs_set_meta`,
`rs_metric_names`, `rs_scenarios` / `rs_parameters`, `rs_runs` (scenario, replication, seed, elapsed, the run info, or
none for a failed run) / `rs_run_metrics`, `rs_objects` (scope, id, name, type, group per run) / `rs_stats`,
`rs_series` (one row per point), `rs_extra` (an extra table as `MiniJson`) and `rs_candidates` /
`rs_candidate_values`, with indexes by set and scenario / object name / metric. `Save(set, kind)` → id (a failed save
deletes its rows), `List(model)` (newest first, with scenario / run / candidate counts), `Load(id)` (the same
`ResultSet`, so the same tables), `Rename`, `Delete` (every row of the set), and queries across sets:
`Statistic(scope, object, metric, sets)` and `Metric(name, sets)` → `ResultTable`s with `set_id, set, scenario,
replication, …`. NaN / infinity are stored as NULL and read back as NaN. **Candidates:** `OptimizationRun` records
every simulated candidate (`OptimizationResult.Candidates`: iteration, the variables' values as text, its
`Evaluation`; cache hits are not repeated) and `CopyCandidatesTo(set)` gives them to `ResultSet.Candidates`
(`ResultCandidate`: iteration, parameters, fitness, metric means, error) — the `candidates` table; the Optimizer's
result set carries them. `ResultSet.StoreId` is the set's id once saved or loaded (0 = not stored; not a table value).
**`ResultSet.Compare(sets)`** (G1b) makes one set of several to compare them: every scenario becomes `set · scenario`
(parameters and runs with their results kept), the metric names joined, source `comparison`. **Saved on request (was by default, G1b):**
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
inspector, the dashboards (G2b) and the exported PNG draw alike, Unity-free and testable:
- **`ChartQuery`** (serialisable: what to plot) — `Kind` (`Bar` mean ± CI, `Box` quartiles, `Histogram`, `Scatter` X
  vs Y per run, `Line` recorded series, `States` share of time stacked), `Y` / `X` = `ChartValue` (`Source` Metric /
  Statistic / Parameter, `Name`, `Scope`, `Objects` = names or `*` patterns separated by `;`), `GroupBy` (Scenario /
  Object / Run: the categories; the other dimension, when several, makes the series), `Scenarios` filter, `Bins`,
  `Confidence`, `ValueLabels`.
- **`ChartBuilder.Build(query, tables | set)`** reads only the long tables (`metrics`, `results`, `scenarios`, `runs`,
  `series`), so a live set, a stored set and a `.json` export give the same chart; a name not found gives an empty
  chart whose `Message` lists what exists. Quartiles as numpy's linear default; histogram bins by Sturges' rule.
- **`ChartData`** — categories, `ChartSeries` (name, colour or palette, per-category `Values` / `Low` / `High`,
  `Boxes`, `Points`, a `Note`), axis titles, `Unit`, sparse column labels (`XStart` / `XCenter` / `XEnd`), `Normalise`.
  Two kinds come only from data built in code (live views): `Stacked` (columns, to 100 % when normalised) and `Area`
  (first series filled, the others as lines).
- **`ChartRenderer`** lays a chart out once on an **`IChartCanvas`** (rectangles, rounded-top bars, lines, polylines,
  circles, text with a size / weight / alignment, turned 90° when needed; `Measure`, `Ascent`) — the modern style of G3a
: semibold title left, subtitle (`ChartData.Subtitle`, filled by the builder: "mean ± 95 % confidence
  interval…"), legend as round swatches under them, light grid without a frame, zero line, bars with rounded tops and
  thin whiskers, sizes in points × `scale` (pixels per point: the editor's, the HUD's; `AutoScale` = 1 at 640 × 400),
  Y ticks fitted to the plot height. `Render(data, w, h, theme, scale)` → **`ChartImage`** through **`RasterCanvas`**
  (2× supersampled, then `Downsample`; RGBA top-down; `ToPng` with its own zlib / CRC writer). `RenderPlot` draws only
  the plot box (grid at the quarters, top = `NiceMax`) for views that write labels in their own font. Themes `Light`,
  `Dark`, `Panel`. **SVG (G3b):** `ToSvg(data, w, h, theme, scale)` draws the same layout on **`SvgCanvas`** — vector
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
- **Timeline and Gantt (G3c).** Opt-in: `RunResults.Capture(model, name, timeline: true)` /
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
  **Job Gantt (G3d):** with the timeline on, a **`JobRecorder`** (`JobRecorder.Attach(model)` before the model starts;
  `HeadlessModelFactory.Jobs`) listens to the link layer's `ItemEntered` / `ItemExited` (item flow has one source) and
  keeps every stay of an item in an element (`JobEntry`: item, element, start, end; items numbered by first arrival —
  their ids are not unique —, compared by reference; a pass-through, reported exit-then-entry at one instant, is a
  0-length stay and dropped, as the statistics do; sinks are left out). `RunResults.Jobs` → the `jobs` table (*Jobs*
  sheet; `rs_jobs` in the store, schema 2). **`ChartKind.JobGantt`**: a row per item (by first arrival), a colour per
  element in flow order (mean position along the routes), one run, time window; the Results tab's *Timeline* offers
  *jobs* beside the state kinds. Tests: one stay per entry (= the input statistic), stays never overlap.
- **`ChartPalette`** — the series colours (a Tableau-10-like palette readable on light and dark) and **the** state
  colours / order (`InspectorPalette` reads them).
Unity: **`ChartDefinition`** (ScriptableObject, *Create ▸ SimuLean ▸ Chart* or the Results tab's *New chart*, saved
under `Assets/SimuLeanCharts/`) holds a `ChartQuery`, its source (`ShownResults` or `StoredSets` by id — several are
compared through `ResultSet.Compare`) and the export size; `ChartTextures.ToTexture` turns an image into a texture
(rows flipped). Its inspector (`ChartDefinitionEditor`) shows only the fields the kind uses, a ▾ beside every name
listing what the results have, and a preview. Tests: `resultstests` (every kind, messages, the `.json` path, the
stacked / area plots, PNG re-read with CRC / zlib), EditMode `ChartDefinitionTests`.

### 3.12 Downtime subsystem (`SimElements/ElementDowntime.cs`, `Downtime/`)

One interruption mechanism serves failures, planned stops and shift calendars
(backlog 5-A / 5-C) and applies to **any element**.

- **Element side (the mechanism).** `Element.Stop(StopRequest)` returns a
  `StopToken`; `Resume(token)` ends it. A `StopRequest` carries the accounted
  `State` (default STOPPED), a `StopMode` — `Immediate` (pre-empt: pending work is
  paused now and resumed with its remaining time) or `AfterCurrent` (finish work in
  progress, refuse new items; the stop state becomes visible when no work is left —
  the pending `ScheduleWork` jobs, or what an element adds through `HasWorkInProgress` (a resource's current task,
  also while it is held in a process or walks open-ended) —, typical for shift ends) — plus `BlockInput`/`BlockOutput` (output default: blocked
  for Immediate, allowed for AfterCurrent), `Code`, `Reason`, `Source`. Stops may
  overlap (counted); the most recent effective stop's state is accounted, and the
  element's own state returns when all end. On resume the element retries its
  blocked output (`Unblock`) and wakes its input (`NotifyAvailable`) — *2026-10-06*:
  repeatedly while upstream keeps handing items over and the element has room (one
  notification used to refill a single slot). The `StopToken` **snapshots** State, Mode,
  BlockInput and BlockOutput when the stop starts (`token.Mode`, `token.BlocksInput`, …),
  so editing a reused `StopRequest` cannot unbalance the block counters. Cancelling a
  `WorkHandle` refreshes the masking like a completion (a pending AfterCurrent stop shows).
- **Pausable work.** Elements schedule processing/setup/travel delays with
  `ScheduleWork(evt, dt)` instead of `SimClock.ScheduleEvent`; the returned
  `WorkHandle` is tracked by the element, cancelled on pause (lazy heap deletion) and
  rescheduled with the remaining time on resume. Stations with slots (`MultiServer`, `Combiner`, `ErectionDock`)
  pass the effective stop state to their slots (`OnStopShown` → `ServerProcess.ShowStop`), so per-slot statistics and
  the utilisation leave out stopped time like the element's (benchmark C2, `bugfixtests` #78). Retrofitted: `MultiServer` (+ load
  time shifted by the part of the pause each job spent loaded — `PauseShiftFor`; *2026-10-06*
  also in `Combiner`, `SingleJobStation` / `PickingStation` and the naval `ErectionDock`, views only), `Combiner`,
  `SingleJobStation` (`PickingStation`), `AssemblyStation`, `Stacker` (built on it), the resources'
  `TaskExecutor` (every task step and travel leg, traffic segments included) and the `ErectionDock` (N2). Arrival events (sources, timetables)
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
  `Until > First`, else the build fails naming the downtime). `DowntimeTable` + `DowntimeTableMapping` turn table rows (project
  database or any rows; start as number or date; end or duration; per-code state,
  mode, duration factor, ignored codes) into intervals grouped by target. Typed
  `DateTime`/`DateTimeOffset` cells are converted directly (*2026-10-06*: going through
  invariant text produced MM/dd, which the dd/MM-first parser swapped).
- **Serialization:** `DowntimeConfig` + `DowntimeBuilder` (§3.5). Kind `Periodic`: `period`, `duration`, `first`
  (default 0), optional `until` (absent = no end), `allowOverlap`, `code` — numbers offered as parameters, labelled in
  `ParameterSchema.Downtime` (§3.19).
- **Unity.** `UnityDowntime` (§4.2) configures one generator per target element;
  the model builds and starts them (`DowntimesById`) and binds them to the component (`UnityDowntime.Bind`).
- Generators implement `IWarmupStatistics`: the warm-up resets their counters (`bugfixtests` #52).
- Verified in-repo by `componenttests` (shift pattern, `ShiftDowntime`), `bugfixtests` (#8–#14, #20, #26, #50, #52,
  #55, #58: typed dates, mutated requests, timetable mode / cancel, progress shift, shift slots, warm-up reset, resume
  releases, unreadable records), stop tests in `resourcetests` / `stackertests` / `listtests` and the reference models
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
  `CheckAvailability` = fits **and** the input strategy accepts.
- **Exit:** global FIFO (items are assigned to lanes in arrival order, so the oldest
  item is always at a lane front). A lone item passes straight through when the
  output accepts (like `ItemsQueue`). `Unblock` **drains while the output accepts**
  (not one item per call), so the lane empties after a stop or behind an always-free
  sink.
- **Statistics:** only through the element's links (Link-layer rule); states IDLE /
  PROCESSING (holding items) / BLOCKED (head refused). No transit time — put a
  `MultiServer` in series if conveyor travel time matters.
- **Views:** `Items`, `LaneOf`, `LengthOf`, `OffsetFromExit` (for drawing items end
  to end), `UsedLength`, `FreeLength`, `CountOnLane`. `GetFreeCapacity` is an estimate
  in units of `DefaultItemLength + Gap` (capacity-based output strategies).
- **Unity / headless:** `UnityLengthLimitedQueue` (§4.2) and
  `UnityLengthLimitedQueueHandler` → `LengthLimitedQueueBuilder` (§3.5).
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
  `stackReason`. It is sent through the **output link**; refused stacks wait in completion order
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
  `UnityStackerHandler` → `StackerBuilder` (§3.5). Engine-pure.
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
a missing baseline fails — it is written only with `--update`). Both are engine-pure, so the
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
  `resultstests`, `navaltests`, `datatests`, `signaltests`, `agentinterfacetests` (study checks, unique
  lookups, `ModelDescription`), `docsymboltests` (every back-ticked code name of `CLAUDE.md`,
  `docs/architecture.md` and `docs/project-overview.md` exists in the code — types / members, snake_case statistic and
  command names, file paths; a name quoted as history in its sentence is skipped, the other exceptions are in
  `allowlist.txt` with their reason), `architecturelint` (conventions of `CLAUDE.md` as text rules over
  the C# sources, comments and strings left out — `ScheduleEvent(` only where allow-listed (element work goes through
  `ScheduleWork`), no global / unseeded random source, no direct `Find*` / `GetEntityId` outside `UnityCompat`, no
  `RuntimeInitializeOnLoadMethod`, no Unity namespace in the engine, every Library element with a registered extractor
  and handler; existing exceptions in `allowlist.txt` with their reason, each rule self-tested); they include
  `seedtests` (per-element streams — same seed identical, an unrelated branch leaves the
  existing one unchanged, order independence, parallel = sequential, pinned stream-seed values);
- `SimuLean.Unity/Editor/Testing/SimuLeanTestRunner` runs the EditMode suite inside an **open**
  editor (MCP `eval`, or *Tools ▸ SimuLean ▸ Checks ▸ Run EditMode Tests (Safe)*). It does not start while a scene
  has unsaved changes, because the Test Runner's save dialog would block the editor and the MCP. It runs
  asynchronously and writes `Temp/simulean_test_results.json` (a full run also `…_full.json`, with the gate's
  working-tree fingerprint `treeFp`); it refreshes the asset database first, so it never tests scripts the unfocused
  editor has not compiled (see `CLAUDE.md`, Test gate). Scene tests open sample scenes through `IsolatedScene`
  (a temporary copy when the scene is open), so they never skip. Related rule: editor
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
  tests: the same baselines with the Unity-compiled engine, and the sample scenes `CombinerScene`, `AssemblyLine` and
  `ListsDemo` extracted and run headless (`SampleSceneRunsHeadless`). `SimuLeanTestRunner` also offers
  `ModifiedScenes()` and `OpenNeutral()` (opens `Assets/Scenes/Neutral.unity` before a branch switch, `CLAUDE.md`).

To make that possible the element handlers compile outside Unity: since 2026-10-06 the extraction half
lives in the Unity layer (`IElementExtractor`) and the engine assembly has no Unity reference, so
`Engine.props` compiles all of `SimuLean.Net` with no stubs. Not built yet: a scene generator
(`SimulationConfig` → Unity scene, the inverse of the extractor).

### 3.16 Lists (`Collections/SimList.cs` + one file per list type, `SimLink/ListLink.cs`)

FlexSim-style lists. **`SimList`** (on `ModelList<T>`) holds
**entries**: items advertised by the element that holds them (the item does **not** move) or any
value (orders, tokens, messages, with push-time fields). **Queries** (`ListQuery`: `Where(field, op,
value)` / custom predicate, `OrderBy(field, desc)`, list default order, then FIFO) read fields
through one access layer: item labels (numeric or text), value fields, built-ins `age`,
`pushTime`, `itemPriority`, `owner`, `type`, and named calculated fields (`DefineField`). `TryPeek` / `TryPull` / `Pull` (back-order if nothing
matches, **delivered by a zero-delay event**, cancellable through `ListPull`; a pull cancelled
after its match puts the entry back as it was and is not counted; a query's own order replaces the
list's default order).
Entries without the order field (or with a non-numeric value) rank as **0**, so ordering never fails
on a missing label. Statistics: time-weighted content and pending pulls (a cancelled pull stops
counting), waiting time, pushes / pulls; lists register in
`SimContext.Lists` and the warm-up resets them. `ListQuerySpec` / `ListCondition` are the serializable
form (text `field op value;…|orderBy[ desc]`). `ModelList.Pull` now returns a cancellable `BackOrder`.

**General list extensions for consumers:** values may implement
**`IListFieldSource`** (fields read live from the value — resolved after the list's aliases / formulas
and built-ins, before push-time fields); `Ordered(query)` returns the matching entries best first
(stable: ties stay FIFO) so a consumer can look past the best one; `Take(entry)` pulls a given entry
(counted, waiting time recorded); `ListQuery.Then(other)` combines two queries (filters of both,
orders appended).

**List fields.** `SimList.DefineFields(ListFieldSpec…)`: each field has a name, a kind —
`Label` (an item label, optionally under another name), `BuiltIn` (`age`, `pushTime`, `itemPriority`,
`owner`, `type`) or `Expression` (a formula over other fields) — and a `monitor` flag. Fields are used by
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
  behind the first (`ListLink.Advertise`, idempotent): `ItemsQueue`, `LengthLimitedQueue`,
  `ReleaseSource`; `MultiServer` / `ScheduleSource` offer each finished / created item anyway. An
  origin offering an already-advertised item again (it retries, e.g. after a stop) re-evaluates the
  pending pulls (`RefreshPulls`). Entries of a stopped origin stay listed but are not matched until
  it resumes; reserved items wait for it too.
- Pull: a puller with free capacity registers one pull (its query + its input strategy with the
  origin); on a match the item is reserved for it and, after the current event, the origin hands over
  **that very item** through **`Element.SendHeldItem(item)`** — overridden by every element that
  holds several outgoing items (`ItemsQueue`, `LengthLimitedQueue`, `MultiServer` (finished slots),
  `ScheduleSource` (backlog), `ReleaseSource`, `StorageSystem` (retrieved items at its outbound point), `ErectionDock`
  (the finished ship)); the default returns false and the link falls back to
  `NotifyUnblock` (single-item elements resend their item). The transfer is `Receive`, then
  `OnExit` / `OnEntry` — statistics stay link-driven. (`IListOrigin` was removed.) After a transfer the puller
  asks again if it still has room. Only entries the puller can take **now** match (its
  `CheckAvailability` and input strategy), and `NotifyAvailable` from the puller re-registers its
  pull so entries already listed are re-evaluated; a failed transfer re-advertises the item and the
  puller asks again after the current event. `ListLink.Start` (after the elements' `Start`) registers
  the first pulls; `Reset` forgets advertisements, reservations and pending pulls.
- **Combiner inputs** pull per port: the main input (`GetInputPort(0)`, the combiner) or a
  component input (`GetInputPort(k)`, a `CombinerInput` that accepts only while a main item is being
  assembled — the readiness filter above keeps it from matching too early).
- **Transport on list transfers.** A holder with a transport team (`transportBy`) carries the
  items the list hands to a puller — the holder's transport, as FlexSim's *Use Transport*: `ListLink.SetTransport`
  (built by the factory for a `PushToList` origin with `transportTeam`); the reserved item is not transferred at once:
  `TransferCarried` requests a carrier (`TransportMoves`, below), the item leaves the holder when loaded (`OnExit`) and
  enters the puller at the delivery (`OnEntry`, `DeliverCarried`); the reservation keeps the puller from pulling
  another item meanwhile, and items being carried are not resent. A carrier waiting at a full puller delivers on its
  `NotifyAvailable`. A Combiner's component input (the port a list delivers to) is where its Combiner is
  (`CombinerInput.Location` → the owner's; `Element.Location` is virtual — follow-up #11, 2026-10-08: a spatial crane
  failed with "both elements need a location"). Reference models `yard_kitting_list` (abstract crane), `yard_list_axes`
  (an overhead crane on axes, out-of-order blocks; `bugfixtests` #44).
- A pulling element must not also be fed by ordinary links (one input link per element); a pushing
  element's ordinary outputs are replaced by the list.

Unity: **`UnitySimList`** (`[SimuLeanTool]`, category Lists: name, **fields** — default one `prio` label,
monitored — and default order chosen from them, **`prio`, highest first** by default) built by the model factory
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
(inline routing) + `outputStrategyAsset` (shared asset, overrides it) and `SElement.inputStrategy` (admission), drawn
by the same helper next to *Send To* / *Receive From* (the shared asset used to be invisible in every custom
inspector) and exported once by `SceneExtractor` (`HasOutput` false for sinks, `HasInput` false for sources and the
combiner, whose inputs use its pull mode). The fields kept their names, so scenes needed no migration.
`UnityDowntimeEditor` shows only the chosen kind's section (and the database options only when used).
**`UnityCombiner.listInputs`** (`ListInput`: input index 0 = main / k = component k−1, list,
query) feeds inputs from lists; connections fill the remaining inputs in order
(`ResolveInputSlots`: null = list-fed; the extractor writes it as the combiner's `inputOrder`, from which the factory
builds the engine `InputOrderProvider`), the inspector shows the
feeder of every input, and connections + lists must equal requirements + 1 (`FedInputCount`).
Sources give every item the labels of **`assignLabels`** (`LabelAssignment`; default `prio = 0`, so
list ordering works out of the box; engine `InterArrivalSource` / `ScheduleSource.AssignedLabels`,
table columns of the same name override them). Labels drawn per item (a case mix) are the source's **exit labels**
(`dx = pick(0.6, 'diagnosis', 0.4, 'control')`, §3.4 *Labels set on hand-over*).
Headless: `SimulationConfig.Lists` (`ListConfig`) and `PushToList` (element → list id) /
`PullFromList` (list id → element, parameter `query`, and `port` for a combiner input) connections;
the combiner's `inputOrder` holds `""` for list-fed inputs; sources export `assignLabels` as
`label=value` records. All exported by the extractor and wired by `HeadlessModelFactory`; the
reference runner records `list:<id>` statistics. Tests: `StandaloneTests/listtests`, reference models
`list_priority_dispatch` / `list_filtered_pull` / `list_combiner`, EditMode `ListSceneTests` (incl.
combiner list input). Demo scene: `Assets/Scenes/ListsDemo.unity` (two queues → list `Work` → two
servers by priority; combiner whose component input pulls from list `Parts`), also run headless by
`ReferenceModelEditModeTests.SampleSceneRunsHeadless`.

### 3.17 Resources (`Resources/`)

Everything that does work for tasks — operators now, vehicles / AGVs next, later gantry cranes, AS/RS,
robots — shares one model (namespace `SimuLean.Resources`):

- **`Resource : Element`** — identity, **labels** (numeric / text, like item labels: skills,
  attributes), `Mode` (**Abstract** = capacity only, no position, travel takes no time; **Spatial** = on
  the network, `HomeNode` / `Location`), `Capacity` (items carried), `CurrentTask`, `IsAvailable` (no task,
  not stopped, not finishing before a stop), `IdleSince`, `BecameAvailable` event; labels via `SetLabelValue` /
  `GetLabelValue` / `GetLabelText` / `LabelNames` (stored in the element's `Properties`); `DistanceTo(location)` (0 when
  abstract). It is an element for its
  identity, states, statistics and **downtime** (`Element.Stop`: `Immediate` pauses the task's timed
  steps, `AfterCurrent` = finish the task, then the break / shift), but **not part of the item flow** (no
  links; `Receive` refuses). Statistics come from its state tracker and counters, not from links (the
  two-source statistics rule): `Utilization` = time in `ResourceStates.Busy` (PROCESSING, SETUP,
  TRAVELLING_EMPTY / LOADED, LOADING, UNLOADING, HELD, WAITING, TRAFFIC_WAIT), `TasksDone`, `UsageByRequester` (time per
  requesting element); warm-up resets them.
- **`TaskExecutor : Resource`** runs one **`TaskSequence`** at a time (`Execute`; throws when the executor is not
  available or the task already ran): steps `Process` (PROCESSING), `Setup` (SETUP), `Delay` (WAITING), `Travel` (instant when
  abstract; `TravelTime(from, to)` when spatial — TRAVELLING_EMPTY / LOADED by what it carries — a spatial
  executor without a travel model fails loudly), `Load` / `Unload` (LOADING / UNLOADING, `LoadTime` /
  `UnloadTime` defaults, item callbacks, `Carried`), `Callback` (instant) and **`Hold`** (HELD until
  `Continue()` — e.g. an operator kept by a station until its item leaves) / **`HoldUntil(done, state)`** (the same,
  skipped when `done` is already true at the step's start). Durations are functions
  evaluated when their step starts; timed steps use `ScheduleWork`. `TaskSequence` carries `Type`
  (`TaskType`: Process / Setup / Transport / Other — it decides when its resources are released),
  `Requester`, `Priority`, start / end times and `Completed`; `TaskStarted` / `TaskFinished` on the executor. Also
  `CurrentStep`, `StepStartTime` / `StepEndTime`, and `RecordMoves` / `Moves` (`MoveRecord`, calibration input; on for
  `ExternalTravel` teams). An AfterCurrent stop waits for the current task (`HasWorkInProgress`).
- **`ResourceDispatcher`** (the "ordenador") hands its members out through two ordinary `SimList`s:
  free resources (`Resources`, values = the executors, which give their fields live: `idleTime`,
  `utilization`, `tasksDone`, `capacity`, `distance`, `name`, `location`, every label) and pending
  **`ResourceRequest`**s (`Requests`; fields `priority`, `requestTime`, `count`, `station`, `location`, `taskType`,
  `skill` (the first requirement's label), plus the list's `age` and user fields). The priority rule is two list orders —
  `RequestOrder` (which request first) and `ResourceOrder` (which free resource) — **null = FIFO if
  available** (requests by arrival, resources longest idle first). A request lists
  **`ResourceRequirement`**s (count + filter query, e.g. `skill == painting`; several = "operator and
  tool"), all from that dispatcher, served **all-or-nothing**; requests that cannot be served yet do not
  block the ones behind (big requests can wait longer — no reservation yet). On assignment the request's
  `OnAssigned` gives each resource its task; a resource left without one goes back to the list. **A stopped
  station's process request waits**: while its requester `IsDown` (off shift, a failure, an
  AfterCurrent stop in effect) a `Process` request is skipped, so the job that cannot start does not keep a shared
  resource from the others; `StationResources` retries the dispatcher on the station's `Resumed`
  (`ResourceDispatcher.Retry`). Work already running when a stop begins keeps its resources (no preemption);
  transport requests are not held back. `bugfixtests` #45.
  Dispatching runs on every request and every `BecameAvailable`, re-entrant-safe (zero-time tasks loop,
  not recurse). The lists keep their statistics (request wait = waiting for resources). API: `Request(request)`,
  `Cancel(request)`, `Retry()`, `Members`, `Served`; a `ResourceRequest` has `State` (`RequestState`: Pending /
  Assigned / Cancelled), `RequestTime`, `AssignTime`, `Assigned`, `Location`.
- **Stations (`IResourceUser` = `StationResources Resources`, `StationResources`).** A station's `Resources` =
  dispatcher + requirements + **`Release`** (`ProcessRelease`: `EndOfProcess`, default; `ItemLeaves` = kept, HELD,
  while the station is BLOCKED; **`AtElement`** = the resource goes with the item, HELD, until
  it enters `ReleaseAt` (config `processReleaseAt`, an element name or id; Unity `ProcessResourceUse.releaseAt`) — an
  equipment pool: a team of devices without travel lent to patients for a night at home and returned at the device
  desk; the station is free as soon as the item leaves it; an item that never gets there keeps them) + request `Priority` (`Func<Item,double>`, an expression over the item and the station,
  `ResourceBuild.PriorityOf`). On a job the station calls `Acquire(station, item, onReady)` → `ResourceJob` (slot
  WAITING_FOR_RESOURCE until assigned; then `onReady` starts processing — once, after every resource has its task),
  `ProcessEnded(job)`, `ItemLeft(job)`, `Cancel(job)` (a job still pending). **The station keeps its
  process time** (its own stops pause it; KISS); its resources run a task of [`Travel` to the station / work point,
  spatial only +] `HoldUntil(processEnded, PROCESSING)` [+ `HoldUntil(released, HELD)` with `ItemLeaves`] and are
  continued at the release points — or, with **`HoldFor`** (a sampler, catalog key `processHoldFor`), a
  `Process` step of their own time, after which they are free while the item stays (painting then curing, a crane's
  lift then the joint welding); `Next` continues only an executor still on that job's task (a released one may hold
  another job) — so their statistics show the processing and held time per station. An operator
  breakdown does not interrupt a running process (preemption — pending issue). Every station uses it:
  `MultiServer` and `Combiner` (per job),
  `PickingStation` (per pick cycle), `AssemblyStation` (one request per task; with `ItemLeaves` only the
  last task's resources wait for the job to leave), `Stacker` (per cycle, released at the cycle's end), `ErectionDock`
  (per erection front); `PickingStation` gets it through its base `SingleJobStation`. Work points are taken
  per job (with `HoldFor` a resource may already work there for a later job).
  With `ItemLeaves` and a FIFO dispatcher each reproduced the former `OperatorPool` exactly (verified
  before the pool was removed; `operator_pool` and `resource_shared_team` kept their baseline values
  through the migration).
- **Transport by resources (abstract) — `ResourceTransportLink`.** The output link of an element whose items
  are carried by resources (operators now, vehicles in B-II) to its next elements. A move takes **load +
  travel + unload**, defined separately: `LoadTime` / `UnloadTime` per item (null = the resource's own
  `TaskExecutor.LoadTime` / `UnloadTime`, so each kind of resource can load at its own pace) and
  `TravelTime` (item, destination; null = 0 for abstract carriers, the carrier's travel model for spatial ones; set,
  it overrides the model's time and the carrier still ends at the destination); spatial carriers first travel empty to
  the origin's `Location` (origin and destination must have one). The moves themselves are `TransportMoves` (below),
  shared with the list link. An offered item gets a destination with room —
  the origin's output strategy among the destinations that can take it, **counting the items already on
  their way** to each — and a Transport request; the origin keeps it (refused send → BLOCKED) until the
  assigned resource loads it: then the origin hands it over (`SendHeldItem`, after the current event if the
  load happened inside the origin's own send) and it leaves (`OnExit`); the resource travels
  (TRAVELLING_LOADED), unloads and delivers (`Receive`, `OnEntry`) — statistics stay link-driven; if the
  destination was filled meanwhile by another element, the resource waits HELD until it has room (the
  destination's input fan-in notifies this link). Released at the delivery; after each delivery the origin
  is asked to send again. Requests needing several resources: the first carries, the others accompany it.
  **Every held item gets its own carrier**: a holder releasing in order (`ItemsQueue` FIFO,
  `LengthLimitedQueue`, `Delay`) passes over the items whose carrier is already requested
  (`Element.AwaitsCarrier`, `ResourceTransportLink.IsRequested`) and offers the next, until one is refused (no
  destination with room) — it offered only its head, so a queue as transport origin moved one carrier at a time.
  The holders that release by slot (`MultiServer`) or scan (`StorageSystem`, *any accepted*) already did.
- **Items that walk by themselves — `WalkLink`** (`SimLink/WalkLink.cs`). The item is a
  *mobile entity* (a patient — as Simio's entities on paths or AnyLogic's `MoveTo`; FlexSim's People module makes
  the person a TaskExecuter instead, which would take the pathway logic out of the item flow). Decision: the patient
  stays an item (agendas, routing, labels, link statistics, lists, studies unchanged) and moves with the
  **resources' travel machinery**: the link plans each walk with an `ITravelModel` (`NetworkTravel` on the model's
  network — the default —, `GridTravel` on a walk grid, `MatrixTravel`; `Plan(null, from, to)`) and exposes it as a
  `TravelLeg` (`ItemWalk.Leg`: route, path, start / end), the same animation contract as a resource's move. The
  destination is chosen as `ResourceTransportLink` does (output strategy among the destinations that can take it,
  counting the items on their way); the item leaves at once (`ItemHandOver.Exit`, `OnExit`), walks from the origin's
  `Location` to the destination's in the planned time (model time, `SimClock.Schedule`), and enters
  (`ItemHandOver.Receive(null, …)`, `OnEntry`); a destination without room on arrival makes it wait at its door
  (`ItemWalk.Waiting`) until its input notifies the link (the first to arrive enters first). `WaitAtDoor`
  (`walkWaitAtDoor`, Unity `walkBy.waitAtDoor`; set by an agenda that `ArriveOnTime`): items set off even to a full
  destination and wait at its door (patients going to their appointment). A routing by rule (`IRuleRouting`: the
  expression and delegate strategies, `Route` = the named output whether it has room or not) chooses over **all** the
  destinations: the item goes there (or to its door) or waits — never to another one because the named one is full
  (`ResourceTransportLink` had the same flaw — the index applied to the destinations with room — and
  chooses the same way now, baselines unchanged). Two elements without a location, or at the same one, hand
  over at once (a general link). No resource, no capacity on the way, not paused by stops; the walk is in neither
  element (a sink's `age` includes it). Config: `ResourceUseKeys.WalkSpeed` (m/s, physical: × the calendar's seconds
  per unit, as a physical team), `WalkTravel` (Network / Grid / Matrix), `WalkGrid` / `WalkMatrix`; built by
  `ResourceBuild.WalkTravel` (the team travel builder with a team config made from the keys); a transport team wins
  (warning). `HeadlessModelFactory.WalkLinks` lists them for the views; `WalkLink.Arrived` (an event) tells
  them a walk ended — for views only, nothing feeds back. Unity: `SElement.walkBy` (`ItemWalkUse`:
  speed, route, grid, matrix; extracted in `SceneExtractor.ExtractOwn`). Reference model `walking_patients`;
  `resourcetests` (route and time, room counted, waiting at the door, same place, config and units).
- **Headless configuration.** `SimulationConfig.ResourceTeams` (`ResourceTeamConfig`: id, name, members —
  `ResourceMemberConfig` name, `label=value` records, load / unload time — and the request / resource orders
  as `ListQuerySpec` text; travel: `Travel` (`""` / `Abstract`, `Network` (+ `Traffic`), `Matrix`, `Grid`, `Axes`,
  `External`, `Calibrated` — the last two below), `Units`, `Speed`, `HomeNode`, `Matrix`, `Grid`, `AxisSpeed` /
  `AxisAccel` / `AxesSequential`, `CalibrationBase` / `Fit` / `Pairs` / `MinSamples`; an unknown kind fails); an
  element's use is in its parameters (`ResourceUseKeys`): `processTeam`,
  `processRequirements` (`count#query#label`), `processRelease`, `processPriority` (an expression over the item and
  the element; a bare name = the item's property), `processHoldFor` (sampler spec) for its process, and
  `transportTeam`, `transportRequirements`, `transportPriority` (expression), `transportLoad` / `transportTravel` /
  `transportUnload` (sampler specs, own random streams) for the transport of its items. `ResourceBuild` builds them;
  `HeadlessModelFactory` builds the teams, sets each station's `Resources`, creates a
  `ResourceTransportLink` instead of the `GeneralLink` for elements with a transport team, starts the
  members after the elements and then the dispatchers (`Teams`, `Executors`). The reference runner
  records `resource:<member>` (`utilization`, `tasks`, `state:*` ratios) and `team:<id>` (`members`, `served`,
  `tasks`, `utilization`, `request_wait_avg` / `_std`, `requests_waiting_avg`).
  Reference models: `operator_pool` (a team of one shared by two servers), `resource_shared_team`,
  `resource_skill_query`, `resource_transport`, `resource_count_two`, `resource_request_priority`.
- **Unity (§4.2, §4.3).** `UnityResourceTeam` (tool *Resources ▸ Resource Team*) holds the members
  (`ResourceMemberSpec`: name, labels, load / unload time, spawn point; *Add N* in the inspector), the request /
  resource orders (order part of a `ListQuerySpec`) and the travel (`TeamTravel`: Abstract / Network / Matrix / Axes /
  Grid / UnityPhysics, with `physicsEstimate` and `calibration` for physics); its inspector shows each member's state, utilisation
  and tasks in Play mode. Every `SElement` has a **Resources** section with two blocks, each assigned or
  not: **Process** (`processBy`, `ProcessResourceUse`: team, requirements, priority expression, release, optional own
  time `holdForOwnTime` / `holdFor` → `processHoldFor` — shown only on `IProcessResourceUser` stations) and
  **Transport** (`transportBy`, `TransportResourceUse`: team, requirements, priority expression, travel time and optional load / unload overrides — the factory then
  creates a `ResourceTransportLink` instead of the `GeneralLink`). `SceneExtractor` exports the teams
  (`ResourceTeamConfig`, id = name + object id) and both uses as `ResourceUseKeys` parameters.
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
  nearest free vehicle. Teams: `ResourceTeamConfig.Travel = "Network"`, `Speed`, `HomeNode` → spatial
  members with `NetworkTravel` on the model's network. Reference models: `transport`, `resource_fleet_nearest`.
- **Units of a team.** `ResourceTeamConfig.Units` (`ResourceUnits`; a `Choice` of the team
  schema): "" / `Model` = speeds in distance per model time unit, accelerations per unit², load / unload times in
  model units (the behaviour so far); `Physical` = m/s, m/s² and seconds, converted at build by
  `ResourceBuild.InModelUnits(tc, calendar.SecondsPerUnit)` (a copy: the config and the catalog keep the values as
  entered; a travel calibration is measured in model time and is not converted). `UnityResourceTeam.units` (default
  Model, so scenes are unchanged); its inspector shows the unit of every speed and what one time unit is; the
  physics-travel view gets the converted speed (`SimSpeed`). Tests: `bugfixtests` #37, EditMode `TimeUnitsTests`.
- **Matrix travel, stops mid-way, walking operators.** `DistanceMatrix` (model data:
  `from|to|distance`, symmetric option; `SimulationConfig.TravelMatrices`) + `MatrixTravel(matrix, speed)` —
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
- **Kinematic resources.** **`AxisTravel`**: between the coordinates of two points (nodes
  of the model's network, tracks unused), each axis x / y / z moves with a trapezoidal profile (max speed,
  acceleration; 0 = instant; `AxisTime`), together (slowest axis) or one after the other (`AxesSequential`); an
  axis with speed 0 cannot move; its `Distance` is the travel time. Team `Travel = "Axes"`, `AxisSpeed` /
  `AxisAccel` (`x|y|z`). The kinds are compositions, not new elements: **AS/RS** = a `StorageSystem` whose
  Transport team is a crane on axes (lanes at points, its I/O point = its location); **robot / gantry between
  stations** = an element's Transport by an axes team; **gantry crane on a `Stacker`** = crane mode: with a
  `PickLocation` and a location per `StackPosition`, and an axes team as its process resources, the Stacker
  asks for the crane, chooses the target when it is assigned (a crane with nothing to place goes back) and
  gives it travel to the pick point → load (the pick point frees) → travel to the position → unload (the
  item is placed); `CycleTime` is not used, `StackerCycle.Duration` is the expected time (views). Headless:
  stacker parameters `pickLocation`, `positionLocations`. Unity: team travel *Axes* (axis speed / acceleration
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
  member prefab = a box placeholder). Engine support: `TaskExecutor.CurrentStep`, `StepStartTime` / `StepEndTime`
  (shifted by stops like the leg), `AxisTravel.Speed(i)` / `Accel(i)` / static `AxisPosition(d, v, a, t)` (same profile as the times).
  - `GantryRig` (`Visuals/`) = the gantry model's mechanics (Bridge along the frame Z, Trolley along X, Head up /
    down; `Fit` / `FitAround` resize the portal: posts, runways, end beams, saddles move, girders stretch;
    `SetHead` clamps to the travel limits) — **shared by `GantryStackerView`** (time-mode stacker cycles, logic
    unchanged; it stands aside in crane mode) **and `CraneResourceView`** (each horizontal axis follows its own
    `AxisTravel` profile; load / unload = head down to the item's top / its rest height, grip / release in the
    middle, head up; the item hangs from the head between grip and release).
  - **`ICraneRig`** (`Visuals/`) = what `CraneResourceView` drives: `HasHead`, `RestCup`, `Plan`,
    `CupWorld`, `PlanOf` / `HeightOf`, `SetHead(plan, cup)`, `FitAround(points, margin)`. Implemented by `GantryRig`, by
    **`TravellingGantryRig`** (`Visuals/`: a gantry whose `Bridge` carries its legs `Leg_W` / `Leg_E` on ground rails
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
    (`Art/Models/LevelLuffingCrane`, shipyard "grúa cigüeña"). Its team uses **Axes** travel, and the axes move
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
    (Generic); *Tools ▸ SimuLean ▸ Build People Animations* (`Editor/Tools/PeopleAnimationBuilder.cs`) turns them into
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
    `LieOnBed`). **Item walk view** (healthcare §6): an item the model places somewhere else (a layout, a station's slot) walks there from where it is shown — straight, `Walk` clip, at least 1.4 m/s and at most `maxWalkSeconds` (1.5 s real) — instead of jumping; moves under 0.6 m (a queue shuffling) or beyond `maxWalkDistance` (40 m), and an item shown again after being hidden, are taken at once; it then takes the pose it was given (a view: the model's times are unchanged; `walkBetweenPlaces` off restores the jump). *(the element's named positions)* A Multi-Server whose model has an `EntrySocket` (a box's door) makes its new items walk in **through it** (`PersonAnimator.WalkVia`) and those leaving walk out through it (`LeaveVia` → the `ItemWalkView` track starts by the door): no person crosses a box's walls; the walk ends at the element's location node (put it at the door). `PersonAnimator` runs after the layouts (`DefaultExecutionOrder(100)`): no walk clip shown with the person already at its place. A resource member with a `PersonAnimator` at a station — working, loading or unloading — stands at the station's staff position (`StationModel.StaffPosition`: a `StaffSocket` / `StaffSocket_1` / `OperatorSocket` of the element or its model, else in front of it — the side its +Z faces, half its depth + 0.6 m), not at its location node, which may be inside the machine; its walks to and from the station are drawn to and from that position (`UnityVehicleAnimator.CacheRoute`: a machine's replaces the node; a box with an `EntrySocket`, whose node is at the door, adds the way in from the door), so nobody walks through a machine or a box wall. The station of a node: the element located there that processes (a buffer sharing the node gives way). A view: the travel times are the model's. The default Source / Sink prefabs were scaled to the stations' size (×0.65). It freezes while the clock is paused and turns `UnityVehicleAnimator.ProceduralLimbs` off — the
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
    `SPMT` (vehicle view, `Lift` = the deck, stroke 0.35 m), `GoliathCrane`, `WeldingGantry` (`TravellingGantryRig`),
    `HallCrane` (`UnityOverheadCrane`), plus the existing `LevelLuffingCrane` and `IndustrialRobot_Welder`.
- **Unity-physics travel + calibration to 1D.** Engine: every executor can log its moves
  (`RecordMoves`, `Moves`: `MoveRecord` from / to / loaded / start / end / estimate distance; stops excluded — open-ended
  legs are shifted by stops too). **`ExternalTravel(estimate)`**: open-ended plans (duration NaN) ended by
  `TaskExecutor.Arrive()`; the estimate model (grid / matrix / network) gives distances. **`TravelCalibrationFit.Fit`**:
  per (from, to, loaded) mean / sd / n + least squares `time = A + B · distance + C · loaded` (R², residual sd).
  **`CalibratedTravel`**: the pair's mean when seen ≥ `MinSamples` times, else the general model on the estimate's
  distance (so it still holds when the layout changes); deterministic. Config: team `Travel = "Calibrated"` +
  `CalibrationBase` (estimate kind, with the team's Grid / Matrix fields), `CalibrationFit` `A|B|C`,
  `CalibrationPairs` (`from|to|loaded|mean|sd|n`), `CalibrationMinSamples`. Unity: team travel **Unity Physics**
  (scene only) — each member gets a **`PhysicsTravelMember`**: on an open-ended move it sends its
  **`IPhysicsMover`** (default **`NavMeshMover`** = `NavMeshAgent`; or the prefab's own controller) to the node at
  speed × clock time scale, calls `Arrive()` when it gets there, halts while the member is stopped or the clock
  paused, ends overlong moves (time-out factor × estimate) with a warning; maximum-speed clock mode is turned off.
  **`RuntimeNavMesh`** (tool) bakes a NavMesh on start from colliders (core API, no package). **`UnityTravelCalibration`**
  (tool): *Fit from this run* writes the team's **`TravelCalibrationAsset`** (ScriptableObject: survives Play mode);
  *Compare with headless* runs the extracted model with the calibration for the time simulated and lists sinks'
  throughput and members' utilisation side by side; the inspector reports coverage of pairs, R², residual sd. The
  extractor exports a physics team as `Calibrated`, and **refuses** (clear exception) when it has no calibration —
  so experiments / the optimiser never run a physics model silently (EditMode `PhysicsTeamNeedsCalibrationForHeadless`). Reference model: `resource_walk_calibrated`;
  example scene `Resources_PhysicsCalibration`.
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
- **Example scenes** (`Assets/Scenes/ResourceExamples/`, one per resource kind; all but the physics one run headless in
  the EditMode test `ExampleSceneRunsHeadless`): `Resources_Operators` (abstract team shared by two stations),
  `Resources_OperatorsWalking` (matrix travel, process + transport), `Resources_OperatorsWalkingGrid`
  (operators as spheres on a walk grid around walls), `Resources_AGVs` (fleet on a network, nearest AGV),
  `Resources_RobotCell` (axes with acceleration; the `IndustrialRobot` member on the floor at (0, 0, 0.2) since
  2026-10-07 — the points are unchanged, so the results are too), `Resources_GantryStacker` (the stacker's own gantry driven by the
  crane resource), `Shipyard_BlockAssembly` (generic ship block assembly: plate cutting → fitting → sub-block
  welding → inspection, block assembly of a jig + 3 sub-blocks → block welding → painting; forklift on a matrix,
  two gantry cranes on axes, transporters on a road network, workers with skills on a walk grid; minutes),
  `Resources_PhysicsCalibration` (two operators moved by NavMeshAgents with a runtime NavMesh, walk grid as the
  estimate, the calibration tool ready: run, fit, compare).
- Tests: `StandaloneTests/resourcetests`.

**Matching + transport (FlexSim's split: the receiver
decides *which* item it takes, the holder *whether* it is carried).**
- **Release mode** (`ReleaseMode`, config `releaseMode` / `releaseScan`, `ParameterSchema` choice) of `ItemsQueue` and
  `MultiServer`: `FIFO` (default, unchanged: only the first item is offered) or **`AnyAccepted`** — `Unblock` offers
  each held item, oldest first (up to `releaseScan`, 0 = all), through `SendHeldItem` until a receiver accepts one;
  a new arrival may leave at once. The receiver chooses with its input strategy (a Combiner's pull mode); the output
  strategies already pick among the receivers that `CanAccept` the item. With a transport (`ResourceTransportLink`)
  every acceptable item gets its own transport request.
- **`TransportMoves`** (`Resources/TransportMoves.cs`) — the move of one element's items by its transport team, shared
  by `ResourceTransportLink` (direct links) and `ListLink` (list transfers): the request, the carrier's task (empty
  travel to the origin, load — the origin hands the item over and the link sees `TakeLoading` —, loaded travel,
  unload, deliver through the link's callback, or wait HELD until `DeliverWaiting`). The direct link keeps its
  destination choice and the room promised to items on their way; an item stays "requested" until it is actually
  handed over (a deferred hand-over, e.g. zero load time, must not get a second carrier). A carrier
  that finishes loading at a **stopped** origin (output blocked) waits HELD (`WaitingForOrigin`) and takes the item when
  the stop ends; a run reset forgets such waits.
- Reference models `yard_kitting` (direct, Any Accepted: two bays by key, out-of-order blocks, one crane; FIFO deadlocks
  — `bugfixtests` #39), `yard_kitting_list`, `yard_kitting_capacity` (one Combiner, 2 assemblies); EditMode
  `MatchingTransportSceneTests` (the scene's extraction gives the reference result). Pattern guide:
  `Assets/Docs/Kitting_By_Key.md`.

### 3.18 Expressions — one syntax for every decision (`Expressions/`, namespace `SimuLean.Expressions`)

Wherever a property of the item or the element drives a decision the same syntax is used (user reference:
`Assets/Docs/Expressions.md`). **`Expression.Parse`** compiles the text once (recursive descent; `FormatException`
with the position; `Check` for editors) into a tree evaluated many times **without allocating** (verified by
`expressiontests`). Values are **`ExprValue`** (number or text, plus *missing*). Syntax: numbers, texts `'…'` /
`"…"`; `item.x`, `element.x` (alias `station.x`), `item['any name']`; a bare name = the context's default; `+ - * / %`
(÷0 = 0), comparisons (texts with `==` / `!=`, a missing value equals `''`, text vs number compares as numbers),
`&& || !`, `?:`; functions `min max abs floor ceil round sqrt pow if now has`, the random functions `rand() uniform(a, b)
exponential(mean) normal(mean, sd) pick(w1, v1, w2, v2, …)` (drawn from the scope's
**`IRandomScope.Random`** — for `EntityScope` the element's own stream `Element.ExpressionRandom` = `name/expr`, created on
first use, so a seeded run is reproducible; no stream = an error), the functions that read **other elements** by name
`content('X') free('X') get('X', 'property')` (overflow and balancing rules — **`IModelScope`**,
`SimContext.FindElement`: the first element created with that name, a station before its parts; an unknown name fails
naming it) and the table functions below (function
names case-insensitive; `1e3`, unary `+`; `has` takes a property; `if` / `?:` evaluate one branch; `round` half away
from zero, `sqrt(<0)` = 0, `%0` = 0; `References` lists the properties read; `Number` / `Condition` are NaN-safe; an
`ExpressionSampler` result < 0 or NaN counts as 0). Scopes (**`IExpressionScope`**):
**`EntityScope`** (item + element, reusable; bare = the item's property; built-in values when no property has the
name: `item.id / type / priority / created / age`, `element.name / content / free`) and the list's entry scope (bare
= the entry's fields, `item.x` its item). Uses:
- **times** — **`ExpressionSampler`** (replaces `LabelExpressionStrategy`, which substituted labels with regexes and
  evaluated with `DataTable.Compute`); stations draw through **`Samplers.Sample(sampler, item, element)`** (MultiServer
  / Combiner slots through `ServerProcess`, `SingleJobStation` (PickingStation), AssemblyStation tasks, Stacker cycles,
  `ErectionDock`, process-team hold times `StationResources.HoldFor` (`processHoldFor`), transport load / travel /
  unload times).
  Sampler spec / config type `Expression` (parameter `expression`; Unity `RandomProcessType.Expression`, field
  `expression`; scenes and prefabs migrated from `labelExpression`, a bare label name is a valid expression);
- **request priority** — `ResourceBuild.PriorityOf(expression, element)` (config `processPriority` /
  `transportPriority`, Unity field `ResourceUse.priority`, scenes migrated from `priorityLabel`; a bare label name is a
  valid expression);
- **routing** — **`ExpressionRoutingStrategy`** (replaces `LabelRoutingStrategy`): the value (floored) is the output
  index — or, built in code, a key of a value → output map (`valueToOutput`, else `DefaultIndex`); out of range or
  refused = wait; output strategy type `Expression` (`outputStrategy_expression`; empty = FirstAvailable);
- **admission** — **`ConditionStrategy`** (input strategy type `Condition`, `inputStrategy_condition`): the condition
  over the item and the receiving element;
- **lists** — calculated fields (replaces `FieldExpression`);
- **objective** — the optimiser's fitness formula over metric / parameter names (`SimulationObjective.MetricScope`,
  §3.19; no data tables).

- **Data tables in expressions.** `table('t', key, 'col'[, 'keyCol'])` (first row whose key
  column — the first one by default — equals `key`; a number matches the same value written otherwise),
  `cell('t', row, 'col')` (from 1), `rows('t'[, 'col', value])`, `sum('t', 'col'[, 'keyCol', key])` over
  `SimContext.Data` (§3.8). A scope reaches the tables through **`IDataScope`** (`EntityScope`: the element's clock;
  the list entries' scope: the list's clock; the optimiser's metric scope has none — the functions fail clearly).
  Cells read as numbers when they parse, else as texts; no row / an empty cell = missing; unknown table or column =
  an error naming it; argument counts are checked when parsed. Tests: `expressiontests` (a process time read from a
  table by the item's kind, list formulas). Guide: `Assets/Docs/Expressions.md`.

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

**The model as a problem (R-5b).** Everything is expressed on the `SimulationConfig`, so the same optimisation runs
for a scene, a headless model or a model written in code:
- **Every property is a parameter.** **`ModelParameterCatalog`** (`ForElement`, `ForTeam`,
  `ForDowntime`, `For(model, scope, owner)`) walks **every key** of an element / downtime config and every property
  of a `ResourceTeamConfig` (plus `members` and the members' `LoadTime` / `UnloadTime`) and offers it as a
  **`ParameterInfo`** (key, label, `VariableKind`, length, options, `ValueEncoding`, current value, suggested range):
  - **by the value's type**, with nothing to declare: `int` → integer, `double` → real, `bool` → yes / no, `int[]` /
    `double[]` → vector;
  - **by the `ParameterSchema`** for texts and lists — each handler declares its own (`IElementHandler.Schema`),
    merged with `ParameterSchema.Common` (routing / admission types, the distribution of the flat sampler keys,
    `processRelease`, transport times, user properties, structural references) and the shared `Team` / `Downtime`
    schemas: `Choice` (a text that is one of options — enums by `Choice<TEnum>`), `Sampler` (each number of a
    `Type~p1~p2` spec: `key~n`), `NumberList` (`"a|b|c"`), `Numbers` (a list of numbers as texts),
    `Properties` (user property records: each number / number array `properties.name`), `File` (a data file — a
    schedule source's `fileName`, follow-up #29: varied by a study with file names as levels, not offered to the
    numeric optimisers), `Structural` (names, references, tables, expressions, coordinates: not a parameter);
    `Describe(key, label, min, max)` only refines the label / suggested range of any key; schemas merge with `With`
    (the handler's wins over `Common`). The integer parts of a sampler spec (Binomial n, degrees of freedom, discrete
    bounds) are integer variables.
  - **what the current settings use**: an element's flat sampler keys only for its chosen
    distribution, labelled by the sampler's role ("Service time" for `delay…`, "Inter-arrival time — Uniform min" for
    `interArrival…`); a team's travel properties only for its travel mode (`Speed` / `Traffic` for *Network*,
    `AxisSpeed` / `AxisAccel` / `AxesSequential` for *Axes*…). `ConfigParameter.Of` resolves against every key
    (`ForAll`), so a saved parameter keeps working after its owner's settings change.
  **`Undecided(model)`** lists the keys neither rule decides; `optimizationtests` (every reference model) and the
  EditMode `ParameterCatalogTests` (every library element type with its defaults, each downtime kind, a team, user
  properties, every sample scene) require it to be empty — **a new element type, key or property must be decided**
  (offered or declared) to pass the gate. `ScheduleRows` gives a source's table length for sequence / per-row
  decisions. (Replaced the handlers' hand-written `Optimizable` lists, R-5b.)
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
  the `Study` parameters `TeamLabelMembersParameter`, `SourceTableParameter` and `DataTableParameter`
  (`Optimization/Study.cs`, §3.5) are `ModelParameter`s too.
- **`ModelMetric`** (name, element / team / list, statistic) read by **`MetricCatalog`** from the run's `RunResults`
  (§3.11; the lists are `ResultMetrics`): element `input`, `output`,
  `content` (at the end) / `content_avg` / `content_max`, `stay_avg` / `stay_std` / `stay_min` / `stay_max` /
  `stay_count`, `utilization`, `stops`, `last_entry` / `last_exit`, `state:NAME`,
  `member:NAME` (any public number of the engine element: a property or a method without parameters, e.g.
  `member:GetMeanServiceTime`, `member:CompletedTaskCount` — what the removed property registry read by reflection); team `members`, `utilization`,
  `served`, `tasks`, `request_wait_avg` / `std`, `requests_waiting_avg`; list `pushed`, `pulled`, `content` / `content_avg` /
  `max`, `wait_avg` / `std`, `pending_pulls_avg`, `field:F:…`; model (`MetricScope.Model`, no target) `end_time`,
  `makespan`, `deadlocked`, `deadlock_time`, `stalled`, `stall_time`, `empty_time`, `finish_time`, `stopped` (a time
  never reached reads NaN, so the fitness is −∞); reaction (`MetricScope.Reaction`, §3.21) `fired`, `not_met`. `SimulationObjective.Run(candidate, seed, inspect)` hands `inspect`
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

### 3.20 Naval library — block breakdown (`Naval/`, namespace `SimuLean.Naval`, N1 2026-10-08)

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

**`ErectionDock`** (`Naval/ErectionDock.cs`, N2; handler `UnityErectionDockHandler` in
`Naval/ErectionDockHandler.cs`, wrapper
`UnityErectionDock`, library module **Shipbuilding**, presets *Slipway* / *Building Dock*):
- `IWorkStation`, `IResourceUser`, `IKeepsItems`; `Capacity` (`capacity`) erection fronts (one `ServerProcess` each;
  per-front results `<dock>/front0…` when more than one); the erection time is its `delay` sampler (an expression reads the block's labels).
- **Admission** (`CheckAvailability` and `Receive`, so every link type agrees): a free front and an erectable block —
  `Sequence`: its `erect_seq` = blocks started + 1; `Precedence`: every block of its `erect_after` text (`"4 5"`) erected.
  A refused block stays upstream (a yard releasing any accepted item / a list lets others pass); the dock calls
  `NotifyInput` after every erection.
- An erected block **stays** (the hull; its resources are released at once, `processHoldFor` limits them further).
  With `ExpectedBlocks`, the complete hull leaves as one **ship** item (the blocks as sub-items,
  `CountSubItemsOnExit`: each counted as leaving; its visual from `GenerateItem`); kept BLOCKED while the output refuses
  it; then the next hull starts. Labels and the ship name are parameters (`blockLabel`, `sequenceLabel`,
  `predecessorsLabel`, `shipName`, `erectionRule` (`Sequence` / `Precedence`), `expectedBlocks`, `capacity`).
- Reference models `erection_sequence` (out-of-order arrivals, crane for its lift only, ship launched to a sink that
  keeps it) and `erection_precedence` (two fronts, a crew of two per erection, the hull kept: makespan = last erection).

**Presets (N3)** — `Editor/Naval/NavalPresets.cs`, *GameObject ▸ SimuLean ▸ Shipbuilding*: the shop stations
on existing elements (no new engine type): *Steel Stockyard* (`UnityStorageRack`, floor storage), *Profile Rack*,
*Sub-Block Yard*, *Block Yard* (Queue, `AnyAccepted`), *Plate Cutting*, *Sub-Assembly Cell* (fitters by `skill`),
*Turning Station*, *Outfitting Station*, *Blasting & Painting Hall* (MultiServer; the painters held 6 h by `holdFor`),
*Sub-Block Welding* (Combiner, routing on `item.turn`), *Block Assembly Bay* (Combiner, SingleLabel `block`,
`updateRequirements` from `nsub`), *Slipway* / *Building Dock*, *Launch* (Sink, keeps the ship). The times are
expressions with a default (`has(t_sba) ? t_sba : 6`), so a breakdown column drives them. Two composites place connected
elements under one parent: *Panel Line* (4 stations in line) and *Block Process (IJPR)* — the flow of the reference model
**`naval_block_process`** (`ReferenceModels.NavalBlockProcess`, on the generic `ReferenceModels.SampleHull()`) with its
teams and a `UnityBlockBreakdown` on the project database's `sample_hull` table (written from the sample hull only when
missing — by the preset, and by `SampleHullOnOpen` when a scene that reads it is opened, since the database is not in
the repository). Teams (*Shipbuilding ▸ Teams*; the *Hall Crane* is a Core team): *Goliath Crane*, *Level-Luffing Crane*, *SPMT Transporters*,
*Welding Gantry*, *Welding Robots*. The block workspace model stands in for the station models still to come (N6). Tests: standalone `navaltests`
(the block process), EditMode `BlockBreakdownTests` (every preset visible with its icon, the composite runs headless, an opened scene gets the missing sample hull).

### 3.21 Signals and reactions (`Signals/`, namespace `SimuLean.Signals`)

"When an element signals, if a condition, do actions".
- **Signals** are the hook points elements already raise: `ItemEntered` / `ItemExited` (the link layer's entry / exit,
  `ElementStatsCollector` → `Element.RaiseItemEntered`), `ProcessEnded` (`Element.ProcessEnded`, raised by the Multi-Server,
  single-job station, Combiner and assembly station when they finish an item, before sending it on), `StateChanged`,
  `Stopped`, `Resumed`; and the clock's: `RunStarted`, `Every` (`period`, `first`), `AtTime` (`at`) — a reaction's own
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
  `SignalHub.ReactionStops` —, never a downtime's), **PushToList** (a value to a model list). **Groups**: `ReactionAction.Group` (empty = default) tags a stop; a resume ends only its group's, so independent
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
  a parameter bag: `enabled`, `signal`, `state`, `condition`, `delivery`, and per action `aN.kind`, `aN.target`,
  `aN.name`, `aN.number` (a plain number) or `aN.expression`, `aN.state`, `aN.mode`, `aN.duration`, `aN.team` /
  `aN.count`, `aN.group`, `aN.condition` (only if; structural); clock signals' `period` / `first` / `at`; `AddAction` writes them). `ReactionBuilder` builds each reaction after the elements and downtimes (pass 5 of `HeadlessModelFactory`,
  `ReactionsById`) and fails the build naming the reaction and the field (unknown element / list / signal / action, an
  expression that does not parse, an item label on a signal without an item, a negative duration, two reactions of one
  name).
- **Parameters**: `ParameterScope.Reaction`, `ParameterSchema.Reaction` — `enabled`, the clock's
  `period` / `first` / `at` and each action's `aN.number` / `aN.duration` / `aN.count` are catalog parameters; signal, actions, modes, names, expressions are the
  reaction's design (structural). A study: `reaction:Name.enabled=true|false`, `reaction:Name.a1.number=…`.
- Tests: standalone `signaltests` (each action, delivery, state filter, determinism, build / run errors, catalog and
  studies, no subscribers).

## 4. Unity integration layer (`Assets/SimuLean.Unity/`)

### 4.1 The wrapper pattern: `SElement` + `IVElement` — one build path

**`SElement`** (`Core/SElement.cs`) is an `abstract MonoBehaviour` — the base of every element component. It holds the
connections (`nextElements`, or `sendTo` / `receiveFrom` a list: `pushToList`, `pullFromList` with `pullQuery`), the
routing (inline `outputStrategy` / `inputStrategy`, or the shared `OutputStrategyAsset outputStrategyAsset`, which
overrides the inline one), the location node and `workNodes`, and the resources (`processBy`, `transportBy`). **It does not build anything**: a component is the *description* of an
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
any more (the parity audits §8.1 were all of this kind). The EditMode test `SceneRunTests` checks that a scene
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
| `UnitySource` | `ScheduleSource` / `InterArrivalSource` (`mode`) | Schedule: the arrivals are a **data table** (`dataTable`, a `UnityDataTable`; inspector: *New* creates one named after the source; `simulean_load_table` creates / reuses it; a file table's *Read file at run* / stale-copy warning, live on the `UnityDataTable`); the source's own table file was removed 2026-10-11 (rows set from code with `SetArrivalsTable` are still exported as `arrivalsTable`, `UnitySource.TableForRun`). Configs run without the scene carry their table, as before; or `tableSource` (+ `tableDatabase`): an `sql:` / `naval:` spec read at build (`TableSources`, §3.8 / §3.20); inter-arrival: a distribution; item labels `assignLabels` (label = expression); **`demandFactor`** (default 1, always extracted so the catalog offers it: `Source.demandFactor=1|1.2` runs demand scenarios) — the inter-arrival rate times the factor, or each table row's quantity times it with the fractions carried from row to row (deterministic; the total is the table's times the factor); ≤ 0 fails the build |
| `UnityQueue` | `ItemsQueue` | FIFO. Item layout: *Grid* (default) — centred on the queue's footprint (its mesh, in its axes, or `gridArea`), spacing from the item size (`separation` 0 = auto), front row first, layers on top when full — *Stack* (one column upwards), *Seats* (the queue's `Seat` nodes in hierarchy order, e.g. waiting chairs; then the grid) or *Line* (a single file, the head at the item position, the others behind along −Z facing it; with `lineLength` the line folds back beside itself, 1.5 steps aside, as a serpentine — `ItemLayout.LineOffset`); the layouts live in **`ItemLayout`** (`Visuals/ItemLayout.cs`, shared by every holding element, today `UnityQueue` and `UnityDelay`); `showFootprint` hides the queue's own pad (the footprint still sizes the grid); no fixed sizes. `releaseMode` / `releaseScan` (also on the Multi-Server) choose which waiting item leaves first |
| `UnityDelay` | `Delay` | *Processors ▸ Delay*: every item waits its own delay at once (`delayStrategy`, an expression reads the item) — a night at home, a therapy, a curing time — whatever how many wait (a huge-capacity Multi-Server was the workaround). Items drawn with the queue's layouts (`ItemLayout`); an `UnityElementLabel` can hide them behind a count |
| `UnityAgenda` | `Agenda` | *Healthcare ▸ Agenda* (module `SimuLeanModules.Healthcare`): `sessions` (weekly pattern text), `slotMinutes` / `leadDays` (calendar minutes / days, converted with the model's time unit by the handler), `reservedPerSession`, `priorityLabel`, `overflow`, `noShow` (expression), `holidays`; the waiting patients drawn with the queue's layouts (`ItemLayout`) or hidden behind an `UnityElementLabel`. The visit station after it needs no shift |
| `UnityGateQueue` | `GateQueue` | Gated release, 3D grid layout |
| `UnityMultiServer` | `MultiServer` | N parallel servers (the element's state from per-state slot counters and an indexed work list, O(1) per event whatever the capacity —, 2026-10-08; `GetItems()` lists every held item — waiting for its resources, in process, blocked — from an indexed list of the waiting slots), selectable delay strategy, optional process / transport resources. Item layout: one slot per server (`ServerProcess.Index`) in a row along the element's X, centred on `inItemPosition` (else the top of the footprint, `ItemVisuals`), spaced by the item size (`separation` 0 = auto); with `outItemPosition` each item moves from its slot towards it (same offset) while processed |
| `UnityCombiner` | `Combiner` | Joins main + component streams. every upstream element connects through its own `nextElements`; the combiner maps connection 0 to the main input and the next ones to component inputs 0..N−1 (`Combiner.ResolveInputFor`, called by `GeneralLink.CreateLink`; order = optional `inputOrder` list, then hierarchy order; exported as the `inputOrder` parameter). The former `myInputs` wiring (component sources listed on the combiner, `CombinerInput` connection type) was **removed** with no compatibility path — the sample scenes were migrated. Components can also be pulled from lists (`listInputs`). *Visuals:* the main item waits on the work point (`itemPosition`, `Combiner.prefab`'s `ItemPoint` on the work zone; else the top of the footprint, `ItemVisuals`), the components behind it, one column per input, spaced by their size — with `capacity` > 1 each assembly has its own work point, side by side along the combiner's right axis (`assemblySpacing`, 0 = from the items' size; set it to the pitch of the assembly beds when the model has one per assembly, with an explicit `itemPosition` at their centre — the automatic work point is the top of the largest single part); the waiting components stand behind the main item, one column per input (`componentLayout` **Behind**, default) or pile on the work point (*OnWorkPoint*: parts laid on the assembly bed; `UnityCombiner.PartOffset`, EditMode `CombinerLayoutTests`); at the processing start (`UnloadItem`) `visualMode` decides: **Attach** (default) — the components become children of the main item in a grid on its top and travel with it; *MainOnly* — they are destroyed; *NewItem* — a `combinedItemPrefab` instance replaces the main item and becomes `vItem` (none set → Attach) |
| `UnityPickingStation` | `PickingStation` | Containers arrive through the upstream `nextElements`, stock from `stockSources` into the SKU stock port (`stockCapacity`); pick list from a `pickTable` data table, the container's labels or the static list; surplus goes to `returnsDestination` |
| `UnityAssemblyStation` | `AssemblyStation` | A precedence network of `tasks` (id, predecessors, materials by SKU, duration, priority), `workerCount` tasks at a time, materials from `materialSources` into a shared stock (`stockCapacity`) |
| `UnityErectionDock` | `ErectionDock` (§3.20) | `capacity` fronts, `erectionRule`, duration, `expectedBlocks`, block / sequence / predecessor labels, `shipName`; view: a block hangs `hoistHeight` over its hull place (`positionLabels`) and settles when erected; the launched ship gathers the hull's blocks and travels on |
| `UnitySink` | `Sink` | Terminal; `GetNumberItems()` / input count, `expectedItems` (progress); `keepItems` keeps the received items in the scene, laid out by `positionLabels` (else a row along +X), removed in `ClearViews` |
| `UnityResourceTeam` | `ResourceDispatcher` + `TaskExecutor`s | Plain `MonoBehaviour` (not an `SElement`): a team of resources and its dispatch orders (§3.17); extracted by `SceneExtractor`, built by the model factory and bound (`Bind(dispatcher, points)`); member views created in `OnRunStarted`, removed in `ClearViews`. Travel `TeamTravel`: Abstract / Network / Matrix / Axes / Grid / UnityPhysics (+ `physicsEstimate`). Elements use it through their `processBy` / `transportBy` fields |
| `UnitySimList` (`Lists/UnitySimList.cs`) | `SimList` + `ListLink` (§3.16) | Plain `MonoBehaviour` (library tool, group *Lists*): name, fields, default order; built by the model factory, bound with `Bind(ListLink)`; elements push / pull through `pushToList` / `pullFromList` + `pullQuery`; the inspector shows the live content |
| `UnityDataTable` | a `ModelTable` of `SimContext.Data` (§3.8) | Library tool, group *Data* (`SimuLeanToolCategories.Data`): a named table — rows typed as CSV in the inspector (Inline), a file (CSV / `.data` / Excel through `TableFiles`; *Load* keeps a copy, *Read file at run* re-reads it at every run start, else a warning when the file is newer than the copy) or an SQL query on the project database (run at every run start). `SceneExtractor` adds each one's snapshot (`Snapshot`) to `SimulationConfig.Tables` and sets `DatabasePath` |
| `UnityTransportNode` / `UnityTransportNetwork` / `UnityVehicleAnimator` | `TransportNetwork` | Plain `MonoBehaviour`s (not `SElement`s): scene node marker; the model's network (one per model, exported as `SimulationConfig.Network`), authoring + build + arrow gizmos + explicit *Place Elements On Closest Node* tool (fills each element's `location`); per-member view that interpolates along `TaskExecutor.CurrentLeg`'s route from sim time and stands at its node otherwise, carried items riding on it — **Unity animates, the engine decides the times**. `UnityTransferPoint` / `UnityTransportSystem` were removed 2026-10-06: every `SElement` has a `location` node, and a fleet is a `UnityResourceTeam` with *Network* travel (home node, speed, member prefab) |
| `UnityStorageSystem` | `StorageSystem` | Storage S5. Lanes from an Inspector list (`StorageLaneKind` presets or custom access/SKU rule, per-lane anchor Transform + optional network node) or auto-generated one-per-child-Transform; location/relocation policy dropdowns (+ SKU→lane map for DedicatedBySku); timed moves by its Transport team (resources section) to / from its `location`; put-away via upstream `nextElements`, retrievals programmatic (`RequestRetrievalBySku` or core API), delivered through its own `nextElements` link; stored items stacked above their lane anchor by default (`protected virtual GetStoredItemPosition` — the hook subclasses override for exact location views) |
| `UnityStorageRack` | `StorageSystem` (via `UnityStorageSystem`) | Parametric storage element **library**: `StorageRackStyle` ∈ ConventionalRack / FloorStorage / DriveInRack / PushBackRack, created preconfigured from *GameObject → SimuLean → Storage*. Counts (bays, slots/bay, levels, depth, stack height) + physical pitches (bay width, slot width, level height, slot depth); `Rebuild()` (Inspector button / context menu) regenerates one marker-tagged child anchor per lane plus slot visuals (optional **unscaled** slot prefab per position, else generated shelf/floor plates) and rewrites the inherited lane list — so behaviour, extraction (registry alias `"UnityStorageRack"` → the storage handler) and headless runs are unchanged. `[SelectionBase]` makes clicking any slot select the rack root. Overrides `GetStoredItemPosition` for the item location view: rack styles place units along lane depth, floor storage stacks upward (oldest on the ground) |
| `UnityItem` | — | Debug MonoBehaviour on item GameObjects (`Core/UnityItem.cs`, namespace `SimuLean.Unity`) |
| `UnityReaction` | `Reaction` | Plain `MonoBehaviour` (Library ▸ *Logic*, `SimuLeanToolCategories.Logic`, its own icon `Editor/Icons/Reaction.png`): *When* `sources` `signal` (`state` filter; clock signals: `period`, `firstTime`, `atTime`, no sources) *if* `condition` *do* `actions` (`ActionSpec`: kind, target element or `onTeam` + `team` + `count`, name, list, value — a plain number or an expression —, stop state / mode / duration), `reactionEnabled`, `timing`. Connection lines (`SceneConnections`: `ReactionSource` element → reaction, `ReactionTarget` reaction → element / team, group `Logic`, purple, dotted; the Connect tool adds a source); a team's inspector lists its reactions. Exported by `SceneExtractor` (`ToConfig`, `ReactionIds`) as a `ReactionConfig`, built by the engine, bound in Play (`Bind`, `Bound`). `UnityReactionEditor` reads as its sentence (`Sentence`), shows only each action's fields, checks it (no elements, expressions that do not parse, a label without an item…) and, in Play, its counts; the Play inspector panel (`ElementInspectorReport`) lists the reactions of the selected element from `SimContext.SignalsIfAny`. User guide: `Assets/Docs/Signals_And_Reactions.md` |
| `UnityDowntime` | `DowntimeGenerator`s (one per target) | Plain `MonoBehaviour` (not an `SElement`): kind MtbfMttr / Timetable / Shift / Periodic (`periodicFirst`, `periodicPeriod`, `periodicDuration`, `useUntil` + `periodicUntil`, `allowOverlap`, `periodicCode`; the inspector turns the BREAKDOWN default into SCHEDULED_DOWN when Periodic is chosen and flags invalid settings), target list (+ optional keys for timetable matching), stop behaviour (state, mode, blocking), MTBF/MTTR distributions (`RandomProcessConfig.ToSamplerSpec()`), shift pattern + holidays, inline timetable rows and/or a data table with column mapping and per-code rules. Exported by `SceneExtractor` (`ToConfig`) as a `DowntimeConfig`, built by the model factory (`Bind(generators)`) and started by `StartModel`; the rows can come from a data table (`dataTable`) |
| `UnityStacker` | `Stacker` | Positions from `positionAnchors` or a generated row (`firstPosition` + i × `positionStep`), `stackBaseHeight`, pick point (`pickPoint` / `pickOffset`), all engine settings (rule, labels, capacities, height, cycle time as a `RandomProcessConfig`). Visual: item GameObjects go to the pick point on arrival and snap to their slot (`SlotWorldPosition`, stacked by item height or `itemThickness`) when placed — unless an `IStackerAnimator` (`GantryStackerView`, §4.10) is still animating them; on `StackCompleted` a new parent GameObject is created at the stack base, the items are re-parented and it becomes the compound's `vItem`, so downstream elements move the whole stack. Crane positions `pickNode` / `positionNodes`. Extracted by `UnityStackerExtractor`, built by `UnityStackerHandler`; inspector with live stacks and *GameObject → SimuLean → Stacking → Stacker / Gantry Stacker* |
| `UnityLengthLimitedQueue` | `LengthLimitedQueue` | Lane lengths list, gap, default length, length label, max items, oversize rule, input/output strategies. Local +Z of `laneOrigin` = flow direction, origin = lane exit; items drawn end to end backwards along −Z with their real lengths, lanes offset by `laneSpacing` along +X; lanes drawn as gizmos. Extracted by `UnityLengthLimitedQueueExtractor`, built by `UnityLengthLimitedQueueHandler` |

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
  model on a new `SimClock` (§8.1).
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
element ids) / `StopAtCount` / `StopWhenEmpty`; `Update` finishes the run when the clock is stopped (`clock.IsStopped`) as at max time, and
*Run Headless* stops there too. The extractor
exports `maxTime`, the seed and `warmupTime` into `SimulationConfig` (§8.1).

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

Built on the optimisation model (§3.19, §5): the same `ParameterSpec` /
`MetricSpec` lists as the Optimizer and `ExperimentScenario` rows (name, replications, one value per parameter **as
text** — a number, an option, `true` / `false`, values separated by commas for vectors and sequences —, read by
`Candidate.SetText`; any kind of parameter). The range of a number is the scenarios' values. `Validate()` builds
every scenario without running it; the inspector draws each value by its kind (popup, toggle, number / text). Scenario
names are unique (`Study.Plan` refuses two with one name; a new row gets the first unused "Scenario k"); the set's
metadata keeps the study's `base_seed`, `cancelled`, `failed_runs` and `first_error`; removing a
parameter removes its value in every scenario; an error during an in-scene run ends the experiment and gives the clock
its settings back. Two modes:
- **Headless** (`headlessMode`, default): the scene is extracted once; the objective's parameter ranges are the
  scenarios' values; every (scenario, replication) is a `SimulationObjective.Run(candidate, seed, inspect)` in
  `Parallel.For` (`threads`, 0 = `Study.DefaultThreads`: one per processor up to 8), in Edit or Play mode, cancellable. Replication r uses the seed
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
  over the progress slices). **A Play run records them too (G2b)** when `seriesInterval` > 0 (default 0 =
  unchanged): the clock advances through `SeriesRecorder.RunTo(t, final: false)` (`AdvanceClock` at each interval boundary, then
  up to the frame's target — the same events run, tested frame by frame in `resultstests`), the
  last, shorter interval is closed in `PublishRun` and the series go with the run's results. `CaptureLive()` gives the
  run going on as `RunResults` (statistics now + a copy of the series so far), read-only, for views.
- **Utilisation** shown by the inspector panel and the element inspector uses the results' definition
  (`ResultMetrics.Utilization`: the mean slot utilisation of a Multi-Server, Combiner or Erection Dock, else the
  PROCESSING share; was the share of "busy" states).

### 4.6 Custom editors (`Editor/`)

**Inspector standard (`Editor/Inspectors/Framework/`; review2 block 4, 2026-10-11: the header icon is
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
needs (an expression shows its error). Every element follows it: Multi-Server, Source, Queue, Gate Queue, Sink,
Combiner (its inputs in Flow), Stacker and Storage Rack (their fields through `DrawRemaining`, which splits scene
references / layout fields into Visual), and `GenericElementInspector` (fallback for any other `SElement`).
Element-specific hooks: `DrawFlowExtra`, `DrawPlayExtra`, `CollectIssues`. The `SElementConnectionGizmo` component is
hidden from the inspector (its switch is *Draw connections* in Visual).

`UnityMultiServerEditor` & `UnityCombinerEditor`
(conditional inspectors that hide irrelevant delay-strategy fields);
`UnitySourceEditor` (mode, edit-time data load / preview);
`UnityResourceTeamEditor` (members, *Add N*, live member state / utilisation / tasks; while editing,
`Tools/TeamMemberPreviews` shows each member's prefab at its spawn point — a `DontSave` child `MembersPreview`, removed in
Play, the team a `[SelectionBase]`, review2 #48) and `ResourceUseDrawer` (an element's Process / Transport blocks, drawn by `SElementEditorHelper.DrawResources` after the connections; the query builder offers request / member fields); `UnitySimClockEditor` (live sim-time
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
Other inspectors: `UnityQueueEditor`, `UnityGateQueueEditor`, `UnitySinkEditor` (on `SElementInspector`);
`UnitySimListEditor` (fields, pushers / pullers; in Play the live content, pending pulls, statistics) with `ListDrawers`
(one-line list field, the pull query builder); `UnityDataTableEditor` (source, *Load*, inline CSV rows, preview);
`OperatorAppearanceEditor` (the look as a `PeopleLooks` list); `SElementLabelsEditor`; `ElementInspectorPanelEditor`
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
  push / pull lists, combiner list inputs, process / transport teams, element locations, network edges, team home
  nodes, downtime targets), with the kinds / groups / colours / labels; shared with the HUD's run-time view (§4.9).
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

**Automatic scene hierarchy** (`Editor/Hierarchy/ModelHierarchy.cs`): a root `SimuLean Model`
with fixed groups by function — `Data` (data tables, the block breakdown), `Flow` (every element), `Resources`, `Lists`,
`Transport`, `Downtimes`, `Logic` (reactions), `Experiments`, `Runtime` (clock, views and analysis tools). The group comes from the library attributes (`[SimuLeanElement]` → Flow,
`[SimuLeanTool]`'s category; `OnMainCamera` tools and cameras stay put), so a new type needs nothing. An
`ObjectChangeEvents` hook places whatever is created at the scene root with such a component (library, *Set Up
Scene*, a prefab dragged in…; *Tools ▸ SimuLean ▸ Auto Organize Hierarchy*, per user, default on); *Tools ▸ SimuLean ▸
Organize Hierarchy* tidies the open scenes. Only scene-root objects move (what the user nests stays), with their world
placement kept; groups sit at the origin. Nothing in the build path depends on the hierarchy — except the combiner's
input order when `inputOrder` is empty (hierarchy order), which the sample scenes all set.

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
lane's length and spacing (`UnityRollerConveyor.FitTo`, from the lane's `OnValidate` in the editor). Each switch keeps
its own scroll position. Tests: `LibraryPacksTests`.

Tabs (Library, Model, Data, Fit, Experiments, Results, Settings — callers name them, `TabIndex("Fit")`), by what a thing
is for:
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
- **Library presets** (`Editor/LibraryPresets.cs`) — the library's objects as in FlexSim: each is an element
  with its default behaviour and a model, visible in the scene as soon as it is placed. *Manufacturing*: CNC Machine,
  Workstation, Paint Booth, Inspection Station, Weld Positioner (MultiServer), Assembly Bench, Palletizer (Combiner),
  Safety Fence (`UnityModularStrip` of the `SafetyFence` kit). *Healthcare* (`Editor/Healthcare/HealthcarePresets.cs`,
  2026-10-09, moved from *Hospital* and extended): Referrals (Source of
  `Prefabs/Items/Patient`, exponential inter-arrival, the **hospital door** — `Art/Models/HospitalDoor`, automatic sliding glass doors whose leaves `Door_L` / `Door_R` a **`SlidingDoorView`** (`Visuals/SlidingDoorView.cs`, read-only: the element's `ItemEntered` / `ItemExited` events) opens as patients pass — and, with `openWhenNear` (2.5 m), when a shown person comes near: a door with no element,
  e.g. a building's entrance, opens for walking patients (`PersonAnimator.AnyVisibleNear`); patients appear at its `ItemSocket`), Clinic Agenda (`UnityAgenda`,
  virtual look, a hidden-items count), Waiting List (at home) (Queue, virtual look *At home*, count), Waiting Room (Queue in the
  **Seats** layout over a `WaitingChairs` strip, one seat per place: each patient takes the first free seat and keeps it until it leaves (`ItemLayout.SeatsTaken`: the others do not move up when one goes), sits with the `SitIdle` clip, those beyond the seats stand; the queue inspector shows seats vs places and *Fit Chairs to Capacity* rebuilds the strip, `UnityQueueEditor.FitChairs`), Waiting Line (Queue, *Line* layout, no pad),
  Reception Desk (MultiServer, 2 places), Consultation Room (MultiServer, consultation box), Home Test and Treatment at
  Home (`UnityDelay`, virtual look), Report (not face to face) (MultiServer, virtual look), Triage (MultiServer; entry label `triage = pick(0.1, 1, 0.3, 2, 0.6, 3)`), Diagnostic Test (hospital) (MultiServer with its own equipment pool of 2 as the process team), Discharge (Sink with the
  item statistic `time_in_system = age`, the hospital door), Equipment Pool (a team of 3 devices without views; staff are the Operators
  team with the Doctor / Nurse look, not presets); composites
  **Outpatient Clinic** (referrals → agenda → waiting room → consultation room with a doctor → discharge) and **Home
  Diagnostic Test** (device desk lending a device of the pool until the device return — `AtElement` — → home test →
  return; `valid = rand() < 0.9` on entry to the desk, invalid tests back to the desk's line, valid ones to the report) and **Follow-up Loop** (referrals → treatment at home → review line → review — `reviews = reviews + 1`, `continue = rand() < 0.6` — → back to the treatment or discharge).
  **Healthcare Dashboard** (a `UnityDashboard`, top right, with the library charts `Charts/Healthcare/*.asset` created once by
  `HealthcarePresets.HealthcareCharts`: patients waiting per list, time in system P90 at the discharges, agenda slots
  booked, lead time to the appointment — elements found by the presets' names). EditMode test `HealthcarePresetsTests`
  (the composites run headless; the dashboard's charts build from a clinic run). *Teams*: a
  `UnityResourceTeam` with its member prefab — Operators, Bed / Stretcher / Wheelchair Porters, AGV Fleet, Forklifts, Pallet Trucks, Robots (Gripper / Vacuum / Welding) — a doctor, nurse or fitter team is an Operators
  team whose member prefab (`Doctor`, `Nurse`, … variants of `Operator`) or `OperatorAppearance.look` gives the role.
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
- **Results** — first a **Summary** (review2 #35: per element in, out, average content and stay,
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
- **Settings** — *Set Up Scene*, *Organize Hierarchy*, the switches *Set up new scenes automatically* (project) and
  *Put new objects in their hierarchy group* (user), the clock (select / create), the run-time views as on / off
  switches (singletons; added to the clock's object, the camera controller to the camera; HUD and panel wired), the
  floor plan (add / select), **Results** (project: the results folder — relative to the project or absolute —, the
  confidence level of the intervals, the formats of an export made by hand, *Store results* = `storeResults`), *Reimport 3D Model Library* and every
  `Tools/SimuLean/Checks/…` item. The project settings are one file, `ProjectSettings/SimuLeanSettings.json`
  (`SimuLeanProjectSettings`, runtime assembly: also read by `ResultsOutput` at run time in the editor).

The attributes are pure metadata (nothing happens at run time). The *Tools ▸ SimuLean* menu keeps only *Open
SimuLean*, *Set Up Scene*, *Checks* and *Reimport 3D Model Library*.

**Scene set-up** (`Editor/Tools/SimuLeanSceneSetup.cs`): a modelling scene comes ready —
one `UnitySimClock` with its default runtime views on the same object (`SimRuntimeHud`,
`ElementInspectorPanel`, `SimVisualDetail`) and a `SimCameraController` on the main camera, wired to
each other. It runs from *Tools ▸ SimuLean ▸ Set Up Scene*, when the clock is created from the
library or added by hand (`ObjectFactory.componentWasAdded`), automatically in every new scene
(`EditorSceneManager.newSceneCreated`) and once per project, on the first load, in the open scene;
the automatic part is the project setting *Set up new scenes automatically* (SimuLean window ▸ Settings)
(`ProjectSettings/SimuLeanSettings.json`). Editor-only and never in batch mode; idempotent
(existing components are kept and only wired). With several scenes open it works on the scene it sets up (its own
clock, camera and views; it used to find another scene's clock). The HUD and the inspector panel also fill their clock
/ camera references in `Reset`. **`ElementInspectorPanel.elements`** (*Inspectable Elements*; only
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
  the shape of a FlexSim object. The chosen model is the child `Model`, placed in edit mode (`[ExecuteAlways]`, rebuilt
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
  SETUP, red STOPPED / BREAKDOWN / SCHEDULED_DOWN / OFF_SHIFT, amber otherwise (`LampFor`). Parts move by node name
  while the element works (`Spindle` / `Turntable` spin, `Head` / `Table` / `Tilt` / `Screwdriver` cycle, `Lights`
  flash), doors (`Door_L/R` slide, `Door_In/Out` roll up) and the `Curtain` are open while it waits and closed while
  it works, the palletizer's `Slew` › `Lift` cycle; motions advance only while the simulation clock does. Before a
  run the baked look (all lamps lit) shows.

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
  setting `length` and calling `Rebuild()` at runtime.
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
  (±span/2, at `railHeight`, along +Z), a `Bridge` transform holding two fixed-shape
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
  `WorkstationProcessor` / `AssemblyWorkstation` models of the MultiServer / Combiner / AssemblyStation prefabs; each station carries a stack light `Beacon` › `Lamp_Green` / `Lamp_Amber` / `Lamp_Red`
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
  components on a container's `LoadSocket` (`ItemVisuals.LoadSocket`) — and `Patient`. `LoadUnits/` (the item looks `EuroPallet` / `Tote` / `RollCage`; kit:
  `EuroPallet`, `Tote` (KLT), `RollCage`; `LoadSocket`, the tote's `StackSocket`), `PalletTruck/` (hand pallet
  truck: `Lift` › `LoadSocket`, `Handle`, `Wheel_L/R` r 0.10 m — the AGV's names, so `UnityVehicleAnimator` drives
  it — and `DriverSocket`; member `PalletTruckOperator`), `SafetyFence/` (the *Safety Fence* preset; kit: `Fence_Panel` 1.0 m segment, `Fence_End` cap, `Fence_Door` with a
  `Door` node) and `ASRS/` (kit: `Rack_Bay` 1.4 m segment with `Slot_1..5`, `Rack_End` cap, `Aisle_Rail` segment,
  `StackerCrane` with `Carriage` › `Forks` › `LoadSocket`; `Rack_Bay` is the *Profile Rack* station model and a preset
  icon, `StackerCrane` is not used yet). `ShipBlock/` (`assets/ship_block.py`): two static hull blocks
  sharing one atlas, `ShipBlockCurved` (12 × 16 × 5 m bilge / side block) and `ShipBlockFlat`
  (12 × 16 × 1.6 m double-bottom panel); pivot at the base centre, nodes `Lug_1..4` (sling
  attach points) and `LiftPoint` (crane hook); LOD0 ≈ 3.4 k / 2.6 k tris, one renderer. *(N5)* No number is
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
  the same names are variants of `Operator` (the *Block Process (IJPR)* composite's crews use them, members labelled with
  their `skill`). `operator.orient_outward` turns the faces of a garment lofted round the torso (vest, belt) outwards —
  an open loft cannot be recalculated, and engines cull back faces (the vest showed its dark inner back in Unity;
  the operator was re-exported with it). Sample scene `Scenes/LibraryExamples/Shipyard_Library.unity`:
  the *Block Process (IJPR)* composite and the yard's resources.
  `BlockWorkspace/` (`assets/block_workspace.py`): `BlockWorkspace` (a 16 × 20 m floor pad with bay
  markings and 8 block stools; node `BlockSocket` at the stool top, where a ShipBlock root goes;
  LOD0 ≈ 4.1 k tris) and `BlockScaffold` (a 2.5 m scaffold bay with its deck at 2.0 m; ≈ 1.0 k
  tris), sharing one atlas. `LevelLuffingCrane/` (`assets/level_luffing_crane.py`): a double-link
  level-luffing portal crane ("grúa cigüeña") with travel on rails along local Z. Its nodes are
  `Slew` › `Boom` › `Jib`, `Slew` › `Tie`, and `Ropes` / `HookBlock` (also under `Slew`), plus the
  sockets `JibTip`, `TieEnd` and `HookSocket`. The four-bar linkage (`linkage(theta)` in the script;
  formulas in `Tools/Blender/README.md`) keeps the hook level over a 17–35 m reach. LOD0 ≈ 9.8 k tris
  on 7 renderers; member prefab `Prefabs/ResourceMembers/LevelLuffingCrane`, animated by `LevelLuffingCraneView`
  (§3, Resource views). The blocks are the `Block` / `SubBlock` item looks (`Prefabs/Items/Shipbuilding/`), the
  workspace is the station model of the naval presets and erection docks; `BlockScaffold`, `RailShuttle/` and the ASRS
  `StackerCrane` are not used yet. **Hand-made models** (no `<Folder>_Albedo.png`, so the postprocessor leaves them
  alone): `SourceStation`, `SinkStation`, `BufferPosition`, `CartonItem`, `WorkstationProcessor`,
  `AssemblyWorkstation` — the looks of the element prefabs `Source`, `Sink`, `Queue` / `GateQueue`, `Item`,
  `Multiserver`, `AssemblyStation` / `Combiner` — plus the unused `AGV_Mouse` and `BufferBox`, and their `Materials/`.
  Shared Blender modules: `simulean_kit.py` (scene, bake, LODs, `export_fbx`), `simulean_materials.py` (procedural
  source materials baked into the atlas), `simulean_modular.py` (kits: many FBX pieces on one atlas),
  `assets/station_parts.py` / `hospital_parts.py` (shared parts and palettes), `assets/workshop_preview.py` (building +
  crane kits together); each script also saves `Tools/Blender/build/<Asset>.blend` (git-ignored), from which
  `export_flexsim.py --asset <Name>` writes FlexSim files.
- **`SimuLeanModelPostprocessor`** (`Editor/Tools/`, an `AssetPostprocessor` scoped
  to library folders of `Assets/SimuLean.Unity/Art/Models/` — those with their own
  `<Folder>_Albedo.png`): sets texture import types; creates `<Name>.mat` (Standard,
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

- **`SimCameraController`** (`Core/Camera/`) — FlexSim-style navigation camera
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
  key N; on for up to 40 elements; review2 #45), the help shown by itself only the first time (`PlayerPrefs`), the **model seed** (a field with the
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
  *Connections* button) draws `SceneConnections.All()` as camera-facing GL bands with arrows in `OnRenderObject`
  (game cameras only, on top of the geometry), filtered by group (`showFlow` / `showLists` on, the others off);
  read-only, rescanned once a second.
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
  button to collapse). Every `refreshSeconds` of real time it takes `UnitySimClock.CaptureLive()` as a single-run set
  and builds each chart from it (`ShownResults` = the run going on; `StoredSets` read once), drawn by `ChartRenderer`
  (theme `Panel`, text ×round(UI scale)) — the same charts as the Results tab and the PNG. Read-only (reads the
  statistics, schedules nothing); registers its screen rect with the HUD so the camera ignores clicks on it. Charts
  over time need the clock's `seriesInterval`.
- **Element Label** (`Visuals/UnityElementLabel.cs`, 2026-10-09; tool *Visualization ▸ Element
  Label*, opt-in): a `TextMesh` over an element — `text` with `{n}` (the element's content from its statistics,
  `GetStatsContentCurrent`) and `{name}` — or, without an element on or above it, a zone label. `facing` Camera
  (parallel to the view of `SimRecorder.ViewCamera`, upright on screen; was turned to the camera's position, so labels
  below a high camera spun round — 2026-10-12, as `ItemLabelView`) or Floor. `hideItems` switches the renderers of the items the
  element holds off (queues, Multi-Servers, gate queues, lanes): e.g. patients waiting at home shown as a number. A
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
- **Pathway Arrows** (`Visuals/UnityPathwayArrows.cs`, 2026-10-09, healthcare §6; tool *Visualization ▸ Pathway
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
- **Recorder** (`Visuals/SimRecorder.cs`, 2026-10-09; shots as
  data, fly-paths, captions and Unity's encoder the same day; tool *Visualization ▸ Recorder*, opt-in): in Play, drives
  the clock to `startAt` (a model date; the paced loop at about the remaining gap per real second, `SimRecorder.Approaching`
  makes a Speed Profile wait; a date after the run's end, a finished run or 30 s without progress fail the take), then,
  with the paced loop off for the take (the legacy loop follows game time; review2 #9) and the clock's speed and pacing
  given back afterwards (#8), renders its `shots` through its own `DontSave` camera into a `RenderTexture` at `fps` with
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

| Command | Does |
|---|---|
| `simulean_types [type]` | The Library's entries (`SimuLeanWindow.CollectEntries`: elements, model-group tools, presets) and the tools of the window's other tabs (Visualization, Analysis…: labels, pathway arrows, speed profile, recorder, dashboard); with a type, its serialized fields and its config (catalog) keys, from a hidden throwaway instance — of the entry's prefab when it has one, so the values are those the Library places |
| `simulean_params element` | An object's fields (path, type, value; a distribution shows only its active type's fields — follow-up #30) and its catalog keys / values (scene extraction) |
| `simulean_add_element type [name x y z parent]` | Places an entry exactly as the Library does (`SimuLeanWindow.Place`, now returning the object and taking an optional parent); a tool of another tab gets its own object with the component, in its hierarchy group (`ModelHierarchy.Place`; a singleton already in the scene is returned) |
| `simulean_connect from to` | Adds `to` to the Next Elements of `from` |
| `simulean_place_model [model name x y z rotY scale parent]` | A library 3D model as decoration: a model folder (`HospitalBed`), a kit file (`WaitingChairs/Seat_Table`) or an asset path, under a root `Decor` group by default, keeping the import's root rotation / scale; no model = the list of library models (`instantiate_prefab` refuses `.fbx`) |
| `simulean_disconnect from to` / `simulean_remove object` / `simulean_rename object name` | The inverse commands (`Editor/Pipeline/SimuLeanEditCommands.cs`): `to` leaves the Next Elements of `from` (a warning when later outputs move up one index; two transport nodes: their track); an element / team / list / table / tool deleted with its children, every object reference to them in the other SimuLean components cleared and listed (`referencesCleared`; list entries removed, fields set to none; the referencing objects recorded whole first, so the one undo step brings the references back too); a rename checked as a new name (`EnsureFreeName`), the team / list / table name field updated when set. Both warn about texts (expressions, queries, labels) that still name the old object |
| `simulean_set element key value` | A serialized field by name / path (`SerializedObject`: numbers, bools, strings, enums by name, object fields by GameObject name or by asset path — `Assets/…/Doctor.prefab`, the asset or its prefab's component of the field's type —, lists comma-separated, vectors (`x,y,z`, `x,y`), colours) — undoable; a field of an inactive distribution answers a `warning`; `properties.NAME` (the key a study varies) is the element's user property (`SElementLabels`, added when missing; the property created when missing; a number, or else a text with a warning; quotes around the name dropped, from the case-2 v2 run, where the agent wrote the property list through the generic tools) |
| `simulean_add_team name [members travel … parent]` | A resource team with n members |
| `simulean_load_table source file [sheet readAtRun]` | Puts a Source in Schedule mode with a `UnityDataTable` (named after the source, created or reused, in its hierarchy group) reading the file (CSV / `.data` / Excel; absolute or relative to the project or `Assets/`), loaded into the project database; `readAtRun` = read the file at each run start (§4.2, S6); one undo group, reverted when the table cannot be loaded |
| `simulean_model [element parameters root]` | The scene's model in one answer: `ModelDescription.Of` (engine, `Optimization/ModelDescription.cs`) over the extracted `SimulationConfig` — the clock (time unit, horizon, warm-up, start, end condition, seed), every element (type without the `Unity` prefix, `next` in connection order, parameters under the catalog keys a study varies), the other connections (lists, ports), teams (members with labels, catalog parameters), lists, downtimes, reactions, data tables — all by name; `element` = one object, `parameters=false` = the structure only. Read-only |
| `simulean_validate [set metrics root]` | Extract + build without running: `Study.Check` (§3.5) on the scene's model with the optional factors / metrics; `ok`, the counts (elements, connections, teams, scenarios, metrics) and the errors / warnings a run would meet (logged by the extractor and the build too) |
| `simulean_run_headless [maxTime replications seed set metrics root detail]` | A `SceneStudy` (§3.19): `set` = catalog overrides `Owner.key=v1|v2;…` (one scenario per level combination), `metrics` = `name=path;…` — both also as JSON (`FactorsOf` / `MetricsOf`; `{"Buffer.capacity": [2, 4]}` or `[{"path": …, "values": […]}]`, `{"out": "Sink.input"}` or `[{"name": …, "path": …}]`; levels as numbers, texts or booleans, so a value may hold `;` `=` `|`; string arguments, as the client schema of object arguments is broken upstream); returns per scenario the named metrics and the model's statistics, plus the elements' and resources' KPIs with `detail=full` — the default without `metrics`; with `metrics` the default is `detail=metrics` (follow-up #30: 6 full scenarios were 86 KB). The answer opens with the effective `settings` (time unit, horizon run, warm-up, end condition, replications, seeds, confidence); each named metric is `{mean, halfWidth, n}` over the completed runs (the half-width of the `ResultSet.Confidence` interval, null with one value; NaN values — a time not reached — left out of n), the model block and KPIs are means (seeds `seed`, `seed + 1`, …; one thread, deterministic); NaN → null. The scene is not changed; the result set is published to the Results tab, never stored (*Save* there keeps it). **Studies (`Editor/Pipeline/StudyOptions.cs`; also `simulean_run_start`, `simulean_validate`):** `scenarios` = explicit scenarios as JSON (their paths become the factors, own `replications`); `where` = a condition over `set`'s factors (each path with every character that is not a letter or digit as `_`: `Buf1_capacity`) keeping the combinations that meet it, as explicit scenarios (`Study.FactorLabels`); `derived` = `name=expression;…` computed per completed run from the metrics before it (the expression language, §3.18), reported like them; `confidence`; `warmup`; a metric path with `[*]` (`Exit.tally:tis[*]:mean`) is checked unsplit and expanded after the runs into one metric per label value (`name[value]`); `experimenter` = a scene Experimenter's study (`Experimenter.PrepareStudy`: its factors, scenarios, metrics and settings; `maxTime`, `replications` ≠ 1, `seed` ≠ 1 and `metrics` override / add) |
| `simulean_run_start …` / `simulean_run_status [cancel wait]` | The same study in the background: the scene is read on the main thread, the runs go on a worker (all cores) and the call returns at once; the status gives runs done / in all, then the same answer as `simulean_run_headless` (or the error); one run at a time; `cancel` keeps the runs done; status `none` / `running` / `completed` / `failed`. A long study no longer blocks the editor nor times the MCP out. `wait`: see below |
| `simulean_optimize_start [parameters metrics fitness optimizer algorithm algorithmSettings maxEvaluations maxSeconds replications runTime seed threads root]` / `simulean_optimize_status [cancel wait]` | An optimisation in the background (`Editor/Pipeline/SimuLeanOptimizeCommands.cs`): the search given in the call — `parameters` in the factor grammar with a range (`Buffer.capacity=1..10`, `team:Welders.members=1..4`; `Study.ParameterOf`; JSON `[lo, hi]` too) or options (`a|b|c`: `Study.OptionsOf` → `OptionsParameter`, searched over those values only — numbers too; before 2026-10-20 a numeric key given options was searched over the range between them, and a property as a real), a whole-number range of a real key answering a warning that suggests the options (`Study.RangeNote`), `metrics` as `simulean_run_headless` (`Study.MetricOf`; checked on the built model, `Study.Check`), `fitness` maximised (the only metric when omitted) — or a scene `Optimizer`'s setup (`Optimizer.Prepare`, the same `Optimizer.Setup` its inspector button runs), the other arguments overriding its settings; `algorithm` by name (`genetic`, `random` or any `AlgorithmSettings` type, `TypeCache`) with `algorithmSettings` `name=value;…` into its fields. Runs on a worker (`Optimizer.Setup.Run`: the search, then the best re-run for every replication); the status gives the iteration, candidates and best so far, then the best candidate (fitness, values, each metric `{mean, halfWidth, n}`), the stop reason and the failed candidates. The scene is not changed; the result set is published, never stored. One search at a time |
| `simulean_diagnose [time seed set all root]` | One replication of the scene's model run headless (`Editor/Pipeline/SimuLeanDiagnoseCommands.cs` → `Study.Diagnose`) to `time` — or, with 0, with `StopWhenEmpty` until nothing can happen (stall, deadlock, empty; at most the horizon) — and its state (`ModelDiagnosis.Of`, engine): the run monitor's verdict and `waiting` list, each element holding items, stopped or not idle (state, items, what it waits for — `RunMonitor.WaitsFor`; sinks and idle empty elements only with `all`), each team's pending requests (requester, task, count, label, time waiting) and members (state, task, for whom, step, `taskSince`, carried), each list's entries; `progress` (the last item entry / exit of any element, `noMovementFor`, the items in the model, the oldest pending request, the longest task in progress) and `notes` (`ModelDiagnosis.Notes`: facts, no verdict — no item moved, a request waited or a task lasted for a quarter of the run or more while the run monitor sees no stall because events such as shifts or reactions are still scheduled; whether the blocking is the design's or the logic's is the modeller's call, from the case-2 v2 run); `set` = one level per factor (a what-if). Read-only, synchronous |
| `simulean_record_shot [shot framing target distance elevation azimuth orbit lookHeight position rotation pose fromView seconds glide glideSeconds fov title subtitle index remove recorder]` | Adds or changes a shot of the scene's `SimRecorder` (placed when there is none, §4.9): by name, empty arguments keep the shot's values (a single space clears a text); a target makes an element shot; `fromView` takes the Scene view's camera (a fixed pose, or with a target its angle and distance); `index` inserts / moves; without `shot` lists the shots (start, length, camera position). Undoable |
| `simulean_record_start [scene startAt seconds fps width height exitPlay recorder quality bitrateMbps]` / `simulean_record_status [cancel]` | Records a take in Play (`RecorderSession`, §4.9): opens the scene (modified scenes must be saved), the take's settings apply to it only (`seconds` stretches the shots), Play ends with the take; status `starting` / `approaching` / `recording` (frames / in all) / `completed` (the MP4's path) / `failed` / `stopped`; `cancel` ends Play keeping the video so far |
| `simulean_apply operations` | A JSON list of edits in one call (`Editor/Pipeline/SimuLeanBulkCommands.cs`): `add`, `set`, `connect`, `disconnect`, `remove`, `rename`, `add_team`, `invoke`, `repeat`, each run by its command; all or nothing — at the first error every edit before it is undone (`Undo.RevertAllDownToGroup`) and the error names the operation —, one undo step (`Undo.CollapseUndoOperations`); a short result per operation |
| `simulean_repeat group [count offset names connectFrom connectTo]` | Copies a group of objects (with their children) N times, each moved by `offset` and its model objects (elements, teams, lists, tables) renamed by `names` (`{name}_{i}`, checked unique); every object reference inside the group is remapped to the copy's own members (`SerializedObject`, the hierarchies paired object by object), references outside it kept; `connectFrom 'Outside>Member;…'` / `connectTo 'Member>Outside;…'` connect each copy. One undo step |
| `simulean_invoke object [action]` | Runs a parameterless action of the object's components — marked `[SimuLeanAction("…")]` (`Core/SimuLeanLibraryAttributes.cs`: `UnityTravelMatrix.FillStraightLine`, `UnityFloorZone.Rebuild`, `UnityPathwayArrows.Rebuild`, `StationModel.Rebuild`, `UnityWalkGrid.Bake`) or `[ContextMenu]` —, by method name or menu label; no action = the list with descriptions. Undoable |
| `simulean_capture [targets elevation azimuth distance fov shot width height path]` | A PNG of the scene (`Editor/Pipeline/SimuLeanCaptureCommands.cs`): the named objects — or every model element — framed from an elevation and azimuth at the distance that fits their renderers' bounds (`SimRecorder.Around`), or a Recorder shot's framing (`SimRecorder.Framing`); rendered by a temporary hidden camera with the main camera's settings into `Recordings/Captures/` (git-ignored) or `path`; returns the path and the camera. The scene is not changed |
| `simulean_results [action set formats kind y x groupBy title path name]` | Result sets (`Editor/Pipeline/SimuLeanResultsCommands.cs`): `list` (the last published set, `ResultsOutput.Last`, and the stored ones), `read` (the run answer's scenarios and metrics, plus the metadata), `save` (`ResultsOutput.Save`, optionally renamed), `export` (`ResultsOutput.Export`: csv / excel / sqlite / json), `chart` (`ChartBuilder.Build` + `ChartRenderer.Save`, PNG or SVG by the extension, `Recordings/Charts/` by default; `y` / `x` as `metric:NAME`, `stat:SCOPE:OBJECTS:STATISTIC`, `param:PATH`; an empty chart is an error naming the set's metrics). `set` = `last`, a stored id or name |
| `simulean_query [sql import table sheet append maxRows]` | The project database (`Editor/Pipeline/SimuLeanDataCommands.cs`): no argument = its tables and row counts; `sql` = a statement's rows (at most `maxRows`, 200 in all, with the count); `import` = a CSV / Excel file into a table (`TableFiles.Read` + `ProjectDatabase.ImportTable`, replacing it unless `append`) |
| `simulean_fit [values sql file column sheet criterion top into pick]` | Distribution fitting: the data from `values`, a query's or a file's column; `DistributionFitter.FitAll` (AD or KS) ranked, each with its parameters, sampler spec, p-values, AIC and verdict; `into = Element.field` writes the `pick`-th fit into a distribution field as the Fit tab's *Insert* does (undoable) |
| `simulean_test [filter]` / `simulean_test_status [wait]` | `SimuLeanTestRunner` (never the MCP's `run_tests`) |

**Agent documentation.** [`AGENTS.md`](../AGENTS.md) (set-up and rules) and `docs/agents/`: `workflow.md`, `errors.md` and
`commands.md`, generated from the `[CliCommand]` / `[CliArg]` attributes by `Tools/Docs/command_reference.py` (the
`commandreference` step of `run-all.ps1` fails when it is stale).

**Waiting for long jobs.** The `_status` commands of runs, optimisations, tests and takes take
`wait` (seconds, at most 55): `JobWait.Until` (`Editor/Pipeline/JobWait.cs`) answers as soon as the job is no longer
going on (status not `running` / `starting` / `approaching` / `recording`) or when the wait is over. The command returns
a task: the status is read on the main thread at each editor tick (every 0.25 s, `EditorApplication.update`) while the
pipeline server awaits the task off it, so the editor and the job go on; a domain reload ends the wait with the status
then. While a call waits, the server's other commands queue behind it (one command at a time). It replaces the agents'
sleep-and-poll through `eval` (≈ half of a long agent session's calls and tokens). A run
answer also gives, per scenario, `waiting` — who waited for what in the first replication that stalled or deadlocked.

**Answers.** Every command answers a JSON object with its fields
plus **`errors`** and **`warnings`**: `AgentReply.Of` (`Editor/Pipeline/AgentReply.cs`) captures what is logged while
the command runs (`Application.logMessageReceivedThreaded`: the engine's `SimLog` through the editor bridge, the
extractor, the editor; worker threads included), messages only, each once, at most 20 of each; a command that fails
throws as before, its message followed by the warnings logged until then. Lists are fields (`simulean_types` →
`types`). A study is **checked before it runs** (`Study.Check`, §3.5): an error refuses the run with every problem in
the message ("did you mean"), the warnings go into the answer. A run answer is `{settings, scenarios, errors, warnings}` (`Settings(study)`: nothing
the run used is a silent default); each
scenario has `runs {ok, failed}` and `firstError` — a failed run is never averaged in nor reported as a null metric —
and `errors` names each scenario with failed runs. The background run captures the log for its whole length.
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
inspectors in an off-screen window and saves a PNG (through `eval`): interface reviews, where the docked Inspector cannot
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
  `name` labels it in the results (empty = `owner.key`).
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

### 5.3 Removed (R-5d)

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
  bridge; the `unity mcp` server of `.mcp.json`, §4.11) and MCP for Unity (`com.coplaydev.unity-mcp`, git)
  (`Packages/manifest.json`).
- No optimisation library: the algorithms are in the engine (§3.19); GeneticSharp was removed in R-5d (§5.3).
- No numerics library (D1): `Stochastics/Probability/Probability.cs` — ln Γ / Γ (Lanczos), regularized
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
  (`ScheduleSource(name, clock, arrivalsTable)`, R-10e). Excel *export* uses the engine's own `XlsxWriter` (§3.8), no
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

Open limitations to keep in mind (resolve or document as the code evolves); the history of resolved items is in §8.1.

1. **Assemblies.** The engine (`SimuLean.Net`) is its own assembly (the compiler keeps Unity out of the engine); the Unity layer, optimisation and experiments still share `Assembly-CSharp`.
2. **Downtime coverage.** Sources' arrival events and `ItemsQueue` / `StorageSystem` (no timed work) are not paused
   by a stop: sources keep their schedule (their output is blocked), queues / warehouses refuse items in and out.
   The end of a stop calls `Unblock` until it sends nothing, so every waiting item the receivers take leaves (queues,
   Multi-Servers, Combiners); an element's `Unblock` must return true only when an item left. A stop freezes and shifts a resource's current
   leg / step (`TaskExecutor.OnStopped` / `OnResumed`), so its views pause with it. Repair crews (a resource-team requirement for repairs) are not modelled.
3. **Queue pass-through order.** When `ItemsQueue` / `LengthLimitedQueue` forward an item in the same call that
   receives it, the link records the exit before the entry: the content dips to −1 for zero time (time-weighted
   averages are unaffected). The stay time is right: `ElementStatsCollector` pairs the exit with the entry that
   follows at the same instant and records a zero stay (fixed 2026-10-07, R-9).
4. **Compound-exit statistics are opt-in.** `Stacker` and `ErectionDock` (naval) set
   `ElementStatsCollector.CountSubItemsOnExit`; components
   drawn from `CombinerInput` / `SkuStockPort` ports into a composite never leave those ports through a link, so the
   ports' output / content statistics keep counting them. Downstream of a batching element the compound is one item.
5. **`Stacker` scope.** One pick-and-place cycle at a time; positions are not transport resources (no travel time
   between them other than the cycle time); `ByLabelIndex` falls back to first-not-full; with
   `AcceptOversizeOnEmpty = false` an item higher than every limit waits forever (BLOCKED).
6. **`ElementState.SETUP` is reserved for elements:** no element models setup / changeover time yet (a resource shows
   SETUP only for a `TaskSequence.Setup` step, which no built-in element issues).
7. **Config files on disk.** JSON export of a `SimulationConfig` through `JsonUtility` cannot serialize the
   property-based DTOs or the `Dictionary<string, object>` parameter bags (the in-memory pipeline is unaffected); a
   model format is planned.
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
    sample scene (N6) and the naval results — item trace, KPIs, block Gantt (N7) — are not built; the `sample_hull` table is
    written into the project database when it is missing (by the preset and when a scene that reads it is opened).
13. **Resources under stops — design limitations.**
    - *Downtimes cannot use resources (C3).* A downtime's targets are elements (`DowntimeBuilder`); a repair cannot
      request a team member. Work-around: a failure-token loop — a token item cycling through a "failure clock" station
      and a "repair" station processed by the crew, with reactions on `ProcessEnded` that stop / resume the machine.
    - *No pre-emption (C3).* Work already running keeps its resources when a stop begins (§3.17), and an operator's
      breakdown does not interrupt a running process. With an **Immediate** stop the paused machine keeps its operator,
      so a repair needing that operator waits forever — a resource deadlock (since 2026-10-13 reported as one, §3.5);
      model such a failure **AfterCurrent** (the machine finishes its part and releases the operator first).
    - *A station takes its part before it has its operators (C5).* Receiving the item comes first, then
      `WAITING_FOR_RESOURCE` with the item inside; the buffer in front of a station sharing a scarce operator therefore
      stays empty and its statistics say little about the real wait (the wait is in the station's state share).
    - *A generator that never stops scheduling hides a deadlock.* The stall / deadlock check runs at `Idle` (no foreground
      event left); a calendar-time MTBF generator (or a periodic clock signal) always has its next event pending, so a
      model deadlocked beside one is not reported until the horizon (its throughput shows it). Busy-time MTBF and
      timetables run out.

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
