# Kitting by key: a yard serving assembly bays

A buffer (a **yard**) holds parts that several **assembly bays** need, each bay only "its own" part — e.g. the
sub-blocks of a shipyard: bay A builds block 3, then 1; bay B builds 2, then 4; the sub-blocks arrive 1, 2, 3, 4. A
crane carries each part from the yard to its bay. SimuLean does it the way FlexSim does: **the receiver decides which
item it takes** (its pull requirement), **the holder decides whether the item is carried** (its transport).

## The bay: a Combiner that takes "the part of my block"

- Main input: the blocks (one main item per assembly).
- Component input: the yard.
- *Pull Mode* **Single Label**, *Label Name* `block`, *Label Value* **empty** — the value comes from the main item:
  the bay admits only the sub-block whose `block` equals its block's.
- **Capacity** n = n assemblies in parallel in one element (each with its own main item and its own match), e.g. three
  bays of the same kind as one Combiner of capacity 3.

## The yard: two ways

1. **Direct connection** — Queue (or Multi-Server) connected to every bay, **Release Mode: Any Accepted**: the yard
   offers each part it holds (oldest first) until a bay accepts one. With *FIFO* (the default) a part nobody wants
   yet at the head holds the others back — the model deadlocks (the run monitor reports it).
2. **Through a list** — the yard pushes its parts to a **SimList**; each bay's component input pulls from it
   (Combiner *List Inputs*, input 1). The list may order or filter (queries, priorities).

## The crane: the yard's transport

Set the yard's **Transport By** to the crane team (travel / load / unload times, or a spatial team on the network):
in both ways the crane goes to the yard, loads the part, carries it to the bay that took it and unloads it there.

Reference models: `yard_kitting` (direct), `yard_kitting_list` (list), `yard_kitting_capacity` (one Combiner with two
assemblies) — `StandaloneTests/referencemodels`.
