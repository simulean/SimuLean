---
name: simulean-modeller
description: Build, check, run, study, optimise and present a discrete-event simulation model in SimuLean through the simulean_* MCP commands of the open Unity editor. Use when the task is to model a system (a production line, a workshop, a warehouse, a hospital pathway…), to run or study a model, or to find why a model stalls — not to change SimuLean's code.
---

# SimuLean modeller

1. Read [`AGENTS.md`](../../../AGENTS.md) (set-up, rules) and the core [`docs/agents/workflow.md`](../../../docs/agents/workflow.md)
   before the first call, then only the topic modules its index names for the task (`docs/agents/topics/`: resources,
   warehouse, logic, setup-and-data). Look commands up in [`docs/agents/commands.md`](../../../docs/agents/commands.md)
   and errors in [`docs/agents/errors.md`](../../../docs/agents/errors.md); do not read them whole.
   If the MCP tools load on demand, load the ones the task needs in one call: `simulean_models`, `simulean_types`,
   `simulean_apply`, `simulean_set`, `simulean_validate`, `simulean_run_headless`, `simulean_diagnose`,
   `simulean_capture`, `simulean_results` and `eval` (for `OpenNeutral`), plus any the brief names.
2. Orient with `simulean_model` (an existing scene; `section=logic` for what decides each routing, admission, order, dispatch and reaction) or `simulean_types` (an empty one).
3. Build with `simulean_apply` (a JSON list of edits) and `simulean_repeat` (repeated cells); one `simulean_set` per
   field only for a few changes. Express each decision at the lowest rung of
   [the escalation ladder](../../../docs/agents/topics/logic.md#the-escalation-ladder) (field → expression → query / order →
   reaction → choice rule); a rule only with a `Why` naming the declarative form tried.
4. `simulean_validate` with the factors and metrics you will study, then a short `simulean_run_headless`.
5. Long runs and searches: `_start`, then `_status wait=50`. Never sleep in `eval`; never write C# to build, run or
   inspect a model.
6. A stall or deadlock: `simulean_diagnose`. The names the commands take: `simulean_vocabulary` (an index; `section=` one part). A study: one call with `factors` / `scenarios` / `where` / `derived`.
   An optimisation: `simulean_optimize_start`. Results: `simulean_results`. A picture: `simulean_capture`.
7. Read `errors` and `warnings` in every answer; report the effective `settings` (horizon, warm-up, seeds) with the
   results, as `{mean ± halfWidth, n}`.
8. Save with `simulean_models action=save` (the open model you created or opened through `simulean_models`); never the
   editor's generic scene save or `eval`, never a scene you did not create.
