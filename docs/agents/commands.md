# SimuLean MCP commands

> Generated from the code by `Tools/Docs/command_reference.py` — do not edit by hand (run it after changing a
> command; `--check` fails when this file is not current). How to use them together: [workflow.md](workflow.md);
> what their errors mean: [errors.md](errors.md).

39 commands. Every answer is a JSON object with the command's fields plus `errors` and `warnings` (what was
logged while it ran). Objects are named by their GameObject name (unique) or their hierarchy path. String
arguments that take a list accept the shorthand shown or JSON.

## Describing the model

### `simulean_types`

List the SimuLean element types and tools that can be placed (the Library's entries: by default the Core pack's, each other pack as one line; pack=<name> or pack=all lists them); with 'type', that type's fields (simulean_set), the keys of every distribution, the fields of its lists' items and its catalog keys (simulean_run_headless factors; textFactors: the text settings, varied by texts). Compact by default: the groups every element shares are counted, not listed (detail=full or fields=<group> lists them).

| Argument | Type | Default | |
|---|---|---|---|
| `type` | string | — | A Library name, component type or engine type, in any case, with or without spaces / hyphens (e.g. 'Multi-Server', 'MultiServer', 'UnityMultiServer'); several comma-separated ('Queue,Sink'); empty = all. |
| `detail` | string | `"compact"` | Fields shown: compact (default: the type's own fields, shared groups left out unless changed, a distribution as one text) or full (every field); full or fields= also give the inspector's tooltip of each field without a help note, under tips. |
| `fields` | string | — | Only the fields and catalog keys under these paths, comma-separated (e.g. 'processBy,outputStrategy'). |
| `pack` | string | — | Without 'type': the entries listed — empty = the Core pack's (decoration left out) plus one line per other pack; a pack (Core, Manufacturing, Warehousing, Healthcare, Shipbuilding), a group (Flow, Presets, Resources, Transport…) or a module (Storage, Processors…); all = every entry. |

### `simulean_params`

An element's (or tool's) fields with their values — the keys simulean_set writes —, the keys of every distribution, the fields of its lists' items and its catalog keys (the parameters a study varies; textFactors: the text settings, varied by the texts given, with their current text). Compact by default: the groups every element shares are listed only where changed (detail=full or fields=<group> lists them).

