using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Data;

namespace SimuLean.Results
{
    /// <summary>A result set as the store lists it.</summary>
    public sealed class StoredSet
    {
        public int Id { get; set; }
        public string Name { get; set; }
        /// <summary>experiment, optimization, study, run… (the set's source).</summary>
        public string Kind { get; set; }
        public string Model { get; set; }
        public DateTime Created { get; set; }
        public int Scenarios { get; set; }
        public int Runs { get; set; }
        public int Candidates { get; set; }

        public override string ToString() => $"#{Id} {Name} ({Kind}, {Model}, {Created:yyyy-MM-dd HH:mm})";
    }

    /// <summary>
    /// The results store: every result set kept in the project database, indexed by set — the
    /// catalog <c>rs_sets</c> (integer id, name, kind, model, created, confidence) and the set's scenarios with their
    /// parameters, runs with their metrics, every object's statistics, the time series, the extra tables and an
    /// optimisation's candidates, in <c>rs_*</c> tables with indexes by set / scenario / object / metric and a schema
    /// version (<c>rs_meta</c>). <see cref="Save"/> returns the set's id; <see cref="Load"/> gives the same
    /// <see cref="ResultSet"/> back; <see cref="Statistic"/> / <see cref="Metric"/> query one value across sets. Engine
    /// only (any <see cref="ProjectDatabase"/>); thread-safe through the database's lock.
    /// </summary>
    public sealed class ResultStore
    {
        /// <summary>The store's schema version (<c>rs_meta.schema_version</c>).</summary>
        public const int SchemaVersion = 2;   // 2: rs_timeline / rs_jobs (G3c–d); a version-1 database gets the tables on opening

        private readonly ProjectDatabase db;

        public ResultStore(ProjectDatabase database)
        {
            db = database ?? throw new ArgumentNullException(nameof(database));
            EnsureSchema();
        }

