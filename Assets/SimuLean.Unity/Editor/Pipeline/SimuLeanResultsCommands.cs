using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimuLean.Results;
using SimuLean.Results.Charts;
using Unity.Pipeline.Commands;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Results through the MCP: what the Results tab does — the last result
    /// set and the stored ones, keep one, export it, chart it — had no command.
    /// </summary>
    public static partial class SimuLeanCommands
    {
        [CliCommand("simulean_results", "Result sets: action=list (the last one and the stored ones), read (a set's metrics per " +
                                        "scenario, mean ± half-width), save (keep a set in the project's store), export (files: " +
                                        "csv, excel, sqlite, json) or chart (a PNG / SVG: kind bar | box | histogram | scatter | " +
                                        "line | states; y and x as metric:NAME, stat:SCOPE:OBJECTS:STATISTIC or param:PATH). set = " +
                                        "'last' (default), a stored set's id or its name.", Tags = new[] { Tag })]
        public static object Results(
            [CliArg("action", "list | read | save | export | chart.")] string action = "list",
            [CliArg("set", "'last' (the last run / study / optimisation), a stored set's id or its name.")] string set = "last",
            [CliArg("formats", "export: 'csv;excel;sqlite;json' (any of them).")] string formats = "csv",
            [CliArg("kind", "chart: bar | box | histogram | scatter | line | states.")] string kind = "bar",
            [CliArg("y", "chart: the value — metric:out, stat:element:Srv;Buf:utilization (objects ';'-separated or *), param:Buf.capacity.")] string y = "",
            [CliArg("x", "chart: scatter's x (as y).")] string x = "",
            [CliArg("groupBy", "chart: scenario | object | run.")] string groupBy = "scenario",
            [CliArg("title", "chart: its title.")] string title = "",
            [CliArg("path", "chart: the file, .png or .svg (empty = Recordings/Charts/<set>_<time>.png).")] string path = "",
            [CliArg("name", "save: the stored set's name (empty = the set's).")] string name = "")
            => AgentReply.Of(() =>
            {
                switch ((action ?? "list").Trim().ToLowerInvariant())
                {
                    case "list": return ListResults();
                    case "read": return ReadResults(set);
                    case "save": return SaveResults(set, name);
                    case "export": return ExportResults(set, formats);
                    case "chart": return ChartResults(set, kind, y, x, groupBy, title, path);
                }
                throw new ArgumentException($"action: '{action}' is not list, read, save, export or chart.");
            });

        private static object ListResults()
        {
            var last = ResultsOutput.Last;
            var stored = ResultsOutput.Store(ResultsOutput.ProjectRoot).List();
            return new
            {
                last = last == null ? null : new { name = last.Name, source = last.Source, model = last.Model, scenarios = last.Scenarios.Count, runs = last.Runs.Count },
                stored = stored.Select(s => new { id = s.Id, name = s.Name, kind = s.Kind, model = s.Model, created = s.Created.ToString("s"), s.Scenarios, s.Runs }).ToList(),
            };
        }

        /// <summary>The set named by <paramref name="set"/>: <c>last</c>, a stored set's id, or its name (the newest of that name).</summary>
        internal static ResultSet ResultSetOf(string set)
        {
            string s = string.IsNullOrWhiteSpace(set) ? "last" : set.Trim();
            if (s.Equals("last", StringComparison.OrdinalIgnoreCase))
                return ResultsOutput.Last ?? throw new ArgumentException("set: no result set yet — run a study (simulean_run_headless) first.");
            var store = ResultsOutput.Store(ResultsOutput.ProjectRoot);
            var all = store.List();
            var hit = int.TryParse(s, out int id) ? all.FirstOrDefault(x => x.Id == id) : all.Where(x => x.Name == s).OrderByDescending(x => x.Created).FirstOrDefault();
            if (hit == null)
                throw new ArgumentException($"set: no stored set '{s}'" + (all.Count == 0 ? " (none stored)." : $" (stored: {string.Join(", ", all.Take(10).Select(x => $"#{x.Id} {x.Name}"))})."));
            return store.Load(hit.Id);
        }

        private static object ReadResults(string set)
        {
            var rs = ResultSetOf(set);
            var answer = Newtonsoft.Json.Linq.JObject.FromObject(Answer(rs, false, null));
            answer["name"] = rs.Name;
            answer["source"] = rs.Source;
            answer["metadata"] = Newtonsoft.Json.Linq.JObject.FromObject(rs.Metadata.GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value));
            return answer;
        }

        private static object SaveResults(string set, string name)
        {
            var rs = ResultSetOf(set);
            if (!string.IsNullOrWhiteSpace(name)) rs.Name = name.Trim();
            int id = ResultsOutput.Save(rs, ResultsOutput.ProjectRoot);
            return new { saved = id, name = rs.Name };
        }

        private static object ExportResults(string set, string formats)
        {
            var rs = ResultSetOf(set);
            var f = ResultFormats.None;
            foreach (string part in (formats ?? "").Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Enum.TryParse(part.Trim(), true, out ResultFormats one) || one == ResultFormats.None)
                    throw new ArgumentException($"formats: '{part.Trim()}' is not csv, excel, sqlite or json.");
                f |= one;
            }
            if (f == ResultFormats.None) throw new ArgumentException("formats: give one or more of csv, excel, sqlite, json.");
            return new { exported = ResultsOutput.Export(rs, f, ResultsOutput.ProjectRoot).Select(p => p.Replace('\\', '/')).ToList() };
        }

        private static object ChartResults(string set, string kind, string y, string x, string groupBy, string title, string path)
        {
            var rs = ResultSetOf(set);
            if (!Enum.TryParse((kind ?? "").Trim(), true, out ChartKind k)
                || !new[] { ChartKind.Bar, ChartKind.Box, ChartKind.Histogram, ChartKind.Scatter, ChartKind.Line, ChartKind.States }.Contains(k))
                throw new ArgumentException($"kind: '{kind}' is not bar, box, histogram, scatter, line or states.");
            if (!Enum.TryParse((groupBy ?? "scenario").Trim(), true, out ChartGroup g))
                throw new ArgumentException($"groupBy: '{groupBy}' is not scenario, object or run.");
            var query = new ChartQuery { Kind = k, GroupBy = g, Title = title ?? "", Confidence = rs.Confidence > 0 ? rs.Confidence : 0.95 };
            if (k != ChartKind.States)
            {
                if (string.IsNullOrWhiteSpace(y)) throw new ArgumentException("y: the value to chart, e.g. metric:out or stat:element:Srv:utilization.");
                query.Y = ChartValueOf(y, "y");
            }
            if (!string.IsNullOrWhiteSpace(x)) query.X = ChartValueOf(x, "x");
            else if (k == ChartKind.Scatter) throw new ArgumentException("x: a scatter needs x, e.g. param:Buffer.capacity.");
            var data = ChartBuilder.Build(query, rs);
            if (data.Series.Count == 0 && data.Segments.Count == 0)
                throw new ArgumentException($"The chart has no data: check y / x against the set's metrics ({string.Join(", ", rs.MetricNames)}) and objects.");
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(ResultsOutput.ProjectRoot, "Recordings", "Charts", $"{Safe(rs.Name)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            else if (!Path.IsPathRooted(path)) path = Path.Combine(ResultsOutput.ProjectRoot, path);
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) path += ".png";
            ChartRenderer.Save(data, path);
            return new { path = path.Replace('\\', '/'), kind = k.ToString(), categories = data.Categories.Count, series = data.Series.Select(s => s.Name).ToList() };
        }

        // metric:NAME | stat:SCOPE:OBJECTS:STATISTIC | param:PATH
        private static ChartValue ChartValueOf(string text, string what)
        {
            string t = text.Trim();
            int c = t.IndexOf(':');
            string head = c > 0 ? t.Substring(0, c).ToLowerInvariant() : "";
            string rest = c > 0 ? t.Substring(c + 1) : "";
            switch (head)
            {
                case "metric": return new ChartValue(ChartSource.Metric, rest);
                case "param": return new ChartValue(ChartSource.Parameter, rest);
                case "stat":
                    var parts = rest.Split(new[] { ':' }, 3);
                    if (parts.Length == 3) return new ChartValue(ChartSource.Statistic, parts[2], parts[0], parts[1]);
                    break;
            }
            throw new ArgumentException($"{what}: '{text}' is not metric:NAME, stat:SCOPE:OBJECTS:STATISTIC (e.g. stat:element:Srv:utilization) or param:PATH.");
        }

        private static string Safe(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string((name ?? "results").Select(ch => bad.Contains(ch) || ch == ' ' ? '_' : ch).ToArray());
        }
    }
}
