// Standalone tests of the warehouse supply: a storage publishing its stock as a
// model list (a pull retrieves the item and the storage's transport delivers it to the puller), waiting pulls, the
// storage's own move times and programmatic retrievals of a published stock; the Warehousing pack's PickFace (single
// pieces, kanban replenishment, empties port, list output) and BaseStockReplenisher (truckloads of the consumed mix, lead
// time, unloading windows, forklift unloading, initial stock). Expected times are hand-computed (see each test).
// Run: dotnet run  (from StandaloneTests/warehousetests)
using System;
using System.Collections.Generic;
using SimuLean;
using SimuLean.Collections;
using SimuLean.Headless;
using SimuLean.Results;
using SimuLean.Serialization;
using SimuLean.Storage;
using SimuLean.Testing;

static class Program
{
    static int passed, failed;

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    static void Near(double actual, double expected, string what, double tol = 1e-6)
        => Check(Math.Abs(actual - expected) <= tol, $"{what}: expected {expected}, got {actual}");

    static void Times(List<double> actual, double[] expected, string what)
    {
        Check(actual.Count == expected.Length, $"{what}: {expected.Length} times expected, got {actual.Count} ({string.Join(", ", actual)})");
        for (int i = 0; i < Math.Min(actual.Count, expected.Length); i++) Near(actual[i], expected[i], $"{what} #{i + 1}");
    }