        private static readonly string[] Schema =
        {
            "CREATE TABLE IF NOT EXISTS rs_meta (key TEXT PRIMARY KEY, value TEXT)",
            "CREATE TABLE IF NOT EXISTS rs_sets (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, kind TEXT, model TEXT, created TEXT, confidence REAL)",
            "CREATE TABLE IF NOT EXISTS rs_set_meta (set_id INTEGER NOT NULL REFERENCES rs_sets(id), position INTEGER, key TEXT, value TEXT)",
            "CREATE TABLE IF NOT EXISTS rs_metric_names (set_id INTEGER NOT NULL REFERENCES rs_sets(id), position INTEGER, name TEXT)",
            "CREATE TABLE IF NOT EXISTS rs_scenarios (set_id INTEGER NOT NULL REFERENCES rs_sets(id), id INTEGER, name TEXT, PRIMARY KEY (set_id, id))",
            "CREATE TABLE IF NOT EXISTS rs_parameters (set_id INTEGER NOT NULL, scenario_id INTEGER, position INTEGER, name TEXT, value TEXT)",
            "CREATE TABLE IF NOT EXISTS rs_runs (set_id INTEGER NOT NULL, id INTEGER, scenario_id INTEGER, replication INTEGER, seed INTEGER, " +
                "elapsed REAL, recorded INTEGER, model TEXT, end_time REAL, warmup REAL, end_reason TEXT, deadlocked INTEGER, waiting TEXT, PRIMARY KEY (set_id, id))",
            "CREATE TABLE IF NOT EXISTS rs_run_metrics (set_id INTEGER NOT NULL, run_id INTEGER, position INTEGER, name TEXT, value REAL)",
            "CREATE TABLE IF NOT EXISTS rs_objects (set_id INTEGER NOT NULL, run_id INTEGER, id INTEGER, scope TEXT, object_id TEXT, name TEXT, type TEXT, grp TEXT)",
            "CREATE TABLE IF NOT EXISTS rs_stats (set_id INTEGER NOT NULL, run_id INTEGER, object INTEGER, position INTEGER, metric TEXT, value REAL)",
            "CREATE TABLE IF NOT EXISTS rs_series (set_id INTEGER NOT NULL, run_id INTEGER, series INTEGER, scope TEXT, name TEXT, metric TEXT, t REAL, value REAL)",
            "CREATE TABLE IF NOT EXISTS rs_extra (set_id INTEGER NOT NULL, position INTEGER, name TEXT, json TEXT)",
            "CREATE TABLE IF NOT EXISTS rs_timeline (set_id INTEGER NOT NULL, run_id INTEGER, scope TEXT, name TEXT, state TEXT, t_start REAL, t_end REAL)",
            "CREATE TABLE IF NOT EXISTS rs_jobs (set_id INTEGER NOT NULL, run_id INTEGER, item TEXT, element TEXT, t_start REAL, t_end REAL)",
            "CREATE INDEX IF NOT EXISTS rs_jobs_by_run ON rs_jobs (set_id, run_id)",
            "CREATE TABLE IF NOT EXISTS rs_candidates (set_id INTEGER NOT NULL, id INTEGER, iteration INTEGER, fitness REAL, error TEXT)",
            "CREATE TABLE IF NOT EXISTS rs_candidate_values (set_id INTEGER NOT NULL, candidate_id INTEGER, position INTEGER, kind TEXT, name TEXT, text TEXT, value REAL)",
            "CREATE INDEX IF NOT EXISTS rs_runs_by_scenario ON rs_runs (set_id, scenario_id)",
            "CREATE INDEX IF NOT EXISTS rs_run_metrics_by_name ON rs_run_metrics (set_id, name)",
            "CREATE INDEX IF NOT EXISTS rs_objects_by_name ON rs_objects (set_id, scope, name)",
            "CREATE INDEX IF NOT EXISTS rs_stats_by_metric ON rs_stats (set_id, metric, object)",
            "CREATE INDEX IF NOT EXISTS rs_series_by_name ON rs_series (set_id, name, metric)",
            "CREATE INDEX IF NOT EXISTS rs_timeline_by_run ON rs_timeline (set_id, run_id, name)",
            "CREATE INDEX IF NOT EXISTS rs_candidates_by_set ON rs_candidates (set_id, iteration)",
            "CREATE INDEX IF NOT EXISTS rs_candidate_values_by_set ON rs_candidate_values (set_id, candidate_id)",
        };

        private static readonly string[] SetTables =
        {
            "rs_set_meta", "rs_metric_names", "rs_scenarios", "rs_parameters", "rs_runs", "rs_run_metrics", "rs_objects", "rs_stats",
            "rs_series", "rs_extra", "rs_candidates", "rs_candidate_values", "rs_timeline", "rs_jobs",
        };

