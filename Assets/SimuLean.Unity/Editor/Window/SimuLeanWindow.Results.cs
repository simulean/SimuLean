using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SimuLean.Data;
using SimuLean.Results;
using SimuLean.Results.Charts;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The Results tab: charts of a result set — the last one produced in the session (an experiment, an
    /// optimisation, a run: <see cref="ResultsOutput.Last"/>) or a <c>.json</c> export opened from disk (the stable
    /// format, <see cref="ResultSet.FormatVersion"/>). Works on the set's long tables only, so a live set and a file
    /// draw the same: the metrics per scenario with their confidence intervals, a statistic per object, the share of
    /// time per state, and the time series.
    /// </summary>
    public partial class SimuLeanWindow
    {
        /// <summary>The tables of a result set being shown.</summary>
        private sealed class ResultsDoc
        {
            public string Title;
            public ResultSet Set;                       // when live (exportable)
            public Dictionary<string, ResultTable> Tables = new Dictionary<string, ResultTable>();
            public ResultTable Table(string name) => Tables.TryGetValue(name, out var t) ? t : null;

            // the series indexed once (object → statistic → one line per run): the table can hold many rows
            private Dictionary<string, Dictionary<string, List<(string run, List<(double t, double v)> points)>>> series;
            public Dictionary<string, Dictionary<string, List<(string run, List<(double t, double v)> points)>>> Series
            {
                get
                {
                    if (series != null) return series;
                    series = new Dictionary<string, Dictionary<string, List<(string, List<(double, double)>)>>>();
                    var t = Table("series");
                    if (t == null) return series;
                    int cRun = Col(t, "run"), cScope = Col(t, "scope"), cName = Col(t, "name"), cMetric = Col(t, "metric"), cTime = Col(t, "time"), cValue = Col(t, "value");
                    var lines = new Dictionary<(string, string, string), List<(double, double)>>();
                    foreach (var r in t.Rows)
                    {
                        string obj = Str(r[cScope]) + " · " + Str(r[cName]), metric = Str(r[cMetric]), run = Str(r[cRun]);
                        if (!lines.TryGetValue((obj, metric, run), out var pts))
                        {
                            lines[(obj, metric, run)] = pts = new List<(double, double)>();
                            if (!series.TryGetValue(obj, out var byMetric)) series[obj] = byMetric = new Dictionary<string, List<(string, List<(double, double)>)>>();
                            if (!byMetric.TryGetValue(metric, out var runs)) byMetric[metric] = runs = new List<(string, List<(double, double)>)>();
                            runs.Add((run, pts));
                        }
                        pts.Add((Num(r[cTime]), Num(r[cValue])));
                    }
                    return series;
                }
            }
        }

        private ResultsDoc resultsDoc;
        private bool followLatest = true;
        private int resMetric, resScope, resStat, resScenario, resStateScope, resSeries, resSeriesMetric;

        /// <summary>Opens the window on the Results tab showing <paramref name="set"/> (null: the latest).</summary>
        public static void OpenResults(ResultSet set)
        {
            Open(TabIndex("Results"));
            var w = GetWindow<SimuLeanWindow>();
            if (set != null) { w.resultsDoc = Doc(set); w.followLatest = ReferenceEquals(set, ResultsOutput.Last); }
            w.Repaint();
        }

        private static ResultsDoc Doc(ResultSet set)
        {
            var doc = new ResultsDoc { Title = set.Name, Set = set };
            foreach (var t in set.Tables()) doc.Tables[t.Name] = t;
            return doc;
        }

        private static ResultsDoc LoadJson(string path)
        {
            if (!(MiniJson.Parse(File.ReadAllText(path)) is Dictionary<string, object> root) || !(root.TryGetValue("tables", out object t) && t is Dictionary<string, object> tables))
                throw new FormatException("Not a SimuLean results file (no \"tables\").");
            var doc = new ResultsDoc { Title = Path.GetFileNameWithoutExtension(path) };
            foreach (var kv in tables)
            {
                var rows = kv.Value as List<object>;
                if (rows == null || rows.Count == 0 || !(rows[0] is Dictionary<string, object> first)) continue;
                var columns = first.Keys.ToArray();
                var table = new ResultTable(kv.Key, columns);
                foreach (var r in rows.OfType<Dictionary<string, object>>())
                    table.Add(columns.Select(c => r.TryGetValue(c, out object v) ? v : null).ToArray());
                doc.Tables[kv.Key] = table;
            }
            return doc;
        }

        private void DrawResults()
        {
            var latest = ResultsOutput.Last;
            if (followLatest && latest != null && (resultsDoc == null || !ReferenceEquals(resultsDoc.Set, latest))) resultsDoc = Doc(latest);

            EditorGUILayout.BeginHorizontal();
            bool follow = GUILayout.Toggle(followLatest, new GUIContent("Latest", "Show the last results produced in this session (experiment, optimisation, run)."), EditorStyles.miniButtonLeft);
            if (follow != followLatest) { followLatest = follow; if (follow && latest != null) resultsDoc = Doc(latest); }
            if (GUILayout.Button(new GUIContent("Open…", "A .json export (results folder)."), EditorStyles.miniButtonMid))
            {
                string path = EditorUtility.OpenFilePanel("Open results", ResultsOutput.Folder(ResultsOutput.ProjectRoot), "json");
                if (!string.IsNullOrEmpty(path))
                {
                    try { resultsDoc = LoadJson(path); followLatest = false; }
                    catch (Exception e) { EditorUtility.DisplayDialog("SimuLean", "Could not read the file: " + e.Message, "OK"); }
                }
            }
            var settings = SimuLeanProjectSettings.Load();
            GUI.enabled = resultsDoc?.Set != null;
            if (GUILayout.Button(new GUIContent("Export", $"Writes these results ({settings.defaultFormats}) to the results folder — Settings tab."), EditorStyles.miniButtonMid))
                foreach (string path in ResultsOutput.Export(resultsDoc.Set, settings.defaultFormats, ResultsOutput.ProjectRoot))
                    Debug.Log($"[SimuLean] Results: {path}");
            GUI.enabled = resultsDoc?.Set != null && resultsDoc.Set.StoreId == 0 && resultsDoc.Set.Source != "comparison";
            if (GUILayout.Button(new GUIContent(resultsDoc?.Set != null && resultsDoc.Set.StoreId > 0 ? $"Saved #{resultsDoc.Set.StoreId}" : "Save",
                                                "Keeps these results in the project database (the stored list below). Experiments, studies and " +
                                                "optimisations are kept on their own; a single run is kept with this button."), EditorStyles.miniButtonMid))
            {
                try { ResultsOutput.Save(resultsDoc.Set, ResultsOutput.ProjectRoot); }
                catch (Exception e) { EditorUtility.DisplayDialog("SimuLean", "Could not save the results: " + e.Message, "OK"); }
            }
            GUI.enabled = true;
            showStored = GUILayout.Toggle(showStored, new GUIContent("Stored", "The results kept in the project database: open, rename, delete, compare."), EditorStyles.miniButtonMid);
            if (GUILayout.Button(new GUIContent("Folder", "Opens the results folder."), EditorStyles.miniButtonRight))
            {
                string folder = ResultsOutput.Folder(ResultsOutput.ProjectRoot);
                Directory.CreateDirectory(folder);
                EditorUtility.RevealInFinder(folder);
            }
            EditorGUILayout.EndHorizontal();
            if (showStored) DrawStored();

            if (resultsDoc == null)
            {
                EditorGUILayout.HelpBox("No results yet: run an experiment or an optimisation (Experiments tab), Run Headless on the clock, " +
                                        "or finish a run in Play mode — or open a .json export or a stored set (Stored).", MessageType.Info);
                return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            var meta = resultsDoc.Table("metadata");
            string source = Meta(meta, "source"), runs = Meta(meta, "runs"), level = Meta(meta, "confidence");
            double conf = double.TryParse(level, NumberStyles.Float, CultureInfo.InvariantCulture, out double c) ? c : 0.95;
            EditorGUILayout.LabelField(resultsDoc.Title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"{source} · model {Meta(meta, "model")} · {runs} run(s) · {Meta(meta, "created")} · intervals {conf * 100:0.#} %", EditorStyles.miniLabel);

            DrawSummary(conf);
            DrawMetricChart(conf);
            DrawStatisticChart(conf);
            DrawStateChart();
            DrawSeriesChart();
            DrawTimelineChart();
            DrawChartDefinitions();
            EditorGUILayout.EndScrollView();
        }

        // ── the summary: the basics of every element, team and list (review2 #35) ──
        private static GUIStyle rightLabel, rightBold;

        private void DrawSummary(double conf)
        {
            var t = resultsDoc.Table("summary");
            if (t == null || t.RowCount == 0) return;
            var meta = resultsDoc.Table("metadata");
            var scenarios = Distinct(t, "scenario");
            if (scenarios.Length > 1) resScenario = Mathf.Clamp(EditorGUILayout.Popup("Scenario", resScenario, scenarios), 0, scenarios.Length - 1);
            else resScenario = 0;
            string scenario = scenarios[Mathf.Clamp(resScenario, 0, scenarios.Length - 1)];
            double unitSeconds = double.TryParse(Meta(meta, "time_unit_seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out double us) && us > 0 ? us : 1;
            string unit = ResultMetrics.TimeUnitName(unitSeconds);
            var rows = Rows(t).Where(r => S(r, "scenario") == scenario).ToList();
            int n = rows.Count > 0 ? rows.Max(r => (int)Math.Max(1, D(r, "n"))) : 1;
            GroupHeader("Summary", "Flow", n > 1 ? $"mean ± {conf * 100:0.#} % CI, {n} runs" : "one run");
            if (rightLabel == null)
            {
                rightLabel = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleRight };
                rightBold = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleRight };
            }

            // per object: metric → (mean, half width of the interval)
            var objects = new List<(string scope, string name, string type)>();
            var values = new Dictionary<(string, string, string), (double mean, double half)>();
            foreach (var r in rows)
            {
                var key = (S(r, "scope"), S(r, "name"));
                if (!objects.Exists(o => o.scope == key.Item1 && o.name == key.Item2)) objects.Add((key.Item1, key.Item2, S(r, "type")));
                double mean = D(r, "mean"), hi = D(r, "ci_high");
                values[(key.Item1, key.Item2, S(r, "metric"))] = (mean, n > 1 && !double.IsNaN(hi) ? hi - mean : double.NaN);
            }
            string Cell(string scope, string name, string metric, bool percent = false)
            {
                if (!values.TryGetValue((scope, name, metric), out var v) || double.IsNaN(v.mean)) return "—";
                string f(double x) => percent ? (x * 100).ToString("0.#", CultureInfo.InvariantCulture) + " %"
                                    : x.ToString(Math.Abs(x) >= 100 ? "0.#" : "0.##", CultureInfo.InvariantCulture);
                // no "± 0": an interval of zero width (a deterministic model) is noise
                return double.IsNaN(v.half) || f(v.half) == f(0) ? f(v.mean) : f(v.mean) + " ± " + f(v.half);
            }

            string end = Cell("model", "model", ResultMetrics.EndTime), span = Cell("model", "model", ResultMetrics.Makespan);
            EditorGUILayout.LabelField($"Times in {unit} · run to t = {end}" + (span != "—" ? $" · makespan {span}" : ""), EditorStyles.miniLabel);

            // columns share the window's width (no horizontal scroll); a cell that says nothing for the object is blank
            void Table(string scope, string title, (string metric, string head, bool percent, bool time)[] columns,
                       Func<string, string, bool> blank = null, Func<string, int> rank = null)
            {
                var list = objects.Where(o => o.scope == scope).ToList();
                if (list.Count == 0) return;
                if (rank != null) list = list.Select((o, i) => (o, i)).OrderBy(x => rank(x.o.type)).ThenBy(x => x.i).Select(x => x.o).ToList();
                float col = Mathf.Max(52f, (position.width - 150f) / columns.Length);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(title, EditorStyles.miniBoldLabel, GUILayout.MinWidth(80));
                foreach (var c in columns) GUILayout.Label(c.time ? $"{c.head} ({unit})" : c.head, rightBold, GUILayout.Width(col));
                EditorGUILayout.EndHorizontal();
                foreach (var o in list)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label(new GUIContent(o.name, o.type), GUILayout.MinWidth(80));
                    foreach (var c in columns)
                        GUILayout.Label(blank != null && blank(o.type, c.metric) ? "" : Cell(scope, o.name, c.metric, c.percent), rightLabel, GUILayout.Width(col));
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.Space(4);
            }

            Table("element", "Element", new[]
            {
                (ResultMetrics.Input, "In", false, false), (ResultMetrics.Output, "Out", false, false),
                (ResultMetrics.ContentAvg, "Content avg", false, false), (ResultMetrics.StayAvg, "Stay avg", false, true),
                (ResultMetrics.Utilization, "Utilisation", true, false),
            }, ElementBlank, type => IsSource(type) ? 0 : IsSink(type) ? 2 : 1);   // in the flow's order: sources … sinks
            Table("team", "Team", new[]
            {
                (ResultMetrics.Members, "Members", false, false), (ResultMetrics.Utilization, "Utilisation", true, false),
                (ResultMetrics.RequestWaitAvg, "Wait avg", false, true), (ResultMetrics.RequestsWaitingAvg, "Waiting avg", false, false),
            });
            Table("list", "List", new[]
            {
                (ResultMetrics.Pushed, "Pushed", false, false), (ResultMetrics.Pulled, "Pulled", false, false),
                (ResultMetrics.ContentAvg, "Content avg", false, false), (ResultMetrics.WaitAvg, "Wait avg", false, true),
            });
        }

        // sources create (only "out" applies), sinks absorb (only "in"); the other flow-only elements have no utilisation
        private static bool IsSource(string type) => type != null && type.EndsWith("Source", StringComparison.Ordinal);
        private static bool IsSink(string type) => type == "Sink";
        private static bool ElementBlank(string type, string metric)
        {
            if (IsSource(type)) return metric != ResultMetrics.Output;
            if (IsSink(type)) return metric != ResultMetrics.Input;
            return metric == ResultMetrics.Utilization && ResultMetrics.IsFlowOnly(type);
        }

        // ── the user's metrics per scenario, with their confidence intervals ──
        private void DrawMetricChart(double conf)
        {
            var t = resultsDoc.Table("metric_summary");
            if (t == null || t.RowCount == 0) return;
            GroupHeader("Metrics by scenario", "Experiments", $"mean ± {conf * 100:0.#} % CI");
            var metrics = Distinct(t, "metric");
            resMetric = Mathf.Clamp(EditorGUILayout.Popup("Metric", resMetric, metrics), 0, metrics.Length - 1);
            DrawQuery("metric", new ChartQuery
            {
                Kind = ChartKind.Bar, GroupBy = ChartGroup.Scenario, Confidence = conf, ValueLabels = true,
                Y = new ChartValue(ChartSource.Metric, metrics[resMetric]),
            });
        }

        // ── one statistic of every object of a kind ───────────────────────────
        private void DrawStatisticChart(double conf)
        {
            var t = resultsDoc.Table("summary");
            if (t == null || t.RowCount == 0) return;
            GroupHeader("Statistics by object", "Flow", "mean over the replications ± CI");
            var scenarios = Distinct(t, "scenario");
            if (scenarios.Length > 1) resScenario = Mathf.Clamp(EditorGUILayout.Popup("Scenario", resScenario, scenarios), 0, scenarios.Length - 1);
            else resScenario = 0;
            var scopes = Distinct(t, "scope").Where(s => s != "model").ToArray();   // the model's figures are in the summary
            if (scopes.Length == 0) return;
            resScope = Mathf.Clamp(EditorGUILayout.Popup("Kind", resScope, scopes), 0, scopes.Length - 1);
            var stats = Rows(t).Where(r => S(r, "scenario") == scenarios[resScenario] && S(r, "scope") == scopes[resScope])
                               .Select(r => S(r, "metric")).Where(m => !ResultMetrics.IsState(m)).Distinct().ToArray();
            if (stats.Length == 0) return;
            resStat = Mathf.Clamp(EditorGUILayout.Popup("Statistic", resStat, stats), 0, stats.Length - 1);
            // elements: the ones the statistic applies to (no "input" of a source; review2 #36)
            string objects = "";
            if (scopes[resScope] == "element")
            {
                var apply = Rows(t).Where(r => S(r, "scenario") == scenarios[resScenario] && S(r, "scope") == "element" && S(r, "metric") == stats[resStat]
                                               && !ElementBlank(S(r, "type"), stats[resStat])).Select(r => S(r, "name")).Distinct().ToList();
                if (apply.Count > 0) objects = string.Join(";", apply);
            }
            DrawQuery("statistic", new ChartQuery
            {
                Kind = ChartKind.Bar, GroupBy = ChartGroup.Object, Confidence = conf, ValueLabels = true, Scenarios = scenarios[resScenario],
                Title = $"{stats[resStat]} of {scopes[resScope]} objects" + (scenarios.Length > 1 ? $" · {scenarios[resScenario]}" : ""),
                Y = new ChartValue(ChartSource.Statistic, stats[resStat], scopes[resScope], objects),
            });
        }

        // ── share of time per state (elements, slots, resources) ──────────────
        private void DrawStateChart()
        {
            var t = resultsDoc.Table("summary");
            if (t == null) return;
            var scenarios = Distinct(t, "scenario");
            if (scenarios.Length == 0) return;
            string scenario = scenarios[Mathf.Clamp(resScenario, 0, scenarios.Length - 1)];
            var scopes = Rows(t).Where(r => S(r, "scenario") == scenario && ResultMetrics.IsState(S(r, "metric"))).Select(r => S(r, "scope")).Distinct().ToArray();
            if (scopes.Length == 0) return;
            GroupHeader("Time per state", "Resources", scenarios.Length > 1 ? scenario : "mean share of time");
            resStateScope = Mathf.Clamp(EditorGUILayout.Popup("Kind", resStateScope, scopes), 0, scopes.Length - 1);
            // elements: the ones that work on their items (a sink "processes" its whole run, a buffer idles; review2 #36)
            string objects = "";
            if (scopes[resStateScope] == "element")
            {
                var working = Rows(t).Where(r => S(r, "scenario") == scenario && S(r, "scope") == "element" && !ResultMetrics.IsFlowOnly(S(r, "type")))
                                     .Select(r => S(r, "name")).Distinct().ToList();
                if (working.Count > 0) objects = string.Join(";", working);
            }
            DrawQuery("states", new ChartQuery
            {
                Kind = ChartKind.States, Scenarios = scenario,
                Title = $"Time per state of {scopes[resStateScope]} objects" + (scenarios.Length > 1 ? $" · {scenario}" : ""),
                Y = new ChartValue(ChartSource.Statistic, "", scopes[resStateScope], objects),
            });
        }

        // ── time series: one line per run ─────────────────────────────────────
        private void DrawSeriesChart()
        {
            var t = resultsDoc.Table("series");
            if (t == null || t.RowCount == 0) return;
            GroupHeader("Over time", "Transport", "one line per run");
            var index = resultsDoc.Series;
            var objects = index.Keys.ToArray();
            resSeries = Mathf.Clamp(EditorGUILayout.Popup("Object", resSeries, objects), 0, objects.Length - 1);
            var byMetric = index[objects[resSeries]];
            var metrics = byMetric.Keys.ToArray();
            int def = Array.IndexOf(metrics, ResultMetrics.ContentAvg);
            if (resSeriesMetric >= metrics.Length) resSeriesMetric = Math.Max(0, def);
            resSeriesMetric = Mathf.Clamp(EditorGUILayout.Popup("Statistic", resSeriesMetric, metrics), 0, metrics.Length - 1);
            string[] parts = objects[resSeries].Split(new[] { " · " }, 2, StringSplitOptions.None);
            DrawQuery("series", new ChartQuery
            {
                Kind = ChartKind.Line,
                Y = new ChartValue(ChartSource.Statistic, metrics[resSeriesMetric], parts[0], parts.Length > 1 ? parts[1] : ""),
            });
        }

        // ── state timeline: a Gantt of one run (G3c) ──────────────────────────
        private int resGanttScope, resGanttScenario, resGanttRep;
        private void DrawTimelineChart()
        {
            var t = resultsDoc.Table("timeline");
            if (t == null || t.RowCount == 0) return;
            GroupHeader("Timeline", "Resources", "states (or items) over time, one run");
            bool jobs = resultsDoc.Table("jobs") != null;
            var scopes = Distinct(t, "scope").Concat(jobs ? new[] { "jobs" } : new string[0]).ToArray();
            resGanttScope = Mathf.Clamp(EditorGUILayout.Popup("Kind", resGanttScope, scopes), 0, scopes.Length - 1);
            var scenarios = Distinct(t, "scenario");
            if (scenarios.Length > 1) resGanttScenario = Mathf.Clamp(EditorGUILayout.Popup("Scenario", resGanttScenario, scenarios), 0, scenarios.Length - 1);
            else resGanttScenario = 0;
            var reps = Rows(t).Where(r => S(r, "scenario") == scenarios[resGanttScenario]).Select(r => S(r, "replication")).Distinct().ToArray();
            if (reps.Length > 1) resGanttRep = Mathf.Clamp(EditorGUILayout.Popup("Replication", resGanttRep, reps), 0, reps.Length - 1);
            else resGanttRep = 0;
            int.TryParse(reps.Length > 0 ? reps[resGanttRep] : "0", out int rep);
            DrawQuery("timeline", new ChartQuery
            {
                Kind = scopes[resGanttScope] == "jobs" ? ChartKind.JobGantt : ChartKind.Gantt, Scenarios = scenarios[resGanttScenario], Replication = rep,
                Y = new ChartValue(ChartSource.Statistic, "", scopes[resGanttScope]),
            });
        }

        // ── saved charts (ChartDefinition assets) ──────────────────
        private void DrawChartDefinitions()
        {
            GroupHeader("Charts", "Experiments", "saved chart definitions");
            var defs = AssetDatabase.FindAssets("t:" + nameof(ChartDefinition))
                .Select(g => AssetDatabase.LoadAssetAtPath<ChartDefinition>(AssetDatabase.GUIDToAssetPath(g))).Where(d => d != null)
                .OrderBy(d => d.name, StringComparer.OrdinalIgnoreCase).ToList();
            if (defs.Count == 0)
                EditorGUILayout.LabelField("No chart saved yet: New makes a chart asset (what to plot, how, from which results) " +
                                           "drawn here for the results shown, or from stored sets.", EditorStyles.wordWrappedMiniLabel);
            int hidden = 0;
            foreach (var d in defs)
            {
                // a chart of another model (e.g. the healthcare library's) has nothing to draw here: listed, not drawn (review2 #37)
                var probe = d.source == ChartDataSource.StoredSets ? null : d.Build(resultsDoc?.Tables, ResultsOutput.ProjectRoot);
                if (probe != null && (probe.IsEmpty || !string.IsNullOrEmpty(probe.Message))) { hidden++; continue; }
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(d.name, EditorStyles.boldLabel);
                if (GUILayout.Button(new GUIContent("Edit", "Selects the chart asset (its inspector)."), EditorStyles.miniButtonLeft, GUILayout.Width(40)))
                    Selection.activeObject = d;
                if (GUILayout.Button(new GUIContent("PNG…", $"Saves the chart as an image ({d.exportSize.x} × {d.exportSize.y} points, 2 pixels per point)."), EditorStyles.miniButtonMid, GUILayout.Width(44)))
                    SaveChart(d.Build(resultsDoc?.Tables, ResultsOutput.ProjectRoot), d.name, d.exportSize.x, d.exportSize.y, "png");
                if (GUILayout.Button(new GUIContent("SVG…", "Saves the chart as vector graphics (papers)."), EditorStyles.miniButtonRight, GUILayout.Width(44)))
                    SaveChart(d.Build(resultsDoc?.Tables, ResultsOutput.ProjectRoot), d.name, d.exportSize.x, d.exportSize.y, "svg");
                EditorGUILayout.EndHorizontal();
                string key = "def:" + AssetDatabase.GetAssetPath(d) + EditorJsonUtility.ToJson(d);
                DrawChartRect(key, () => d.Build(resultsDoc?.Tables, ResultsOutput.ProjectRoot), null, 0);
            }
            if (hidden > 0)
                EditorGUILayout.LabelField($"{hidden} saved chart(s) with nothing to draw for these results (other models' data) — hidden.",
                                           EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button(new GUIContent("New chart", "A chart asset in Assets/SimuLeanCharts, starting from these results.")))
                NewChartDefinition();
        }

        private void NewChartDefinition()
        {
            var def = CreateInstance<ChartDefinition>();
            var metrics = resultsDoc?.Table("metric_summary");
            def.spec = metrics != null && metrics.RowCount > 0
                ? new ChartQuery { Kind = ChartKind.Bar, ValueLabels = true, Y = new ChartValue(ChartSource.Metric, Distinct(metrics, "metric")[0]) }
                : new ChartQuery { Kind = ChartKind.Bar, GroupBy = ChartGroup.Object, Y = new ChartValue(ChartSource.Statistic, ResultMetrics.Output, "element") };
            const string folder = "Assets/SimuLeanCharts";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "SimuLeanCharts");
            AssetDatabase.CreateAsset(def, AssetDatabase.GenerateUniqueAssetPath(folder + "/Chart.asset"));
            AssetDatabase.SaveAssets();
            Selection.activeObject = def;
            EditorGUIUtility.PingObject(def);
        }

        // ── drawing: the engine's charts (one renderer for the window, the inspector, dashboards and PNG) ──

        private sealed class CachedChart { public string Key; public ChartData Data; public Texture2D Texture; public Vector2Int Size; }
        private readonly Dictionary<string, CachedChart> chartCache = new Dictionary<string, CachedChart>();
        private object chartDoc;

        private void DrawQuery(string slot, ChartQuery query)
        {
            string key = slot + ":" + JsonUtility.ToJson(query);
            DrawChartRect(key, () => ChartBuilder.Build(query, resultsDoc.Tables), slot, 0);
        }

        /// <summary>Draws a chart at the window's width (rendered at the screen's pixel size, rebuilt only when the
        /// results, the definition or the size change), with a PNG… button.</summary>
        private void DrawChartRect(string key, Func<ChartData> build, string slot, float height)
        {
            if (!ReferenceEquals(chartDoc, resultsDoc)) { chartDoc = resultsDoc; foreach (var c in chartCache.Values) if (c.Texture != null) DestroyImmediate(c.Texture); chartCache.Clear(); }
            string cacheKey = slot ?? key;
            if (!chartCache.TryGetValue(cacheKey, out var cached) || cached.Key != key)
            {
                if (cached?.Texture != null) DestroyImmediate(cached.Texture);
                chartCache[cacheKey] = cached = new CachedChart { Key = key, Data = build() };
            }
            float h = height > 0 ? height : Mathf.Clamp(position.width * 0.5f, 200, 340);
            var rect = GUILayoutUtility.GetRect(10, h, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint && rect.width > 20)
            {
                float ppp = EditorGUIUtility.pixelsPerPoint;
                var size = new Vector2Int(Mathf.RoundToInt(rect.width * ppp), Mathf.RoundToInt(rect.height * ppp));
                if (cached.Texture == null || cached.Size != size)
                {
                    var image = ChartRenderer.Render(cached.Data, size.x, size.y, EditorGUIUtility.isProSkin ? ChartTheme.Dark : ChartTheme.Light,
                                                      ppp);
                    cached.Texture = ChartTextures.ToTexture(image, cached.Texture);
                    cached.Size = size;
                }
                GUI.DrawTexture(rect, cached.Texture);
            }
            if (slot != null)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("PNG…", "Saves this chart as an image (800 × 500 points, 2 pixels per point)."), EditorStyles.miniButtonLeft, GUILayout.Width(44)))
                    SaveChart(cached.Data, resultsDoc.Title + "_" + slot, 800, 500, "png");
                if (GUILayout.Button(new GUIContent("SVG…", "Saves this chart as vector graphics (papers)."), EditorStyles.miniButtonRight, GUILayout.Width(44)))
                    SaveChart(cached.Data, resultsDoc.Title + "_" + slot, 800, 500, "svg");
                EditorGUILayout.EndHorizontal();
            }
        }

        /// <summary>Saves a chart as PNG (2 pixels per point) or SVG (vector), sizes in points.</summary>
        private static void SaveChart(ChartData data, string name, int width, int height, string extension)
        {
            string folder = ResultsOutput.Folder(ResultsOutput.ProjectRoot);
            Directory.CreateDirectory(folder);
            string path = EditorUtility.SaveFilePanel("Save chart", folder, ResultExport.FileName(name) + "." + extension, extension);
            if (string.IsNullOrEmpty(path)) return;
            ChartRenderer.Save(data, path, Math.Max(200, width), Math.Max(150, height), ChartTheme.Light);
            Debug.Log($"[SimuLean] Chart: {path}");
        }

        // ── table access ──────────────────────────────────────────────────────

        private static IEnumerable<(ResultTable t, object[] r)> Rows(ResultTable t) => t.Rows.Select(r => (t, r));
        private static int Col(ResultTable t, string c) { for (int i = 0; i < t.Columns.Count; i++) if (t.Columns[i] == c) return i; return -1; }
        private static string S((ResultTable t, object[] r) row, string c) { int i = Col(row.t, c); return i < 0 ? "" : Convert.ToString(row.r[i], CultureInfo.InvariantCulture) ?? ""; }
        private static double D((ResultTable t, object[] r) row, string c)
        {
            int i = Col(row.t, c);
            object v = i < 0 ? null : row.r[i];
            return v == null || v is string ? double.NaN : Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }
        private static string[] Distinct(ResultTable t, string c) => Rows(t).Select(r => S(r, c)).Distinct().ToArray();
        private static string Meta(ResultTable meta, string key)
            => meta == null ? "" : Rows(meta).Where(r => S(r, "key") == key).Select(r => S(r, "value")).FirstOrDefault() ?? "";
        private static string Str(object v) => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        private static double Num(object v) => v == null || v is string ? double.NaN : Convert.ToDouble(v, CultureInfo.InvariantCulture);
    }
}
