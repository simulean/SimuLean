# Recipes

Common modelling needs and how to build them in SimuLean. Calls are the agent commands
([docs/agents/workflow.md](../agents/workflow.md)); in the editor, the same fields are in the inspector. Behaviour
details: [elements.md](elements.md), [mechanics.md](mechanics.md).

## Arrivals and demand

**Keep every order, even when the line is full.** An InterArrival Source that cannot hand an item over stops creating
items (its arrival clock waits). To keep every order, put an unlimited buffer right after it:
Source → Queue (`capacity` = a large number) → line. The backlog is the queue's `content`; the waiting time its `stay_avg`.
A Schedule source keeps its own backlog, but a queue still makes it visible.

**Lost demand (customers who leave when the line is long).** Route at the source by the queue's content: two next
elements — the queue and a "Lost" sink — and `outputStrategy.strategyType = Expression`, `outputStrategy.routingExpression =
content('Queue') >= 10 ? 1 : 0`. Lost demand = `Lost.input`.

**Arrivals from a table** (orders, a production plan, appointments): `simulean_load_table object=Src file=orders.csv`.
Columns `Time`, `Name` (the item type), `Q` (quantity); every other column becomes a label (`due`, `priority`, `job`).

**More or less demand in a study**: vary `Src.demandFactor` (1.2 = 20 % more arrivals or quantities).

## Routing and priorities

Every recipe here stays on the lowest rung that expresses the rule — a field, an expression, a query / order, a reaction —
and a C# choice rule is the last resort ([the escalation ladder](../agents/topics/logic.md#the-escalation-ladder),
[`Choice_Rules.md`](../../Assets/Docs/Choice_Rules.md)).

**By product type or another label**: `outputStrategy.strategyType = Expression`, `outputStrategy.routingExpression = item.type == 'A' ? 0 : 1`
(the index of the next element, in connection order). Or give each receiver an input strategy (`SingleLabel`) and keep
`FirstAvailable`.

**A percentage split**: `outputStrategy.strategyType = Probability`, `outputStrategy.weights = 70|30`. Each item draws once and
waits for its output.

**The least busy of several machines**: `ShortestQueue` (or `RoundRobin`) on the element that feeds them.

**The best destination by a rule** (most open orders, the aisle that holds the family, fast and empty first): one score
per next element instead of nested ternaries — `outputStrategy.strategyType = BestScore`, `outputStrategy.scoreExpression
= get(out.name, 'orders') * 100 - out.content` (or `count(out.name, 'family', item.family)`); the highest wins, ties to the
first in order. `whenBestFull = Wait` makes the item wait for the best when it is full (default `Skip`: the best with room).
To keep an item away from a next element, give that element an admission.

**Serve by priority instead of first come**: give the Queue an offer order — `offerSort = priority desc` (or `due`, or an
expression: `item.prio * 10 + age desc` for urgency that grows while waiting; `count('Kits', 'order', item.order) desc`
for "the order whose kit is complete"). With `offerOrder = AnyAccepted` the items are offered best first until the
receiver takes one. A list with a pull order (`|priority desc`) does it too when several stations share the buffer.

**A machine that accepts only some items while others wait behind**: give the Queue `offerOrder = AnyAccepted`, so a
refused head does not block the rest.

**Open a gate by a reaction** (an admission condition over a property a reaction sets, `element.open == 1` or
`get('Gate', 'open') == 1`): the reaction's *Set Element Property* is enough — writing a property an admission reads offers
the items it refused again at once (the wake-up). A condition that reads something else (`content('Buffer')
< 5`, `now() >= 480`, a table) needs a **Retry** action of the receiver in a reaction on what changes it (the buffer's *Item
Exited*, a clock signal). A Queue released on command and its **Release** action remain the gate that counts items. A max
wait is **not** a gate: it only gives up or reroutes at its limit.

**Repeat a test** (an invalid result sends the item back, maybe several times): count the attempts with a label
(`entryLabels`: `tries = (tries ?? 0) + 1`) and route by it (`item.ok == 0 && item.tries < 3 ? 1 : 0`, output 1 = back).
Send it **back to the input Queue**, which needs room for every item that may return (else the loop deadlocks at the
first return), or through a **zero-time Delay** that sends it back (the station frees its slot first). ⚠ Never connect a
full station to itself: it keeps its slot while it offers the item to itself and the loop deadlocks (it works only while a
slot is spare). `simulean_diagnose` shows such a loop BLOCKED on itself.

## Assembly, kits and batches

**Assemble a product from parts**: a Combiner with `requirements = {2, 4}` needs 3 inputs — the main item (the base)
on input 0, 2 parts of the first kind on input 1, 4 of the second on input 2 (`inputOrder` fixes which upstream element
is which). The parts are consumed; `batchMode = true` keeps them as sub-items.

**A batch of N into one unit**: a Stacker with `capacity = N` releases every N items as one compound item (one
position, `positions = 1`).

