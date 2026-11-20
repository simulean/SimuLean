// Queues: capacity, gated queues, items passing straight through, the BLOCKED state, stay-time statistics.
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Serialization;

static partial class Program
{
    static void TestMaxQueue()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 3, 1);
        var q0 = new ItemsQueue(10, "Q0", clock, new MaxQueueInputStrategy(2));
        var q1 = new ItemsQueue(10, "Q1", clock);
        var o0 = new Collector(clock, "O0") { Open = false };
        var o1 = new Collector(clock, "O1") { Open = false };
        GeneralLink.CreateLink(feeder, L(q0, q1));
        GeneralLink.CreateLink(q0, L(o0)); GeneralLink.CreateLink(q1, L(o1));
        foreach (var e in new Element[] { feeder, q0, q1, o0, o1 }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(5);
        Check(q0.GetQueueLength() == 2 && q1.GetQueueLength() == 1,
              $"MaxQueue(2): Q0 {q0.GetQueueLength()} (want 2), Q1 {q1.GetQueueLength()} (want 1)");
    }

    static void TestGateReleaseWakesUpstream()
    {
        var clock = new SimClock(1);
        var src = new InterArrivalSource("S", clock, new SimuLean.ConstantRandomProcess(1));
        var gate = new ItemsQueue(2, "G", clock) { Gated = true };
        var sink = new Sink("K", clock);
        GeneralLink.CreateLink(src, new List<Element> { gate });
        GeneralLink.CreateLink(gate, new List<Element> { sink });
        foreach (var e in new Element[] { src, gate, sink }) e.Start();
        clock.AdvanceClock(10.5);
        gate.ReleaseItems(100);
        clock.AdvanceClock(50);
        Check(sink.StatsCollector.TotalInput >= 40, $"the source keeps sending after the release (sink got {sink.StatsCollector.TotalInput})");
        Check(src.CurrentState != ElementState.BLOCKED, "the source is not left BLOCKED");
    }

    // A queue forwards an item inside the Receive that brought it, so the link records the exit before the entry.
    // The zero wait must still be a stay-time sample, and no entry may be left behind.
    static Dictionary<string, Element> MM1(double arrivalRate, double serviceRate, bool exponential, int seed, double until)
    {
        var cfg = new SimulationConfig();
        var s = new ElementConfig("s", "Source", "S");
        s.Set("mode", "InterArrival");
        var q = new ElementConfig("q", "Queue", "Q");
        q.Set("capacity", 1000000);
        var m = new ElementConfig("m", "MultiServer", "M");
        m.Set("capacity", 1);
        if (exponential)
        {
            s.Set("interArrivalDistribution.strategyType", "Exponential"); s.Set("interArrivalDistribution.exponentialRate", arrivalRate);
            m.Set("delayStrategy.strategyType", "Exponential"); m.Set("delayStrategy.exponentialRate", serviceRate);
        }
        else
        {
            s.Set("interArrivalDistribution.strategyType", "Constant"); s.Set("interArrivalDistribution.constantValue", 1.0 / arrivalRate);
            m.Set("delayStrategy.strategyType", "Constant"); m.Set("delayStrategy.constantValue", 1.0 / serviceRate);
        }
        cfg.Elements.Add(s); cfg.Elements.Add(q); cfg.Elements.Add(m); cfg.Elements.Add(new ElementConfig("k", "Sink", "K"));
        cfg.Connections.Add(new ConnectionConfig("s", "q")); cfg.Connections.Add(new ConnectionConfig("q", "m"));
        cfg.Connections.Add(new ConnectionConfig("m", "k"));
        var clock = new SimClock(seed);
        var factory = new SimuLean.Headless.HeadlessModelFactory(clock);
        var built = factory.BuildModel(cfg);
        factory.StartModel();
        clock.AdvanceClock(until);
        return built.Values.ToDictionary(e => e.Name);
    }

    static int TrackedEntries(Element e)
    {
        var f = typeof(SimuLean.Statistics.ElementStatsCollector).GetField("_entryTimes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return ((System.Collections.IDictionary)f.GetValue(e.StatsCollector)).Count;
    }

    static void TestQueuePassThroughStay()
    {
        // Deterministic: an arrival every 1, service 0.5 — every item passes the queue at once.
        var d = MM1(1.0, 2.0, false, 1, 100.5);
        var dq = d["Q"].StatsCollector;
        Check(dq.VarStayTime.Count == dq.TotalOutput, $"every item leaving the queue is a stay sample ({dq.VarStayTime.Count} of {dq.TotalOutput})");
        Near(dq.AvgStayTime, 0, "queue stay with no waiting");
        Check(TrackedEntries(d["Q"]) == (int)dq.ContentCurrent, $"no entry left behind ({TrackedEntries(d["Q"])} tracked, {dq.ContentCurrent} inside)");

        // M/M/1, lambda = 1, mu = 2: Wq = lambda / (mu (mu - lambda)) = 0.5 and Lq = 0.5 (Little: Wq = Lq / lambda).
        var e = MM1(1.0, 2.0, true, 3, 200000);
        var eq = e["Q"].StatsCollector;
        Check(eq.VarStayTime.Count == eq.TotalOutput, $"M/M/1: every departure is a stay sample ({eq.VarStayTime.Count} of {eq.TotalOutput})");
        Near(eq.AvgStayTime, 0.5, "M/M/1 queue stay = Wq (was 1.0 = Wq / rho)", 0.03);
        Near(eq.AvgStayTime, eq.ContentAvgTimeWeighted * eq.TotalOutput / 200000.0, "Little's law on the queue", 0.01);
        Check(eq.MinStayTime == 0, "the zero waits are in the minimum");
        Check(TrackedEntries(e["Q"]) == (int)eq.ContentCurrent, $"M/M/1: no entry left behind ({TrackedEntries(e["Q"])} tracked, {eq.ContentCurrent} inside)");
    }

    static void TestStayStdDev()
    {
        var d = MM1(1.0, 2.0, false, 1, 100.5);
        Near(d["M"].GetStatsStayTimeStdDev(), 0, "constant service: no spread");
        var e = MM1(1.0, 2.0, true, 3, 200000);
        Element m = e["M"];
        Near(m.GetStatsStayTimeMean(), 0.5, "exponential service: mean 1 / mu", 0.01);
        Near(m.GetStatsStayTimeStdDev(), 0.5, "exponential service: standard deviation = mean", 0.01);
        var t = new SimuLean.Statistics.StatTally();
        foreach (double x in new[] { 2.0, 4, 4, 4, 5, 5, 7, 9 }) t.Update(x);
        Near(t.Average, 5, "tally mean");
        Near(t.Variance, 32.0 / 7, "tally sample variance (n - 1)");
        Check(SimuLean.Optimization.MetricCatalog.ElementStatistics.Contains("stay_std")
              && SimuLean.Optimization.MetricCatalog.ListStatistics.Contains("wait_std")
              && SimuLean.Optimization.MetricCatalog.TeamStatistics.Contains("request_wait_std"), "the catalog offers the standard deviations");
    }

    static void TestQueueBlockedState()
    {
        // One part every 1 for a station that takes 3: the station is always busy, so every part the queue holds waits
        // behind a refused send — BLOCKED whenever it holds parts (it showed PROCESSING after each arrival).
        var clock = new SimClock(1);
        var src = new InterArrivalSource("Src", clock, Const(1));
        var q = new ItemsQueue(1000, "Q", clock);
        var m = new MultiServer(new[] { Const(3) }, "M", clock);
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(src, L(q)); GeneralLink.CreateLink(q, L(m)); GeneralLink.CreateLink(m, L(sink));
        foreach (var e in new Element[] { src, q, m, sink }) e.Start();
        clock.AdvanceClock(300);
        double blocked = q.GetStateRatio(ElementState.BLOCKED), processing = q.GetStateRatio(ElementState.PROCESSING);
        Check(blocked > 0.98 && processing < 0.01, $"the queue is BLOCKED while it holds parts ({blocked:P1} blocked, {processing:P1} processing)");

        // A station fast enough: the queue's parts leave at once, nothing refused — never BLOCKED.
        var c2 = new SimClock(1);
        var s2 = new InterArrivalSource("Src", c2, Const(1));
        var q2 = new ItemsQueue(1000, "Q", c2);
        var m2 = new MultiServer(new[] { Const(0.5) }, "M", c2);
        var k2 = new Sink("Sink", c2);
        GeneralLink.CreateLink(s2, L(q2)); GeneralLink.CreateLink(q2, L(m2)); GeneralLink.CreateLink(m2, L(k2));
        foreach (var e in new Element[] { s2, q2, m2, k2 }) e.Start();
        c2.AdvanceClock(100);
        Check(q2.GetStateRatio(ElementState.BLOCKED) == 0, $"never blocked when the station keeps up ({q2.GetStateRatio(ElementState.BLOCKED):P1})");
    }

    static void TestQueuePassThroughCounter()
    {
        var clock = new SimClock(1);
        var src = new Feeder(clock, 10, 1, "Src");
        var q = new ItemsQueue(100, "Q", clock);
        var m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(2) }, "M", clock);
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(src, new List<Element> { q });
        GeneralLink.CreateLink(q, new List<Element> { m });
        GeneralLink.CreateLink(m, new List<Element> { sink });
        foreach (var e in new Element[] { src, q, m, sink }) e.Start();
        src.Begin();
        clock.AdvanceClock(100);
        var k = q.Decisions;
        Check(k != null && k.PassedThrough >= 1 && k.Released >= 1, $"some passed straight through ({k?.PassedThrough}), some waited ({k?.Released})");
        Check(k.Left == m.GetStatsInputCount(), $"every item that left is counted: {k.Left} vs {m.GetStatsInputCount()}");
    }

    // ── #54 ──────────────────────────────────────────────────────────────────

    static void TestGateQueueUnblock()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 2, 0.1);
        var g = new ItemsQueue(5, "G", clock) { Gated = true };
        var outp = new Collector(clock) { Open = false };
        GeneralLink.CreateLink(feeder, L(g)); GeneralLink.CreateLink(g, L(outp));
        foreach (var e in new Element[] { feeder, g, outp }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(1);
        g.ReleaseItems(2);                                   // output closed: nothing leaves
        Check(!g.Unblock(), "Unblock with the output still closed: false (nothing sent)");
        Check(g.CurrentState == ElementState.BLOCKED, $"blocked while the released orders cannot leave ({g.CurrentState})");
        Schedule(clock, 3, () => outp.OpenNow());
        clock.AdvanceClock(5);
        Check(outp.Got.Count == 2 && g.GetQueueLength() == 0, $"both out once the output opened ({outp.Got.Count})");
        Near(g.GetTimeInState(ElementState.BLOCKED), 2, "BLOCKED time recorded (1 -> 3)");
    }

    // ── #59 ──────────────────────────────────────────────────────────────────

    static void TestHotPathBookkeeping()
    {
        var t = new ElementStateTracker(ElementState.IDLE, 0, keepLog: false);
        for (int i = 1; i <= 1000; i++) t.Transition(i % 2 == 1 ? ElementState.PROCESSING : ElementState.IDLE, i);
        Check(t.Log.Count == 1 && Math.Abs(t.GetTimeInState(ElementState.PROCESSING, 1000) - 500) < 1e-9, $"totals kept, no growing log ({t.Log.Count} entries)");

        // "any accepted" release out of a 1 000-item queue; the held items stay a live view
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 1000, 0);
        var q = new ItemsQueue(2000, "Q", clock) { OfferOrder = OfferOrder.AnyAccepted };
        var outp = new Collector(clock) { Open = false };
        GeneralLink.CreateLink(feeder, L(q)); GeneralLink.CreateLink(q, L(outp));
        foreach (var e in new Element[] { feeder, q, outp }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(1);
        var view = q.GetItems();
        Check(view.Count == 1000, $"1 000 held ({view.Count})");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) outp.OpenNow();   // one release per notification of room
        clock.AdvanceClock(2);
        Check(watch.Elapsed.TotalSeconds < 1, $"1 000 releases in linear time ({watch.Elapsed.TotalSeconds:F2} s)");
        Check(outp.Got.Count == 1000 && view.Count == 0 && q.GetQueueLength() == 0, $"all released; the view the caller holds is current ({view.Count})");
    }
}
