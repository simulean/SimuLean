using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SimuLean.Data;

namespace SimuLean.Results
{
    /// <summary>Formats a <see cref="ResultSet"/> can be exported to.</summary>
    [Flags]
    public enum ResultFormats
    {
        None = 0,
        /// <summary>An .xlsx workbook with readable sheets.</summary>
        Excel = 1,
        /// <summary>A folder with one CSV file per table.</summary>
        Csv = 2,
        /// <summary>The tables appended to a SQLite database (the project database), keyed by the set's name.</summary>
        Sqlite = 4,
        /// <summary>One JSON document with every table.</summary>
        Json = 8,
    }

    /// <summary>
    /// Writes a <see cref="ResultSet"/>. CSV, SQLite and JSON hold the set's standard long tables as they are
    /// (the stable format, <see cref="ResultSet.FormatVersion"/>); Excel adds readable sheets on top — the summary with
    /// confidence intervals, the scenarios side by side, one sheet per kind of object (elements, resources, teams,
    /// lists…), the runs — and keeps the long tables. Engine-only (no Unity): the callers choose the folder.
    /// </summary>
    public static class ResultExport
    {
        /// <summary>Writes the chosen formats under <paramref name="folder"/> (<c>&lt;name&gt;.xlsx</c>,
        /// <c>&lt;name&gt;/*.csv</c>, <c>&lt;name&gt;.json</c>) and into <paramref name="database"/> (SQLite); returns what
        /// was written (paths, or the database path).</summary>
        public static List<string> Write(ResultSet set, string folder, ResultFormats formats, ProjectDatabase database = null)
        {
            if (set == null) throw new ArgumentNullException(nameof(set));
            var written = new List<string>();
            string name = FileName(set.Name);
            if ((formats & ResultFormats.Excel) != 0) written.Add(WriteExcel(set, Path.Combine(folder, name + ".xlsx")));
            if ((formats & ResultFormats.Csv) != 0) written.Add(WriteCsv(set, Path.Combine(folder, name)));
            if ((formats & ResultFormats.Json) != 0)
            {
                string path = Path.Combine(folder, name + ".json");
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, ToJson(set), new UTF8Encoding(false));
                written.Add(path);
            }
            if ((formats & ResultFormats.Sqlite) != 0)
            {
                if (database == null) throw new ArgumentException("SQLite export needs a database.", nameof(database));
                WriteSqlite(set, database);
                written.Add(database.Path);
            }
            return written;
        }

        // ── CSV ──────────────────────────────────────────────────────────────

        /// <summary>One <c>&lt;table&gt;.csv</c> per table in <paramref name="folder"/> (created; UTF-8, invariant culture).</summary>
        public static string WriteCsv(ResultSet set, string folder)
        {
            Directory.CreateDirectory(folder);
            foreach (var t in set.Tables())
                File.WriteAllText(Path.Combine(folder, FileName(t.Name) + ".csv"), t.ToCsv(), new UTF8Encoding(false));
            return folder;
        }

        // ── SQLite ───────────────────────────────────────────────────────────

