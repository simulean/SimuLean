// Storage, yards and lists: stock queries, kitting by key with cranes, stock feeds, picking, nodes listed again.
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Serialization;

static partial class Program
{
    static void TestStorageStockQueries()
    {
        var clock = new SimClock(1);
        var storage = new SimuLean.Storage.StorageSystem("Store", clock);
        storage.AddLane(new SimuLean.Storage.StorageLane("deep", new SimuLean.Storage.Coordinate(0, 0, 0), 3,
            SimuLean.Storage.AccessDiscipline.Lifo, SimuLean.Storage.SkuConstraint.Mixed));
        storage.AddLane(new SimuLean.Storage.StorageLane("free", new SimuLean.Storage.Coordinate(5, 0, 0), 3,
            SimuLean.Storage.AccessDiscipline.Direct, SimuLean.Storage.SkuConstraint.Mixed));
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(storage, new List<Element> { sink });
        storage.Start(); sink.Start();

        Item Make(string sku, double expiry)
        {
            var it = new Item(0);
            it.SetType(sku);
            it.SetLabelValue("expiry", expiry);
            return it;
        }
        var a1 = Make("A", 30); var b1 = Make("B", 5); var a2 = Make("A", 10);
        foreach (var it in new[] { a1, b1, a2 }) storage.Receive(it);     // all in "deep" (first lane that accepts)
        Check(storage.GetQueueLength() == 3, "three items in stock");

        var got = new List<Item>();
        var A = SimuLean.Collections.ListQuery.All.Where("sku", SimuLean.Collections.ListOp.Equal, "A");
        Check(storage.RequestRetrieval(A.OrderBy("expiry"), got.Add), "FEFO retrieval of A accepted");
        Check(got.Count == 1 && got[0] == a2, "FEFO: the A expiring first (a label, no field to define)");
        Check(storage.RequestRetrieval(A, got.Add) && got[1] == a1, "FIFO by default: the oldest A");
        Check(storage.TotalRelocations == 1, $"b1 blocked a1 at the face of the LIFO lane and was re-homed (got {storage.TotalRelocations})");
        Check(!storage.RequestRetrieval(A, got.Add), "no A left: the request fails, nothing moves");
        Check(storage.RequestRetrieval(SimuLean.Collections.ListQuery.All.Where("depth", SimuLean.Collections.ListOp.Equal, 0), got.Add)
              && got[2] == b1, "the depth field reads where the item sits now");
        Check(sink.GetStatsInputCount() == 3 && storage.GetQueueLength() == 0 && storage.StoredItems.Count == 0, "every retrieved item left through the link");
    }

    static void TestYardListAxes()
    {
        var cfg = SimuLean.Testing.ReferenceModels.YardListAxes();
        var clock = new SimClock(cfg.Seed);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(cfg);
        var order = new Dictionary<string, List<string>> { ["erectedA"] = new List<string>(), ["erectedB"] = new List<string>() };
        foreach (var sink in new[] { "erectedA", "erectedB" })
        {
            string s = sink;
            model.Find(s).ItemEntered += (el, it) => order[s].Add(Convert.ToString(it.GetLabelValue("block"), System.Globalization.CultureInfo.InvariantCulture));
        }
        model.StartModel();
        clock.AdvanceClock(cfg.MaxSimulationTime);
        var r = SimuLean.Results.RunResults.Capture(model);
        var crane = r.Find(SimuLean.Results.ResultScope.Resource, "Crane1");
        Check(string.Join(",", order["erectedA"]) == "3,1" && string.Join(",", order["erectedB"]) == "2,4",
              $"each bay gets its own sub-block, out of order (A {string.Join(",", order["erectedA"])}; B {string.Join(",", order["erectedB"])})");
        Check(crane[SimuLean.Results.ResultMetrics.Tasks] == 4, $"the crane carried the 4 sub-blocks ({crane[SimuLean.Results.ResultMetrics.Tasks]})");
        Check(r.Info.EndReason == "Empty" && !r.Info.Deadlocked, $"the run ends empty, no deadlock ({r.Info.EndReason})");
        double span = r.Find(SimuLean.Results.ResultScope.Model, "model")[SimuLean.Results.ResultMetrics.Makespan];
        Check(span > 20, $"the crane's travel on its axes takes time (makespan {span})");
    }

