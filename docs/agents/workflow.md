# Modelling with SimuLean: the workflow

The core an agent reads before the first call: the loop, the argument vocabulary, the build format, the run settings,
the metrics grammar and the traps that cost retries. Then read **only the topic modules the task needs** (index below).
Arguments in full: [commands.md](commands.md); errors: [errors.md](errors.md). Calls are written `command arg=value …`.

## Topic index

| Modelling … | Read |
|---|---|
| teams (operators, vehicles, cranes), travel and home nodes, request order and priority, preemption, two-team jobs, wait limits, token pools, downtimes, shifts, repair crews, member breakdowns | [topics/resources.md](topics/resources.md) |
| a warehouse or line supply: storage racks and lanes, stock lists and pulls, pick faces, replenishment, packing, milk runs and empties, line-side unpacking | [topics/warehouse.md](topics/warehouse.md) |
| decisions: routing by a rule, admission, release order, gates, reactions, item looks, C# choice rules (the escalation ladder) | [topics/logic.md](topics/logic.md) |
| sequence-dependent setups, data tables, arrivals from a schedule, a database, distributions fitted to data | [topics/setup-and-data.md](topics/setup-and-data.md) |

## The loop

1. **Orient** — an existing scene: `simulean_model` (one answer, all by name; `info` is the model card); its decisions:
   `simulean_model section=logic`. An empty project: `simulean_types` (the Core entries, one line per other pack;
   `pack=Warehousing`, `pack=all`). Never edit an example scene: copy it
   (`simulean_models action=copy_example example=…`) or start one (`simulean_models action=new model=… timeUnit=min`).
2. **Build** — `simulean_apply` with the whole list of edits (below), `simulean_repeat` for repeated cells.
3. **Check** — `simulean_validate factors=… metrics=…`: builds without running, echoes the run `settings`, lists every
   error and warning with "did you mean". Fix them all before the first run.
4. **Run** — a short `simulean_run_headless` with `metrics`; read `settings` and `endReasons` (`{"Horizon": 3}`: how
   many runs ended for each reason; `deadlocked` / `stalled` count the runs that did) first.
5. **Diagnose** — a stall or deadlock: `simulean_diagnose`.
6. **Study / optimise** — one call per study (`factors`, `scenarios`, `where`, `derived`, `replications`).
7. **Results / present** — `simulean_results`, `simulean_capture`, `simulean_record_shot`.
8. **Save** — `simulean_models action=save` (the open model; only one these commands created or opened in this editor
   session — never the editor's generic scene save or `eval`, which would also save a user's or an example scene).

| Need | Call |
|---|---|
| A type's fields, list-item fields, notes (`help`), catalog keys | `simulean_types type=Queue,MultiServer` (any spelling: `multi server`; `fields=processBy` one group; `detail=full` one type per call; both add the inspector's tooltips under `tips`) |
| An object's current values | `simulean_params object=Buffer` |
| The names the commands use; the model card | `simulean_vocabulary` (an index of its sections; `section=paths,statistics`, `section=arguments.study`); `simulean_card` (`field=kpis value=…` writes) |

`simulean_model` is compact: empty texts and the scene's automatic points are left out, a long or generated list (a
rack's lanes) is one line per field (`lanes[*].capacity`); `detail=full` lists every value.

## Build

Elements connected by their **Next Elements** (`simulean_connect from=A to=B`). Names are unique; name objects so a
study path reads well (`Welding.capacity`). Every answer carries `errors` and `warnings`: read them.

**Many edits, one call** — `simulean_apply operations=[…]`: `add`, `set`, `connect`, `disconnect`, `reorder`, `remove`,
`rename`, `move`, `add_team`, `invoke`, `repeat`; all or nothing; every bad operation is named at once; `validateOnly=true`
checks only. **Operations run in list order:** add an object (`add`, `add_team`) before a `set` that names it. The
answer is a summary (operations per kind, objects `created`, racks `rebuilt`, tools `placed`, warnings); `detail=full`
gives one result per operation. A List, Downtime or Reaction added without `x` / `z` goes beside the model (a row behind
it, +Z, `placed`): its place does not change a run, only the picture. A team added without `x` / `z` is placed once the
whole list stands, in front of the model (−Z, `teamsPlaced`).