**Kitting by key** (each block needs its own parts): the bay is a Combiner with `pullModeType = SingleLabel`,
`singleLabelName = block` and an empty value — each assembly takes the parts whose `block` equals its main item's.
Feed the parts from a Queue with `offerOrder = AnyAccepted`, or from a list. See
[`Assets/Docs/Kitting_By_Key.md`](../../Assets/Docs/Kitting_By_Key.md).

## Warehouse and line supply

The example scene `Assets/Scenes/LibraryExamples/Warehouse_ToLine.unity` puts these together (rebuild it from
*Tools ▸ SimuLean ▸ Examples*; headless it is the reference model `warehouse_to_line`).

**A warehouse that feeds the line on demand**: put the stock in a Storage System (or Rack) whose *Send To* is a list
(`Stock`); the elements that need it pull from the list with a query (`sku == A`). The storage retrieves the unit — the
units in front are re-homed first — and its transport team (forklifts) carries it to the puller's location.

**Pick faces with kanban**: a Pick Face per SKU (`positions = 2`, `reorderPoint = 1`: one pallet open, one in reserve)
pulling from the stock list; it hands out single pieces and sends the empty pallets out of its `empties` port.

**Trucks of what was consumed**: a Base-Stock Replenisher in front of the storage (SKU table with pieces per pallet and
initial pallets, `truckloadSize`, `leadTime`, optional `unloadingWindows`): every pallet retrieved from the storage is
consumption, and each full truckload of consumption is ordered.

**Totes to the line by tugger train**: pack the pieces into totes — a Combiner per SKU whose main input is a Source of
empty totes (`itemPrefab` = the tote; a short constant inter-arrival keeps one always waiting), the pick face its
component input (`requirements = 4`) and `batchMode` on, so the pieces travel inside the tote — keep the full totes in a
depot queue, and a Milk Run with one tour type (flows depot → stop → line-side unpacker, `maxPerTour`) takes them to the line side
and collects the empty totes from a queue at the stop (`emptiesOrigins`). At the line a Line-Side Unpacker hands the pieces to the
station one by one and sends the empty tote to that queue.

**Sequenced supply (JIS)**: a Call-Off Point on the line's feed emits an order per vehicle; a Sequencing Station fills
carriers in that order; the Paced Line fits the parts at its stations and stops when one is missing. See the reference
models `call_off_buffer_line` and `sequencing_station_lot`.

## Time

**A transit, a cure, a wait without capacity**: a Delay (every item its own time, all at once).

**A conveyor with travel time**: a Conveyor (accumulation limited by length) followed by a Delay with the travel time —
the Conveyor alone has no transit time.

**Times that depend on the item**: an expression in the distribution, e.g. a constant of `2 + 0.1 * item.size`.

**Failures**: a Downtime of kind MTBF/MTTR on the machines (Immediate: the process pauses and resumes). **Shifts**: kind
Shift with the week's pattern and the holidays (AfterCurrent: the job in hand finishes). **Planned maintenance**: kind
Periodic or Timetable.

## People and vehicles

**Operators at the machines**: a team (`simulean_add_team name=Operators members=3`), and `processBy` on the
stations. Release them when the item leaves (`ItemLeaves`) if they must unload a blocked machine.

**Operators with skills**: labels on the members (`skill = 'weld'`, a `name = expression` record: text in quotes) and a requirement with a query
(`count#query#label`: `1#skill == weld#welder` — a query is `field op value`, without quotes).

**At most N items in a zone at once** (a rework zone, a test bed, a crane's N moves): a **token pool** — a team of N
members with `travel=Abstract` (the Library's *Teams ▸ Token Pool* preset, or `simulean_add_element type="Token Pool"`, then
its members; or `simulean_add_team name=ZoneTokens members=3 travel=Abstract`), requested by each
station of the zone (a requirement of that team, `processBy.acquire = InOrder` when it is taken before the station's own
operators) and released when the item leaves the zone. The waits, the utilisation and `maxWait` work as for any team, and
`simulean_diagnose` shows who holds the tokens. ⚠ Not a counter property raised and lowered by reactions: that gate is
checked at one instant and can be exceeded (two items admitted at the same time).

**AGVs, forklifts, cranes**: a team of carriers and `transportBy` on the sending element; a transport network
(nodes + tracks) or a travel matrix gives the travel times; elements need a `location` node.

## Running and measuring

**A warm-up and replications**: `warmup = 480`, `replications = 10` — read `{mean ± halfWidth}`, not one run.

**Stop when an order is complete**: `StopAt = Exit`, `StopAtCount = 500` on the clock; the makespan is
`model.makespan`.

**Time in system per product**: on the sink, `tallies = tis = age`, `tallySplit = type`; metric
`Exit.tally:tis[*]:mean` (over every product the Sink gives it built in: `Exit.tis_avg`).

**A KPI in working days**: `derived = days=span/16` (16 working hours a day), with `span=model.makespan`.

**Why does my model stop moving?** `simulean_diagnose`: what each element holds and waits for, the requests nobody
serves, the resources and what they do.

**Compare designs**: one call with `factors=Buf.capacity=2|4|8;team:Operators.members=2|3`, a `where` to drop combinations,
or hand-picked `scenarios`. **Find the best**: `simulean_optimize_start` with the parameter ranges and a fitness.
