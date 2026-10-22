using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using SimuLean.Unity;
using SimuLean.Serialization;
using SimuLean.Optimization;
using SimuLean.Results;
using SimuLean.Experimentation.Core;
using Debug = UnityEngine.Debug;

namespace SimuLean.Experimentation.Controller
{
    /// <summary>
    /// Scenarios × replications of the scene's model. Parameters and metrics are the same <see cref="ParameterSpec"/> /
    /// <see cref="MetricSpec"/> as the Optimizer's; each scenario gives one value per parameter.
    /// <list type="bullet">
    /// <item><b>Headless</b> (default): the scene is extracted once and every run (scenario, replication) is a
    /// <see cref="SimulationObjective.Run"/> in the background, in Edit or Play mode; replication r uses the seed
    /// base + r − 1 in every scenario (common random numbers).</item>
    /// <item><b>In the scene</b> (Play mode): the <see cref="UnitySimClock"/> runs each scenario once, visibly — the
    /// scenario's values are applied to the extracted config before the build (<see cref="UnitySimClock.ConfigureModel"/>),
    /// the metrics are read from the run's model at its end.</item>
    /// </list>
    /// </summary>
    [AddComponentMenu("SimuLean/Experimenter")]
    [SimuLean.Unity.SimuLeanTool(SimuLean.Unity.SimuLeanToolCategories.Experiments, "Experimenter", Description = "Scenarios x replications, headless or in the scene")]
    public class Experimenter : MonoBehaviour, IExperimenter
    {
        [Tooltip("Root of the model (empty = the whole scene).")]
        [SerializeField] private GameObject modelRoot;

        [Tooltip("The parameters the scenarios set (element keys, team members / speed).")]
        [SerializeField] private List<ParameterSpec> parameters = new List<ParameterSpec>();

        [Tooltip("The values observed after each run.")]
        [SerializeField] private List<MetricSpec> metrics = new List<MetricSpec>();

        [Tooltip("One row per scenario: a value per parameter, in order.")]
        [SerializeField] private List<ExperimentScenario> scenarios = new List<ExperimentScenario>();

        [Tooltip("Simulated time of each run (0 = the Simulation Clock's max time).")]
        [SerializeField] private double runTime = 0;
        [Tooltip("Warm-up (simulated time): statistics restart then, so results cover the steady state. 0 = the clock's.")]
        [SerializeField] private double warmupTime = 0;

        [Tooltip("On: background runs (Edit or Play mode). Off: every scenario × replication runs in the scene, one after another (Play mode).")]
        [SerializeField] private bool headlessMode = true;
        [Tooltip("Runs at once (headless; 0 = one per processor up to 8, 1 = one after another).")]
        [Min(0)] [SerializeField] private int threads = 0;
        [Tooltip("Seed of replication 1 (replication r: seed + r − 1, the same in every scenario). 0 = a new seed each time.")]
        [SerializeField] private int randomSeed = 1;

        [Tooltip("Formats the results are exported to when the experiment completes (the results folder of the project settings; SQLite: the project database). Nothing = no file (the SimuLean window's Results tab still shows them).")]
        [SerializeField] private ResultFormats exportFormats = ResultFormats.None;   // the Results tab shows them; a file on request (review2 #43)
        [Tooltip("Record every statistic of each run (elements, resources, teams, lists…) — the model sheets of the report.")]
        [SerializeField] private bool exportElementStats = true;
        [Tooltip("Interval of the time series recorded in each headless run (content, throughput, states per interval). 0 = none.")]
        [Min(0)] [SerializeField] private double seriesInterval = 0;
        [Tooltip("Keep every state interval of elements and resources (the Gantt charts; G3c). Off by default: the results grow with every state change.")]
        [SerializeField] private bool recordTimeline = false;

        // progress and results: in memory only (the report keeps them) — never saved with the scene
        [NonSerialized] private string status = "Ready";
        [NonSerialized] private int completedRuns;
        [NonSerialized] private int totalRuns;

        private volatile bool running;
        private CancellationTokenSource cancellation;
        private readonly List<ResultRun> results = new List<ResultRun>();
        private string projectRoot;
        private DateTime started;
        private List<(ExperimentScenario scenario, int rep)> sceneRuns;
        private int sceneRun, sceneBaseSeed;
        private SeedMode savedSeedMode;
        private int savedSeed;
        private bool savedRecordTimeline;

