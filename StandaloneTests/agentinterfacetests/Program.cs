// Standalone tests of the engine side of the agent interface: a study checked on
// the built model before it runs (unknown / ambiguous targets, statistics and states with "did you mean", factors on an
// unused distribution, unbuildable levels) and name lookups that fail on two objects with one name.
// Run: dotnet run  (from StandaloneTests/agentinterfacetests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean.Optimization;
using SimuLean.Serialization;
using SimuLean.Testing;

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
        Run("Did you mean: closest names by edit distance, case-insensitive", TestSuggestions);
        Run("Study.Check: a valid study has no problem and runs nothing", TestValidStudy);
        Run("Study.Check: unknown target, statistic and state are errors with suggestions (a state typo read 0)", TestMetricProblems);
        Run("Study.Check: a factor on a key of another distribution is a warning; an unbuildable level an error", TestFactorProblems);
        Run("A metric on a team member says which team to read; scope 'resource' points to the team", TestMemberMetrics);
        Run("A team named like an element (metric or factor) gets the team: path in the error", TestScopePrefixHints);
        Run("A tally metric: an undefined tally or statistic is refused by Check; a defined tally with no value reads n 0", TestTallyMetrics);
        Run("Downtime metrics (downtime:Id.stat, repair crews) and preemption metrics (team / element) are checked and read", TestDowntimeAndPreemptionMetrics);
        Run("Diagnosis reports progress: last movement, oldest request, longest task; notes on a model frozen for long (facts, no verdict)", TestDiagnosisProgress);
        Run("Diagnosis names a circular wait: a rework loop with too little room deadlocks; a flowing loop has none", TestDiagnosisCircularWait);
        Run("Two elements with one name: factors and metrics fail instead of taking the first", TestAmbiguousNames);
        Run("ModelDescription: the whole model by name, every reference model; one object; an unknown name suggests", TestModelDescription);
        Run("ModelDescription: every team's and downtime's metric paths are valid study metrics; members by their built names", TestModelMetricPaths);
        Run("ModelDescription: a timetable downtime's rows and state; one downtime alone has no empty element list", TestDowntimeDescription);
        Run("Transport load / unload times are study factors by their key (a one-number distribution); others name their parts", TestTransportTimeFactors);
        Run("Vocabulary: every part built from the code; the built-ins resolve; one concept per argument; a model's states", TestVocabulary);
        Run("Factor paths: the key is the field path (dots); the owner is the longest known name (dots, spaces)", TestFactorPathGrammar);
        Run("Validate info: a team's member breakdowns do not interrupt a station using the team without a preemption mode", TestMemberStopInfo);
        Run("The agents' guide cites only statistics the engine emits, for the element type it names", TestDocStatistics);
        Run("ModelDescription: the transport network — nodes with positions and elements, tracks with lengths, teams; alone by name", TestNetworkDescription);
        Run("Validate info: each exponential distribution by its mean", TestExponentialMeans);
        Run("Validate info: each LogNormal / Gamma / Weibull distribution by its mean", TestShapeScaleMeans);
        Run("A connection without a type or 'Simple' is General in the build, ModelDescription and ModelLogic alike", TestConnectionTypeDefault);
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    static void TestSuggestions()
    {
        var options = new[] { "input", "output", "content_avg", "utilization" };
        Check(NameSuggestions.Closest("inpt", options).FirstOrDefault() == "input", "inpt → input");
        Check(NameSuggestions.Closest("Utilisation", options).FirstOrDefault() == "utilization", "case and one letter");
        Check(NameSuggestions.Closest("content", options).FirstOrDefault() == "content_avg", "a prefix");
        Check(NameSuggestions.Closest("makespan", options).Count == 0, "nothing close: no suggestion");
        Check(NameSuggestions.DidYouMean("snk", new[] { "sink", "src" }).Contains("'sink'"), "the sentence names the option");
    }

    // A key is the field path: "Srv.delayStrategy.constantValue" splits after the owner, the owner found among the
    // model's names first (a name may hold dots or spaces), else at the first dot
    static void TestFactorPathGrammar()
    {
        Check(Study.Split("Srv.delayStrategy.constantValue") == ("element", "Srv", "delayStrategy.constantValue"), "no model: the owner ends at the first dot");
        Check(Study.Split("team:Welders.members") == ("team", "Welders", "members"), "a scope before the first dot");
        var config = ReferenceModels.MultiServer3();
        var ms = config.Elements.Find(e => e.Name == "ms");
        var dotted = SimuLean.Serialization.ConfigCloner.DeepClone(ms);
        dotted.Id = "dotted"; dotted.Name = "Line 1.ms";
        config.Elements.Add(dotted);
        Check(Study.Split("Line 1.ms.delayStrategy.strategyType", config) == ("element", "Line 1.ms", "delayStrategy.strategyType"), "an owner with a dot and a space, by its name");
        Check(Study.Split("ms.capacity", config) == ("element", "ms", "capacity"), "the plain name");
        Check(Study.Split("Line 1.ms.capacity", config).owner == "Line 1.ms", "the longest name wins");
        var p = new Study(config) { Threads = 1 }.ParameterOf("Line 1.ms.capacity", "1", "4") as ConfigParameter;
        Check(p != null && p.Owner == "Line 1.ms" && p.Info.Key == "capacity", "a factor on the dotted owner resolves: " + p?.Owner + " / " + p?.Info.Key);
        var t = new Study(config) { Threads = 1 }.ParameterOf("ms.delayStrategy.triangularMax", "2", "3") as ConfigParameter;
        Check(t != null && t.Info.Key == "delayStrategy.triangularMax", "a dotted key resolves");
    }

    static void TestTransportTimeFactors()
    {
        // 'Rack.transportBy.loadTime' — the key simulean_params lists — was "no parameter"
        Study Transport() => new Study(ReferenceModels.ResourceTransport()) { Threads = 1 }.WithRunTime(300);
        var load = Transport().ParameterOf("m1.transportBy.loadTime", "0.2", "3") as ConfigParameter;
        Check(load != null && load.Info.Key == "transportBy.loadTime.constantValue", "a one-number distribution key stands for its number: " + load?.Info.Key);
        var unload = Transport().ParameterOf("m1.transportBy.unloadTime.constantValue", "0.2", "3") as ConfigParameter;
        Check(unload != null && unload.Info.Key == "transportBy.unloadTime.constantValue", "and its part key as before");
        var copy = ReferenceModels.ResourceTransport();
        load.ApplyText(copy, "3");
        Check(copy.Elements.Find(e => e.Id == "m1").Get<SamplerSettings>("transportBy.loadTime")?.ConstantValue == 3, "the value is written into the distribution");

        var set = Transport().Factor("m1.transportBy.loadTime", "0.2", "3").Factor("m1.transportBy.unloadTime", "0.2", "3")
                             .Metric("out", "sink.input").Run();
        Check(set.Runs.All(r => r.Error == null), "a study varies the load / unload times: " + string.Join(" | ", set.Runs.Select(r => r.Error)));
        double fast = set.Runs.First().Metrics["out"], slow = set.Runs.Last().Metrics["out"];
        Check(slow < fast, $"longer load / unload times carry fewer items ({slow} < {fast})");

        string error = null;
        try { Transport().ParameterOf("m1.transportBy.travelTime", "1", "2"); } catch (ArgumentException e) { error = e.Message; }
        Check(error != null && error.Contains("transportBy.travelTime.uniformMin") && error.Contains("transportBy.travelTime.uniformMax"),
              "a distribution of two numbers names its parts: " + error);
        error = null;
        try { Transport().ParameterOf("m2.transportBy.loadTime", "1", "2"); } catch (ArgumentException e) { error = e.Message; }
        Check(error != null && error.Contains("loadTime") && error.Contains("overrideLoadTimes"),
              "an element without its own load times says where they are: " + error);
    }

    static Study MultiServer() =>new Study(ReferenceModels.MultiServer3()) { Threads = 1 }.WithRunTime(100);

    static void TestValidStudy()
    {
        var study = MultiServer().Factor("ms.capacity", 2, 3)
            .Metric("out", "sink.input").Metric("busy", "ms.utilization").Metric("proc", "ms.state:PROCESSING")
            .Metric("span", "model.makespan").Metric("wait", "q.stay_avg");
        var check = study.Check();
        Check(check.Ok && check.Warnings.Count == 0, "valid: " + string.Join(" | ", check.Errors.Concat(check.Warnings)));
    }

    static void TestMetricProblems()
    {
        var check = MultiServer().Metric("out", "snk.input").Check();
        Check(!check.Ok && check.Errors[0].Contains("no element 'snk'") && check.Errors[0].Contains("'sink'"), "unknown target: " + string.Join(" | ", check.Errors));
        check = MultiServer().Metric("out", "sink.inpt").Check();
        Check(!check.Ok && check.Errors[0].Contains("'input'"), "unknown statistic suggests 'input': " + string.Join(" | ", check.Errors));
        check = MultiServer().Metric("busy", "ms.state:PROCESING").Check();
        Check(!check.Ok && check.Errors[0].Contains("no state 'PROCESING'") && check.Errors[0].Contains("'PROCESSING'"),
              "a misspelt state is an error (a run reads 0): " + string.Join(" | ", check.Errors));
        check = MultiServer().Metric("u", "team:crew.utilization").Check();
        Check(!check.Ok && check.Errors[0].Contains("no resource team 'crew'"), "a team the model has not: " + string.Join(" | ", check.Errors));
        check = MultiServer().Metric("u", "model.makespam").Check();
        Check(!check.Ok && check.Errors[0].Contains("'makespan'"), "model statistic: " + string.Join(" | ", check.Errors));
        // the runs still fail with the same message (the check reads the metrics the way a run does)
        var set = MultiServer().Metric("out", "snk.input").Run();
        Check(set.Runs.All(r => r.Error != null && r.Error.Contains("snk")), "a run of an unchecked study records the error");
    }

    static void TestFactorProblems()
    {
        // a scene exports every distribution's fields (the extractor), so the unused keys are in the config
        var config = ReferenceModels.MultiServer3();
        config.Elements.Find(e => e.Name == "ms").Set("delayStrategy.uniformMin", 0.2);
        var check = new Study(config) { Threads = 1 }.WithRunTime(100).Factor("ms.delayStrategy.uniformMin", 0.5, 1).Metric("out", "sink.input").Check();
        Check(check.Ok && check.Warnings.Count == 1 && check.Warnings[0].Contains("Triangular"),
              "a factor on Uniform keys of a Triangular service time: warned: " + string.Join(" | ", check.Warnings));
        check = MultiServer().Factor("ms.delayStrategy.uniformMin", 0.5, 1).Metric("out", "sink.input").Check();
        Check(!check.Ok && check.Errors[0].Contains("belongs to another distribution") && check.Errors[0].Contains("Triangular"),
              "the key not in the config (a scene exports the chosen distribution only): an error that says why: " + string.Join(" | ", check.Errors));
        check = MultiServer().Factor("ms.delayStrategy.triangularMax", 2, 3).Metric("out", "sink.input").Check();
        Check(check.Ok && check.Warnings.Count == 0, "a factor on the active distribution: no warning");
        check = MultiServer().Factor("ms.capacity", 2, -1).Metric("out", "sink.input").Check();
        Check(!check.Ok && check.Errors.Count == 1 && check.Errors[0].StartsWith("Scenario 'ms.capacity=-1'"),
              "the level the model cannot be built with is named: " + string.Join(" | ", check.Errors));
        check = MultiServer().Factor("mss.capacity", 2).Check();
        Check(!check.Ok && check.Errors[0].Contains("'ms'"), "an unknown factor owner suggests: " + string.Join(" | ", check.Errors));
    }

    static void TestMemberMetrics()
    {
        var team = new Study(ReferenceModels.ResourceSharedTeam()) { Threads = 1 }.WithRunTime(50);
        var check = team.Metric("w", "Team_1.utilization").Check();
        Check(!check.Ok && check.Errors[0].Contains("'Team_1' is a member of team 'team'") && check.Errors[0].Contains("'team:team.utilization'"),
              "a member named as an element: " + string.Join(" | ", check.Errors));
        string error = null;
        try { Study.MetricOf("w", "resource:Team_1.utilization"); } catch (ArgumentException e) { error = e.Message; }
        Check(error != null && error.Contains("unknown scope 'resource'") && error.Contains("through its team"), "scope resource: " + error);
        check = new Study(ReferenceModels.ResourceSharedTeam()) { Threads = 1 }.WithRunTime(50).Metric("w", "team:team.utilization").Check();
        Check(check.Ok, "the team's metric is valid: " + string.Join(" | ", check.Errors));
    }

    // A tally metric was not checked before the run (an undefined one failed every replication), and a defined tally that
    // got no value in a run (no item reached the element) failed the replication too, losing its other metrics.
    static void TestTallyMetrics()
    {
        var check = MultiServer().Metric("t", "sink.tally:tis:mean").Check();
        Check(!check.Ok && check.Errors[0].Contains("has no tally 'tis'") && check.Errors[0].Contains("key=tallies"),
              "an undefined tally is refused, saying how to define it: " + string.Join(" | ", check.Errors));
        var config = ReferenceModels.MultiServer3();
        config.Elements.Find(e => e.Name == "sink").Set("tallies", new List<string> { "tis = age" });
        check = new Study(config) { Threads = 1 }.WithRunTime(100).Metric("t", "sink.tally:tis:sum").Check();
        Check(!check.Ok && check.Errors[0].Contains("Statistics of a tally"), "an unknown tally statistic lists them: " + string.Join(" | ", check.Errors));
        var set = new Study(config) { Threads = 1 }.WithRunTime(0.001).Metric("n", "sink.tally:tis:count").Metric("t", "sink.tally:tis:mean")
            .Metric("in", "sink.input").Run();
        Check(set.Runs.All(r => r.Error == null), "a defined tally with no value does not fail the run: " + set.Runs[0].Error);
        Check(set.Runs[0].Metrics["n"] == 0 && double.IsNaN(set.Runs[0].Metrics["t"]) && set.Runs[0].Metrics["in"] == 0,
              "its count is 0, its mean NaN, the other metrics kept");
    }

    // A team (downtime, reaction) named without its scope prefix, the way an element is: the error names the prefixed
    // path, for a metric and for a factor — "no element 'AGVs'" alone leaves the caller to find the prefix in the docs.
    static void TestScopePrefixHints()
    {
        var check = new Study(ReferenceModels.ResourceSharedTeam()) { Threads = 1 }.WithRunTime(50).Metric("w", "team.utilization").Check();
        Check(!check.Ok && check.Errors[0].Contains("'team' is a resource team") && check.Errors[0].Contains("'team:team.utilization'"),
              "a team metric without team: names the prefixed path: " + string.Join(" | ", check.Errors));
        check = new Study(ReferenceModels.ResourceSharedTeam()) { Threads = 1 }.WithRunTime(50).Factor("team.members", 1, 2).Check();
        Check(!check.Ok && check.Errors[0].Contains("'team' is a resource team") && check.Errors[0].Contains("team:team.members"),
              "a team factor without team: names the prefixed path: " + string.Join(" | ", check.Errors));
        check = MultiServer().Metric("out", "snk.input").Check();
        Check(!check.Ok && !check.Errors[0].Contains("is a resource team"), "a misspelt element gets no scope hint: " + string.Join(" | ", check.Errors));
    }

    // The factory wired a connection without a type (a hand-written / JSON config) or "Simple" as General, but
    // ModelDescription and ModelLogic tested == "General" and listed no next element for it. One normaliser,
    // ConnectionConfig.EffectiveType, decides for all three.
    static void TestConnectionTypeDefault()
    {
        Check(ConnectionConfig.EffectiveType((string)null) == "General" && ConnectionConfig.EffectiveType("Simple") == "General"
              && ConnectionConfig.EffectiveType("General") == "General" && ConnectionConfig.EffectiveType("PushToList") == "PushToList"
              && ConnectionConfig.EffectiveType("") == "", "EffectiveType: null / Simple / General = General, the others as given");
        var general = ReferenceModelRunner.Run("general", ReferenceModels.BasicLine());
        foreach (string type in new[] { null, "Simple", "General" })
        {
            var config = ReferenceModels.BasicLine();
            foreach (var c in config.Connections) c.ConnectionType = type;
            string label = type ?? "no type";
            var elements = (System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)ModelDescription.Of(config, parameters: false)["elements"];
            var next = (System.Collections.Generic.List<string>)elements.First(e => (string)e["name"] == "q")["next"];
            Check(next.SequenceEqual(new[] { "srv" }), $"{label}: ModelDescription lists q -> srv ({string.Join(", ", next)})");
            var owner = SimuLean.Logic.ModelLogic.Of(config).OfKind("element").First(o => o.Name == "q");
            Check(owner.Next.SequenceEqual(new[] { "srv" }), $"{label}: ModelLogic lists q -> srv ({string.Join(", ", owner.Next)})");
            var r = ReferenceModelRunner.Run(label, config);
            bool same = r.Stats.Count == general.Stats.Count && r.Stats.All(kv => general.Stats.TryGetValue(kv.Key, out var g)
                        && kv.Value.Count == g.Count && kv.Value.All(s => g.TryGetValue(s.Key, out double v) && v.Equals(s.Value)));
            Check(same, $"{label}: the run equals the run with General connections");
        }
    }

    static void TestModelMetricPaths()
    {
        var config = ReferenceModels.RepairCrew();
        var d = ModelDescription.Of(config, parameters: false);
        var teams = (System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)d["teams"];
        var downtimes = (System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)d["downtimes"];
        var study = new Study(ReferenceModels.RepairCrew()) { Threads = 1 }.WithRunTime(3000);
        int n = 0;
        var paths = new System.Collections.Generic.List<string>();
        // every combination the description lists: path x statistics (states aside: any state name), and each target
        System.Collections.Generic.IEnumerable<string> Statistics(System.Collections.Generic.Dictionary<string, object> m)
            => ((string)m["statistics"]).Split(new[] { ", " }, StringSplitOptions.None).Where(x => !x.StartsWith("state:"));
        foreach (var t in teams)
        {
            var m = (System.Collections.Generic.Dictionary<string, object>)t["metrics"];
            Check(((string)m["path"]).StartsWith("team:" + t["name"] + ".") && ((string)m["statistics"]).Contains("utilization") && ((string)m["statistics"]).Contains("state:<STATE>")
                  && ((string)m["eachMember"]).Contains("not a study metric"), "a team's metric paths: " + SimuLean.Data.MiniJson.Serialize(m));
            foreach (string s in Statistics(m)) paths.Add(((string)m["path"]).Replace("<statistic>", s));
        }
        foreach (var x in downtimes)
        {
            var m = (System.Collections.Generic.Dictionary<string, object>)x["metrics"];
            foreach (string s in Statistics(m))
            {
                paths.Add(((string)m["path"]).Replace("<statistic>", s));
                if (m.ContainsKey("targets"))
                    foreach (string target in ((string)m["targets"]).Split(new[] { ", " }, StringSplitOptions.None))
                        paths.Add(((string)m["eachTarget"]).Replace("<target>", target).Replace("<statistic>", s));
            }
        }
        var fail = (System.Collections.Generic.Dictionary<string, object>)downtimes.First(x => (string)x["id"] == "operatorFail")["metrics"];
        Check((string)fail["eachTarget"] == "downtime:operatorFail@<target>.<statistic>" && (string)fail["targets"] == "Op1, Op2"
              && ((string)fail["statistics"]).Contains("repairs"), "a team target's members by name: " + SimuLean.Data.MiniJson.Serialize(fail));
        foreach (string p in paths) study.Metric("m" + n++, p);
        var check = study.Check();
        Check(paths.Count > 20 && check.Ok, $"all {paths.Count} paths listed are valid metrics: " + string.Join(" | ", check.Errors));
        // a downtime without a crew lists no repair statistic; one target: no '@'
        var single = (System.Collections.Generic.Dictionary<string, object>)((System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)
                         ModelDescription.Of(ReferenceModels.DowntimeModel())["downtimes"])[0]["metrics"];
        Check(!((string)single["statistics"]).Contains("repairs") && !single.ContainsKey("eachTarget"), "one target, no crew: " + SimuLean.Data.MiniJson.Serialize(single));
        // an unnamed member is <Team>_<n>, as built
        var shared = ModelDescription.Of(ReferenceModels.ResourceSharedTeam());
        var members = (System.Collections.Generic.List<string>)((System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)shared["teams"])[0]["members"];
        Check(members.SequenceEqual(new[] { "Team_1", "Team_2" }), "members by their built names: " + string.Join(", ", members));
    }

    // The pooled wait of a downtime on several targets weights each target's mean by its crews arrived, not by its
    // repairs completed: a repair still open at the end has its wait in the target's mean but not in its repairs.
    static void TestRepairWaitPooling()
    {
        SimuLean.Results.ResultObject Target(SimuLean.Results.RunResults r, string id, double repairs, double arrived, double waitAvg, double timeAvg)
        {
            var o = r.Add(new SimuLean.Results.ResultObject(SimuLean.Results.ResultScope.Downtime, id, id, "DowntimeGenerator"));
            o.Set(SimuLean.Results.ResultMetrics.Stops, arrived);
            o.Set(SimuLean.Results.ResultMetrics.DowntimeTotal, 0);
            o.Set(SimuLean.Results.ResultMetrics.Repairs, repairs);
            o.Set(SimuLean.Results.ResultMetrics.RepairWaits, arrived);
            o.Set(SimuLean.Results.ResultMetrics.RepairWaitAvg, waitAvg);
            o.Set(SimuLean.Results.ResultMetrics.RepairTimeAvg, timeAvg);
            return o;
        }
        double Read(SimuLean.Results.RunResults r, string statistic)
            => MetricCatalog.Read(new ModelMetric("m", MetricScope.Downtime, "fail", statistic), r);

        // A: 10 crews arrived, 10 repairs, wait 30; B: 11 crews arrived (the last repair open at the end), 10 repairs, wait 50
        var open = new SimuLean.Results.RunResults();
        Target(open, "fail@A", 10, 10, 30, 5);
        Target(open, "fail@B", 10, 11, 50, 7);
        double pooled = (30.0 * 10 + 50.0 * 11) / 21;
        Check(Math.Abs(Read(open, SimuLean.Results.ResultMetrics.RepairWaitAvg) - pooled) < 1e-12,
              $"a repair open at the end: the pooled wait {Read(open, SimuLean.Results.ResultMetrics.RepairWaitAvg)} is the mean over every crew arrived ({pooled})");
        Check(Math.Abs(Read(open, SimuLean.Results.ResultMetrics.RepairTimeAvg) - 6.0) < 1e-12, "the pooled repair time is over the repairs completed");
        Check(Read(open, SimuLean.Results.ResultMetrics.RepairWaits) == 21 && Read(open, SimuLean.Results.ResultMetrics.Repairs) == 20,
              "repair_waits and repairs are summed over the targets");

        // one crew arrived on A and no repair finished anywhere: the wait is that crew's, not NaN; the repair time NaN
        var first = new SimuLean.Results.RunResults();
        Target(first, "fail@A", 0, 1, 8.98, 0);
        Target(first, "fail@B", 0, 0, 0, 0);
        double w = Read(first, SimuLean.Results.ResultMetrics.RepairWaitAvg);
        Check(Math.Abs(w - 8.98) < 1e-12, $"one crew arrived, none finished: the pooled wait is its wait ({w})");
        Check(double.IsNaN(Read(first, SimuLean.Results.ResultMetrics.RepairTimeAvg)), "no repair completed: the pooled repair time is NaN");
    }

    static void TestDowntimeAndPreemptionMetrics()
    {
        string Errors(StudyCheck c) => string.Join(" | ", c.Errors);
        // repair crews: a downtime on two machines (one generator each: machineFail@m1, machineFail@m2), one on a team's members
        Study Crew() => new Study(ReferenceModels.RepairCrew()) { Threads = 1 }.WithRunTime(3000);
        var study = Crew().Metric("rep", "downtime:machineFail@m1.repairs").Metric("wait", "downtime:machineFail@m1.repair_wait_avg")
                          .Metric("fix", "downtime:machineFail@m2.repair_time_avg").Metric("stops", "downtime:operatorFail@Op1.stops")
                          .Metric("down", "downtime:operatorFail@Op2.downtime_total")
                          .Metric("pre", "team:mech.preemptions").Metric("inter", "team:ops.interruptions")
                          .Metric("jobs", "m1.interruptions").Metric("paused", "m1.interrupted_time_avg");
        var check = study.Check();
        Check(check.Ok, "the downtime and preemption metrics are valid: " + Errors(check));
        var run = study.Run().Runs[0];
        Check(run.Error == null, "the study runs: " + run.Error);
        var direct = run.Results.Find(SimuLean.Results.ResultScope.Downtime, "machineFail@m1");
        Check(run.Metrics["rep"] > 0 && run.Metrics["rep"] == direct[SimuLean.Results.ResultMetrics.Repairs], $"repairs as captured ({run.Metrics["rep"]})");
        Check(run.Metrics["wait"] > 0 && run.Metrics["fix"] > 0 && run.Metrics["stops"] > 0 && run.Metrics["down"] > 0, "repair waits / times and stops read");
        Check(run.Metrics["pre"] == run.Results.Find(SimuLean.Results.ResultScope.Team, "mech")[SimuLean.Results.ResultMetrics.Preemptions],
              "team preemptions as captured");
        Check(run.Metrics["inter"] > 0 && run.Metrics["jobs"] > 0, $"interruptions of the team and of a station's jobs ({run.Metrics["inter"]}, {run.Metrics["jobs"]})");

        // the downtime's config id alone reads all its targets together: counts summed, the wait over every crew
        // arrived (each target's mean weighted by its repair_waits), the repair time over every repair completed
        var all = Crew().Metric("r", "downtime:machineFail.repairs").Metric("s", "downtime:machineFail.stops")
                        .Metric("w", "downtime:machineFail.repair_wait_avg").Metric("w1", "downtime:machineFail@m1.repair_wait_avg")
                        .Metric("w2", "downtime:machineFail@m2.repair_wait_avg").Metric("r1", "downtime:machineFail@m1.repairs")
                        .Metric("r2", "downtime:machineFail@m2.repairs").Metric("s1", "downtime:machineFail@m1.stops")
                        .Metric("s2", "downtime:machineFail@m2.stops").Metric("a", "downtime:machineFail.repair_waits")
                        .Metric("a1", "downtime:machineFail@m1.repair_waits").Metric("a2", "downtime:machineFail@m2.repair_waits")
                        .Metric("t", "downtime:machineFail.repair_time_avg").Metric("t1", "downtime:machineFail@m1.repair_time_avg")
                        .Metric("t2", "downtime:machineFail@m2.repair_time_avg");
        Check(all.Check().Ok, "a downtime on several targets is a valid metric: " + Errors(all.Check()));
        var am = all.Run().Runs[0].Metrics;
        double wExpected = (am["w1"] * am["a1"] + am["w2"] * am["a2"]) / (am["a1"] + am["a2"]);
        double tExpected = (am["t1"] * am["r1"] + am["t2"] * am["r2"]) / (am["r1"] + am["r2"]);
        Check(am["r"] == am["r1"] + am["r2"] && am["s"] == am["s1"] + am["s2"] && am["a"] == am["a1"] + am["a2"]
              && Math.Abs(am["w"] - wExpected) < 1e-9 && Math.Abs(am["t"] - tExpected) < 1e-9,
              $"a downtime on several targets: repairs {am["r"]} = {am["r1"]} + {am["r2"]}, crews arrived {am["a"]}, wait {am["w"]} = {wExpected}, time {am["t"]} = {tExpected}");
        // a crew arrives before its repair ends: per target repairs <= repair_waits <= stops
        Check(am["r1"] <= am["a1"] && am["a1"] <= am["s1"] && am["r2"] <= am["a2"] && am["a2"] <= am["s2"],
              $"repairs <= crews arrived <= stops ({am["r1"]} {am["a1"]} {am["s1"]}; {am["r2"]} {am["a2"]} {am["s2"]})");
        TestRepairWaitPooling();
        check = Crew().Metric("r", "downtime:machineFail.repairz").Check();
        Check(!check.Ok && check.Errors[0].Contains("unknown statistic 'repairz'") && check.Errors[0].Contains("'repairs'"),
              "an unknown statistic of a downtime on several targets: " + Errors(check));
        check = Crew().Metric("r", "downtime:machinFail@m1.repairs").Check();
        Check(!check.Ok && check.Errors[0].Contains("no downtime 'machinFail@m1'") && check.Errors[0].Contains("'machineFail@m1'"), "unknown downtime: " + Errors(check));
        check = Crew().Metric("r", "downtime:machineFail@m1.repairz").Check();
        Check(!check.Ok && check.Errors[0].Contains("unknown statistic 'repairz'") && check.Errors[0].Contains("'repairs'"), "unknown statistic: " + Errors(check));

        // a downtime without a crew: stops and downtime_total; a repair statistic says it needs a crew
        Study Mtbf() => new Study(ReferenceModels.DowntimeModel()) { Threads = 1 }.WithRunTime(500);
        check = Mtbf().Metric("s", "downtime:fail.stops").Metric("t", "downtime:fail.downtime_total").Check();
        Check(check.Ok, "a downtime's stops: " + Errors(check));
        check = Mtbf().Metric("r", "downtime:fail.repairs").Check();
        Check(!check.Ok && check.Errors[0].Contains("repaired by a crew"), "repairs without a crew: " + Errors(check));

        // a study switches a downtime off: no stop, its statistics and the target's stops 0; on, the same runs as without
        // the factor (enabled true is the default)
        var onOff = Mtbf().WithSeed(4).Factor("downtime:fail.enabled", "true", "false")
                          .Metric("s", "downtime:fail.stops").Metric("t", "downtime:fail.downtime_total").Metric("out", "sink.input");
        check = onOff.Check();
        Check(check.Ok, "downtime:Id.enabled is a study factor: " + Errors(check));
        var oo = onOff.Run();
        var on = oo.Runs.First(r => oo.Scenarios.First(s => s.Name == r.Scenario).Parameters[0].Value == "true");
        var off = oo.Runs.First(r => oo.Scenarios.First(s => s.Name == r.Scenario).Parameters[0].Value == "false");
        Check(on.Error == null && off.Error == null && on.Metrics["s"] > 0 && off.Metrics["s"] == 0 && off.Metrics["t"] == 0,
              $"enabled: stops {on.Metrics["s"]} on, {off.Metrics["s"]} off");
        var plain = Mtbf().WithSeed(4).Metric("s", "downtime:fail.stops").Metric("out", "sink.input").Run().Runs[0];
        Check(plain.Metrics["s"] == on.Metrics["s"] && plain.Metrics["out"] == on.Metrics["out"], "enabled true runs as the model without the factor");
        Check(ModelParameterCatalog.ForDowntime(ReferenceModels.DowntimeModel().Downtimes[0]).Any(p => p.Key == "enabled"),
              "enabled is in a downtime's catalog");

        // preemption statistics of a team / station without a preemption use read 0 (nothing was ever interrupted)
        var none = new Study(ReferenceModels.ResourceSharedTeam()) { Threads = 1 }.WithRunTime(50)
            .Metric("pre", "team:team.preemptions").Metric("inter", "team:team.interruptions");
        check = none.Check();
        Check(check.Ok, "preemption statistics without preemption are valid: " + Errors(check));
        var r0 = none.Run().Runs[0];
        Check(r0.Metrics["pre"] == 0 && r0.Metrics["inter"] == 0, "and read 0");
        var p1 = new Study(ReferenceModels.ResourcePreemption()) { Threads = 1 }.WithRunTime(2000)
            .Metric("pre", "team:ops.preemptions").Metric("hit", "routine.interruptions").Metric("lost", "routine.interrupted_time");
        Check(p1.Check().Ok, "preemption metrics valid: " + Errors(p1.Check()));
        var r1 = p1.Run().Runs[0];
        Check(r1.Metrics["pre"] > 0 && r1.Metrics["hit"] > 0 && r1.Metrics["lost"] > 0, $"preemptions and interrupted jobs ({r1.Metrics["pre"]}, {r1.Metrics["hit"]})");

        // the scope list names downtime
        string error = null;
        try { Study.MetricOf("x", "failure:a.stops"); } catch (ArgumentException e) { error = e.Message; }
        Check(error != null && error.Contains("downtime"), "the scopes listed: " + error);
    }

    static void TestDiagnosisProgress()
    {
        // requests that waited 6 350 h with a crane held as long must not read "not stalled" (a shift calendar keeps
        // scheduling events): the diagnosis reports the facts and leaves the judgement to the modeller
        var frozen = SimuLean.Headless.ModelDiagnosis.Notes(8760, 2410, 12, 6350, "Robot", 6340, "Crane span 5", false);
        Check(frozen.Count == 3 && frozen[0].Contains("No item has moved since t = 2410") && frozen[0].Contains("12 item(s)")
              && frozen[0].Contains("what the design does"), "a frozen model: " + string.Join(" | ", frozen));
        Check(frozen[1].Contains("'Robot' has waited 6350") && frozen[2].Contains("'Crane span 5' has been on one task for 6340"), "request and task facts");
        Check(SimuLean.Headless.ModelDiagnosis.Notes(8760, 2410, 12, 6350, "Robot", 6340, "Crane", true).Count == 0, "a stall / deadlock verdict says it already");
        Check(SimuLean.Headless.ModelDiagnosis.Notes(1000, 990, 12, 5, "a", 8, "b", false).Count == 0, "a short pause is no note");
        Check(SimuLean.Headless.ModelDiagnosis.Notes(1000, 100, 0, double.NaN, null, double.NaN, null, false).Count == 0, "an empty model is no note");
        var d = MultiServer().Diagnose(50);
        var progress = (System.Collections.Generic.Dictionary<string, object>)d["progress"];
        Check(progress["lastMovement"] is double t && t > 40 && ((System.Collections.Generic.List<string>)d["notes"]).Count == 0,
              "a flowing model: its last movement, no note");
    }

    // Source (every 1) → Lathe Buffer (2) → Lathe (1, takes 1) → Check Buffer (2) → Check (1, takes 1) → back to Lathe
    // Buffer, with no way out (or, flowing, a sink beside the loop): the loop holds at most 6 items, then every element
    // in it waits for room in the next
    static SimulationConfig LoopModel(bool exit)
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 200 };
        ElementConfig El(string name, string type) { var e = new ElementConfig(name, type, name); c.Elements.Add(e); return e; }
        var src = El("Src", "Source");
        src.Set("mode", "InterArrival");
        src.Set("interArrivalDistribution.strategyType", "Constant");
        src.Set("interArrivalDistribution.constantValue", 1.0);
        El("Lathe Buffer", "Queue").Set("capacity", 2);
        foreach (var name in new[] { "Lathe", "Check" })
        {
            var m = El(name, "MultiServer");
            m.Set("capacity", 1);
            m.Set("delayStrategy.strategyType", "Constant");
            m.Set("delayStrategy.constantValue", exit ? 0.4 : 1.0);
        }
        El("Check Buffer", "Queue").Set("capacity", 2);
        void Connect(string from, string to) => c.Connections.Add(new ConnectionConfig(from, to, "General"));
        Connect("Src", "Lathe Buffer"); Connect("Lathe Buffer", "Lathe"); Connect("Lathe", "Check Buffer");
        Connect("Check Buffer", "Check"); Connect("Check", "Lathe Buffer");
        if (exit) { El("Exit", "Sink"); Connect("Check", "Exit"); }
        return c;
    }

    static void TestDiagnosisCircularWait()
    {
        var d = new Study(LoopModel(false)) { Threads = 1 }.WithRunTime(200).Diagnose(0);
        var notes = (List<string>)d["notes"];
        var circular = notes.Where(n => n.StartsWith("Circular wait:")).ToList();
        Check((bool)d["deadlocked"], "the closed loop deadlocks");
        Check(circular.Count == 1 && circular[0].Contains("Lathe Buffer → Lathe → Check Buffer → Check → Lathe Buffer"),
              "the loop is named once, in flow order, back to its start: " + string.Join(" | ", notes));
        var flowing = new Study(LoopModel(true)) { Threads = 1 }.WithRunTime(200).Diagnose(50);
        Check(!((List<string>)flowing["notes"]).Any(n => n.StartsWith("Circular wait:")), "a loop that flows has no circular wait: " +
              string.Join(" | ", (List<string>)flowing["notes"]));
        Check(SimuLean.Headless.ModelDiagnosis.CircularWaitNote(new List<string> { "A", "B", "A" }).StartsWith("Circular wait: A → B → A"), "the note's form");
    }

    static void TestAmbiguousNames()
    {
        // two queues named "Buffer" (ids differ, as a scene's Type_Name_instanceId do)
        var config = ReferenceModels.BasicLine();
        var q = config.Elements.Find(e => e.Name == "q");
        foreach (var id in new[] { "buffer_a", "buffer_b" })
        {
            var twin = ConfigCloner.DeepClone(q);
            twin.Id = id; twin.Name = "Buffer";
            config.Elements.Add(twin);
        }
        var check = new Study(config) { Threads = 1 }.WithRunTime(50).Factor("Buffer.capacity", 5).Check();
        Check(!check.Ok && check.Errors[0].Contains("2 elements are named 'Buffer'"), "a factor on a shared name: " + string.Join(" | ", check.Errors));
        check = new Study(config) { Threads = 1 }.WithRunTime(50).Metric("w", "Buffer.content_avg").Check();
        Check(!check.Ok && check.Errors[0].Contains("named 'Buffer'"), "a metric on a shared name: " + string.Join(" | ", check.Errors));
        check = new Study(config) { Threads = 1 }.WithRunTime(50).Metric("w", "buffer_b.content_avg").Check();
        Check(check.Ok, "an id still resolves: " + string.Join(" | ", check.Errors));
        check = new Study(config) { Threads = 1 }.WithRunTime(50).Metric("out", "sink.input").Check();
        Check(check.Ok, "a unique name still resolves: " + string.Join(" | ", check.Errors));
    }

    static void TestModelDescription()
    {
        int described = 0;
        foreach (var kv in ReferenceModels.All())
        {
            try { ModelDescription.Of(kv.Value); described++; }
            catch (Exception ex) { Check(false, kv.Key + ": " + ex.Message); }
        }
        Check(described == ReferenceModels.All().Count, $"every reference model described ({described})");
        var d = ModelDescription.Of(ReferenceModels.ResourceSharedTeam());
        var elements = (System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)d["elements"];
        var q = elements.Find(e => (string)e["name"] == "q");
        Check(q != null && (string)q["type"] == "Queue", "an element by name, its type without 'Unity'");
        CollectionAssertEqual((System.Collections.Generic.List<string>)q["next"], new[] { "a", "b", "c" }, "next elements in connection order");
        var a = elements.Find(e => (string)e["name"] == "a");
        var pa = (System.Collections.Generic.SortedDictionary<string, string>)a["parameters"];
        Check(pa["capacity"] == "1" && pa["delayStrategy.strategyType"] == "Uniform" && pa["delayStrategy.uniformMax"] == "2", "parameters under their catalog keys: " + string.Join(", ", pa));
        var teams = (System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)d["teams"];
        CollectionAssertEqual((System.Collections.Generic.List<string>)teams[0]["members"], new[] { "Team_1", "Team_2" }, "the team's members");
        var clock = (System.Collections.Generic.Dictionary<string, object>)d["clock"];
        Check((string)clock["timeUnit"] == "s" && (double)clock["horizon"] == 2000, "the clock: unit and horizon");
        var one = ModelDescription.Of(ReferenceModels.ResourceSharedTeam(), parameters: false, only: "slow");
        var oneElements = (System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)one["elements"];
        Check(oneElements.Count == 1 && !oneElements[0].ContainsKey("parameters") && one.ContainsKey("clock"), "one element, structure only; the clock kept");
        string error = null;
        try { ModelDescription.Of(ReferenceModels.ResourceSharedTeam(), only: "slwo"); } catch (ArgumentException ex) { error = ex.Message; }
        Check(error != null && error.Contains("'slow'"), "an unknown name suggests: " + error);
    }

    static void TestDowntimeDescription()
    {
        var c = ReferenceModels.BasicLine();
        var plan = new DowntimeConfig("Plan", "Timetable", "srv");
        plan.Settings.StateName = "PLANNED";
        plan.Settings.Rows = new System.Collections.Generic.List<DowntimeIntervalSettings>();
        for (int i = 0; i < 10; i++) plan.Settings.Rows.Add(DowntimeIntervalSettings.Of(i == 0 ? "srv" : "", 100 * i, 30, mode: i == 1 ? SimuLean.StopMode.AfterCurrent : (SimuLean.StopMode?)null));
        c.Downtimes.Add(plan);
        var d = ModelDescription.Of(c, only: "Plan");
        Check(!d.ContainsKey("elements"), "one downtime: no empty element list (" + string.Join(", ", d.Keys) + ")");
        var downtime = ((System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)d["downtimes"])[0];
        var p = (System.Collections.Generic.SortedDictionary<string, string>)downtime["parameters"];
        Check(p.TryGetValue("stateName", out string state) && state == "PLANNED", "its state: " + string.Join(", ", p));
        var rows = (System.Collections.Generic.List<string>)downtime["rows"];
        Check(rows.Count == ModelDescription.LongList + 1 && rows[0] == "0+30 srv" && rows[1] == "100+30 AfterCurrent" && rows.Last() == "… 2 more (full lists them)",
              "the rows, capped: " + string.Join(" | ", rows));
        var all = (System.Collections.Generic.List<string>)((System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)ModelDescription.Of(c, only: "Plan", full: true)["downtimes"])[0]["rows"];
        Check(all.Count == 10, "full: every row");
        Check(ModelDescription.Of(c).ContainsKey("elements"), "the whole model keeps its elements");
    }

    // docs/agents/topics/warehouse.md gave the replenisher the pick face's statistics
    // (loads_received / loads_ordered) and the milk run the sequencing station's (carriers). The doc-symbol check only asks
    // that a name exists somewhere in the code; this one runs every reference model, collects the statistic names each
    // engine type / scope really reports (RunResults.Capture) and checks every statistic quoted in a "Statistics …" or
    // "Metric paths …" sentence of docs/agents/** against them: a scoped path (team:, list:, downtime:, reaction:, model.)
    // against its scope, a bare name against the element type the paragraph (or its section) names as `UnityX`.
    static void TestDocStatistics()
    {
        var byType = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>(StringComparer.Ordinal);
        var byScope = new System.Collections.Generic.Dictionary<SimuLean.Results.ResultScope, System.Collections.Generic.HashSet<string>>();
        foreach (SimuLean.Results.ResultScope s in Enum.GetValues(typeof(SimuLean.Results.ResultScope)))
            byScope[s] = new System.Collections.Generic.HashSet<string>(SimuLean.Results.ResultMetrics.Of(s).Select(Base), StringComparer.Ordinal);
        foreach (var kv in ReferenceModels.All())
        {
            var cfg = kv.Value;
            var clock = new SimuLean.SimClock(cfg.Seed);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(cfg); model.StartModel(); clock.AdvanceClock(cfg.MaxSimulationTime);
            foreach (var o in SimuLean.Results.RunResults.Capture(model, kv.Key).Objects)
                foreach (var m in o.Metrics)
                {
                    byScope[o.Scope].Add(Base(m.Key));
                    if (o.Scope != SimuLean.Results.ResultScope.Element) continue;
                    if (!byType.TryGetValue(o.Type, out var set)) byType[o.Type] = set = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    set.Add(Base(m.Key));
                }
        }
        // names two engine types or more report (input, setups, carriers_emptied…) are any element's; one type's own are its own
        var generic = new System.Collections.Generic.HashSet<string>(byType.Values.SelectMany(v => v).GroupBy(n => n)
            .Where(g => g.Count() >= 2).Select(g => g.Key).Concat(SimuLean.Results.ResultMetrics.Of(SimuLean.Results.ResultScope.Element).Select(Base)), StringComparer.Ordinal);
        var elementAll = new System.Collections.Generic.HashSet<string>(byType.Values.SelectMany(v => v).Concat(byScope[SimuLean.Results.ResultScope.Element])
            .Concat(byScope[SimuLean.Results.ResultScope.Resource]), StringComparer.Ordinal);

        string root = AppContext.BaseDirectory;
        while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root, "CLAUDE.md"))) root = System.IO.Path.GetDirectoryName(root.TrimEnd('\\', '/'));
        Check(root != null, "repository root found");
        if (root == null) return;
        var tick = new System.Text.RegularExpressions.Regex("`([^`\r\n]+)`");
        var unityType = new System.Text.RegularExpressions.Regex(@"`Unity([A-Z][A-Za-z]+)`");
        var statSentence = new System.Text.RegularExpressions.Regex(@"(Statistics?|Metric paths)\b.*?(?=(?<=[.;])\s+[A-Z*]|$)", System.Text.RegularExpressions.RegexOptions.Singleline);
        var scoped = new System.Text.RegularExpressions.Regex(@"^(?<scope>team|list|downtime|reaction|resource):[^.\s]+\.(?<stat>[a-z][a-z0-9_]*(:\S+)?)$");
        var owned = new System.Text.RegularExpressions.Regex(@"^(?<owner>[A-Za-z][A-Za-z0-9_<>]*)\.(?<stat>[a-z][a-z0-9_]*(:\S+)?)$");
        var bare = new System.Text.RegularExpressions.Regex(@"^[a-z][a-z0-9]*(_[a-z0-9]+)*(:\S+)?$");
        int checkedNames = 0;
        foreach (string file in System.IO.Directory.EnumerateFiles(System.IO.Path.Combine(root, "docs", "agents"), "*.md", System.IO.SearchOption.AllDirectories))
        {
            string doc = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            string sectionType = null;
            foreach (var (line, para) in Paragraphs(System.IO.File.ReadAllLines(file)))
            {
                if (para.StartsWith("#")) { sectionType = null; continue; }
                var own = unityType.Matches(para).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).FirstOrDefault(t => byType.ContainsKey(t));
                string type = own ?? sectionType;
                foreach (System.Text.RegularExpressions.Match s in statSentence.Matches(para))
                    foreach (System.Text.RegularExpressions.Match t in tick.Matches(s.Value))
                    {
                        string token = t.Groups[1].Value.Trim();
                        string problem = null;
                        var sc = scoped.Match(token);
                        var ow = owned.Match(token);
                        if (sc.Success)
                        {
                            var scope = sc.Groups["scope"].Value switch
                            {
                                "team" => SimuLean.Results.ResultScope.Team, "list" => SimuLean.Results.ResultScope.List,
                                "downtime" => SimuLean.Results.ResultScope.Downtime, "reaction" => SimuLean.Results.ResultScope.Reaction,
                                _ => SimuLean.Results.ResultScope.Resource,
                            };
                            if (!Known(sc.Groups["stat"].Value, byScope[scope])) problem = $"no {scope} statistic '{sc.Groups["stat"].Value}'";
                        }
                        else if (ow.Success)
                        {
                            string owner = ow.Groups["owner"].Value, stat = ow.Groups["stat"].Value;
                            if (owner == "model") { if (!Known(stat, byScope[SimuLean.Results.ResultScope.Model])) problem = $"no model statistic '{stat}'"; }
                            else if (char.IsUpper(owner[0]) && !Known(stat, elementAll)) problem = $"no element statistic '{stat}'";
                            else if (!char.IsUpper(owner[0])) continue;   // a key path (processBy.priority), not a metric
                        }
                        else if (bare.IsMatch(token))
                        {
                            var allowed = type != null ? new System.Collections.Generic.HashSet<string>(byType[type].Concat(generic)) : elementAll;
                            if (!Known(token, allowed)) problem = type != null ? $"{type} reports no statistic '{token}' (it reports {string.Join(", ", byType[type].OrderBy(x => x))})" : $"no element statistic '{token}'";
                        }
                        else continue;
                        checkedNames++;
                        Check(problem == null, $"{doc}:{line}: `{token}` — {problem}");
                    }
                if (own != null) sectionType = own;
            }
        }
        Check(checkedNames > 40, $"statistic names checked in docs/agents ({checkedNames})");
        // self-test: names that are easy to confuse are caught
        Check(!byType["BaseStockReplenisher"].Contains("loads_received") && !generic.Contains("loads_received"), "loads_received is not the replenisher's");
        Check(!byType["MilkRun"].Contains("carriers") && !generic.Contains("carriers"), "carriers is not the milk run's");
    }

    static void TestMemberStopInfo()
    {
        var c = ReferenceModels.ResourcePrimitives();
        Check(Study.MemberStopsNotInterrupting(c).Count == 0, "no downtime on a team: no line");
        var d = new DowntimeConfig { Id = "opFail", Kind = "MtbfMttr" };
        d.TargetIds.Add("ops");
        c.Downtimes.Add(d);
        var lines = Study.MemberStopsNotInterrupting(c);
        Check(lines.Count == 1 && lines[0].Contains("'cut'") && lines[0].Contains("'opFail'") && lines[0].Contains("preemption = None"),
              "the station using the team without a mode is named: " + string.Join(" | ", lines));
        c.Elements.Find(x => x.Id == "cut").Set("processBy.preemption", "Resume");
        Check(Study.MemberStopsNotInterrupting(c).Count == 0, "with a mode (Resume): no line");
        // the driver team of a two-team job counts too (a requirement's own team)
        d.TargetIds[0] = "drivers";
        var two = Study.MemberStopsNotInterrupting(c);
        Check(two.Count == 2 && two.Any(l => l.Contains("'lift'")) && two.Any(l => l.Contains("'haul'")), "the lift (its driver) and the haul: " + string.Join(" | ", two));
    }

    static void TestNetworkDescription()
    {
        var c = ReferenceModels.TransportModel();
        var d = ModelDescription.Of(c);
        var net = d.TryGetValue("network", out var n) ? n as System.Collections.Generic.Dictionary<string, object> : null;
        Check(net != null, "the whole model has its network: " + string.Join(", ", d.Keys));
        if (net == null) return;
        var nodes = ((System.Collections.IEnumerable)net["nodes"]).Cast<object>().Select(x => x.ToString()).ToList();
        var tracks = ((System.Collections.IEnumerable)net["tracks"]).Cast<object>().Select(x => x.ToString()).ToList();
        var teams = ((System.Collections.IEnumerable)net["teams"]).Cast<object>().Select(x => x.ToString()).ToList();
        Check(nodes.Contains("n1 (0, 0): q1") && nodes.Contains("n2 (10, 0): q2"), "nodes with x, z and the elements there: " + string.Join(" | ", nodes));
        Check(tracks.Count == 1 && tracks[0] == "n1 – n2: 10", "a two-way track with its length: " + string.Join(" | ", tracks));
        Check(teams.Count == 1 && teams[0].StartsWith("agv (Network") && teams[0].Contains("home n1"), "the team on it: " + string.Join(" | ", teams));
        var alone = ModelDescription.Of(c, only: "network");
        Check(alone.ContainsKey("network") && !alone.ContainsKey("elements"), "only=network: the network alone");
        Check(ModelDescription.Of(c, only: "n2").ContainsKey("network"), "only = one of its nodes: the network");
        // a track without a length: the straight distance; one-way
        c.Network.Tracks[0].Length = -1;
        c.Network.Tracks[0].Bidirectional = false;
        var t2 = ((System.Collections.IEnumerable)((System.Collections.Generic.Dictionary<string, object>)ModelDescription.Of(c)["network"])["tracks"]).Cast<object>().First().ToString();
        Check(t2 == "n1 → n2: 10", "no length = the distance, one-way →: " + t2);
        Check(!ModelDescription.Of(ReferenceModels.BasicLine()).ContainsKey("network"), "a model without nodes has no network");
    }

    static void TestExponentialMeans()
    {
        var lines = Study.ExponentialMeans(ReferenceModels.BasicLine());
        Check(lines.Count == 1 && lines[0].Contains("srv.") && lines[0].Contains("mean 0.8 (rate 1.25)") && lines[0].Contains("ExponentialMean~x a mean"),
              "the server's exponential by its mean: " + string.Join(" | ", lines));
        var check = new Study(ReferenceModels.BasicLine()) { Threads = 1 }.WithRunTime(100).Check();
        Check(check.Info.Any(i => i.Contains("mean 0.8 (rate 1.25)")), "in validate's info: " + string.Join(" | ", check.Info));
        var none = ReferenceModels.BasicLine();
        none.Elements.RemoveAll(e => e.Id == "srv" || e.Id == "src");
        Check(Study.ExponentialMeans(none).Count == 0, "no exponential: no line");
    }

    static void TestShapeScaleMeans()
    {
        var c = ReferenceModels.BasicLine();
        var srv = c.Elements.Find(e => e.Id == "srv");
        srv.Set("delayStrategy.strategyType", "LogNormal"); srv.Set("delayStrategy.logNormalMu", 1.0); srv.Set("delayStrategy.logNormalSigma", 0.5);
        var lines = Study.ShapeScaleMeans(c);
        Check(lines.Count == 1 && lines[0].Contains("srv.delayStrategy LogNormal~1~0.5 mean 3.0802") && lines[0].Contains("of ln x"), "a LogNormal by its mean: " + string.Join(" | ", lines));
        srv.Set("delayStrategy.strategyType", "Gamma"); srv.Set("delayStrategy.gammaShape", 2.0); srv.Set("delayStrategy.gammaScale", 3.0);
        Check(Study.ShapeScaleMeans(c)[0].Contains("Gamma~2~3 mean 6"), "a Gamma: shape × scale");
        srv.Set("delayStrategy.strategyType", "Weibull"); srv.Set("delayStrategy.weibullShape", 1.0); srv.Set("delayStrategy.weibullScale", 4.0);
        Check(Study.ShapeScaleMeans(c)[0].Contains("Weibull~1~4 mean 4"), "a Weibull of shape 1: its scale");
        var check = new Study(c).WithRunTime(100).Check();
        Check(check.Info.Any(i => i.Contains("Weibull~1~4 mean 4")), "in validate's info: " + string.Join(" | ", check.Info));
        Check(Study.ShapeScaleMeans(ReferenceModels.BasicLine()).Count == 0, "none: no line");
    }

    // a statistic's name without its argument (state:IDLE → state, tally:x:p90 → tally, delivered:FlowA → delivered)
    static string Base(string metric) { int i = metric.IndexOf(':'); return i < 0 ? metric : metric.Substring(0, i); }
    static bool Known(string stat, System.Collections.Generic.ICollection<string> names)
        => names.Contains(stat) || names.Contains(Base(stat)) || Base(stat) == "state" || Base(stat) == "tally" || Base(stat) == "property";

    // paragraphs of a markdown file with their first line: a list item, a table row and a heading are paragraphs of their own
    static System.Collections.Generic.List<(int, string)> Paragraphs(string[] lines)
    {
        var result = new System.Collections.Generic.List<(int, string)>();
        var para = new System.Text.StringBuilder(); int start = 0; bool fence = false;
        void Flush() { if (para.Length > 0) result.Add((start, para.ToString().Trim())); para.Clear(); }
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i];
            if (l.TrimStart().StartsWith("```")) { Flush(); fence = !fence; continue; }
            if (fence) continue;
            string tr = l.TrimStart();
            bool block = l.Trim().Length == 0 || tr.StartsWith("|") || tr.StartsWith("#") || System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*([-*]|\d+\.)\s");
            if (block) Flush();
            if (l.Trim().Length == 0) continue;
            if (para.Length == 0) start = i + 1;
            para.Append(' ').Append(l.Trim());
            if (tr.StartsWith("|") || tr.StartsWith("#")) Flush();
        }
        Flush();
        return result;
    }

    static void CollectionAssertEqual(System.Collections.Generic.List<string> got, string[] expected, string what)
        => Check(got != null && got.SequenceEqual(expected), what + ": " + (got == null ? "null" : string.Join(", ", got)));

    static void TestVocabulary()
    {
        var v = Vocabulary.Describe();
        foreach (string part in new[] { "arguments", "retired", "scopes", "paths", "statistics", "states", "expressions", "options", "catalog" })
            Check(v.ContainsKey(part), "part " + part);
        // one concept per argument name, the canonical names present, the retired ones not
        var args = Vocabulary.Concepts.SelectMany(c => c.Arguments).ToList();
        Check(args.Count == args.Distinct().Count(), "an argument is in one concept only: " + string.Join(", ", args.GroupBy(a => a).Where(g => g.Count() > 1).Select(g => g.Key)));
        foreach (string a in new[] { "object", "objects", "newName", "model", "asset", "factors", "resultSet", "horizon", "until", "warmup", "noProgress", "xAxis", "yAxis" })
            Check(Vocabulary.ConceptOf(a) != null, "canonical argument " + a);
        foreach (string a in new[] { "element", "source", "targets", "target", "set", "maxTime", "runTime" })
            Check(Vocabulary.ConceptOf(a) == null && Vocabulary.Retired.ContainsKey(a), "retired argument " + a);
        // scopes from the enums, the path grammar with an example each
        var scopes = (System.Collections.Generic.Dictionary<string, object>)v["scopes"];
        Check(Strings(scopes["factor"]).Contains("data") && Strings(scopes["metric"]).Contains("downtime"), "factor and metric scopes");
        Check(Vocabulary.PathGrammar.All(p => !string.IsNullOrEmpty(p.example)), "every path form has an example");
        // the expression built-ins resolve on an item and an element (the lists are the ones the scope answers)
        var clock = new SimuLean.SimClock(1);
        var item = new SimuLean.Item(0); item.SetId("part", 1, 0);
        var q = new SimuLean.ItemsQueue(3, "Q", clock);
        foreach (string b in SimuLean.Expressions.EntityScope.ItemBuiltIns)
            Check(!SimuLean.Expressions.EntityScope.OfItem(item, b, 1).Missing, "item built-in " + b);
        foreach (string b in SimuLean.Expressions.EntityScope.ElementBuiltIns)
            Check(!SimuLean.Expressions.EntityScope.OfElement(q, b).Missing, "element built-in " + b);
        Check(SimuLean.Expressions.Expression.FunctionArities["pick"].min == 2 && SimuLean.Expressions.Expression.FunctionArities.ContainsKey("table"), "functions with arities");
        // states: declared (static fields) and a built model's
        var declared = Vocabulary.DeclaredStates();
        Check(declared.Contains("IDLE") && declared.Contains("TRAVELING_EMPTY") && declared.Contains("OFF_SHIFT"), "declared states: " + string.Join(", ", declared));
        var withModel = (System.Collections.Generic.Dictionary<string, object>)Vocabulary.Describe(ReferenceModels.All().First().Value)["states"];
        Check(withModel.ContainsKey("model") && !withModel.ContainsKey("modelError"), "a model's states");
        // catalog keys with their rule: team keys camelCase, reaction actions once
        var catalog = (System.Collections.Generic.Dictionary<string, object>)v["catalog"];
        var team = ((System.Collections.Generic.List<object>)catalog["team"]).Cast<System.Collections.Generic.Dictionary<string, object>>().ToList();
        Check(team.Any(e => (string)e["key"] == "speed") && team.All(e => char.IsLower(((string)e["key"])[0])), "team keys camelCase");
        var reaction = ((System.Collections.Generic.List<object>)catalog["reaction"]).Cast<System.Collections.Generic.Dictionary<string, object>>().Select(e => (string)e["key"]).ToList();
        Check(reaction.Contains("actions[].kind") && !reaction.Any(k => k.StartsWith("actions[0].") || k.StartsWith("actions[1].")), "reaction action keys once (actions[].…)");
        Check(Strings(((System.Collections.Generic.Dictionary<string, object>)v["options"])["runEndReason"]).Contains("NoProgress"), "option enums");
    }

    static System.Collections.Generic.List<string> Strings(object list) => ((System.Collections.IEnumerable)list).Cast<object>().Select(o => o.ToString()).ToList();

}
