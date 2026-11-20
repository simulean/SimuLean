# Topic: resources — teams, priorities, preemption, downtimes

Read after the core ([workflow.md](../workflow.md)) when the model has operators, vehicles, cranes, shifts, failures or
repair crews. Every recipe gives the exact keys; `simulean_types type=…` adds the fields' `help` notes.

## A team and who uses it

```json
[{"op": "add_team", "name": "Operators", "members": 2, "kind": "operator"},
 {"op": "set", "object": "CNC_A", "key": "processBy.team", "value": "Operators"},
 {"op": "set", "object": "Rack", "key": "transportBy.team", "value": "Forklifts"}]
```

- **`kind`** gives the members that kind's 3D model and realistic speed (sets `speed` and `units = Physical`, m/s):
  operator, doctor, nurse, fitter, welder, painter, outfitter 1.2; bed / stretcher / wheelchair porter, pallet truck, agv,
  hall crane, level luffing crane 1.0; forklift 2.5; tugger, stacker crane 2.0; robot (vacuum, welding) 1.5; spmt 0.8;
  goliath crane 0.6; welding gantry 0.5 (plurals and aliases accepted; an unknown kind gets a did-you-mean). **Setters,
  mechanics, technicians:** `kind = operator`. **A kind is a preset over the team component's defaults:** it sets only
  the members' 3D model, `speed` (the kind's m/s) and `units = Physical` (a robot also gets its arm cell); every other
  field keeps the component default. **The component default** (what a team gets with no kind and a name that is not a
  kind) is `speed = 1` in `units = Model` (1 distance unit per model time unit: 1 m/min in a minutes model, 1 m/h in an
  hours model — far too slow for people or vehicles). So `kind = operator` gives 1.2 m/s, no kind 1 unit per time unit.
  A name that is a kind (`Forklifts`, `AGVs`) applies that kind. The `add_team` answer says which it applied: `speed`,
  `units` and `speedFrom` (the kind, the name, or the default).