        public bool IsRunning => running;
        public string Status => status;
        public List<ParameterSpec> Parameters => parameters;
        public List<MetricSpec> Metrics => metrics;
        public List<ExperimentScenario> Scenarios => scenarios;
        /// <summary>The runs of the last experiment (scenario, replication, seed, metrics, statistics).</summary>
        public IReadOnlyList<ResultRun> Results => results;
        /// <summary>Formats exported at the end (<see cref="ResultFormats.None"/> = no export).</summary>
        public ResultFormats ExportFormats { get => exportFormats; set => exportFormats = value; }

        /// <summary>Checks the set-up (builds the parameters and every scenario's values without running); null when valid,
        /// else the problem.</summary>
        public string Validate()
        {
            try { return BuildStudy(SceneExtractor.Extract(modelRoot).Config).Validate(); }
            catch (Exception e) { return e.Message; }
        }

        /// <summary>The experiment as a <see cref="Study"/> over its model (the scene extracted now, under the model root):
        /// what the MCP's runs use with <c>experimenter</c>.</summary>
        public Study PrepareStudy() => BuildStudy(SceneExtractor.Extract(modelRoot).Config);

        /// <summary>
        /// The experiment as a <see cref="Study"/> over <paramref name="config"/>: this component's parameters
        /// (the range of a number = the scenarios' values), scenarios with their replications, metrics and settings. The
        /// headless runs go through it; scripts and the MCP can build one the same way (or add factors to it).
        /// </summary>
        public Study BuildStudy(SimulationConfig config)
        {
            if (scenarios.Count == 0 && parameters.Count > 0) throw new InvalidOperationException("Add at least one scenario.");
            foreach (var s in scenarios)
                if (s.parameterValues.Count != parameters.Count)
                    throw new InvalidOperationException($"Scenario '{s.scenarioName}': {s.parameterValues.Count} values for {parameters.Count} parameters.");
            if (warmupTime > 0) config.WarmupTime = warmupTime;
            var study = new Study(config)
            {
                Name = $"Experiment_{gameObject.name}_{started:yyyyMMdd_HHmmss}",
                RunTime = runTime > 0 ? runTime : config.MaxSimulationTime,
                BaseSeed = randomSeed != 0 ? randomSeed : Environment.TickCount,
                SeriesInterval = seriesInterval, RecordTimeline = recordTimeline, Threads = threads, RecordStatistics = exportElementStats,
                TableLoader = path => TableFiles.Read(path),   // Excel too (Unity reader)
            };
            for (int i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i].ToModelParameter(config);
                var kind = p.Variable.Kind;
                if (kind == VariableKind.Integer || kind == VariableKind.Real)
                {
                    var (lo, hi) = ScenarioRange(i);
                    p = parameters[i].ToModelParameter(config, lo, hi);
                }
                study.Factor(p);
            }
            foreach (var s in Runs) study.Scenario(s.scenarioName, Math.Max(1, s.replications), s.parameterValues.ToArray());
            study.Metrics.AddRange(metrics.Select(m => m.ToModelMetric()));
            return study;
        }

