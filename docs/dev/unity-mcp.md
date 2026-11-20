# Working through the Unity MCP (development)

Detail behind the short rules of [`CLAUDE.md`](../../CLAUDE.md) for an agent that changes SimuLean and drives the open
editor. An agent that **builds models** reads [`AGENTS.md`](../../AGENTS.md) and [`docs/agents/`](../agents/workflow.md).

## The commands

- **Modelling guide for agents: [`AGENTS.md`](../../AGENTS.md) and [`docs/agents/`](../agents/)** — the workflow, the
  command reference (generated: after adding or changing a command run `python Tools/Docs/command_reference.py`;
  `run-all.ps1` fails when it is stale) and the error catalogue. Keep them in step with the commands.
- **One Unity MCP server per project** — the project's `.mcp.json` (`unity mcp`, the `com.unity.pipeline` server); don't
  drive the same editor from two servers.
- **Build and study models with the SimuLean commands, not `eval`** (`Editor/Pipeline/SimuLeanCommands.cs`; one argument
  vocabulary — `object` addresses an object, `factors` / `horizon` / `resultSet`, `-1` = the model's value;
  `simulean_vocabulary` lists it, and a new argument needs a concept in `Optimization/Vocabulary.cs`):
  - what can be placed: `simulean_types` (the Core pack and a line per other pack, `pack=` / `pack=all`; with `type`, its
    fields and catalog keys), `simulean_params`;
  - models: `simulean_models` (`action=save` saves the model these commands made or opened), `simulean_model` (the
    whole model in one call, parameters under the keys a study varies), `simulean_validate` (builds without running);
  - building: `simulean_add_element` (also the Visualization / Analysis tools), `simulean_connect` (between two transport
    nodes: a track), `simulean_set` (a field by name / path; objects by GameObject name or asset path `Assets/…`; a list
    item's field as `actions[0].kind`), `simulean_add_team`, `simulean_load_table`, `simulean_place_model` (a library 3D
    model as decoration); in bulk `simulean_apply` (a JSON list of edits, all or nothing) and `simulean_repeat` (a group
    copied N times); a component's action `simulean_invoke` (mark a new one `[SimuLeanAction]`) — never loops or
    reflection in `eval`;
  - undoing: `simulean_disconnect`, `simulean_reorder` (Next Elements' order), `simulean_remove` (references to the object
    cleared and listed), `simulean_rename` — not the generic editor tools;
  - running: `simulean_run_headless` (per scenario the `metrics` asked and the model block — pass `metrics` to keep the
    answer small, `detail=full` adds every element's KPIs; `factors` = catalog overrides `Owner.key=v1|v2;…`, the `Study`
    path grammar of architecture §3.5). A study that may last more than ~60 s: `simulean_run_start` (same arguments)
    then `simulean_run_status wait=50` — a long synchronous run times the MCP out and blocks every following call; every
    `_status` takes `wait` (never sleep in `eval`). Optimise with `simulean_optimize_start` / `simulean_optimize_status`;
    find stalls and deadlocks with `simulean_diagnose`;
  - seeing and results: `simulean_capture` (a framed PNG) instead of placing cameras; `simulean_record_shot` /
    `simulean_record_start` / `simulean_record_status` (a demo video ending as an MP4 — never place cameras or encode
    frames by hand); `simulean_results` (list / read / save / export / chart); data: `simulean_query`, `simulean_fit`.
- Every answer carries `errors` and `warnings` (what was logged while the command ran) — read them; a study is checked
  before it runs. Elements are named by GameObject name (unique: a duplicate name is refused) or by full hierarchy path.
  In code, `HeadlessModelFactory.Find(name)` finds an element by display name.

## EditMode tests in the open editor

`simulean_test` (filter) then `simulean_test_status` until `completed`, or `eval`
`return SimuLean.Unity.Editor.SimuLeanTestRunner.Start("<filter>");` / `.Status()`. **Never the MCP's `run_tests`.** The
first call after a recompile may return a network error: retry.

Why: if an open scene has unsaved changes, Unity's Test Runner shows the modal "Scene(s) Have Been Modified" dialog. That
dialog stalls the editor's main thread, and so every MCP command, until a human answers it; a cancelled run also leaves
the MCP's synchronous `run_tests` waiting forever.
- `SimuLeanTestRunner.Start` refuses to start while a scene is modified and lists the scenes; nothing blocks.
- A run that includes `ExternalRunnerTests` (every full run) first builds the external runner of the engine version when
  it is missing (after an engine change, ~40 s): the status is `running` with `"phase": "building runner"`, and
  `simulean_test_status wait=50` keeps waiting through it. A runner that cannot be built is a failure of the run.
- Poll `Status()` until `"status":"completed"`. The results are also in `Temp/simulean_test_results.json` (the last full
  run also in `…_full.json`, which the gate reads). A `blocked: compiling …` answer means `Start` found edited scripts:
  call it again once the editor is idle.
- If it says `blocked`: save the scenes you created; never save or discard a user's scene without asking.
- Keep your own scenes saved after every edit.
- Editor tooling must not dirty a scene just by opening it (e.g. the inspector panel's element auto-collection is not
  marked dirty).

## Captures, handles, a blocked editor

- **Captures:** prefer `simulean_capture` (a framed PNG, any path); `capture_game_view save_path=Temp/…` writes into
  `Assets/Temp` (upstream tool, not SimuLean: give it an absolute path outside `Assets`, `docs/agents/errors.md` §5). Use a
  scene camera (`capture_game_view camera=…`); the Scene View image is stale while the editor is unfocused.
- **Hierarchy handles** of the generic tools need the full path (elements sit under organiser groups).
- **A blocked editor / the MCP stops answering:** read the port and token from `Library/Pipeline/.unity-pipeline-port`,
  check `GET http://127.0.0.1:<port>/api/dialog` (Bearer token) for a modal dialog, and request a domain reload /
  recompile (`recompile`). A stuck queue clears with a domain reload.
- **Known upstream issue:** the `batch` tool's `params` (client schema: string; server: object) — use parallel single
  calls instead.
- Leave no modified scene open; save only the scenes you create.
