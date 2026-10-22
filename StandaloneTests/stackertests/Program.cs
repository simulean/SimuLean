// Standalone runtime tests for the Stacker element (stack positions, selection rules,
// count / height capacity, compound release, Link-driven statistics, downtime, builder).
// Run: dotnet run  (from StandaloneTests/stackertests)
using System;
using System.Collections.Generic;
using SimuLean;
using SimuLean.Headless;
using SimuLean.Serialization;

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
        Run("FirstNotFull: full stacks leave as compound items", TestFirstNotFull);
        Run("RoundRobin positions", TestRoundRobin);
        Run("ByKey stacks + partial release when blocked", TestByKey);
        Run("ByLabelIndex + out-of-range fallback", TestByLabelIndex);
        Run("Height limit: complete by height, blocked stack closes", TestHeight);
        Run("Cycle time: pipelined pick point, timing, stay times", TestCycleTime);
        Run("Blocked output: stacks wait in order, retried on Unblock", TestBlockedOutput);
        Run("Immediate stop pauses the cycle", TestStop);
        Run("ReleaseStack / ReleaseAll / PositionSelector", TestControl);
        Run("StackerBuilder round trip + headless factory", TestBuilder);
        Run("Compound exit is opt-in", TestOptIn);

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

    /// <summary>Emits items at fixed intervals; holds refused items and retries on Unblock.</summary>
    sealed class Feeder : Element, IEvent
    {
        readonly Queue<Item> held = new Queue<Item>();
        readonly double interval;
        readonly Action<Item, int> label;
        int remaining, n;
        public Feeder(SimClock c, int count, double interval, Action<Item, int> label = null) : base("Feeder", c)
        { remaining = count; this.interval = interval; this.label = label; }
        public void Begin() => simClock.ScheduleEvent(this, 0);
        public void Execute()
        {
            var it = new Item(simClock.GetSimulationTime());
            it.SetId("A", ++n, 0);
            label?.Invoke(it, n);
            if (held.Count > 0 || !GetOutput().SendItem(it, this)) held.Enqueue(it);
            if (--remaining > 0) simClock.ScheduleEvent(this, interval);
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

    /// <summary>Downstream that records what arrives (and when); refuses while closed.</summary>
    sealed class Collector : Element
    {
        public readonly List<Item> Got = new List<Item>();
        public readonly List<double> At = new List<double>();
        public bool Open = true;
        public Collector(SimClock c) : base("Collector", c) { }
        public void OpenNow() { Open = true; GetInput().NotifyAvailable(this); }
        public override bool Receive(Item i) { if (!Open) return false; Got.Add(i); At.Add(simClock.GetSimulationTime()); return true; }
        public override bool CheckAvailability(Item i) => Open;
        public override bool Unblock() => false;
        public override int GetQueueLength() => 0;
        public override int GetFreeCapacity() => Open ? 1 : 0;
    }

    sealed class At : IEvent
    {
        readonly Action a;
        public At(Action a) { this.a = a; }
        public void Execute() => a();
    }

    static (SimClock clock, Feeder feeder, Stacker stacker, Collector sink) Line(int count, double interval, int positions, int capacity,
                                                                          Action<Item, int> label = null, Action<Stacker> setup = null)
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, count, interval, label);
        var stacker = new Stacker("Stacker", clock, positions, capacity);
        var sink = new Collector(clock);
        setup?.Invoke(stacker);
        GeneralLink.CreateLink(feeder, new List<Element> { stacker });
        GeneralLink.CreateLink(stacker, new List<Element> { sink });
        feeder.Start(); stacker.Start(); sink.Start();
        feeder.Begin();
        return (clock, feeder, stacker, sink);
    }

    static List<int> Ids(Item stack)
    {
        var l = new List<int>();
        if (stack?.subItems != null) foreach (var s in stack.subItems) l.Add(s.GetId());
        return l;
    }

    static string Str(List<int> l) => string.Join(",", l);

    // ── tests ────────────────────────────────────────────────────────────────

    static void TestFirstNotFull()
    {
        var (clock, _, st, sink) = Line(7, 1.0, 2, 3);
        clock.AdvanceClock(20);
        Check(sink.Got.Count == 2, $"two stacks released (got {sink.Got.Count})");
        Check(Str(Ids(sink.Got[0])) == "1,2,3", "first stack holds items 1-3: " + Str(Ids(sink.Got[0])));
        Check(Str(Ids(sink.Got[1])) == "4,5,6", "second stack holds items 4-6");
        var s0 = sink.Got[0];
        Check(s0.GetTypeName() == "Stack", "compound type");
        Near(s0.GetLabelOrDefault(Stacker.LabelCount), 3, "stackCount label");
        Near(s0.GetLabelOrDefault(Stacker.LabelPosition), 0, "stackPosition label");
        Check(s0.GetLabelText(Stacker.LabelReason) == "full", "reason full");
        Check(st.Positions[0].Count == 1 && st.Positions[1].Count == 0, "item 7 on position 0 (freed), position 1 unused");
        Check(st.ItemsStacked == 7 && st.StacksReleased == 2 && st.PartialStacksReleased == 0, "counters");
        // Link-driven statistics: 7 in, 6 out (inside the 2 compounds), 1 held
        Near(st.StatsCollector.VarInput.Value, 7, "stacker input");
        Near(st.StatsCollector.VarOutput.Value, 6, "stacker output counts the stacked items");
        Near(st.StatsCollector.VarContent.Value, 1, "stacker content");
        Near(sink.StatsCollector.VarInput.Value, 2, "downstream counts the compounds");
        Check(st.CurrentState == ElementState.RECEIVING, "forming a stack = RECEIVING, got " + st.CurrentState);
        Near(st.GetQueueLength(), 1, "queue length");
    }

    static void TestRoundRobin()
    {
        var (clock, _, st, sink) = Line(4, 1.0, 2, 2, setup: s => s.SelectionRule = StackSelectionRule.RoundRobin);
        clock.AdvanceClock(10);
        Check(sink.Got.Count == 2, "two stacks");
        Check(Str(Ids(sink.Got[0])) == "1,3", "position 0 = items 1,3: " + Str(Ids(sink.Got[0])));
        Check(Str(Ids(sink.Got[1])) == "2,4", "position 1 = items 2,4");
        Near(sink.Got[1].GetLabelOrDefault(Stacker.LabelPosition), 1, "second stack from position 1");
        Near(sink.At[0], 2, "first stack leaves when item 3 is placed (t = 2)");
    }

    static void TestByKey()
    {
        string[] keys = { "A", "B", "A", "C", "B", "D", "E" };
        var (clock, _, st, sink) = Line(keys.Length, 1.0, 2, 2,
            (it, n) => it.SetLabelText("sku", keys[n - 1]),
            s => { s.SelectionRule = StackSelectionRule.ByKey; s.KeyLabel = "sku"; });
        clock.AdvanceClock(20);
        // A,B -> p0,p1; A -> p0 full; C -> p0; B -> p1 full; D -> p1; E: no empty position -> oldest open (C, p0) closes
        Check(sink.Got.Count == 3, $"three stacks (got {sink.Got.Count})");
        Check(Str(Ids(sink.Got[0])) == "1,3" && sink.Got[0].GetLabelText(Stacker.LabelKey) == "A", "A stack = 1,3");
        Check(Str(Ids(sink.Got[1])) == "2,5" && sink.Got[1].GetLabelText(Stacker.LabelKey) == "B", "B stack = 2,5");
        Check(Str(Ids(sink.Got[2])) == "4" && sink.Got[2].GetLabelText(Stacker.LabelReason) == "blocked", "partial C stack released (blocked)");
        Check(sink.Got[2].GetLabelText("sku") == "C", "labels copied from the first item");
        Check(st.PartialStacksReleased == 1, "one partial stack");
        Check(st.Positions[0].Key == "E" && st.Positions[1].Key == "D", "E took p0, D on p1");
        Near(st.StatsCollector.VarContent.Value, 2, "content D + E");
    }

    static void TestByLabelIndex()
    {
        double[] target = { 1, 1, 7, 0 };   // 7 = out of range -> first not full
        var (clock, _, st, sink) = Line(4, 1.0, 2, 2,
            (it, n) => it.SetLabelValue("stackPosition", target[n - 1]),
            s => s.SelectionRule = StackSelectionRule.ByLabelIndex);
        clock.AdvanceClock(10);
        Check(sink.Got.Count == 2, "two stacks");
        Check(Str(Ids(sink.Got[0])) == "1,2", "position 1 = items 1,2");
        Near(sink.Got[0].GetLabelOrDefault(Stacker.LabelPosition), 1, "compound position overrides the copied label");
        Check(Str(Ids(sink.Got[1])) == "3,4", "item 3 (out of range) fell back to position 0 with item 4");
    }

    static void TestHeight()
    {
        // homogeneous: 0.3 each, limit 1.0 -> 3 items (0.9; a 4th would exceed)
        var (clock, _, st, sink) = Line(6, 1.0, 1, 100, (it, n) => it.SetLabelValue("height", 0.3), s => s.MaxHeight = 1.0);
        clock.AdvanceClock(10);
        Check(sink.Got.Count == 2, "two stacks by height");
        Near(sink.Got[0].GetLabelOrDefault(Stacker.LabelCount), 3, "3 items per stack");
        Near(sink.Got[0].GetLabelOrDefault(Stacker.LabelHeight), 0.9, "stack height 0.9", 1e-9);
        Check(sink.Got[0].GetLabelText(Stacker.LabelReason) == "height", "reason height");

        // heterogeneous: 0.3, 0.3, then 0.5 does not fit (1.1) -> the blocking stack closes, 0.5 starts a new one
        double[] h = { 0.3, 0.3, 0.5 };
        (clock, _, st, sink) = Line(3, 1.0, 1, 100, (it, n) => it.SetLabelValue("height", h[n - 1]), s => s.MaxHeight = 1.0);
        clock.AdvanceClock(10);
        Check(sink.Got.Count == 1 && Str(Ids(sink.Got[0])) == "1,2", "the 0.6 stack left");
        Check(sink.Got[0].GetLabelText(Stacker.LabelReason) == "blocked", "reason blocked");
        Check(st.Positions[0].Count == 1, "0.5 item on the freed position");
        Near(st.Positions[0].Height, 0.5, "new stack height");

        // oversize on an empty position (default accepted)
        (clock, _, st, sink) = Line(1, 1.0, 1, 100, (it, n) => it.SetLabelValue("height", 2.0), s => s.MaxHeight = 1.0);
        clock.AdvanceClock(5);
        Check(sink.Got.Count == 1 && sink.Got[0].GetLabelText(Stacker.LabelReason) == "height", "oversize item placed and released");

        // AcceptOversizeOnEmpty = false: the item can never be placed (waits)
        (clock, _, st, sink) = Line(1, 1.0, 1, 100, (it, n) => it.SetLabelValue("height", 2.0),
                                   s => { s.MaxHeight = 1.0; s.AcceptOversizeOnEmpty = false; });
        clock.AdvanceClock(5);
        Check(sink.Got.Count == 0 && st.PickItem != null && st.CurrentState == ElementState.BLOCKED, "oversize refused waits at the pick point (BLOCKED)");
        Check(!st.CheckAvailability(new Item(0)), "pick point occupied");
    }

    static void TestCycleTime()
    {
        var starts = new List<double>();
        var placed = new List<double>();
        double released = -1;
        var (clock, feeder, st, sink) = Line(4, 1.0, 1, 4, setup: s =>
        {
            s.CycleTime = new ConstantRandomProcess(2.0);
            s.CycleStarted += (x, c) => starts.Add(c.Start);
            s.ItemPlaced += (x, it, p, lvl) => placed.Add(x.GetSimClock().GetSimulationTime());
            s.StackReleased += (x, p, stack) => released = x.GetSimClock().GetSimulationTime();
        });
        clock.AdvanceClock(5);
        Check(st.CycleInProgress && st.CurrentState == ElementState.PROCESSING, "cycle running at t=5 (PROCESSING)");
        clock.AdvanceClock(20);
        Check(Str(starts.ConvertAll(d => (int)d)) == "0,2,4,6", "cycles back to back from t=0: " + string.Join(",", starts));
        Check(Str(placed.ConvertAll(d => (int)d)) == "2,4,6,8", "placements every 2 s");
        Near(released, 8, "stack leaves when the 4th item is placed");
        Near(sink.At[0], 8, "downstream receives it at t=8");
        // items entered at 0,1,2,4 (item 4 waited in the feeder until the pick point freed at t=4)
        Near(st.StatsCollector.VarStayTime.Average, (8 + 7 + 6 + 4) / 4.0, "mean stay time");
        Near(st.StatsCollector.VarContent.Value, 0, "empty after release");
        Check(st.CurrentState == ElementState.IDLE, "IDLE at the end");
    }

    static void TestBlockedOutput()
    {
        var (clock, feeder, st, sink) = Line(6, 1.0, 2, 2, setup: s => s.SelectionRule = StackSelectionRule.RoundRobin);
        sink.Open = false;
        clock.AdvanceClock(10);
        // p0 = 1,3 ; p1 = 2,4 complete & refused ; item 5 at the pick point cannot be placed ; item 6 held upstream
        Check(st.WaitingStacks == 2, "two complete stacks waiting");
        Check(st.PickItem != null && st.PickItem.GetId() == 5, "item 5 waits at the pick point");
        Check(feeder.Held == 1, "item 6 held upstream");
        Check(st.CurrentState == ElementState.BLOCKED, "BLOCKED");
        sink.OpenNow();
        clock.AdvanceClock(20);
        Check(sink.Got.Count == 2, $"both waiting stacks left after opening (got {sink.Got.Count})");
        Check(Str(Ids(sink.Got[0])) == "1,3" && Str(Ids(sink.Got[1])) == "2,4", "completion order kept");
        Near(sink.At[0], 10, "released when the output opened");
        Check(st.Positions[0].Count == 1 && st.Positions[0].Items[0].GetId() == 5
              && st.Positions[1].Count == 1 && st.Positions[1].Items[0].GetId() == 6, "items 5,6 placed round-robin after the release");
        Check(feeder.Held == 0 && st.PickItem == null, "upstream drained");
        Near(st.StatsCollector.VarInput.Value, 6, "input 6");
        Near(st.StatsCollector.VarOutput.Value, 4, "output 4");
        Near(st.StatsCollector.VarContent.Value, 2, "content 2");
    }

    static void TestStop()
    {
        var (clock, _, st, sink) = Line(1, 1.0, 1, 1, setup: s => s.CycleTime = new ConstantRandomProcess(10.0));
        StopToken tok = null;
        clock.ScheduleEvent(new At(() => tok = st.Stop(new StopRequest(ElementState.BREAKDOWN))), 5);
        clock.ScheduleEvent(new At(() => st.Resume(tok)), 10);
        clock.AdvanceClock(14.9);
        Check(sink.Got.Count == 0, "not placed before t=15");
        clock.AdvanceClock(15.1);
        Check(sink.Got.Count == 1, "placed and released at t=15 (cycle paused 5 s)");
        Near(sink.At[0], 15, "release time");
        Near(st.GetTimeInState(ElementState.BREAKDOWN), 5, "5 s of breakdown");
    }

    static void TestControl()
    {
        var (clock, _, st, sink) = Line(3, 1.0, 2, 10);
        clock.AdvanceClock(5);
        Check(st.Positions[0].Count == 3, "three items on position 0");
        Check(!st.ReleaseStack(1), "empty position: nothing to release");
        Check(st.ReleaseStack(0), "forced release");
        Check(sink.Got.Count == 1 && sink.Got[0].GetLabelText(Stacker.LabelReason) == "forced", "partial stack forced out");

        // release during a running cycle: the stack closes right after the placement
        (clock, _, st, sink) = Line(2, 1.0, 1, 10, setup: s => s.CycleTime = new ConstantRandomProcess(3.0));
        clock.AdvanceClock(4);   // item 1 placed at 3, item 2 cycle 3..6
        Check(st.CycleInProgress && st.ReleaseAll() == 1, "ReleaseAll during the cycle");
        clock.AdvanceClock(10);
        Check(sink.Got.Count == 1 && Str(Ids(sink.Got[0])) == "1,2", "closed after placing item 2");

        // PositionSelector delegate overrides the rule
        (clock, _, st, sink) = Line(4, 1.0, 3, 2, setup: s => s.PositionSelector = (it, x) => it.GetId() % 2 == 0 ? 2 : 1);
        clock.AdvanceClock(10);
        Check(sink.Got.Count == 2 && Str(Ids(sink.Got[0])) == "1,3" && Str(Ids(sink.Got[1])) == "2,4", "odd -> 1, even -> 2");
        Near(sink.Got[1].GetLabelOrDefault(Stacker.LabelPosition), 2, "even stack from position 2");
    }

    sealed class TestHandler : ElementHandlerBase
    {
        readonly string type; readonly Func<ElementConfig, SimClock, IVElement, Element> build;
        public TestHandler(string type, Func<ElementConfig, SimClock, IVElement, Element> build) { this.type = type; this.build = build; }
        public override string TypeName => type;
        public override Element Build(ElementConfig c, SimClock clock, IVElement v) => build(c, clock, v);
    }

    static void TestBuilder()
    {
        var cfg = new ElementConfig("st", "UnityStacker", "Stacker");
        StackerBuilder.Encode(cfg, new StackerBuilder.Settings
        {
            Positions = 3, Capacity = 4, PositionCapacities = new List<int> { 2, 0, 5 },
            MaxHeight = 1.5, HeightLabel = "h", DefaultItemHeight = 0.1, AcceptOversize = false,
            SelectionRule = StackSelectionRule.ByKey, KeyLabel = "sku", PositionLabel = "pos",
            ReleasePartialWhenBlocked = false, CycleTime = "Constant~3", StackType = "Pallet", CopyLabels = false,
        });
        var clock = new SimClock(3);
        var s = StackerBuilder.Build(cfg, clock, null);
        Check(s.PositionCount == 3 && s.Capacity == 4, "positions + capacity");
        Check(s.CapacityOf(s.Positions[0]) == 2 && s.CapacityOf(s.Positions[1]) == 4 && s.CapacityOf(s.Positions[2]) == 5, "per-position capacities");
        Near(s.MaxHeight, 1.5, "max height");
        Check(s.HeightLabel == "h" && Math.Abs(s.DefaultItemHeight - 0.1) < 1e-12 && !s.AcceptOversizeOnEmpty, "height settings");
        Check(s.SelectionRule == StackSelectionRule.ByKey && s.KeyLabel == "sku" && s.PositionLabel == "pos", "rule + labels");
        Check(!s.ReleasePartialWhenBlocked && s.StackType == "Pallet" && !s.CopyLabelsFromFirst, "release / compound settings");
        Check(s.CycleTime != null && Math.Abs(s.CycleTime.NextValue() - 3) < 1e-12, "cycle time sampler");
        Check(StackerBuilder.ParseRule("roundrobin") == StackSelectionRule.RoundRobin && StackerBuilder.ParseRule("?") == StackSelectionRule.FirstNotFull, "rule parsing");

        // tolerant reads (strings) and defaults
        var loose = new ElementConfig("x", "UnityStacker", "Loose");
        loose.Parameters["positions"] = "2"; loose.Parameters["capacity"] = 3.0f; loose.Parameters["cycleTime"] = "";
        var l = StackerBuilder.Build(loose, clock, null);
        Check(l.PositionCount == 2 && l.Capacity == 3 && l.CycleTime == null && l.SelectionRule == StackSelectionRule.FirstNotFull, "tolerant reads + defaults");

        // headless factory: release source -> stacker -> sink
        var reg = new ElementHandlerRegistry();
        reg.Register(new TestHandler("UnityStacker", (c, ck, v) => StackerBuilder.Build(c, ck, v)));
        reg.Register(new TestHandler("TestSource", (c, ck, v) => new ReleaseSource(c.Name, ck, v)));
        reg.Register(new TestHandler("TestSink", (c, ck, v) => new Sink(c.Name, ck, v)));
        var sim = new SimulationConfig();
        var src = new ElementConfig("src", "TestSource", "Src");
        var stc = new ElementConfig("st", "UnityStacker", "Stacker");
        StackerBuilder.Encode(stc, new StackerBuilder.Settings { Positions = 2, Capacity = 3, CycleTime = "Constant~1" });
        var snk = new ElementConfig("snk", "TestSink", "Sink");
        sim.Elements.Add(src); sim.Elements.Add(stc); sim.Elements.Add(snk);
        sim.Connections.Add(new ConnectionConfig("src", "st"));
        sim.Connections.Add(new ConnectionConfig("st", "snk"));
        var hclock = new SimClock(5);
        var factory = new HeadlessModelFactory(hclock, false, reg);
        var model = factory.BuildModel(sim);
        factory.StartModel();
        var rs = (ReleaseSource)model["src"];
        var items = new List<Item>();
        for (int i = 1; i <= 7; i++) { var it = new Item(0); it.SetId("P", i, 0); items.Add(it); }
        rs.Enqueue(items);
        hclock.AdvanceClock(100);
        var hs = (Stacker)model["st"];
        var sink = (Sink)model["snk"];
        Check(sink.GetNumberItems() == 2, $"two stacks reached the sink headless (got {sink.GetNumberItems()})");
        Check(hs.ItemsStacked == 7 && hs.Positions[0].Count == 1, "seventh item forming a new stack");
        Near(hs.StatsCollector.VarOutput.Value, 6, "headless stats: 6 items out");
    }

    static void TestOptIn()
    {
        var clock = new SimClock(1);
        var q = new ItemsQueue(10, "Q", clock);
        Check(!q.StatsCollector.CountSubItemsOnExit, "off by default on other elements");
        var st = new Stacker("S", clock, 1, 1);
        Check(st.StatsCollector.CountSubItemsOnExit, "on for the stacker");
    }
}
