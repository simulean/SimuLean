// Model data: named data tables — ModelTable / ModelData (typed access, key lookup, clear
// errors), their sources (inline, CSV file, SQL on the project database) and the snapshot rule (read at the run's start,
// never during the run; a config without a snapshot is read when the model is built).
// Run: dotnet run  (from StandaloneTests/datatests)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimuLean;
using SimuLean.Data;
using SimuLean.Headless;
using SimuLean.Serialization;
using SimuLean.Testing;

static class Program
{
    static int passed, failed;

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    static string Fails(Action a)
    {
        try { a(); return null; }
        catch (Exception e) { return e.Message; }
    }

    static Dictionary<string, List<string>> Cols(params (string name, string[] cells)[] cols)
        => cols.ToDictionary(c => c.name, c => c.cells.ToList());

    static SimClock Build(SimulationConfig c)
    {
        var clock = new SimClock(1);
        new HeadlessModelFactory(clock).BuildModel(c);
        return clock;
    }

    // 4 items of kind a at t = 0 through one server whose time is read from the table 'times' (kind -> time; inline 5).
    static SimulationConfig TimedLine()
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
        var src = new ElementConfig("src", "UnitySource", "src");
        src.Parameters["sourceMode"] = "Schedule";
        src.Parameters["arrivalsTable"] = Cols(("Time", new[] { "0", "0", "0", "0" }), ("Name", new[] { "s", "s", "s", "s" }),
                                               ("Q", new[] { "1", "1", "1", "1" }), ("kind", new[] { "a", "a", "a", "a" }));
        var srv = new ElementConfig("srv", "UnityMultiServer", "srv");
        srv.Parameters["capacity"] = 1;
        srv.Parameters["delayStrategyType"] = "Expression";
        srv.Parameters["expression"] = "table('times', item.kind, 'time')";
        c.Elements.Add(src); c.Elements.Add(srv); c.Elements.Add(new ElementConfig("sink", "UnitySink", "sink"));
        c.Connections.Add(new ConnectionConfig("src", "srv", "General"));
        c.Connections.Add(new ConnectionConfig("srv", "sink", "General"));
        c.Tables.Add(new DataTableConfig { Name = "times", Columns = Cols(("kind", new[] { "a" }), ("time", new[] { "5" })) });
        return c;
    }

    static int Main()
    {
        Console.WriteLine("- ModelTable: typed access, key lookup, required columns, errors that name the table");
        var t = ModelTable.From("orders", Cols(("id", new[] { "A", "B", "C" }), ("qty", new[] { "2", "3,5", "x" })));
        Check(t.RowCount == 3 && t.Columns.SequenceEqual(new[] { "id", "qty" }), "rows and columns in order");
        Check(t.Text(1, "id") == "B" && t.Number(0, "qty") == 2 && t.Number(1, "qty") == 3.5, "text, numbers (a decimal comma too)");
        Check(t.Find("id", "C") == 2 && t.Find("id", "Z") == -1, "key lookup");
        string bad = Fails(() => t.Number(2, "qty"));
        Check(bad != null && bad.Contains("'orders'") && bad.Contains("row 3") && bad.Contains("'qty'"), $"a bad number names table, row, column: {bad}");
        string missing = Fails(() => t.Require("id", "due"));
        Check(missing != null && missing.Contains("'orders'") && missing.Contains("'due'") && missing.Contains("id, qty"),
              $"a missing column names the table and the columns there are: {missing}");
        string ragged = Fails(() => ModelTable.From("r", Cols(("a", new[] { "1", "2" }), ("b", new[] { "1" }))));
        Check(ragged != null && ragged.Contains("'r'"), $"columns of different length fail: {ragged}");

        Console.WriteLine("- ModelData: tables by name; an unknown one names those there are");
        var c = ReferenceModels.BasicLine();
        c.Tables.Add(new DataTableConfig { Name = "speeds", Columns = Cols(("station", new[] { "a", "b" }), ("speed", new[] { "1.5", "2" })) });
        var clock = Build(c);
        Check(clock.Context.Data["speeds"].Number(1, "speed") == 2, "an inline table reaches SimContext.Data");
        string unknown = Fails(() => { var _ = clock.Context.Data["nope"]; });
        Check(unknown != null && unknown.Contains("'nope'") && unknown.Contains("speeds"), $"unknown table: {unknown}");
        var dup = ReferenceModels.BasicLine();
        dup.Tables.Add(new DataTableConfig { Name = "x", Columns = Cols(("a", new[] { "1" })) });
        dup.Tables.Add(new DataTableConfig { Name = "x", Columns = Cols(("a", new[] { "2" })) });
        Check(Fails(() => Build(dup))?.Contains("defined twice") == true, "a name used twice fails the build");

        string dir = Path.Combine(Path.GetTempPath(), "simulean_data_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Console.WriteLine("- File source: read at build when there is no snapshot; a snapshot is never re-read");
            string csv = Path.Combine(dir, "plan.csv");
            File.WriteAllLines(csv, new[] { "block,start", "B1,0", "B2,12.5" });
            var f = ReferenceModels.BasicLine();
            f.Tables.Add(new DataTableConfig { Name = "plan", Source = DataSourceKind.File, File = csv });
            Check(Build(f).Context.Data["plan"].Number(1, "start") == 12.5, "the file is read when the model is built");
            f.Tables[0].Columns = DataResolver.Read(f.Tables[0], null);   // the snapshot (as Unity takes at extraction)
            File.Delete(csv);
            Check(Build(f).Context.Data["plan"].RowCount == 2, "with a snapshot the file is not needed (no I/O at run)");
            var gone = ReferenceModels.BasicLine();
            gone.Tables.Add(new DataTableConfig { Name = "plan", Source = DataSourceKind.File, File = csv });
            string err = Fails(() => Build(gone));
            Check(err != null && err.Contains("'plan'") && err.Contains("plan.csv"), $"a missing file names the table and the file: {err}");
            var xl = new DataTableConfig { Name = "book", Source = DataSourceKind.File, File = Path.Combine(dir, "book.xlsx") };
            File.WriteAllText(xl.File, "");
            string excel = Fails(() => DataResolver.Read(xl, null));
            Check(excel != null && excel.Contains("'book'") && excel.Contains("Excel"), $"Excel without the Unity reader: a clear error: {excel}");
            Check(DataResolver.Read(xl, null, (p, s) => Cols(("k", new[] { "v" })))["k"][0] == "v", "a file reader can be given (Unity: Excel)");

            Console.WriteLine("- SQL source: a SELECT on the project database, columns named even with no row");
            string dbPath = Path.Combine(dir, "project.db");
            var db = ProjectDatabaseRegistry.GetOrOpen(dbPath);
            db.Execute("CREATE TABLE orders (id TEXT, qty REAL, due INTEGER)");
            db.ExecuteBatch("INSERT INTO orders VALUES (?, ?, ?)", new[] { new object[] { "A", 2.5, 10 }, new object[] { "B", 4.0, 20 } });
            var s = ReferenceModels.BasicLine();
            s.DatabasePath = dbPath;
            s.Tables.Add(new DataTableConfig { Name = "orders", Source = DataSourceKind.Sql, Sql = "SELECT id, qty, due FROM orders ORDER BY id" });
            s.Tables.Add(new DataTableConfig { Name = "none", Source = DataSourceKind.Sql, Sql = "SELECT id, qty FROM orders WHERE qty > 100" });
            var data = Build(s).Context.Data;
            Check(data["orders"].RowCount == 2 && data["orders"].Number(0, "qty") == 2.5 && data["orders"].Text(1, "due") == "20",
                  "rows as invariant text (2.5, 20)");
            Check(data["none"].RowCount == 0 && data["none"].Columns.SequenceEqual(new[] { "id", "qty" }), "an empty result keeps its columns");
            var noDb = ReferenceModels.BasicLine();
            noDb.Tables.Add(new DataTableConfig { Name = "orders", Source = DataSourceKind.Sql, Sql = "SELECT 1" });
            Check(Fails(() => Build(noDb))?.Contains("project database") == true, "an SQL table without a database path fails clearly");
            var badSql = ReferenceModels.BasicLine();
            badSql.DatabasePath = dbPath;
            badSql.Tables.Add(new DataTableConfig { Name = "q", Source = DataSourceKind.Sql, Sql = "SELECT nope FROM orders" });
            string qerr = Fails(() => Build(badSql));
            Check(qerr != null && qerr.Contains("'q'"), $"a failing query names the table: {qerr}");

            Console.WriteLine("- Import into the project database (D2): types inferred, append or replace, tables listed");
            var plan = Cols(("block", new[] { "B1", "B2", "B3" }), ("start", new[] { "0", "12,5", "" }), ("note", new[] { "a", "1", "c" }));
            Check(db.ImportTable("plan", plan) == 3, "3 rows imported");
            var types = db.QueryTable("SELECT typeof(start) AS s, typeof(note) AS n FROM plan ORDER BY block");
            Check(types["s"][0] == "real" && types["s"][1] == "real" && types["s"][2] == "null" && types["n"][1] == "text",
                  $"a column of numbers is REAL (a decimal comma too), mixed is TEXT, empty is NULL ({string.Join(",", types["s"])}; {string.Join(",", types["n"])})");
            Check(db.QueryTable("SELECT start FROM plan WHERE block = 'B2'")["start"][0] == "12.5", "read back as invariant text");
            db.ImportTable("plan", Cols(("block", new[] { "B4" }), ("start", new[] { "40" }), ("note", new[] { "d" })), replace: false);
            Check(db.QueryTable("SELECT block FROM plan")["block"].Count == 4, "append keeps the rows");
            db.ImportTable("plan", Cols(("block", new[] { "X" }), ("start", new[] { "1" }), ("note", new[] { "x" })));
            Check(db.QueryTable("SELECT block FROM plan")["block"].Count == 1, "replace drops the table first");
            Check(db.ListTables().SequenceEqual(new[] { "orders", "plan" }), $"tables listed, sorted ({string.Join(",", db.ListTables())})");
            Check(Fails(() => db.ImportTable("bad name", plan))?.Contains("not a valid table name") == true, "an unsafe table name is refused");
            Check(Fails(() => db.ImportTable("t2", Cols(("a b", new[] { "1" }))))?.Contains("not a valid column name") == true, "an unsafe column name is refused");

            Console.WriteLine("- Data variants in studies (D5): data:Table.file / data:Table.sql, one scenario per level");
            var timed = TimedLine();
            File.WriteAllLines(Path.Combine(dir, "fast.csv"), new[] { "kind,time", "a,1" });
            File.WriteAllLines(Path.Combine(dir, "slow.csv"), new[] { "kind,time", "a,3" });
            var byFile = new SimuLean.Optimization.Study(timed)
                .Factor("data:times.file", Path.Combine(dir, "fast.csv"), Path.Combine(dir, "slow.csv"))
                .Metric("span", "model.makespan").WithRunTime(100).Run();
            Check(byFile.Runs.Count == 2 && byFile.Runs[0].Metrics["span"] == 4 && byFile.Runs[1].Metrics["span"] == 12,
                  $"each scenario's table from its file: 4 items x 1 / x 3 ({byFile.Runs[0].Metrics["span"]}, {byFile.Runs[1].Metrics["span"]})");
            db.ImportTable("times_a", Cols(("kind", new[] { "a" }), ("time", new[] { "2" })));
            timed.DatabasePath = dbPath;
            var bySql = new SimuLean.Optimization.Study(timed)
                .Factor("data:times.sql", "SELECT kind, time FROM times_a", "SELECT kind, time * 2 AS time FROM times_a")
                .Metric("span", "model.makespan").WithRunTime(100).Run();
            Check(bySql.Runs[0].Metrics["span"] == 8 && bySql.Runs[1].Metrics["span"] == 16,
                  $"each scenario's table from its query ({bySql.Runs[0].Metrics["span"]}, {bySql.Runs[1].Metrics["span"]})");
            Check(timed.Tables[0].Source == DataSourceKind.Inline && timed.Tables[0].Columns["time"][0] == "5", "the study never changes the model it was given");
            Check(new SimuLean.Optimization.Study(timed).Factor("data:nope.file", "x.csv").Validate()?.Contains("'nope'") == true,
                  "an unknown table is invalid, naming it");
            Check(new SimuLean.Optimization.Study(timed).Factor("data:times.rows", "1").Validate()?.Contains("'file' or 'sql'") == true,
                  "only file / sql vary");

            Console.WriteLine("- A Schedule Source reads its arrivals from a data table (D3)");
            var fromTable = TimedLine();
            var srcCfg = fromTable.Elements.Find(e => e.Id == "src");
            srcCfg.Parameters.Remove("arrivalsTable");
            srcCfg.Parameters["dataTable"] = "arrivals";
            fromTable.Tables.Add(new DataTableConfig
            {
                Name = "arrivals",
                Columns = Cols(("Time", new[] { "0", "0" }), ("Name", new[] { "s", "s" }), ("Q", new[] { "1", "1" }), ("kind", new[] { "a", "a" })),
            });
            var run = new SimuLean.Optimization.Study(fromTable).Metric("out", "sink.input").Metric("span", "model.makespan").WithRunTime(100).Run();
            Check(run.Runs[0].Metrics["out"] == 2 && run.Runs[0].Metrics["span"] == 10, $"2 arrivals from the table, 5 each ({run.Runs[0].Metrics["out"]}, {run.Runs[0].Metrics["span"]})");
            File.WriteAllLines(Path.Combine(dir, "three.csv"), new[] { "Time,Name,Q,kind", "0,s,1,a", "0,s,1,a", "0,s,1,a" });
            var variants = new SimuLean.Optimization.Study(fromTable).Factor("data:arrivals.file", Path.Combine(dir, "three.csv"))
                .Metric("out", "sink.input").WithRunTime(100).Run();
            Check(variants.Runs[0].Metrics["out"] == 3, "data:arrivals.file varies the source's arrivals");
            var byFileKey = new SimuLean.Optimization.Study(fromTable).Factor("src.fileName", Path.Combine(dir, "three.csv"))
                .Metric("out", "sink.input").WithRunTime(100).Run();
            Check(byFileKey.Runs[0].Metrics["out"] == 3, "Owner.fileName (#29) still works: its file wins over the data table");
            var both = fromTable.Clone();
            both.Elements.Find(e => e.Id == "src").Parameters["fileName"] = Path.Combine(dir, "three.csv");
            string bErr = Fails(() => Build(both));
            Check(bErr != null && bErr.Contains("'arrivals'") && bErr.Contains("data:arrivals.file"),
                  $"a hand-written config with a data table and a file fails instead of ignoring the file: {bErr}");
            var missingTable = TimedLine();
            missingTable.Elements.Find(e => e.Id == "src").Parameters.Remove("arrivalsTable");
            missingTable.Elements.Find(e => e.Id == "src").Parameters["dataTable"] = "nope";
            string mErr = Fails(() => Build(missingTable));
            Check(mErr != null && mErr.Contains("'src'") && mErr.Contains("'nope'") && mErr.Contains("times"), $"an unknown data table names the source, the table and those there are: {mErr}");

            Console.WriteLine("- A downtime timetable read from a data table (D3b): keys to targets, code rules");
            var stopsModel = TimedLine();
            stopsModel.Tables.Add(new DataTableConfig
            {
                Name = "stops",
                Columns = Cols(("machine", new[] { "M1", "M1", "M1", "other" }), ("start", new[] { "10", "30", "50", "20" }),
                               ("duration", new[] { "4", "3", "5", "7" }), ("code", new[] { "A", "X", "B", "A" })),
            });
            var dt = new DowntimeConfig("plan", "Timetable", "srv");
            dt.Parameters["dataTable"] = "stops";
            dt.Parameters["targetColumn"] = "machine";
            dt.Parameters["startColumn"] = "start";
            dt.Parameters["durationColumn"] = "duration";
            dt.Parameters["codeColumn"] = "code";
            dt.Parameters["codeRules"] = new List<string> { "X|||1|1", "B||Immediate|2|0" };
            dt.Parameters["targetKeys"] = new List<string> { "M1|srv" };
            stopsModel.Downtimes.Add(dt);
            var dclock = new SimClock(1);
            var dmodel = new HeadlessModelFactory(dclock);
            dmodel.BuildModel(stopsModel);
            dmodel.StartModel();
            dclock.AdvanceClock(100);
            var gen = dmodel.DowntimeGenerators[0];
            Check(gen.StopCount == 2 && Math.Abs(gen.TotalDowntime - 14) < 1e-9,
                  $"M1's rows only, X dropped, B doubled: 4 + 2 x 5 = 14 ({gen.StopCount} stops, {gen.TotalDowntime})");
            var noTable = TimedLine();
            var dt2 = new DowntimeConfig("plan", "Timetable", "srv");
            dt2.Parameters["dataTable"] = "nope";
            noTable.Downtimes.Add(dt2);
            string dErr = Fails(() => Build(noTable));
            Check(dErr != null && dErr.Contains("'plan'") && dErr.Contains("'nope'"), $"an unknown table names the downtime and the table: {dErr}");

            Console.WriteLine("- Pick lists from a data table (D3c): indexed once, per order key or one shared list");
            var picks = ModelTable.From("picks", Cols(("order_id", new[] { "7.0", "7", "8", "8" }), ("sku", new[] { "A", "B", "A", "A" }),
                                                      ("qty", new[] { "2", "1", "3", "1" })));
            var perOrder = new TablePickListSource(picks, "sku", "qty", "order_id", "order_id");
            var o7 = new Item(0); o7.SetLabelValue("order_id", 7);
            var o8 = new Item(0); o8.SetLabelValue("order_id", 8);
            var o9 = new Item(0); o9.SetLabelValue("order_id", 9);
            var l7 = perOrder.GetPickList(o7); var l8 = perOrder.GetPickList(o8);
            Check(l7 != null && l7["A"] == 2 && l7["B"] == 1, "order 7: its lines (the key '7.0' matches the label 7)");
            Check(l8 != null && l8.Count == 1 && l8["A"] == 4, "order 8: lines of one SKU add up");
            Check(perOrder.GetPickList(o9) == null, "an order without lines: null (the next source of the chain)");
            l7["A"] = 99;
            Check(perOrder.GetPickList(o7)["A"] == 2, "each container gets its own copy");
            var shared = new TablePickListSource(picks, "sku", "qty");
            Check(shared.GetPickList(o9)["A"] == 6, "no key column: the whole table is one list");
            Check(Fails(() => new TablePickListSource(picks, "sku", "amount"))?.Contains("'amount'") == true, "a missing column names it");
            var pickCfg = ReferenceModels.PickingModel();
            var stationCfg = pickCfg.Elements.Find(e => e.Type == "UnityPickingStation");
            stationCfg.Parameters["pickTable"] = "nope";
            string pErr = Fails(() => Build(pickCfg));
            Check(pErr != null && pErr.Contains("'nope'") && pErr.Contains(stationCfg.Name), $"a picking station with an unknown table names it: {pErr}");

            Console.WriteLine("- The config: tables are copied with it (studies clone the config per run)");
            var clone = s.Clone();
            clone.Tables[0].Sql = "changed";
            Check(s.Tables[0].Sql != "changed" && clone.Tables.Count == 2 && clone.DatabasePath == dbPath, "deep copy of the tables");
        }
        finally
        {
            ProjectDatabaseRegistry.CloseAll();
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine();
        // ── JSON of any value, CSV of an Excel in a decimal-comma locale ──
        {
            var when = new DateTime(2026, 10, 9, 12, 0, 0);
            string json = MiniJson.Serialize(new Dictionary<string, object> { ["when"] = when, ["n"] = 3.5, ["k"] = (short)2 });
            object back = null;
            try { back = MiniJson.Parse(json); } catch (Exception) { }
            Check(back is Dictionary<string, object> d && (string)d["when"] == when.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
                  && Convert.ToDouble(d["k"]) == 2, $"a date is written as a string, other numbers as numbers: valid JSON ({json})");
            bool threw = false;
            try { MiniJson.Parse("\"\\u12\""); } catch (FormatException) { threw = true; } catch (Exception) { }
            Check(threw, "a truncated \\u escape is a FormatException");

            var rows = TableReader.ParseCsv("Time;Name;Q\n0;A;1,5\n10;B;2\n");
            Check(rows.Count == 3 && rows[0].Count == 3 && rows[1][2] == "1,5", $"a ';' CSV: three columns, decimal commas kept ({string.Join("|", rows[0])})");
            Check(TableReader.ParseCsv("a,b\n\"x;y\",2\n")[1][0] == "x;y", "a ',' CSV is unchanged (quoted ';')");
            Console.WriteLine("- MiniJson: dates / enums as strings, bad escapes; CSV separator ';' detected");
        }

        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
