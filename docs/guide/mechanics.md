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

Key `outputStrategy.strategyType` on the sending element:

| Strategy | Chooses | ⚠ |
|---|---|---|
| `FirstAvailable` (default) | the first next element, in connection order, that accepts now | the order of the connections matters |
| `RoundRobin` | the next one after the last chosen, among those that accept | skips busy ones (not a strict cycle) |
| `ShortestQueue` | the lowest content among those that accept | ties in turn: the first tied output after the one chosen last (connection order) |
| `MostAvailableCapacity` | the most free capacity | a sink (unlimited) ranks last |
| `Probability` | output i with weight w_i (`outputStrategy.weights` = `6|3|1`, in connection order) | drawn once per item and **kept**: the item waits for that output, never redrawn |
| `Expression` | the output index (0-based) computed by `outputStrategy.routingExpression`, e.g. `item.type == 'A' ? 0 : 1` | the item waits if that output is busy; out of range = never sent |
| `PriorityBased` | first available for items with priority > 0, else shortest queue | |
| `BestScore` | the next element with the highest `outputStrategy.scoreExpression`, evaluated once per next element (`out.x` = that element, `item.x` = the item), e.g. `out.speed - out.content` or `count(out.name, 'family', item.family)` | ties go to the first in connection order; `whenBestFull = Skip` (default) scores only those with room, `Wait` waits for the best; no random functions |

Routing **by label**: an `Expression` on the label, or an input strategy on each receiver with `FirstAvailable`.

## Admission (input strategies)

