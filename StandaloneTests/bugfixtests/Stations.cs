// Stations: Multi-Server, Delay, conditions, elements without links, and element statistics (resets, state ratios, content, tallies).
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Serialization;

static partial class Program
{
    // ── #7 ───────────────────────────────────────────────────────────────────

    static void TestStartResetsStats()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 5, 1);
        var q = new ItemsQueue(10, "Q", clock);
        var m = new MultiServer(new[] { Const(0.5) }, "M", clock);
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(feeder, L(q)); GeneralLink.CreateLink(q, L(m)); GeneralLink.CreateLink(m, L(sink));
        foreach (var e in new Element[] { feeder, q, m, sink }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(10);
        Check(sink.GetStatsInputCount() == 5, "first run counted");
        foreach (var e in new Element[] { q, m, sink }) e.Start();
        foreach (var e in new Element[] { q, m, sink })
            Check(e.GetStatsInputCount() == 0 && e.GetStatsOutputCount() == 0 && e.GetStatsContentMax() == 0,
                  $"{e.Name} statistics reset by Start (in {e.GetStatsInputCount()}, out {e.GetStatsOutputCount()})");
    }

    // ── #16 ──────────────────────────────────────────────────────────────────

    static void TestStateRatioSinceReset()
    {
        var t = new ElementStateTracker(ElementState.IDLE, 0);
        t.Reset(ElementState.IDLE, 100);
        t.Transition(ElementState.PROCESSING, 150);
        Near(t.GetStateRatio(ElementState.PROCESSING, 200), 0.5, "PROCESSING ratio over [100, 200]");
        Near(t.GetStateRatio(ElementState.IDLE, 200), 0.5, "IDLE ratio over [100, 200]");
    }

