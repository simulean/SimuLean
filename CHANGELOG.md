# Changelog

SimuLean follows [semantic versioning](https://semver.org/) for its features; while the project is young, a minor
version may still change model keys or scene formats — each such change is listed under **Breaking changes**, with what
to do. Seeded results may change between minor versions when a random stream draws differently.

1.1.0 and 1.2.0 were development milestones, not released separately: 1.3.0 is the first release after 1.0.0 and
contains all three sections below.

## 1.3.0 — 2026-11-20

The agent interface after evaluation rounds with AI agents building models from written briefs, and a cleaner code base.

**Agent interface**
- Compact answers with pages for long studies; vocabulary sections in `simulean_vocabulary`; field tooltips in
  `simulean_types` / `simulean_params` on request; the model's own warnings in run and study answers.
- New commands and operations: `simulean_move` (and the `move` operation of `simulean_apply`), `simulean_results
  action=delete` for what the commands wrote; `simulean_set value=""` clears a field.
- Studies: paired scenario comparison (`compareTo`), min / max over replications, derived metrics that read the
  factors, a downtime's enabled switch and declared text settings as study factors; an incomplete scenario's error
  gives the model's values; `simulean_validate` takes horizon, replications and seed.
- `simulean_model` names the teams, shows a timetable's rows, every downtime in the logic, and each team's and
  downtime's metric paths; `simulean_capture` frames the model with its nodes and teams and draws the connections.
- Clearer errors and notes: errors name the team path or the Multi-Server, notes on metrics, factor levels and full
  buffers at a deadlock, circular waits, default capacities, distributions echoed as set.

**Engine**
- Sources: a rate per period (non-stationary Poisson arrivals from a rate table).
- Statistics: a Sink's time in system (`tis_*`), a Queue's `stay_p90`, user properties as metrics (`property:NAME`),
  `downtime_total` (time stopped) next to `downtime_scheduled`.
- Downtime: an *After Current* stop waits for the work in hand instead of cutting it; a new downtime takes its kind's
  state and mode (a shift: `OFF_SHIFT`, *After Current*).
- Resources: requirement-only resource uses; a use's team is its switch and its default team.
- Fixes: carriers waiting at a puller deliver once each when it resumes; a downtime's pooled repair wait is weighted by
  the crews that arrived; logic priorities, skipped outputs and the BLOCKED reason reported right.

**Unity**
- Team placement and overlaps, the crane team's edit-mode preview as in the run, every resource use drawn in captures.

**Project**
- Opening the project needs no git: the unused *MCP for Unity* package (a git dependency) was removed; every
  package comes from Unity's registry.

**Code and tests**
- The engine's model build in named passes, namespace cycles removed, duplicated helpers merged; the Unity commands edit
  through one edit session; extraction rules testable without the editor.
- The standalone test suites on GitHub Actions (Windows, .NET 10); a lint for comments that should say why, not when.
- Documentation for agents as a core guide plus topic modules (resources, warehouse, logic, setup and data); the paper
  linked in the README and `CITATION.cff`.

## 1.2.0 — 2026-11-09 (milestone, in 1.3.0)

Resources, setups and operating logic.

**Resources**
- Preemption on everything that uses resources (modes None, Resume, Restart, Abort); a resource's breakdown hands its
  work over.
- Repair crews: a downtime repaired by members of a team (`WAITING_FOR_REPAIR`); a team as a downtime's target.
- Resources from several teams, all at once or in order; reserving room downstream before a job; securing the process
  resources before the part enters (Multi-Server).
- Max-wait timers on resource requests, queue items and list pulls (Give Up, Reroute, the *Wait Timed Out* signal).
- Team `state:X` shares in results, series and study metrics; stop a run when there is no progress.

**Setups**
- Setup / changeover times on the Multi-Server, the Combiner and the Picking Station; a setup matrix; grouping by setup.

**Operating logic**
- Typed settings for every element (one settings class per type), the parameter catalog built from them.
- Best-score routing, a Queue's offer order, expressions in list queries and orders, a team's named request orders,
  the admission wake-up and the *Retry* action, the Token Pool preset, the *Set Item Look* action.
- The logic viewer (*Tools ▸ SimuLean ▸ Model Logic*, `simulean_model section=logic`) and choice rules written in C#
  (`simulean_rule_*`), with rule warnings in `simulean_validate`.
- One expression language everywhere: contexts, `origin.` / `destination.`, `stat` / `team` / `list` / `count`,
  `has(x)` and `??`, `ln`, `exp`, `clamp`, `triangular`, `lognormal`; expressions checked as they are entered and at
  build (unknown names, NaN, text arithmetic).
- A stopped source holds, pauses or loses its arrivals.

**Examples**
- `Workshop_Resources`, `Setup_Changeover`, `Workshop_Advanced`, `ChoiceLogicDemo`; a model card and a result check for
  every example. `CombinerScene` and the old resource example scenes were removed.

**Breaking changes**
- Catalog and config keys are the settings' field paths (e.g. `processBy.priority`, `tasks[i].duration`); item labels
  are `name = expression` records. Scenes made with an earlier version are migrated when the project is updated; keys in your own
  scripts or study files must be renamed.
- Public names in US spelling (states `TRAVELING`, …) and clearer names: `offerOrder` / `offerScan`, `gated`,
  `arrivalsOnStop`, `jobOnMemberStop`.
- Exchange format 2 (typed settings): a model or study file written by an earlier version must be exported again.

## 1.1.0 — 2026-11-04 (milestone, in 1.3.0)

Studies outside the editor, the Warehousing pack and the model frame.

**Studies outside the editor**
- The runner `simulean` (`Tools/Runner`, .NET 10): studies and optimisations from the editor run in a separate process
  with identical results, about 10–25× faster on long runs; self-contained builds for Windows, macOS and Linux.
- The model as JSON without Unity (exact round trip), the study as data, whole result sets as JSON.

**Warehousing and line supply**
- A storage's stock as a model list, named ports and element metrics; pick faces and a base-stock replenisher; milk
  runs (tugger trains, rail shuttles); call-off points, a paced line and a sequencing station; procedural racks, floor
  markings and signs; the `Warehouse_ToLine` example.
- The Separator (the Combiner's inverse; a Line-Side Unpacker in the Manufacturing pack) and the Combiner's assembly
  view: components travel onto the main item, packing on load, Palletizer and Tote Packer presets.
- Storage with physical dimensions, put-away by a transport team, pass-through after a minimum dwell time.

**Modelling environment**
- Modelling in the Scene view (Place tool, the SimuLean bar), a 100 × 100 m floor in new scenes, a click on an
  element's part selects the element.
- The model frame: every element view flows along +X and faces −Z; scenes and prefabs migrated.
- Model management: a models folder, model cards, the Models tab, `simulean_models` / `simulean_card`.
- Library entries folded in families with rendered icons; new 3D models (AS/RS stacker crane, turning rollers,
  inspection reject pusher, unpacking and picking stations, hospital furniture); resource teams visible by default.
- Fixes: a queue is BLOCKED while none of its items can leave, a conveying speed for
  length-limited lanes, a team option to return home after each task, downtime totals during a running stop.

**Breaking changes**
- The Unpacker is replaced by the Separator; the Gate Queue is the Queue's *Release on command*.
- Every random use of an element draws from its own stream: seeded results differ from 1.0.

## 1.0.0 — 2026-10-22

First public release.