    static int Main()
    {
        Run("Published stock: a list pull digs (blocker re-homed) and the forklift delivers to the puller", TestStockListPull);
        Run("A pull with no room to re-home its blocker waits; it starts when a retrieval frees the lane", TestWaitingPull);
        Run("Storage keys (physical dimensions, put-away by the team) and a lane's room through the config", TestStorageKeysRoundTrip);
        Run("Storage moves use the element's transport load / unload times when set", TestStorageMoveTimes);
        Run("Programmatic retrieval of a published stock withdraws the entry; the Retrieved event", TestProgrammaticRetrieval);
        Run("Pick face: kanban of one load, single pieces, empties port, truckload of the consumed mix", TestPickFaceKanban);
        Run("Replenisher: lead time, unloading window, forklift unloading, consumed mix per truck", TestBaseStockTruck);
        Run("Pick face pieces published to a list: pulled by query sku == X; reserve load and reorder point", TestPiecesList);
        Run("Replenisher observing a storage: programmatic retrievals are consumption; unknown SKUs ignored", TestReplenisherObserves);
        Run("Codec round trip, SKU table, config errors", TestCodec);
        Run("Size from the model: the replenisher's and the face's loads, the face's pieces; labels win", TestModelSizes);
        Run("User example warehouse_to_line: a warehouse supplies an assembly line through pick faces and a tugger train", TestWarehouseToLine);
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    // Builds and starts a config; records the entry times of the given elements.
    static (HeadlessModelFactory f, SimClock clock, Dictionary<string, List<double>> times) Build(SimulationConfig c, params string[] watch)
    {
        var clock = new SimClock(c.Seed);
        var f = new HeadlessModelFactory(clock);
        f.BuildModel(c);
        var times = new Dictionary<string, List<double>>();
        foreach (string w in watch)
        {
            var list = new List<double>(); times[w] = list;
            f.GetElement(w).ItemEntered += (e, it) => list.Add(clock.GetSimulationTime());
        }
        f.StartModel();
        return (f, clock, times);
    }

    // Network dock(0) — n1(10) — n2(20) — face(30), forklift 1 m/s, load / unload 1, home dock; the queue at face (1)
    // feeds a station of 100 per item and pulls sku == A.
    //   A@0 into L1 (n1), pulled at once: dock→n1 10, load 11, n1→face 31, unload 32 (station 32–132).
    //   A@40 into L1 (free again), pulled: face→n1 60, load 61, →face 81, unload 82 (waits in the queue).
    //   A@50, B@60: L1 is being emptied → L2 (n2), B in front (LIFO).
    //   132: the station takes A, the queue pulls A@50: B re-homed face→n2 142, load 143, n2→n1 153, unload 154;
    //   A: n1→n2 164, load 165, n2→face 175, unload 176. Station: 132–232, 232–332.
    static void TestStockListPull()
    {
        var (f, clock, t) = Build(ReferenceModels.StorageStockListPull(), "q", "sink");
        var wh = (StorageSystem)f.GetElement("wh");
        int retrieved = 0;
        wh.Retrieved += (s, it) => retrieved++;
        clock.AdvanceClock(400);
        Times(t["q"], new[] { 32.0, 82, 176 }, "A delivered to the queue");
        Times(t["sink"], new[] { 132.0, 232, 332 }, "station output");
        Check(wh.TotalRelocations == 1, $"one blocker re-homed (got {wh.TotalRelocations})");
        Check(wh.TotalRetrievals == 3 && retrieved == 3, $"three retrievals (got {wh.TotalRetrievals} / event {retrieved})");
        Check(wh.GetLane("L1").Count == 1 && wh.GetLane("L1").Contents[0].Item.Name == "B", "B now in L1");
        Check(wh.WaitingPullCount == 0, "no pull waiting");
        var stock = f.Lists["stock"];
        Check(stock.Pushed == 4 && stock.Pulled == 3 && stock.Count == 1, $"list pushed 4 / pulled 3 / content 1 (got {stock.Pushed}/{stock.Pulled}/{stock.Count})");
        var r = RunResults.Capture(f);
        var whRes = r.Find(ResultScope.Element, "wh");
        Near(whRes[ResultMetrics.Input], 4, "storage input (link-driven)");
        Near(whRes[ResultMetrics.Output], 3, "storage output (link-driven)");
        Near(r.Find(ResultScope.List, "stock")[ResultMetrics.Pulled], 3, "list metric pulled");
    }

    // The same model with one lane L1 (n1, capacity 3) and arrivals A@0, A@40, A@90, B@95: the first two as above (32,
    // 82); A@90 and B@95 into L1, B in front. 132: the queue pulls A@90 — B has nowhere to go: the pull waits.
    // 200: B retrieved by program: face→n1 220, load 221, n1→dock 231, unload 232; the waiting pull starts:
    // dock→n1 242, load 243, n1→face 263, unload 264.
    // The storage keys and a lane's room survive the config.
    static void TestStorageKeysRoundTrip()
    {
        var c = ReferenceModels.StorageStockListPull();
        var wh = c.Elements.Find(e => e.Id == "wh");
        wh.SettingsAs<StorageSystemSettings>().Lanes = new List<StorageLaneSettings>
            { StorageLaneSettings.Of("L1", 10, 0, 0, 3, AccessDiscipline.Lifo, SkuConstraint.Mixed, "n1", 0.9, 1.38, 1.2) };
        wh.Set("physicalDimensions", true);
        wh.Set("heightLabel", "h");
        wh.Set("putAwayByTransport", true);
        wh.Set("inboundCapacity", 3.0);
        wh.Set("releaseAfterDwell", true);
        wh.Set("minimumDwellTime", 7.5);
        var (f, clock, t) = Build(c, "q");
        var storage = (StorageSystem)f.GetElement("wh");
        var lane = storage.Lanes[0];
        Check(storage.PhysicalDimensions && storage.HeightLabel == "h" && storage.LengthLabel == "length" && storage.PutAwayByTransport
              && storage.InboundCapacity == 3 && storage.ReleaseAfterDwell && storage.MinimumDwellTime == 7.5, "keys built");
        Check(Math.Abs(lane.SlotWidth - 0.9) < 1e-12 && Math.Abs(lane.SlotHeight - 1.38) < 1e-12 && Math.Abs(lane.SlotLength - 1.2) < 1e-12, "the lane's room");
        clock.AdvanceClock(400);
        Check(storage.TotalRetrievals > 0, "the model runs with put-away by the team (a published stock: the dwell release does not apply)");
        bool refused = false;
        try { wh.Set("lanes[0].access", "Sideways"); } catch (FormatException) { refused = true; }
        Check(refused && wh.Get<int>("lanes[0].capacity", 0) == 3, "typed lane records (lanes[i].capacity); an unknown access fails");
    }

    static void TestWaitingPull()
    {
        var c = ReferenceModels.StorageStockListPull();
        var wh = c.Elements.Find(e => e.Id == "wh");
        wh.SettingsAs<StorageSystemSettings>().Lanes = new List<StorageLaneSettings>
            { StorageLaneSettings.Of("L1", 10, 0, 0, 3, AccessDiscipline.Lifo, SkuConstraint.Mixed, "n1") };
        var src = c.Elements.Find(e => e.Id == "in");
        var table = src.Get<Dictionary<string, List<string>>>("arrivalsTable");
        table["Time"] = new List<string> { "0", "40", "90", "95" };
        var (f, clock, t) = Build(c, "q");
        var storage = (StorageSystem)f.GetElement("wh");
        clock.AdvanceClock(200);
        Check(storage.WaitingPullCount == 1, $"the pull of A@90 waits (got {storage.WaitingPullCount})");
        Times(t["q"], new[] { 32.0, 82 }, "first two deliveries");
        Item got = null;
        Check(storage.RequestRetrieval(ListQuery.All.Where("sku", ListOp.Equal, "B"), it => got = it), "programmatic retrieval of B");
        clock.AdvanceClock(400);
        Check(got != null && got.Name == "B", "B handed to its callback");
        Check(storage.WaitingPullCount == 0, "the waiting pull started");
        Times(t["q"], new[] { 32.0, 82, 264 }, "A delivered once B is out");
        Check(storage.TotalRelocations == 0, "nothing re-homed");
    }

    // Same model with the storage's transport load 3 / unload 2 (the member's 1 / 1): dock→n1 10, load 13, n1→face 33,
    // unload 35.
    static void TestStorageMoveTimes()
    {
        var c = ReferenceModels.StorageStockListPull();
        var wh = c.Elements.Find(e => e.Id == "wh");
        wh.Set("transportBy.loadTime", "Constant~3");
        wh.Set("transportBy.unloadTime", "Constant~2");
        var (f, clock, t) = Build(c, "q");
        clock.AdvanceClock(100);
        Check(t["q"].Count > 0, "A delivered");
        if (t["q"].Count > 0) Near(t["q"][0], 35, "first delivery with the storage's own move times");
    }

    // An instant storage (no transport) publishing to a list with no puller: a programmatic retrieval takes the item out
    // of the list and hands it to the callback only (the list is not a destination).
    static void TestProgrammaticRetrieval()
    {
        var clock = new SimClock(1);
        var wh = new StorageSystem("wh", clock);
        wh.AddLane(new StorageLane("L", new Coordinate(0, 0, 0), 5, AccessDiscipline.Direct, SkuConstraint.Mixed));
        var link = new ListLink(new SimList("stock", clock));
        link.AddOrigin(wh);
        var events = new List<string>();
        wh.Retrieved += (s, it) => events.Add(it.Name);
        wh.Start(); link.Start();
        foreach (string s in new[] { "A", "B", "A" })
        {
            var it = new Item(0); it.SetId(s, 0, 0); it.SetLabelText("sku", s);
            Check(wh.Receive(it), "stored " + s);
        }
        Check(wh.PublishedList == link, "the list output is its published list");
        Check(link.List.Count == 3, "three entries published");
        Item got = null;
        Check(wh.RequestRetrieval(ListQuery.All.Where("sku", ListOp.Equal, "B"), it => got = it), "programmatic retrieval");
        Check(got != null && got.Name == "B", "B retrieved (instant)");
        Check(link.List.Count == 2, "its entry withdrawn from the published list");
        Check(wh.RequestRetrieval(null, null), "oldest retrieved");
        Check(link.List.Count == 1 && wh.GetLane("L").Count == 1, "one left, still listed");
        Check(string.Join(",", events) == "B,A", $"Retrieved raised per retrieval (got {string.Join(",", events)})");
    }

    // Face P (1 load of 3 pieces, kanban of one), instant storage with 3 loads, station 2 per piece.
    //   load 1 at 0: pieces done 2, 4, 6; empty at 4 (third piece taken) → load 2 at 4: done 8, 10, 12; empty 10 →
    //   load 3: done 14, 16, 18; empty 16 → no stock (3 consumed at 0, 4, 10 → truck ordered at 10, lead 20 → 30).
    //   At 30: load 4 → pieces taken 30, 32, 34 (done 32, 34, 36); empty 34 → load 5 → done 38, 40, 42; empty 40 →
    //   load 6 → done 44, 46, 48; empty 46. Second truck ordered at 40 (arrives 60 > 50).
    static void TestPickFaceKanban()
    {
        var (f, clock, t) = Build(ReferenceModels.PickFaceKanban(), "sink", "empties");
        clock.AdvanceClock(50);
        Times(t["sink"], new[] { 2.0, 4, 6, 8, 10, 12, 14, 16, 18, 32, 34, 36, 38, 40, 42, 44, 46, 48 }, "pieces done");
        Times(t["empties"], new[] { 4.0, 10, 16, 34, 40, 46 }, "empty loads");
        var face = (PickFace)f.GetElement("face");
        Check(face.PiecesTaken == 18 && face.LoadsReceived == 6 && face.LoadsEmptied == 6, "face counters");
        Check(face.StockOuts == 2, $"two stock-outs (16–30 and open at the end) (got {face.StockOuts})");
        Near(face.GetStateRatio(ElementState.STARVED), (14.0 + 4.0) / 50.0, "STARVED share (16–30, 46–50)");
        var r = RunResults.Capture(f);
        var fr = r.Find(ResultScope.Element, "face");
        Near(fr[ResultMetrics.Input], 6 + 18, "face input = loads + pieces");
        Near(fr[ResultMetrics.Output], 18 + 6, "face output = pieces + empties");
        Near(fr[ResultMetrics.Content], 0, "face content at the end");
        Near(fr[ResultMetrics.StockOuts], 2, "stockouts metric");
        Check(r.Find(ResultScope.Element, "face/empties") != null, "empties port reported");
        var repl = r.Find(ResultScope.Element, "repl");
        Near(repl[ResultMetrics.TrucksOrdered], 2, "trucks ordered");
        Near(repl[ResultMetrics.TrucksReceived], 1, "trucks received");
        Near(repl[ResultMetrics.LoadsReleased], 3, "loads released (initial stock not counted)");
        Check(((Sink)f.GetElement("sink")).GetNumberItems() == 18, "sink count");
    }

    // Minutes; calendar starts Monday 00:00, window Mon–Fri 06:00–14:00 (360–840). Line 60 per load, pulls everything.
    //   A1 0→60, A2 60→120, B1 120→180; consumed A, A → truck 1 at 60 (lead 120) arrives 180, waits for 360;
    //   forklift (load 2.5 + unload 2.5): stored 365 and 370 → line A3 365→425, A4 425→485.
    //   Consumed B (120), A3 (365) → truck 2 at 365, arrives 485 (in the window): B stored 490, A 495; line B 490→550,
    //   A 550→610 (> 600). Consumed A4 (425), B (490) → truck 3 at 490, arrives 610 (> 600).
    static void TestBaseStockTruck()
    {
        var (f, clock, t) = Build(ReferenceModels.BaseStockTruck(), "sink", "wh");
        clock.AdvanceClock(600);
        Times(t["sink"], new[] { 60.0, 120, 180, 425, 485, 550 }, "line output");
        Times(t["wh"], new[] { 0.0, 0, 0, 365, 370, 490, 495 }, "stored");
        var repl = (BaseStockReplenisher)f.GetElement("repl");
        Check(repl.TrucksOrdered == 3 && repl.TrucksReceived == 2 && repl.TrucksUnloaded == 2, $"trucks 3/2/2 (got {repl.TrucksOrdered}/{repl.TrucksReceived}/{repl.TrucksUnloaded})");
        Near(repl.LeadTimes.Average, 120, "lead time");
        Near(repl.Trucks[0].ArrivalTime, 180, "truck 1 arrival");
        Near(repl.Trucks[0].UnloadedTime, 367.5, "truck 1 handed over (end of the second load)");
        Near(repl.Trucks[1].UnloadedTime, 492.5, "truck 2 handed over");
        Check(string.Join(",", repl.Trucks[1].Skus) == "B,A", "truck 2 = the consumed mix, in order (B, A)");
        Near(repl.UnloadTimes.Average, (187.5 + 7.5) / 2, "unloading time");
        Check(repl.GetTimeInState(ElementState.OFF_SHIFT) > 0, "off shift outside the window");
        var r = RunResults.Capture(f);
        var rr = r.Find(ResultScope.Element, "repl");
        Near(rr[ResultMetrics.Backlog], 1, "backlog (A retrieved at 550, not yet ordered)");
        Near(rr[ResultMetrics.Consumed], 7, "consumed (0, 60, 120, 365, 425, 490, 550)");
        Near(rr[ResultMetrics.Output], 4, "loads released through the link");
        Near(rr[ResultMetrics.LeadTimeAvg], 120, "lead_time_avg metric");
        Check(r.Find(ResultScope.List, "stock")[ResultMetrics.PendingPullsAvg] > 0, "the line waited for stock (pending pulls)");
    }

    // Face P: 2 positions (open + reserve), reorder point 1, starts full with 2 loads of 2 pieces, pushes its pieces to
    // the list "pieces"; face Q: 1 load of 2 pieces, no input. Station sp pulls sku == P (1 per piece), sq sku == Q.
    //   P: pieces done 1, 2 (load 1 empty at 1: reserve opens, order 1 → stock load arrives at 1), 3, 4 (load 2 empty at
    //   3 → load 3 opens; order → no stock), 5, 6 (empty at 5).  Q: done 1, 2.
    static void TestPiecesList()
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 7, MaxSimulationTime = 20 };
        var repl = new ElementConfig("repl", "BaseStockReplenisher", "repl");
        var rs = new ReplenisherSettings();
        rs.Skus.Add(new ReplenisherSkuSettings { Sku = "P", PiecesPerLoad = 2, InitialLoads = 1 });
        rs.Skus.Add(new ReplenisherSkuSettings { Sku = "Q", PiecesPerLoad = 2, InitialLoads = 0 });
        rs.TruckloadSize = 99;
        repl.Settings = rs;
        c.Elements.Add(repl);
        var wh = new ElementConfig("wh", "StorageSystem", "wh");
        wh.SettingsAs<StorageSystemSettings>().Lanes = new List<StorageLaneSettings> { StorageLaneSettings.Of("L", 0, 0, 0, 10) };
        c.Elements.Add(wh);
        var fp = new ElementConfig("fp", "PickFace", "fp");
        fp.Settings = new PickFaceSettings { Sku = "P", Positions = 2, ReorderPoint = 1, StartFull = true, PiecesPerLoad = 2 };
        var fq = new ElementConfig("fq", "PickFace", "fq");
        fq.Settings = new PickFaceSettings { Sku = "Q", StartFull = true, PiecesPerLoad = 2 };
        c.Elements.Add(fp); c.Elements.Add(fq);
        foreach (string s in new[] { "sp", "sq" })
        {
            var e = new ElementConfig(s, "MultiServer", s);
            e.Set("capacity", 1); e.Set("delayStrategy.strategyType", "Constant"); e.Set("delayStrategy.constantValue", 1.0);
            c.Elements.Add(e);
            c.Elements.Add(new ElementConfig(s + "Sink", "Sink", s + "Sink"));
            c.Connections.Add(new ConnectionConfig(s, s + "Sink", "General"));
        }
        c.Connections.Add(new ConnectionConfig("repl", "wh", "General"));
        c.Lists.Add(new ListConfig { Id = "stock", Name = "stock" });
        c.Lists.Add(new ListConfig { Id = "pieces", Name = "pieces" });
        c.Connections.Add(new ConnectionConfig("wh", "stock", "PushToList"));
        var pull = new ConnectionConfig("stock", "fp", "PullFromList"); pull.Query = "sku == P"; c.Connections.Add(pull);
        c.Connections.Add(new ConnectionConfig("fp", "pieces", "PushToList"));
        c.Connections.Add(new ConnectionConfig("fq", "pieces", "PushToList"));
        var pp = new ConnectionConfig("pieces", "sp", "PullFromList"); pp.Query = "sku == P"; c.Connections.Add(pp);
        var pq = new ConnectionConfig("pieces", "sq", "PullFromList"); pq.Query = "sku == Q"; c.Connections.Add(pq);

