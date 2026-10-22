// Run results: the snapshot of a run (RunResults), its table views, the summary across replications
// (StatSummary, ResultSummary — t-based confidence intervals against scipy), the per-replication metric values, the time
// series (SeriesRecorder) and the result sets with their exports (ResultSet, ResultExport).
// Run: dotnet run  (from StandaloneTests/resultstests)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimuLean;
using SimuLean.Headless;
using SimuLean.Optimization;
using SimuLean.Results;
using SimuLean.Results.Charts;
using SimuLean.Testing;

static class Program
{
    static int passed, failed;

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    static bool Near(double a, double b, double tol = 1e-9) => Math.Abs(a - b) <= tol * Math.Max(1, Math.Abs(b));

    // a minimal PNG reader for the tests: checks every chunk's CRC, inflates IDAT, undoes filter 0
    static byte[] DecodePng(byte[] png, out int width, out int height)
    {
        width = height = 0;
        int at = 8;
        var idat = new MemoryStream();
        uint Be(int i) => (uint)(png[i] << 24 | png[i + 1] << 16 | png[i + 2] << 8 | png[i + 3]);
        while (at < png.Length)
        {
            int len = (int)Be(at);
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            uint crc = 0xFFFFFFFFu;
            for (int i = at + 4; i < at + 8 + len; i++)
            {
                crc ^= png[i];
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
            if ((crc ^ 0xFFFFFFFFu) != Be(at + 8 + len)) throw new InvalidDataException("bad CRC in " + type);
            if (type == "IHDR") { width = (int)Be(at + 8); height = (int)Be(at + 12); }
            if (type == "IDAT") idat.Write(png, at + 8, len);
            at += 12 + len;
        }
        var z = idat.ToArray();
        var raw = new MemoryStream();
        using (var d = new System.IO.Compression.DeflateStream(new MemoryStream(z, 2, z.Length - 6), System.IO.Compression.CompressionMode.Decompress)) d.CopyTo(raw);
        var r = raw.ToArray();
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++) Buffer.BlockCopy(r, y * (width * 4 + 1) + 1, rgba, y * width * 4, width * 4);
        return rgba;
    }

    static RunResults RunBasicLine(int seed, double time = 500)
    {
        var clock = new SimClock(seed);
        var model = new HeadlessModelFactory(clock);
        model.BuildModel(ReferenceModels.BasicLine());
        model.StartModel();
        clock.AdvanceClock(time);
        return RunResults.Capture(model, "basic_line");
    }

