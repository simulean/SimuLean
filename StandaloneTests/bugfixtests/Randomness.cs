// Randomness: per-model seeds, one stream per element and per use, random labels and random expressions.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Serialization;

static partial class Program
{
    // ── #1 ───────────────────────────────────────────────────────────────────

    static int RunSeeded(int seed)
    {
        var clock = new SimClock(seed);
        var cfg = new ElementConfig("src", "Source", "Src");
        cfg.Set("interArrivalDistribution.strategyType", "Exponential");
        cfg.Set("interArrivalDistribution.exponentialRate", 1.0);
        var src = new InterArrivalSource("Src", clock, ElementBuildHelpers.BuildRandomProcess(cfg.Get<SamplerSettings>("interArrivalDistribution"), cfg.Name, "interArrivalDistribution", clock));
        var sink = new Collector(clock);
        GeneralLink.CreateLink(src, L(sink));
        src.Start(); sink.Start();
        clock.AdvanceClock(500);
        return sink.Got.Count;
    }

    static void TestSeeds()
    {
        int a1 = RunSeeded(101), a2 = RunSeeded(101), b = RunSeeded(102);
        Check(a1 == a2, $"same seed reproducible ({a1} vs {a2})");
        Check(a1 != b, $"different seeds differ ({a1} vs {b})");
        var par = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => RunSeeded(101)))).Result;
        Check(par.All(x => x == a1), "same seed identical in parallel: " + string.Join(",", par));
    }

    static void TestUnassignedStreamWarns()
    {
        var warnings = new List<string>();
        var previous = SimLog.WarningSink;
        SimLog.WarningSink = warnings.Add;
        try
        {
            var unseeded = new SimuLean.Stochastics.Distributions.WeibullDistribution(2, 1);   // a type no other test samples unseeded
            unseeded.Sample(); unseeded.Sample();
            new SimuLean.Stochastics.Distributions.WeibullDistribution(2, 1).Sample();
            Check(warnings.Count == 1 && warnings[0].Contains("WeibullDistribution"), $"one warning naming the type (got {warnings.Count})");

            var clock = new SimClock(5);
            var seeded = clock.Context.AssignRandomStream(new SimuLean.Stochastics.Distributions.LogNormalDistribution(0, 1), "Lathe", "delay");
            seeded.Sample();
            Check(warnings.Count == 1, "a sampler with its own stream does not warn");
        }
        finally { SimLog.WarningSink = previous; }
    }

    // A test that is invalid half of the time is repeated (entry labels: reps = (reps ?? 0) + 1,
    // valid = rand() < 0.5; routing item.valid ? 0 : 1 back to its queue): every patient leaves with a valid test and
    // about two tests on average; the case mix drawn by pick() on the source's exit.
    static void TestEntryLabelsLoop()
    {
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 3, MaxSimulationTime = 5000 };
        var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
        src.Set("mode", "InterArrival");
        src.Set("interArrivalDistribution.strategyType", "Constant");
        src.Set("interArrivalDistribution.constantValue", 3.0);
        src.Set("exitLabels", new List<string> { "dx = pick(0.75, 'diagnosis', 0.25, 'control')" });
        var q = new SimuLean.Serialization.ElementConfig("q", "Queue", "q");
        q.Set("capacity", 100000);
        var st = new SimuLean.Serialization.ElementConfig("test", "MultiServer", "test");
        st.Set("capacity", 1); st.Set("delayStrategy.strategyType", "Constant"); st.Set("delayStrategy.constantValue", 1.0);
        st.Set("entryLabels", new List<string> { "reps = (reps ?? 0) + 1", "valid = rand() < 0.5", "t_test = now()" });
        st.Set("outputStrategy.strategyType", "Expression");
        st.Set("outputStrategy.routingExpression", "item.valid ? 0 : 1");
        var sink = new SimuLean.Serialization.ElementConfig("done", "Sink", "done");
        sink.Set("keepItems", true);
        c.Elements.Add(src); c.Elements.Add(q); c.Elements.Add(st); c.Elements.Add(sink);
        foreach (var (f, t) in new[] { ("src", "q"), ("q", "test"), ("test", "done"), ("test", "q") })
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(f, t, "General"));
        var clock = new SimClock(3);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
        clock.AdvanceClock(5000);
        var kept = ((Sink)model.Find("done")).Kept;
        Check(kept.Count > 1000, $"patients done ({kept.Count})");
        Check(kept.All(it => it.GetLabelOrDefault("valid") == 1 && it.GetLabelOrDefault("reps") >= 1), "every patient leaves with a valid test");
        double meanReps = kept.Average(it => it.GetLabelOrDefault("reps"));
        Check(Math.Abs(meanReps - 2.0) < 0.15, $"about two tests each (geometric, p = 0.5): {meanReps:0.000}");
        double diag = kept.Count(it => it.GetLabelText("dx") == "diagnosis") / (double)kept.Count;
        Check(Math.Abs(diag - 0.75) < 0.04, $"case mix 75 % diagnosis ({diag:0.000})");
        Check(kept.All(it => it.GetLabelOrDefault("t_test") <= clock.GetSimulationTime()), "timestamps from now()");
    }

    // A full queue refuses the second item; its entry label (tries = (tries ?? 0) + 1) is undone. Random
    // functions fail outside an element (no stream).
    static void TestLabelsUndoneOnRefusal()
    {
        var clock = new SimClock(1);
        var q = new ItemsQueue(1, "q", clock, null, new SimuLean.Headless.HeadlessVElement(false));
        q.EntryLabels = LabelAssignments.Parse(new[] { "tries = (tries ?? 0) + 1" }, "q");
        var first = new Item(0); var second = new Item(0);
        Check(ItemHandOver.Receive(null, q, first) && first.GetLabelOrDefault("tries") == 1, "taken: labelled");
        second.SetLabelValue("tries", 5.0);
        Check(!ItemHandOver.Receive(null, q, second), "the full queue refuses the second");
        Check(second.GetLabelOrDefault("tries") == 5, $"its label as it was (5, got {second.GetLabelOrDefault("tries")})");
        var fresh = new Item(0);
        ItemHandOver.Receive(null, q, fresh);
        Check(!fresh.HasLabel("tries"), "a label it did not have is removed again");
        bool threw = false;
        try { SimuLean.Expressions.Expression.Parse("rand()").Evaluate(new SimuLean.Expressions.EntityScope()); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw, "rand() without an element fails");
        bool bad = false;
        try { LabelAssignments.Parse(new[] { "x == 1" }, "q"); } catch (FormatException) { bad = true; }
        Check(bad, "a record that is not 'name = expression' fails");
    }

    static void TestRandomExpressionSamplers()
    {
        int Arrivals(int seed)
        {
            var clock = new SimClock(seed);
            var src = new InterArrivalSource("Src", clock, new SimuLean.Expressions.ExpressionSampler("exponential(0.2)"));
            var sink = new Sink("Sink", clock);
            GeneralLink.CreateLink(src, L(sink));
            src.Start(); sink.Start();
            clock.AdvanceClock(500);
            return (int)sink.GetStatsInputCount();
        }
        int n = -1, again = -2;
        string error = null;
        try { n = Arrivals(7); again = Arrivals(7); } catch (Exception e) { error = e.Message; }
        Check(error == null && n > 50 && n < 150 && n == again, $"a source with exponential(0.2) (rate; mean 5) runs and repeats for a seed ({n}, {again}, {error})");

        error = null;
        int stops = 0;
        try
        {
            var clock = new SimClock(3);
            var m = new MultiServer(new IRandomSampler[] { Const(1) }, "M", clock);
            var feed = new InterArrivalSource("Feed", clock, Const(1));
            var sink = new Sink("Sink", clock);
            GeneralLink.CreateLink(feed, L(m)); GeneralLink.CreateLink(m, L(sink));
            var gen = new SimuLean.Downtime.MtbfMttrDowntime(m, new SimuLean.Expressions.ExpressionSampler("20 + 10 * rand()"),
                                                             new SimuLean.Expressions.ExpressionSampler("exponential(0.5)"), clock);
            foreach (var e in new Element[] { feed, m, sink }) e.Start();
            gen.Start();
            clock.AdvanceClock(300);
            stops = m.StopCount;
        }
        catch (Exception e) { error = e.Message; }
        Check(error == null && stops > 3, $"failures / repairs drawn from expressions ({stops} stops, {error})");
    }

    // A source labels T = pick(0.5, 1, 0.5, 2) on exit, then a sink. The labels of an element shared one stream with every
    // random function of its expressions: adding Color = pick(...) shifted the draws of T, so two scenarios that differ only
    // by another label got other types - noise in a comparison (common random numbers). Each label now draws from its own
    // stream (owner = the element, purpose exitLabel:T / entryLabel:T).
    static void TestRandomLabelsOwnStream()
    {
        SimuLean.Serialization.SimulationConfig Model(params string[] labels)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 11, MaxSimulationTime = 4000 };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Constant");
            src.Set("interArrivalDistribution.constantValue", 1.0);
            src.Set("exitLabels", new List<string>(labels));
            c.Elements.Add(src);
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("done", "Sink", "done"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "done", "General"));
            return c;
        }
        List<string> Run(SimuLean.Serialization.SimulationConfig c)
        {
            var clock = new SimClock(c.Seed);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            var seen = new List<string>();
            model.Find("done").ItemEntered += (el, it) => seen.Add(it.GetLabelOrDefault("T") + "/" + it.GetLabelText("Color"));
            model.StartModel();
            clock.AdvanceClock(c.MaxSimulationTime);
            return seen;
        }
        var t1 = Run(Model("T = pick(0.5, 1, 0.5, 2)"));
        double share = t1.Count(x => x.StartsWith("1/")) / (double)t1.Count;
        Check(t1.Count > 3900 && Math.Abs(share - 0.5) < 0.03, $"half of the items are type 1 (got {share:0.000} of {t1.Count})");
        var t2 = Run(Model("T = pick(0.5, 1, 0.5, 2)", "Color = pick(1, 'red', 1, 'blue')"));
        Check(t1.Select(x => x.Split('/')[0]).SequenceEqual(t2.Select(x => x.Split('/')[0])), "another random label leaves the draws of T as they were");
        var t3 = Run(Model("Color = pick(1, 'red', 1, 'blue')", "T = pick(0.5, 1, 0.5, 2)"));
        Check(t2.SequenceEqual(t3), "the order of the records does not change the draws either");
        int red = t2.Count(x => x.EndsWith("/red"));
        Check(red > 1900 && red < 2100, $"Color drawn too ({red} red)");
    }

    // A station with process time uniform(1, 3) (an expression) between a source every 4 and a sink. The scenario B adds
    // random functions to the other uses of the same element — a routing expression floor(rand()) (always output 0), an
    // admission condition rand() < 2 at the sink (always true) and an exit label x = rand() —: they all shared the
    // element's stream, so B's process times differed from A's. Each use now draws from its own: the same times.
    static void TestRandomUsesOwnStreams()
    {
        SimuLean.Serialization.SimulationConfig Model(bool otherRandomUses)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 21, MaxSimulationTime = 2000 };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Constant");
            src.Set("interArrivalDistribution.constantValue", 4.0);
            var st = new SimuLean.Serialization.ElementConfig("st", "MultiServer", "st");
            st.Set("capacity", 1);
            st.Set("delayStrategy.strategyType", "Expression");
            st.Set("delayStrategy.expression", "uniform(1, 3)");
            var done = new SimuLean.Serialization.ElementConfig("done", "Sink", "done");
            if (otherRandomUses)
            {
                st.Set("outputStrategy.strategyType", "Expression");
                st.Set("outputStrategy.routingExpression", "floor(rand())");
                st.Set("exitLabels", new List<string> { "x = rand()" });
                done.Set("inputStrategy.strategyType", "Condition");
                done.Set("inputStrategy.condition", "rand() < 2");
            }
            c.Elements.Add(src); c.Elements.Add(st); c.Elements.Add(done);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "st", "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("st", "done", "General"));
            return c;
        }
        List<double> Times(SimuLean.Serialization.SimulationConfig c)
        {
            var clock = new SimClock(c.Seed);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            var t = new List<double>();
            model.Find("done").ItemEntered += (el, it) => t.Add(clock.GetSimulationTime());
            model.StartModel();
            clock.AdvanceClock(c.MaxSimulationTime);
            return t;
        }
        var a = Times(Model(false));
        var b = Times(Model(true));
        Check(a.Count > 450 && a.Count == b.Count, $"the same items done ({a.Count} / {b.Count})");
        Check(a.SequenceEqual(b), "the same process times: the other random uses draw from their own streams");
        var spans = a.Select((x, k) => x - 4.0 * (k + 1)).ToList();
        Check(spans.Min() >= 1 - 1e-9 && spans.Max() <= 3 + 1e-9, "uniform(1, 3) process times (no queueing: arrivals every 4)");
    }
}
