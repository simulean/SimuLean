// Standalone tests of the choice primitives:
// 1a best-score routing; 1b a Queue's offer order, list queries and orders written as expressions; 1d a team's named
// request orders; 1c the expression functions ln, exp, clamp, triangular, lognormal.
// Run: dotnet run  (from StandaloneTests/choicetests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Expressions;
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

    static int Main()
    {
        Run("1a best-score routing: the highest score wins, ties by order, full outputs skipped or waited for", TestBestScore);
        Run("1a best-score routing: the score's context (out., no random) and its build check", TestBestScoreChecks);
        Run("1b a Queue's offer order: best first by order keys (labels or expressions), ties by arrival", TestOfferSort);
        Run("1b list queries and order keys written as expressions", TestListExpressions);
        Run("1d a team's named request orders: requestOrder names one, a study chooses among them", TestNamedRequestOrders);
        Run("1c ln, exp, clamp; triangular and lognormal with the samplers' parameters, from the use's stream", TestFunctions);
        Run("5 routing rule: = the BestScore expression it mirrors (Skip and Wait), ties by order, reads the context", TestRuleRouting);
        Run("5 release rule: = the offerSort it mirrors (FIFO and AnyAccepted); not with offerSort", TestRuleRelease);
        Run("5 dispatch rule: = the request order it mirrors, in place or as a named order; parameters reach it", TestRuleDispatch);
        Run("5 rule parameters in the catalog (rule.<Field>), varied by a study; the rule a choice of the site's rules", TestRuleCatalog);
        Run("5 a rule's random stream: per decision, reproducible for a seed", TestRuleRandom);
        Run("5 build errors (missing type, wrong interface, parameters, contract) and errors in a run (t=, owner, rule)", TestRuleErrors);
        Run("5 the logic viewer shows a rule, its parameters and reads; the config round-trips", TestRuleViewer);
        Run("6 the card's scripts: the rules used and the decisions by rung; validate warns on a rule without Why or equal to its primitive", TestRuleScripts);
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    static ElementConfig Cfg(string id, string type) => new ElementConfig(id, type, id);

    static ElementConfig Station(string id, double time, int capacity = 1)
    {
        var e = Cfg(id, "MultiServer");
        e.Set("capacity", capacity);
        e.Set("delayStrategy", SamplerSettings.Constant(time));
        return e;
    }

    static ElementConfig Arrivals(string id, params double[] times)
    {
        var src = Cfg(id, "Source");
        src.Set("mode", "Schedule");
        src.Set("arrivalsTable", new Dictionary<string, List<string>>
        {
            ["Time"] = times.Select(t => t.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList(),
            ["Name"] = times.Select(_ => "P").ToList(),
            ["Q"] = times.Select(_ => "1").ToList(),
        });
        return src;
    }

    // src (parts at the times given) -> router (best score) -> m1 | m2 | m3 (capacity 1, 5 each, a property 'speed') -> sink
    static SimulationConfig ScoredModel(string score, BestScoreFull whenFull, double[] speeds, params double[] arrivals)
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
        c.Elements.Add(Arrivals("src", arrivals));
        var router = Station("router", 0, 10);
        router.Set("outputStrategy.strategyType", "BestScore");
        router.Set("outputStrategy.scoreExpression", score);
        router.Set("outputStrategy.whenBestFull", whenFull);
        c.Elements.Add(router);
        for (int i = 0; i < speeds.Length; i++)
        {
            var m = Station("m" + (i + 1), 5);
            m.Set(PropertyRecords.Key, new List<PropertySettings> { PropertySettings.Number("speed", speeds[i]) });
            c.Elements.Add(m);
            c.Connections.Add(new ConnectionConfig("router", "m" + (i + 1), "General"));
            c.Connections.Add(new ConnectionConfig("m" + (i + 1), "sink", "General"));
        }
        c.Elements.Add(Cfg("sink", "Sink"));
        c.Connections.Insert(0, new ConnectionConfig("src", "router", "General"));
        return c;
    }

    // the times each station took an item, by station
    static Dictionary<string, List<double>> Entries(SimulationConfig c, double until = 100)
    {
        var model = new HeadlessModelFactory(new SimClock(1));
        model.BuildModel(c);
        var entries = new Dictionary<string, List<double>>();
        foreach (var e in c.Elements.Where(e => e.Id.StartsWith("m")))
        {
            var list = entries[e.Id] = new List<double>();
            model.Find(e.Id).ItemEntered += (el, it) => list.Add(model.Clock.GetSimulationTime());
        }
        model.StartModel();
        model.Clock.AdvanceClock(until);
        return entries;
    }

    static string Show(Dictionary<string, List<double>> e) => string.Join("; ", e.Select(kv => kv.Key + ": " + string.Join(",", kv.Value)));

    static void TestBestScore()
    {
        // speeds 1, 3, 2: the first part to m2 (3), the second (m2 busy) to m3 (2), the third to m1
        var skip = Entries(ScoredModel("out.speed", BestScoreFull.Skip, new[] { 1.0, 3, 2 }, 0, 1, 2));
        Check(skip["m2"].SequenceEqual(new[] { 0.0 }) && skip["m3"].SequenceEqual(new[] { 1.0 }) && skip["m1"].SequenceEqual(new[] { 2.0 }),
              "Skip: the best output with room: " + Show(skip));

        // Wait: every part waits for m2, which takes them at 0, 5, 10
        var wait = Entries(ScoredModel("out.speed", BestScoreFull.Wait, new[] { 1.0, 3, 2 }, 0, 1, 2));
        Check(wait["m2"].SequenceEqual(new[] { 0.0, 5, 10 }) && wait["m1"].Count == 0 && wait["m3"].Count == 0,
              "Wait: the parts wait for the best output: " + Show(wait));

        // ties go to the first in connection order (equal speeds: m1, then m2 while m1 is busy, then m3)
        var ties = Entries(ScoredModel("out.speed", BestScoreFull.Skip, new[] { 2.0, 2, 2 }, 0, 1, 2));
        Check(ties["m1"].SequenceEqual(new[] { 0.0 }) && ties["m2"].SequenceEqual(new[] { 1.0 }) && ties["m3"].SequenceEqual(new[] { 2.0 }),
              "ties: the first in order: " + Show(ties));

        // the score reads the output's built-ins and the item: content and a label; a negative score still wins when best
        var builtIns = Entries(ScoredModel("-out.content - out.speed", BestScoreFull.Skip, new[] { 3.0, 1, 2 }, 0, 0, 0));
        Check(builtIns["m2"].SequenceEqual(new[] { 0.0 }) && builtIns["m3"].SequenceEqual(new[] { 0.0 }) && builtIns["m1"].SequenceEqual(new[] { 0.0 }),
              "a negative score: the lowest speed first: " + Show(builtIns));

        // a model read: count(out.name, ...) — the station holding no item of the kind
        var count = Entries(ScoredModel("out.free * 10 + out.speed", BestScoreFull.Skip, new[] { 1.0, 2, 3 }, 0, 1));
        Check(count["m3"].SequenceEqual(new[] { 0.0 }) && count["m2"].SequenceEqual(new[] { 1.0 }), "free and speed together: " + Show(count));

        // a scene's results: the same as the same rule written as an Expression routing (nested ternaries)
        var ternary = ScoredModel("out.speed", BestScoreFull.Skip, new[] { 1.0, 3, 2 }, 0, 1, 2);
        var r = ternary.Elements.First(e => e.Id == "router");
        r.Set("outputStrategy.strategyType", "Expression");
        r.Set("outputStrategy.routingExpression", "free('m2') > 0 ? 1 : (free('m3') > 0 ? 2 : 0)");
        Check(Show(Entries(ternary)) == Show(skip), "one score line = the nested ternaries it replaces");
    }

    // src (one part per time, label prio) -> queue (offerSort) -> station (capacity 1, 5 each, admission) -> sink
    static SimulationConfig QueuedModel(string offerSort, double[] times, double[] prios, OfferOrder order = OfferOrder.FIFO, string admission = null)
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
        var src = Arrivals("src", times);
        var table = src.Get<Dictionary<string, List<string>>>("arrivalsTable", null);
        table["prio"] = prios.Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        src.Set("arrivalsTable", table);
        c.Elements.Add(src);
        var q = Cfg("q", "Queue");
        q.Set("offerOrder", order);
        if (offerSort != null) q.Set("offerSort", offerSort);
        c.Elements.Add(q);
        var m = Station("m1", 5);
        if (admission != null) { m.Set("inputStrategy.strategyType", "Condition"); m.Set("inputStrategy.condition", admission); }
        c.Elements.Add(m);
        c.Elements.Add(Cfg("sink", "Sink"));
        foreach (var (a, b) in new[] { ("src", "q"), ("q", "m1"), ("m1", "sink") }) c.Connections.Add(new ConnectionConfig(a, b, "General"));
        return c;
    }

    // the prio of each part, in the order the station took them, with the time
    static List<(double t, double prio)> Taken(SimulationConfig c, double until = 100)
    {
        var model = new HeadlessModelFactory(new SimClock(1));
        model.BuildModel(c);
        var taken = new List<(double, double)>();
        model.Find("m1").ItemEntered += (el, it) => taken.Add((model.Clock.GetSimulationTime(), it.GetLabelValue("prio") ?? -1));
        model.StartModel();
        model.Clock.AdvanceClock(until);
        return taken;
    }

    static string Prios(List<(double t, double prio)> l) => string.Join(",", l.Select(x => x.prio));

    static void TestOfferSort()
    {
        double[] times = { 0, 1, 2, 3 }, prios = { 1, 3, 2, 5 };
        Check(Prios(Taken(QueuedModel(null, times, prios))) == "1,3,2,5", "no offer order: oldest first (unchanged)");
        Check(Prios(Taken(QueuedModel("prio desc", times, prios))) == "1,5,3,2", "prio desc: the best waiting part next");
        Check(Prios(Taken(QueuedModel("item.prio * -1", times, prios))) == "1,5,3,2", "an expression key");
        Check(Prios(Taken(QueuedModel("0", times, prios))) == "1,3,2,5", "every key equal: ties by arrival");
        Check(Prios(Taken(QueuedModel("prio desc", times, prios, OfferOrder.AnyAccepted))) == "1,5,3,2", "AnyAccepted offers best first");
        // two keys: 'kind' first (missing = 0 for all), then prio ascending
        Check(Prios(Taken(QueuedModel("has(item.kind), prio", times, prios))) == "1,2,3,5", "two keys, the second decides");

        // a new best part is offered at once: the head (prio 1) is refused, the new one (prio 5) enters when it arrives
        var refused = Taken(QueuedModel("prio desc", new double[] { 0, 1 }, new double[] { 1, 5 }, OfferOrder.FIFO, "item.prio >= 4"));
        Check(refused.Count == 1 && refused[0].t == 1 && refused[0].prio == 5, "a new best part goes past a refused head: " +
              string.Join(";", refused.Select(x => x.t + ":" + x.prio)));
        // without the order the FIFO head blocks it (the default is unchanged)
        Check(Taken(QueuedModel(null, new double[] { 0, 1 }, new double[] { 1, 5 }, OfferOrder.FIFO, "item.prio >= 4")).Count == 0, "FIFO: the refused head holds the rest");

        // the keys are checked when the model is built
        string Build(string sort)
        {
            try { new HeadlessModelFactory(new SimClock(1)).BuildModel(QueuedModel(sort, times, prios)); return null; }
            catch (Exception e) { return e.Message; }
        }
        string bad = Build("prio desc, content('nowhere')");
        Check(bad != null && bad.Contains("nowhere"), "an unknown element in a key fails the build: " + bad);
        string random = Build("rand()");
        Check(random != null && random.Contains("rand"), "no random function in an offer order: " + random);
    }

    static void TestListExpressions()
    {
        var spec = SimuLean.Collections.ListQuerySpec.Decode("prio * 2 > 3; kind == 'a' | max(prio, 1) * -1, age desc");
        Check(spec.conditions.Count == 2 && spec.conditions[0].field == "prio * 2 > 3" && spec.conditions[0].op == SimuLean.Collections.ListOp.NotEqual
              && spec.conditions[1].field == "kind", "a condition that is not 'field op value' is an expression");
        var keys = spec.OrderKeys();
        Check(keys.Count == 2 && keys[0].Key == "max(prio, 1) * -1" && !keys[0].Value && keys[1].Key == "age" && keys[1].Value,
              "a key's commas inside parentheses do not split it: " + string.Join(" | ", keys.Select(k => k.Key)));
        Check(SimuLean.Collections.ListQuerySpec.Decode("a == 1 || b == 2").conditions.Count == 1, "'||' is not the order bar");

        var clock = new SimClock(1);
        var list = new SimuLean.Collections.SimList("L", clock);
        foreach (var (p, k) in new[] { (1.0, "a"), (4.0, "b"), (3.0, "a"), (6.0, "a") })
        {
            var it = new Item(0); it.SetLabelValue("prio", p); it.SetLabelText("kind", k);
            list.PushItem(it, null);
        }
        var q = SimuLean.Collections.ListQuerySpec.Decode("prio * 2 > 3; kind == 'a' | prio * -1");
        list.DefineExpressionKeys(q, "test");
        Check(list.TryPull(q.ToQuery(), out var e) && e.Item.GetLabelValue("prio") == 6, "expression condition and order: the a with the highest prio");
        Check(list.TryPull(q.ToQuery(), out e) && e.Item.GetLabelValue("prio") == 3, "then the next");
        Check(!list.TryPull(q.ToQuery(), out e), "prio 1 fails the expression condition");

        // only a list of items takes expressions; a team's request order does not
        string err = null;
        try { SimuLean.Collections.ListFields.Check(SimuLean.Collections.ListQuerySpec.Decode("|priority * 2"), "|priority * 2", SimuLean.Collections.ListFields.Request,
                                                     SimuLean.Collections.ListFields.RequestRoots, false, "team 'T'"); }
        catch (FormatException x) { err = x.Message; }
        Check(err != null && err.Contains("expression"), "an expression key in a request order is refused: " + err);
    }

    // two stations (prio 5 and 1, three parts each at 0) sharing one operator whose requests are ordered by requestOrder
    static SimulationConfig TeamModel(string requestOrder)
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 50 };
        foreach (var (name, prio) in new[] { ("high", 5.0), ("low", 1.0) })
        {
            var src = Arrivals("src_" + name, 0, 0, 0);
            var table = src.Get<Dictionary<string, List<string>>>("arrivalsTable", null);
            table["prio"] = new List<string> { prio.ToString(System.Globalization.CultureInfo.InvariantCulture), "" + prio, "" + prio };
            src.Set("arrivalsTable", table);
            c.Elements.Add(src);
            c.Elements.Add(Cfg("q_" + name, "Queue"));
            var m = Station(name, 1, 3);
            m.Set("processBy.team", "ops"); m.Set("processBy.release", "EndOfProcess"); m.Set("processBy.priority", "prio");
            c.Elements.Add(m);
            c.Connections.Add(new ConnectionConfig("src_" + name, "q_" + name, "General"));
            c.Connections.Add(new ConnectionConfig("q_" + name, name, "General"));
            c.Connections.Add(new ConnectionConfig(name, "sink", "General"));
        }
        c.Elements.Add(Cfg("sink", "Sink"));
        var t = new ResourceTeamConfig { Id = "ops", Name = "ops", RequestOrder = requestOrder };
        t.Members.Add(new ResourceMemberConfig { Name = "Op" });
        c.ResourceTeams.Add(t);
        return c;
    }

    // the total time in the system of the high-priority parts, and of the low ones
    static (double high, double low) Served(SimulationConfig c)
    {
        var model = new HeadlessModelFactory(new SimClock(1));
        model.BuildModel(c);
        double high = 0, low = 0;
        model.Find("sink").ItemEntered += (el, it) =>
        {
            double t = model.Clock.GetSimulationTime() - it.GetCreationTime();
            if (it.GetLabelValue("prio") == 5) high += t; else low += t;
        };
        model.StartModel();
        model.Clock.AdvanceClock(c.MaxSimulationTime);
        return (Math.Round(high, 6), Math.Round(low, 6));
    }

    static void TestNamedRequestOrders()
    {
        // the request order written in place, then by name: the same run
        var inPlace = Served(TeamModel("|priority desc"));
        var named = TeamModel("");
        var team = named.ResourceTeams[0];
        team.Set("namedOrders", new List<NamedOrderSettings> { NamedOrderSettings.Of("LowFirst", "|priority"), NamedOrderSettings.Of("Urgent", "|priority desc") });
        team.Set("requestOrder", "Urgent");
        Check(Served(named) == inPlace, $"a named order gives the run of the same order written in place: {Served(named)} vs {inPlace}");
        team.Set("requestOrder", "LowFirst");
        var lowFirst = Served(named);
        Check(lowFirst.high > inPlace.high && lowFirst.low < inPlace.low, $"the other named order: the high-priority parts wait longer, the low ones less: {lowFirst} vs {inPlace}");

        // the catalog offers the request order as a choice of the named orders (a study can compare them)
        team.Set("requestOrder", "Urgent");
        var info = SimuLean.Optimization.ModelParameterCatalog.ForTeam(team);
        var ro = info.FirstOrDefault(i => i.Key == "requestOrder");
        Check(ro != null && ro.Kind == SimuLean.Optimization.VariableKind.Choice && string.Join(",", ro.Options) == "LowFirst,Urgent" && ro.CurrentText == "Urgent",
              "requestOrder is a choice of the named orders: " + (ro == null ? "absent" : string.Join(",", ro.Options ?? new string[0])));
        team.Set("requestOrder", "|priority desc");
        var inPlaceOrder = SimuLean.Optimization.ModelParameterCatalog.ForTeam(team).FirstOrDefault(i => i.Key == "requestOrder");
        Check(inPlaceOrder != null && inPlaceOrder.IsText && inPlaceOrder.CurrentText == "|priority desc",
              "an order written in place is a text setting, not a choice");
        Check(SimuLean.Optimization.ModelParameterCatalog.Undecided(named).Count == 0, "every key decided");

        // a name that is none of the named orders fails the build, naming them
        team.Set("requestOrder", "Urgnt");
        string err = null;
        try { new HeadlessModelFactory(new SimClock(1)).BuildModel(named); } catch (Exception x) { err = x.Message; }
        Check(err != null && err.Contains("Urgnt") && err.Contains("Urgent"), "an unknown named order fails the build: " + err);

        // a study varies it by name (the path team:T.requestOrder)
        team.Set("requestOrder", "Urgent");
        var json = ConfigJson.Serialize(named);
        var back = ConfigJson.Deserialize(json).ResourceTeams[0];
        Check(back.NamedOrders.Count == 2 && back.NamedOrders[1].Query == "|priority desc" && back.RequestOrder == "Urgent", "the named orders round-trip");
    }

    // ── choice rules (the examples of Assets/SimuLean.UserLogic and the test rules at the end) ──

    static SimulationConfig RuleRouted(string rule, BestScoreFull whenFull, double[] speeds, params double[] arrivals)
    {
        var c = ScoredModel("0", whenFull, speeds, arrivals);
        var r = c.Elements.First(e => e.Id == "router");
        r.Set("outputStrategy.strategyType", "Rule");
        r.Set("outputStrategy.scoreExpression", null);
        r.Set("outputStrategy.rule", rule);
        return c;
    }

    static void TestRuleRouting()
    {
        double[] arrivals = { 0, 0, 0.5, 1, 1, 2, 3, 3, 3, 4, 6, 6, 7 };
        foreach (var mode in new[] { BestScoreFull.Skip, BestScoreFull.Wait })
        {
            var declarative = Entries(ScoredModel("-out.content", mode, new[] { 1.0, 3, 2 }, arrivals));
            var ruled = Entries(RuleRouted("ShortestQueueRule", mode, new[] { 1.0, 3, 2 }, arrivals));
            Check(Show(ruled) == Show(declarative) && declarative.Values.Sum(l => l.Count) > 3,
                  $"{mode}: ShortestQueueRule = BestScore '-out.content': {Show(ruled)} vs {Show(declarative)}");
        }
        // a rule reading the output's property: = the score 'out.speed', ties to the first in order
        var speed = Entries(RuleRouted("SpeedRule", BestScoreFull.Skip, new[] { 1.0, 3, 2 }, 0, 1, 2));
        Check(Show(speed) == Show(Entries(ScoredModel("out.speed", BestScoreFull.Skip, new[] { 1.0, 3, 2 }, 0, 1, 2))), "SpeedRule = out.speed: " + Show(speed));
        var ties = Entries(RuleRouted("SpeedRule", BestScoreFull.Skip, new[] { 2.0, 2, 2 }, 0, 1, 2));
        Check(ties["m1"].SequenceEqual(new[] { 0.0 }) && ties["m2"].SequenceEqual(new[] { 1.0 }) && ties["m3"].SequenceEqual(new[] { 2.0 }), "ties: connection order: " + Show(ties));
        // the context: the owner, the item, the candidates, now, the output and a model read by name
        var c = RuleRouted("ContextProbeRule", BestScoreFull.Skip, new[] { 1.0, 3, 2 }, 0, 2);
        var model = new HeadlessModelFactory(new SimClock(1));
        model.BuildModel(c);
        model.StartModel();
        model.Clock.AdvanceClock(10);
        var probe = (ContextProbeRule)((SimuLean.Rules.RuleRoutingStrategy)model.Find("router").GetOutputStrategy()).Rule;
        Check(probe.Seen.Contains("router|P|3|0|m1|0") && probe.Seen.Contains("router|P|2|2|m3|1"),
              "the context: owner, item, candidates, now, output and a model read: " + string.Join("; ", probe.Seen.Take(6)));
    }

    static SimulationConfig RuleQueued(string rule, double[] times, double[] prios, OfferOrder order = OfferOrder.FIFO, bool duePrio = false)
    {
        var c = QueuedModel(null, times, prios, order);
        var q = c.Elements.First(e => e.Id == "q");
        q.Set("offerRule", rule);
        if (duePrio) q.Set("rule", new List<PropertySettings> { PropertySettings.OfText("DueLabel", "prio") });
        return c;
    }

    static void TestRuleRelease()
    {
        double[] times = { 0, 1, 2, 3, 3, 4, 4, 9 }, prios = { 1, 3, 2, 5, 3, 0, 2, 4 };
        foreach (var order in new[] { OfferOrder.FIFO, OfferOrder.AnyAccepted })
        {
            string byRule = Prios(Taken(RuleQueued("PrioRule", times, prios, order)));
            Check(byRule == Prios(Taken(QueuedModel("prio desc", times, prios, order))), $"{order}: PrioRule = offerSort 'prio desc': " + byRule);
            string edd = Prios(Taken(RuleQueued("EarliestDueDateRule", times, prios, order, true)));
            Check(edd == Prios(Taken(QueuedModel("prio", times, prios, order))), $"{order}: EarliestDueDateRule (DueLabel = prio) = offerSort 'prio': " + edd);
        }
        Check(Prios(Taken(RuleQueued("EarliestDueDateRule", times, prios))) == Prios(Taken(QueuedModel(null, times, prios))), "no label 'due': every score 0, arrival order");
        var both = RuleQueued("PrioRule", times, prios);
        both.Elements.First(e => e.Id == "q").Set("offerSort", "prio");
        string err = BuildError(both);
        Check(err != null && err.Contains("offerSort") && err.Contains("offerRule"), "offerSort and offerRule together fail: " + err);
    }

    static void TestRuleDispatch()
    {
        var declarative = Served(TeamModel("|priority desc"));
        var byRule = Served(TeamModel("rule:HighestPriorityRule"));
        Check(byRule == declarative, $"HighestPriorityRule = |priority desc: {byRule} vs {declarative}");
        var named = TeamModel("ByRule");
        named.ResourceTeams[0].Set("namedOrders", new List<NamedOrderSettings> { NamedOrderSettings.Of("Fifo", ""), NamedOrderSettings.Of("ByRule", "rule:HighestPriorityRule") });
        Check(Served(named) == declarative, "a named order naming the rule: the same run");
        var lowServed = Served(TeamModel("rule:LowestPriorityRule"));
        Check(lowServed == Served(TeamModel("|priority")) && lowServed != declarative, $"LowestPriorityRule = |priority: {lowServed}");
        var shifted = TeamModel("rule:LowestPriorityRule");
        shifted.ResourceTeams[0].Set("rule", new List<PropertySettings> { PropertySettings.Number("Shift", 10) });
        Check(Served(shifted) == lowServed, "a parameter's value reaches the rule (Shift adds the same to every score)");
        var bad = TeamModel("rule:LowestPriorityRule");
        bad.ResourceTeams[0].Set("rule", new List<PropertySettings> { PropertySettings.Number("Shif", 10) });
        string unknown = BuildError(bad);
        Check(unknown != null && unknown.Contains("no parameter 'Shif'") && unknown.Contains("Shift"), "an unknown team rule parameter: " + unknown);
        string err = BuildError(TeamModel("rule:ShortestQueueRule"));
        Check(err != null && err.Contains("not a dispatch rule") && err.Contains("IDispatchRule"), "a routing rule as a request order fails: " + err);
    }

    static void TestRuleCatalog()
    {
        var c = RuleRouted("ShortestQueueRule", BestScoreFull.Skip, new[] { 1.0, 3, 2 }, 0, 0, 0, 1);
        var router = c.Elements.First(e => e.Id == "router");
        var info = SimuLean.Optimization.ModelParameterCatalog.ForElement(router);
        var w = info.FirstOrDefault(i => i.Key == "rule.FreeWeight");
        Check(w != null && w.Current == 0 && w.Kind == SimuLean.Optimization.VariableKind.Real, "rule.FreeWeight offered at the rule's default: " + string.Join(", ", info.Select(i => i.Key)));
        var choice = info.FirstOrDefault(i => i.Key == "outputStrategy.rule");
        Check(choice != null && choice.Kind == SimuLean.Optimization.VariableKind.Choice && choice.Options.Contains("ShortestQueueRule") && choice.Options.Contains("SpeedRule")
              && !choice.Options.Contains("PrioRule"), "outputStrategy.rule: a choice of the routing rules: " + (choice == null ? "absent" : string.Join(",", choice.Options)));
        var st = info.FirstOrDefault(i => i.Key == "outputStrategy.strategyType");
        Check(st != null && st.Options.Contains("Rule") && st.Options.Contains("BestScore"), "strategyType offers Rule beside the declarative modes");
        Check(SimuLean.Optimization.ModelParameterCatalog.Undecided(c).Count == 0, "every key decided: " + string.Join(", ", SimuLean.Optimization.ModelParameterCatalog.Undecided(c)));
        // a study writes it: the rule is built with the value
        var p = SimuLean.Optimization.ConfigParameter.Of(c, SimuLean.Optimization.ParameterScope.Element, "router", "rule.FreeWeight");
        p.ApplyText(c, "2.5");
        var model = new HeadlessModelFactory(new SimClock(1));
        model.BuildModel(c);
        var rule = (SimuLean.UserLogic.Examples.ShortestQueueRule)((SimuLean.Rules.RuleRoutingStrategy)model.Find("router").GetOutputStrategy()).Rule;
        Check(rule.FreeWeight == 2.5, "the study's value reaches the rule: " + rule.FreeWeight);
        var team = TeamModel("rule:HighestPriorityRule").ResourceTeams[0];
        Check(SimuLean.Optimization.ModelParameterCatalog.ForTeam(team).Any(i => i.Key == "rule.AgeWeight" && i.Current == 0), "a team's dispatch rule parameter: team:T.rule.AgeWeight");
        var q = RuleQueued("EarliestDueDateRule", new double[] { 0 }, new double[] { 1 }).Elements.First(e => e.Id == "q");
        Check(!SimuLean.Optimization.ModelParameterCatalog.ForElement(q).Any(i => i.Key.StartsWith("rule.")), "a text parameter is not offered");
    }

    static void TestRuleRandom()
    {
        double[] arrivals = { 0, 0, 0, 1, 1, 2, 2, 2, 3, 5, 5, 6, 8, 8 };
        string RunSeed(int seed)
        {
            var c = RuleRouted("RandomRule", BestScoreFull.Wait, new[] { 1.0, 1, 1 }, arrivals);
            var model = new HeadlessModelFactory(new SimClock(seed));
            model.BuildModel(c);
            var log = new List<string>();
            foreach (var m in new[] { "m1", "m2", "m3" }) model.Find(m).ItemEntered += (el, it) => log.Add(el.Name + "@" + model.Clock.GetSimulationTime());
            model.StartModel();
            model.Clock.AdvanceClock(100);
            var rule = (RandomRule)((SimuLean.Rules.RuleRoutingStrategy)model.Find("router").GetOutputStrategy()).Rule;
            return string.Join(",", log) + " | rescored " + rule.Rescored + " redrawn " + rule.Redrawn;
        }
        string a = RunSeed(7), b = RunSeed(7), other = RunSeed(8);
        Check(a == b, "the same seed: the same run: " + a);
        Check(a.Split('|')[0] != other.Split('|')[0], "another seed: other draws");
        Check(a.Contains("redrawn 0") && !a.Contains("rescored 0 "), "a waiting item scored again gets the same numbers (per decision): " + a.Split('|')[1]);
    }

    static string BuildError(SimulationConfig c)
    {
        try { new HeadlessModelFactory(new SimClock(1)).BuildModel(c); return null; }
        catch (Exception e) { return e.Message; }
    }

    static string RunError(SimulationConfig c)
    {
        try
        {
            var model = new HeadlessModelFactory(new SimClock(1));
            model.BuildModel(c);
            model.StartModel();
            model.Clock.AdvanceClock(100);
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    static void TestRuleErrors()
    {
        double[] speeds = { 1.0, 3, 2 };
        string missing = BuildError(RuleRouted("ShortestQueueRul", BestScoreFull.Skip, speeds, 0));
        Check(missing != null && missing.Contains("no choice rule 'ShortestQueueRul'") && missing.Contains("ShortestQueueRule") && missing.Contains("router"),
              "a missing rule: did you mean: " + missing);
        string wrong = BuildError(RuleRouted("PrioRule", BestScoreFull.Skip, speeds, 0));
        Check(wrong != null && wrong.Contains("not a routing rule") && wrong.Contains("IReleaseRule"), "the wrong interface: " + wrong);
        string none = BuildError(RuleRouted("", BestScoreFull.Skip, speeds, 0));
        Check(none != null && none.Contains("outputStrategy.rule"), "Rule without its type: " + none);
        var badParam = RuleRouted("ShortestQueueRule", BestScoreFull.Skip, speeds, 0);
        badParam.Elements.First(e => e.Id == "router").Set("rule", new List<PropertySettings> { PropertySettings.Number("FreeWeigth", 1) });
        string bp = BuildError(badParam);
        Check(bp != null && bp.Contains("no parameter 'FreeWeigth'") && bp.Contains("FreeWeight"), "an unknown parameter: " + bp);
        badParam.Elements.First(e => e.Id == "router").Set("rule", new List<PropertySettings> { PropertySettings.Number("FreeWeight", -1) });
        string range = BuildError(badParam);
        Check(range != null && range.Contains("below its minimum"), "a parameter out of range: " + range);
        string contract = BuildError(RuleRouted("StaticStateRule", BestScoreFull.Skip, speeds, 0));
        Check(contract != null && contract.Contains("breaks the rule contract") && contract.Contains("static field"), "static state breaks the contract: " + contract);
        foreach (var t in SimuLean.Rules.ChoiceRules.All.Where(t => t.Namespace == "SimuLean.UserLogic.Examples"))
            Check(SimuLean.Rules.ChoiceRules.ContractProblems(t).Count == 0, "the contract scan over every shipped rule: " + t.Name);
        Check(SimuLean.Rules.ChoiceRules.All.Count(t => t.Namespace == "SimuLean.UserLogic.Examples") == 3, "the three example rules are found");

        string thrown = RunError(RuleRouted("ThrowingRule", BestScoreFull.Skip, speeds, 0, 3));
        Check(thrown != null && thrown.StartsWith("t=3 ") && thrown.Contains("Element 'router'") && thrown.Contains("rule 'ThrowingRule'") && thrown.Contains("no stock"),
              "an exception in a rule stops the run with t=, the element, the rule, the message: " + thrown);
        string nan = RunError(RuleRouted("NaNRule", BestScoreFull.Skip, speeds, 0));
        Check(nan != null && nan.Contains("NaN") && nan.Contains("NaNRule"), "a NaN score stops the run: " + nan);
        string read = RunError(RuleRouted("BadReadRule", BestScoreFull.Skip, speeds, 0));
        Check(read != null && read.Contains("no element 'Nowhere'") && read.Contains("BadReadRule"), "a read of a missing element: " + read);
    }

    static void TestRuleViewer()
    {
        var c = RuleRouted("ShortestQueueRule", BestScoreFull.Skip, new[] { 1.0, 3, 2 }, 0);
        c.Elements.First(e => e.Id == "router").Set("rule", new List<PropertySettings> { PropertySettings.Number("FreeWeight", 0.5) });
        var report = SimuLean.Logic.ModelLogic.Of(c);
        var site = report.Find("router").Sites.FirstOrDefault(s => s.Key == "outputStrategy.rule");
        Check(site != null && site.Site == "routing" && site.Value.StartsWith("rule: ShortestQueueRule") && site.Rule == "ShortestQueueRule"
              && site.Parameters.Contains("rule.FreeWeight = 0.5") && site.Reads.Contains("out.content") && site.Writes.Count == 0,
              "the routing site: " + site?.Line);
        Check(report.Find("router").Sites.Any(s => s.Key == "outputStrategy.whenBestFull"), "whenBestFull shown with a rule");
        Check(SimuLean.Logic.ModelLogic.Undeclared(typeof(QueueSettings)).Count == 0 && SimuLean.Logic.ModelLogic.Undeclared(typeof(OutputStrategySettings)).Count == 0
              && SimuLean.Logic.ModelLogic.Undeclared(typeof(ResourceTeamConfig)).Count == 0, "the rule settings are declared decisions");
        var q = SimuLean.Logic.ModelLogic.Of(RuleQueued("EarliestDueDateRule", new double[] { 0 }, new double[] { 1 })).Find("q").Sites.FirstOrDefault(s => s.Key == "offerRule");
        Check(q != null && q.Site == "order" && q.Parameters.Contains("rule.DueLabel = due"), "the release site: " + q?.Line);
        var team = SimuLean.Logic.ModelLogic.Of(TeamModel("rule:HighestPriorityRule")).Find("ops", "team").Sites.FirstOrDefault(s => s.Key == "requestOrder");
        Check(team != null && team.Rule == "HighestPriorityRule" && team.Parameters.Contains("rule.AgeWeight = 0"), "the dispatch site: " + team?.Line);
        var back = ConfigJson.Deserialize(ConfigJson.Serialize(c)).Elements.First(e => e.Id == "router");
        Check(back.Settings.OutputStrategy.Rule == "ShortestQueueRule" && back.Settings.Rule.Count == 1 && back.Settings.Rule[0].Value == 0.5, "the rule and its parameters round-trip");
    }

    static void TestRuleScripts()
    {
        // a declarative model: the BestScore routing is one decision at the expression rung, nothing scripted
        var declarative = SimuLean.Logic.ModelLogic.Of(ScoredModel("-out.content", BestScoreFull.Skip, new[] { 1.0, 3, 2 }, 0));
        var dd = declarative.Decisions();
        Check(dd["expression"] == 1 && dd["rule"] == 0 && dd["query"] == 0 && dd["reaction"] == 0, "BestScore: one expression decision: " + string.Join(", ", dd));
        var ds = declarative.Scripts();
        Check(((System.Collections.IList)ds["rules"]).Count == 0 && (int)ds["scripted"] == 0 && (int)ds["declarative"] == dd.Values.Sum(),
              "no rule in a declarative model: " + ds["summary"]);

        // the same routing by the example rule: one decision at the rule rung, listed with its Why, reads and catalog keys
        var c = RuleRouted("ShortestQueueRule", BestScoreFull.Skip, new[] { 1.0, 3, 2 }, 0);
        var report = SimuLean.Logic.ModelLogic.Of(c);
        var scripts = report.Scripts();
        var rules = ((System.Collections.IList)scripts["rules"]).Cast<Dictionary<string, object>>().ToList();
        Check(rules.Count == 1 && (string)rules[0]["owner"] == "router" && (string)rules[0]["key"] == "outputStrategy.rule" && (string)rules[0]["site"] == "routing"
              && (string)rules[0]["rule"] == "ShortestQueueRule" && (string)rules[0]["label"] == "Shortest queue" && ((string)rules[0]["why"]).Contains("BestScore")
              && ((List<string>)rules[0]["reads"]).Contains("out.content") && ((List<string>)rules[0]["catalog"]).SequenceEqual(new[] { "router.rule.FreeWeight" }),
              "the rule's use: " + string.Join("; ", rules.SelectMany(r => r.Select(kv => kv.Key + "=" + kv.Value))));
        var d = (Dictionary<string, int>)scripts["decisions"];
        Check(d["rule"] == 1 && d["expression"] == 0 && (int)scripts["scripted"] == 1 && ((string)scripts["summary"]).Contains("1 by a choice rule"),
              "the routing counted once, at the rule rung: " + scripts["summary"]);
        Check(report.RuleSites("ShortestQueueRule").Count == 1 && report.RuleSites("SpeedRule").Count == 0, "the uses of one rule (simulean_rule_show)");
        var team = SimuLean.Logic.ModelLogic.Of(TeamModel("rule:HighestPriorityRule"));
        Check(team.Decisions()["rule"] == 1 && SimuLean.Logic.LogicReport.RuleUse(team.RuleSites()[0].Owner, team.RuleSites()[0].Site)["catalog"] is List<string> tk
              && tk.SequenceEqual(new[] { "team:ops.rule.AgeWeight" }), "a team's dispatch rule: catalog key team:ops.rule.AgeWeight");

        // validate: the example at its initial values equals its primitive; a changed or varied parameter does not
        var w = report.RuleWarnings();
        Check(w.Count == 1 && w[0].StartsWith("router: outputStrategy.rule = ShortestQueueRule") && w[0].Contains("scoreExpression = -out.content"),
              "the rule at its defaults = BestScore -out.content: " + string.Join(" | ", w));
        Check(report.RuleWarnings(k => k == "router.rule.FreeWeight").Count == 0, "no warning while a study varies its parameter");
        c.Elements.First(e => e.Id == "router").Set("rule", new List<PropertySettings> { PropertySettings.Number("FreeWeight", 0.5) });
        Check(SimuLean.Logic.ModelLogic.Of(c).RuleWarnings().Count == 0, "FreeWeight = 0.5: not the primitive, no warning");
        Check(team.RuleWarnings().Count == 1 && team.RuleWarnings()[0].Contains("|priority desc"), "HighestPriorityRule at AgeWeight = 0 = |priority desc");

        // a rule without a Why
        var noWhy = SimuLean.Logic.ModelLogic.Of(RuleRouted("SpeedRule", BestScoreFull.Skip, new[] { 1.0, 3, 2 }, 0)).RuleWarnings();
        Check(noWhy.Count == 1 && noWhy[0].Contains("'SpeedRule'") && noWhy[0].Contains("no Why"), "a rule without a Why: " + string.Join(" | ", noWhy));
        var check = new SimuLean.Optimization.Study(RuleRouted("SpeedRule", BestScoreFull.Skip, new[] { 1.0, 3, 2 }, 0)) { Threads = 1 }.Check();
        Check(check.Ok && check.Warnings.Any(x => x.Contains("'SpeedRule'") && x.Contains("no Why")), "Study.Check (simulean_validate) warns: " + string.Join(" | ", check.Warnings));
        var varied = new SimuLean.Optimization.Study(RuleRouted("ShortestQueueRule", BestScoreFull.Skip, new[] { 1.0, 3, 2 }, 0)) { Threads = 1 };
        Check(varied.Check().Warnings.Any(x => x.Contains("decides exactly as")), "Study.Check: the equivalence warning");
    }

    sealed class Fixed : SimuLean.Stochastics.Streams.IRandomEngine
    {
        private readonly SimuLean.Stochastics.Streams.IRandomEngine inner;
        public Fixed(int seed) { inner = new SimuLean.Stochastics.Streams.DefaultRandomEngine(seed); }
        public double NextDouble() => inner.NextDouble();
        public int NextInt(int a, int b) => inner.NextInt(a, b);
        public void SetSeed(int seed) => inner.SetSeed(seed);
        public double NextStandardNormal() => inner.NextStandardNormal();
    }

    static void TestFunctions()
    {
        var scope = new EntityScope();
        double N(string text) => Expression.Parse(text).Number(scope);
        Check(Math.Abs(N("ln(exp(2))") - 2) < 1e-12 && N("ln(1)") == 0 && Math.Abs(N("exp(1)") - Math.E) < 1e-12, "ln and exp");
        Check(N("clamp(5, 0, 3)") == 3 && N("clamp(-1, 0, 3)") == 0 && N("clamp(2, 0, 3)") == 2, "clamp");
        Check(Expression.Parse("ln(2) + clamp(1, 0, 2)").Number(scope) > 0 && Expression.Check("clamp(1, 3, 2)") is string cl && cl.Contains("lower bound"),
              "clamp with constant bounds in the wrong order fails the parse");

        // invalid parameters fail as the other random functions do: constant ones at the parse, computed ones at the draw
        string Parse(string t) => Expression.Check(t);
        Check(Parse("triangular(3, 1, 5)")?.Contains("min <= mode <= max") == true, "triangular: mode below min fails the parse: " + Parse("triangular(3, 1, 5)"));
        Check(Parse("lognormal(0, -1)")?.Contains("sigma must be >= 0") == true, "lognormal: a negative sigma fails the parse");
        Check(Parse("triangular(1, 2)")?.Contains("takes") == true, "triangular takes three parameters");
        var item = new Item(0); item.SetLabelValue("lo", 4);
        string Run(string t)
        {
            try { Expression.Parse(t).Number(new EntityScope { RandomStream = new Fixed(1) }.Set(item, null)); return null; }
            catch (Exception e) { return e.Message; }
        }
        Check(Run("triangular(item.lo, 1, 5)")?.Contains("min <= mode <= max") == true, "a computed invalid triangular stops the run");
        Check(Run("ln(item.lo - 4)")?.Contains("ln(0)") == true, "ln(0) is not finite: it stops the run");
        Check(Run("clamp(1, item.lo, 2)")?.Contains("lower bound") == true, "a computed clamp with lo > hi stops the run");

        // the samplers' parameters: the same mean (min + mode + max) / 3 and exp(mu + sigma² / 2)
        double Mean(string text, int n = 20000)
        {
            var ex = Expression.Parse(text);
            var s = new EntityScope { RandomStream = new Fixed(7) };
            double sum = 0;
            for (int i = 0; i < n; i++) sum += ex.Number(s);
            return sum / n;
        }
        double tri = Mean("triangular(1, 2, 6)"), logn = Mean("lognormal(0.5, 0.25)");
        Check(Math.Abs(tri - 3) < 0.05, $"triangular(1, 2, 6): mean {tri} (3, as the Triangular sampler)");
        Check(Math.Abs(logn - Math.Exp(0.5 + 0.25 * 0.25 / 2)) < 0.02, $"lognormal(0.5, 0.25): mean {logn} (exp(mu + sigma^2 / 2), as the LogNormal sampler)");
        var draw = new EntityScope { RandomStream = new Fixed(3) };
        double t1 = Expression.Parse("triangular(1, 2, 6)").Number(draw), t2 = Expression.Parse("triangular(1, 2, 6)").Number(new EntityScope { RandomStream = new Fixed(3) });
        Check(t1 == t2 && t1 >= 1 && t1 <= 6, "the draw comes from the use's stream: the same seed gives the same value");

        // in a model: a process time drawn from the station's own stream (seeded runs reproduce)
        string Times()
        {
            var c = QueuedModel(null, new double[] { 0, 0, 0 }, new double[] { 1, 1, 1 });
            c.Elements.First(e => e.Id == "m1").Set("delayStrategy", SamplerSettings.Parse("Expression~triangular(1, 2, 4) + lognormal(0, 0.1)"));
            return string.Join(",", Taken(c).Select(x => x.t.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)));
        }
        string a = Times(), b = Times();
        Check(a == b && a.Split(',').Length == 3 && a != "0,0,0", "a seeded run draws the same: " + a);
        Check(Expression.Check("triangular(1, 2, 3)", ExpressionContext.Score) != null, "random functions stay out of a score");
    }

    static void TestBestScoreChecks()
    {
        // out. only where a score is written
        string routing = Expression.Check("out.speed", ExpressionContext.Routing);
        Check(routing != null && routing.Contains("'out.' is not available"), "out. refused in a routing expression: " + routing);
        Check(Expression.Check("out.speed + item.w + element.k + content(out.name)", ExpressionContext.Score) == null, "out., item., element. and model reads in a score");
        string random = Expression.Check("out.speed + rand()", ExpressionContext.Score);
        Check(random != null && random.Contains("rand"), "no random function in a score: " + random);
        // a bare label named 'out' is still a label elsewhere
        Check(Expression.Check("out + 1", ExpressionContext.Time) == null, "a label named out is a label");

        // the build checks out.p against the model's elements
        string Build(string score)
        {
            try { new HeadlessModelFactory(new SimClock(1)).BuildModel(ScoredModel(score, BestScoreFull.Skip, new[] { 1.0, 2 }, 0)); return null; }
            catch (Exception e) { return e.Message; }
        }
        string unknown = Build("out.sped");
        Check(unknown != null && unknown.Contains("no element has a property 'sped'") && unknown.Contains("speed"), "an unknown out. property fails the build: " + unknown);
        Check(Build("out.speed + out.content") == null, "a known property and a built-in build");
        string empty = Build(" ");
        Check(empty != null && empty.Contains("scoreExpression"), "a BestScore routing without a score fails the build: " + empty);

        // the setting is a catalog parameter of the output strategy, and an unknown option fails when written
        var e = Cfg("r", "MultiServer");
        bool refused = false;
        try { e.Set("outputStrategy.whenBestFull", "Later"); } catch (Exception x) { refused = x.Message.Contains("Later"); }
        Check(refused, "an unknown whenBestFull fails when written");
        e.Set("outputStrategy.whenBestFull", "Wait");
        Check(e.Settings.OutputStrategy.WhenBestFull == BestScoreFull.Wait, "whenBestFull by name");
    }
}

