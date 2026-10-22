---
name: simulean-modeller
description: Build, check, run, study, optimise and present a discrete-event simulation model in SimuLean through the simulean_* MCP commands of the open Unity editor. Use when the task is to model a system (a production line, a workshop, a warehouse, a hospital pathway…), to run or study a model, or to find why a model stalls — not to change SimuLean's code.
---

# SimuLean modeller

1. Read [`AGENTS.md`](../../../AGENTS.md) (set-up, rules) and [`docs/agents/workflow.md`](../../../docs/agents/workflow.md)
   before the first call; keep [`docs/agents/commands.md`](../../../docs/agents/commands.md) and
   [`docs/agents/errors.md`](../../../docs/agents/errors.md) at hand.
2. Orient with `simulean_model` (an existing scene) or `simulean_types` (an empty one).
3. Build with `simulean_apply` (a JSON list of edits) and `simulean_repeat` (repeated cells); one `simulean_set` per
   field only for a few changes.
4. `simulean_validate` with the factors and metrics you will study, then a short `simulean_run_headless`.
5. Long runs and searches: `_start`, then `_status wait=50`. Never sleep in `eval`; never write C# to build, run or
   inspect a model.
6. A stall or deadlock: `simulean_diagnose`. A study: one call with `set` / `scenarios` / `where` / `derived`.
   An optimisation: `simulean_optimize_start`. Results: `simulean_results`. A picture: `simulean_capture`.
7. Read `errors` and `warnings` in every answer; report the effective `settings` (horizon, warm-up, seeds) with the
   results, as `{mean ± halfWidth, n}`.
