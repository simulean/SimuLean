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
elements — the queue and a "Lost" sink — and `outputStrategyType = Expression`, `outputStrategy_expression =
content('Queue') >= 10 ? 1 : 0`. Lost demand = `Lost.input`.

**Arrivals from a table** (orders, a production plan, appointments): `simulean_load_table source=Src file=orders.csv`.
Columns `Time`, `Name` (the item type), `Q` (quantity); every other column becomes a label (`due`, `priority`, `job`).

**More or less demand in a study**: vary `Src.demandFactor` (1.2 = 20 % more arrivals or quantities).

## Routing and priorities

**By product type or another label**: `outputStrategyType = Expression`, `outputStrategy_expression = item.type == 'A' ? 0 : 1`
(the index of the next element, in connection order). Or give each receiver an input strategy (`SingleLabel`) and keep
`FirstAvailable`.

**A percentage split**: `outputStrategyType = Probability`, `outputStrategy_weights = 70|30`. Each item draws once and
waits for its output.

**The least busy of several machines**: `ShortestQueue` (or `RoundRobin`) on the element that feeds them.

**Serve by priority instead of first come**: the buffer pushes its items to a list; the station pulls with an order
(`|priority desc`, or `|due`). A plain Queue is always oldest first.

**A machine that accepts only some items while others wait behind**: give the Queue `releaseMode = AnyAccepted`, so a
refused head does not block the rest.

## Assembly, kits and batches

**Assemble a product from parts**: a Combiner with `requirements = {2, 4}` needs 3 inputs — the main item (the base)
on input 0, 2 parts of the first kind on input 1, 4 of the second on input 2 (`inputOrder` fixes which upstream element
is which). The parts are consumed; `batchMode = true` keeps them as sub-items.

**A batch of N into one unit**: a Stacker with `capacity = N` releases every N items as one compound item (one
position, `positions = 1`).

**Kitting by key** (each block needs its own parts): the bay is a Combiner with `pullModeType = SingleLabel`,
`singleLabelName = block` and an empty value — each assembly takes the parts whose `block` equals its main item's.
Feed the parts from a Queue with `releaseMode = AnyAccepted`, or from a list. See
[`Assets/Docs/Kitting_By_Key.md`](../../Assets/Docs/Kitting_By_Key.md).

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

**Operators with skills**: labels on the members (`skill = weld`) and a requirement with a query
(`count#query#label`: `1#skill == weld#welder` — a query is `field op value`, without quotes).

**AGVs, forklifts, cranes**: a team of carriers and `transportBy` on the sending element; a transport network
(nodes + tracks) or a travel matrix gives the travel times; elements need a `location` node.

## Running and measuring

**A warm-up and replications**: `warmup = 480`, `replications = 10` — read `{mean ± halfWidth}`, not one run.

**Stop when an order is complete**: `StopAt = Exit`, `StopAtCount = 500` on the clock; the makespan is
`model.makespan`.

**Time in system per product**: on the sink, `tallies = tis = age`, `tallySplit = type`; metric
`Exit.tally:tis[*]:mean`.

**A KPI in working days**: `derived = days=span/16` (16 working hours a day), with `span=model.makespan`.

**Why does my model stop moving?** `simulean_diagnose`: what each element holds and waits for, the requests nobody
serves, the resources and what they do.

**Compare designs**: one call with `set=Buf.capacity=2|4|8;team:Operators.members=2|3`, a `where` to drop combinations,
or hand-picked `scenarios`. **Find the best**: `simulean_optimize_start` with the parameter ranges and a fitness.
