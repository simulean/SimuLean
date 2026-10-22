using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Data;

namespace SimuLean.Results
{
    /// <summary>One run of a result set: a replication of a scenario, its named metrics and its statistics.</summary>
    public sealed class ResultRun
    {
        public string Scenario { get; set; } = "";
        /// <summary>1-based replication number within the scenario.</summary>
        public int Replication { get; set; } = 1;
        public int Seed { get; set; }
        /// <summary>Wall-clock seconds the run took (0 = not measured).</summary>
        public double ElapsedSeconds { get; set; }
        /// <summary>The user's named metrics of the run (Experimenter / Optimizer metrics).</summary>
        public Dictionary<string, double> Metrics { get; } = new Dictionary<string, double>();
        /// <summary>Every statistic of the run (null when not recorded).</summary>
        public RunResults Results { get; set; }
        /// <summary>Why the run failed (the model could not be built or measured; no metrics then), else null.</summary>
        public string Error { get; set; }

        /// <summary>The run's key in the tables: <c>scenario #replication</c>.</summary>
        public string Id => $"{Scenario} #{Replication}";
    }

    /// <summary>A scenario of a result set: its name and its parameter values (as text, in order).</summary>
    public sealed class ResultScenario
    {
        public string Name { get; set; } = "";
        public List<KeyValuePair<string, string>> Parameters { get; } = new List<KeyValuePair<string, string>>();
    }

    /// <summary>
    /// Everything an experiment, an optimisation or a single run produced, in one place: the scenarios and
    /// their parameters, the runs (replications) with their metrics and statistics (<see cref="RunResults"/>, with time
    /// series when recorded), metadata and extra tables (e.g. an optimiser's history). Its tables are the stable,
    /// tool-neutral format every exporter writes (<see cref="ResultExport"/>, <see cref="FormatVersion"/>):
    /// <c>metadata</c>, <c>scenarios</c>, <c>runs</c>, <c>metrics</c>, <c>metric_summary</c>, <c>results</c>,
    /// <c>summary</c>, <c>series</c> — long tables keyed by run / scenario, the statistic names of
    /// <see cref="ResultMetrics"/>.
    /// </summary>
    /// <summary>One evaluated candidate of an optimisation: its iteration, parameter values (text), fitness,
    /// metric means and the error when it failed.</summary>
    public sealed class ResultCandidate
    {
        public int Iteration { get; set; }
        public List<KeyValuePair<string, string>> Parameters { get; } = new List<KeyValuePair<string, string>>();
        public double Fitness { get; set; } = double.NaN;
        public Dictionary<string, double> Metrics { get; } = new Dictionary<string, double>();
        public string Error { get; set; }
    }

    public sealed class ResultSet
    {
        /// <summary>Identifies the table layout; bumped when a table or column changes meaning.</summary>
        public const string FormatVersion = "simulean-results/1";

        /// <summary>Name of the set: the file / folder name of the exports and the <c>set</c> key in a database.</summary>
        public string Name { get; set; } = "Results";
        /// <summary>What produced it: <c>experiment</c>, <c>optimization</c>, <c>run</c>.</summary>
        public string Source { get; set; } = "run";
        public string Model { get; set; } = "";
        public DateTime Created { get; set; } = DateTime.Now;
        /// <summary>Confidence level of the intervals across replications.</summary>
        public double Confidence { get; set; } = 0.95;

        public List<string> MetricNames { get; } = new List<string>();
        public List<ResultScenario> Scenarios { get; } = new List<ResultScenario>();
        public List<ResultRun> Runs { get; } = new List<ResultRun>();
        /// <summary>Extra key / values written to the metadata (settings, algorithm, hyperparameters…).</summary>
        public List<KeyValuePair<string, string>> Metadata { get; } = new List<KeyValuePair<string, string>>();
        /// <summary>Extra tables (e.g. <c>history</c> of an optimisation), exported after the standard ones.</summary>
        public List<ResultTable> Extra { get; } = new List<ResultTable>();
        /// <summary>An optimisation's every evaluated candidate (empty for experiments and runs).</summary>
        public List<ResultCandidate> Candidates { get; } = new List<ResultCandidate>();
        /// <summary>Its id in the results store (<see cref="ResultStore"/>, set by Save and Load); 0 = not stored. Not a table value.</summary>
        public int StoreId { get; set; }

