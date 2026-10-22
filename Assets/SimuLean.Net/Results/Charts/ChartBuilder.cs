using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Data;

namespace SimuLean.Results.Charts
{
    /// <summary>
    /// Builds a chart's data from a result set's long tables — <c>metrics</c>, <c>results</c>,
    /// <c>scenarios</c>, <c>runs</c>, <c>series</c> — so a live set, a stored set (<see cref="ResultStore.Load"/>) and a
    /// <c>.json</c> export give the same chart. Read-only; a name not found gives an empty chart whose
    /// <see cref="ChartData.Message"/> lists what there is.
    /// </summary>
    public static class ChartBuilder
    {
        public static ChartData Build(ChartQuery spec, ResultSet set)
        {
            var tables = new Dictionary<string, ResultTable>();
            foreach (var t in set.Tables()) tables[t.Name] = t;
            return Build(spec, tables);
        }

        public static ChartData Build(ChartQuery spec, IReadOnlyDictionary<string, ResultTable> tables)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            var data = new ChartData { Kind = spec.Kind, ValueLabels = spec.ValueLabels, Title = string.IsNullOrWhiteSpace(spec.Title) ? DefaultTitle(spec) : spec.Title };
            try
            {
                switch (spec.Kind)
                {
                    case ChartKind.Bar: case ChartKind.Box: Categorical(spec, tables, data); break;
                    case ChartKind.Histogram: Histogram(spec, tables, data); break;
                    case ChartKind.Scatter: Scatter(spec, tables, data); break;
                    case ChartKind.Line: Line(spec, tables, data); break;
                    case ChartKind.States: States(spec, tables, data); break;
                    case ChartKind.Gantt: Gantt(spec, tables, data); break;
                    case ChartKind.JobGantt: JobGantt(spec, tables, data); break;
                    default: throw new ChartProblem($"A {spec.Kind} chart is drawn from live data (the element inspector, dashboards), not from results.");
                }
            }
            catch (ChartProblem p) { data.Message = p.Message; data.Categories.Clear(); data.Series.Clear(); }
            if (data.Subtitle == null && !data.IsEmpty) data.Subtitle = Subtitle(spec, data);
            return data;
        }

        private static string Subtitle(ChartQuery spec, ChartData data)
        {
            switch (spec.Kind)
            {
                case ChartKind.Bar:   // one run: no interval to speak of (review2 #36)
                    return data.Series.All(x => x.Low.All(double.IsNaN)) ? "one run"
                         : $"mean ± {spec.Confidence * 100:0.#} % confidence interval over the replications";
                case ChartKind.Box: return "min, quartiles, median and max over the replications";
                case ChartKind.Histogram: return $"{data.Series.Sum(x => x.Points.Sum(p => p.y)):0} values in {data.Series.FirstOrDefault()?.Points.Count ?? 0} bins";
                case ChartKind.Scatter: return $"one point per run ({data.Series.Sum(x => x.Points.Count)})";
                case ChartKind.Line: return $"one line per run ({data.Series.Count})";
                case ChartKind.States: return "mean share of time over the replications";
                case ChartKind.Gantt: case ChartKind.JobGantt: return data.Subtitle;
                default: return null;
            }
        }

        private static string DefaultTitle(ChartQuery s)
        {
            switch (s.Kind)
            {
                case ChartKind.Scatter: return $"{s.Y.Label} against {s.X.Label}";
                case ChartKind.States: return $"Time per state ({s.Y.Scope})";
                case ChartKind.Gantt: return $"State timeline ({s.Y.Scope})";
                case ChartKind.JobGantt: return "Jobs over time";
                case ChartKind.Line: return $"{s.Y.Label} over time";
                case ChartKind.Histogram: return $"Distribution of {s.Y.Label}";
                default: return $"{s.Y.Label} by {s.GroupBy.ToString().ToLowerInvariant()}";
            }
        }

        private sealed class ChartProblem : Exception { public ChartProblem(string m) : base(m) { } }

        /// <summary>One value of one run (and one object, for a statistic).</summary>
        private struct Obs
        {
            public string Run, Scenario, Object;
            public int Replication;
            public double Value;
        }

        // ── categories: bar, box ──────────────────────────────────────────────

