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
    /// set and the stored ones, keep one, export it, chart it — as commands.
    /// </summary>
    public static partial class SimuLeanCommands
    {
        [CliCommand("simulean_results", "Result sets: action=list (the last one and the stored ones), read (a set's metrics per " +
                                        "scenario, mean ± half-width; the last study's answer as its command gave it, a page at a time with top / " +
                                        "offset / sort, every digit and each run with detail=full), save (keep a set in the project's store), export (files: " +
                                        "csv, excel, sqlite, json), chart (a PNG / SVG: kind bar | box | histogram | scatter | " +
                                        "line | states; yAxis and xAxis written as in a study: a named metric, a factor path or a statistic path " +
                                        "'last' (default), a stored set's id or its name) or delete (path= a file or folder that export, " +
                                        "chart, simulean_capture or simulean_record_start wrote in this editor session under the results " +
                                        "folder or Recordings/, to the OS trash; or resultSet= a set that save stored in this session, " +
                                        "removed from the store; anything else is refused).", Tags = new[] { Tag })]
        public static object Results(
            [CliArg("action", "list | read | save | export | chart | delete.")] string action = "list",
            [CliArg("resultSet", "'last' (the last run / study / optimisation), a stored set's id or its name.")] string resultSet = "last",
            [CliArg("formats", "export: 'csv;excel;sqlite;json' (any of them).")] string formats = "csv",
            [CliArg("kind", "chart: bar | box | histogram | scatter | line | states.")] string kind = "bar",
            [CliArg("yAxis", "chart: the value, as a study writes it — a named metric (out), a factor (Buf.capacity) or a statistic path (Srv.utilization, Srv;Buf.utilization, *.utilization, team:*.utilization, model.makespan).")] string yAxis = "",
            [CliArg("xAxis", "chart: scatter's x axis (as yAxis).")] string xAxis = "",
            [CliArg("groupBy", "chart: scenario | object | run.")] string groupBy = "scenario",
            [CliArg("title", "chart: its title.")] string title = "",
            [CliArg("path", "chart: the file, .png or .svg (empty = Recordings/Charts/<set>_<time>.png); delete: the file or folder to delete (as the command that wrote it answered, or relative to the project).")] string path = "",
            [CliArg("name", "save: the stored set's name (empty = the set's).")] string name = "",
            [CliArg("detail", "read: 'metrics' (the compact answer: the named metrics, 6 significant digits) or 'full' (every digit, the model block, the elements' and resources' KPIs and perRun: each run's end reason and metric values); empty = as the study answered (a stored set: metrics).")] string detail = "",
            [CliArg("top", "read: how many scenarios a page gives (0 = 25; an answer over ~40 KB without top / offset / sort is a table of the first ones).")] int top = 0,
            [CliArg("sort", "read: a page's order — a metric (ascending), -metric or 'metric desc' descending (empty = the plan's order).")] string sort = "",
            [CliArg("offset", "read: a page's first scenario, from 0 (page.next of an answer gives the next page).")] int offset = 0)
            => AgentReply.Of(() =>
            {
                switch ((action ?? "list").Trim().ToLowerInvariant())
                {
                    case "list": return ListResults();
                    case "read": return ReadResults(resultSet, detail, top, sort, offset);
                    case "save": return SaveResults(resultSet, name);
                    case "export": return ExportResults(resultSet, formats);
                    case "chart": return ChartResults(resultSet, kind, yAxis, xAxis, groupBy, title, path);
                    case "delete": return DeleteResults(resultSet, path);
                }
                throw new ArgumentException($"action: '{action}' is not list, read, save, export, chart or delete.");
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
                return ResultsOutput.Last ?? throw new ArgumentException("resultSet: no result set yet — run a study (simulean_run_headless) first.");
            var store = ResultsOutput.Store(ResultsOutput.ProjectRoot);
            var all = store.List();
            var hit = int.TryParse(s, out int id) ? all.FirstOrDefault(x => x.Id == id) : all.Where(x => x.Name == s).OrderByDescending(x => x.Created).FirstOrDefault();
            if (hit == null)
                throw new ArgumentException($"resultSet: no stored set '{s}'" + (all.Count == 0 ? " (none stored)." : $" (stored: {string.Join(", ", all.Take(10).Select(x => $"#{x.Id} {x.Name}"))})."));
            return store.Load(hit.Id);
        }

        /// <summary>
        /// <c>action=read</c>: a set's answer as a study gives it (<see cref="SimuLean.Optimization.StudySpec.Answer"/>), paged
        /// (<see cref="SimuLean.Optimization.AnswerPages.Study"/>). The last study the commands ran answers as its command did
        /// — its settings, warnings, reference scenario —, so its pages need no second run; another set (a stored one, an
        /// optimisation's) answers its scenarios and metrics.
        /// </summary>
        private static object ReadResults(string set, string detail, int top, string sort, int offset)
        {
            var rs = ResultSetOf(set);
            string d = (detail ?? "").Trim();
            if (d.Length > 0 && !d.Equals("full", StringComparison.OrdinalIgnoreCase) && !d.Equals("metrics", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"detail: '{detail}' is not 'metrics' or 'full'.");
            var study = lastStudy;
            bool ofStudy = study != null && ReferenceEquals(study.Set, rs);
            bool full = d.Length > 0 ? d.Equals("full", StringComparison.OrdinalIgnoreCase) : ofStudy && study.Full;
            var answer = ofStudy ? study.Answer(full) : SimuLean.Optimization.StudySpec.Answer(rs, full, null);
            answer["name"] = rs.Name;
            answer["source"] = rs.Source;
            answer["metadata"] = rs.Metadata.GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => (object)g.Last().Value);
            string which = string.IsNullOrWhiteSpace(set) ? "last" : set.Trim();
            return SimuLean.Optimization.AnswerPages.Study(answer, top, sort, offset, PagesOf(which.Contains(" ") ? $"'{which}'" : which, full), MaxAnswer);
        }

        private static object SaveResults(string set, string name)
        {
            var rs = ResultSetOf(set);
            if (!string.IsNullOrWhiteSpace(name)) rs.Name = name.Trim();
            int id = ResultsOutput.Save(rs, ResultsOutput.ProjectRoot);
            AgentSaved(id);
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
            var written = ResultsOutput.Export(rs, f, ResultsOutput.ProjectRoot);
            // the project database (sqlite) is not a file the export made: never one to delete
            string database = FullPath(ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot));
            foreach (string p in written) if (!SamePath(FullPath(p), database)) AgentWrote(p);
            return new { exported = written.Select(p => p.Replace('\\', '/')).ToList() };
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
                if (string.IsNullOrWhiteSpace(y)) throw new ArgumentException("yAxis: the value to chart, e.g. out (a named metric) or Srv.utilization.");
                query.Y = ChartValueOf(y, "yAxis", rs);
            }
            if (!string.IsNullOrWhiteSpace(x)) query.X = ChartValueOf(x, "xAxis", rs);
            else if (k == ChartKind.Scatter) throw new ArgumentException("xAxis: a scatter needs xAxis, e.g. a factor such as Buffer.capacity.");
            var data = ChartBuilder.Build(query, rs);
            if (data.Series.Count == 0 && data.Segments.Count == 0)
                throw new ArgumentException($"The chart has no data: check yAxis / xAxis against the set's metrics ({string.Join(", ", rs.MetricNames)}) and objects.");
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(ResultsOutput.ProjectRoot, "Recordings", "Charts", $"{Safe(rs.Name)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            else if (!Path.IsPathRooted(path)) path = Path.Combine(ResultsOutput.ProjectRoot, path);
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) path += ".png";
            ChartRenderer.Save(data, path);
            AgentWrote(path);
            return new { path = path.Replace('\\', '/'), kind = k.ToString(), categories = data.Categories.Count, series = data.Series.Select(s => s.Name).ToList() };
        }

        // a named metric, a factor or a statistic path, as a study writes them (ChartValue.Parse)
        private static ChartValue ChartValueOf(string text, string what, ResultSet set)
        {
            try { return ChartValue.Parse(text, set); }
            catch (ArgumentException ex) { throw new ArgumentException($"{what}: {ex.Message}"); }
        }

        // ── what the commands wrote in this editor session, and deleting it ──────────────

        // kept across domain reloads (a take enters and leaves Play) as SimuLeanModelCommands.AgentScenes
        private const string WrittenKey = "SimuLean.AgentWrittenFiles", SavedSetsKey = "SimuLean.AgentSavedSets";

        /// <summary>Notes a file or folder a command wrote (an export, a chart, a capture, a recorder take): the only ones
        /// <c>action=delete</c> removes — a file the user made under the same folders is never one.</summary>
        internal static void AgentWrote(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full = FullPath(path);
            var list = Listed(WrittenKey);
            if (list.Any(p => SamePath(p, full))) return;
            list.Add(full);
            UnityEditor.SessionState.SetString(WrittenKey, string.Join("|", list));
        }

        /// <summary>Notes a result set a command stored (<c>action=save</c>).</summary>
        internal static void AgentSaved(int id)
        {
            var list = Listed(SavedSetsKey);
            string key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (list.Contains(key)) return;
            list.Add(key);
            UnityEditor.SessionState.SetString(SavedSetsKey, string.Join("|", list));
        }

        private static List<string> Listed(string key)
            => (UnityEditor.SessionState.GetString(key, "") ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        private static void Unlist(string key, Func<string, bool> gone)
            => UnityEditor.SessionState.SetString(key, string.Join("|", Listed(key).Where(p => !gone(p))));

        internal static string FullPath(string path)
        {
            string p = Path.IsPathRooted(path) ? path : Path.Combine(ResultsOutput.ProjectRoot, path);
            return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static StringComparison PathComparison
            => UnityEngine.Application.platform == UnityEngine.RuntimePlatform.LinuxEditor ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        private static bool SamePath(string a, string b) => string.Equals(a, b, PathComparison);

        // a path inside a folder (not the folder itself)
        private static bool Under(string path, string folder) => path.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison);

        /// <summary>
        /// <c>action=delete</c>: a stored set that <c>save</c> stored in this editor session, removed from the store; or a file /
        /// folder that a command wrote in this session (<see cref="AgentWrote"/>, or one inside a folder it wrote) under the
        /// results folder or <c>Recordings/</c>, moved to the OS trash (<see cref="Trash"/>). Anything else is refused, naming why, so
        /// nothing is deleted through <c>eval</c>.
        /// </summary>
        private static object DeleteResults(string set, string path)
        {
            bool byPath = !string.IsNullOrWhiteSpace(path);
            bool bySet = !string.IsNullOrWhiteSpace(set) && !set.Trim().Equals("last", StringComparison.OrdinalIgnoreCase);
            if (byPath == bySet)
                throw new ArgumentException("delete: give path= (a file or folder that export, chart, simulean_capture or simulean_record_start wrote) " +
                                            "or resultSet= (a stored set's id or name that save stored), one of them.");
            if (bySet)
            {
                var store = ResultsOutput.Store(ResultsOutput.ProjectRoot);
                var all = store.List();
                string s = set.Trim();
                var hit = int.TryParse(s, out int id) ? all.FirstOrDefault(x => x.Id == id) : all.Where(x => x.Name == s).OrderByDescending(x => x.Created).FirstOrDefault();
                if (hit == null) throw new ArgumentException($"resultSet: no stored set '{s}' — not deleted.");
                if (!Listed(SavedSetsKey).Contains(hit.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    throw new ArgumentException($"resultSet: #{hit.Id} '{hit.Name}' was not stored by simulean_results action=save in this editor session: " +
                                                "not deleted (it may be the user's; ask them, or remove it in the Results tab).");
                store.Delete(hit.Id);
                Unlist(SavedSetsKey, k => k == hit.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return new { deleted = $"#{hit.Id} {hit.Name}", from = "the results store" };
            }
            string full = FullPath(path.Trim());
            string root = ResultsOutput.ProjectRoot;
            var places = new[] { FullPath(ResultsOutput.Folder(root)), FullPath(Path.Combine(root, "Recordings")) };
            if (!places.Any(f => Under(full, f)))
                throw new ArgumentException($"path: '{path}' is not inside the results folder or Recordings/: not deleted (the commands delete only " +
                                            "what they wrote there).");
            if (SamePath(full, FullPath(ResultsOutput.DatabasePath(root))))
                throw new ArgumentException($"path: '{path}' is the project database: not deleted.");
            var written = Listed(WrittenKey);
            if (!written.Any(w => SamePath(w, full) || Under(full, w)))
                throw new ArgumentException($"path: '{path}' was not written by these commands in this editor session: not deleted (it may be the " +
                                            "user's; ask them)." + (written.Count == 0 ? "" : $" Written this session: {IdRange(written.Select(w => w.Replace('\\', '/')).ToList())}."));
            if (!File.Exists(full) && !Directory.Exists(full))
            {
                Unlist(WrittenKey, w => SamePath(w, full));
                throw new ArgumentException($"path: '{path}' does not exist (deleted already?).");
            }
            string where = MoveToTrash(full);
            Unlist(WrittenKey, w => SamePath(w, full) || Under(w, full));
            return new { deleted = full.Replace('\\', '/'), to = where };
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct ShellFileOperation
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom, pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SHFileOperation(ref ShellFileOperation operation);

        /// <summary>How <c>action=delete</c> removes a file or folder (returns where it went): the OS trash
        /// (<see cref="Trash"/>). A test replaces it, so a test run sends nothing to the machine's recycle bin.</summary>
        internal static Func<string, string> MoveToTrash = Trash;

        /// <summary>Moves a file or folder to the OS trash, with no dialog, and says where: the Windows recycle bin (the shell's
        /// delete with undo: FO_DELETE, FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR),
        /// the macOS Trash (moved into ~/.Trash). Elsewhere, or when it fails, nothing is deleted and it throws: never a
        /// permanent delete.</summary>
        private static string Trash(string full)
        {
            var platform = UnityEngine.Application.platform;
            if (platform == UnityEngine.RuntimePlatform.WindowsEditor)
            {
                var op = new ShellFileOperation { wFunc = 3, pFrom = full + "\0\0", fFlags = 0x40 | 0x10 | 0x4 | 0x400 | 0x200 };
                int code = SHFileOperation(ref op);
                if (code != 0 || op.fAnyOperationsAborted != 0 || File.Exists(full) || Directory.Exists(full))
                    throw new IOException($"'{full}' could not be moved to the recycle bin (code {code}): not deleted.");
                return "the recycle bin";
            }
            if (platform == UnityEngine.RuntimePlatform.OSXEditor)
            {
                string trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".Trash");
                if (!Directory.Exists(trash)) throw new IOException("No ~/.Trash: not deleted.");
                string name = Path.GetFileName(full), target = Path.Combine(trash, name);
                for (int i = 2; File.Exists(target) || Directory.Exists(target); i++)
                    target = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(name)} {i}{Path.GetExtension(name)}");
                if (Directory.Exists(full)) Directory.Move(full, target); else File.Move(full, target);
                return "the Trash";
            }
            throw new InvalidOperationException("No trash this editor can use on this platform: not deleted (ask the user to remove it).");
        }

        private static string Safe(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string((name ?? "results").Select(ch => bad.Contains(ch) || ch == ' ' ? '_' : ch).ToArray());
        }
    }
}
