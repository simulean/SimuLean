// Standalone runtime tests of the line-supply elements: the Unpacker (pieces one
// by one, empties port, link statistics, capacity, change time, blocking, stops), named ports (IPortedElement, the
// PortInput / PortOutput connections of the headless factory) and elements' own metrics in the results; the
// Manufacturing pack's CallOffPoint and PacedLine (+ stations, call-off families, DiscreteChoice, RecordCodec). Expected
// times are computed by hand in the comments. Run: dotnet run (from StandaloneTests/linesupplytests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Headless;
using SimuLean.Results;
using SimuLean.Serialization;
using SimuLean.Stochastics.Distributions;
using SimuLean.Stochastics.Streams;
using SimuLean.Testing;

static class Program
{
    static int passed, failed;

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    static void Near(double actual, double expected, string what, double tol = 1e-9)
        => Check(Math.Abs(actual - expected) <= tol, $"{what}: expected {expected}, got {actual}");

    static int Main()
    {
        Run("DiscreteChoice: parse, probabilities, frequencies, errors", TestChoice);
        Run("RecordCodec: escaped fields round trip", TestRecordCodec);
        Run("Line-side unpacker: pieces one by one, the empty carrier after them (no empties port), link statistics", TestUnpackerBasic);
        Run("Line-side unpacker: LIFO order and carrier labels copied", TestUnpackerLifoLabels);
        Run("Line-side unpacker: carrier capacity, change time, empties keep their place", TestUnpackerCapacityChange);
        Run("Line-side unpacker: pull-driven (blocked piece), stop pauses the work", TestUnpackerBlockedStop);
        Run("Line-side unpacker: the change time before the first carrier too only with changeTimeFirst", TestUnpackerChangeTimeFirst);
        Run("Separator unpack: all together after the process time; BLOCKED until downstream takes them", TestSeparatorAllTogether);
        Run("Separator split: N items (type and labels), process time, link statistics", TestSeparatorSplit);
        Run("Separator: the inverse of a Combiner in batch mode (reference model combiner_separator)", TestCombinerSeparator);
        Run("Builder + headless factory: the empties port wired and reported, own metrics, determinism", TestFactoryPorts);
        Run("Port connections: one link shared by a port's outputs; unknown ports fail the build", TestPortConnections);
        Run("PacedLine: indexing, exits, stay time, empty pitches", TestLineBasic);
        Run("PacedLine: starvation stops the line, indexes when the part arrives", TestLineStarvation);
        Run("PacedLine: blocked output, downtime pauses the takt", TestLineBlockedDowntime);
        Run("PacedLine: q = 2, condition, call-off at the line, references, mismatches", TestLineCallOffQuantity);
        Run("PacedLine: optional positions ('-' = no part) are not fitted and do not starve the line", TestLineOptionalPosition);
        Run("CallOffPoint + buffer + line: requisition time = buffer + positions x takt", TestCallOffPointRequisition);
        Run("Builders + headless factory: line ports (PortInput), results, determinism, codecs", TestLineBuildersFactory);
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    // ── harness ──────────────────────────────────────────────────────────────

    /// <summary>Emits items at given times; holds refused items (FIFO) and retries on Unblock.</summary>
    sealed class Feeder : Element, IEvent
    {
        readonly Queue<Item> held = new Queue<Item>();
        readonly double[] times;
        readonly Func<int, Item> make;
        int next;
        public Feeder(SimClock c, string name, double[] times, Func<int, Item> make) : base(name, c)
        { this.times = times; this.make = make; StatsCollector.TracksContent = false; }
        public void Begin() { if (times.Length > 0) simClock.ScheduleEvent(this, times[0] - simClock.GetSimulationTime()); }
        public void Execute()
        {
            var it = make(next + 1);
            if (held.Count > 0 || !GetOutput().SendItem(it, this)) held.Enqueue(it);
            next++;
            if (next < times.Length) simClock.ScheduleEvent(this, times[next] - simClock.GetSimulationTime());
        }
        public override bool Unblock()
        {
            bool any = false;
            while (held.Count > 0 && GetOutput().SendItem(held.Peek(), this)) { held.Dequeue(); any = true; }
            return any;
        }
        public int Held => held.Count;
        public override int GetQueueLength() => held.Count;
        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item i) => false;
        public override bool Receive(Item i) => false;
    }

    /// <summary>Turns each order into a part (labels copied; optional tamper) and sends it on, holding refused parts.</summary>
    sealed class Echo : Element
    {
        readonly Queue<Item> held = new Queue<Item>();
        public Func<Item, Item> Transform;
        public readonly List<Item> Orders = new List<Item>();
        int n;
        public Echo(SimClock c) : base("Echo", c) { }
        public override bool Receive(Item order)
        {
            Orders.Add(order);
            var part = new Item(simClock.GetSimulationTime());
            part.SetId("Part", ++n, 0);
            part.CopyLabelsFrom(order);
            if (Transform != null) part = Transform(part);
            if (part == null) return true;   // no part for this order
            held.Enqueue(part);
            SendHeld();
            return true;
        }
        void SendHeld() { while (held.Count > 0 && GetOutput() != null && GetOutput().SendItem(held.Peek(), this)) held.Dequeue(); }
        public override bool Unblock() { int before = held.Count; SendHeld(); return held.Count < before; }
        public override int GetQueueLength() => held.Count;
        public override int GetFreeCapacity() => int.MaxValue;
        public override bool CheckAvailability(Item i) => true;
    }

    /// <summary>Records arrival times; optionally refuses everything.</summary>
    sealed class Collector : Element
    {
        public readonly List<(double t, Item item)> Got = new List<(double, Item)>();
        public bool Refuse;
        public Collector(SimClock c, string name = "Out") : base(name, c) { }
        public override bool Receive(Item i) { if (Refuse) return false; Got.Add((simClock.GetSimulationTime(), i)); return true; }
        public override bool CheckAvailability(Item i) => !Refuse;
        public override bool Unblock() => false;
        public override int GetQueueLength() => 0;
        public override int GetFreeCapacity() => Refuse ? 0 : int.MaxValue;
    }

    static Item Carrier(int id, int pieces, double t = 0)
    {
        var c = new Item(t);
        c.SetId("Wagon", id, 0);
        for (int k = 1; k <= pieces; k++)
        {
            var p = new Item(t);
            p.SetId("Piece", id * 100 + k, 0);
            p.SetLabelValue("k", k);
            c.AddItem(p);
        }
        return c;
    }

    static Item Vehicle(int id, string model = null)
    {
        var v = new Item(0);
        v.SetId("Car", id, 0);
        if (model != null) v.SetLabelText("model", model);
        return v;
    }

    static void Link(Element from, Element to) => GeneralLink.CreateLink(from, new List<Element> { to });

