// Standalone tests of the logic viewer: the report
// of a small model with each kind of decision site (routing, admission, offer order, a team's named order and member choice,
// a list's order and query, reactions with what they write, a view-only look), the admission wake-up map, the notes, the
// diagnosis marks; every reference model reported without being changed; every decision-like setting declared.
// Run: dotnet run  (from StandaloneTests/logictests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Collections;
using SimuLean.Logic;
using SimuLean.Optimization;
using SimuLean.Data;
using SimuLean.Serialization;
using SimuLean.Testing;

static partial class Program
{
    static int passed, failed;

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    static int Main(string[] args)
    {
        // -- --show [model]: prints the report of a reference model (or of this suite's model) as lines
        if (args.Length > 0 && args[0] == "--show")
        {
            var models = args.Length > 1 && args[1] == "*" ? ReferenceModels.All().Select(kv => kv.Value).ToList()
                       : new List<SimulationConfig> { args.Length > 1 ? ReferenceModels.All().First(kv => kv.Key == args[1]).Value : LogicModel() };
            foreach (var model in models)
            foreach (var o in ModelLogic.Of(model).Owners)
            {
                Console.WriteLine($"{o.Kind} {o.Name} ({o.Type})" + (o.Next.Count > 0 ? " -> " + string.Join(", ", o.Next) : ""));
                foreach (var d in o.Decisions)
                    Console.WriteLine($"  [{d.Site}] {d.Sentence}" + (d.IsDefault ? " (default)" : "") + (d.Links.Count > 0 ? " -> links " + string.Join(", ", d.Links) : ""));
                if (args.Length > 2) foreach (var s in o.Sites) Console.WriteLine("    " + s.Line);
                foreach (var w in o.WokenBy) Console.WriteLine("  woken by " + w);
                foreach (var n in o.Notes) Console.WriteLine("  note: " + n);
            }
            return 0;
        }
        Run("every setting that looks like a decision declares its site (or None)", TestCoverage);
        Run("the report of a model with each kind of decision site", TestSites);
        Run("reactions: signal, condition, actions with what they write; a look is view only", TestReactions);
        Run("the admission wake-up map and the notes of a condition nothing wakes", TestWakeUps);
        Run("as data: compact, one owner, JSON", TestData);
        Run("diagnosis marks: the decisions where items wait", TestMarks);
        Run("every reference model reported, unchanged", TestReferenceModels);
        Run("applicability: each strategy or mode shows and reads only the settings it uses", TestApplicability);
        Run("defaults: values written at their default are defaults (an extractor writes every value)", TestDefaults);
        Run("sentences: one plain-language line per decision, links, the default view", TestSentences);
        Run("run-time counters: routing per output and last choice, admission, wake-ups, releases, rules, reactions", TestCounters);
        Run("run-time counters never change results: a run read every 100 time units is the run not read", TestCountersChangeNothing);
        Run("run-time counters: the item's size labels are not among a decision's labels, the data gives them apart", TestSizeLabelsApart);
        Run("setups and preemption in words: a station's setup rule; which uses can take a preemptable use's members", TestSetupAndPreemptors);
        Run("a team's uses: requirement teams, setup crew, repair crew", TestTeamUses);
        Run("a choice text (routing, admission, timeout action) means in the logic what the builder builds from it", TestChoicesReadAsBuilt);
        Run("every downtime is listed with its targets, one without a decision too", TestDowntimesListed);
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

    static ElementConfig Station(string id, double time)
    {
        var e = Cfg(id, "MultiServer");
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

    // src -> router (best score, wait; offer sort) -> m1 (team Ops) | m2 (admission on a property) | gate (admission on content)
    //   -> sink; a list 'orders' pulled by the sink; a reaction on m1 writing m2's property, a look and a retry
    static SimulationConfig LogicModel()
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
        var src = Arrivals("src", 0, 1, 2);
        src.Set("createLabels", new List<string> { "prio = 2", "family = 'A'" });
        c.Elements.Add(src);
        var router = Cfg("router", "Queue");
        router.Set("outputStrategy.strategyType", "BestScore");
        router.Set("outputStrategy.scoreExpression", "-out.content + get('m2', 'open')");
        router.Set("outputStrategy.whenBestFull", "Wait");
        router.Set("offerSort", "prio desc, age");
        c.Elements.Add(router);
        var m1 = Station("m1", 5);
        m1.Set("processBy.team", "Ops");
        m1.Set("processBy.priority", "item.prio");
        c.Elements.Add(m1);
        var m2 = Station("m2", 5);
        m2.Set(PropertyRecords.Key, new List<PropertySettings> { PropertySettings.Number("open", 0) });
        m2.Set("inputStrategy.strategyType", "Condition");
        m2.Set("inputStrategy.condition", "element.open == 1 && item.family == 'A'");
        c.Elements.Add(m2);
        var gate = Cfg("gate", "Queue");
        gate.Set("inputStrategy.strategyType", "Condition");
        gate.Set("inputStrategy.condition", "content('m1') < 1");
        c.Elements.Add(gate);
        var sink = Cfg("sink", "Sink");
        c.Elements.Add(sink);
        c.Connections.Add(new ConnectionConfig("src", "router"));
        foreach (var to in new[] { "m1", "m2", "gate" }) c.Connections.Add(new ConnectionConfig("router", to));
        foreach (var from in new[] { "m1", "m2", "gate" }) c.Connections.Add(new ConnectionConfig(from, "sink"));
        c.Lists.Add(new ListConfig
        {
            Id = "orders", Name = "orders", DefaultOrderBy = "prio", DefaultDescending = true,
            Fields = { new ListFieldSpec("urgent", ListFieldKind.Expression, "prio > 1") },
        });
        c.Connections.Add(new ConnectionConfig("orders", "sink", "PullFromList") { Query = "family == 'A'|urgent desc" });
        c.ResourceTeams.Add(new ResourceTeamConfig
        {
            Id = "Ops", Name = "Ops", Members = { new ResourceMemberConfig { Name = "op1" } },
            NamedOrders = new List<NamedOrderSettings> { NamedOrderSettings.Of("urgentFirst", "|priority desc") }, RequestOrder = "urgentFirst",
        });
        var r = new ReactionConfig("Open", "m1");
        r.Set("signal", "ItemEntered");
        r.AddAction("SetElementProperty", target: "m2", name: "open", value: 1);
        r.AddAction("SetItemLabel", name: "seen", value: "now()");
        r.AddAction("SetItemLook", color: "red", scale: 1.5);
        r.AddAction("Retry", target: "gate", condition: "content('gate') > 0");
        c.Reactions.Add(r);
        return c;
    }

    static DecisionSite Site(LogicOwner o, string key) => o?.Sites.FirstOrDefault(s => s.Key == key);

    static void TestCoverage()
    {
        var types = ModelLogic.SettingsTypes();
        Check(types.Count > 40 && types.Contains(typeof(OutputStrategySettings)) && types.Contains(typeof(ReactionActionSettings))
              && types.Contains(typeof(RepairCrewSettings)), $"the settings types reached: {types.Count}");
        var undeclared = types.SelectMany(ModelLogic.Undeclared).ToList();
        Check(undeclared.Count == 0, "settings that look like decisions and declare no site: " + string.Join(", ", undeclared));
    }

    static void TestSites()
    {
        var report = ModelLogic.Of(LogicModel());
        var router = report.Find("router", "element");
        Check(router != null && router.Next.SequenceEqual(new[] { "m1", "m2", "gate" }), "the router's next elements in order");
        var type = Site(router, "outputStrategy.strategyType");
        Check(type != null && type.Site == "routing" && type.Value == "BestScore" && !type.IsDefault, "routing: BestScore");
        var score = Site(router, "outputStrategy.scoreExpression");
        Check(score != null && score.Context == "score" && score.Reads.SequenceEqual(new[] { "out.content", "get('m2', 'open')" }),
              "the score's context and reads: " + score?.Line);
        Check(Site(router, "outputStrategy.whenBestFull")?.Value == "Wait", "whenBestFull shown with BestScore");
        var sort = Site(router, "offerSort");
        Check(sort != null && sort.Site == "order" && sort.Context == "offerSort" && sort.Reads.SequenceEqual(new[] { "item.prio", "item.age" }),
              "the offer sort's keys and reads: " + sort?.Line);
        var fifo = Site(router, "offerOrder");
        Check(fifo != null && fifo.IsDefault && fifo.Value == "FIFO", "the default offer order is shown as such");

        // a single output: the default routing is no decision; the source's labels are what the others read
        var src = report.Find("src", "element");
        Check(Site(src, "outputStrategy.strategyType") == null, "one output: no routing site");
        var labels = Site(src, "createLabels");
        Check(labels != null && labels.Site == "labels" && labels.Writes.SequenceEqual(new[] { "item.prio", "item.family" }), "labels written: " + labels?.Line);
        // a two-output element without a strategy shows its default
        var c = LogicModel();
        c.Connections.Add(new ConnectionConfig("src", "gate"));
        var two = Site(ModelLogic.Of(c).Find("src", "element"), "outputStrategy.strategyType");
        Check(two != null && two.IsDefault && two.Value == "FirstAvailable", "two outputs: the default routing");

        var m1 = report.Find("m1", "element");
        Check(Site(m1, "processBy.team")?.Value == "Ops" && Site(m1, "processBy.priority")?.Reads.SequenceEqual(new[] { "item.prio" }) == true,
              "who serves m1 and with which priority");
        var m2 = report.Find("m2", "element");
        var admission = Site(m2, "inputStrategy.condition");
        Check(admission != null && admission.Site == "admission" && admission.Context == "admission"
              && admission.Reads.SequenceEqual(new[] { "element.open", "item.family" }), "m2's admission reads: " + admission?.Line);

        var team = report.Find("Ops", "team");
        var order = Site(team, "requestOrder");
        Check(order != null && order.Site == "dispatch" && order.Value == "urgentFirst: |priority desc" && order.Reads.SequenceEqual(new[] { "priority" }),
              "the team's named request order: " + order?.Line);
        var members = Site(team, "resourceOrder");
        Check(members != null && members.IsDefault && members.Value == "longest idle first", "member choice by default: " + members?.Line);
        Check(team.UsedBy.SequenceEqual(new[] { "m1 (processBy)" }), "the team's uses: " + string.Join(", ", team.UsedBy));

        var list = report.Find("orders", "list");
        Check(Site(list, "order")?.Value == "prio desc" && Site(list, "fields.urgent")?.Reads.SequenceEqual(new[] { "entry.prio" }) == true,
              "the list's order and calculated field");
        Check(list.UsedBy.SequenceEqual(new[] { "pulled by sink where family == 'A'|urgent desc" }), "who pulls: " + string.Join(", ", list.UsedBy));
        var pull = Site(report.Find("sink", "element"), "pull:orders");
        Check(pull != null && pull.Site == "admission" && pull.Reads.SequenceEqual(new[] { "family", "urgent" }), "the sink's pull: " + pull?.Line);
    }

    static void TestReactions()
    {
        var report = ModelLogic.Of(LogicModel());
        var r = report.Find("Open", "reaction");
        Check(r != null && r.Type == "ItemEntered" && r.Next.SequenceEqual(new[] { "m1" }), "the reaction's signal and sources");
        Check(Site(r, "signal")?.Value == "ItemEntered of m1", "the signal: " + Site(r, "signal")?.Value);
        var set = Site(r, "actions[0]");
        Check(set != null && set.Value == "set m2.open = 1" && set.Writes.SequenceEqual(new[] { "m2.open (property)" }) && !set.ViewOnly, "a property write: " + set?.Line);
        var label = Site(r, "actions[1]");
        Check(label != null && label.Writes.SequenceEqual(new[] { "item.seen" }) && label.Reads.SequenceEqual(new[] { "now()" }), "a label write: " + label?.Line);
        var look = Site(r, "actions[2]");
        Check(look != null && look.ViewOnly && look.Writes.Count == 0 && look.Value == "item look red ×1.5", "a look is view only: " + look?.Line);
        var retry = Site(r, "actions[3]");
        Check(retry != null && retry.Value.StartsWith("retry gate") && retry.Value.EndsWith("only if content('gate') > 0")
              && retry.Reads.SequenceEqual(new[] { "content('gate')" }), "a retry with its condition: " + retry?.Line);
    }

    static void TestWakeUps()
    {
        var report = ModelLogic.Of(LogicModel());
        var wake = report.WakeUps.FirstOrDefault(w => w.Element == "m2");
        Check(wake != null && wake.Property == "open" && wake.WrittenBy.SequenceEqual(new[] { "reaction 'Open' actions[0]" }), "m2 woken by 'open'");
        Check(report.Find("m2", "element").WokenBy.SequenceEqual(new[] { "property 'open' (reaction 'Open' actions[0])" }), "m2's wokenBy");
        var gate = report.Find("gate", "element");
        Check(gate.Notes.Count == 1 && gate.Notes[0].Contains("content('m1')") && gate.Notes[0].Contains("retried by reaction 'Open' actions[3]"),
              "a condition over content: retried: " + string.Join(" | ", gate.Notes));

        // the property never written and no retry: both noted
        var c = LogicModel();
        c.Reactions.Clear();
        report = ModelLogic.Of(c);
        Check(report.WakeUps.Any(w => w.Element == "m2" && w.WrittenBy.Count == 0), "nothing writes 'open'");
        Check(report.Notes.Any(n => n.StartsWith("m2:") && n.Contains("property 'open', which no reaction writes"))
              && report.Notes.Any(n => n.StartsWith("gate:") && n.Contains("it is checked again when 'gate' frees a place; if what it reads changes elsewhere, add a Retry action targeting it")), "notes: " + string.Join(" | ", report.Notes));

        // a computed property name wakes on any write
        var m2 = c.Elements.First(e => e.Id == "m2");
        m2.Set("inputStrategy.condition", "get('m2', item.family) == 1");
        Check(ModelLogic.Of(c).WakeUps.Any(w => w.Element == "m2" && w.Property == null), "a computed name: any property");
    }

    static void TestData()
    {
        var report = ModelLogic.Of(LogicModel());
        var d = report.ToData();
        var elements = (List<object>)d["elements"];
        Check(elements.Count == 6 && d.ContainsKey("teams") && d.ContainsKey("lists") && d.ContainsKey("reactions") && d.ContainsKey("wakeUps") && d.ContainsKey("notes"),
              "the blocks: " + string.Join(", ", d.Keys));
        string json = MiniJson.Serialize(d);
        Check(json.Contains("\"scoreExpression\"") == false && json.Contains("outputStrategy.scoreExpression = ") && json.Contains("actions[2] = item look red")
              && json.Contains("\"say\":\"Route best score: "), "JSON: " + json);
        var one = report.ToData("m2");
        Check(one.Count >= 1 && ((List<object>)one["elements"]).Count == 1 && one.ContainsKey("wakeUps"), "one owner: " + string.Join(", ", one.Keys));
        string error = null;
        try { report.ToData("m3"); } catch (ArgumentException e) { error = e.Message; }
        Check(error != null && error.ToLowerInvariant().Contains("did you mean"), "an unknown name suggests: " + error);
    }

    // src -> q -> m2 (admission on a property nothing writes) -> sink: the items stall in q
    static void TestMarks()
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
        c.Elements.Add(Arrivals("src", 0, 1, 2));
        c.Elements.Add(Cfg("q", "Queue"));
        var m2 = Station("m2", 5);
        m2.Set(PropertyRecords.Key, new List<PropertySettings> { PropertySettings.Number("open", 0) });
        m2.Set("inputStrategy.strategyType", "Condition");
        m2.Set("inputStrategy.condition", "element.open == 1");
        c.Elements.Add(m2);
        c.Elements.Add(Cfg("sink", "Sink"));
        c.Connections.Add(new ConnectionConfig("src", "q"));
        c.Connections.Add(new ConnectionConfig("q", "m2"));
        c.Connections.Add(new ConnectionConfig("m2", "sink"));
        string before = ConfigJson.Serialize(c);
        var d = new Study(c) { Threads = 1 }.WithRunTime(100).Diagnose(0);
        var q = ((List<object>)d["elements"]).OfType<Dictionary<string, object>>().FirstOrDefault(x => (string)x["name"] == "q");
        var marks = q != null && q.TryGetValue("decisions", out var m) ? (List<string>)m : new List<string>();
        Check(marks.Any(x => x.StartsWith("next m2: Admit when element.open == 1"))
              && marks.Any(x => x.Contains("which no reaction writes")), "the marks on q: " + string.Join(" | ", marks));
        Check(ConfigJson.Serialize(c) == before, "the diagnosis leaves the model as it was");
    }