**Moving** — `simulean_move object=… x=… z=…` (or the `move` operation) moves an element, a team or a tool with what it
holds; the answer gives `from`, `at`, the footprints it now `overlaps` and, when the place enters a run, the warning
`travel distances changed`: an element a Straight team walks to (no location node), a Straight team without a home node,
a transport node (its tracks' lengths). Undoable.

```json
[{"op": "add", "type": "Source", "name": "Src"},
 {"op": "add", "type": "Queue", "name": "Buf", "x": 4},
 {"op": "add", "type": "MultiServer", "name": "Srv", "x": 8},
 {"op": "add", "type": "Sink", "name": "Exit", "x": 12},
 {"op": "set", "object": "Src", "key": "mode", "value": "InterArrival"},
 {"op": "set", "object": "Src", "key": "interArrivalDistribution", "value": "ExponentialMean~5"},
 {"op": "set", "object": "Srv", "key": "delayStrategy", "value": "Triangular~3~4~6"},
 {"op": "connect", "from": "Src", "to": "Buf"},
 {"op": "connect", "from": "Buf", "to": "Srv"},
 {"op": "connect", "from": "Srv", "to": "Exit"}]
```

- **Spacing by the footprints:** a Queue's pad is 3 × 3 m, a Source / Sink 1.2 × 1, a Multi-Server 1.3 × 0.9: centres
  about 4 m apart along the flow (`x`), rows 6–7 m apart. Each answer gives the size on the floor — `simulean_model`
  `at` / `size` [x, z], `simulean_add_element` `size`, `simulean_apply` `sizes` and `overlaps`, a rack's built extent in
  `rebuilt` — and `simulean_validate` names overlapping footprints in `info`.
- **A distribution is one text:** `Triangular~min~mode~max`, `Uniform~a~b`, `Normal~mean~sd`, `Expression~item.t * 2`,
  `Gamma~shape~scale` (mean shape × scale), `LogNormal~mu~sigma` (of ln x: mean e^(mu + sigma²/2)),
  `Weibull~shape~scale`, or a number (a constant); `simulean_fit` writes any of them with `into`. **A new station's
  process time (`delayStrategy`) is `Constant~5` until set.** **The exponential has two forms:** `Exponential~x` takes a
  **rate** (`Exponential~0.2` = a mean of 5), `ExponentialMean~x` a **mean** (`ExponentialMean~5`, as in the example
  above): a mean of 5 written `Exponential~5` is a mean of 0.2. The scene stores the mean as its rate, so
  `simulean_params` shows `Exponential~0.2`; `simulean_validate` lists each exponential's mean in `info`.
  `simulean_types` lists every type's keys.
- **Label values are expressions: text needs quotes.** `createLabels = type = 'A'` (not `type = A`, which reads a label
  `A`). A list value splits at the commas outside parentheses and quotes:
  `exitLabels = mix = pick(0.7, 'std', 0.3, 'rush'), due = now() + 480`.
- **A new Source sets `prio = 0`** (`createLabels`); replace or clear it as needed.
- **Source arrivals (`mode`):** `InterArrival` — the first item at t = the first draw (not at 0), then one per drawn
  interval; `Schedule` — a table's rows at their `Time`, `Q` items each (`simulean_load_table`, columns `Time`, `Name`,
  `Q`, labels); `RateTable` — random arrivals (Poisson) whose rate changes by period: `ratePeriods[i].start` /
  `ratePeriods[i].rate` (per model time unit) / `ratePeriods[i].labels`, or a table with the columns `Start`, `Rate`
  (`Name`, labels) loaded by `simulean_load_table` (a table with `Start` and `Rate` and no `Time` sets the mode). Before
  the first start the rate is 0; after the last start its rate holds to the end — **end with rate 0 to close the door**:
  no arrival comes after it, so a run with `stopWhenEmpty` ends at the last exit (a clinic's day). A study varies
  `Src.ratePeriods[1].rate` or `Src.demandFactor` (every rate). **A full receiver:** an InterArrival / RateTable source
  holds its item (`BLOCKED`) and draws the next arrival only after it left — the arrival clock waits, so fewer items
  arrive over the horizon (for customers who wait further back, put an unlimited Queue, `capacity = 1000000`, between
  the Source and the limited one: its stay is that wait); a Schedule source keeps its timetable and its items wait in
  order (`arrivalsOnStop` is for downtimes).
- **A statistic per item:** `tallies = time_in_system = age` (each item `name = expression`, recorded as each item
  **enters**; several comma-separated); read as `Exit.tally:time_in_system:mean` (`std`, `min`, `max`, `p50`, `p90`,
  `count`); `tallySplit = prio` splits it by a label. `age` = now − `created` (the time since the item was created, not
  since it entered this element): at a Queue's entry it is the time spent before the Queue. **Built in, no tally
  needed:** a Sink's time in system `Exit.tis_avg` (`tis_std`, `tis_min`, `tis_max`, `tis_p90`: each item's age as it
  enters the Sink, after the warm-up — a Sink's `stay_avg` is 0) and a Queue's wait percentile `Buf.stay_p90` (zero
  waits included, as its `stay_avg`). Elsewhere the stay per item: the element's `entryLabels = tin = now()` and the
  next element's `tallies = stay = now() - item.tin`; per product: `tallySplit`.
- **A user property:** `simulean_set object=Beds key=properties.beds value=3` (read as `get('Beds','beds')`).
- **A list item's field:** `actions[0].kind`, `stops[0].emptiesOrigins` (an index past the end adds an item). **Items by
  id:** in place of the index, an item's id (`laneId`, `id`, `name` or `sku`) or a pattern — `lanes[B0_S0_L0].node` one
  lane, `lanes[B0_*].node` every lane of bay 0, `lanes[B0_*|B1_*]`, `lanes[*]` every item (`*` any text, `?` one
  character); the answer gives `matched` and the `items`.
- **A pull query is one text:** `pullQuery = sku == 'A'; qty > 0 | due desc` (conditions `;`, then `|` and the order).
- **An unlimited Queue:** there is no unlimited value (default 100, minimum 1): give a capacity no run reaches,
  `capacity = 1000000`. A run that fills a Queue left at 100 says so in `warnings` ("Queue 'Depot' reached its capacity
  100, the default …"). The model's own run-time warnings (a label read that nothing set, an unpacker given an empty
  carrier…) are in a run's `warnings` too, each cause once (`simulean_diagnose` lists one run's). A Queue's `stay_avg` counts every item that left, those that passed straight through with a stay
  of 0 included (`stay_min` 0). Stays count only the items that left: under overload read them with `content` at the
  end and `Src.state:BLOCKED` (items still inside are not in `stay_avg`).