    static void TestWeldQueueSameList()
    {
        var cfg = SimuLean.Testing.ReferenceModels.WeldQueueSameList();
        var clock = new SimClock(cfg.Seed);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(cfg);
        model.StartModel();
        clock.AdvanceClock(cfg.MaxSimulationTime);
        var r = SimuLean.Results.RunResults.Capture(model);
        double welded = r.Find(SimuLean.Results.ResultScope.Element, "welded")[SimuLean.Results.ResultMetrics.Input];
        double viaQueue = r.Find(SimuLean.Results.ResultScope.Element, "weldQueue")[SimuLean.Results.ResultMetrics.Output];
        Check(welded == 4, $"the robot welds all 4 nodes ({welded})");
        Check(viaQueue >= 1, $"some nodes went through the welding queue ({viaQueue})");
        Check(r.Info.EndReason == "Empty" && !r.Info.Deadlocked, $"the run ends empty, no deadlock ({r.Info.EndReason})");
    }

    static void TestYardKitting()
    {
        (SimuLean.Results.RunResults r, Dictionary<string, List<double>> order) Run(SimuLean.Serialization.SimulationConfig cfg)
        {
            var clock = new SimClock(cfg.Seed);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(cfg);
            var order = new Dictionary<string, List<double>>();
            foreach (var e in model.GetAllElements().Values)
                if (e is Sink)
                {
                    var list = order[e.Name] = new List<double>();
                    e.ItemEntered += (el, it) => list.Add(it.GetLabelValue("block") ?? -1);
                }
            model.StartModel();
            clock.AdvanceClock(cfg.MaxSimulationTime);
            return (SimuLean.Results.RunResults.Capture(model), order);
        }
        string Seq(List<double> l) => string.Join(",", l);

        var direct = Run(SimuLean.Testing.ReferenceModels.YardKitting());
        Check(Seq(direct.order["erectedA"]) == "3,1" && Seq(direct.order["erectedB"]) == "2,4",
              $"direct links, any accepted: each bay gets the sub-block of its own block, out of order (A {Seq(direct.order["erectedA"])}, B {Seq(direct.order["erectedB"])})");
        var model = direct.r.Find(SimuLean.Results.ResultScope.Model, "model");
        Check(model[SimuLean.Results.ResultMetrics.Makespan] == 20 && direct.r.Find(SimuLean.Results.ResultScope.Resource, "Crane1")[SimuLean.Results.ResultMetrics.Tasks] == 4,
              "the crane carries every sub-block (4 moves of 3), makespan 20 (worked by hand)");

        var fifo = Run(SimuLean.Testing.ReferenceModels.YardKitting("FIFO"));
        Check(fifo.r.Info.Deadlocked && fifo.r.Info.Waiting.Exists(w => w.StartsWith("yard:") && w.Contains("waits for bayA, bayB")),
              $"FIFO: block 1 at the head, wanted by nobody yet — the yard deadlocks ({string.Join(" | ", fifo.r.Info.Waiting)})");

        var list = Run(SimuLean.Testing.ReferenceModels.YardKittingList());
        Check(Seq(list.order["erectedA"]) == "3,1" && Seq(list.order["erectedB"]) == "2,4"
              && list.r.Find(SimuLean.Results.ResultScope.Resource, "Crane1")[SimuLean.Results.ResultMetrics.Tasks] == 4
              && list.r.Find(SimuLean.Results.ResultScope.Model, "model")[SimuLean.Results.ResultMetrics.Makespan] == 20,
              "through a list: the same matching, and the yard's crane carries each pulled sub-block (the holder's transport)");

        var cap = Run(SimuLean.Testing.ReferenceModels.YardKittingCapacity());
        Check(Seq(cap.order["erected"]) == "1,3,2,4" && cap.r.Find(SimuLean.Results.ResultScope.Model, "model")[SimuLean.Results.ResultMetrics.Makespan] == 19,
              $"one Combiner, capacity 2: each assembly matches its own main item (erected {Seq(cap.order["erected"])}, worked by hand: 1,3,2,4 by t = 19)");
        Check(cap.r.Of(SimuLean.Results.ResultScope.Slot).Count() == 2, "an assembly is a slot in the results");

        // a Multi-Server as the yard (finished items held) releases any accepted item too
        var msCfg = SimuLean.Testing.ReferenceModels.YardKitting();
        var yard = msCfg.Elements.Find(e => e.Id == "yard");
        yard.Type = "MultiServer";
        yard.Set("capacity", 4);
        yard.Set("delayStrategy.strategyType", "Constant");
        yard.Set("delayStrategy.constantValue", 0.0);
        var ms = Run(msCfg);
        Check(Seq(ms.order["erectedA"]) == "3,1" && Seq(ms.order["erectedB"]) == "2,4", "a Multi-Server releases any accepted finished item");
    }

