// Model description and diagnosis texts: unset label reads, the real waits, names, priorities, default home nodes.
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Serialization;

static partial class Program
{
    static void TestUnsetLabelReads()
    {
        SimuLean.Serialization.SimulationConfig Model(string labels, string routing = null)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "Src");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Constant"); src.Set("interArrivalDistribution.constantValue", 1.0);
            src.Set("createLabels", new List<string> { labels });
            var q = new SimuLean.Serialization.ElementConfig("q", "Queue", "Q");
            if (routing != null) { q.Set("outputStrategy.strategyType", "BestScore"); q.Set("outputStrategy.scoreExpression", routing); }
            c.Elements.Add(src); c.Elements.Add(q);
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("out", "Sink", "Out"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "q", "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("q", "out", "General"));
            return c;
        }
        var bad = SimuLean.Logic.ModelLogic.UnsetLabelReads(Model("type = A"));
        Check(bad.Count == 1 && bad[0].Contains("Src.createLabels") && bad[0].Contains("'A'") && bad[0].Contains("did you mean the text"),
              $"'type = A' reads label A, which nothing sets: {string.Join(" | ", bad)}");
        Check(SimuLean.Logic.ModelLogic.UnsetLabelReads(Model("type = 'A'")).Count == 0, "a quoted text is no read");
        Check(SimuLean.Logic.ModelLogic.UnsetLabelReads(Model("type = 1, prio = type * 2")).Count == 0, "a label set by an earlier assignment");
        Check(SimuLean.Logic.ModelLogic.UnsetLabelReads(Model("type = 1", "item.type + item.id")).Count == 0, "a label set by the source and a built-in");
        var routed = SimuLean.Logic.ModelLogic.UnsetLabelReads(Model("type = 1", "item.lot"));
        Check(routed.Count == 1 && routed[0].StartsWith("Q.outputStrategy.scoreExpression"), $"a routing reading a label nothing sets: {string.Join(" | ", routed)}");
        // a schedule table's columns are labels (Source.cs sets them)
        var table = Model("type = 1", "item.lot");
        table.Elements[0].Set("mode", "Schedule");
        table.Elements[0].Set("arrivalsTable", new Dictionary<string, List<string>> { ["Time"] = new List<string> { "0" }, ["lot"] = new List<string> { "x" } });
        Check(SimuLean.Logic.ModelLogic.UnsetLabelReads(table).Count == 0, "a table column sets the label");
        // a file read at run may set any label: nothing is flagged
        var file = Model("type = A");
        file.Elements[0].Set("fileName", "arrivals.csv");
        Check(SimuLean.Logic.ModelLogic.UnsetLabelReads(file).Count == 0, "a file read at run: not flagged");
        // no false alarm on the reference models
        var flagged = new List<string>();
        foreach (var kv in SimuLean.Testing.ReferenceModels.All())
            foreach (string w in SimuLean.Logic.ModelLogic.UnsetLabelReads(kv.Value)) flagged.Add(kv.Key + ": " + w);
        Check(flagged.Count == 0, "the reference models read only labels they set: " + string.Join(" | ", flagged));
    }

    static void TestDiagnosisRealWaits()
    {
        // cnc (capacity 2, 10 per job) needs the one operator: the job of t = 0 is processed, the job of t = 1 waits for the
        // operator — cnc is PROCESSING, its next element insp is empty. plain (no team) is processing its item: no wait.
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
        SimuLean.Serialization.ElementConfig Src(string id, params string[] times)
        {
            var e = new SimuLean.Serialization.ElementConfig(id, "Source", id);
            e.Set("mode", "Schedule");
            e.Set("arrivalsTable", new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string>(times), ["Name"] = times.Select(_ => id).ToList(), ["Q"] = times.Select(_ => "1").ToList(),
            });
            return e;
        }
        SimuLean.Serialization.ElementConfig Station(string id, int capacity, double time, string team)
        {
            var e = new SimuLean.Serialization.ElementConfig(id, "MultiServer", id);
            e.Set("capacity", capacity); e.Set("delayStrategy.strategyType", "Constant"); e.Set("delayStrategy.constantValue", time);
            if (team != null) e.Set("processBy.team", team);
            return e;
        }
        c.Elements.Add(Src("src", "0", "1")); c.Elements.Add(Src("src2", "0"));
        c.Elements.Add(Station("cnc", 2, 10, "ops")); c.Elements.Add(Station("insp", 1, 1, null)); c.Elements.Add(Station("plain", 1, 10, null));
        c.Elements.Add(new SimuLean.Serialization.ElementConfig("out", "Sink", "out"));
        foreach (var (f, t) in new[] { ("src", "cnc"), ("cnc", "insp"), ("insp", "out"), ("src2", "plain"), ("plain", "out") })
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(f, t, "General"));
        var team = new SimuLean.Serialization.ResourceTeamConfig { Id = "ops", Name = "ops" };
        team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "Op" });
        c.ResourceTeams.Add(team);
        var clock = new SimClock(1);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
        clock.AdvanceClock(5);
        var d = SimuLean.Headless.ModelDiagnosis.Of(model, clock);
        var elements = ((System.Collections.IEnumerable)d["elements"]).Cast<Dictionary<string, object>>().ToList();
        string Wait(string name) => elements.First(e => (string)e["name"] == name)["waitsFor"] as string;
        string cnc = Wait("cnc"), plain = Wait("plain");
        Check(cnc != null && cnc.Contains("waits for team ops (1 for Process") && !cnc.Contains("insp"),
              $"cnc waits for its operator, not for insp: {cnc}");
        Check(plain == null, $"a station processing its item waits for nothing: {plain}");
        var teams = ((System.Collections.IEnumerable)d["teams"]).Cast<Dictionary<string, object>>().ToList();
        var member = ((System.Collections.IEnumerable)teams[0]["members"]).Cast<Dictionary<string, object>>().First();
        Check((string)member["task"] == "Process", $"the member's task is named once: {member["task"]}");
    }

    // ── diagnosis and description texts ──────────────────────────────────

    static void TestEmptiesOriginError()
    {
        var c = SimuLean.Testing.ReferenceModels.WarehouseToLine();
        c.Elements.First(e => e.Name == "TuggerTrain").Set("stops", new List<MilkRunStopSettings> { MilkRunStopSettings.Of("LineSide", "", new[] { "LineA", "LineB" }) });
        string error = null;
        try { new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(c); }
        catch (Exception e) { error = e.Message; }
        Check(error != null && error.Contains("empties origin") && error.Contains("emptiesDestination") && error.Contains("Queue"),
              "an unpacker as an empties origin fails the build with the pattern: " + error);
        // the pattern (the unpackers' empties port to a queue at the stop, the queues listed) builds
        new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(SimuLean.Testing.ReferenceModels.WarehouseToLine());
        Check(true, "the queue pattern builds");
    }

    static void TestDescriptionNames()
    {
        var c = SimuLean.Testing.ReferenceModels.RepairCrew();
        // ids that differ from the names, as a scene extracts them
        foreach (var t in c.ResourceTeams) t.Id = t.Name + "_123:456";
        foreach (var e in c.Elements)
            foreach (var (path, value) in e.Values().ToList())
                if (path.EndsWith(".team") && value is string v && v.Length > 0) e.Set(path, v + "_123:456");
        foreach (var d0 in c.Downtimes)
        {
            if (d0.Settings?.RepairBy?.Team is string rt && rt.Length > 0) d0.Set("repairBy.team", rt + "_123:456");
            for (int i = 0; i < d0.TargetIds.Count; i++) if (d0.TargetIds[i] == "ops") d0.TargetIds[i] = "ops_123:456";
        }
        c.Elements.First(e => e.Name == "m1").Set("location", "pos:m1");
        var d = SimuLean.Optimization.ModelDescription.Of(c);
        var m1 = ((System.Collections.IEnumerable)d["elements"]).Cast<Dictionary<string, object>>().First(e => (string)e["name"] == "m1");
        var p = (SortedDictionary<string, string>)m1["parameters"];
        Check(p["processBy.team"] == "ops", "a station's team by name: " + p["processBy.team"]);
        Check(!p.ContainsKey("location"), "an automatic point left out of the compact view");
        Check(!p.Values.Any(x => x.Length == 0), "no empty text in the compact view");
        var teams = ((System.Collections.IEnumerable)d["teams"]).Cast<Dictionary<string, object>>().ToList();
        var mech = (SortedDictionary<string, string>)teams.First(t => (string)t["name"] == "mech")["parameters"];
        Check(mech.TryGetValue("requestOrder", out string ro) && ro.Contains("priority desc"), "a team's request order: " + ro);
        var downtimes = ((System.Collections.IEnumerable)d["downtimes"]).Cast<Dictionary<string, object>>().ToList();
        var opFail = downtimes.First(x => (string)x["id"] == "operatorFail");
        Check(((List<string>)opFail["targets"]).SequenceEqual(new[] { "ops" }), "a downtime's team target by name");
        var dp = (SortedDictionary<string, string>)opFail["parameters"];
        Check(dp.TryGetValue("repairBy.team", out string crew) && crew == "mech", "a downtime's repair crew by name: " + crew);
        var full = SimuLean.Optimization.ModelDescription.Of(c, full: true);
        var m1f = (SortedDictionary<string, string>)((System.Collections.IEnumerable)full["elements"]).Cast<Dictionary<string, object>>().First(e => (string)e["name"] == "m1")["parameters"];
        Check(m1f.TryGetValue("location", out string loc) && loc == "m1 (its position)", "full keeps the location, by name: " + loc);

        // a network team: its travel and home; a rack's lanes summarised
        var w = SimuLean.Optimization.ModelDescription.Of(SimuLean.Testing.ReferenceModels.WarehouseToLine());
        var fork = (SortedDictionary<string, string>)((System.Collections.IEnumerable)w["teams"]).Cast<Dictionary<string, object>>().First(t => (string)t["name"] == "Forklifts")["parameters"];
        Check(fork["travel"] == "Network" && fork["homeNode"] == "dock", "a network team's travel and home");
        var rack = (SortedDictionary<string, string>)((System.Collections.IEnumerable)w["elements"]).Cast<Dictionary<string, object>>().First(e => (string)e["name"] == "Rack")["parameters"];
        Check(rack.TryGetValue("lanes", out string lanes) && lanes.StartsWith("12 items") && !rack.Keys.Any(k => k.StartsWith("lanes[0]")),
              "the lanes in one summary: " + lanes);
        Check(rack.TryGetValue("lanes[*].capacity", out string cap) && cap == "1", "a field every lane shares: " + cap);
        var wf = SimuLean.Optimization.ModelDescription.Of(SimuLean.Testing.ReferenceModels.WarehouseToLine(), full: true);
        var rackFull = (SortedDictionary<string, string>)((System.Collections.IEnumerable)wf["elements"]).Cast<Dictionary<string, object>>().First(e => (string)e["name"] == "Rack")["parameters"];
        Check(rackFull.ContainsKey("lanes[11].id"), "full lists each lane");
    }

    static void TestRequesterPriorityShown()
    {
        var report = SimuLean.Logic.ModelLogic.Of(SimuLean.Testing.ReferenceModels.ResourcePreemption());
        string Say(string name) => string.Join(" / ", report.Find(name, "element").Decisions.Select(x => x.Sentence));
        Check(Say("routine").Contains("priority 1"), "the default priority beside 5 and 3: " + Say("routine"));
        Check(Say("urgent").Contains("priority 5"), "the urgent station's: " + Say("urgent"));
        // one priority everywhere, first come first served: nothing to say
        var plain = SimuLean.Logic.ModelLogic.Of(SimuLean.Testing.ReferenceModels.RepairCrew());
        Check(!string.Join(" / ", plain.Find("m1", "element").Decisions.Select(x => x.Sentence)).Contains("priority"), "no priority when nothing reads it");
    }

    static void TestDiagnosisCombinerAndCarriers()
    {
        var c = SimuLean.Testing.ReferenceModels.WarehouseToLine();
        var clock = new SimClock(1);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
        var asm = model.Find("Assembly");
        var line = (Separator)model.Find("LineA");
        bool sawProcessing = false, sawCarriers = false;
        for (double t = 60; t < 6000 && !(sawProcessing && sawCarriers); t += 7)
        {
            clock.AdvanceClock(t);
            var d = SimuLean.Headless.ModelDiagnosis.Of(model, clock);
            var rows = ((System.Collections.IEnumerable)d["elements"]).Cast<Dictionary<string, object>>().ToList();
            var a = rows.FirstOrDefault(r => (string)r["name"] == "Assembly");
            if (a != null && asm.CurrentState == ElementState.PROCESSING && !sawProcessing)
            {
                sawProcessing = true;
                Check(a["waitsFor"] == null, $"t={t}: a Combiner processing waits for nothing: {a["waitsFor"]}");
            }
            var l = rows.FirstOrDefault(r => (string)r["name"] == "LineA");
            if (l != null && (int)l["items"] > line.Capacity && !sawCarriers)
            {
                sawCarriers = true;
                Check(l.ContainsKey("carriers") && (int)l["carriers"] <= line.Capacity && (int)l["pieces"] + line.ItemsHeldCount == (int)l["items"],
                      $"t={t}: carriers {l["carriers"]} (capacity {l["capacity"]}) and pieces {l["pieces"]} apart, items {l["items"]}");
                Check(model.Monitor.WaitsFor(line).Contains("carrier(s) (capacity 2)"), "the monitor's text: " + model.Monitor.WaitsFor(line));
            }
        }
        Check(sawProcessing && sawCarriers, $"the run reached both cases (processing {sawProcessing}, more items than carriers {sawCarriers})");
    }

    static void TestDefaultHomeNode()
    {
        var c = SimuLean.Testing.ReferenceModels.WarehouseToLine();
        c.ResourceTeams.First(t => t.Name == "Forklifts").HomeNode = "";
        var model = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
        model.BuildModel(c);
        var fork = model.Teams.Values.First(t => t.Name == "Forklifts");
        Check(fork.Members[0].HomeNode == "dock", "the network's first node: " + fork.Members[0].HomeNode);
        var check = new SimuLean.Optimization.Study(c) { Threads = 1 }.Check();
        Check(check.Ok && check.Info.Any(i => i.Contains("Forklifts") && i.Contains("'dock'")),
              "validate says so: " + string.Join(" | ", check.Info) + string.Join(" | ", check.Errors));
        // a home that is no node: the error says what to set
        c.ResourceTeams.First(t => t.Name == "Forklifts").HomeNode = "nowhere";
        string error = null;
        try { new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(c); } catch (Exception e) { error = e.Message; }
        Check(error != null && error.Contains("set its homeNode to a Transport Node"), "a wrong home: " + error);
    }
}