    static void TestReferenceModels()
    {
        int reported = 0;
        foreach (var kv in ReferenceModels.All())
        {
            string before = ConfigJson.Serialize(kv.Value);
            try
            {
                var report = ModelLogic.Of(kv.Value);
                MiniJson.Serialize(report.ToData());
                foreach (var s in report.Owners.SelectMany(o => o.Sites))
                    if (s.Problem != null) Check(false, $"{kv.Key}: {s.Line}");
                reported++;
            }
            catch (Exception e) { Check(false, $"{kv.Key}: {e.Message}"); }
            Check(ConfigJson.Serialize(kv.Value) == before, kv.Key + ": the model unchanged");
        }
        Check(reported == ReferenceModels.All().Count, $"reported {reported} reference models");
    }

    static void TestSetupAndPreemptors()
    {
        var setup = SimuLean.Testing.ReferenceModels.SetupChangeover();
        var press = ModelLogic.Of(setup).Find("press", "element").Decisions.Select(d => d.Sentence).ToList();
        Check(press.Contains("Setup when item.type changes · time Uniform~0.5~1.5 · crew: team setters"), "a label-change setup with its crew: " + string.Join(" | ", press));
        var paint = ModelLogic.Of(setup).Find("paint", "element").Decisions.Select(d => d.Sentence).ToList();
        Check(paint.Any(x => x.StartsWith("Setup before each item · time Constant~0.2") && x.Contains("the crew stays for the process")), "each item, kept for the process: " + string.Join(" | ", paint));
        var matrix = ModelLogic.Of(SimuLean.Testing.ReferenceModels.SetupMatrixGrouping()).Owners.SelectMany(o => o.Decisions).Select(d => d.Sentence).FirstOrDefault(x => x.StartsWith("Setup"));
        Check(matrix != null && matrix.Contains("time from matrix 'changeovers'") && matrix.Contains("by the machine alone"), "a matrix, the machine alone: " + matrix);

        var pre = ModelLogic.Of(SimuLean.Testing.ReferenceModels.ResourcePreemption());
        string Res(string owner) => pre.Find(owner, "element").Decisions.First(d => d.Site == "resources").Sentence;
        Check(Res("routine").EndsWith("preemptable by urgent, rework"), "the lowest priority: taken by both preempting uses: " + Res("routine"));
        Check(Res("rework").EndsWith("preemptable by urgent"), "a middle priority: only by the higher one: " + Res("rework"));
        Check(!Res("urgent").Contains("preemptable by"), "the highest: by none: " + Res("urgent"));
        var none = ModelLogic.Of(SimuLean.Testing.ReferenceModels.ResourceRequestPriority()).Owners.SelectMany(o => o.Decisions).Select(d => d.Sentence);
        Check(!none.Any(x => x.Contains("preemptable by")), "no preemption mode in the model: nothing said");
    }

