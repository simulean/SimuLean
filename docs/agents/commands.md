# SimuLean MCP commands

> Generated from the code by `Tools/Docs/command_reference.py` — do not edit by hand (run it after changing a
> command; `--check` fails when this file is not current). How to use them together: [workflow.md](workflow.md);
> what their errors mean: [errors.md](errors.md).

31 commands. Every answer is a JSON object with the command's fields plus `errors` and `warnings` (what was
logged while it ran). Objects are named by their GameObject name (unique) or their hierarchy path. String
arguments that take a list accept the shorthand shown or JSON.

## Describing the model

### `simulean_types`

List the SimuLean element types and tools that can be placed (the Library's entries); with 'type', that type's fields (simulean_set) and catalog keys (simulean_run_headless --set).

| Argument | Type | Default | |
|---|---|---|---|
| `type` | string | — | A Library name or component type (e.g. 'Queue', 'UnityQueue'); empty = all. |

### `simulean_params`

An element's (or tool's) fields with their values — the keys simulean_set writes — and its catalog keys (the parameters a study varies).

| Argument | Type | Default | |
|---|---|---|---|
| `element` | string | required | GameObject name or hierarchy path. |

### `simulean_model`

The scene's model in one compact answer, as a run builds it: the clock (time unit, horizon, warm-up, end condition, seed), every element (type, next elements in order, parameters under the keys a study varies), the other connections, teams (members, labels, parameters), lists, downtimes, reactions and data tables — all by name. Read-only.