- **Repeated cells:** build one, then `simulean_repeat group=Q1;S1 count=7 offset=0,0,6 connectFrom=Router>Q1 connectTo=S1>Exit`.
- **Mistakes:** `simulean_disconnect`, `simulean_remove`, `simulean_rename`, `simulean_reorder`, `simulean_move`. Component actions
  (rebuild, fill a matrix): `simulean_invoke object=… action=…`.

## Run settings — the time unit

**A new model runs in seconds.** Every time of the model (delays, arrivals, horizon, warm-up) is in its time unit:
choose it when the model is made, `simulean_models action=new model=Line timeUnit=min horizon=480 seed=1 warmup=60`
(`seed` = the clock's Fixed seed mode with that seed, `warmup` in the model's unit; the answer echoes them), or set it on
the clock. Every time statistic (`stay_avg`, `repair_wait_avg`, `interrupted_time`, …) is in that unit too: convert with
`derived`.
`simulean_validate` and every run answer echo the effective `settings` (time unit, horizon, warm-up, end condition,
seeds): check them before reading results.

The clock is the GameObject `SimClock` (component `UnitySimClock`; `simulean_set object=SimClock key=… value=…`):
`timeUnitSeconds` (60 = minutes), `maxTime` (horizon), `warmupTime`, `startDateTime`, `seedMode` (`RandomEachRun`, the
default, or `Fixed` with `seed`), `stopAt` / `stopAtCount`, `stopWhenEmpty`, `stopWhenNoProgressFor`, `seriesInterval`.
A study's arguments override them for its runs: `horizon`, `warmup`, `noProgress`, `seed`. `stopWhenEmpty` (off by
default) ends a run only when nothing can happen any more: an InterArrival / RateTable source with arrivals to come
keeps it running (close a rate table with rate 0, or use `stopAt`); a momentarily empty model never ends it.