    // a team's usedBy lists every use — a requirement's team, a setup crew, a downtime's repair crew
    static void TestTeamUses()
    {
        var c = new SimulationConfig();
        foreach (var t in new[] { "Crane", "Machinists", "Mechanics", "Setters" })
            c.ResourceTeams.Add(new ResourceTeamConfig { Id = t, Name = t, Members = { new ResourceMemberConfig { Name = t + "1" } } });
        var lift = Station("CraneLift", 5);
        lift.Set("processBy.requirements", new List<RequirementSettings> { RequirementSettings.Of(1, "", "", "Crane"), RequirementSettings.Of(1, "", "", "Machinists") });
        var weld = Station("Weld", 3);
        weld.Set("processBy.team", "Machinists");
        weld.Set("setup.when", "EachItem"); weld.Set("setup.time", "Constant~1"); weld.Set("setup.setupBy.team", "Setters");
        c.Elements.Add(lift); c.Elements.Add(weld);
        c.Connections.Add(new ConnectionConfig("CraneLift", "Weld"));
        var fail = new DowntimeConfig("Breakdowns", "MtbfMttr", "Weld");
        fail.Set("timeToFailure", "ExponentialMean~100"); fail.Set("timeToRepair", "ExponentialMean~5"); fail.Set("repairBy.team", "Mechanics");
        c.Downtimes.Add(fail);
        var report = ModelLogic.Of(c);
        List<string> Used(string team) => report.Find(team, "team").UsedBy;
        Check(Used("Crane").SequenceEqual(new[] { "CraneLift (processBy)" }), "Crane: the lift's requirement: " + string.Join(", ", Used("Crane")));
        Check(Used("Machinists").SequenceEqual(new[] { "CraneLift (processBy)", "Weld (processBy)" }), "Machinists: the lift's requirement and the weld: " + string.Join(", ", Used("Machinists")));
        Check(Used("Setters").SequenceEqual(new[] { "Weld (setup.setupBy)" }), "Setters: the setup crew: " + string.Join(", ", Used("Setters")));
        Check(Used("Mechanics").SequenceEqual(new[] { "downtime Breakdowns (repairBy)" }), "Mechanics: the repair crew: " + string.Join(", ", Used("Mechanics")));
        var data = MiniJson.Serialize(report.ToData("Crane"));
        Check(data.Contains("usedBy") && data.Contains("CraneLift"), "the data lists the use: " + data);
        // the lift's resource line names both requirement teams
        var line = report.Find("CraneLift", "element").Decisions.FirstOrDefault(d => d.Site == "resources")?.Sentence ?? "";
        Check(line.Contains("Crane") && line.Contains("Machinists"), "the lift's resource line names its requirement teams: " + line);
    }

    static void TestDowntimesListed()
    {
        var c = ReferenceModels.BasicLine();
        var off = new DowntimeConfig("CallAway", "Periodic", "srv");
        off.Set("periodicPeriod", 100.0); off.Set("periodicDuration", 5.0);
        c.Downtimes.Add(off);
        var data = ModelLogic.Of(c).ToData();
        var downtimes = data.TryGetValue("downtimes", out var d) ? (List<object>)d : new List<object>();
        Check(downtimes.Count == 1, "the downtime without a repair crew is listed: " + MiniJson.Serialize(data));
        var first = downtimes.Count == 1 ? (Dictionary<string, object>)downtimes[0] : new Dictionary<string, object>();
        Check(first.TryGetValue("targets", out var t) && ((IEnumerable<string>)t).SequenceEqual(new[] { "srv" }), "with its targets: " + MiniJson.Serialize(first));
    }
}
