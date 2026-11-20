// Standalone runtime tests for the SequencingStation (JIS sequencing / kitting into carriers): lot accumulation,
// the carrier timeline, pieces pulled on demand from a list or fed by links, gaps, stock-outs, blocked output,
// max wait, operators (carrier / piece scope) and the oldest-order dispatching rule, builder + headless factory.
// Expected times are computed by hand in the comments. Run: dotnet run  (from StandaloneTests/sequencingtests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Collections;
using SimuLean.Resources;
using SimuLean.Results;
using SimuLean.Serialization;
using SimuLean.Stochastics.Distributions;

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

    static int Main()
    {
        Run("Timeline: lot, prepare, pick + place, gap, close; labels and statistics", TestTimeline);
        Run("Stock-out: waits for the piece of its reference, other pieces stay listed", TestStockOut);
        Run("Pieces fed by a link into a stock buffer, drawn by reference", TestBufferByLink);
        Run("Blocked output: the carrier waits, the operator is held until it leaves (ItemLeaves)", TestBlocked);
        Run("Max wait: starts with fewer orders, closes a partial carrier", TestMaxWait);
        Run("Oldest-order rule: |oldestItemTime vs FIFO with one shared operator", TestOldestOrderRule);
        Run("Operator per piece", TestPieceScope);
        Run("Expressions: applies, per-reference extra time", TestExpressions);
        Run("Builder round trip + headless factory (ports, list pull, served orders to a sink)", TestFactory);

        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    // ── harness ──────────────────────────────────────────────────────────

    /// <summary>Emits given items at given times; keeps refused ones and offers them again (any of them on request).</summary>
    sealed class Emitter : Element
    {
        readonly List<Item> held = new List<Item>();
        public Emitter(SimClock c, string name) : base(name, c) { StatsCollector.TracksContent = false; }

        public Item At(double t, string type, params (string label, string value)[] labels)
        {
            var it = new Item(t);
            it.SetId(type, 0, 0);
            foreach (var l in labels) it.SetLabelValue(l.label, l.value);
            simClock.ScheduleEvent(new ActionEvent(() =>
            {
                held.Add(it);
                if (GetOutput() != null && GetOutput().SendItem(it, this)) held.Remove(it);
            }), t);
            return it;
        }

        public override bool SendHeldItem(Item item)
        {
            if (!held.Contains(item) || !GetOutput().SendItem(item, this)) return false;
            held.Remove(item);
            return true;
        }

        public override bool Unblock()
        {
            bool any = false;
            foreach (var it in held.ToArray())
                if (GetOutput().SendItem(it, this)) { held.Remove(it); any = true; }
            return any;
        }

        public int Held => held.Count;
        public override int GetQueueLength() => held.Count;
        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item i) => false;
        public override bool Receive(Item i) => false;
    }

    /// <summary>Downstream that records what arrives and when; refuses while closed.</summary>
    sealed class Collector : Element
    {
        public readonly List<Item> Got = new List<Item>();
        public readonly List<double> At = new List<double>();
        public bool Open = true;
        public Collector(SimClock c, string name = "Collector") : base(name, c) { }
        public void OpenNow() { Open = true; GetInput().NotifyAvailable(this); }
        public override bool Receive(Item i) { if (!Open) return false; Got.Add(i); At.Add(simClock.GetSimulationTime()); return true; }
        public override bool CheckAvailability(Item i) => Open;
        public override bool Unblock() => false;
        public override int GetQueueLength() => 0;
        public override int GetFreeCapacity() => Open ? 1 : 0;
    }

    static IRandomSampler C(double v) => new ConstantRandomProcess(v);

    sealed class Rig
    {
        public SimClock Clock;
        public SequencingStation St;
        public Emitter Orders, Carriers, Pieces;
        public ItemsQueue Stock;
        public SimList List;
        public ListLink ListLink;
        public Collector Out;
        public Sink OrderSink;
        public List<Element> All = new List<Element>();

        public void Start()
        {
            foreach (var e in All) e.Start();
            ListLink?.Reset(); List?.Reset();
            ListLink?.Start();
        }
    }

    /// <summary>Carriers → station → collector; orders → port "orders" (served → a sink); pieces → queue → list → port
    /// "pieces" (on demand) unless <paramref name="piecesByLink"/>.</summary>
    static Rig Build(Action<SequencingStation> setup, bool piecesByLink = false, bool orderSink = true)
    {
        var r = new Rig { Clock = new SimClock(1) };
        r.St = new SequencingStation("Seq", r.Clock);
        setup?.Invoke(r.St);
        r.Orders = new Emitter(r.Clock, "Orders");
        r.Carriers = new Emitter(r.Clock, "Carriers");
        r.Pieces = new Emitter(r.Clock, "Pieces");
        r.Out = new Collector(r.Clock);
        GeneralLink.CreateLink(r.Carriers, new List<Element> { r.St });
        GeneralLink.CreateLink(r.St, new List<Element> { r.Out });
        GeneralLink.CreateLink(r.Orders, new List<Element> { r.St.GetPort(SequencingStation.PortOrders) });
        if (orderSink)
        {
            r.OrderSink = new Sink("OrderSink", r.Clock);
            GeneralLink.CreateLink(r.St.GetPort(SequencingStation.PortOrders), new List<Element> { r.OrderSink });
            r.All.Add(r.OrderSink);
        }
        if (piecesByLink)
            GeneralLink.CreateLink(r.Pieces, new List<Element> { r.St.GetPort(SequencingStation.PortPieces) });
        else
        {
            r.Stock = new ItemsQueue(1000, "Stock", r.Clock);
            GeneralLink.CreateLink(r.Pieces, new List<Element> { r.Stock });
            r.List = new SimList("stock", r.Clock);
            r.ListLink = new ListLink(r.List);
            r.ListLink.AddOrigin(r.Stock);
            r.ListLink.AddPuller(r.St.GetPort(SequencingStation.PortPieces));
            r.All.Add(r.Stock);
        }
        r.All.AddRange(new Element[] { r.St, r.Orders, r.Carriers, r.Pieces, r.Out });
        r.Start();
        return r;
    }

    static (string, string)[] Order(int vehicle, string reference, double callOff)
    {
        var l = new List<(string, string)> { ("vehicle", vehicle.ToString()), ("callOffTime", callOff.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        if (reference != null) l.Add(("reference", reference));
        return l.ToArray();
    }

    static Dictionary<string, double> Metrics(IElementMetrics e)
    {
        var d = new Dictionary<string, double>();
        e.ReportMetrics((k, v) => d[k] = v);
        return d;
    }

    // ── tests ────────────────────────────────────────────────────────────

    static void TestTimeline()
    {
        var r = Build(s =>
        {
            s.CarrierCapacity = 3;
            s.PrepareTime = C(5); s.PickTime = C(2); s.PlaceTime = C(1); s.GapTime = C(4); s.CloseTime = C(3);
        });
        r.Carriers.At(0, "Wagon");
        r.Pieces.At(0, "Part", ("sku", "A"));
        r.Pieces.At(0, "Part", ("sku", "B"));
        r.Orders.At(0, "Order", Order(1, "A", 0));
        r.Orders.At(10, "Order", Order(2, "B", 10));
        r.Orders.At(20, "Order", Order(3, null, 20));   // no reference: an empty slot
        r.Clock.AdvanceClock(100);

        // Lot of 3 ready at 20 → prepare 20–25 → A taken at 25 (list pull, no wait), 2 + 1 → placed 28;
        // B placed 31; gap 31–35; close 35–38.
        Check(r.Out.Got.Count == 1, "one carrier out");
        Near(r.Out.At[0], 38, "carrier leaves");
        Item w = r.Out.Got[0];
        Check(w.subItems != null && w.subItems.Count == 2, "two pieces in the carrier");
        Near(w.subItems[0].GetLabelOrDefault("slot", -1), 0, "piece A in slot 0");
        Near(w.subItems[1].GetLabelOrDefault("slot", -1), 1, "piece B in slot 1");
        Check(w.subItems[0].GetLabelText("sku") == "A" && w.subItems[1].GetLabelText("sku") == "B", "pieces in sequence");
        Near(w.subItems[1].GetLabelOrDefault("vehicle", -1), 2, "order labels copied to the piece");
        Near(w.GetLabelOrDefault("firstVehicle", -1), 1, "firstVehicle");
        Near(w.GetLabelOrDefault("lastVehicle", -1), 3, "lastVehicle");
        Near(w.GetLabelOrDefault("pieces", -1), 2, "pieces label");
        Near(w.GetLabelOrDefault("gaps", -1), 1, "gaps label");
        Near(w.GetLabelOrDefault("closeTime", -1), 38, "closeTime label");

        var m = Metrics(r.St);
        Near(m[ResultMetrics.Carriers], 1, "carriers");
        Near(m[ResultMetrics.Pieces], 2, "pieces");
        Near(m[ResultMetrics.Gaps], 1, "gaps");
        Near(m[ResultMetrics.StockOuts], 0, "no stock-out (list hand-over is immediate)");
        Near(m[ResultMetrics.OrderWaitAvg], (28 + 21 + 15) / 3.0, "order wait avg (28, 21, 15)");
        Near(m[ResultMetrics.OrderWaitMax], 28, "order wait max");
        Near(m[ResultMetrics.LotWaitAvg], 20, "lot wait (oldest order 0 → start 20)");

        // Statistics through the links: carriers, served orders, consumed pieces.
        Near(r.St.GetStatsInputCount(), 1, "station input (carriers)");
        Near(r.St.GetStatsOutputCount(), 1, "station output");
        Near(r.St.GetStatsStayTimeMean(), 38, "carrier stay");
        Element op = r.St.OrderPort, pp = r.St.PiecePort;
        Near(op.GetStatsInputCount(), 3, "order port in"); Near(op.GetStatsOutputCount(), 3, "order port out");
        Near(op.GetStatsContentCurrent(), 0, "order port empty");
        Near(op.GetStatsStayTimeMean(), (28 + 21 + 15) / 3.0, "order stay = order wait");
        Near(r.OrderSink.GetStatsInputCount(), 3, "served orders reach the sink");
        Near(pp.GetStatsInputCount(), 2, "piece port in"); Near(pp.GetStatsOutputCount(), 2, "piece port out");
        Near(pp.GetStatsContentCurrent(), 0, "piece port empty");
        Near(r.Stock.GetStatsOutputCount(), 2, "stock queue out");
        Near(r.St.GetTimeInState(SequencingStation.WAITING), 20, "WAITING for the lot 0–20");
        Near(r.St.GetTimeInState(ElementState.PROCESSING), 18, "PROCESSING 20–38");
        Near(r.St.GetTimeInState(ElementState.IDLE), 62, "IDLE after 38");
    }

    static void TestStockOut()
    {
        var r = Build(s => { s.CarrierCapacity = 1; s.PickTime = C(2); s.PlaceTime = C(1); });
        r.Carriers.At(0, "Wagon");
        r.Orders.At(0, "Order", Order(1, "C", 0));
        r.Pieces.At(30, "Part", ("sku", "D"));
        r.Pieces.At(50, "Part", ("sku", "C"));
        r.Clock.AdvanceClock(40);
        var m40 = Metrics(r.St);
        Near(m40[ResultMetrics.StockOuts], 1, "a stock-out under way counts");
        Near(m40[ResultMetrics.StockOutTime], 40, "its time so far");
        Check(r.St.CurrentState == SequencingStation.WAITING_FOR_MATERIAL, "WAITING_FOR_MATERIAL");
        r.Clock.AdvanceClock(100);
        // C arrives at 50 → 2 + 1 → placed 53, close 0 → leaves 53.
        Near(r.Out.At.Single(), 53, "carrier leaves");
        var m = Metrics(r.St);
        Near(m[ResultMetrics.StockOuts], 1, "one stock-out");
        Near(m[ResultMetrics.StockOutTime], 50, "stock-out time 0–50");
        Near(r.St.GetTimeInState(SequencingStation.WAITING_FOR_MATERIAL), 50, "WAITING_FOR_MATERIAL 50");
        Near(m[ResultMetrics.OrderWaitAvg], 53, "order wait");
        Check(r.Stock.GetQueueLength() == 1 && r.List.Count == 1, "piece D stays in stock (listed)");
    }

    static void TestBufferByLink()
    {
        var r = Build(s => { s.CarrierCapacity = 2; s.PickTime = C(1); s.PiecePort.Capacity = -1; }, piecesByLink: true, orderSink: false);
        r.Carriers.At(0, "Wagon");
        r.Pieces.At(0, "Part", ("sku", "B"));
        r.Pieces.At(0, "Part", ("sku", "A"));
        r.Pieces.At(0, "Part", ("sku", "Z"));
        r.Orders.At(0, "Order", Order(1, "A", 0));
        r.Orders.At(0, "Order", Order(2, "B", 0));
        r.Clock.AdvanceClock(10);
        Near(r.Out.At.Single(), 2, "two picks of 1");
        var w = r.Out.Got[0];
        Check(w.subItems[0].GetLabelText("sku") == "A" && w.subItems[1].GetLabelText("sku") == "B", "drawn by reference, in order sequence");
        Check(r.St.PiecePort.Count == 1, "Z stays in the buffer");
        Element op = r.St.OrderPort;
        Near(op.GetStatsOutputCount(), 2, "served orders end at the port (unwired: ConsumeLink)");
        Near(op.GetStatsContentCurrent(), 0, "order port content back to 0");
    }

    static void TestBlocked()
    {
        var r = Build(s => { s.CarrierCapacity = 1; s.CloseTime = C(4); });
        var d = new ResourceDispatcher("ops", r.Clock);
        var opr = new TaskExecutor("Op", r.Clock); opr.Start(); d.Add(opr); d.Start();
        r.St.Resources = new StationResources(d) { Release = ProcessRelease.ItemLeaves };
        r.Out.Open = false;
        r.Carriers.At(0, "Wagon");
        r.Orders.At(0, "Order", Order(1, null, 0));
        r.Clock.ScheduleEvent(new ActionEvent(() => r.Out.OpenNow()), 100);
        r.Clock.AdvanceClock(200);
        Near(r.Out.At.Single(), 100, "sent when the output opens");
        Near(r.St.GetTimeInState(ElementState.BLOCKED), 96, "BLOCKED 4–100");
        Near(opr.GetTimeInState(ResourceStates.HELD), 96, "operator HELD while blocked");
        Check(opr.IsAvailable, "operator free after the carrier left");
    }

    static void TestMaxWait()
    {
        var r = Build(s => { s.CarrierCapacity = 3; s.OldestOrderMaxWait = 15; s.WaitForFullCarrier = false; s.PickTime = C(1); });
        r.Carriers.At(0, "Wagon");
        r.Pieces.At(0, "Part", ("sku", "A"));
        r.Orders.At(0, "Order", Order(1, "A", 0));
        r.Clock.AdvanceClock(100);
        // Oldest order waited 15 at t = 15 → start, A picked 15–16, no more orders → close → leaves 16.
        Near(r.Out.At.Single(), 16, "partial carrier leaves");
        Near(r.Out.Got[0].GetLabelOrDefault("pieces", -1), 1, "one piece");
        Near(Metrics(r.St)[ResultMetrics.LotWaitAvg], 15, "lot wait = max wait");
    }

    static void TestOldestOrderRule()
    {
        double[] Run3(bool oldest)
        {
            var clock = new SimClock(1);
            var d = new ResourceDispatcher("ops", clock);
            if (oldest) d.RequestOrder = ListQuery.All.OrderBy("oldestItemTime");
            var opr = new TaskExecutor("Op", clock);
            var stations = new List<SequencingStation>();
            var outs = new List<Collector>();
            var all = new List<Element>();
            Emitter carriers = new Emitter(clock, "Carriers");
            var orderEmitters = new List<Emitter>();
            for (int i = 0; i < 3; i++)
            {
                var st = new SequencingStation("S" + (i + 1), clock) { CarrierCapacity = 1, PrepareTime = C(10) };
                st.Resources = new StationResources(d);
                var cs = new Emitter(clock, "C" + i); var os = new Emitter(clock, "O" + i); var o = new Collector(clock, "Out" + i);
                GeneralLink.CreateLink(cs, new List<Element> { st });
                GeneralLink.CreateLink(os, new List<Element> { st.GetPort(SequencingStation.PortOrders) });
                GeneralLink.CreateLink(st, new List<Element> { o });
                stations.Add(st); outs.Add(o); orderEmitters.Add(os);
                all.AddRange(new Element[] { st, cs, os, o });
                cs.At(0, "Wagon");
            }
            foreach (var e in all) e.Start();
            opr.Start(); d.Add(opr); d.Start();
            orderEmitters[0].At(0, "Order", Order(1, null, 0));
            orderEmitters[1].At(2, "Order", Order(2, null, 2));
            orderEmitters[2].At(3, "Order", Order(3, null, 0.5));   // arrives last, but called off first among the waiting
            clock.AdvanceClock(100);
            return outs.Select(o => o.At.Single()).ToArray();
        }
        var fifo = Run3(false);
        var old = Run3(true);
        // S1 holds the operator 0–10. Waiting at 10: S2 (asked at 2, oldest order 2), S3 (asked at 3, oldest 0.5).
        Check(fifo.SequenceEqual(new double[] { 10, 20, 30 }), "FIFO: S2 then S3 — " + string.Join(",", fifo));
        Check(old.SequenceEqual(new double[] { 10, 30, 20 }), "oldestItemTime: S3 then S2 — " + string.Join(",", old));
    }

    static void TestPieceScope()
    {
        var r = Build(s => { s.CarrierCapacity = 2; s.PrepareTime = C(3); s.PickTime = C(2); s.PlaceTime = C(1); s.OperatorScope = SequencingOperatorScope.Piece; });
        var d = new ResourceDispatcher("ops", r.Clock);
        var opr = new TaskExecutor("Op", r.Clock); opr.Start(); d.Add(opr); d.Start();
        r.St.Resources = new StationResources(d);
        r.Carriers.At(0, "Wagon");
        r.Pieces.At(0, "Part", ("sku", "A")); r.Pieces.At(0, "Part", ("sku", "A"));
        r.Orders.At(0, "Order", Order(1, "A", 0)); r.Orders.At(0, "Order", Order(2, "A", 0));
        r.Clock.AdvanceClock(50);
        Near(r.Out.At.Single(), 9, "3 + 2 × 3");
        Near(opr.TasksDone, 2, "one operator task per piece");
        Near(opr.GetTimeInState(ElementState.PROCESSING), 6, "operator busy only for the pieces");
    }

    static void TestExpressions()
    {
        var r = Build(s =>
        {
            s.CarrierCapacity = 3; s.PickTime = C(1);
            s.ExtraTime = new SimuLean.Expressions.ExpressionSampler("sku == 'X' ? 35 : 0");
            s.Applies = SimuLean.Expressions.Expression.Parse("item.reference != 'NONE'");
        });
        r.Carriers.At(0, "Wagon");
        r.Pieces.At(0, "Part", ("sku", "X")); r.Pieces.At(0, "Part", ("sku", "Y"));
        r.Orders.At(0, "Order", Order(1, "X", 0)); r.Orders.At(0, "Order", Order(2, "NONE", 0)); r.Orders.At(0, "Order", Order(3, "Y", 0));
        r.Clock.AdvanceClock(100);
        // X: 1 + 35; NONE: gap 0; Y: 1 → leaves 37.
        Near(r.Out.At.Single(), 37, "extra time for X only");
        Near(Metrics(r.St)[ResultMetrics.Gaps], 1, "NONE does not apply");
    }

    static void TestFactory()
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 7, MaxSimulationTime = 1000 };
        ElementConfig Sched(string id, params (double t, string name, (string, string)[] labels)[] rows)
        {
            var table = new Dictionary<string, List<string>> { ["Time"] = new List<string>(), ["Name"] = new List<string>(), ["Q"] = new List<string>() };
            foreach (var row in rows) foreach (var l in row.labels) if (!table.ContainsKey(l.Item1)) table[l.Item1] = new List<string>();
            foreach (var row in rows)
            {
                table["Time"].Add(row.t.ToString(System.Globalization.CultureInfo.InvariantCulture));
                table["Name"].Add(row.name); table["Q"].Add("1");
                foreach (var col in table.Keys.Where(k => k != "Time" && k != "Name" && k != "Q"))
                    table[col].Add(row.labels.FirstOrDefault(l => l.Item1 == col).Item2 ?? "");
            }
            var e = new ElementConfig(id, "Source", id);
            e.Set("mode", "Schedule");
            e.Set("arrivalsTable", table);
            return e;
        }
        c.Elements.Add(Sched("orders", (0, "Order", Order(1, "A", 0)), (10, "Order", Order(2, "B", 10))));
        c.Elements.Add(Sched("pieces", (0, "Part", new[] { ("sku", "A") }), (0, "Part", new[] { ("sku", "B") })));
        c.Elements.Add(Sched("wagons", (0, "Wagon", new (string, string)[0])));
        var q = new ElementConfig("stock", "Queue", "stock"); q.Set("capacity", 100); c.Elements.Add(q);
        var st = new ElementConfig("seq", SequencingStationBuilder.TypeName, "seq");
        st.Settings = new SequencingStationSettings { CarrierCapacity = 2, PickTime = SamplerSettings.Constant(2), CloseTime = SamplerSettings.Constant(1) };
        c.Elements.Add(st);
        c.Elements.Add(new ElementConfig("out", "Sink", "out"));
        c.Elements.Add(new ElementConfig("served", "Sink", "served"));
        c.Lists.Add(new ListConfig { Id = "stockList", Name = "stockList" });
        c.Connections.Add(new ConnectionConfig("wagons", "seq"));
        c.Connections.Add(new ConnectionConfig("seq", "out"));
        var pi = new ConnectionConfig("orders", "seq", "PortInput"); pi.Port = "orders"; c.Connections.Add(pi);
        var po = new ConnectionConfig("seq", "served", "PortOutput"); po.Port = "orders"; c.Connections.Add(po);
        c.Connections.Add(new ConnectionConfig("pieces", "stock"));
        c.Connections.Add(new ConnectionConfig("stock", "stockList", "PushToList"));
        var pull = new ConnectionConfig("stockList", "seq", "PullFromList"); pull.Query = ""; pull.Port = "pieces";
        c.Connections.Add(pull);

        var clock = new SimClock(7);
        var f = new SimuLean.Headless.HeadlessModelFactory(clock);
        var els = f.BuildModel(c);
        var seq = (SequencingStation)els["seq"];
        Check(seq.CarrierCapacity == 2 && seq.PickTime != null && seq.PlaceTime == null, "settings decoded");
        f.StartModel();
        clock.AdvanceClock(1000);
        // Lot of 2 at 10: A 10–12, B 12–14, close 14–15.
        Near(els["out"].GetStatsInputCount(), 1, "carrier out");
        Near(els["out"].GetStatsStayTimeMean() + 0, 0, "sink");
        Near(seq.GetStatsStayTimeMean(), 15, "carrier stay 0–15");
        Near(els["served"].GetStatsInputCount(), 2, "served orders to the sink (PortOutput)");
        Near(seq.PiecesPlaced, 2, "pieces pulled from the list (PullFromList, port pieces)");

        // Round trip of the settings.
        var e2 = new ElementConfig("x", SequencingStationBuilder.TypeName, "x");
        e2.Settings = new SequencingStationSettings
        {
            CarrierCapacity = 5, LotSize = 3, OldestOrderMaxWait = 60, OperatorScope = SequencingOperatorScope.Piece, PieceStock = -1,
            Applies = "reference != ''", GapTime = SamplerSettings.Uniform(1, 2),
        };
        var b = SequencingStationBuilder.Build(e2, new SimClock(1), null);
        Check(b.CarrierCapacity == 5 && b.LotSize == 3 && b.OldestOrderMaxWait == 60 && b.OperatorScope == SequencingOperatorScope.Piece
              && b.PiecePort.Capacity == -1 && b.Applies != null && b.GapTime != null && b.PrepareTime == null, "round trip");

        // the same settings through the factory (its max-wait pass read the key 'maxWait' of every element)
        var c2 = new SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 10 };
        c2.Elements.Add(e2);
        Check(((SequencingStation)new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(c2)["x"]).OldestOrderMaxWait == 60,
              "the factory builds a sequencing station with a max wait of the oldest order");

        var results = RunResults.Capture(f, "seq");
        var so = results.Find(ResultScope.Element, "seq");
        Check(so != null && so.TryGet(ResultMetrics.Carriers, out double carriers) && carriers == 1, "RunResults reports the station's own metrics");
    }
}