    static IRandomSampler Const(double v) => new ConstantRandomProcess(v);

    // ── tests ────────────────────────────────────────────────────────────────

    static void TestChoice()
    {
        var c = DiscreteChoice.Parse("R1:0.28|R2:0.24| R3 : 0,48");
        Check(c.Values.SequenceEqual(new[] { "R1", "R2", "R3" }), "values in table order");
        Near(c.Probability(0), 0.28, "p(R1)"); Near(c.Probability(2), 0.48, "p(R3), decimal comma");
        var w = DiscreteChoice.Parse("A:2|B:6");   // normalised
        Near(w.Probability(0), 0.25, "weights normalised");
        Near(DiscreteChoice.Parse("Solo").Probability(0), 1, "a value without a weight");
        c.SetRandomEngine(new DefaultRandomEngine(7));
        var counts = new Dictionary<string, int> { ["R1"] = 0, ["R2"] = 0, ["R3"] = 0 };
        for (int i = 0; i < 20000; i++) counts[c.Sample()]++;
        Near(counts["R1"] / 20000.0, 0.28, "frequency R1", 0.015);
        Near(counts["R3"] / 20000.0, 0.48, "frequency R3", 0.015);
        // same seed → same draws
        var a = DiscreteChoice.Parse("x:1|y:1", new DefaultRandomEngine(3));
        var b = DiscreteChoice.Parse("x:1|y:1", new DefaultRandomEngine(3));
        bool same = true;
        for (int i = 0; i < 50; i++) same &= a.Sample() == b.Sample();
        Check(same, "seeded draws reproducible");
        Check(Throws(() => DiscreteChoice.Parse("")), "empty table fails");
        Check(Throws(() => DiscreteChoice.Parse("a:x")), "bad weight fails");
        Check(Throws(() => DiscreteChoice.Parse("a:0|b:0")), "zero total fails");
        Check(DiscreteChoice.Parse(c.ToSpec()).Values.Count == 3, "ToSpec parses back");
    }

    static void TestRecordCodec()
    {
        var fields = new[] { "a|b", "c\\d", "", "item.model == \"K9\" || item.x > 1" };
        var back = RecordCodec.Split(RecordCodec.Join(fields));
        Check(back.SequenceEqual(fields), "fields with | and \\ survive");
        var nested = RecordCodec.Join(new[] { "n", RecordCodec.Join(new[] { "A:1|B:2", "C:1" }) });
        var outer = RecordCodec.Split(nested);
        Check(RecordCodec.Split(outer[1]).SequenceEqual(new[] { "A:1|B:2", "C:1" }), "nested records");
    }