- **The speed fields** of a team: `speed` and `units` (`Physical` = m/s, converted by the clock's time unit; `Model`);
  `simulean_set object=Forklifts key=speed value=1.5`. A repair crew is a team like any other: its `speed` too.
- **Travel** (`travel` of the team): `Straight` (the default: straight lines between the elements, a move takes
  distance ÷ speed), `Abstract` (no travel), `Network` (on the tracks between Transport Nodes), `Matrix`, `Grid`, `Axes`.
  **A team with no `travel` given walks:** each job then includes the walk from where the member stands, and the walk
  counts as busy. A setter or repair crew whose walk the system does not describe needs `travel = Abstract`; the team's
  `simulean_model` block shows the travel it got.
- **`homeNode`** — where idle members start and, with `returnHome = true`, go back to. **A `Straight` team needs no
  node:** its home is the team's own position (`add_team x / z`), and `simulean_set object=Mechanics key=returnHome
  value=true` sends the members back there (a mechanic's corner). Set a `homeNode` (a Transport Node) only when the home
  is elsewhere: `simulean_set object=Forklifts key=homeNode value=N_Dock` (Straight travel: straight lines from and to
  that node; a lone node needs no network). A Network team without one starts at the network's first node;
  `simulean_validate` names it under `info`. **Without `returnHome = true` a member stays where its last job ended**
  (the default). `add_team` takes neither: set them afterwards (the example below).
- **A station's request:** `processBy.team`, `processBy.requirements[j]` (`count`, `query`, `label`, `team`),
  `processBy.priority`, `processBy.release` (`EndOfProcess`, `ItemLeaves`, `AtElement` with `processBy.releaseAt`).
  A transport — on the **sending** element (it carries every item that leaves it to its next elements):
  `transportBy.team`, `transportBy.priority`, `transportBy.loadTime` / `unloadTime`
  (with `transportBy.overrideLoadTimes = true`; see *Load and unload times* below).
- **`team` and `requirements` of a use** (`processBy`, `setup.setupBy`, `transportBy`, `repairBy`, an assembly task's
  `tasks[i].processBy`): `team` is the switch and the **default team**; `requirements` say what each job needs (empty =
  one member of `team`). A requirement without a `team` takes the use's. A use with `team` empty but requirements that
  name their teams is active (a crane of `Cranes` and a driver of `Drivers` need no `processBy.team`); a requirement
  without a team is then a build error (`simulean_validate`) that says to set `processBy.team` or that requirement's
  `team`. Requirements with no team anywhere do nothing: the scene extraction warns.
- **A team's size:** `simulean_set object=Operators key=members value=3` — more keeps the members and adds copies of the
  last one (numbered on: `Operator 3`), fewer keeps the first ones; a study varies it with `team:Operators.members`.
- **A member's labels:** `members[i].properties = skill = 'weld', level = 3`; a requirement picks them with its `query`
  (`skill == 'weld'`); a study varies `team:T.members[skill=weld]`.

## Load and unload times of a transport

A carried move is load + travel + unload. The load / unload times come from one of three places:

| Level | Keys | Units |
|---|---|---|
| the element's use (wins) | `transportBy.loadTime` / `transportBy.unloadTime` (distributions) with `transportBy.overrideLoadTimes = true` — setting a time with `simulean_set` or an `apply` `set` turns it on | model time units |
| each member's own | `members[i].loadTime` / `members[i].unloadTime` of the team (numbers) | seconds with the team's `units = Physical`, else model time units |
| the whole team (a study key) | `team:Forklifts.loadTime` / `unloadTime` in `factors` — sets every member's | as the members' |

The element's times win for its moves whenever `overrideLoadTimes` is on; otherwise each member uses its own (0 by
default: a move then takes only its travel). There is no team field of its own: `simulean_set object=Forklifts
key=members[0].loadTime value=30` per member, or the factor `team:Forklifts.loadTime=30` for all of them. A milk run's
empties have their own `overrideEmptiesTimes`, `emptiesLoadTime`, `emptiesUnloadTime` ([warehouse.md](warehouse.md)).

## A transport network (vehicles on tracks)

```json
[{"op": "add", "type": "Transport Network", "name": "Net"},
 {"op": "add", "type": "Transport Node", "name": "N_Dock", "x": 0, "z": -4, "parent": "Net"},
 {"op": "add", "type": "Transport Node", "name": "N_Rack", "x": 10, "z": -4, "parent": "Net"},
 {"op": "connect", "from": "N_Dock", "to": "N_Rack"},
 {"op": "add_team", "name": "Forklifts", "members": 2, "kind": "forklift", "travel": "Network"},
 {"op": "set", "object": "Forklifts", "key": "speed", "value": 1.5},
 {"op": "set", "object": "Forklifts", "key": "network", "value": "Net"},
 {"op": "set", "object": "Forklifts", "key": "homeNode", "value": "N_Dock"},
 {"op": "set", "object": "Rack", "key": "location", "value": "N_Rack"}]
```

- Nodes **must** be children of the network (`parent=Net`): a node elsewhere is not part of it (it is a plain point,
  enough as a Straight team's `homeNode`). `connect` between two nodes makes a two-way track (its length is the
  distance).
- With `travel = Network`, every element a member works at or carries from / to needs a `location` (a node of the
  network); a transport between elements without one fails at run time. `network` empty = the scene's.
- `Straight` travel needs no network: each element without a location gets a point in front of it.
- **Where a team stands:** `add_team` without `x` / `z` places it in front of the model (not at the origin, on an
  element); its members stand at the `homeNode` when it has one, else at the team. Overlaps (`simulean_apply`,
  `simulean_validate` `info`) include the members.
- **Read it back:** `simulean_model` gives the `network` — its `nodes` (`N_Dock (0, -4): Rack`: position x, z and the
  elements located there), its `tracks` with their lengths (`N_Dock – N_Rack: 10`; `→` one-way) and the `teams` on it;
  `simulean_model object=network` (or the network's name) answers it alone.

## Request order and priority

`processBy.priority` (an expression, default 1) is only a property of the request: **the team's queue uses it only
when its `requestOrder` sorts by it**, else first come, first served. Preemption always compares it.

- Serve by priority: `simulean_set object=Operators key=requestOrder value="|priority desc"`.
- Compare dispatch rules in a study: `namedOrders[0].name = Urgent`, `namedOrders[0].query = |priority desc`,
  `namedOrders[1].name = Oldest`, `namedOrders[1].query = |oldestItemTime`, then `requestOrderName = Urgent`; the factor
  `team:Operators.requestOrder=Urgent|Oldest`.
- Which member takes a request: `resourceOrder` (a query over the members; default the longest idle).
- `simulean_model section=logic` shows every requester's priority whenever the team can tell them apart.

## Preemption

`processBy.preemption = Resume` (the interrupted job goes on later with its remaining time), `Restart` (starts again),
`Abort`; it takes a member from a request of lower priority whose `processBy.preemptable = true`. **`preemptable` is on
by default** for every use but a repair crew (`repairBy.preemptable = false`): as soon as one use of a team has a mode and
a higher priority, it can take members from every other use of that team — set `preemptable = false` on the work that
must not be interrupted. `simulean_model section=logic` says on each preemptable use who can take its members
(`preemptable by Rush CNC`). Give the team `requestOrder = |priority desc` too, so waiting requests are served by
priority.

**Which use needs the mode:** only the one that takes members (a strictly higher `priority` number wins). Its mode also
decides how the job it interrupts goes on (`Resume`: the remaining time; `Restart`: from the start), whatever the
interrupted use's own mode; the interrupted use needs only `preemptable = true` (the default), and a mode of its own
only for its members' breakdowns (*What counts* below). The taken job asks the team again: any member may take it over
(the team's `resourceOrder`), not necessarily the same one (`jobOnMemberStop = WaitForSame` is for member stops only).
Statistics:
`team:T.preemptions`, `team:T.interruptions`, `Station.interruptions`, `Station.interrupted_time`,
`Station.interrupted_time_avg`.

**What counts:** `team:T.preemptions` = tasks taken by a request of higher priority; `team:T.interruptions` = those plus
the tasks a member's own breakdown or shift end (an Immediate stop) interrupted; `Station.interruptions` = the station's
jobs interrupted either way. **A member's stop interrupts only a job whose use has a preemption mode**
(`processBy.preemption` ≠ `None`): a preemptable station without a mode loses its member to priority (counted), but a
breakdown of its member does not interrupt it — its process runs on, as without preemption. To have breakdowns
interrupt (and count) at a station, give its `processBy.preemption` a mode (`Resume`). `simulean_validate` says so under
`info` for each station that uses a team with member breakdowns and has no mode.

## Jobs that need more than one team, holding and waiting

- Two teams (a crane and a driver): one requirement per team, `processBy.requirements[0].team = Cranes`,
  `processBy.requirements[1].team = Drivers`; `processBy.acquire = InOrder` holds the first while it waits for the next,
  the default `AllAtOnce` never holds and waits (`InOrder` in opposite orders can deadlock: `simulean_diagnose`).
- An Assembly Station task with its own crew: `tasks[i].processBy` (the same keys); what it does not set is the
  station's `processBy`.
- Do not start what cannot leave: `reserveExit = true` (Multi-Server, Combiner, Picking Station). A job waiting for its
  promised place downstream shows **BLOCKED** (its item is in the station; `state:BLOCKED` counts that wait).
- The part waits upstream until the operator is there: `processBy.acquireBeforeEntry = true` (Multi-Server only).
- A limit on a wait: `processBy.maxWait` (a time or an expression; it runs from the request until every requirement is
  granted — with `acquireBeforeEntry`, until the part enters) with `processBy.onTimeout` = `KeepWaiting`, `GiveUp`
  (frees what it holds and asks again) or `Reroute` (`processBy.timeoutTarget`); a Queue's customers renege with
  `maxWait`, `onTimeout = Reroute`, `timeoutTarget` (no connection needed: the target gets a link of its own; a
  connected target is also a normal route); a list puller `pullMaxWait`. While the target is busy a timed-out item keeps
  its place and leaves by whichever comes first, so `timeouts` can exceed the target's `input`. Statistics on the
  element that waits: `timeouts` (every timeout, whatever the action, `GiveUp` included), `timeouts:resource`,
  `timeouts:queue`, `timeouts:list_pull`. A reaction on `WaitTimedOut` acts on the timed-out item
  ([logic.md](logic.md)).
- At most N in a zone: a token pool — a team of N members with `travel = Abstract` (`simulean_add_element
  type="Token Pool"`) required by the zone's stations (`acquire = InOrder` before their own crew), never a counter
  property kept by reactions.

## Downtimes, shifts and repair crews

A downtime is a tool: `simulean_add_element type=Downtime name=CNCFailures`, then:

| Key | Values |
|---|---|
| `kind` | `MtbfMttr` (failures), `Shift` (a calendar), `Timetable` (`rows[i]`: `start`, `duration`, `target`), `Periodic` |
| `id` | the downtime's name in paths (`downtime:<id>.…`) and in `simulean_model`; empty (the default) = its object name — leave it empty (`simulean_set` always takes the object name) |
| `downtimeEnabled` | `false` = it stops nothing (its statistics read 0); a study switches it as `downtime:Id.enabled=true\|false` |
| `targets` | the elements it stops (names, comma-separated); `teamTargets` the teams whose members it stops |
| `timeToFailure`, `timeToRepair` | distributions (`ExponentialMean~2400`); `basis` = `CalendarTime` (each time to failure counts from the end of the repair) or `BusyTime` (only while the target is `PROCESSING`) |
| `shiftPattern`, `holidays` | `Mon-Fri 06:00-22:00` (needs the clock's `startDateTime`) |
| `periodicFirst`, `periodicPeriod`, `periodicDuration` | a periodic stop, in model time units (`periodicUntil` with `useUntil = true`) |
| `mode` | `KindDefault` (the default: `AfterCurrent` for a Shift, `Immediate` for the other kinds), `Immediate` (the work in hand pauses) or `AfterCurrent` (the job in hand finishes first, then the stop starts and lasts its whole duration — a row 30+20 on a job 0..100 stops 100..120; a Shift's stop still ends at the next window). By design a Timetable's return moves with the overrun, so a fixed-hour calendar written as rows drifts day after day (125-min jobs: back at 06:20, 06:40, 07:00…): **for stops at fixed clock times use `kind = Shift`**. Rows that wait for the same job overlap under `overlap = Allow` (the default: the shorter is absorbed); `overlap = Serialize` queues them; `blockOutput` left at its default blocks the finished items only with `Immediate` |
| `stateName` | the state the targets show while stopped, read as `state:<stateName>`; empty (the default) = the kind's: `BREAKDOWN` (MtbfMttr), `STOPPED` (Timetable), `OFF_SHIFT` (Shift), `SCHEDULED_DOWN` (Periodic) — or set a name of your own (`LUNCH`, `CALLED_AWAY`) |
| `repairBy.team`, `repairBy.requirements`, `repairBy.priority`, `repairBy.preemption` | a repair crew: the repair waits for a member of that team |

- **Several targets = independent copies.** The build makes one generator per target (a team target: per member), so
  each target has its own failure clock (its own random streams, keyed `<downtime>:<target>`), its own stops and its own
  repair: a failure of one target does not stop the others, and with a repair crew each stop asks the shared team for
  its own member (two failed targets, one mechanic: the second waits). One Downtime on `CNC_A, CNC_B` behaves as two
  Downtimes with the same settings; over all its targets `stops`, `downtime_total`, `downtime_scheduled`, `repairs` and `repair_waits` are
  summed, and the means pooled over every repair: `repair_wait_avg` weighted by each target's `repair_waits` (crews
  arrived), `repair_time_avg` by its `repairs` (completed) — a repair still open at the end counts in the wait only;
  `downtime:Id@CNC_A.<statistic>` each one's. A `Periodic`, `Shift` or `Timetable` stop is the same schedule on every
  target (a timetable row with a `target` only on that one).
- **A single stop at time T:** `kind = Timetable`, `rows[0].start = T`, `rows[0].duration = D`.
- **A member's breakdown** (`teamTargets = Operators`): the team's `jobOnMemberStop` decides — `HandOver` (the default:
  the job asks again and any other member takes it) or `WaitForSame` (the job waits for that member) — for a job whose
  use has a preemption mode (above, *What counts*). Each member has its own failures: `downtime:Id@Operators_1.stops`
  (the member's name; an unnamed member is `<Team>_<n>`).
- **The paths to copy:** `simulean_model object=<team or downtime id>` — each team's and downtime's `metrics` list the
  path, the statistic names and, for a downtime on several targets, `eachTarget` with the target names (a team target's
  members), e.g. `{"path": "downtime:operatorFail.<statistic>", "statistics": "stops, downtime_total, repairs, …",
  "eachTarget": "downtime:operatorFail@<target>.<statistic>", "targets": "Op1, Op2"}`. A downtime also shows its `stateName` and, a
  Timetable, its `rows` (`start+duration target state mode`, the first 8; `detail=full` all); `section=logic` lists every
  downtime with its `targets`.
- Statistics: `downtime:Id.stops`, `downtime:Id.downtime_total` (the time the targets actually stood — an AfterCurrent
  stop from the end of the job in hand; equal to the target's `state:<stateName>` time), `downtime:Id.downtime_scheduled`
  (the stop time as scheduled: each stop's duration; a Shift's off-shift windows); with a repair crew `downtime:Id.repairs` (completed),
  `downtime:Id.repair_waits` (crews arrived, i.e. repairs started), `downtime:Id.repair_wait_avg`,
  `downtime:Id.repair_time_avg` (one target of several: `downtime:Id@M1.repairs`).
  `downtime:Id.repair_wait_avg` = the mean time from the stop's start to the crew's arrival at the element (the wait for a free
  member **and its travel**), over the crews that arrived; `downtime:Id.repair_time_avg` = crew there → end of the repair (a
  pause of a preempted repair included), over the repairs completed. The stop lasts the wait + the repair. Every time
  statistic is in the model time unit.
- Timed work of a station is paused by a stop and goes on afterwards; a team's member statistics are in the
  `resources` block of a run with `detail=full`; study metrics read the team (`team:Operators.utilization`,
  `team:Operators.state:HELD`).
- **`team:T.utilization`** = the members' mean busy share: `PROCESSING`, `SETUP`, travel (empty and loaded),
  `LOADING`, `UNLOADING`, `HELD` (assigned but kept waiting: for the part, the job's other members, room downstream or
  its item leaving), `WAITING` and `TRAFFIC_WAIT` count as busy; `IDLE`, `WAITING_DEPARTURE` and the stop states
  (breakdown, call-away, shift off) do not. Hands-on time alone: `team:T.state:PROCESSING` (+ `state:SETUP`); the
  `state:X` shares add up to 1. At a machine that breaks down mid-job the operator stays with the job, `PROCESSING`,
  until the process ends (no preemption mode needed or used).