| Argument | Type | Default | |
|---|---|---|---|
| `element` | string | — | Only this element / team / list / downtime / reaction / table (empty = the whole model). |
| `parameters` | bool | `true` | Include the parameters (false = the structure only: smaller). |
| `root` | string | — | Model root GameObject (empty = the scene's model). |

### `simulean_validate`

Check the scene's model without running it: extract it, build it (each scenario of the optional study: set / metrics as simulean_run_headless) and resolve the metrics. Answers ok plus the errors and warnings a run would meet (extractor, build, study), with "did you mean".

| Argument | Type | Default | |
|---|---|---|---|
| `set` | string | — | Catalog overrides 'path=v1\|v2;…' or JSON to check too (as simulean_run_headless). |
| `metrics` | string | — | Named metrics 'name=path;…' or JSON to check too. |
| `root` | string | — | Model root GameObject (empty = the scene's model). |
| `scenarios` | string | — | Explicit scenarios instead of set (JSON): [{"name": "A", "Buf.capacity": 2, "replications": 5}, ...] - every scenario gives every factor. |
| `where` | string | — | Keep only the combinations of set's factors for which this holds, e.g. 'B1_capacity + B2_capacity <= 10' (a factor path with every character that is not a letter or digit as _). |
| `derived` | string | — | Metrics computed per replication from the others, 'name=expression;...' (e.g. 'days=floor(span/24)'); reported as mean +- half-width like them. |
| `confidence` | double | `0` | Confidence level of the half-widths (0 = 0.95). |
| `warmup` | double | `-1` | Warm-up time: statistics start after it (-1 = the model's). |
| `experimenter` | string | — | Run a scene Experimenter's study (its factors, scenarios, metrics and settings); maxTime, replications other than 1, seed other than 1 and the metrics given here override / are added. |

## Building

### `simulean_add_element`

Place a Library entry (element, tool or preset) in the open scene, as the SimuLean window does. Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `type` | string | required | A Library name or component type (simulean_types). |
| `name` | string | — | GameObject name (empty = the Library's, made unique). |
| `x` | float | `0` | World X. |
| `y` | float | `0` | World Y. |
| `z` | float | `0` | World Z. |
| `parent` | string | — | Parent GameObject (name or path); empty = its hierarchy group. |

### `simulean_set`

Set a field of an element or tool (simulean_params lists them): a name or path such as 'capacity', 'delayStrategy.constantValue', 'processBy.team' (object fields take a GameObject name, or an asset path such as 'Assets/…/Doctor.prefab'), 'mode' (enums by name), lists comma-separated; a list item's field as 'actions[0].kind' (an index past the end adds items); an element's user property as 'properties.NAME' (created when missing; a number, or else a text). Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `element` | string | required | GameObject name or hierarchy path. |
| `key` | string | required | Field name or path. |
| `value` | string | required | The value as text. |

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

Place a resource team (operators, cranes, vehicles…) with n members. Assign it with simulean_set <station> processBy.team <team> (or transportBy.team). Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `name` | string | required | Team name. |
| `members` | int | `1` | Number of members. |
| `travel` | string | — | Travel mode (TeamTravel: Abstract, Network, Matrix, Axes…); empty = the default. |
| `x` | float | `0` | World X. |
| `y` | float | `0` | World Y. |
| `z` | float | `0` | World Z. |
| `parent` | string | — | Parent GameObject (name or path); empty = its hierarchy group. |

### `simulean_load_table`

Give a Schedule Source its arrivals from a table file (CSV, .data or Excel): a Data Table named after the source reads the file (created or reused) and is loaded; with readAtRun the runs read the file as it is when they start.

| Argument | Type | Default | |
|---|---|---|---|
| `source` | string | required | The Source (name or path). |
| `file` | string | required | Table file: absolute, or relative to the project or to Assets/. |
| `sheet` | string | — | Excel sheet (empty = the first). |
| `readAtRun` | bool | `false` | Read the file at each run start (else the loaded copy). |

### `simulean_place_model`

Place a 3D model of the library (decoration: furniture, beds, walls…) in the open scene: a model folder name (e.g. HospitalBed), a file under it (WaitingChairs/Seat_Table) or an asset path (.fbx / .prefab). Empty model = the list of library models. Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `model` | string | — | Library model (folder, folder/file) or asset path; empty = list them. |
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

### `simulean_rename`

Rename a SimuLean object (its GameObject name, and the team / list / table name it carries): the new name must be free; texts that name the old one (expressions, queries, specs) are warned, not changed. Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `object` | string | required | The object (name or path). |
| `name` | string | required | The new name. |

## Building in bulk

### `simulean_apply`

Run a list of edits in one call, all or nothing (one undo step; at the first error every edit before it is undone and the error names the operation). JSON array of {"op": …}: add (type name x y z parent), set (element key value), connect / disconnect (from to), remove (object), rename (object name), add_team (name members travel x y z parent), invoke (object action), repeat (group count offset names connectFrom connectTo).

| Argument | Type | Default | |
|---|---|---|---|
| `operations` | string | required | JSON array, e.g. [{"op":"add","type":"UnityQueue","name":"Q1","x":2}, {"op":"set","element":"Q1","key":"capacity","value":5}, {"op":"connect","from":"Src","to":"Q1"}]. |

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

Run the scene's model headless (the scene is not changed) and return per scenario the named metrics and the model block (makespan, end reason, deadlock…); with detail=full (the default without metrics) also the elements' (input, output, content, stay, utilisation) and resources' KPIs.

| Argument | Type | Default | |
|---|---|---|---|
| `maxTime` | double | `0` | Horizon in model time units (0 = the clock's). |
| `replications` | int | `1` | Replications (seeds seed, seed+1, …). |
| `seed` | int | `1` | First seed. |
| `set` | string | — | Catalog overrides 'path=value;…' (e.g. 'Buffer.capacity=4;team:Crew.members[skill=weld]=3'); a value list 'path=1\|2\|4' makes one scenario per level. Or JSON: {"Buffer.capacity": [2, 4], "Src.tableSource": "sql: SELECT a; b"} (values may then hold ; = \|). |
| `metrics` | string | — | Named metrics 'name=path;…' (e.g. 'span=model.makespan;out=Sink.input'), or JSON {"span": "model.makespan", "out": "Sink.input"}. |
| `root` | string | — | Model root GameObject (empty = the scene's model). |
| `detail` | string | — | 'metrics' = only the named metrics and the model block per scenario (the default when metrics are given: small enough for an agent's context); 'full' = also every element's and resource's KPIs. |
| `scenarios` | string | — | Explicit scenarios instead of set (JSON): [{"name": "A", "Buf.capacity": 2, "replications": 5}, ...] - every scenario gives every factor. |
| `where` | string | — | Keep only the combinations of set's factors for which this holds, e.g. 'B1_capacity + B2_capacity <= 10' (a factor path with every character that is not a letter or digit as _). |
| `derived` | string | — | Metrics computed per replication from the others, 'name=expression;...' (e.g. 'days=floor(span/24)'); reported as mean +- half-width like them. |
| `confidence` | double | `0` | Confidence level of the half-widths (0 = 0.95). |
| `warmup` | double | `-1` | Warm-up time: statistics start after it (-1 = the model's). |
| `experimenter` | string | — | Run a scene Experimenter's study (its factors, scenarios, metrics and settings); maxTime, replications other than 1, seed other than 1 and the metrics given here override / are added. |

### `simulean_run_start`

Start simulean_run_headless in the background (same arguments) and return at once: a long study no longer blocks the editor or times the MCP out. Poll simulean_run_status; one run at a time.

| Argument | Type | Default | |
|---|---|---|---|
| `maxTime` | double | `0` | Horizon in model time units (0 = the clock's). |
| `replications` | int | `1` | Replications (seeds seed, seed+1, …). |
| `seed` | int | `1` | First seed. |
| `set` | string | — | Catalog overrides 'path=v1\|v2;…' or JSON, as simulean_run_headless. |
| `metrics` | string | — | Named metrics 'name=path;…' or JSON, as simulean_run_headless. |
| `root` | string | — | Model root GameObject (empty = the scene's model). |
| `detail` | string | — | 'metrics' or 'full', as simulean_run_headless. |
| `scenarios` | string | — | Explicit scenarios instead of set (JSON): [{"name": "A", "Buf.capacity": 2, "replications": 5}, ...] - every scenario gives every factor. |
| `where` | string | — | Keep only the combinations of set's factors for which this holds, e.g. 'B1_capacity + B2_capacity <= 10' (a factor path with every character that is not a letter or digit as _). |
| `derived` | string | — | Metrics computed per replication from the others, 'name=expression;...' (e.g. 'days=floor(span/24)'); reported as mean +- half-width like them. |
| `confidence` | double | `0` | Confidence level of the half-widths (0 = 0.95). |
| `warmup` | double | `-1` | Warm-up time: statistics start after it (-1 = the model's). |
| `experimenter` | string | — | Run a scene Experimenter's study (its factors, scenarios, metrics and settings); maxTime, replications other than 1, seed other than 1 and the metrics given here override / are added. |

### `simulean_run_status`

The background run of simulean_run_start: 'running' (runs done / in all), then 'completed' with the same answer as simulean_run_headless, or 'failed'. cancel=true stops it; wait=s answers as soon as the run ends (at most 55 s).

| Argument | Type | Default | |
|---|---|---|---|
| `cancel` | bool | `false` | Stop the run (the runs done so far are kept). |
| `wait` | double | `0` | Seconds to wait for the job to end (0 = answer at once; at most 55): the answer comes as soon as it ends, so poll with wait instead of sleeping. |

### `simulean_diagnose`

Run the scene's model headless (one replication) to a time — or, with time 0, until nothing can happen any more (stall, deadlock or empty; at most the horizon) — and report its state: stalled / deadlocked and when, each element holding items, stopped or busy (state, items, what it waits for), each team's pending requests (who, how many, for how long) and members (state, task, for whom, since when), the lists, and the progress (last item movement, oldest request, longest task) with notes when the model has not moved for a quarter of the run or more although events keep it from being called stalled (shifts, reactions): facts to check — a real blocking of the design or the model's logic. set = one level per factor (a what-if). The scene is not changed.

| Argument | Type | Default | |
|---|---|---|---|
| `time` | double | `0` | Model time to stop at (0 = until nothing can happen, at most the horizon). |
| `seed` | int | `1` | Model seed of the run. |
| `set` | string | — | Catalog overrides 'path=value;…' or JSON, one level each (as simulean_run_headless). |
| `all` | bool | `false` | List every element, not only those holding items, stopped or busy. |
| `root` | string | — | Model root GameObject (empty = the scene's model). |

## Optimising

### `simulean_optimize_start`

Start an optimisation in the background and return at once (poll simulean_optimize_status with wait). Either give the search here — parameters (study paths with a range 'Buffer.capacity=1..10;team:Welders.members=1..4', or options 'a\|b\|c', searched over those values only), metrics ('name=path;…' as simulean_run_headless) and fitness (maximised expression over the metric names; negate to minimise) — or name a scene Optimizer set up in the inspector; the other arguments override its settings. Checked before it starts. One search at a time.

| Argument | Type | Default | |
|---|---|---|---|
| `parameters` | string | — | Decision variables: 'path=lo..hi' (a range) or 'path=a\|b\|c' (those values only, numbers too) or JSON {"path": [lo, hi]} (empty = the Optimizer's). |
| `metrics` | string | — | Observed values 'name=path;…' or JSON (empty = the Optimizer's). |
| `fitness` | string | — | Maximised expression over the metric names, e.g. 'out - 50*operators' (empty = the Optimizer's, or the only metric). |
| `optimizer` | string | — | A scene Optimizer (name or path) whose setup is used (empty = none: the search given here). |
| `algorithm` | string | — | 'genetic' or 'random' (any AlgorithmSettings type by name; empty = the Optimizer's, else genetic). |
| `algorithmSettings` | string | — | The algorithm's settings 'populationSize=20;generations=10' or JSON. |
| `maxEvaluations` | int | `-1` | Stop after this many candidates (0 = the algorithm's end; -1 = the Optimizer's). |
| `maxSeconds` | double | `-1` | Stop after this many seconds (0 = no limit; -1 = the Optimizer's). |
| `replications` | int | `0` | Runs per candidate, common random numbers (0 = the Optimizer's, else 1). |
| `runTime` | double | `0` | Simulated time of each run (0 = the Optimizer's, else the clock's). |
| `seed` | int | `0` | Seed of the runs and of the algorithm (0 = the Optimizer's, else 1). |
| `threads` | int | `0` | Candidates simulated at once (0 = the Optimizer's, else one per processor up to 8). |
| `root` | string | — | Model root GameObject when no Optimizer is named (empty = the scene's model). |

### `simulean_optimize_status`

The search of simulean_optimize_start: 'running' (iteration, candidates, best so far), then 'completed' with the best candidate (fitness, values) and its metrics re-run for every replication (mean ± half-width), or 'failed'. wait=s answers as soon as it ends (at most 55 s); cancel=true stops it (the best so far is kept).

| Argument | Type | Default | |
|---|---|---|---|
| `cancel` | bool | `false` | Stop the search (the best so far is the result). |
| `wait` | double | `0` | Seconds to wait for the search to end (0 = answer at once; at most 55). |

## Results and data

### `simulean_results`

Result sets: action=list (the last one and the stored ones), read (a set's metrics per scenario, mean ± half-width), save (keep a set in the project's store), export (files: csv, excel, sqlite, json) or chart (a PNG / SVG: kind bar \| box \| histogram \| scatter \| line \| states; y and x as metric:NAME, stat:SCOPE:OBJECTS:STATISTIC or param:PATH). set = 'last' (default), a stored set's id or its name.

| Argument | Type | Default | |
|---|---|---|---|
| `action` | string | `"list"` | list \| read \| save \| export \| chart. |
| `set` | string | `"last"` | 'last' (the last run / study / optimisation), a stored set's id or its name. |
| `formats` | string | `"csv"` | export: 'csv;excel;sqlite;json' (any of them). |
| `kind` | string | `"bar"` | chart: bar \| box \| histogram \| scatter \| line \| states. |
| `y` | string | — | chart: the value — metric:out, stat:element:Srv;Buf:utilization (objects ';'-separated or *), param:Buf.capacity. |
| `x` | string | — | chart: scatter's x (as y). |
| `groupBy` | string | `"scenario"` | chart: scenario \| object \| run. |
| `title` | string | — | chart: its title. |
| `path` | string | — | chart: the file, .png or .svg (empty = Recordings/Charts/<set>_<time>.png). |
| `name` | string | — | save: the stored set's name (empty = the set's). |

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

Render a PNG of the scene: the named elements (empty = the whole model) seen from an elevation and azimuth at the distance that fits them, or a shot of the scene's Recorder. Returns the file's path (open it to see the picture) and the camera used. The scene is not changed.

| Argument | Type | Default | |
|---|---|---|---|
| `targets` | string | — | Objects to frame 'A;B' (names or paths; empty = every model element). |
| `elevation` | float | `40` | Degrees above the horizon (90 = from above). |
| `azimuth` | float | `30` | Compass direction the camera looks along (0 = +Z, 90 = +X). |
| `distance` | float | `0` | Distance from the centre (0 = fit the targets). |
| `fov` | float | `40` | Vertical field of view in degrees. |
| `shot` | string | — | A shot of the scene's Recorder instead (its name): its framing at its start. |
| `width` | int | `1280` | Image width in pixels. |
| `height` | int | `720` | Image height in pixels. |
| `path` | string | — | PNG file (empty = Recordings/Captures/capture_<time>.png in the project). |

### `simulean_record_shot`

Add or change a shot of the scene's Recorder (one is placed when there is none): framing fixed (position/rotation, or fromView = the Scene view's camera), element (target seen from distance/elevation/azimuth: follows the layout), orbit (the same, turning orbit degrees) or pose (a placed GameObject); seconds; glide (a smooth curve from the previous shot); caption title / subtitle. Empty arguments keep the shot's values (a single space clears a text). Without 'shot': the list. Undoable.

| Argument | Type | Default | |
|---|---|---|---|
| `shot` | string | — | Shot name: changed when it exists, added otherwise; empty = list the shots. |
| `framing` | string | — | fixed \| pose \| element \| orbit (empty = element when a target is given, else unchanged / fixed). |
| `target` | string | — | Element / orbit: the framed GameObject (name or path). |
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
