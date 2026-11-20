using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean.Expressions;
using SimuLean.Headless;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Optimization
{
    /// <summary>Where an observed value comes from.</summary>
    public enum MetricScope { Element, Team, List, Model, Reaction, Downtime }

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
        /// <summary>A downtime's statistics (<c>downtime:Id.stops</c>): stops, downtime_total (time stopped),
        /// downtime_scheduled (stop time as scheduled); with a repair crew also repairs, repair_waits, repair_wait_avg,
        /// repair_time_avg.</summary>
        public static readonly IReadOnlyList<string> DowntimeStatistics = new[]
            { ResultMetrics.Stops, ResultMetrics.DowntimeTotal, ResultMetrics.DowntimeScheduled, ResultMetrics.Repairs, ResultMetrics.RepairWaits, ResultMetrics.RepairWaitAvg, ResultMetrics.RepairTimeAvg };

        // Preemption statistics appear only where some use of the team has a preemption mode; elsewhere nothing was
        // ever interrupted, and they read 0 (a study may switch the mode on and off between its scenarios).
        private static readonly string[] TeamPreemption = { ResultMetrics.Preemptions, ResultMetrics.Interruptions };

        /// <summary>Every statistic a team metric (<c>team:T.statistic</c>) reads: <see cref="ResultMetrics.Team"/> and the
        /// preemption counts (0 without a preemption mode); <c>state:NAME</c> comes on top.</summary>
        public static IReadOnlyList<string> TeamMetricStatistics => System.Linq.Enumerable.ToList(System.Linq.Enumerable.Concat(ResultMetrics.Team, TeamPreemption));
        private static readonly string[] ElementPreemption = { ResultMetrics.Interruptions, ResultMetrics.InterruptedTime, ResultMetrics.InterruptedTimeAvg,
                                                               // setups: a station without a setup rule set up 0 times
                                                               // (a study may switch setup.when between its scenarios)
                                                               ResultMetrics.Setups, ResultMetrics.SetupTime, ResultMetrics.SetupTimeAvg,
                                                               // a source that does not lose its arrivals lost none (arrivalsOnStop may vary too)
                                                               ResultMetrics.LostArrivals,
                                                               // a wait without a max wait timed out 0 times (a study may vary maxWait
                                                               // to 0 = none)
                                                               ResultMetrics.Timeouts, ResultMetrics.TimeoutsOf(WaitKind.Resource),
                                                               ResultMetrics.TimeoutsOf(WaitKind.Queue), ResultMetrics.TimeoutsOf(WaitKind.ListPull) };
        private static readonly string[] Repair = { ResultMetrics.Repairs, ResultMetrics.RepairWaits, ResultMetrics.RepairWaitAvg, ResultMetrics.RepairTimeAvg };

        /// <summary>Reads a metric of a finished run; <paramref name="model"/> is only needed for <c>member:</c> metrics.</summary>
        public static double Read(ModelMetric m, RunResults results, HeadlessModelFactory model = null)
        {
            var scope = ScopeOf(m);
            string kind = KindOf(m);
            ResultObject o;
            try { o = results.FindUnique(scope, scope == ResultScope.Model ? "model" : m.Target); }
            catch (ArgumentException e) { throw new ArgumentException($"Metric '{m.Name}': {e.Message}", e); }
            if (o == null && scope == ResultScope.Downtime && TargetsOf(m.Target, results).Count > 0) return Aggregate(m, TargetsOf(m.Target, results));
            if (o == null) throw new ArgumentException($"Metric '{m.Name}': no {kind} '{m.Target}'.");
            string statistic = m.Statistic ?? "";
            if (scope == ResultScope.Element && statistic.StartsWith("member:", StringComparison.Ordinal))
            {
                Element e = ElementOf(o, model) ?? throw new ArgumentException($"Metric '{m.Name}': '{statistic}' needs the run model.");
                return Member(e, statistic.Substring(7), m.Name);
            }
            if (scope == ResultScope.Element && ResultMetrics.IsProperty(statistic))
            {
                Element e = ElementOf(o, model) ?? throw new ArgumentException($"Metric '{m.Name}': '{statistic}' needs the run model.");
                return PropertyValue(e, statistic.Substring(ResultMetrics.PropertyPrefix.Length).Trim(), m.Name);
            }
            if (o.TryGet(statistic, out double v)) return v;
            // a tally the element defines that got no value in this run (no item, a label never set): count 0, the
            // others NaN — not an unknown statistic, which failed the whole replication and lost its other metrics
            if (scope == ResultScope.Element && ResultMetrics.IsTally(statistic) && ElementOf(o, model) is Element te
                && TryParseTally(statistic, out string tally, out string tallyStat) && TallyNames(te).Contains(tally)
                && System.Linq.Enumerable.Contains(ResultMetrics.TallyStatistics, tallyStat))
                return tallyStat == ResultMetrics.TallyCount ? 0 : double.NaN;
            // a model time not reached (no makespan: nothing reached a sink; no deadlock / empty time) is NaN
            if (scope == ResultScope.Model && System.Linq.Enumerable.Contains(ResultMetrics.Model, statistic)) return double.NaN;
            // A state never entered or a field never recorded is 0; anything else is not a statistic of the scope.
            if ((ResultMetrics.IsState(statistic) && (scope == ResultScope.Element || scope == ResultScope.Team)) || (ResultMetrics.IsField(statistic) && scope == ResultScope.List)) return 0;
            if ((scope == ResultScope.Team && Array.IndexOf(TeamPreemption, statistic) >= 0)
                || (scope == ResultScope.Element && Array.IndexOf(ElementPreemption, statistic) >= 0)) return 0;
            if (scope == ResultScope.Downtime && Array.IndexOf(Repair, statistic) >= 0)
                throw new ArgumentException($"Metric '{m.Name}': '{statistic}' is a statistic of a downtime repaired by a crew, and " +
                                            $"downtime '{m.Target}' has none (Repair by: a team).");
            throw new ArgumentException($"Metric '{m.Name}': unknown statistic '{statistic}' for a {kind}." + OwnerHint(scope, statistic));
        }

        // a statistic only some element types keep: which, and what to use elsewhere
        private static string OwnerHint(ResultScope scope, string statistic)
        {
            if (scope != ResultScope.Element) return "";
            if (System.Linq.Enumerable.Contains(ResultMetrics.QueueStay, statistic))
                return $" '{statistic}' is a Queue's (the only element that keeps its stays); elsewhere label the entry (entryLabels = tin = now()) and tally on the next element (tallies = stay = now() - item.tin), read as tally:stay:p90.";
            if (System.Linq.Enumerable.Contains(ResultMetrics.TimeInSystem, statistic))
                return $" '{statistic}' is a Sink's (the time in system of the items it receives); elsewhere a tally tis = age, read as tally:tis:mean.";
            return "";
        }

        /// <summary>The objects of a downtime that runs on several targets (one generator each: <c>Id@target</c>).</summary>
        private static List<ResultObject> TargetsOf(string downtime, RunResults results)
        {
            var each = new List<ResultObject>();
            if (string.IsNullOrEmpty(downtime) || downtime.Contains("@")) return each;
            foreach (var x in results.Of(ResultScope.Downtime))
                if (x.Id != null && x.Id.StartsWith(downtime + "@", StringComparison.Ordinal)) each.Add(x);
            return each;
        }

        /// <summary>
        /// A downtime's statistic over all its targets (<c>downtime:Id.stat</c> without <c>@target</c>): stops,
        /// downtime_total, downtime_scheduled, repairs and repair_waits summed; repair_wait_avg the mean over every crew arrived (each target's
        /// mean weighted by its repair_waits) and repair_time_avg over every repair completed (weighted by its repairs) —
        /// each mean by its own count, so a repair still open at the end counts in the wait and not in the time; NaN when
        /// that count is 0 on every target.
        /// </summary>
        private static double Aggregate(ModelMetric m, List<ResultObject> each)
        {
            string statistic = m.Statistic ?? "";
            bool average = statistic == ResultMetrics.RepairWaitAvg || statistic == ResultMetrics.RepairTimeAvg;
            if (!System.Linq.Enumerable.Contains(DowntimeStatistics, statistic))
                throw new ArgumentException($"Metric '{m.Name}': unknown statistic '{statistic}' for a downtime.");
            double sum = 0, weight = 0;
            foreach (var o in each)
            {
                if (!o.TryGet(statistic, out double v))
                {
                    if (Array.IndexOf(Repair, statistic) >= 0)
                        throw new ArgumentException($"Metric '{m.Name}': '{statistic}' is a statistic of a downtime repaired by a crew, and " +
                                                    $"downtime '{m.Target}' has none (Repair by: a team).");
                    continue;
                }
                if (!average) { sum += v; continue; }
                double n = o.TryGet(statistic == ResultMetrics.RepairWaitAvg ? ResultMetrics.RepairWaits : ResultMetrics.Repairs, out double r) ? r : 0;
                if (n > 0 && !double.IsNaN(v)) { sum += v * n; weight += n; }
            }
            return average ? (weight > 0 ? sum / weight : double.NaN) : sum;
        }

        private static ResultScope ScopeOf(ModelMetric m)
            => m.Scope == MetricScope.Team ? ResultScope.Team : m.Scope == MetricScope.List ? ResultScope.List
             : m.Scope == MetricScope.Model ? ResultScope.Model : m.Scope == MetricScope.Reaction ? ResultScope.Reaction
             : m.Scope == MetricScope.Downtime ? ResultScope.Downtime : ResultScope.Element;

        /// <summary>The results scope a metric scope reads.</summary>
        public static ResultScope ResultScopeOf(MetricScope scope) => ScopeOf(new ModelMetric { Scope = scope });

        private static string KindOf(ModelMetric m) => m.Scope == MetricScope.Team ? "resource team" : m.Scope.ToString().ToLowerInvariant();

        /// <summary>
        /// Why a metric cannot be read from a model's results (null = it can), checked on a built model before the runs
        /// an unknown or ambiguous target, an unknown statistic, or a state no element has — which a run reads
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
                // downtimes by their config id (a downtime's name is "id:target"), the other scopes by name
                foreach (var x in results.Of(scope)) names.Add(scope == ResultScope.Downtime ? x.Id : x.Name);
                // a downtime on several targets (elements, a team's members): one object each, read together (Aggregate)
                if (scope == ResultScope.Downtime && TargetsOf(m.Target, results).Count > 0)
                {
                    try { Read(m, results, model); return null; }
                    catch (ArgumentException e) { return e.Message + $" Statistics of a downtime: {string.Join(", ", DowntimeStatistics)}." + NameSuggestions.DidYouMean(statistic, DowntimeStatistics); }
                }
                return $"Metric '{m.Name}': no {kind} '{m.Target}' in the model ({kind}s: " +
                       (names.Count == 0 ? "none" : string.Join(", ", names)) + ")." + MemberHint(m, results) + ScopeHint(m, results) +
                       NameSuggestions.DidYouMean(m.Target, names);
            }
            if ((scope == ResultScope.Element || scope == ResultScope.Team || scope == ResultScope.Resource) && ResultMetrics.IsState(statistic))
            {
                // a team's / member's states too (a misspelt one read 0); the resource states are registered
                System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(SimuLean.Resources.ResourceStates).TypeHandle);
                string state = statistic.Substring(ResultMetrics.StatePrefix.Length);
                return ElementState.IsKnown(state) ? null
                     : $"Metric '{m.Name}': no state '{state}' (states: {string.Join(", ", Sorted(ElementState.KnownNames))})." +
                       NameSuggestions.DidYouMean(state, ElementState.KnownNames);
            }
            // an element's property: it exists when the model is built, or a reaction writes it
            if (scope == ResultScope.Element && ResultMetrics.IsProperty(statistic))
                return PropertyProblem(m, o, model);
            // an element's tally: its name is known once the model is built (its values only after the run)
            if (scope == ResultScope.Element && ResultMetrics.IsTally(statistic) && ElementOf(o, model) is Element te)
                return TallyProblem(m, te, statistic);
            // tallies without the model and list fields appear with the first value recorded: not checkable before the run
            if ((scope == ResultScope.Element && ResultMetrics.IsTally(statistic)) || (scope == ResultScope.List && ResultMetrics.IsField(statistic)))
                return null;
            try { Read(m, results, model); return null; }
            catch (ArgumentException e)
            {
                var known = new List<string>(scope == ResultScope.Downtime ? DowntimeStatistics : ResultMetrics.Of(scope));
                if (scope == ResultScope.Team) known.AddRange(TeamPreemption);
                if (scope == ResultScope.Element) known.AddRange(ElementPreemption);
                foreach (var kv in o.Metrics) if (!known.Contains(kv.Key)) known.Add(kv.Key);
                return e.Message + $" Statistics of a {kind}: {string.Join(", ", known)}." + NameSuggestions.DidYouMean(statistic, known);
            }
        }

        // tally:NAME[split]:STAT → NAME, STAT
        private static bool TryParseTally(string statistic, out string name, out string stat)
        {
            name = stat = null;
            string rest = statistic.Substring(ResultMetrics.TallyPrefix.Length);
            int colon = rest.LastIndexOf(':');
            if (colon <= 0) return false;
            stat = rest.Substring(colon + 1).Trim();
            name = rest.Substring(0, colon);
            int bracket = name.IndexOf('[');
            if (bracket >= 0) name = name.Substring(0, bracket);
            name = name.Trim();
            return name.Length > 0;
        }

        private static List<string> TallyNames(Element e)
            => e.StatsCollector?.Tallies?.Names is IReadOnlyList<string> names ? new List<string>(names) : new List<string>();

        // validate: a tally the element does not define, or a statistic tallies do not have, failed only at run time
        private static string TallyProblem(ModelMetric m, Element e, string statistic)
        {
            if (!TryParseTally(statistic, out string name, out string stat))
                return $"Metric '{m.Name}': '{statistic}' is not tally:NAME:STATISTIC (e.g. tally:time_in_system:mean).";
            var names = TallyNames(e);
            if (!names.Contains(name))
                return $"Metric '{m.Name}': '{e.Name}' has no tally '{name}' (tallies: {(names.Count == 0 ? "none" : string.Join(", ", names))}). " +
                       $"Define it: simulean_set object={e.Name} key=tallies value=\"{name} = age\"." + NameSuggestions.DidYouMean(name, names);
            if (!System.Linq.Enumerable.Contains(ResultMetrics.TallyStatistics, stat))
                return $"Metric '{m.Name}': unknown statistic '{stat}' of a tally. Statistics of a tally: {string.Join(", ", ResultMetrics.TallyStatistics)}." +
                       NameSuggestions.DidYouMean(stat, ResultMetrics.TallyStatistics);
            return null;
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

        /// <summary>" 'X' is a resource team: …" when an element metric names a team, downtime, list or reaction: those
        /// are read through their scope prefix (<c>team:X.utilization</c>), and a bare name only finds elements.</summary>
        private static string ScopeHint(ModelMetric m, RunResults results)
        {
            if (m.Scope != MetricScope.Element) return "";
            foreach (var (scope, kind, prefix) in new[] { (ResultScope.Team, "a resource team", "team"), (ResultScope.Downtime, "a downtime", "downtime"),
                                                          (ResultScope.List, "a list", "list"), (ResultScope.Reaction, "a reaction", "reaction") })
                foreach (var x in results.Of(scope))
                    if (string.Equals(scope == ResultScope.Downtime ? x.Id : x.Name, m.Target, StringComparison.OrdinalIgnoreCase))
                        return $" '{m.Target}' is {kind}: write '{prefix}:{(scope == ResultScope.Downtime ? x.Id : x.Name)}." +
                               $"{(string.IsNullOrEmpty(m.Statistic) ? "<statistic>" : m.Statistic)}'.";
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

        private static Element ElementOf(ResultObject o, HeadlessModelFactory model)
        {
            if (model != null) foreach (var kv in model.GetAllElements()) if (kv.Key == o.Id) return kv.Value;
            return null;
        }

        /// <summary>
        /// <c>property:NAME</c> — the element's user property at the end of the run (a number; a text property
        /// fails, naming it). The value as the run left it: what reactions set, or the build value.
        /// </summary>
        private static double PropertyValue(Element e, string name, string metric)
        {
            if (e.HasProperties && e.Properties.TryGetDouble(name, out double v)) return v;
            if (e.HasProperties && e.Properties.Has(name))
                throw new ArgumentException($"Metric '{metric}': property '{name}' of '{e.Name}' is not a number ({e.Properties.GetText(name)}).");
            throw new ArgumentException($"Metric '{metric}': '{e.Name}' has no property '{name}'" + PropertyNames(e) +
                                        NameSuggestions.DidYouMean(name, e.HasProperties ? e.Properties.Names : new List<string>()));
        }

        private static string PropertyNames(Element e)
            => " (properties: " + (e.HasProperties && e.Properties.Count > 0 ? string.Join(", ", e.Properties.Names) : "none") + ").";

        // validate (Study.Check): an unknown property — none at the build and no reaction writes it — fails with a did-you-mean
        private static string PropertyProblem(ModelMetric m, ResultObject o, HeadlessModelFactory model)
        {
            string name = (m.Statistic ?? "").Substring(ResultMetrics.PropertyPrefix.Length).Trim();
            var e = ElementOf(o, model);
            if (e == null) return null;   // no model to look at: read after the run
            if (name.Length == 0) return $"Metric '{m.Name}': 'property:' needs a property name (property:late).";
            if (e.HasProperties && e.Properties.Has(name))
                return e.Properties.TryGetDouble(name, out _) ? null
                     : $"Metric '{m.Name}': property '{name}' of '{e.Name}' is not a number ({e.Properties.GetText(name)}).";
            if (model.ReactionsById != null)
                foreach (var r in model.ReactionsById.Values)
                    foreach (var a in r.Actions)
                        if (a.Kind == SimuLean.Signals.ReactionActionKind.SetElementProperty && a.Name == name) return null;
            return $"Metric '{m.Name}': '{e.Name}' has no property '{name}'" + PropertyNames(e) +
                   " Give it the property (simulean_set key=properties." + name + ") or have a reaction set it." +
                   NameSuggestions.DidYouMean(name, e.HasProperties ? e.Properties.Names : new List<string>());
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
        /// <summary>The inspected runs also keep their state intervals (<see cref="RunResults.Timeline"/>, the Gantt).</summary>
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
                if (v.IsNumber && !names.Add(FitnessName(v.Name)))
                    throw new ArgumentException($"'{FitnessName(v.Name)}' names both a metric and a parameter.");
            }
            fitness = Expression.Parse(string.IsNullOrWhiteSpace(fitnessExpression) ? "0" : fitnessExpression, ExpressionContext.Fitness);
            foreach (string r in fitness.References)
                if (!names.Contains(r)) throw new ArgumentException($"Fitness: '{r}' is not a metric or a numeric parameter (a parameter's path is written with _: B1_capacity).");
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
                if (inspect != null && RecordTimeline) JobRecorder.Attach(model);   // the job Gantt
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
            return Combine(candidate, values, seeds.Length);
        }

        // the replications' metric values (in seed order) → the means and the fitness
        private Evaluation Combine(Candidate candidate, List<double>[] values, int replications)
        {
            var e = new Evaluation();
            for (int k = 0; k < Metrics.Count; k++)
            {
                double sum = 0;
                foreach (double v in values[k]) sum += v;
                e.Metrics[Metrics[k].Name] = sum / replications;
                e.MetricValues[Metrics[k].Name] = values[k];
            }
            var variables = new Dictionary<string, double>(e.Metrics);
            for (int i = 0; i < Parameters.Count; i++)
                if (Parameters[i].Variable.IsNumber) variables[FitnessName(Parameters[i].Variable.Name)] = candidate.Number(i);
            e.Fitness = fitness.Number(new MetricScope(variables));
            return e;
        }

        /// <summary>
        /// The candidates as a batch (a generation sent to the out-of-editor runner): one scenario per
        /// candidate — its model (<see cref="ConfigFor"/>) and <see cref="Replications"/> runs with seeds
        /// <see cref="BaseSeed"/>, +1, … (those of <see cref="Evaluate"/>) —, the metrics, no statistics kept.
        /// <see cref="EvaluationFrom"/> gives each candidate's evaluation from its runs.
        /// </summary>
        public StudyBatch BatchFor(IReadOnlyList<Candidate> candidates)
        {
            var batch = new StudyBatch
            {
                Name = "candidates", Replications = Math.Max(1, Replications), BaseSeed = BaseSeed, RunTime = RunTime,
                RecordStatistics = false, Metrics = new List<ModelMetric>(Metrics),
            };
            for (int i = 0; i < candidates.Count; i++)
            {
                var sc = new StudyBatch.Scenario { Name = "c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), Replications = batch.Replications };
                try { sc.Model = ConfigFor(candidates[i]); }
                catch (Exception ex) { sc.Error = ex.GetType().Name + ": " + ex.Message; }
                batch.Scenarios.Add(sc);
            }
            return batch;
        }

        /// <summary>The evaluation of <paramref name="candidate"/> from its runs (<see cref="BatchFor"/>, in replication
        /// order): the same means and fitness as <see cref="Evaluate"/>; a failed run fails the candidate with its error;
        /// null when its runs did not all run (a cancelled batch).</summary>
        public Evaluation EvaluationFrom(Candidate candidate, IReadOnlyList<Results.ResultRun> runs)
        {
            int n = Math.Max(1, Replications);
            var failed = runs.FirstOrDefault(r => r.Error != null);
            if (failed != null) return Evaluation.Failed(failed.Error);
            if (runs.Count < n) return null;
            var values = new List<double>[Metrics.Count];
            for (int k = 0; k < values.Length; k++)
            {
                values[k] = new List<double>(n);
                foreach (var r in runs.OrderBy(r => r.Replication))
                    values[k].Add(r.Metrics.TryGetValue(Metrics[k].Name, out double v) ? v : double.NaN);
            }
            return Combine(candidate, values, n);
        }

        /// <summary>A parameter's name in the fitness: its own when it is an identifier (<c>operators</c>), else its path
        /// written as <c>where</c> writes it (<c>B1.capacity</c> → <c>B1_capacity</c>) — a study's parameters are named by
        /// their paths, which an expression cannot read.</summary>
        public static string FitnessName(string name) => IsIdentifier(name) ? name : StudyOptions.NameOf(name);

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
