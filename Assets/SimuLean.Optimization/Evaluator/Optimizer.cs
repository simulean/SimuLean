using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using SimuLean.Serialization;
using SimuLean.Unity;
using SimuLean.Results;

namespace SimuLean.Optimization.Evaluator
{
    /// <summary>
    /// Optimises parameters of the scene's model: the scene is extracted once, each candidate is run headless
    /// (<see cref="SimulationObjective"/>: replications with common random numbers, the fitness expression over the
    /// metrics) by the chosen algorithm (<see cref="AlgorithmSettings"/> — any subclass appears in the inspector), in the
    /// background, in Edit or Play mode. The best candidate, its metrics and an optional Excel report are the result.
    /// </summary>
    [AddComponentMenu("SimuLean/Optimizer")]
    [SimuLean.Unity.SimuLeanTool(SimuLean.Unity.SimuLeanToolCategories.Experiments, "Optimizer", Description = "Optimisation of model parameters (genetic algorithm, random search or any plugged-in algorithm)", Order = 1)]
    public class Optimizer : MonoBehaviour
    {
        [Tooltip("Root of the model (empty = the whole scene).")]
        [SerializeField] private GameObject modelRoot;

        [Tooltip("The parameters to optimise: element keys, team sizes, schedule sequences…")]
        [SerializeField] private List<ParameterSpec> parameters = new List<ParameterSpec>();

        [Tooltip("The observed values: their names are the variables of the fitness.")]
        [SerializeField] private List<MetricSpec> metrics = new List<MetricSpec>();

        [Tooltip("Maximised. An expression over the metric names, e.g. throughput - 50 * operators (negate to minimise: -wip).")]
        [SerializeField] private string fitness = "throughput";

        [SerializeReference] private AlgorithmSettings algorithm = new GeneticAlgorithmSettings();

        [Tooltip("Runs per candidate (averaged); every candidate uses the same seeds (common random numbers).")]
        [Min(1)] [SerializeField] private int replications = 1;
        [Tooltip("Simulated time of each run (0 = the Simulation Clock's max time).")]
        [SerializeField] private double runTime = 0;
        [Tooltip("Seed of the runs and of the algorithm.")]
        [SerializeField] private int seed = 1;
        [Tooltip("Stop after this many simulated candidates (0 = the algorithm's own end).")]
        [Min(0)] [SerializeField] private int maxEvaluations = 0;
        [Tooltip("Stop after this many seconds (0 = no limit).")]
        [Min(0)] [SerializeField] private float maxSeconds = 0;
        [Tooltip("Candidates simulated at once (0 = one per processor up to 8).")]
        [Min(0)] [SerializeField] private int threads = 0;
        [Tooltip("Formats the results are exported to when the run completes (the results folder of the project settings; SQLite: the project database): the best solution re-run for every replication, the history. Nothing = no file (the SimuLean window's Results tab still shows them).")]
        [SerializeField] private ResultFormats exportFormats = ResultFormats.Excel;
        [Tooltip("Interval of the time series recorded in the best solution's re-run (content, throughput, states per interval). 0 = none.")]
        [Min(0)] [SerializeField] private double seriesInterval = 0;
        [Tooltip("Keep every state interval of elements and resources (the Gantt charts; G3c). Off by default: the results grow with every state change. (the best solution's re-run)")]
        [SerializeField] private bool recordTimeline = false;

        // results: in memory only (shown by the inspector, written to the report) — never saved with the scene
        [NonSerialized] private string status = "Ready";
        [NonSerialized] private double bestFitness;
        [NonSerialized] private readonly List<string> bestValues = new List<string>();
        [NonSerialized] private readonly List<string> bestMetrics = new List<string>();

        private CancellationTokenSource cancellation;
        private volatile bool running;

        public bool IsRunning => running;
        public string Status => status;
        /// <summary>The best candidate of the last run (empty before a run or when none was evaluated).</summary>
        public bool HasBest => bestValues.Count > 0;
        public double BestFitness => bestFitness;
        public IReadOnlyList<string> BestValues => bestValues;
        public IReadOnlyList<string> BestMetrics => bestMetrics;
        public AlgorithmSettings Algorithm { get => algorithm; set => algorithm = value; }
        public List<ParameterSpec> Parameters => parameters;
        public List<MetricSpec> Metrics => metrics;
        public OptimizationResult LastResult { get; private set; }

        /// <summary>Checks the setup (builds the problem without running it); null when valid, else the problem.</summary>
        public string Validate()
        {
            try { BuildProblem(out _, out _); return null; }
            catch (Exception e) { return e.Message; }
        }

