# Element behaviour reference

What each element does, exactly — the points where discrete-event tools differ and a model built from another tool's
habits goes wrong. **Key** = the catalog key a study varies (`Owner.key`); the scene field has the same name unless
stated. Shared mechanics (links, routing, labels, stops, resources) are in [mechanics.md](mechanics.md); what each
statistic means in [results-and-data.md](results-and-data.md). ⚠ marks behaviour that often surprises.

| Element | In one line |
|---|---|
| [Source](#source) | Creates items: by inter-arrival time, or from a table of arrivals |
| [Sink](#sink) | Takes items out of the model (optionally keeps them) |
| [Queue](#queue) | A buffer of N items, oldest first |
| [Gate Queue](#gate-queue) | A buffer that lets items go only when released |
| [Length-Limited Lane / Conveyor](#length-limited-lane-and-conveyor) | Lanes whose capacity is a length |
| [Multi-Server](#multi-server) | N parallel processing slots, no queue in front |
| [Delay](#delay) | Unlimited capacity: every item waits its own time |
| [Combiner](#combiner) | A main item + N components → one assembled item |
| [Assembly Station](#assembly-station) | A job through a network of tasks that draw materials by SKU |
| [Picking Station](#picking-station) | An order container filled from stock by a pick list |
| [Stacker](#stacker) | Builds stacks at N positions, released as one compound item |
| [Storage System / Rack](#storage-system-and-storage-rack) | Lanes of stored units (selective, drive-in, push-back, floor…) |
| [Agenda](#agenda-healthcare) | Healthcare: books every arrival into a slot and releases it then |
| [Erection Dock](#erection-dock-shipbuilding) | Shipbuilding: erects blocks in order on a hull that stays |

## Source

Two modes (`sourceMode`): **InterArrival** and **Schedule** (the scene default).

**InterArrival** — one item per arrival; gaps from `interArrival*` (the scene default is Exponential, mean 5), divided
by `demandFactor`.
- The first arrival is at the **first sampled gap, not at 0**.
- ⚠ **When the next element refuses**, the source keeps that one item, shows BLOCKED and **stops its arrival clock**.
  Arrivals that would have happened meanwhile are not created (no backlog); the next gap is counted from the moment
  the item leaves. This models a blocked supply, not lost demand: to keep every order, put an unlimited
  [Queue](#queue) (or a list) right after the source and read its content.
- There is no maximum number of items: it creates until the run ends.

**Schedule** — one event per table row: columns `Time` (absolute), `Name` (the item type), `Q` (quantity, default 1);
**every other column becomes a label** of the items. The table comes from a data table, a file or an SQL query
(`simulean_load_table`, `tableSource`).
- Rows are processed in table order (not sorted); a row whose time has passed fires at once. `Q` must be a whole
  number (`2.0` counts as 1). `demandFactor` scales `Q`, carrying the fraction from row to row.
- ⚠ **When refused, items go into an unlimited backlog** and the schedule keeps running; the backlog is sent oldest
  first when the next element frees up. New items are offered before the backlog, so with routing by label a new
  item may overtake held ones.

**Both modes** — `assignLabels` sets constant labels on every item; for labels computed by an expression use the
source's `exitLabels` (`name = expression`). Arrivals are **not paused by a stop**: a stopped source cannot hand
items over, so InterArrival freezes and Schedule fills its backlog. A source holds no content and has no stay time.

## Sink

Takes every item (never full). `keepItems` keeps them (a finished-goods area): they stay at rest, the run monitor does
not count them as waiting, and the makespan includes them.
- ⚠ Its `content` is the **cumulative count received** (it never decreases) and its "utilisation" is the share of time
  since the first item arrived. Read `input` (or the metric `Sink.input`) for throughput.
- No stay times are recorded at a sink.

## Queue

A buffer of `capacity` items (default 100), held oldest first. No priority, no LIFO, no sorting by label: **the
receiver chooses** — combine the receiver's input strategy with the queue's release mode.
- An arriving item passes straight through when nothing is waiting and the next element accepts it (stay 0).
- **Release mode** (`releaseMode`): `FIFO` (default) offers only the oldest item, and ⚠ a refused head holds back
  everything behind it (head-of-line blocking); `AnyAccepted` offers held items oldest first (up to `releaseScan`, 0 =
  all) until one is accepted — use it in front of elements that admit only some items (by label, kitting by key).
- New arrivals are accepted up to capacity while the head is blocked.
- ⚠ Its state is PROCESSING whenever it holds items (BLOCKED only once a send is refused), so its "utilisation" means
  "not empty".
- No timed work: a stop only refuses items in (and, if Immediate, out). Under an **off-shift** (AfterCurrent) stop it
  keeps releasing items.
- With a carrier team on its output (`transportBy`), every held item gets its own carrier request, in order.

## Gate Queue

A buffer (`capacity`) that lets items go only when **released**: `Release(n)` adds n credits and sends up to n items
oldest first; unused credit is kept, so releasing 5 on an empty gate lets the next 5 arrivals through.
- ⚠ A release is called from Unity (a script, the Play-mode inspector button): **no reaction action releases a gate**,
  so in a headless run or a study a Gate Queue only fills up.
- ⚠ The default capacity differs: 1 in the scene, 100 when the key is missing from a config.

## Length-Limited Lane and Conveyor

One or more parallel lanes (`laneLengths`) whose capacity is a **length**: an item takes its `length` label (else
`defaultItemLength`, 1) plus `gap` (0.2) to the item before it. `maxItems` (≤ 0 = unlimited) limits the count too.
The **Conveyor** preset is a one-lane Length-Limited Lane with a roller-conveyor look that follows the lane's length.
- An item goes to the first lane (lowest index) where it fits; no balancing between lanes. An item longer than every
  lane is accepted only by an empty lane, and only with `acceptOversize` (default on) — otherwise it is never accepted.
- Items leave in global arrival order (FIFO across lanes); a refused head blocks the whole element.
- ⚠ **No transit time**: an item is available at the exit as soon as it enters. A conveyor here is an accumulation
  buffer limited by length; for the travel time put a [Delay](#delay) or a [Multi-Server](#multi-server) in series.

## Multi-Server

`capacity` parallel slots ("servers"), each processing one item for a time drawn from `delay*` (key prefix `delay`:
`delayStrategyType`, `constantValue`, `uniformMin`…).
- ⚠ **No queue in front**: a full station refuses items, which wait upstream. Put a Queue before it.
- The time is drawn **when processing starts** (after its resources arrive), with an expression it can read the item.
  Each slot draws from its own random stream. Free slots are used in rotation.
- **No setup times** (model a setup as a preceding element, or with resources).
- A finished item that cannot leave stays in its slot (BLOCKED), so the free capacity shrinks. Finished items leave in
  the order they finished (`releaseMode` `AnyAccepted` lets any of them go first to a receiver that accepts it).
- **Stops**: Immediate pauses every slot and resumes with the remaining time (nothing lost or restarted);
  AfterCurrent lets the items in process finish and refuses new ones.
- **Resources** (`processBy`): the item enters the slot first, the slot requests the team (WAITING_FOR_RESOURCE) and
  processing starts when all of them have arrived; they are released at the end of the process (default), when the
  item leaves (`ItemLeaves`, kept while blocked) or when it enters another element (`AtElement`); `HoldFor` lets them
  go after their own time.
- `utilization` is the mean over the slots (one busy slot of three = 1/3); the element's state shows PROCESSING while
  any slot processes.

## Delay

Unlimited capacity: every item that enters waits its own time (`delay*`, drawn on entry), all at once, with no queue —
a treatment at home, drying, transit. Not able to use resources.
- An Immediate stop pauses every item's delay; an AfterCurrent stop lets them run.
- Finished items that cannot leave wait in finishing order.

## Combiner

A **main item** (the job) plus the required number of **components** from each component input → assembled for a
drawn time → the main item leaves (`capacity` = assemblies in parallel).
- ⚠ **Inputs**: with K values in `requirements` the Combiner needs **K + 1 inputs** — input 0 is the main input,
  input k the component input k−1. `requirements = {2}` means one main input and one component input that brings 2
  parts. Which upstream element feeds which input: the order in `inputOrder`, else (in a scene) the Hierarchy order of
  the upstream elements. A list can feed any input (`listInputs`).
- ⚠ **Components are accepted only while an assembly is gathering** (it has its main item): they cannot be staged
  before the main item, they wait upstream. Each goes to the first gathering assembly whose matching admits it.
- **Matching** (`pullModeType`): `Default` (any), `SingleLabel` (numeric label equal to a value; with an empty value,
  the main item's own value — kitting by key), `MultiLabel` (any of several pairs). Numeric labels only.
- `updateRequirements` reads each requirement from a label of the main item; ⚠ a missing label keeps the previous job's
  value.
- **Output**: the same main item (same id and labels). Components are consumed (`batchMode` off) or travel on as
  sub-items (`batchMode` on); their labels are not merged.
- ⚠ The Combiner's input/output count main items only; components are not counted at the Combiner.
- Stops pause the assembly time; the input ports are not stopped with it (components keep arriving).

## Assembly Station

One job at a time through a **network of tasks** (`assemblyTasks`: `id|predecessors|sku:qty,…|priority|duration`) run
by up to `workerCount` at once. A task starts when its predecessors are done **and** its materials are in stock;
materials come by SKU (the item's type) from a shared stock (`stockCapacity`, −1 = unlimited) fed by `StockInput`
connections. The job leaves with all materials as sub-items.
- Stock is kept between jobs and admitted even with no job present. ⚠ A stock smaller than one task's needs deadlocks.
- With resources, one request per task.

## Picking Station

An order container arrives on the main input; when **every** line of its pick list is in stock, the items are picked
into it (no partial picks) for the drawn time, and it leaves with them as sub-items. The pick list: a table
(`pickTable`, matched to the container by `keyColumn` / `containerKeyLabel`), labels `pick_<SKU>` on the container, or
a static list — the first non-empty one. The SKU is the item's type.
- ⚠ An empty pick list: the container passes unpicked, still taking the process time.
- ⚠ When each container leaves, the remaining stock goes to the returns output, or is **discarded** if there is none —
  stock does not persist between orders.

## Stacker

Builds stacks at `positions` positions and releases each as **one new compound item** (type `stackType`). One pick
point, one cycle at a time (`cycleTime`, or a crane team).
- Position choice (`selectionRule`): FirstNotFull, RoundRobin, ByLabelIndex, ByKey (one open stack per key).
- A stack is released when full (`capacity`), when the next item would exceed `maxHeight`, when forced, or when the
  waiting item cannot be placed (the blocking or oldest open stack closes, possibly partial).
- ⚠ No time-out release: open stacks stay at the end of a run.
- The new item copies the first item's labels and adds `stackCount`, `stackHeight`, `stackKey`, `stackReason`…;
  downstream counts it as 1 while the stacker counts the items.

## Storage System and Storage Rack

Lanes of stored units with an access rule (`Direct`, `Lifo`, `Fifo`) and a SKU rule (`SingleSku`, `Mixed`); the rack
presets (conventional, drive-in, push-back, floor) set them. Put-away is instantaneous, by the location policy.
- ⚠ **Retrieval is only programmatic** (`RequestRetrieval` from code). No element, list or reaction retrieves, so in a
  flow model a storage is an end store: items go in and stay.
- Retrieval (from code) relocates the units in front, timed by the storage's transport team when lanes have nodes.

## Agenda (healthcare)

Books every arriving patient into the first free **slot** at least `leadDays` ahead (`sessions`, `slotMinutes`,
reserved slots for priority patients, overbooking) and releases them at the slot time.
- ⚠ It never refuses an arrival, and booking ignores the next room's capacity.
- Priority comes from the **label** `priorityLabel`, not from the item's priority field.
- Labels set: `appt_time`, `lead_time`, `no_show`, `rescheduled`. ⚠ A no-show patient still leaves the agenda,
  labelled `no_show = 1`: route on it.
- The release is at an absolute time: a stop does not delay it.

## Erection Dock (shipbuilding)

Erects blocks on a hull that stays in the dock (`capacity` erection fronts), by sequence (`erect_seq`) or by
precedence (`erect_after`); when `expectedBlocks` are erected a new `Ship` item leaves with the blocks.
- ⚠ A block needs a numeric `block` label; one that is not erectable yet is refused and stays upstream — feed the dock
  through a queue with `AnyAccepted` or a list, never a FIFO queue.
- Erected blocks are at rest (not waiting) and count in the makespan.