        private static void Categorical(ChartQuery spec, IReadOnlyDictionary<string, ResultTable> tables, ChartData data)
        {
            var obs = Observations(spec.Y, spec, tables);
            data.YLabel = spec.Y.Label;
            data.XLabel = spec.GroupBy.ToString().ToLowerInvariant();
            string Cat(Obs o) => spec.GroupBy == ChartGroup.Scenario ? o.Scenario : spec.GroupBy == ChartGroup.Object ? o.Object : $"{o.Scenario} #{o.Replication}";
            string Ser(Obs o) => spec.GroupBy == ChartGroup.Object ? o.Scenario : o.Object;
            data.Categories.AddRange(obs.Select(Cat).Distinct());
            var seriesNames = obs.Select(Ser).Distinct().ToList();
            bool single = seriesNames.Count == 1;
            foreach (string sn in seriesNames)
            {
                var s = new ChartSeries { Name = single ? spec.Y.Label : sn };
                int n = data.Categories.Count;
                s.Values = new double[n]; s.Low = new double[n]; s.High = new double[n]; s.Boxes = new BoxStats[n];
                for (int c = 0; c < n; c++)
                {
                    var values = obs.Where(o => Ser(o) == sn && Cat(o) == data.Categories[c]).Select(o => o.Value).ToList();
                    var st = StatSummary.Of(values, spec.Confidence);
                    s.Values[c] = values.Count > 0 ? st.Mean : double.NaN;
                    s.Low[c] = st.HasInterval ? st.Lower : double.NaN;
                    s.High[c] = st.HasInterval ? st.Upper : double.NaN;
                    s.Boxes[c] = Box(values);
                }
                data.Series.Add(s);
            }
        }

        /// <summary>Min, quartiles (linear interpolation between order statistics, as numpy's default), median, max.</summary>
        public static BoxStats Box(IList<double> values)
        {
            if (values.Count == 0) return new BoxStats { Min = double.NaN, Q1 = double.NaN, Median = double.NaN, Q3 = double.NaN, Max = double.NaN };
            var v = values.OrderBy(x => x).ToArray();
            double Q(double p)
            {
                double h = (v.Length - 1) * p;
                int lo = (int)Math.Floor(h);
                return lo + 1 < v.Length ? v[lo] + (h - lo) * (v[lo + 1] - v[lo]) : v[lo];
            }
            return new BoxStats { N = v.Length, Min = v[0], Q1 = Q(0.25), Median = Q(0.5), Q3 = Q(0.75), Max = v[v.Length - 1] };
        }

        // ── histogram ─────────────────────────────────────────────────────────

        private static void Histogram(ChartQuery spec, IReadOnlyDictionary<string, ResultTable> tables, ChartData data)
        {
            var obs = Observations(spec.Y, spec, tables);
            data.XLabel = spec.Y.Label;
            data.YLabel = "count";
            double min = obs.Min(o => o.Value), max = obs.Max(o => o.Value);
            int bins = spec.Bins > 0 ? spec.Bins : (int)Math.Ceiling(Math.Log(obs.Count, 2)) + 1;
            double width = max > min ? (max - min) / bins : 1;
            if (!(max > min)) { bins = 1; min -= 0.5; }
            data.BinWidth = width;
            var scenarios = obs.Select(o => o.Scenario).Distinct().ToList();
            for (int k = 0; k < scenarios.Count; k++)
            {
                var counts = new int[bins];
                foreach (var o in obs.Where(o => o.Scenario == scenarios[k]))
                    counts[Math.Min(bins - 1, Math.Max(0, (int)((o.Value - min) / width)))]++;
                var s = new ChartSeries { Name = scenarios.Count == 1 ? spec.Y.Label : scenarios[k] };
                for (int b = 0; b < bins; b++) s.Points.Add((min + b * width, counts[b]));
                data.Series.Add(s);
            }
        }

        // ── scatter ───────────────────────────────────────────────────────────

