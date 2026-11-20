# Topic: warehouse and line supply

Read after the core ([workflow.md](../workflow.md)) when the model stores loads, picks pieces, replenishes stock or
supplies a line by a train. Teams, travel and home nodes: [resources.md](resources.md). Every recipe gives the exact
keys; `simulean_types type=…` adds the fields' `help` notes.

## The pattern

Replenisher → storage rack → stock list → pick faces → packing (a Combiner) → depot queues → milk run → line-side
unpackers → the line. Empty carriers go back the other way through queues the train collects from.

## Storage rack and its lanes

`simulean_add_element type="Pallet Rack" name=Rack` (also Carton Flow Rack, Floor Storage, Drive-In Rack, Push-Back Rack;
component `UnityStorageRack`). These are presets of one type, **Storage Rack** (`simulean_types` shows `variantOf`): a
preset places a Storage Rack with its `style` and a starting layout (Pallet Rack: 3 bays × 3 slots × 4 levels);
`simulean_types type="Storage Rack"` lists the fields, and placing `Storage Rack` itself gives the bare type.

- **Layout:** `style`, `bays`, `slotsPerBay`, `levels`, `depth` (positions per lane), `unitsPerLane`, pitches
  `bayWidth`, `slotWidth`, `levelHeight`, `slotDepth`, `groundLevel`. **`levels` counts the ground level:** lanes are
  `B{bay}_S{slot}_L{level}`, **every index 0-based** — the first lane is `B0_S0_L0`, the last of 3 bays × 3 slots × 4
  levels `B2_S2_L3` (floor storage `B{bay}_R{row}`). Setting any layout field through `simulean_set` **rebuilds the
  lanes at once** (the answer's `rebuilt` gives the lane ids); `simulean_apply` rebuilds them once the whole list has
  run, so set `lanes[...]` in a second `apply` after the one that changes the layout. A lane's own `node` is kept by its
  id. In the inspector, press *Rebuild Storage Locations* (`simulean_invoke object=Rack action=Rebuild`); validate warns
  about stale lanes.
- **Lanes by id:** `lanes[B0_S0_L0].node` sets one lane, `lanes[B0_*].node` every lane of bay 0 (`*` any text, `|`
  between patterns: `lanes[B0_*|B1_*]`, `lanes[*]` all); the answer gives `matched` and the `items`. With one node per bay:
  `{"op": "set", "object": "Rack", "key": "lanes[B1_*].node", "value": "N_Bay1"}`.
- **Where moves go:** the rack's `location` (a Transport Node) is its **inbound point**: every put-away starts there,
  so it is the dock where the trucks unload, not the rack's own place (the replenisher's `location` does not set it).
  It is also where a lane is unless `lanes[i].node` says otherwise; with one node per bay, set `location` to the dock
  and each bay's lanes' `node` (`lanes[B1_*].node`). Give the team load / unload times or real distances, else a move
  takes no time (validate warns); which load time wins: [resources.md](resources.md) *Load and unload times*.
- **Who moves:** `transportBy.team` (forklifts on a `Network`); `putAwayByTransport = true` also carries the put-away.
  **A load a pick face (or any puller) pulls from the stock list is carried by the rack's `transportBy` team** — from its
  lane to the puller's `location`; the puller needs no team of its own.
- **Where a load goes:** `locationPolicy` = `FirstAvailable`, `ClosestSpot`, `Random`, `DedicatedBySku`
  (`dedicatedSkuMappings[i]`: `sku`, `laneId`).
- `simulean_model` shows the lanes as one summary (`lanes`, `lanes[*].capacity`…); `detail=full` lists each.

## Stock list, push and pull

```json
[{"op": "add", "type": "List", "name": "Stock"},
 {"op": "set", "object": "Rack", "key": "sendTo", "value": "List"},
 {"op": "set", "object": "Rack", "key": "pushToList", "value": "Stock"},
 {"op": "set", "object": "Face1", "key": "receiveFrom", "value": "List"},
 {"op": "set", "object": "Face1", "key": "pullFromList", "value": "Stock"},
 {"op": "set", "object": "Face1", "key": "pullQuery", "value": "sku == 'S1'"}]
```

A new List has no fields and no order (first pushed, first pulled). An order: `defaultOrderBy` (a field or an
expression) with `defaultDescending`; fields: `fields[i]`. A pull query is one text: conditions `field op value`
joined by `;`, then `|` and the order (`sku == 'S1'; qty > 0 | due desc`). Statistics `list:Stock.wait_avg`,
`list:Stock.content_avg`.

## Replenishment

