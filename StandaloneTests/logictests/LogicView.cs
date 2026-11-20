// The logic viewer's plain view: every decision setting shown and read only when its owning strategy / mode uses
// it (LogicWhen), defaults detected when an extractor writes them explicitly (LogicDefaultValue), one sentence per decision
// with its links, and the default view of the ChoiceLogicDemo-like model without the noise lines.
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean.Logic;
using SimuLean.Serialization;
using SimuLean.Data;

static partial class Program
{
    // an element whose routing and admission have every field filled as the Unity extractor writes them (the stale fields of
    // the other modes included), then the mode under test
    static ElementConfig Filled(string id, string type)
    {
        var e = Cfg(id, type);
        e.Set("outputStrategy.parameterKey", "routing_mode");
        e.Set("outputStrategy.routingExpression", "item.k");
        e.Set("outputStrategy.weights", new[] { 1.0, 2.0 });
        e.Set("outputStrategy.scoreExpression", "-out.content");
        e.Set("outputStrategy.whenBestFull", "Wait");
        e.Set("outputStrategy.rule", "ShortestQueueRule");
        e.Set("inputStrategy.singleLabelName", "type");
        e.Set("inputStrategy.singleLabelValue", "A");
        e.Set("inputStrategy.multiLabelPairs", new List<LabelValueSettings> { new LabelValueSettings { Label = "type", Value = "B" } });
        e.Set("inputStrategy.condition", "item.ok == 1");
        return e;
    }