    static int Main()
    {
        // ── StatSummary against scipy (stats.t.ppf, numpy std ddof=1) ──────
        var s = StatSummary.Of(new[] { 2.0, 4, 4, 4, 5, 5, 7, 9 });
        Check(s.N == 8 && Near(s.Mean, 5) && Near(s.StdDev, 2.138089935299395) && s.Min == 2 && s.Max == 9, $"mean / std / min / max ({s})");
        Check(Near(s.HalfWidth, 1.7874879182362098, 1e-9) && Near(s.Lower, 5 - s.HalfWidth) && s.HasInterval, $"95 % half-width = t(0.975, 7)·s/√n ({s.HalfWidth:R})");
        Check(Near(StatSummary.TQuantile(0.95, 1), 12.706204736174694, 1e-9) && Near(StatSummary.TQuantile(0.95, 10), 2.2281388519862744, 1e-9)
              && Near(StatSummary.TQuantile(0.95, 30), 2.0422724563012378, 1e-9) && Near(StatSummary.TQuantile(0.90, 4), 2.1318467863266495, 1e-9),
              "t critical values (df 1, 10, 30; 90 % df 4)");
        var one = StatSummary.Of(new[] { 3.5 });
        Check(one.N == 1 && one.Mean == 3.5 && one.StdDev == 0 && !one.HasInterval, "one value: no interval");
        Check(StatSummary.Of(new double[0]).N == 0, "no values");
        var shifted = StatSummary.Of(new[] { 2.0, 4, 4, 4, 5, 5, 7, 9 }.Select(v => v + 1e9).ToArray());
        Check(Near(shifted.StdDev, 2.138089935299395, 1e-6), $"stable far from 0 (std {shifted.StdDev:R})");
        bool threw = false;
        try { StatSummary.Of(new[] { 1.0, 2 }, 1.5); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "a confidence outside (0, 1) fails");
        Console.WriteLine("- StatSummary: mean, std, min, max and the t-based interval against scipy");

        // coverage: 95 % intervals of normal samples (n = 10) contain the mean ~95 % of the time
        var rng = new Random(11);
        int sets = 4000, hits = 0;
        for (int k = 0; k < sets; k++)
        {
            var x = new double[10];
            for (int i = 0; i < x.Length; i++)
                x[i] = 10 + 3 * Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
            var st = StatSummary.Of(x);
            if (st.Lower <= 10 && 10 <= st.Upper) hits++;
        }
        double coverage = (double)hits / sets;
        Check(Math.Abs(coverage - 0.95) < 0.012, $"95 % intervals cover the true mean {coverage:P1} of the time (n = 10)");
        Console.WriteLine("- coverage of the interval on normal samples");

        // ── RunResults: the snapshot and its views ──────────────────────────
        var r = RunBasicLine(5);
        Check(r.Info.Model == "basic_line" && r.Info.Seed == 5 && r.Info.EndTime == 500 && r.Info.ObservedTime == 500, "run info");
        var srv = r.Find(ResultScope.Element, "srv");
        Check(srv != null && srv.Type.Length > 0 && srv.Scope == ResultScope.Element, $"an element by id / name with its type ({srv?.Type})");
        Check(ResultMetrics.Element.All(m => srv.TryGet(m, out _)), "every element statistic present");
        Check(srv.Metrics.Any(m => ResultMetrics.IsState(m.Key)), "state shares present");
        Check(srv[ResultMetrics.StayCount] == srv[ResultMetrics.Output] && srv[ResultMetrics.Output] > 0, "one stay sample per item out");
        double stateSum = srv.Metrics.Where(m => ResultMetrics.IsState(m.Key)).Sum(m => m.Value);
        Check(Near(stateSum, 1, 1e-9), $"state shares add up to 1 ({stateSum:R})");
        var longTable = r.ToLongTable();
        Check(longTable.RowCount == r.Objects.Sum(o => o.Metrics.Count) && longTable.Columns.SequenceEqual(new[] { "scope", "name", "type", "group", "metric", "value" }),
              "long table: one row per statistic");
        var wide = r.ToTable(ResultScope.Element);
        Check(wide.RowCount == r.Of(ResultScope.Element).Count() && wide.Columns.Take(3).SequenceEqual(new[] { "name", "type", "group" })
              && wide.Columns.Skip(3).Take(ResultMetrics.Element.Count).SequenceEqual(ResultMetrics.Element), "wide table: the scope's statistics first, in catalog order");
        Console.WriteLine("- RunResults: run info, element statistics, the long and wide tables");

        // ── ResultSummary across replications ───────────────────────────────
        var runs = Enumerable.Range(1, 6).Select(seed => RunBasicLine(seed)).ToList();
        var sum = ResultSummary.Of(runs);
        Check(sum.Replications == 6 && sum.Objects.Count == runs[0].Objects.Count, "one summary object per object");
        var srvSum = sum.Find(ResultScope.Element, "srv");
        var outs = runs.Select(x => x.Find(ResultScope.Element, "srv")[ResultMetrics.Output]).ToList();
        Check(srvSum.Values(ResultMetrics.Output).SequenceEqual(outs) && Near(srvSum[ResultMetrics.Output].Mean, outs.Average()),
              "values per replication and their mean");
        Check(srvSum[ResultMetrics.Output].HasInterval && srvSum[ResultMetrics.Output].N == 6, "an interval over 6 replications");
        // a state seen in one replication only counts as 0 in the others
        var a = new RunResults(); a.Add(new ResultObject(ResultScope.Element, "x", "x")).Set(ResultMetrics.State("BLOCKED"), 0.4);
        var b = new RunResults(); b.Add(new ResultObject(ResultScope.Element, "x", "x")).Set(ResultMetrics.Input, 3);
        var ab = ResultSummary.Of(new[] { a, b }).Find(ResultScope.Element, "x");
        Check(ab[ResultMetrics.State("BLOCKED")].N == 2 && Near(ab[ResultMetrics.State("BLOCKED")].Mean, 0.2), "a missing state is 0 in that replication");
        Check(ab[ResultMetrics.Input].N == 1, "a fixed statistic is summarised over the runs that have it");
        // the stay time of an M/M/1-like server: the interval of the means contains the service mean (Exponential rate 1.25)
        var longRuns = Enumerable.Range(1, 10).Select(seed => RunBasicLine(seed, 3000)).ToList();
        var stay = ResultSummary.Of(longRuns).Find(ResultScope.Element, "srv")[ResultMetrics.StayAvg];
        Check(stay.Lower <= 0.8 && 0.8 <= stay.Upper, $"server stay time {stay} contains the service mean 0.8");
        Console.WriteLine("- ResultSummary: per-replication values, means, intervals; missing states as 0");

        // ── the objective keeps each replication's metric values ────────────
        var metric = new ModelMetric("done", MetricScope.Element, "sink", ResultMetrics.Input);
        var objective = new SimulationObjective(ReferenceModels.BasicLine(), new ModelParameter[0], new[] { metric }, "done") { Replications = 4, RunTime = 400 };
        var candidate = new Candidate(objective.BuildSpace());
        var e = objective.Evaluate(candidate);
        Check(e.MetricValues["done"].Count == 4 && Near(e.Metrics["done"], e.MetricValues["done"].Average()), "Evaluate: 4 replication values, the metric is their mean");
        var replicated = new List<RunResults>();
        var e2 = objective.Replicate(candidate, replicated);
        Check(replicated.Count == 4 && e2.MetricValues["done"].SequenceEqual(e.MetricValues["done"])
              && replicated.Select(x => x.Find(ResultScope.Element, "sink")[ResultMetrics.Input]).SequenceEqual(e.MetricValues["done"]),
              "Replicate: the same runs, with each replication's statistics");
        Check(replicated.Select(x => x.Info.Seed).SequenceEqual(Enumerable.Range(objective.BaseSeed, 4)), "replication seeds base, +1, +2, +3");
        Console.WriteLine("- SimulationObjective: per-replication metric values; Replicate gives each run's statistics");

        // ── time series (E3): recorded without changing the run ─────────────
        {
            var plain = RunBasicLine(9, 600);
            var clock = new SimClock(9);
            var model = new HeadlessModelFactory(clock);
            model.BuildModel(ReferenceModels.BasicLine());
            model.StartModel();
            var recorder = new SeriesRecorder(model, 50);
            recorder.RunTo(600);
            var withSeries = RunResults.Capture(model, "basic_line");
            recorder.AddTo(withSeries);
            bool same = plain.Objects.Count == withSeries.Objects.Count;
            for (int i = 0; same && i < plain.Objects.Count; i++)
                same = plain.Objects[i].Metrics.SequenceEqual(withSeries.Objects[i].Metrics);
            Check(same, "recording series leaves every statistic of the run unchanged");
            TimeSeries Ser(string name, string metric) => withSeries.Series.FirstOrDefault(x => x.Name == name && x.Metric == metric);
            var input = Ser("srv", ResultMetrics.Input);
            Check(input != null && input.Times.Count == 12 && input.Times[0] == 50 && input.Times.Last() == 600, "an interval every 50 up to the end (12 points)");
            Check(Near(input.Values.Sum(), withSeries.Find(ResultScope.Element, "srv")[ResultMetrics.Input]), "the items per interval add up to the run's input");
            var avg = Ser("srv", ResultMetrics.ContentAvg);
            var srvElement = model.GetAllElements().Values.First(x => x.GetName() == "srv");
            double integral = srvElement.StatsCollector.VarContent.GetIntegral(600);
            Check(Near(avg.Values.Sum() * 50, integral, 1e-9), $"the intervals' average content × length adds up to the content integral ({avg.Values.Sum() * 50:R} vs {integral:R})");
            var stateSums = Enumerable.Range(0, input.Times.Count)
                .Select(i => withSeries.Series.Where(x => x.Name == "srv" && x.Scope == ResultScope.Element && ResultMetrics.IsState(x.Metric)).Sum(x => x.Values[i])).ToList();
            Check(stateSums.All(v => Near(v, 1, 1e-9)), $"the state shares of every interval add up to 1 ({string.Join(" ", stateSums.Select(v => v.ToString("G6")))}; {string.Join(",", withSeries.Series.Where(x => x.Name == "srv").Select(x => x.Metric + ":" + x.Times.Count))})");
            Check(withSeries.Series.Where(x => x.Name == "srv").All(x => x.Times.Count == input.Times.Count), "every series of an object has every interval (states entered late: 0 before)");
            var table = withSeries.ToSeriesTable();
            Check(table.RowCount == withSeries.Series.Sum(x => x.Times.Count), "series table: one row per point");

            // a warm-up: the series start at its end, and the run is unchanged
            var warm = ReferenceModels.All().First(kv => kv.Key == "warmup").Value;
            var c1 = new SimClock(warm.Seed); var m1 = new HeadlessModelFactory(c1); m1.BuildModel(warm); m1.StartModel(); c1.AdvanceClock(warm.MaxSimulationTime);
            var warmPlain = RunResults.Capture(m1);
            var c2 = new SimClock(warm.Seed); var m2 = new HeadlessModelFactory(c2); m2.BuildModel(ReferenceModels.All().First(kv => kv.Key == "warmup").Value); m2.StartModel();
            var rec2 = new SeriesRecorder(m2, 400); rec2.RunTo(warm.MaxSimulationTime);
            var warmSeries = RunResults.Capture(m2); rec2.AddTo(warmSeries);
            var msIn = warmSeries.Series.First(x => x.Name == "ms" && x.Metric == ResultMetrics.Input);
            Check(msIn.Times.First() == 900 && msIn.Times.Last() == 2500 && msIn.Times.Count == 5, $"with a warm-up the series start after it ({string.Join(",", msIn.Times)})");
            Check(Near(msIn.Values.Sum(), warmSeries.Find(ResultScope.Element, "ms")[ResultMetrics.Input])
                  && warmPlain.Find(ResultScope.Element, "ms").Metrics.SequenceEqual(warmSeries.Find(ResultScope.Element, "ms").Metrics),
                  "after the warm-up the series add up to the run's statistics; the run is unchanged");
            // progress slices (RunTo without final): whole intervals, the same series as one call
            var c3 = new SimClock(9); var m3 = new HeadlessModelFactory(c3); m3.BuildModel(ReferenceModels.BasicLine()); m3.StartModel();
            var rec3 = new SeriesRecorder(m3, 50);
            for (int i = 1; i <= 7; i++) rec3.RunTo(600.0 * i / 7, final: i == 7);
            bool sameSeries = rec3.Series.Count == recorder.Series.Count;
            for (int i = 0; sameSeries && i < rec3.Series.Count; i++)
                sameSeries = rec3.Series[i].Times.SequenceEqual(recorder.Series[i].Times)
                             && rec3.Series[i].Values.Zip(recorder.Series[i].Values, (x, y) => Near(x, y, 1e-9)).All(b => b);
            Check(sameSeries, "run in progress slices: the same whole intervals and values as one call");
            // a Play run (G2b): the clock advances frame by frame (uneven steps) through the recorder, then closes it at the end
            var c4 = new SimClock(9); var m4 = new HeadlessModelFactory(c4); m4.BuildModel(ReferenceModels.BasicLine()); m4.StartModel();
            var rec4 = new SeriesRecorder(m4, 50);
            for (double t = 0, step = 0.37; t < 600; step = step * 1.7 % 3.1 + 0.05) { t = Math.Min(600, t + step); rec4.RunTo(t, final: false); }
            rec4.RunTo(600, final: true);
            bool sameFrames = rec4.Series.Count == recorder.Series.Count;
            for (int i = 0; sameFrames && i < rec4.Series.Count; i++)
                sameFrames = rec4.Series[i].Times.SequenceEqual(recorder.Series[i].Times)
                             && rec4.Series[i].Values.Zip(recorder.Series[i].Values, (x, y) => Near(x, y, 1e-9)).All(b => b);
            var framesRun = RunResults.Capture(m4, "basic_line");
            var c5 = new SimClock(9); var m5 = new HeadlessModelFactory(c5); m5.BuildModel(ReferenceModels.BasicLine()); m5.StartModel(); c5.AdvanceClock(600);
            var plainRun = RunResults.Capture(m5, "basic_line");
            Check(sameFrames && framesRun.Find(ResultScope.Element, "srv").Metrics.SequenceEqual(plainRun.Find(ResultScope.Element, "srv").Metrics)
                  && framesRun.Find(ResultScope.Element, "sink")[ResultMetrics.Input] == plainRun.Find(ResultScope.Element, "sink")[ResultMetrics.Input],
                  "a run advanced frame by frame through the recorder: the same series and the same run");
            bool threw2 = false;
            try { new SeriesRecorder(model, 0); } catch (ArgumentOutOfRangeException) { threw2 = true; }
            Check(threw2, "a series interval must be positive");

            // the objective records them for inspected runs only
            var obj2 = new SimulationObjective(ReferenceModels.BasicLine(), new ModelParameter[0], new[] { new ModelMetric("done", MetricScope.Element, "sink", ResultMetrics.Input) }, "done")
                       { Replications = 2, RunTime = 300, SeriesInterval = 100 };
            var reps = new List<RunResults>();
            var ev = obj2.Replicate(new Candidate(obj2.BuildSpace()), reps);
            Check(reps.Count == 2 && reps.All(x => x.Series.Count > 0 && x.Series[0].Times.Count == 3), "Replicate with SeriesInterval: every replication has its series");
            var noSeries = new SimulationObjective(ReferenceModels.BasicLine(), new ModelParameter[0], new[] { new ModelMetric("done", MetricScope.Element, "sink", ResultMetrics.Input) }, "done")
                           { Replications = 2, RunTime = 300 };
            Check(Near(ev.Metrics["done"], noSeries.Evaluate(new Candidate(noSeries.BuildSpace())).Metrics["done"]), "the same metrics with and without series");
        }
        Console.WriteLine("- SeriesRecorder: per-interval series that add up to the run; warm-up; the run unchanged");

        // ── ResultSet and its exports (E3) ──────────────────────────────────
        {
            var set = new ResultSet { Name = "Test set", Source = "experiment", Model = "basic_line", Created = new DateTime(2026, 10, 7, 12, 0, 0) };
            set.MetricNames.Add("done");
            foreach (var (name, seeds) in new[] { ("A", new[] { 1, 2, 3 }), ("B", new[] { 4, 5 }) })
            {
                var sc = new ResultScenario { Name = name };
                sc.Parameters.Add(new KeyValuePair<string, string>("servers", name == "A" ? "1" : "2"));
                set.Scenarios.Add(sc);
                int rep = 0;
                foreach (int seed in seeds)
                {
                    var rr = RunBasicLine(seed, 200);
                    var run = new ResultRun { Scenario = name, Replication = ++rep, Seed = seed, Results = rr };
                    run.Metrics["done"] = rr.Find(ResultScope.Element, "sink")[ResultMetrics.Input];
                    set.Runs.Add(run);
                }
            }
            set.Extra.Add(new SimuLean.Data.ResultTable("history", "iteration", "best_fitness"));
            set.Extra[0].Add(1, 2.5);
            var ms = set.MetricSummaryTable();
            Check(ms.RowCount == 2, "metric summary: one row per scenario and metric");
            var aDone = set.RunsOf("A").Select(r => r.Metrics["done"]).ToList();
            var aStat = StatSummary.Of(aDone);
            Check((int)ms.Rows[0][2] == 3 && Near((double)ms.Rows[0][3], aStat.Mean) && Near((double)ms.Rows[0][7], aStat.Lower), "metric summary = StatSummary of the runs");
            Check(set.ResultsTable().RowCount == set.Runs.Sum(r => r.Results.Objects.Sum(o => o.Metrics.Count)), "results: every statistic of every run");
            var sumTable = set.SummaryTable();
            Check(sumTable.Rows.Count(r => (string)r[0] == "A") == set.SummaryOf("A").Objects.Sum(o => o.Metrics.Count), "summary: every statistic of every object per scenario");
            var meta = set.MetadataTable();
            Check(meta.Rows.Any(r => (string)r[0] == "format" && (string)r[1] == ResultSet.FormatVersion) && meta.Rows.Any(r => (string)r[0] == "runs" && (string)r[1] == "5"),
                  "metadata: format version, counts");
            Check(set.Tables().Select(t => t.Name).SequenceEqual(new[] { "metadata", "scenarios", "runs", "metrics", "metric_summary", "results", "summary", "history" }),
                  "tables in order; empty ones (series) left out; extras last");

            string dir = Path.Combine(Path.GetTempPath(), "simulean_resultstests_" + Guid.NewGuid().ToString("N"));
            try
            {
                var db = new SimuLean.Data.ProjectDatabase(Path.Combine(dir, "results.db"));
                var written = ResultExport.Write(set, dir, ResultFormats.Excel | ResultFormats.Csv | ResultFormats.Json | ResultFormats.Sqlite, db);
                Check(written.Count == 4 && File.Exists(Path.Combine(dir, "Test set.xlsx")) && File.Exists(Path.Combine(dir, "Test set.json")), "every format written");
                string csv = File.ReadAllText(Path.Combine(dir, "Test set", "results.csv"));
                Check(csv.Split('\n').Count(l => l.Trim().Length > 0) == set.ResultsTable().RowCount + 1
                      && csv.StartsWith("run,scenario,replication,scope,name,type,group,metric,value"), "CSV: one file per table, header + rows");
                var json = (Dictionary<string, object>)SimuLean.Data.MiniJson.Parse(File.ReadAllText(Path.Combine(dir, "Test set.json")));
                var tables = (Dictionary<string, object>)json["tables"];
                Check((string)json["format"] == ResultSet.FormatVersion && ((List<object>)tables["results"]).Count == set.ResultsTable().RowCount
                      && tables.ContainsKey("history"), "JSON: every table as rows of objects");
                using (var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(dir, "Test set.xlsx")))
                {
                    string wb = new StreamReader(zip.GetEntry("xl/workbook.xml").Open()).ReadToEnd();
                    Check(new[] { "Summary", "Scenarios", "Elements", "Runs", "Statistics", "Run statistics", "history", "Metadata" }.All(n => wb.Contains($"name=\"{n}\"")),
                          "Excel: readable sheets + the long tables");
                }
                long count = Convert.ToInt64(db.Query("SELECT COUNT(*) AS n FROM results WHERE \"set\" = ?", "Test set")[0]["n"]);
                ResultExport.WriteSqlite(set, db);   // again: replaces the set's rows
                long again = Convert.ToInt64(db.Query("SELECT COUNT(*) AS n FROM results WHERE \"set\" = ?", "Test set")[0]["n"]);
                Check(count == set.ResultsTable().RowCount && again == count, "SQLite: the set's rows, replaced (not duplicated) on re-export");
                Check(db.TableExists("experiment_history"), "SQLite: extra tables named by source");
                db.Dispose();
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        Console.WriteLine("- ResultSet: standard tables, metric / statistic summaries; Excel, CSV, JSON and SQLite exports");

        // ── SQLite: sets whose tables have other columns go into the same tables ──
        {
            string dir = Path.Combine(Path.GetTempPath(), "simulean_sqlite_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = new SimuLean.Data.ProjectDatabase(Path.Combine(dir, "p.db"));
            try
            {
                ResultSet Opt(string name, string[] columns, object[] row)
                {
                    var set = new ResultSet { Name = name, Source = "optimization" };
                    var t = new SimuLean.Data.ResultTable("candidates", columns);
                    t.Add(row);
                    set.Extra.Add(t);
                    return set;
                }
                ResultExport.WriteSqlite(Opt("first", new[] { "x", "fitness" }, new object[] { 1.0, 5.0 }), db);
                Exception error = null;
                try { ResultExport.WriteSqlite(Opt("second", new[] { "y", "z", "fitness" }, new object[] { 2.0, 3.0, 7.0 }), db); }
                catch (Exception ex) { error = ex; }
                Check(error == null, "a second optimisation with other parameters exports into the same table (" + error?.Message + ")");
                if (error == null)
                {
                    var rows = db.Query("SELECT * FROM optimization_candidates ORDER BY \"set\"");
                    Check(rows.Count == 2, $"both sets' rows ({rows.Count})");
                    var second = rows.First(r => (string)r["set"] == "second");
                    var first = rows.First(r => (string)r["set"] == "first");
                    Check(Convert.ToDouble(second["y"]) == 2 && Convert.ToDouble(second["z"]) == 3 && Convert.ToDouble(second["fitness"]) == 7
                          && Convert.ToDouble(first["x"]) == 1 && Convert.ToDouble(first["fitness"]) == 5,
                          "each value under its own column (not by position)");
                }
            }
            finally { db.Dispose(); try { Directory.Delete(dir, true); } catch { } }
            Console.WriteLine("- SQLite: result tables with other columns are appended by column name");
        }

        // ── ResultStore: result sets kept in the project database ──
        {
            ResultSet MakeSet(string name, int firstSeed)
            {
                var set = new ResultSet { Name = name, Source = "optimization", Model = "basic_line", Created = new DateTime(2026, 10, 8, 9, 30, 0), Confidence = 0.9 };
                set.MetricNames.Add("done");
                set.Metadata.Add(new KeyValuePair<string, string>("algorithm", "GA"));
                foreach (var sc in new[] { "A", "B" })
                {
                    var s0 = new ResultScenario { Name = sc };
                    s0.Parameters.Add(new KeyValuePair<string, string>("servers", sc == "A" ? "1" : "2"));
                    s0.Parameters.Add(new KeyValuePair<string, string>("rule", "fifo"));
                    set.Scenarios.Add(s0);
                    for (int rep = 1; rep <= 2; rep++)
                    {
                        int seed = firstSeed + rep + (sc == "B" ? 10 : 0);
                        var rr = RunBasicLine(seed, 150);
                        var ts = new TimeSeries(ResultScope.Element, "sink", ResultMetrics.Input);
                        ts.Times.AddRange(new[] { 0.0, 50, 100 }); ts.Values.AddRange(new[] { 0.0, 3, double.NaN });
                        rr.Series.Add(ts);
                        var run = new ResultRun { Scenario = sc, Replication = rep, Seed = seed, Results = rr, ElapsedSeconds = 0.25 };
                        run.Metrics["done"] = rr.Find(ResultScope.Element, "sink")[ResultMetrics.Input];
                        set.Runs.Add(run);
                    }
                }
                set.Runs.Add(new ResultRun { Scenario = "B", Replication = 3, Seed = 99 });   // a run without results (failed)
                var history = new SimuLean.Data.ResultTable("history", "iteration", "best_fitness", "note");
                history.Add(1, 2.5, "first"); history.Add(2, 3.25, null);
                set.Extra.Add(history);
                for (int i = 0; i < 3; i++)
                {
                    var c = new ResultCandidate { Iteration = i / 2 + 1, Fitness = i == 2 ? double.NaN : 10 + i, Error = i == 2 ? "deadlock" : null };
                    c.Parameters.Add(new KeyValuePair<string, string>("servers", (i + 1).ToString()));
                    if (i < 2) c.Metrics["done"] = 40 + i;
                    set.Candidates.Add(c);
                }
                return set;
            }

            bool SameCell(object a, object b)
            {
                bool Number(object v) => v != null && !(v is string) && !(v is bool) && !(v is DateTime) && v is IConvertible;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (Number(a) && Number(b)) return Convert.ToDouble(a, inv).Equals(Convert.ToDouble(b, inv));
                return Convert.ToString(a ?? "", inv) == Convert.ToString(b ?? "", inv);
            }

            string Diff(ResultSet x, ResultSet y)
            {
                var tx = x.Tables().ToList(); var ty = y.Tables().ToList();
                if (!tx.Select(t => t.Name).SequenceEqual(ty.Select(t => t.Name)))
                    return "tables " + string.Join(",", tx.Select(t => t.Name)) + " / " + string.Join(",", ty.Select(t => t.Name));
                for (int t = 0; t < tx.Count; t++)
                {
                    if (!tx[t].Columns.SequenceEqual(ty[t].Columns)) return tx[t].Name + ": columns";
                    if (tx[t].RowCount != ty[t].RowCount) return $"{tx[t].Name}: {tx[t].RowCount} rows / {ty[t].RowCount}";
                    for (int r = 0; r < tx[t].RowCount; r++)
                        for (int c = 0; c < tx[t].Columns.Count; c++)
                            if (!SameCell(tx[t].Rows[r][c], ty[t].Rows[r][c]))
                                return $"{tx[t].Name}[{r}].{tx[t].Columns[c]}: {tx[t].Rows[r][c]} / {ty[t].Rows[r][c]}";
                }
                return null;
            }

            string dir = Path.Combine(Path.GetTempPath(), "simulean_resultstore_" + Guid.NewGuid().ToString("N"));
            try
            {
                var db = new SimuLean.Data.ProjectDatabase(Path.Combine(dir, "project.db"));
                var store = new ResultStore(db);
                var first = MakeSet("study one", 1);
                var second = MakeSet("study two", 100);
                int id1 = store.Save(first), id2 = store.Save(second, "experiment");
                second.Source = "experiment";   // the kind given to Save is the stored set's source
                Check(id1 > 0 && id2 > id1, $"Save gives increasing ids ({id1}, {id2})");
                var loaded = store.Load(id1);
                string diff = Diff(first, loaded);
                Check(diff == null, "Load gives the same tables back: " + diff);
                Check(loaded.Name == "study one" && loaded.Source == "optimization" && loaded.Model == "basic_line" && loaded.Confidence == 0.9
                      && loaded.Created == first.Created, "Load: name, kind, model, confidence, date");
                Check(loaded.Candidates.Count == 3 && double.IsNaN(loaded.Candidates[2].Fitness) && loaded.Candidates[2].Error == "deadlock"
                      && loaded.Candidates[1].Metrics["done"] == 41 && loaded.Candidates[0].Parameters[0].Value == "1", "Load: the candidates");
                var lts = loaded.Runs[0].Results.Series[0];
                Check(lts.Times.SequenceEqual(new[] { 0.0, 50, 100 }) && lts.Values[1] == 3 && double.IsNaN(lts.Values[2]), "Load: the time series (NaN kept)");
                Check(loaded.Runs[4].Results == null && loaded.Runs[4].Seed == 99, "Load: a run without results stays without");

                var list = store.List();
                Check(list.Count == 2 && list[0].Id == id2 && list[0].Kind == "experiment" && list[1].Runs == 5 && list[1].Scenarios == 2 && list[1].Candidates == 3,
                      "List: newest first, with counts");
                Check(store.List("other model").Count == 0 && store.List("basic_line").Count == 2, "List: filtered by model");

                var stat = store.Statistic(ResultScope.Element, "sink", ResultMetrics.Input);
                Check(stat.RowCount == 8 && stat.Rows.Count(r => Convert.ToInt32(r[0]) == id2) == 4, $"Statistic across sets: one row per recorded run ({stat.RowCount})");
                Check(Near(Convert.ToDouble(stat.Rows[0][5]), first.Runs[0].Results.Find(ResultScope.Element, "sink")[ResultMetrics.Input]), "Statistic: the stored value");
                Check(store.Statistic(ResultScope.Element, "sink", ResultMetrics.Input, new[] { id1 }).RowCount == 4
                      && store.Statistic(ResultScope.Element, "sink", ResultMetrics.Input, new int[0]).RowCount == 0, "Statistic: limited to some sets");
                var metricRows = store.Metric("done");
                Check(metricRows.RowCount == 8 && (string)metricRows.Rows[0][2] == "A", "Metric across sets");
                Check(first.StoreId == id1 && loaded.StoreId == id1 && new ResultSet().StoreId == 0, "StoreId: set by Save and Load");

                // comparing sets (G1b): one set whose scenarios are every set's
                var cmp = ResultSet.Compare(new[] { first, second });
                Check(cmp.Source == "comparison" && cmp.Scenarios.Count == 4 && cmp.Runs.Count == 10 && cmp.Scenarios[0].Name == "study one · A"
                      && cmp.Scenarios[2].Parameters.Count == 2 && cmp.Runs[9].Scenario == "study two · B", "Compare: every set's scenarios and runs, by set");
                Check(cmp.MetricSummaryTable().RowCount == 4 && cmp.SummaryOf("study two · A") != null && cmp.StoreId == 0, "Compare: the summaries per set and scenario");
                Check(ResultSet.Compare(new[] { first }).Scenarios[0].Name == "A", "Compare of one set keeps its names");

                Check(store.Rename(id1, "renamed") && store.Load(id1).Name == "renamed" && !store.Rename(9999, "x"), "Rename");
                Check(store.Delete(id1) && !store.Delete(id1), "Delete (once)");
                bool gone = false;
                try { store.Load(id1); } catch (KeyNotFoundException) { gone = true; }
                long leftovers = 0;
                foreach (var t in new[] { "rs_runs", "rs_objects", "rs_stats", "rs_series", "rs_candidates", "rs_candidate_values", "rs_extra", "rs_parameters" })
                    leftovers += Convert.ToInt64(db.Query($"SELECT COUNT(*) AS n FROM {t} WHERE set_id = ?", id1)[0]["n"]);
                Check(gone && leftovers == 0 && store.List().Count == 1 && Diff(second, store.Load(id2)) == null, "Delete removes every row of the set, and only those");

                Check(Convert.ToString(db.Query("SELECT value FROM rs_meta WHERE key = 'schema_version'")[0]["value"]) == ResultStore.SchemaVersion.ToString(),
                      "schema version recorded");
                db.Execute("UPDATE rs_meta SET value = '99' WHERE key = 'schema_version'");
                bool newer = false;
                try { new ResultStore(db); } catch (InvalidOperationException) { newer = true; }
                Check(newer, "a newer schema is refused");
                db.Execute("UPDATE rs_meta SET value = ? WHERE key = 'schema_version'", ResultStore.SchemaVersion.ToString());

                // Sets saved from several threads keep their own rows (the insert and its id were read
                // under separate locks: a set could take another's id)
                var parallelStore = new ResultStore(db);
                var savedIds = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
                System.Threading.Tasks.Parallel.For(0, 24, k =>
                {
                    var p = new ResultSet { Name = "parallel " + k, Source = "experiment" };
                    p.Metadata.Add(new KeyValuePair<string, string>("who", "parallel " + k));
                    savedIds[p.Name] = parallelStore.Save(p);
                });
                bool own = savedIds.Count == 24 && new HashSet<int>(savedIds.Values).Count == 24;
                foreach (var kv in savedIds)
                {
                    var back = parallelStore.Load(kv.Value);
                    own &= back.Name == kv.Key && back.Metadata.Count(m => m.Key == "who") == 1
                           && back.Metadata.First(m => m.Key == "who").Value == kv.Key;
                }
                Check(own, "sets saved in parallel get distinct ids and keep their own metadata");
                db.Dispose();
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        Console.WriteLine("- ResultStore: save / load round trip, list, rename, delete, statistics across sets, schema version");

        // ── Charts: data from the long tables, the raster, PNG ──
        {
            var study = new Study(ReferenceModels.MultiServer3()) { Name = "chart study", Threads = 2, SeriesInterval = 100 }
                .Factor("ms.capacity", 1, 2, 3)
                .Metric("out", "sink.input").Metric("util", "ms.utilization")
                .WithReplications(4).WithSeed(3).WithRunTime(600);
            var set = study.Run();
            Check(set.Runs.Count == 12, "chart study: 3 scenarios × 4 replications");

            // bar: one category per scenario, the mean and the interval of the runs' metric
            var bar = ChartBuilder.Build(new ChartQuery { Kind = ChartKind.Bar, Y = new ChartValue(ChartSource.Metric, "out") }, set);
            var firstRuns = set.RunsOf(set.Scenarios[0].Name).Select(r => r.Metrics["out"]).ToList();
            var st = StatSummary.Of(firstRuns);
            Check(!bar.IsEmpty && bar.Categories.Count == 3 && bar.Series.Count == 1 && Near(bar.Series[0].Values[0], st.Mean)
                  && Near(bar.Series[0].Low[0], st.Lower) && Near(bar.Series[0].High[0], st.Upper), $"bar: mean and CI per scenario ({bar.Message})");

            // a statistic of several objects grouped by object: one series per scenario
            var byObject = ChartBuilder.Build(new ChartQuery { Kind = ChartKind.Bar, GroupBy = ChartGroup.Object,
                Y = new ChartValue(ChartSource.Statistic, ResultMetrics.Input, "element", "q;ms;sink") }, set);
            Check(byObject.Categories.SequenceEqual(new[] { "q", "ms", "sink" }) && byObject.Series.Count == 3, "bar by object: categories = objects, series = scenarios");
            var filtered = ChartBuilder.Build(new ChartQuery { Kind = ChartKind.Bar, Scenarios = set.Scenarios[2].Name, Y = new ChartValue(ChartSource.Statistic, ResultMetrics.Input, "element", "si*") }, set);
            Check(filtered.Categories.Count == 1 && filtered.Series.Count == 1 && filtered.Series[0].Name == ResultMetrics.Input, "scenario filter and object pattern");

            // box: quartiles as numpy (linear)
            var quart = ChartBuilder.Box(new[] { 7.0, 1, 3, 9, 5, 2 });
            Check(quart.N == 6 && quart.Min == 1 && quart.Max == 9 && Near(quart.Q1, 2.25) && Near(quart.Median, 4) && Near(quart.Q3, 6.5), $"box quartiles = numpy ({quart.Q1}, {quart.Median}, {quart.Q3})");
            var box = ChartBuilder.Build(new ChartQuery { Kind = ChartKind.Box, Y = new ChartValue(ChartSource.Metric, "out") }, set);
            Check(box.Series[0].Boxes.All(x => x.N == 4), "box: one box per scenario over its 4 replications");

            // histogram: every value in a bin
            var hist = ChartBuilder.Build(new ChartQuery { Kind = ChartKind.Histogram, Bins = 5, Y = new ChartValue(ChartSource.Metric, "util") }, set);
            Check(hist.Series.Count == 3 && hist.Series.Sum(s => s.Points.Sum(p => p.y)) == 12 && hist.Series[0].Points.Count == 5 && hist.BinWidth > 0,
                  "histogram: 12 values in 5 bins, one series per scenario");

            // scatter: a parameter against a metric, one point per run
            var scatter = ChartBuilder.Build(new ChartQuery { Kind = ChartKind.Scatter, X = new ChartValue(ChartSource.Parameter, "ms.capacity"),
                                                             Y = new ChartValue(ChartSource.Metric, "util") }, set);
            Check(!scatter.IsEmpty && scatter.Series.Sum(s => s.Points.Count) == 12 && scatter.Series[1].Points.All(p => p.x == 2), $"scatter: parameter as X ({scatter.Message})");

            // line: the recorded series, one line per run
            var line = ChartBuilder.Build(new ChartQuery { Kind = ChartKind.Line, Scenarios = set.Scenarios[0].Name,
                                                          Y = new ChartValue(ChartSource.Statistic, ResultMetrics.ContentAvg, "element", "q") }, set);
            Check(!line.IsEmpty && line.Series.Count == 4 && line.Series.All(s => s.Points.Count == 6), $"line: 4 runs of 6 intervals ({line.Message})");

            // states: shares stacked per object, summing to 1
            var states = ChartBuilder.Build(new ChartQuery { Kind = ChartKind.States, Scenarios = set.Scenarios[0].Name, Y = new ChartValue(ChartSource.Statistic, "", "element", "ms") }, set);
            double total = states.Series.Sum(s => s.Values[0]);
            Check(!states.IsEmpty && states.Categories.SequenceEqual(new[] { "ms" }) && Near(total, 1, 1e-6) && states.Series[0].Name == "PROCESSING"
                  && states.Series[0].Color == ChartPalette.StateColor("PROCESSING"), $"states: shares sum to 1 ({total}), known states first, state colours");

            // names not found: an empty chart that lists what there is
            var missing = ChartBuilder.Build(new ChartQuery { Y = new ChartValue(ChartSource.Metric, "nope") }, set);
            Check(missing.IsEmpty && missing.Message.Contains("out") && missing.Message.Contains("util"), "an unknown metric: the message lists the metrics");
            var noStat = ChartBuilder.Build(new ChartQuery { Y = new ChartValue(ChartSource.Statistic, "nope", "element", "ms") }, set);
            Check(noStat.IsEmpty && noStat.Message.Contains(ResultMetrics.Input), "an unknown statistic: the message lists the statistics");

            // the same chart from the tables of a .json export (cells as parsed JSON)
            var tables = new Dictionary<string, SimuLean.Data.ResultTable>();
            var json = (Dictionary<string, object>)SimuLean.Data.MiniJson.Parse(ResultExport.ToJson(set));
            foreach (var kv in (Dictionary<string, object>)json["tables"])
            {
                var rows = (List<object>)kv.Value;
                var cols = ((Dictionary<string, object>)rows[0]).Keys.ToArray();
                var t = new SimuLean.Data.ResultTable(kv.Key, cols);
                foreach (Dictionary<string, object> row in rows) t.Add(cols.Select(c => row.TryGetValue(c, out object v) ? v : null).ToArray());
                tables[kv.Key] = t;
            }
            var fromJson = ChartBuilder.Build(new ChartQuery { Kind = ChartKind.Bar, Y = new ChartValue(ChartSource.Metric, "out") }, tables);
            Check(!fromJson.IsEmpty && Near(fromJson.Series[0].Values[0], bar.Series[0].Values[0]), "a .json export gives the same chart");

            // the raster: ticks, sizes, the PNG (re-read: signature, size, pixels)
            var ticks = ChartRenderer.NiceTicks(0.3, 9.7, 5);
            Check(ticks.First() == 0 && ticks.Last() == 10 && Near(ticks[1] - ticks[0], 2), $"nice ticks ({string.Join(" ", ticks)})");
            Check(ChartRenderer.Format(0.25, new[] { 0.0, 0.25, 0.5 }) == "0.25" && ChartRenderer.Format(2000, new[] { 0.0, 1000, 2000 }) == "2000", "tick labels");
            var img = ChartRenderer.Render(bar, 640, 400);
            Check(img.Width == 640 && img.Height == 400 && Enumerable.Range(0, 640).Any(x => img.Pixel(x, 300) == ChartPalette.SeriesColor(0)), "a bar is drawn in the series colour");
            byte[] png = img.ToPng();
            var pixels = DecodePng(png, out int pw, out int ph);
            Check(png.Take(8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) && pw == 640 && ph == 400 && pixels.SequenceEqual(img.Rgba),
                  "PNG: signature, size, the same pixels (CRC and zlib checked)");
            var empty = ChartRenderer.Render(missing, 300, 200);
            Check(empty.Width == 300, "an empty chart renders its message");

            // live kinds (the element inspector, dashboards): stacked columns to 100 %, area + line; the plot alone
            var stacked = new ChartData { Kind = ChartKind.Stacked, Normalise = true, Title = "State split per day (%)", Unit = "%", XStart = "01/01", XCenter = "3 days", XEnd = "03/01" };
            stacked.Series.Add(new ChartSeries { Name = "Processing", Color = ChartPalette.StateColor("PROCESSING"), Values = new[] { 3.0, 6, 0 } });
            stacked.Series.Add(new ChartSeries { Name = "Idle", Color = ChartPalette.StateColor("IDLE"), Values = new[] { 1.0, 6, 5 } });
            var plot = ChartRenderer.RenderPlot(stacked, 90, 40, ChartTheme.Panel, out double top);
            Check(top == 100 && plot.Pixel(15, 39) == ChartPalette.StateColor("PROCESSING") && plot.Pixel(15, 2) == ChartPalette.StateColor("IDLE")
                  && plot.Pixel(15, 12) == ChartPalette.StateColor("PROCESSING") && plot.Pixel(75, 39) == ChartPalette.StateColor("IDLE"),
                  "stacked to 100 %: column 1 = 75 % processing under 25 % idle, column 3 all idle");
            var area = new ChartData { Kind = ChartKind.Area, Title = "Content: mean and max", Unit = "items" };
            area.Series.Add(new ChartSeries { Name = "mean", Values = new[] { 1.0, 2, 3, 2 } });
            area.Series.Add(new ChartSeries { Name = "max", Values = new[] { 2.0, 4, 5, 3 } });
            ChartRenderer.RenderPlot(area, 80, 40, ChartTheme.Panel, out double areaTop);
            Check(areaTop == ChartRenderer.NiceMax(5) && ChartRenderer.NiceMax(5) == 6 && ChartRenderer.NiceMax(0.9) == 1 && ChartRenderer.NiceMax(130) == 150, $"area: the axis top is a round number ({areaTop})");
            bool refused = false;
            try { ChartRenderer.RenderPlot(bar, 50, 50, null, out _); } catch (ArgumentException) { refused = true; }
            Check(refused, "RenderPlot draws only stacked / area / states");
            var labelled = ChartBuilder.Build(new ChartQuery { Kind = ChartKind.Bar, ValueLabels = true, Y = new ChartValue(ChartSource.Metric, "out") }, set);
            Check(labelled.ValueLabels && !ChartRenderer.Render(labelled, 640, 400).Rgba.SequenceEqual(img.Rgba), "values written above the bars");
            Check(ChartBuilder.Build(new ChartQuery { Kind = ChartKind.Area, Y = new ChartValue(ChartSource.Metric, "out") }, set).IsEmpty, "a live kind is not built from results");

            // the chart font (G3a): Inter read and rasterised by the engine
            var font = ChartFonts.Regular;
            Check(font.UnitsPerEm > 0 && font.GlyphIndex('A') > 0 && font.GlyphIndex('é') > 0 && font.GlyphIndex('±') > 0 && font.GlyphIndex('…') > 0
                  && font.GlyphIndex(0x10FFFF) == 0, "font: Unicode lookup (letters, accents, ±, …; missing → 0)");
            Check(font.Measure("WWW", 12) > font.Measure("iii", 12) && Near(font.Measure("ab", 24), 2 * font.Measure("ab", 12), 1e-9)
                  && font.Measure("", 12) == 0 && font.AscentPx(10) > 5 && font.DescentPx(10) > 0, "font: widths scale with the size, metrics");
            int covered = 0, partial = 0;
            font.Draw("Hé±", 0, 20, 16, (x, y, c) => { covered++; if (c < 0.99f) partial++; });
            int bold = 0;
            ChartFonts.SemiBold.Draw("H", 0, 20, 16, (x, y, c) => bold++);
            int regularH = 0;
            font.Draw("H", 0, 20, 16, (x, y, c) => regularH++);
            Check(covered > 100 && partial > 10 && bold > regularH, $"font: anti-aliased glyphs ({covered} pixels, {partial} partial); semibold is heavier");
            var glyphImage = new RasterCanvas(60, 30, 0xFFFFFF, 1);
            glyphImage.Text("é", 5, 22, 20, 0x000000);
            var ink = glyphImage.ToImage();
            int dark = 0;
            for (int yy = 0; yy < 30; yy++) for (int xx = 0; xx < 60; xx++) if (ink.Pixel(xx, yy) != 0xFFFFFF) dark++;
            Check(dark > 30, "font: an accented (composite) glyph draws");

            // SVG (G3b): the same layout as vector shapes and real text
            string svg = ChartRenderer.ToSvg(labelled, 640, 400);
            var xml = System.Xml.Linq.XDocument.Parse(svg);
            System.Xml.Linq.XNamespace ns = "http://www.w3.org/2000/svg";
            var texts = xml.Descendants(ns + "text").Select(t => t.Value).ToList();
            Check(xml.Root.Name == ns + "svg" && (string)xml.Root.Attribute("viewBox") == "0 0 640 400" && texts.Contains(labelled.Title)
                  && texts.Contains(labelled.Categories[0]) && xml.Descendants(ns + "path").Count() >= 3, $"SVG: valid, title / categories as text, bars as paths ({texts.Count} texts)");
            var tricky = new ChartData { Kind = ChartKind.Bar, Title = "a < b & \"c\"", Categories = { "x" } };
            tricky.Series.Add(new ChartSeries { Name = "s", Values = new[] { 1.0 }, Low = new[] { double.NaN }, High = new[] { double.NaN } });
            Check(System.Xml.Linq.XDocument.Parse(ChartRenderer.ToSvg(tricky)).Descendants(ns + "text").Any(t => t.Value == "a < b & \"c\""), "SVG: text escaped");
            string svgDir = Path.Combine(Path.GetTempPath(), "simulean_svg_" + Guid.NewGuid().ToString("N"));
            ChartRenderer.Save(labelled, Path.Combine(svgDir, "c.svg"));
            ChartRenderer.Save(labelled, Path.Combine(svgDir, "c.png"), 320, 200);
            var savedPng = DecodePng(File.ReadAllBytes(Path.Combine(svgDir, "c.png")), out int sw, out int sh);
            Check(File.ReadAllText(Path.Combine(svgDir, "c.svg")).StartsWith("<svg") && sw == 640 && sh == 400, "Save: .svg as vector, .png at 2 pixels per point");
            try { Directory.Delete(svgDir, true); } catch { }

            string outDir = Environment.GetEnvironmentVariable("SIMULEAN_CHART_DIR");
            if (!string.IsNullOrEmpty(outDir))   // a visual check: every kind as PNG
            {
                var all = new (string name, ChartData data)[] { ("bar", bar), ("bar_objects", byObject), ("box", box), ("hist", hist), ("scatter", scatter), ("line", line),
                    ("states", ChartBuilder.Build(new ChartQuery { Kind = ChartKind.States, Y = new ChartValue(ChartSource.Statistic, "", "element", "") }, set)), ("missing", missing), ("stacked", stacked), ("area", area), ("labelled", labelled) };
                foreach (var (name, d) in all)
                {
                    ChartRenderer.Render(d, 640, 400).SavePng(Path.Combine(outDir, name + ".png"));
                    ChartRenderer.Render(d, 360, 220, ChartTheme.Dark).SavePng(Path.Combine(outDir, name + "_small.png"));
                    File.WriteAllText(Path.Combine(outDir, name + ".svg"), ChartRenderer.ToSvg(d));
                    if (d.Kind == ChartKind.Stacked || d.Kind == ChartKind.Area) ChartRenderer.RenderPlot(d, 300, 150, ChartTheme.Panel, out _, 1).SavePng(Path.Combine(outDir, name + "_plot.png"));
                }
            }
        }
        Console.WriteLine("- Charts: bar / box / histogram / scatter / line / states from the long tables (also .json); messages; raster and PNG");

        // ── Timeline and Gantt ──────────────────────────────────
        {
            // off by default: the results unchanged
            var plain = new Study(ReferenceModels.MultiServer3()) { Threads = 1 }.WithReplications(1).WithSeed(5).WithRunTime(300);
            plain.RecordStatistics = true;
            var plainSet = plain.Run();
            Check(plainSet.Runs[0].Results.Timeline.Count == 0 && !plainSet.Tables().Any(t => t.Name == "timeline"), "timeline: off by default, no table");

            var study = new Study(ReferenceModels.MultiServer3()) { Name = "gantt study", Threads = 2, RecordTimeline = true }
                .Factor("ms.capacity", 1, 2).WithReplications(2).WithSeed(5).WithRunTime(300);
            var set = study.Run();
            var run = set.Runs[0].Results;
            Check(run.Timeline.Count > 10 && run.Timeline.Any(e => e.Scope == ResultScope.Element && e.Name == "ms"), $"timeline: recorded ({run.Timeline.Count} intervals)");
            // the intervals of an object tile the run and add up to its time per state (one source: the state tracker)
            var ms = run.Timeline.Where(e => e.Scope == ResultScope.Element && e.Name == "ms").ToList();
            bool tiled = ms.First().Start == 0 && Near(ms.Last().End, 300);
            for (int i = 1; i < ms.Count; i++) tiled &= Near(ms[i].Start, ms[i - 1].End) && ms[i].State != ms[i - 1].State;
            double processing = ms.Where(e => e.State == "PROCESSING").Sum(e => e.End - e.Start) / 300;
            Check(tiled && Near(processing, run.Find(ResultScope.Element, "ms")[ResultMetrics.State("PROCESSING")], 1e-9),
                  $"timeline: contiguous intervals, the PROCESSING share = the statistic ({processing:F4} vs {run.Find(ResultScope.Element, "ms")[ResultMetrics.State("PROCESSING")]:F4}; tiled {tiled}, first {ms.First().Start}, last {ms.Last().End}, n {ms.Count})");
            var table = set.TimelineTable();
            Check(table.RowCount == set.Runs.Sum(r => r.Results.Timeline.Count) && table.Columns.SequenceEqual(new[] { "run", "scenario", "replication", "scope", "name", "state", "start", "end" }),
                  "timeline table: every interval of every run");

            // the job timeline (G3d): every stay of an item in an element, from the link layer's entries / exits
            var jobsMs = run.Jobs.Where(j => j.Element == "ms").ToList();
            Check(run.Jobs.Count > 0 && jobsMs.Count == (int)run.Find(ResultScope.Element, "ms")[ResultMetrics.Input] && run.Jobs.All(j => j.Element != "sink"),
                  $"jobs: one stay per entry into ms ({jobsMs.Count}), none in the sink");
            bool contiguous = true;
            string bad = "";
            foreach (var g2 in run.Jobs.GroupBy(j => j.Item))
            {
                var stays = g2.OrderBy(j => j.Start).ToList();
                for (int i = 1; i < stays.Count; i++)
                    if (stays[i].Start < stays[i - 1].End - 1e-9 && contiguous) { contiguous = false; bad = string.Join(" | ", stays.Take(5).Select(x => $"{x.Item} {x.Element} {x.Start:F2}-{x.End:F2}")); }
            }
            Check(contiguous, "jobs: an item's stays follow one another (no overlap) " + bad);
            Check(set.JobsTable().RowCount == set.Runs.Sum(r => r.Results.Jobs.Count) && plainSet.Runs[0].Results.Jobs.Count == 0, "jobs table; none without Record timeline");
            var jg = ChartBuilder.Build(new ChartQuery { Kind = ChartKind.JobGantt, Y = new ChartValue(ChartSource.Statistic, "", "element", "") }, set);
            var flow = jg.Series.Select(x => x.Name).ToList();
            Check(!jg.IsEmpty && flow.Contains("ms") && (!flow.Contains("q") || flow.IndexOf("q") < flow.IndexOf("ms")) && jg.Categories.Count == run.Jobs.Select(j => j.Item).Distinct().Count()
                  && jg.Subtitle.Contains("items"), $"job Gantt: a row per item, a colour per element in flow order ({jg.Message}; {string.Join(",", flow)})");
            Check(ChartBuilder.Build(new ChartQuery { Kind = ChartKind.JobGantt }, plainSet).Message.Contains("Record timeline"), "job Gantt without a timeline: the message");

            // the results store keeps it (schema 2); an older database is upgraded on opening
            string dir = Path.Combine(Path.GetTempPath(), "simulean_timeline_" + Guid.NewGuid().ToString("N"));
            try
            {
                var db = new SimuLean.Data.ProjectDatabase(Path.Combine(dir, "old.db"));
                db.Execute("CREATE TABLE rs_meta (key TEXT PRIMARY KEY, value TEXT)");
                db.Execute("INSERT INTO rs_meta VALUES ('schema_version', '1')");
                var store = new ResultStore(db);
                Check(db.TableExists("rs_timeline") && Convert.ToString(db.Query("SELECT value FROM rs_meta WHERE key = 'schema_version'")[0]["value"]) == "2",
                      "store: a schema-1 database gets the timeline table (version 2)");
                int id = store.Save(set);
                var back = store.Load(id);
                Check(back.TimelineTable().RowCount == table.RowCount && back.Runs[1].Results.Timeline.Last().End == set.Runs[1].Results.Timeline.Last().End,
                      "store: the timeline round-trips");
                Check(back.JobsTable().RowCount == set.JobsTable().RowCount, "store: the jobs round-trip");
                store.Delete(id);
                Check(Convert.ToInt64(db.Query("SELECT COUNT(*) AS n FROM rs_timeline")[0]["n"]) == 0, "store: deleting a set deletes its timeline");
                db.Dispose();
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            // the Gantt: one run, a row per object, its intervals in the states' colours
            var g = ChartBuilder.Build(new ChartQuery { Kind = ChartKind.Gantt, Scenarios = set.Scenarios[1].Name, Replication = 2,
                                                         Y = new ChartValue(ChartSource.Statistic, "", "element", "") }, set);
            var r2 = set.Runs.First(r => r.Scenario == set.Scenarios[1].Name && r.Replication == 2).Results;
            Check(!g.IsEmpty && g.Categories.Count == r2.Timeline.Where(e => e.Scope == ResultScope.Element).Select(e => e.Name).Distinct().Count()
                  && g.Segments.Count == r2.Timeline.Count(e => e.Scope == ResultScope.Element) && g.Subtitle.Contains("replication 2")
                  && g.Series.All(sr => sr.Color == ChartPalette.StateColor(sr.Name)) && g.TimeFrom == 0 && Near(g.TimeTo, 300), $"Gantt: rows, segments, the run chosen ({g.Message})");
            var windowed = ChartBuilder.Build(new ChartQuery { Kind = ChartKind.Gantt, From = 100, To = 150, Y = new ChartValue(ChartSource.Statistic, "", "element", "ms") }, set);
            Check(windowed.Categories.SequenceEqual(new[] { "ms" }) && windowed.Segments.All(sg => sg.Start >= 100 && sg.End <= 150) && windowed.TimeFrom == 100 && windowed.TimeTo == 150,
                  "Gantt: object filter and time window (intervals clipped)");
            Check(ChartBuilder.Build(new ChartQuery { Kind = ChartKind.Gantt, Y = new ChartValue(ChartSource.Statistic, "", "element", "") }, plainSet).Message.Contains("Record timeline"),
                  "Gantt without a timeline: the message says how to record one");

            // a dense timeline (more intervals than pixels): each pixel column takes its dominant state
            var dense = new ChartData { Kind = ChartKind.Gantt, Title = "dense", TimeFrom = 0, TimeTo = 10000, Categories = { "m" } };
            dense.Series.Add(new ChartSeries { Name = "PROCESSING", Color = ChartPalette.StateColor("PROCESSING") });
            dense.Series.Add(new ChartSeries { Name = "IDLE", Color = ChartPalette.StateColor("IDLE") });
            for (int i = 0; i < 10000; i++) dense.Segments.Add(new GanttSegment(0, i < 5000 ? (i % 10 == 0 ? 1 : 0) : (i % 10 == 0 ? 0 : 1), i, i + 1));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var denseImg = ChartRenderer.Render(dense, 640, 200);
            var denseSvg = ChartRenderer.ToSvg(dense, 640, 200);
            int rects = System.Xml.Linq.XDocument.Parse(denseSvg).Descendants((System.Xml.Linq.XNamespace)"http://www.w3.org/2000/svg" + "rect").Count();
            Check(sw.Elapsed.TotalSeconds < 5 && rects < 200, $"Gantt: 10 000 intervals merged per pixel ({rects} SVG rects, {sw.Elapsed.TotalSeconds:F2} s)");
            int processingLeft = 0, idleRight = 0;
            for (int yy = 0; yy < 200; yy++)
            {
                int pl = 0, ir = 0;
                for (int x = 0; x < 640; x++) { int c = denseImg.Pixel(x, yy); if (c == ChartPalette.StateColor("PROCESSING") && x < 320) pl++; if (c == ChartPalette.StateColor("IDLE") && x > 320) ir++; }
                if (pl + ir > processingLeft + idleRight) { processingLeft = pl; idleRight = ir; }
            }
            Check(processingLeft > 100 && idleRight > 100, "Gantt: the dominant state shows (processing first half, idle second)");

            string outDir2 = Environment.GetEnvironmentVariable("SIMULEAN_CHART_DIR");
            if (!string.IsNullOrEmpty(outDir2))
            {
                ChartRenderer.Render(g, 800, 360, null, 1.25).SavePng(Path.Combine(outDir2, "gantt.png"));
                ChartRenderer.Render(dense, 640, 200).SavePng(Path.Combine(outDir2, "gantt_dense.png"));
                ChartRenderer.Render(ChartBuilder.Build(new ChartQuery { Kind = ChartKind.JobGantt, Y = new ChartValue(ChartSource.Statistic, "", "element", ""), To = 60 }, set), 800, 420, null, 1.25)
                    .SavePng(Path.Combine(outDir2, "jobs.png"));
                File.WriteAllText(Path.Combine(outDir2, "gantt.svg"), ChartRenderer.ToSvg(g, 640, 300));
            }
        }
        Console.WriteLine("- Timeline and Gantt: opt-in state intervals = the statistics, table, store (schema 2), Gantt rows / window / dense merge");

        // ── Study: a failing run is recorded, the others are kept ──
        {
            var study = new Study(ReferenceModels.MultiServer3()) { Name = "failing scenario", Threads = 2 }
                .Factor("ms.capacity", 2, -1)                                 // -1 cannot be built
                .Metric("out", "sink.input")
                .WithReplications(2).WithSeed(3).WithRunTime(300);
            ResultSet set = null;
            try { set = study.Run(); }
            catch (Exception ex) { Check(false, "a failing run aborted the study: " + ex.GetType().Name + ": " + ex.Message); }
            if (set != null)
            {
                var good = set.Runs.Where(r => r.Error == null).ToList();
                var bad = set.Runs.Where(r => r.Error != null).ToList();
                Check(set.Runs.Count == 4 && good.Count == 2 && bad.Count == 2, $"4 runs: 2 good, 2 failed ({good.Count}, {bad.Count})");
                Check(good.All(r => r.Metrics.ContainsKey("out")) && bad.All(r => r.Metrics.Count == 0), "the good runs keep their metrics");
                Check(set.Metadata.Any(kv => kv.Key == "failed_runs" && kv.Value == "2"), "the set says how many runs failed");
            }
            Console.WriteLine("- Study: a failing run is recorded with its error; the completed runs are kept");
        }

        // ── Study: two scenarios with the same name are refused ──
        {
            var dup = new Study(ReferenceModels.MultiServer3()) { Threads = 1 }
                .Factor("ms.capacity", 1, 2).Metric("out", "sink.input").WithRunTime(100);
            dup.Scenario("Scenario 3", 1, "1").Scenario("Scenario 3", 1, "2");
            string problem = dup.Validate();
            Check(problem != null && problem.Contains("Scenario 3"), $"duplicate scenario names: invalid ({problem ?? "valid"})");
            bool dupThrew = false;
            try { dup.Run(); } catch (InvalidOperationException) { dupThrew = true; }
            Check(dupThrew, "and not run");
            Console.WriteLine("- Study: scenario names are unique");
        }

        // ── Study: the scriptable experiment ─────────────────────
        {
            var study = new Study(ReferenceModels.MultiServer3()) { Name = "ms study", Threads = 2 }
                .Factor("ms.capacity", 1, 3)
                .Metric("makespan", "model.makespan").Metric("out", "sink.input").Metric("util", "ms.utilization")
                .WithReplications(3).WithSeed(11).WithRunTime(500);
            Check(study.Validate() == null, $"valid ({study.Validate()})");
            var rs = study.Run();
            Check(rs.Scenarios.Count == 2 && rs.Runs.Count == 6 && rs.Scenarios[0].Name == "ms.capacity=1", $"2 scenarios × 3 replications ({string.Join(", ", rs.Scenarios.Select(x => x.Name))})");
            double Mean(string sc, string m) => rs.RunsOf(sc).Average(r => r.Metrics[m]);
            Check(Mean("ms.capacity=3", "out") > Mean("ms.capacity=1", "out") * 0.99 && Mean("ms.capacity=1", "util") > Mean("ms.capacity=3", "util"),
                  "more servers: lower utilisation, at least the throughput");
            Check(rs.RunsOf("ms.capacity=1").Select(r => r.Seed).SequenceEqual(new[] { 11, 12, 13 }), "replication seeds base, +1, +2 in every scenario");
            Check(rs.Runs.All(r => r.Results != null && r.Metrics["makespan"] <= 500), "every run's statistics and its makespan");
            // the same run done by hand gives the same numbers (the study is the objective's run)
            var cfg = ReferenceModels.MultiServer3();
            cfg.Elements.Find(e => e.Id == "ms").Parameters["capacity"] = 3;
            var clock = new SimClock(12); var m = new HeadlessModelFactory(clock); m.BuildModel(cfg); m.StartModel(); clock.AdvanceClock(500);
            Check(rs.RunsOf("ms.capacity=3")[1].Metrics["out"] == RunResults.Capture(m).Find(ResultScope.Element, "sink")[ResultMetrics.Input],
                  "a study run = the model with the scenario's values and seed");

            // members with a label: painters (Painter and AllRounder carry paint=1)
            var crew = new Study(ReferenceModels.ResourceSkillQuery())
                .Factor("team:crew.members[paint=1]", "1", "4").Metric("served", "team:crew.served").Metric("members", "team:crew.members").WithRunTime(300);
            var crs = crew.Run();
            Check(crs.RunsOf(crs.Scenarios[0].Name)[0].Metrics["members"] == 2 && crs.RunsOf(crs.Scenarios[1].Name)[0].Metrics["members"] == 5,
                  $"members with paint=1: 1 → the welder + 1 painter = 2 members, 4 → 5 ({crs.Scenarios[0].Name}; {crs.Scenarios[1].Name})");

            // a source's table from a file per level (push / pull plans)
            string dir = Path.Combine(Path.GetTempPath(), "simulean_study_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string Plan(string file, int rows)
                {
                    var lines = new List<string> { "Time,Name,Q,pick_Item" };
                    for (int i = 0; i < rows; i++) lines.Add($"{i * 10},orders,1,2");
                    string f = Path.Combine(dir, file); File.WriteAllLines(f, lines); return f;
                }
                string few = Plan("few.csv", 5), many = Plan("many.csv", 12);
                var tables = new Study(ReferenceModels.PickingModel()).Factor("orders.table", few, many).Metric("orders", "orders.output").WithRunTime(1000);
                var trs = tables.Run();
                Check(trs.Scenarios.Select(x => x.Name).SequenceEqual(new[] { "orders.table=few", "orders.table=many" }), "scenarios named by the file");
                Check(trs.Runs[0].Metrics["orders"] == 5 && trs.Runs[1].Metrics["orders"] == 12, $"each scenario runs its own table ({trs.Runs[0].Metrics["orders"]}, {trs.Runs[1].Metrics["orders"]})");

                // the table file is a catalog key (follow-up #29): Owner.fileName varies it the same way, and a config
                // with the file only (no copied table) reads it at build
                var withFile = ReferenceModels.PickingModel();
                withFile.Elements.Find(x => x.Name == "orders").Parameters["fileName"] = few;
                var catalog = ModelParameterCatalog.ForElement(withFile.Elements.Find(x => x.Name == "orders"));
                Check(!catalog.Any(p => p.Key == "fileName") && !ModelParameterCatalog.Undecided(withFile).Contains("orders.fileName")
                      && ModelParameterCatalog.Undecided(withFile).Count == 0,
                      "fileName is declared (File): decided, not a numeric catalog parameter");
                var byFile = new Study(withFile).Factor("orders.fileName", few, many).Metric("orders", "orders.output").WithRunTime(1000).Run();
                Check(byFile.Runs[0].Metrics["orders"] == 5 && byFile.Runs[1].Metrics["orders"] == 12,
                      $"orders.fileName=few|many: each scenario reads its file ({byFile.Runs[0].Metrics["orders"]}, {byFile.Runs[1].Metrics["orders"]})");
                var fileOnly = ReferenceModels.PickingModel();
                var src = fileOnly.Elements.Find(x => x.Name == "orders");
                src.Parameters.Remove("arrivalsTable");
                src.Parameters["fileName"] = many;
                var only = new Study(fileOnly).Metric("orders", "orders.output").WithRunTime(1000).Run();
                Check(only.Runs[0].Metrics["orders"] == 12, $"a config with the table file only reads it at build ({only.Runs[0].Metrics["orders"]})");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            // explicit scenarios; a deadlock is a metric
            var dl = new Study(ReferenceModels.DeadlockDeadEnd()).Metric("dead", "model.deadlocked").Metric("t", "model.deadlock_time");
            var drs = dl.Run();
            Check(drs.Runs.Count == 1 && drs.Runs[0].Metrics["dead"] == 1 && drs.Runs[0].Metrics["t"] == 5, "the deadlock flag and time as metrics");
            var ok = new Study(ReferenceModels.BasicLine()).Metric("t", "model.deadlock_time").WithRunTime(50).Run();
            Check(double.IsNaN(ok.Runs[0].Metrics["t"]), "a model time never reached is NaN (no deadlock)");
            var bad = new Study(ReferenceModels.MultiServer3()).Factor("ms.capacity", "1").Scenario("x", "1", "2");
            Check(bad.Validate() != null, "a scenario with the wrong number of values is invalid");
            Check(new Study(ReferenceModels.MultiServer3()).Factor("nope.capacity", "1").Validate() != null, "an unknown element is invalid");
        }
        Console.WriteLine("- Study: factorial scenarios over catalog paths, team members by label, source tables from files, model metrics");

        // members of one name in two teams are two resources in the results (team/member)
        {
            var shared = ReferenceModels.ResourceSharedTeam();
            var second = new SimuLean.Serialization.ResourceTeamConfig { Id = "team2", Name = "team2" };
            second.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = shared.ResourceTeams[0].Members[0].Name });
            shared.ResourceTeams.Add(second);
            string warned = null;
            var oldSink = SimLog.WarningSink;
            SimLog.WarningSink = w => warned = w;
            try
            {
                var clock = new SimClock(1);
                var model = new HeadlessModelFactory(clock);
                model.BuildModel(shared);
                model.StartModel();
                var recorder = new SeriesRecorder(model, 25);
                recorder.RunTo(100);
                var res = RunResults.Capture(model).Of(ResultScope.Resource).ToList();
                string name = shared.ResourceTeams[0].Members[0].Name;
                // the time series use the same ids (both appended to one series, two values per time)
                var util = recorder.Series.Where(x => x.Scope == ResultScope.Resource && x.Metric == ResultMetrics.Utilization).ToList();
                Check(util.Any(x => x.Name == "team/" + name) && util.Any(x => x.Name == "team2/" + name) && util.All(x => x.Times.Count == 4),
                      $"time series: one per resource, 4 points each ({string.Join(", ", util.Select(x => x.Name + ":" + x.Times.Count))})");
                Check(res.Count(o => o.Name == name) == 2 && res.Any(o => o.Id == "team/" + name) && res.Any(o => o.Id == "team2/" + name),
                      $"two members named '{name}': two resources, team/member ids ({string.Join(", ", res.Select(o => o.Id))})");
                Check(res.Any(o => o.Id == shared.ResourceTeams[0].Members[1].Name), "a unique name keeps its id (baselines unchanged)");
                Check(warned != null && warned.Contains(name), $"the build warns about the shared name: {warned}");
            }
            finally { SimLog.WarningSink = oldSink; }
        }
        Console.WriteLine("- Resources of one name in two teams stay apart");

        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
