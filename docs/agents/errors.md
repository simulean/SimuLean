# Errors and warnings: what they mean, what to do

Every `simulean_*` answer carries `errors` and `warnings` — what was logged while the command ran. A command that fails
answers with its message instead. Messages name the object, the key, the value and, when they can, the closest valid
option ("Did you mean …"): read them before retrying. `{…}` below stands for a name or value.

**How messages are wrapped**

- A command that fails after warnings were logged: `{message} Warnings logged: {w1} | {w2}` — the warnings usually say
  why.
- `simulean_apply`: `operation {i} ({kind} …): {message} — nothing was changed (…)`. The list is all or nothing: fix that
  operation and send the whole list again.
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
| `'{path}' ({type}): '{value}' is not a whole number / a number with '.' as the decimal point / true or false (e.g. …)` | The value does not parse | Invariant format: `4`, `2.5`, `true` |
| `'{path}': '{value}' is not one of {names}` | Wrong enum value | Use one of the names listed |
| `'{path}' ({type}) is not set by text: set its fields (e.g. '{path}.<field>')` | A composite field set as a whole | Set a sub-field: `delayStrategy.constantValue` |
| `'{path}': '{value}' is not one of {types}` (a `[SerializeReference]` field, e.g. `algorithm`) | The type name matches none or several types | Use a listed type name (`GeneticAlgorithm`, `RandomSearch`) |
| `'{object}' has no {Type} for '{path}'` / `No asset '{path}'` | An object field got the wrong object or asset | Name a GameObject with that component, or an `Assets/…` path |
| `No Transport Network in the scene …` | A node field set with no network | `simulean_add_element "Transport Network"`, nodes under it |
| `'{x}' is not a SimuLean element.` | `connect` / `disconnect` on something else | Connect elements (two transport nodes make a track) |
| `'{a}' ({Type}) has no output` / `'{b}' ({Type}) takes no input` | A Sink cannot send; a Source cannot receive | Connect from upstream / to an element with an input |
| `'{a}' does not send to '{b}' (its next elements: …)` | `disconnect` of a link that does not exist | Use one of the listed next elements |
| warning `'{a}' already sends to '{b}': nothing changed.` | Duplicate connection | Nothing |
| warning `'{a}' sent its items to list '{L}': it now sends to its Next Elements.` | `connect` switched an element from a list to its connections | Check it was meant; set `sendTo` back if not |
| warning `'{key}' belongs to another distribution: the active one is {X}, so a run does not use it.` | You set a parameter of a distribution type the field does not use | Also set `….strategyType`, or edit the active type's keys |
| `'{object}' is not a SimuLean object … simulean_remove deletes elements, teams, lists, tables and tools only.` | Removing an organiser group, a model, a camera | Remove the SimuLean object inside it |
| `No Library entry '{type}'` / `No Library entry or tool '{type}'. Did you mean …` | Unknown type for `simulean_add_element` | `simulean_types` lists the entries |
| `travel: '{t}' is not one of {modes}` / `members: {n} — a team has at least 1 member.` | Wrong `simulean_add_team` arguments | Use a listed travel mode, members ≥ 1 |
| `'{x}' is not a Source.` / `Table file '{f}' not found.` | `simulean_load_table` on another element, or a missing file | A Source and an existing CSV / Excel file |
| `A background run is going on …` / `A search is going on …` | One run (one search) at a time | `simulean_run_status` / `simulean_optimize_status` with `wait`, or `cancel=true` |
| `{arg}: not valid JSON (…). Expected e.g. …, or the shorthand.` / `'{part}' is not 'name=value'.` | Malformed `set`, `metrics`, `scenarios`, `operations` | Fix the JSON, or use `name=value;…` |
| `set: '{path}' has no "values"` / `a level is a number, a text or true / false` | Malformed factor JSON | `{"Buf.capacity": [2, 4]}` or `Buf.capacity=2|4` |
| `scenarios: give the factors in the scenarios or in set, not both.` / `scenarios: '{s}' gives no value for {f}` | Both given; an incomplete scenario | One of them; every scenario gives every factor |
| `where: unknown name {x} (the factors are …)` / `where: no combination of the {n} meets '{w}'.` | A wrong name in the condition; a condition nothing meets | Use the listed names (`Buf_capacity`); loosen it |
| `derived '{n}': unknown name {x} (the metrics are …)` | A derived metric uses something that is not a metric | Use metric names, or derived ones defined before it |
| `experimenter: its factors and scenarios are the study's …` | `experimenter` with `set` / `scenarios` | Give one or the other |
| `operation {i}: no "{key}".` / `'{kind}' is not add, set, connect, …` | Malformed `simulean_apply` operation | Fix that operation |
| `count: {n} is not between 1 and 500` / `names: the pattern needs {i}` / `connectFrom: '{m}' is not in the group.` / `group: an object is inside another …` | Wrong `simulean_repeat` arguments | Count 1–500, a pattern with `{i}`, `Outside>Member` with a group member, top-level objects only |
| `'{object}' has no action '{a}' (it has …)` | Wrong `simulean_invoke` action | Use a listed action (no `action` lists them) |
| `parameters: '{p}={v}' is not a range lo..hi.` / `fitness: give the expression to maximise over the metrics (…)` | Wrong optimisation arguments | `Buf.capacity=1..10`; a fitness when there are several metrics |
| `parameters / metrics come from the Optimizer when one is named …` | `optimizer` with `parameters` / `metrics` | Leave `optimizer` empty, or edit the Optimizer with `simulean_set` |
| `algorithm: '{a}' is not one of …` / `algorithmSettings: {Alg} has no '{n}' (it has …)` | Wrong algorithm or setting | Use the listed names |
| `{n} value(s): at least 5 are needed to fit.` / `Give the data one way: values, sql or file.` / `column: no '{c}' (columns: …)` | Wrong `simulean_fit` data | ≥ 5 numbers from exactly one source, a listed column |
| `into: '{object}' has no distribution field '{f}'` / `into: {Family} is not a distribution type of the field.` | Wrong `into` | `Element.field` of a distribution (`Srv.delayStrategy`); pick another fit (`pick=2`) |
| `set: no result set yet …` / `set: no stored set '{s}' (stored: …)` | `simulean_results` before any study, or a wrong id / name | Run a study first, or use a listed set |
| `The chart has no data: check y / x against the set's metrics (…)` / `'{t}' is not metric:NAME, stat:SCOPE:OBJECTS:STATISTIC … or param:PATH.` | A chart value the set does not have | `metric:out`, `stat:element:Srv:utilization`, `param:Buf.capacity` |