    static List<string> KeysOf(SimulationConfig c, string owner, string prefix)
        => ModelLogic.Of(c).Find(owner).Sites.Where(s => s.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(s => s.Key.Substring(prefix.Length)).ToList();

    static SimulationConfig Around(ElementConfig e)
    {
        var c = new SimulationConfig();
        c.Elements.Add(e);
        c.Elements.Add(Cfg("a", "Sink"));
        c.Elements.Add(Cfg("b", "Sink"));
        c.Connections.Add(new ConnectionConfig(e.Id, "a"));
        c.Connections.Add(new ConnectionConfig(e.Id, "b"));
        return c;
    }

    static void TestApplicability()
    {
        // routing: each strategy shows (and reads) only its own fields
        var routing = new Dictionary<string, string[]>
        {
            ["FirstAvailable"] = new[] { "strategyType" }, ["ShortestQueue"] = new[] { "strategyType" }, ["RoundRobin"] = new[] { "strategyType" },
            ["MostAvailableCapacity"] = new[] { "strategyType" }, ["PriorityBased"] = new[] { "strategyType" },
            ["Parameterized"] = new[] { "strategyType", "parameterKey" }, ["Expression"] = new[] { "strategyType", "routingExpression" },
            ["Probability"] = new[] { "strategyType", "weights" }, ["BestScore"] = new[] { "strategyType", "scoreExpression", "whenBestFull" },
            ["Rule"] = new[] { "strategyType", "whenBestFull", "rule" },
        };
        foreach (var kv in routing)
        {
            var e = Filled("x", "Queue");
            e.Set("outputStrategy.strategyType", kv.Key);
            var c = Around(e);
            var keys = KeysOf(c, "x", "outputStrategy.");
            Check(keys.SequenceEqual(kv.Value), $"routing {kv.Key}: {string.Join(", ", keys)}");
            var reads = ModelLogic.Of(c).Find("x").Decisions.Where(d => d.Site == "routing").SelectMany(d => d.Reads).ToList();
            Check(kv.Key == "Parameterized" ? reads.SequenceEqual(new[] { "item.routing_mode" }) : !reads.Contains("item.routing_mode"),
                  $"routing {kv.Key}: reads {string.Join(", ", reads)}");
        }
        // admission
        var admission = new Dictionary<string, string[]>
        {
            ["Default"] = new string[0], ["SingleLabel"] = new[] { "strategyType", "singleLabelName", "singleLabelValue" },
            ["MultiLabel"] = new[] { "strategyType", "multiLabelPairs" }, ["Condition"] = new[] { "strategyType", "condition" },
        };
        foreach (var kv in admission)
        {
            var e = Filled("x", "Queue");
            e.Set("inputStrategy.strategyType", kv.Key);
            var c = Around(e);
            var keys = KeysOf(c, "x", "inputStrategy.");
            Check(keys.SequenceEqual(kv.Value), $"admission {kv.Key}: {string.Join(", ", keys)}");
            var reads = ModelLogic.Of(c).Find("x").Decisions.Where(d => d.Site == "admission").SelectMany(d => d.Reads).ToList();
            Check(kv.Key == "Condition" ? reads.SequenceEqual(new[] { "item.ok" }) : reads.Count == 0, $"admission {kv.Key}: reads {string.Join(", ", reads)}");
        }
        // a Queue's wait: the action and the target only with a max wait; the target only when the action leaves by it
        foreach (var (max, action, expected) in new[]
        {
            ("0", "GiveUp", new string[0]), ("", "Reroute", new string[0]), ("5", "KeepWaiting", new[] { "maxWait" }),
            ("5", "GiveUp", new[] { "maxWait", "onTimeout", "timeoutTarget" }), ("item.patience", "Reroute", new[] { "maxWait", "onTimeout", "timeoutTarget" }),
        })
        {
            var q = Cfg("x", "Queue");
            q.Set("maxWait", max);
            q.Set("onTimeout", action);
            q.Set("timeoutTarget", "a");
            var keys = ModelLogic.Of(Around(q)).Find("x").Sites.Where(s => s.Site == "wait").Select(s => s.Key).ToList();
            Check(keys.SequenceEqual(expected), $"queue wait {max}/{action}: {string.Join(", ", keys)}");
        }
        // a station's resource wait and release point
        var m = Station("x", 1);
        m.Set("processBy.team", "T");
        m.Set("processBy.maxWait", "0");
        m.Set("processBy.onTimeout", "Reroute");
        m.Set("processBy.timeoutTarget", "a");
        m.Set("processBy.release", "EndOfProcess");
        m.Set("processBy.releaseAt", "b");
        var mk = KeysOf(Around(m), "x", "processBy.");
        Check(mk.SequenceEqual(new[] { "team" }), "processBy without a max wait, released at the end: " + string.Join(", ", mk));
        m.Set("processBy.maxWait", "10");
        m.Set("processBy.release", "AtElement");
        mk = KeysOf(Around(m), "x", "processBy.");
        Check(mk.SequenceEqual(new[] { "team", "release", "releaseAt", "maxWait", "onTimeout", "timeoutTarget" }), "with them: " + string.Join(", ", mk));
        // a Queue's group limit and offer scan
        var g = Cfg("x", "Queue");
        g.Set("groupLimit", 3);
        g.Set("offerScan", 4);
        Check(!ModelLogic.Of(Around(g)).Find("x").Sites.Any(s => s.Key == "groupLimit" || s.Key == "offerScan"), "group limit / offer scan without their mode");
        g.Set("groupBySetup", true);
        g.Set("offerOrder", "AnyAccepted");
        var gk = ModelLogic.Of(Around(g)).Find("x").Sites.Select(s => s.Key).ToList();
        Check(gk.Contains("groupLimit") && gk.Contains("offerScan"), "with their mode: " + string.Join(", ", gk));
        // a Combiner's component admission
        var cb = Cfg("x", "Combiner");
        cb.Set("singleLabelName", "kind");
        cb.Set("singleLabelValue", "2");
        Check(!ModelLogic.Of(Around(cb)).Find("x").Sites.Any(s => s.Key.StartsWith("singleLabel", StringComparison.Ordinal)), "combiner: labels without SingleLabel");
        cb.Set("pullModeType", "SingleLabel");
        Check(ModelLogic.Of(Around(cb)).Find("x").Decisions.Any(d => d.Sentence == "Admit components with item.kind = 2"), "combiner SingleLabel: " +
              string.Join(" | ", ModelLogic.Of(Around(cb)).Find("x").Decisions.Select(d => d.Sentence)));
        // a milk run: the dynamic settings only on dynamic tours, the fixed ones only on fixed tours
        var mr = Cfg("x", "MilkRun");
        mr.Set("policy", "FillThenDeliver");
        mr.Set("fillTimeout", 5.0);
        mr.Set("pattern", new List<string> { "A" });
        var fixedKeys = KeysOf(new SimulationConfig { Elements = { mr } }, "x", "");
        Check(fixedKeys.Contains("pattern") && !fixedKeys.Contains("policy") && !fixedKeys.Contains("fillTimeout"), "fixed tours: " + string.Join(", ", fixedKeys));
        mr.Set("tourMode", "Dynamic");
        var dynamicKeys = KeysOf(new SimulationConfig { Elements = { mr } }, "x", "");
        Check(!dynamicKeys.Contains("pattern") && dynamicKeys.Contains("policy") && dynamicKeys.Contains("fillTimeout"), "dynamic tours: " + string.Join(", ", dynamicKeys));

        // every LogicWhen names settings of its own type
        foreach (var t in ModelLogic.SettingsTypes())
            foreach (var f in t.GetFields())
            {
                var a = (SettingAttribute)Attribute.GetCustomAttribute(f, typeof(SettingAttribute));
                if (a?.LogicWhen == null) continue;
                string error = null;
                try { ModelLogic.Applies(a.LogicWhen, t, null); } catch (FormatException e) { error = e.Message; }
                Check(error == null, $"{t.Name}.{f.Name}: {error}");
            }
    }

    static void TestDefaults()
    {
        // what the Unity extractor writes for a plain element: every value, at its default
        var q = Cfg("x", "Queue");
        q.Set("outputStrategy.strategyType", "FirstAvailable");
        q.Set("outputStrategy.parameterKey", "routing_mode");
        q.Set("inputStrategy.strategyType", "Default");
        q.Set("inputStrategy.singleLabelName", "");
        q.Set("offerOrder", "FIFO");
        q.Set("offerScan", 0);
        q.Set("maxWait", "0");
        q.Set("onTimeout", "KeepWaiting");
        var o = ModelLogic.Of(Around(q)).Find("x");
        Check(o.Sites.All(s => s.IsDefault), "every site a default: " + string.Join(" | ", o.Sites.Select(s => s.Line)));
        Check(o.Sites.Select(s => s.Key).SequenceEqual(new[] { "outputStrategy.strategyType", "offerOrder" }),
              "only the defaults declared to be shown: " + string.Join(", ", o.Sites.Select(s => s.Key)));
        Check(!o.HasOwnLogic && o.Decisions.All(d => d.IsDefault), "no decision of its own");
        // a station: priority 1, the resource wait 0 / KeepWaiting
        var m = Station("x", 1);
        m.Set("processBy.team", "T");
        m.Set("processBy.priority", "1");
        m.Set("processBy.maxWait", "0");
        m.Set("processBy.onTimeout", "KeepWaiting");
        m.Set("processBy.acquire", "AllAtOnce");
        m.Set("processBy.release", "EndOfProcess");
        var keys = ModelLogic.Of(Around(m)).Find("x").Sites.Where(s => s.Site != "routing").Select(s => s.Key).ToList();
        Check(keys.SequenceEqual(new[] { "processBy.team" }), "a station's defaults: " + string.Join(", ", keys));
        // a storage system's location policy FirstAvailable
        var st = Cfg("x", "StorageSystem");
        st.Set("locationPolicy", "FirstAvailable");
        Check(!ModelLogic.Of(new SimulationConfig { Elements = { st } }).Find("x").Sites.Any(s => s.Key == "locationPolicy"), "location policy FirstAvailable");
        // a value that is not the default stays
        q.Set("maxWait", "0.5");
        Check(ModelLogic.Of(Around(q)).Find("x").Sites.Any(s => s.Key == "maxWait" && !s.IsDefault), "a max wait of 0.5 is a decision");
        // a requirement of another team (the lift's driver) asks with its use's priority; no requirement priority
        var prim = SimuLean.Testing.ReferenceModels.ResourcePrimitives();
        prim.Elements.Find(x => x.Id == "lift").Set("processBy.priority", "2");
        var lift = ModelLogic.Of(prim).Find("lift");
        Check(lift.Sites.Any(s => s.Key == "processBy.priority" && s.Value == "2") && !lift.Sites.Any(s => s.Key.Contains("requirements[") && s.Key.EndsWith(".priority")),
              "the lift's two-team request has one priority, 2: " + string.Join(" | ", lift.Sites.Select(s => s.Line)));
    }

    // the ChoiceLogicDemo example scene's model, as the extractor writes it
    static SimulationConfig DemoModel(int arrivals = 40)
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 7, MaxSimulationTime = 3600 };
        ElementConfig Plain(string id, string type)
        {
            var e = new ElementConfig(id, type, id);
            if (type != "Sink")
            {
                e.Set("outputStrategy.strategyType", "FirstAvailable");
                e.Set("outputStrategy.parameterKey", "routing_mode");
                e.Set("outputStrategy.routingExpression", "");
            }
            if (type != "Source")
            {
                e.Set("inputStrategy.strategyType", "Default");
                e.Set("inputStrategy.singleLabelName", "");
                e.Set("inputStrategy.condition", "");
                e.Set("inputStrategy.singleLabelValue", "");
            }
            if (type == "Queue") { e.Set("offerOrder", "FIFO"); e.Set("offerScan", 0); e.Set("maxWait", "0"); e.Set("onTimeout", "KeepWaiting"); }
            if (type == "MultiServer") e.Set("delayStrategy", SamplerSettings.Constant(60));
            return e;
        }
        var src = Plain("Orders", "Source");
        src.Set("mode", "Schedule");
        src.Set("arrivalsTable", new Dictionary<string, List<string>>
        {
            ["Time"] = Enumerable.Range(0, arrivals).Select(i => (i * 20).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList(),
            ["Name"] = Enumerable.Range(0, arrivals).Select(_ => "P").ToList(),
            ["Q"] = Enumerable.Range(0, arrivals).Select(_ => "1").ToList(),
        });
        src.Set("createLabels", new List<string> { "family = pick(4, 1, 4, 2, 2, 3)", "prio = pick(5, 1, 3, 2, 2, 3)" });
        c.Elements.Add(src);
        var router = Plain("Router", "Queue");
        router.Set("outputStrategy.strategyType", "BestScore");
        router.Set("outputStrategy.scoreExpression", "(out.family == item.family ? 10 : 0) - out.content");
        router.Set("outputStrategy.whenBestFull", "Skip");
        c.Elements.Add(router);
        int k = 1;
        foreach (string cell in new[] { "Cell A", "Cell B", "Flex Cell" })
        {
            var e = Plain(cell, "MultiServer");
            var props = new List<PropertySettings> { PropertySettings.Number("family", k++) };
            if (cell == "Flex Cell")
            {
                props.Add(PropertySettings.Number("open", 0));
                e.Set("inputStrategy.strategyType", "Condition");
                e.Set("inputStrategy.condition", "element.open == 1");
            }
            e.Set(PropertyRecords.Key, props);
            c.Elements.Add(e);
        }
        var pack = Plain("Pack Queue", "Queue");
        pack.Set("outputStrategy.strategyType", "Rule");
        pack.Set("outputStrategy.rule", "ShortestQueueRule");
        pack.Set("outputStrategy.whenBestFull", "Skip");
        pack.Set("rule", new List<PropertySettings> { PropertySettings.Number("FreeWeight", 0.5) });
        pack.Set("offerSort", "prio desc, age");
        c.Elements.Add(pack);
        c.Elements.Add(Plain("Packer 1", "MultiServer"));
        c.Elements.Add(Plain("Packer 2", "MultiServer"));
        c.Elements.Add(Plain("Shipped", "Sink"));
        c.Connections.Add(new ConnectionConfig("Orders", "Router"));
        foreach (string cell in new[] { "Cell A", "Cell B", "Flex Cell" })
        {
            c.Connections.Add(new ConnectionConfig("Router", cell));
            c.Connections.Add(new ConnectionConfig(cell, "Pack Queue"));
        }
        c.Connections.Add(new ConnectionConfig("Pack Queue", "Packer 1"));
        c.Connections.Add(new ConnectionConfig("Pack Queue", "Packer 2"));
        c.Connections.Add(new ConnectionConfig("Packer 1", "Shipped"));
        c.Connections.Add(new ConnectionConfig("Packer 2", "Shipped"));
        var shift = new ReactionConfig("Flex Cell Shift");
        shift.Set("signal", "Every");
        shift.Set("period", 600.0);
        shift.AddAction("SetElementProperty", target: "Flex Cell", name: "open", value: "1 - get('Flex Cell', 'open')");
        c.Reactions.Add(shift);
        var look = new ReactionConfig("Colour By Family", "Orders");
        look.Set("signal", "ItemExited");
        look.AddAction("SetItemLook", color: "#2F6FD6", condition: "item.family == 1");
        c.Reactions.Add(look);
        return c;
    }

