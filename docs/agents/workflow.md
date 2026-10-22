# Modelling with SimuLean: the workflow

How an agent builds, checks, runs, studies and presents a model through the `simulean_*` commands. Arguments in full:
[commands.md](commands.md); errors: [errors.md](errors.md). Calls are written `command arg=value …`.

## 1. Orient

| Need | Call |
|---|---|
| What can be placed | `simulean_types` (elements, tools, presets of the Library) |
| A type's fields and the keys a study varies | `simulean_types type=Queue` |
| An object's current values | `simulean_params element=Buffer` |
| The whole model, by name | `simulean_model` (`parameters=false` for the structure only) |

Start every session on an existing scene with `simulean_model`: one answer instead of one call per object.

## 2. Build

The flow is **elements connected by their Next Elements** (`simulean_connect from to`): a Source creates items, buffers
(Queue, lanes) hold them, stations (Multi-Server, Delay, Combiner…) process them, a Sink takes them out. Resources
(teams of operators, vehicles) are requested by stations and transports; lists, downtimes and reactions are tools that
act on elements.

- **One edit:** `simulean_add_element type=UnityQueue name=Buffer x=2`, `simulean_set element=Buffer key=capacity value=5`,
  `simulean_connect from=Src to=Buffer`. A field of a list item by path: `simulean_set element=R key=actions[0].kind value=Stop`.
  An element's user property (read by expressions as `get('Name','key')`, varied by studies as `Name.properties.key`):
  `simulean_set element=Beds key=properties.beds value=3` (created when missing).
- **Many edits — one call:** `simulean_apply operations=[…]` runs a JSON list of `add`, `set`, `connect`, `disconnect`,
  `remove`, `rename`, `add_team`, `invoke`, `repeat`, all or nothing, in one undo step:

  ```json
  [{"op": "add", "type": "UnitySource", "name": "Src"},
   {"op": "add", "type": "UnityQueue", "name": "Buf", "x": 2},
   {"op": "add", "type": "UnityMultiServer", "name": "Srv", "x": 4},
   {"op": "add", "type": "UnitySink", "name": "Exit", "x": 6},
   {"op": "set", "element": "Src", "key": "mode", "value": "InterArrival"},
   {"op": "set", "element": "Src", "key": "interArrivalDistribution.strategyType", "value": "Exponential"},
   {"op": "set", "element": "Srv", "key": "delayStrategy.strategyType", "value": "Triangular"},
   {"op": "connect", "from": "Src", "to": "Buf"},
   {"op": "connect", "from": "Buf", "to": "Srv"},
   {"op": "connect", "from": "Srv", "to": "Exit"}]
  ```

- **A repeated sub-network** (a cell per bed, a line per product): build it once, then
  `simulean_repeat group=Queue1;Station1 count=7 offset=0,0,6 connectFrom=Router>Queue1 connectTo=Station1>Exit` —
  each copy is renamed (`Queue1_1`, `Station1_1`…), its inner connections point to its own members.
- **Resources:** `simulean_add_team name=Welders members=2 travel=…`, then the station's `processBy` / a transport's
  `transportBy` names the team.
- **Data:** `simulean_load_table source=Src file=arrivals.csv` puts a Source on a schedule; `simulean_query import=…`
  loads a file into the project database, `simulean_query sql=…` reads it.
- **Distributions from data:** `simulean_fit values=… into=Srv.delayStrategy` ranks distributions and writes the best
  into the field.
- **Component actions** (fill a travel matrix, rebuild a floor zone, bake a walk grid): `simulean_invoke object=Matrix`
  lists them, `simulean_invoke object=Matrix action=FillStraightLine` runs one.
- **Mistakes:** `simulean_disconnect`, `simulean_remove` (references to the object cleared and listed), `simulean_rename`.

Names are unique: a duplicate is refused. Name objects so that a study path reads well (`Welding.capacity`).

## 3. Check

`simulean_validate set=… metrics=…` extracts and builds the model — with the factors and metrics you plan to study —
without running it, and lists the errors and warnings a run would meet ("did you mean" for a misspelt element, key or
statistic). Fix them before the first run.

## 4. Run

- A short run: `simulean_run_headless maxTime=480 replications=3 metrics=out=Exit.input;wip=Buf.content_avg`.
  The answer opens with the effective `settings` (time unit, horizon, warm-up, end condition, seeds); each metric is
  `{mean, halfWidth, n}`; `endReasons` says how each replication ended.
- Anything longer than about a minute: `simulean_run_start` (same arguments), then `simulean_run_status wait=50` until
  it answers `completed`. **Never** sleep in `eval` to wait.