        /// <summary>Lowest and highest number among the scenarios' values of parameter <paramref name="i"/> (every value of
        /// a vector).</summary>
        private (double lo, double hi) ScenarioRange(int i)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var s in scenarios)
                foreach (string part in (s.parameterValues[i] ?? "").Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!double.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v))
                        throw new InvalidOperationException($"Scenario '{s.scenarioName}', parameter '{parameters[i].Label}': '{part.Trim()}' is not a number.");
                    lo = Math.Min(lo, v); hi = Math.Max(hi, v);
                }
            return lo <= hi ? (lo, hi) : (0, 0);
        }

        private List<ExperimentScenario> Runs => scenarios.Count > 0 ? scenarios
            : new List<ExperimentScenario> { new ExperimentScenario { scenarioName = "Baseline" } };

        // The metadata of the last headless study (null in the scene mode).
        private List<KeyValuePair<string, string>> studyMetadata;

        public void StartExperiments()
        {
            if (running) return;
            studyMetadata = null;
            projectRoot = Path.Combine(Application.dataPath, "..");
            results.Clear();
            completedRuns = 0;
            started = DateTime.Now;
            if (headlessMode) RunHeadless();
            else RunInScene();
        }

        // Leaving Play mode, disabling or deleting the component stops the headless runs going on in the
        // background.
        private void OnDisable() => cancellation?.Cancel();

        public void CancelExperiments()
        {
            if (!running) return;
            cancellation?.Cancel();
            if (!headlessMode) FinishInScene("Cancelled");
        }

        // ── Headless ───────────────────────────────────────────────────────────

        private async void RunHeadless()
        {
            Study study;
            try { study = BuildStudy(SceneExtractor.Extract(modelRoot).Config); }
            catch (Exception e) { Fail(e.Message); return; }
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            running = true;
            try
            {
                bool complete = await Task.Run(() => Execute(study, token));
                Complete(complete ? "Done" : "Cancelled");
            }
            catch (Exception e)
            {
                Fail(e.InnerException?.Message ?? e.Message);
            }
        }

        /// <summary>Runs the whole experiment headless on the calling thread (blocking; scripts and tests).</summary>
        public IReadOnlyList<ResultRun> RunHeadlessNow()
        {
            results.Clear();
            started = DateTime.Now;
            completedRuns = 0;
            Execute(BuildStudy(SceneExtractor.Extract(modelRoot).Config), CancellationToken.None);
            return results;
        }

        /// <summary>Every (scenario, replication) in parallel (<see cref="Study.Run"/>); the results in run order. False when
        /// cancelled (the runs done are kept).</summary>
        private bool Execute(Study study, CancellationToken token)
        {
            totalRuns = Runs.Sum(s => Math.Max(1, s.replications));
            status = $"Running {totalRuns} runs…";
            var set = study.Run(token, (n, total) => { completedRuns = n; status = $"Run {n} / {total}"; });
            results.AddRange(set.Runs);
            studyMetadata = set.Metadata;   // base_seed actually used, cancelled, failed_runs / first_error
            return !token.IsCancellationRequested;
        }

        // ── In the scene ───────────────────────────────────────────────────────

        private void RunInScene()
        {
            var clock = UnitySimClock.Instance;
            if (!Application.isPlaying || clock == null) { Fail("Running in the scene needs Play mode and a Simulation Clock."); return; }
            // Validate returns the problem (it was discarded: an invalid set-up ran anyway)
            string problem;
            try { problem = BuildStudy(SceneExtractor.Extract(modelRoot).Config).Validate(); }
            catch (Exception e) { problem = e.Message; }
            if (problem != null) { Fail(problem); return; }
            // every scenario × replication, replication r with seed + r − 1 in every scenario (common random numbers,
            // the same seeds as the headless runs); the clock's own seed settings are restored at the end
            sceneRuns = new List<(ExperimentScenario, int)>();
            foreach (var sc in Runs) for (int r = 1; r <= Math.Max(1, sc.replications); r++) sceneRuns.Add((sc, r));
            totalRuns = sceneRuns.Count;
            sceneRun = 0;
            sceneBaseSeed = randomSeed != 0 ? randomSeed : Environment.TickCount;
            savedSeedMode = clock.SeedMode;
            savedSeed = clock.Seed;
            // the experiment's Record Timeline also attaches the job recorder, which the clock does at the build only
            // with its own switch on (the jobs Gantt was missing unless the clock's switch was on too)
            savedRecordTimeline = clock.RecordTimeline;
            if (recordTimeline) clock.RecordTimeline = true;
            running = true;
            clock.SimEvents.OnSimFinish += NextScenario;
            StartSceneRun(clock);
        }

        private void StartSceneRun(UnitySimClock clock)
        {
            var (s, rep) = sceneRuns[sceneRun];
            clock.UseFixedSeed(sceneBaseSeed + rep - 1);
            status = $"Scenario '{s.scenarioName}', replication {rep} ({sceneRun + 1} / {totalRuns}) in the scene";
            clock.ConfigureModel = config =>
            {
                // an error here would be thrown from the clock's Update every frame, leaving the experiment "running"
                // and the scenario's configuration on the clock: it ends the experiment instead
                try { BuildStudy(config).Apply(config, s.scenarioName); }
                catch (Exception e) { FailInScene($"scenario '{s.scenarioName}': {e.Message}"); }
            };
            clock.SimEvents.OnSimStart?.Invoke(runTime > 0 ? runTime : clock.MaxTime);   // a double: no float rounding
        }

        /// <summary>The clock's run ended (scene mode): records the scenario's metrics and starts the next one.</summary>
        public void NextScenario()
        {
            var clock = UnitySimClock.Instance;
            if (!running || headlessMode || clock == null) return;
            var (s, rep) = sceneRuns[sceneRun];
            try
            {
                var model = clock.Model;
                var runResults = RunResults.Capture(model, gameObject.scene.name, recordTimeline);
                var result = new ResultRun { Scenario = s.scenarioName, Replication = rep, Seed = runResults.Info.Seed };
                foreach (var m in metrics) result.Metrics[m.name] = MetricCatalog.Read(m.ToModelMetric(), runResults, model);
                if (exportElementStats) result.Results = runResults;
                results.Add(result);
            }
            catch (Exception e) { FailInScene($"scenario '{s.scenarioName}', replication {rep}: {e.Message}"); return; }
            completedRuns++;
            sceneRun++;
            if (sceneRun < sceneRuns.Count) StartSceneRun(clock);
            else FinishInScene("Done");
        }

        private void FinishInScene(string how)
        {
            var clock = UnitySimClock.Instance;
            if (clock != null)
            {
                clock.SimEvents.OnSimFinish -= NextScenario;
                clock.ConfigureModel = null;
                clock.Seed = savedSeed;
                clock.SeedMode = savedSeedMode;
                clock.RecordTimeline = savedRecordTimeline;
                clock.SimEvents.OnExperimentFinish?.Invoke();
            }
            Complete(how);
        }

        // An error during an in-scene run: the clock gets its own settings back, the runs done are kept.
        private void FailInScene(string message)
        {
            Debug.LogError("[Experimenter] " + message);
            FinishInScene("Failed (" + message + ")");
        }

        // ── End ────────────────────────────────────────────────────────────────

        private void Fail(string message)
        {
            running = false;
            status = "Invalid: " + message;
            Debug.LogError("[Experimenter] " + message);
        }

        private void Complete(string how)
        {
            running = false;
            status = $"{how}: {results.Count} run(s) in {(DateTime.Now - started).TotalSeconds:F1} s";
            Debug.Log("[Experimenter] " + status);
            if (results.Count > 0)   // published for the Results tab; written in the chosen formats
            {
                try
                {
                    foreach (string path in ResultsOutput.Keep(BuildResultSet((DateTime.Now - started).TotalSeconds), exportFormats, projectRoot))
                        Debug.Log($"[Experimenter] Results: {path}");
                }
                catch (Exception e) { Debug.LogWarning($"[Experimenter] Results export failed: {e.Message}"); }
            }
        }

        /// <summary>The last experiment as a result set (scenarios with their parameter values, runs, settings).</summary>
        public ResultSet BuildResultSet(double seconds = 0)
        {
            var set = new ResultSet
            {
                Name = $"Experiment_{gameObject.name}_{started:yyyyMMdd_HHmmss}", Source = "experiment",
                Model = gameObject.scene.name, Created = started,
            };
            set.MetricNames.AddRange(metrics.Select(m => m.name));
            foreach (var s in Runs)
            {
                var scenario = new ResultScenario { Name = s.scenarioName };
                for (int i = 0; i < parameters.Count; i++)
                    scenario.Parameters.Add(new KeyValuePair<string, string>(parameters[i].Label, i < s.parameterValues.Count ? s.parameterValues[i] : ""));
                set.Scenarios.Add(scenario);
            }
            set.Runs.AddRange(results);
            set.Metadata.Add(new KeyValuePair<string, string>("experiment", gameObject.name));
            set.Metadata.Add(new KeyValuePair<string, string>("mode", headlessMode ? "headless" : "scene"));
            set.Metadata.Add(new KeyValuePair<string, string>("run_time", runTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            // the seed actually used (randomSeed 0 = a new base each run) and the study's own notes (cancelled, failed runs)
            if (studyMetadata != null)
            {
                foreach (var kv in studyMetadata)
                    if (kv.Key == "base_seed" || kv.Key == "cancelled" || kv.Key == "failed_runs" || kv.Key == "first_error") set.Metadata.Add(kv);
            }
            else set.Metadata.Add(new KeyValuePair<string, string>("base_seed", (randomSeed != 0 ? randomSeed : sceneBaseSeed).ToString()));
            if (seriesInterval > 0) set.Metadata.Add(new KeyValuePair<string, string>("series_interval", seriesInterval.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            if (seconds > 0) set.Metadata.Add(new KeyValuePair<string, string>("wall_seconds", seconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));
            return set;
        }
    }
}
