// Standalone tests of the lists: SimList queries / back-orders / statistics
// and the push / pull flow integration through ListLink.
// Run: dotnet run  (from StandaloneTests/listtests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Collections;

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
        Run("SimList: values, filters, orders, fields", TestQueries);
        Run("SimList: back-orders delivered by a zero-delay event, cancellable", TestBackOrders);
        Run("SimList: statistics", TestStatistics);
        Run("Flow: global priority queue across two queues", TestPriorityAcrossQueues);
        Run("Flow: filtered pulls split item kinds", TestFilteredPulls);
        Run("Flow: pull respects the puller's input strategy", TestPullInputStrategy);
        Run("Flow: stopped puller does not pull; resumes", TestPullerDowntime);
        Run("Flow: link statistics and conservation", TestFlowStatistics);
        Run("Flow: priority inside one queue (every item advertised)", TestPriorityInsideQueue);
        Run("Flow: no head-of-line blocking with filtered pulls", TestNoHeadOfLineBlocking);
        Run("Combiner: component input pulled from a list", TestCombinerComponentFromList);
        Run("Combiner: main and component inputs both from lists", TestCombinerAllFromLists);
        Run("SimList: entries without the order label rank as 0", TestMissingOrderLabel);
        Run("Fields: labels, built-ins and formulas usable in queries and orders", TestListFields);
        Run("Fields: monitored values on push and pull; invalid definitions fail", TestFieldMonitoring);
        Run("SimList: Ordered / Take / Then; values giving their own live fields", TestOrderedAndFieldSources);
        Run("Origins: a multi-slot server advertises every finished item", TestServerOrigin);
        Run("Origins: a release source advertises every pending item", TestReleaseSourceOrigin);
        Run("Origins: a stopped origin's entries wait until it resumes", TestStoppedOrigin);

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

    /// <summary>Emits items at fixed intervals with a label; holds refused items, retries on Unblock.</summary>
    sealed class Feeder : Element, IEvent
    {
        readonly Queue<Item> held = new Queue<Item>();
        readonly double interval;
        readonly Action<Item, int> label;
        int remaining, n;
        readonly string prefix;
        public Feeder(SimClock c, string name, int count, double interval, Action<Item, int> label) : base(name, c)
        { remaining = count; this.interval = interval; this.label = label; prefix = name; }
        public void Begin(double at = 0) => simClock.ScheduleEvent(this, at);
        public void Execute()
        {
            var it = new Item(simClock.GetSimulationTime());
            it.SetId(prefix, ++n, 0);
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
        public override int GetQueueLength() => held.Count;
        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item i) => false;
        public override bool Receive(Item i) => false;
    }

    sealed class Collector : Element
    {
        public readonly List<Item> Got = new List<Item>();
        public Collector(SimClock c, string name = "Collector") : base(name, c) { }
        public override bool Receive(Item i) { Got.Add(i); return true; }
        public override bool CheckAvailability(Item i) => true;
        public override bool Unblock() => false;
        public override int GetQueueLength() => Got.Count;
        public override int GetFreeCapacity() => -1;
    }

    sealed class At : IEvent { readonly Action a; public At(Action a) { this.a = a; } public void Execute() => a(); }
    static void Schedule(SimClock c, double t, Action a) => c.ScheduleEvent(new At(a), t - c.GetSimulationTime());
    static IRandomSampler Const(double v) => new ConstantRandomProcess(v);
    static List<Element> L(params Element[] e) => new List<Element>(e);
    static string Ids(IEnumerable<Item> items) => string.Join(",", items.Select(i => i.GetTypeName() + i.GetId()));

    // ── SimList ──────────────────────────────────────────────────────────────

    static Dictionary<string, object> F(params (string k, object v)[] kv) => kv.ToDictionary(p => p.k, p => p.v);

    static void TestQueries()
    {
        var clock = new SimClock(1);
        var list = new SimList("orders", clock);
        list.PushValue("o1", F(("qty", 3), ("customer", "acme")));
        list.PushValue("o2", F(("qty", 9), ("customer", "beta")));
        list.PushValue("o3", F(("qty", 7), ("customer", "acme")));

        Check(list.TryPeek(null, out var first) && (string)first.Value == "o1", "default order is FIFO");
        Check(list.TryPeek(ListQuery.All.OrderBy("qty", descending: true), out var big) && (string)big.Value == "o2", "OrderBy qty desc");
        Check(list.TryPeek(ListQuery.All.Where("customer", ListOp.Equal, "acme").OrderBy("qty", true), out var acme) && (string)acme.Value == "o3",
              "text filter + order");
        Check(list.TryPeek(ListQuery.All.Where("qty", ListOp.Greater, 10), out _) == false, "no match");
        list.DefineField("double", e => list.GetNumber(e, "qty") * 2);
        Check(list.TryPeek(ListQuery.All.Where("double", ListOp.Equal, 14), out var calc) && (string)calc.Value == "o3", "calculated field");
        list.SetDefaultOrder("qty");
        Check(list.TryPull(null, out var low) && (string)low.Value == "o1", "default order by qty");
        Check(list.Count == 2, "pulled entry removed");
        Check(ListQuerySpec.Decode("qty > 5;customer == acme|qty desc").ToQuery() != null
              && ListQuerySpec.Decode("qty > 5;customer == acme|qty desc").Encode() == "qty > 5;customer == acme|qty desc", "spec codec round trip");
    }

    static void TestBackOrders()
    {
        var clock = new SimClock(1);
        var list = new SimList("l", clock);
        string got = null; double at = -1;
        var pull = list.Pull(ListQuery.All.Where("qty", ListOp.Greater, 5), e => { got = (string)e.Value; at = clock.GetSimulationTime(); });
        Check(pull != null && pull.IsPending && list.PendingPullCount == 1, "pull pending on an empty list");
        Schedule(clock, 2, () =>
        {
            list.PushValue("small", F(("qty", 1)));
            list.PushValue("big", F(("qty", 8)));
            Check(got == null, "delivery not synchronous with the push");
        });
        clock.AdvanceClock(3);
        Check(got == "big" && at == 2, $"matching push delivered at the same time ({got} at {at})");
        Check(list.Count == 1, "non-matching entry stays");

        string got2 = null;
        var pull2 = list.Pull(ListQuery.All.Where("qty", ListOp.Greater, 50), e => got2 = (string)e.Value);
        pull2.Cancel();
        list.PushValue("huge", F(("qty", 99)));
        clock.AdvanceClock(4);
        Check(got2 == null && list.Count == 2, "cancelled pull gets nothing; entry stays");
        Check(list.PendingPullCount == 0 && list.PendingPulls.Value == 0, $"cancelled pull no longer pending ({list.PendingPulls.Value})");

        // Cancel between match and delivery: the entry returns to the list.
        string got3 = null;
        int pulledBefore = list.Pulled, waitsBefore = list.WaitTime.Count, pushedBefore = list.Pushed;
        var pull3 = list.Pull(ListQuery.All.Where("qty", ListOp.Equal, 42), e => got3 = (string)e.Value);
        list.PushValue("answer", F(("qty", 42)));
        double pushedAt = clock.GetSimulationTime();
        pull3.Cancel();
        clock.AdvanceClock(5);
        Check(got3 == null && list.Entries.Any(e => (string)e.Value == "answer"), "cancelled after match: entry back in the list");
        // No pull and no wait recorded, the entry keeps its push time, it is not pushed twice
        var back = list.Entries.First(e => (string)e.Value == "answer");
        Check(list.Pulled == pulledBefore && list.WaitTime.Count == waitsBefore && list.Pushed == pushedBefore + 1 && back.PushTime == pushedAt,
              $"cancelled after match: statistics untouched, push time kept (pulled {list.Pulled - pulledBefore}, waits {list.WaitTime.Count - waitsBefore}, pushed {list.Pushed - pushedBefore}, push time {back.PushTime} vs {pushedAt})");
    }

    static void TestStatistics()
    {
        var clock = new SimClock(1);
        var list = new SimList("l", clock);
        Schedule(clock, 0, () => list.PushValue("a"));
        Schedule(clock, 10, () => list.PushValue("b"));
        Schedule(clock, 20, () => { list.TryPull(null, out _); list.TryPull(null, out _); });
        clock.AdvanceClock(40);
        Near(list.Content.GetTimeWeightedAverage(40), (10 * 1 + 10 * 2) / 40.0, "time-weighted content");
        Near(list.Content.Max, 2, "peak content");
        Near(list.WaitTime.Average, (20 + 10) / 2.0, "mean waiting time");
        Check(list.Pushed == 2 && list.Pulled == 2, "push / pull counters");
        Check(clock.Context.GetList("l") == list, "registered in the model context");
    }

    // ── flow ─────────────────────────────────────────────────────────────────

    static void TestPriorityAcrossQueues()
    {
        var clock = new SimClock(1);
        var list = new SimList("work", clock);
        var fa = new Feeder(clock, "A", 3, 0, (it, n) => it.SetLabelValue("prio", 1.0));
        var fb = new Feeder(clock, "B", 3, 0, (it, n) => it.SetLabelValue("prio", 5.0));
        var qa = new ItemsQueue(10, "QA", clock);
        var qb = new ItemsQueue(10, "QB", clock);
        var m = new MultiServer(new[] { Const(2) }, "M", clock);
        var sink = new Collector(clock);
        GeneralLink.CreateLink(fa, L(qa)); GeneralLink.CreateLink(fb, L(qb));
        var link = new ListLink(list);
        link.AddOrigin(qa); link.AddOrigin(qb);
        link.AddPuller(m, ListQuery.All.OrderBy("prio", descending: true));
        GeneralLink.CreateLink(m, L(sink));
        foreach (var e in new Element[] { fa, fb, qa, qb, m, sink }) e.Start();
        link.Start();
        fa.Begin(0); fb.Begin(0.1);
        clock.AdvanceClock(100);
        // M takes A1 at t=0 (only item); then the highest priority head each time it frees up.
        Check(Ids(sink.Got) == "A1,B1,B2,B3,A2,A3", "served by priority across queues: " + Ids(sink.Got));
        Check(qa.GetQueueLength() == 0 && qb.GetQueueLength() == 0 && list.Count == 0, "everything consumed");
    }

    static void TestFilteredPulls()
    {
        var clock = new SimClock(1);
        var list = new SimList("work", clock);
        var f = new Feeder(clock, "F", 6, 1, (it, n) => it.SetLabelValue("kind", n % 2 == 0 ? 2.0 : 1.0));
        var q = new ItemsQueue(10, "Q", clock);
        var s1 = new MultiServer(new[] { Const(0.5) }, "S1", clock);
        var s2 = new MultiServer(new[] { Const(0.5) }, "S2", clock);
        var c1 = new Collector(clock, "C1"); var c2 = new Collector(clock, "C2");
        GeneralLink.CreateLink(f, L(q));
        var link = new ListLink(list);
        link.AddOrigin(q);
        link.AddPuller(s1, ListQuery.All.Where("kind", ListOp.Equal, 1));
        link.AddPuller(s2, ListQuery.All.Where("kind", ListOp.Equal, 2));
        GeneralLink.CreateLink(s1, L(c1)); GeneralLink.CreateLink(s2, L(c2));
        foreach (var e in new Element[] { f, q, s1, s2, c1, c2 }) e.Start();
        link.Start();
        f.Begin();
        clock.AdvanceClock(50);
        Check(c1.Got.Count == 3 && c1.Got.All(i => i.GetLabelValue("kind") == 1), "S1 got only kind 1: " + Ids(c1.Got));
        Check(c2.Got.Count == 3 && c2.Got.All(i => i.GetLabelValue("kind") == 2), "S2 got only kind 2: " + Ids(c2.Got));
    }

    static void TestPullInputStrategy()
    {
        var clock = new SimClock(1);
        var list = new SimList("work", clock);
        var fa = new Feeder(clock, "A", 2, 1, null);
        var fb = new Feeder(clock, "B", 2, 1, null);
        var qa = new ItemsQueue(10, "QA", clock); var qb = new ItemsQueue(10, "QB", clock);
        var m = new MultiServer(new[] { Const(0.1) }, "M", clock, new OriginNameInputStrategy("QB"));
        var sink = new Collector(clock);
        GeneralLink.CreateLink(fa, L(qa)); GeneralLink.CreateLink(fb, L(qb));
        var link = new ListLink(list);
        link.AddOrigin(qa); link.AddOrigin(qb);
        link.AddPuller(m);
        GeneralLink.CreateLink(m, L(sink));
        foreach (var e in new Element[] { fa, fb, qa, qb, m, sink }) e.Start();
        link.Start();
        fa.Begin(0); fb.Begin(0.5);
        clock.AdvanceClock(20);
        Check(Ids(sink.Got) == "B1,B2", "only items from QB pulled: " + Ids(sink.Got));
        Check(qa.GetQueueLength() == 2, "QA's items wait");
    }

    static void TestPullerDowntime()
    {
        var clock = new SimClock(1);
        var list = new SimList("work", clock);
        var f = new Feeder(clock, "F", 3, 1, null);
        var q = new ItemsQueue(10, "Q", clock);
        var m = new MultiServer(new[] { Const(0.5) }, "M", clock);
        var sink = new Collector(clock);
        GeneralLink.CreateLink(f, L(q));
        var link = new ListLink(list);
        link.AddOrigin(q); link.AddPuller(m);
        GeneralLink.CreateLink(m, L(sink));
        foreach (var e in new Element[] { f, q, m, sink }) e.Start();
        StopToken stop = m.Stop(new StopRequest(ElementState.STOPPED));
        link.Start();
        f.Begin();
        Schedule(clock, 10, () => m.Resume(stop));
        clock.AdvanceClock(9);
        Check(sink.Got.Count == 0 && q.GetQueueLength() == 3, "stopped puller takes nothing");
        clock.AdvanceClock(20);
        Check(sink.Got.Count == 3, $"all served after resume ({sink.Got.Count})");
    }

    static void TestPriorityInsideQueue()
    {
        var clock = new SimClock(1);
        var list = new SimList("work", clock);
        int[] prios = { 1, 3, 2, 5, 4 };
        var f = new Feeder(clock, "F", 5, 0, (it, n) => it.SetLabelValue("prio", prios[n - 1]));
        var q = new ItemsQueue(10, "Q", clock);
        var m = new MultiServer(new[] { Const(1) }, "M", clock);
        var sink = new Collector(clock);
        GeneralLink.CreateLink(f, L(q));
        var link = new ListLink(list);
        link.AddOrigin(q); link.AddPuller(m, ListQuery.All.OrderBy("prio", true));
        GeneralLink.CreateLink(m, L(sink));
        foreach (var e in new Element[] { f, q, m, sink }) e.Start();
        link.Start();
        f.Begin();
        clock.AdvanceClock(20);
        // F1 (prio 1) is taken at once by the waiting server; then 5, 4, 3, 2.
        string order = string.Join(",", sink.Got.Select(i => i.GetLabelValue("prio")));
        Check(order == "1,5,4,3,2", "served by priority within the queue: " + order);
        Check(q.GetQueueLength() == 0 && q.GetStatsOutputCount() == 5, "queue emptied, outputs counted");
    }

    static void TestNoHeadOfLineBlocking()
    {
        var clock = new SimClock(1);
        var list = new SimList("work", clock);
        // Head is kind 2 (its server is slow); the kind-1 items behind it must not wait for it.
        int[] kinds = { 2, 2, 1, 1, 1 };
        var f = new Feeder(clock, "F", 5, 0, (it, n) => it.SetLabelValue("kind", kinds[n - 1]));
        var q = new ItemsQueue(10, "Q", clock);
        var s1 = new MultiServer(new[] { Const(1) }, "S1", clock);
        var s2 = new MultiServer(new[] { Const(100) }, "S2", clock);
        var c1 = new Collector(clock, "C1"); var c2 = new Collector(clock, "C2");
        GeneralLink.CreateLink(f, L(q));
        var link = new ListLink(list);
        link.AddOrigin(q);
        link.AddPuller(s1, ListQuery.All.Where("kind", ListOp.Equal, 1));
        link.AddPuller(s2, ListQuery.All.Where("kind", ListOp.Equal, 2));
        GeneralLink.CreateLink(s1, L(c1)); GeneralLink.CreateLink(s2, L(c2));
        foreach (var e in new Element[] { f, q, s1, s2, c1, c2 }) e.Start();
        link.Start();
        f.Begin();
        clock.AdvanceClock(10);
        Check(c1.Got.Count == 3, $"all kind-1 items served while kind 2 is busy ({c1.Got.Count})");
        Check(q.GetQueueLength() == 1, "only the second kind-2 item waits");
    }

    static void TestCombinerComponentFromList()
    {
        var clock = new SimClock(1);
        var list = new SimList("parts", clock);
        var fm = new Feeder(clock, "M", 3, 5, null);
        var fp = new Feeder(clock, "P", 6, 0, (it, n) => it.SetLabelValue("prio", n));   // P6 highest
        var qp = new ItemsQueue(10, "QP", clock);
        var comb = new Combiner(new[] { 2 }, Const(1), "C", clock, batchMode: true);
        var sink = new Collector(clock);
        GeneralLink.CreateLink(fm, L(comb));          // main input: a connection
        GeneralLink.CreateLink(fp, L(qp));
        var link = new ListLink(list);
        link.AddOrigin(qp);
        link.AddPuller(comb.GetInputPort(1), ListQuery.All.OrderBy("prio", descending: true));
        GeneralLink.CreateLink(comb, L(sink));
        foreach (var e in new Element[] { fm, fp, qp, comb, sink }) e.Start();
        link.Start();
        fp.Begin(0); fm.Begin(0.5);
        clock.AdvanceClock(100);
        Check(sink.Got.Count == 3, $"3 assemblies ({sink.Got.Count})");
        Check(qp.GetQueueLength() == 0 && list.Count == 0, "every part consumed");
        Check(qp.GetStatsOutputCount() == 6, $"parts counted out of the queue ({qp.GetStatsOutputCount()})");
    }

    static void TestCombinerAllFromLists()
    {
        var clock = new SimClock(1);
        var mains = new SimList("mains", clock);
        var parts = new SimList("parts", clock);
        var fm = new Feeder(clock, "M", 4, 2, null);
        var fp = new Feeder(clock, "P", 4, 1, null);
        var qm = new ItemsQueue(10, "QM", clock);
        var qp = new ItemsQueue(10, "QP", clock);
        var comb = new Combiner(new[] { 1 }, Const(0.5), "C", clock, batchMode: true);
        var sink = new Collector(clock);
        GeneralLink.CreateLink(fm, L(qm)); GeneralLink.CreateLink(fp, L(qp));
        var lm = new ListLink(mains); lm.AddOrigin(qm); lm.AddPuller(comb.GetInputPort(0), null);
        var lp = new ListLink(parts); lp.AddOrigin(qp); lp.AddPuller(comb.GetInputPort(1), null);
        GeneralLink.CreateLink(comb, L(sink));
        foreach (var e in new Element[] { fm, fp, qm, qp, comb, sink }) e.Start();
        lm.Start(); lp.Start();
        fm.Begin(0); fp.Begin(0);
        clock.AdvanceClock(100);
        Check(sink.Got.Count == 4, $"4 assemblies ({sink.Got.Count})");
        Check(mains.Count == 0 && parts.Count == 0 && qm.GetQueueLength() == 0 && qp.GetQueueLength() == 0, "both lists emptied");
    }

    static void TestMissingOrderLabel()
    {
        var clock = new SimClock(1);
        var list = new SimList("l", clock);
        list.SetDefaultOrder("prio", true);
        list.PushValue("none", F(("x", 1)));
        list.PushValue("high", F(("prio", 3)));
        list.PushValue("neg", F(("prio", -1)));
        Check(list.TryPull(null, out var a) && (string)a.Value == "high", "highest first");
        Check(list.TryPull(null, out var b) && (string)b.Value == "none", "missing label ranks as 0");
        Check(list.TryPull(null, out var c) && (string)c.Value == "neg", "negative last");
    }

    static void TestListFields()
    {
        var clock = new SimClock(1);
        var list = new SimList("l", clock);
        list.DefineFields(new[]
        {
            new ListFieldSpec("priority", ListFieldKind.Label, "prio"),
            new ListFieldSpec("waited", ListFieldKind.BuiltIn, "age"),
            new ListFieldSpec("score", ListFieldKind.Expression, "priority * 10 + waited"),
            new ListFieldSpec("urgent", ListFieldKind.Expression, "score >= 30"),
        });
        list.PushValue("a", F(("prio", 1)));
        Schedule(clock, 5, () => list.PushValue("b", F(("prio", 2))));
        clock.AdvanceClock(6);   // a: age 6, score 16; b: age 1, score 21
        Check(list.TryPeek(ListQuery.All.OrderBy("score", true), out var best) && (string)best.Value == "b", "order by a formula");
        Check(list.TryPeek(ListQuery.All.OrderBy("waited", true), out var old) && (string)old.Value == "a", "order by a built-in alias");
        Check(list.TryPeek(ListQuery.All.Where("priority", ListOp.Equal, 1), out var p1) && (string)p1.Value == "a", "filter by a label alias");
        Check(!list.TryPeek(ListQuery.All.Where("urgent", ListOp.Equal, 1), out _), "nobody urgent yet");
        clock.AdvanceClock(20);  // t = 26: a age 26 score 36, b age 21 score 41 — both urgent
        Check(list.TryPeek(ListQuery.All.Where("urgent", ListOp.Equal, 1).OrderBy("score", true), out var u) && (string)u.Value == "b", "formula over a formula");
        Check(list.Fields.Count == 4, "fields listed");
    }

    static void TestFieldMonitoring()
    {
        var clock = new SimClock(1);
        var list = new SimList("l", clock);
        list.DefineFields(new[] { new ListFieldSpec("prio", ListFieldKind.Label), new ListFieldSpec("waited", ListFieldKind.BuiltIn, "age", monitor: false) });
        list.PushValue("a", F(("prio", 2)));
        list.PushValue("b", F(("prio", 4)));
        clock.AdvanceClock(3);
        list.TryPull(ListQuery.All.OrderBy("prio", true), out _);
        var s = list.FieldStatistics;
        Check(s.Count == 1 && s.ContainsKey("prio"), "only monitored fields recorded");
        Near(s["prio"].AtPush.Average, 3, "average on push");
        Near(s["prio"].AtPull.Average, 4, "value on pull");
        list.Reset();
        Check(s["prio"].AtPush.Count == 0, "reset clears field statistics");

        void Bad(string what, params ListFieldSpec[] specs)
        {
            bool threw = false;
            try { new SimList("x", clock).DefineFields(specs); } catch (Exception ex) when (ex is ArgumentException || ex is FormatException) { threw = true; }
            Check(threw, what);
        }
        Bad("empty name", new ListFieldSpec("", ListFieldKind.Label, "p"));
        Bad("duplicate", new ListFieldSpec("p", ListFieldKind.Label), new ListFieldSpec("p", ListFieldKind.Label));
        Bad("unknown built-in", new ListFieldSpec("w", ListFieldKind.BuiltIn, "speed"));
        Bad("bad formula", new ListFieldSpec("f", ListFieldKind.Expression, "1 +"));
        Bad("self reference", new ListFieldSpec("f", ListFieldKind.Expression, "f + 1"));
    }

    sealed class Live : IListFieldSource
    {
        public double Load; public string Kind;
        public double GetListNumber(string f) => f == "load" ? Load : double.NaN;
        public string GetListText(string f) => f == "kind" ? Kind : null;
    }

    static void TestOrderedAndFieldSources()
    {
        var clock = new SimClock(1);
        var list = new SimList("l", clock);
        var a = new Live { Load = 3, Kind = "x" }; var b = new Live { Load = 1, Kind = "y" }; var c = new Live { Load = 3, Kind = "x" };
        list.PushValue(a); list.PushValue(b); list.PushValue(c);
        var byLoad = list.Ordered(ListQuery.All.OrderBy("load"));
        Check(byLoad[0].Value == b && byLoad[1].Value == a && byLoad[2].Value == c, "ordered by a live field, ties stay FIFO");
        b.Load = 9;   // read live: the order changes with the value
        Check(list.Ordered(ListQuery.All.OrderBy("load"))[2].Value == b, "fields read live from the value");
        var xs = list.Ordered(ListQuery.All.Where("kind", ListOp.Equal, "x").Then(ListQuery.All.OrderBy("load", true)));
        Check(xs.Count == 2 && xs[0].Value == a, "Then: filters of both, orders appended");
        clock.AdvanceClock(2);
        Check(list.Take(xs[1]) && !list.Take(xs[1]) && list.Count == 2 && list.Pulled == 1, "Take removes a given entry once, as a pull");
        Near(list.WaitTime.Average, 2, "Take records the waiting time");
    }

    static void TestServerOrigin()
    {
        var clock = new SimClock(1);
        var list = new SimList("done", clock);
        int[] prios = { 1, 3, 2 };
        var f = new Feeder(clock, "F", 3, 0, (it, n) => it.SetLabelValue("prio", prios[n - 1]));
        var m = new MultiServer(new[] { Const(1), Const(1), Const(1) }, "M", clock);   // 3 slots
        var p = new MultiServer(new[] { Const(1) }, "P", clock);
        var sink = new Collector(clock);
        GeneralLink.CreateLink(f, L(m));
        var link = new ListLink(list);
        link.AddOrigin(m); link.AddPuller(p, ListQuery.All.OrderBy("prio", true));
        GeneralLink.CreateLink(p, L(sink));
        foreach (var e in new Element[] { f, m, p, sink }) e.Start();
        StopToken stop = p.Stop(new StopRequest(ElementState.STOPPED));   // the puller waits until all 3 are done
        link.Start();
        f.Begin();
        Schedule(clock, 2, () => p.Resume(stop));
        clock.AdvanceClock(20);
        string order = string.Join(",", sink.Got.Select(i => i.GetLabelValue("prio")));
        Check(order == "3,2,1", "finished items of one server served by priority: " + order);
        Check(m.GetQueueLength() == 0 && m.GetStatsOutputCount() == 3, "server emptied, outputs counted");
        Check(list.Pushed == 3 && list.Count == 0, $"each item listed once (pushed {list.Pushed})");
    }

    static void TestReleaseSourceOrigin()
    {
        var clock = new SimClock(1);
        var list = new SimList("orders", clock);
        var rs = new ReleaseSource("RS", clock);
        var p = new MultiServer(new[] { Const(1) }, "P", clock);
        var sink = new Collector(clock);
        var link = new ListLink(list);
        link.AddOrigin(rs); link.AddPuller(p, ListQuery.All.OrderBy("prio", true));
        GeneralLink.CreateLink(p, L(sink));
        foreach (var e in new Element[] { rs, p, sink }) e.Start();
        StopToken stop = p.Stop(new StopRequest(ElementState.STOPPED));
        link.Start();
        int n = 0;
        foreach (int prio in new[] { 2, 5, 1, 4 })
        {
            var it = new Item(0); it.SetId("O", ++n, 0); it.SetLabelValue("prio", prio);
            rs.Enqueue(it);
        }
        Schedule(clock, 1, () => p.Resume(stop));
        clock.AdvanceClock(20);
        string order = string.Join(",", sink.Got.Select(i => i.GetLabelValue("prio")));
        Check(order == "5,4,2,1", "pending items of a release source served by priority: " + order);
        Check(rs.PendingCount == 0 && rs.ReleasedCount == 4, $"all released ({rs.ReleasedCount})");
    }

    static void TestStoppedOrigin()
    {
        var clock = new SimClock(1);
        var list = new SimList("work", clock);
        var f = new Feeder(clock, "F", 3, 0, null);
        var q = new ItemsQueue(10, "Q", clock);
        var m = new MultiServer(new[] { Const(1) }, "M", clock);
        var sink = new Collector(clock);
        GeneralLink.CreateLink(f, L(q));
        var link = new ListLink(list);
        link.AddOrigin(q); link.AddPuller(m);
        GeneralLink.CreateLink(m, L(sink));
        foreach (var e in new Element[] { f, q, m, sink }) e.Start();
        link.Start();
        f.Begin();                                    // F1 goes to M at once, F2 / F3 wait in Q
        StopToken stop = null;
        Schedule(clock, 0.5, () => stop = q.Stop(new StopRequest(ElementState.STOPPED)));
        Schedule(clock, 5, () => q.Resume(stop));
        clock.AdvanceClock(4.9);
        Check(sink.Got.Count == 1 && q.GetQueueLength() == 2, $"nothing pulled from the stopped queue ({sink.Got.Count})");
        clock.AdvanceClock(20);
        Check(sink.Got.Count == 3 && q.GetQueueLength() == 0, $"served after the queue resumed ({sink.Got.Count})");
    }

    static void TestFlowStatistics()
    {
        var clock = new SimClock(1);
        var list = new SimList("work", clock);
        var f = new Feeder(clock, "F", 10, 1, (it, n) => it.SetLabelValue("prio", n % 3));
        var q = new ItemsQueue(100, "Q", clock);
        var m = new MultiServer(new[] { Const(1.5) }, "M", clock);
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(f, L(q));
        var link = new ListLink(list);
        link.AddOrigin(q); link.AddPuller(m, ListQuery.All.OrderBy("prio", true));
        GeneralLink.CreateLink(m, L(sink));
        foreach (var e in new Element[] { f, q, m, sink }) e.Start();
        link.Start();
        f.Begin();
        clock.AdvanceClock(100);
        Check(q.GetStatsInputCount() == 10 && q.GetStatsOutputCount() == 10, "queue in / out counted through the links");
        Check(m.GetStatsInputCount() == 10 && sink.GetStatsInputCount() == 10, "server and sink counted");
        Near(q.GetStatsContentCurrent(), 0, "queue content back to 0");
        Check(list.Pushed == 10 && list.Pulled == 10 && list.Count == 0, $"list pushed {list.Pushed} / pulled {list.Pulled}");
    }
}