        /// <summary>
        /// Appends every table to <paramref name="db"/> with a leading <c>set</c> column (the set's name): the tables
        /// gather every exported set and are queried with <c>WHERE "set" = …</c>. Re-exporting a set replaces its rows.
        /// Extra tables are named <c>&lt;source&gt;_&lt;table&gt;</c> (e.g. <c>optimization_history</c>).
        /// </summary>
        public static void WriteSqlite(ResultSet set, ProjectDatabase db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            var extra = new HashSet<ResultTable>(set.Extra);
            foreach (var t in set.Tables())
            {
                string name = extra.Contains(t) ? set.Source + "_" + t.Name : t.Name;
                var keyed = WithSet(t, set.Name, name);
                if (db.TableExists(name)) db.Execute($"DELETE FROM \"{name.Replace("\"", "\"\"")}\" WHERE \"set\" = ?", set.Name);
                keyed.WriteTo(db, name, replace: false);
            }
        }

        private static ResultTable WithSet(ResultTable t, string set, string name)
        {
            var columns = new List<string> { "set" };
            columns.AddRange(t.Columns);
            var keyed = new ResultTable(name, columns.ToArray());
            foreach (var r in t.Rows)
            {
                var row = new object[r.Length + 1];
                row[0] = set;
                Array.Copy(r, 0, row, 1, r.Length);
                keyed.Add(row);
            }
            return keyed;
        }

        // ── JSON ─────────────────────────────────────────────────────────────

        /// <summary><c>{ "format", "name", "tables": { table: [ { column: value } ] } }</c>.</summary>
        public static string ToJson(ResultSet set)
        {
            var tables = new Dictionary<string, object>();
            foreach (var t in set.Tables())
            {
                var rows = new List<object>(t.RowCount);
                foreach (var r in t.Rows)
                {
                    var o = new Dictionary<string, object>();
                    for (int c = 0; c < t.Columns.Count; c++) o[t.Columns[c]] = r[c];
                    rows.Add(o);
                }
                tables[t.Name] = rows;
            }
            return MiniJson.Serialize(new Dictionary<string, object>
            {
                ["format"] = ResultSet.FormatVersion, ["name"] = set.Name, ["tables"] = tables,
            });
        }

        // ── Excel ────────────────────────────────────────────────────────────

        /// <summary>
        /// The workbook: <c>Summary</c> (each metric per scenario with n, mean, std and the confidence interval),
        /// <c>Scenarios</c> (a row per scenario: parameters, metric means and intervals), a sheet per kind of object
        /// (mean per statistic and scenario, ± the interval's half-width with several replications), <c>Runs</c>
        /// (parameters and metrics per run), the long tables (<c>Statistics</c> = <c>summary</c>, <c>Run statistics</c> =
        /// <c>results</c>, <c>Series</c> = <c>series</c>), the extra tables and <c>Metadata</c>.
        /// </summary>
        public static string WriteExcel(ResultSet set, string path)
        {
            var xlsx = new XlsxWriter();
            var scenarios = set.ScenarioOrder();
            string level = $"{set.Confidence * 100:0.#}%";

            if (set.MetricNames.Count > 0)
            {
                var summary = xlsx.AddSheet("Summary");
                summary.AddHeaderRow("Scenario", "Metric", "Replications", "Mean", "Std", $"CI {level} low", $"CI {level} high");
                foreach (var r in set.MetricSummaryTable().Rows)
                    summary.AddRow(r[0], r[1], r[2], r[3], r[4], r[7], r[8]);

                var sheet = xlsx.AddSheet("Scenarios");
                var paramNames = ParameterNames(scenarios);
                var header = new List<string> { "Scenario", "Replications" };
                header.AddRange(paramNames);
                foreach (string m in set.MetricNames) header.AddRange(new[] { m, m + " ±" });
                sheet.AddHeaderRow(header.ToArray());
                foreach (var s in scenarios)
                {
                    var runs = set.RunsOf(s.Name);
                    var row = new List<object> { s.Name, runs.Count };
                    foreach (string p in paramNames) row.Add(Cell(Param(s, p)));
                    foreach (string m in set.MetricNames)
                    {
                        var values = new List<double>();
                        foreach (var r in runs) if (r.Metrics.TryGetValue(m, out double v)) values.Add(v);
                        var st = StatSummary.Of(values, set.Confidence);
                        row.Add(ResultSet.Num(st.Mean));
                        row.Add(st.HasInterval ? (object)st.HalfWidth : null);
                    }
                    sheet.AddRow(row.ToArray());
                }
            }

            foreach (ResultScope scope in Enum.GetValues(typeof(ResultScope))) ScopeSheet(xlsx, set, scenarios, scope);

            var runsSheet = xlsx.AddSheet("Runs");
            {
                var paramNames = ParameterNames(scenarios);
                var header = new List<string> { "Run", "Scenario", "Replication", "Seed", "Elapsed (s)" };
                header.AddRange(paramNames);
                header.AddRange(set.MetricNames);
                runsSheet.AddHeaderRow(header.ToArray());
                foreach (var r in set.Runs)
                {
                    var s = scenarios.Find(x => x.Name == r.Scenario);
                    var row = new List<object> { r.Id, r.Scenario, r.Replication, r.Seed, r.ElapsedSeconds };
                    foreach (string p in paramNames) row.Add(Cell(s != null ? Param(s, p) : null));
                    foreach (string m in set.MetricNames) row.Add(r.Metrics.TryGetValue(m, out double v) ? ResultSet.Num(v) : null);
                    runsSheet.AddRow(row.ToArray());
                }
            }

            // the long tables (sheet names distinct from the readable ones: Excel ignores case)
            foreach (var (t, sheet) in new[] { (set.SummaryTable(), "Statistics"), (set.ResultsTable(), "Run statistics"), (set.SeriesTable(), "Series"), (set.TimelineTable(), "Timeline"), (set.JobsTable(), "Jobs") })
                if (t.RowCount > 0) xlsx.AddSheet(sheet).AddTable(t);
            foreach (var t in set.Extra) xlsx.AddSheet(t.Name).AddTable(t);
            xlsx.AddSheet("Metadata").AddTable(set.MetadataTable());

            xlsx.Save(path);
            return path;
        }

        // A sheet per kind of object: a row per scenario and object, the mean of each statistic (± the half-width).
        private static void ScopeSheet(XlsxWriter xlsx, ResultSet set, List<ResultScenario> scenarios, ResultScope scope)
        {
            var rows = new List<(string scenario, SummaryObject o)>();
            foreach (var s in scenarios)
            {
                var summary = set.SummaryOf(s.Name);
                if (summary != null) foreach (var o in summary.Of(scope)) rows.Add((s.Name, o));
            }
            if (rows.Count == 0) return;
            var names = new List<string>();
            bool groups = false, intervals = false;
            foreach (var (_, o) in rows)
            {
                names.AddRange(o.Metrics);
                groups |= o.Group.Length > 0;
                foreach (string m in o.Metrics) intervals |= o[m].HasInterval;
            }
            var columns = RunResults.Order(names, scope);
            var sheet = xlsx.AddSheet(SheetName(scope));
            var header = new List<string> { "Scenario", "Name", "Type" };
            if (groups) header.Add("Group");
            foreach (string c in columns) { header.Add(c); if (intervals) header.Add(c + " ±"); }
            sheet.AddHeaderRow(header.ToArray());
            foreach (var (scenario, o) in rows)
            {
                var row = new List<object> { scenario, o.Name, o.Type };
                if (groups) row.Add(o.Group);
                foreach (string c in columns)
                {
                    bool has = o.TryGet(c, out var st);
                    row.Add(has ? ResultSet.Num(st.Mean) : null);
                    if (intervals) row.Add(has && st.HasInterval ? (object)st.HalfWidth : null);
                }
                sheet.AddRow(row.ToArray());
            }
        }

        private static string SheetName(ResultScope scope)
        {
            switch (scope)
            {
                case ResultScope.Element: return "Elements";
                case ResultScope.Slot: return "Server slots";
                case ResultScope.Resource: return "Resources";
                case ResultScope.Team: return "Teams";
                case ResultScope.List: return "Lists";
                case ResultScope.Downtime: return "Downtimes";
                case ResultScope.Reaction: return "Reactions";
                default: return "Traffic";
            }
        }

        private static List<string> ParameterNames(List<ResultScenario> scenarios)
        {
            var names = new List<string>();
            foreach (var s in scenarios) foreach (var p in s.Parameters) if (!names.Contains(p.Key)) names.Add(p.Key);
            return names;
        }

        private static string Param(ResultScenario s, string name)
        {
            foreach (var p in s.Parameters) if (p.Key == name) return p.Value;
            return null;
        }

        /// <summary>A parameter value: a number when it is one, else the text (an option, a vector).</summary>
        private static object Cell(string value)
            => value != null && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)
                ? (object)d : value;

        /// <summary>A file / folder name from a set name (invalid characters replaced by <c>_</c>).</summary>
        public static string FileName(string name)
        {
            var sb = new StringBuilder();
            var invalid = Path.GetInvalidFileNameChars();
            foreach (char c in string.IsNullOrWhiteSpace(name) ? "Results" : name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