        /// <summary>
        /// Several sets as one, to compare them: every scenario becomes <c>set · scenario</c> (with its parameters
        /// and runs), the metric names are joined; the runs keep their results, so every table and chart compares the sets.
        /// </summary>
        public static ResultSet Compare(IList<ResultSet> sets, string name = null)
        {
            var names = new List<string>();
            foreach (var s in sets) names.Add(s.Name);
            var all = new ResultSet { Name = name ?? "Compare " + string.Join(" vs ", names), Source = "comparison", Created = DateTime.Now };
            var models = new List<string>();
            foreach (var s in sets)
            {
                if (!models.Contains(s.Model ?? "")) models.Add(s.Model ?? "");
                all.Confidence = s.Confidence;
                all.Metadata.Add(new KeyValuePair<string, string>("compared", $"{s.Name} ({s.Source}, {s.Created:yyyy-MM-dd HH:mm})"));
                foreach (string m in s.MetricNames) if (!all.MetricNames.Contains(m)) all.MetricNames.Add(m);
                string Prefix(string scenario) => sets.Count == 1 ? scenario : $"{s.Name} · {scenario}";
                foreach (var sc in s.ScenarioOrder())
                {
                    var copy = new ResultScenario { Name = Prefix(sc.Name) };
                    copy.Parameters.AddRange(sc.Parameters);
                    all.Scenarios.Add(copy);
                }
                foreach (var r in s.Runs)
                {
                    var run = new ResultRun { Scenario = Prefix(r.Scenario), Replication = r.Replication, Seed = r.Seed, ElapsedSeconds = r.ElapsedSeconds, Results = r.Results };
                    foreach (var kv in r.Metrics) run.Metrics[kv.Key] = kv.Value;
                    all.Runs.Add(run);
                }
            }
            all.Model = string.Join(", ", models);
            return all;
        }

        /// <summary>The scenarios in run order (a scenario named by the runs but not listed comes last, without parameters).</summary>
        public List<ResultScenario> ScenarioOrder()
        {
            var list = new List<ResultScenario>(Scenarios);
            var known = new HashSet<string>();
            foreach (var s in Scenarios) known.Add(s.Name);
            foreach (var r in Runs) if (known.Add(r.Scenario)) list.Add(new ResultScenario { Name = r.Scenario });
            return list;
        }

        public List<ResultRun> RunsOf(string scenario) => Runs.FindAll(r => r.Scenario == scenario);

        /// <summary>The statistics of a scenario across its replications (null when no run recorded them).</summary>
        public ResultSummary SummaryOf(string scenario)
        {
            var list = new List<RunResults>();
            foreach (var r in RunsOf(scenario)) if (r.Results != null) list.Add(r.Results);
            return list.Count > 0 ? ResultSummary.Of(list, Confidence) : null;
        }

        // ── the standard tables ──────────────────────────────────────────────