`UnityBaseStockReplenisher` (a source of loads) connected to the rack: `skus[i]` (`sku`, `piecesPerLoad`,
`initialLoads`), `truckloadSize`, `useLeadTime` + `leadTime` (a distribution), `unloadingWindows`, `observe`,
`initialStockTarget`. Statistics `trucks_ordered`, `trucks_received`, `trucks_unloaded`, `loads_released` (loads
delivered), `consumed` (loads retrieved from the observed storages), `backlog` (consumed loads not yet reordered),
`lead_time_avg`, `unload_time_avg`.

- **`initialStockTarget`** is where the initial loads (`skus[i].initialLoads`) go at time 0 (default: the first element
  it is connected to). They are **placed directly**, already in their lanes at the start — not put away by the forklift,
  no transport time; a target without room gets the rest through the output, like a truck's loads (with a warning).
- **`observe` takes storages only** (a Storage Rack / Storage System; empty = the storages it delivers to) — not a pick
  face or a queue. **Consumption counts when a load is retrieved** from an observed storage (a pull from its stock list),
  not when its pieces are used: a pick face reordering a load is one consumed load.

## Pick faces and packing

- `UnityPickFace`: `sku`, `positions` (loads at the face), `reorderPoint`, `reorderQuantity`, `piecesPerLoad`,
  `startFull`; it pulls whole loads from the stock list and hands out single pieces through its Next Elements; the empty
  load leaves by `emptiesDestination` (a Sink or a Queue; unset = discarded at once, still counted in
  `carriers_emptied`, nothing reaches a Sink). The empty load moves with no transport and no time (model one with a
  Queue and a transport if it matters). Statistics `pieces` (pieces handed out), `loads_ordered`, `loads_received`,
  `carriers_emptied`, `stockouts`.
- **Pick face stock:** `positions` = loads held (1 open + reserve). It orders `reorderQuantity` loads (capped by the
  free positions) whenever loads held + on order ≤ `reorderPoint` (default 0: when the last is emptied; one open + one
  reserve = `positions 2, reorderPoint 1`). Without `startFull` it starts empty and orders at time 0; with it, it
  starts with `positions` loads of `piecesPerLoad`.
- Packing pieces into a carrier: a Combiner (`MultiServer`-like; `UnityCombiner`) with `inputOrder` (the main input —
  the empty carrier — first, then the pieces), `requirements` (pieces per input, after the main one: `[6]`),
  `batchMode = true` (the pieces travel inside the carrier), `delayStrategy`. The **Tote Packer** preset (Manufacturing
  pack: `simulean_types pack=Manufacturing`) is this Combiner with `requirements = [4]`, the pieces laid in the tote
  (`componentLayout = OnLoad`) and a 30-unit constant time on a bench model: place it, then set its inputs,
  `requirements` and time (and `batchMode`) as above.
- **Always one empty tote waiting:** the main input is a Source with `mode = InterArrival` and a very small constant
  interval (`interArrivalDistribution = 0.01`) connected straight to the Combiner: it holds its one tote while the
  Combiner is busy (BLOCKED, no backlog) and makes the next as soon as it is taken. Its `output` counts the totes used.

## Milk run (tugger train)

`simulean_add_element type="Tugger Train" name=Train` (component `UnityMilkRun`) **places no team**: add one and name it.

```json
[{"op": "add_team", "name": "Tuggers", "members": 1, "kind": "tugger"},
 {"op": "set", "object": "Train", "key": "transportBy.team", "value": "Tuggers"},
 {"op": "set", "object": "Train", "key": "capacity", "value": 4},
 {"op": "set", "object": "Train", "key": "stops[0].name", "value": "Stop1"},
 {"op": "set", "object": "Train", "key": "stops[0].emptiesOrigins", "value": "EmptyBin1,EmptyBin2"},
 {"op": "set", "object": "Train", "key": "emptiesDestination", "value": "EmptyReturn"},
 {"op": "set", "object": "Train", "key": "flows[0].name", "value": "Flow1"},
 {"op": "set", "object": "Train", "key": "flows[0].origin", "value": "KitBuffer1"},
 {"op": "set", "object": "Train", "key": "flows[0].stop", "value": "Stop1"},
 {"op": "set", "object": "Train", "key": "flows[0].destination", "value": "LineFeed1"},
 {"op": "set", "object": "Train", "key": "flows[0].maxPerTour", "value": 2},
 {"op": "set", "object": "Train", "key": "legs[0].from", "value": "Depot"},
 {"op": "set", "object": "Train", "key": "legs[0].to", "value": "Stop1"},
 {"op": "set", "object": "Train", "key": "legs[0].time", "value": "60"}]
```

- **`trains`** is how many trains run at once; each is driven by one member of `transportBy.team` (the team needs at
  least that many members).