// ── test rules (the examples ShortestQueueRule, EarliestDueDateRule, HighestPriorityRule are in Assets/SimuLean.UserLogic) ──

public sealed class SpeedRule : SimuLean.Rules.IRoutingRule
{
    public double Score(in SimuLean.Rules.ChoiceContext context, in SimuLean.Rules.ElementView output) => output.Get("speed");
}

public sealed class ContextProbeRule : SimuLean.Rules.IRoutingRule
{
    public readonly List<string> Seen = new List<string>();
    public double Score(in SimuLean.Rules.ChoiceContext context, in SimuLean.Rules.ElementView output)
    {
        Seen.Add($"{context.Owner}|{context.Item.Type}|{context.Candidates}|{context.Now}|{output.Name}|{context.Content("m1")}");
        return -output.Index;
    }
}

public sealed class PrioRule : SimuLean.Rules.IReleaseRule
{
    public double Score(in SimuLean.Rules.ChoiceContext context, in SimuLean.Rules.ItemView item) => item.Get("prio");
}

public sealed class LowestPriorityRule : SimuLean.Rules.IDispatchRule
{
    [SimuLean.Rules.RuleParameter] public double Shift = 0;
    public double Score(in SimuLean.Rules.ChoiceContext context, in SimuLean.Rules.RequestView request) => Shift - request.Priority;
}

