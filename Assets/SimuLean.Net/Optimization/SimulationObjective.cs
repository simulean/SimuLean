using System;
using System.Collections.Generic;
using SimuLean.Expressions;
using SimuLean.Headless;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Optimization
{
    /// <summary>Where an observed value comes from.</summary>
    public enum MetricScope { Element, Team, List, Model, Reaction }

    /// <summary>
    /// An observed value of a run: a statistic of an element, a resource team or a list, by name (see
    /// <see cref="MetricCatalog"/>). <see cref="Name"/> is how the fitness expression refers to it.
    /// </summary>
    public sealed class ModelMetric
    {
        public string Name { get; set; }
        public MetricScope Scope { get; set; }
        public string Target { get; set; }
        public string Statistic { get; set; }

        public ModelMetric() { }
        public ModelMetric(string name, MetricScope scope, string target, string statistic)
        {
            Name = name; Scope = scope; Target = target; Statistic = statistic;
        }
    }

    /// <summary>The statistics a run offers (<see cref="ResultMetrics"/>), and how a metric is read from a run's results.</summary>
    public static class MetricCatalog
    {
        /// <summary>Element statistics (plus <c>state:NAME</c>, the share of time in a state).</summary>
        public static IReadOnlyList<string> ElementStatistics => ResultMetrics.Element;
        public static IReadOnlyList<string> TeamStatistics => ResultMetrics.Team;
        /// <summary>List statistics (plus <c>field:F:push_avg</c> / <c>pull_avg</c> / <c>pull_max</c>).</summary>
        public static IReadOnlyList<string> ListStatistics => ResultMetrics.List;
        /// <summary>The model's statistics: end time, makespan, deadlocked, … — no target.</summary>
        public static IReadOnlyList<string> ModelStatistics => ResultMetrics.Model;
        /// <summary>A reaction's statistics: fired, not_met.</summary>
        public static IReadOnlyList<string> ReactionStatistics => ResultMetrics.Reaction;

        /// <summary>Reads a metric of a finished run; <paramref name="model"/> is only needed for <c>member:</c> metrics.</summary>
        public static double Read(ModelMetric m, RunResults results, HeadlessModelFactory model = null)
        {
            var scope = ScopeOf(m);
            string kind = KindOf(m);
            ResultObject o;
            try { o = results.FindUnique(scope, scope == ResultScope.Model ? "model" : m.Target); }
            catch (ArgumentException e) { throw new ArgumentException($"Metric '{m.Name}': {e.Message}"); }
            if (o == null) throw new ArgumentException($"Metric '{m.Name}': no {kind} '{m.Target}'.");
            string statistic = m.Statistic ?? "";
            if (scope == ResultScope.Element && statistic.StartsWith("member:", StringComparison.Ordinal))
            {
                Element e = null;
                if (model != null) foreach (var kv in model.GetAllElements()) if (kv.Key == o.Id) { e = kv.Value; break; }
                if (e == null) throw new ArgumentException($"Metric '{m.Name}': '{statistic}' needs the run model.");
                return Member(e, statistic.Substring(7), m.Name);
            }
            if (o.TryGet(statistic, out double v)) return v;
            // a model time not reached (no makespan: nothing reached a sink; no deadlock / empty time) is NaN
            if (scope == ResultScope.Model && System.Linq.Enumerable.Contains(ResultMetrics.Model, statistic)) return double.NaN;
            // A state never entered or a field never recorded is 0; anything else is not a statistic of the scope.
            if ((ResultMetrics.IsState(statistic) && scope == ResultScope.Element) || (ResultMetrics.IsField(statistic) && scope == ResultScope.List)) return 0;
            throw new ArgumentException($"Metric '{m.Name}': unknown statistic '{statistic}' for a {kind}.");
        }

        private static ResultScope ScopeOf(ModelMetric m)
            => m.Scope == MetricScope.Team ? ResultScope.Team : m.Scope == MetricScope.List ? ResultScope.List
             : m.Scope == MetricScope.Model ? ResultScope.Model : m.Scope == MetricScope.Reaction ? ResultScope.Reaction
             : ResultScope.Element;

        private static string KindOf(ModelMetric m) => m.Scope == MetricScope.Team ? "resource team" : m.Scope.ToString().ToLowerInvariant();

        /// <summary>
        /// Why a metric cannot be read from a model's results (null = it can), checked on a built model before the runs
        ///: an unknown or ambiguous target, an unknown statistic, or a state no element has — which a run reads
        /// as 0, indistinguishable from a state never entered. Each problem names the closest options ("did you mean").
        /// </summary>
        public static string Problem(ModelMetric m, RunResults results, HeadlessModelFactory model = null)
        {
            var scope = ScopeOf(m);
            string kind = KindOf(m), statistic = m.Statistic ?? "";
            ResultObject o;
            try { o = results.FindUnique(scope, scope == ResultScope.Model ? "model" : m.Target); }
            catch (ArgumentException e) { return $"Metric '{m.Name}': {e.Message}"; }
            if (o == null)
            {
                var names = new List<string>();
                foreach (var x in results.Of(scope)) names.Add(x.Name);
                return $"Metric '{m.Name}': no {kind} '{m.Target}' in the model ({kind}s: " +
                       (names.Count == 0 ? "none" : string.Join(", ", names)) + ")." + MemberHint(m, results) + NameSuggestions.DidYouMean(m.Target, names);
            }
            if (scope == ResultScope.Element && ResultMetrics.IsState(statistic))
            {
                string state = statistic.Substring(ResultMetrics.StatePrefix.Length);
                return ElementState.IsKnown(state) ? null
                     : $"Metric '{m.Name}': no state '{state}' (states: {string.Join(", ", Sorted(ElementState.KnownNames))})." +
                       NameSuggestions.DidYouMean(state, ElementState.KnownNames);
            }
            // tallies and list fields appear with the first value recorded: not checkable before the run
            if ((scope == ResultScope.Element && ResultMetrics.IsTally(statistic)) || (scope == ResultScope.List && ResultMetrics.IsField(statistic)))
                return null;
            try { Read(m, results, model); return null; }
            catch (ArgumentException e)
            {
                var known = new List<string>(ResultMetrics.Of(scope));
                foreach (var kv in o.Metrics) if (!known.Contains(kv.Key)) known.Add(kv.Key);
                return e.Message + $" Statistics of a {kind}: {string.Join(", ", known)}." + NameSuggestions.DidYouMean(statistic, known);
            }
        }

        /// <summary>" 'X' is a member of team 'T': …" when the target is a resource (a team member), not a team or an element:
        /// metrics read teams (<c>team:T.utilization</c>), not single members.</summary>
        private static string MemberHint(ModelMetric m, RunResults results)
        {
            foreach (var r in results.Of(ResultScope.Resource))
                if (string.Equals(r.Name, m.Target, StringComparison.OrdinalIgnoreCase))
                    return MemberHint(r.Name, r.Group, m.Statistic);
            return "";
        }

        /// <summary>The sentence of <see cref="MemberHint(ModelMetric, RunResults)"/> for a member of <paramref name="team"/>.</summary>
        public static string MemberHint(string member, string team, string statistic)
            => $" '{member}' is a member of team '{team}': a metric reads the team, e.g. " +
               $"'team:{team}.{(string.IsNullOrEmpty(statistic) ? ResultMetrics.Utilization : statistic)}' " +
               $"(team statistics: {string.Join(", ", ResultMetrics.Team)}).";

        private static List<string> Sorted(IEnumerable<string> names)
        {
            var l = new List<string>(names);
            l.Sort(StringComparer.Ordinal);
            return l;
        }

        /// <summary>
        /// <c>member:Name</c> — a public number of the element's engine class that the catalog does not list (a property or
        /// a method without parameters, e.g. <c>member:GetMeanServiceTime</c>, <c>member:CompletedTaskCount</c>).
        /// </summary>
        private static double Member(Element e, string name, string metric)
        {
            var type = e.GetType();
            var property = type.GetProperty(name);
            if (property != null && property.GetIndexParameters().Length == 0) return Convert.ToDouble(property.GetValue(e));
            var method = type.GetMethod(name, Type.EmptyTypes);
            if (method != null && method.ReturnType != typeof(void)) return Convert.ToDouble(method.Invoke(e, null));
            throw new ArgumentException($"Metric '{metric}': {type.Name} has no public number '{name}'.");
        }
    }

    /// <summary>
    /// The objective of a simulation optimisation: each candidate is applied to a copy of the model config
    /// (<see cref="ModelParameter"/>s), the model is built and run headless — <see cref="Replications"/> times, with the
    /// seeds <see cref="BaseSeed"/>, +1, … (the same for every candidate: common random numbers) — the
    /// <see cref="Metrics"/> are averaged and the <see cref="Fitness"/> expression (the common syntax; metric names as
    /// variables, and the numeric parameters by name — e.g. <c>throughput - 50 * operators</c> with a team-size parameter
    /// <c>operators</c>) is maximised. Thread-safe.
    /// </summary>
    public sealed class SimulationObjective : IObjective
    {
        private readonly SimulationConfig template;
        private readonly Expression fitness;

        public IReadOnlyList<ModelParameter> Parameters { get; }
        public IReadOnlyList<ModelMetric> Metrics { get; }
        public int Replications { get; set; } = 1;
        public int BaseSeed { get; set; }
        /// <summary>Simulated time of each run (0 = the config's MaxSimulationTime).</summary>
        public double RunTime { get; set; }
        /// <summary>Interval of the time series recorded in the runs handed to an inspector (<see cref="Run"/>,
        /// <see cref="Replicate"/>; <see cref="SeriesRecorder"/>); 0 = none. Plain evaluations never record them.</summary>
        public double SeriesInterval { get; set; }
        /// <summary>The inspected runs also keep their state intervals (<see cref="RunResults.Timeline"/>, the Gantt; G3c).</summary>
        public bool RecordTimeline { get; set; }

        /// <summary>The same problem (model, parameters, metrics and run settings) with another fitness expression.</summary>
        public SimulationObjective WithFitness(string fitnessExpression)
            => new SimulationObjective(template, Parameters, Metrics, fitnessExpression)
            {
                Replications = Replications, BaseSeed = BaseSeed, RunTime = RunTime,
                SeriesInterval = SeriesInterval, RecordTimeline = RecordTimeline,
            };

        public SimulationObjective(SimulationConfig model, IEnumerable<ModelParameter> parameters,
                                   IEnumerable<ModelMetric> metrics, string fitnessExpression)
        {
            template = model?.Clone() ?? throw new ArgumentNullException(nameof(model));
            Parameters = new List<ModelParameter>(parameters ?? throw new ArgumentNullException(nameof(parameters)));
            Metrics = new List<ModelMetric>(metrics ?? throw new ArgumentNullException(nameof(metrics)));
            var names = new HashSet<string>();
            foreach (var m in Metrics)
            {
                if (string.IsNullOrEmpty(m.Name) || !IsIdentifier(m.Name))
                    throw new ArgumentException($"Metric name '{m.Name}': letters, digits and _ only (it is a variable of the fitness).");
                if (!names.Add(m.Name)) throw new ArgumentException($"Metric '{m.Name}' is defined twice.");
            }
            foreach (var p in Parameters)
            {
                var v = p.Variable;
                if (v.IsNumber && IsIdentifier(v.Name) && !names.Add(v.Name))
                    throw new ArgumentException($"'{v.Name}' names both a metric and a parameter.");
            }
            fitness = Expression.Parse(string.IsNullOrWhiteSpace(fitnessExpression) ? "0" : fitnessExpression);
            foreach (string r in fitness.References)
                if (!names.Contains(r)) throw new ArgumentException($"Fitness: '{r}' is not a metric or a numeric parameter.");
            BaseSeed = template.UseFixedSeed ? template.Seed : 1;
        }

        /// <summary>The decision variables, in parameter order (the candidate's variable i is parameter i).</summary>
        public SearchSpace BuildSpace()
        {
            var space = new SearchSpace();
            foreach (var p in Parameters) space.Add(p.Variable);
            return space;
        }

        /// <summary>The model config of a candidate (what the evaluation runs).</summary>
        public SimulationConfig ConfigFor(Candidate candidate)
        {
            var config = template.Clone();
            for (int i = 0; i < Parameters.Count; i++) Parameters[i].Apply(config, candidate, i);
            return config;
        }

        public Evaluation Evaluate(Candidate candidate)
        {
            var seeds = new int[Math.Max(1, Replications)];
            for (int r = 0; r < seeds.Length; r++) seeds[r] = BaseSeed + r;
            return Measure(candidate, seeds, null);
        }

        /// <summary>
        /// One run of a candidate with the given model seed (a replication of an experiment): its metrics and fitness;
        /// <paramref name="inspect"/> gets the run's statistics (<see cref="RunResults"/>).
        /// </summary>
        public Evaluation Run(Candidate candidate, int seed, Action<RunResults> inspect = null)
            => Measure(candidate, new[] { seed }, inspect);

        /// <summary>The evaluation of a candidate (every replication, as <see cref="Evaluate"/>) with the statistics of each
        /// replication, in seed order — e.g. to summarise the best solution (<see cref="ResultSummary"/>).</summary>
        public Evaluation Replicate(Candidate candidate, List<RunResults> runs)
        {
            var seeds = new int[Math.Max(1, Replications)];
            for (int r = 0; r < seeds.Length; r++) seeds[r] = BaseSeed + r;
            return Measure(candidate, seeds, runs != null ? runs.Add : (Action<RunResults>)null);
        }

        private Evaluation Measure(Candidate candidate, int[] seeds, Action<RunResults> inspect)
        {
            var config = ConfigFor(candidate);
            double time = RunTime > 0 ? RunTime : config.MaxSimulationTime > 0 ? config.MaxSimulationTime : 100.0;
            var values = new List<double>[Metrics.Count];
            for (int k = 0; k < values.Length; k++) values[k] = new List<double>(seeds.Length);
            for (int r = 0; r < seeds.Length; r++)
            {
                var clock = new SimClock(seeds[r]);
                var model = new HeadlessModelFactory(clock) { KeepStateLogs = inspect != null && RecordTimeline };
                model.BuildModel(r == seeds.Length - 1 ? config : config.Clone());   // building may keep references into the config
                if (inspect != null && RecordTimeline) JobRecorder.Attach(model);   // the job Gantt (G3d)
                model.StartModel();
                var recorder = inspect != null && SeriesInterval > 0 ? new SeriesRecorder(model, SeriesInterval) : null;
                if (recorder != null) recorder.RunTo(time);
                else clock.AdvanceClock(time);
                if (Metrics.Count == 0 && inspect == null) continue;
                var results = RunResults.Capture(model, null, inspect != null && RecordTimeline);
                recorder?.AddTo(results);
                for (int k = 0; k < Metrics.Count; k++) values[k].Add(MetricCatalog.Read(Metrics[k], results, model));
                inspect?.Invoke(results);
            }
            var e = new Evaluation();
            for (int k = 0; k < Metrics.Count; k++)
            {
                double sum = 0;
                foreach (double v in values[k]) sum += v;
                e.Metrics[Metrics[k].Name] = sum / seeds.Length;
                e.MetricValues[Metrics[k].Name] = values[k];
            }
            var variables = new Dictionary<string, double>(e.Metrics);
            for (int i = 0; i < Parameters.Count; i++)
                if (Parameters[i].Variable.IsNumber) variables[Parameters[i].Variable.Name] = candidate.Number(i);
            e.Fitness = fitness.Number(new MetricScope(variables));
            return e;
        }

        private static bool IsIdentifier(string s)
        {
            if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (char ch in s) if (!(char.IsLetterOrDigit(ch) || ch == '_')) return false;
            return true;
        }

        private readonly struct MetricScope : IExpressionScope
        {
            private readonly Dictionary<string, double> values;
            public MetricScope(Dictionary<string, double> values) { this.values = values; }
            public double Now => 0;
            public ExprValue Get(string root, string name)
                => root == null && values.TryGetValue(name, out double v) ? ExprValue.Of(v) : ExprValue.None;
        }
    }
}
