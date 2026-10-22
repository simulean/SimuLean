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

   One server per project; do not drive the same editor from two servers.
3. Check the connection: `simulean_types` lists what can be placed.

## Read next

| | |
|---|---|
| [`docs/agents/workflow.md`](docs/agents/workflow.md) | How a model is built, checked, run, studied and presented — the order of the calls and the patterns that keep a session short |
| [`docs/agents/commands.md`](docs/agents/commands.md) | Every command with its arguments (generated from the code) |
| [`docs/agents/errors.md`](docs/agents/errors.md) | What the errors and warnings in the answers mean and how to fix them |
| [`docs/guide/`](docs/guide/README.md) | What each element does exactly, how a model runs, what the statistics mean, recipes — read [elements.md](docs/guide/elements.md) before modelling a system with another tool's habits |
| [`docs/architecture.md`](docs/architecture.md) | How the engine works (elements, links, resources, statistics) — when a behaviour needs explaining |

## Rules

1. **Model through the `simulean_*` commands.** Do not write C# through `eval` to build, run or inspect a model: every
   need has a command (bulk edits: `simulean_apply`, `simulean_repeat`; component actions: `simulean_invoke`;
   stalls: `simulean_diagnose`; results: `simulean_results`). Do not change the simulator's code.
2. **Read before you guess.** `simulean_types type=…` gives a type's fields and catalog keys, `simulean_params` an
   object's, `simulean_model` the whole model. Every answer carries `errors` and `warnings`: read them.
3. **Check before you run.** `simulean_validate` builds the model (with the study's factors and metrics) without running
   it; the run commands check the study too and refuse one that cannot run.
4. **Never sleep to wait.** Start long jobs with `_start` and call `_status wait=50`: it answers as soon as the job ends.
5. **One call per study.** Factors (`set`), explicit `scenarios`, a `where` filter, `derived` metrics and replications
   go in one `simulean_run_headless` / `simulean_run_start`; do not loop over scenarios.
6. **Runs never change the scene.** Edits are undoable (`simulean_remove`, `simulean_disconnect`, `simulean_rename`
   undo a mistake). Save the scenes you create; never save or discard a scene you did not create.
7. **Runs are reproducible.** The same seed gives the same replication (each element draws from its own random
   stream); replications use the seeds `seed`, `seed + 1`, …