    static void TestSentences()
    {
        var report = ModelLogic.Of(DemoModel());
        string Say(string owner, string site) => report.Find(owner).Decisions.FirstOrDefault(d => d.Site == site)?.Sentence;
        Check(Say("Router", "routing") == "Route best score: (out.family == item.family ? 10 : 0) - out.content · if best is full: skip",
              "the router: " + Say("Router", "routing"));
        Check(Say("Flex Cell", "admission") == "Admit when element.open == 1 · woken by Flex Cell Shift", "the flex cell: " + Say("Flex Cell", "admission"));
        Check(Say("Pack Queue", "order") == "Release order prio desc, age", "the pack queue's order: " + Say("Pack Queue", "order"));
        var rule = report.Find("Pack Queue").Decisions.First(d => d.Site == "routing");
        Check(rule.Verb == "Rule" && rule.Sentence == "Rule ShortestQueueRule (FreeWeight = 0.5) picks the next element · if best is full: skip" && rule.Rule == "ShortestQueueRule",
              "the pack queue's rule: " + rule.Sentence);
        Check(Say("Flex Cell Shift", "reaction") == "When every 600: set Flex Cell.open = 1 - get('Flex Cell', 'open')", "the shift: " + Say("Flex Cell Shift", "reaction"));
        Check(Say("Orders", "labels") == "Labels on creation: family = pick(4, 1, 4, 2, 2, 3), prio = pick(5, 1, 3, 2, 2, 3)", "the labels: " + Say("Orders", "labels"));
        // links: what a decision names, the reactions that wake it
        Check(report.Find("Flex Cell").Decisions.First(d => d.Site == "admission").Links.SequenceEqual(new[] { "Flex Cell Shift" }), "the wake-up is a link");
        Check(report.Find("Flex Cell Shift").Decisions[0].Links.Contains("Flex Cell"), "the reaction links the element it writes");
        // the default view: no parameterKey anywhere, the plain elements have no decision of their own
        Check(!report.Owners.SelectMany(o => o.Sites).Any(s => s.Key.EndsWith("parameterKey", StringComparison.Ordinal) || s.Key == "maxWait"
                                                              || s.Key == "onTimeout" || s.Key == "inputStrategy.strategyType" && s.Value == "Default"),
              "no stale or default-valued setting listed");
        var own = report.OfKind("element").Where(o => o.HasOwnLogic).Select(o => o.Name).ToList();
        Check(own.SequenceEqual(new[] { "Orders", "Router", "Flex Cell", "Pack Queue" }), "the elements with logic of their own: " + string.Join(", ", own));
        var cellA = report.Find("Cell A");
        Check(cellA.Decisions.Count == 0, "Cell A: nothing (one output, default admission): " + string.Join(" | ", cellA.Decisions.Select(d => d.Sentence)));
        // the data: one sentence per decision, the raw settings beside it; a default decision is one short line
        var data = (List<object>)report.ToData()["elements"];
        var router = (Dictionary<string, object>)data.First(x => (string)((Dictionary<string, object>)x)["name"] == "Router");
        var decisions = ((System.Collections.IEnumerable)router["decisions"]).Cast<Dictionary<string, object>>().ToList();
        Check(decisions.Any(d => (string)d["say"] == Say("Router", "routing") && ((List<string>)d["settings"]).Contains("outputStrategy.scoreExpression = (out.family == item.family ? 10 : 0) - out.content")),
              "the router's routing as data");
        Check(decisions.Any(d => d.ContainsKey("default") && d.Count == 3 && (string)d["say"] == "Release order first in, first out"), "a default decision is short");
    }
}

