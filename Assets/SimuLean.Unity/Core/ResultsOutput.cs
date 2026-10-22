using System;
using System.Collections.Generic;
using System.IO;
using SimuLean.Data;
using SimuLean.Results;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Where the project's results go: the exports of the Experimenter, the Optimizer and single runs
    /// (<see cref="ResultExport"/>) into the results folder of the project settings (<see cref="SimuLeanProjectSettings"/>,
    /// default <c>SimuLeanResults/</c>, git-ignored) and, for SQLite, the project database; the confidence level of the
    /// settings. Keeps the last result set (<see cref="Last"/>) for the SimuLean window's Results tab.
    /// </summary>
    public static class ResultsOutput
    {
        private static readonly object sync = new object();
        private static ResultSet last;

        /// <summary>The last result set produced in this session (exported or not); null if none.</summary>
        public static ResultSet Last { get { lock (sync) return last; } }

        /// <summary>Raised (on the producing thread) when <see cref="Last"/> changes.</summary>
        public static event Action<ResultSet> Produced;

        /// <summary>The project folder (call on the main thread; pass it to worker threads).</summary>
        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>The results folder of the project at <paramref name="projectRoot"/> (its settings).</summary>
        public static string Folder(string projectRoot)
        {
            string folder = SimuLeanProjectSettings.Load(projectRoot).resultsFolder;
            return Path.IsPathRooted(folder) ? folder : Path.Combine(projectRoot, folder);
        }

        /// <summary>The project database (the project settings' <c>databasePath</c>, default <c>SimuLeanData/simulean.db</c>;
        /// The one place every data table, timetable, pick list and export resolves it).</summary>
        public static string DatabasePath(string projectRoot)
        {
            string path = SimuLeanProjectSettings.Load(projectRoot).databasePath;
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
        }

        /// <summary>Records <paramref name="set"/> as the last result set (the Results tab shows it) with the project's
        /// confidence level.</summary>
        public static void Publish(ResultSet set, string projectRoot)
        {
            if (set == null) return;
            set.Confidence = SimuLeanProjectSettings.Load(projectRoot).confidence;
            lock (sync) last = set;
            Produced?.Invoke(set);
        }

        /// <summary>Publishes <paramref name="set"/> and writes it in <paramref name="formats"/>; returns what was written.
        /// Thread-safe given <paramref name="projectRoot"/> (<see cref="ProjectRoot"/> read on the main thread).</summary>
        public static List<string> Export(ResultSet set, ResultFormats formats, string projectRoot)
        {
            Publish(set, projectRoot);
            if (formats == ResultFormats.None) return new List<string>();
            string folder = Folder(projectRoot);
            Directory.CreateDirectory(folder);
            string ignore = Path.Combine(folder, ".gitignore");
            if (!File.Exists(ignore)) File.WriteAllText(ignore, "*\n!.gitignore\n");
            ProjectDatabase db = (formats & ResultFormats.Sqlite) != 0 ? ProjectDatabaseRegistry.GetOrOpen(DatabasePath(projectRoot)) : null;
            return ResultExport.Write(set, folder, formats, db);
        }

        /// <summary>The results store of the project database (thread-safe given <paramref name="projectRoot"/>).</summary>
        public static ResultStore Store(string projectRoot)
            => new ResultStore(ProjectDatabaseRegistry.GetOrOpen(StoreOverride ?? DatabasePath(projectRoot)));

        /// <summary>Tests only: another database for the results store, so test runs never fill the project's (null = the project's).</summary>
        public static string StoreOverride { get; set; }

        /// <summary>Keeps <paramref name="set"/> in the results store (once: a stored set returns its id) and returns its id.</summary>
        public static int Save(ResultSet set, string projectRoot)
        {
            if (set == null) throw new ArgumentNullException(nameof(set));
            if (set.StoreId > 0) return set.StoreId;
            int id = Store(projectRoot).Save(set);
            Stored?.Invoke(set);
            return id;
        }

        /// <summary>Raised when a set is kept in the store (the Results tab refreshes its list).</summary>
        public static event Action<ResultSet> Stored;

        /// <summary>
        /// The end of an experiment, a study or an optimisation: <see cref="Export"/>s <paramref name="set"/> and keeps
        /// it in the results store when the project settings say so (<c>storeResults</c>, off by default: results are stored only on request). A store failure is
        /// logged, never thrown: the results are still published and exported.
        /// </summary>
        public static List<string> Keep(ResultSet set, ResultFormats formats, string projectRoot)
        {
            var written = Export(set, formats, projectRoot);
            if (set != null && SimuLeanProjectSettings.Load(projectRoot).storeResults)
            {
                try { written.Add($"results store #{Save(set, projectRoot)} ({DatabasePath(projectRoot)})"); }
                catch (Exception e) { Debug.LogWarning($"[SimuLean] The results could not be kept in the project database: {e.Message}"); }
            }
            return written;
        }

        /// <summary>A single run as a result set (source <c>run</c>, one scenario <c>run</c>, one replication).</summary>
        public static ResultSet SingleRun(RunResults results, string model, DateTime when, double elapsedSeconds = 0)
        {
            var set = new ResultSet { Name = $"Run_{(string.IsNullOrEmpty(model) ? "model" : model)}_{when:yyyyMMdd_HHmmss}", Source = "run", Model = model ?? "", Created = when };
            set.Scenarios.Add(new ResultScenario { Name = "run" });
            set.Runs.Add(new ResultRun { Scenario = "run", Replication = 1, Seed = results?.Info.Seed ?? 0, ElapsedSeconds = elapsedSeconds, Results = results });
            return set;
        }
    }
}
