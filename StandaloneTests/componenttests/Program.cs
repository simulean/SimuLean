// Components without a suite of their own: LengthLimitedQueue,
// the gated ItemsQueue (the former GateQueue), WeeklyShiftPattern + ShiftDowntime, ResultTable + ProjectDatabase; PeriodicDowntime; TopPacking.
// Run: dotnet run  (from StandaloneTests/componenttests)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SimuLean;
using SimuLean.Data;
using SimuLean.Downtime;
using SimuLean.Serialization;

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
        Run("LengthLimitedQueue: lane length, gap and item lengths", TestLaneLength);
        Run("LengthLimitedQueue: lanes, oversize items, count limit", TestLanesAndLimits);
        Run("LengthLimitedQueue: pass-through, FIFO exit, Unblock drains, Start resets", TestLaneFlow);
        Run("LengthLimitedQueue: a speed conveys (length ÷ speed to the end), accumulates behind a slow station, stops pause it", TestConveyor);
        Run("Statistics: a compound's sub-items counted on entry (opt-in), so they can leave one by one", TestSubItemsOnEntry);
        Run("Gated queue: holds items until released; spare releases are credits", TestGateQueue);
        Run("WeeklyShiftPattern: parse, working time, next change, merged and overnight windows, holidays", TestShiftPattern);
        Run("WeeklyShiftPattern: malformed text fails", TestShiftPatternErrors);
        Run("ShiftDowntime: a server works only in its shifts", TestShiftDowntime);
        Run("PeriodicDowntime: stops at first, first + period, ... before until, each of the duration", TestPeriodicTimes);
        Run("PeriodicDowntime: processing pauses and resumes (Immediate), or finishes first (AfterCurrent)", TestPeriodicPausesWork);
        Run("PeriodicDowntime: the warm-up resets its counters; a late start clips the running stop", TestPeriodicWarmupAndLateStart);
        Run("TopPacking: rows across the width, a new row then a new layer, each centred; the height of the layers", TestTopPacking);
        Run("PeriodicDowntime: invalid settings fail the build; overlap only when allowed", TestPeriodicValidation);
        Run("PeriodicDowntime: its config round-trips through JSON", TestPeriodicJsonRoundTrip);
        Run("ResultTable: CSV in the invariant culture, quoting", TestResultTableCsv);
        Run("ProjectDatabase + ResultTable: write, query, parameters, batch, reopen", TestDatabase);
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex.GetType().Name + ": " + ex.Message); }
    }

    static Item Sized(double length)
    {
        var it = new Item(0);
        it.SetLabelValue("length", length);
        return it;
    }

    // ── LengthLimitedQueue ───────────────────────────────────────────────────

    static void TestLaneLength()
    {
        var clock = new SimClock(1);
        var lane = new LengthLimitedQueue("Lane", clock, 5.0) { Gap = 0.2 };   // default item length 1
        lane.Start();
        int took = 0;
        for (int i = 0; i < 6; i++) if (lane.Receive(new Item(0))) took++;
        Check(took == 4, $"1 + 0.2 + 1 + 0.2 + 1 + 0.2 + 1 = 4.6 <= 5: four fit, the fifth (5.8) does not (took {took})");
        Near(lane.TotalUsedLength, 4.6, "used length counts the gaps between items");
        Near(lane.FreeLength, 0.2, "free length keeps the gap the next item needs (5 - 4.6 - 0.2)");

        var labelled = new LengthLimitedQueue("Labelled", clock, 5.0) { Gap = 0.2 };
        labelled.Start();
        Check(labelled.Receive(Sized(2.0)) && labelled.Receive(Sized(2.5)), "2 + 0.2 + 2.5 = 4.7 fits");
        Check(!labelled.Receive(Sized(0.5)), "4.7 + 0.2 + 0.5 = 5.4 does not");
        Near(labelled.LengthOf(labelled.Items[1]), 2.5, "the item length comes from its 'length' label");

        var byRule = new LengthLimitedQueue("Rule", clock, 10.0) { Gap = 0, ItemLength = it => 3 };
        byRule.Start();
        int n = 0; while (byRule.Receive(Sized(1))) n++;
        Check(n == 3, $"the ItemLength rule wins over the label (3 items of 3 in 10; got {n})");
    }

    static void TestLanesAndLimits()
    {
        var clock = new SimClock(1);
        var q = new LengthLimitedQueue("Two", clock, new[] { 3.0, 3.0 }) { Gap = 0.2 };
        q.Start();
        Item a = Sized(2), b = Sized(2), c = Sized(2);
        Check(q.Receive(a) && q.Receive(b) && !q.Receive(c), "two items of 2 in two lanes of 3; a third does not fit");
        Check(q.LaneOf(a) == 0 && q.LaneOf(b) == 1, "the first lane that fits (2 + 0.2 + 2 > 3, so the second lane)");
        Check(q.CountOnLane(0) == 1 && q.CountOnLane(1) == 1, "one item per lane");

        var big = new LengthLimitedQueue("Big", clock, 3.0);
        big.Start();
        Check(big.Receive(Sized(4)), "an item longer than the lane enters an empty lane (no deadlock)");
        Check(!big.Receive(Sized(0.5)), "nothing else while it is there");
        var strict = new LengthLimitedQueue("Strict", clock, 3.0) { AcceptOversizeOnEmptyLane = false };
        strict.Start();
        Check(!strict.Receive(Sized(4)), "AcceptOversizeOnEmptyLane = false refuses it");

        var counted = new LengthLimitedQueue("Counted", clock, 100.0) { MaxItems = 2 };
        counted.Start();
        Check(counted.Receive(new Item(0)) && counted.Receive(new Item(0)) && !counted.Receive(new Item(0)), "MaxItems limits the count");
        Check(!counted.CheckAvailability(new Item(0)) && counted.GetFreeCapacity() == 0, "and the availability says so");
    }

    static void TestLaneFlow()
    {
        var clock = new SimClock(1);
        // pass-through: an empty lane hands the item straight to a free successor
        var lane = new LengthLimitedQueue("Lane", clock, 10.0);
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(lane, new List<Element> { sink });
        lane.Start(); sink.Start();
        Check(lane.Receive(new Item(0)) && lane.GetQueueLength() == 0 && sink.GetStatsInputCount() == 1,
              "an item that can pass straight through never occupies the lane (the link records it)");

        // FIFO exit: three items held (the successor is full), then released in arrival order
        var buffer = new LengthLimitedQueue("Buffer", clock, 10.0);
        var next = new LengthLimitedQueue("Next", clock, 1.0) { AcceptOversizeOnEmptyLane = false };   // holds one item of 1
        GeneralLink.CreateLink(buffer, new List<Element> { next });
        buffer.Start(); next.Start();
        Item blocker = new Item(0);
        next.Receive(blocker);
        var order = new[] { new Item(0), new Item(0), new Item(0) };
        foreach (var it in order) Check(buffer.Receive(it), "held while the successor is full");
        Check(buffer.GetQueueLength() == 3 && buffer.CurrentState == ElementState.PROCESSING, "three held, PROCESSING");
        Check(!buffer.Unblock() && buffer.CurrentState == ElementState.BLOCKED, "the successor still refuses: BLOCKED");

        var wide = new LengthLimitedQueue("Wide", clock, 100.0);
        var buffer2 = new LengthLimitedQueue("Buffer2", clock, 10.0);
        var gate = new ItemsQueue(10, "Gate", clock) { Gated = true };
        GeneralLink.CreateLink(buffer2, new List<Element> { gate });
        GeneralLink.CreateLink(gate, new List<Element> { wide });
        buffer2.Start(); gate.Start(); wide.Start();
        gate.SetCapacity(0);                                     // refuses everything for now
        foreach (var it in order) buffer2.Receive(it);
        Check(buffer2.GetQueueLength() == 3, "held behind a closed gate");
        gate.SetCapacity(10); gate.ReleaseItems(10);
        Check(buffer2.Unblock() && buffer2.GetQueueLength() == 0, "Unblock drains the whole lane while the output accepts");
        Check(wide.Items.SequenceEqual(order), "in arrival order (global FIFO)");
        buffer2.Start();
        Check(buffer2.GetQueueLength() == 0 && buffer2.TotalUsedLength == 0, "Start empties the lanes");
    }

    static void TestSubItemsOnEntry()
    {
        var clock = new SimClock(1);
        var q = new ItemsQueue(100, "Unpack", clock);
        q.Start();
        var pallet = new Item(0);
        pallet.subItems = new List<Item>();
        for (int i = 0; i < 4; i++) pallet.subItems.Add(new Item(0));
        q.StatsCollector.OnEntry(pallet);
        Check(q.GetStatsInputCount() == 1, "off (default): the pallet only");
        var q2 = new ItemsQueue(100, "Unpack2", clock);
        q2.Start();
        q2.StatsCollector.CountSubItemsOnEntry = true;
        var pallet2 = new Item(0);
        var boxes = new List<Item>();
        pallet2.subItems = new List<Item>();
        for (int i = 0; i < 4; i++) { var b = new Item(0); boxes.Add(b); pallet2.subItems.Add(b); }
        q2.StatsCollector.OnEntry(pallet2);
        Check(q2.GetStatsInputCount() == 5, $"on: the pallet and its 4 boxes ({q2.GetStatsInputCount()})");
        clock.AdvanceClock(3);
        foreach (var b in boxes) q2.StatsCollector.OnExit(b);
        q2.StatsCollector.OnExit(pallet2);
        Check(q2.GetStatsOutputCount() == 5 && q2.GetStatsContentCurrent() == 0, "each leaves on its own; content back to 0");
        Near(q2.GetStatsStayTimeMean(), 3, "each stayed 3");
    }

    static void TestConveyor()
    {
        // A 10 m conveyor at 2 m per time unit: 5 to the end. Items at 0, 1, 2 for a free sink leave at 5, 6, 7.
        (List<double> exits, LengthLimitedQueue lane, SimClock clock) Run(double serverTime, double stopFrom = -1, double stopTo = -1)
        {
            var clock = new SimClock(1);
            var src = new ScheduleSource("Src", clock, new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string> { "1", "2", "3" }, ["Name"] = new List<string> { "P", "P", "P" }, ["Q"] = new List<string> { "1", "1", "1" },
            });
            var lane = new LengthLimitedQueue("Conveyor", clock, 10.0) { Speed = 2 };
            var exits = new List<double>();
            Element next;
            var sink = new Sink("Sink", clock);
            if (serverTime > 0)
            {
                var m = new MultiServer(new[] { (IRandomSampler)new ConstantRandomProcess(serverTime) }, "M", clock);
                GeneralLink.CreateLink(m, new List<Element> { sink });
                next = m;
                m.Start();
            }
            else next = sink;
            GeneralLink.CreateLink(src, new List<Element> { lane });
            GeneralLink.CreateLink(lane, new List<Element> { next });
            lane.ItemExited += (e, it) => exits.Add(clock.GetSimulationTime());
            src.Start(); lane.Start(); sink.Start();
            StopToken stop = null;
            if (stopFrom >= 0)
            {
                clock.ScheduleEvent(new ActionEvent(() => stop = lane.Stop(new StopRequest(ElementState.STOPPED))), stopFrom);
                clock.ScheduleEvent(new ActionEvent(() => lane.Resume(stop)), stopTo);
            }
            clock.AdvanceClock(50);
            return (exits, lane, clock);
        }
        string T(List<double> l) => string.Join(",", l.Select(x => x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));

        var free = Run(0);
        Check(T(free.exits) == "6,7,8", "a free exit: each item 5 after it entered (arrivals at 1, 2, 3): " + T(free.exits));
        var slow = Run(3);
        Check(T(slow.exits) == "6,9,12", "behind a station of 3 they accumulate and leave as it frees: " + T(slow.exits));
        var stopped = Run(0, 2, 4);
        // the first two (in at 1 and 2) pause 2; the third, refused during the stop, enters at 4 and leaves at 9
        Check(T(stopped.exits) == "8,9,9", "a stop from 2 to 4 pauses the conveying (+2) and refuses items: " + T(stopped.exits));
        Check(free.lane.GetQueueLength() == 0, "the conveyor empties");
        bool threw = false;
        try { new LengthLimitedQueue("Bad", new SimClock(1), 10.0) { Speed = -1 }; } catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "a negative speed fails");
    }

    // ── GateQueue ────────────────────────────────────────────────────────────

    static void TestGateQueue()
    {
        var clock = new SimClock(1);
        var gate = new ItemsQueue(3, "Gate", clock) { Gated = true };
        var lane = new LengthLimitedQueue("Out", clock, 100.0);
        GeneralLink.CreateLink(gate, new List<Element> { lane });
        gate.Start(); lane.Start();
        var items = Enumerable.Range(0, 4).Select(_ => new Item(0)).ToArray();
        Check(gate.Receive(items[0]) && gate.Receive(items[1]) && gate.Receive(items[2]), "takes up to its capacity");
        Check(!gate.CheckAvailability(items[3]) && !gate.Receive(items[3]), "and refuses beyond it");
        Check(lane.GetQueueLength() == 0 && gate.CurrentState == ElementState.PROCESSING, "nothing leaves before a release");
        gate.ReleaseItems(2);
        Check(lane.GetQueueLength() == 2 && gate.GetQueueLength() == 1, "Release(2) lets two out, in order");
        Check(lane.Items[0] == items[0] && lane.Items[1] == items[1], "FIFO");
        gate.ReleaseItems(3);
        Check(gate.GetQueueLength() == 0 && gate.PendingRelease == 2 && gate.CurrentState == ElementState.IDLE,
              "Release(3) with one item: it leaves and two releases remain as credit");
        Check(gate.Receive(items[3]) && gate.GetQueueLength() == 0 && gate.PendingRelease == 1, "an arrival uses a credit at once");
        gate.Start();
        Check(gate.PendingRelease == 0 && gate.GetQueueLength() == 0, "Start clears items and credits");
    }

    // ── WeeklyShiftPattern ───────────────────────────────────────────────────

    static readonly DateTime Monday = new DateTime(2026, 10, 5);   // a Monday

    static void TestShiftPattern()
    {
        var p = WeeklyShiftPattern.Parse("Mon-Fri 06:00-14:00,14:00-22:00; Sat 06:00-14:00");
        Check(p.Windows.Count == 11, $"5 days × 2 ranges + Saturday (got {p.Windows.Count})");
        Check(p.IsWorking(Monday.AddHours(7)) && p.IsWorking(Monday.AddHours(21.5)), "working at 07:00 and 21:30");
        Check(!p.IsWorking(Monday.AddHours(5)) && !p.IsWorking(Monday.AddHours(22)), "not at 05:00, nor at 22:00 (end excluded)");
        Check(!p.IsWorking(Monday.AddDays(6).AddHours(10)), "Sunday off");
        var merged = p.MergedWindows(Monday, Monday.AddDays(1));
        Check(merged.Count == 1 && merged[0].start == Monday.AddHours(6) && merged[0].end == Monday.AddHours(22),
              "06–14 and 14–22 merge into one window 06–22");
        Check(p.NextChange(Monday.AddHours(5)) == Monday.AddHours(6), "next change before the shift: its start");
        Check(p.NextChange(Monday.AddHours(10)) == Monday.AddHours(22), "inside: the end of the merged window");
        Check(p.NextChange(Monday.AddDays(4).AddHours(23)) == Monday.AddDays(5).AddHours(6), "Friday night → Saturday 06:00");
        Check(p.NextChange(Monday.AddDays(5).AddHours(15)) == Monday.AddDays(7).AddHours(6), "Saturday afternoon → next Monday 06:00");

        var night = WeeklyShiftPattern.Parse("Mon 22:00-06:00");
        Check(night.IsWorking(Monday.AddDays(1).AddHours(3)), "22:00-06:00 crosses midnight (Tuesday 03:00 works)");
        Check(!night.IsWorking(Monday.AddDays(1).AddHours(7)), "and ends at 06:00");

        var wrap = WeeklyShiftPattern.Parse("Sat-Mon 00:00-24:00");
        Check(wrap.IsWorking(Monday.AddDays(6).AddHours(12)) && wrap.IsWorking(Monday.AddHours(23.9)) && !wrap.IsWorking(Monday.AddDays(1).AddHours(1)),
              "a day range wraps the week (Sat, Sun, Mon) and 00:00-24:00 is the whole day");

        p.AddHoliday(Monday);
        Check(!p.IsWorking(Monday.AddHours(7)) && p.IsWorking(Monday.AddDays(1).AddHours(7)), "a holiday skips that day only");
        night.AddHoliday(Monday);
        Check(!night.IsWorking(Monday.AddDays(1).AddHours(3)), "a window that starts on a holiday is skipped, also after midnight");
    }

    static void TestShiftPatternErrors()
    {
        foreach (string bad in new[] { "Mon", "Xyz 06:00-07:00", "Mon 0600-0700", "Mon 06:00-7", "Mon 25:00-26:00" })
        {
            bool threw = false;
            try { WeeklyShiftPattern.Parse(bad); } catch (FormatException) { threw = true; }
            Check(threw, $"'{bad}' fails with a FormatException");
        }
        Check(WeeklyShiftPattern.Parse("  ").Windows.Count == 0, "blank text: no windows");
    }

    static void TestShiftDowntime()
    {
        // One week in minutes from Monday 00:00; the server works Mon–Fri 06:00–14:00 (40 h of 168 h).
        var cfg = SimuLean.Testing.ReferenceModels.ShiftModel();
        var clock = new SimClock(cfg.Seed);
        var factory = new SimuLean.Headless.HeadlessModelFactory(clock);
        var built = factory.BuildModel(cfg);
        factory.StartModel();
        clock.AdvanceClock(cfg.MaxSimulationTime);
        Element srv = built.Values.Single(e => e.Name == "srv");
        Element sink = built.Values.Single(e => e.Name == "sink");
        Near(srv.GetStateRatio(ElementState.OFF_SHIFT), 128.0 / 168, "off shift 128 h of 168", 0.01);
        Check(sink.GetStatsInputCount() > 0, "it still produces in its shifts");
    }

    // ── PeriodicDowntime ─────────────────────────────────────────────────────

    // src (one item at 0) → srv (one slot, constant time) → sink, with a periodic stop on srv.
    static SimulationConfig PeriodicModel(double processTime, Action<DowntimeConfig> set, double horizon = 1000, double warmup = 0)
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 7, MaxSimulationTime = horizon, WarmupTime = warmup };
        var src = new ElementConfig("src", "Source", "src");
        src.Set("mode", "Schedule");
        src.Set("arrivalsTable", new Dictionary<string, List<string>>
        {
            ["Time"] = new List<string> { "0" }, ["Name"] = new List<string> { "Part" }, ["Q"] = new List<string> { "1" },
        });
        var srv = new ElementConfig("srv", "MultiServer", "srv");
        srv.Set("capacity", 1);
        srv.Set("delayStrategy.strategyType", "Constant");
        srv.Set("delayStrategy.constantValue", processTime);
        c.Elements.Add(src); c.Elements.Add(srv); c.Elements.Add(new ElementConfig("sink", "Sink", "sink"));
        c.Connections.Add(new ConnectionConfig("src", "srv", "General"));
        c.Connections.Add(new ConnectionConfig("srv", "sink", "General"));
        var dt = new DowntimeConfig("pm", "Periodic", "srv");
        set(dt);
        c.Downtimes.Add(dt);
        return c;
    }

    // Builds and runs to `until` (default: the horizon); returns the generator and the stop start / end times.
    static (SimuLean.Headless.HeadlessModelFactory model, SimClock clock, DowntimeGenerator gen, List<double> starts, List<double> ends)
        RunPeriodic(SimulationConfig c, double? until = null)
    {
        var clock = new SimClock(c.Seed);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        var gen = model.DowntimeGenerators.Single();
        var starts = new List<double>(); var ends = new List<double>();
        gen.StopStarted += (g, t) => starts.Add(clock.GetSimulationTime());
        gen.StopEnded += (g, t) => ends.Add(clock.GetSimulationTime());
        model.StartModel();
        clock.AdvanceClock(until ?? c.MaxSimulationTime);
        return (model, clock, gen, starts, ends);
    }

    static void TestTopPacking()
    {
        // four 0.5 x 0.5 cartons (0.3 high) on a 1 x 1 top: two rows of two, one layer
        var carton = (0.5, 0.3, 0.5);
        var four = new[] { carton, carton, carton, carton };
        var at = TopPacking.Places(1, 1, four);
        double rowWidth = 0.5 * 1.01 + 0.5, rowDepth = 0.5 * 1.01;
        Near(at[0].x, 0.25 - rowWidth / 2, "first part: left of the row's centre");
        Near(at[1].x, 0.5 * 1.01 + 0.25 - rowWidth / 2, "second part beside it");
        Check(at[0].y == 0 && at[3].y == 0, "one layer");
        Near(at[0].z, (rowDepth + 0.5) / 2 - 0.25, "first row towards +z");
        Near(at[2].z, (rowDepth + 0.5) / 2 - (rowDepth + 0.25), "second row towards -z");
        Near(TopPacking.Height(1, 1, four), 0.3, "height: one layer");
        // a fifth starts a second layer, as high as its tallest part
        var five = new[] { carton, carton, carton, carton, (0.5, 0.4, 0.5) };
        Near(TopPacking.Places(1, 1, five)[4].y, 0.3, "the fifth on the first layer");
        Near(TopPacking.Height(1, 1, five), 0.7, "height: two layers");
        // a part larger than the top: a row and a layer of its own
        var big = new[] { (2.0, 0.5, 2.0), (2.0, 0.5, 2.0) };
        Near(TopPacking.Height(1, 1, big), 1.0, "two oversize parts: two layers");
        Check(TopPacking.Places(1, 1, new (double, double, double)[0]).Length == 0 && TopPacking.Height(1, 1, new (double, double, double)[0]) == 0, "nothing: no places, no height");
    }

    static void TestPeriodicTimes()
    {
        var r = RunPeriodic(PeriodicModel(5, d => { d.Set("periodicFirst", 10.0); d.Set("periodicPeriod", 100.0); d.Set("periodicDuration", 20.0); d.Set("periodicUntil", 350.0); }));
        Check(r.gen is PeriodicDowntime, "kind Periodic builds a PeriodicDowntime");
        Check(r.starts.SequenceEqual(new[] { 10.0, 110, 210, 310 }), $"starts at 10, 110, 210, 310, none from 350 on (got {string.Join(", ", r.starts)})");
        Check(r.ends.SequenceEqual(new[] { 30.0, 130, 230, 330 }), $"each lasts 20 (ends {string.Join(", ", r.ends)})");
        Check(r.gen.StopCount == 4, $"four stops ({r.gen.StopCount})");
        Near(r.gen.TotalDowntime, 80, "total downtime");
        Check(r.gen.State == ElementState.SCHEDULED_DOWN && r.gen.Mode == StopMode.Immediate, "defaults: SCHEDULED_DOWN, Immediate");
        Element srv = r.model.Find("srv");
        Near(srv.GetStateRatio(ElementState.SCHEDULED_DOWN), 80.0 / 1000, "the element spends 8 % SCHEDULED_DOWN", 1e-9);

        var forever = RunPeriodic(PeriodicModel(5, d => { d.Set("periodicPeriod", 100.0); d.Set("periodicDuration", 20.0); }), 999);
        Check(forever.starts.Count == 10 && forever.starts[0] == 0 && forever.starts[9] == 900,
              $"no until: every 100 from 0 (got {forever.starts.Count}: {string.Join(", ", forever.starts)})");
        var exact = RunPeriodic(PeriodicModel(5, d => { d.Set("periodicPeriod", 0.1); d.Set("periodicDuration", 0.05); d.Set("periodicFirst", 0.3); }), 1000.01);
        Near(exact.starts.Last(), 0.3 + 9997 * 0.1, "the k-th start is first + k * period (no summed drift)", 1e-12);
    }

    static void TestPeriodicPausesWork()
    {
        // the part is processed 0..50; the stop 10..30 pauses it: 10 done, 40 left, so it leaves at 70
        var imm = PeriodicModel(50, d => { d.Set("periodicFirst", 10.0); d.Set("periodicPeriod", 1000.0); d.Set("periodicDuration", 20.0); });
        var r1 = RunPeriodic(imm, 69.9);
        Check(r1.model.Find("sink").GetStatsInputCount() == 0, "Immediate: still in process at 69.9");
        r1.clock.AdvanceClock(70.1);
        Check(r1.model.Find("sink").GetStatsInputCount() == 1, "and done at 70 (the 20 of stop added)");
        Check(r1.model.Find("srv").CurrentState != ElementState.SCHEDULED_DOWN, "resumed");

        var after = PeriodicModel(50, d => { d.Set("periodicFirst", 10.0); d.Set("periodicPeriod", 1000.0); d.Set("periodicDuration", 20.0); d.Set("mode", "AfterCurrent"); });
        var r2 = RunPeriodic(after, 50.1);
        Check(r2.model.Find("sink").GetStatsInputCount() == 1, "AfterCurrent: the job in progress finishes at 50");
        Check(r2.gen.StopCount == 1 && r2.ends.Count == 0 && r2.model.Find("srv").CurrentState == ElementState.SCHEDULED_DOWN,
              "the stop takes effect when the job ends (50), not cut: still running");
        r2.clock.AdvanceClock(70.1);
        Check(r2.ends.SequenceEqual(new[] { 70.0 }), $"it lasts its whole 20: stopped 50..70 (ends {string.Join(", ", r2.ends)})");
        Near(r2.gen.TotalDowntime, 20, "downtime_total: the 20 the element stood");
        Near(r2.gen.ScheduledDowntime, 20, "downtime_scheduled: the 20 planned");
    }

    static void TestPeriodicWarmupAndLateStart()
    {
        // stops 10 + 100k for 20; the warm-up at 520 falls inside the stop 510..530
        var r = RunPeriodic(PeriodicModel(5, d => { d.Set("periodicFirst", 10.0); d.Set("periodicPeriod", 100.0); d.Set("periodicDuration", 20.0); }, 1000, 520));
        Check(r.gen.StopCount == 4, $"after the warm-up: the stops at 610, 710, 810, 910 ({r.gen.StopCount})");
        Near(r.gen.TotalDowntime, 10 + 4 * 20, "the running stop counts from the warm-up (10) + 4 x 20");

        // a generator started at 215 applies the stop 210..230 for its remaining 15
        var b = RunPeriodic(PeriodicModel(5, d => { d.Set("periodicFirst", 9999.0); d.Set("periodicPeriod", 10000.0); d.Set("periodicDuration", 1.0); }), 215);
        var late = new PeriodicDowntime(b.model.Find("srv"), 100, 20, 10, clock: b.clock);
        var starts = new List<double>(); var ends = new List<double>();
        late.StopStarted += (g, t) => starts.Add(b.clock.GetSimulationTime());
        late.StopEnded += (g, t) => ends.Add(b.clock.GetSimulationTime());
        late.Start();
        b.clock.AdvanceClock(400);
        Check(starts.SequenceEqual(new[] { 215.0, 310 }) && ends.SequenceEqual(new[] { 230.0, 330 }),
              $"late start: 215..230 then 310..330 (starts {string.Join(", ", starts)}; ends {string.Join(", ", ends)})");
        late.CancelPending();
    }

    static void TestPeriodicValidation()
    {
        void Fails(string what, Action<DowntimeConfig> set)
        {
            bool threw = false;
            try { RunPeriodic(PeriodicModel(5, set), 10); }
            catch (FormatException e) { threw = e.Message.Contains("'pm'"); }
            Check(threw, what + " fails the build, naming the downtime");
        }
        Fails("period 0", d => { d.Set("periodicPeriod", 0.0); d.Set("periodicDuration", 1.0); });
        Fails("a negative duration", d => { d.Set("periodicPeriod", 10.0); d.Set("periodicDuration", -1.0); });
        Fails("duration > period", d => { d.Set("periodicPeriod", 10.0); d.Set("periodicDuration", 15.0); });
        Fails("a negative first", d => { d.Set("periodicPeriod", 10.0); d.Set("periodicDuration", 1.0); d.Set("periodicFirst", -5.0); });
        Fails("until not after the first", d => { d.Set("periodicPeriod", 10.0); d.Set("periodicDuration", 1.0); d.Set("periodicFirst", 5.0); d.Set("periodicUntil", 5.0); });
        Fails("no period", d => { d.Set("periodicDuration", 1.0); });

        var o = RunPeriodic(PeriodicModel(5, d => { d.Set("periodicPeriod", 10.0); d.Set("periodicDuration", 15.0); d.Set("allowOverlap", true); }), 12);
        Check(o.model.Find("srv").ActiveStops.Count == 2, "allowOverlap: the stops at 0 and 10 stack at 12");
        o.clock.AdvanceClock(99);
        Check(o.gen.StopCount == 10 && o.ends.Count == 9, $"ten started by 99, nine ended ({o.gen.StopCount}, {o.ends.Count})");
    }

    static void TestPeriodicJsonRoundTrip()
    {
        var c = PeriodicModel(5, d =>
        {
            d.Set("periodicFirst", 10.0); d.Set("periodicPeriod", 100.0); d.Set("periodicDuration", 20.0); d.Set("periodicUntil", 350.0);
            d.Set("allowOverlap", false); d.Set("periodicCode", "PM"); d.Set("stateName", "STOPPED"); d.Set("mode", "AfterCurrent");
        });
        var dt = c.Downtimes[0];
        var copy = ConfigJson.Deserialize(ConfigJson.Serialize(c)).Downtimes[0];
        Check(copy.Kind == "Periodic" && copy.Values().Count() == dt.Values().Count(), "kind and settings survive");
        c.Downtimes[0] = copy;
        var r = RunPeriodic(c);
        var gen = (PeriodicDowntime)r.gen;
        Check(gen.First == 10 && gen.Period == 100 && gen.Duration == 20 && gen.Until == 350 && gen.Code == "PM"
              && gen.State == ElementState.STOPPED && gen.Mode == StopMode.AfterCurrent, "every setting read back (numbers parsed as long / double)");
        Check(r.starts.SequenceEqual(new[] { 10.0, 110, 210, 310 }), "the same stops as before the round trip");
    }

    // ── ResultTable + ProjectDatabase ────────────────────────────────────────

    static void TestResultTableCsv()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("es-ES");   // a comma decimal culture must not leak in
        try
        {
            var t = new ResultTable("runs", "name", "value", "note");
            t.Add("a", 1.5, "plain");
            t.Add("b,c", 2, "say \"hi\"");
            t.Add(null, null, "line\nbreak");
            string csv = t.ToCsv().Replace("\r\n", "\n");
            string[] lines = csv.Split('\n');
            Check(lines[0] == "name,value,note", "header");
            Check(lines[1] == "a,1.5,plain", $"numbers in the invariant culture (got '{lines[1]}')");
            Check(lines[2] == "\"b,c\",2,\"say \"\"hi\"\"\"", $"separators and quotes are quoted (got '{lines[2]}')");
            Check(lines[3] == ",,\"line", "nulls are empty; a line break is quoted");
            Check(t.ToCsv(';').Split('\n')[1].Trim() == "a;1.5;plain", "another separator");
            Check(t.RowCount == 3, "three rows");
            bool threw = false;
            try { t.Add("only one"); } catch (ArgumentException) { threw = true; }
            Check(threw, "a row with the wrong number of values fails");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    static void TestDatabase()
    {
        string path = Path.Combine(Path.GetTempPath(), "simulean_componenttests_" + Guid.NewGuid().ToString("N") + ".db");
        ProjectDatabase db;
        try { db = new ProjectDatabase(path); }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
        {
            Console.WriteLine("  (skipped: no SQLite native library on this machine — " + e.GetType().Name + ")");
            return;
        }
        try
        {
            var t = new ResultTable("results", "scenario", "replication", "throughput", "ok");
            t.Add("A", 1, 98.5, true);
            t.Add("A", 2, 101.25, false);
            t.Add("B", 1, 77.0, true);
            t.WriteTo(db);
            Check(db.TableExists("results"), "WriteTo creates the table");
            var rows = db.Query("SELECT scenario, replication, throughput, ok FROM results ORDER BY scenario, replication");
            Check(rows.Count == 3, "three rows written");
            Check((string)rows[1]["scenario"] == "A" && Convert.ToInt64(rows[1]["replication"]) == 2, "text and integer columns");
            Near(Convert.ToDouble(rows[1]["throughput"]), 101.25, "a real column keeps its value");
            Check(Convert.ToInt64(rows[0]["ok"]) == 1 && Convert.ToInt64(rows[1]["ok"]) == 0, "bools as integers");
            var b = db.Query("SELECT throughput FROM results WHERE scenario = ? AND replication = ?", "B", 1);
            Check(b.Count == 1 && Convert.ToDouble(b[0]["throughput"]) == 77.0, "parameters bind");
            t.WriteTo(db);
            Check(db.Query("SELECT COUNT(*) AS n FROM results")[0]["n"] is long n && n == 3, "WriteTo replaces the table (no duplicates)");
            int inserted = db.ExecuteBatch("INSERT INTO results VALUES (?, ?, ?, ?)", new[] { new object[] { "C", 1, 50.0, true }, new object[] { "C", 2, 51.0, true } });
            Check(inserted == 2, $"a batch inserts every row (got {inserted})");
            db.Execute("CREATE TABLE IF NOT EXISTS params (k TEXT, v REAL)");
            db.Execute("INSERT INTO params VALUES (?, ?)", "rate", 0.25);
            Check(!db.TableExists("nothing"), "TableExists is false for a missing table");
            db.Dispose();
            using (var again = new ProjectDatabase(path))
                Check(again.Query("SELECT COUNT(*) AS n FROM results")[0]["n"] is long m && m == 5, "the data persist after reopening the file");
        }
        finally
        {
            db.Dispose();
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