public sealed class RandomRule : SimuLean.Rules.IRoutingRule
{
    private readonly Dictionary<string, double> first = new Dictionary<string, double>();
    public int Rescored, Redrawn;
    public double Score(in SimuLean.Rules.ChoiceContext context, in SimuLean.Rules.ElementView output)
    {
        double u = context.Random.NextDouble();
        string key = context.Item.Id + "/" + output.Index;
        if (first.TryGetValue(key, out double was)) { Rescored++; if (was != u) Redrawn++; }
        else first[key] = u;
        return u;
    }
}

public sealed class StaticStateRule : SimuLean.Rules.IRoutingRule
{
    public static int Calls;
    public double Score(in SimuLean.Rules.ChoiceContext context, in SimuLean.Rules.ElementView output) => Calls++;
}

public sealed class ThrowingRule : SimuLean.Rules.IRoutingRule
{
    public double Score(in SimuLean.Rules.ChoiceContext context, in SimuLean.Rules.ElementView output)
        => context.Now >= 3 ? throw new InvalidOperationException("no stock") : 0;
}

public sealed class NaNRule : SimuLean.Rules.IRoutingRule
{
    public double Score(in SimuLean.Rules.ChoiceContext context, in SimuLean.Rules.ElementView output) => double.NaN;
}

public sealed class BadReadRule : SimuLean.Rules.IRoutingRule
{
    public double Score(in SimuLean.Rules.ChoiceContext context, in SimuLean.Rules.ElementView output) => context.Content("Nowhere");
}