- Without `metrics` the answer includes every element's and resource's KPIs (`detail=full`): useful once, large.

## 5. Diagnose

When a run stalls or deadlocks (`endReasons`, a `waiting` list in the answer, `model.deadlocked`), call
`simulean_diagnose` (`time=0`: until nothing can happen, or `time=T`): every element holding items, stopped or busy —
its state, items and what it waits for —, each team's pending requests (who asks, how many, for how long) and members
(state, task, for whom, since when), the lists. `set=` tries a what-if (one level per factor).

A model can stop moving without being *stalled*: shifts or reactions still schedule events. `progress` gives the last
item movement, the oldest pending request and the longest task; `notes` says when one of them reaches a quarter of the
run. That is a fact to check, not an error: the blocking may be what the design does (too little capacity, a rule that
holds a resource) or a mistake in the model's logic.

## 6. Study

All in one `simulean_run_headless` / `simulean_run_start`:

| Need | Argument |
|---|---|
| Factors, every combination | `set=Buf.capacity=2|4|8;team:Welders.members=1|2` |
| Only some combinations | `where=Buf_capacity + team_Welders_members <= 8` (paths with every non-letter / digit as `_`) |
| Hand-picked scenarios | `scenarios=[{"name": "base", "Buf.capacity": 4}, {"name": "big", "Buf.capacity": 8, "replications": 10}]` |
| A value computed from the metrics | `derived=days=span/24;rate=out/480` (names of metrics, or derived ones before it) |
| A statistic per label value | `metrics=tis=Exit.tally:time_in_system[*]:mean` → `tis[A]`, `tis[B]`… (the element splits its tallies by a label: `tallies`, `tallySplit`) |
| Confidence of the half-widths, a warm-up | `confidence=0.9`, `warmup=100` |
| A designed experiment saved in the scene | `experimenter=MyExperimenter` |

**Path grammar.** A factor is `Owner.key` (an element's catalog key), `team:Team.members`,
`team:Team.members[skill=weld]`, `downtime:Id.key`, `reaction:Name.key`, `Src.tableSource` (a file per level). A metric
is `model.makespan`, `Element.input`, `Element.content_avg`, `Element.state:BUSY`, `team:Team.utilization`,
`list:List.wait_avg`, `reaction:Name.fired` or `Element.tally:name:p90`. The statistic names are those of
`ResultMetrics` (architecture §3.11); `simulean_validate` names the closest when one is wrong.

## 7. Optimise

`simulean_optimize_start parameters=Buf.capacity=1..10;team:Welders.members=1..4 metrics=out=Exit.input;wip=Buf.content_avg
fitness=out-0.5*wip algorithm=genetic algorithmSettings=populationSize=20;generations=15 replications=3 runTime=480`,
then `simulean_optimize_status wait=50` until `completed`: the best candidate, its fitness and its metrics re-run for
every replication. The fitness is maximised (negate to minimise). `lo..hi` is a range (a real key — a property —
is searched continuously); `a|b|c` searches those values only (whole numbers of a property: `beds=1|2|3`). A scene Optimizer set up in the inspector runs with
`optimizer=Name` (the other arguments override its settings).

## 8. Results

`simulean_results` lists the last result set and the stored ones; `action=read` gives a set's metrics per scenario,
`action=save name=…` keeps one in the project's store, `action=export formats=csv;excel`, and
`action=chart kind=bar y=metric:out` (or `kind=scatter y=metric:out x=param:Buf.capacity`,
`y=stat:element:Srv;Buf:utilization`) draws a PNG / SVG.

## 9. Present

- `simulean_capture` renders a framed PNG of the model (`targets=` some elements, `elevation`, `azimuth`); open the file
  to see it.
- A video: `simulean_record_shot` (shots framing elements, orbits, fixed poses, captions), then `simulean_record_start`
  and `simulean_record_status wait=50`: an MP4. Never place cameras or encode frames by hand.

## Patterns that keep a session short

| Instead of | Do |
|---|---|
| one `simulean_set` per field over a whole model | `simulean_apply` with the list |
| building the same cell N times | build one, `simulean_repeat` |
| a C# loop over scenarios or seeds | `set` / `scenarios` / `replications` in one run |
| sleeping and polling a long job | `_status wait=50` |
| a script that dumps the model's state | `simulean_diagnose` |
| guessing a key or a statistic | `simulean_types type=…`, `simulean_params`, `simulean_validate` |
| post-processing results in code | `derived` metrics, `[*]` splits, `simulean_results` |