        private SimulationObjective BuildProblem(out SimulationConfig config, out SearchSpace space)
        {
            if (algorithm == null) throw new InvalidOperationException("Choose an algorithm.");
            if (parameters.Count == 0) throw new InvalidOperationException("Add at least one parameter.");
            if (metrics.Count == 0) throw new InvalidOperationException("Add at least one metric.");
            config = SceneExtractor.Extract(modelRoot).Config;
            var ps = new List<ModelParameter>();
            foreach (var p in parameters) ps.Add(p.ToModelParameter(config));
            var ms = new List<ModelMetric>();
            foreach (var m in metrics) ms.Add(m.ToModelMetric());
            var objective = new SimulationObjective(config, ps, ms, fitness)
            {
                Replications = replications,
                RunTime = runTime > 0 ? runTime : config.MaxSimulationTime,
                BaseSeed = seed,
            };
            space = objective.BuildSpace();
            return objective;
        }

        /// <summary>A search ready to run: the problem (the scene extracted once), the algorithm's settings and the run
        /// settings. Built on the main thread; run anywhere (<see cref="Run"/>).</summary>
        public sealed class Setup
        {
            public SimulationObjective Objective;
            public SearchSpace Space;
            public AlgorithmSettings Algorithm;
            public OptimizationSettings Settings;
            public string Fitness;
            public double SeriesInterval;
            public bool RecordTimeline;

            /// <summary>Runs the search and re-runs the best candidate for every replication (the result set; null with the
            /// error when it could not be built). Off the main thread: it never touches Unity.</summary>
            public (OptimizationResult result, ResultSet set, string setError) Run(Action<OptimizationProgress> progress, CancellationToken token)
            {
                DateTime start = DateTime.Now;
                var r = OptimizationEngine.Run(Space, Objective, Algorithm.Create(), Settings, progress, token);
                try { return (r, BuildResultSet(r, Objective, Fitness, Algorithm.Describe(), start, SeriesInterval, RecordTimeline), null); }
                catch (Exception e) { return (r, null, e.Message); }
            }
        }

        /// <summary>The search this component is set up for (what <see cref="StartOptimization"/> runs, and the MCP's
        /// <c>simulean_optimize_start</c>); throws when the setup is not valid. The algorithm's settings are a copy.</summary>
        public Setup Prepare()
        {
            var objective = BuildProblem(out _, out SearchSpace space);
            return new Setup
            {
                Objective = objective, Space = space, Fitness = fitness, SeriesInterval = seriesInterval, RecordTimeline = recordTimeline,
                Algorithm = (AlgorithmSettings)JsonUtility.FromJson(JsonUtility.ToJson(algorithm), algorithm.GetType()),
                Settings = new OptimizationSettings
                {
                    MaxEvaluations = maxEvaluations, MaxSeconds = maxSeconds, Seed = seed,
                    Parallelism = threads > 0 ? threads : Study.DefaultThreads,
                },
            };
        }

        public async void StartOptimization()
        {
            if (running) return;
            Setup setup;
            try { setup = Prepare(); }
            catch (Exception e)
            {
                status = "Invalid: " + e.Message;
                Debug.LogError("[Optimizer] " + e.Message);
                return;
            }
            string root = ResultsOutput.ProjectRoot;
            string sceneName = gameObject.scene.name;   // read now: the component may be gone when the search ends
            cancellation = new CancellationTokenSource();
            running = true;
            status = $"Running {setup.Algorithm.DisplayName}…";
            try
            {
                var token = cancellation.Token;
                // the search AND the re-run of the best (every replication) off the main thread: the re-run froze the
                // editor for as many simulations
                var (result, set, exportError) = await Task.Run(() => setup.Run(
                    p => status = $"Iteration {p.Iteration} · {p.Evaluations} runs · best {p.BestEvaluation?.Fitness:G6} · {p.Seconds:F0} s",
                    token));
                LastResult = result;
                if (this == null) return;   // destroyed meanwhile (left Play mode): it was cancelled, nothing to show
                ShowResult(LastResult);
                ExportResults(set, exportError, sceneName, root);   // published for the Results tab; written in the chosen formats
            }
            catch (Exception e)
            {
                status = "Failed: " + e.Message;
                Debug.LogError("[Optimizer] " + e);
            }
            finally
            {
                running = false;
            }
        }

        public void CancelOptimization() => cancellation?.Cancel();

        // Leaving Play mode, disabling or deleting the component stops a search running in the background (it went on
        // for its whole budget).
        private void OnDisable() => cancellation?.Cancel();

        private void ShowResult(OptimizationResult r)
        {
            bestValues.Clear(); bestMetrics.Clear();
            if (r.Best == null) { status = "No candidate evaluated"; return; }
            bestFitness = r.BestEvaluation.Fitness;
            for (int i = 0; i < r.Best.Space.Count; i++) bestValues.Add(r.Best.Space.Variables[i].Name + " = " + r.Best.DisplayText(i));
            foreach (var kv in r.BestEvaluation.Metrics) bestMetrics.Add($"{kv.Key} = {kv.Value:G6}");
            status = $"Done ({r.StopReason}): {r.Evaluations} runs in {r.Seconds:F1} s";
            Debug.Log($"[Optimizer] {status}. Best fitness {bestFitness:G6}: {r.Best}");
        }