static partial class Program
{
    // the size of the items' 3D model (width, height, length labels) filled the labels a decision shows
    static void TestSizeLabelsApart()
    {
        var c = DemoModel(120);
        foreach (var e in c.Elements.Where(e => e.Type == "Source"))
            SimuLean.Serialization.ReplenishmentBuilders.EncodeModelSize(e, "", new[] { 0.6, 0.4, 0.8 });
        var clock = new SimuLean.SimClock(c.Seed);
        var f = new SimuLean.Headless.HeadlessModelFactory(clock);
        f.BuildModel(c);
        f.StartModel();
        clock.AdvanceClock(3600);
        var routing = ModelLogic.Live(ModelLogic.Of(c), f).Find("Router").Decisions.First(d => d.Site == "routing");
        var lastItem = (Dictionary<string, object>)routing.Live["lastItem"];
        var size = lastItem.TryGetValue("size", out var s) ? (Dictionary<string, object>)s : null;
        Check(size != null && size.ContainsKey("width") && size.ContainsKey("height") && size.ContainsKey("length"), "the size apart in the data: " + string.Join(", ", lastItem.Keys));
        Check(!routing.LiveText.Contains("width=") && !routing.LiveText.Contains("height=") && routing.LiveText.Contains("family="),
              "the line shows the model's labels, not the size: " + routing.LiveText);
        Check(!((Dictionary<string, object>)lastItem["labels"]).ContainsKey("width"), "not among the labels");
    }