Key `inputStrategy.strategyType` on the receiving element: `Default` (everything), `SingleLabel` (a label equal to a
value: a number a numeric label, a text a text label), `MultiLabel` (any of several label / value records), `Condition` (an expression over the item and the element).
Capacity is checked apart. ⚠ A Combiner uses its pull mode instead ([elements.md](elements.md#combiner)).
- A refused item is offered again when the receiver frees a place, ends a stop or is released, **and when an element
  property the condition reads is written** (`element.open == 1`, `get('Gate', 'open') == 1`; a reaction's *Set Element
  Property*): at the same time, after the reaction's actions. ⚠ A condition over anything else — `content('Buffer') < 5`,
  `now()`, a table — is not evaluated again by itself: a reaction's **Retry** action offers the receiver again (on
  *Item Exited* of the buffer, on a clock signal).

## Labels

- An item carries **labels** (numbers or texts). A Schedule source turns its table's extra columns into labels;
  `createLabels` sets them as a source creates the item, `entryLabels` / `exitLabels` as items enter or leave an
  element — all `name = expression` records, in order (text constants quoted: `sku = '007'`). Labels are undone if the receiver refuses the item.
- ⚠ Routing and admission of a hand-over are decided **before** that hand-over's labels are applied; the receiver's
  process time and its own routing see them.
- Expressions read `item.<label>` and the built-ins `item.id`, `type`, `priority`, `created` (when the source created
  the item), `age` (now − `created`: since creation, not since entering this element); a label named like a built-in
  hides it. Language: [`Assets/Docs/Expressions.md`](../../Assets/Docs/Expressions.md).
- Compound items (assemblies, stacks, picked orders) carry their parts as sub-items; ⚠ no expression reads the parts,
  and no element splits a compound.

## The end of a run

- `horizon` (or the clock's) always ends the run; the statistics include the last interval.
- **End condition** `StopAt` + `StopAtCount`: the run stops when the named elements (any, not only sinks) have received
  the count between them. The count starts with the observed period: the warm-up resets it, as it resets the statistics.
- `StopWhenEmpty`: the run ends when nothing can happen any more — **empty** (no items), **finished** (items only at
  rest: in end buffers, kept, or surplus), **stalled** (items waiting, nothing able to move them) or **deadlock**
  (stalled with an element blocked or waiting for a resource). ⚠ A source with an endless arrival process always has
  a next event, so its model never "ends" before the horizon. A night off shift is not a stall: the monitor waits for
  the next shift before it judges. Failures in calendar time and periodic / timetable stops do not hide a stall: the
  check runs beside them.
- `StopWhenNoProgressFor` (the clock's *Stop When No Progress For*, model time, 0 = off): the run ends with the reason
  **NoProgress** when no item has entered or left any element for that time while items wait — the way to catch a stall
  beside an `Every` reaction or another periodic signal, which keep the run from ever becoming idle. Choose it longer
  than the longest process, stop or break; a night off shift is never taken for it. Studies: `noProgress=T`;
  `simulean_diagnose noProgress=T`.
- **Warm-up** (`warmup`): at that time the statistics restart (counts, stays, tallies, states, resources, lists,
  generators, reactions); items, work and stops stay as they are. An item that entered before and is recorded after it
  keeps its full `age`.

## Downtime and calendars

Stops come from **downtimes** (generators), **shifts** and **reactions**. Details:
[`Assets/Docs/Downtime_And_Calendar.md`](../../Assets/Docs/Downtime_And_Calendar.md).

- **Immediate** stop: the element's timed work **pauses** and later resumes with its remaining time (the item stays,
  nothing is lost or restarted); input and output are blocked.
- **AfterCurrent** stop: the work in progress finishes; new items are refused; output stays open. ⚠ The stop shows
  when no timed work is left — a queue shows it at once while still holding items and **keeps releasing them**. The
  stop is **delayed, not cut**: a timetable row, a periodic stop, a repair and a reaction's stop for a time last their
  whole duration from the end of the job in hand (a row 30 + 20 on a job 0..100 stops 100..120); a Shift's stop still
  ends at the next window (shorter by the overrun). Rows that wait for the same job overlap unless the timetable
  serialises them.
- Generators: **MTBF/MTTR** (default BREAKDOWN, Immediate; time to failure in calendar or busy time), **Timetable**
  (STOPPED, Immediate; rows of start / duration, with an overlap policy), **Shift** (OFF_SHIFT, AfterCurrent, with
  holidays), **Periodic** (SCHEDULED_DOWN, Immediate; first, period, duration) — a Downtime's empty state and *Kind
  Default* mode take these. Stops from several sources stack: the element runs again when all have ended.
- ⚠ Arrivals are not paused by stops (a stopped source simply cannot hand items over). An Agenda's releases are at
  fixed times too.
- A downtime's *Repair by* sends a crew (a team, its requirements, a priority and a preemption mode): the element is
  WAITING_FOR_REPAIR until the whole crew is there, then the repair time runs (BREAKDOWN) and the element resumes.
- Preemption is opt-in, per resource use (*Preemption*: None / Resume / Restart / Abort; priority default 1, the larger
  wins): a request may take a member from work of strictly lower priority, and a member's Immediate stop interrupts only
  work whose use asked for it (the team's *On Stop*: hand it over, or wait for the same member). Without it, a stop does
  not take resources away from paused work and an operator's breakdown does not interrupt a process.
- What can be taken (every element that uses resources): a member walking to its station; working during the process
  (only that job waits — one slot, one assembly task, one dock front, the stacker's cycle, the sequencing step — then
  goes on with its remaining time, Resume, or starts again, Restart); working its own time (*Own Time*: another member
  finishes it); holding an item after the process (*Item Leaves*: another member replaces it). Carriers only empty, on
  their way to the pick-up: **a loaded carrier always delivers**. Several carriers moving one load gather at the pick-up:
  one taken there is replaced while the others wait (HELD); once loaded none is taken, and a stop of one pauses them all.
  Abort takes only work without an item (never a station's job or a carried load). A milk-run train is never taken.
- ⚠ An Immediate stop whose repair needs the operator the stopped station holds is a deadlock — use AfterCurrent, or
  give the repair a preemption mode and a higher priority than the station's use.
- `downtime_total` counts the time actually stopped within the observed period (an AfterCurrent stop from the end of
  the job in hand; a stop still running at the end up to the end) — the target's time in the stop's state;
  `downtime_scheduled` the stop time as scheduled (each stop's planned duration).

## Resources

A **team** (`UnityResourceTeam`) has **members** (task executors: operators, robots, vehicles, cranes) and a dispatcher.

- **Process resources** (`processBy` on a station): the item enters, the station requests the team, and processing
  starts when every required member has arrived. **Release**: at the end of the process (default), when the item
  leaves (kept while blocked), when it enters another element (`ReleaseAt`), or after their own time (`HoldFor`).
- **Setups** (the *Setup* section of a Multi-Server, Combiner or Picking Station): a changeover before the process —
  *When* None (default), Each Item, or Label Change (only when the item's label, e.g. `type`, differs from the previous
  item's **at the same slot**; the first item of a slot pays one with *First Item*, the default). *Setup Time* is any
  distribution (its own random stream). *Setup By* a team: its members come, the station and they are SETUP, then they
  are free and the process's resources are asked; no team = the machine alone (even when the process needs an
  operator); *Keep For Process* with Setup By = the Process team: the process's operators come once, set up and process.
  A stop pauses a setup. Results: `setups`, `setup_time`, `setup_time_avg`, `state:SETUP` (utilisation counts only
  PROCESSING).
- **Transport resources** (`transportBy` on the sending element): every item gets its own carrier — travel empty to the
  origin, load, travel, unload, deliver. The destination is chosen among those with room, counting items on their way.
  A requirement of several carriers (count > 1) makes them gather at the origin, then move the load together.
- **Requests** are all-or-nothing (every requirement at once) and served in `RequestOrder` (default first come; e.g.
  `|priority desc`); members are chosen by `ResourceOrder` (default longest idle; `|distance` = nearest). A
  requirement can filter by a member's properties (skills): `count#query#label`.
- Travel models: **straight** (the default of a team in a scene: straight lines between the elements at the team speed,
  no obstacles; each element without a location gets a point in front of it, the team starts at its own position),
  abstract (instant, no positions: a logic model), network (+ traffic), travel matrix, walk grid (round obstacles), axes,
  physics (Unity, calibrated for headless runs). Every team is seen in Play; an abstract one stands still.
- An idle member stays where its last task ended, unless the team's **Return Home** (`ReturnHome`) is on: then after
  each task it walks back to the home node and is free only there (a waiting point; the walk is busy time).
- ⚠ A member's utilisation counts held and waiting time as busy; an operator held by a stopped station stays busy.
- ⚠ A station takes its part first, then waits for its operator: with a scarce shared operator the wait shows as the
  station's **WAITING_FOR_RESOURCE** share (`state:WAITING_FOR_RESOURCE`, with the part inside), not as a queue in the
  buffer in front of it — that buffer stays short and its statistics say little about the real wait. With *Transport by*
  the part waits upstream until it is loaded. (By design: architecture §8 item 13.)

## Lists

A **list** (`SimList`) holds entries — items advertised by the element that holds them, or values pushed by reactions —
and serves **pulls** by query (`field op value;…|order[ desc]`).

- An element that **pushes to a list** keeps its item (shown BLOCKED) until a puller takes it; a puller takes the best
  entry it can accept now, or waits for one (a back-order served as soon as a matching entry arrives).
- Fields: `age`, `pushTime`, the item's labels, calculated fields; `item.x` (`item.type`, `item.priority`…) and `origin.x` (`origin.name`…).
- ⚠ Values pushed by reactions are never pulled into elements: they are tokens (orders, counters) for reactions and
  statistics.
- An element that pulls from a list must not also have ordinary input connections.

## Signals and reactions

A **reaction** listens to a **signal** — of elements (`ItemEntered`, `ItemExited`, `ProcessEnded`, `StateChanged`,
`Stopped`, `Resumed`) or of the clock (`RunStarted`, `Every`, `AtTime`) — and, if its condition holds, runs its
**actions**: `SetItemLabel`, `SetElementProperty`, `Stop` (for a time, or until a `Resume`; also members of a team),
`Resume` (only its group's stops), `PushToList`, `Release` (a Queue released on command), `Retry`, `SetItemLook` (the item's
colour / scale in the 3D view only — results unchanged). Each action can have its own *only if*.

- Delivery `NextEvent` (default: right after the step that raised the signal; the condition is evaluated then) or
  `Immediate` (inside it). Reactions on one signal run in build (Hierarchy) order.
- ⚠ An `Every` reaction is a pending event, so a stalled model is not detected until the horizon — unless the end
  condition `StopWhenNoProgressFor` is set (see *The end of a run*).
- Guide: [`Assets/Docs/Signals_And_Reactions.md`](../../Assets/Docs/Signals_And_Reactions.md).

## Random numbers and reproducibility

- Every random draw comes from a stream of its own, keyed by **the element's name and the purpose** (`Srv/delay`,
  `Src/interArrival`, `Srv/expr`, `<downtime>:<target>`…), derived from the model seed. Adding or removing other
  elements does not change an element's draws; ⚠ **renaming it does**.
- The same seed gives the same replication. Replications use the seeds `seed`, `seed + 1`, …; scenarios of a study use
  the same seeds (common random numbers).
- All random functions in one element's expressions share its `expr` stream: adding one shifts the others.
- An unseeded run draws a fresh seed and records it (`RunInfo.Seed`).
