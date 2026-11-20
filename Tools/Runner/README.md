# `simulean` — SimuLean studies out of the editor

The engine (`Assets/SimuLean.Net`, compiled from source), the headless factory and the study runner as a command-line
program: no Unity. Architecture: §3.5 (`ConfigJson`, `StudySpec`, `EngineFingerprint`).

```
dotnet build Tools/Runner/simulean.csproj -c Release
dotnet Tools/Runner/bin/Release/net10.0/simulean.dll version
dotnet Tools/Runner/bin/Release/net10.0/simulean.dll run study.json --out results.json
```

| Command | Answer (JSON on the standard output, or `--out FILE`) |
|---|---|
| `version` | `{"format", "kind": "version", "engine", "runtime"}` |
| `run FILE [--out RESULTS] [--threads N] [--quiet]` | `{"format", "kind": "results", "engine", "settings", "scenarios", "errors", "warnings", "seconds"}` — the answer of the editor's `simulean_run_headless` (`StudySpec.Answer`); progress `runs d/t` on the standard error |
| `validate FILE` | `{"format", "kind": "validation", "engine", "ok", "elements", "scenarios", "metrics", "errors", "warnings"}` |

**Batch** — a batch file (`{"kind": "batch"}`, `StudyBatch`: the editor's studies with their scenarios applied) given to
`run` answers the whole result set: `{"format", "kind": "resultset", "engine", "resultSet", "errors", "warnings", "seconds"}`
(`ResultSetJson`).

`--stream` (what the editor uses): each run on its own line as soon as it ends (`{"kind": "run", "index", "run"}`), the
set without its runs last; runs are compact (`ResultSetJson`: statistic names once per run, numbers as base64 doubles).

**Session** (`simulean serve`, what the editor uses): answers `{"kind": "ready", "engine", …}`, then reads one JSON line
per request on its standard input — a batch envelope with an `id` (answered as with `--stream`, the `id` on every line),
`{"kind": "cancel", "id"}` (the runs done come back), `{"kind": "exit"}` (or the end of the input). Batches run one
after another; a failed one answers `{"kind": "error", "id", "message"}` and the session goes on.

**Input** — a study file `{"format": 1, "kind": "study", "model": {…}, "study": {…}}` (`StudySpec.Serialize`: the model as
`ConfigJson` writes it, the study's fields as `simulean_run_headless` takes them: `Horizon` (−1 = the model's), `Replications`, `Seed`,
`Factors`, `Metrics`, `Scenarios`, `Where`, `Derived`, `Confidence`, `Warmup`, `NoProgress`, `Detail`, `Threads`, `CompareTo`), or a model file alone
(one run with the model's settings). A `format` newer than the runner's is refused.

**Engine fingerprint** — `engine` in every answer: a hash of the engine sources the runner was built from
(`Fingerprint.targets` at build time, `EngineFingerprint.OfSources` in the engine; `runnertests` checks they agree). The
editor compares it with its own and never uses a runner built from other sources.

**Errors** — `{"format", "kind": "error", "engine", "message"}`; exit code 2 for bad input (command, file, format,
study), 1 for any other failure. What the engine logs while it runs goes to the standard error and into the answer.

Models that need Unity's physics (a team moved by physics without a travel calibration) cannot run here: they run only
in the scene, and the extraction's message says how to calibrate the travel. **Self-contained builds**: `powershell -File Tools/Runner/publish.ps1` (with `-Version <x.y.z>` it also writes the release's archives `dist/SimuLeanRunner-<x.y.z>-<rid>.zip`, which the editor downloads when its engine is the release's — `RunnerRelease`) → `Tools/Runner/dist/<rid>/simulean[.exe]`
(win-x64, osx-arm64, linux-x64; the .NET 10 runtime bundled, one compressed file of ≈ 37–40 MB, start ≈ 0.1 s; not
trimmed nor AOT, as the codec and the engine build by reflection; ready-to-run, so less is compiled at start). The runner
uses the server garbage collector (`simulean.csproj`): a run allocates ≈ 140 MB and the workstation collector made
parallel runs stop each other.