        public ResultTable MetadataTable()
        {
            var t = new ResultTable("metadata", "key", "value");
            var info = FirstInfo();
            t.Add("format", FormatVersion);
            t.Add("name", Name);
            t.Add("source", Source);
            t.Add("model", Model);
            t.Add("created", Created.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            t.Add("engine", typeof(ResultSet).Assembly.GetName().Version?.ToString() ?? "");
            t.Add("confidence", Text(Confidence));
            t.Add("scenarios", ScenarioOrder().Count.ToString(CultureInfo.InvariantCulture));
            t.Add("runs", Runs.Count.ToString(CultureInfo.InvariantCulture));
            if (info != null)
            {
                t.Add("end_time", Text(info.EndTime));
                t.Add("warmup", Text(info.WarmupTime));
                t.Add("time_unit_seconds", Text(info.TimeUnitSeconds));
            }
            foreach (var kv in Metadata) t.Add(kv.Key, kv.Value ?? "");
            return t;
        }

        public ResultTable ScenariosTable()
        {
            var t = new ResultTable("scenarios", "scenario", "parameter", "value");
            foreach (var s in ScenarioOrder())
                foreach (var p in s.Parameters) t.Add(s.Name, p.Key, p.Value ?? "");
            return t;
        }

        public ResultTable RunsTable()
        {
            var t = new ResultTable("runs", "run", "scenario", "replication", "seed", "end_time", "warmup", "elapsed_s", "end_reason", "deadlocked");
            foreach (var r in Runs)
                t.Add(r.Id, r.Scenario, r.Replication, r.Seed, r.Results?.Info.EndTime, r.Results?.Info.WarmupTime, r.ElapsedSeconds,
                      r.Results?.Info.EndReason, r.Results == null ? null : (object)(r.Results.Info.Deadlocked ? 1 : 0));
            return t;
        }

        public ResultTable MetricsTable()
        {
            var t = new ResultTable("metrics", "run", "scenario", "replication", "metric", "value");
            foreach (var r in Runs)
                foreach (string m in MetricNames)
                    if (r.Metrics.TryGetValue(m, out double v)) t.Add(r.Id, r.Scenario, r.Replication, m, Num(v));
            return t;
        }

        public ResultTable MetricSummaryTable()
        {
            var t = new ResultTable("metric_summary", "scenario", "metric", "n", "mean", "std", "min", "max", "ci_low", "ci_high");
            foreach (var s in ScenarioOrder())
            {
                var runs = RunsOf(s.Name);
                foreach (string m in MetricNames)
                {
                    var values = new List<double>();
                    foreach (var r in runs) if (r.Metrics.TryGetValue(m, out double v)) values.Add(v);
                    if (values.Count == 0) continue;
                    var st = StatSummary.Of(values, Confidence);
                    t.Add(s.Name, m, st.N, Num(st.Mean), Num(st.StdDev), Num(st.Min), Num(st.Max), Ci(st, true), Ci(st, false));
                }
            }
            return t;
        }

        public ResultTable ResultsTable()
        {
            var t = new ResultTable("results", "run", "scenario", "replication", "scope", "name", "type", "group", "metric", "value");
            foreach (var r in Runs)
            {
                if (r.Results == null) continue;
                foreach (var o in r.Results.Objects)
                    foreach (var m in o.Metrics)
                        t.Add(r.Id, r.Scenario, r.Replication, Scope(o.Scope), o.Name, o.Type, o.Group, m.Key, Num(m.Value));
            }
            return t;
        }

        public ResultTable SummaryTable()
        {
            var t = new ResultTable("summary", "scenario", "scope", "name", "type", "group", "metric",
                                    "n", "mean", "std", "min", "max", "ci_low", "ci_high");
            foreach (var s in ScenarioOrder())
            {
                var summary = SummaryOf(s.Name);
                if (summary == null) continue;
                foreach (var o in summary.Objects)
                    foreach (string m in RunResults.Order(o.Metrics, o.Scope))
                    {
                        var st = o[m];
                        t.Add(s.Name, Scope(o.Scope), o.Name, o.Type, o.Group, m,
                              st.N, Num(st.Mean), Num(st.StdDev), Num(st.Min), Num(st.Max), Ci(st, true), Ci(st, false));
                    }
            }
            return t;
        }

        public ResultTable SeriesTable()
        {
            var t = new ResultTable("series", "run", "scenario", "replication", "scope", "name", "metric", "time", "value");
            foreach (var r in Runs)
            {
                if (r.Results == null) continue;
                foreach (var ts in r.Results.Series)
                    for (int i = 0; i < ts.Times.Count; i++)
                        t.Add(r.Id, r.Scenario, r.Replication, Scope(ts.Scope), ts.Name, ts.Metric, ts.Times[i], Num(ts.Values[i]));
            }
            return t;
        }

        /// <summary>The state intervals of the runs that recorded a timeline: <c>run, scenario, replication, scope,
        /// name, state, start, end</c>.</summary>
        public ResultTable TimelineTable()
        {
            var t = new ResultTable("timeline", "run", "scenario", "replication", "scope", "name", "state", "start", "end");
            foreach (var r in Runs)
            {
                if (r.Results == null) continue;
                foreach (var e in r.Results.Timeline) t.Add(r.Id, r.Scenario, r.Replication, Scope(e.Scope), e.Name, e.State, e.Start, e.End);
            }
            return t;
        }

        /// <summary>Where each item was and when (G3d): <c>run, scenario, replication, item, element, start, end</c>.</summary>
        public ResultTable JobsTable()
        {
            var t = new ResultTable("jobs", "run", "scenario", "replication", "item", "element", "start", "end");
            foreach (var r in Runs)
            {
                if (r.Results == null) continue;
                foreach (var j in r.Results.Jobs) t.Add(r.Id, r.Scenario, r.Replication, j.Item, j.Element, j.Start, j.End);
            }
            return t;
        }

        /// <summary>The optimisation's candidates: <c>candidate, iteration, fitness, error</c>, a column per parameter and per
        /// metric.</summary>
        public ResultTable CandidatesTable()
        {
            var parameters = new List<string>();
            var metrics = new List<string>();
            foreach (var c in Candidates)
            {
                foreach (var p in c.Parameters) if (!parameters.Contains(p.Key)) parameters.Add(p.Key);
                foreach (var m in c.Metrics.Keys) if (!metrics.Contains(m)) metrics.Add(m);
            }
            var columns = new List<string> { "candidate", "iteration", "fitness", "error" };
            columns.AddRange(parameters);
            columns.AddRange(metrics);
            var t = new ResultTable("candidates", columns.ToArray());
            for (int i = 0; i < Candidates.Count; i++)
            {
                var c = Candidates[i];
                var row = new List<object> { i + 1, c.Iteration, Num(c.Fitness), c.Error ?? "" };
                foreach (string p in parameters) { string v = null; foreach (var kv in c.Parameters) if (kv.Key == p) v = kv.Value; row.Add(v ?? ""); }
                foreach (string m in metrics) row.Add(c.Metrics.TryGetValue(m, out double v) ? Num(v) : null);
                t.Add(row.ToArray());
            }
            return t;
        }

        /// <summary>Every table of the set in export order (the standard ones that have rows, then <see cref="Extra"/>).</summary>
        public List<ResultTable> Tables()
        {
            var list = new List<ResultTable> { MetadataTable() };
            foreach (var t in new[] { ScenariosTable(), RunsTable(), MetricsTable(), MetricSummaryTable(), ResultsTable(), SummaryTable(), SeriesTable(), TimelineTable(), JobsTable(), CandidatesTable() })
                if (t.RowCount > 0) list.Add(t);
            list.AddRange(Extra);
            return list;
        }

        private RunInfo FirstInfo()
        {
            foreach (var r in Runs) if (r.Results != null) return r.Results.Info;
            return null;
        }

        internal static string Scope(ResultScope s) => s.ToString().ToLowerInvariant();
        private static string Text(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        // missing / undefined numbers are empty cells (null), never NaN text
        internal static object Num(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : (object)v;
        private static object Ci(StatSummary st, bool low) => st.HasInterval ? (object)(low ? st.Lower : st.Upper) : null;
    }
}