        private static void Scatter(ChartQuery spec, IReadOnlyDictionary<string, ResultTable> tables, ChartData data)
        {
            var x = PerRun(Observations(spec.X, spec, tables));
            var y = PerRun(Observations(spec.Y, spec, tables));
            data.XLabel = spec.X.Label;
            data.YLabel = spec.Y.Label;
            var bySeries = new Dictionary<string, ChartSeries>();
            foreach (var kv in y)
            {
                if (!x.TryGetValue(kv.Key, out var xo)) continue;
                if (!bySeries.TryGetValue(kv.Value.Scenario, out var s))
                {
                    bySeries[kv.Value.Scenario] = s = new ChartSeries { Name = kv.Value.Scenario };
                    data.Series.Add(s);
                }
                s.Points.Add((xo.Value, kv.Value.Value));
            }
            if (data.Series.Count == 0) throw new ChartProblem($"No run has both {spec.X.Label} and {spec.Y.Label}.");
            if (data.Series.Count > 12)   // a value per scenario (an optimisation's candidates): one colour
            {
                var all = new ChartSeries { Name = "runs" };
                foreach (var s in data.Series) all.Points.AddRange(s.Points);
                data.Series.Clear();
                data.Series.Add(all);
            }
        }

        /// <summary>A run's value: the mean over the objects matched (one value per run).</summary>
        private static Dictionary<string, Obs> PerRun(List<Obs> obs)
        {
            var map = new Dictionary<string, Obs>();
            foreach (var g in obs.GroupBy(o => o.Run))
            {
                var first = g.First();
                first.Value = g.Average(o => o.Value);
                map[g.Key] = first;
            }
            return map;
        }

        // ── line: recorded series ─────────────────────────────────────────────

        private static void Line(ChartQuery spec, IReadOnlyDictionary<string, ResultTable> tables, ChartData data)
        {
            var t = Table(tables, "series", "No time series in these results: record them (the clock's or the study's series interval).");
            int cRun = Col(t, "run"), cScen = Col(t, "scenario"), cRep = Col(t, "replication"), cScope = Col(t, "scope"), cName = Col(t, "name"),
                cMetric = Col(t, "metric"), cTime = Col(t, "time"), cValue = Col(t, "value");
            var objects = Patterns(spec.Y.Objects);
            var scenarios = Patterns(spec.Scenarios);
            var lines = new Dictionary<string, ChartSeries>();
            var metricsSeen = new HashSet<string>();
            foreach (var r in t.Rows)
            {
                if (!Same(Str(r[cScope]), spec.Y.Scope) || !Matches(objects, Str(r[cName])) || !Matches(scenarios, Str(r[cScen]))) continue;
                metricsSeen.Add(Str(r[cMetric]));
                if (Str(r[cMetric]) != spec.Y.Name) continue;
                double v = Num(r[cValue]);
                string key = Str(r[cRun]) + "\u0001" + Str(r[cName]);
                if (!lines.TryGetValue(key, out var s))
                {
                    lines[key] = s = new ChartSeries { Name = $"{Str(r[cScen])} #{Str(r[cRep])}\u0001{Str(r[cName])}" };
                    data.Series.Add(s);
                }
                if (!double.IsNaN(v)) s.Points.Add((Num(r[cTime]), v));
            }
            if (data.Series.Count == 0)
                throw new ChartProblem(metricsSeen.Count == 0 ? $"No series of {spec.Y.Scope} objects{Which(spec.Y.Objects)}."
                                                              : $"No series '{spec.Y.Name}'; recorded: {string.Join(", ", metricsSeen.OrderBy(m => m))}.");
            bool oneObject = data.Series.Select(s => s.Name.Split('\u0001')[1]).Distinct().Count() == 1;
            bool oneRun = data.Series.Select(s => s.Name.Split('\u0001')[0]).Distinct().Count() == 1;
            foreach (var s in data.Series)
            {
                var parts = s.Name.Split('\u0001');
                s.Name = oneObject ? parts[0] : oneRun ? parts[1] : $"{parts[0]} · {parts[1]}";
            }
            data.XLabel = "time";
            data.YLabel = spec.Y.Label;
        }

        // ── states ────────────────────────────────────────────────────────────

