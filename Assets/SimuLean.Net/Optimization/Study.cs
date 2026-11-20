using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimuLean.Data;
using SimuLean.Headless;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Optimization
{
    /// <summary>A scenario of a <see cref="Study"/>: its name and one value (text) per factor, in factor order.</summary>
    public sealed class StudyScenario
    {
        public string Name { get; set; }
        public List<string> Values { get; } = new List<string>();
        /// <summary>Replications of this scenario (0 = the study's).</summary>
        public int Replications { get; set; }
    }

    /// <summary>The outcome of <see cref="Study.Check"/>: errors (the study would fail or read a wrong number) and warnings
    /// (it runs, but something has no effect). Each message once.</summary>
    public sealed class StudyCheck
    {
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        /// <summary>Facts worth knowing that need no change (a default the build chose: a team's home node).</summary>
        public List<string> Info { get; } = new List<string>();
        public bool Ok => Errors.Count == 0;

        internal void Add(bool error, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            var list = error ? Errors : Warnings;
            if (!list.Contains(message)) list.Add(message);
        }
    }

    /// <summary>
    /// A scriptable experiment: factors over the parameter catalog, scenarios (given, or
    /// every combination of the factors' levels), metrics, N replications with common random numbers (replication r: seed
    /// <see cref="BaseSeed"/> + r − 1 in every scenario), run headless in parallel and returned as a <see cref="ResultSet"/>
    /// — from code, from the MCP or from the Experimenter component, which runs through it. Engine-only.
    /// <para><b>Factor paths</b> — <c>Owner.key</c> (an element's catalog key, e.g. <c>Yard.capacity</c>,
    /// <c>Srv.delay~1</c>), <c>team:Welders.members</c>, <c>team:Ops.members[skill=welding]</c> (the members with that
    /// label, <see cref="TeamPropertyMembersParameter"/>), <c>downtime:id.key</c>, <c>Source.table</c> (a schedule
    /// source's arrivals table read from a file per level, <see cref="SourceTableParameter"/>), and
    /// <c>data:Table.file</c> / <c>data:Table.sql</c> (a data table's source per level, <see cref="DataTableParameter"/>),
    /// and a declared text setting by its texts (<c>Station.processBy.priority</c>, <c>team:Ops.requestOrder</c>,
    /// <see cref="TextSettingParameter"/>).
    /// <b>Metric paths</b> — <c>model.makespan</c>, <c>Sink.input</c>, <c>team:Welders.utilization</c>,
    /// <c>list:Yard.wait_avg</c> (<see cref="MetricCatalog"/>).</para>
    /// </summary>
    public sealed class Study
    {
        private readonly SimulationConfig model;
        private readonly List<(string path, List<string> levels)> factors = new List<(string, List<string>)>();
        private readonly List<ModelParameter> extraParameters = new List<ModelParameter>();

        public Study(SimulationConfig model) { this.model = model?.Clone() ?? throw new ArgumentNullException(nameof(model)); }

        public string Name { get; set; } = "Study";
        public int Replications { get; set; } = 1;
        public int BaseSeed { get; set; } = 1;
        /// <summary>Simulated time of each run (0 = the model's max time; an end condition may stop it earlier).</summary>
        public double RunTime { get; set; }
        /// <summary>Time series interval of each run (0 = none).</summary>
        public double SeriesInterval { get; set; }
        /// <summary>Every run also keeps its state intervals (the Gantt; opt-in: the results grow with every state change).</summary>
        public bool RecordTimeline { get; set; }
        /// <summary>Record every statistic of each run (<see cref="RunResults"/>); off = only the metrics.</summary>
        public bool RecordStatistics { get; set; } = true;
        /// <summary>Runs at once (0 = <see cref="DefaultThreads"/>).</summary>
        public int Threads { get; set; }

        /// <summary>Runs at once by default: half the logical processors — about one per physical core —, between 1 and 16.
        /// The runs are bound by memory and garbage collection, so two on one core gain little (measured
        /// on a 10-core / 16-thread machine: the gain levels off from 6–8 threads). The user can set it.</summary>
        public static int DefaultThreads => Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 16));
        public double Confidence { get; set; } = 0.95;
        /// <summary>Reads a table file for <see cref="SourceTableParameter"/> (default: <see cref="TableReader.Read"/>, CSV /
        /// .data; Unity passes its reader for Excel too).</summary>
        public Func<string, Dictionary<string, List<string>>> TableLoader { get; set; } = TableReader.Read;

        public List<ModelMetric> Metrics { get; } = new List<ModelMetric>();
        public List<StudyScenario> Scenarios { get; } = new List<StudyScenario>();

        /// <summary>Adds a factor and its levels (texts: numbers, options, file paths). Without explicit
        /// <see cref="Scenarios"/>, the study runs every combination of the levels.</summary>
        public Study Factor(string path, params string[] levels)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A factor needs a path.");
            if (levels == null || levels.Length == 0) throw new ArgumentException($"Factor '{path}' needs at least one level.");
            factors.Add((path.Trim(), new List<string>(levels)));
            return this;
        }

        public Study Factor(string path, params double[] levels)
            => Factor(path, levels.Select(v => v.ToString("R", CultureInfo.InvariantCulture)).ToArray());

        /// <summary>A factor already built (any <see cref="ModelParameter"/>, e.g. the Experimenter's), with its levels.</summary>
        public Study Factor(ModelParameter parameter, params string[] levels)
        {
            extraParameters.Add(parameter ?? throw new ArgumentNullException(nameof(parameter)));
            factors.Add((null, new List<string>(levels ?? new string[0])));
            return this;
        }

        /// <summary>Adds a metric by path (<c>model.makespan</c>, <c>Sink.input</c>, <c>team:T.utilization</c>…).</summary>
        public Study Metric(string name, string path)
        {
            Metrics.Add(ParseMetric(name, path, model));
            return this;
        }

        /// <summary>A model parameter by factor path whose range covers <paramref name="values"/> (numbers: the bounds; texts:
        /// the options, e.g. a source's table files) — an optimisation variable in the factors' grammar.</summary>
        public ModelParameter ParameterOf(string path, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A parameter needs a path.");
            var list = new List<string>(values ?? new string[0]);
            if (list.Count == 0) throw new ArgumentException($"Parameter '{path}' needs its range or options.");
            return ParseParameter(path.Trim(), list, list);
        }

        /// <summary>A model parameter by factor path searched over <paramref name="options"/> only (<c>2|4|8</c>): a catalog
        /// parameter becomes an <see cref="OptionsParameter"/> (a numeric one is never searched between the values given);
        /// a data / table / labelled-members parameter is built as <see cref="ParameterOf"/> builds it.</summary>
        public ModelParameter OptionsOf(string path, params string[] options)
        {
            var p = ParameterOf(path, options);
            if (!(p is ConfigParameter c)) return p;
            var kind = c.Variable.Kind;
            if (kind != VariableKind.Integer && kind != VariableKind.Real && kind != VariableKind.Choice) return p;
            return new OptionsParameter(c, options, path.Trim());
        }

        /// <summary>A note when a range of whole numbers is searched as real numbers (a property's value is a real number):
        /// the agent may have meant whole numbers. Null otherwise.</summary>
        public static string RangeNote(ModelParameter parameter)
        {
            var v = parameter?.Variable;
            if (v == null || v.Kind != VariableKind.Real || v.Min != Math.Floor(v.Min) || v.Max != Math.Floor(v.Max)) return null;
            return $"'{v.Name}' is a real number: it is searched over [{v.Min.ToString(CultureInfo.InvariantCulture)}, " +
                   $"{v.Max.ToString(CultureInfo.InvariantCulture)}] continuously (e.g. 1.37); for whole numbers give the options " +
                   $"'{v.Name}=" + string.Join("|", WholeSteps(v.Min, v.Max)) + "'.";
        }

        private static IEnumerable<string> WholeSteps(double lo, double hi)
        {
            int n = 0;
            for (double x = lo; x <= hi && n < 12; x++, n++) yield return x.ToString(CultureInfo.InvariantCulture);
            if (hi - lo >= 12) yield return "…";
        }

        /// <summary>The factors' labels (their paths, or the parameter's name), in factor order — the order of a scenario's
        /// values.</summary>
        public IReadOnlyList<string> FactorLabels => Enumerable.Range(0, factors.Count).Select(FactorLabel).ToList();

        /// <summary>The model's value of a factor path as a level is written (<c>4</c>, <c>true</c>, <c>Fifo</c>), or null when
        /// it has none to show (a data table, a source's table, labelled members) or the path names nothing.</summary>
        public string CurrentLevel(string path)
        {
            try
            {
                var (scopeText, owner, key) = Split((path ?? "").Trim(), model);
                if (!TryScope(scopeText, out ParameterScope scope) || scope == ParameterScope.Data) return null;
                if (scope == ParameterScope.Element && (key == SourceTableParameter.Key || key == SourceTableParameter.FileKey)) return null;
                if (scope == ParameterScope.Team && key.Contains("[")) return null;
                if (ModelParameterCatalog.IsTextSetting(model, scope, owner, key)) return ModelParameterCatalog.TextSetting(model, scope, owner, key);
                return ConfigParameter.Of(model, scope, owner, key).Info.CurrentText;
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException || e is InvalidOperationException) { return null; }
        }

        /// <summary>A metric by path, as <see cref="Metric"/> adds it.</summary>
        public static ModelMetric MetricOf(string name, string path) => ParseMetric(name, path);

        /// <summary>Adds an explicit scenario: one value per factor, in factor order.</summary>
        public Study Scenario(string name, params string[] values) => Scenario(name, 0, values);

        /// <summary>Adds an explicit scenario with its own number of replications (0 = the study's).</summary>
        public Study Scenario(string name, int replications, params string[] values)
        {
            var s = new StudyScenario { Name = name, Replications = replications };
            s.Values.AddRange(values ?? new string[0]);
            Scenarios.Add(s);
            return this;
        }

        /// <summary>The model the study runs (its own copy; read it, do not change it).</summary>
        public SimulationConfig Model => model;

        /// <summary>The simulated time of each run: <see cref="RunTime"/>, else the model's max time (else 100, as
        /// <see cref="SimulationObjective"/>) — what a run answer reports as its horizon.</summary>
        public double EffectiveRunTime => RunTime > 0 ? RunTime : model.MaxSimulationTime > 0 ? model.MaxSimulationTime : 100.0;

        public Study WithReplications(int n) { Replications = Math.Max(1, n); return this; }
        public Study WithSeed(int seed) { BaseSeed = seed; return this; }
        public Study WithRunTime(double t) { RunTime = t; return this; }

        /// <summary>The scenarios run: the explicit ones, else every combination of the levels (first factor slowest).</summary>
        public List<StudyScenario> Plan()
        {
            if (Scenarios.Count > 0)
            {
                var names = new HashSet<string>();
                foreach (var s in Scenarios)
                {
                    if (s.Values.Count != factors.Count)
                        throw new InvalidOperationException($"Scenario '{s.Name}': {s.Values.Count} values for {factors.Count} factors.");
                    // scenarios are found and their runs grouped by name: two with one name would merge
                    if (!names.Add(s.Name ?? ""))
                        throw new InvalidOperationException($"Two scenarios are named '{s.Name}': scenario names must be unique.");
                }
                return Scenarios;
            }
            var plan = new List<StudyScenario> { new StudyScenario { Name = factors.Count == 0 ? "Baseline" : "" } };
            foreach (var (_, levels) in factors)
            {
                var next = new List<StudyScenario>();
                foreach (var s in plan)
                    foreach (string level in levels)
                    {
                        var c = new StudyScenario();
                        c.Values.AddRange(s.Values);
                        c.Values.Add(level);
                        next.Add(c);
                    }
                plan = next;
            }
            if (factors.Count > 0)
                foreach (var s in plan)
                    s.Name = string.Join(" · ", s.Values.Select((v, i) => $"{FactorLabel(i)}={ShortValue(v)}"));
            return plan;
        }

        /// <summary>Applies scenario <paramref name="scenario"/>'s values to <paramref name="config"/> (a run in the scene:
        /// the Experimenter's in-scene mode builds the scene's model with them).</summary>
        public void Apply(SimulationConfig config, string scenario)
        {
            var objective = Objective(out var plan);
            var s = plan.Find(x => x.Name == scenario) ?? throw new ArgumentException($"Study: no scenario '{scenario}'.");
            var c = CandidateOf(objective, s);
            for (int i = 0; i < objective.Parameters.Count; i++) objective.Parameters[i].Apply(config, c, i);
        }

        /// <summary>
        /// The state of the study's one scenario run with model seed <paramref name="seed"/> to <paramref name="time"/> — or,
        /// with time 0, until nothing is left to do (a stall, a deadlock or the model empty; at most the horizon): what each
        /// element holds and waits for, each team's requests and members, the lists (<see cref="ModelDiagnosis"/>).
        /// </summary>
        public Dictionary<string, object> Diagnose(double time, int seed = 1, bool all = false)
        {
            var objective = Objective(out var plan);
            if (plan.Count != 1)
                throw new ArgumentException($"Diagnosis: one scenario at a time (the factors give {plan.Count}): give one level per factor.");
            var config = objective.ConfigFor(CandidateOf(objective, plan[0]));
            if (time <= 0) config.StopWhenEmpty = true;
            var clock = new SimClock(seed);
            var factory = new HeadlessModelFactory(clock);
            factory.BuildModel(config);
            factory.StartModel();
            clock.AdvanceClock(time > 0 ? time : EffectiveRunTime);
            var d = ModelDiagnosis.Of(factory, clock, all);
            SimuLean.Logic.ModelLogic.Mark(d, config, factory);   // the decisions where items wait, with their counters
            d["seed"] = seed;
            d["horizon"] = time > 0 ? time : EffectiveRunTime;
            return d;
        }

        /// <summary>Builds the problem without running it (null = valid, else the problem).</summary>
        public string Validate()
        {
            try
            {
                var objective = Objective(out var plan);
                foreach (var s in plan) CandidateOf(objective, s);
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        /// <summary>
        /// Checks the study on the built model without running it (<c>simulean_validate</c>, and before
        /// every MCP run): the syntax (<see cref="Validate"/>), then for each scenario its levels applied, the model built
        /// and started, and every metric resolved on the results it would give (<see cref="MetricCatalog.Problem"/>: an
        /// unknown or ambiguous target, an unknown statistic or state — "did you mean"). A factor on a key of a distribution
        /// the element does not use is a warning. Nothing is run; the model's own clock is never advanced.
        /// </summary>
        public StudyCheck Check()
        {
            var check = new StudyCheck();
            // a factor on a flat sampler key of a distribution the element does not use: a scene exports only the chosen
            // distribution's keys (the factor cannot apply), a config may carry them all (it applies, and changes nothing)
            foreach (var (path, _) in factors)
            {
                if (path == null) continue;
                (string scope, string owner, string key) f;
                try { f = Split(path, model); } catch (ArgumentException) { continue; }
                string used = f.scope == "element" ? ModelParameterCatalog.InactiveSamplerKey(model, f.owner, f.key) : null;
                if (used == null) continue;
                bool present = model.Elements.Exists(e => e.Name == f.owner && e.Has(f.key));
                check.Add(!present, $"Factor '{path}': '{f.key}' belongs to another distribution — '{f.owner}' uses {used}" +
                                    (present ? ", so this factor changes nothing" : "") + ". Vary that distribution's keys, or its type key too.");
            }
            if (!check.Ok) return check;
            SimulationObjective objective;
            List<StudyScenario> plan;
            try
            {
                objective = Objective(out plan);
                foreach (var s in plan) CandidateOf(objective, s);
            }
            catch (Exception e) { check.Errors.Add(e.Message); return check; }
            foreach (var s in plan)
            {
                HeadlessModelFactory built;
                Results.RunResults results;
                try
                {
                    built = new HeadlessModelFactory(new SimClock(BaseSeed));
                    built.BuildModel(objective.ConfigFor(CandidateOf(objective, s)));
                    built.StartModel();
                    results = Results.RunResults.Capture(built);
                }
                catch (Exception e)
                {
                    check.Add(true, (plan.Count > 1 ? $"Scenario '{s.Name}': " : "") + "the model cannot be built: " + e.Message);
                    continue;
                }
                foreach (var m in Metrics)
                {
                    string problem = MetricCatalog.Problem(m, results, built);
                    check.Add(true, problem == null ? null : problem + ExpressionHint(m.Target + "." + m.Statistic));
                }
                // a spatial team without a home node starts at the network's first node
                foreach (var tc in model.ResourceTeams)
                {
                    if (tc == null || !string.IsNullOrEmpty(tc.HomeNode) || tc.Travel == TravelKind.Abstract) continue;
                    if (!built.Teams.TryGetValue(tc.Name ?? tc.Id ?? "", out var team) && !built.Teams.TryGetValue(tc.Id ?? "", out team)) continue;
                    string home = team.Members.Count > 0 ? team.Members[0].HomeNode : null;
                    string text = $"Team '{tc.Name ?? tc.Id}' has no homeNode: its members start at node '{home}', the network's first. " +
                                  "Set homeNode to a Transport Node to choose.";
                    if (home != null && !check.Info.Contains(text)) check.Info.Add(text);
                }
            }
            foreach (var m in Metrics) check.Add(false, FlowOnlyMetricNote(m, model));
            foreach (string text in MemberStopsNotInterrupting(model)) if (!check.Info.Contains(text)) check.Info.Add(text);
            foreach (string text in ExponentialMeans(model)) if (!check.Info.Contains(text)) check.Info.Add(text);
            foreach (string text in ShapeScaleMeans(model)) if (!check.Info.Contains(text)) check.Info.Add(text);
            // the model's choice rules: a rule without a Why, a rule used where the primitive it equals would do
            var logic = SimuLean.Logic.ModelLogic.Of(model);
            foreach (string w in logic.RuleWarnings(key => factors.Exists(f => f.path == key))) check.Add(false, w);
            // an item label read that nothing sets: most often a text without quotes
            foreach (string w in SimuLean.Logic.ModelLogic.UnsetLabelReads(model, logic)) check.Add(false, w);
            return check;
        }

        /// <summary>The hint added to a metric's error when its path reads like an expression (a sum, a ratio, a call): a
        /// metric is one path, and <c>derived</c> combines them. Empty otherwise.</summary>
        public static string ExpressionHint(string path)
            => (path ?? "").IndexOfAny(new[] { '+', '*', '/', '(', ')', '^' }) < 0 ? ""
             : " A metric is one path; for a sum or a ratio name each part as a metric and combine them with derived: " +
               "metrics=a=Line.content_avg;b=Clerk.content_avg derived=L=a+b.";

        /// <summary>
        /// A warning for a metric that is constant, or meaningless, by its element's type: a Source holds no items (its
        /// <c>stay_*</c> and <c>content*</c> are always 0 — items held at a blocked source wait in its BLOCKED state); a Sink
        /// keeps none (its <c>stay_*</c> is 0, its <c>utilization</c> ≈ 1, its <c>content_avg</c> grows with the count
        /// received) — its time in system is <c>tis_avg</c>. Null for any other metric.
        /// </summary>
        public static string FlowOnlyMetricNote(ModelMetric m, SimulationConfig model)
        {
            if (m == null || m.Scope != MetricScope.Element || model?.Elements == null) return null;
            var e = model.Elements.Find(x => x != null && x.Name == m.Target);
            string s = m.Statistic ?? "";
            bool stay = s.StartsWith("stay_", StringComparison.Ordinal);
            if (e?.Type == "Source" && (stay || s == ResultMetrics.Content || s == ResultMetrics.ContentAvg || s == ResultMetrics.ContentMax))
                return $"Metric '{m.Name}': '{e.Name}' is a Source: it holds no items, so {s} is always 0. The time items wait at a " +
                       $"blocked source is its state: {e.Name}.state:BLOCKED (a share of the time), or a tally wait = age on the next element.";
            if (e?.Type == "Sink" && (stay || s == ResultMetrics.Utilization || s == ResultMetrics.ContentAvg))
                return $"Metric '{m.Name}': '{e.Name}' is a Sink: it keeps no items, so {s} is no KPI ({ResultMetrics.StayAvg} is 0, " +
                       $"{ResultMetrics.Utilization} ≈ 1, {ResultMetrics.ContentAvg} grows with the count received). Its time in system: " +
                       $"{e.Name}.{ResultMetrics.TisAvg} ({ResultMetrics.TisP90}, …); the items received: {e.Name}.{ResultMetrics.Input}.";
            return null;
        }

        /// <summary>Exponential distributions listed at most in <see cref="ExponentialMeans"/>.</summary>
        public const int ExponentialsShown = 12;

        /// <summary>
        /// The model's exponential distributions by their mean (<c>Exponential~x</c> takes a rate,
        /// <c>ExponentialMean~x</c> a mean, and a scene stores the mean as its rate): one info line, <c>Src.interArrivalDistribution
        /// mean 5 (rate 0.2)</c> per distribution of the elements, teams and downtimes (the first <see cref="ExponentialsShown"/>);
        /// none without one.
        /// </summary>
        public static List<string> ExponentialMeans(SimulationConfig model)
        {
            var found = new List<string>();
            string Fmt(double x) => Math.Round(x, 4).ToString("0.####", CultureInfo.InvariantCulture);
            void Scan(string owner, IEnumerable<(string path, object value)> values)
            {
                var list = values.ToList();
                var byPath = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var (p, x) in list) byPath[p] = x;
                foreach (var (p, x) in list)
                {
                    if (!(x is string type) || !(p == "strategyType" || p.EndsWith(".strategyType", StringComparison.Ordinal))) continue;
                    string at = p.Substring(0, p.Length - "strategyType".Length);
                    string where = owner + (at.Length > 0 ? "." + at.TrimEnd('.') : "");
                    if (type == "Exponential" && byPath.TryGetValue(at + "exponentialRate", out var r) && r is IConvertible
                        && Convert.ToDouble(r, CultureInfo.InvariantCulture) is double rate && rate > 0)
                        found.Add($"{where} mean {Fmt(1.0 / rate)} (rate {Fmt(rate)})");
                    else if (type == "ExponentialMean" && byPath.TryGetValue(at + "exponentialMean", out var m) && m is IConvertible)
                        found.Add($"{where} mean {Fmt(Convert.ToDouble(m, CultureInfo.InvariantCulture))}");
                }
            }
            foreach (var e in model.Elements) if (e != null) Scan(e.Name ?? e.Id, e.Values());
            foreach (var t in model.ResourceTeams) if (t != null) Scan("team:" + ResourceBuild.TeamName(t), t.Values());
            foreach (var d in model.Downtimes) if (d != null) Scan("downtime:" + d.Id, d.Values());
            if (found.Count == 0) return new List<string>();
            string shown = string.Join("; ", found.Take(ExponentialsShown)) + (found.Count > ExponentialsShown ? $"; … {found.Count - ExponentialsShown} more" : "");
            return new List<string> { "Exponential distributions by their mean (Exponential~x takes a rate, ExponentialMean~x a mean): " + shown + "." };
        }

        /// <summary>
        /// The model's LogNormal, Gamma and Weibull distributions by their mean — their parameters are not means (a LogNormal's
        /// mu and sigma are of ln x; Gamma and Weibull take a shape and a scale): one info line,
        /// <c>Srv.delayStrategy LogNormal~1~0.5 mean 3.08</c> per distribution (the first <see cref="ExponentialsShown"/>);
        /// none without one. Info only: nothing is drawn.
        /// </summary>
        public static List<string> ShapeScaleMeans(SimulationConfig model)
        {
            var found = new List<string>();
            string Fmt(double x) => Math.Round(x, 4).ToString("0.####", CultureInfo.InvariantCulture);
            void Scan(string owner, IEnumerable<(string path, object value)> values)
            {
                var list = values.ToList();
                var byPath = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var (p, x) in list) byPath[p] = x;
                double? Number(string key) => byPath.TryGetValue(key, out var v) && v is IConvertible ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : (double?)null;
                foreach (var (p, x) in list)
                {
                    if (!(x is string type) || !(p == "strategyType" || p.EndsWith(".strategyType", StringComparison.Ordinal))) continue;
                    string at = p.Substring(0, p.Length - "strategyType".Length);
                    string where = owner + (at.Length > 0 ? "." + at.TrimEnd('.') : "");
                    double mean = double.NaN;
                    string text = null;
                    if (type == "LogNormal" && Number(at + "logNormalMu") is double mu && Number(at + "logNormalSigma") is double sigma)
                    { mean = Math.Exp(mu + sigma * sigma / 2); text = $"LogNormal~{Fmt(mu)}~{Fmt(sigma)}"; }
                    else if (type == "Gamma" && Number(at + "gammaShape") is double k && Number(at + "gammaScale") is double theta)
                    { mean = k * theta; text = $"Gamma~{Fmt(k)}~{Fmt(theta)}"; }
                    else if (type == "Weibull" && Number(at + "weibullShape") is double ws && ws > 0 && Number(at + "weibullScale") is double wl)
                    { mean = wl * SimuLean.Stochastics.Probability.Gamma(1 + 1 / ws); text = $"Weibull~{Fmt(ws)}~{Fmt(wl)}"; }
                    if (text != null && !double.IsNaN(mean) && !double.IsInfinity(mean)) found.Add($"{where} {text} mean {Fmt(mean)}");
                }
            }
            foreach (var e in model.Elements) if (e != null) Scan(e.Name ?? e.Id, e.Values());
            foreach (var t in model.ResourceTeams) if (t != null) Scan("team:" + ResourceBuild.TeamName(t), t.Values());
            foreach (var d in model.Downtimes) if (d != null) Scan("downtime:" + d.Id, d.Values());
            if (found.Count == 0) return new List<string>();
            string shown = string.Join("; ", found.Take(ExponentialsShown)) + (found.Count > ExponentialsShown ? $"; … {found.Count - ExponentialsShown} more" : "");
            return new List<string> { "LogNormal / Gamma / Weibull distributions by their mean (LogNormal~mu~sigma of ln x: mean e^(mu + sigma²/2); Gamma~shape~scale: shape × scale; Weibull~shape~scale: scale × Γ(1 + 1/shape)): " + shown + "." };
        }

        /// <summary>
        /// The stations whose job a member's downtime will not interrupt: a downtime stops the members of a team
        /// (a team among its targets), and a station uses that team with no preemption mode (<c>processBy.preemption</c> None),
        /// so a member's breakdown leaves its job running as if nothing happened — one info line per station and team.
        /// </summary>
        public static List<string> MemberStopsNotInterrupting(SimulationConfig model)
        {
            var lines = new List<string>();
            if (model?.Downtimes == null || model.ResourceTeams == null) return lines;
            var byTeam = new Dictionary<string, (string team, List<string> downtimes)>(StringComparer.Ordinal);
            foreach (var d in model.Downtimes)
                foreach (string id in d?.TargetIds ?? new List<string>())
                {
                    var t = model.ResourceTeams.Find(x => x != null && (x.Id == id || x.Name == id));
                    if (t == null) continue;
                    string key = t.Id ?? t.Name;
                    if (!byTeam.TryGetValue(key, out var entry)) byTeam[key] = entry = (t.Name ?? t.Id, new List<string>());
                    if (!entry.downtimes.Contains(d.Id)) entry.downtimes.Add(d.Id);
                }
            if (byTeam.Count == 0) return lines;
            foreach (var e in model.Elements)
            {
                if (e?.Settings == null) continue;
                var values = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var (path, value) in e.Values()) values[path] = value;
                if (!values.TryGetValue("processBy.team", out var main) || !(main is string)) continue;
                string mode = values.TryGetValue("processBy.preemption", out var p) ? p?.ToString() : null;
                if (!string.IsNullOrEmpty(mode) && mode != "None") continue;
                var teams = values.Where(kv => kv.Key.StartsWith("processBy.", StringComparison.Ordinal) && kv.Key.EndsWith(".team", StringComparison.Ordinal))
                                  .Select(kv => (kv.Value as string ?? "").Trim()).Where(s => s.Length > 0).Distinct().ToList();
                foreach (string team in teams)
                {
                    if (!byTeam.TryGetValue(team, out var hit))
                    {
                        var t = model.ResourceTeams.Find(x => x != null && x.Name == team);
                        if (t == null || !byTeam.TryGetValue(t.Id ?? t.Name, out hit)) continue;
                    }
                    lines.Add($"Downtime {string.Join(", ", hit.downtimes.Select(x => "'" + x + "'"))} stops members of team '{hit.team}', but " +
                              $"'{e.Name ?? e.Id}' uses that team with processBy.preemption = None: a member's breakdown will not interrupt its job " +
                              "(the process runs on). Give it a mode (Resume) to have breakdowns interrupt and count.");
                }
            }
            return lines;
        }

        /// <summary>Runs every scenario × replication headless (in parallel) and returns the result set.
        /// <paramref name="progress"/> gets (runs done, runs in all). Runs through <see cref="Batch"/> (the scenarios applied
        /// first), the same code the out-of-editor runner runs.</summary>
        public ResultSet Run(CancellationToken token = default, Action<int, int> progress = null) => Batch().Run(token, progress);

        /// <summary>The study with its scenarios applied (<see cref="StudyBatch"/>): what <see cref="Run"/> runs, and what is
        /// sent to the out-of-editor runner. A scenario whose levels cannot be applied keeps the error for its runs.</summary>
        public StudyBatch Batch()
        {
            var objective = Objective(out var plan);
            var batch = new StudyBatch
            {
                Name = Name, Replications = Replications, BaseSeed = BaseSeed, RunTime = objective.RunTime,
                SeriesInterval = SeriesInterval, RecordTimeline = RecordTimeline, RecordStatistics = RecordStatistics,
                Confidence = Confidence, Threads = Threads, Metrics = new List<ModelMetric>(Metrics),
            };
            foreach (var s in plan)
            {
                var sc = new StudyBatch.Scenario { Name = s.Name, Replications = Math.Max(1, s.Replications > 0 ? s.Replications : Replications) };
                for (int i = 0; i < s.Values.Count; i++) sc.Parameters.Add(new KeyValuePair<string, string>(FactorLabel(i), s.Values[i]));
                try { sc.Model = objective.ConfigFor(CandidateOf(objective, s)); }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { sc.Error = ex.GetType().Name + ": " + ex.Message; }
                batch.Scenarios.Add(sc);
            }
            return batch;
        }

        // ── building ─────────────────────────────────────────────────────────

        private SimulationObjective Objective(out List<StudyScenario> plan)
        {
            plan = Plan();
            var parameters = new List<ModelParameter>();
            int extra = 0;
            for (int i = 0; i < factors.Count; i++)
            {
                var (path, levels) = factors[i];
                parameters.Add(path == null ? extraParameters[extra++] : ParseParameter(path, levels, plan.Select(s => s.Values[i])));
            }
            return new SimulationObjective(model, parameters, Metrics, "0")
            {
                RunTime = RunTime > 0 ? RunTime : model.MaxSimulationTime, SeriesInterval = SeriesInterval, RecordTimeline = RecordTimeline,
            };
        }

        private static Candidate CandidateOf(SimulationObjective objective, StudyScenario s)
        {
            var c = new Candidate(objective.BuildSpace());
            for (int i = 0; i < s.Values.Count; i++)
            {
                try { c.SetText(i, s.Values[i]); }
                catch (FormatException e) { throw new InvalidOperationException($"Scenario '{s.Name}': {e.Message}", e); }
            }
            return c;
        }

        private string FactorLabel(int i) => factors[i].path ?? extraParameters[ExtraIndex(i)].Variable.Name;

        private int ExtraIndex(int factor)
        {
            int n = 0;
            for (int i = 0; i < factor; i++) if (factors[i].path == null) n++;
            return n;
        }

        /// <summary>A file path level by its file name without extension (<c>Data/push.csv</c> → <c>push</c>); any other
        /// text as given — an expression level (<c>item.a / 2</c>) is no path, and two shortened alike would share a
        /// scenario name. No <c>System.IO.Path</c>: it refuses characters an expression may hold on some runtimes.</summary>
        private static string ShortValue(string v)
        {
            if (v == null || v.IndexOfAny(new[] { '/', '\\' }) < 0) return v;
            int slash = v.LastIndexOfAny(new[] { '/', '\\' });
            string name = v.Substring(slash + 1);
            int dot = name.LastIndexOf('.');
            bool file = dot > 0 && dot < name.Length - 1 && name.IndexOfAny(new[] { ' ', '(', ')', '\'', '"', '*', '+', '<', '>', '=' }) < 0
                        && v.IndexOfAny(new[] { '(', ')', '\'', '"', '*', '+', '<', '>', '=' }) < 0;
            return file ? name.Substring(0, dot) : v;
        }

        /// <summary>A factor path as a model parameter whose range covers <paramref name="values"/>.</summary>
        private ModelParameter ParseParameter(string path, List<string> levels, IEnumerable<string> values)
        {
            var (scopeText, owner, key) = Split(path, model);
            if (!TryScope(scopeText, out ParameterScope scope))
                throw new ArgumentException($"Factor '{path}': unknown scope '{scopeText}' ({ScopeNames<ParameterScope>()}).");
            switch (scope)
            {
                case ParameterScope.Team:
                {
                    int br = key.IndexOf('[');
                    if (br > 0 && key.EndsWith("]"))
                    {
                        string label = key.Substring(br + 1, key.Length - br - 2);
                        int eq = label.IndexOf('=');
                        if (key.Substring(0, br) != ModelParameterCatalog.TeamMembers || eq <= 0)
                            throw new ArgumentException($"Factor '{path}': use team:Team.members[label=value].");
                        var (lo, hi) = Range(path, values);
                        return new TeamPropertyMembersParameter(owner, label.Substring(0, eq).Trim(), label.Substring(eq + 1).Trim(), (int)lo, (int)hi, path);
                    }
                    return Ranged(model, scope, owner, key, path, values);
                }
                case ParameterScope.Data:
                    if (!model.Tables.Exists(t => t.Name == owner))
                        throw new ArgumentException($"Factor '{path}': no data table '{owner}' in the model (tables: " +
                                                    $"{(model.Tables.Count == 0 ? "none" : string.Join(", ", model.Tables.Select(t => t.Name)))}).");
                    return new DataTableParameter(owner, key, levels, TableLoader, path);
                case ParameterScope.Element:
                    if (key == SourceTableParameter.Key) return new SourceTableParameter(owner, levels, TableLoader, path);
                    if (key == SourceTableParameter.FileKey)
                        throw new ArgumentException($"Factor '{path}': a source's arrivals file per level is '{owner}.{SourceTableParameter.Key}'.");
                    return Ranged(model, scope, owner, key, path, values);
                default:
                    return Ranged(model, scope, owner, key, path, values);
            }
        }

        /// <summary>A scope by its lower-case name (any case), defined in <typeparamref name="T"/> (not a number).</summary>
        public static bool TryScope<T>(string text, out T scope) where T : struct, Enum
            => Enum.TryParse(text, true, out scope) && Enum.IsDefined(typeof(T), scope) && !char.IsDigit((text ?? "0").TrimStart('-').FirstOrDefault());

        /// <summary>The names of a scope enum as written in a path (lower case, comma-separated).</summary>
        public static string ScopeNames<T>() where T : struct, Enum => string.Join(", ", Enum.GetNames(typeof(T)).Select(n => n.ToLowerInvariant()));

        private static ModelParameter Ranged(SimulationConfig model, ParameterScope scope, string owner, string key, string path, IEnumerable<string> values)
        {
            ConfigParameter p;
            bool text = ModelParameterCatalog.IsTextSetting(model, scope, owner, key);
            // a declared text setting the catalog does not offer (an expression that reads the item, a request order written
            // in place, a reference): varied by its texts; any other key keeps the catalog's error and its did-you-mean
            try { p = ConfigParameter.Of(model, scope, owner, key, name: path); }
            catch (ArgumentException) when (text) { return new TextSettingParameter(scope, owner, key, values, path); }
            // offered while its value is a number (a constant priority) or one of its records' names: a level that is
            // neither makes it a text factor
            if (text && values.Any(v => !Accepts(p, v)))
                return new TextSettingParameter(scope, owner, key, values, path);
            var kind = p.Variable.Kind;
            if (kind != VariableKind.Integer && kind != VariableKind.Real) return p;
            var (lo, hi) = Range(path, values);
            return ConfigParameter.Of(model, scope, owner, key, lo, hi, path);
        }

        private static bool Accepts(ConfigParameter p, string level)
        {
            try { p.ParseText(level); return true; }
            catch (FormatException) { return false; }
        }

        private static (double lo, double hi) Range(string path, IEnumerable<string> values)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (string v in values)
                foreach (string part in (v ?? "").Trim('[', ']').Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        throw new FormatException($"Factor '{path}': '{part.Trim()}' is not a number." + RangeHint(part));
                    lo = Math.Min(lo, d); hi = Math.Max(hi, d);
                }
            return double.IsInfinity(lo) ? (0, 0) : (lo, hi);
        }

        /// <summary>The hint for a level written as a range (<c>1..10</c>): a study lists its levels; a range is the
        /// optimiser's. Empty otherwise.</summary>
        public static string RangeHint(string level)
            => (level ?? "").Contains("..") ? " A study lists its levels (1|2|3); lo..hi is a range for simulean_optimize_start." : "";

        private static ModelMetric ParseMetric(string name, string path, SimulationConfig model = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"Metric '{name}' needs a path.");
            path = path.Trim();
            if (path.StartsWith("model.", StringComparison.OrdinalIgnoreCase))
                return new ModelMetric(name, MetricScope.Model, "model", path.Substring(6));
            (string scopeText, string owner, string key) parts;
            try { parts = Split(path, model); }
            catch (ArgumentException e) when (ExpressionHint(path).Length > 0) { throw new ArgumentException($"Metric '{name}': {e.Message}" + ExpressionHint(path), e); }
            var (scopeText, owner, key) = parts;
            if (TryScope(scopeText, out MetricScope scope) && scope != MetricScope.Model)
                return new ModelMetric(name, scope, owner, key);
            throw new ArgumentException($"Metric '{name}': unknown scope '{scopeText}' ({ScopeNames<MetricScope>()})." +
                                        (scopeText == "resource" || scopeText == "member"
                                             ? " A team member's statistics are read through its team: 'team:Team." + ResultMetrics.Utilization + "'."
                                             : "") +
                                        (scope == MetricScope.Model ? " A model statistic is 'model.<statistic>'." : "") + ExpressionHint(path));
        }

        /// <summary>
        /// A path as (scope, owner, key) — the factor / metric grammar <c>[scope:]Owner.key</c> (keys are field
        /// paths and contain dots, <c>Srv.delayStrategy.constantValue</c>). The scope is the text before a colon that comes
        /// before the first dot (<c>team:Welders.members</c>); element when there is none. The owner is the longest name of
        /// that scope in <paramref name="model"/> (elements, teams by name or id, downtimes, reactions, data tables, lists)
        /// followed by a dot — so an owner whose name has dots or spaces (<c>Line 1.Buffer</c>) is found whole — and the key
        /// is the rest. Without a model, or no known name, the owner ends at the first dot (names without dots).
        /// <c>"Exit.tally:time_in_system:p90"</c> → (element, Exit, tally:time_in_system:p90).
        /// </summary>
        public static (string scope, string owner, string key) Split(string path, SimulationConfig model = null)
        {
            string scope = "element";
            int colon = path.IndexOf(':');
            int firstDot = path.IndexOf('.');
            if (colon > 0 && (firstDot < 0 || colon < firstDot)) { scope = path.Substring(0, colon).Trim().ToLowerInvariant(); path = path.Substring(colon + 1); }
            string owner = null;
            foreach (string name in OwnerNames(model, scope))
                if (!string.IsNullOrEmpty(name) && path.Length > name.Length + 1 && path.StartsWith(name + ".", StringComparison.Ordinal)
                    && (owner == null || name.Length > owner.Length))
                    owner = name;
            if (owner != null) return (scope, owner, path.Substring(owner.Length + 1).Trim());
            int dot = path.IndexOf('.');
            if (dot <= 0 || dot == path.Length - 1) throw new ArgumentException($"'{path}': expected Owner.key.");
            return (scope, path.Substring(0, dot).Trim(), path.Substring(dot + 1).Trim());
        }

        /// <summary>The names an owner of <paramref name="scope"/> may have in <paramref name="model"/> (none without one).</summary>
        private static IEnumerable<string> OwnerNames(SimulationConfig model, string scope)
        {
            if (model == null) yield break;
            switch (scope)
            {
                case "element": foreach (var e in model.Elements) yield return e.Name; break;
                case "team": foreach (var t in model.ResourceTeams) { yield return t.Name; yield return t.Id; } break;
                case "downtime": foreach (var d in model.Downtimes) yield return d.Id; break;
                case "reaction": foreach (var r in model.Reactions) yield return r.Id; break;
                case "data": foreach (var t in model.Tables) yield return t.Name; break;
                case "list": foreach (var l in model.Lists) yield return l.Name; break;
            }
        }
    }

    /// <summary>
    /// The number of members of a team that carry a label (e.g. welders: <c>skill=welding</c>): extra members
    /// copy the last member with that label (named team_n), fewer members remove the last ones with it; the others stay.
    /// </summary>
    public sealed class TeamPropertyMembersParameter : ModelParameter
    {
        public string Team { get; }
        public string Label { get; }
        public string Value { get; }
        public override DecisionVariable Variable { get; }

        public TeamPropertyMembersParameter(string team, string label, string value, int min, int max, string name = null)
        {
            Team = team; Label = label; Value = value;
            Variable = DecisionVariable.Integer(name ?? $"team:{team}.members[{label}={value}]", Math.Max(0, min), Math.Max(min, max));
        }

        public override void Apply(SimulationConfig config, Candidate candidate, int index)
        {
            var team = ModelParameterCatalog.TeamNamed(config, Team, "Study");
            int n = Math.Max(0, (int)Math.Round(candidate.Number(index)));
            string record = Label + "=" + Value;
            bool Has(ResourceMemberConfig m) => m.Properties != null && m.Properties.Exists(Carries);
            var with = team.Members.FindAll(Has);
            // the member's label record (name = expression) carries the path's value: a text (quoted or not) or a number
            if (with.Count == 0) throw new ArgumentException($"Study: no member of '{Team}' has {record} (one is needed as the template).");
            var template = with[with.Count - 1];
            while (with.Count > n) { team.Members.Remove(with[with.Count - 1]); with.RemoveAt(with.Count - 1); }
            while (with.Count < n)
            {
                var copy = ConfigCloner.DeepClone(template);
                copy.Name = ModelParameterCatalog.NewMemberName(team);
                team.Members.Add(copy);
                with.Add(copy);
            }
        }

        /// <summary>True when a member's label record sets <see cref="Label"/> to <see cref="Value"/> (constant records only;
        /// the value of the path may be quoted).</summary>
        private bool Carries(string record)
        {
            if (!LabelAssignments.TryConstant(record, out string name, out var v) || name != Label) return false;
            string want = Value.Trim();
            if (want.Length >= 2 && (want[0] == '\'' || want[0] == '"') && want[want.Length - 1] == want[0]) want = want.Substring(1, want.Length - 2);
            if (v.IsText) return v.Text == want;
            return double.TryParse(want, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) && d == v.Number;
        }
    }

    /// <summary>
    /// A schedule source's arrivals table from a file, one file per level (e.g. a push and a pull release plan):
    /// the level is the file path; the table is read when the run's config is built (<see cref="TableReader"/>, or the
    /// loader given).
    /// </summary>
    public sealed class SourceTableParameter : ModelParameter
    {
        public const string Key = "table";
        /// <summary>The source's table file in its config (where <see cref="Apply"/> writes the level; a factor on it is refused:
        /// the factor is <see cref="Key"/>, the one name).</summary>
        public const string FileKey = "fileName";
        public string Source { get; }
        private readonly Func<string, Dictionary<string, List<string>>> load;
        private readonly Dictionary<string, Dictionary<string, List<string>>> cache = new Dictionary<string, Dictionary<string, List<string>>>();
        public override DecisionVariable Variable { get; }

        public SourceTableParameter(string source, IEnumerable<string> files, Func<string, Dictionary<string, List<string>>> loader = null, string name = null)
        {
            Source = source;
            load = loader ?? TableReader.Read;
            Variable = DecisionVariable.Choice(name ?? source + "." + Key, files);
        }

        public override void Apply(SimulationConfig config, Candidate candidate, int index)
        {
            string file = candidate.Option(index);
            Dictionary<string, List<string>> table;
            lock (cache)
                if (!cache.TryGetValue(file, out table)) cache[file] = table = load(file);
            var e = ElementNamed(config, Source);
            var copy = new Dictionary<string, List<string>>();
            foreach (var kv in table) copy[kv.Key] = new List<string>(kv.Value);
            e.Set("arrivalsTable", copy);
            e.Set(FileKey, file);
            e.Remove("dataTable");     // the file given here wins over the source's data table / table source
            e.Remove("tableSource");
            if (e.Get<string>("mode") != "RateTable") e.Set("mode", "Schedule");   // a rate table stays one
        }
    }

    /// <summary>
    /// A data table's source per level: <c>data:Table.file</c> — each level a file (read with
    /// the study's loader: CSV / <c>.data</c>, Excel in Unity), or <c>data:Table.sql</c> — each level a SELECT on the
    /// project database. The run's config gets the table with that source and its snapshot (a query is run when the
    /// run's model is built), so every consumer of the table (sources, expressions…) sees the variant.
    /// </summary>
    public sealed class DataTableParameter : ModelParameter
    {
        public string Table { get; }
        public string Key { get; }
        private readonly Func<string, Dictionary<string, List<string>>> load;
        private readonly Dictionary<string, Dictionary<string, List<string>>> cache = new Dictionary<string, Dictionary<string, List<string>>>();
        public override DecisionVariable Variable { get; }

        public DataTableParameter(string table, string key, IEnumerable<string> levels, Func<string, Dictionary<string, List<string>>> loader = null, string name = null)
        {
            Table = table;
            Key = (key ?? "").Trim().ToLowerInvariant();
            if (Key != "file" && Key != "sql")
                throw new ArgumentException($"Factor 'data:{table}.{key}': a data table varies by 'file' or 'sql'.");
            load = loader ?? TableReader.Read;
            Variable = DecisionVariable.Choice(name ?? "data:" + table + "." + Key, levels);
        }

        public override void Apply(SimulationConfig config, Candidate candidate, int index)
        {
            string level = candidate.Option(index);
            var t = config.Tables.Find(x => x.Name == Table)
                    ?? throw new ArgumentException($"Factor 'data:{Table}.{Key}': no data table '{Table}' in the model (tables: " +
                                                   $"{(config.Tables.Count == 0 ? "none" : string.Join(", ", config.Tables.Select(x => x.Name)))}).");
            if (Key == "sql")
            {
                t.Source = DataSourceKind.Sql;
                t.Sql = level;
                t.Columns = null;   // queried when the run's model is built (DataResolver)
                return;
            }
            Dictionary<string, List<string>> columns;
            lock (cache)
                if (!cache.TryGetValue(level, out columns)) cache[level] = columns = load(level);
            t.Source = DataSourceKind.File;
            t.File = level;
            t.Columns = new Dictionary<string, List<string>>();
            foreach (var kv in columns) t.Columns[kv.Key] = new List<string>(kv.Value);
        }
    }
}