## 2. Study checks

Met by `simulean_validate`, and before every run, study and search.

| Message | Cause | What to do |
|---|---|---|
| `{what}: no element '{name}' in the model. Did you mean …` | A factor or metric names a missing element | Fix the name (`simulean_model`) |
| `{what}: {n} elements are named '{name}' … give them different names.` | Duplicate names | `simulean_rename` |
| `{what}: no resource team '{name}' in the model …` | Wrong team name | Fix it, or `simulean_add_team` |
| `'{owner}' has no parameter '{key}'.` | The key is not in the owner's catalog (the catalog key is not always the inspector field) | `simulean_params element=…` lists the catalog keys |
| `Factor '{p}': '{k}' belongs to another distribution — '{owner}' uses {used} …` | A factor on a key of an unused distribution type | Vary the active type's keys, or the `…strategyType` key too |
| `'{path}': expected Owner.key.` | Path grammar | `Buf.capacity`, `team:Ops.members`, `downtime:Id.key` |
| `Factor '{p}': unknown scope '{s}' (element, team, downtime, reaction, data).` / `Metric '{n}': unknown scope '{s}' (model, element, team, list, reaction).` | Wrong scope prefix | Use a listed scope |
| `Metric '{n}': no element '{x}' … '{x}' is a member of team '{t}': a metric reads the team, e.g. 'team:{t}.utilization'` / `… A team member's statistics are read through its team` | A metric on one team member (or scope `resource` / `member`) | Read the team: `team:{t}.utilization` |
| `Factor '{p}': use team:Team.members[label=value].` / `Study: no member of '{Team}' has {label} …` | The members-with-a-label factor | Follow the form; give one member the label first |
| `Factor '{p}': '{v}' is not a number.` | A text level on a numeric key | Numeric levels |
| `Scenario '{s}': the model cannot be built: {inner}` | These levels break the build | The inner message is a [build error](#3-model-build) |
| `Metric '{m}': no {kind} '{target}' in the model (…). Did you mean …` | Metric target not found | Fix the target |
| `Metric '{m}': unknown statistic '{s}' for a {kind}. Statistics of a {kind}: …` | Wrong statistic | Use a listed one (architecture §3.11) |
| `Metric '{m}': no state '{s}' (states: …)` | Wrong `state:NAME` | Use a listed state |
| `Metric name '{m}': letters, digits and _ only` / `Metric '{m}' is defined twice.` | Metric names are expression names | Rename it |
| `Fitness: '{r}' is not a metric or a numeric parameter.` | The fitness uses an unknown name | Metric names (or numeric parameters) |
| `Diagnosis: one scenario at a time …` | `simulean_diagnose` with several levels | One level per factor |

## 3. Model build

Met when a run, a check or a diagnosis builds the model.

| Message | Cause | What to do |
|---|---|---|
| `Element '{e}': resource team '{t}' does not exist.` | `processBy` / `transportBy` names a team not in the model | `simulean_add_team`, or set the field to an existing team |
| `Resource team '{t}' travels on the network, but the model has none.` / `… travel matrix / walk grid '{x}' does not exist` / `… home '{n}' is not a location` | The team's travel set-up is incomplete | Add the network / matrix / grid, set the home, or change `travel` |
| `Element '{e}': location '{l}' … is neither a network node nor in a travel matrix.` | A wrong `location` / work node | Set it to an existing node |
| `Transport by '{c}' from '{a}' to '{b}': both elements need a location …` | A spatial carrier between elements without locations | Set `location` on both |
| `Element '{e}' has process resources but its type does not use them.` | `processBy` on a type that takes no resources | Clear it |
| `Connection '{s}' -> '{t}': the target is not an element of the model.` | A connection to a deleted object or one outside the model root | Reconnect or `simulean_disconnect` |
| `{type}: element '{e}' or list '{l}' is not in the model.` | A list link to a missing list | Set `pushToList` / `pullFromList` to an existing list |
| `Source '{s}': schedule mode without an arrivals table …` / `… both a data table and a table file` | A Source on a schedule is not set up | `simulean_load_table`, or exactly one table source |
| `Downtime '{d}': target '{x}' is not an element` / `unknown kind '{k}'` / `a periodic stop needs 'period' and 'duration'` | A downtime set-up error | Fix the target, kind or fields |
| `{at}: no element / resource team / list '{x}' in the model` / `an item label needs an item signal` / `two reactions are called '{n}'` | A reaction set-up error | Fix the names or the signal; unique reaction names |
| `Agenda '{a}': no sessions (e.g. Mon-Fri 08:30-12:30)` | An agenda without sessions | Give them in that form |
| warning `'{e}' has an empty Next Elements entry (a deleted element?) …` | A gap in Next Elements (the outputs after it move up one index) | Rewrite `nextElements` without the gap |
| warning `'{e}' is tilted (rotation …) … Set its rotation to (0, y, 0)` | An element rotated about X / Z | Reset the rotation |
| warning `{n} transport networks found: a model has one …` | Several networks | Remove the extra ones |
| warning `Data table '{t}': the file '{f}' changed after it was loaded …` | The run uses the stored copy | `simulean_load_table` again, or `readAtRun=true` |
| warning `Several resources are named '{m}' …` | Members of different teams share a name (the results then say `team/member`) | Rename the members |
| warning `'{s}': its items are carried by a transport team and also walk — the team carries them.` | Both set | Clear one |

## 4. Run time

| Message | Cause | What to do |
|---|---|---|
| warning `[SimuLean] Deadlock at t = {t}: no event can move the {n} element(s) holding items — {element: state, items → waits for …}` | Items blocked with no future event: a full element downstream, a Combiner missing an input, a resource never released, a list nobody pulls | `simulean_diagnose` (`time=0`) shows each element, request and resource; add capacity, connect the missing input, fix the release |
| `waiting` in a run answer, `model.stalled` = 1 | Items wait and nothing can happen, none blocked (e.g. a kit that will never be complete) | `simulean_diagnose` |
| `'{r}' cannot reach node '{to}' from '{from}' on the network.` | A disconnected network or a one-way track | Add tracks (`simulean_connect node node`) |
| `'{r}': the travel matrix has no distance {a} → {b}.` / `cannot walk from '{a}' to '{b}' …` | An incomplete travel model | Fill the matrix (`simulean_invoke … FillStraightLine`), free the path |
| `Spatial resource '{r}' has no location (set its home node).` | A team without a home node | Set the team's home |
| warning `Downtime '{d}': {n} row(s) of '{t}' skipped (no start or no positive duration).` | Bad timetable rows | Fix the table |
| warning `A {owner} drew from the global random engine (no stream assigned) …` | A sampler without a stream: draws not reproducible | Report it — model elements always have one; it points at custom code |
