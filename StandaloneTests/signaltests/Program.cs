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
        var e = El(id, "UnitySource");
        e.Parameters["sourceMode"] = "InterArrival";
        e.Parameters["interArrivalStrategyType"] = "Constant";
        e.Parameters["constantValue"] = gap;
        return e;
    }

    static ElementConfig Server(string id, double time, int capacity = 1)
    {
        var e = El(id, "UnityMultiServer");
        e.Parameters["capacity"] = capacity;
        e.Parameters["delayStrategyType"] = "Constant";
        e.Parameters["constantValue"] = time;
        return e;
    }

    static ElementConfig Queue(string id, int capacity = 100)
    {
        var e = El(id, "UnityQueue");
        e.Parameters["capacity"] = capacity;
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
        src.Parameters["outputStrategyType"] = "Expression";
        src.Parameters["outputStrategy_expression"] = "element.lane";
        c.Elements.Add(src);
        c.Elements.Add(Server("a", 0.5)); c.Elements.Add(Server("b", 0.5));
        c.Elements.Add(El("sink", "UnitySink"));
        Connect(c, "src", "a"); Connect(c, "src", "b"); Connect(c, "a", "sink"); Connect(c, "b", "sink");
        var r = new ReactionConfig("switch", "sink");
        r.Parameters["signal"] = "ItemEntered";
        r.Parameters["condition"] = "now() >= 10";
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

    static void TestStopResume()
    {
        // a stop for a time: the queue reaching 3 stops the source for 5
        var c = Model();
        c.Elements.Add(Source("src", 1)); c.Elements.Add(Queue("q")); c.Elements.Add(Server("srv", 3)); c.Elements.Add(El("sink", "UnitySink"));
        Connect(c, "src", "q"); Connect(c, "q", "srv"); Connect(c, "srv", "sink");
        var hold = new ReactionConfig("hold", "q");
        hold.Parameters["condition"] = "element.content >= 3";
        hold.AddAction("Stop", target: "src", duration: 5);
        c.Reactions.Add(hold);
        var (model, _) = RunModel(c, 60);
        var src = model.GetElement("src");
        double stopped = src.GetTimeInState(ElementState.STOPPED);
        Check(model.ReactionsById["hold"].Fired > 0 && stopped >= 5, $"the source was stopped ({stopped} time units, {model.ReactionsById["hold"].Fired} firings)");
        Check(src.GetStatsOutputCount() < 55, $"fewer arrivals than without the reaction ({src.GetStatsOutputCount()})");

        // two reactions: the queue full (≥ 4) stops the source, the queue drained (≤ 1) resumes it
        var c2 = Model();
        c2.Elements.Add(Source("src", 1)); c2.Elements.Add(Queue("q")); c2.Elements.Add(Server("srv", 3)); c2.Elements.Add(El("sink", "UnitySink"));
        Connect(c2, "src", "q"); Connect(c2, "q", "srv"); Connect(c2, "srv", "sink");
        var full = new ReactionConfig("full", "q");
        full.Parameters["condition"] = "element.content >= 4";
        full.AddAction("Stop", target: "src");
        var drained = new ReactionConfig("drained", "q");
        drained.Parameters["signal"] = "ItemExited";
        drained.Parameters["condition"] = "element.content <= 1";
        drained.AddAction("Resume", target: "src");
        c2.Reactions.Add(full); c2.Reactions.Add(drained);
        var (m2, _) = RunModel(c2, 200);
        var q = m2.GetElement("q");
        Check(q.GetStatsContentMax() <= 5, $"the queue stays small ({q.GetStatsContentMax()} at most)");
        Check(m2.ReactionsById["full"].Fired > 0 && m2.ReactionsById["drained"].Fired > 0 && m2.GetElement("src").GetStatsOutputCount() > 50,
              $"stopped and resumed by different reactions; the source kept going ({m2.GetElement("src").GetStatsOutputCount()} out)");
    }

    static ReactionConfig At(string id, double t)
    {
        var r = new ReactionConfig(id);
        r.Parameters["signal"] = "AtTime";
        r.Parameters["at"] = t;
        return r;
    }

    static void TestStopGroups()
    {
        // orders stop the station at 5 and resume it at 12; an incident stops it at 10 and is fixed at 20: the orders'
        // resume must not end the incident's stop (stopped 5 → 20), nor a resume of the default group
        var c = Model();
        c.Elements.Add(Source("src", 1)); c.Elements.Add(Queue("q")); c.Elements.Add(Server("srv", 1)); c.Elements.Add(El("sink", "UnitySink"));
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
        c2.Elements.Add(Source("src", 1)); c2.Elements.Add(Queue("q")); c2.Elements.Add(Server("srv", 1)); c2.Elements.Add(El("sink", "UnitySink"));
        Connect(c2, "src", "q"); Connect(c2, "q", "srv"); Connect(c2, "srv", "sink");
        r = At("noOrders", 5); r.AddAction("Stop", target: "srv"); c2.Reactions.Add(r);
        r = At("incident", 10); r.AddAction("Stop", target: "srv"); c2.Reactions.Add(r);
        r = At("order", 12); r.AddAction("Resume", target: "srv"); c2.Reactions.Add(r);
        var (m2, _) = RunModel(c2, 40);
        st = m2.GetElement("srv").GetTimeInState(ElementState.STOPPED);
        Check(Math.Abs(st - 7) < 1e-6, $"one group: the resume ends both stops ({st})");

        // team members: a group's resume calls back only the members that group stopped
        var c3 = SimuLean.Testing.ReferenceModels.ResourceSharedTeam();
        r = new ReactionConfig("offA"); r.Parameters["signal"] = "RunStarted";
        r.AddAction("Stop", state: "OFF_SHIFT", team: "team", count: 1, group: "a");
        r.AddAction("Stop", state: "OFF_SHIFT", team: "team", count: 1, group: "b");
        c3.Reactions.Add(r);
        r = At("backA", 1000); r.AddAction("Resume", team: "team", count: 0, group: "a"); c3.Reactions.Add(r);
        var (m3, _) = RunModel(c3, 2000);
        double off1 = m3.Executors.First(x => x.GetName() == "Team_1").GetTimeInState(ElementState.Of("OFF_SHIFT"));
        double off2 = m3.Executors.First(x => x.GetName() == "Team_2").GetTimeInState(ElementState.Of("OFF_SHIFT"));
        Check(Math.Abs(off2 - 1000) < 1e-6 && Math.Abs(off1 - 2000) < 1e-6,
              $"group b's stop takes the member still working; group a's resume calls back only its own (off {off1}, {off2})");
    }

    static void TestOrderAndOnlyIf()
    {
        // one reaction: at the run start 'left' = 3; at each end of process, decrement, then stop the station only if it
        // reached 0 (the 'only if' sees the decrement): exactly three items, then stopped
        var c = Model();
        c.Elements.Add(Source("src", 1)); c.Elements.Add(Queue("q")); c.Elements.Add(Server("srv", 1)); c.Elements.Add(El("sink", "UnitySink"));
        Connect(c, "src", "q"); Connect(c, "q", "srv"); Connect(c, "srv", "sink");
        var init = new ReactionConfig("init");
        init.Parameters["signal"] = "RunStarted";
        init.AddAction("SetElementProperty", target: "srv", name: "left", value: 3);
        c.Reactions.Add(init);
        var gate = new ReactionConfig("gate", "srv");
        gate.Parameters["signal"] = "ProcessEnded";
        gate.Parameters["delivery"] = "Immediate";
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
            c2.Elements.Add(Source("src", 1)); c2.Elements.Add(El("sink", "UnitySink"));
            Connect(c2, "src", "sink");
            c2.Lists.Add(new ListConfig { Id = "log", Name = "log" });
            foreach (string id in new[] { "first", "second", "third" })
            {
                var r = new ReactionConfig(id, "sink");
                r.Parameters["delivery"] = delivery;
                r.AddAction("SetElementProperty", name: "order", value: id == "first" ? "1" : id == "second" ? "element.order * 10 + 2" : "element.order * 10 + 3");
                c2.Reactions.Add(r);
            }
            var (m2, _) = RunModel(c2, 1.5);
            Check(m2.GetElement("sink").Properties.GetDouble("order") == 123, $"{delivery}: build order ({m2.GetElement("sink").Properties.GetDouble("order")})");
        }

        var bad = Model();
        bad.Elements.Add(Source("src", 1)); bad.Elements.Add(El("sink", "UnitySink")); Connect(bad, "src", "sink");
        var br = new ReactionConfig("broken", "sink");
        br.AddAction("SetElementProperty", name: "x", value: 1, condition: "element.x >");
        bad.Reactions.Add(br);
        string error = Fails(() => RunModel(bad, 1));
        Check(error != null && error.Contains("broken") && error.Contains("only if"), $"a bad 'only if' fails the build naming it ({error})");
    }

    static void TestLabelsAndLists()
    {
        var c = Model();
        c.Elements.Add(Source("src", 1)); c.Elements.Add(Queue("q")); c.Elements.Add(Server("srv", 0.5)); c.Elements.Add(El("sink", "UnitySink"));
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
            c.Elements.Add(Source("src", 2)); c.Elements.Add(Server("srv", 1)); c.Elements.Add(El("sink", "UnitySink"));
            Connect(c, "src", "srv"); Connect(c, "srv", "sink");
            var r = new ReactionConfig("count", "srv");
            r.Parameters["signal"] = "StateChanged";
            r.Parameters["delivery"] = delivery;
            if (state != null) r.Parameters["state"] = state;
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
            c.Reactions[0].Parameters["condition"] = "rand() < 0.5";
            c.Reactions[0].Parameters.Clear();
            c.Reactions[0].Parameters["condition"] = "rand() < 0.5";
            c.Reactions[0].AddAction("SetElementProperty", target: "src", name: "lane", value: "pick(1, 0, 1, 1)");
            c.Elements.Find(e => e.Id == "a").Parameters["delayStrategyType"] = "Exponential";
            c.Elements.Find(e => e.Id == "a").Parameters["exponentialRate"] = 2.0;
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
        string e2 = Error(r => { r.Parameters["signal"] = "StateChanged"; r.AddAction("SetItemLabel", name: "x", value: 1); });
        Check(e2 != null && e2.Contains("action 2") && e2.Contains("item signal"), $"an item label on a signal without an item: {e2}");
        string e3 = Error(r => r.Parameters["condition"] = "now( >= 1");
        Check(e3 != null && e3.Contains("condition"), $"an expression that does not parse: {e3}");
        string e4 = Error(r => r.AddAction("PushToList", name: "nope", value: 1));
        Check(e4 != null && e4.Contains("no list 'nope'"), $"an unknown list: {e4}");
        string e5 = Error(r => r.Parameters["signal"] = "Arrived");
        Check(e5 != null && e5.Contains("ItemEntered"), $"an unknown signal lists the signals: {e5}");
        string e6 = Error(r => r.AddAction("Stop", target: "src", duration: -1));
        Check(e6 != null && e6.Contains("duration"), $"a negative stop duration: {e6}");
    }

    static void TestRunError()
    {
        var c = SwitchModel();
        c.Reactions[0].Parameters["condition"] = "table('nope', 1, 'x') > 0";
        string e = Fails(() => RunModel(c, 20));
        Check(e != null && e.Contains("Reaction 'switch'") && e.Contains("'nope'"), $"the error names the reaction and the cause: {e}");
    }

    static void TestCatalog()
    {
        var c = SwitchModel();
        c.Reactions[0].AddAction("Stop", target: "a", duration: 2);
        var keys = ModelParameterCatalog.ForReaction(c.Reactions[0]).Select(p => p.Key).ToList();
        Check(keys.SequenceEqual(new[] { "enabled", "a1.number", "a2.duration" }),
              $"enabled and the actions' numbers are offered, nothing else ({string.Join(", ", keys)})");
        Check(ModelParameterCatalog.Undecided(c).Count == 0, $"nothing undecided ({string.Join(", ", ModelParameterCatalog.Undecided(c))})");

        var set = new Study(SwitchModel()).Factor("reaction:switch.enabled", "true", "false").Metric("b", "b.input").WithRunTime(30.2).Run();
        Check(set.Runs.Count == 2 && set.Runs[0].Metrics["b"] > 15 && set.Runs[1].Metrics["b"] == 0,
              $"a study switches the reaction on and off (b {set.Runs[0].Metrics["b"]} / {set.Runs[1].Metrics["b"]})");
        var lane = new Study(SwitchModel()).Factor("reaction:switch.a1.number", "0", "1").Metric("b", "b.input").WithRunTime(30.2).Run();
        Check(lane.Runs[0].Metrics["b"] == 0 && lane.Runs[1].Metrics["b"] > 15, $"and an action's number ({lane.Runs[0].Metrics["b"]} / {lane.Runs[1].Metrics["b"]})");
    }

    static void TestProcessEnded()
    {
        var c = Model();
        c.Elements.Add(Source("src", 2)); c.Elements.Add(Server("srv", 1)); c.Elements.Add(El("sink", "UnitySink"));
        Connect(c, "src", "srv"); Connect(c, "srv", "sink");
        c.Lists.Add(new ListConfig { Id = "done", Name = "done" });
        var r = new ReactionConfig("finished", "srv");
        r.Parameters["signal"] = "ProcessEnded";
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
            r.Parameters["signal"] = signal;
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
        int every = Fired("Every", r => r.Parameters["period"] = 10.0, 95, out double tl);
        Check(every == 9 && tl == 90, $"every 10 until 95: 9 times, the last at 90 ({every}, {tl})");
        int from = Fired("Every", r => { r.Parameters["period"] = 10.0; r.Parameters["first"] = 0.0; }, 95, out _);
        Check(from == 10, $"from time 0: 10 times ({from})");
        Check(Fired("AtTime", r => r.Parameters["at"] = 42.0, 95, out double ta) == 1 && ta == 42, $"at 42: once ({ta})");

        string Error(Action<ReactionConfig> edit)
        {
            var c = SwitchModel();
            var r = new ReactionConfig("tick");
            edit(r);
            c.Reactions.Add(r);
            return Fails(() => new HeadlessModelFactory(new SimClock(1), enableLogging: false).BuildModel(c));
        }
        string e1 = Error(r => { r.Parameters["signal"] = "Every"; });
        Check(e1 != null && e1.Contains("period"), $"every without a period: {e1}");
        string e2 = Error(r => { r.Parameters["signal"] = "AtTime"; r.Parameters["at"] = 5.0; r.Sources.Add("src"); });
        Check(e2 != null && e2.Contains("time signal"), $"a time signal with elements: {e2}");
        string e3 = Error(r => { r.Parameters["signal"] = "RunStarted"; r.AddAction("Stop"); });
        Check(e3 != null && e3.Contains("element or team"), $"a stop of nothing on a time signal: {e3}");
    }

    static void TestTeamMembers()
    {
        // two operators; one is sent off when the run starts and called back at 1000
        var c = SimuLean.Testing.ReferenceModels.ResourceSharedTeam();
        var off = new ReactionConfig("off");
        off.Parameters["signal"] = "RunStarted";
        off.AddAction("Stop", state: "OFF_SHIFT", team: "team", count: 1);
        var back = new ReactionConfig("back");
        back.Parameters["signal"] = "AtTime";
        back.Parameters["at"] = 1000.0;
        back.AddAction("Resume", team: "team", count: 1);
        c.Reactions.Add(off); c.Reactions.Add(back);
        var (model, _) = RunModel(c, 2000);
        var t1 = model.Executors.First(x => x.GetName() == "Team_1");
        var t2 = model.Executors.First(x => x.GetName() == "Team_2");
        double offTime = t2.GetTimeInState(ElementState.Of("OFF_SHIFT"));
        Check(Math.Abs(offTime - 1000) < 1e-6, $"the last member was off until 1000 ({offTime})");
        Check(t1.GetTimeInState(ElementState.Of("OFF_SHIFT")) == 0, "the first one kept working");
        var (plain, _) = RunModel(SimuLean.Testing.ReferenceModels.ResourceSharedTeam(), 2000);
        double busy2 = model.Executors.First(x => x.GetName() == "Team_2").GetTimeInState(ElementState.PROCESSING);
        double busy2Plain = plain.Executors.First(x => x.GetName() == "Team_2").GetTimeInState(ElementState.PROCESSING);
        Check(busy2 < busy2Plain, $"so it worked less ({busy2:0} vs {busy2Plain:0} without the reactions)");

        var bad = SimuLean.Testing.ReferenceModels.ResourceSharedTeam();
        var r = new ReactionConfig("bad");
        r.Parameters["signal"] = "RunStarted";
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
        var set = new Study(SwitchModel()).Factor("reaction:switch.enabled", "true", "false").Metric("f", "reaction:switch.fired").WithRunTime(30.2).Run();
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
        var (model, clock) = RunModel(c, 10);
        Check(clock.Context.Signals.Count == 0 && model.ReactionsById.Count == 0, "no subscriptions, no reactions");
    }
}
