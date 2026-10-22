# How a SimuLean model runs

The mechanics every element shares: how items move, how they are routed and admitted, labels, the end of a run,
downtime and calendars, resources, lists, reactions and random numbers. Per-element details:
[elements.md](elements.md). ⚠ marks behaviour that often surprises.

## Items move by push, and wait by pull

- An element that finishes with an item **offers** it to its next elements (its connections, in order). The **output
  strategy** picks one; the receiver accepts or refuses (capacity, its input strategy, a stop).
- A refused item **stays where it is** and the element shows BLOCKED. Nothing retries on a timer: when a receiver gets
  room it **notifies** the elements that feed it, which offer again. Several feeders are served in turn (round robin,
  starting after the last one that sent).
- An element hands over one item per notification, so a long chain moves item by item, all at the same instant.
- **Events at the same time run in the order they were scheduled**; the model's elements start in Hierarchy order, so
  simultaneous first events follow it (moving an object in the Hierarchy may change seeded results; renaming does not).

## Routing (output strategies)

Key `outputStrategyType` on the sending element:

| Strategy | Chooses | ⚠ |
|---|---|---|
| `FirstAvailable` (default) | the first next element, in connection order, that accepts now | the order of the connections matters |
| `RoundRobin` | the next one after the last chosen, among those that accept | skips busy ones (not a strict cycle) |
| `ShortestQueue` | the lowest content among those that accept | ties go to the first |
| `MostAvailableCapacity` | the most free capacity | a sink (unlimited) ranks last |
| `Probability` | output i with weight w_i (`outputStrategy_weights` = `6|3|1`, in connection order) | drawn once per item and **kept**: the item waits for that output, never redrawn |
| `Expression` | the output index (0-based) computed by `outputStrategy_expression`, e.g. `item.type == 'A' ? 0 : 1` | the item waits if that output is busy; out of range = never sent |
| `PriorityBased` | first available for items with priority > 0, else shortest queue | |

Routing **by label**: an `Expression` on the label, or an input strategy on each receiver with `FirstAvailable`.

## Admission (input strategies)