        private void EnsureSchema()
        {
            if (db.TableExists("rs_meta"))
            {
                var rows = db.Query("SELECT value FROM rs_meta WHERE key = 'schema_version'");
                int version = rows.Count > 0 && int.TryParse(Convert.ToString(rows[0]["value"], CultureInfo.InvariantCulture), out int v) ? v : 0;
                if (version > SchemaVersion)
                    throw new InvalidOperationException($"Results store: the database has schema {version}, this SimuLean knows {SchemaVersion} — update SimuLean.");
            }
            foreach (string sql in Schema) db.Execute(sql);
            db.Execute("INSERT OR REPLACE INTO rs_meta (key, value) VALUES ('schema_version', ?)", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        }

        // ── saving ───────────────────────────────────────────────────────────

        /// <summary>Stores <paramref name="set"/> (a new entry, whatever its name) and returns its id.</summary>
        public int Save(ResultSet set, string kind = null)
        {
            if (set == null) throw new ArgumentNullException(nameof(set));
            int id = (int)db.Insert("INSERT INTO rs_sets (name, kind, model, created, confidence) VALUES (?, ?, ?, ?, ?)",
                       set.Name ?? "", kind ?? set.Source ?? "", set.Model ?? "", set.Created.ToString("o", CultureInfo.InvariantCulture), set.Confidence);
            try
            {
                var meta = new List<object[]>();
                for (int i = 0; i < set.Metadata.Count; i++) meta.Add(new object[] { id, i, set.Metadata[i].Key, set.Metadata[i].Value });
                Batch("INSERT INTO rs_set_meta VALUES (?, ?, ?, ?)", meta);
                var names = new List<object[]>();
                for (int i = 0; i < set.MetricNames.Count; i++) names.Add(new object[] { id, i, set.MetricNames[i] });
                Batch("INSERT INTO rs_metric_names VALUES (?, ?, ?)", names);

                var scenarioId = new Dictionary<string, int>(StringComparer.Ordinal);
                var scenarios = new List<object[]>();
                var parameters = new List<object[]>();
                for (int s = 0; s < set.Scenarios.Count; s++)
                {
                    var sc = set.Scenarios[s];
                    scenarioId[sc.Name ?? ""] = s;
                    scenarios.Add(new object[] { id, s, sc.Name ?? "" });
                    for (int p = 0; p < sc.Parameters.Count; p++) parameters.Add(new object[] { id, s, p, sc.Parameters[p].Key, sc.Parameters[p].Value });
                }
                Batch("INSERT INTO rs_scenarios VALUES (?, ?, ?)", scenarios);
                Batch("INSERT INTO rs_parameters VALUES (?, ?, ?, ?, ?)", parameters);

                var runs = new List<object[]>();
                var runMetrics = new List<object[]>();
                var objects = new List<object[]>();
                var stats = new List<object[]>();
                var series = new List<object[]>();
                var timeline = new List<object[]>();
                var jobs = new List<object[]>();
                int objectId = 0, seriesId = 0;
                for (int r = 0; r < set.Runs.Count; r++)
                {
                    var run = set.Runs[r];
                    if (!scenarioId.TryGetValue(run.Scenario ?? "", out int sid)) scenarioId[run.Scenario ?? ""] = sid = AddScenario(id, run.Scenario, scenarioId.Count);
                    var info = run.Results?.Info;
                    runs.Add(new object[]
                    {
                        id, r, sid, run.Replication, run.Seed, run.ElapsedSeconds, run.Results != null ? 1 : 0, info?.Model, Real(info?.EndTime),
                        Real(info?.WarmupTime), info?.EndReason, info != null && info.Deadlocked ? 1 : 0, info != null ? string.Join("\n", info.Waiting) : null,
                    });
                    int m = 0;
                    foreach (var kv in run.Metrics) runMetrics.Add(new object[] { id, r, m++, kv.Key, Real(kv.Value) });
                    if (run.Results == null) continue;
                    foreach (var o in run.Results.Objects)
                    {
                        objects.Add(new object[] { id, r, objectId, ResultSet.Scope(o.Scope), o.Id, o.Name, o.Type, o.Group });
                        int position = 0;
                        foreach (var st in o.Metrics) stats.Add(new object[] { id, r, objectId, position++, st.Key, Real(st.Value) });
                        objectId++;
                    }
                    foreach (var ts in run.Results.Series)
                    {
                        for (int i = 0; i < ts.Times.Count; i++)
                            series.Add(new object[] { id, r, seriesId, ResultSet.Scope(ts.Scope), ts.Name, ts.Metric, ts.Times[i], Real(ts.Values[i]) });
                        seriesId++;
                    }
                    foreach (var e in run.Results.Timeline)
                        timeline.Add(new object[] { id, r, ResultSet.Scope(e.Scope), e.Name, e.State, e.Start, e.End });
                    foreach (var j in run.Results.Jobs) jobs.Add(new object[] { id, r, j.Item, j.Element, j.Start, j.End });
                }
                Batch("INSERT INTO rs_runs VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", runs);
                Batch("INSERT INTO rs_run_metrics VALUES (?, ?, ?, ?, ?)", runMetrics);
                Batch("INSERT INTO rs_objects VALUES (?, ?, ?, ?, ?, ?, ?, ?)", objects);
                Batch("INSERT INTO rs_stats VALUES (?, ?, ?, ?, ?, ?)", stats);
                Batch("INSERT INTO rs_series VALUES (?, ?, ?, ?, ?, ?, ?, ?)", series);
                Batch("INSERT INTO rs_timeline VALUES (?, ?, ?, ?, ?, ?, ?)", timeline);
                Batch("INSERT INTO rs_jobs VALUES (?, ?, ?, ?, ?, ?)", jobs);

                var extra = new List<object[]>();
                for (int i = 0; i < set.Extra.Count; i++) extra.Add(new object[] { id, i, set.Extra[i].Name, TableJson(set.Extra[i]) });
                Batch("INSERT INTO rs_extra VALUES (?, ?, ?, ?)", extra);

                var candidates = new List<object[]>();
                var values = new List<object[]>();
                for (int c = 0; c < set.Candidates.Count; c++)
                {
                    var cand = set.Candidates[c];
                    candidates.Add(new object[] { id, c, cand.Iteration, Real(cand.Fitness), cand.Error });
                    int position = 0;
                    foreach (var p in cand.Parameters) values.Add(new object[] { id, c, position++, "parameter", p.Key, p.Value, null });
                    foreach (var kv in cand.Metrics) values.Add(new object[] { id, c, position++, "metric", kv.Key, null, Real(kv.Value) });
                }
                Batch("INSERT INTO rs_candidates VALUES (?, ?, ?, ?, ?)", candidates);
                Batch("INSERT INTO rs_candidate_values VALUES (?, ?, ?, ?, ?, ?, ?)", values);
            }
            catch
            {
                Delete(id);   // no half-stored set
                throw;
            }
            set.StoreId = id;
            return id;
        }

        private int AddScenario(int setId, string name, int position)
        {
            db.Execute("INSERT INTO rs_scenarios VALUES (?, ?, ?)", setId, position, name ?? "");
            return position;
        }

        private void Batch(string sql, List<object[]> rows)
        {
            if (rows.Count > 0) db.ExecuteBatch(sql, rows);
        }

        // ── listing, renaming, deleting ──────────────────────────────────────

        /// <summary>The stored sets, newest first (only those of <paramref name="model"/> when given).</summary>
        public List<StoredSet> List(string model = null)
        {
            string where = model == null ? "" : " WHERE s.model = ?";
            var rows = db.Query(
                "SELECT s.id, s.name, s.kind, s.model, s.created, " +
                "(SELECT COUNT(*) FROM rs_scenarios c WHERE c.set_id = s.id) AS scenarios, " +
                "(SELECT COUNT(*) FROM rs_runs r WHERE r.set_id = s.id) AS runs, " +
                "(SELECT COUNT(*) FROM rs_candidates k WHERE k.set_id = s.id) AS candidates " +
                "FROM rs_sets s" + where + " ORDER BY s.id DESC", model == null ? Array.Empty<object>() : new object[] { model });
            var list = new List<StoredSet>();
            foreach (var r in rows)
                list.Add(new StoredSet
                {
                    Id = Int(r["id"]), Name = Str(r["name"]), Kind = Str(r["kind"]), Model = Str(r["model"]),
                    Created = DateTime.TryParse(Str(r["created"]), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime d) ? d : DateTime.MinValue,
                    Scenarios = Int(r["scenarios"]), Runs = Int(r["runs"]), Candidates = Int(r["candidates"]),
                });
            return list;
        }

        /// <summary>Renames a stored set; false when there is no such set.</summary>
        public bool Rename(int id, string name)
        {
            if (!Exists(id)) return false;
            db.Execute("UPDATE rs_sets SET name = ? WHERE id = ?", name ?? "", id);
            return true;
        }

        /// <summary>Deletes a stored set and all its rows; false when there is no such set.</summary>
        public bool Delete(int id)
        {
            bool existed = Exists(id);
            foreach (string t in SetTables) db.Execute($"DELETE FROM {t} WHERE set_id = ?", id);
            db.Execute("DELETE FROM rs_sets WHERE id = ?", id);
            return existed;
        }

        private bool Exists(int id) => db.Query("SELECT id FROM rs_sets WHERE id = ?", id).Count > 0;

        // ── loading ──────────────────────────────────────────────────────────

        /// <summary>The stored set <paramref name="id"/> as a <see cref="ResultSet"/> (the same tables as when it was saved).</summary>
        public ResultSet Load(int id)
        {
            var head = db.Query("SELECT name, kind, model, created, confidence FROM rs_sets WHERE id = ?", id);
            if (head.Count == 0) throw new KeyNotFoundException($"Results store: no set #{id}.");
            var h = head[0];
            var set = new ResultSet
            {
                Name = Str(h["name"]), Source = Str(h["kind"]), Model = Str(h["model"]),
                Created = DateTime.TryParse(Str(h["created"]), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime d) ? d : DateTime.MinValue,
                Confidence = h["confidence"] == null ? 0.95 : Convert.ToDouble(h["confidence"], CultureInfo.InvariantCulture),
                StoreId = id,
            };
            foreach (var r in db.Query("SELECT key, value FROM rs_set_meta WHERE set_id = ? ORDER BY position", id))
                set.Metadata.Add(new KeyValuePair<string, string>(Str(r["key"]), Str(r["value"])));
            foreach (var r in db.Query("SELECT name FROM rs_metric_names WHERE set_id = ? ORDER BY position", id)) set.MetricNames.Add(Str(r["name"]));

            var scenarioName = new Dictionary<int, string>();
            var scenarioById = new Dictionary<int, ResultScenario>();
            foreach (var r in db.Query("SELECT id, name FROM rs_scenarios WHERE set_id = ? ORDER BY id", id))
            {
                var sc = new ResultScenario { Name = Str(r["name"]) };
                scenarioName[Int(r["id"])] = sc.Name;
                scenarioById[Int(r["id"])] = sc;
                set.Scenarios.Add(sc);
            }
            foreach (var r in db.Query("SELECT scenario_id, name, value FROM rs_parameters WHERE set_id = ? ORDER BY scenario_id, position", id))
                if (scenarioById.TryGetValue(Int(r["scenario_id"]), out var sc)) sc.Parameters.Add(new KeyValuePair<string, string>(Str(r["name"]), Str(r["value"])));

            var runs = new Dictionary<int, ResultRun>();
            foreach (var r in db.Query("SELECT * FROM rs_runs WHERE set_id = ? ORDER BY id", id))
            {
                var run = new ResultRun
                {
                    Scenario = scenarioName.TryGetValue(Int(r["scenario_id"]), out string sn) ? sn : "", Replication = Int(r["replication"]),
                    Seed = Int(r["seed"]), ElapsedSeconds = Dbl(r["elapsed"]),
                };
                if (Int(r["recorded"]) == 1)
                {
                    run.Results = new RunResults();
                    var info = run.Results.Info;
                    info.Model = Str(r["model"]); info.Seed = run.Seed; info.EndTime = Dbl(r["end_time"]); info.WarmupTime = Dbl(r["warmup"]);
                    info.EndReason = Str(r["end_reason"]); info.Deadlocked = Int(r["deadlocked"]) == 1;
                    string waiting = Str(r["waiting"]);
                    if (waiting.Length > 0) info.Waiting.AddRange(waiting.Split('\n'));
                }
                runs[Int(r["id"])] = run;
                set.Runs.Add(run);
            }
            foreach (var r in db.Query("SELECT run_id, name, value FROM rs_run_metrics WHERE set_id = ? ORDER BY run_id, position", id))
                if (runs.TryGetValue(Int(r["run_id"]), out var run)) run.Metrics[Str(r["name"])] = Dbl(r["value"]);

            var objects = new Dictionary<int, ResultObject>();
            foreach (var r in db.Query("SELECT run_id, id, scope, object_id, name, type, grp FROM rs_objects WHERE set_id = ? ORDER BY id", id))
                if (runs.TryGetValue(Int(r["run_id"]), out var run) && run.Results != null)
                    objects[Int(r["id"])] = run.Results.Add(new ResultObject(ParseScope(Str(r["scope"])), Str(r["object_id"]), Str(r["name"]), Str(r["type"]), Str(r["grp"])));
            foreach (var r in db.Query("SELECT object, metric, value FROM rs_stats WHERE set_id = ? ORDER BY object, position", id))
                if (objects.TryGetValue(Int(r["object"]), out var o)) o.Set(Str(r["metric"]), Dbl(r["value"]));

            var series = new Dictionary<int, TimeSeries>();
            foreach (var r in db.Query("SELECT run_id, series, scope, name, metric, t, value FROM rs_series WHERE set_id = ? ORDER BY series, rowid", id))
            {
                int sid = Int(r["series"]);
                if (!series.TryGetValue(sid, out var ts))
                {
                    if (!runs.TryGetValue(Int(r["run_id"]), out var run) || run.Results == null) continue;
                    ts = new TimeSeries(ParseScope(Str(r["scope"])), Str(r["name"]), Str(r["metric"]));
                    run.Results.Series.Add(ts);
                    series[sid] = ts;
                }
                ts.Times.Add(Dbl(r["t"]));
                ts.Values.Add(Dbl(r["value"]));
            }

            foreach (var r in db.Query("SELECT run_id, scope, name, state, t_start, t_end FROM rs_timeline WHERE set_id = ? ORDER BY rowid", id))
                if (runs.TryGetValue(Int(r["run_id"]), out var run) && run.Results != null)
                    run.Results.Timeline.Add(new TimelineEntry(ParseScope(Str(r["scope"])), Str(r["name"]), Str(r["state"]), Dbl(r["t_start"]), Dbl(r["t_end"])));

            foreach (var r in db.Query("SELECT run_id, item, element, t_start, t_end FROM rs_jobs WHERE set_id = ? ORDER BY rowid", id))
                if (runs.TryGetValue(Int(r["run_id"]), out var run) && run.Results != null)
                    run.Results.Jobs.Add(new JobEntry(Str(r["item"]), Str(r["element"]), Dbl(r["t_start"]), Dbl(r["t_end"])));

            foreach (var r in db.Query("SELECT name, json FROM rs_extra WHERE set_id = ? ORDER BY position", id))
                set.Extra.Add(TableFromJson(Str(r["name"]), Str(r["json"])));

            var candidates = new Dictionary<int, ResultCandidate>();
            foreach (var r in db.Query("SELECT id, iteration, fitness, error FROM rs_candidates WHERE set_id = ? ORDER BY id", id))
            {
                var c = new ResultCandidate { Iteration = Int(r["iteration"]), Fitness = Dbl(r["fitness"]), Error = r["error"] == null ? null : Str(r["error"]) };
                candidates[Int(r["id"])] = c;
                set.Candidates.Add(c);
            }
            foreach (var r in db.Query("SELECT candidate_id, kind, name, text, value FROM rs_candidate_values WHERE set_id = ? ORDER BY candidate_id, position", id))
            {
                if (!candidates.TryGetValue(Int(r["candidate_id"]), out var c)) continue;
                if (Str(r["kind"]) == "parameter") c.Parameters.Add(new KeyValuePair<string, string>(Str(r["name"]), Str(r["text"])));
                else c.Metrics[Str(r["name"])] = Dbl(r["value"]);
            }
            return set;
        }

        // ── queries across sets ──────────────────────────────────────────────

        /// <summary>
        /// A statistic of an object across stored sets: <c>set_id, set, scenario, replication, object, value</c> — e.g.
        /// <c>Statistic(ResultScope.Element, "Sink", "input")</c>. <paramref name="sets"/> null = every set.
        /// </summary>
        public ResultTable Statistic(ResultScope scope, string objectName, string metric, IEnumerable<int> sets = null)
        {
            var args = new List<object> { ResultSet.Scope(scope), metric };
            string filter = "";
            if (!string.IsNullOrEmpty(objectName)) { filter += " AND o.name = ?"; args.Add(objectName); }
            filter += SetFilter(sets, args, "o.set_id");
            var rows = db.Query(
                "SELECT o.set_id, s.name AS set_name, c.name AS scenario, r.replication, o.name AS object, st.value " +
                "FROM rs_stats st JOIN rs_objects o ON o.set_id = st.set_id AND o.id = st.object " +
                "JOIN rs_runs r ON r.set_id = o.set_id AND r.id = o.run_id JOIN rs_scenarios c ON c.set_id = r.set_id AND c.id = r.scenario_id " +
                "JOIN rs_sets s ON s.id = o.set_id WHERE o.scope = ? AND st.metric = ?" + filter + " ORDER BY o.set_id, r.id, o.id", args.ToArray());
            var t = new ResultTable("statistic", "set_id", "set", "scenario", "replication", "object", "value");
            foreach (var r in rows) t.Add(Int(r["set_id"]), Str(r["set_name"]), Str(r["scenario"]), Int(r["replication"]), Str(r["object"]), r["value"]);
            return t;
        }

        /// <summary>A named run metric across stored sets: <c>set_id, set, scenario, replication, value</c>.</summary>
        public ResultTable Metric(string metric, IEnumerable<int> sets = null)
        {
            var args = new List<object> { metric };
            string filter = SetFilter(sets, args, "m.set_id");
            var rows = db.Query(
                "SELECT m.set_id, s.name AS set_name, c.name AS scenario, r.replication, m.value FROM rs_run_metrics m " +
                "JOIN rs_runs r ON r.set_id = m.set_id AND r.id = m.run_id JOIN rs_scenarios c ON c.set_id = r.set_id AND c.id = r.scenario_id " +
                "JOIN rs_sets s ON s.id = m.set_id WHERE m.name = ?" + filter + " ORDER BY m.set_id, r.id", args.ToArray());
            var t = new ResultTable("metric", "set_id", "set", "scenario", "replication", "value");
            foreach (var r in rows) t.Add(Int(r["set_id"]), Str(r["set_name"]), Str(r["scenario"]), Int(r["replication"]), r["value"]);
            return t;
        }

        private static string SetFilter(IEnumerable<int> sets, List<object> args, string column)
        {
            if (sets == null) return "";
            var marks = new List<string>();
            foreach (int s in sets) { marks.Add("?"); args.Add(s); }
            return marks.Count == 0 ? " AND 0" : $" AND {column} IN ({string.Join(", ", marks)})";
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private static object Real(double? v) => v == null || double.IsNaN(v.Value) || double.IsInfinity(v.Value) ? null : (object)v.Value;
        private static string Str(object v) => v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        private static int Int(object v) => v == null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
        private static double Dbl(object v) => v == null ? double.NaN : Convert.ToDouble(v, CultureInfo.InvariantCulture);

        private static ResultScope ParseScope(string s)
            => Enum.TryParse(s, true, out ResultScope scope) ? scope : ResultScope.Element;

        private static string TableJson(ResultTable t)
        {
            var rows = new List<object>(t.RowCount);
            foreach (var r in t.Rows) rows.Add(new List<object>(r));
            return MiniJson.Serialize(new Dictionary<string, object> { ["columns"] = new List<object>(t.Columns), ["rows"] = rows });
        }

        private static ResultTable TableFromJson(string name, string json)
        {
            var o = MiniJson.Parse(json) as Dictionary<string, object>;
            var columns = new List<string>();
            if (o != null && o.TryGetValue("columns", out object cs) && cs is List<object> cl) foreach (var c in cl) columns.Add(Convert.ToString(c, CultureInfo.InvariantCulture));
            var t = new ResultTable(name, columns.ToArray());
            if (o != null && o.TryGetValue("rows", out object rs) && rs is List<object> rl)
                foreach (var r in rl) if (r is List<object> cells) t.Add(cells.ToArray());
            return t;
        }
    }
}