| Argument | Type | Default | |
|---|---|---|---|
| `object` | string | required | The element or tool (GameObject name or hierarchy path). |
| `detail` | string | `"compact"` | Fields shown: compact (default: the type's own fields, shared groups left out unless changed, a distribution as one text) or full (every field); full or fields= also give the inspector's tooltip of each field without a help note, under tips. |
| `fields` | string | — | Only the fields and catalog keys under these paths, comma-separated (e.g. 'processBy,outputStrategy'). |

### `simulean_model`

The scene's model in one compact answer, as a run builds it: the clock (time unit, horizon, warm-up, end condition, seed), every element (type, next elements in order, parameters under the keys a study varies), the other connections, teams (members, labels, parameters), lists, downtimes, reactions, data tables and the transport network (nodes with their positions and the elements there, tracks with their lengths, the teams on it) — all by name; each team and downtime with its metrics (the study metric path, the statistic names, a downtime's per-target paths downtime:Id@<target> with a team target's members by name); info = the model card (what the model represents, its KPIs, parameters worth varying, assumptions) when the scene has one. section=logic answers instead every decision as one plain sentence (routing, admission, release order, a team's request order and member choice, list queries and orders, waits, reactions; a setting its strategy does not use is left out, a default is marked) with its raw settings, what it reads and writes, and which property writes wake an admission; in Play also its run-time counters (live: items per output and the last choice with the item's labels, entries / refused checks of an admission, wake-ups, releases, requests served, a reaction's fires, a rule's calls). Read-only.

| Argument | Type | Default | |
|---|---|---|---|
| `object` | string | — | Only this element / team / list / downtime / reaction / table, or the transport network (network, its name or one of its nodes) (empty = the whole model). |
| `parameters` | bool | `true` | Include the parameters (false = the structure only: smaller). |
| `root` | string | — | Model root GameObject (empty = the scene's model). |
| `section` | string | `"model"` | What to answer: model (the default: structure and parameters) or logic (the decisions: one sentence each with its settings, what it reads / writes, the admission wake-ups and notes). |
| `detail` | string | `"compact"` | compact (default: empty texts and the scene's automatic points left out, a long or generated list such as a rack's lanes summarised per field) or full (every value). |

### `simulean_validate`

Check the scene's model without running it: extract it, build it (each scenario of the optional study: factors / metrics as simulean_run_headless) and resolve the metrics. Answers ok plus the errors and warnings a run would meet (extractor, build, study), with "did you mean"; a choice rule without a Why, or used where the primitive it equals would do, is a warning.

| Argument | Type | Default | |
|---|---|---|---|
| `factors` | string | — | Catalog overrides 'path=v1\|v2;…' or JSON to check too (as simulean_run_headless). |
| `metrics` | string | — | Named metrics 'name=path;…' or JSON to check too. |
| `root` | string | — | Model root GameObject (empty = the scene's model). |
| `scenarios` | string | — | Explicit scenarios instead of factors (JSON): [{"name": "A", "Buf.capacity": 2, "replications": 5}, ...] - every scenario gives every factor. |
| `where` | string | — | Keep only the combinations of the factors for which this holds, e.g. 'B1_capacity + B2_capacity <= 10' (a factor path with every character that is not a letter or digit as _). |
| `derived` | string | — | Metrics computed per replication from the others and the study's factors (named as in where: B1.capacity -> B1_capacity), 'name=expression;...' (e.g. 'days=floor(span/24)', 'profit=10*out-100*B1_capacity'); reported as mean +- half-width like them. |
| `confidence` | double | `0` | Confidence level of the half-widths (0 = 0.95). |
| `warmup` | double | `-1` | Warm-up time: statistics start after it (-1 = the model's). |
| `noProgress` | double | `-1` | End condition: stop a run when no item has moved for this time while items wait (end reason NoProgress: a stall beside a periodic signal or reaction; 0 = off, -1 = the model's). |
| `compareTo` | string | — | A scenario (its name or its 1-based position) the others are compared with: each other scenario gets paired = per metric the mean difference to it over the runs of the same seed and its half-width (common random numbers: far tighter than comparing two means). |
| `experimenter` | string | — | Run a scene Experimenter's study (its factors, scenarios, metrics and settings); horizon, replications other than 1, seed other than 1 and the metrics given here override / are added. |
| `horizon` | double | `-1` | Horizon the run would use, echoed in settings (-1 = the model's). |
| `replications` | int | `1` | Replications the run would use, echoed in settings (a scenario's own replications win). |
| `seed` | int | `1` | First seed the run would use, echoed in settings. |

### `simulean_vocabulary`

The names the SimuLean commands use, from the code: every command argument grouped by concept (object, factors, horizon…; retired names listed), the factor / metric / result scopes, the path grammar with an example per form, the statistics per scope, the state names (declared, and the scene model's), the expression roots / built-ins / functions, the option enums and the catalog keys with their rule. Read-only. By default an index of its sections (each one's size and what it holds: the whole is over what a tool answer holds); section= answers them.

| Argument | Type | Default | |
|---|---|---|---|
| `root` | string | — | Model root GameObject whose built model adds its state names (empty = the scene's model, if any). |
| `section` | string | — | The sections answered: one (arguments, retired, scopes, paths, statistics, states, expressions, options, catalog), several comma-separated, a part of one (arguments.study: a concept; expressions.functions) or all (the whole vocabulary, ~80 KB); empty = the index. |

## Building

### `simulean_add_element`

Place a Library entry (element, tool or preset) in the open scene, as the SimuLean window does. Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `type` | string | required | A Library name or component type (simulean_types). |
| `name` | string | — | GameObject name (empty = the Library's, made unique). |
| `x` | float | `0` | World X (x, y, z all 0 = not given: a List, Downtime or Reaction then goes beside the model, in a row behind it). |
| `y` | float | `0` | World Y. |
| `z` | float | `0` | World Z. |
| `parent` | string | — | Parent GameObject (name or path); empty = its hierarchy group. |

### `simulean_set`

Set a field of an element or tool (simulean_params lists them): a name or path such as 'capacity', 'delayStrategy.constantValue', 'processBy.team' (object fields take a GameObject name, or an asset path such as 'Assets/…/Doctor.prefab'), 'mode' (enums by name), lists comma-separated; a list item's field as 'actions[0].kind' (an index past the end adds items), or items by id or pattern in place of the index: 'lanes[B0_S0_L0].node', 'lanes[B0_*].node' (* any text, ? one character, \| alternatives; answers matched / items); an element's user property as 'properties.NAME' (created when missing; a number, or else a text). An expression field is checked as it is set: a text that does not parse, or reads a root / function its place does not serve, is refused; an element it names that the scene does not have is a warning. Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `object` | string | required | The element or tool (GameObject name or hierarchy path). |
| `key` | string | required | Field name or path. |
| `value` | string | required | The value as text (required); "" clears the field: an empty text or list, no object reference. |

### `simulean_connect`

Connect two elements: 'to' is added to the Next Elements of 'from' (item flow). Two transport nodes: a two-way track of their network (paths of walking items and vehicles). Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `from` | string | required | Upstream element (name or path). |
| `to` | string | required | Downstream element (name or path). |

### `simulean_disconnect`

Undo simulean_connect: 'to' leaves the Next Elements of 'from' (the outputs after it move up one index); between two transport nodes, their track is removed. Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `from` | string | required | Upstream element (name or path). |
| `to` | string | required | Downstream element (name or path). |

### `simulean_add_team`

Place a resource team (operators, cranes, vehicles…) with n members. Pass its kind (operator, agv, forklift, tugger, pallet truck, stacker crane, hall crane, robot…): the members get that kind's 3D model and realistic speed (m/s), as the Library's team presets. A kind is a preset over the team component's defaults: it sets only the members' 3D model, speed = the kind's m/s and units = Physical (a robot also gets its arm cell); every other field keeps the component default. Without a kind, a team named after one (AGVs, Forklifts…) is of that kind; otherwise its members get the look of their travel (Network: an AGV, Axes: a crane, else an operator) and the component default: speed 1 with units = Model (1 distance unit per model time unit, not m/s). So kind=operator gives 1.2 m/s, no kind 1 distance unit per time unit. Assign it with simulean_set <station> processBy.team <team> (or transportBy.team). The answer gives speed, units and speedFrom (the kind, the name or the default). Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `name` | string | required | Team name. |
| `members` | int | `1` | Number of members. |
| `travel` | string | — | Travel mode (TeamTravel: Straight — the default: straight lines between the elements —, Abstract — no travel —, Network, Grid, Matrix, Axes…); empty = the default. |
| `x` | float | `0` | World X (x, y, z all 0 = not given: the team goes in front of the model, not on an element). |
| `y` | float | `0` | World Y. |
| `z` | float | `0` | World Z. |
| `parent` | string | — | Parent GameObject (name or path); empty = its hierarchy group. |
| `kind` | string | — | Member kind: its 3D model and speed in m/s — operator, doctor, nurse, fitter, welder, painter, outfitter (1.2); bed / stretcher / wheelchair porter, pallet truck, agv, hall crane, level luffing crane (1.0); forklift (2.5); tugger, stacker crane (2.0); robot, vacuum robot, welding robot (1.5); goliath crane (0.6); spmt (0.8); welding gantry (0.5). Plurals and aliases accepted; setters, mechanics and other walking staff: operator. A kind sets the model, that speed and units = Physical (m/s), nothing else. Empty = the look of its travel and the component default: speed 1, units = Model (1 distance unit per model time unit). |

### `simulean_load_table`

Give a Schedule Source its arrivals (columns Time, Name, Q, labels), a RateTable Source its periods (columns Start, Rate, Name, labels: a table with Start and Rate and no Time puts the source in RateTable mode), or a station with a setup its setup matrix (columns from, to, time), from a table file (CSV, .data or Excel): a Data Table named after the source / '<station> setup matrix' reads the file (created or reused) and is loaded; with readAtRun the runs read the file as it is when they start.

| Argument | Type | Default | |
|---|---|---|---|
| `object` | string | required | The Source, or a station with a setup (name or path). |
| `file` | string | required | Table file: absolute, or relative to the project or to Assets/ — keep it in the model's folder (Assets/SimuLeanModels/<Model>/file.csv; a bare name is not looked up there). Read into the scene, not copied. |
| `sheet` | string | — | Excel sheet (empty = the first). |
| `readAtRun` | bool | `false` | Read the file at each run start (else the loaded copy). |

### `simulean_place_model`

Place a 3D model of the library (decoration: furniture, beds, walls…) in the open scene: a model folder name (e.g. HospitalBed), a file under it (WaitingChairs/Seat_Table) or an asset path (.fbx / .prefab). Empty asset = the list of library models. Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `asset` | string | — | Library 3D asset (folder, folder/file) or asset path; empty = list them. |
| `name` | string | — | GameObject name (empty = the model's). |
| `x` | float | `0` | World X. |
| `y` | float | `0` | World Y. |
| `z` | float | `0` | World Z. |
| `rotY` | float | `0` | Rotation about the vertical axis (degrees). |
| `scale` | float | `1` | Uniform scale. |
| `parent` | string | — | Parent GameObject (name or path); empty = a root 'Decor' group. |

### `simulean_remove`

Delete a SimuLean object (element, team, list, data table, reaction, tool…) with what it contains; every reference to it in other objects (Next Elements, processBy.team, pushToList, dataTable, reaction / downtime targets…) is cleared and listed, and texts that still name it are warned. One undo step.

| Argument | Type | Default | |
|---|---|---|---|
| `object` | string | required | The object (name or path). |
| `key` | string | — | Not taken: the whole object is removed. Given, nothing is removed and the answer says how to clear a field. |

### `simulean_rename`

Rename a SimuLean object (its GameObject name, and the team / list / table name it carries): the new name must be free; texts that name the old one (expressions, queries, specs) are warned, not changed. Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `object` | string | required | The object (name or path). |
| `newName` | string | required | The new name. |

### `simulean_move`

Move an element, a team or a tool on the floor to x / z (world, metres; its height kept) with what it holds: a rack its lanes, a team its members (those with a spawn point or a home node stay there). Answers where it stood and stands, its footprint, the footprints it now overlaps and, when its place enters a run, the warning 'travel distances changed': an element a Straight team walks to (one without a location node), a Straight team without a home node (where its members start), a transport node (its tracks' lengths). Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `object` | string | required | The element, team or tool (name or path). |
| `x` | float | required | World X to move it to (m). |
| `z` | float | required | World Z to move it to (m). |

## Building in bulk

### `simulean_apply`

Run a list of edits in one call, all or nothing (one undo step). Every operation is tried: on errors nothing is changed and the answer names every bad operation (a later one may fail because of an earlier one). validateOnly=true checks the list the same way and changes nothing. JSON array of {"op": …}: add (type name x y z parent), set (object key value), connect / disconnect (from to), reorder (object next), remove (object), rename (object newName), move (object x z: as simulean_move), add_team (name members travel kind x y z parent; without x / y / z it is placed once the whole list is applied, in front of the model, so it does not stand on an element the list adds later), invoke (object action; not run by validateOnly), repeat (group count offset names connectFrom connectTo).

| Argument | Type | Default | |
|---|---|---|---|
| `operations` | string | required | JSON array, e.g. [{"op":"add","type":"UnityQueue","name":"Q1","x":2}, {"op":"set","object":"Q1","key":"capacity","value":5}, {"op":"connect","from":"Src","to":"Q1"}]. |
| `validateOnly` | bool | `false` | Check every operation (each one tried, then all undone) and report all the bad ones; the scene is not changed. |
| `detail` | string | `"summary"` | summary (default: the count per kind of operation, the objects created — their names as placed —, the racks rebuilt and every warning) or full (one result per operation). |

### `simulean_repeat`

Copy a group of objects (elements, with their children) N times, each copy moved by the offset and renamed by the pattern; connections inside the group point to the copy's own members, connections to objects outside it are kept. connectFrom 'Outside>Member;…' connects an outside element to each copy's member; connectTo 'Member>Outside;…' each copy's member to an outside element. One undo step.

| Argument | Type | Default | |
|---|---|---|---|
| `group` | string | required | The objects to copy, 'A;B;C' (names or paths). |
| `count` | int | `1` | Number of copies. |
| `offset` | string | — | Move of each copy from the previous one, 'x,y,z' (e.g. '0,0,6'). |
| `names` | string | — | Name pattern of the copies: {name} = the original's, {i} = the copy's number 1…N (default '{name}_{i}'). |
| `connectFrom` | string | — | Outside elements feeding each copy: 'Router>Queue;…' (Queue = the member of the group). |
| `connectTo` | string | — | Each copy feeding outside elements: 'Station>Exit;…'. |

### `simulean_invoke`

Run an action of an object's components: one marked for agents ([SimuLeanAction]: fill a travel matrix, rebuild a floor zone, bake a walk grid…) or a context-menu action. Without action, lists them. Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `object` | string | required | The object (name or path). |
| `action` | string | — | The action's method name or menu label (empty = list them). |

## Running and studying

### `simulean_run_headless`

Run the scene's model headless (the scene is not changed) and return per scenario the end reasons counted, the named metrics (6 significant digits) and, when a run deadlocked or stalled, the model block (makespan, deadlock and stall times…); with detail=full (the default without metrics) every digit, the model block, the elements' (input, output, content, stay, utilisation) and resources' KPIs and each run's end reason and metric values (perRun). An answer over ~40 KB is a table of the first scenarios plus page: the arguments (top, offset, sort) that read every scenario, a page at a time, with simulean_results action=read.

| Argument | Type | Default | |
|---|---|---|---|
| `horizon` | double | `-1` | Horizon in model time units (-1 = the model's). |
| `replications` | int | `1` | Replications (seeds seed, seed+1, …). |
| `seed` | int | `1` | First seed. |
| `factors` | string | — | Catalog overrides 'path=value;…' (e.g. 'Buffer.capacity=4;team:Crew.members[skill=weld]=3'); a value list 'path=1\|2\|4' makes one scenario per level. Or JSON: {"Buffer.capacity": [2, 4], "Src.tableSource": "sql: SELECT a; b"} (values may then hold ; = \|). |
| `metrics` | string | — | Named metrics 'name=path;…' (e.g. 'span=model.makespan;out=Sink.input'), or JSON {"span": "model.makespan", "out": "Sink.input"}. |
| `root` | string | — | Model root GameObject (empty = the scene's model). |
| `detail` | string | — | 'metrics' = the named metrics per scenario to 6 significant digits, the model block when a run stalled (the default when metrics are given: small enough for an agent's context); 'full' = every digit, the model block, every element's and resource's KPIs and perRun (each run's end reason and metric values). |
| `scenarios` | string | — | Explicit scenarios instead of factors (JSON): [{"name": "A", "Buf.capacity": 2, "replications": 5}, ...] - every scenario gives every factor. |
| `where` | string | — | Keep only the combinations of the factors for which this holds, e.g. 'B1_capacity + B2_capacity <= 10' (a factor path with every character that is not a letter or digit as _). |
| `derived` | string | — | Metrics computed per replication from the others and the study's factors (named as in where: B1.capacity -> B1_capacity), 'name=expression;...' (e.g. 'days=floor(span/24)', 'profit=10*out-100*B1_capacity'); reported as mean +- half-width like them. |
| `confidence` | double | `0` | Confidence level of the half-widths (0 = 0.95). |
| `warmup` | double | `-1` | Warm-up time: statistics start after it (-1 = the model's). |
| `noProgress` | double | `-1` | End condition: stop a run when no item has moved for this time while items wait (end reason NoProgress: a stall beside a periodic signal or reaction; 0 = off, -1 = the model's). |
| `compareTo` | string | — | A scenario (its name or its 1-based position) the others are compared with: each other scenario gets paired = per metric the mean difference to it over the runs of the same seed and its half-width (common random numbers: far tighter than comparing two means). |
| `experimenter` | string | — | Run a scene Experimenter's study (its factors, scenarios, metrics and settings); horizon, replications other than 1, seed other than 1 and the metrics given here override / are added. |
| `top` | int | `0` | A page of the answer: how many scenarios it gives (0 = 25); an answer over ~40 KB gives a table of the first ones and the arguments of its pages. |
| `sort` | string | — | A page's order: a metric (ascending), -metric or 'metric desc' descending (empty = the plan's order; a large answer's table: the first metric). |
| `offset` | int | `0` | A page's first scenario, from 0 (the answer's page.next gives the next page). |

### `simulean_run_start`

Start simulean_run_headless in the background (same arguments) and return at once: a long study no longer blocks the editor or times the MCP out. Poll simulean_run_status; one run at a time.

| Argument | Type | Default | |
|---|---|---|---|
| `horizon` | double | `-1` | Horizon in model time units (-1 = the model's). |
| `replications` | int | `1` | Replications (seeds seed, seed+1, …). |
| `seed` | int | `1` | First seed. |
| `factors` | string | — | Catalog overrides 'path=v1\|v2;…' or JSON, as simulean_run_headless. |
| `metrics` | string | — | Named metrics 'name=path;…' or JSON, as simulean_run_headless. |
| `root` | string | — | Model root GameObject (empty = the scene's model). |
| `detail` | string | — | 'metrics' or 'full', as simulean_run_headless. |
| `scenarios` | string | — | Explicit scenarios instead of factors (JSON): [{"name": "A", "Buf.capacity": 2, "replications": 5}, ...] - every scenario gives every factor. |
| `where` | string | — | Keep only the combinations of the factors for which this holds, e.g. 'B1_capacity + B2_capacity <= 10' (a factor path with every character that is not a letter or digit as _). |
| `derived` | string | — | Metrics computed per replication from the others and the study's factors (named as in where: B1.capacity -> B1_capacity), 'name=expression;...' (e.g. 'days=floor(span/24)', 'profit=10*out-100*B1_capacity'); reported as mean +- half-width like them. |
| `confidence` | double | `0` | Confidence level of the half-widths (0 = 0.95). |
| `warmup` | double | `-1` | Warm-up time: statistics start after it (-1 = the model's). |
| `noProgress` | double | `-1` | End condition: stop a run when no item has moved for this time while items wait (end reason NoProgress: a stall beside a periodic signal or reaction; 0 = off, -1 = the model's). |
| `compareTo` | string | — | A scenario (its name or its 1-based position) the others are compared with: each other scenario gets paired = per metric the mean difference to it over the runs of the same seed and its half-width (common random numbers: far tighter than comparing two means). |
| `experimenter` | string | — | Run a scene Experimenter's study (its factors, scenarios, metrics and settings); horizon, replications other than 1, seed other than 1 and the metrics given here override / are added. |

### `simulean_run_status`

The background run of simulean_run_start: 'running' (runs done / in all), then 'completed' with the same answer as simulean_run_headless (over ~40 KB a table and its pages: top, offset, sort), or 'failed'. cancel=true stops it; wait=s answers as soon as the run ends (at most 55 s).

| Argument | Type | Default | |
|---|---|---|---|
| `cancel` | bool | `false` | Stop the run (the runs done so far are kept). |
| `wait` | double | `0` | Seconds to wait for the job to end (0 = answer at once; at most 55): the answer comes as soon as it ends, so poll with wait instead of sleeping. |
| `top` | int | `0` | A page of the answer: how many scenarios it gives (0 = 25); an answer over ~40 KB gives a table of the first ones and the arguments of its pages. |
| `sort` | string | — | A page's order: a metric (ascending), -metric or 'metric desc' descending (empty = the plan's order; a large answer's table: the first metric). |
| `offset` | int | `0` | A page's first scenario, from 0 (the answer's page.next gives the next page). |

### `simulean_diagnose`

Run the scene's model headless (one replication) to a time — or, with time 0, until nothing can happen any more (stall, deadlock or empty; at most the horizon) — and report its state: stalled / deadlocked and when, each element holding items, stopped or busy (state, items, what it waits for), each team's pending requests (who, how many, for how long) and members (state, task, for whom, since when), the lists, and the progress (last item movement, oldest request, longest task) with notes when the model has not moved for a quarter of the run or more although events keep it from being called stalled (shifts, reactions): facts to check — a real blocking of the design or the model's logic. factors = one level per factor (a what-if); noProgress ends the run when nothing has moved for that time (end reason NoProgress). The scene is not changed.

| Argument | Type | Default | |
|---|---|---|---|
| `until` | double | `-1` | Model time to stop at (-1 = until nothing can happen, at most the horizon). |
| `seed` | int | `1` | Model seed of the run. |
| `factors` | string | — | Catalog overrides 'path=value;…' or JSON, one level each (as simulean_run_headless). |
| `all` | bool | `false` | List every element, not only those holding items, stopped or busy. |
| `root` | string | — | Model root GameObject (empty = the scene's model). |
| `noProgress` | double | `-1` | End condition: stop when no item has moved for this time while items wait (end reason NoProgress) - finds a stall beside a periodic signal or reaction, which keep the run from becoming idle (0 = off, -1 = the model's). |

## Optimising

### `simulean_optimize_start`

Start an optimisation in the background and return at once (poll simulean_optimize_status with wait). Either give the search here — factors (study paths with a range 'Buffer.capacity=1..10;team:Welders.members=1..4', or options 'a\|b\|c', searched over those values only), metrics ('name=path;…' as simulean_run_headless) and fitness (maximised expression over the metric names; negate to minimise) — or name a scene Optimizer set up in the inspector; the other arguments override its settings. Checked before it starts. One search at a time.

| Argument | Type | Default | |
|---|---|---|---|
| `factors` | string | — | Decision variables: 'path=lo..hi' (a range) or 'path=a\|b\|c' (those values only, numbers too) or JSON {"path": [lo, hi]} (empty = the Optimizer's). |
| `metrics` | string | — | Observed values 'name=path;…' or JSON (empty = the Optimizer's). |
| `fitness` | string | — | Maximised expression over the metric names, e.g. 'out - 50*operators' (empty = the Optimizer's, or the only metric). |
| `optimizer` | string | — | A scene Optimizer (name or path) whose setup is used (empty = none: the search given here). |
| `algorithm` | string | — | 'genetic' or 'random' (any AlgorithmSettings type by name; empty = the Optimizer's, else genetic). |
| `algorithmSettings` | string | — | The algorithm's settings 'populationSize=20;generations=10' or JSON. |
| `maxEvaluations` | int | `-1` | Stop after this many candidates (0 = the algorithm's end; -1 = the Optimizer's). |
| `maxSeconds` | double | `-1` | Stop after this many seconds (0 = no limit; -1 = the Optimizer's). |
| `replications` | int | `-1` | Runs per candidate, common random numbers (-1 = the Optimizer's, else 1). |
| `horizon` | double | `-1` | Simulated time of each run (-1 = the Optimizer's, else the model's). |
| `seed` | int | `-1` | Seed of the runs and of the algorithm (-1 = the Optimizer's, else 1). |
| `threads` | int | `-1` | Candidates simulated at once (-1 = the Optimizer's, else half the logical processors, 1 to 16). |
| `root` | string | — | Model root GameObject when no Optimizer is named (empty = the scene's model). |

### `simulean_optimize_status`

The search of simulean_optimize_start: 'running' (iteration, candidates, best so far), then 'completed' with the best candidate (fitness, values) and its metrics re-run for every replication (mean ± half-width), or 'failed'. wait=s answers as soon as it ends (at most 55 s); cancel=true stops it (the best so far is kept).

| Argument | Type | Default | |
|---|---|---|---|
| `cancel` | bool | `false` | Stop the search (the best so far is the result). |
| `wait` | double | `0` | Seconds to wait for the search to end (0 = answer at once; at most 55). |

## Results and data

### `simulean_results`

Result sets: action=list (the last one and the stored ones), read (a set's metrics per scenario, mean ± half-width; the last study's answer as its command gave it, a page at a time with top / offset / sort, every digit and each run with detail=full), save (keep a set in the project's store), export (files: csv, excel, sqlite, json), chart (a PNG / SVG: kind bar \| box \| histogram \| scatter \| line \| states; yAxis and xAxis written as in a study: a named metric, a factor path or a statistic path 'last' (default), a stored set's id or its name) or delete (path= a file or folder that export, chart, simulean_capture or simulean_record_start wrote in this editor session under the results folder or Recordings/, to the OS trash; or resultSet= a set that save stored in this session, removed from the store; anything else is refused).

| Argument | Type | Default | |
|---|---|---|---|
| `action` | string | `"list"` | list \| read \| save \| export \| chart \| delete. |
| `resultSet` | string | `"last"` | 'last' (the last run / study / optimisation), a stored set's id or its name. |
| `formats` | string | `"csv"` | export: 'csv;excel;sqlite;json' (any of them). |
| `kind` | string | `"bar"` | chart: bar \| box \| histogram \| scatter \| line \| states. |
| `yAxis` | string | — | chart: the value, as a study writes it — a named metric (out), a factor (Buf.capacity) or a statistic path (Srv.utilization, Srv;Buf.utilization, *.utilization, team:*.utilization, model.makespan). |
| `xAxis` | string | — | chart: scatter's x axis (as yAxis). |
| `groupBy` | string | `"scenario"` | chart: scenario \| object \| run. |
| `title` | string | — | chart: its title. |
| `path` | string | — | chart: the file, .png or .svg (empty = Recordings/Charts/<set>_<time>.png); delete: the file or folder to delete (as the command that wrote it answered, or relative to the project). |
| `name` | string | — | save: the stored set's name (empty = the set's). |
| `detail` | string | — | read: 'metrics' (the compact answer: the named metrics, 6 significant digits) or 'full' (every digit, the model block, the elements' and resources' KPIs and perRun: each run's end reason and metric values); empty = as the study answered (a stored set: metrics). |
| `top` | int | `0` | read: how many scenarios a page gives (0 = 25; an answer over ~40 KB without top / offset / sort is a table of the first ones). |
| `sort` | string | — | read: a page's order — a metric (ascending), -metric or 'metric desc' descending (empty = the plan's order). |
| `offset` | int | `0` | read: a page's first scenario, from 0 (page.next of an answer gives the next page). |

### `simulean_query`

The project database (SQLite): without arguments, its tables and their row counts; sql = run a statement (rows of a SELECT, at most maxRows); import = load a CSV / Excel file into a table (replacing it unless append).

| Argument | Type | Default | |
|---|---|---|---|
| `sql` | string | — | A statement, e.g. SELECT * FROM arrivals WHERE day = 2. |
| `import` | string | — | A CSV / Excel file to load (absolute or relative to the project). |
| `table` | string | — | import: the table's name (empty = the file's name). |
| `sheet` | string | — | import: the Excel sheet (empty = the first). |
| `append` | bool | `false` | import: add the rows to the table instead of replacing it. |
| `maxRows` | int | `50` | sql: at most this many rows in the answer. |

### `simulean_fit`

Fit distributions to data and rank them (Anderson-Darling or Kolmogorov-Smirnov): each with its parameters, the sampler spec, the p-values and a verdict. The data: values, or a column of an SQL query on the project database or of a file. into = write the best (or pick-th) fit into a distribution field (Element.field, e.g. Srv.delayStrategy; undoable).

| Argument | Type | Default | |
|---|---|---|---|
| `values` | string | — | The data, '1.2, 3.4, …'. |
| `sql` | string | — | A query on the project database; its column (or the first) is the data. |
| `file` | string | — | A CSV / Excel file; its column (or the first) is the data. |
| `column` | string | — | The column of the query / file. |
| `sheet` | string | — | file: the Excel sheet. |
| `criterion` | string | `"ad"` | 'ad' (Anderson-Darling) or 'ks' (Kolmogorov-Smirnov). |
| `top` | int | `5` | How many ranked fits to answer. |
| `into` | string | — | Write a fit into this distribution field: 'Element.field' (e.g. 'Srv.delayStrategy'). |
| `pick` | int | `1` | into: which ranked fit (1 = the best). |

## Seeing and recording

### `simulean_capture`

Render a PNG of the scene: the named objects (empty = the whole model: its elements, transport nodes, teams' members, lists, downtimes and reactions — not the floor or decoration) seen from an elevation and azimuth at the distance that fits them, or a shot of the scene's Recorder. The connections are drawn as coloured bands with arrows (flow, lists and the network's tracks by default) and an object without a mesh as a labelled disc. Returns the file's path (open it to see the picture), what was framed and drawn, and the camera used. The scene is not changed.

| Argument | Type | Default | |
|---|---|---|---|
| `objects` | string | — | Objects to frame 'A;B' (names or paths; empty = the whole model). |
| `elevation` | float | `40` | Degrees above the horizon (90 = from above). |
| `azimuth` | float | `30` | Compass direction the camera looks along (0 = +Z, 90 = +X). |
| `distance` | float | `0` | Distance from the centre (0 = fit the targets). |
| `fov` | float | `40` | Vertical field of view in degrees. |
| `shot` | string | — | A shot of the scene's Recorder instead (its name): its framing at its start. |
| `width` | int | `1280` | Image width in pixels. |
| `height` | int | `720` | Image height in pixels. |
| `path` | string | — | PNG file (empty = Recordings/Captures/capture_<time>.png in the project). |
| `connections` | string | — | Connections drawn: groups comma-separated (flow, lists, resources, locations, network, downtimes, logic), all or none (empty = flow,lists,network). resources = every use of a team: process (and its requirements' teams), setup crew, transport and a downtime's repair crew. |

### `simulean_record_shot`

Add or change a shot of the scene's Recorder (one is placed when there is none): framing fixed (position/rotation, or fromView = the Scene view's camera), element (target seen from distance/elevation/azimuth: follows the layout), orbit (the same, turning orbit degrees) or pose (a placed GameObject); seconds; glide (a smooth curve from the previous shot); caption title / subtitle. Empty arguments keep the shot's values (a single space clears a text). Without 'shot': the list. Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `shot` | string | — | Shot name: changed when it exists, added otherwise; empty = list the shots. |
| `framing` | string | — | fixed \| pose \| element \| orbit (empty = element when an object is given, else unchanged / fixed). |
| `object` | string | — | Element / orbit: the framed GameObject (name or path). |
| `distance` | string | — | Element / orbit: metres from the framed point. |
| `elevation` | string | — | Element / orbit: degrees above the horizon. |
| `azimuth` | string | — | Element / orbit: compass direction the camera looks along (degrees, 0 = +Z, 90 = +X). |
| `orbit` | string | — | Orbit: degrees turned during the shot. |
| `lookHeight` | string | — | Element / orbit: framed point's height above the element (m). |
| `position` | string | — | Fixed: camera position 'x,y,z'. |
| `rotation` | string | — | Fixed: camera rotation 'x,y,z' (Euler degrees). |
| `pose` | string | — | Pose: the GameObject whose position and rotation the camera takes. |
| `fromView` | bool | `false` | Take the Scene view's camera: a fixed shot, or with a target its angle and distance. |
| `seconds` | string | — | Length in seconds of video. |
| `glide` | string | — | true = glide from the previous shot, false = cut. |
| `glideSeconds` | string | — | Seconds of the glide (0 = the whole shot). |
| `fov` | string | — | Field of view (0 = the recorder's). |
| `title` | string | — | Caption title (placeholders {content:Element}, {time}…). |
| `subtitle` | string | — | Caption subtitle. |
| `index` | string | — | Position in the list (0 = first) to insert or move the shot to; empty = keep / append. |
| `remove` | bool | `false` | Remove the shot. |
| `recorder` | string | — | The Recorder (name or path; empty = the scene's). |

### `simulean_record_start`

Record a video of the scene in Play with its Recorder: the clock is driven to the start date, then the shots are rendered at a fixed frame rate with their captions into an MP4 (Unity's encoder: nothing to install); Play ends with the take. Returns at once: poll simulean_record_status. The settings given apply to this take only.

| Argument | Type | Default | |
|---|---|---|---|
| `scene` | string | — | Scene to open (asset path or name; empty = the open one). Modified scenes must be saved first. |
| `startAt` | string | — | Model date of the first frame (e.g. '2025-03-11 08:50'; empty = the recorder's). |
| `seconds` | float | `0` | Video length (0 = the shots' total; given, the shots are stretched to it). |
| `fps` | int | `0` | Frames per second (0 = the recorder's). |
| `width` | int | `0` | Frame width (0 = the recorder's). |
| `height` | int | `0` | Frame height (0 = the recorder's). |
| `exitPlay` | bool | `true` | Leave Play when the take ends. |
| `recorder` | string | — | The Recorder (name or path; empty = the scene's). |
| `quality` | string | — | Standard \| High \| Maximum (empty = the recorder's): the MP4's bitrate by the frame size and rate. |
| `bitrateMbps` | float | `0` | MP4 bitrate in Mbps (0 = by the quality). |

### `simulean_record_status`

The take of simulean_record_start: 'starting', 'approaching' (the clock driven to the start date), 'recording' (frames / in all), then 'completed' with the MP4's path, 'failed' or 'stopped'. cancel=true ends Play (the video so far is kept); wait=s answers as soon as the take ends (at most 55 s).

| Argument | Type | Default | |
|---|---|---|---|
| `cancel` | bool | `false` | Stop the take. |
| `wait` | double | `0` | Seconds to wait for the job to end (0 = answer at once; at most 55): the answer comes as soon as it ends, so poll with wait instead of sleeping. |

## Tests

### `simulean_test`

Start the SimuLean EditMode tests (SimuLeanTestRunner; filter = part of a test name, empty = all); poll simulean_test_status until 'completed'.

| Argument | Type | Default | |
|---|---|---|---|
| `filter` | string | — | Part of a test's full name (empty = all). |

### `simulean_test_status`

Status of the SimuLean EditMode tests started with simulean_test (JSON: status, passed, failed, failures); wait=s answers as soon as the run ends (at most 55 s).

| Argument | Type | Default | |
|---|---|---|---|
| `wait` | double | `0` | Seconds to wait for the job to end (0 = answer at once; at most 55): the answer comes as soon as it ends, so poll with wait instead of sleeping. |

## Other

### `simulean_card`

A model's card: what the model represents, the KPIs that matter, the parameters worth varying, the assumptions, plus its name, description, author, dates and the example it was copied from. No field = the whole card; field + value = set it (displayName, description, author, represents, kpis, parameters, assumptions). Read it when you start on a model; write what you learn. scripts (the open model): its choice rules (owner, setting, Why, reads, parameters) and its decisions by rung of the ladder field / expression / query / reaction / rule.

| Argument | Type | Default | |
|---|---|---|---|
| `field` | string | — | The field to set (empty = read the card). |
| `value` | string | — | Its new text (with field). |
| `model` | string | — | The model (empty = the open scene's). |

### `simulean_models`

The project's models (one folder each under the models folder: scene, model card, data) and the example scenes. action: list (default; with the examples), new (a folder of that name that holds no model yet, e.g. its data, is used as it is), open, duplicate, rename, copy_example (an example is never opened itself: it is copied into a new model, whose card names it), save (the open model, when these commands created or opened it in this editor session; never an example or another scene). Opening refuses while a scene has unsaved changes.

| Argument | Type | Default | |
|---|---|---|---|
| `action` | string | `"list"` | list \| new \| open \| duplicate \| rename \| copy_example \| save. |
| `model` | string | — | The model (new: its name; open / duplicate / rename: an existing model's name; save: empty = the open one, else it must be the open one). |
| `newName` | string | — | duplicate / rename: the new name (duplicate: empty = a free numbered name). |
| `example` | string | — | copy_example: the example (its name or scene path, as list gives them). |
| `open` | bool | `true` | new / duplicate / copy_example: open the model afterwards. |
| `timeUnit` | string | — | new: the model's time unit — s (the default), min, h, d, or the seconds per unit (e.g. 60). Every time of the model (delays, horizon, warm-up) is in it. |
| `horizon` | double | `-1` | new: the run length (the clock's maxTime) in that unit (-1 = the clock's default; the answer gives it). |
| `seed` | int | `-1` | new: a fixed model seed (the clock's seed mode Fixed with this seed: every run of the scene is reproducible; -1 = a new seed each run, the default). |
| `warmup` | double | `-1` | new: the warm-up in the model's time unit (statistics are reset then; -1 = none). |

### `simulean_reorder`

Put the Next Elements of an element in a new order (the order routing by index reads: round robin, probability weights, an expression's output number): 'next' lists the same elements, ';'-separated, in their new order. Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `object` | string | required | The element whose Next Elements are reordered (name or path). |
| `next` | string | required | Its Next Elements in their new order, e.g. 'Line B;Line A' (every one of them, each once). |

### `simulean_rule_check`

Check a choice rule: found after the last compile, its site, the contract scan (no static state, no System.Random, threads or I/O), its parameters, source hash and Why; with object, the element / team that uses it in the scene's model: the model builds with it, and a run of horizon (-1 = the model's) twice with one seed makes the same decisions (counted).

| Argument | Type | Default | |
|---|---|---|---|
| `name` | string | — | The rule's class name. |
| `object` | string | — | An element / team using it (empty = the rule alone). |
| `horizon` | double | `-1` | The check run's length (-1 = the model's). |
| `seed` | int | `-1` | The check run's seed (-1 = 1). |

### `simulean_rule_new`

Write a choice rule from a template: kind routing (scores each next element), release (scores each item a Queue holds) or dispatch (scores each pending request of a team); name = its class name; why = the declarative form tried first (BestScore expression, offerSort, a request order, a reaction) and why it is not enough — refused without it. Writes Assets/SimuLean.UserLogic/<name>.cs and starts a recompile: edit its Score, then simulean_rule_check.

| Argument | Type | Default | |
|---|---|---|---|
| `kind` | string | — | The site: routing, release or dispatch. |
| `name` | string | — | The rule's class name (a C# identifier, e.g. NearestFreeCell). |
| `why` | string | — | The declarative form tried first and why it is not enough (required). |

### `simulean_rule_show`

Show a choice rule: its site, label, Why, declared reads, parameters (type, default, range; catalog keys <Element>.rule.<Field>), source file and hash, and the elements / teams of the scene's model that use it with their parameter values. Empty name = every rule.

| Argument | Type | Default | |
|---|---|---|---|
| `name` | string | — | The rule's class name (empty = list every rule). |
