// Standalone tests of the engine side of the agent interface: a study checked on
// the built model before it runs (unknown / ambiguous targets, statistics and states with "did you mean", factors on an
// unused distribution, unbuildable levels) and name lookups that fail on two objects with one name.
// Run: dotnet run  (from StandaloneTests/agentinterfacetests)
using System;
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
        Run("A metric on a team member says which team to read; scope 'resource' points to the team (CASE2 v2 run)", TestMemberMetrics);
        Run("Diagnosis reports progress: last movement, oldest request, longest task; notes on a model frozen for long (facts, no verdict)", TestDiagnosisProgress);
        Run("Two elements with one name: factors and metrics fail instead of taking the first", TestAmbiguousNames);
        Run("ModelDescription: the whole model by name, every reference model; one object; an unknown name suggests", TestModelDescription);
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

    static Study MultiServer() => new Study(ReferenceModels.MultiServer3()) { Threads = 1 }.WithRunTime(100);

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
        config.Elements.Find(e => e.Name == "ms").Parameters["uniformMin"] = 0.2;
        var check = new Study(config) { Threads = 1 }.WithRunTime(100).Factor("ms.uniformMin", 0.5, 1).Metric("out", "sink.input").Check();
        Check(check.Ok && check.Warnings.Count == 1 && check.Warnings[0].Contains("Triangular"),
              "a factor on Uniform keys of a Triangular service time: warned: " + string.Join(" | ", check.Warnings));
        check = MultiServer().Factor("ms.uniformMin", 0.5, 1).Metric("out", "sink.input").Check();
        Check(!check.Ok && check.Errors[0].Contains("belongs to another distribution") && check.Errors[0].Contains("Triangular"),
              "the key not in the config (a scene exports the chosen distribution only): an error that says why: " + string.Join(" | ", check.Errors));
        check = MultiServer().Factor("ms.triangularMax", 2, 3).Metric("out", "sink.input").Check();
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

    static void TestDiagnosisProgress()
    {
        // CASE2 v2 run: requests waited 6 350 h and a crane was held as long, yet the diagnosis said "not stalled" (the shift
        // calendar kept scheduling events); it now reports the facts and leaves the judgement to the modeller
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
        Check(pa["capacity"] == "1" && pa["delayStrategyType"] == "Uniform" && pa["uniformMax"] == "2", "parameters under their catalog keys: " + string.Join(", ", pa));
        var teams = (System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)d["teams"];
        CollectionAssertEqual((System.Collections.Generic.List<string>)teams[0]["members"], new[] { "Team_1", "Team_2" }, "the team's members");
        var clock = (System.Collections.Generic.Dictionary<string, object>)d["clock"];
        Check((string)clock["timeUnit"] == "s" && (double)clock["horizon"] == 2000, "the clock: unit and horizon");
        var one = ModelDescription.Of(ReferenceModels.ResourceSharedTeam(), parameters: false, only: "slow");
        var oneElements = (System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>)one["elements"];
        Check(oneElements.Count == 1 && !oneElements[0].ContainsKey("parameters") && !one.ContainsKey("clock"), "one element, structure only");
        string error = null;
        try { ModelDescription.Of(ReferenceModels.ResourceSharedTeam(), only: "slwo"); } catch (ArgumentException ex) { error = ex.Message; }
        Check(error != null && error.Contains("'slow'"), "an unknown name suggests: " + error);
    }

    static void CollectionAssertEqual(System.Collections.Generic.List<string> got, string[] expected, string what)
        => Check(got != null && got.SequenceEqual(expected), what + ": " + (got == null ? "null" : string.Join(", ", got)));
}