        var (f, clock, t) = Build(c, "spSink", "sqSink", "sp");
        var got = new List<string>();
        f.GetElement("sp").ItemEntered += (e, it) => got.Add(it.Name + ":" + it.GetLabelText(PickFace.SkuLabel));
        clock.AdvanceClock(20);
        Times(t["spSink"], new[] { 1.0, 2, 3, 4, 5, 6 }, "P pieces done");
        Times(t["sqSink"], new[] { 1.0, 2 }, "Q pieces done");
        Check(got.TrueForAll(s => s == "P:P"), "pieces of type P with label sku = P");
        var face = (PickFace)f.GetElement("fp");
        Check(face.LoadsOrdered == 3 && face.LoadsReceived == 3, $"P ordered 3 (at 1, 3, 5: position <= 1), received 3 (2 initial + 1) (got {face.LoadsOrdered}/{face.LoadsReceived})");
        Check(face.OnOrder == 2, "two loads still on order (no stock)");
        Near(f.Lists["pieces"].Pulled, 8, "pieces pulled from the list");
        Near(f.Lists["stock"].PendingPullCount, 1, "the face's pull waits (back-order)");
    }

    static void TestReplenisherObserves()
    {
        var clock = new SimClock(1);
        var wh = new StorageSystem("wh", clock);
        wh.AddLane(new StorageLane("L", new Coordinate(0, 0, 0), 5, AccessDiscipline.Direct, SkuConstraint.Mixed));
        var link = new ListLink(new SimList("stock", clock));
        link.AddOrigin(wh);
        var repl = new BaseStockReplenisher("repl", clock) { TruckloadSize = 2 };
        repl.AddSku("A", 1); repl.AddSku("B", 1);
        repl.Observe(wh);
        wh.Start(); repl.Start(); link.Start();
        foreach (string s in new[] { "A", "B", "A" })
        {
            var it = new Item(0); it.SetId(s, 0, 0); it.SetLabelText(PickFace.SkuLabel, s);
            Check(wh.Receive(it), "stored " + s);
        }
        Check(wh.RequestRetrieval(ListQuery.All.Where("sku", ListOp.Equal, "B"), null), "programmatic retrieval");
        Check(repl.ConsumedCount == 1 && repl.TrucksOrdered == 0, "consumption observed, no truck yet");
        Check(wh.RequestRetrieval(null, null), "oldest retrieved");
        Check(repl.TrucksOrdered == 1 && repl.Trucks[0].Skus.Count == 2, "truck of 2 ordered");
        repl.NotifyConsumed("Z");   // not in the table: ignored with a warning
        Check(repl.ConsumedCount == 2, "unknown SKU ignored");
    }

    static void TestCodec()
    {
        var c = new ElementConfig("f", "PickFace", "f");
        c.Settings = new PickFaceSettings { Sku = "1001", Positions = 3, ReorderPoint = 2, ReorderQuantity = 2, PiecesPerLoad = 40, StartFull = true };
        var face = ReplenishmentBuilders.BuildPickFace(c, new SimClock(1), null);
        Check(face.Sku == "1001" && face.Positions == 3 && face.ReorderPoint == 2 && face.ReorderQuantity == 2 && face.PiecesPerLoad == 40 && face.StartFull, "pick face round trip");

        var r = new ElementConfig("r", "BaseStockReplenisher", "r");
        var rs = new ReplenisherSettings { TruckloadSize = 26, LeadTime = SamplerSettings.Constant(48), UnloadingWindows = "Mon-Fri 06:00-22:00" };
        rs.Skus.Add(new ReplenisherSkuSettings { Sku = "X", PiecesPerLoad = 12, InitialLoads = 4 });
        r.Settings = rs;
        var rep = ReplenishmentBuilders.BuildReplenisher(r, new SimClock(1), null);
        Check(rep.TruckloadSize == 26 && rep.LeadTime != null && rep.UnloadingWindows != null && rep.Skus.Count == 1
              && rep.Skus[0].PiecesPerLoad == 12 && rep.Skus[0].InitialLoads == 4, "replenisher round trip");

        var table = new Dictionary<string, List<string>>
        {
            ["SKU"] = new List<string> { "A", "B", "" }, ["PiecesPerLoad"] = new List<string> { "10", "", "" }, ["initialLoads"] = new List<string> { "2", "1", "" },
        };
        var rows = ReplenishmentBuilders.SkusFromTable(table, "r");
        Check(rows.Count == 2 && rows[0].PiecesPerLoad == 10 && rows[0].InitialLoads == 2 && rows[1].PiecesPerLoad == 1, "SKU table (case-insensitive, defaults, blank rows skipped)");

        bool threw = false;
        try { ReplenishmentBuilders.SkusFromTable(new Dictionary<string, List<string>> { ["sku"] = new List<string> { "A" }, ["piecesPerLoad"] = new List<string> { "x" } }, "r"); }
        catch (FormatException ex) { threw = ex.Message.Contains("'x'"); }
        Check(threw, "a SKU count that is not an integer fails loudly");
        r.Set("skus[0].initialLoads", 6);
        Check(r.Get<int>("skus[0].initialLoads", 0) == 6 && r.Get<string>("skus[0].sku") == "X", "typed SKU records (skus[i].initialLoads)");
        threw = false;
        try { ReplenishmentBuilders.BuildPickFace(new ElementConfig("g", "PickFace", "g"), new SimClock(1), null); } catch (FormatException) { threw = true; }
        Check(threw, "pick face without sku fails loudly");
    }

    // The size of the load / piece prefab (written by the scene's extractor) reaches the items where their labels set none
    // the replenisher's loads, the face's own loads and the pieces of every load the face opens, received ones
    // included. The kanban model: the replenisher's 3 loads of 3 pieces go through the storage to the face.
    static void TestModelSizes()
    {
        // codec: keys only when a size is known
        var c = new ElementConfig("f", "PickFace", "f");
        c.Settings = new PickFaceSettings { Sku = "P" };
        ReplenishmentBuilders.EncodeModelSize(c, "", new[] { 1.2, 0.144, 0.8 });
        ReplenishmentBuilders.EncodeModelSize(c, ReplenishmentBuilders.PieceSizePrefix, new[] { 0.3, 0.2, 0.2 });
        Near(c.Get<double>("modelHeight", 0), 0.144, "load height key");
        Near(c.Get<double>("pieceModelWidth", 0), 0.3, "piece width key");
        var bare = new ElementConfig("g", "PickFace", "g");
        bare.Settings = new PickFaceSettings { Sku = "P" };
        ReplenishmentBuilders.EncodeModelSize(bare, ReplenishmentBuilders.PieceSizePrefix, new double[3]);
        Check(!bare.Has("modelWidth") && !bare.Has("pieceModelWidth"), "no size, no keys");
        Check(ReplenishmentBuilders.DecodeModelSize(bare, ReplenishmentBuilders.PieceSizePrefix) == null, "no size decodes to null");
        var own = ReplenishmentBuilders.BuildPickFace(c, new SimClock(1), null).CreateLoad();
        Near(own.GetLabelOrDefault("height"), 0.144, "the face's own load gets the load model's height");
        Check(!own.subItems[0].HasLabel("width"), "its pieces get their size when the load is opened");

        // a run: the replenisher's loads and the pieces the face hands out
        var cfg = ReferenceModels.PickFaceKanban();
        ElementConfig El(string id) => cfg.Elements.Find(e => e.Id == id);
        ReplenishmentBuilders.EncodeModelSize(El("repl"), "", new[] { 1.2, 0.9, 0.8 });
        ReplenishmentBuilders.EncodeModelSize(El("face"), "", new[] { 1.0, 0.1, 1.0 });   // only its own loads
        ReplenishmentBuilders.EncodeModelSize(El("face"), ReplenishmentBuilders.PieceSizePrefix, new[] { 0.4, 0.25, 0.3 });
        var (f, clock, t) = Build(cfg);
        var pieces = new List<Item>(); var empties = new List<Item>();
        f.GetElement("sink").ItemEntered += (e, it) => pieces.Add(it);
        f.GetElement("empties").ItemEntered += (e, it) => empties.Add(it);
        clock.AdvanceClock(50);
        Check(pieces.Count == 18 && empties.Count == 6, $"18 pieces and 6 loads out (got {pieces.Count}, {empties.Count})");
        Check(empties.TrueForAll(l => Math.Abs(l.GetLabelOrDefault("height") - 0.9) < 1e-9 && Math.Abs(l.GetLabelOrDefault("width") - 1.2) < 1e-9),
              "the replenisher's loads have its load model's size (the face's own size not applied to received loads)");
        Check(pieces.TrueForAll(p => Math.Abs(p.GetLabelOrDefault("width") - 0.4) < 1e-9 && Math.Abs(p.GetLabelOrDefault("height") - 0.25) < 1e-9
                                     && Math.Abs(p.GetLabelOrDefault("length") - 0.3) < 1e-9),
              "every piece handed out has the piece model's size");

        // a label wins: a load that already has a height keeps it, its pieces keep theirs
        var labelled = new ElementConfig("h", "PickFace", "h");
        labelled.Settings = new PickFaceSettings { Sku = "P", PiecesPerLoad = 1 };
        ReplenishmentBuilders.EncodeModelSize(labelled, "", new[] { 1.2, 0.144, 0.8 });
        var face = ReplenishmentBuilders.BuildPickFace(labelled, new SimClock(1), null);
        var load = new Item(0); load.SetLabelValue("height", 1.5);
        load.SetSizeWhereMissing(face.LoadModelSize);
        Near(load.GetLabelOrDefault("height"), 1.5, "a labelled height is kept");
        Near(load.GetLabelOrDefault("width"), 1.2, "the missing sides from the model");
    }

    static double Val(ResultObject o, string metric) => o != null && o.TryGet(metric, out double v) ? v : double.NaN;

    // Bodies every 60 s from 60 to 28800: 480 arrive; the assembly takes 50 s, so if the supply never stops the line, every
    // body but the last (arrived at 28800, in assembly at the end) leaves: 479 cars. Each pick face has one stock-out: at the
    // start, before the forklift brings its first pallet. Pallets of 40 pieces: 496 pieces taken = 12 pallets emptied per
    // face (+ 2 open / in reserve); 24 empty pallets in all. A combiner per SKU packs 4 pieces into an empty tote (20 s; the
    // face hands out no piece while a tote is packed, so one piece fewer than the stacker it replaced). The train
    // brings 2 totes per part per tour; the unpackers empty them and the train takes the empty totes back.
    static void TestWarehouseToLine()
    {
        var c = ReferenceModels.WarehouseToLine();
        var (f, clock, t) = Build(c, "Cars");
        clock.AdvanceClock(c.MaxSimulationTime);
        var r = RunResults.Capture(f);
        Check(!r.Info.Deadlocked, "no deadlock");
        Near(Val(r.Find(ResultScope.Element, "Cars"), ResultMetrics.Input), 479, "every body assembled but the last (the line never starves)");
        Near(Val(r.Find(ResultScope.Element, "Assembly"), ResultMetrics.Content), 1, "the last body in assembly at the end");
        foreach (var sku in new[] { "A", "B" })
        {
            var face = r.Find(ResultScope.Element, "Face" + sku);
            Near(Val(face, ResultMetrics.StockOuts), 1, $"Face{sku}: one stock-out, at the start");
            Near(Val(face, ResultMetrics.Pieces), 496, $"Face{sku}: pieces taken (packed into totes)");
            Near(Val(r.Find(ResultScope.Element, "Face" + sku + "/empties"), ResultMetrics.Output), 12, $"Face{sku}: 12 pallets emptied");
            double toLine = Val(r.Find(ResultScope.Element, "Line" + sku), ResultMetrics.Input);
            Check(toLine > 0, $"Line{sku} received totes and pieces");
            var line = r.Find(ResultScope.Element, "Line" + sku);
            double emptied = Val(line, ResultMetrics.CarriersEmptied), released = Val(line, ResultMetrics.Released);
            Check(emptied > 100 && released >= 4 * emptied && released < 4 * (emptied + 1),
                  $"Line{sku}: each tote brings 4 pieces and is emptied (totes {emptied}, pieces {released})");
        }
        Near(Val(r.Find(ResultScope.Element, "EmptyPallets"), ResultMetrics.Input), 24, "24 empty pallets");
        var train = r.Find(ResultScope.Element, "TuggerTrain");
        double tours = Val(train, ResultMetrics.Tours), delivered = Val(train, ResultMetrics.Delivered), back = Val(train, ResultMetrics.EmptiesReturned);
        Check(tours > 100 && delivered == 2 * tours, $"two totes per tour (tours {tours}, delivered {delivered})");
        Check(back >= delivered - 4 && back <= delivered, $"the empty totes come back (delivered {delivered}, returned {back})");
        Near(Val(r.Find(ResultScope.Element, "TotesBack"), ResultMetrics.Input), back, "the returned totes reach TotesBack");
        var trucks = r.Find(ResultScope.Element, "Trucks");
        Check(Val(trucks, ResultMetrics.TrucksReceived) >= 5 && Val(trucks, ResultMetrics.Consumed) == 4 * Val(trucks, ResultMetrics.TrucksOrdered),
              "trucks of 4 pallets of the consumed mix");
        var stock = f.Lists["Stock"];
        Check(stock.Pushed > 0 && stock.Pulled == Val(trucks, ResultMetrics.Consumed), "every pallet consumed was pulled from the published stock");
    }
}
