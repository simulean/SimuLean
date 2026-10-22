# Results and data

What each statistic means, how to name it in a study, and where SimuLean keeps data and results. ⚠ marks what often
surprises.

## What the statistics mean

**Flow** (from the item movements between elements):

| Statistic | Meaning |
|---|---|
| `input`, `output` | Items that entered / left the element in the observed period (after the warm-up) |
| `content`, `content_avg`, `content_max` | Items inside now / time-weighted average / maximum |
| `stay_avg`, `stay_std`, `stay_min`, `stay_max`, `stay_count` | Time from entry to exit, recorded **when the item leaves** (items still inside are not counted); it includes waiting, blocked and stopped time; an item that passes straight through counts 0 |
| `last_entry`, `last_exit` | Times of the last entry / exit |

⚠ A Sink's `content` is the cumulative count it received; a Source has no content and no stays. A Combiner counts its
main items only.

**States and utilisation**

| Statistic | Meaning |
|---|---|
| `state:NAME` | Share of the observed time in that state (`IDLE`, `PROCESSING`, `BLOCKED`, `WAITING_FOR_RESOURCE`, `BREAKDOWN`, `OFF_SHIFT`…) |
| `utilization` | Share of the observed time in PROCESSING. Stopped time counts as not processing (it is in the total, not in the busy part). Multi-Server, Combiner and Erection Dock: the mean over their slots |
| `utilization_onshift` | Utilisation over the time on shift (when the element has a shift) |
| `stops` | Stops started in the observed period |

⚠ A Queue is PROCESSING whenever it holds items, so its utilisation means "not empty". A resource's utilisation counts
travelling, loading, held and waiting time as busy.

**Resources, lists, downtimes, reactions**

| Scope | Statistics |
|---|---|
| Team (`team:T.…`) | `members`, `utilization` (mean of the members), `served`, `tasks`, `request_wait_avg`, `request_wait_std`, `requests_waiting_avg` |
| List (`list:L.…`) | `pushed`, `pulled`, `content`, `content_avg`, `content_max`, `wait_avg`, `wait_std`, `pending_pulls_avg`, `field:F:push_avg|pull_avg|pull_max` |
| Downtime | `stops`, `downtime_total` (stops that ended in the run) |
| Reaction (`reaction:R.…`) | `fired`, `not_met` |
| Model (`model.…`) | `end_time`, `makespan` (the last entry into an element without outputs, or the last item kept), `deadlocked`, `deadlock_time`, `stalled`, `stall_time`, `empty_time`, `finish_time`, `stopped` |

**Per-item records (tallies).** An element's `tallies` (`name = expression`, evaluated as each item **enters** it —
e.g. `tis = age` at the sink, `late = age > 48`) give `tally:name:mean|std|min|max|p50|p90|count`; with `tallySplit`
(a label) also per value, `tally:name[value]:…` — in a study, `Exit.tally:tis[*]:mean` gives one metric per value.

**In a study** a metric is a path: `model.makespan`, `Element.statistic`, `team:Team.statistic`, `list:List.statistic`,
`reaction:Name.statistic`, `Element.tally:name:p90` (see [docs/agents/workflow.md](../agents/workflow.md)); its result
is `{mean, halfWidth, n}` over the replications, the half-width of the confidence interval (95 % by default). `derived`
metrics compute values from them per replication.

## Where things are kept

| What | Where | Notes |
|---|---|---|
| The model | the Unity scene (`.unity`) | A run extracts it into a `SimulationConfig`; `UnityModelExtractor` exports it as JSON |
| Project settings | `ProjectSettings/SimuLeanSettings.json` | Shared through version control: database path, results folder, confidence, export formats, library packs |
| Input data (tables) | the project database `SimuLeanData/simulean.db` (SQLite) and the scene's data tables | Import a CSV / Excel file with `simulean_query import=…` or the Data tab; a data table reads the database, a file or inline rows, and keeps a snapshot so every run reads the same data |
| The last result set | memory (the Results tab, `simulean_results`) | Every run, study and optimisation publishes its set; it is **not stored** unless you save it |
| Stored result sets | the project database (`rs_*` tables) | *Save* in the Results tab or `simulean_results action=save`; compare and chart them later |
| Exported results | the results folder (`SimuLeanResults/`, git-ignored) | Excel (readable sheets), CSV (one file per table), JSON, or SQLite tables, by `simulean_results action=export` or the export formats of the Experimenter / Optimizer |
| Pictures and videos | `Recordings/` (git-ignored) | `simulean_capture` → `Recordings/Captures`, charts → `Recordings/Charts`, recorder takes → MP4 |

A result set holds: the scenarios and their parameter values, every replication (seed, metrics, every element's,
resource's and list's statistics, optionally time series and the state timeline), the metadata (horizon, warm-up,
replications, seeds, algorithm…).

## Reproducing a result

The same model, the same seed and the same settings give the same numbers (each element draws from its own stream,
keyed by its name). A run answer and a result set record the effective settings — time unit, horizon, warm-up, end
condition, seeds, confidence — so they are enough to repeat it. Renaming an element changes its draws; moving it in the
Hierarchy may change the order of simultaneous events.
