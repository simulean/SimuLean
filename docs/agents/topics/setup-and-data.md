# Topic: setups and data

Read after the core ([workflow.md](../workflow.md)) when a station changes over between products, when arrivals come
from a schedule, or when parameters come from data.

## Sequence-dependent setups

On the station (Multi-Server, Combiner…): `setup.when` = `None` (default), `LabelChange` (a setup when the label
`setup.label` differs from the previous item's) or the other options listed by `simulean_types`; `setup.time` (a
distribution, default `Constant~1` in a scene: the time of every setup no matrix row matches — set it, or give a `*,*`
row); `setup.setupFirst`; `setup.keepForProcess`. A setup crew:
`setup.setupBy.team` (resources: [resources.md](resources.md)).

A setup matrix is a data table `from` / `to` / `time`, loaded with `simulean_load_table object=Station file=matrix.csv`:

```csv
from,to,time
,*,10
A,B,5
B,A,Triangular~4~6~9
*,*,8
```

**Where the file goes and what it is called:** put the data file in the model's folder
(`Assets/SimuLeanModels/<Model>/matrix.csv`) and pass that path (`file` is absolute, or relative to the project or to
`Assets/`; a bare name is not looked up in the model's folder). The file is read into the scene (not copied); with
`readAtRun = true` every run reads it again, else a changed file warns. The command creates a Data Table named
`<Station> setup matrix` (for a Source: the object `<Source> table`, whose table name is the Source's name) and links it; a table already linked is
reused under its name (`data:<name>.file` in a study).

`*` is any value; an empty `from` is the first setup (no previous item); a `time` cell is a number or a distribution
text; the column names `from`, `to`, `time` are lower-case and case-sensitive. Lookup, most specific first: (from,
to), (from, *), (*, to), (*, *); no row = `setup.time`. The answer's `read`
shows each row as it was taken — `(first setup) → any: 10`, `A → B: 5`, `any → any: 8` (the first 12) — with the
`lookup` rule: check it before running.

- Group the waiting items by the station's setup: the Queue's `groupBySetup = true`, `groupLimit` = how many items in
  a row may leave ahead of the oldest waiting one (the count restarts each time the oldest leaves); **0 (the default) =
  no limit** (the oldest may wait long: `stay_max`). Only items the station is set up for (its slot's last
  `setup.label` value) overtake; before its first setup the queue releases FIFO; it scans its own items (`offerScan`,
  0 = all).
- Statistics: `Station.setups`, `Station.setup_time`, `Station.setup_time_avg`; `utilization` does not include setup
  time (the state `SETUP` has its own share: `Station.state:SETUP`). `setups` counts every setup done, the first of
  each slot included (`setupFirst`, on by default — even when its matrix time is 0); `setupFirst = false` leaves it
  out.
- Study factors: `Station.setup.when=None|LabelChange`, `Station.setup.time.constantValue=…`; another matrix file:
  `data:Station setup matrix.file`.

## Arrivals from a schedule

`simulean_load_table object=Src file=arrivals.csv` puts a Source on a schedule (`mode = Schedule`): columns `Time`,
`Name`, `Q` (quantity) and any label columns (they override `createLabels` of the same name). The names `Time`, `Name`
and `Q` are case-sensitive (`time` is a label column: the answer's `notes` say so); `read` sums up how the rows are
taken (arrivals, the first and last time, the labels). A study varies the file with `Src.table` or a query with
`Src.tableSource`.

## A database and fitting

- `simulean_query import=file.csv` loads a file into the project database; `simulean_query sql=…` reads it.
- `simulean_fit values=… into=Srv.delayStrategy` ranks distributions for the data and writes the best into the field.

## Data tables read by expressions

A Data Table tool (`simulean_load_table` creates one) is read by expressions (`Assets/Docs/Expressions.md`: the table
functions) and varied as `data:Name.file`.
