using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SimuLean.Optimization;
using SimuLean.Optimization.Evaluator;
using Unity.Pipeline.Commands;
using UnityEditor;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Optimisation through the MCP: the agents set up, started, polled
    /// (sleeping through <c>eval</c>) and read the Optimizer in C#. <c>simulean_optimize_start</c> runs a search in the
    /// background — given in the call (parameters, metrics and fitness in the study grammar) or set up on a scene
    /// Optimizer — and <c>simulean_optimize_status</c> reports it (with <c>wait</c>). The scene is never changed.
    /// </summary>
    public static partial class SimuLeanCommands
    {
        private sealed class OptimizeJob
        {
            public Task<object> Task;
            public CancellationTokenSource Cancel;
            public DateTime Started;
            public AgentReply.LogCapture Log;
            public volatile OptimizationProgress Progress;
        }
        private static OptimizeJob optimizeJob;

        [CliCommand("simulean_optimize_start", "Start an optimisation in the background and return at once (poll simulean_optimize_status " +
                                               "with wait). Either give the search here — parameters (study paths with a range " +
                                               "'Buffer.capacity=1..10;team:Welders.members=1..4', or options 'a|b|c', searched over those values only), metrics " +
                                               "('name=path;…' as simulean_run_headless) and fitness (maximised expression over the " +
                                               "metric names; negate to minimise) — or name a scene Optimizer set up in the inspector; " +
                                               "the other arguments override its settings. Checked before it starts. One search at a time.",
                    Tags = new[] { Tag })]
        public static object OptimizeStart(
            [CliArg("parameters", "Decision variables: 'path=lo..hi' (a range) or 'path=a|b|c' (those values only, numbers too) or JSON {\"path\": [lo, hi]} (empty = the Optimizer's).")] string parameters = "",
            [CliArg("metrics", "Observed values 'name=path;…' or JSON (empty = the Optimizer's).")] string metrics = "",
            [CliArg("fitness", "Maximised expression over the metric names, e.g. 'out - 50*operators' (empty = the Optimizer's, or the only metric).")] string fitness = "",
            [CliArg("optimizer", "A scene Optimizer (name or path) whose setup is used (empty = none: the search given here).")] string optimizer = "",
            [CliArg("algorithm", "'genetic' or 'random' (any AlgorithmSettings type by name; empty = the Optimizer's, else genetic).")] string algorithm = "",
            [CliArg("algorithmSettings", "The algorithm's settings 'populationSize=20;generations=10' or JSON.")] string algorithmSettings = "",
            [CliArg("maxEvaluations", "Stop after this many candidates (0 = the algorithm's end; -1 = the Optimizer's).")] int maxEvaluations = -1,
            [CliArg("maxSeconds", "Stop after this many seconds (0 = no limit; -1 = the Optimizer's).")] double maxSeconds = -1,
            [CliArg("replications", "Runs per candidate, common random numbers (0 = the Optimizer's, else 1).")] int replications = 0,
            [CliArg("runTime", "Simulated time of each run (0 = the Optimizer's, else the clock's).")] double runTime = 0,
            [CliArg("seed", "Seed of the runs and of the algorithm (0 = the Optimizer's, else 1).")] int seed = 0,
            [CliArg("threads", "Candidates simulated at once (0 = the Optimizer's, else one per processor up to 8).")] int threads = 0,
            [CliArg("root", "Model root GameObject when no Optimizer is named (empty = the scene's model).")] string root = "")
            => AgentReply.Of(() => OptimizeStartOf(parameters, metrics, fitness, optimizer, algorithm, algorithmSettings, maxEvaluations,
                                                   maxSeconds, replications, runTime, seed, threads, root));

        [CliCommand("simulean_optimize_status", "The search of simulean_optimize_start: 'running' (iteration, candidates, best so far), " +
                                                "then 'completed' with the best candidate (fitness, values) and its metrics re-run for " +
                                                "every replication (mean ± half-width), or 'failed'. wait=s answers as soon as it ends " +
                                                "(at most 55 s); cancel=true stops it (the best so far is kept).", Tags = new[] { Tag })]
        public static Task<object> OptimizeStatus(
            [CliArg("cancel", "Stop the search (the best so far is the result).")] bool cancel = false,
            [CliArg("wait", "Seconds to wait for the search to end (0 = answer at once; at most 55).")] double wait = 0)
        {
            if (cancel && optimizeJob != null && !optimizeJob.Task.IsCompleted) optimizeJob.Cancel.Cancel();
            return JobWait.Until(() => JobWait.Json(AgentReply.Of(OptimizeStatusOf)), wait);
        }

        private static object OptimizeStartOf(string parameters, string metrics, string fitness, string optimizer, string algorithm,
                                              string algorithmSettings, int maxEvaluations, double maxSeconds, int replications,
                                              double runTime, int seed, int threads, string root)
        {
            if (optimizeJob != null && !optimizeJob.Task.IsCompleted)
                throw new InvalidOperationException("A search is going on: poll simulean_optimize_status (or cancel it there).");

            Optimizer.Setup setup;
            List<string> warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(optimizer))
            {
                var component = FindObject(optimizer).GetComponent<Optimizer>()
                                ?? throw new ArgumentException($"'{optimizer}' has no Optimizer component.");
                if (!string.IsNullOrWhiteSpace(parameters) || !string.IsNullOrWhiteSpace(metrics))
                    throw new ArgumentException("parameters / metrics come from the Optimizer when one is named: set them on it " +
                                                "(simulean_set) or leave optimizer empty.");
                setup = component.Prepare();
                if (!string.IsNullOrWhiteSpace(fitness)) { setup.Objective = setup.Objective.WithFitness(fitness); setup.Fitness = fitness; }
            }
            else setup = SearchOf(parameters, metrics, fitness, root, warnings);

            if (!string.IsNullOrWhiteSpace(algorithm)) setup.Algorithm = AlgorithmOf(algorithm);
            ApplySettings(setup.Algorithm, algorithmSettings);
            if (maxEvaluations >= 0) setup.Settings.MaxEvaluations = maxEvaluations;
            if (maxSeconds >= 0) setup.Settings.MaxSeconds = maxSeconds;
            if (replications > 0) setup.Objective.Replications = replications;
            if (runTime > 0) setup.Objective.RunTime = runTime;
            if (seed != 0) { setup.Objective.BaseSeed = seed; setup.Settings.Seed = seed; }
            if (threads > 0) setup.Settings.Parallelism = threads;

            var job = new OptimizeJob { Cancel = new CancellationTokenSource(), Started = DateTime.Now, Log = new AgentReply.LogCapture() };
            string projectRoot = ResultsOutput.ProjectRoot, scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            job.Task = Task.Run(() =>
            {
                try
                {
                    var (result, set, setError) = setup.Run(p => job.Progress = p, job.Cancel.Token);
                    if (set != null) Kept(set, projectRoot, scene);
                    return (object)AgentReply.With(OptimizeAnswer(result, set, setError, job), job.Log.Errors, job.Log.Warnings);
                }
                finally { job.Log.Dispose(); }
            });
            optimizeJob = job;
            return new
            {
                status = "running",
                algorithm = setup.Algorithm.Describe(),
                parameters = setup.Objective.Parameters.Select(p => p.Variable.Name).ToList(),
                metrics = setup.Objective.Metrics.Select(m => m.Name).ToList(),
                fitness = setup.Fitness,
                replications = setup.Objective.Replications,
                runTime = setup.Objective.RunTime,
                seed = setup.Settings.Seed,
                maxEvaluations = setup.Settings.MaxEvaluations,
                maxSeconds = setup.Settings.MaxSeconds,
                threads = setup.Settings.Parallelism,
                warnings,
            };
        }

        /// <summary>The search given in the call, on the scene's model, checked like a study (the metric paths on the built
        /// model).</summary>
        private static Optimizer.Setup SearchOf(string parameters, string metrics, string fitness, string root, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(parameters)) throw new ArgumentException("parameters: give at least one (e.g. 'Buffer.capacity=1..10'), or name an optimizer.");
            if (string.IsNullOrWhiteSpace(metrics)) throw new ArgumentException("metrics: give at least one (e.g. 'out=Sink.input'), or name an optimizer.");
            var study = SceneStudy.Of(string.IsNullOrEmpty(root) ? null : FindObject(root));
            var ps = OptimizationParametersOf(parameters).Select(p => p.options ? study.OptionsOf(p.path, p.values) : study.ParameterOf(p.path, p.values)).ToList();
            foreach (var p in ps)
            {
                string note = SimuLean.Optimization.Study.RangeNote(p);
                if (note != null) warnings.Add(note);
            }
            var ms = MetricsOf(metrics);
            foreach (var (name, path) in ms) study.Metric(name, path);
            var check = study.Check();
            if (!check.Ok) throw new ArgumentException("The search cannot run: " + string.Join(" | ", check.Errors));
            warnings.AddRange(check.Warnings);
            if (string.IsNullOrWhiteSpace(fitness))
                fitness = ms.Count == 1 ? ms[0].name : throw new ArgumentException("fitness: give the expression to maximise over the metrics " +
                                                                                   $"({string.Join(", ", ms.Select(m => m.name))}).");
            var objective = new SimulationObjective(study.Model, ps, study.Metrics, fitness)
            {
                Replications = 1, RunTime = study.EffectiveRunTime, BaseSeed = 1,
            };
            return new Optimizer.Setup
            {
                Objective = objective, Space = objective.BuildSpace(), Fitness = fitness, Algorithm = new GeneticAlgorithmSettings(),
                Settings = new OptimizationSettings { Seed = 1, Parallelism = SimuLean.Optimization.Study.DefaultThreads },
            };
        }

        /// <summary>The decision variables of <c>parameters</c>: the <c>set</c> grammar, a level <c>lo..hi</c> giving the
        /// range, two or more levels <c>a|b|c</c> the options (searched over those values only, numbers too); JSON
        /// <c>{"path": [lo, hi]}</c> is a range.</summary>
        internal static List<(string path, string[] values, bool options)> OptimizationParametersOf(string parameters)
        {
            bool json = (parameters ?? "").TrimStart().StartsWith("{") || (parameters ?? "").TrimStart().StartsWith("[");
            return FactorsOf(parameters).Select(f =>
            {
                if (f.levels.Length == 1 && f.levels[0].Contains(".."))
                {
                    var parts = f.levels[0].Split(new[] { ".." }, StringSplitOptions.None);
                    if (parts.Length != 2) throw new ArgumentException($"parameters: '{f.path}={f.levels[0]}' is not a range lo..hi.");
                    return (f.path, new[] { parts[0].Trim(), parts[1].Trim() }, false);
                }
                return (f.path, f.levels, !json && f.levels.Length > 1);
            }).ToList();
        }

        /// <summary>An algorithm's settings by name: the type name (with or without "Settings"), its display name, or the
        /// start of either (<c>genetic</c>, <c>random</c>).</summary>
        internal static AlgorithmSettings AlgorithmOf(string name)
        {
            var types = TypeCache.GetTypesDerivedFrom<AlgorithmSettings>().Where(t => !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null).ToList();
            string n = name.Trim().ToLowerInvariant().Replace(" ", "");
            string Key(Type t) => t.Name.ToLowerInvariant().Replace("settings", "");
            string Display(Type t) => ((AlgorithmSettings)Activator.CreateInstance(t)).DisplayName.ToLowerInvariant().Replace(" ", "");
            var match = types.Where(t => Key(t) == n || Display(t) == n).ToList();
            if (match.Count == 0) match = types.Where(t => Key(t).StartsWith(n) || Display(t).StartsWith(n)).ToList();
            if (match.Count != 1)
                throw new ArgumentException($"algorithm: '{name}' is {(match.Count == 0 ? "not" : "more than")} one of " +
                                            string.Join(", ", types.Select(t => ((AlgorithmSettings)Activator.CreateInstance(t)).DisplayName)) + ".");
            return (AlgorithmSettings)Activator.CreateInstance(match[0]);
        }

        /// <summary>Writes <c>name=value;…</c> (or a JSON object) into the algorithm's public fields.</summary>
        internal static void ApplySettings(AlgorithmSettings algorithm, string settings)
        {
            if (string.IsNullOrWhiteSpace(settings)) return;
            var fields = algorithm.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            IEnumerable<(string, string)> pairs = settings.Trim().StartsWith("{")
                ? JObject.Parse(settings).Properties().Select(p => (p.Name, p.Value.Type == JTokenType.String ? (string)p.Value
                                                                     : p.Value.ToString(Newtonsoft.Json.Formatting.None)))
                : Pairs(settings, "algorithmSettings");
            foreach (var (name, value) in pairs)
            {
                var f = fields.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"algorithmSettings: {algorithm.DisplayName} has no '{name}' (it has " +
                                                       string.Join(", ", fields.Select(x => x.Name)) + ").");
                try { f.SetValue(algorithm, Convert.ChangeType(value, f.FieldType, CultureInfo.InvariantCulture)); }
                catch (Exception e) when (e is FormatException || e is InvalidCastException || e is OverflowException)
                { throw new ArgumentException($"algorithmSettings: {name}: '{value}' is not a {f.FieldType.Name}."); }
            }
        }

        private static object OptimizeStatusOf()
        {
            var job = optimizeJob;
            if (job == null) return new { status = "none" };
            if (!job.Task.IsCompleted)
            {
                var p = job.Progress;
                return new
                {
                    status = "running",
                    iteration = p?.Iteration ?? 0,
                    evaluations = p?.Evaluations ?? 0,
                    bestFitness = p?.BestEvaluation != null ? Finite(p.BestEvaluation.Fitness) : null,
                    best = p?.Best != null ? Values(p.Best) : null,
                    seconds = Math.Round((DateTime.Now - job.Started).TotalSeconds, 1),
                };
            }
            if (job.Task.IsFaulted)
            {
                string error = job.Task.Exception?.GetBaseException().Message;
                return new { status = "failed", error, errors = new[] { error }.Concat(job.Log.Errors).ToList(), warnings = job.Log.Warnings };
            }
            return job.Task.Result;
        }

        private static object OptimizeAnswer(OptimizationResult r, SimuLean.Results.ResultSet set, string setError, OptimizeJob job)
        {
            object best = null;
            if (r.Best != null && r.BestEvaluation != null && r.BestEvaluation.Error == null && !double.IsNegativeInfinity(r.BestEvaluation.Fitness))
            {
                var metrics = new Dictionary<string, object>();
                if (set != null)
                    foreach (string m in set.MetricNames)
                        metrics[m] = Interval(set.Runs.Where(x => x.Metrics.ContainsKey(m)).Select(x => x.Metrics[m]), 0.95);
                best = new { fitness = Finite(r.BestEvaluation.Fitness), values = Values(r.Best), metrics };
            }
            return new
            {
                status = "completed",
                seconds = Math.Round((DateTime.Now - job.Started).TotalSeconds, 1),
                stopReason = r.StopReason,
                evaluations = r.Evaluations,
                failedCandidates = r.Candidates.Count(c => c.Evaluation?.Error != null),
                firstError = r.Candidates.FirstOrDefault(c => c.Evaluation?.Error != null)?.Evaluation.Error,
                best,
                resultSet = set?.Name,
                warnings = setError != null ? new List<string> { "The best candidate's re-run failed: " + setError } : new List<string>(),
            };
        }

        private static Dictionary<string, string> Values(Candidate c)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i < c.Space.Count; i++) d[c.Space.Variables[i].Name] = c.DisplayText(i);
            return d;
        }
    }
}