        private static void States(ChartQuery spec, IReadOnlyDictionary<string, ResultTable> tables, ChartData data)
        {
            var t = Table(tables, "results", "No statistics per object in these results.");
            int cScen = Col(t, "scenario"), cScope = Col(t, "scope"), cName = Col(t, "name"), cMetric = Col(t, "metric"), cValue = Col(t, "value"), cRun = Col(t, "run");
            var objects = Patterns(spec.Y.Objects);
            var scenarios = Patterns(spec.Scenarios);
            var shares = new Dictionary<(string cat, string state), List<double>>();
            var categories = new List<(string scenario, string name)>();
            var runsOf = new Dictionary<(string, string), HashSet<string>>();
            var states = new List<string>();
            foreach (var r in t.Rows)
            {
                string metric = Str(r[cMetric]);
                if (!ResultMetrics.IsState(metric) || !Same(Str(r[cScope]), spec.Y.Scope) || !Matches(objects, Str(r[cName])) || !Matches(scenarios, Str(r[cScen]))) continue;
                var cat = (Str(r[cScen]), Str(r[cName]));
                if (!runsOf.TryGetValue(cat, out var runs)) { runsOf[cat] = runs = new HashSet<string>(); categories.Add(cat); }
                runs.Add(Str(r[cRun]));
                string state = metric.Substring(ResultMetrics.StatePrefix.Length);
                if (!states.Contains(state)) states.Add(state);
                var key = (cat.Item1 + "\u0001" + cat.Item2, state);
                if (!shares.TryGetValue(key, out var list)) shares[key] = list = new List<double>();
                list.Add(Num(r[cValue]));
            }
            if (categories.Count == 0) throw new ChartProblem($"No time per state of {spec.Y.Scope} objects{Which(spec.Y.Objects)}.");
            bool oneScenario = categories.Select(c => c.scenario).Distinct().Count() == 1;
            foreach (var c in categories) data.Categories.Add(oneScenario ? c.name : $"{c.name} · {c.scenario}");   // the object first: it survives a cut
            foreach (string state in states.OrderBy(ChartPalette.StateRank).ThenBy(s => states.IndexOf(s)))
            {
                var s = new ChartSeries { Name = state, Color = ChartPalette.StateColor(state), Values = new double[categories.Count] };
                for (int i = 0; i < categories.Count; i++)
                {
                    // the mean share over the category's runs (a run without the state counts as 0)
                    var key = (categories[i].scenario + "\u0001" + categories[i].name, state);
                    double sum = shares.TryGetValue(key, out var list) ? list.Where(v => !double.IsNaN(v)).Sum() : 0;
                    s.Values[i] = sum / runsOf[categories[i]].Count;
                }
                data.Series.Add(s);
            }
            data.XLabel = spec.Y.Scope;
            data.YLabel = "share of time";
        }

        // ── Gantt: one run's state intervals ──────────────────────────────────

