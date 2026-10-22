using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SimuLean.Data;
using SimuLean.Stochastics.Fitting;
using Unity.Pipeline.Commands;
using UnityEditor;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Data and distribution fitting through the MCP: what the Data tab
    /// (the project database: tables, SQL, import) and the Fit tab (distributions ranked against data, written into a
    /// distribution field) do had no command.
    /// </summary>
    public static partial class SimuLeanCommands
    {
        private const int MaxRows = 200;

        [CliCommand("simulean_query", "The project database (SQLite): without arguments, its tables and their row counts; sql = run " +
                                      "a statement (rows of a SELECT, at most maxRows); import = load a CSV / Excel file into a " +
                                      "table (replacing it unless append).", Tags = new[] { Tag })]
        public static object Query(
            [CliArg("sql", "A statement, e.g. SELECT * FROM arrivals WHERE day = 2.")] string sql = "",
            [CliArg("import", "A CSV / Excel file to load (absolute or relative to the project).")] string import = "",
            [CliArg("table", "import: the table's name (empty = the file's name).")] string table = "",
            [CliArg("sheet", "import: the Excel sheet (empty = the first).")] string sheet = "",
            [CliArg("append", "import: add the rows to the table instead of replacing it.")] bool append = false,
            [CliArg("maxRows", "sql: at most this many rows in the answer.")] int maxRows = 50)
            => AgentReply.Of(() =>
            {
                var db = Database();
                if (!string.IsNullOrWhiteSpace(import))
                {
                    string file = ProjectFile(import);
                    var columns = TableFiles.Read(file, string.IsNullOrWhiteSpace(sheet) ? null : sheet);
                    string name = string.IsNullOrWhiteSpace(table) ? Path.GetFileNameWithoutExtension(file) : table.Trim();
                    int rows = db.ImportTable(name, columns, !append);
                    return new { imported = name, rows, columns = columns.Keys.ToList() };
                }
                if (!string.IsNullOrWhiteSpace(sql))
                {
                    var result = db.Query(sql);
                    int keep = Math.Max(1, Math.Min(maxRows, MaxRows));
                    return new { rows = result.Take(keep).ToList(), count = result.Count, truncated = result.Count > keep };
                }
                return new { tables = db.ListTables().Select(t => new { table = t, rows = RowCount(db, t) }).ToList(), path = db.Path.Replace('\\', '/') };
            });

        private static ProjectDatabase Database()
        {
            string path = ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return ProjectDatabaseRegistry.GetOrOpen(path);
        }

        private static long RowCount(ProjectDatabase db, string table)
        {
            var r = db.Query($"SELECT COUNT(*) AS n FROM \"{table.Replace("\"", "\"\"")}\"");
            return r.Count > 0 && r[0].TryGetValue("n", out object n) ? Convert.ToInt64(n, CultureInfo.InvariantCulture) : 0;
        }

        private static string ProjectFile(string file)
        {
            string f = Path.IsPathRooted(file) ? file : Path.Combine(ResultsOutput.ProjectRoot, file);
            if (!File.Exists(f)) throw new ArgumentException($"'{file}': no such file.");
            return f;
        }

        [CliCommand("simulean_fit", "Fit distributions to data and rank them (Anderson-Darling or Kolmogorov-Smirnov): each with " +
                                    "its parameters, the sampler spec, the p-values and a verdict. The data: values, or a column of " +
                                    "an SQL query on the project database or of a file. into = write the best (or pick-th) fit " +
                                    "into a distribution field (Element.field, e.g. Srv.delayStrategy; undoable).", Tags = new[] { Tag })]
        public static object Fit(
            [CliArg("values", "The data, '1.2, 3.4, …'.")] string values = "",
            [CliArg("sql", "A query on the project database; its column (or the first) is the data.")] string sql = "",
            [CliArg("file", "A CSV / Excel file; its column (or the first) is the data.")] string file = "",
            [CliArg("column", "The column of the query / file.")] string column = "",
            [CliArg("sheet", "file: the Excel sheet.")] string sheet = "",
            [CliArg("criterion", "'ad' (Anderson-Darling) or 'ks' (Kolmogorov-Smirnov).")] string criterion = "ad",
            [CliArg("top", "How many ranked fits to answer.")] int top = 5,
            [CliArg("into", "Write a fit into this distribution field: 'Element.field' (e.g. 'Srv.delayStrategy').")] string into = "",
            [CliArg("pick", "into: which ranked fit (1 = the best).")] int pick = 1)
            => AgentReply.Of(() =>
            {
                var data = FitData(values, sql, file, column, sheet);
                if (data.Count < 5) throw new ArgumentException($"{data.Count} value(s): at least 5 are needed to fit.");
                var c = (criterion ?? "ad").Trim().ToLowerInvariant();
                if (c != "ad" && c != "ks") throw new ArgumentException($"criterion: '{criterion}' is not ad or ks.");
                var fits = DistributionFitter.FitAll(new FitSample(data), c == "ks" ? FitCriterion.KolmogorovSmirnov : FitCriterion.AndersonDarling);
                if (fits.Count == 0) throw new ArgumentException("No distribution could be fitted to these values.");
                object written = null;
                if (!string.IsNullOrWhiteSpace(into))
                {
                    if (pick < 1 || pick > fits.Count) throw new ArgumentException($"pick: {pick} is not between 1 and {fits.Count}.");
                    written = WriteFit(into, fits[pick - 1].Distribution);
                }
                var sample = new FitSample(data);
                return new
                {
                    n = sample.N, mean = sample.Mean, std = sample.StdDev, min = sample.Min, max = sample.Max,
                    fits = fits.Take(Math.Max(1, top)).Select(f => new
                    {
                        rank = f.Rank, family = f.Distribution.Family,
                        parameters = f.Distribution.ParameterNames.Zip(f.Distribution.Parameters, (k, v) => new { k, v }).ToDictionary(p => p.k, p => p.v),
                        spec = f.Spec, verdict = f.VerdictText, adP = Finite(f.ADP), ksP = Finite(f.KSP), aic = Finite(f.AIC),
                    }).ToList(),
                    written,
                };
            });

        private static List<double> FitData(string values, string sql, string file, string column, string sheet)
        {
            int given = new[] { values, sql, file }.Count(x => !string.IsNullOrWhiteSpace(x));
            if (given != 1) throw new ArgumentException("Give the data one way: values, sql or file.");
            IEnumerable<string> texts;
            if (!string.IsNullOrWhiteSpace(values)) texts = values.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            else
            {
                var table = !string.IsNullOrWhiteSpace(sql) ? Database().QueryTable(sql)
                          : TableFiles.Read(ProjectFile(file), string.IsNullOrWhiteSpace(sheet) ? null : sheet);
                if (table.Count == 0) throw new ArgumentException("The query / file gives no column.");
                string col = string.IsNullOrWhiteSpace(column) ? table.Keys.First() : column.Trim();
                if (!table.TryGetValue(col, out var list))
                    throw new ArgumentException($"column: no '{col}' (columns: {string.Join(", ", table.Keys)}).");
                texts = list;
            }
            var data = new List<double>();
            foreach (string t in texts)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (!double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    throw new ArgumentException($"'{t.Trim()}' is not a number (use '.' as the decimal point).");
                if (!double.IsNaN(v) && !double.IsInfinity(v)) data.Add(v);
            }
            return data;
        }

        // the Fit tab's Insert: the distribution type and its parameters into a RandomProcessConfig field
        private static object WriteFit(string into, FittedDistribution d)
        {
            int dot = into.IndexOf('.');
            if (dot <= 0 || dot == into.Length - 1) throw new ArgumentException($"into: '{into}' is not Element.field (e.g. Srv.delayStrategy).");
            var go = FindObject(into.Substring(0, dot).Trim());
            string field = into.Substring(dot + 1).Trim();
            SerializedProperty prop = null;
            UnityEngine.Object owner = null;
            foreach (var c in go.GetComponents<UnityEngine.MonoBehaviour>())
            {
                if (c == null) continue;
                var p = new SerializedObject(c).FindProperty(field);
                if (p != null && p.FindPropertyRelative("strategyType") != null) { prop = p; owner = c; break; }
            }
            if (prop == null) throw new ArgumentException($"into: '{go.name}' has no distribution field '{field}'.");
            if (!Enum.TryParse(d.Family, out RandomProcessType type))
                throw new ArgumentException($"into: {d.Family} is not a distribution type of the field.");
            prop.FindPropertyRelative("strategyType").enumValueIndex = (int)type;
            var fields = RandomProcessConfigDrawer.Parameters(type);
            for (int i = 0; i < fields.Length && i < d.Parameters.Length; i++)
            {
                var q = prop.FindPropertyRelative(fields[i].field);
                if (q == null) continue;
                if (q.propertyType == SerializedPropertyType.Integer) q.intValue = (int)Math.Round(d.Parameters[i]);
                else q.doubleValue = d.Parameters[i];
            }
            prop.serializedObject.ApplyModifiedProperties();   // Undo-recorded
            return new { into = go.name + "." + field, family = d.Family, spec = d.ToSpec(), component = owner.GetType().Name };
        }
    }
}
