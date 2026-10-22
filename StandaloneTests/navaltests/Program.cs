using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SimuLean;
using SimuLean.Data;
using SimuLean.Naval;
using SimuLean.Serialization;
using SimuLean.Headless;

// Naval library N1: the block breakdown — reading (table, SQL), validation,
// erection order, the generated parts — and sources that read SQL / a breakdown part when the model is built.
internal static class Program
{
    private static int passed, failed;

    private static void Check(bool ok, string what)
    {
        if (ok) passed++; else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    private static void Section(string name) => Console.WriteLine("- " + name);

    // a small hull: 4 blocks, 7 sub-blocks; block 1 first, 2 and 3 on it, 4 on 2 and 3
    private static Dictionary<string, List<string>> Hull(bool withSeq = false)
    {
        var rows = new[]
        {
            //  block sub kind     turn len  br   depth weight after  x     y  z
            ("1", "1", "flat",   "0", "12", "16", "1.6", "40",  "",    "0",  "0", "0"),
            ("1", "2", "flat",   "0", "12", "16", "1.6", "38",  "",    "0",  "0", "0"),
            ("2", "1", "curved", "1", "12", "8",  "5",   "55",  "1",   "12", "4", "0"),
            ("2", "2", "curved", "0", "12", "8",  "5",   "50",  "1",   "12", "4", "0"),
            ("3", "1", "curved", "1", "12", "8",  "5",   "54",  "1",   "12", "-4", "0"),
            ("4", "1", "flat",   "0", "12", "16", "2",   "45",  "2 3", "24", "0", "5"),
            ("4", "2", "flat",   "0", "12", "16", "2",   "44",  "2;3", "24", "0", "5"),
        };
        string[] cols = { "block", "sub", "kind", "turn", "length", "breadth", "depth", "weight", "erect_after", "hull_x", "hull_y", "hull_z" };
        var t = cols.ToDictionary(c => c, c => new List<string>());
        foreach (var r in rows)
        {
            var v = new[] { r.Item1, r.Item2, r.Item3, r.Item4, r.Item5, r.Item6, r.Item7, r.Item8, r.Item9, r.Item10, r.Item11, r.Item12 };
            for (int i = 0; i < cols.Length; i++) t[cols[i]].Add(v[i]);
        }
        t["t_ba"] = rows.Select(r => r.Item1 == "4" ? "30" : "20").ToList();
        if (withSeq) t["erect_seq"] = rows.Select(r => r.Item1 == "1" ? "1" : r.Item1 == "3" ? "2" : r.Item1 == "2" ? "3" : "4").ToList();
        return t;
    }

    private static Dictionary<string, List<string>> Without(Dictionary<string, List<string>> t, int row)
        => t.ToDictionary(kv => kv.Key, kv => kv.Value.Where((_, i) => i != row).ToList());

    private static Dictionary<string, List<string>> With(Dictionary<string, List<string>> t, string col, int row, string value)
    {
        var c = t.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        if (!c.ContainsKey(col)) c[col] = Enumerable.Repeat("", c.Values.First().Count).ToList();
        c[col][row] = value;
        return c;
    }

    private static bool HasError(BlockBreakdown bd, string fragment)
        => bd.Validate().Any(i => i.IsError && i.Message.Contains(fragment));

    private static HeadlessModelFactory Run(SimulationConfig cfg, double until)
    {
        var clock = new SimClock(cfg.Seed);
        var model = new HeadlessModelFactory(clock);
        model.BuildModel(cfg);
        model.StartModel();
        clock.AdvanceClock(until);
        return model;
    }

    private static bool Near(double a, double b, double tol = 1e-9) => Math.Abs(a - b) <= tol;

    // the erection dock, sinks that keep their items, resources held for their own time
    private static void ErectionTests()
    {
        Section("erection in sequence: out-of-order arrivals, crane held for its lift only, the ship launched and kept");
        var seq = SimuLean.Testing.ReferenceModels.ErectionSequence();
        var m = Run(seq, 500);
        var dock = (ErectionDock)m.GetElement("slipway");
        var launch = (Sink)m.GetElement("launch");
        Check(dock.ShipsLaunched == 1 && launch.GetNumberItems() == 1, "one ship launched");
        Check(launch.KeptCount == 1 && launch.Kept[0].GetSubItems().Count == 6, "the launch sink keeps the ship with its 6 blocks");
        var order = launch.Kept[0].GetSubItems().Select(b => (int)(double)b.GetLabelValue("block")).ToList();
        Check(order.SequenceEqual(new[] { 1, 2, 3, 4, 5, 6 }), "blocks erected in sequence: " + string.Join(",", order));
        Check(Near(dock.LastErectionTime, 29.25), $"last erection at 29.25 (got {dock.LastErectionTime})");
        Check(dock.ErectedCount == 0 && dock.GetStatsContentCurrent() == 0, "the dock is empty after the launch (blocks counted as leaving)");
        var r = SimuLean.Results.RunResults.Capture(m);
        Check(Near(r.Find(SimuLean.Results.ResultScope.Model, "model")[SimuLean.Results.ResultMetrics.Makespan], 29.25), "makespan = the launch");
        var crane = r.Find(SimuLean.Results.ResultScope.Resource, "Crane");
        Check(Near(crane[SimuLean.Results.ResultMetrics.Utilization] * 500, 6, 1e-6), $"crane busy 6 h = 1 h per block (processHoldFor) (got {crane[SimuLean.Results.ResultMetrics.Utilization] * 500})");
        Check(Near(r.Find(SimuLean.Results.ResultScope.Element, "slipway")[SimuLean.Results.ResultMetrics.Utilization] * 500, 26.25, 1e-6),
              "dock erecting 26.25 h (4.75 + 4.5 + 4 + 5 + 4.25 + 3.75)");
        Check(m.GetElement("yard").GetQueueLength() == 0, "the yard is empty");

        Section("erection by precedence: two fronts, a crew of two per erection, the hull kept in the dock");
        var pre = SimuLean.Testing.ReferenceModels.ErectionPrecedence();
        var mp = Run(pre, 500);
        var dk = (ErectionDock)mp.GetElement("dock");
        var hullOrder = dk.Hull.Select(b => (int)(double)b.GetLabelValue("block")).ToList();
        Check(dk.ErectedCount == 6 && dk.ShipsLaunched == 0, "6 blocks erected, the hull stays");
        Check(hullOrder.First() == 1 && hullOrder.Last() == 6 && hullOrder.IndexOf(2) < hullOrder.IndexOf(4) && hullOrder.IndexOf(3) < hullOrder.IndexOf(5),
              "precedence respected: " + string.Join(",", hullOrder));
        Check(Near(dk.LastErectionTime, 46), $"last erection at 46 (crew of 3 covers one erection of 2 at a time) (got {dk.LastErectionTime})");
        var rp = SimuLean.Results.RunResults.Capture(mp);
        Check(Near(rp.Find(SimuLean.Results.ResultScope.Model, "model")[SimuLean.Results.ResultMetrics.Makespan], 46), "makespan = the last erection, not the last arrival");
        Check(!SimClockRun(mp).Stalled, "the kept hull is at rest: no stall reported");
        Check(rp.Find(SimuLean.Results.ResultScope.Slot, "dock/front1") != null, "per-front results");

        Section("a block whose predecessor never comes: the yard waits, reported");
        var stuck = SimuLean.Testing.ReferenceModels.ErectionPrecedence();
        var table = (Dictionary<string, List<string>>)stuck.Elements.Find(e => e.Id == "blocks").Parameters["arrivalsTable"];
        int row1 = table["block"].IndexOf("1");
        foreach (var col in table.Keys.ToList()) table[col].RemoveAt(row1);
        var ms = Run(stuck, 500);
        Check(((ErectionDock)ms.GetElement("dock")).ErectedCount == 0, "nothing erected without block 1");
        Check(SimClockRun(ms).Stalled, "the run is reported stalled / deadlocked (blocks waiting in the yard)");

        Section("sink keeping items; holdFor with the item leaving later");
        var cfg = new SimulationConfig { UseFixedSeed = true, Seed = 5, MaxSimulationTime = 100 };
        ElementConfig El(string id, string type) => new ElementConfig(id, type, id);
        var src = El("src", "UnitySource"); src.Parameters["sourceMode"] = "InterArrival"; src.Parameters["interArrivalStrategyType"] = "Constant"; src.Parameters["constantValue"] = 10.0;
        var ms2 = El("paint", "UnityMultiServer"); ms2.Parameters["capacity"] = 1; ms2.Parameters["delayStrategyType"] = "Constant"; ms2.Parameters["constantValue"] = 8.0;
        ms2.Parameters[ResourceUseKeys.ProcessTeam] = "painters"; ms2.Parameters[ResourceUseKeys.ProcessHoldFor] = "Constant~3";
        var snk = El("store", "UnitySink"); snk.Parameters["keepItems"] = true;
        foreach (var e in new[] { src, ms2, snk }) cfg.Elements.Add(e);
        cfg.Connections.Add(new ConnectionConfig("src", "paint", "General"));
        cfg.Connections.Add(new ConnectionConfig("paint", "store", "General"));
        var team = new ResourceTeamConfig { Id = "painters", Name = "painters" };
        team.Members.Add(new ResourceMemberConfig { Name = "Painter", Labels = new List<string>() });
        cfg.ResourceTeams.Add(team);
        var mh = Run(cfg, 100);
        var store = (Sink)mh.GetElement("store");
        Check(store.KeptCount == store.GetNumberItems() && store.KeptCount >= 9, $"the sink keeps every item ({store.KeptCount})");
        var rh = SimuLean.Results.RunResults.Capture(mh);
        double painterBusy = rh.Find(SimuLean.Results.ResultScope.Resource, "Painter")[SimuLean.Results.ResultMetrics.Utilization] * 100;
        double paintBusy = rh.Find(SimuLean.Results.ResultScope.Element, "paint")[SimuLean.Results.ResultMetrics.Utilization] * 100;
        Check(Near(painterBusy * 8, paintBusy * 3, 1e-6), $"painter 3 of the 8 h per item (painter {painterBusy:0.##} h, station {paintBusy:0.##} h)");
    }

    private static SimuLean.RunStatus SimClockRun(HeadlessModelFactory m) => m.Clock.Context.Run;

    // the block process of the presets (IJPR flow on existing elements) on the sample hull
    // an item announces its visual, so the views that show its labels get it
    private static void VisualHookTests()
    {
        Section("item visual hook (N5): assigning a visual raises Item.VisualAttached; null does not");
        var seen = new System.Collections.Generic.List<Item>();
        System.Action<Item> h = i => seen.Add(i);
        Item.VisualAttached += h;
        try
        {
            var item = new Item(0);
            item.vItem = "a visual";
            item.vItem = null;
            Check(seen.Count == 1 && seen[0] == item, "raised once, for the item with a visual");
            Check(item.vItem == null, "the visual reads back");
        }
        finally { Item.VisualAttached -= h; }
    }

    private static void BlockProcessTests()
    {
        Section("the block process (N3): kits -> sub-assembly -> turning -> yard -> block bays -> outfitting -> painting -> slipway -> launch");
        var hull = SimuLean.Testing.ReferenceModels.SampleHull();
        Check(hull.IsValid && hull.BlockCount == 4 && hull.SubBlocks.Count == 7, "the sample hull is valid");
        var m = Run(SimuLean.Testing.ReferenceModels.NavalBlockProcess(), 2000);
        var slipway = (ErectionDock)m.GetElement("slipway");
        var launch = (Sink)m.GetElement("launch");
        Check(slipway.ShipsLaunched == 1 && launch.KeptCount == 1, "one ship launched and kept");
        Check(launch.KeptCount == 1 && launch.Kept[0].GetSubItems().Count == 4, "the ship holds the 4 blocks");
        Check(m.GetElement("subblock_yard").GetQueueLength() == 0 && m.GetElement("block_yard").GetQueueLength() == 0, "the yards are empty");
        var r = SimuLean.Results.RunResults.Capture(m);
        double turned = r.Find(SimuLean.Results.ResultScope.Element, "turning")[SimuLean.Results.ResultMetrics.Input];
        Check(Near(turned, 2), $"the 2 turned sub-blocks went through turning (got {turned})");
        double bays = r.Find(SimuLean.Results.ResultScope.Element, "block_bays")[SimuLean.Results.ResultMetrics.Output];
        Check(Near(bays, 4), $"4 blocks assembled (got {bays})");
        double paintBusy = r.Find(SimuLean.Results.ResultScope.Element, "painting")[SimuLean.Results.ResultMetrics.Utilization];
        double painterBusy = r.Find(SimuLean.Results.ResultScope.Resource, "Painter1")[SimuLean.Results.ResultMetrics.Utilization];
        Check(Near(painterBusy * 10, paintBusy * 6, 1e-6), "the painter works 6 of the 10 h per block (processHoldFor)");
        var makespan = r.Find(SimuLean.Results.ResultScope.Model, "model")[SimuLean.Results.ResultMetrics.Makespan];
        Check(!SimClockRun(m).Deadlocked && makespan > 0 && makespan < 2000, $"no deadlock, makespan {makespan:0.##} h");
    }

    private static int Main()
    {
        Section("reading: blocks, sub-blocks, block-level columns, labels");
        var bd = BlockBreakdown.FromTable(Hull());
        Check(bd.BlockCount == 4 && bd.SubBlocks.Count == 7, "4 blocks, 7 sub-blocks");
        Check(Math.Abs(bd.SteelWeight - 326) < 1e-9, "steel weight 326 t");
        Check(bd.GetBlock(4).ErectAfter.OrderBy(x => x).SequenceEqual(new[] { 2, 3 }), "block 4 after 2 and 3 (spaces and semicolons)");
        Check(bd.GetBlock(2).HullX == 12 && bd.GetBlock(3).HullY == -4 && bd.GetBlock(4).HullZ == 5, "hull positions");
        Check(bd.SubBlocks.Count(s => s.Turn) == 2, "2 turned sub-blocks");
        Check(bd.GetBlock(4).SubBlocks[0].Columns["t_ba"] == "30", "other columns kept as labels");
        Check(bd.Validate().Count(i => i.IsError) == 0 && bd.IsValid, "the hull is valid");

        Section("validation: duplicates, unknown / self / cyclic predecessors, sequence vs precedence, numbers");
        var t = Hull();
        Check(HasError(BlockBreakdown.FromTable(With(t, "sub", 1, "1")), "appears 2 times"), "duplicated sub-block");
        Check(HasError(BlockBreakdown.FromTable(With(With(t, "erect_after", 5, "9"), "erect_after", 6, "9")), "not in the breakdown"), "unknown predecessor");
        Check(HasError(BlockBreakdown.FromTable(With(t, "erect_after", 0, "1")), "after itself"), "erected after itself");
        var cyc = With(With(t, "erect_after", 0, "4"), "erect_after", 1, "4");
        Check(HasError(BlockBreakdown.FromTable(cyc), "cycle"), "cycle 1 → 4 → 2 → 1");
        Check(HasError(BlockBreakdown.FromTable(With(t, "erect_after", 3, "3")), "differs between its rows"), "block-level column differs between rows");
        Check(BlockBreakdown.FromTable(With(t, "erect_after", 3, "")).IsValid, "a block-level column written on one row only");
        Check(HasError(BlockBreakdown.FromTable(With(t, "weight", 2, "heavy")), "not a number"), "non-numeric weight");
        Check(HasError(BlockBreakdown.FromTable(With(t, "block", 2, "B2")), "whole numbers"), "non-numeric block");
        var seq = Hull(withSeq: true);
        Check(BlockBreakdown.FromTable(seq).IsValid, "sequence consistent with the precedence");
        var badSeq = Hull(withSeq: true);
        for (int i = 0; i < 2; i++) badSeq["erect_seq"][i] = "4";
        for (int i = 5; i < 7; i++) badSeq["erect_seq"][i] = "1";
        Check(HasError(BlockBreakdown.FromTable(badSeq), "breaks the precedence"), "sequence breaking the precedence");
        Check(!BlockBreakdown.FromTable(Without(Without(Hull(), 6), 5)).Validate().Any(i => i.IsError), "a hull without block 4 is still valid");

        Section("erection order and levels");
        var levels = bd.ErectionLevels();
        Check(levels[1] == 0 && levels[2] == 1 && levels[3] == 1 && levels[4] == 2, "levels 0 / 1 / 1 / 2");
        Check(bd.ErectionOrder().Select(b => b.Number).SequenceEqual(new[] { 1, 2, 3, 4 }), "precedence order 1, 2, 3, 4");
        Check(BlockBreakdown.FromTable(seq).ErectionOrder().Select(b => b.Number).SequenceEqual(new[] { 1, 3, 2, 4 }), "sequence order 1, 3, 2, 4");

        Section("generated parts: sub-block kits, block orders, erection plan");
        var opt = new BlockBreakdown.ReleaseOptions { BlockInterval = 10, Start = 5, SubBlockLead = 3 };
        var kits = bd.Generate(BlockBreakdown.Part.SubBlocks, opt);
        var orders = bd.Generate(BlockBreakdown.Part.Blocks, opt);
        var plan = bd.Generate(BlockBreakdown.Part.Erection, opt);
        Check(kits["Name"].Count == 7 && kits["Name"].All(n => n == "SubBlockKit"), "7 kits");
        Check(orders["Name"].Count == 4 && plan["Name"].Count == 4, "4 block orders, 4 erection tokens");
        Check(orders["nsub"].SequenceEqual(new[] { "2", "2", "1", "2" }), "nsub per block = its sub-blocks");
        Check(orders["Time"].SequenceEqual(new[] { "5", "15", "25", "35" }), "blocks every 10 from 5, in erection order");
        Check(kits["Time"].Take(2).All(x => x == "2") && kits["Time"].Last() == "32", "kits 3 before their block");
        Check(kits["weight"][0] == "40" && orders["weight"][0] == "78", "kit weight = the sub-block's, order weight = the block's");
        Check(kits["turn"].Count(x => x == "1") == 2 && kits.ContainsKey("t_ba") && kits.ContainsKey("hull_x"), "kits carry turn, stage times, hull");
        Check(plan["erect_seq"].SequenceEqual(new[] { "1", "2", "3", "4" }) && plan["block"].SequenceEqual(new[] { "1", "2", "3", "4" }), "erection plan in order");
        Check(orders["erect_after"][3] == "2 3" && orders["level"][3] == "2", "orders carry the precedence and level");
        bool threw = false;
        try { BlockBreakdown.FromTable(cyc).Generate(BlockBreakdown.Part.Blocks); } catch (FormatException) { threw = true; }
        Check(threw, "an invalid breakdown does not generate");
        Check(BlockBreakdown.ParsePart("orders") == BlockBreakdown.Part.Blocks && BlockBreakdown.ParsePart("kits") == BlockBreakdown.Part.SubBlocks
              && BlockBreakdown.ParsePart("plan") == BlockBreakdown.Part.Erection, "part names");

        Section("SQL: save, load by table and by query, table specs");
        string dbPath = Path.Combine(Path.GetTempPath(), "simulean_naval_" + Guid.NewGuid().ToString("N") + ".db");
        bool sqlite = true;
        try
        {
            var db = ProjectDatabaseRegistry.GetOrOpen(dbPath);
            bd.Save(db);
            var back = BlockBreakdown.Load(BlockBreakdown.DefaultTable, dbPath);
            Check(back.BlockCount == 4 && back.SubBlocks.Count == 7 && back.IsValid, "round trip through block_breakdown");
            Check(Math.Abs(back.SteelWeight - bd.SteelWeight) < 1e-9 && back.GetBlock(4).ErectAfter.Count == 2, "weights and precedence read back");
            var q = BlockBreakdown.Load("SELECT * FROM block_breakdown WHERE block <> '4'", dbPath);
            Check(q.BlockCount == 3 && q.IsValid, "a SELECT query as the breakdown (ship variant)");
            var spec = TableSources.NavalSpec(BlockBreakdown.Part.Blocks, BlockBreakdown.DefaultTable, interval: 10, start: 5);
            Check(spec == "naval:blocks;interval=10;start=5:block_breakdown", "spec text: " + spec);
            var viaSpec = TableSources.Read(spec, dbPath);
            Check(viaSpec["Time"].SequenceEqual(orders["Time"]) && viaSpec["nsub"].SequenceEqual(orders["nsub"]), "a naval spec reads the same block orders");
            var sql = TableSources.Read("sql:SELECT block, COUNT(*) AS n FROM block_breakdown GROUP BY block ORDER BY block", dbPath);
            Check(sql["n"].SequenceEqual(new[] { "2", "2", "1", "2" }), "an sql spec returns the query's columns");
            bool bad = false;
            try { TableSources.Read("naval:hull:block_breakdown", dbPath); } catch (FormatException) { bad = true; }
            Check(bad && !TableSources.IsSpec("orders.csv") && TableSources.IsSpec(" sql:SELECT 1"), "spec errors and detection");

            Section("a model built from the breakdown: kits → yard → block bays (nsub by block) → sink");
            var cfg = new SimulationConfig { UseFixedSeed = true, Seed = 3, MaxSimulationTime = 1000 };
            ElementConfig El(string id, string type) => new ElementConfig(id, type, id);
            var kitSrc = El("kits", "UnitySource");
            kitSrc.Parameters["sourceMode"] = "Schedule";
            kitSrc.Parameters["tableSource"] = TableSources.NavalSpec(BlockBreakdown.Part.SubBlocks, BlockBreakdown.DefaultTable, interval: 10);
            kitSrc.Parameters["tableDatabase"] = dbPath;
            var orderSrc = El("orders", "UnitySource");
            orderSrc.Parameters["sourceMode"] = "Schedule";
            orderSrc.Parameters["tableSource"] = TableSources.NavalSpec(BlockBreakdown.Part.Blocks, BlockBreakdown.DefaultTable, interval: 10);
            orderSrc.Parameters["tableDatabase"] = dbPath;
            var yard = El("yard", "UnityQueue");
            yard.Parameters["capacity"] = 20;
            yard.Parameters["releaseMode"] = "AnyAccepted";
            var bay = El("bay", "UnityCombiner");
            bay.Parameters["requirements"] = new[] { 1 };
            bay.Parameters["capacity"] = 2;
            bay.Parameters["pullModeType"] = "SingleLabel";
            bay.Parameters["singleLabelName"] = "block";
            bay.Parameters["singleLabelValue"] = "";
            bay.Parameters["updateRequirements"] = true;
            bay.Parameters["updateLabels"] = new List<string> { "nsub" };
            bay.Parameters["delayStrategyType"] = "Constant";
            bay.Parameters["constantValue"] = 8.0;
            bay.Parameters["inputOrder"] = new List<string> { "orders", "yard" };
            var sink = El("hull", "UnitySink");
            foreach (var e in new[] { kitSrc, orderSrc, yard, bay, sink }) cfg.Elements.Add(e);
            cfg.Connections.Add(new ConnectionConfig("kits", "yard", "General"));
            cfg.Connections.Add(new ConnectionConfig("orders", "bay", "General"));
            cfg.Connections.Add(new ConnectionConfig("yard", "bay", "General"));
            cfg.Connections.Add(new ConnectionConfig("bay", "hull", "General"));
            var clock = new SimClock(3);
            var model = new HeadlessModelFactory(clock);
            model.BuildModel(cfg);
            model.StartModel();
            clock.AdvanceClock(1000);
            var hull = (Sink)model.GetElement("hull");
            Check(hull.GetNumberItems() == 4, $"4 blocks assembled (got {hull.GetNumberItems()})");
            Check(model.GetElement("yard").GetQueueLength() == 0, "every sub-block used (yard empty)");

            // the same model with a ship variant (a query without block 4): the spec is the scenario
            kitSrc.Parameters["tableSource"] = "naval:subblocks:SELECT * FROM block_breakdown WHERE block <> '4'";
            orderSrc.Parameters["tableSource"] = "naval:blocks:SELECT * FROM block_breakdown WHERE block <> '4'";
            var clock2 = new SimClock(3);
            var model2 = new HeadlessModelFactory(clock2);
            model2.BuildModel(cfg);
            model2.StartModel();
            clock2.AdvanceClock(1000);
            Check(((Sink)model2.GetElement("hull")).GetNumberItems() == 3, "the variant assembles 3 blocks");

            // a missing table fails the build with the source's name
            kitSrc.Parameters["tableSource"] = "naval:subblocks:no_such_table";
            bool failedBuild = false;
            try { new HeadlessModelFactory(new SimClock(3)).BuildModel(cfg); }
            catch (Exception e) { failedBuild = e.ToString().Contains("kits") && e.ToString().Contains("no_such_table"); }
            Check(failedBuild, "a missing table fails the build naming the source");
        }
        catch (DllNotFoundException) { sqlite = false; Console.WriteLine("  (skipped: no SQLite library)"); }
        finally
        {
            ProjectDatabaseRegistry.CloseAll();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }

        Section("files: a CSV breakdown");
        string csv = Path.Combine(Path.GetTempPath(), "simulean_naval_" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(csv, "block,sub,weight,erect_seq\n1,1,10,1\n1,2,12,1\n2,1,9,2\n");
        var fromCsv = BlockBreakdown.Load(csv, null);
        Check(fromCsv.BlockCount == 2 && fromCsv.IsValid && fromCsv.Generate(BlockBreakdown.Part.Blocks)["nsub"].SequenceEqual(new[] { "2", "1" }), "CSV breakdown");
        File.Delete(csv);

        ErectionTests();
        BlockProcessTests();
        VisualHookTests();

        Console.WriteLine($"{passed} passed, {failed} failed" + (sqlite ? "" : " (SQL part skipped)"));
        return failed == 0 ? 0 : 1;
    }
}