        private static void Gantt(ChartQuery spec, IReadOnlyDictionary<string, ResultTable> tables, ChartData data)
        {
            var t = Table(tables, "timeline", "No timeline in these results: switch on Record timeline (the clock, the Experimenter, " +
                                              "the Optimizer or the Study) and run again.");
            int cScen = Col(t, "scenario"), cRep = Col(t, "replication"), cScope = Col(t, "scope"), cName = Col(t, "name"),
                cState = Col(t, "state"), cStart = Col(t, "start"), cEnd = Col(t, "end");
            var scenarios = Patterns(spec.Scenarios);
            var objects = Patterns(spec.Y.Objects);
            // the run: the first scenario kept, its replication (or the first one)
            string scenario = null;
            int replication = 0;
            foreach (var r in t.Rows)
            {
                if (!Matches(scenarios, Str(r[cScen]))) continue;
                if (scenario == null) scenario = Str(r[cScen]);
                if (Str(r[cScen]) != scenario) continue;
                int rep = Int(r[cRep]);
                if (spec.Replication > 0 ? rep == spec.Replication : replication == 0 || rep < replication) replication = rep;
            }
            if (scenario == null) throw new ChartProblem($"No timeline of the scenarios '{spec.Scenarios}'.");
            if (replication == 0) throw new ChartProblem($"No replication {spec.Replication} of '{scenario}' in the timeline.");
            bool window = spec.To > spec.From;
            var rows = new Dictionary<string, int>();
            var states = new List<string>();
            var raw = new List<(int row, string state, double start, double end)>();
            double t0 = double.MaxValue, t1 = double.MinValue;
            bool anyScope = false;
            foreach (var r in t.Rows)
            {
                if (Str(r[cScen]) != scenario || Int(r[cRep]) != replication || !Same(Str(r[cScope]), spec.Y.Scope)) continue;
                anyScope = true;
                string name = Str(r[cName]);
                if (!Matches(objects, name)) continue;
                double start = Num(r[cStart]), end = Num(r[cEnd]);
                if (window) { start = Math.Max(start, spec.From); end = Math.Min(end, spec.To); }
                if (!(end > start)) continue;
                if (!rows.TryGetValue(name, out int row)) { rows[name] = row = data.Categories.Count; data.Categories.Add(name); }
                string state = Str(r[cState]);
                if (!states.Contains(state)) states.Add(state);
                raw.Add((row, state, start, end));
                t0 = Math.Min(t0, start); t1 = Math.Max(t1, end);
            }
            if (raw.Count == 0) throw new ChartProblem(anyScope ? $"No {spec.Y.Scope} objects{Which(spec.Y.Objects)} in the timeline." : $"No {spec.Y.Scope} objects in the timeline.");
            var ordered = states.OrderBy(ChartPalette.StateRank).ThenBy(x => states.IndexOf(x)).ToList();
            foreach (string st in ordered) data.Series.Add(new ChartSeries { Name = st, Color = ChartPalette.StateColor(st) });
            foreach (var (row, state, start, end) in raw) data.Segments.Add(new GanttSegment(row, ordered.IndexOf(state), start, end));
            data.TimeFrom = window ? spec.From : t0;
            data.TimeTo = window ? spec.To : t1;
            data.XLabel = "time";
            data.Subtitle = $"{scenario} · replication {replication} · {data.Categories.Count} {spec.Y.Scope} objects";
        }

        /// <summary>The run a Gantt shows: the first scenario the filter keeps, its replication (or the first one).</summary>
        private static (string scenario, int replication) PickRun(ChartQuery spec, ResultTable t, int cScen, int cRep)
        {
            var scenarios = Patterns(spec.Scenarios);
            string scenario = null;
            int replication = 0;
            foreach (var r in t.Rows)
            {
                if (!Matches(scenarios, Str(r[cScen]))) continue;
                if (scenario == null) scenario = Str(r[cScen]);
                if (Str(r[cScen]) != scenario) continue;
                int rep = Int(r[cRep]);
                if (spec.Replication > 0 ? rep == spec.Replication : replication == 0 || rep < replication) replication = rep;
            }
            if (scenario == null) throw new ChartProblem($"No timeline of the scenarios '{spec.Scenarios}'.");
            if (replication == 0) throw new ChartProblem($"No replication {spec.Replication} of '{scenario}' in the timeline.");
            return (scenario, replication);
        }

        // ── job Gantt: where each item was (G3d) ──────────────────────────────