Key `inputStrategyType` on the receiving element: `Default` (everything), `SingleLabel` (a numeric label equal to a
value), `MultiLabel` (any of several label = value pairs), `Condition` (an expression over the item and the element).
Capacity is checked apart. ⚠ A Combiner uses its pull mode instead ([elements.md](elements.md#combiner)).

## Labels

- An item carries **labels** (numbers or texts). A Schedule source turns its table's extra columns into labels;
  `assignLabels` sets constants; `entryLabels` / `exitLabels` (`name = expression`, in order) set them as items enter
  or leave an element. Labels are undone if the receiver refuses the item.
- ⚠ Routing and admission of a hand-over are decided **before** that hand-over's labels are applied; the receiver's
  process time and its own routing see them.
- Expressions read `item.<label>` and the built-ins `item.id`, `type`, `priority`, `created`, `age`; a label named like a
  built-in hides it. Language: [`Assets/Docs/Expressions.md`](../../Assets/Docs/Expressions.md).
- Compound items (assemblies, stacks, picked orders) carry their parts as sub-items; ⚠ no expression reads the parts,
  and no element splits a compound.

## The end of a run

- `maxTime` (or the clock's horizon) always ends the run; the statistics include the last interval.
- **End condition** `StopAt` + `StopAtCount`: the run stops when the named elements (any, not only sinks) have received
  the count between them. ⚠ The count starts at time 0 (the warm-up does not reset it).
- `StopWhenEmpty`: the run ends when nothing can happen any more — **empty** (no items), **finished** (items only at
  rest: in end buffers, kept, or surplus), **stalled** (items waiting, nothing able to move them) or **deadlock**
  (stalled with an element blocked or waiting for a resource). ⚠ A source with an endless arrival process always has
  a next event, so its model never "ends" before the horizon.
- **Warm-up** (`warmup`): at that time the statistics restart (counts, stays, states, resources, lists, generators,
  reactions); items, work and stops stay as they are.

## Downtime and calendars

Stops come from **downtimes** (generators), **shifts** and **reactions**. Details:
[`Assets/Docs/Downtime_And_Calendar.md`](../../Assets/Docs/Downtime_And_Calendar.md).

- **Immediate** stop: the element's timed work **pauses** and later resumes with its remaining time (the item stays,
  nothing is lost or restarted); input and output are blocked.
- **AfterCurrent** stop: the work in progress finishes; new items are refused; output stays open. ⚠ The stop shows
  when no timed work is left — a queue shows it at once while still holding items and **keeps releasing them**.
- Generators: **MTBF/MTTR** (default BREAKDOWN, Immediate; time to failure in calendar or busy time), **Timetable**
  (rows of start / duration, with an overlap policy), **Shift** (OFF_SHIFT, AfterCurrent, with holidays), **Periodic**
  (first, period, duration). Stops from several sources stack: the element runs again when all have ended.
- ⚠ Arrivals are not paused by stops (a stopped source simply cannot hand items over). An Agenda's releases are at
  fixed times too.
- ⚠ Limitations: no pre-emption (a stop does not take resources away from paused work; an operator's breakdown does not
  interrupt a process); a downtime cannot request a repair crew (model it with reactions); an Immediate stop whose
  repair needs the operator the stopped station holds is a deadlock — use AfterCurrent.
- `downtime_total` counts the stops that ended within the run.

## Resources

A **team** (`UnityResourceTeam`) has **members** (task executors: operators, robots, vehicles, cranes) and a dispatcher.

- **Process resources** (`processBy` on a station): the item enters, the station requests the team, and processing
  starts when every required member has arrived. **Release**: at the end of the process (default), when the item
  leaves (kept while blocked), when it enters another element (`ReleaseAt`), or after their own time (`HoldFor`).
- **Transport resources** (`transportBy` on the sending element): every item gets its own carrier — travel empty to the
  origin, load, travel, unload, deliver. The destination is chosen among those with room, counting items on their way.
- **Requests** are all-or-nothing (every requirement at once) and served in `RequestOrder` (default first come; e.g.
  `|priority desc`); members are chosen by `ResourceOrder` (default longest idle; `|distance` = nearest). A
  requirement can filter by a member's labels (skills): `count#query#label`.
- Travel models: abstract (instant), network (+ traffic), travel matrix, walk grid, axes, physics (Unity, calibrated
  for headless runs).
- ⚠ An idle member stays where its last task ended (it does not return home).
- ⚠ A member's utilisation counts held and waiting time as busy; an operator held by a stopped station stays busy.

## Lists

A **list** (`SimList`) holds entries — items advertised by the element that holds them, or values pushed by reactions —
and serves **pulls** by query (`field op value;…|order[ desc]`).

- An element that **pushes to a list** keeps its item (shown BLOCKED) until a puller takes it; a puller takes the best
  entry it can accept now, or waits for one (a back-order served as soon as a matching entry arrives).
- Fields: `age`, `pushTime`, `itemPriority`, `owner`, `type`, the item's labels, calculated fields.
- ⚠ Values pushed by reactions are never pulled into elements: they are tokens (orders, counters) for reactions and
  statistics.
- An element that pulls from a list must not also have ordinary input connections.

## Signals and reactions

A **reaction** listens to a **signal** — of elements (`ItemEntered`, `ItemExited`, `ProcessEnded`, `StateChanged`,
`Stopped`, `Resumed`) or of the clock (`RunStarted`, `Every`, `AtTime`) — and, if its condition holds, runs its
**actions**: `SetItemLabel`, `SetElementProperty`, `Stop` (for a time, or until a `Resume`; also members of a team),
`Resume` (only its group's stops), `PushToList`. Each action can have its own *only if*.

- Delivery `NextEvent` (default: right after the step that raised the signal; the condition is evaluated then) or
  `Immediate` (inside it). Reactions on one signal run in build (Hierarchy) order.
- ⚠ An `Every` reaction is a pending event, so a stalled model is not detected until the horizon.
- Guide: [`Assets/Docs/Signals_And_Reactions.md`](../../Assets/Docs/Signals_And_Reactions.md).

## Random numbers and reproducibility

- Every random draw comes from a stream of its own, keyed by **the element's name and the purpose** (`Srv/delay`,
  `Src/interArrival`, `Srv/expr`, `<downtime>:<target>`…), derived from the model seed. Adding or removing other
  elements does not change an element's draws; ⚠ **renaming it does**.
- The same seed gives the same replication. Replications use the seeds `seed`, `seed + 1`, …; scenarios of a study use
  the same seeds (common random numbers).
- All random functions in one element's expressions share its `expr` stream: adding one shifts the others.
- An unseeded run draws a fresh seed and records it (`RunInfo.Seed`).
