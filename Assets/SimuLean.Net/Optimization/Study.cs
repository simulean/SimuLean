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
    /// label, <see cref="TeamLabelMembersParameter"/>), <c>downtime:id.key</c>, <c>Source.table</c> (a schedule
    /// source's arrivals table read from a file per level, <see cref="SourceTableParameter"/>), and
    /// <c>data:Table.file</c> / <c>data:Table.sql</c> (a data table's source per level, <see cref="DataTableParameter"/>).
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
        /// <summary>Every run also keeps its state intervals (the Gantt, G3c; opt-in: the results grow with every state change).</summary>
        public bool RecordTimeline { get; set; }
        /// <summary>Record every statistic of each run (<see cref="RunResults"/>); off = only the metrics.</summary>
        public bool RecordStatistics { get; set; } = true;
        /// <summary>Runs at once (0 = <see cref="DefaultThreads"/>).</summary>
        public int Threads { get; set; }

        /// <summary>Runs at once by default: one per processor, at most 8 — the runs are bound by allocation and garbage
        /// collection, and 16 threads were slower than 8 for resource models (review2 #21, perfbench <c>study</c>).</summary>
        public static int DefaultThreads => Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
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
            Metrics.Add(ParseMetric(name, path));
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
                try { f = Split(path); } catch (ArgumentException) { continue; }
                string used = f.scope == "element" ? ModelParameterCatalog.InactiveSamplerKey(model, f.owner, f.key) : null;
                if (used == null) continue;
                bool present = model.Elements.Exists(e => e.Name == f.owner && e.Parameters.ContainsKey(f.key));
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
                foreach (var m in Metrics) check.Add(true, MetricCatalog.Problem(m, results, built));
            }
            return check;
        }

        /// <summary>Runs every scenario × replication headless (in parallel) and returns the result set.
        /// <paramref name="progress"/> gets (runs done, runs in all).</summary>
        public ResultSet Run(CancellationToken token = default, Action<int, int> progress = null)
        {
            var objective = Objective(out var plan);
            var runs = new List<(StudyScenario s, int rep)>();
            foreach (var s in plan) for (int r = 1; r <= Math.Max(1, s.Replications > 0 ? s.Replications : Replications); r++) runs.Add((s, r));
            var done = new ResultRun[runs.Count];
            int count = 0;
            bool record = RecordStatistics || SeriesInterval > 0 || RecordTimeline;
            bool cancelled = false;
            try
            {
            Parallel.For(0, runs.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Threads > 0 ? Threads : DefaultThreads, CancellationToken = token },
                k =>
                {
                    var (s, rep) = runs[k];
                    var watch = Stopwatch.StartNew();
                    RunResults results = null;
                    int seed = BaseSeed + rep - 1;
                    var run = new ResultRun { Scenario = s.Name, Replication = rep, Seed = seed };
                    // a run that fails (a level the model cannot be built with, a metric it cannot read) is recorded
                    // with its error; the others carry on and are kept (one failure lost them all)
                    try
                    {
                        var e = objective.Run(CandidateOf(objective, s), seed, record ? r => results = r : (Action<RunResults>)null);
                        foreach (var kv in e.Metrics) run.Metrics[kv.Key] = kv.Value;
                        run.Results = results;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { run.Error = ex.GetType().Name + ": " + ex.Message; }
                    run.ElapsedSeconds = watch.Elapsed.TotalSeconds;
                    done[k] = run;
                    progress?.Invoke(Interlocked.Increment(ref count), runs.Count);
                });
            }
            catch (OperationCanceledException) { cancelled = true; }   // the runs done so far are kept

            var set = new ResultSet { Name = Name, Source = "experiment", Created = DateTime.Now, Confidence = Confidence };
            set.MetricNames.AddRange(Metrics.Select(m => m.Name));
            foreach (var s in plan)
            {
                var sc = new ResultScenario { Name = s.Name };
                for (int i = 0; i < s.Values.Count; i++) sc.Parameters.Add(new KeyValuePair<string, string>(FactorLabel(i), s.Values[i]));
                set.Scenarios.Add(sc);
            }
            foreach (var r in done) if (r != null) set.Runs.Add(r);
            if (cancelled) set.Metadata.Add(new KeyValuePair<string, string>("cancelled", "true"));
            var failedRuns = set.Runs.Where(r => r.Error != null).ToList();
            if (failedRuns.Count > 0)
            {
                set.Metadata.Add(new KeyValuePair<string, string>("failed_runs", failedRuns.Count.ToString(CultureInfo.InvariantCulture)));
                set.Metadata.Add(new KeyValuePair<string, string>("first_error", failedRuns[0].Id + ": " + failedRuns[0].Error));
            }
            set.Metadata.Add(new KeyValuePair<string, string>("replications", Replications.ToString(CultureInfo.InvariantCulture)));
            set.Metadata.Add(new KeyValuePair<string, string>("base_seed", BaseSeed.ToString(CultureInfo.InvariantCulture)));
            set.Metadata.Add(new KeyValuePair<string, string>("run_time", objective.RunTime.ToString("R", CultureInfo.InvariantCulture)));
            return set;
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
                catch (FormatException e) { throw new InvalidOperationException($"Scenario '{s.Name}': {e.Message}"); }
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

        private static string ShortValue(string v)
            => v != null && (v.Contains("/") || v.Contains("\\")) ? System.IO.Path.GetFileNameWithoutExtension(v) : v;

        /// <summary>A factor path as a model parameter whose range covers <paramref name="values"/>.</summary>
        private ModelParameter ParseParameter(string path, List<string> levels, IEnumerable<string> values)
        {
            var (scope, owner, key) = Split(path);
            if (scope == "team")
            {
                int br = key.IndexOf('[');
                if (br > 0 && key.EndsWith("]"))
                {
                    string label = key.Substring(br + 1, key.Length - br - 2);
                    int eq = label.IndexOf('=');
                    if (key.Substring(0, br) != ModelParameterCatalog.TeamMembers || eq <= 0)
                        throw new ArgumentException($"Factor '{path}': use team:Team.members[label=value].");
                    var (lo, hi) = Range(path, values);
                    return new TeamLabelMembersParameter(owner, label.Substring(0, eq).Trim(), label.Substring(eq + 1).Trim(), (int)lo, (int)hi, path);
                }
                return Ranged(model, ParameterScope.Team, owner, key, path, values);
            }
            if (scope == "downtime") return Ranged(model, ParameterScope.Downtime, owner, key, path, values);
            if (scope == "reaction") return Ranged(model, ParameterScope.Reaction, owner, key, path, values);
            if (scope == "data")
            {
                if (!model.Tables.Exists(t => t.Name == owner))
                    throw new ArgumentException($"Factor '{path}': no data table '{owner}' in the model (tables: " +
                                                $"{(model.Tables.Count == 0 ? "none" : string.Join(", ", model.Tables.Select(t => t.Name)))}).");
                return new DataTableParameter(owner, key, levels, TableLoader, path);
            }
            if (scope != "element") throw new ArgumentException($"Factor '{path}': unknown scope '{scope}' (element, team, downtime, reaction, data).");
            if (key == SourceTableParameter.Key || key == SourceTableParameter.FileKey) return new SourceTableParameter(owner, levels, TableLoader, path);
            return Ranged(model, ParameterScope.Element, owner, key, path, values);
        }

        private static ModelParameter Ranged(SimulationConfig model, ParameterScope scope, string owner, string key, string path, IEnumerable<string> values)
        {
            var p = ConfigParameter.Of(model, scope, owner, key, name: path);
            var kind = p.Variable.Kind;
            if (kind != VariableKind.Integer && kind != VariableKind.Real) return p;
            var (lo, hi) = Range(path, values);
            return ConfigParameter.Of(model, scope, owner, key, lo, hi, path);
        }

        private static (double lo, double hi) Range(string path, IEnumerable<string> values)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (string v in values)
                foreach (string part in (v ?? "").Trim('[', ']').Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        throw new FormatException($"Factor '{path}': '{part.Trim()}' is not a number.");
                    lo = Math.Min(lo, d); hi = Math.Max(hi, d);
                }
            return double.IsInfinity(lo) ? (0, 0) : (lo, hi);
        }

        private static ModelMetric ParseMetric(string name, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"Metric '{name}' needs a path.");
            path = path.Trim();
            if (path.StartsWith("model.", StringComparison.OrdinalIgnoreCase))
                return new ModelMetric(name, MetricScope.Model, "model", path.Substring(6));
            var (scope, owner, key) = Split(path);
            switch (scope)
            {
                case "element": return new ModelMetric(name, MetricScope.Element, owner, key);
                case "team": return new ModelMetric(name, MetricScope.Team, owner, key);
                case "list": return new ModelMetric(name, MetricScope.List, owner, key);
                case "reaction": return new ModelMetric(name, MetricScope.Reaction, owner, key);
            }
            throw new ArgumentException($"Metric '{name}': unknown scope '{scope}' (model, element, team, list, reaction)." +
                                        (scope == "resource" || scope == "member"
                                             ? " A team member's statistics are read through its team: 'team:Team." + ResultMetrics.Utilization + "'."
                                             : ""));
        }

        // "team:Welders.members" → (team, Welders, members); "Yard.capacity" → (element, Yard, capacity);
        // "Exit.tally:time_in_system:p90" → (element, Exit, tally:time_in_system:p90): a scope only before the first dot
        private static (string scope, string owner, string key) Split(string path)
        {
            string scope = "element";
            int colon = path.IndexOf(':');
            int firstDot = path.IndexOf('.');
            if (colon > 0 && (firstDot < 0 || colon < firstDot)) { scope = path.Substring(0, colon).Trim().ToLowerInvariant(); path = path.Substring(colon + 1); }
            int dot = path.IndexOf('.');
            if (dot <= 0 || dot == path.Length - 1) throw new ArgumentException($"'{path}': expected Owner.key.");
            return (scope, path.Substring(0, dot).Trim(), path.Substring(dot + 1).Trim());
        }
    }

    /// <summary>
    /// The number of members of a team that carry a label (e.g. welders: <c>skill=welding</c>): extra members
    /// copy the last member with that label (named team_n), fewer members remove the last ones with it; the others stay.
    /// </summary>
    public sealed class TeamLabelMembersParameter : ModelParameter
    {
        public string Team { get; }
        public string Label { get; }
        public string Value { get; }
        public override DecisionVariable Variable { get; }

        public TeamLabelMembersParameter(string team, string label, string value, int min, int max, string name = null)
        {
            Team = team; Label = label; Value = value;
            Variable = DecisionVariable.Integer(name ?? $"team:{team}.members[{label}={value}]", Math.Max(0, min), Math.Max(min, max));
        }

        public override void Apply(SimulationConfig config, Candidate candidate, int index)
        {
            var team = ModelParameterCatalog.TeamNamed(config, Team, "Study");
            int n = Math.Max(0, (int)Math.Round(candidate.Number(index)));
            string record = Label + "=" + Value;
            bool Has(ResourceMemberConfig m) => m.Labels != null && m.Labels.Exists(l => l != null && l.Replace(" ", "") == record.Replace(" ", ""));
            var with = team.Members.FindAll(Has);
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
    }

    /// <summary>
    /// A schedule source's arrivals table from a file, one file per level (e.g. a push and a pull release plan):
    /// the level is the file path; the table is read when the run's config is built (<see cref="TableReader"/>, or the
    /// loader given).
    /// </summary>
    public sealed class SourceTableParameter : ModelParameter
    {
        public const string Key = "table";
        /// <summary>The source's table file (its catalog key, <c>ParameterSchema.File</c>; follow-up #29): the same factor.</summary>
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
            e.Parameters["arrivalsTable"] = copy;
            e.Parameters[FileKey] = file;
            e.Parameters.Remove("dataTable");     // the file given here wins over the source's data table / table source
            e.Parameters.Remove("tableSource");
            e.Parameters["sourceMode"] = "Schedule";
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
