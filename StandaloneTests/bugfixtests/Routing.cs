// Routing: merges, input strategies, round robin, shortest queue, probability and expression routing, fair turns, repeats.
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Serialization;

static partial class Program
{
    // Three feeders, each with its OWN link into one small queue (as the factory and Unity wire a merge).
    // Before the fix only the last link was the queue's input: the first two feeders, once refused, were
    // never told about free room again and kept their items for ever.
    static void TestMergeUnblocksEveryUpstream()
    {
        var clock = new SimClock(1);
        var feeders = new[] { new Feeder(clock, 10, 0, "F1"), new Feeder(clock, 10, 0, "F2"), new Feeder(clock, 10, 0, "F3") };
        var q = new ItemsQueue(2, "Q", clock);
        var m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock);
        var sink = new Sink("Sink", clock);
        foreach (var f in feeders) GeneralLink.CreateLink(f, new List<Element> { q });
        GeneralLink.CreateLink(q, new List<Element> { m });
        GeneralLink.CreateLink(m, new List<Element> { sink });
        foreach (var e in new Element[] { feeders[0], feeders[1], feeders[2], q, m, sink }) e.Start();
        foreach (var f in feeders) f.Begin();
        clock.AdvanceClock(100);
        Check(sink.GetStatsInputCount() == 30, $"all 30 items served ({sink.GetStatsInputCount()})");
        Check(feeders.All(f => f.Held == 0), "no feeder keeps items");
        // Fair turns: while all three wait, freed room goes round robin (F1 also got the very first item,
        // which needed no notification): served in the order F1 F2 F1 F2 F3 F1 F2 F3 … — nobody starves.
        clock = new SimClock(1);
        feeders = new[] { new Feeder(clock, 10, 0, "F1"), new Feeder(clock, 10, 0, "F2"), new Feeder(clock, 10, 0, "F3") };
        q = new ItemsQueue(1, "Q", clock);
        m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock);
        var col = new Collector(clock);
        foreach (var f in feeders) GeneralLink.CreateLink(f, new List<Element> { q });
        GeneralLink.CreateLink(q, new List<Element> { m });
        GeneralLink.CreateLink(m, new List<Element> { col });
        foreach (var e in new Element[] { feeders[0], feeders[1], feeders[2], q, m, col }) e.Start();
        foreach (var f in feeders) f.Begin();
        clock.AdvanceClock(15.5);
        var served = feeders.Select(f => col.Got.Count(i => i.Name == f.Name)).ToArray();
        Check(string.Join(",", served) == "6,5,4", $"round-robin turns between upstream elements ({string.Join(",", served)})");
    }

    // ── #3 / #4 ──────────────────────────────────────────────────────────────

    static void TestRoutingInputStrategy()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 1, 1);
        var q0 = new ItemsQueue(10, "Q0", clock, new OriginNameInputStrategy("Nobody"));
        var q1 = new ItemsQueue(10, "Q1", clock);
        var o0 = new Collector(clock, "O0") { Open = false };
        var o1 = new Collector(clock, "O1") { Open = false };
        GeneralLink.CreateLink(feeder, L(q0, q1));
        GeneralLink.CreateLink(q0, L(o0)); GeneralLink.CreateLink(q1, L(o1));
        foreach (var e in new Element[] { feeder, q0, q1, o0, o1 }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(5);
        Check(q1.GetQueueLength() == 1 && q0.GetQueueLength() == 0 && feeder.Held == 0,
              $"item routed to Q1 (Q0 {q0.GetQueueLength()}, Q1 {q1.GetQueueLength()}, held {feeder.Held})");
    }

    // ── #6 ───────────────────────────────────────────────────────────────────

    static void TestParameterizedRoundRobin()
    {
        var clock = new SimClock(1);
        clock.Context.ModelParameters["route"] = "round_robin";
        var feeder = new Feeder(clock, 4, 1);
        feeder.SetOutputStrategy(new ParameterizedRoutingStrategy("route"));
        var c0 = new Collector(clock, "C0");
        var c1 = new Collector(clock, "C1");
        GeneralLink.CreateLink(feeder, L(c0, c1));
        foreach (var e in new Element[] { feeder, c0, c1 }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(10);
        Check(c0.Got.Count == 2 && c1.Got.Count == 2, $"alternates 2/2 (got {c0.Got.Count}/{c1.Got.Count})");
    }

    // ── #92 ──────────────────────────────────────────────────────────────────

    static void TestShortestQueueTiesInTurn()
    {
        // The rule on its own: equal (empty) outputs are taken in turn, the first decision by the first output.
        var c0 = new SimClock(1);
        var outs = new List<Element> { new ItemsQueue(10, "q1", c0), new ItemsQueue(10, "q2", c0), new ItemsQueue(10, "q3", c0) };
        foreach (var e in outs) e.Start();
        var rule = new ShortestQueueStrategy();
        var picks = new List<int>();
        for (int k = 0; k < 6; k++) picks.Add(rule.SelectOutput(outs, new Item(0), null, null));
        Check(string.Join(",", picks) == "0,1,2,0,1,2", $"ties go in turn after the last chosen output ({string.Join(",", picks)})");
        var shorter = new ShortestQueueStrategy();
        outs[1].Receive(new Item(0));
        Check(shorter.SelectOutput(outs, new Item(0), null, null) == 0 && shorter.SelectOutput(outs, new Item(0), null, null) == 2,
              "the shortest queue still wins; the tie between the others goes in turn");

        // A source every 1 → two equal servers (0.5 each, always free on an arrival): they share the work.
        var clock = new SimClock(1);
        var src = new InterArrivalSource("S", clock, new SimuLean.ConstantRandomProcess(1));
        var a = new MultiServer(new[] { Const(0.5) }, "A", clock);
        var b = new MultiServer(new[] { Const(0.5) }, "B", clock);
        var sink = new Sink("K", clock);
        src.SetOutputStrategy(new ShortestQueueStrategy());
        GeneralLink.CreateLink(src, new List<Element> { a, b });
        GeneralLink.CreateLink(a, new List<Element> { sink });
        GeneralLink.CreateLink(b, new List<Element> { sink });
        foreach (var e in new Element[] { src, a, b, sink }) e.Start();
        clock.AdvanceClock(100.5);
        double na = a.GetStatsOutputCount(), nb = b.GetStatsOutputCount();
        Check(Math.Abs(na - nb) <= 1 && na + nb >= 99, $"two equal servers share the ties ({na} / {nb}; it was every item to the first)");
    }

    // ── #75 ──────────────────────────────────────────────────────────────────

    sealed class OnlyFrom : InputStrategy
    {
        readonly Element allowed;
        public OnlyFrom(Element allowed) { this.allowed = allowed; }
        public override bool Admits(Element target, Item item, Element origin) => origin == allowed;
    }

    static void TestFairStart()
    {
        // two origins always waiting, one 1-slot station behind them: every freed place went to the first origin, so
        // the second waited until the first ran out; now they take turns
        (int a, int b) Race(bool onlyB)
        {
            var clock = new SimClock(1);
            var fa = new Feeder(clock, 40, 0, "A");
            var fb = new Feeder(clock, 40, 0, "B");
            var d = new MultiServer(new[] { Const(1) }, "D", clock);
            var outp = new Collector(clock);
            GeneralLink.CreateLink(L(fa, fb), L(d)); GeneralLink.CreateLink(d, L(outp));
            if (onlyB) d.SetInputStrategy(new OnlyFrom(fb));
            foreach (var e in new Element[] { fa, fb, d, outp }) e.Start();
            fa.Begin(); fb.Begin();
            clock.AdvanceClock(20.5);
            return ((int)fa.GetStatsOutputCount(), (int)fb.GetStatsOutputCount());
        }
        var (a1, b1) = Race(false);
        Check(Math.Abs(a1 - b1) <= 1 && a1 + b1 == 21, $"the origins take turns (A {a1}, B {b1}; before: A 21, B 0)");
        var (a2, b2) = Race(true);
        Check(a2 == 0 && b2 == 21, $"the oldest origin's item refused by the input strategy: the next one is served (A {a2}, B {b2})");
    }

    // #93: rand() < 0.5 ? 0 : 1 after a station, output 0 a slow server, output 1 a
    // sink. Before, a blocked item was routed again with a new draw at every offer and fell to the free sink more often
    // (the realised share of output 0 well below 50 %); its draw is now kept until it leaves.
    static void TestExpressionRoutingDrawKept()
    {
        (int slow, int fast, int kept) Run(int seed)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = seed, MaxSimulationTime = 20000 };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Constant");
            src.Set("interArrivalDistribution.constantValue", 1.0);
            var st = new SimuLean.Serialization.ElementConfig("st", "MultiServer", "st");
            st.Set("capacity", 1); st.Set("delayStrategy.strategyType", "Constant"); st.Set("delayStrategy.constantValue", 1.0);
            st.Set("outputStrategy.strategyType", "Expression");
            st.Set("outputStrategy.routingExpression", "rand() < 0.5 ? 0 : 1");
            var slow = new SimuLean.Serialization.ElementConfig("slow", "MultiServer", "slow");
            slow.Set("capacity", 1); slow.Set("delayStrategy.strategyType", "Constant"); slow.Set("delayStrategy.constantValue", 2.5);
            c.Elements.Add(src); c.Elements.Add(st); c.Elements.Add(slow);
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("fast", "Sink", "fast"));
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("out", "Sink", "out"));
            foreach (var (f, t) in new[] { ("src", "st"), ("st", "slow"), ("st", "fast"), ("slow", "out") })
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(f, t, "General"));
            var clock = new SimClock(seed);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            clock.AdvanceClock(20000);
            var routing = (ExpressionRoutingStrategy)model.Find("st").GetOutputStrategy();
            return ((int)model.Find("slow").GetStatsInputCount(), (int)model.Find("fast").GetStatsInputCount(), routing.Remembered);
        }
        var r = Run(5);
        double share = r.slow / (double)(r.slow + r.fast);
        Check(r.slow + r.fast > 5000, $"items routed ({r.slow + r.fast})");
        Check(Math.Abs(share - 0.5) < 0.02, $"share of the busy output stays 50 % ({share:0.000}; redrawn on every offer it fell to about 0.4 or less)");
        Check(r.kept <= 1, $"draws forgotten once the items left (kept {r.kept})");
        Check(Run(5) == r, "the same counts for the same seed");
    }

    // 6|3|1 weights over three sinks share 20 000 items about 60 / 30 / 10 %, the same counts for the same
    // seed and other counts for another.
    static void TestProbabilityRouting()
    {
        int[] Shares(int seed)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = seed, MaxSimulationTime = 20000 };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Constant");
            src.Set("interArrivalDistribution.constantValue", 1.0);
            src.Set("outputStrategy.strategyType", "Probability");
            src.Set("outputStrategy.weights", "6|3|1");   // a text of numbers is read as the double[] (6, 3, 1)
            c.Elements.Add(src);
            foreach (var n in new[] { "a", "b", "c" })
            {
                c.Elements.Add(new SimuLean.Serialization.ElementConfig(n, "Sink", n));
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", n, "General"));
            }
            var clock = new SimClock(seed);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            clock.AdvanceClock(20000.5);
            return new[] { "a", "b", "c" }.Select(n => (int)model.Find(n).GetStatsInputCount()).ToArray();
        }
        var s1 = Shares(7);
        int total = s1.Sum();
        Check(total >= 19999, $"every item routed ({total})");
        Check(Math.Abs(s1[0] / (double)total - 0.6) < 0.015 && Math.Abs(s1[1] / (double)total - 0.3) < 0.015 && Math.Abs(s1[2] / (double)total - 0.1) < 0.01,
              $"shares 60/30/10 % ({string.Join(", ", s1)})");
        Check(Shares(7).SequenceEqual(s1), "the same counts for the same seed");
        Check(!Shares(8).SequenceEqual(s1), "other counts for another seed");
    }

    // Referrals every 1 go to agenda A's list while it holds fewer than 5 (routing
    // content('A list') < 5 ? 0 : 1), else to agenda B's; each agenda sees one patient per 3. A's list never exceeds 5,
    // B takes the overflow. get / free read an element's properties; an unknown element fails naming it.
    static void TestExpressionsReadElements()
    {
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 300 };
        var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
        src.Set("mode", "InterArrival");
        src.Set("interArrivalDistribution.strategyType", "Constant");
        src.Set("interArrivalDistribution.constantValue", 1.0);
        src.Set("outputStrategy.strategyType", "Expression");
        src.Set("outputStrategy.routingExpression", "content('A list') < 5 ? 0 : 1");
        c.Elements.Add(src);
        c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink"));
        foreach (var n in new[] { "A", "B" })
        {
            var q = new SimuLean.Serialization.ElementConfig(n + "q", "Queue", n + " list");
            q.Set("capacity", 100000);
            var st = new SimuLean.Serialization.ElementConfig(n, "MultiServer", n);
            st.Set("capacity", 1); st.Set("delayStrategy.strategyType", "Constant"); st.Set("delayStrategy.constantValue", 3.0);
            c.Elements.Add(q); c.Elements.Add(st);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", n + "q", "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(n + "q", n, "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(n, "sink", "General"));
        }
        var clock = new SimClock(1);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
        clock.AdvanceClock(300);
        var aq = model.Find("A list"); var bq = model.Find("B list");
        Check(aq.GetStatsContentMax() <= 5, $"agenda A's list never above 5 ({aq.GetStatsContentMax()})");
        Check(bq.GetStatsInputCount() > 100, $"agenda B takes the overflow ({bq.GetStatsInputCount()})");

        var scope = new SimuLean.Expressions.EntityScope().Set(null, model.Find("src"));
        Check(SimuLean.Expressions.Expression.Parse("get('A', 'name')").Evaluate(scope).ToString() == "A", "get reads an element's property");
        Check(SimuLean.Expressions.Expression.Parse("free('A list') > 99000").Evaluate(scope).AsBool(), "free: the list's room");
        string error = null;
        try { SimuLean.Expressions.Expression.Parse("content('nowhere')").Evaluate(scope); }
        catch (InvalidOperationException e) { error = e.Message; }
        Check(error != null && error.Contains("nowhere"), $"an unknown element fails naming it ({error})");
    }

    // documents current behaviour (not a fixed bug). Three parts at 0 → q → test (2 each,
    // entry label n = (n ?? 0) + 1, routing n < 3 ? back : sink), i.e. three tests each. A healthcare follow-up loop sends the item back
    // through a zero-length Delay to the test station itself.
    static void TestRepeatBackConnection()
    {
        (string sink, double qIn, double tIn, double tOut, double done, double stayMin, int stuck) Repeat(string form, int qCap, int tCap)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "Schedule");
            src.Set("arrivalsTable", new Dictionary<string, List<string>>
                { ["Time"] = new List<string> { "0" }, ["Name"] = new List<string> { "P" }, ["Q"] = new List<string> { "3" } });
            var q = new SimuLean.Serialization.ElementConfig("q", "Queue", "q");
            q.Set("capacity", qCap);
            var t = new SimuLean.Serialization.ElementConfig("test", "MultiServer", "test");
            t.Set("capacity", tCap); t.Set("delayStrategy.strategyType", "Constant"); t.Set("delayStrategy.constantValue", 2.0);
            t.Set("entryLabels", new List<string> { "n = item.n + 1" });
            t.Set("outputStrategy.strategyType", "Expression");
            t.Set("outputStrategy.routingExpression", "item.n < 3 ? 0 : 1");
            c.Elements.Add(src); c.Elements.Add(q); c.Elements.Add(t); c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink"));
            var links = new List<(string, string)> { ("src", "q"), ("q", "test") };
            if (form == "hop")
            {
                var hop = new SimuLean.Serialization.ElementConfig("hop", "Delay", "hop");
                hop.Set("delayStrategy.strategyType", "Constant"); hop.Set("delayStrategy.constantValue", 0.0);
                c.Elements.Add(hop);
                links.Add(("test", "hop")); links.Add(("hop", "test"));
            }
            else links.Add(("test", form == "self" ? "test" : "q"));
            links.Add(("test", "sink"));
            foreach (var (a, b) in links) c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(a, b, "General"));
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock, enableLogging: false);
            var el = model.BuildModel(c);
            var ends = new List<double>();
            el["sink"].ItemEntered += (e, it) => ends.Add(clock.GetSimulationTime());
            model.StartModel();
            clock.AdvanceClock(100);
            var te = el["test"];
            return (string.Join(" ", ends), el["q"].GetStatsInputCount(), te.GetStatsInputCount(), te.GetStatsOutputCount(),
                    el["sink"].GetStatsInputCount(), te.GetStatsStayTimeMin(), el["q"].GetQueueLength() + te.GetQueueLength());
        }

        // one test slot, a large queue: the hop sends the part straight back, so it is tested three times in a row
        var hop1 = Repeat("hop", 10, 1);
        Check(hop1.sink == "12 16 18" && hop1.tIn == 9 && hop1.tOut == 9 && hop1.done == 3, $"zero-length hop: done at {hop1.sink}, test in {hop1.tIn} out {hop1.tOut}");
        // back to the input queue: the part queues behind the others (FIFO), the same counts, no zero-time loop
        var toQ = Repeat("queue", 10, 1);
        Check(toQ.sink == "14 16 18" && toQ.qIn == 9 && toQ.tIn == 9 && toQ.tOut == 9 && toQ.done == 3 && toQ.stayMin == 2,
              $"back to the queue: done at {toQ.sink}, q in {toQ.qIn}, test in {toQ.tIn} out {toQ.tOut}, shortest stay {toQ.stayMin}");
        // ... a closed loop: the queue needs room for every part that may come back (here 3)
        var toQ2 = Repeat("queue", 2, 1);
        Check(toQ2.done == 0 && toQ2.stuck == 3, $"back to a queue of 2 with 3 parts: deadlock at the first return ({toQ2.done} done, {toQ2.stuck} stuck)");
        Check(Repeat("queue", 3, 1).sink == "14 16 18", "back to a queue of 3: as with room");
        // back to the station itself: it keeps its slot while it offers the part, so a full station blocks on itself
        var self1 = Repeat("self", 10, 1);
        Check(self1.done == 0 && self1.tOut == 0 && self1.stuck == 3, $"self-loop, 1 slot: deadlock at 2 ({self1.done} done, {self1.stuck} stuck)");
        var self3 = Repeat("self", 10, 3);
        Check(self3.done == 0 && self3.stuck == 3, $"self-loop, 3 slots for 3 parts: deadlock at 2 ({self3.done} done)");
        var self4 = Repeat("self", 10, 4);
        var hop3 = Repeat("hop", 10, 3);
        Check(self4.sink == "6 6 6" && self4.tIn == 9 && self4.tOut == 9 && self4.stayMin == 2 && hop3.sink == self4.sink && hop3.tIn == self4.tIn,
              $"self-loop with a spare slot: as the hop with 3 slots (done at {self4.sink}; hop {hop3.sink}), test in {self4.tIn} out {self4.tOut}");
    }
}
