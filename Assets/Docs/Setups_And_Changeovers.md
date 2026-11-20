# Setups and changeovers

Guide to a station's setup before its process. Design and internals: `docs/architecture.md` §3.2
*Setups*; example scene `Assets/Scenes/LibraryExamples/Setup_Changeover.unity` (with its model card).

## 1. Where

The **Setup** section of the Multi-Server, the Combiner, the Picking Station, the Assembly Station, the Stacker and the
Erection Dock (field `setup`, keys `setup.*`). What one setup is:

| Station | A setup happens… | The previous value is… |
|---|---|---|
| Multi-Server | before an item's process | its server's (per slot) |
| Combiner, Picking Station | once the set / pick is gathered, before the process | the assembly's |
| Assembly Station | per job (an order), before its first task | the station's |
| Stacker | before an item's pick-and-place cycle (gripper / pattern change); the item waits at the pick point | the stacker's |
| Erection Dock | before a block's erection (staging, jigs) | its front's |

## 2. When, how long, by whom

- **When** (`setup.when`): `None` (default, no setup), `EachItem`, or `LabelChange` — when the item's label
  (`setup.label`, e.g. `type`) differs from the previous value; `setup.setupFirst` (default on): the first item pays one.
- **How long** (`setup.time`): any distribution, as the process time, on its own random stream.
- **Setup matrix** (`setup.matrix`): a **Data Table** with the columns `from`, `to`, `time` gives a time per pair of the
  label's values; a `time` is a number or a distribution `Type~p1~p2` (`Uniform~1~3`, `Triangular~2~3~5`). `*` is any
  value; an empty `from` is the first setup. The most specific row wins — (from, to), (from, *), (*, to), (*, *) —, a pair
  the table does not list takes `setup.time`.

  ```
  from,to,time
  ,*,3
  A,B,1
  *,C,Triangular~3~4~6
  C,*,Uniform~2~3
  ```

  Load it from a file with `simulean_load_table object=Press file=changeovers.csv` (a table `Press setup matrix`), or
  link any Data Table in the Setup section. A study varies its file as `data:Press setup matrix.file`.
- **By whom** (`setup.setupBy`): empty = the machine alone; a team = a crew of its own (its members travel to the station
  and show SETUP); **Keep For Process** with the process team = the process operators set up and stay for the process
  (one request; not on the Assembly Station or the Stacker, which ask their resources per task / cycle).

A stop pauses a setup; the station shows **SETUP** (WAITING_FOR_RESOURCE while its crew comes). Results: `setups`,
`setup_time`, `setup_time_avg`, `state:SETUP`.

## 3. Grouping items to avoid setups

A **Queue** in front of a station that changes over on a label change can offer first the items the station is already
set up for: **Group by setup** (`groupBySetup`). Items of one type then follow each other. **Most in a row**
(`groupLimit`, 0 = no limit) is the most items that may leave ahead of the oldest one; then the oldest goes, so no item
waits for ever. *Items looked at* (`offerScan`, 0 = all) bounds the search. Grouping works behind an ordinary
connection; a station pulling from a list orders by its pull query instead (e.g. the label first).

In the example scene (40 h, seeds 1..3) grouping gives 1186 items out with 399 changeovers and 6 min waiting, against
883 out, 625 changeovers and 207 min without it. `Assets/Scenes/LibraryExamples/Workshop_Advanced.unity` puts a grouping
buffer and a lathe with a setup matrix and its setter into a larger workshop (40 h, seeds 1..5: 969 out with 295
changeovers against 636 out with 457 without grouping).