    // ── #56 ──────────────────────────────────────────────────────────────────

    static void TestStockFeedTwoStations()
    {
        // the reference picking model with a second picking station fed by the same stock source: each StockInput
        // connection used to replace the source's output link, so the first station never got stock
        var c = SimuLean.Testing.ReferenceModels.PickingModel();
        var copy = c.Clone();
        var orders2 = copy.Elements.First(e => e.Id == "orders");
        orders2.Id = orders2.Name = "orders2";
        var pick2 = copy.Elements.First(e => e.Id == "pick");
        pick2.Id = pick2.Name = "pick2";
        c.Elements.Add(orders2); c.Elements.Add(pick2);
        c.Connections.Add(new ConnectionConfig("orders2", "pick2", "General"));
        c.Connections.Add(new ConnectionConfig("pick2", "out", "General"));
        c.Connections.Add(new ConnectionConfig("stock", "pick2", "StockInput"));
        c.Elements.First(e => e.Id == "stock").Set("outputStrategy.strategyType", "RoundRobin");   // feeds both in turn
        var clock = new SimClock(1);
        var factory = new SimuLean.Headless.HeadlessModelFactory(clock, enableLogging: false);
        var els = factory.BuildModel(c);
        factory.StartModel();
        clock.AdvanceClock(c.MaxSimulationTime);
        Check(els["pick"].GetStatsOutputCount() > 0 && els["pick2"].GetStatsOutputCount() > 0,
              $"both stations picked (pick {els["pick"].GetStatsOutputCount()}, pick2 {els["pick2"].GetStatsOutputCount()})");

        var bad = SimuLean.Testing.ReferenceModels.PickingModel();
        bad.Connections.Add(new ConnectionConfig("stock", "out", "CombinerMainInput"));
        bool threw = false;
        try { new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(bad); }
        catch (FormatException ex) { threw = ex.Message.Contains("CombinerMainInput"); }
        Check(threw, "an unknown connection type fails the build, naming it");
    }

    static void TestPickingKeepsStock()
    {
        // the reference picking model, its stock not picked returned after each order (default) or kept in the bins
        (double picked, double returned, int held) Run(bool returnLeftovers)
        {
            var c = SimuLean.Testing.ReferenceModels.PickingModel();
            if (!returnLeftovers) c.Elements.First(e => e.Id == "pick").Set("returnLeftovers", false);
            var clock = new SimClock(1);
            var factory = new SimuLean.Headless.HeadlessModelFactory(clock, enableLogging: false);
            var els = factory.BuildModel(c);
            factory.StartModel();
            clock.AdvanceClock(c.MaxSimulationTime);
            var pick = (PickingStation)els["pick"];
            return (pick.GetStatsOutputCount(), els["returns"].GetStatsInputCount(), pick.GetStockPort().GetQueueLength());
        }
        var flushed = Run(true);
        var kept = Run(false);
        Check(flushed.returned > 0, $"by default the stock not picked is returned ({flushed.returned})");
        Check(kept.returned == 0 && kept.held > 0, $"off: nothing returned, the stock stays in the bins ({kept.held} held)");
        Check(kept.picked >= flushed.picked, $"and no order waits longer for its stock ({kept.picked} vs {flushed.picked} picked)");
    }
}
