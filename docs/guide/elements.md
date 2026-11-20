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

Two modes (`mode`): **InterArrival** and **Schedule** (the scene default).

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
  first when the next element frees up, and at each arrival before the new items, which queue behind it (no item
  overtakes a held one). Pushing to a list, every item is advertised and the pullers choose.

**Both modes** — `createLabels` sets labels on every item as it is created, `name = expression` records like every
label (`prio = 5`, `sku = 'A-07'` — text in quotes, `mix = pick(0.7, 'std', 0.3, 'rush')`, `due = now() + 480`,
`serial = id`); a Schedule table's column of the same name overrides them. **Item size**: with `sizeFromModel` (default on) the items get the size
of the `itemPrefab` (labels `width`, `height`, `length`, metres) where their labels or table columns set none — a
label always wins; headless the size travels as `modelWidth` / `modelHeight` / `modelLength`. **A stopped source** (a downtime on it,
`arrivalsOnStop`): **Hold** (the default) — it cannot hand items over, so InterArrival keeps the one item that came
and stops its clock, and Schedule keeps its timetable and fills its backlog (the rows leave together when the stop ends);
**Pause** — no arrivals during the stop, the time to the next one (and a Schedule's whole remaining timetable) is shifted
by the stop: nothing piles up (a supplier that does not work); **Lose** — the arrivals come and are dropped, counted in
`lost_arrivals` (customers who find the door closed). A source holds no content and has no stay time.

## Sink

Takes every item (never full). `keepItems` keeps them (a finished-goods area): they stay at rest, the run monitor does
not count them as waiting, and the makespan includes them.
- ⚠ Its `content` is the **cumulative count received** (it never decreases) and its "utilisation" is the share of time
  since the first item arrived. Read `input` (or the metric `Sink.input`) for throughput.
- No stay times are recorded at a sink.

## Queue

A buffer of `capacity` items (default 100), held oldest first. **The receiver chooses** what it takes — combine the
receiver's input strategy with the queue's release mode — and the queue chooses what it offers first (`offerSort`).
- An arriving item passes straight through when nothing is waiting and the next element accepts it (stay 0).
- **Offer order** (`offerOrder`): `FIFO` (default) offers only the oldest item, and ⚠ a refused head holds back
  everything behind it (head-of-line blocking); `AnyAccepted` offers held items oldest first (up to `offerScan`, 0 =
  all) until one is accepted — use it in front of elements that admit only some items (by label, kitting by key).
- **Offer by** (`offerSort`, empty = oldest first): order keys `key [desc], key …` — a label, a built-in (`priority`,
  `age`, `created`…) or an expression over the item and the queue (`item.prio * 10 + age desc`,
  `count('Kits', 'order', item.order) desc`): the best is offered first, ties oldest first. With `FIFO` only the best is
  offered (a new best arrival is offered at once); with `AnyAccepted` they are offered best first. Keys are read live.
- New arrivals are accepted up to capacity while the head is blocked.
- Its state: IDLE when empty; **BLOCKED** while it holds items none of which can leave now (no destination could take
  the head — any of the first items with `AnyAccepted`); PROCESSING otherwise. Items waiting in a list for a puller are
  not blocked. Its "utilisation" is the share of time **not empty** (PROCESSING + BLOCKED), as 100 % − empty in a
  queue's state chart.
- No timed work: a stop only refuses items in (and, if Immediate, out). Under an **off-shift** (AfterCurrent) stop it
  keeps releasing items.
- With a carrier team on its output (`transportBy`), every held item gets its own carrier request, in order.
- **Release on command** (`gated`; it was the Gate Queue element): items leave only when **released** — a
  release of n lets the next n items go, oldest first; unused releases are kept, so releasing 5 on an empty queue lets
  the next 5 arrivals through. A reaction's **Release** action releases it (how many: its value, empty = 1): orders
  every period, a kanban that releases one per item leaving the station; also the Release button of the inspector in
  Play. Items waiting for a release are not BLOCKED. (A gate a reaction opens by a property needs no queue: the write
  offers the refused items again — [recipes.md](recipes.md#routing-and-priorities): *Open a gate by a reaction*.)

## Length-Limited Lane and Conveyor

One or more parallel lanes (`laneLengths`) whose capacity is a **length**: an item takes its `length` label (else
`defaultItemLength`, 1) plus `gap` (0.2) to the item before it. `maxItems` (≤ 0 = unlimited) limits the count too.
⚠ Items from a Source with `sizeFromModel` (default on) carry their model's `length` (e.g. 0.3 m), so
`defaultItemLength` applies only to items without one: for a fixed length per item set `sizeFromModel = false` on the
Source, or a `length` label.
In the Library it is placed through two presets: **Conveyor** — one lane with a speed and a roller-conveyor look that
follows the lane's length — and **Accumulation Lane** — one lane without speed (trucks at a gate, blocks on a quay,
pallets of several lengths), its outline painted on the floor.
- An item goes to the first lane (lowest index) where it fits; no balancing between lanes. An item longer than every
  lane is accepted only by an empty lane, and only with `acceptOversize` (default on) — otherwise it is never accepted.
- Items leave in global arrival order (FIFO across lanes); a refused head blocks the whole element.
- **`speed`** (length per time unit; default 0): with 0 an item is at the exit as soon as it enters (an accumulation
  buffer); with a speed it is a **conveyor** — each item takes its lane's length ÷ speed to reach the end, then leaves
  from the front of its lane (items accumulate behind a refused front; lanes leave in order of arrival at their end; no
  item passes straight through). A stop pauses the conveying. Set it on the Conveyor preset for a moving conveyor.

## Multi-Server

`capacity` parallel slots ("servers"), each processing one item for a time drawn from `delayStrategy` (keys = the field paths:
`delayStrategy.strategyType`, `delayStrategy.constantValue`, `delayStrategy.uniformMin`…).
- ⚠ **No queue in front**: a full station refuses items, which wait upstream. Put a Queue before it.
- The time is drawn **when processing starts** (after its resources arrive), with an expression it can read the item.
  Each slot draws from its own random stream. Free slots are used in rotation.
- **Setups** (`setup`, keys `setup.when` / `setup.label` / `setup.setupFirst` / `setup.time` / `setup.setupBy.team`… / `setup.keepForProcess`): before
  each item (Each Item) or when the item's label changes **at that slot** (Label Change; the slot's first item too by
  default); any time distribution; by the machine alone, a team of its own, or the process's operators kept for the
  process (see Mechanics ▸ Resources). The slot is SETUP meanwhile; the Combiner and the Picking Station set up the same way.
- A finished item that cannot leave stays in its slot (BLOCKED), so the free capacity shrinks. Finished items leave in
  the order they finished (`offerOrder` `AnyAccepted` lets any of them go first to a receiver that accepts it).
  ⚠ Connected to itself (a repeat loop) a full station deadlocks: the item keeps its slot while offered back — send it to
  the input Queue or through a zero-time Delay ([recipes.md](recipes.md#routing-and-priorities): *Repeat a test*).
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
- **Matching** (`pullModeType`): `Default` (any), `SingleLabel` (a label equal to a value; with an empty value,
  the main item's own value — kitting by key), `MultiLabel` (any of several pairs). A number matches a numeric label,
  a text a text label (`sku = 'Garments'`).
- `updateRequirements` reads each requirement from a label of the main item; a main item without the label needs the
  configured number. The default `requirements` is `{2}` (scene and config).
- **Output**: the same main item (same id and labels). Components are consumed (`batchMode` off) or travel on as
  sub-items (`batchMode` on); their labels are not merged.
- **Load height** (`heightLabel`, default `height`; empty = off): the main item leaves with its own height plus the
  layers of components on it, each layer as high as its highest part — `partsPerLayer` parts a layer in arrival order,
  or (0) laid out on the main item's `length` × `width` as the view packs them. Set only when every component has a
  `height` (and, by footprint, the main item and the parts a `length` and a `width`) — e.g. a palletizer's pallet, so
  a storage with physical dimensions can check it. A walled container (a tote, a roll cage: label `loadFloor`, set from
  its model) takes the parts **inside**: it keeps its own height unless its contents stand out of it.
- ⚠ The Combiner's input/output count main items only; components are not counted at the Combiner.
- Stops pause the assembly time; a stop that refuses items also closes the component inputs (components wait
  upstream until the resume).
- **View**: `componentEntries` — where each component input's parts wait (a Transform per input, they pile up on it);
  with `visualMode` *Attach* the components are packed on the main item (a pallet or tote: on its load) and, with
  `animateAssembly` (default), travel onto it one after another during the process. `componentLayout` *OnLoad*: each
  component goes straight to its place in that load as it arrives, so the load grows at the station (a palletizer, a
  tote packer; the library's Palletizer and Tote Packer use it); before the main item arrives they wait behind it.
  Example scene:
  `Assets/Scenes/LibraryExamples/Assembly_Kitting.unity`.

## Assembly Station

One job at a time through a **network of tasks** (`tasks`: `id|predecessors|sku:qty,…|priority|duration`) run
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
- Three ways to model who stacks: nobody in particular (no cycle time: the items fall on their stack); an **automatic
  stacker with its own gantry** — the *Stacker with Gantry* variant, the stacker at the end of a cutting line, its cycle
  time animated by the gantry; or **the hall crane**, which also serves other stations — the Stacker's *Crane* mode
  with a crane team (it travels, picks and places each item and other stations compete for it).
- To sort items by type into separate buffers without making stacks, route them to queues instead (no Stacker).
- Position choice (`selectionRule`): FirstNotFull, RoundRobin, ByLabelIndex, ByKey (one open stack per key).
- A stack is released when full (`capacity`), when the next item would exceed `maxHeight`, when forced, or when the
  waiting item cannot be placed (the blocking or oldest open stack closes, possibly partial).
- ⚠ No time-out release: open stacks stay at the end of a run.
- The new item copies the first item's labels and adds `stackCount`, `stackHeight`, `stackKey`, `stackReason`…, and its
  own height in `heightLabel` when known; downstream counts it as 1 while the stacker counts the items.

## Separator

The inverse of the Combiner. Holds `capacity` items; after `processTime` (optional) each one
leaves:
- **Unpack** — as its contents: a container (a pallet, a tote, what a Combiner in batch mode made) whose contents are
  its sub-items. They leave together in `unpackOrder` (FIFO = the order they were put in, LIFO = the top of a stack),
  then the empty container: to `emptiesDestination` when set (keeping its type and labels), else through the next
  elements after its contents. The empty container frees its place as soon as it has gone to the `empties` port (it
  waits there outside the capacity) — as the Line-Side Unpacker with `emptiesUseCapacity = false`; there is no switch.
- **Split** — as `splitQuantity` items (itself included): copies with its type and labels.
- Results: input = containers + pieces (Unpack) or items + copies (Split), output the same; `released`,
  `carriers_emptied` / `split_copies`; the port appears as `<name>/empties`.
- View: during the process time the contents travel off the container to `piecesPosition`.
- ⚠ It never pushes to a full element: BLOCKED while ready pieces wait.

## Line-Side Unpacker (Manufacturing pack)

The point at an assembly line where carriers (wagons, totes) are emptied — a Separator that unpacks piece by piece as
the line takes them. Holds `capacity` carriers; each piece after `pieceTime` (optional), `changeTime` (optional)
before each new carrier — not before the run's first one unless `changeTimeFirst` (a handling time for every carrier);
`copyCarrierLabels`.
- The empty carrier leaves to `emptiesDestination` (e.g. the queue a milk run collects from); with
  `emptiesUseCapacity` (default) it keeps its place until it has left, as an empty wagon waits to be collected.
- Results: as the Separator's, plus the line-side stock: `pieces_avg` / `pieces_min` / `pieces_max` (pieces held).
  Its content counts the carriers and the pieces in them together (so it can exceed `capacity`, which counts carriers);
  the diagnosis shows `carriers` and `pieces` apart.
- ⚠ Pull-driven: BLOCKED while a ready piece waits.

## Call-Off Point (Manufacturing)

A reporting point on an assembly line's feed: each passing vehicle gets its sequence number and, for every **call-off
family** (e.g. one per part to supply in sequence), an order goes out of the family's port to where the part is prepared
(a kitting queue, a sequencing station).
- A family: an order type, a quantity per vehicle, an optional condition on the vehicle (`item.model == 'K9'`), and the
  reference label of the part, drawn from a choice table per position (`R1:0.3|R2:0.7`, the element's own stream).
- The vehicle passes on unchanged (label `vehicle` = its sequence number); orders carry the same number, so the line can
  check the parts arrive in sequence.

## Paced Line (Manufacturing)

An indexing assembly line: `positions` pitches, an index every `takt`; vehicles enter at the first pitch and leave from the
last. **Stations** at given positions are fed at line side (each through its own port, a buffer of `portCapacity` parts) and
fit one part to the vehicle at their position at each index.
- If a station has no part when the line should index, the line stops (STARVED) until the part arrives; a station can
  also emit its own call-off when a vehicle reaches a position (call-off at the line).
- A part fitted to the wrong vehicle (match label) or of the wrong reference counts as a mismatch.
- Results: `index_count`, `empty_pitches`, `starved_time`, `line_stops` / `line_stop_time` (per station port too),
  `fitted`, `mismatches`.
- ⚠ Stops of the line pause the takt; a blocked exit (the next element full) holds the line.

## Sequencing Station (Manufacturing)

Fills carriers (wagons, totes) with one piece per call-off order, in the order sequence, lot by lot: it waits for a lot of
orders, takes an empty carrier, and for each order picks the piece of its reference (pulled on demand from a list, e.g. pick
faces, or fed by links), with an operator per carrier or per piece. Gaps (an order with no piece) and stock-out waits are
counted; the full carrier leaves with its pieces as sub-items (a Line-Side Unpacker at the line empties them).
- Results: `carriers`, `pieces`, `gaps`, `stockouts`, `stockout_time`, `order_wait_avg` / `max`, `lot_wait_avg`,
  `orders_waiting_avg`.

## Storage System and Storage Rack

Lanes of stored units with an access rule (`Direct`, `Lifo`, `Fifo`) and a SKU rule (`SingleSku`, `Mixed`); the rack
presets (conventional, drive-in, push-back, floor) set them. The location policy picks the lane of each load.
- **The rack's layout** (*Bays*, *Slots Per Bay*, *Levels*, *Depth*, the pitches) generates its lanes, named
  `B{bay}_S{slot}_L{level}`: *Levels* counts every load level, the ground one included (level 0 on the floor with
  *Ground Level*). After changing it in the inspector, press *Rebuild Storage Locations* (a run warns when the lanes do
  not match the layout); the agent commands rebuild at once. A lane's own transport node is kept by its id.
- **Put-away by the transport team** (*Put-Away By Transport*, on by default): with a transport team, each load waits
  at the storage's location (*Inbound Capacity* of them; the next one stays upstream) until a member carries it to its
  lane; it is in stock — retrievable, listed — once placed. Off, or without a team: put-away is instant. The initial
  stock of a base-stock replenisher is placed at once.
- **Physical dimensions** (*Physical Dimensions*, off by default): a load goes only where it fits —
  its labels `length` (along the lane), `width` (across) and `height` (names in *Length / Width / Height Label*)
  against the room of a position (a rack: the slot width × the slot depth, under the next level's beam; the top level
  and floor cells are open above; a listed lane: its *Slot Size*). A load without a label is not checked on that side;
  one load per position.
- **Store or pass-through** (*Release After Dwell*, off by default): off, the storage is a store — a load leaves only
  when something pulls or retrieves it, so a storage connected straight to a sink fills up and sends nothing. On, it is
  a pass-through rack: each load is released to the output connection once it has stayed *Minimum Dwell
  Time* (fetched by the transport team if there is one), one at a time, and waits at the storage's location until the
  receiver takes it.
- **Stock as a list**: connect the storage to a model list (push) and let elements pull from it with a query (e.g.
  `sku == A`). A pull retrieves the matching unit — the units in front are re-homed first — and the storage's transport
  team carries it **to the puller's location**. A pull that cannot start yet (its lane being dug, no room for a unit in
  front) waits and starts when a retrieval ends.
- Retrieval from code (`RequestRetrieval`) works the same way and delivers at the storage's own location; it skips
  units a puller has reserved.
- Moves are timed by the storage's transport team when lanes have nodes (its *override load times* set the load /
  unload of each move); with a team travelling straight (the default) every lane without a node gets one at its own
  position. ⚠ One move at a time per retrieval.
- **AS/RS**: a rack whose transport team is the *Stacker Cranes* preset, the crane placed in the aisle along the rack
  face (its +Z along the aisle): the mast runs along the aisle, the carriage rises to each slot, the forks reach in.

## Pick Face (Warehousing)

The picking location of one SKU (`sku`): up to `positions` unit loads — the front one open, the others in reserve — and
**single pieces** out, front load first. A load's pieces are its sub-items; loads and pieces carry the label `sku`.
- Pieces leave through the next element while it accepts them, or to a model list where stations pull them by query
  (`sku == X`).
- The emptied load leaves to `emptiesDestination` (none = discarded and counted) and the next load opens.
- **Replenishment (kanban)**: when loads held + on order ≤ `reorderPoint` it orders `reorderQuantity` loads and accepts
  that many from its input — typically a pull from a storage's published stock (the forklift brings them). Default: one
  position, reorder point 0 (a kanban of one); one open + one reserve load = positions 2, reorder point 1.
- `startFull` creates its loads at the start (`piecesPerLoad` each); otherwise it orders at time 0.
- **Size from the model**: with `sizeFromModel` (default on) the loads it creates get the size of `loadPrefab`, and the
  pieces of every load it opens (also loads brought from a storage) the size of `piecePrefab` as it lays them (turned by
  `pieceRotation`) — labels `width`, `height`, `length`, only where none is set: a label always wins.
- STARVED while it has no piece; results `pieces`, `stockouts`, `loads_received`, `loads_ordered`, `carriers_emptied`.

## Base-Stock Replenisher (Warehousing)

A source of unit loads that replaces what has been consumed, by the truckload.
- SKU table (`sku`, `piecesPerLoad`, `initialLoads`): inline or from a CSV / Excel file anywhere (read when the scene is
  extracted). `initialLoads` are stored at time 0 in `initialStockTarget` (default its first next element).
- Consumption = every retrieval from the storages it `observe`s (default: the storages it delivers to). Each time
  `truckloadSize` loads have been consumed it orders a truck with exactly those loads, arriving after `leadTime`.
- Trucks are unloaded only within `unloadingWindows` (e.g. `Mon-Fri 06:00-22:00`, the model calendar; OFF_SHIFT
  outside), one load at a time, or carried by its transport team (the same forklifts as the storage, if wished).
- **Size from the model**: with `sizeFromModel` (default on) its loads get the size of `loadPrefab` (labels `width`,
  `height`, `length`) — what a storage with physical dimensions checks; use a prefab that shows the full load (e.g.
  `PalletLoad_Cartons`), not the empty pallet, when the height matters.
- Results `trucks_ordered` / `received` / `unloaded`, `loads_released`, `lead_time_avg` / `max`, `unload_time_avg`,
  `consumed`, `backlog`.
- ⚠ A base-stock policy only: no reorder point on the warehouse total, no forecast, no supplier capacity.

## Milk Run

A vehicle that carries **several carriers per trip** — a tugger train with wagons, a rail shuttle — driven by the
members of its transport team (each member is one train: *Trains* runs that many at once, the team needs as many
members; the Tugger Train preset places no team — add one and set *Transport By ▸ Team*). It is not in the item flow:
it is the output of the origins it serves and the input of the destinations, so their statistics are as usual.
- **Empties**: a stop's *Empties Origins* are the **queues the empty carriers wait in** at the stop; the element that
  empties them (a Line-Side Unpacker, a Separator) sends them there through its *Empties Destination*. Listing the
  unpacker itself, or any element whose output already goes on, is a build error: the train would take over its output
  (the unpacker's pieces with it).
- **Fixed tours**: stops (with the empties to collect there), flows (origin at the depot → stop → destination, an
  optional filter, a maximum per tour), tour types (flows + stops in order) and a pattern (A, B, B…, or on demand). It
  leaves when the departure rule says: ready (any / all flows have a carrier and room at the destination; a minimum per
  tour, optional flows, a maximum wait), every N minutes or a timetable, or a combination. At each stop it unloads, then
  collects empties; back at the depot it unloads the empties.
- **Dynamic tours** (a shuttle): pick-ups at any stop; the next stop is the first request in the request order (FIFO,
  nearest `|distance`, longest queue…); policies opportunistic or fill-then-deliver, assignment at the stop or before the
  trip, free access to the carriers behind a queue's head, a fill time-out. Named orders become choices for the
  optimiser.
- Leg times per leg, or the team's travel model (network, matrix, axes); load / unload times per flow or the team's.
- Results: `tours` (and per type), `tour_time_avg` / `max`, the four stages, `departure_wait_*`, `delivered` (and per
  flow), `empties_returned`, `load_avg`, `distance`.
- ⚠ A queue that feeds it holds its carriers (PROCESSING) while they wait for the train: waiting for a puller is not
  blocked.

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
