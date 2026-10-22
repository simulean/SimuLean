using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Optimization
{
    /// <summary>The result of evaluating one candidate. Algorithms <b>maximise</b> <see cref="Fitness"/>.</summary>
    public sealed class Evaluation
    {
        public double Fitness { get; set; }
        /// <summary>Observed values (e.g. throughput, utilisation) for reports.</summary>
        public Dictionary<string, double> Metrics { get; } = new Dictionary<string, double>();
        /// <summary>Each replication's value of every metric, in seed order (the mean is <see cref="Metrics"/>); may be empty.</summary>
        public Dictionary<string, List<double>> MetricValues { get; } = new Dictionary<string, List<double>>();
        /// <summary>Not null when the evaluation failed (its fitness is then −∞).</summary>
        public string Error { get; set; }

        public static Evaluation Failed(string error) => new Evaluation { Fitness = double.NegativeInfinity, Error = error };
    }

    /// <summary>What is being optimised: evaluates a candidate. Called from several threads at once — keep it stateless.</summary>
    public interface IObjective
    {
        Evaluation Evaluate(Candidate candidate);
    }

    /// <summary>
    /// A search strategy (genetic algorithm, random search, a metaheuristics library…). It proposes candidates and asks
    /// the <see cref="OptimizationRun"/> to evaluate them; the run gives every algorithm the same services — parallel
    /// evaluation, caching, budget, cancellation, the best solution and the history — so a new algorithm only writes
    /// its search logic. Plug one in by implementing this interface.
    /// </summary>
    public interface IOptimizationAlgorithm
    {
        string Name { get; }

        /// <summary>Searches until <see cref="OptimizationRun.ShouldStop"/> (or its own end), calling
        /// <see cref="OptimizationRun.EndIteration"/> after each iteration.</summary>
        void Run(OptimizationRun run);
    }

    /// <summary>Settings common to every algorithm.</summary>
    public sealed class OptimizationSettings
    {
        /// <summary>Stop after this many evaluations (0 = no limit; the algorithm's own end).</summary>
        public int MaxEvaluations { get; set; }
        /// <summary>Stop after this wall-clock time in seconds (0 = no limit).</summary>
        public double MaxSeconds { get; set; }
        /// <summary>Candidates evaluated at once (1 = sequential).</summary>
        public int Parallelism { get; set; } = 1;
        /// <summary>Seed of the algorithm's random decisions (same seed + same objective = same search).</summary>
        public int Seed { get; set; } = 1;
    }

    /// <summary>One point of the history: after an iteration.</summary>
    public struct ProgressPoint
    {
        public int Iteration;
        public int Evaluations;
        public double BestFitness;
        public double Seconds;
    }

    /// <summary>Progress reported after every iteration.</summary>
    public sealed class OptimizationProgress
    {
        public int Iteration { get; internal set; }
        public int Evaluations { get; internal set; }
        public double Seconds { get; internal set; }
        public Candidate Best { get; internal set; }
        public Evaluation BestEvaluation { get; internal set; }
    }

    /// <summary>One simulated candidate of an optimisation: its iteration, the value of each decision variable (as text),
    /// its evaluation (fitness, metric means and per-replication values, or the error).</summary>
    public sealed class EvaluatedCandidate
    {
        public int Iteration { get; set; }
        public List<KeyValuePair<string, string>> Values { get; } = new List<KeyValuePair<string, string>>();
        public Evaluation Evaluation { get; set; }
    }

    public sealed class OptimizationResult
    {
        public string Algorithm { get; internal set; }
        public Candidate Best { get; internal set; }
        public Evaluation BestEvaluation { get; internal set; }
        public int Evaluations { get; internal set; }
        public double Seconds { get; internal set; }
        public string StopReason { get; internal set; }
        public List<ProgressPoint> History { get; } = new List<ProgressPoint>();
        /// <summary>Every candidate simulated, in evaluation order (kept in the results store, as OptQuest /
        /// FlexSim keep them) — cache hits are not repeated.</summary>
        public List<EvaluatedCandidate> Candidates { get; } = new List<EvaluatedCandidate>();

        /// <summary>The candidates as result-set rows (<see cref="SimuLean.Results.ResultSet.Candidates"/>).</summary>
        public void CopyCandidatesTo(SimuLean.Results.ResultSet set)
        {
            foreach (var c in Candidates)
            {
                var row = new SimuLean.Results.ResultCandidate { Iteration = c.Iteration, Fitness = c.Evaluation?.Fitness ?? double.NaN, Error = c.Evaluation?.Error };
                row.Parameters.AddRange(c.Values);
                if (c.Evaluation != null) foreach (var kv in c.Evaluation.Metrics) row.Metrics[kv.Key] = kv.Value;
                set.Candidates.Add(row);
            }
        }
    }

    /// <summary>
    /// The services an algorithm uses during one optimisation: the space, a seeded random engine, evaluation of
    /// candidates (in parallel, cached — a candidate seen before is not simulated again —, counted against the budget),
    /// the stop conditions, the best candidate so far and the progress history.
    /// </summary>
    public sealed class OptimizationRun
    {
        private readonly IObjective objective;
        private readonly Dictionary<string, Evaluation> cache = new Dictionary<string, Evaluation>();
        private readonly object sync = new object();
        private readonly Stopwatch watch = new Stopwatch();
        private readonly Action<OptimizationProgress> progress;
        private readonly CancellationToken cancellation;
        private int iteration;

        public SearchSpace Space { get; }
        public OptimizationSettings Settings { get; }
        /// <summary>The algorithm's random engine (seeded with <see cref="OptimizationSettings.Seed"/>).</summary>
        public IRandomEngine Random { get; }
        public int Evaluations { get; private set; }
        public Candidate Best { get; private set; }
        public Evaluation BestEvaluation { get; private set; }
        public OptimizationResult Result { get; } = new OptimizationResult();

        internal OptimizationRun(SearchSpace space, IObjective objective, OptimizationSettings settings,
                                 Action<OptimizationProgress> progress, CancellationToken cancellation)
        {
            Space = space; this.objective = objective; Settings = settings;
            this.progress = progress; this.cancellation = cancellation;
            Random = new DefaultRandomEngine(settings.Seed);
            watch.Start();
        }

        public double Seconds => watch.Elapsed.TotalSeconds;

        /// <summary>True when the budget (evaluations, time) is spent or the run was cancelled.</summary>
        public bool ShouldStop => StopReason() != null;

        /// <summary>Evaluations still allowed (int.MaxValue without a limit).</summary>
        public int RemainingEvaluations => Settings.MaxEvaluations > 0 ? Math.Max(0, Settings.MaxEvaluations - Evaluations) : int.MaxValue;

        /// <summary>
        /// Evaluates the candidates (in parallel up to <see cref="OptimizationSettings.Parallelism"/>); candidates already
        /// evaluated come from the cache. Beyond the evaluation budget the remaining ones are not simulated (null).
        /// </summary>
        public IReadOnlyList<Evaluation> Evaluate(IReadOnlyList<Candidate> candidates)
        {
            var results = new Evaluation[candidates.Count];
            var todo = new List<int>();
            var firstOfKey = new Dictionary<string, int>();
            lock (sync)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    string k = candidates[i].Key;
                    if (cache.TryGetValue(k, out Evaluation e)) results[i] = e;
                    else if (!firstOfKey.ContainsKey(k) && todo.Count < RemainingEvaluations && !cancellation.IsCancellationRequested)
                    {
                        firstOfKey[k] = i;
                        todo.Add(i);
                    }
                }
            }

            Action<int> one = i =>
            {
                // the time limit and a cancellation stop the batch too (they were checked only between batches: a Stop
                // waited for the whole generation); a candidate not evaluated stays null
                if (ShouldStop) return;
                Evaluation e;
                try { e = objective.Evaluate(candidates[i]) ?? Evaluation.Failed("no evaluation"); }
                catch (Exception ex) { e = Evaluation.Failed(ex.GetType().Name + ": " + ex.Message); }
                // no fitness (NaN: e.g. a makespan not reached) is the worst, never the best: x > NaN is always false,
                // so a NaN first evaluation could never be replaced
                if (double.IsNaN(e.Fitness))
                {
                    e.Fitness = double.NegativeInfinity;
                    if (e.Error == null) e.Error = "the fitness is not a number (a metric was not reached?)";
                }
                results[i] = e;
            };
            if (Settings.Parallelism > 1 && todo.Count > 1)
                Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Settings.Parallelism }, one);
            else
                foreach (int i in todo) one(i);

            lock (sync)
            {
                foreach (int i in todo)
                {
                    if (results[i] == null) continue;   // not evaluated (stopped): neither cached nor counted
                    cache[candidates[i].Key] = results[i];
                    Evaluations++;
                    var record = new EvaluatedCandidate { Iteration = iteration + 1, Evaluation = results[i] };
                    for (int v = 0; v < Space.Count; v++) record.Values.Add(new KeyValuePair<string, string>(Space.Variables[v].Name, candidates[i].Text(v)));
                    Result.Candidates.Add(record);
                    if (BestEvaluation == null || results[i].Fitness > BestEvaluation.Fitness)
                    {
                        BestEvaluation = results[i];
                        Best = candidates[i].Clone();
                    }
                }
                // duplicates inside the batch share the first one's evaluation
                for (int i = 0; i < candidates.Count; i++)
                    if (results[i] == null && firstOfKey.TryGetValue(candidates[i].Key, out int f)) results[i] = results[f];
            }
            return results;
        }

        public Evaluation Evaluate(Candidate candidate) => Evaluate(new[] { candidate })[0];

        /// <summary>Records the end of an iteration (history point) and reports progress.</summary>
        public void EndIteration()
        {
            iteration++;
            var point = new ProgressPoint { Iteration = iteration, Evaluations = Evaluations, BestFitness = BestEvaluation?.Fitness ?? double.NegativeInfinity, Seconds = Seconds };
            Result.History.Add(point);
            progress?.Invoke(new OptimizationProgress
            {
                Iteration = iteration, Evaluations = Evaluations, Seconds = point.Seconds, Best = Best, BestEvaluation = BestEvaluation,
            });
        }

        // ── helpers for algorithms ────────────────────────────────────────────

        /// <summary>A uniformly random candidate.</summary>
        public Candidate RandomCandidate()
        {
            var c = new Candidate(Space);
            for (int i = 0; i < Space.Count; i++)
            {
                var v = Space.Variables[i];
                switch (v.Kind)
                {
                    case VariableKind.Integer:
                    case VariableKind.Choice:
                        if (v.Length == 1) c.SetNumber(i, RandomInt((int)v.Min, (int)v.Max));
                        else { var x = new double[v.Length]; for (int k = 0; k < x.Length; k++) x[k] = RandomInt((int)v.Min, (int)v.Max); c.SetValues(i, x); }
                        break;
                    case VariableKind.Real:
                        if (v.Length == 1) c.SetNumber(i, v.Min + Random.NextDouble() * (v.Max - v.Min));
                        else { var x = new double[v.Length]; for (int k = 0; k < x.Length; k++) x[k] = v.Min + Random.NextDouble() * (v.Max - v.Min); c.SetValues(i, x); }
                        break;
                    case VariableKind.Permutation: c.SetVector(i, RandomPermutation(v.Length)); break;
                    case VariableKind.Binary:
                        var bits = new int[v.Length];
                        for (int k = 0; k < bits.Length; k++) bits[k] = Random.NextDouble() < 0.5 ? 1 : 0;
                        c.SetVector(i, bits);
                        break;
                }
            }
            return c;
        }

        /// <summary>A random whole number in [min, max] (both included).</summary>
        public int RandomInt(int min, int max) => min >= max ? min : Random.NextInt(min, max + 1);

        public int[] RandomPermutation(int n)
        {
            var p = new int[n];
            for (int i = 0; i < n; i++) p[i] = i;
            for (int i = n - 1; i > 0; i--) { int j = RandomInt(0, i); int t = p[i]; p[i] = p[j]; p[j] = t; }
            return p;
        }

        internal string StopReason()
        {
            if (cancellation.IsCancellationRequested) return "cancelled";
            if (Settings.MaxEvaluations > 0 && Evaluations >= Settings.MaxEvaluations) return "evaluation budget";
            if (Settings.MaxSeconds > 0 && Seconds >= Settings.MaxSeconds) return "time limit";
            return null;
        }
    }

    /// <summary>Entry point: runs an algorithm on a problem.</summary>
    public static class OptimizationEngine
    {
        public static OptimizationResult Run(SearchSpace space, IObjective objective, IOptimizationAlgorithm algorithm,
                                             OptimizationSettings settings = null, Action<OptimizationProgress> progress = null,
                                             CancellationToken cancellation = default)
        {
            if (space == null || space.Count == 0) throw new ArgumentException("The search space has no decision variables.");
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            if (algorithm == null) throw new ArgumentNullException(nameof(algorithm));
            var run = new OptimizationRun(space, objective, settings ?? new OptimizationSettings(), progress, cancellation);
            algorithm.Run(run);
            var r = run.Result;
            r.Algorithm = algorithm.Name;
            r.Best = run.Best;
            r.BestEvaluation = run.BestEvaluation;
            r.Evaluations = run.Evaluations;
            r.Seconds = run.Seconds;
            r.StopReason = run.StopReason() ?? "finished";
            return r;
        }
    }
}