        // "Genetic algorithm" → "GeneticAlgorithm" (report file names)
        private static string FileNamePart(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var word in name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
                sb.Append(char.ToUpperInvariant(word[0])).Append(word.Substring(1));
            return sb.ToString();
        }

        /// <summary>
        /// The run as a result set: the best solution (scenario <c>best</c>, its parameter values) re-run for every
        /// replication with its metrics and statistics, the history per iteration (<c>history</c>), the algorithm and
        /// settings in the metadata.
        /// </summary>
        public static ResultSet BuildResultSet(OptimizationResult r, SimulationObjective objective, string fitnessExpression,
                                               IEnumerable<KeyValuePair<string, string>> hyperparameters, DateTime start, double seriesInterval = 0,
                                               bool recordTimeline = false)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var set = new ResultSet { Name = $"{FileNamePart(r.Algorithm)}_Report_{start:yyyyMMdd_HHmmss}", Source = "optimization", Created = start };
            foreach (var m in objective.Metrics) set.MetricNames.Add(m.Name);
            set.Metadata.Add(new KeyValuePair<string, string>("algorithm", r.Algorithm));
            set.Metadata.Add(new KeyValuePair<string, string>("fitness", fitnessExpression ?? ""));
            set.Metadata.Add(new KeyValuePair<string, string>("stop_reason", r.StopReason ?? ""));
            set.Metadata.Add(new KeyValuePair<string, string>("evaluations", r.Evaluations.ToString(ci)));
            set.Metadata.Add(new KeyValuePair<string, string>("seconds", r.Seconds.ToString("F2", ci)));
            set.Metadata.Add(new KeyValuePair<string, string>("replications", objective.Replications.ToString(ci)));
            set.Metadata.Add(new KeyValuePair<string, string>("run_time", objective.RunTime.ToString("R", ci)));
            set.Metadata.Add(new KeyValuePair<string, string>("base_seed", objective.BaseSeed.ToString(ci)));
            if (hyperparameters != null)
                foreach (var kv in hyperparameters) set.Metadata.Add(new KeyValuePair<string, string>("algorithm." + kv.Key, kv.Value));

            var history = new SimuLean.Data.ResultTable("history", "iteration", "evaluations", "best_fitness", "elapsed_s");
            foreach (var h in r.History) history.Add(h.Iteration, h.Evaluations, ResultSetNum(h.BestFitness), h.Seconds);
            if (history.RowCount > 0) set.Extra.Add(history);
            r.CopyCandidatesTo(set);

            // nothing to re-run when no candidate has a valid evaluation (they all failed: the history and the candidates'
            // errors are the result)
            if (r.Best == null || r.BestEvaluation.Error != null || double.IsNegativeInfinity(r.BestEvaluation.Fitness)) return set;
            set.Metadata.Add(new KeyValuePair<string, string>("best_fitness", r.BestEvaluation.Fitness.ToString("R", ci)));
            var best = new ResultScenario { Name = "best" };
            for (int i = 0; i < r.Best.Space.Count; i++)
                best.Parameters.Add(new KeyValuePair<string, string>(r.Best.Space.Variables[i].Name, r.Best.Text(i)));
            set.Scenarios.Add(best);

            var runs = new List<RunResults>();
            double previous = objective.SeriesInterval;
            bool previousTimeline = objective.RecordTimeline;
            objective.SeriesInterval = seriesInterval;
            objective.RecordTimeline = recordTimeline;
            Evaluation again;
            try { again = objective.Replicate(r.Best, runs); }   // every replication, with its statistics
            finally { objective.SeriesInterval = previous; objective.RecordTimeline = previousTimeline; }
            for (int k = 0; k < runs.Count; k++)
            {
                var run = new ResultRun { Scenario = "best", Replication = k + 1, Seed = runs[k].Info.Seed, Results = runs[k] };
                foreach (var kv in again.MetricValues) if (k < kv.Value.Count) run.Metrics[kv.Key] = kv.Value[k];
                set.Runs.Add(run);
            }
            return set;
        }

        private static object ResultSetNum(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : (object)v;

        private void ExportResults(ResultSet set, string buildError, string sceneName, string root)
        {
            if (set == null) { Debug.LogWarning($"[Optimizer] Results export failed: {buildError}"); return; }
            try
            {
                set.Model = sceneName;
                foreach (string path in ResultsOutput.Keep(set, exportFormats, root)) Debug.Log($"[Optimizer] Results: {path}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Optimizer] Results export failed: {e.Message}");
            }
        }
    }
}