        private static void JobGantt(ChartQuery spec, IReadOnlyDictionary<string, ResultTable> tables, ChartData data)
        {
            var t = Table(tables, "jobs", "No job timeline in these results: switch on Record timeline (the clock, the Experimenter, " +
                                          "the Optimizer or the Study) and run again.");
            int cScen = Col(t, "scenario"), cRep = Col(t, "replication"), cItem = Col(t, "item"), cEl = Col(t, "element"), cStart = Col(t, "start"), cEnd = Col(t, "end");
            var (scenario, replication) = PickRun(spec, t, cScen, cRep);
            var items = Patterns(spec.Y.Objects);
            bool window = spec.To > spec.From;
            var raw = new List<(string item, string element, double start, double end)>();
            foreach (var r in t.Rows)
            {
                if (Str(r[cScen]) != scenario || Int(r[cRep]) != replication || !Matches(items, Str(r[cItem]))) continue;
                double start = Num(r[cStart]), end = Num(r[cEnd]);
                if (window) { start = Math.Max(start, spec.From); end = Math.Min(end, spec.To); }
                if (end > start) raw.Add((Str(r[cItem]), Str(r[cEl]), start, end));
            }
            if (raw.Count == 0) throw new ChartProblem($"No item{Which(spec.Y.Objects)} in the job timeline of '{scenario}'.");
            // rows by first arrival; a colour per element, in the order the items meet them
            var rows = new Dictionary<string, int>();
            foreach (var g in raw.GroupBy(x => x.item).OrderBy(g => g.Min(x => x.start)))
            {
                rows[g.Key] = data.Categories.Count;
                data.Categories.Add(g.Key);
            }
            // colours in flow order: an element's mean position along the items' routes (then its first arrival)
            var rank = new Dictionary<string, (double sum, int n, double first)>();
            foreach (var g in raw.GroupBy(x => x.item))
            {
                int k = 0;
                foreach (var x in g.OrderBy(x => x.start))
                {
                    rank.TryGetValue(x.element, out var v);
                    rank[x.element] = (v.sum + k++, v.n + 1, v.n == 0 ? x.start : Math.Min(v.first, x.start));
                }
            }
            var elements = new Dictionary<string, int>();
            foreach (var kv in rank.OrderBy(kv => kv.Value.sum / kv.Value.n).ThenBy(kv => kv.Value.first))
            {
                elements[kv.Key] = data.Series.Count;
                data.Series.Add(new ChartSeries { Name = kv.Key });
            }
            foreach (var x in raw) data.Segments.Add(new GanttSegment(rows[x.item], elements[x.element], x.start, x.end));
            data.TimeFrom = window ? spec.From : raw.Min(x => x.start);
            data.TimeTo = window ? spec.To : raw.Max(x => x.end);
            data.XLabel = "time";
            data.Subtitle = $"{scenario} · replication {replication} · {data.Categories.Count} items";
        }

        // ── reading values ────────────────────────────────────────────────────