    static (SimuLean.Headless.HeadlessModelFactory model, SimulationConfig config) RunDemo(double until, double step = 0, Action<SimuLean.Headless.HeadlessModelFactory> read = null)
    {
        var c = DemoModel(120);
        var clock = new SimuLean.SimClock(c.Seed);
        var f = new SimuLean.Headless.HeadlessModelFactory(clock);
        f.BuildModel(c);
        f.StartModel();
        if (step <= 0) clock.AdvanceClock(until);
        else
            for (double t = step; t <= until + 1e-9; t += step)
            {
                clock.AdvanceClock(Math.Min(t, until));
                read?.Invoke(f);
            }
        return (f, c);
    }

    static void TestCounters()
    {
        var (model, config) = RunDemo(3600);
        var report = ModelLogic.Live(ModelLogic.Of(config), model);
        Decision D(string owner, string site) => report.Find(owner).Decisions.First(d => d.Site == site);
        var routing = D("Router", "routing");
        var routed = (Dictionary<string, object>)routing.Live["routed"];
        long total = routed.Values.Sum(v => (long)v);
        Check(routed.Keys.SequenceEqual(new[] { "Cell A", "Cell B", "Flex Cell" }) && total > 0 && routed.Values.All(v => (long)v > 0),
              "the router's items per output: " + routing.LiveText);
        Check(total == model.Find("Router").GetStatsOutputCount(), $"as many as left the router ({total})");
        Check(routing.LiveText.Contains("last → ") && routing.Live.ContainsKey("lastScores"), "the last choice with its scores: " + routing.LiveText);
        // the outputs the last scored choice passed over (full / refusing) are named, aligned with the scores
        var skipped = (List<string>)routing.Live["lastSkipped"];
        var lastScores = (List<object>)routing.Live["lastScores"];
        Check(skipped.Count == lastScores.Count(x => x == null) && (skipped.Count == 0 || routing.LiveText.Contains("skipped " + skipped[0])),
              $"the skipped outputs of the last choice ({string.Join(", ", skipped)}): " + routing.LiveText);
        // the last choice names its item and the item's labels (why it went there)
        var lastItem = (Dictionary<string, object>)routing.Live["lastItem"];
        Check(System.Text.RegularExpressions.Regex.IsMatch(routing.LiveText, @"last → [^·]+ for item \d+[^·]* \{[^}]*family=\d") && lastItem.ContainsKey("id")
              && ((Dictionary<string, object>)lastItem["labels"]).ContainsKey("family"), "the last choice's item and labels: " + routing.LiveText);
        var flex = D("Flex Cell", "admission");
        long entered = (long)flex.Live["entered"], checks = (long)flex.Live["checks"], refused = (long)flex.Live["refused"];
        Check(entered > 0 && refused > 0 && (long)flex.Live["wakeUps"] > 0 && !flex.Live.ContainsKey("admitted"), "the flex cell's admission: " + flex.LiveText);
        // entries are items (one each, the element's input), checks are evaluations (several per item)
        Check(entered == (long)model.Find("Flex Cell").GetStatsInputCount(), $"entered = the items that entered ({entered}): " + flex.LiveText);
        Check(checks > entered && checks >= refused + entered && flex.LiveText.Contains($"entered {entered} · refused {refused} of {checks} checks"),
              "checks counted apart from the entries: " + flex.LiveText);
        var release = D("Pack Queue", "order");
        Check((long)release.Live["released"] > 0 && release.Live.ContainsKey("lastReleased") && release.Live.ContainsKey("lastReleasedItem"),
              "the pack queue's releases: " + release.LiveText);
        var rule = D("Pack Queue", "routing");
        Check(rule.Live.ContainsKey("ruleCalls") && (long)rule.Live["ruleCalls"] > 0 && (long)rule.Live["ruleErrors"] == 0 && rule.Live.ContainsKey("lastScore")
              && rule.LiveText.Contains("last score ") && rule.Live.ContainsKey("lastScoreItem"),
              "the rule's calls, errors and last score with its item: " + rule.LiveText);
        var shift = D("Flex Cell Shift", "reaction");
        Check((long)shift.Live["fired"] == 6 && !shift.Live.ContainsKey("notMet"), "the shift fired every 600 to 3600: " + shift.LiveText);
        // in the data
        var json = MiniJson.Serialize(report.ToData("Router"));
        Check(json.Contains("\"live\":") && json.Contains("\"routed\":"), "the counters in the data: " + json);
        // in the marks of a diagnosis
        var diagnosis = new Dictionary<string, object> { ["elements"] = new List<object> { new Dictionary<string, object> { ["name"] = "Router", ["items"] = 1 } } };
        ModelLogic.Mark(diagnosis, config, model);
        var marks = (List<string>)((Dictionary<string, object>)((List<object>)diagnosis["elements"])[0])["decisions"];
        Check(marks.Any(m => m.StartsWith("Route best score") && m.Contains("[routed Cell A ")), "the marks carry the counters: " + string.Join(" | ", marks));
        // a model never run: zero counts, nothing fails
        var fresh = new SimuLean.Headless.HeadlessModelFactory(new SimuLean.SimClock(1));
        fresh.BuildModel(DemoModel());
        var idle = ModelLogic.Live(ModelLogic.Of(DemoModel()), fresh);
        Check(idle.Find("Router").Decisions.First(d => d.Site == "routing").LiveText == "routed 0", "before the run: " + idle.Find("Router").Decisions[0].LiveText);
    }

    static void TestCountersChangeNothing()
    {
        string Results(SimuLean.Headless.HeadlessModelFactory f) => SimuLean.Results.RunResults.Capture(f).ToLongTable().ToCsv();
        var plain = RunDemo(3600);
        int reads = 0;
        var read = RunDemo(3600, 100, f =>
        {
            var r = ModelLogic.Live(ModelLogic.Of(f.Config), f);
            MiniJson.Serialize(r.ToData());
            r.Marks("Router");
            reads++;
        });
        Check(reads == 36, "read 36 times during the run");
        Check(Results(plain.model) == Results(read.model), "the results are identical with and without the counters read");
        Check(ConfigJson.Serialize(plain.config) == ConfigJson.Serialize(read.config), "the model unchanged");
    }
}