## Run, diagnose

- Short run: `simulean_run_headless horizon=480 replications=3 metrics=out=Exit.input;wip=Buf.content_avg` — each metric
  `{mean, halfWidth, n}` to 6 significant digits; the `model` block (end time, makespan, deadlock / stall times) only
  when a run deadlocked, stalled or ended on no progress. Without `metrics` (or with `detail=full`) every digit, the
  `model` block, every element's and resource's KPIs and `perRun` (each run's seed, end reason and metric values).
- **A large study** (≈ 0.45 KB per scenario, more with `detail=full`): an answer over ~40 KB comes as `table` — the first
  25 scenarios by the first metric, mean and half-width — and `page`, which says it is cut and gives `next`, the exact
  call of the first page. Every scenario is a page away, without running again: `simulean_results action=read
  resultSet=last top=25 offset=25 sort=-out` (`sort` a metric, `-` or `desc` descending; follow `page.next` until a
  page has none); `detail=full` for every digit and every run; `simulean_results action=export formats=csv` for files.
  `simulean_run_status` takes the same `top` / `offset` / `sort`.
- Longer than ~1 minute: `simulean_run_start` (same arguments), then `simulean_run_status wait=50`. Never sleep.
- Stall / deadlock (`endReasons`, `waiting`, `model.deadlocked`): `simulean_diagnose` — every element holding items
  (state, items, what it really waits for: a team, room downstream), each team's pending requests and members, the
  lists, the decisions involved; a loop of full elements waiting for each other is named in `notes` ("Circular wait:
  A → B → C → A"). `noProgress=T` ends a run that stopped moving beside a periodic reaction.

## Study

| Need | Argument |
|---|---|
| Factors, every combination | `factors=Buf.capacity=2|4|8;team:Welders.members=1|2` |
| Only some combinations | `where=Buf_capacity + team_Welders_members <= 8` |
| Hand-picked scenarios | `scenarios=[{"name": "base", "Buf.capacity": 4}]` — every scenario gives every factor any of them gives (the error names each one missing with the model's value to copy); one may carry its own `"replications": 10` |
| Values computed from metrics and factors | `derived=rate=out/480;profit=10*out-100*Buf_capacity` — a factor is named as in `where`, at the run's scenario level; a metric is one path, so a sum (L = Lq + Ls) is `metrics=lq=Line.content_avg;ls=Clerk.content_avg derived=L=lq+ls` |
| A statistic per label value | `metrics=tis=Exit.tally:time_in_system[*]:mean` |
| Confidence, warm-up | `confidence=0.9`, `warmup=100` (counts, stays, tallies and states restart at it) |
| Compare scenarios on common seeds | `compareTo=base` (a scenario's name, or its position `1`): every other scenario gets `paired`, per metric `{diff, halfWidth, n}` = its runs minus the reference's runs of the same seed — far tighter than two means' half-widths |
| Spread over the replications | `detail=full`: each metric also `min` / `max` (two runs or more) |

The answer's `settings` echo what ran (horizon, warm-up, replications and seeds, `replicationsPerScenario` when the
scenarios' own counts differ); `simulean_validate` takes the same `horizon` / `replications` / `seed` to echo them before
the run. A study lists each factor's levels (`1|2|3`); a range `lo..hi` is for `simulean_optimize_start` only. The
shorthand `path=levels` splits at the first `=` outside `[…]`, so a level may hold `=` (`Exit.tallies=tis=age`) and a
`derived` expression may compare (`late=wait>=60`).
**Precision:** `halfWidth` ∝ 1/√n — to halve it run 4× the replications; report a difference only when it exceeds the
half-widths — or, better, compare with `compareTo`: the replications of one seed share their random numbers, so a
`paired` difference whose `diff ± halfWidth` excludes 0 is real even when the two means' intervals overlap.

**Factor paths:** `Owner.key` (an element's catalog key), `team:T.members`, `team:T.members[skill=weld]`,
`team:T.requestOrder=Urgent|Oldest`, `downtime:Id.key`, `downtime:Id.enabled=true|false` (a downtime on / off), `reaction:Name.reactionEnabled=true|false`,
`Name.properties.key`, `Src.table`, and a text setting by its texts — a priority expression or a request order written in
place, in JSON when a text holds `|` or `;`: `{"Station.processBy.priority": ["prio", "table('ODF', item.odf, 'prio')"],
"team:T.requestOrder": ["", "|priority desc"]}` (each text one level, checked when the run is built; a request order written in place is `condition|order`, so sorting alone starts with `|`). `simulean_params` lists them under `textFactors` with their current text; names and references (`processBy.team`) are never factors. **Metric paths:** `model.makespan` (the last entry into an element without outputs —
not a stall test), `model.deadlocked` / `model.stalled` (0/1 per run: the mean is the share of runs); per element
`input`, `output`, `content`, `content_avg`, `content_max`, `stay_avg`, `stay_max`, `utilization`, `state:PROCESSING`
(`Exit.input`, `Srv.stay_avg`), a Queue's `stay_p90`, a Sink's time in system `tis_avg` / `tis_p90` (`Exit.tis_avg`); `team:T.utilization`, `team:T.state:HELD`; `list:L.wait_avg`; `reaction:Name.fired`;
`downtime:Id.stops`; `Element.tally:name:p90`; an element's user property at the end of the run, `Element.property:NAME`
(`Rush Queue.property:late`: a number the build gives it or a reaction sets; validate refuses an unknown one with a
did-you-mean). The topic modules list their own statistics. **`utilization`** of a station is its `PROCESSING` share
only (not `SETUP`, `BLOCKED`, `WAITING_FOR_RESOURCE` or stops; several slots: their mean, while the element's `state:X`
is its busiest slot's — `PROCESSING` while any slot works, so `state:PROCESSING` ≥ `utilization`; a Queue:
`PROCESSING` + `BLOCKED`); of a team, the members' mean busy share (every member state but `IDLE`, `WAITING_DEPARTURE`
and the stop states — `HELD` and `WAITING` count as busy). `simulean_validate` names the closest when one is wrong.
**A team's and a downtime's paths are in `simulean_model`** (each one's `metrics`: the path, the statistic names, a
downtime's `eachTarget` `downtime:Id@<target>` with its targets — a team target's members by name): copy them.

**State names** for `state:<NAME>` (the share of time in it): stations `IDLE`, `PROCESSING`, `BLOCKED` (done, output
refused), `WAITING_FOR_RESOURCE`, `SETUP`, `STARVED` (a Paced Line or a Pick Face: material missing), `RECEIVING` (a
Combiner or Assembly Station holding its main item, waiting for the components; `IDLE` = no main item), `TRAVELING`;
stops `STOPPED`, `BREAKDOWN`, `WAITING_FOR_REPAIR`, `SCHEDULED_DOWN`, `OFF_SHIFT` (a downtime's own `state` when set);
team members (`team:T.state:…`) `IDLE`, `PROCESSING` (working a station's process; `simulean_diagnose` shows a busy
member so), `SETUP`, `HELD` (assigned but kept waiting: for the part, the other members of the job, room downstream, or
until its item leaves), `WAITING`, `TRAVELING_EMPTY`, `TRAVELING_LOADED`, `LOADING`, `UNLOADING`, `TRAFFIC_WAIT`,
`WAITING_DEPARTURE` (a train at the depot, not busy) and the stop states. A run with `detail=full` shows each object's
states.

## Optimise, results, present

- `simulean_optimize_start factors=Buf.capacity=1..10 metrics=out=Exit.input fitness=out algorithm=genetic replications=3
  horizon=480`, then `simulean_optimize_status wait=50`. The fitness is maximised; `lo..hi` a range, `a|b|c` values.
- `simulean_results` lists result sets; `action=read` (the last study as its command answered it, a page at a time:
  `top` / `offset` / `sort`, `detail=full`), `action=export`, `action=chart kind=bar yAxis=out`.
- **What is written to disk:** the `last` set of a run, study or optimisation through these commands lives only in the
  editor's memory (never stored, gone at the next run or domain reload). Only `action=save` (the project's results
  store), `action=export` (files in the results folder), `action=chart` (an image), `simulean_capture` (a PNG) and
  `simulean_record_start` (a take's folder) write. `simulean_results action=delete path=<what one of them answered>`
  moves such a file or folder to the OS trash, and `action=delete resultSet=<id>` removes a set that `action=save`
  stored — only what these commands wrote in this editor session (the user's files are refused). Delete what you do not
  need kept; name in your report what you leave.
- `simulean_capture` (a framed PNG: the model with its nodes and team members, the flow, lists and tracks drawn —
  `connections=all` adds resources (every use of a team: process, setup, transport, repair crews), locations, downtimes
  and logic; then Read the file); a video: `simulean_record_shot`,
  then `simulean_record_start`.

## Traps that cost retries

- The time unit (above): seconds unless chosen.
- An unquoted label text (`type = A`) — validate warns *did you mean the text 'A'*.
- A type name: any spelling works (`MultiServer`, `Multi-Server`); a wrong one gets a short did-you-mean.
- A rack's layout (`bays`, `slotsPerBay`, `levels`, `depth`) set by `simulean_set` rebuilds its lanes at once,
  `simulean_apply` when its list ends: set `lanes[...]` in a second `apply`; `levels` counts the ground level
  (L0 … L(levels − 1)).
- A milk run's `emptiesOrigins` are the Queues the empty carriers wait in, never the unpacker
  ([warehouse](topics/warehouse.md)).
- A Network team without `homeNode` starts at the network's first node (validate says which, under `info`); a
  Straight team at its own position.
- A priority (`processBy.priority`) changes nothing unless the team's `requestOrder` sorts by it or preemption uses it
  ([resources](topics/resources.md)).
- A Queue has two rule parameter lists: `ruleParameters` (its release rule, `offerRule`) and
  `outputStrategy.ruleParameters` (its routing rule); a list without its rule warns ([logic](topics/logic.md)).

## Patterns that keep a session short

One `simulean_apply` instead of many `simulean_set`; `simulean_repeat` for copies; `factors` / `scenarios` /
`replications` instead of loops; `_status wait=50` instead of sleeping; `simulean_diagnose` instead of a dump script;
`simulean_types` (fields, `listItems`, `help`) and the topic module instead of reading C# or the user guides.