        private static List<Obs> Observations(ChartValue value, ChartQuery spec, IReadOnlyDictionary<string, ResultTable> tables)
        {
            if (string.IsNullOrWhiteSpace(value.Name)) throw new ChartProblem("Choose what to plot (a metric, a statistic or a parameter).");
            var scenarios = Patterns(spec.Scenarios);
            var list = new List<Obs>();
            switch (value.Source)
            {
                case ChartSource.Metric:
                {
                    var t = Table(tables, "metrics", "No named metrics in these results.");
                    int cRun = Col(t, "run"), cScen = Col(t, "scenario"), cRep = Col(t, "replication"), cMetric = Col(t, "metric"), cValue = Col(t, "value");
                    var names = new HashSet<string>();
                    foreach (var r in t.Rows)
                    {
                        names.Add(Str(r[cMetric]));
                        double v = Num(r[cValue]);
                        if (Str(r[cMetric]) == value.Name && !double.IsNaN(v) && Matches(scenarios, Str(r[cScen])))
                            list.Add(new Obs { Run = Str(r[cRun]), Scenario = Str(r[cScen]), Replication = Int(r[cRep]), Object = "", Value = v });
                    }
                    if (list.Count == 0) throw new ChartProblem(names.Contains(value.Name) ? $"Metric '{value.Name}' has no value." : $"No metric '{value.Name}'; there are: {string.Join(", ", names.OrderBy(n => n))}.");
                    break;
                }
                case ChartSource.Statistic:
                {
                    var t = Table(tables, "results", "No statistics per object in these results.");
                    int cRun = Col(t, "run"), cScen = Col(t, "scenario"), cRep = Col(t, "replication"), cScope = Col(t, "scope"), cName = Col(t, "name"),
                        cMetric = Col(t, "metric"), cValue = Col(t, "value");
                    var objects = Patterns(value.Objects);
                    var names = new HashSet<string>();
                    bool anyObject = false;
                    foreach (var r in t.Rows)
                    {
                        if (!Same(Str(r[cScope]), value.Scope) || !Matches(objects, Str(r[cName]))) continue;
                        anyObject = true;
                        names.Add(Str(r[cMetric]));
                        double v = Num(r[cValue]);
                        if (Str(r[cMetric]) == value.Name && !double.IsNaN(v) && Matches(scenarios, Str(r[cScen])))
                            list.Add(new Obs { Run = Str(r[cRun]), Scenario = Str(r[cScen]), Replication = Int(r[cRep]), Object = Str(r[cName]), Value = v });
                    }
                    if (list.Count == 0)
                        throw new ChartProblem(!anyObject ? $"No {value.Scope} objects{Which(value.Objects)}."
                            : names.Contains(value.Name) ? $"Statistic '{value.Name}' has no value." :
                              $"No statistic '{value.Name}' of {value.Scope} objects; there are: {string.Join(", ", names.Where(n => !ResultMetrics.IsState(n)).OrderBy(n => n))}.");
                    break;
                }
                case ChartSource.Parameter:
                {
                    var t = Table(tables, "scenarios", "No scenario parameters in these results.");
                    int cScen = Col(t, "scenario"), cPar = Col(t, "parameter"), cValue = Col(t, "value");
                    var byScenario = new Dictionary<string, double>();
                    var names = new HashSet<string>();
                    foreach (var r in t.Rows)
                    {
                        names.Add(Str(r[cPar]));
                        if (Str(r[cPar]) == value.Name && double.TryParse(Str(r[cValue]), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                            byScenario[Str(r[cScen])] = v;
                    }
                    if (byScenario.Count == 0)
                        throw new ChartProblem(names.Contains(value.Name) ? $"Parameter '{value.Name}' is not a number." : $"No parameter '{value.Name}'; there are: {string.Join(", ", names.OrderBy(n => n))}.");
                    foreach (var run in Runs(tables))
                        if (byScenario.TryGetValue(run.Scenario, out double v) && Matches(scenarios, run.Scenario))
                            list.Add(new Obs { Run = run.Run, Scenario = run.Scenario, Replication = run.Replication, Object = "", Value = v });
                    break;
                }
            }
            if (list.Count == 0) throw new ChartProblem($"No run of the scenarios '{spec.Scenarios}'.");
            return list;
        }

        /// <summary>The runs (the <c>runs</c> table, else those named by the metrics or results).</summary>
        private static List<Obs> Runs(IReadOnlyDictionary<string, ResultTable> tables)
        {
            var list = new List<Obs>();
            var seen = new HashSet<string>();
            foreach (string name in new[] { "runs", "metrics", "results" })
            {
                if (!tables.TryGetValue(name, out var t)) continue;
                int cRun = Col(t, "run"), cScen = Col(t, "scenario"), cRep = Col(t, "replication");
                if (cRun < 0 || cScen < 0) continue;
                foreach (var r in t.Rows)
                    if (seen.Add(Str(r[cRun]))) list.Add(new Obs { Run = Str(r[cRun]), Scenario = Str(r[cScen]), Replication = cRep < 0 ? 0 : Int(r[cRep]) });
                if (list.Count > 0) break;
            }
            return list;
        }

        // ── helpers ───────────────────────────────────────────────────────────

        private static ResultTable Table(IReadOnlyDictionary<string, ResultTable> tables, string name, string missing)
            => tables.TryGetValue(name, out var t) && t.RowCount > 0 ? t : throw new ChartProblem(missing);

        private static int Col(ResultTable t, string name)
        {
            for (int i = 0; i < t.Columns.Count; i++) if (t.Columns[i] == name) return i;
            throw new ChartProblem($"The table '{t.Name}' has no column '{name}'.");
        }

        private static string Str(object v) => v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        private static int Int(object v) => v == null || v is string s && s.Length == 0 ? 0 : Convert.ToInt32(Convert.ToDouble(v, CultureInfo.InvariantCulture));
        private static double Num(object v)
        {
            if (v == null) return double.NaN;
            if (v is string s) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static string Which(string objects) => string.IsNullOrWhiteSpace(objects) ? "" : $" matching '{objects}'";

        /// <summary>Name patterns separated by <c>;</c>, <c>*</c> = any text; none = everything.</summary>
        public static List<string> Patterns(string text)
            => (text ?? "").Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

        public static bool Matches(List<string> patterns, string name)
        {
            if (patterns.Count == 0) return true;
            foreach (string p in patterns) if (Glob(p, name ?? "")) return true;
            return false;
        }

        private static bool Glob(string pattern, string text)
        {
            int p = 0, t = 0, star = -1, mark = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && pattern[p] != '*' && char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])) { p++; t++; }
                else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
                else if (star >= 0) { p = star + 1; t = ++mark; }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }
    }
}