- **Load / unload times, first found wins:** a flow's own `flows[i].loadTime` / `unloadTime` (with
  `flows[i].overrideTimes`; setting a time turns it on) for its carriers; then the train's `transportBy.loadTime` /
  `unloadTime` (with `transportBy.overrideLoadTimes`); then each member's own (0 by default). Empties:
  `emptiesLoadTime` / `emptiesUnloadTime` (with `overrideEmptiesTimes`), then the same fallbacks. More:
  [resources.md](resources.md) *Load and unload times*.
- **Tours:** `flows[i]` (`name`, `origin`, `stop`, `destination`, `maxPerTour`, `minPerTour`, `optional`, `filter`,
  `overrideTimes`, `loadTime`, `unloadTime`);
  `stops[i]` (`name`, `node`, `emptiesOrigins`, `emptiesDestination`, `emptiesFilter`); `tourTypes[i]` (`name`,
  `flows`, `stops`; none = one tour of everything) and `pattern` (their order). The depot is `depotName` (`Depot`).
- **Legs:** `legs[i]` (`from`, `to`, `time` — a distribution); `symmetricLegs = true` uses each leg both ways. **A leg's
  time replaces the travel model for that hop** — whatever the team's `travel`, nothing is added on top. A hop without a
  leg: a spatial team (`Straight`, `Network`…) drives it by its travel model to the stop's location (`stops[i].node`,
  `depotNode`); an `Abstract` team takes `transportBy.travelTime`, else no time. **Travel mode for a train given by its legs:**
  `travel = Abstract` on its team (the times are exactly the legs). With a spatial team, a member not at the depot first
  drives there by its travel model before its first tour (once): set its `homeNode` to the depot's node, or use Abstract.
- **Departure:** `departure` = `Ready` (the default), `Schedule` (`departureInterval`, `firstDeparture` or a
  `timetable`), `ScheduleWhenReady`, `ReadyOrSchedule`; `readyRule` = `Any` or `All`.
  - **`Ready`:** a train waiting at the depot leaves as soon as its next tour is ready. With `readyRule = Any` (the
    default) that is one (non-optional) flow ready; with `All`, every flow. A flow is ready when its origin offers
    max(1, `minPerTour`) carriers not claimed by another train **and** its destination has room for them: with the default
    `minPerTour`, one carrier waiting is enough. Several trains each check this on their own.
  - `readyMaxWait` (> 0): a train that has waited that long leaves as soon as any flow of the tour is ready. `fillTimeout`
    is for `Dynamic` tours only.
- **Empties — the one pattern:** an element that empties carriers (a Line-Side Unpacker, a Separator) sends them through
  its `emptiesDestination` to a **Queue at the stop**; that Queue is listed in `stops[i].emptiesOrigins`; the train
  brings them to `emptiesDestination` of the milk run. Listing the unpacker itself, or any element with its own Next
  Elements, is a build error (the train would take over its output).
- Statistics: the team (`team:Tuggers.utilization`; a train waiting for its departure is `state:WAITING_DEPARTURE`,
  not busy); the milk run's own `tours`, `tour_time_avg`, `delivered` (per flow `delivered:Flow1`), `empties_returned`,
  `departure_wait_avg`, `stage_outbound_avg`.

## Line side

`UnityLineSideUnpacker`: `capacity` (carriers at once), `unpackOrder`, `copyCarrierLabels`, `emptiesUseCapacity` (an
empty carrier keeps its place until collected), `emptiesDestination` (the Queue at the train's stop), `usePieceTime` +
`pieceTime`, `useChangeTime` + `changeTime`, `changeTimeFirst`; its Next Elements get the pieces one by one (e.g. a
Combiner's component input). **`changeTime` is per carrier**: it runs before the first piece of each new carrier (state
`SETUP`); by default not before the run's first carrier (the line starts with it in place). **`pieceTime` is per
piece**: each piece takes it before it can leave. Neither set = the pieces are handed over at once. **A fixed handling
time for every carrier, the first included:** `changeTimeFirst = true` — `changeTime` then runs before the first carrier
too (12 pieces in 3 wagons, `pieceTime` 1, `changeTime` 2: the last piece leaves at 16 by default, at 18 with it).
A Separator with `mode = Unpack` and `processTime` takes that time once per carrier, then releases all its pieces together; its empty carrier frees its
place as soon as it has gone to the `empties` port (it waits there outside the capacity) — as the unpacker with
`emptiesUseCapacity = false`; there is no switch. The unpacker's content counts carriers and pieces together;
`simulean_diagnose` shows `carriers` (against `capacity`) and `pieces` apart. Its `input` / `output` count carriers and
pieces together (output = pieces + empty carriers): pieces delivered = the next element's `input`; carriers =
`carriers_emptied`. Statistics `pieces_avg`, `pieces_min`, `pieces_max`, `carriers_emptied`.
