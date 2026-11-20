# SimuLean for AI agents

SimuLean is a discrete-event simulator (DES) built to be modelled by AI agents: a C# engine with no Unity dependency,
its Unity integration (3D scenes, animation, an editor library) and a set of **domain commands** (`simulean_*`) that
an agent calls through the Unity editor's MCP server to build a model, check it, run it, study and optimise it, and
read its results — without writing C#.

This file is for an agent that **builds and studies models**. An agent that changes SimuLean's own code reads
[`CLAUDE.md`](CLAUDE.md) (the development rules) and [`docs/architecture.md`](docs/architecture.md) instead.

## Set-up

1. Unity **6.6** (6000.6.4f1) with this project open.
2. The Unity editor's MCP server, from Unity's command-line tool, in the agent's MCP configuration:

   ```json
   {
     "mcpServers": {
       "unity-editor-mcp": { "command": "unity", "args": ["mcp", "--project-path", "<path to this project>"] }
     }
   }
   ```

   One server per project; do not drive the same editor from two servers. `unity mcp configure <client>` can write it;
   what else to install (the .NET 10 SDK for the faster runner) is in the README's *Installation*.
3. Check the connection: `simulean_types` lists what can be placed.

## Read next

| | |
|---|---|
| [`docs/agents/workflow.md`](docs/agents/workflow.md) | **Read first** (~10 KB): the loop (build → validate → run → diagnose → study), the argument vocabulary, the build format, the run settings and time unit, the metrics grammar, the traps that cost retries — and the index of the topic modules |
| [`docs/agents/topics/`](docs/agents/topics/resources.md) | **Read only the ones the task needs**: [resources](docs/agents/topics/resources.md) (teams, priorities, preemption, downtimes, repair crews), [warehouse](docs/agents/topics/warehouse.md) (racks, stock lists, pick faces, replenishment, milk runs, line side), [logic](docs/agents/topics/logic.md) (routing, admission, release order, reactions, C# rules, the escalation ladder), [setup-and-data](docs/agents/topics/setup-and-data.md) (setup matrices, schedules, data) — each gives the exact keys in recipe form |
| [`docs/agents/commands.md`](docs/agents/commands.md) | Every command with its arguments (generated from the code): look one up, do not read it whole |
| [`docs/agents/errors.md`](docs/agents/errors.md) | What the errors and warnings in the answers mean and how to fix them |
| [`docs/guide/`](docs/guide/README.md) | What each element does exactly, how a model runs, what the statistics mean, recipes — read [elements.md](docs/guide/elements.md) before modelling a system with another tool's habits |
| [`docs/architecture.md`](docs/architecture.md) | How the engine works (elements, links, resources, statistics) — when a behaviour needs explaining |

## Rules

1. **Model through the `simulean_*` commands.** Do not write C# through `eval` to build, run or inspect a model: every
   need has a command (bulk edits: `simulean_apply`, `simulean_repeat`; component actions: `simulean_invoke`;
   stalls: `simulean_diagnose`; results: `simulean_results`). Do not change the simulator's code.
2. **Read before you guess.** `simulean_types type=…` gives a type's fields and catalog keys, `simulean_params` an
   object's, `simulean_model` the whole model (with its card, `info`: what it represents, its KPIs, the parameters worth
   varying, the assumptions; `simulean_card` reads and writes it). Work in a model of your own (`simulean_models`): an
   example scene is copied, never edited. Every answer carries `errors` and `warnings`: read them.
3. **Check before you run.** `simulean_validate` builds the model (with the study's factors and metrics) without running
   it; the run commands check the study too and refuse one that cannot run.
4. **Never sleep to wait.** Start long jobs with `_start` and call `_status wait=50`: it answers as soon as the job ends.
5. **One call per study.** Factors (`factors`), explicit `scenarios`, a `where` filter, `derived` metrics and replications
   go in one `simulean_run_headless` / `simulean_run_start`; do not loop over scenarios. An answer too large for the
   tool (~40 KB) says so and how to read the rest: a large study comes as a table with `page.next` (`simulean_results
   action=read top= offset= sort=`, no second run), `simulean_vocabulary` as an index of its sections (`section=`).
6. **Runs never change the scene.** Edits are undoable (`simulean_remove`, `simulean_disconnect`, `simulean_rename`,
   `simulean_move` undo a mistake). Save the model you work in with `simulean_models action=save` (it refuses any other scene); never
   save or discard a scene you did not create.
7. **Runs are reproducible.** The same seed gives the same replication (each element draws from its own random
   stream); replications use the seeds `seed`, `seed + 1`, …
8. **Decide at the lowest rung.** A decision is a field, else an expression, else a query / order, else a reaction; a
   C# choice rule is the last rung and carries a `Why` naming the declarative form tried. The ladder, with when to move up
   and an example per rung: [`docs/agents/topics/logic.md` — *The escalation ladder*](docs/agents/topics/logic.md#the-escalation-ladder).
