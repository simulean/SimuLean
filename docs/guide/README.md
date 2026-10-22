# SimuLean modelling guide

How SimuLean models behave and how to build common systems with them — for a modeller in the Unity editor and for an
agent working through the `simulean_*` commands (the agent's workflow is in [docs/agents](../agents/workflow.md)).

| | |
|---|---|
| [elements.md](elements.md) | **What each element does exactly**: capacity, order, what happens when it cannot pass an item on, stops, resources, statistics — and the points where it differs from other DES tools |
| [mechanics.md](mechanics.md) | **How a model runs**: push and pull, routing, admission, labels, the end of a run and the warm-up, downtime and calendars, resources, lists, reactions, random numbers |
| [results-and-data.md](results-and-data.md) | **What each statistic means**, how to name it in a study, where data and results are kept, how to reproduce a result |
| [validation.md](validation.md) | **An M/M/1 checked against queueing theory** (the scene `Assets/Scenes/Validation/MM1.unity`) |
| [recipes.md](recipes.md) | **Problem → solution**: keep every order, lost demand, routing by type, priorities, assembly, kits, failures, shifts, operators, vehicles, KPIs, studies |

Further reading: expressions [`Assets/Docs/Expressions.md`](../../Assets/Docs/Expressions.md), downtime and calendars
[`Downtime_And_Calendar.md`](../../Assets/Docs/Downtime_And_Calendar.md), signals and reactions
[`Signals_And_Reactions.md`](../../Assets/Docs/Signals_And_Reactions.md), headless runs and experiments
[`Headless_And_Experiments.md`](../../Assets/Docs/Headless_And_Experiments.md), the engine's design
[`docs/architecture.md`](../architecture.md).

## The model in five ideas

1. **Items flow between elements.** A Source creates them, buffers hold them, stations process them, a Sink takes them
   out. Elements are connected by their *Next Elements*; an element offers a finished item to them and the receiver
   accepts or refuses it. A refused item **stays and waits** — nothing is lost, nothing is retried on a timer: the
   receiver calls when it has room.
2. **Items carry labels** (numbers or texts) that routing, admission, process times and statistics read through
   expressions (`item.type`, `item.due`, `age`).
3. **Resources are teams** of task executors — operators, robots, vehicles, cranes — that stations request to process
   (`processBy`) and senders request to carry (`transportBy`), with travel over a network, a matrix or a grid.
4. **Time is calendar time**: downtimes (failures, shifts, maintenance) stop elements — the work in hand pauses or
   finishes — and reactions act on what elements signal (an item entered, a process ended, a state changed).
5. **A run is reproducible and measured**: every element draws from its own random stream; a study runs scenarios ×
   replications with the same seeds and reports each metric as mean ± half-width.

## Coming from another tool

| You may expect | In SimuLean |
|---|---|
| A station with its own input queue | A Multi-Server has **no queue**: put a Queue in front |
| A blocked source to keep generating (or to count lost arrivals) | An InterArrival Source **waits** with one item; add an unlimited Queue to keep orders, or route to a "Lost" sink to count them ([recipe](recipes.md#arrivals-and-demand)) |
| A queue sorted by priority | Queues are oldest first; sort with a list and an order query, or let the receiver choose (`AnyAccepted`) |
| A conveyor with travel time | The Conveyor accumulates by length, with **no transit time**: add a Delay |
| Setup times on a machine | No setup on stations: a preceding element, or a resource step |
| A Combiner input per component | K requirements → **K + 1** inputs (input 0 is the main item) |
| Utilisation over the time on shift | `utilization` is over all the time; `utilization_onshift` over the time on shift |
| A seed per run | A stream per element, keyed by its name: renaming an element changes its draws |