#if !OLDAPI
    // ── #15 ──────────────────────────────────────────────────────────────────

    static void TestSlotIdleAfterUnblock()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 2, 0);
        var m = new MultiServer(new[] { Const(1), Const(5) }, "M", clock);
        var outp = new Collector(clock) { Open = false };
        GeneralLink.CreateLink(feeder, L(m)); GeneralLink.CreateLink(m, L(outp));
        foreach (var e in new Element[] { feeder, m, outp }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(2);                          // slot 1 done at t=1 (blocked), slot 2 busy until 5
        Schedule(clock, 2.5, () => outp.OpenNow());     // unblock while the other slot still works
        clock.AdvanceClock(3);
        var idle = (System.Collections.IEnumerable)typeof(MultiServer)
            .GetField("idleProcesses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(m);
        var states = idle.Cast<ServerProcess>().Select(sp => sp.GetState()).ToList();
        Check(states.Count == 1 && states[0] == ElementState.IDLE,
              $"freed slot is IDLE (idle pool: {string.Join(",", states)})");
    }
#endif

    static void TestConditionReadsElement()
    {
        var clock = new SimClock(1);
        var q = new ItemsQueue(10, "Q", clock, new ConditionStrategy("item.weight <= element.maxWeight"));
        q.Properties.SetDouble("maxWeight", 12);
        var sink = new Sink("K", clock);
        GeneralLink.CreateLink(q, new List<Element> { sink });
        q.Start(); sink.Start();
        Check(q.CanAccept(Labelled("weight", 5), null), "weight 5 <= 12 accepted by CanAccept");
        Check(!q.CanAccept(Labelled("weight", 20), null), "weight 20 > 12 refused");
        Feed(clock, "Src", q, Labelled("weight", 5), Labelled("weight", 7));
        Check(sink.StatsCollector.TotalInput == 2, $"both items pass through the link (got {sink.StatsCollector.TotalInput})");
    }

    static void TestElementWithoutLinks()
    {
        var clock = new SimClock(1);
        var q = new ItemsQueue(5, "Q", clock);
        q.Start();
        Check(q.Receive(new Item(0)) && q.GetQueueLength() == 1, "a queue without an output link keeps the item");
        Check(!q.Unblock(), "Unblock without links returns false");
        var m = new MultiServer(new IRandomSampler[] { new SimuLean.ConstantRandomProcess(1) }, "M", clock);
        m.Start();
        Check(m.Receive(new Item(0)), "a server without links accepts an item");
        clock.AdvanceClock(2);
        Check(m.CurrentState == ElementState.BLOCKED, "it finishes and waits (BLOCKED) instead of failing");
    }

    static void TestMultiServerCountInvariant()
    {
        foreach (var mode in new[] { "", "Immediate", "AfterCurrent" })
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 7, MaxSimulationTime = 3000 };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Exponential");
            src.Set("interArrivalDistribution.exponentialRate", 2.0);
            var ms = new SimuLean.Serialization.ElementConfig("ms", "MultiServer", "ms");
            ms.Set("capacity", 150);
            ms.Set("delayStrategy.strategyType", "Expression");
            ms.Set("delayStrategy.expression", "item.id % 7 * 10 + 1");
            var q = new SimuLean.Serialization.ElementConfig("q", "Queue", "q");   // a short buffer: the output blocks
            q.Set("capacity", 5);
            var slow = new SimuLean.Serialization.ElementConfig("slow", "MultiServer", "slow");
            slow.Set("capacity", 1); slow.Set("delayStrategy.strategyType", "Constant"); slow.Set("delayStrategy.constantValue", 0.45);
            c.Elements.Add(src); c.Elements.Add(ms); c.Elements.Add(q); c.Elements.Add(slow);
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink"));
            foreach (var (f, t) in new[] { ("src", "ms"), ("ms", "q"), ("q", "slow"), ("slow", "sink") })
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(f, t, "General"));
            if (mode != "")
            {
                var d = new SimuLean.Serialization.DowntimeConfig("dt", "MtbfMttr", "ms");
                d.Set("timeToFailure", "ExponentialMean~200"); d.Set("timeToRepair", "ExponentialMean~30"); d.Set("mode", mode);
                c.Downtimes.Add(d);
            }
            var clock = new SimClock(7);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            var station = (MultiServer)model.Find("ms");
            model.StartModel();
            string first = null;
            bool blocked = false;
            for (double t = 10; t <= 3000 && first == null; t += 10)
            {
                clock.AdvanceClock(t);
                int held = station.GetItems().Count, count = station.GetQueueLength(), content = (int)Math.Round(station.GetStatsContentCurrent());
                if (station.CurrentState == ElementState.BLOCKED) blocked = true;
                foreach (var slot in station.Slots) if (slot.GetState() == ElementState.BLOCKED) blocked = true;
                if (count != held || count != content) first = $"t={t}: count {count}, items {held}, content {content}";
            }
            Check(first == null, $"stops '{(mode == "" ? "none" : mode)}': count = items = content all along ({first ?? "ok"})");
            Check(blocked, $"stops '{(mode == "" ? "none" : mode)}': the output did block (the case is exercised)");
        }
    }

    static void TestMultiServerLargeCapacity()
    {
        double Run(int capacity, out long delivered)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 7, MaxSimulationTime = 5000 };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Exponential");
            src.Set("interArrivalDistribution.exponentialRate", 2.0);
            var ms = new SimuLean.Serialization.ElementConfig("ms", "MultiServer", "ms");
            ms.Set("capacity", capacity);
            ms.Set("delayStrategy.strategyType", "Constant");
            ms.Set("delayStrategy.constantValue", 40.0);
            c.Elements.Add(src); c.Elements.Add(ms); c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "ms", "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("ms", "sink", "General"));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var clock = new SimClock(7);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            clock.AdvanceClock(5000);
            delivered = (long)model.Find("sink").GetStatsInputCount();
            return watch.Elapsed.TotalSeconds;
        }
        double small = Run(300, out long outSmall), big = Run(100000, out long outBig);
        Check(outSmall == outBig, $"an unbounded delay: the same items out ({outSmall}, {outBig})");
        Check(big < 8, $"100 000 slots run 5000 time units in {big:F2} s (it took ~30 s when every event walked every slot; 300 slots: {small:F2} s)");
    }

    // A station of 10 slots whose jobs each need one of 2 devices: most loaded slots wait for a device. GetItems()
    // must list every held item (it listed only the ones in process and the blocked ones).
    static void TestMultiServerItemsWaitingForResource()
    {
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 3, MaxSimulationTime = 100 };
        var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
        src.Set("mode", "InterArrival");
        src.Set("interArrivalDistribution.strategyType", "Constant");
        src.Set("interArrivalDistribution.constantValue", 1.0);
        var ms = new SimuLean.Serialization.ElementConfig("ms", "MultiServer", "ms");
        ms.Set("capacity", 10); ms.Set("delayStrategy.strategyType", "Constant"); ms.Set("delayStrategy.constantValue", 5.0);
        ms.Set("processBy.team", "devices");
        c.Elements.Add(src); c.Elements.Add(ms); c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink"));
        c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "ms", "General"));
        c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("ms", "sink", "General"));
        var team = new SimuLean.Serialization.ResourceTeamConfig { Id = "devices", Name = "devices" };
        team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "d1" });
        team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "d2" });
        c.ResourceTeams.Add(team);
        var clock = new SimClock(3);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        var station = (MultiServer)model.Find("ms");
        model.StartModel();
        string first = null; bool waited = false;
        for (double t = 0.5; t <= 100 && first == null; t += 1)
        {
            clock.AdvanceClock(t);
            int items = station.GetItems().Count, content = (int)Math.Round(station.GetStatsContentCurrent());
            foreach (var slot in station.Slots) if (slot.GetState() == ElementState.WAITING_FOR_RESOURCE) waited = true;
            if (items != content || items != station.GetQueueLength())
                first = $"t={t}: items {items}, content {content}, count {station.GetQueueLength()}";
        }
        Check(waited, "slots did wait for a device (the case is exercised)");
        Check(first == null, $"GetItems() lists the items waiting for a resource too ({first ?? "ok"})");
    }

    // ── #51 ──────────────────────────────────────────────────────────────────

    static void TestContentAverageFromRunStart()
    {
        // one item reaches a queue at t = 900 and stays there until the end (t = 1000): average content 0.1, not 1
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 1, 1);
        var q = new ItemsQueue(10, "Q", clock);
        var outp = new Collector(clock) { Open = false };
        GeneralLink.CreateLink(feeder, L(q)); GeneralLink.CreateLink(q, L(outp));
        foreach (var e in new Element[] { feeder, q, outp }) e.Start();
        Schedule(clock, 900, () => feeder.Begin());
        clock.AdvanceClock(1000);
        double avg = q.GetStatsContentAvgTW();
        Check(Math.Abs(avg - 0.1) < 1e-9, $"queue content average over the run: {avg} (want 0.1)");

        // same for a list: one value pushed at t = 900
        var list = new SimuLean.Collections.SimList("L", clock);
        list.Reset();
        Schedule(clock, 1900, () => list.PushValue(1));
        clock.AdvanceClock(2000);
        double lavg = list.Content.GetTimeWeightedAverage(clock.GetSimulationTime());
        Check(Math.Abs(lavg - 0.1) < 1e-9, $"list content average over the run: {lavg} (want 0.1)");
    }

    // ── #53 ──────────────────────────────────────────────────────────────────

    static void TestMultiServerFifoNoOvertake()
    {
        // item 1 finishes at t=1 with the output closed (blocked); the output opens at 1.5 without telling anyone;
        // item 2 finishes at t=2: FIFO release sends item 1 first, then item 2 - never item 2 alone, ahead of item 1
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 2, 1);
        var m = new MultiServer(new[] { Const(1), Const(1) }, "M", clock);
        var outp = new Collector(clock) { Open = false };
        GeneralLink.CreateLink(feeder, L(m)); GeneralLink.CreateLink(m, L(outp));
        foreach (var e in new Element[] { feeder, m, outp }) e.Start();
        feeder.Begin();
        Schedule(clock, 1.5, () => outp.Open = true);
        clock.AdvanceClock(5);
        string order = string.Join(",", outp.Got.Select(i => i.GetId()));
        Check(outp.Got.Count == 2 && outp.Got[0].GetId() < outp.Got[1].GetId(), $"both out, oldest first (got {order})");
        Check(m.GetQueueLength() == 0, $"nothing left blocked ({m.GetQueueLength()})");
    }

    // ── #61 ──────────────────────────────────────────────────────────────────

    // A Delay has no capacity — ten items arriving together with delays 10, 9, …, 1 all start at once and
    // leave in delay order; with the output blocked they wait and leave in the order they finished.
    static void TestDelayElement()
    {
        SimuLean.Serialization.SimulationConfig Model(bool gated)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "Schedule");
            src.Set("arrivalsTable", new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string> { "0" }, ["Name"] = new List<string> { "p" }, ["Q"] = new List<string> { "10" },
            });
            var d = new SimuLean.Serialization.ElementConfig("d", "Delay", "d");
            d.Set("delayStrategy.strategyType", "Expression");
            d.Set("delayStrategy.expression", "11 - id");
            c.Elements.Add(src); c.Elements.Add(d);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "d", "General"));
            if (gated)
            {
                var q = new SimuLean.Serialization.ElementConfig("q", "Queue", "q");
                q.Set("capacity", 1);
                c.Elements.Add(q);
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("d", "q", "General"));
            }
            else
            {
                c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink"));
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("d", "sink", "General"));
            }
            return c;
        }

        var clock = new SimClock(1);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(Model(false));
        var delay = (Delay)model.Find("d");
        model.StartModel();
        clock.AdvanceClock(0.5);
        Check(delay.RunningCount == 10, $"all ten in their delay at once (running {delay.RunningCount})");
        Check(delay.CurrentState == ElementState.PROCESSING, "processing while items wait");
        clock.AdvanceClock(5.5);
        Check(delay.GetQueueLength() == 5, $"five left by t=5.5 (held {delay.GetQueueLength()})");
        clock.AdvanceClock(20);
        Check(delay.GetQueueLength() == 0 && delay.CurrentState == ElementState.IDLE, "all gone and idle at the end");
        Check(Math.Abs(delay.GetStatsOutputCount() - 10) < 1e-9, $"output 10 (got {delay.GetStatsOutputCount()})");

        clock = new SimClock(1);
        model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(Model(true));
        delay = (Delay)model.Find("d");
        model.StartModel();
        clock.AdvanceClock(20);
        Check(delay.RunningCount == 0 && delay.GetQueueLength() == 9, $"one item passed, nine wait to leave (held {delay.GetQueueLength()})");
        Check(delay.CurrentState == ElementState.BLOCKED, "blocked when only finished items are held");
        Check(Math.Abs(delay.GetStatsContentCurrent() - 9) < 1e-9, "content from the links: nine");
    }

    // Patients take Uniform(1, 3) at a station; the sink records time_in_system = age and
    // fast = age <= 2 split by the case mix label. Mean ~2, P50 ~2, P90 ~2.8, the share ~0.5; the splits add up to the
    // total; RunResults carries them as tally:… statistics.
    static void TestItemTallies()
    {
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 5, MaxSimulationTime = 20000 };
        var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
        src.Set("mode", "InterArrival");
        src.Set("interArrivalDistribution.strategyType", "Constant");
        src.Set("interArrivalDistribution.constantValue", 4.0);
        src.Set("exitLabels", new List<string> { "dx = pick(1, 'diagnosis', 1, 'control')" });
        var st = new SimuLean.Serialization.ElementConfig("st", "MultiServer", "st");
        st.Set("capacity", 1); st.Set("delayStrategy.strategyType", "Uniform");
        st.Set("delayStrategy.uniformMin", 1.0); st.Set("delayStrategy.uniformMax", 3.0);
        var sink = new SimuLean.Serialization.ElementConfig("done", "Sink", "done");
        sink.Set("tallies", new List<string> { "time_in_system = age", "fast = age <= 2" });
        sink.Set("tallySplit", "dx");
        c.Elements.Add(src); c.Elements.Add(st); c.Elements.Add(sink);
        c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "st", "General"));
        c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("st", "done", "General"));
        var clock = new SimClock(5);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
        clock.AdvanceClock(20000);
        var done = model.Find("done");
        var tallies = done.StatsCollector.Tallies;
        var t = tallies.Get("time_in_system");
        Check(t.Count == (int)done.GetStatsInputCount(), $"one value per patient ({t.Count} / {done.GetStatsInputCount()})");
        Check(Math.Abs(t.Mean - 2) < 0.05 && Math.Abs(t.Percentile(0.5) - 2) < 0.08 && Math.Abs(t.Percentile(0.9) - 2.8) < 0.08,
              $"mean {t.Mean:0.000}, P50 {t.Percentile(0.5):0.000}, P90 {t.Percentile(0.9):0.000}");
        Check(t.Min >= 1 && t.Max <= 3, "within the process time range");
        Check(Math.Abs(tallies.Get("fast").Mean - 0.5) < 0.03, $"share within 2: {tallies.Get("fast").Mean:0.000}");
        var d = tallies.Get("time_in_system", "diagnosis"); var k = tallies.Get("time_in_system", "control");
        Check(d != null && k != null && d.Count + k.Count == t.Count, "the splits add up to the total");
        var r = SimuLean.Results.RunResults.Capture(model, "tallies");
        var o = r.Objects.First(x => x.Name == "done");
        Check(o.TryGet("tally:time_in_system:p90", out double p90) && Math.Abs(p90 - t.Percentile(0.9)) < 1e-12, "tally:time_in_system:p90 in the results");
        Check(o.TryGet("tally:fast[control]:count", out double kc) && kc == k.Count, "split statistics in the results");
        var study = new SimuLean.Optimization.Study(c).Metric("p90", "done.tally:time_in_system:p90")
            .Metric("n", "done.tally:fast[control]:count").WithRunTime(20000).Run();
        Check(Math.Abs(study.Runs[0].Metrics["p90"] - 2.8) < 0.08 && Math.Abs(study.Runs[0].Metrics["n"] - k.Count) < 100,
              $"a study asks for a tally statistic (Owner.tally:NAME:stat; its own seed): P90 {study.Runs[0].Metrics["p90"]:0.000}, {study.Runs[0].Metrics["n"]} controls");
        Check(!r.Objects.First(x => x.Name == "st").TryGet("utilization_onshift", out _), "no on-shift utilisation without a shift");
    }
}
