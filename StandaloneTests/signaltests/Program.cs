// Standalone tests of signals and reactions: the signal table, delivery, each action, build errors,
// determinism and the reactions' catalog parameters.
// Run: dotnet run  (from StandaloneTests/signaltests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Headless;
using SimuLean.Optimization;
using SimuLean.Serialization;
using SimuLean.Signals;

static class Program
{
    static int passed, failed;

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    static int Main()
    {
        Run("A reaction sets an element's property: the routing that reads it changes", TestSetElementProperty);
        Run("An element's property as a study metric (Owner.property:name) — its end value; validate refuses an unknown one", TestPropertyMetric);
        Run("Stop for a time, and one reaction stops a line while another resumes it", TestStopResume);
        Run("Item labels and list pushes; a reaction sees the labels another one set", TestLabelsAndLists);
        Run("Delivery: next event (default) and immediate both fire; state filter", TestDeliveryAndStateFilter);
        Run("Determinism: same seed, same firings; the reaction's own stream leaves the element's draws alone", TestDeterminism);
        Run("Build errors name the reaction and the field", TestBuildErrors);
        Run("A failing action stops the run naming the reaction", TestRunError);
        Run("Catalog: enabled and the actions' numbers are parameters; a study varies them", TestCatalog);
        Run("No reactions: nothing subscribed", TestNoSubscribers);
        Run("Process ended: once per item a station finished, with the item", TestProcessEnded);
        Run("Time signals: run started, every period (from its first time), at a time", TestTimeSignals);
        Run("Team members: stopped at the start, one called back later; errors", TestTeamMembers);
        Run("Results: a reaction's firings in RunResults, studies' metrics, the warm-up", TestResults);
        Run("Stop groups: a resume ends only the stops of its group (orders vs an incident)", TestStopGroups);
        Run("Order: reactions on one signal run in build order; an action's 'only if' sees the actions before it", TestOrderAndOnlyIf);
        Run("Release: a reaction releases a queue released on command (orders every period; one per item that leaves, kanban); errors", TestReleaseGate);
        Run("Admission reading a property a reaction sets: the write offers the refused parts again at once; a Retry action; the old recipes still work", TestAdmissionWakeUp);
        Run("Set Item Look: the item gets its colour / scale outside its labels; results identical to the model without the action; errors", TestSetItemLook);

        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    // ── model helpers ────────────────────────────────────────────────────────

    static ElementConfig El(string id, string type) => new ElementConfig(id, type, id);

    static ElementConfig Source(string id, double gap)
    {
        var e = El(id, "Source");
        e.Set("mode", "InterArrival");
        e.Set("interArrivalDistribution.strategyType", "Constant");
        e.Set("interArrivalDistribution.constantValue", gap);
        return e;
    }

    static ElementConfig Server(string id, double time, int capacity = 1)
    {
        var e = El(id, "MultiServer");
        e.Set("capacity", capacity);
        e.Set("delayStrategy.strategyType", "Constant");
        e.Set("delayStrategy.constantValue", time);
        return e;
    }

    static ElementConfig Queue(string id, int capacity = 100)
    {
        var e = El(id, "Queue");
        e.Set("capacity", capacity);
        return e;
    }

    static SimulationConfig Model(int seed = 7, double time = 100)
        => new SimulationConfig { UseFixedSeed = true, Seed = seed, MaxSimulationTime = time };

    static void Connect(SimulationConfig c, string from, string to) => c.Connections.Add(new ConnectionConfig(from, to, "General"));

    static (HeadlessModelFactory model, SimClock clock) RunModel(SimulationConfig c, double time)
    {
        var clock = new SimClock(c.Seed);
        var model = new HeadlessModelFactory(clock, enableLogging: false);
        model.BuildModel(c);
        model.StartModel();
        clock.AdvanceClock(time);
        return (model, clock);
    }

    static string Fails(Action a)
    {
        try { a(); return null; }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>src (every 1) → a | b (routing by src's property 'lane') → sink.</summary>
    static SimulationConfig SwitchModel()
    {
        var c = Model();
        var src = Source("src", 1);
        src.Set("outputStrategy.strategyType", "Expression");
        src.Set("outputStrategy.routingExpression", "element.lane");
        c.Elements.Add(src);
        c.Elements.Add(Server("a", 0.5)); c.Elements.Add(Server("b", 0.5));
        c.Elements.Add(El("sink", "Sink"));
        Connect(c, "src", "a"); Connect(c, "src", "b"); Connect(c, "a", "sink"); Connect(c, "b", "sink");
        var r = new ReactionConfig("switch", "sink");
        r.Set("signal", "ItemEntered");
        r.Set("condition", "now() >= 10");
        r.AddAction("SetElementProperty", target: "src", name: "lane", value: 1);
        c.Reactions.Add(r);
        return c;
    }

    // ── tests ────────────────────────────────────────────────────────────────

    static void TestSetElementProperty()
    {
        var (model, _) = RunModel(SwitchModel(), 30.2);
        double a = model.GetElement("a").GetStatsOutputCount(), b = model.GetElement("b").GetStatsOutputCount();
        var r = model.ReactionsById["switch"];
        Check(a >= 9 && a <= 11 && b >= 18, $"items to a until t = 10, then to b (a {a}, b {b})");
        Check(r.Fired == a + b - r.Skipped && r.Skipped >= 9, $"fired once per item after t = 10 ({r.Fired} fired, {r.Skipped} skipped)");
        Check(r.LastItem != null && r.LastSource == model.GetElement("sink") && r.LastFired >= 29, $"the last firing: {r.LastItem} at {r.LastFired}");
    }

    static void TestPropertyMetric()
    {
        // 'lane' is written by the reaction (0 at the start, 1 from t = 10): the end value is 1
        var set = new Study(SwitchModel()).Metric("lane", "src.property:lane").WithRunTime(30.2).Run();
        Check(set.Runs.Count == 1 && set.Runs[0].Error == null && set.Runs[0].Metrics["lane"] == 1, $"the property's end value ({set.Runs[0].Metrics.GetValueOrDefault("lane")}, {set.Runs[0].Error})");
        var check = new Study(SwitchModel()).Metric("lane", "src.property:lane").WithRunTime(30.2).Check();
        Check(check.Ok, "a property only a reaction writes is valid: " + string.Join(" | ", check.Errors));
        // a property given at the build, never changed: its build value
        var c = SwitchModel();
        c.Elements.Find(e => e.Id == "a").Set("properties", new List<PropertySettings> { PropertySettings.Number("late", 3) });
        set = new Study(c).Metric("late", "a.property:late").WithRunTime(5).Run();
        Check(set.Runs[0].Metrics["late"] == 3, "a build-time property reads its value");
        check = new Study(c).Metric("late", "a.property:lat").WithRunTime(5).Check();
        Check(!check.Ok && check.Errors[0].Contains("no property 'lat'") && check.Errors[0].Contains("'late'"),
              "an unknown property is an error with a did-you-mean: " + string.Join(" | ", check.Errors));
        c.Elements.Find(e => e.Id == "b").Set("properties", new List<PropertySettings> { PropertySettings.OfText("kind", "x") });
        check = new Study(c).Metric("k", "b.property:kind").WithRunTime(5).Check();
        Check(!check.Ok && check.Errors[0].Contains("not a number"), "a text property is refused: " + string.Join(" | ", check.Errors));
    }

    static void TestStopResume()
    {
        // a stop for a time: the queue reaching 3 stops the source for 5
        var c = Model();
        c.Elements.Add(Source("src", 1)); c.Elements.Add(Queue("q")); c.Elements.Add(Server("srv", 3)); c.Elements.Add(El("sink", "Sink"));
        Connect(c, "src", "q"); Connect(c, "q", "srv"); Connect(c, "srv", "sink");
        var hold = new ReactionConfig("hold", "q");
        hold.Set("condition", "element.content >= 3");
        hold.AddAction("Stop", target: "src", duration: 5);
        c.Reactions.Add(hold);
        var (model, _) = RunModel(c, 60);
        var src = model.GetElement("src");
        double stopped = src.GetTimeInState(ElementState.STOPPED);
        Check(model.ReactionsById["hold"].Fired > 0 && stopped >= 5, $"the source was stopped ({stopped} time units, {model.ReactionsById["hold"].Fired} firings)");
        Check(src.GetStatsOutputCount() < 55, $"fewer arrivals than without the reaction ({src.GetStatsOutputCount()})");

        // an AfterCurrent stop for a time starts when the job in hand ends and lasts its whole time: the first item
        // arrives at 1, its job 1..101; a stop for 20 at 30 → stopped 101..121 (it was cut: never stopped)
        foreach (double horizon in new[] { 110.0, 200.0 })
        {
            var ca = Model();
            ca.Elements.Add(Source("src", 1)); ca.Elements.Add(Server("srv", 100)); ca.Elements.Add(El("sink", "Sink"));
            Connect(ca, "src", "srv"); Connect(ca, "srv", "sink");
            var brk = At("break", 30);
            brk.AddAction("Stop", target: "srv", mode: "AfterCurrent", duration: 20);
            ca.Reactions.Add(brk);
            var (ma, _) = RunModel(ca, horizon);
            double down = ma.GetElement("srv").GetTimeInState(ElementState.STOPPED);
            Check(Math.Abs(down - (horizon < 150 ? 9 : 20)) < 1e-9, $"AfterCurrent stop for 20 at 30, job 1..101: stopped from 101 for 20 ({down} by {horizon})");
        }

        // two reactions: the queue full (≥ 4) stops the source, the queue drained (≤ 1) resumes it
        var c2 = Model();
        c2.Elements.Add(Source("src", 1)); c2.Elements.Add(Queue("q")); c2.Elements.Add(Server("srv", 3)); c2.Elements.Add(El("sink", "Sink"));
        Connect(c2, "src", "q"); Connect(c2, "q", "srv"); Connect(c2, "srv", "sink");
        var full = new ReactionConfig("full", "q");
        full.Set("condition", "element.content >= 4");
        full.AddAction("Stop", target: "src");
        var drained = new ReactionConfig("drained", "q");
        drained.Set("signal", "ItemExited");
        drained.Set("condition", "element.content <= 1");
        drained.AddAction("Resume", target: "src");
        c2.Reactions.Add(full); c2.Reactions.Add(drained);
        var (m2, _) = RunModel(c2, 200);
        var q = m2.GetElement("q");
        Check(q.GetStatsContentMax() <= 5, $"the queue stays small ({q.GetStatsContentMax()} at most)");
        Check(m2.ReactionsById["full"].Fired > 0 && m2.ReactionsById["drained"].Fired > 0 && m2.GetElement("src").GetStatsOutputCount() > 50,
              $"stopped and resumed by different reactions; the source kept going ({m2.GetElement("src").GetStatsOutputCount()} out)");
    }

    static void TestReleaseGate()
    {
        // orders: every 10 time units, release 3 of the parts waiting at the gate
        var c = Model();
        c.Elements.Add(Source("src", 1)); c.Elements.Add(El("gate", "Queue")); c.Elements.Add(Server("srv", 0.5));
        c.Elements.Add(El("sink", "Sink"));
        c.Elements.Find(e => e.Id == "gate").Set("capacity", 200);
        c.Elements.Find(e => e.Id == "gate").Set("gated", true);
        Connect(c, "src", "gate"); Connect(c, "gate", "srv"); Connect(c, "srv", "sink");
        var orders = new ReactionConfig("orders");
        orders.Set("signal", "Every"); orders.Set("period", 10.0); orders.Set("firstTime", 10.0);
        orders.AddAction("Release", target: "gate", value: 3);
        c.Reactions.Add(orders);
        var (model, _) = RunModel(c, 95);
        double done = model.GetElement("sink").GetStatsInputCount();
        Check(done == 27, $"9 orders of 3 released by t = 95 ({done} done)");
        double made = model.GetElement("src").GetStatsOutputCount();
        Check(model.GetElement("gate").GetQueueLength() == made - 27, $"the rest waits at the gate ({model.GetElement("gate").GetQueueLength()} of {made})");

        // kanban: two released at the start, then one per part that leaves the station (empty value = 1)
        var k = Model();
        k.Elements.Add(Source("src", 1)); k.Elements.Add(El("gate", "Queue")); k.Elements.Add(Server("srv", 3));
        k.Elements.Add(El("sink", "Sink"));
        k.Elements.Find(e => e.Id == "gate").Set("capacity", 200);
        k.Elements.Find(e => e.Id == "gate").Set("gated", true);
        Connect(k, "src", "gate"); Connect(k, "gate", "srv"); Connect(k, "srv", "sink");
        var start = new ReactionConfig("start"); start.Set("signal", "RunStarted");
        start.AddAction("Release", target: "gate", value: 2);
        var pull = new ReactionConfig("pull", "srv"); pull.Set("signal", "ItemExited");
        pull.AddAction("Release", target: "gate");
        k.Reactions.Add(start); k.Reactions.Add(pull);
        var (km, _) = RunModel(k, 60);
        Check(km.GetElement("srv").GetStatsContentMax() <= 2, $"never more than two parts past the gate ({km.GetElement("srv").GetStatsContentMax()})");
        Check(km.GetElement("sink").GetStatsInputCount() >= 18, $"the station keeps working ({km.GetElement("sink").GetStatsInputCount()} done)");

        // errors: a release needs a queue released on command; the number must be whole
        var bad = Model();
        bad.Elements.Add(Source("src", 1)); bad.Elements.Add(Queue("q")); bad.Elements.Add(El("sink", "Sink"));
        Connect(bad, "src", "q"); Connect(bad, "q", "sink");
        var rel = new ReactionConfig("rel", "q"); rel.AddAction("Release", target: "q"); bad.Reactions.Add(rel);
        string e1 = Fails(() => new HeadlessModelFactory(new SimClock(1), enableLogging: false).BuildModel(bad));
        Check(e1 != null && e1.Contains("released on command") && e1.Contains("rel"), "a release on a plain queue fails the build: " + e1);
        var rel2 = new ReactionConfig("rel", "q"); rel2.AddAction("Release", value: 1.5);
        bad.Reactions.Clear(); bad.Reactions.Add(rel2);
        bad.Elements[1] = El("q", "Queue"); bad.Elements[1].Set("gated", true);
        string e2 = Fails(() => new HeadlessModelFactory(new SimClock(1), enableLogging: false).BuildModel(bad));
        Check(e2 != null && e2.Contains("whole number"), "1.5 items fails the build: " + e2);
    }

    static ReactionConfig At(string id, double t)
    {
        var r = new ReactionConfig(id);
        r.Set("signal", "AtTime");
        r.Set("atTime", t);
        return r;
    }

    static void TestStopGroups()
    {
        // orders stop the station at 5 and resume it at 12; an incident stops it at 10 and is fixed at 20: the orders'
        // resume must not end the incident's stop (stopped 5 → 20), nor a resume of the default group
        var c = Model();
        c.Elements.Add(Source("src", 1)); c.Elements.Add(Queue("q")); c.Elements.Add(Server("srv", 1)); c.Elements.Add(El("sink", "Sink"));
        Connect(c, "src", "q"); Connect(c, "q", "srv"); Connect(c, "srv", "sink");
        var r = At("noOrders", 5); r.AddAction("Stop", target: "srv", group: "orders"); c.Reactions.Add(r);
        r = At("incident", 10); r.AddAction("Stop", target: "srv", group: "incident"); c.Reactions.Add(r);
        r = At("order", 12); r.AddAction("Resume", target: "srv", group: "orders"); r.AddAction("Resume", target: "srv"); c.Reactions.Add(r);
        r = At("fixed", 20); r.AddAction("Resume", target: "srv", group: "incident"); c.Reactions.Add(r);
        var (model, _) = RunModel(c, 40);
        var srv = model.GetElement("srv");
        double st = srv.GetTimeInState(ElementState.STOPPED);
        Check(Math.Abs(st - 15) < 1e-6, $"stopped from the orders' stop (5) to the incident's end (20): {st}");
        Check(model.GetElement("sink").GetStatsInputCount() > 20, $"runs again after 20 ({model.GetElement("sink").GetStatsInputCount()} out)");

        // without groups (all in the default group) the order's resume ends the incident too: stopped 5 → 12
        var c2 = Model();
        c2.Elements.Add(Source("src", 1)); c2.Elements.Add(Queue("q")); c2.Elements.Add(Server("srv", 1)); c2.Elements.Add(El("sink", "Sink"));
        Connect(c2, "src", "q"); Connect(c2, "q", "srv"); Connect(c2, "srv", "sink");
        r = At("noOrders", 5); r.AddAction("Stop", target: "srv"); c2.Reactions.Add(r);
        r = At("incident", 10); r.AddAction("Stop", target: "srv"); c2.Reactions.Add(r);
        r = At("order", 12); r.AddAction("Resume", target: "srv"); c2.Reactions.Add(r);
        var (m2, _) = RunModel(c2, 40);
        st = m2.GetElement("srv").GetTimeInState(ElementState.STOPPED);
        Check(Math.Abs(st - 7) < 1e-6, $"one group: the resume ends both stops ({st})");

        // team members: a group's resume calls back only the members that group stopped
        var c3 = SimuLean.Testing.ReferenceModels.ResourceSharedTeam();
        r = new ReactionConfig("offA"); r.Set("signal", "RunStarted");
        r.AddAction("Stop", state: "OFF_SHIFT", team: "team", count: 1, group: "a");
        r.AddAction("Stop", state: "OFF_SHIFT", team: "team", count: 1, group: "b");
        c3.Reactions.Add(r);
        r = At("backA", 1000); r.AddAction("Resume", team: "team", count: 0, group: "a"); c3.Reactions.Add(r);
        var (m3, _) = RunModel(c3, 2000);
        double off1 = m3.Executors.First(x => x.Name == "Team_1").GetTimeInState(ElementState.Of("OFF_SHIFT"));
        double off2 = m3.Executors.First(x => x.Name == "Team_2").GetTimeInState(ElementState.Of("OFF_SHIFT"));
        Check(Math.Abs(off2 - 1000) < 1e-6 && Math.Abs(off1 - 2000) < 1e-6,
              $"group b's stop takes the member still working; group a's resume calls back only its own (off {off1}, {off2})");
    }

    static void TestOrderAndOnlyIf()
    {
        // one reaction: at the run start 'left' = 3; at each end of process, decrement, then stop the station only if it
        // reached 0 (the 'only if' sees the decrement): exactly three items, then stopped
        var c = Model();
        c.Elements.Add(Source("src", 1)); c.Elements.Add(Queue("q")); c.Elements.Add(Server("srv", 1)); c.Elements.Add(El("sink", "Sink"));
        Connect(c, "src", "q"); Connect(c, "q", "srv"); Connect(c, "srv", "sink");
        var init = new ReactionConfig("init");
        init.Set("signal", "RunStarted");
        init.AddAction("SetElementProperty", target: "srv", name: "left", value: 3);
        c.Reactions.Add(init);
        var gate = new ReactionConfig("gate", "srv");
        gate.Set("signal", "ProcessEnded");
        gate.Set("timing", "Immediate");
        gate.AddAction("SetElementProperty", name: "left", value: "element.left - 1");
        gate.AddAction("Stop", mode: "AfterCurrent", condition: "element.left <= 0");
        c.Reactions.Add(gate);
        var (model, _) = RunModel(c, 30);
        Check(model.GetElement("sink").GetStatsInputCount() == 3, $"three items, then stopped ({model.GetElement("sink").GetStatsInputCount()})");
        Check(model.ReactionsById["gate"].Fired == 3, $"fired per item ({model.ReactionsById["gate"].Fired})");

        // two reactions on the same signal and delivery run in the order they were built: 'dec' then 'check'
        foreach (string delivery in new[] { "Immediate", "NextEvent" })
        {
            var c2 = Model();
            c2.Elements.Add(Source("src", 1)); c2.Elements.Add(El("sink", "Sink"));
            Connect(c2, "src", "sink");
            c2.Lists.Add(new ListConfig { Id = "log", Name = "log" });
            foreach (string id in new[] { "first", "second", "third" })
            {
                var r = new ReactionConfig(id, "sink");
                r.Set("timing", delivery);
                r.AddAction("SetElementProperty", name: "order", value: id == "first" ? "1" : id == "second" ? "element.order * 10 + 2" : "element.order * 10 + 3");
                c2.Reactions.Add(r);
            }
            var (m2, _) = RunModel(c2, 1.5);
            Check(m2.GetElement("sink").Properties.GetDouble("order") == 123, $"{delivery}: build order ({m2.GetElement("sink").Properties.GetDouble("order")})");
        }

        var bad = Model();
        bad.Elements.Add(Source("src", 1)); bad.Elements.Add(El("sink", "Sink")); Connect(bad, "src", "sink");
        var br = new ReactionConfig("broken", "sink");
        br.AddAction("SetElementProperty", name: "x", value: 1, condition: "element.x >");
        bad.Reactions.Add(br);
        string error = Fails(() => RunModel(bad, 1));
        Check(error != null && error.Contains("broken") && error.Contains("only if"), $"a bad 'only if' fails the build naming it ({error})");
    }

    static void TestLabelsAndLists()
    {
        var c = Model();
        c.Elements.Add(Source("src", 1)); c.Elements.Add(Queue("q")); c.Elements.Add(Server("srv", 0.5)); c.Elements.Add(El("sink", "Sink"));
        Connect(c, "src", "q"); Connect(c, "q", "srv"); Connect(c, "srv", "sink");
        c.Lists.Add(new ListConfig { Id = "log", Name = "log" });
        var tag = new ReactionConfig("tag", "q");
        tag.AddAction("SetItemLabel", name: "tag", value: "now() * 2");
        var log = new ReactionConfig("log", "sink");
        log.AddAction("PushToList", name: "log", value: "item.tag");
        c.Reactions.Add(tag); c.Reactions.Add(log);
        var (model, clock) = RunModel(c, 20);
        var list = clock.Context.GetList("log");
        var values = list.Entries.Select(e => Convert.ToDouble(e.Value)).ToList();
        double sunk = model.GetElement("sink").GetStatsInputCount();
        Check(values.Count == sunk && sunk >= 18, $"one value per item at the sink ({values.Count} values, {sunk} items)");
        Check(values.All(v => v > 0 && Math.Abs(v / 2 - Math.Round(v / 2)) < 1e-9), $"each value is the label set at the queue, twice its entry time ({string.Join(", ", values.Take(4))}…)");
    }

    static void TestDeliveryAndStateFilter()
    {
        int Fired(string delivery, string state = null)
        {
            var c = Model();
            c.Elements.Add(Source("src", 2)); c.Elements.Add(Server("srv", 1)); c.Elements.Add(El("sink", "Sink"));
            Connect(c, "src", "srv"); Connect(c, "srv", "sink");
            var r = new ReactionConfig("count", "srv");
            r.Set("signal", "StateChanged");
            r.Set("timing", delivery);
            if (state != null) r.Set("state", state);
            r.AddAction("SetElementProperty", name: "n", value: "element.n + 1");
            c.Reactions.Add(r);
            var (model, _) = RunModel(c, 40);
            var srv = model.GetElement("srv");
            Check(srv.Properties.TryGetDouble("n", out double n) && n == model.ReactionsById["count"].Fired,
                  $"{delivery}{(state != null ? " / " + state : "")}: the property counts the firings ({n} vs {model.ReactionsById["count"].Fired})");
            return model.ReactionsById["count"].Fired;
        }
        int next = Fired("NextEvent"), now = Fired("Immediate"), busy = Fired("NextEvent", "PROCESSING");
        Check(next == now && next >= 38, $"both deliveries see every state change ({next}, {now})");
        Check(busy >= 19 && busy <= 21, $"only the changes to PROCESSING with the filter ({busy})");
    }

    static void TestDeterminism()
    {
        SimulationConfig RandomModel(int seed)
        {
            var c = SwitchModel();
            c.Seed = seed;
            c.Reactions[0].Set("condition", "rand() < 0.5");
            c.Reactions[0].Settings = new ReactionSettings();
            c.Reactions[0].Set("condition", "rand() < 0.5");
            c.Reactions[0].AddAction("SetElementProperty", target: "src", name: "lane", value: "pick(1, 0, 1, 1)");
            c.Elements.Find(e => e.Id == "a").Set("delayStrategy.strategyType", "Exponential");
            return c;
        }
        (int, double, double) Once(int seed)
        {
            var (m, _) = RunModel(RandomModel(seed), 200);
            return (m.ReactionsById["switch"].Fired, m.GetElement("a").GetStatsOutputCount(), m.GetElement("a").GetStatsStayTimeMean());
        }
        var r1 = Once(3); var r2 = Once(3); var r3 = Once(4);
        Check(r1.Equals(r2), $"same seed, same firings and results ({r1} / {r2})");
        Check(!r1.Equals(r3), $"another seed, other draws ({r1} / {r3})");
    }

    static void TestBuildErrors()
    {
        string Error(Action<ReactionConfig> edit)
        {
            var c = SwitchModel();
            edit(c.Reactions[0]);
            return Fails(() => new HeadlessModelFactory(new SimClock(1), enableLogging: false).BuildModel(c));
        }
        string e1 = Error(r => r.Sources[0] = "nope");
        Check(e1 != null && e1.Contains("'switch'") && e1.Contains("'nope'"), $"an unknown source: {e1}");
        string e2 = Error(r => { r.Set("signal", "StateChanged"); r.AddAction("SetItemLabel", name: "x", value: 1); });
        Check(e2 != null && e2.Contains("action 2") && e2.Contains("item signal"), $"an item label on a signal without an item: {e2}");
        string e3 = Error(r => r.Set("condition", "now( >= 1"));
        Check(e3 != null && e3.Contains("condition"), $"an expression that does not parse: {e3}");
        string e4 = Error(r => r.AddAction("PushToList", name: "nope", value: 1));
        Check(e4 != null && e4.Contains("no list 'nope'"), $"an unknown list: {e4}");
        string e5 = Fails(() => SwitchModel().Reactions[0].Set("signal", "Arrived"));
        Check(e5 != null && e5.Contains("ItemEntered") && e5.Contains("switch"), $"an unknown signal fails when written, listing the signals: {e5}");
        string e7 = Fails(() => SwitchModel().Reactions[0].Set("actions[0].colour", "red"));
        Check(e7 != null && e7.Contains("colour") && e7.Contains("switch"), $"an action field the record does not have fails when written: {e7}");
        string e6 = Error(r => r.AddAction("Stop", target: "src", duration: -1));
        Check(e6 != null && e6.Contains("duration"), $"a negative stop duration: {e6}");
    }

    static void TestRunError()
    {
        var c = SwitchModel();
        // a table named at run time (a constant name is checked when the model is built)
        c.Reactions[0].Set("condition", "table(now() >= 0 ? 'nope' : 'x', 1, 'x') > 0");
        string e = Fails(() => RunModel(c, 20));
        Check(e != null && e.Contains("Reaction 'switch'") && e.Contains("'nope'"), $"the error names the reaction and the cause: {e}");
    }

    static void TestCatalog()
    {
        var c = SwitchModel();
        c.Reactions[0].AddAction("Stop", target: "a", duration: 2);
        var keys = ModelParameterCatalog.ForReaction(c.Reactions[0]).Select(p => p.Key).ToList();
        Check(keys.SequenceEqual(new[] { "reactionEnabled", "actions[0].value", "actions[1].duration" }),
              $"enabled and the actions' numbers are offered, nothing else ({string.Join(", ", keys)})");
        Check(ModelParameterCatalog.Undecided(c).Count == 0, $"nothing undecided ({string.Join(", ", ModelParameterCatalog.Undecided(c))})");

        var set = new Study(SwitchModel()).Factor("reaction:switch.reactionEnabled", "true", "false").Metric("b", "b.input").WithRunTime(30.2).Run();
        Check(set.Runs.Count == 2 && set.Runs[0].Metrics["b"] > 15 && set.Runs[1].Metrics["b"] == 0,
              $"a study switches the reaction on and off (b {set.Runs[0].Metrics["b"]} / {set.Runs[1].Metrics["b"]})");
        var lane = new Study(SwitchModel()).Factor("reaction:switch.actions[0].value", "0", "1").Metric("b", "b.input").WithRunTime(30.2).Run();
        Check(lane.Runs[0].Metrics["b"] == 0 && lane.Runs[1].Metrics["b"] > 15, $"and an action's number ({lane.Runs[0].Metrics["b"]} / {lane.Runs[1].Metrics["b"]})");
    }

    static void TestProcessEnded()
    {
        var c = Model();
        c.Elements.Add(Source("src", 2)); c.Elements.Add(Server("srv", 1)); c.Elements.Add(El("sink", "Sink"));
        Connect(c, "src", "srv"); Connect(c, "srv", "sink");
        c.Lists.Add(new ListConfig { Id = "done", Name = "done" });
        var r = new ReactionConfig("finished", "srv");
        r.Set("signal", "ProcessEnded");
        r.AddAction("SetItemLabel", name: "finished_at", value: "now()");
        var log = new ReactionConfig("log", "sink");
        log.AddAction("PushToList", name: "done", value: "item.finished_at");
        c.Reactions.Add(r); c.Reactions.Add(log);
        var (model, clock) = RunModel(c, 41);
        var srv = model.GetElement("srv");
        Check(model.ReactionsById["finished"].Fired == (int)srv.GetStatsOutputCount() && model.ReactionsById["finished"].Fired >= 19, $"once per processed item ({model.ReactionsById["finished"].Fired}, {srv.GetStatsOutputCount()} out)");
        var values = clock.Context.GetList("done").Entries.Select(e => Convert.ToDouble(e.Value)).ToList();
        Check(values.Count > 0 && values.All(v => v > 0), $"the item carried the time its process ended ({string.Join(", ", values.Take(3))}…)");
    }

    static void TestTimeSignals()
    {
        int Fired(string signal, Action<ReactionConfig> set, double horizon, out double last)
        {
            var c = SwitchModel();
            var r = new ReactionConfig("tick");
            r.Set("signal", signal);
            set(r);
            r.AddAction("SetElementProperty", target: "sink", name: "n", value: "get('sink', 'n') + 1");
            c.Reactions.Add(r);
            var (model, _) = RunModel(c, horizon);
            last = model.ReactionsById["tick"].LastFired;
            model.GetElement("sink").Properties.TryGetDouble("n", out double n);
            Check(n == model.ReactionsById["tick"].Fired, $"{signal}: no element of its own, the actions name theirs ({n})");
            return model.ReactionsById["tick"].Fired;
        }
        Check(Fired("RunStarted", r => { }, 50, out double t0) == 1 && t0 == 0, $"run started: once, at 0 ({t0})");
        int every = Fired("Every", r => r.Set("period", 10.0), 95, out double tl);
        Check(every == 9 && tl == 90, $"every 10 until 95: 9 times, the last at 90 ({every}, {tl})");
        int from = Fired("Every", r => { r.Set("period", 10.0); r.Set("firstTime", 0.0); }, 95, out _);
        Check(from == 10, $"from time 0: 10 times ({from})");
        // a fire at exactly the horizon runs, as every event at that time does (documented: logic.md)
        int atEnd = Fired("Every", r => r.Set("period", 10.0), 100, out double te);
        Check(atEnd == 10 && te == 100, $"every 10 until 100: 10 times, the last at the horizon ({atEnd}, {te})");
        Check(Fired("Every", r => { r.Set("period", 10.0); r.Set("firstTime", 0.0); }, 100, out _) == 11, "from time 0 until 100: 11 times");
        Check(Fired("AtTime", r => r.Set("atTime", 42.0), 95, out double ta) == 1 && ta == 42, $"at 42: once ({ta})");

        string Error(Action<ReactionConfig> edit)
        {
            var c = SwitchModel();
            var r = new ReactionConfig("tick");
            edit(r);
            c.Reactions.Add(r);
            return Fails(() => new HeadlessModelFactory(new SimClock(1), enableLogging: false).BuildModel(c));
        }
        string e1 = Error(r => { r.Set("signal", "Every"); });
        Check(e1 != null && e1.Contains("period"), $"every without a period: {e1}");
        string e2 = Error(r => { r.Set("signal", "AtTime"); r.Set("atTime", 5.0); r.Sources.Add("src"); });
        Check(e2 != null && e2.Contains("time signal"), $"a time signal with elements: {e2}");
        string e3 = Error(r => { r.Set("signal", "RunStarted"); r.AddAction("Stop"); });
        Check(e3 != null && e3.Contains("element or team"), $"a stop of nothing on a time signal: {e3}");
    }

    static void TestTeamMembers()
    {
        // two operators; one is sent off when the run starts and called back at 1000
        var c = SimuLean.Testing.ReferenceModels.ResourceSharedTeam();
        var off = new ReactionConfig("off");
        off.Set("signal", "RunStarted");
        off.AddAction("Stop", state: "OFF_SHIFT", team: "team", count: 1);
        var back = new ReactionConfig("back");
        back.Set("signal", "AtTime");
        back.Set("atTime", 1000.0);
        back.AddAction("Resume", team: "team", count: 1);
        c.Reactions.Add(off); c.Reactions.Add(back);
        var (model, _) = RunModel(c, 2000);
        var t1 = model.Executors.First(x => x.Name == "Team_1");
        var t2 = model.Executors.First(x => x.Name == "Team_2");
        double offTime = t2.GetTimeInState(ElementState.Of("OFF_SHIFT"));
        Check(Math.Abs(offTime - 1000) < 1e-6, $"the last member was off until 1000 ({offTime})");
        Check(t1.GetTimeInState(ElementState.Of("OFF_SHIFT")) == 0, "the first one kept working");
        var (plain, _) = RunModel(SimuLean.Testing.ReferenceModels.ResourceSharedTeam(), 2000);
        double busy2 = model.Executors.First(x => x.Name == "Team_2").GetTimeInState(ElementState.PROCESSING);
        double busy2Plain = plain.Executors.First(x => x.Name == "Team_2").GetTimeInState(ElementState.PROCESSING);
        Check(busy2 < busy2Plain, $"so it worked less ({busy2:0} vs {busy2Plain:0} without the reactions)");

        var bad = SimuLean.Testing.ReferenceModels.ResourceSharedTeam();
        var r = new ReactionConfig("bad");
        r.Set("signal", "RunStarted");
        r.AddAction("Stop", team: "nope");
        bad.Reactions.Add(r);
        string e = Fails(() => new HeadlessModelFactory(new SimClock(1), enableLogging: false).BuildModel(bad));
        Check(e != null && e.Contains("no resource team 'nope'"), $"an unknown team: {e}");
    }

    static void TestResults()
    {
        var (model, _) = RunModel(SwitchModel(), 30.2);
        var o = SimuLean.Results.RunResults.Capture(model).Find(SimuLean.Results.ResultScope.Reaction, "switch");
        Check(o != null && o["fired"] == model.ReactionsById["switch"].Fired && o["not_met"] == model.ReactionsById["switch"].Skipped,
              $"fired / not_met in the results ({o?["fired"]}, {o?["not_met"]})");
        var set = new Study(SwitchModel()).Factor("reaction:switch.reactionEnabled", "true", "false").Metric("f", "reaction:switch.fired").WithRunTime(30.2).Run();
        Check(set.Runs[0].Metrics["f"] > 15 && set.Runs[1].Metrics["f"] == 0, $"a study's metric reaction:switch.fired ({set.Runs[0].Metrics["f"]}, {set.Runs[1].Metrics["f"]})");
        var w = SwitchModel();
        w.WarmupTime = 20;
        var (wm, _) = RunModel(w, 30.2);
        double warm = SimuLean.Results.RunResults.Capture(wm).Find(SimuLean.Results.ResultScope.Reaction, "switch")["fired"];
        Check(warm >= 9 && warm <= 11, $"after a warm-up at 20 only the last ten count ({warm})");
    }

    static void TestNoSubscribers()
    {
        var c = SwitchModel();
        c.Reactions.Clear();
        // the routing reads src's property 'lane': without the reaction that sets it, it is declared (or the build says so)
        c.Elements.Find(e => e.Id == "src").Set(SimuLean.Serialization.PropertyRecords.Key, new List<PropertySettings> { PropertySettings.Number("lane", 0) });
        var (model, clock) = RunModel(c, 10);
        Check(clock.Context.Signals.Count == 0 && model.ReactionsById.Count == 0, "no subscriptions, no reactions");
    }

    // ── an admission condition that reads a property a reaction sets ───
    // Writing a property (SetElementProperty) does more than store the value: it wakes the elements whose
    // admission reads the property, and their refused items are offered again at the same time.

    /// <summary>3 parts at 0 → q → st (1 slot, 1 each, admits while get('Gate', 'open') == 1) → sink; Gate holds open = 0;
    /// a reaction at 5 sets open = 1.</summary>
    static SimulationConfig GateModel(Action<SimulationConfig> edit = null)
    {
        var c = Model(1, 50);
        var src = El("src", "Source");
        src.Set("mode", "Schedule");
        src.Set("arrivalsTable", new Dictionary<string, List<string>>
            { ["Time"] = new List<string> { "0" }, ["Name"] = new List<string> { "P" }, ["Q"] = new List<string> { "3" } });
        c.Elements.Add(src);
        c.Elements.Add(Queue("q", 10));
        var st = Server("st", 1);
        st.Set("inputStrategy.strategyType", "Condition");
        st.Set("inputStrategy.condition", "get('Gate', 'open') == 1");
        c.Elements.Add(st);
        var gate = Queue("Gate", 1);
        gate.Set("properties", new List<PropertySettings> { PropertySettings.Number("open", 0) });
        c.Elements.Add(gate);
        c.Elements.Add(El("sink", "Sink"));
        Connect(c, "src", "q"); Connect(c, "q", "st"); Connect(c, "st", "sink");
        var open = At("open", 5);
        open.AddAction("SetElementProperty", target: "Gate", name: "open", value: 1);
        c.Reactions.Add(open);
        edit?.Invoke(c);
        return c;
    }

    // the times parts enter st, and q's content at the end (horizon 50)
    static (string entries, int left) GateRun(SimulationConfig c)
    {
        var clock = new SimClock(c.Seed);
        var model = new HeadlessModelFactory(clock, enableLogging: false);
        model.BuildModel(c);
        var times = new List<string>();
        model.GetElement("st").ItemEntered += (e, it) => times.Add(clock.GetSimulationTime().ToString(System.Globalization.CultureInfo.InvariantCulture));
        model.StartModel();
        clock.AdvanceClock(50);
        return (string.Join(" ", times), model.GetElement("q").GetQueueLength());
    }

    static void Poke(ReactionConfig r, string target)
    {
        r.AddAction("Stop", target: target, group: "poke");
        r.AddAction("Resume", target: target, group: "poke");
    }

    static void TestAdmissionWakeUp()
    {
        var (e, left) = GateRun(GateModel());
        Check(e == "5 6 7" && left == 0, $"open = 1 at 5 alone: the refused parts are offered again at once ({e}; {left} left in q)");
        (e, left) = GateRun(GateModel(c =>
        {
            c.Elements.Find(x => x.Id == "src").Set("arrivalsTable", new Dictionary<string, List<string>>
                { ["Time"] = new List<string> { "0", "8" }, ["Name"] = new List<string> { "P", "P" }, ["Q"] = new List<string> { "3", "1" } });
        }));
        Check(e == "5 6 7 8" && left == 0, $"and a later arrival passes as usual ({e}; {left} left)");

        // only a property a condition reads wakes: another one writes the value and offers nothing
        (e, left) = GateRun(GateModel(c => c.Reactions[0].Settings.Actions[0].Name = "other"));
        Check(e == "" && left == 3, $"a property no condition reads offers nothing ({e}; {left} left)");
        // the condition may read the property of its own element, or through a computed name (then any write wakes)
        (e, _) = GateRun(GateModel(c =>
        {
            var st = c.Elements.Find(x => x.Id == "st");
            st.Set("inputStrategy.condition", "element.open == 1");
            st.Set("properties", new List<PropertySettings> { PropertySettings.Number("open", 0) });
            c.Reactions[0].Settings.Actions[0].Target = "st";
        }));
        Check(e == "5 6 7", $"element.open of the station itself ({e})");
        (e, _) = GateRun(GateModel(c =>
        {
            var st = c.Elements.Find(x => x.Id == "st");
            st.Set("inputStrategy.condition", "get('Gate', item.which) == 1");
            c.Elements.Find(x => x.Id == "src").Set("createLabels", new List<string> { "which = 'open'" });
        }));
        Check(e == "5 6 7", $"a computed property name: every write wakes ({e})");

        // Retry: a condition that reads no property (time, content, a table) is offered again by a reaction's Retry
        (e, left) = GateRun(GateModel(c =>
        {
            c.Elements.Find(x => x.Id == "st").Set("inputStrategy.condition", "now() >= 5");
            c.Reactions[0].Settings.Actions.Clear();
            c.Reactions[0].AddAction("Retry", target: "st");
        }));
        Check(e == "5 6 7" && left == 0, $"Retry of the station at 5 ({e}; {left} left)");
        (e, left) = GateRun(GateModel(c =>
        {
            c.Elements.Find(x => x.Id == "st").Set("inputStrategy.condition", "now() >= 5");
            c.Reactions[0].Settings.Actions.Clear();
            c.Reactions[0].AddAction("SetElementProperty", target: "Gate", name: "open", value: 1);
        }));
        Check(e == "" && left == 3, $"without Retry such a condition is not evaluated again ({e}; {left} left)");
        Check(Fails(() => new HeadlessModelFactory(new SimClock(1)).BuildModel(GateModel(c => { c.Reactions[0].Settings.Actions.Clear(); c.Reactions[0].AddAction("Retry"); })))
              ?.Contains("choose the element") == true, "a Retry on a time signal needs its element");

        // the former recipes still work (now redundant)
        (e, _) = GateRun(GateModel(c => Poke(c.Reactions[0], "q")));
        Check(e == "5 6 7", $"Stop + Resume of the queue in the same reaction: the end of the stop calls Unblock ({e})");
        (e, _) = GateRun(GateModel(c => Poke(c.Reactions[0], "st")));
        Check(e == "5 6 7", $"Stop + Resume of the station: its end asks upstream again ({e})");
        (e, _) = GateRun(GateModel(c =>
        {
            c.Elements.Find(x => x.Id == "q").Set("gated", true);
            c.Reactions[0].AddAction("Release", target: "q", value: 100);
        }));
        Check(e == "5 6 7", $"a queue released on command: Release re-offers (Unblock) ({e})");
        (e, _) = GateRun(GateModel(c =>
        {
            c.Elements.Find(x => x.Id == "st").Set("inputStrategy.condition", "item.type == 'X' || get('Gate', 'open') == 1");
            var x = El("srcX", "Source");
            x.Set("mode", "Schedule");
            x.Set("arrivalsTable", new Dictionary<string, List<string>>
                { ["Time"] = new List<string> { "6" }, ["Name"] = new List<string> { "X" }, ["Q"] = new List<string> { "1" } });
            c.Elements.Add(x); Connect(c, "srcX", "st");
        }));
        Check(e == "5 6 7 8", $"another origin's part (6) does not delay the gate's ({e})");

        // max wait: the parts no longer wait for a timeout
        (e, left) = GateRun(GateModel(c =>
        {
            var q = c.Elements.Find(x => x.Id == "q"); q.Set("maxWait", 7.0); q.Set("onTimeout", "KeepWaiting");
        }));
        Check(e == "5 6 7" && left == 0, $"maxWait 7, KeepWaiting: the parts left at the gate's opening ({e}; {left} left)");
        SimulationConfig TimeoutPoke(double maxWait) => GateModel(c =>
        {
            var q = c.Elements.Find(x => x.Id == "q"); q.Set("maxWait", maxWait); q.Set("onTimeout", "KeepWaiting");
            var w = new ReactionConfig("timeout", "q"); w.Set("signal", "WaitTimedOut");
            Poke(w, "q");
            c.Reactions.Add(w);
        });
        (e, _) = GateRun(TimeoutPoke(7));
        Check(e == "5 6 7", $"maxWait 7 + a poke on WaitTimedOut: the opening moved them first ({e})");
        (e, left) = GateRun(TimeoutPoke(2));
        Check(e == "5 6 7" && left == 0, $"maxWait 2 + the same poke: the pokes before the opening do nothing, the opening does ({e}; {left} left)");
        (e, left) = GateRun(GateModel(c =>
        {
            c.Elements.Add(El("lost", "Sink")); Connect(c, "q", "lost");
            var q = c.Elements.Find(x => x.Id == "q"); q.Set("maxWait", 7.0); q.Set("onTimeout", "Reroute"); q.Set("timeoutTarget", "lost");
        }));
        Check(e == "" && left == 0, $"maxWait 7, Reroute: the parts leave for the timeout target, never re-evaluated at st ({e}; {left} left)");
    }
    // ── Set Item Look ───────────────────────────────────────

    /// <summary>A random line: src (exponential) → q → srv (exponential, 2 slots) → sink, a reaction on srv's process end
    /// with <paramref name="actions"/> (null = no reaction at all).</summary>
    static SimulationConfig LookModel(SignalDelivery timing, Action<ReactionConfig> actions)
    {
        var c = Model(11, 500);
        var src = Source("src", 1);
        src.Set("interArrivalDistribution.strategyType", "Exponential");
        src.Set("interArrivalDistribution.exponentialRate", 1.0);
        c.Elements.Add(src);
        c.Elements.Add(Queue("q"));
        var srv = Server("srv", 1.6, 2);
        srv.Set("delayStrategy.strategyType", "Exponential");
        srv.Set("delayStrategy.exponentialRate", 1 / 1.6);
        c.Elements.Add(srv);
        c.Elements.Add(El("sink", "Sink"));
        Connect(c, "src", "q"); Connect(c, "q", "srv"); Connect(c, "srv", "sink");
        if (actions != null)
        {
            var r = new ReactionConfig("look", "srv");
            r.Set("signal", "ProcessEnded");
            r.Set("timing", timing.ToString());
            actions(r);
            c.Reactions.Add(r);
        }
        return c;
    }

    // every element statistic, in a fixed order
    static string ElementResults(HeadlessModelFactory model)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var o in SimuLean.Results.RunResults.Capture(model).Of(SimuLean.Results.ResultScope.Element))
            foreach (var m in o.Metrics) sb.Append(o.Id).Append('.').Append(m.Key).Append('=').Append(m.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
        return sb.ToString();
    }

    static void TestSetItemLook()
    {
        // the look is set on the item, outside its labels
        Item seen = null;
        var c = LookModel(SignalDelivery.Immediate, r =>
        {
            r.AddAction("SetItemLook", color: "#FF8000", scale: 1.5);
            r.AddAction("SetItemLook", color: "blue", condition: "now() < 0");   // only if: never — the look stays
        });
        var clock = new SimClock(c.Seed);
        var model = new HeadlessModelFactory(clock, enableLogging: false);
        model.BuildModel(c);
        model.GetElement("sink").ItemEntered += (e, it) => seen = it;
        model.StartModel();
        clock.AdvanceClock(50);
        Check(seen?.Look != null && seen.Look.Color.Value.ToString() == "#FF8000" && seen.Look.Scale == 1.5,
              $"the item that left srv carries the look ({seen?.Look})");
        Check(seen != null && !seen.HasLabel("color") && !seen.HasLabel("scale") && !seen.HasLabel("look"), "no label written");
        Check(model.ReactionsById["look"].Fired > 0, "the reaction fired");

        // a second action changes only what it sets
        var item = new Item(0);
        item.SetLook(new ItemLook(new LookColor(1, 0, 0), 2));
        item.SetLook(new ItemLook(null, double.NaN));
        item.SetLook(new ItemLook(LookColor.Names["green"]));
        Check(item.Look.Color.Value.Equals(LookColor.Names["green"]) && item.Look.Scale == 2, $"a colour keeps the scale ({item.Look})");
        Check(LookColor.TryParse("#0f0", out var g, out _) && g.ToString() == "#00FF00" && LookColor.TryParse("#11223380", out var h, out _) && h.ToString() == "#11223380",
              "#RGB and #RRGGBBAA");

        // results identical to the same model without the action (Immediate: also identical to no reaction at all — nothing scheduled)
        string Results(SimulationConfig cfg) { var (m, _) = RunModel(cfg, 500); return ElementResults(m); }
        string none = Results(LookModel(SignalDelivery.Immediate, null));
        string empty = Results(LookModel(SignalDelivery.Immediate, r => { }));
        string look = Results(LookModel(SignalDelivery.Immediate, r => r.AddAction("SetItemLook", color: "red", scale: 2)));
        Check(none.Length > 0 && look == none && look == empty, "Immediate: the same results as without the action, and as no reaction");
        string emptyNext = Results(LookModel(SignalDelivery.NextEvent, r => { }));
        string lookNext = Results(LookModel(SignalDelivery.NextEvent, r => r.AddAction("SetItemLook", color: "red", scale: 0.5)));
        Check(lookNext == emptyNext && emptyNext.Length > 0, "Next Event: the same results with and without a look action");

        // errors: no item signal, a bad colour, a bad scale, nothing to set
        string Build(Action<ReactionConfig> edit, string signal = "ProcessEnded")
            => Fails(() => new HeadlessModelFactory(new SimClock(1)).BuildModel(LookModel(SignalDelivery.NextEvent, r => { r.Set("signal", signal); edit(r); })));
        Check(Build(r => r.AddAction("SetItemLook", color: "red"), "StateChanged")?.Contains("an item look needs an item signal") == true, "an item signal is needed");
        Check(Build(r => r.AddAction("SetItemLook", color: "reddish"))?.Contains("'reddish' is not a colour") == true, "an unknown colour fails");
        Check(Build(r => r.AddAction("SetItemLook", color: "#12345"))?.Contains("not a colour") == true, "a malformed hex fails");
        Check(Build(r => r.AddAction("SetItemLook", scale: -1))?.Contains("the scale -1 must be > 0") == true, "a scale ≤ 0 fails");
        Check(Build(r => r.AddAction("SetItemLook"))?.Contains("give the colour or the scale") == true, "a look that sets nothing fails");
        Check(Build(r => r.AddAction("SetItemLook", color: "red")) == null, "a good look builds");
    }
}