    static void TestUnpackerBasic()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, "In", new[] { 0.0 }, i => Carrier(i, 3));
        var u = new LineSideUnpacker("U", clock, 2) { PieceTime = Const(1) };
        var outp = new Collector(clock);
        Link(feeder, u); Link(u, outp);
        u.Start(); outp.Start(); feeder.Start(); feeder.Begin();
        clock.AdvanceClock(10);
        // pieces ready at 1, 2, 3 (one piece time each, sent at once); the empty carrier leaves after them at 3, through
        // the output (the empties port is not wired)
        Check(outp.Got.Select(g => g.t).SequenceEqual(new[] { 1.0, 2.0, 3.0, 3.0 }), "pieces at 1, 2, 3, then the container: " + string.Join(",", outp.Got.Select(g => g.t)));
        Check(outp.Got.Take(3).Select(g => (int)g.item.GetLabelOrDefault("k")).SequenceEqual(new[] { 1, 2, 3 }), "FIFO order");
        Check(outp.Got[3].item.Name == "Wagon", "the empty container last, through the output");
        Check(u.CarriersEmptied == 1 && u.Released == 3, "counters");
        Check(u.EmptiesPort.DiscardedCount == 0 && u.EmptiesPort.SentCount == 0, "the unwired empties port is not used");
        var s = u.StatsCollector;
        Near(s.VarInput.Value, 4, "input = carrier + 3 pieces");
        Near(s.VarOutput.Value, 4, "output = 3 pieces + carrier");
        Near(s.VarContent.Value, 0, "content back to 0");
        Near(s.VarContent.Max, 4, "content peak = carrier + 3 pieces");
        Near(s.VarStayTime.Average, (1 + 2 + 3 + 3) / 4.0, "stay times: pieces 1, 2, 3, carrier 3");
        // time-weighted content over [0, 10]: 4 on [0,1], 3 on [1,2], 2 on [2,3] → 9 / 10
        Near(s.ContentAvgTimeWeighted, 0.9, "time-weighted content");
        Check(u.GetQueueLength() == 0 && u.CurrentState == ElementState.IDLE, "idle and empty at the end");
        // pieces held (carriers not counted): 3 on [0,1], 2 on [1,2], 1 on [2,3], 0 after → 6 / 10
        Near(u.PiecesHeld.GetTimeWeightedAverage(10), 0.6, "pieces held, time-weighted");
        Near(u.PiecesHeld.Max, 3, "pieces held max");
        Near(u.PiecesHeld.Min, 0, "pieces held min");
        Near(u.EmptiesPort.StatsCollector.VarOutput.Value, 0, "nothing through the port");
    }

    static void TestSeparatorAllTogether()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, "In", new[] { 0.0, 0.0 }, i => Carrier(i, 3));
        var u = new Separator("U", clock, 1) { ProcessTime = Const(2) };
        var outp = new Collector(clock);
        var empties = new Collector(clock, "Empties");
        Link(feeder, u); Link(u, outp); Link(u.EmptiesPort, empties);
        u.Start(); outp.Start(); empties.Start(); feeder.Start(); feeder.Begin();
        clock.AdvanceClock(10);
        // C1: process 0→2, its 3 pieces at 2, empty at 2 → C2 enters at 2: process 2→4, pieces at 4
        Check(outp.Got.Select(g => g.t).SequenceEqual(new[] { 2.0, 2.0, 2.0, 4.0, 4.0, 4.0 }), "all together at 2 and 4: " + string.Join(",", outp.Got.Select(g => g.t)));
        Check(empties.Got.Select(g => g.t).SequenceEqual(new[] { 2.0, 4.0 }), "the empties right after their contents");
        Near(u.GetTimeInState(ElementState.PROCESSING), 4, "two process times");
        var um = new Dictionary<string, double>();
        u.ReportMetrics((k, v) => um[k] = v);
        Check(um.ContainsKey(ResultMetrics.CarriersEmptied) && !um.ContainsKey(ResultMetrics.PiecesAvg), "the Core separator reports no line-side stock");
        Check(u.Released == 6 && u.CarriersEmptied == 2, "counters");
        Check(!clock.Context.ModelWarnings.Any(w => w.Contains("with nothing in it")), "full carriers: no warning");

        // an empty carrier (a Combiner without batchMode detaches what it packs) leaves at once: warned once, naming batchMode
        var clock0 = new SimClock(1);
        var f0 = new Feeder(clock0, "In", new[] { 0.0, 1.0 }, i => Carrier(i, 0));
        var u0 = new Separator("U", clock0, 1) { ProcessTime = Const(1) };
        var out0 = new Collector(clock0);
        Link(f0, u0); Link(u0, out0);
        u0.Start(); out0.Start(); f0.Start(); f0.Begin();
        clock0.AdvanceClock(10);
        var warned = clock0.Context.ModelWarnings.Where(w => w.Contains("with nothing in it")).ToList();
        Check(warned.Count == 1 && warned[0].Contains("batchMode"), "an empty carrier is warned once, naming batchMode: " + string.Join(" | ", warned));

        // downstream holds one piece: the rest wait, BLOCKED
        var clock2 = new SimClock(1);
        var f2 = new Feeder(clock2, "In", new[] { 0.0 }, i => Carrier(i, 3));
        var u2 = new Separator("U", clock2, 1) { ProcessTime = Const(1) };
        var q = new ItemsQueue(1, "Q", clock2);
        Link(f2, u2); Link(u2, q);
        u2.Start(); q.Start(); f2.Start(); f2.Begin();
        clock2.AdvanceClock(10);
        Check(u2.Released == 1 && u2.CurrentState == ElementState.BLOCKED, "one piece out, BLOCKED with the rest");
        Check(u2.GetQueueLength() == 3, "content = container + 2 pieces");
        Near(u2.StatsCollector.VarContent.Value, 3, "content statistic agrees");
        Near(u2.GetTimeInState(ElementState.BLOCKED), 9, "blocked from 1 to 10");
    }

    static void TestCombinerSeparator()
    {
        var r = RunConfig(ReferenceModels.CombinerSeparator(), out var factory);
        var sep = (Separator)factory.GetElement("sep");
        Check(sep.Mode == SeparatorMode.Unpack && sep.ProcessTime != null, "separator built");
        double mains = r.Find(ResultScope.Element, "mains")[ResultMetrics.Input];
        double parts = r.Find(ResultScope.Element, "parts")[ResultMetrics.Input];
        double emptied = r.Find(ResultScope.Element, "sep")[ResultMetrics.CarriersEmptied];
        Check(mains > 60, $"the main items came back ({mains})");
        Near(emptied, mains, "every container emptied reached its sink");
        Near(parts, 2 * mains, "two parts per main item came back out");
    }

    static void TestSeparatorSplit()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, "In", new[] { 0.0, 0.0 }, i =>
        {
            var it = new Item(0);
            it.SetId("Order", i, 0);
            it.SetLabelText("sku", "S" + i);
            return it;
        });
        var u = new Separator("U", clock, 1, SeparatorMode.Split) { ProcessTime = Const(1), SplitQuantity = Const(3) };
        var outp = new Collector(clock);
        int splitEvents = 0;
        u.ItemSplit += (x, original, copy) => splitEvents++;
        Link(feeder, u); Link(u, outp);
        u.Start(); outp.Start(); feeder.Start(); feeder.Begin();
        clock.AdvanceClock(10);
        // O1: process 0→1, 3 items at 1; O2 enters at 1: process →2, 3 items at 2
        Check(outp.Got.Select(g => g.t).SequenceEqual(new[] { 1.0, 1.0, 1.0, 2.0, 2.0, 2.0 }), "3 items each, at 1 and 2: " + string.Join(",", outp.Got.Select(g => g.t)));
        Check(outp.Got.All(g => g.item.Name == "Order"), "copies keep the type");
        Check(outp.Got.Take(3).All(g => g.item.GetLabelText("sku") == outp.Got[0].item.GetLabelText("sku")) &&
              outp.Got.Skip(3).All(g => g.item.GetLabelText("sku") == outp.Got[3].item.GetLabelText("sku")) &&
              outp.Got[0].item.GetLabelText("sku") != outp.Got[3].item.GetLabelText("sku"), "copies keep the labels");
        Check(outp.Got.Select(g => g.item).Distinct().Count() == 6, "six distinct items");
        Check(u.SplitCopies == 4 && splitEvents == 4 && u.Released == 6 && u.CarriersEmptied == 0, "counters");
        var s = u.StatsCollector;
        Near(s.VarInput.Value, 6, "input = 2 items + 4 copies");
        Near(s.VarOutput.Value, 6, "output = 6");
        Near(s.VarContent.Value, 0, "content back to 0");
        Check(u.GetQueueLength() == 0 && u.CurrentState == ElementState.IDLE, "idle at the end");
        var m = new Dictionary<string, double>();
        u.ReportMetrics((k, v) => m[k] = v);
        Check(m.ContainsKey(ResultMetrics.SplitCopies) && !m.ContainsKey(ResultMetrics.CarriersEmptied), "split metrics");

        // a quantity of 1 (or none) lets the item through unchanged
        var clock2 = new SimClock(1);
        var f2 = new Feeder(clock2, "In", new[] { 0.0 }, i => { var it = new Item(0); it.SetId("Order", i, 0); return it; });
        var u2 = new Separator("U", clock2, 1, SeparatorMode.Split);
        var o2 = new Collector(clock2);
        Link(f2, u2); Link(u2, o2);
        u2.Start(); o2.Start(); f2.Start(); f2.Begin();
        clock2.AdvanceClock(5);
        Check(o2.Got.Count == 1 && u2.SplitCopies == 0, "no quantity: the item alone");
    }

    static void TestUnpackerLifoLabels()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, "In", new[] { 0.0 }, i =>
        {
            var c = Carrier(i, 3);
            c.SetLabelText("route", "L1"); c.SetLabelValue("k", 99);
            return c;
        });
        var u = new LineSideUnpacker("U", clock, 1) { Order = UnpackOrder.Lifo, CopyCarrierLabels = true };
        var outp = new Collector(clock);
        var empties = new Collector(clock, "Empties");
        Link(feeder, u); Link(u, outp); Link(u.EmptiesPort, empties);
        u.Start(); outp.Start(); empties.Start(); feeder.Start(); feeder.Begin();
        clock.AdvanceClock(5);
        Check(outp.Got.Select(g => (int)g.item.GetLabelOrDefault("k")).SequenceEqual(new[] { 3, 2, 1 }), "LIFO; the piece keeps its own k");
        Check(outp.Got.All(g => g.item.GetLabelText("route") == "L1"), "carrier label copied to every piece");
        Check(outp.Got.All(g => g.t == 0), "no times: all at 0");
        Check(empties.Got.Count == 1 && empties.Got[0].item.Name == "Wagon" && empties.Got[0].item.GetLabelText("route") == "L1"
              && (empties.Got[0].item.subItems == null || empties.Got[0].item.subItems.Count == 0), "the empty carrier keeps type and labels");
    }

    static void TestUnpackerCapacityChange()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, "In", new[] { 0.0, 0.0, 0.0 }, i => Carrier(i, 2));
        var u = new LineSideUnpacker("U", clock, 2) { PieceTime = Const(1), ChangeTime = Const(0.5) };
        var outp = new Collector(clock);
        var empties = new Collector(clock, "Empties");
        Link(feeder, u); Link(u, outp); Link(u.EmptiesPort, empties);
        var carrierIn = new List<double>();
        u.ItemArrived += (x, c) => carrierIn.Add(clock.GetSimulationTime());
        u.Start(); outp.Start(); empties.Start(); feeder.Start(); feeder.Begin();
        clock.AdvanceClock(20);
        // C1, C2 at 0 (capacity 2), C3 held. C1: pieces 1, 2; empty at 2 → C3 enters at 2.
        // C2: change 2→2.5, pieces 3.5, 4.5; empty 4.5. C3: change →5, pieces 6, 7; empty 7.
        Check(carrierIn.SequenceEqual(new[] { 0.0, 0.0, 2.0 }), "carriers enter at 0, 0, 2: " + string.Join(",", carrierIn));
        Check(outp.Got.Select(g => g.t).SequenceEqual(new[] { 1.0, 2.0, 3.5, 4.5, 6.0, 7.0 }), "piece times: " + string.Join(",", outp.Got.Select(g => g.t)));
        Check(empties.Got.Select(g => g.t).SequenceEqual(new[] { 2.0, 4.5, 7.0 }), "empties leave at 2, 4.5, 7");
        Near(u.GetTimeInState(ElementState.SETUP), 1.0, "two changes of 0.5 (SETUP)");

        // Empties keep their place until collected: the empties destination refuses → the unpacker fills up.
        var clock2 = new SimClock(1);
        var f2 = new Feeder(clock2, "In", new[] { 0.0, 0.0, 0.0 }, i => Carrier(i, 1));
        var u2 = new LineSideUnpacker("U", clock2, 2);
        var o2 = new Collector(clock2);
        var e2 = new Collector(clock2, "Empties") { Refuse = true };
        Link(f2, u2); Link(u2, o2); Link(u2.EmptiesPort, e2);
        u2.Start(); o2.Start(); e2.Start(); f2.Start(); f2.Begin();
        clock2.AdvanceClock(5);
        Check(u2.EmptiesWaiting == 2 && f2.Held == 1, $"two empty carriers occupy both places, the third waits ({u2.EmptiesWaiting}, {f2.Held})");
        e2.Refuse = false;
        u2.EmptiesPort.Unblock();   // the tugger collects
        clock2.AdvanceClock(6);
        Check(u2.EmptiesWaiting == 0 && f2.Held == 0 && o2.Got.Count == 3, "after collection the third carrier enters and is unpacked");
        Near(u2.EmptiesPort.StatsCollector.VarStayTime.Max, 5, "the empty waited 5 in the port");
    }

    // 12 pieces in 3 wagons of 4 at 0 (2 held at once), pieceTime 1, changeTime 2. Default: W1 pieces 1..4, W2 change
    // 4→6, pieces 7..10, W3 change 10→12, pieces 13..16. With the switch the first wagon changes too: 0→2, then 3..6,
    // 8 + 1..4 → 9..12, 14 + 1..4 → 15..18.
    static double LastPieceTime(bool changeFirst, out LineSideUnpacker u)
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, "In", new[] { 0.0, 0.0, 0.0 }, i => Carrier(i, 4));
        u = new LineSideUnpacker("U", clock, 2) { PieceTime = Const(1), ChangeTime = Const(2), ChangeTimeFirst = changeFirst };
        var outp = new Collector(clock);
        var empties = new Collector(clock, "Empties");
        Link(feeder, u); Link(u, outp); Link(u.EmptiesPort, empties);
        u.Start(); outp.Start(); empties.Start(); feeder.Start(); feeder.Begin();
        clock.AdvanceClock(40);
        var pieces = outp.Got.Where(g => g.item.Name == "Piece").Select(g => g.t).ToList();
        Check(pieces.Count == 12, $"12 pieces out (changeTimeFirst = {changeFirst}): {pieces.Count}");
        return pieces.Count > 0 ? pieces.Max() : double.NaN;
    }

    static void TestUnpackerChangeTimeFirst()
    {
        Near(LastPieceTime(false, out var u0), 16, "default: the first wagon has no change time, last piece at 16");
        Near(u0.GetTimeInState(ElementState.SETUP), 4, "default: two changes (SETUP)");
        Near(LastPieceTime(true, out var u1), 18, "changeTimeFirst: every wagon changes, last piece at 18");
        Near(u1.GetTimeInState(ElementState.SETUP), 6, "changeTimeFirst: three changes (SETUP)");

        // through the settings and the builder (the one build path): the setting reaches the element; default off
        var cfg = new ElementConfig("unp", "LineSideUnpacker", "unp");
        cfg.Settings = new LineSideUnpackerSettings { ChangeTime = SamplerSettings.Constant(2), ChangeTimeFirst = true };
        var built = FlowBuilders.BuildLineSideUnpacker(cfg, new SimClock(1), null);
        Check(built.ChangeTimeFirst && built.ChangeTime != null, "changeTimeFirst built from the settings");
        Check(!new LineSideUnpackerSettings().ChangeTimeFirst, "changeTimeFirst defaults to false");
    }

    static void TestUnpackerBlockedStop()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, "In", new[] { 0.0 }, i => Carrier(i, 3));
        var u = new LineSideUnpacker("U", clock, 1) { PieceTime = Const(1) };
        var q = new ItemsQueue(1, "Q", clock);   // holds one piece, sends nowhere
        Link(feeder, u); Link(u, q);
        u.Start(); q.Start(); feeder.Start(); feeder.Begin();
        clock.AdvanceClock(10);
        Check(u.Released == 1 && u.ReadyPiece != null, "one piece taken; the next is ready and waits");
        Check(u.CurrentState == ElementState.BLOCKED, "BLOCKED while the ready piece is refused");
        Check(u.GetQueueLength() == 1 + 2, "content = carrier + 2 pieces (one ready)");
        Near(u.StatsCollector.VarContent.Value, 3, "content statistic agrees");

        // Stop: an Immediate stop at 0.5 for 2 pauses the first unpack → piece at 3
        var clock2 = new SimClock(1);
        var f2 = new Feeder(clock2, "In", new[] { 0.0 }, i => Carrier(i, 2));
        var u2 = new LineSideUnpacker("U", clock2, 1) { PieceTime = Const(1) };
        var o2 = new Collector(clock2);
        Link(f2, u2); Link(u2, o2);
        u2.Start(); o2.Start(); f2.Start(); f2.Begin();
        StopToken tok = null;
        clock2.Schedule(() => tok = u2.Stop(new StopRequest()), 0.5);
        clock2.Schedule(() => u2.Resume(tok), 2.5);
        clock2.AdvanceClock(10);
        Check(o2.Got.Where(g => g.item.Name == "Piece").Select(g => g.t).SequenceEqual(new[] { 3.0, 4.0 }), "pieces at 3, 4 after a 2-long stop: " + string.Join(",", o2.Got.Select(g => g.t)));
    }

    static RunResults RunConfig(SimulationConfig cfg, out HeadlessModelFactory factory)
    {
        var clock = new SimClock(cfg.Seed);
        factory = new HeadlessModelFactory(clock);
        factory.BuildModel(cfg);
        factory.StartModel();
        clock.AdvanceClock(cfg.MaxSimulationTime);
        return RunResults.Capture(factory, "separator");
    }

    // The reference model: wagons of 4 every 4, unpacked at 1.5 per piece + 2 per change (8 per wagon): the unpacker is
    // the bottleneck, the stacker blocks; every piece reaches the sink and every emptied wagon the empties sink.
    static void TestFactoryPorts()
    {
        var r1 = RunConfig(ReferenceModels.LineSideUnpacking(), out var factory);
        var u = (LineSideUnpacker)factory.GetElement("unp");
        Check(u.Capacity == 2 && u.PieceTime != null && u.ChangeTime != null && u.Order == UnpackOrder.Fifo &&
              u.EmptiesUseCapacity && u.Mode == SeparatorMode.Unpack, "line-side unpacker built");
        var port = r1.Find(ResultScope.Element, "unp/empties");
        Check(port != null && port.Group == "unp", "the empties port in the results (unp/empties, group unp)");
        var uo = r1.Find(ResultScope.Element, "unp");
        bool reported = uo.TryGet(ResultMetrics.Released, out double unpacked) & uo.TryGet(ResultMetrics.CarriersEmptied, out double emptied)
                        & uo.TryGet(ResultMetrics.PiecesAvg, out _) & uo.TryGet(ResultMetrics.PiecesMax, out double piecesMax);
        Check(reported, "own metrics reported");
        Check(unpacked >= 190 && unpacked <= 200, $"bottleneck: about horizon / 2 pieces (got {unpacked})");
        Near(r1.Find(ResultScope.Element, "sink")[ResultMetrics.Input], unpacked, "every piece reached the sink");
        Near(r1.Find(ResultScope.Element, "empties")[ResultMetrics.Input], emptied, "every emptied wagon reached the empties sink");
        Near(port[ResultMetrics.Output], emptied, "the port's output = wagons emptied");
        Near(uo[ResultMetrics.Output], unpacked + emptied, "unpacker output = pieces + wagons");
        Near(uo[ResultMetrics.Input], uo[ResultMetrics.Output] + uo[ResultMetrics.Content], "input = output + content");
        Check(piecesMax <= 8, "at most two wagons of 4 held");
        var r2 = RunConfig(ReferenceModels.LineSideUnpacking(), out _);
        bool same = r1.Objects.Count == r2.Objects.Count;
        for (int i = 0; same && i < r1.Objects.Count; i++)
            foreach (var kv in r1.Objects[i].Metrics)
                same &= r2.Objects[i].TryGet(kv.Key, out double v) && v.Equals(kv.Value);
        Check(same, "same seed, same results");
    }

    static void TestPortConnections()
    {
        // two PortOutput connections from the same port share one link: the first destination (a queue of 1 that never
        // releases) takes one wagon, the default output strategy then sends the others to the second one
        var c = ReferenceModels.LineSideUnpacking();
        c.Connections.RemoveAll(x => x.ConnectionType == "PortOutput");
        var hold = new ElementConfig("hold", "Queue", "hold");
        hold.Set("capacity", 1);
        c.Elements.Add(hold);
        foreach (string to in new[] { "hold", "empties" })
        {
            var cc = new ConnectionConfig("unp", to, "PortOutput");
            cc.Port = Separator.PortEmpties;
            c.Connections.Add(cc);
        }
        var r = RunConfig(c, out var factory);
        double e1 = r.Find(ResultScope.Element, "hold")[ResultMetrics.Input], e2 = r.Find(ResultScope.Element, "empties")[ResultMetrics.Input];
        Check(e1 == 1 && e2 > 0, $"the first destination takes one wagon, the second the rest ({e1}, {e2})");
        Near(e1 + e2, r.Find(ResultScope.Element, "unp")[ResultMetrics.CarriersEmptied], "together, every emptied wagon");

        // an unknown port, and a port on an element without ports, fail the build
        foreach (var (owner, type) in new[] { ("unp", "PortOutput"), ("sink", "PortOutput"), ("unp", "PortInput") })
        {
            var bad = ReferenceModels.LineSideUnpacking();
            var cc = type == "PortOutput" ? new ConnectionConfig(owner, "empties", type) : new ConnectionConfig("src", owner, type);
            cc.Port = "nowhere";
            bad.Connections.Add(cc);
            bool threw = false;
            try { new HeadlessModelFactory(new SimClock(1)).BuildModel(bad); }
            catch (FormatException ex) { threw = ex.Message.Contains("no port 'nowhere'"); }
            Check(threw, $"{type} to an unknown port of '{owner}' fails the build naming the port");
        }
    }

    static PacedLine Line(SimClock clock, int n, double takt) => new PacedLine("Line", clock, n, Const(takt));

    static void TestLineBasic()
    {
        var clock = new SimClock(1);
        var cars = new Feeder(clock, "Cars", Enumerable.Repeat(0.0, 20).ToArray(), i => Vehicle(i));
        var line = Line(clock, 3, 2);
        var outp = new Collector(clock);
        Link(cars, line); Link(line, outp);
        line.Start(); outp.Start(); cars.Start(); cars.Begin();
        clock.AdvanceClock(20.5);
        // v1 enters at 0; indexes at 2, 4, … 20 (10); v1 leaves at the 3rd index (6), then one per takt: 6..20 = 8
        Check(line.IndexCount == 10, "10 indexes by 20.5");
        Check(outp.Got.Select(g => g.t).SequenceEqual(new[] { 6.0, 8, 10, 12, 14, 16, 18, 20 }), "exits every takt from 6");
        Check((int)outp.Got[0].item.GetLabelOrDefault("vehicle") == 1 && (int)outp.Got[7].item.GetLabelOrDefault("vehicle") == 8, "sequence labels");
        Near(line.StatsCollector.VarStayTime.Average, 6, "stay = 3 takts");
        Check(line.EmptyPitches == 0, "no empty pitch with vehicles waiting");
        Check(line.GetQueueLength() == 3, "three vehicles on the line");

        // vehicles every 5 with a takt of 2: position 0 is empty at some indexes
        var clock2 = new SimClock(1);
        var c2 = new Feeder(clock2, "Cars", new[] { 0.0, 5, 10 }, i => Vehicle(i));
        var l2 = Line(clock2, 3, 2);
        var o2 = new Collector(clock2);
        Link(c2, l2); Link(l2, o2);
        l2.Start(); o2.Start(); c2.Start(); c2.Begin();
        clock2.AdvanceClock(11);
        // indexes 2,4,6,8,10; pos0 occupied at index 2 (v1), 6 (v2, entered 5), 10 (v3) → empty at 4, 8
        Check(l2.EmptyPitches == 2, $"empty pitches 2 (got {l2.EmptyPitches})");
        Check(o2.Got.Select(g => g.t).SequenceEqual(new[] { 6.0, 10.0 }), "v1 leaves at 6, v2 at 10");
    }

    static void TestLineStarvation()
    {
        var clock = new SimClock(1);
        var cars = new Feeder(clock, "Cars", Enumerable.Repeat(0.0, 10).ToArray(), i => Vehicle(i));
        var line = Line(clock, 3, 2);
        var st = line.AddStation(new LineStation("S", 1));
        var parts = new Feeder(clock, "Parts", new[] { 0.0, 7.0 }, i => { var p = new Item(0); p.SetId("P", i, 0); return p; });
        var outp = new Collector(clock);
        Link(cars, line); Link(line, outp); Link(parts, st.Port);
        line.Start(); outp.Start(); cars.Start(); parts.Start(); cars.Begin(); parts.Begin();
        double starvedAt = double.NaN;
        line.StateChanged += (e, o, n) => { if (n == ElementState.STARVED && double.IsNaN(starvedAt)) starvedAt = clock.GetSimulationTime(); };
        clock.AdvanceClock(10);
        // index 2: v1 → S with the part from 0. Due 4: v2 needs a part → STARVED until 7 → index at 7, next takt 9:
        // v3 needs a part → STARVED again from 9 (no more parts). Starved time 3 + 1 = 4; two line stops.
        Near(starvedAt, 4, "starved at 4");
        Check(line.IndexCount == 2 && line.LastIndexTime == 7, $"indexes at 2 and 7 (count {line.IndexCount}, last {line.LastIndexTime})");
        Check(line.LineStops == 2 && st.LineStops == 2, "two line stops, both caused by S");
        Near(line.GetTimeInState(ElementState.STARVED), 4, "line starved time");
        Near(st.Port.GetTimeInState(ElementState.STARVED), 4, "station port STARVED time");
        Check(st.Fitted == 2, "two parts fitted");
        Check(st.Port.StatsCollector.VarOutput.Value == 2, "parts leave the port through the consume link");
        Near(st.Port.StatsCollector.VarStayTime.Average, (2 + 0) / 2.0, "part waits: 2 (0→2), 0 (7→7)");
        Check(line.VehicleAt(1)?.subItems?.Count == 1, "the part is a sub-item of the vehicle");
        // the warm-up reset restarts the line's and the stations' counters — checked on a copy of the counts
        var before = (line.IndexCount, line.LineStops, st.LineStops, st.Fitted);
        var m = new Dictionary<string, double>();
        line.ReportMetrics((k, v) => m[k] = v);
        Near(m[ResultMetrics.StarvedTime], 4, "metric starved_time");
        Near(m[ResultMetrics.IndexCount], 2, "metric index_count");
        Check(line.CurrentState == ElementState.STARVED, "STARVED at the end");
        Check(before.Item1 == 2 && before.Item4 == 2, "counts before the reset");
        line.ResetStatistics();
        Check(line.IndexCount == 0 && line.LineStops == 0 && st.LineStops == 0 && st.Fitted == 0, "warm-up reset clears the line and station counters");
        Near(st.Port.StatsCollector.VarOutput.Value, 0, "warm-up reset restarts the station port statistics");
    }

    static void TestLineBlockedDowntime()
    {
        var clock = new SimClock(1);
        var cars = new Feeder(clock, "Cars", Enumerable.Repeat(0.0, 5).ToArray(), i => Vehicle(i));
        var line = Line(clock, 2, 1);
        var q = new ItemsQueue(1, "Q", clock);
        Link(cars, line); Link(line, q);
        line.Start(); q.Start(); cars.Start(); cars.Begin();
        clock.AdvanceClock(3.5);
        // idx 1: v1 → pos1; idx 2: v1 → Q, v2 → pos1; due 3: v2 refused → BLOCKED
        Check(line.IndexCount == 2 && line.CurrentState == ElementState.BLOCKED, $"BLOCKED after 2 indexes ({line.IndexCount}, {line.CurrentState})");

        var clock2 = new SimClock(1);
        var c2 = new Feeder(clock2, "Cars", Enumerable.Repeat(0.0, 5).ToArray(), i => Vehicle(i));
        var l2 = Line(clock2, 2, 2);
        var o2 = new Collector(clock2);
        Link(c2, l2); Link(l2, o2);
        l2.Start(); o2.Start(); c2.Start(); c2.Begin();
        StopToken tok = null;
        clock2.Schedule(() => tok = l2.Stop(new StopRequest(ElementState.SCHEDULED_DOWN)), 1);
        clock2.Schedule(() => l2.Resume(tok), 4);
        double first = double.NaN;
        l2.Indexed += x => { if (double.IsNaN(first)) first = clock2.GetSimulationTime(); };
        clock2.AdvanceClock(10);
        Near(first, 5, "a 3-long break at 1 moves the first index from 2 to 5");
        Near(l2.GetTimeInState(ElementState.SCHEDULED_DOWN), 3, "break accounted");
    }

    static void TestLineCallOffQuantity()
    {
        var clock = new SimClock(11);
        var models = new[] { "K9", "X", "K9", "K9", "X", "K9" };
        var cars = new Feeder(clock, "Cars", Enumerable.Repeat(0.0, models.Length).ToArray(), i => Vehicle(i, models[i - 1]));
        var line = Line(clock, 4, 2);
        var fam = new CallOffFamily("Door", 2) { Condition = "item.model == \"K9\"", OrderType = "DoorOrder" };
        fam.ReferenceTables.Add(DiscreteChoice.Parse("L1:1"));
        fam.ReferenceTables.Add(DiscreteChoice.Parse("R1:1"));
        fam.AssignStreams(clock, "Line");
        var st = line.AddStation(new LineStation("Door", 2, fam) { MatchLabel = "vehicle", CallOffPosition = 0, PortCapacity = 4 });
        var echo = new Echo(clock);
        // tamper: the 2nd order of vehicle 3 gets the wrong reference
        echo.Transform = p => { if (p.GetLabelOrDefault("vehicle") == 3 && p.GetLabelOrDefault("position") == 2) p.SetLabelText("reference", "WRONG"); return p; };
        var outp = new Collector(clock);
        Link(cars, line); Link(line, outp); Link(st.CallOffPort, echo); Link(echo, st.Port);
        line.Start(); outp.Start(); echo.Start(); cars.Start(); cars.Begin();
        clock.AdvanceClock(30);
        // K9 vehicles 1, 3, 4, 6 call off 2 orders each when entering position 0
        Check(echo.Orders.Count == 8, $"8 orders (got {echo.Orders.Count})");
        var o1 = echo.Orders[0]; var o2 = echo.Orders[1];
        Check(o1.Name == "DoorOrder" && o1.GetLabelText("family") == "Door", "order type and family");
        Check(o1.GetLabelOrDefault("position") == 1 && o2.GetLabelOrDefault("position") == 2, "positions 1, 2 in order");
        Check(o1.GetLabelText("reference") == "L1" && o2.GetLabelText("reference") == "R1", "per-position tables");
        Check(o1.GetLabelText("model") == "K9" && o1.GetLabelOrDefault("vehicle") == 1 && o1.HasLabel("callOffTime"), "vehicle labels copied + vehicle + callOffTime");
        var v1 = outp.Got[0].item;
        Check(v1.GetLabelText("Door.p1.reference") == "L1" && v1.GetLabelText("Door.p2.reference") == "R1", "references written on the vehicle");
        Check(v1.subItems != null && v1.subItems.Count == 2, "two parts fitted to v1");
        Check(outp.Got[1].item.subItems == null, "X vehicle gets nothing");
        Check(st.Fitted == 8 && st.Mismatches == 1, $"8 fitted, 1 mismatch (got {st.Fitted}, {st.Mismatches})");
        Check(line.LineStops == 0, "parts always in time: no line stop");
        Near(st.CallOffPort.StatsCollector.VarOutput.Value, 8, "call-off port output = orders");
        Near(st.CallOffPort.StatsCollector.VarContent.Max, 0, "generator port holds no content");
        Check(Throws(() => line.AddStation(new LineStation("Bad", 0))), "a station at position 0 is refused");
    }

    static void TestLineOptionalPosition()
    {
        var clock = new SimClock(5);
        var cars = new Feeder(clock, "Cars", new[] { 0.0, 0.0, 0.0, 0.0 }, i => Vehicle(i));
        var line = Line(clock, 4, 2);
        var fam = new CallOffFamily("Console", 2);
        fam.ReferenceTables.Add(DiscreteChoice.Parse("B1:1"));
        fam.ReferenceTables.Add(DiscreteChoice.Parse(CallOffFamily.NoPart + ":1"));   // the top is never fitted
        fam.AssignStreams(clock, "Line");
        var st = line.AddStation(new LineStation("Console", 2, fam) { MatchLabel = "vehicle", CallOffPosition = 0, PortCapacity = 4 });
        var echo = new Echo(clock) { Transform = p => p.HasLabel("reference") ? p : null };   // a part only for orders with a reference
        var outp = new Collector(clock);
        Link(cars, line); Link(line, outp); Link(st.CallOffPort, echo); Link(echo, st.Port);
        line.Start(); outp.Start(); echo.Start(); cars.Start(); cars.Begin();
        clock.AdvanceClock(30);
        Check(echo.Orders.Count == 8, $"two orders per vehicle are still emitted (got {echo.Orders.Count})");
        Check(!echo.Orders[1].HasLabel("reference") && echo.Orders[1].GetLabelOrDefault("position") == 2, "the no-part order has no reference label");
        var v1 = outp.Got[0].item;
        Check(v1.GetLabelText("Console.p2.reference") == CallOffFamily.NoPart, "the vehicle records the no-part position");
        Check(fam.PartsFor(v1) == 1, "one part needed");
        Check(v1.subItems != null && v1.subItems.Count == 1, "one part fitted");
        Check(st.Fitted == 4 && st.Mismatches == 0, $"4 fitted, no mismatch (got {st.Fitted}, {st.Mismatches})");
        Check(line.LineStops == 0, "no line stop for the missing optional part");
    }

    static void TestCallOffPointRequisition()
    {
        var clock = new SimClock(5);
        var cars = new Feeder(clock, "Cars", Enumerable.Range(0, 12).Select(i => (double)i).ToArray(), i => Vehicle(i));
        var fam = new CallOffFamily("Door") { };
        fam.ReferenceTables.Add(DiscreteChoice.Parse("R1:0.5|R2:0.5"));
        fam.AssignStreams(clock, "CP");
        var cp = new CallOffPoint("CP", clock, new[] { fam });
        var buffer = new ItemsQueue(100, "Buffer", clock);
        var line = Line(clock, 3, 2);
        var st = line.AddStation(new LineStation("Door", 2) { MatchLabel = "vehicle" });
        var echo = new Echo(clock);
        var outp = new Collector(clock);
        Link(cars, cp); Link(cp, buffer); Link(buffer, line); Link(line, outp);
        Link(cp.GetPort("Door"), echo); Link(echo, st.Port);
        var callOff = new Dictionary<double, double>();
        var entered = new Dictionary<double, double>();
        var req = new List<(double req, double expected)>();
        line.VehicleEntered += (l, v) => entered[v.GetLabelOrDefault("vehicle")] = clock.GetSimulationTime();
        line.PartsFitted += (l, s, v, ps) =>
        {
            double id = v.GetLabelOrDefault("vehicle");
            double c = ps[0].GetLabelOrDefault("callOffTime");
            req.Add((clock.GetSimulationTime() - c, (entered[id] - c) + 2 * 2));
        };
        cp.Start(); buffer.Start(); line.Start(); outp.Start(); echo.Start(); cars.Start(); cars.Begin();
        clock.AdvanceClock(30);
        Check(cp.VehiclesReported == 12 && fam.OrdersEmitted == 12, "every vehicle reported, one order each");
        Check(req.Count > 5 && req.All(r => Math.Abs(r.req - r.expected) < 1e-9), "requisition = buffer time + 2 takts");
        Check(req.Select(r => r.req).Distinct().Count() > 1, "requisition time varies with the buffer");
        Check(st.Mismatches == 0, "references and sequence match");
        var v1 = outp.Got.Count > 0 ? outp.Got[0].item : null;
        Check(v1 != null && v1.GetLabelText("Door.p1.reference") == v1.subItems[0].GetLabelText("reference"), "fitted part carries the vehicle's reference");
        var m = new Dictionary<string, double>();
        cp.ReportMetrics((k, v) => m[k] = v);
        Near(m[ResultMetrics.CallOffs], 12, "call_offs metric");
        Near(cp.StatsCollector.VarOutput.Value, 12, "every vehicle passed the point");
    }

    static void TestLineBuildersFactory()
    {
        var cfg = LineConfig();
        var r1 = RunLine(cfg, out var factory);
        var line = (PacedLine)factory.GetElement("line");
        Check(line.Stations.Count == 1 && line.Stations[0].Family.Quantity == 1 && line.Stations[0].Port.Capacity == 3, "station built (port capacity 3)");
        var u = (LineSideUnpacker)factory.GetElement("unp");
        Check(u.Capacity == 2 && u.PieceTime != null && u.Order == UnpackOrder.Fifo, "line-side unpacker built");
        var port = r1.Find(ResultScope.Element, "line/Door");
        Check(port != null && port.Group == "line" && port.TryGet(ResultMetrics.LineStops, out _) && port.TryGet(ResultMetrics.Fitted, out _),
              "station port in the results (line/Door) with its metrics");
        Check(r1.Find(ResultScope.Element, "cp/Door") != null && r1.Find(ResultScope.Element, "unp/empties") != null, "call-off and empties ports in the results");
        var lo = r1.Find(ResultScope.Element, "line");
        Check(lo.TryGet(ResultMetrics.IndexCount, out double idx) && idx > 10, "line index_count");
        Check(lo.TryGet(ResultMetrics.Fitted, out double fitted) && fitted > 5, "parts fitted through unpacker → port");
        var r2 = RunLine(LineConfig(), out _);
        bool same = r1.Objects.Count == r2.Objects.Count;
        for (int i = 0; same && i < r1.Objects.Count; i++)
            foreach (var kv in r1.Objects[i].Metrics)
                same &= r2.Objects[i].TryGet(kv.Key, out double v) && v.Equals(kv.Value);
        Check(same, "same seed, same results");

        // typed station / family records: JSON round trip, paths
        var fs = new CallOffFamilySettings { Name = "D|x", Quantity = 2, Condition = "item.a == 1 || item.b == 2", ReferenceTables = new List<ReferenceTableSettings> { ReferenceTableSettings.Parse("A:1|B:2"), ReferenceTableSettings.Parse("C:1") } };
        var jc = new SimulationConfig();
        var je = new ElementConfig("line", "PacedLine", "line");
        je.Settings = new PacedLineSettings
        {
            Stations = new List<LineStationSettings> { new LineStationSettings { Name = "S", Position = 3, MatchLabel = "vehicle", CallOffPosition = 1, Family = fs } },
        };
        jc.Elements.Add(je);
        var sb = ConfigJson.Deserialize(ConfigJson.Serialize(jc)).Elements[0].SettingsAs<PacedLineSettings>().Stations[0];
        Check(sb.Position == 3 && sb.CallOffPosition == 1 && sb.MatchLabel == "vehicle" && sb.Family.Name == fs.Name && sb.Family.Quantity == 2
              && sb.Family.Condition == fs.Condition && sb.Family.ReferenceTables.Count == 2 && sb.Family.ReferenceTables[0].Choices[1].Reference == "B"
              && sb.Family.ReferenceTables[0].Choices[1].Weight == 2 && sb.Family.ReferenceTables[1].Choices[0].Reference == "C", "station / family record JSON round trip");
        je.Set("stations[0].family.quantity", 4);
        Check(je.Get<int>("stations[0].family.quantity", 0) == 4 && je.Get<int>("stations[0].portCapacity", 0) == 2, "station paths (stations[i].portCapacity, .family.quantity)");
        Check(Throws(() => je.Set("stationCapacities", new List<string> { "2" })), "the old stationCapacities key fails");
    }

    /// <summary>Source → call-off point → buffer → line; orders → kitting queue → (as wagons) … simplified: the order
    /// queue feeds a stacker of 2 that builds wagons → line-side unpacker → the station port.</summary>
    static SimulationConfig LineConfig()
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 42, MaxSimulationTime = 60 };
        var src = new ElementConfig("src", "Source", "src");
        src.Set("mode", "InterArrival");
        src.Set("interArrivalDistribution.strategyType", "Constant");
        src.Set("interArrivalDistribution.constantValue", 2.0);
        c.Elements.Add(src);
        var cp = new ElementConfig("cp", "CallOffPoint", "cp");
        cp.Settings = new CallOffPointSettings
        {
            Families = new List<CallOffFamilySettings> { new CallOffFamilySettings { Name = "Door", ReferenceTables = new List<ReferenceTableSettings> { ReferenceTableSettings.Parse("R1:0.6|R2:0.4") } } },
        };
        c.Elements.Add(cp);
        var buf = new ElementConfig("buf", "Queue", "buf");
        buf.Set("capacity", 50);
        c.Elements.Add(buf);
        var st = new ElementConfig("st", "Stacker", "st");
        st.Settings = new StackerSettings { PositionCount = 1, Capacity = 2, StackType = "Wagon" };
        c.Elements.Add(st);
        var unp = new ElementConfig("unp", "LineSideUnpacker", "unp");
        unp.Settings = new LineSideUnpackerSettings { Capacity = 2, PieceTime = SamplerSettings.Constant(0.1) };
        c.Elements.Add(unp);
        var line = new ElementConfig("line", "PacedLine", "line");
        line.Settings = new PacedLineSettings
        {
            Positions = 4, Takt = SamplerSettings.Constant(2),
            Stations = new List<LineStationSettings> { new LineStationSettings { Name = "Door", Position = 3, MatchLabel = "vehicle", PortCapacity = 3 } },
        };
        c.Elements.Add(line);
        c.Elements.Add(new ElementConfig("sink", "Sink", "sink"));
        c.Elements.Add(new ElementConfig("empties", "Sink", "empties"));
        void Conn(string a, string b, string type = "General", string port = null)
        {
            var cc = new ConnectionConfig(a, b, type);
            if (port != null) cc.Port = port;
            c.Connections.Add(cc);
        }
        Conn("src", "cp"); Conn("cp", "buf"); Conn("buf", "line"); Conn("line", "sink");
        Conn("cp", "st", "PortOutput", "Door"); Conn("st", "unp");
        Conn("unp", "line", "PortInput", "Door"); Conn("unp", "empties", "PortOutput", Separator.PortEmpties);
        return c;
    }

    static RunResults RunLine(SimulationConfig cfg, out HeadlessModelFactory factory)
    {
        var clock = new SimClock(cfg.Seed);
        factory = new HeadlessModelFactory(clock);
        factory.BuildModel(cfg);
        factory.StartModel();
        clock.AdvanceClock(cfg.MaxSimulationTime);
        return RunResults.Capture(factory, "line");
    }

    static bool Throws(Action a)
    {
        try { a(); return false; } catch { return true; }
    }
}
