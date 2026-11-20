# Errors and warnings: what they mean, what to do

Every `simulean_*` answer carries `errors` and `warnings` — what was logged while the command ran. A command that fails
answers with its message instead. Messages name the object, the key, the value and, when they can, the closest valid
option ("Did you mean …"): read them before retrying. `{…}` below stands for a name or value.

**How messages are wrapped**

- A command that fails after warnings were logged: `{message} Warnings logged: {w1} | {w2}` — the warnings usually say
  why.
- `simulean_apply`: `{n} of {m} operation(s) failed — nothing was changed: operation {i} ({kind} …): {message} | …`. The list
  is all or nothing and every operation is tried: fix every one named (a later one may fail because of an earlier one) and
  send the whole list again; `validateOnly=true` checks a list without changing the scene.
- A study that cannot run: `The study cannot run: {e1} | {e2} …` — each is one of the [study checks](#2-study-checks).
  A search: `The search cannot run: …`.
- A replication that fails while the others complete: its scenario reports `runs.failed` and `firstError`
  (`{ExceptionType}: {message}`).

## 1. Command arguments

| Message | Cause | What to do |
|---|---|---|
| `No object '{name}' in the open scenes. Did you mean …` | The name or path is not in the scene | Use the suggestion; `simulean_model` lists the objects |
| `Several objects are named '{name}': use the path (…)` | Ambiguous name | Pass the hierarchy path, or `simulean_rename` one |
| `'{name}' is already the name of '{path}' … Choose another, e.g. '{free}'` | Names are unique (add, rename, add_team, repeat) | Use the suggested free name |
| `'{object}' has no field '{key}'. Did you mean … Fields: …` | Wrong `simulean_set` key | Pick one listed; `simulean_params` shows them with their values |
| `value is required (value="" clears the field).` | `simulean_set` without `value` (a JSON `null` reads as missing too) | Give the value; `value=""` clears the field (an empty text or list, no object reference), as `simulean_apply`'s `set` with `"value": ""` |
| `'{path}' ({type}): '{value}' is not a whole number / a number with '.' as the decimal point / true or false (e.g. …)` | The value does not parse | Invariant format: `4`, `2.5`, `true` |
| `'{path}': '{value}' is not one of {names}` | Wrong enum value | Use one of the names listed |
| `'sendTo': 'Stock' is not one of Elements, List. 'Stock' is the list: set sendTo = List and pushToList = Stock.` | The list given as the mode (`receiveFrom`: `pullFromList`) | Two fields: the mode `List`, then the list |
| `'{object}' has no field 'rule.X'. A rule's parameter is set in its site's list: …` | `rule.X` is a study factor's form | `simulean_set key=outputStrategy.ruleParameters value="X = 0.5"` (a Queue's release rule: `ruleParameters`) |
| `'{path}' ({type}) is not set by text: set its fields (e.g. '{path}.<field>')` | A composite field set as a whole | Set a sub-field: `delayStrategy.constantValue` (a distribution takes its text, below) |
| `'{path}': Sampler spec '…': unknown type '…' — a distribution is set as a whole by text 'Type~p1~p2…' … Keys per type: …` | A distribution text that does not parse | `Triangular~0.6~1~1.6`, `ExponentialMean~5`, a number; or the type's keys listed |
| `'{path}': '{part}' is not 'Name = value'` | `ruleParameters` given without `=` | `ruleParameters = FreeWeight = 0.5, DueLabel = due` |
| warning `rule {Rule} has no parameter '{name}'. Did you mean …` | A rule parameter misspelt, or in the other list of a Queue (`… did you mean outputStrategy.ruleParameters?`) | Use one of the listed parameters (setting the rule lists them); a Queue's release rule (`offerRule`) reads `ruleParameters`, its routing rule `outputStrategy.ruleParameters` |
| warning `'{object}'.{list}: {names} set, but {rule} is empty: they are not used` | A rule parameter list whose rule is not set (often the Queue's other list) | Set them in the list of the rule that has them (named in the warning), or set the rule |
| `save: the open scene (…) is not one of the project's models` / `save: model '…' has unsaved changes, but it was not created or opened through simulean_models …` | `simulean_models action=save` saves only a model of the project that these commands created, copied or opened in this editor session | Work in your own model (`action=new` / `copy_example`, or `action=open` when it is saved); never save someone else's scene — ask the user |
| warning `{Owner}.{key}: reads the item label '{X}', which nothing in the model sets — did you mean the text '{X}'?` | A label value without quotes (`type = A`), or a label no source / assignment / reaction sets | Quote text (`type = 'A'`), or set the label where the items get it |
| `{n} of {m} operation(s) failed — nothing was changed: operation i (…): … \| operation j …` | `simulean_apply`: every bad operation of the list | Fix them all, check with `validateOnly=true`, send again |
| `'{path}': '{value}' is not one of {types}` (a `[SerializeReference]` field, e.g. `algorithm`) | The type name matches none or several types | Use a listed type name (`GeneticAlgorithm`, `RandomSearch`) |
| `'{object}' has no {Type} for '{path}'` / `No asset '{path}'` | An object field got the wrong object or asset | Name a GameObject with that component, or an `Assets/…` path |
| `No Transport Network in the scene …` | A node field set with no network | `simulean_add_element "Transport Network"`, nodes under it |
| `'{x}' is not a SimuLean element.` | `connect` / `disconnect` on something else | Connect elements (two transport nodes make a track) |
| `'{a}' ({Type}) has no output` / `'{b}' ({Type}) takes no input` | A Sink cannot send; a Source cannot receive | Connect from upstream / to an element with an input |
| `'{a}' does not send to '{b}' (its next elements: …)` | `disconnect` of a link that does not exist | Use one of the listed next elements |
| warning `'{a}' already sends to '{b}': nothing changed.` | Duplicate connection | Nothing |
| info `'{a}' (W × D m at x …, z …) and '{b}' (…) overlap on the floor by … m: move one of them` | `simulean_validate` (also `overlaps` in `simulean_apply`): two footprints overlap; a run does not care, a picture does | Move one with `simulean_move object=… x=… z=…`, spacing by the `size` that `simulean_model` gives |
| `connections: '{x}' is not a group (flow, lists, resources, locations, network, downtimes, logic), all or none.` | `simulean_capture connections=` misspelt | One of the groups, comma-separated |
| warning `'{a}' sent its items to list '{L}': it now sends to its Next Elements.` | `connect` switched an element from a list to its connections | Check it was meant; set `sendTo` back if not |
| warning `'{key}' belongs to another distribution: the active one is {X}, so a run does not use it.` | You set a parameter of a distribution type the field does not use | Also set `….strategyType`, or edit the active type's keys |
| `'{object}' is not a SimuLean object … simulean_remove deletes elements, teams, lists, tables and tools only.` | Removing an organiser group, a model, a camera | Remove the SimuLean object inside it |
| `'{object}' is not a SimuLean object … simulean_move moves elements, teams and tools only.` | Moving an organiser group, a model, a camera | Move the SimuLean objects inside it, one by one (or in one `simulean_apply`) |
| `path: '{p}' was not written by these commands in this editor session: not deleted …` | `simulean_results action=delete` of a file the commands did not write (the user's, or one from an earlier editor session) | Leave it; name it in your report for the user |
| `path: '{p}' is not inside the results folder or Recordings/: not deleted …` | `action=delete` of a file elsewhere (a chart or capture written to another folder, a project file) | Leave it; name it in your report |
| `resultSet: #{id} '{name}' was not stored by simulean_results action=save in this editor session: not deleted …` | `action=delete resultSet=` of a set the user (or an earlier session) stored | Leave it |
| `delete: give path= … or resultSet= …, one of them.` | `action=delete` with neither (or both) | One of them: the path an export / chart / capture answered, or a saved set's id |
| `'{p}' could not be moved to the recycle bin …: not deleted.` / `No trash this editor can use on this platform …` | The OS trash refused it, or the editor runs where it has none | Nothing was deleted: name the file in your report |
| `operation {n}: no "x".` / `no "z".` | A `move` operation without both coordinates | Give `x` and `z` (world, m) |
| warning `travel distances changed: …` | `simulean_move` / `move` of an element a Straight team walks to, a Straight team without a home node, or a transport node | Expected when the layout is meant to change: the walking / travel times and the results change; re-run the study |
| `'{object}'.{key}: '{text}' is not a valid expression: …` | `simulean_set` / `simulean_apply` on an expression field with a syntax error, an unknown function or root, or a root / function the field does not serve | Fix the text; the message gives the position and the closest names; `simulean_vocabulary` → `expressions.contexts` |
| `Unknown function 'MAX' … did you mean 'max'? (names are case-sensitive …)` / `Unknown root 'Item' … did you mean 'item'?` | Names are case-sensitive: functions and roots are lower-case | Write them in lower case |
| `the text '{t}' is not a condition (a condition is a number or true / false) …` | A constant text where a condition goes (`'yes' ? 1 : 0`, an admission condition `'0'`) | Compare the text (`x == 'yes'`) or test it with `has(x)` |
| `'{op}' on the text '{t}': arithmetic takes numbers …` | A constant text in arithmetic or a number argument (`'a' + 1`, `-'x'`, `'3' * 2`, `abs('x')`) | Write the number without quotes (`3`), or compare the text |
| `exponential({r}): the rate must be > 0 …` / `uniform({a}, {b}): the minimum is above the maximum …` / `normal({m}, {sd}): the standard deviation must be >= 0` / `pick: weight {k} is {w} …` / `pick: every weight is 0 …` | A random function with a constant parameter it cannot draw with | `exponential(rate)` takes the rate (mean 1 / rate); `uniform(min, max)`; `sd >= 0`; weights `>= 0`, one `> 0` |
| `The model's expressions read names it does not have: - '{owner}' ({field}): '{expr}': …` | An element, team, list, table, column, property or statistic named in an expression does not exist (checked when the model is built: run, validate) | Use the suggested name, declare the property (`properties.NAME`), or read it with `has()` / `??` |
| `… '{fn}' reads the element named by the item label '{x}', which '{writer}' sets to '{v}': no element '{v}' in the model …` | A name computed from a label (`content(item.dest)`) whose writes are all known at build (constants, a choice of constants, a table column) and one value names nothing of that kind | Fix the value at the writer named; for a "none" case leave the label unset and test `has(item.x)` |
| warning `t=… '{owner}' ({field}): the item's label '{x}' is not set …` | An item label read during the run that the item did not have (warned once) | Set the label upstream, or write `has(item.x)` / `item.x ?? value` where it may be missing |
| warning `… routing gives output {n} of {k} …` | A routing expression's value names no output: the item waits for ever | Fix the expression (outputs count from 0); `simulean_diagnose` shows it under `waitsFor` |
| `… unknown field '{f}' in '{query}' …` | A list query / order key reads a field the entries do not have | Use a listed field, `item.x` or `origin.x` |
| `No Library entry or tool '{type}'. Did you mean … simulean_types (no type) lists them all.` / `'{type}' names several Library entries: …` | Unknown or ambiguous type for `simulean_add_element` / `simulean_types` (any case, spaces and hyphens are already accepted: `MultiServer`, `multi server`) | Use the suggestion or one of the entries named |
| `travel: '{t}' is not one of {modes}` / `members: {n} — a team has at least 1 member.` | Wrong `simulean_add_team` arguments | Use a listed travel mode, members ≥ 1 |
| `'{x}' is not a Source.` / `Table file '{f}' not found.` | `simulean_load_table` on another element, or a missing file | A Source and an existing CSV / Excel file |
| `A background run is going on …` / `A search is going on …` | One run (one search) at a time | `simulean_run_status` / `simulean_optimize_status` with `wait`, or `cancel=true` |
| `{arg}: not valid JSON (…). Expected e.g. …, or the shorthand.` / `'{part}' is not 'name=value'.` | Malformed `factors`, `metrics`, `scenarios`, `operations` | Fix the JSON, or use `name=value;…` |
| `set: '{path}' has no "values"` / `a level is a number, a text or true / false` | Malformed factor JSON | `{"Buf.capacity": [2, 4]}` or `Buf.capacity=2|4` |
| `scenarios: give the factors in 'factors' (a study file's 'set') or in the scenarios, not both.` / `scenarios: '{s}' gives no value for {f} (the model's value: {v}), … — every scenario gives every factor any of them gives` | Both given; an incomplete scenario | One of them; add each factor named to that scenario (the model's value keeps it as it is) |
| `where: unknown name {x} (the factors are …)` / `where: no combination of the {n} meets '{w}'.` | A wrong name in the condition; a condition nothing meets | Use the listed names (`Buf_capacity`); loosen it |
| `derived '{n}': unknown name {x} (the metrics are …; the factors are …)` | A derived metric uses something that is neither a metric nor a factor | Use metric names, derived ones defined before it, or a factor named as in `where` (`Buf.capacity` → `Buf_capacity`) |
| `experimenter: its factors and scenarios are the study's …` | `experimenter` with `factors` / `scenarios` | Give one or the other |
| `operation {i}: no "{key}".` / `'{kind}' is not add, set, connect, …` | Malformed `simulean_apply` operation | Fix that operation |
| `count: {n} is not between 1 and 500` / `names: the pattern needs {i}` / `connectFrom: '{m}' is not in the group.` / `group: an object is inside another …` | Wrong `simulean_repeat` arguments | Count 1–500, a pattern with `{i}`, `Outside>Member` with a group member, top-level objects only |
| `'{object}' has no action '{a}' (it has …)` | Wrong `simulean_invoke` action | Use a listed action (no `action` lists them) |
| `parameters: '{p}={v}' is not a range lo..hi.` / `fitness: give the expression to maximise over the metrics (…)` | Wrong optimisation arguments | `Buf.capacity=1..10`; a fitness when there are several metrics |
| `factors / metrics come from the Optimizer when one is named …` | `optimizer` with `factors` / `metrics` | Leave `optimizer` empty, or edit the Optimizer with `simulean_set` |
| `algorithm: '{a}' is not one of …` / `algorithmSettings: {Alg} has no '{n}' (it has …)` | Wrong algorithm or setting | Use the listed names |
| `{n} value(s): at least 5 are needed to fit.` / `Give the data one way: values, sql or file.` / `column: no '{c}' (columns: …)` | Wrong `simulean_fit` data | ≥ 5 numbers from exactly one source, a listed column |
| `into: '{object}' has no distribution field '{f}'` / `into: {Family} is not a distribution type of the field.` | Wrong `into` | `Element.field` of a distribution (`Srv.delayStrategy`); pick another fit (`pick=2`) |
| `resultSet: no result set yet …` / `resultSet: no stored set '{s}' (stored: …)` | `simulean_results` before any study, or a wrong id / name | Run a study first, or use a listed set |
| `sort: no metric '{m}' — one of …` / `sort: '{s}' — the study has no named metrics to sort by …` / `top: …` / `offset: …` | A page of a study answer (`simulean_run_headless`, `simulean_run_status`, `simulean_results action=read`) sorted by a name that is not a named metric, or a negative `top` / `offset` | A listed metric (`-out` or `out desc` descending), or no `sort` (the plan's order); `top` > 0, `offset` from 0 |
| answer with `table` and `page.truncated: true` instead of `scenarios` | The study's answer is over ~40 KB (an agent's tool spills a larger one to a file) | Nothing is lost: follow `page.next` (`simulean_results action=read resultSet=last top=… offset=… sort=…`) page by page; `detail=full` for every digit and each run (`perRun`); `action=export formats=csv` for files |
| `section: no section '{s}' — one of …` / `section: '{s}' has no part '{p}' — one of …` | `simulean_vocabulary section=` misspelt | A section the index lists (`simulean_vocabulary` without `section`), a part `arguments.study`, or `all` |
| `The chart has no data: check yAxis / xAxis against the set's metrics (…)` / `yAxis: '{t}' is not a metric of the set (…), one of its factors (…) or a statistic path …` | A chart value the set does not have (or an old `metric:` / `stat:` / `param:` prefix) | `out` (a named metric), `Srv.utilization`, `Srv;Buf.utilization`, `*.utilization`, `Buf.capacity` (a factor) |

## 2. Study checks

Met by `simulean_validate`, and before every run, study and search.

| Message | Cause | What to do |
|---|---|---|
| `{what}: no element '{name}' in the model. Did you mean …` | A factor or metric names a missing element — or a team, downtime, list or reaction without its prefix (the message then says `'{name}' is a resource team: write 'team:{name}.…'`) | Fix the name (`simulean_model`), or use the prefixed path it gives |
| `{what}: {n} elements are named '{name}' … give them different names.` | Duplicate names | `simulean_rename` |
| `{what}: no resource team '{name}' in the model …` | Wrong team name | Fix it, or `simulean_add_team` |
| `'{owner}' has no parameter '{key}'. Did you mean …` | The key is not in the owner's catalog (the catalog key is the field path, e.g. `transportBy.loadTime`) | `simulean_params object=…` lists the catalog keys. A distribution key of one number (`Rack.transportBy.loadTime` = `Constant~8`) is varied by its name |
| `'{owner}.{key}' is a text setting (now '…'): give the texts to try …` | A text setting (a priority expression, a request order written in place) given a range, or an Optimizer row without texts | Give its texts as levels / options (JSON when a text holds `\|` or `;`), or the Optimizer row's *Texts to try*; `simulean_params` lists them under `textFactors` |
| `'{owner}.{key}' is a distribution of {n} numbers: vary one of them, {key}.{field} (…), …` | A distribution key (`transportBy.travelTime` = `Uniform~1~2`) given as a whole | Vary one number by its distribution field: `Srv.transportBy.travelTime.uniformMax=3|4` |
| `'{owner}' has no parameter 'transportBy.loadTime'. The element's carriers use their own load / unload times …` | The element does not override its carriers' load / unload times | Vary `team:{Team}.LoadTime` / `UnloadTime`, or set `transportBy.overrideLoadTimes` true |
| `Factor '{p}': '{k}' belongs to another distribution — '{owner}' uses {used} …` | A factor on a key of an unused distribution type | Vary the active type's keys, or the `…strategyType` key too |
| `'{path}': expected Owner.key.` | Path grammar | `Buf.capacity`, `team:Ops.members`, `downtime:Id.key` |
| `Factor '{p}': unknown scope '{s}' (element, team, downtime, reaction, data).` / `Metric '{n}': unknown scope '{s}' (model, element, team, list, reaction, downtime).` | Wrong scope prefix | Use a listed scope |
| `Metric '{n}': '{s}' is a statistic of a downtime repaired by a crew, and downtime '{d}' has none …` | `repairs` / `repair_waits` / `repair_wait_avg` / `repair_time_avg` of a downtime without *Repair by* | Give the downtime a repair crew, or read `stops` / `downtime_total` |
| `Metric '{n}': no element '{x}' … '{x}' is a member of team '{t}': a metric reads the team, e.g. 'team:{t}.utilization'` / `… A team member's statistics are read through its team` | A metric on one team member (or scope `resource` / `member`) | Read the team: `team:{t}.utilization` |
| `Factor '{p}': use team:Team.members[label=value].` / `Study: no member of '{Team}' has {label} …` | The members-with-a-label factor | Follow the form; give one member the label first |
| `Factor '{p}': '{v}' is not a number.` | A text level on a numeric key | Numeric levels |
| `… is not a number. A study lists its levels (1\|2\|3); lo..hi is a range for simulean_optimize_start.` | A range `1..10` as a study level | List the levels (`1\|2\|5\|10`), or optimise over the range |
| `Scenario '{s}': the model cannot be built: {inner}` | These levels break the build | The inner message is a [build error](#3-model-build) |
| `Metric '{m}': no {kind} '{target}' in the model (…). Did you mean …` | Metric target not found | Fix the target |
| `Metric '{m}': unknown statistic '{s}' for a {kind}. Statistics of a {kind}: …` | Wrong statistic | Use a listed one (architecture §3.11) |
| `… A metric is one path; for a sum or a ratio name each part as a metric and combine them with derived: …` | An expression (`Line.content_avg+Clerk.content_avg`) given as a metric's path | Name each part as a metric, combine them with `derived=L=a+b` |
| warning `Metric '{m}': '{e}' is a Source: it holds no items, so {s} is always 0 …` / `'{e}' is a Sink: it keeps no items, so {s} is no KPI …` | A Source's `stay_*` / `content*`, a Sink's `stay_*` / `utilization` / `content_avg` | Time held at a blocked source: `Src.state:BLOCKED` or a tally `wait = age` on the next element; time in system: `Exit.tis_avg` |
| warning `derived '{n}' reads no metric and no factor: it is the same constant ({x}) in every run.` | A derived metric with no name in it (`stalls=0`) | Name the metric it should read (`stalls=model.stalled` as a metric); keep it if a constant reference is meant |
| warning `Factor '{p}': every level ({a\|b}) gave the same results … — the factor may not bind` (study answer) | With common random numbers a change that does nothing gives identical runs: e.g. a capacity never reached (`content_max` below it) | Check the factor reaches the model (`Buf.content_max` against the capacity); vary something that binds |
| `Metric '{m}': no state '{s}' (states: …)` | Wrong `state:NAME` | Use a listed state |
| `Metric name '{m}': letters, digits and _ only` / `Metric '{m}' is defined twice.` | Metric names are expression names | Rename it |
| `Fitness: '{r}' is not a metric or a numeric parameter.` | The fitness uses an unknown name | Metric names (or numeric parameters) |
| `Diagnosis: one scenario at a time …` | `simulean_diagnose` with several levels | One level per factor |

## 3. Model build

Met when a run, a check or a diagnosis builds the model.

| Message | Cause | What to do |
|---|---|---|
| `Element '{e}': resource team '{t}' does not exist.` | `processBy` / `transportBy` names a team not in the model | `simulean_add_team`, or set the field to an existing team |
| `Resource team '{t}' travels on the network, but the model has none.` / `… travel matrix / walk grid '{x}' does not exist` / `… home '{n}' is not a location of the model: set its homeNode to a Transport Node of the network.` | The team's travel set-up is incomplete | Add the network / matrix / grid, set the home, or change `travel` |
| info `Team '{t}' has no homeNode: its members start at node '{n}', the network's first.` (`simulean_validate` `info`) | A spatial team without a home node: the build chose the network's first node | Nothing, or set `homeNode` to the Transport Node where its members should start |
| `Milk run '{m}': stop '{s}' lists '{o}' as an empties origin, but '{o}' empties carriers … / already sends its items on: the milk run would take over its output. Put a Queue at the stop …` | `stops[i].emptiesOrigins` names the unpacker (or an element with its own output) instead of the queue its empty carriers wait in | Set the element's `emptiesDestination` to a Queue at the stop and list that Queue in `emptiesOrigins` ([warehouse](topics/warehouse.md)) |
| warning `Storage rack '{r}': its {n} lane(s) are not those of its layout (…) — press Rebuild Storage Locations …` | A rack's layout changed in the inspector without *Rebuild* (the commands rebuild at once) | `simulean_invoke object={r} action=Rebuild`, or set a layout field again with `simulean_set` |
| `Element '{e}': location '{l}' … is neither a network node nor in a travel matrix.` | A wrong `location` / work node | Set it to an existing node |
| `Transport by '{c}' from '{a}' to '{b}': both elements need a location …` | A spatial carrier between elements without locations | Set `location` on both |
| `Element '{e}' has process resources but its type does not use them.` | `processBy` on a type that takes no resources | Clear it |
| `Connection '{s}' -> '{t}': the target is not an element of the model.` | A connection to a deleted object or one outside the model root | Reconnect or `simulean_disconnect` |
| `{type}: element '{e}' or list '{l}' is not in the model.` | A list link to a missing list | Set `pushToList` / `pullFromList` to an existing list |
| `Source '{s}': schedule mode without an arrivals table (load one with simulean_load_table object={s} file=<table.csv>, or in the Source's inspector)` / `… both a data table and a table file` | A Source on a schedule is not set up | `simulean_load_table object={s} file=…`, or exactly one table source |
| `Downtime '{d}': target '{x}' is not an element` / `unknown kind '{k}'` / `a periodic stop needs 'period' and 'duration'` | A downtime set-up error | Fix the target, kind or fields |
| `{at}: no element / resource team / list '{x}' in the model` / `an item label needs an item signal` / `two reactions are called '{n}'` | A reaction set-up error | Fix the names or the signal; unique reaction names |
| `Agenda '{a}': no sessions (e.g. Mon-Fri 08:30-12:30)` | An agenda without sessions | Give them in that form |
| warning `'{e}' has an empty Next Elements entry (a deleted element?) …` | A gap in Next Elements (the outputs after it move up one index) | Rewrite `nextElements` without the gap |
| warning `'{e}' is tilted (rotation …) … Set its rotation to (0, y, 0)` | An element rotated about X / Z | Reset the rotation |
| warning `{n} transport networks found: a model has one …` | Several networks | Remove the extra ones |
| warning `Data table '{t}': the file '{f}' changed after it was loaded …` | The run uses the stored copy | `simulean_load_table` again, or `readAtRun=true` |
| warning `Several resources are named '{m}' …` | Members of different teams share a name (the results then say `team/member`) | Rename the members |
| warning `'{s}': its items are carried by a transport team and also walk — the team carries them.` | Both set | Clear one |
| warning `Storage '{r}': {n} of its {k} lane(s) are at the storage's location '{l}' … and no load / unload time is set: a move takes only the team's travel to the rack …` | The rack's lanes share its node and the moves have no handling time | Give the lanes nodes (`lanes[i].node`) or a load time; when zero is intended, set `transportBy.loadTime = 0` explicitly (`simulean_set` turns `transportBy.overrideLoadTimes` on): the warning goes |
| info `Downtime '{d}' stops members of team '{t}', but '{e}' uses that team with processBy.preemption = None: a member's breakdown will not interrupt its job …` (`simulean_validate` `info`) | A member breakdown without a preemption mode at the station: the job runs on | Nothing if intended, else `processBy.preemption = Resume` ([resources](topics/resources.md)) |
| `kind: '{k}' is not a team member kind (…)` (`simulean_add_team`) | An unknown member kind; the answer suggests the closest | A listed kind; setters, mechanics and other walking staff: `operator` |

## 4. Run time

| Message | Cause | What to do |
|---|---|---|
| `t={t} '{owner}' ({field}): '{expr}': {a} / 0 (a division by zero) gives an infinite value …` / `… gives NaN (not a number) …` (`sqrt(-1)`, `pow(-1, 0.5)`, `0 / 0`, a label holding NaN) | An expression computed a value that is not a finite number; the run stops (it gave 0 before) | Guard it: `item.k > 0 ? 10 / item.k : 0`, `sqrt(max(0, x))` |
| `t={t} '{owner}' ({field}): '{expr}': the text '{v}' is not a condition …` | A text read as a condition at run time (`item.color ? 1 : 2`) | Compare it (`item.color == 'red'`) or use `has(item.color)` |
| `t={t} '{owner}' ({field}): '{expr}': '{op}' on the text '{v}': arithmetic takes numbers …` | A text read at run time in arithmetic (`item.color + 1`; a label holding `'3'` is a text) | Write the label as a number, or compare it |
| `t={t} '{owner}' ({field}): '{expr}': exponential(0): the rate must be > 0 …` (likewise `uniform`, `normal`, a negative `pick` weight) | A random function's parameter computed at run time is invalid (it drew 0 before) | Guard it: `item.r > 0 ? exponential(item.r) : 0`, `max(0, w)` |
| `t={t} '{owner}' ({field}): '{expr}': '{fn}': no element '{x}' in the model …` | A name computed at run time (`content(item.dest)`) names nothing of that kind | Fix the label's value upstream; `simulean_diagnose` shows the time |
| warning `[SimuLean] Deadlock at t = {t}: no event can move the {n} element(s) holding items — {element: state, items → waits for …}` (a full buffer without outputs that holds a feeder up: `{buffer}: full (n/n), no outputs`) | Items blocked with no future event: a full element downstream, a Combiner missing an input, a resource never released, a list nobody pulls | `simulean_diagnose` (`time=0`) shows each element, request and resource; add capacity, connect the missing input, fix the release |
| `{q}: BLOCKED, {n} item(s) → no receiver has room (full, refusing or stopped): waits for …` (`simulean_diagnose` `waitsFor`) | A queue (or station) holds items no receiver takes now: the documented BLOCKED state, not an error by itself | Normal while downstream is busy; a long one is a bottleneck or a missing route (the routing's `last` decision names the outputs it skipped) |
| warning `Queue '{q}' reached its capacity 100, the default (content_max = 100 in {k} of {n} runs): raise it if it is meant to be unlimited …` (run / study answer) | A Queue left at the default capacity filled up in a run: the default caps the flow silently (there is no unlimited Queue) | Set a capacity no run reaches (`capacity = 1000000`) if it is meant to be unlimited; else keep it (the capacity is then the design's) |
| note `Circular wait: {A} → {B} → … → {A} — each holds items and waits for room in the next, which is full: none can move …` (`simulean_diagnose` `notes`) | A loop (rework, returning carriers) whose elements are all full and wait for each other: a deadlock by too little room | More room in the loop than the items it can hold (a buffer's capacity), or limit the items let into it |
| `waiting` in a run answer, `model.stalled` = 1 | Items wait and nothing can happen, none blocked (e.g. a kit that will never be complete) | `simulean_diagnose` |
| warning `[SimuLean] No progress at t = {t}: no item has moved for {T} (since t = {last}) while {n} element(s) hold waiting items — …`; `endReason` NoProgress | The end condition *Stop When No Progress For*: a stall beside a periodic signal or reaction (or `T` shorter than a long process / stop) | `simulean_diagnose noProgress=T`; lengthen `T` if the pause is the design's |
| `'{r}' cannot reach node '{to}' from '{from}' on the network.` | A disconnected network or a one-way track | Add tracks (`simulean_connect node node`) |
| `'{r}': the travel matrix has no distance {a} → {b}.` / `cannot walk from '{a}' to '{b}' …` | An incomplete travel model | Fill the matrix (`simulean_invoke … FillStraightLine`), free the path |
| `Spatial resource '{r}' has no location (set its home node).` | A team without a home node | Set the team's home |
| warning `Downtime '{d}': {n} row(s) of '{t}' skipped (no start or no positive duration).` | Bad timetable rows | Fix the table |
| warning `A {owner} drew from the global random engine (no stream assigned) …` | A sampler without a stream: draws not reproducible | Report it — model elements always have one; it points at custom code |
| warning `[SingleLabel] main item {n} has no label '{l}': its assembly admits no component.` | A Combiner pull mode *Single Label* (no value) whose main item lacks the key | Give the main items the label (entry labels of the source); a number matches numeric labels, a text text labels |

## 5. Editor tools outside SimuLean

The generic tools of the Unity pipeline server (`capture_game_view`, `run_tests`, …) are not SimuLean code: their behaviour is reported upstream, not patched here.

| Behaviour | What to do |
|---|---|
| `capture_game_view save_path=Temp/x.png` writes under `Assets/Temp` (an asset and its `.meta` in the shared tree): the path is taken relative to `Assets/`, not to the project | Use `simulean_capture path=…` (a project path or an absolute one, e.g. the scratchpad), or give `capture_game_view` an absolute path outside `Assets`; delete an `Assets/Temp` it made |
| `run_tests` blocks on the "Scene(s) Have Been Modified" dialog | Use `simulean_test` / `simulean_test_status` |
| A `simulean_*` tool's schema in the client differs from [commands.md](commands.md) (e.g. `element` where the command takes `object`, an argument such as `simulean_model section` missing) | The server builds the schemas from the commands' code (`[CliCommand]` / `[CliArg]`) when the client connects; the client keeps the list it got then. Restart the MCP client (or reconnect the server) after the commands change |
