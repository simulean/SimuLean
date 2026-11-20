// Standalone tests of the out-of-editor runner: the model exchange format (ConfigJson) — every reference
// model round-trips exactly (same JSON, same results bit for bit), hand-written JSON needs no type tags, and unknown
// formats, kinds, members and types fail naming them.
// Run: dotnet run (from StandaloneTests/runnertests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Data;
using SimuLean.Optimization;
using SimuLean.Results;
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

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception e) { failed++; Console.WriteLine("  FAIL (exception): " + e); }
    }

    static int Main()
    {
        Run("ConfigJson: every reference model round-trips (same JSON, same results bit for bit)", TestReferenceModelsRoundTrip);
        Run("ConfigJson: typed connections and downtimes round-trip; unknown members fail", TestBagTypes);
        Run("ConfigJson: unknown format, kind, member and type fail naming them", TestErrors);
        Run("StudySpec: a study file round-trips; a model file alone is a study of one run", TestStudyFile);
        Run("StudySpec: set, metrics, scenarios, where, derived — the same results as the Study API; the answer", TestStudySpecRuns);
        Run("StudySpec: a Queue a run filled at the default capacity is noted in warnings; no number changes", TestDefaultCapacityNote);
        Run("StudySpec: min / max over the replications with detail=full; compareTo gives paired differences on common seeds", TestPairedAndRange);
        Run("StudySpec: the model's own warnings travel in the results and the answer gives each cause once, capped", TestModelWarnings);
        Run("StudySpec: metrics of a Source / Sink that are no KPIs, an expression as a metric, a constant derived metric, a range as a level are flagged", TestStudyMetricNotes);
        Run("StudySpec: factor levels echoed typed; a factor whose every level gives the same results is noted", TestStudyLevelNotes);
        Run("StudySpec: end reasons counted, the model block when a run stalled, every digit and every run with detail=full", TestCompactAnswer);
        Run("AnswerPages: a large answer is a table that says how to read the rest; walking its pages gives the whole answer", TestAnswerPages);
        Run("Vocabulary: an index of the sections; the sections together are the whole vocabulary", TestVocabularySections);
        Run("StudyInputs: JSON and shorthand factors / metrics; bad input fails naming it", TestStudyInputs);
        Run("Runner: the build's engine fingerprint is the sources' (both algorithms agree); version", TestFingerprint);
        Run("Runner: run / validate a study file — the answer as the engine's, the format and engine in the envelope", TestRunnerRun);
        Run("Runner: downtime / preemption metrics and the 'no progress' end condition through a study file", TestRunnerDowntimeMetricsAndNoProgress);
        Run("Runner: bad commands, options, files and studies answer kind error with exit code 2", TestRunnerErrors);
        Run("ResultSetJson: a whole result set round-trips (series, timeline, candidates, extra tables, NaN, the date)", TestResultSetJson);
        Run("StudyBatch: the scenarios applied, from a file, run as the study; a level that fails keeps its error; the runner", TestStudyBatch);
        Run("BatchedObjective: an optimisation by batches (as to the runner) makes the same search as the classic one", TestBatchedObjective);
        Run("Runner --stream: each run on its own line as it ends, the set last; reassembled it is the study's", TestStreaming);
        Run("Runner serve: the editor's session — batches by id on the standard input, errors answered, exit", TestServe);
        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void TestReferenceModelsRoundTrip()
    {
        foreach (var kv in ReferenceModels.All())
        {
            string json = ConfigJson.Serialize(kv.Value);
            SimulationConfig back = ConfigJson.Deserialize(json);
            string again = ConfigJson.Serialize(back);
            Check(again == json, $"{kv.Key}: the JSON of the read model is the same");
            var a = ReferenceModelRunner.ToJson(ReferenceModelRunner.Run(kv.Key, kv.Value));
            var b = ReferenceModelRunner.ToJson(ReferenceModelRunner.Run(kv.Key, back));
            Check(a == b, $"{kv.Key}: the read model gives the same results");
        }
    }

    static void TestBagTypes()
    {
        // a connection's typed fields
        var c = new SimulationConfig();
        c.Connections.Add(new ConnectionConfig("L", "cmb", "PullFromList") { Input = 1, Query = "sku == P|age desc" });
        c.Connections.Add(new ConnectionConfig("sep", "out", "PortOutput") { Port = "empties" });
        var back = ConfigJson.Deserialize(ConfigJson.Serialize(c)).Connections;
        Check(back[0].Input == 1 && back[0].Query == "sku == P|age desc" && back[0].Port == null && back[1].Port == "empties" && back[1].Input == null,
              "a connection's port, input and query round-trip");

        // a downtime's typed settings: written under their paths, an unknown one refused
        var dc = new SimulationConfig();
        var dt = new DowntimeConfig("dt", "Timetable", "m");
        dt.Set("timeToFailure", "ExponentialMean~60"); dt.Set("mode", "AfterCurrent"); dt.Set("periodicUntil", 5.0);
        dt.Set("rows", new List<DowntimeIntervalSettings> { DowntimeIntervalSettings.Of("m", 10, 5, code: "PM") });
        dt.Set("repairBy.team", "mech");
        dc.Downtimes.Add(dt);
        var dback = ConfigJson.Deserialize(ConfigJson.Serialize(dc)).Downtimes[0];
        Check(dback.Get<SamplerSettings>("timeToFailure")?.ToString() == "ExponentialMean~60" && dback.Settings.Mode == StopMode.AfterCurrent
              && dback.Settings.PeriodicUntil == 5.0 && dback.Settings.Rows[0].Code == "PM" && dback.Settings.RepairBy.Team == "mech",
              "a downtime's typed settings round-trip");
        string derr = null;
        try { ConfigJson.Deserialize("{\"format\":2,\"kind\":\"model\",\"model\":{\"Downtimes\":[{\"Settings\":{\"ttf\":1}}]}}"); }
        catch (FormatException ex) { derr = ex.Message; }
        Check(derr != null && derr.Contains("ttf"), "an unknown downtime setting fails: " + derr);

        // an element's typed settings: written under their paths, read as the type's settings
        string hand = "{\"format\":2,\"kind\":\"model\",\"model\":{\"MaxSimulationTime\":100,\"Elements\":[" +
                      "{\"Id\":\"q\",\"Type\":\"Queue\",\"Name\":\"q\",\"Settings\":{\"capacity\":4,\"offerScan\":2.0}}," +
                      "{\"Id\":\"c\",\"Type\":\"Combiner\",\"Name\":\"c\",\"Settings\":{\"requirements\":[1,2],\"delayStrategy\":{\"strategyType\":\"Uniform\",\"uniformMin\":1,\"uniformMax\":3}}}]}}";
        var hc = ConfigJson.Deserialize(hand);
        Check(hc.MaxSimulationTime == 100 && hc.Elements.Count == 2, "a hand-written model reads");
        var hp = hc.Elements[0];
        Check(hp.Settings is QueueSettings qs && qs.Capacity == 4 && qs.OfferScan == 2, "the settings are the type's, an integer where one is wanted");
        Check(hc.Elements[1].Get<int[]>("requirements", null)?.SequenceEqual(new[] { 1, 2 }) == true, "[1, 2] is an int[]");
        Check(hc.Elements[1].Get<double>("delayStrategy.uniformMax", 0) == 3, "a nested group by its path");
        Check(ConfigJson.Serialize(ConfigJson.Deserialize(ConfigJson.Serialize(hc))) == ConfigJson.Serialize(hc), "typed settings round-trip");
        try { ConfigJson.Deserialize(hand.Replace("capacity", "capacty")); Check(false, "an unknown setting fails"); }
        catch (FormatException x) { Check(x.Message.Contains("capacty") && x.Message.Contains("capacity") && x.Message.Contains("'q'"), "an unknown setting names the element and the closest one: " + x.Message); }
        try { new ElementConfig("q", "Queue", "q").Set("delayStrategy.constantValue", 2); Check(false, "Set of an undeclared path fails"); }
        catch (ArgumentException x) { Check(x.Message.Contains("delayStrategy.constantValue"), "Set names the path: " + x.Message); }
        try { ConfigJson.Deserialize(hand.Replace("\"format\":2", "\"format\":1")); Check(false, "format 1 refused"); }
        catch (FormatException x) { Check(x.Message.Contains("older"), "an older format is refused: " + x.Message); }
    }

    static void TestStudyFile()
    {
        var model = ReferenceModels.All().First(m => m.Key == "basic_line").Value;
        var spec = new StudySpec { Horizon = 500, Replications = 3, Seed = 5, Factors = "{\"buffer.capacity\": [2, 4]}", Metrics = "out=sink.input", Detail = "metrics" };
        string json = StudySpec.Serialize(model, spec);
        var (m2, s2) = StudySpec.Deserialize(json);
        Check(StudySpec.Serialize(m2, s2) == json, "the study file round-trips");
        Check(s2.Replications == 3 && s2.Seed == 5 && s2.Factors == spec.Factors && s2.Detail == "metrics", "the spec's fields");
        var (m3, s3) = StudySpec.Deserialize(ConfigJson.Serialize(model));
        Check(m3.Elements.Count == model.Elements.Count && s3.Replications == 1 && s3.Horizon == -1, "a model file: the defaults");
        try { StudySpec.Deserialize("{\"format\":2,\"kind\":\"study\",\"model\":{},\"study\":{\"Replicas\":3}}"); Check(false, "unknown spec member"); }
        catch (FormatException e) { Check(e.Message.Contains("Replicas"), "an unknown spec member is named: " + e.Message); }
    }

    static void TestStudySpecRuns()
    {
        var model = ReferenceModels.All().First(m => m.Key == "basic_line").Value;
        string q = model.Elements.First(e => e.Type == "Queue").Name, k = model.Elements.First(e => e.Type == "Sink").Name;
        var spec = new StudySpec
        {
            Horizon = 300, Replications = 2, Seed = 3,
            Factors = "{\"" + q + ".capacity\": [1, 3, 5]}", Where = q + "_capacity > 1",
            Metrics = "{\"out\": \"" + k + ".input\"}", Derived = "half=out/2", Detail = "metrics",
        };
        var study = spec.Build(model, out var options, out var warnings);
        var results = study.Run();
        options.Finish(results);
        Check(results.Scenarios.Count == 2, $"where keeps 2 of the 3 levels ({results.Scenarios.Count})");
        // an empty scenario list beside factors is no scenarios (it was refused as "factors and scenarios both")
        var empty = new StudySpec { Horizon = 300, Factors = "{\"" + q + ".capacity\": [1, 3]}", Scenarios = "[]",
                                    Metrics = "{\"out\": \"" + k + ".input\"}" };
        Check(empty.Build(model, out _, out _).Run().Scenarios.Count == 2, "scenarios=[] with factors runs the factors");
        Check(results.MetricNames.Contains("out") && results.MetricNames.Contains("half"), "metric and derived metric");
        Check(results.Runs.All(r => r.Error == null && Math.Abs(r.Metrics["half"] - r.Metrics["out"] / 2) < 1e-12), "derived per run");

        // a derived metric reads the factors by their where names, each run at its scenario's level
        var withFactor = new StudySpec { Horizon = 300, Replications = 2, Seed = 3, Factors = "{\"" + q + ".capacity\": [1, 3]}",
                                         Metrics = "{\"out\": \"" + k + ".input\"}", Derived = "profit=10*out-100*" + q + "_capacity" };
        var fr = withFactor.Build(model, out var fo, out _).Run();
        fo.Finish(fr);
        var capOf = fr.Scenarios.ToDictionary(sc => sc.Name, sc => double.Parse(sc.Parameters[0].Value, System.Globalization.CultureInfo.InvariantCulture));
        Check(fr.Runs.Count == 4 && fr.Runs.All(r => r.Error == null && Math.Abs(r.Metrics["profit"] - (10 * r.Metrics["out"] - 100 * capOf[r.Scenario])) < 1e-9)
              && capOf.Values.Distinct().Count() == 2,
              "derived reads the scenario's factor level: " + string.Join(", ", fr.Runs.Select(r => $"{r.Scenario}: {r.Metrics["profit"]}")));
        try { new StudySpec { Factors = "{\"" + q + ".capacity\": [1, 3]}", Metrics = "{\"out\": \"" + k + ".input\"}", Derived = "y=out*nothing" }.Build(model, out _, out _); Check(false, "unknown name"); }
        catch (ArgumentException e) { Check(e.Message.Contains("unknown name nothing") && e.Message.Contains("the factors are " + q + "_capacity"), "the error lists the factors: " + e.Message); }

        // a scenario that omits a factor: refused, each missing factor named with the model's value to copy
        var probe = new Study(model);
        string qBase = probe.CurrentLevel(q + ".capacity"), sBase = probe.CurrentLevel("srv.capacity");
        Check(qBase == model.Elements.First(e => e.Name == q).Get<int>("capacity").ToString(System.Globalization.CultureInfo.InvariantCulture) && sBase != null,
              $"the model's value of a factor path ({qBase}, {sBase})");
        Check(probe.CurrentLevel("nowhere.capacity") == null && probe.CurrentLevel("srv.nokey") == null, "a path that names nothing has no value");
        try
        {
            new StudySpec { Horizon = 300, Scenarios = "[{\"name\": \"a\", \"" + q + ".capacity\": 2, \"srv.capacity\": 2}, {\"name\": \"b\"}]",
                            Metrics = "{\"out\": \"" + k + ".input\"}" }.Build(model, out _, out _);
            Check(false, "an incomplete scenario is refused");
        }
        catch (ArgumentException e)
        {
            Check(e.Message.Contains($"'b' gives no value for {q}.capacity (the model's value: {qBase}), srv.capacity ({sBase})")
                  && e.Message.Contains("every scenario gives every factor any of them gives"), "the missing factors with their values: " + e.Message);
        }

        var manual = new Study(model).WithRunTime(300).WithReplications(2).WithSeed(3);
        manual.Factor(q + ".capacity", "3", "5");
        manual.Metric("out", k + ".input");
        var m = manual.Run();
        var a = results.Runs.Select(r => r.Metrics["out"]).ToList();
        var b = m.Runs.Select(r => r.Metrics["out"]).ToList();
        Check(a.SequenceEqual(b), $"the same runs as the Study API ({string.Join(",", a)} vs {string.Join(",", b)})");

        var answer = StudySpec.Answer(results, spec.Full, warnings, StudySpec.Settings(study));
        string text = MiniJson.Serialize(answer);
        var back = (Dictionary<string, object>)MiniJson.Parse(text);
        var scenarios = ((List<object>)back["scenarios"]);
        var first = (Dictionary<string, object>)scenarios[0];
        var outMetric = (Dictionary<string, object>)((Dictionary<string, object>)first["metrics"])["out"];
        Check(scenarios.Count == 2 && outMetric.ContainsKey("mean") && outMetric.ContainsKey("halfWidth") && (long)outMetric["n"] == 2, "the answer: per scenario, mean ± half-width, n");
        Check(!first.ContainsKey("elements"), "detail=metrics: no element KPIs");
        var settings = ((Dictionary<string, object>)back["settings"]);
        Check((long)settings["replications"] == 2 && (string)settings["seeds"] == "3..4", "the settings");
        try { new StudySpec { Detail = "all" }.Build(model, out _, out _); Check(false, "bad detail"); }
        catch (ArgumentException e) { Check(e.Message.Contains("detail"), e.Message); }
        try { new StudySpec { Metrics = "x=nowhere.input" }.Build(model, out _, out _); Check(false, "bad metric"); }
        catch (ArgumentException e) { Check(e.Message.Contains("cannot run"), e.Message); }
    }

    static void TestDefaultCapacityNote()
    {
        // basic_line with a server twice as slow as the arrivals: its Queue (capacity 100, the default) fills
        var model = ReferenceModels.BasicLine();
        model.Elements.First(e => e.Name == "srv").Set("delayStrategy.exponentialRate", 0.5);
        var spec = new StudySpec { Horizon = 600, Replications = 2, Metrics = "out=sink.input;qmax=q.content_max" };
        var study = spec.Build(model, out var options, out var warnings);
        var results = study.Run();
        options.Finish(results);
        var with = StudySpec.Answer(results, spec.Full, warnings, StudySpec.Settings(study), study.Model);
        var without = StudySpec.Answer(results, spec.Full, warnings, StudySpec.Settings(study));
        var notes = ((List<object>)with["warnings"]).Cast<string>().Where(w => w.Contains("Queue 'q'")).ToList();
        Check(notes.Count == 1 && notes[0].Contains("capacity 100, the default") && notes[0].Contains("2 of 2 runs")
              && notes[0].Contains("unlimited"), "the full default-capacity Queue is noted once: " + string.Join(" | ", notes));
        Check(MiniJson.Serialize(with["scenarios"]) == MiniJson.Serialize(without["scenarios"]), "the note changes no number");
        Check(!((List<object>)without["warnings"]).Cast<string>().Any(w => w.Contains("Queue 'q'")), "no model given: no note");

        // a capacity the study sets is chosen, not the default: no note, even at 100
        var chosen = new StudySpec { Horizon = 600, Factors = "q.capacity=100|200", Metrics = "out=sink.input" };
        var cs = chosen.Build(model, out var co, out var cw);
        var cr = cs.Run();
        co.Finish(cr);
        Check(!((List<object>)StudySpec.Answer(cr, false, cw, null, cs.Model)["warnings"]).Cast<string>().Any(w => w.Contains("Queue 'q'")),
              "a capacity set by a factor is not noted");

        // the default capacity never reached (the original server keeps up): no note
        var calm = ReferenceModels.BasicLine();
        var ks = new StudySpec { Horizon = 600, Metrics = "out=sink.input" }.Build(calm, out var ko, out var kw);
        var kr = ks.Run();
        ko.Finish(kr);
        Check(StudySpec.DefaultCapacityNotes(ks.Model, kr).Count == 0, "a Queue that never filled is not noted");
    }

    static void TestModelWarnings()
    {
        // basic_line routed by a label nothing sets: the model warns once per run, at the first item (a time per seed)
        var model = ReferenceModels.BasicLine();
        var srv = model.Elements.First(e => e.Name == "srv");
        srv.Set("outputStrategy.strategyType", "Expression");
        srv.Set("outputStrategy.routingExpression", "item.tpye == 'P' ? 0 : 0");
        var spec = new StudySpec { Horizon = 300, Replications = 3, Metrics = "out=sink.input" };
        var study = spec.Build(model, out var options, out var warnings);
        var results = study.Run();
        options.Finish(results);
        Check(results.Runs.Count == 3 && results.Runs.All(r => r.Results != null && r.Results.Info.Warnings.Count == 1
                                                               && r.Results.Info.Warnings[0].Contains("'tpye' is not set")),
              "each run's results carry its model warning");
        var back = ResultSetJson.Deserialize(ResultSetJson.Serialize(results));
        Check(back.Runs.Select(r => string.Join("|", r.Results.Info.Warnings)).SequenceEqual(results.Runs.Select(r => string.Join("|", r.Results.Info.Warnings))),
              "the warnings travel in the result set's JSON (the runner's answer)");
        var notes = ((List<object>)StudySpec.Answer(results, false, warnings)["warnings"]).Cast<string>().Where(w => w.StartsWith("[SimuLean] t=")).ToList();
        Check(notes.Count == 1 && notes[0].Contains("'tpye' is not set") && (results.Runs.Select(r => r.Results.Info.Warnings[0]).Distinct().Count() == 1
                                                                           || notes[0].Contains("more run(s) of this answer")),
              "one line for the three runs: " + string.Join(" | ", notes));

        var calm = new StudySpec { Horizon = 300, Metrics = "out=sink.input" }.Build(ReferenceModels.BasicLine(), out var co, out var cw);
        var cr = calm.Run();
        co.Finish(cr);
        Check(StudySpec.ModelWarningNotes(cr).Count == 0 && cr.Runs.All(r => r.Results.Info.Warnings.Count == 0), "no warning, no line");

        // twelve causes in one run: the first ten, then a count; the same kind twice in a run (two elements) is two causes
        var set = new ResultSet();
        var run = new ResultRun { Scenario = "s", Results = new RunResults() };
        foreach (char c in "abcdefghijkl") run.Results.Info.Warnings.Add("[SimuLean] t=1 '" + c + "' warned");
        set.Runs.Add(run);
        var capped = StudySpec.ModelWarningNotes(set);
        Check(capped.Count == StudySpec.MaxModelWarnings + 1 && capped.Last().Contains("2 more model warning(s)"), "capped: " + string.Join(" | ", capped));
        var twice = new ResultSet();
        foreach (int seed in new[] { 1, 2 })
        {
            var r = new ResultRun { Scenario = "s", Seed = seed, Results = new RunResults() };
            r.Results.Info.Warnings.Add($"[SimuLean] t={seed} 'M1' warned");
            r.Results.Info.Warnings.Add($"[SimuLean] t={seed} 'M2' warned");
            twice.Runs.Add(r);
        }
        var two = StudySpec.ModelWarningNotes(twice);
        Check(two.Count == 2 && two.All(w => w.EndsWith("(and in 1 more run(s) of this answer, at other times)")), "per cause over the runs: " + string.Join(" | ", two));
    }

    static Dictionary<string, object> Metric(Dictionary<string, object> scenario, string block, string metric)
        => (Dictionary<string, object>)((Dictionary<string, object>)(block == null ? scenario : (Dictionary<string, object>)scenario[block])["metrics"])[metric];

    static double Num(object o) => Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture);

    static void TestPairedAndRange()
    {
        // two service rates of basic_line, 10 replications on the same seeds (common random numbers)
        var model = ReferenceModels.BasicLine();
        var spec = new StudySpec { Horizon = 500, Replications = 10, Factors = "srv.delayStrategy.exponentialRate=1.25|1.35",
                                   Metrics = "wait=q.stay_avg;out=sink.input", Detail = "full", CompareTo = "1" };
        var study = spec.Build(model, out var options, out var warnings);
        var results = study.Run();
        options.Finish(results);
        var answer = (Dictionary<string, object>)MiniJson.Parse(MiniJson.Serialize(
            StudySpec.Answer(results, spec.Full, warnings, StudySpec.Settings(study), study.Model, spec.CompareTo)));
        var scenarios = ((List<object>)answer["scenarios"]).Cast<Dictionary<string, object>>().ToList();
        Check(scenarios.Count == 2, "two scenarios");
        bool ordered = true;
        foreach (var sc in scenarios)
            foreach (var m in new[] { "wait", "out" })
            {
                var iv = Metric(sc, null, m);
                ordered &= iv.ContainsKey("min") && iv.ContainsKey("max") && Num(iv["min"]) <= Num(iv["mean"]) && Num(iv["mean"]) <= Num(iv["max"]);
            }
        Check(ordered, "detail=full: min <= mean <= max for every metric");
        Check(!scenarios[0].ContainsKey("paired") && scenarios[1].ContainsKey("paired"), "the reference has no paired block, the other has");
        var paired = (Dictionary<string, object>)scenarios[1]["paired"];
        Check((string)paired["to"] == (string)scenarios[0]["scenario"], "compareTo by position names the reference: " + paired["to"]);
        var pw = Metric(scenarios[1], "paired", "wait");
        var a = Metric(scenarios[0], null, "wait");
        var b = Metric(scenarios[1], null, "wait");
        Check(Num(pw["n"]) == 10 && Math.Abs(Num(pw["diff"]) - (Num(b["mean"]) - Num(a["mean"]))) < 1e-9,
              $"diff = the difference of the means over 10 pairs ({pw["diff"]})");
        double unpaired = Math.Sqrt(Num(a["halfWidth"]) * Num(a["halfWidth"]) + Num(b["halfWidth"]) * Num(b["halfWidth"]));
        Check(Num(pw["halfWidth"]) < unpaired, $"common seeds: the paired half-width {pw["halfWidth"]} < the unpaired {unpaired}");

        // detail=metrics: no range (smaller answers); a single run: no range either
        var small = new StudySpec { Horizon = 500, Replications = 3, Metrics = "wait=q.stay_avg" };
        var ss = small.Build(model, out var so, out var sw);
        var sr = ss.Run();
        so.Finish(sr);
        var sm = Metric((Dictionary<string, object>)((List<object>)StudySpec.Answer(sr, small.Full, sw)["scenarios"])[0], null, "wait");
        Check(!sm.ContainsKey("min") && !sm.ContainsKey("max"), "detail=metrics: mean, halfWidth, n only");
        Check(!StudySpec.IntervalOf(new[] { 4.0 }, 0.95, true).ContainsKey("min"), "one value: no range");

        // the reference by name (any case); an unknown one fails before any run, listing the scenarios
        var names = study.Plan().Select(p => p.Name).ToList();
        Check(StudySpec.ScenarioNamed(names, names[1].ToUpperInvariant()) == names[1], "by name, ignoring case");
        try { new StudySpec { Factors = "srv.delayStrategy.exponentialRate=1.25|1.35", Metrics = "out=sink.input", CompareTo = "Base" }.Build(model, out _, out _); Check(false, "unknown compareTo"); }
        catch (ArgumentException e) { Check(e.Message.Contains("compareTo") && e.Message.Contains("1 = '"), e.Message); }
        // the study file carries it (the runner answers the same)
        var back = StudySpec.Deserialize(StudySpec.Serialize(model, spec)).spec;
        Check(back.CompareTo == "1", "compareTo in the study file");
    }

    static void TestStudyMetricNotes()
    {
        var model = ReferenceModels.BasicLine();
        var spec = new StudySpec { Horizon = 200, Metrics = "srcStay=src.stay_avg;exitUtil=sink.utilization;exitStay=sink.stay_avg;wait=q.stay_avg;out=sink.input" };
        spec.Build(model, out _, out var warnings);
        Check(warnings.Exists(w => w.Contains("'srcStay'") && w.Contains("'src' is a Source") && w.Contains("src.state:BLOCKED")), "a Source's stay_avg: " + string.Join(" | ", warnings));
        Check(warnings.Exists(w => w.Contains("'exitUtil'") && w.Contains("'sink' is a Sink") && w.Contains("sink.tis_avg")), "a Sink's utilization points to tis_avg");
        Check(warnings.Exists(w => w.Contains("'exitStay'")), "a Sink's stay_avg");
        Check(!warnings.Exists(w => w.Contains("'wait'") || w.Contains("'out'")), "a Queue's stay and a Sink's input: no warning");

        // an expression is not a metric path: the error points to derived
        try { new StudySpec { Metrics = "L=q.content_avg+srv.content_avg" }.Build(model, out _, out _); Check(false, "an expression as a metric is refused"); }
        catch (ArgumentException e) { Check(e.Message.Contains("derived=L=a+b"), "the error points to derived: " + e.Message); }
        try { new StudySpec { Metrics = "L=(q.content_avg)" }.Build(model, out _, out _); Check(false, "a parenthesis is refused"); }
        catch (ArgumentException e) { Check(e.Message.Contains("derived"), "unknown scope or owner: points to derived: " + e.Message); }

        // a derived metric that reads nothing is the same constant in every run: a warning, not an error
        new StudySpec { Horizon = 200, Metrics = "out=sink.input", Derived = "stalls=0;twice=2*out" }.Build(model, out var options, out var w2);
        Check(w2.Exists(w => w.Contains("derived 'stalls' reads no metric")) && !w2.Exists(w => w.Contains("'twice'")), "a constant derived metric is noted: " + string.Join(" | ", w2));
        Check(options.Warnings.Count == 1, "the options carry it");
        new StudySpec { Horizon = 200, Factors = "q.capacity=5|10", Metrics = "out=sink.input", Derived = "cost=10*q_capacity" }.Build(model, out var o3, out _);
        Check(o3.Warnings.Count == 0, "a derived metric reading a factor is not constant");

        // a range lo..hi is the optimiser's: a study lists its levels
        try { new StudySpec { Factors = "q.capacity=1..10", Metrics = "out=sink.input" }.Build(model, out _, out _); Check(false, "a range level is refused"); }
        catch (Exception e) when (e is ArgumentException || e is FormatException) { Check(e.Message.Contains("lists its levels") && e.Message.Contains("simulean_optimize_start"), e.Message); }
    }

    static void TestStudyLevelNotes()
    {
        // the queue never holds 50: its capacity does not bind, and with common random numbers the levels give identical runs
        var model = ReferenceModels.BasicLine();
        var spec = new StudySpec { Horizon = 200, Replications = 2, Factors = "q.capacity=50|80;srv.delayStrategy.exponentialRate=1.25|1.4", Metrics = "out=sink.input;wait=q.stay_avg" };
        var study = spec.Build(model, out var options, out var warnings);
        var results = study.Run();
        options.Finish(results);
        var answer = (Dictionary<string, object>)MiniJson.Parse(MiniJson.Serialize(StudySpec.Answer(results, spec.Full, warnings, StudySpec.Settings(study), study.Model)));
        var notes = ((List<object>)answer["warnings"]).Cast<string>().ToList();
        Check(notes.Exists(n => n.StartsWith("Factor 'q.capacity': every level (50|80) gave the same results (q.content_max ") && n.Contains("never reached the smallest capacity, 50")),
              "the capacity that never binds is noted: " + string.Join(" | ", notes));
        Check(!notes.Exists(n => n.Contains("'srv.delayStrategy.exponentialRate'")), "a factor that changes the results is not");
        var first = (Dictionary<string, object>)((Dictionary<string, object>)((List<object>)answer["scenarios"])[0])["parameters"];
        Check(!(first["q.capacity"] is string) && Num(first["q.capacity"]) == 50, "a number level is echoed as a number: " + first["q.capacity"]?.GetType().Name);
        Check(StudySpec.TypedLevel("true") is bool b && b && StudySpec.TypedLevel("Fifo") as string == "Fifo" && StudySpec.TypedLevel("1,2") as string == "1,2",
              "a bool as a bool, an option or a list as its text");
    }

    static void TestCompactAnswer()
    {
        // three replications of basic_line: the end reasons counted, no model block, no null first error, short numbers
        var model = ReferenceModels.BasicLine();
        var spec = new StudySpec { Horizon = 300, Replications = 3, Metrics = "wait=q.stay_avg;out=sink.input" };
        var study = spec.Build(model, out var options, out var warnings);
        var results = study.Run();
        options.Finish(results);
        var compact = (Dictionary<string, object>)((List<object>)StudySpec.Answer(results, spec.Full, warnings, StudySpec.Settings(study), study.Model)["scenarios"])[0];
        var reasons = (Dictionary<string, object>)compact["endReasons"];
        Check(reasons.Count == 1 && (int)reasons["Horizon"] == 3, "the end reasons counted: " + MiniJson.Serialize(reasons));
        Check(!compact.ContainsKey("model") && !compact.ContainsKey("firstError") && !compact.ContainsKey("deadlocked") && !compact.ContainsKey("perRun"),
              "compact: no model block when no run stalled, no null field, no runs: " + string.Join(", ", compact.Keys));
        double raw = results.Runs.Average(r => r.Metrics["wait"]);
        double shown = Num(Metric(compact, null, "wait")["mean"]);
        Check(shown == StudySpec.Significant(raw, StudySpec.CompactDigits) && Math.Abs(shown - raw) <= 1e-5 * Math.Abs(raw) && shown.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length <= 8,
              $"compact: {StudySpec.CompactDigits} significant digits ({raw} -> {shown})");

        // detail=full: every digit, the model block, each run's end reason and metric values in seed order
        var full = (Dictionary<string, object>)((List<object>)StudySpec.Answer(results, true, warnings, StudySpec.Settings(study), study.Model)["scenarios"])[0];
        double fullMean = Num(Metric(full, null, "wait")["mean"]);
        Check(Math.Abs(fullMean - raw) < 1e-12 * Math.Max(1, Math.Abs(raw)) && fullMean != shown, $"full: the mean at full precision ({fullMean})");
        Check(full.ContainsKey("model") && full.ContainsKey("elements"), "full: the model block and the KPIs");
        var perRun = ((List<object>)full["perRun"]).Cast<Dictionary<string, object>>().ToList();
        Check(perRun.Count == 3 && perRun.Select(r => (int)r["seed"]).SequenceEqual(results.Runs.Select(r => r.Seed))
              && perRun.All(r => (string)r["endReason"] == "Horizon")
              && perRun.Select(r => Num(((Dictionary<string, object>)r["metrics"])["out"])).SequenceEqual(results.Runs.Select(r => r.Metrics["out"])),
              "full: perRun gives each run's seed, end reason and metric values");
        Check(MiniJson.Serialize(full["endReasons"]) == MiniJson.Serialize(compact["endReasons"]), "the counts in both");

        // a run that deadlocks (the end condition 'when empty'): the reason, the count of deadlocked runs, the model block
        var dead = ReferenceModels.DeadlockDeadEnd();
        dead.StopWhenEmpty = true;
        var ds = new StudySpec { Replications = 2, Metrics = "span=model.end_time" };
        var dstudy = ds.Build(dead, out var dopt, out var dw);
        var dres = dstudy.Run();
        dopt.Finish(dres);
        var stuck = (Dictionary<string, object>)((List<object>)StudySpec.Answer(dres, false, dw)["scenarios"])[0];
        var dreasons = (Dictionary<string, object>)stuck["endReasons"];
        bool deadlocked = dres.Runs.All(r => r.Results.Info.Deadlocked);
        Check(!deadlocked || ((int)stuck["deadlocked"] == 2 && stuck.ContainsKey("model")),
              "a deadlock: counted and the model block shown: " + MiniJson.Serialize(stuck));
        Check(deadlocked, "the reference model deadlocks: " + MiniJson.Serialize(dreasons));

        // a failed run: its error, and in perRun
        var set = new ResultSet { Name = "t" };
        set.MetricNames.Add("out");
        set.Scenarios.Add(new ResultScenario { Name = "A" });
        var good = new ResultRun { Scenario = "A", Replication = 1, Seed = 1 };
        good.Metrics["out"] = 10;
        set.Runs.Add(good);
        set.Runs.Add(new ResultRun { Scenario = "A", Replication = 2, Seed = 2, Error = "boom" });
        var withError = (Dictionary<string, object>)((List<object>)StudySpec.Answer(set, true, null)["scenarios"])[0];
        Check((string)withError["firstError"] == "boom" && ((List<object>)withError["perRun"]).Cast<Dictionary<string, object>>().Any(r => r.TryGetValue("error", out object x) && (string)x == "boom"),
              "a failed run: firstError and perRun carry it");
    }

    // the arguments of a page's next: "… top=25 offset=50 sort=-out"
    static (int top, int offset, string sort) NextOf(string next)
    {
        int Int(string key) => int.Parse(System.Text.RegularExpressions.Regex.Match(next, @"\b" + key + @"=(\d+)").Groups[1].Value);
        var sort = System.Text.RegularExpressions.Regex.Match(next, @"\bsort=(\S+)");
        return (Int("top"), Int("offset"), sort.Success ? sort.Groups[1].Value : "");
    }

    static void TestAnswerPages()
    {
        // 4 x 4 x 2 = 32 scenarios, two metrics; a small limit stands for the tool's
        var model = ReferenceModels.BasicLine();
        foreach (string detail in new[] { "metrics", "full" })
        {
            var spec = new StudySpec { Horizon = 200, Replications = 2, Detail = detail, Metrics = "out=sink.input;wait=q.stay_avg",
                                       Factors = "q.capacity=1|2|3|4;srv.delayStrategy.exponentialRate=1|1.1|1.2|1.3;src.interArrivalDistribution.exponentialRate=1|0.9" };
            var study = spec.Build(model, out var options, out var warnings);
            var results = study.Run();
            options.Finish(results);
            var whole = (Dictionary<string, object>)MiniJson.Parse(MiniJson.Serialize(StudySpec.Answer(results, spec.Full, warnings, StudySpec.Settings(study), study.Model)));
            var all = (List<object>)whole["scenarios"];
            Check(all.Count == 32, "32 scenarios: " + all.Count);
            int limit = detail == "full" ? 30000 : 6000;
            Check(AnswerPages.SizeOf(whole) > limit, $"{detail}: the whole answer is over the limit ({AnswerPages.SizeOf(whole)})");
            Check(ReferenceEquals(AnswerPages.Study(whole, maxChars: 10_000_000), whole), "an answer within the limit is answered as it is");

            // over the limit: a table of the first ones by the first metric, saying it is cut and how to read the rest
            var cut = AnswerPages.Study(whole, more: "simulean_results action=read resultSet=last", maxChars: limit);
            Check(AnswerPages.SizeOf(cut) <= limit, $"{detail}: the cut answer fits ({AnswerPages.SizeOf(cut)} <= {limit})");
            Check(!cut.ContainsKey("scenarios") && cut.ContainsKey("table") && cut.ContainsKey("settings") && cut.ContainsKey("warnings") && cut.ContainsKey("errors"),
                  "a table instead of the scenarios; settings, errors and warnings kept: " + string.Join(", ", cut.Keys));
            var page = (Dictionary<string, object>)cut["page"];
            string note = (string)page["note"];
            Check((bool)page["truncated"] && (int)page["scenarios"] == 32 && note.Contains("32 scenarios") && note.Contains("detail=full")
                  && note.Contains("simulean_results action=export") && note.Contains("sort="), "the note says it is cut and how to read the rest: " + note);
            var table = (Dictionary<string, object>)cut["table"];
            var rows = ((List<object>)table["rows"]).Cast<List<object>>().ToList();
            Check(((List<object>)table["columns"]).Cast<string>().SequenceEqual(new[] { "scenario", "out", "out ±", "wait", "wait ±" }), "the table's columns");
            var outs = rows.Select(r => Num(r[1])).ToList();
            Check(outs.SequenceEqual(outs.OrderBy(x => x)) && rows.Count == (int)page["shown"], "sorted by the first metric, ascending");
            Check(((string)page["next"]).StartsWith("simulean_results action=read resultSet=last top=") && ((string)page["next"]).Contains("offset=0 sort=out"),
                  "next: the exact arguments of the first page: " + page["next"]);

            // walking the pages from the table's next until a page has none gives every scenario once, whole
            foreach (string order in new[] { null, "-wait", "out desc" })
            {
                var (top, offset, sort) = order == null ? NextOf((string)page["next"]) : (7, 0, order);
                var walked = new List<object>();
                for (int guard = 0; guard < 100; guard++)
                {
                    var p = AnswerPages.Study(whole, top, sort, offset, "simulean_results action=read resultSet=last", limit);
                    Check(AnswerPages.SizeOf(p) <= limit || ((List<object>)p["scenarios"]).Count == 1, $"a page fits ({AnswerPages.SizeOf(p)})");
                    Check(MiniJson.Serialize(p["settings"]) == MiniJson.Serialize(whole["settings"]) && MiniJson.Serialize(p["warnings"]) == MiniJson.Serialize(whole["warnings"]),
                          "every page keeps the settings and warnings");
                    walked.AddRange((List<object>)p["scenarios"]);
                    var pg = (Dictionary<string, object>)p["page"];
                    if (!pg.ContainsKey("next")) break;
                    (top, offset, sort) = NextOf((string)pg["next"]);
                }
                string Key(object sc) => (string)((Dictionary<string, object>)sc)["scenario"];
                Check(walked.Count == 32 && walked.Select(Key).Distinct().Count() == 32, $"{detail} {order}: the pages give every scenario once ({walked.Count})");
                var byName = all.ToDictionary(Key, sc => MiniJson.Serialize(sc));
                Check(walked.All(sc => byName[Key(sc)] == MiniJson.Serialize(sc)), $"{detail} {order}: each scenario as the whole answer gives it");
            }
            // plan order: the pages concatenated are the whole list
            var plan = new List<object>();
            for (int off = 0; off < 32; off += 5) plan.AddRange((List<object>)AnswerPages.Study(whole, 5, "", off, "x", 10_000_000)["scenarios"]);
            Check(MiniJson.Serialize(plan) == MiniJson.Serialize(all), $"{detail}: pages in plan order concatenated = the whole scenarios list");
        }
        // a page past the end, a bad sort, a bad top
        var small = (Dictionary<string, object>)MiniJson.Parse(MiniJson.Serialize(StudySpec.Answer(
            new StudySpec { Horizon = 50, Metrics = "out=sink.input" }.Build(model, out var so, out var sw).Run(), false, sw)));
        var past = AnswerPages.Study(small, 5, "", 10);
        Check(((List<object>)past["scenarios"]).Count == 0 && ((string)((Dictionary<string, object>)past["page"])["note"]).Contains("past the last"), "a page past the end says so");
        try { AnswerPages.Study(small, 5, "outt"); Check(false, "an unknown sort metric"); }
        catch (ArgumentException e) { Check(e.Message.Contains("sort: no metric 'outt'") && e.Message.Contains("out"), e.Message); }
        try { AnswerPages.Study(small, -1); Check(false, "a negative top"); }
        catch (ArgumentException e) { Check(e.Message.StartsWith("top:"), e.Message); }
        Check(AnswerPages.SizeOf(new Dictionary<string, object> { ["a"] = new List<object> { 1, "x" } }) == "{\n  \"a\": [\n    1,\n    \"x\"\n  ]\n}".Length,
              "the size of an answer as a client indents it");
    }

    static void TestVocabularySections()
    {
        var whole = Vocabulary.Describe();
        var index = Vocabulary.Index(whole, 8000);
        var sections = ((List<object>)index["sections"]).Cast<Dictionary<string, object>>().ToList();
        Check(sections.Select(e => (string)e["section"]).SequenceEqual(whole.Keys) && sections.All(e => (int)e["size"] > 0 && ((string)e["holds"]).Length > 0),
              "the index lists every section with its size and what it holds");
        Check(Vocabulary.Sections.Select(x => x.name).SequenceEqual(whole.Keys), "every section of Describe is described");
        Check(((string)index["ask"]).Contains("section=all") && ((string)index["ask"]).Contains("section=<name>"), "the index says how to ask: " + index["ask"]);
        Check(AnswerPages.SizeOf(index) < AnswerPages.MaxChars, "the index is small: " + AnswerPages.SizeOf(index));
        // every section, merged, is the whole vocabulary; all is the whole
        var merged = new Dictionary<string, object>();
        foreach (string name in whole.Keys)
            foreach (var kv in Vocabulary.Section(whole, name)) merged[kv.Key] = kv.Value;
        Check(MiniJson.Serialize(merged) == MiniJson.Serialize(whole), "the sections together are the whole vocabulary");
        Check(ReferenceEquals(Vocabulary.Section(whole, "all"), whole), "section=all is the whole");
        var two = Vocabulary.Section(whole, "paths, Statistics");
        Check(two.Count == 2 && two.ContainsKey("paths") && two.ContainsKey("statistics"), "several, any case");
        // a large section lists its parts; the parts together are the section
        foreach (var e in sections.Where(e => e.ContainsKey("parts")))
        {
            string name = (string)e["section"];
            var parts = ((List<object>)e["parts"]).Cast<string>().ToList();
            var joined = Vocabulary.Section(whole, string.Join(",", parts.Select(x => name + "." + x)));
            Check(MiniJson.Serialize(joined[name]) == MiniJson.Serialize(whole[name]), $"the parts of {name} together are the section");
        }
        Check(sections.Any(e => e.ContainsKey("parts")), "a section over the limit lists its parts");
        var study = Vocabulary.Section(whole, "arguments.study");
        Check(((List<object>)study["arguments"]).Count == 1 && (string)((Dictionary<string, object>)((List<object>)study["arguments"])[0])["concept"] == "study", "a concept by name");
        try { Vocabulary.Section(whole, "statistic"); Check(false, "an unknown section"); }
        catch (ArgumentException e) { Check(e.Message.Contains("no section 'statistic'") && e.Message.Contains("statistics"), e.Message); }
        try { Vocabulary.Section(whole, "arguments.nothing"); Check(false, "an unknown part"); }
        catch (ArgumentException e) { Check(e.Message.Contains("no part 'nothing'") && e.Message.Contains("study"), e.Message); }
    }

    static void TestStudyInputs()
    {
        var f = StudyInputs.FactorsOf("{\"Src.tableSource\": \"sql: SELECT a; b\", \"Srv.capacity\": [1, 2.5], \"Q.flag\": true}");
        Check(f.Count == 3 && f[0].levels[0] == "sql: SELECT a; b" && f[1].levels.SequenceEqual(new[] { "1", "2.5" }) && f[2].levels[0] == "true", "JSON factors");
        Check(StudyInputs.FactorsOf("a.b=1|2;c.d=x").Count == 2, "shorthand factors");
        Check(StudyInputs.FactorsOf("[{\"path\": \"Srv.capacity\", \"values\": [3]}]")[0].levels[0] == "3", "array factors");
        Check(StudyInputs.MetricsOf("[{\"name\": \"out\", \"path\": \"Sink.input\"}]")[0] == ("out", "Sink.input"), "array metrics");
        // the shorthand splits at the first '=' outside brackets: a level may hold '=', a label filter keeps its own
        var tally = StudyInputs.FactorsOf("Exit.tallies=tis=age");
        Check(tally.Count == 1 && tally[0].path == "Exit.tallies" && tally[0].levels.SequenceEqual(new[] { "tis=age" }), "a level holding '=': " + tally[0].path);
        var team = StudyInputs.FactorsOf("team:W.members[skill=weld]=1|2");
        Check(team[0].path == "team:W.members[skill=weld]" && team[0].levels.SequenceEqual(new[] { "1", "2" }), "a label filter's '=' stays in the path");
        Check(StudyInputs.MetricsOf("late=wait>=60;eq=a==b")[0] == ("late", "wait>=60") && StudyInputs.MetricsOf("late=wait>=60;eq=a==b")[1] == ("eq", "a==b"),
              "a derived expression holding '=' (>=, ==)");
        Check(StudyInputs.MetricsOf("t=Exit.tally:tis[kind=A]:mean")[0] == ("t", "Exit.tally:tis[kind=A]:mean"), "a split value in a metric path");
        try { StudyInputs.FactorsOf("{\"a\": "); Check(false, "bad JSON"); }
        catch (ArgumentException e) { Check(e.Message.Contains("not valid JSON"), e.Message); }
    }

    static string RepoRoot()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "CLAUDE.md"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository root (CLAUDE.md) not found.");
    }

    static (int code, Dictionary<string, object> json, string err) Cli(params string[] args)
    {
        var o = new System.IO.StringWriter();
        var e = new System.IO.StringWriter();
        int code = SimuLean.Runner.RunnerCli.Run(args, o, e);
        string text = o.ToString().Trim();
        return (code, text.StartsWith("{") ? (Dictionary<string, object>)MiniJson.Parse(text) : null, e.ToString());
    }

    static void TestFingerprint()
    {
        string sources = EngineFingerprint.OfSources(System.IO.Path.Combine(RepoRoot(), "Assets", "SimuLean.Net"),
                                                     System.IO.Path.Combine(RepoRoot(), "Assets", "SimuLean.UserLogic"));
        Check(sources == EngineBuild.Fingerprint, $"the build's fingerprint {EngineBuild.Fingerprint} = the sources' {sources}");
        Check(sources.Length == 16 && sources.All(c => "0123456789abcdef".IndexOf(c) >= 0), "16 hex digits");
        var (code, json, _) = Cli("version");
        Check(code == 0 && (string)json["kind"] == "version" && (string)json["engine"] == sources && (long)json["format"] == ConfigJson.FormatVersion, "version");
    }

    static void TestRunnerRun()
    {
        var model = ReferenceModels.All().First(m => m.Key == "basic_line").Value;
        string q = model.Elements.First(e => e.Type == "Queue").Name, k = model.Elements.First(e => e.Type == "Sink").Name;
        var spec = new StudySpec { Horizon = 300, Replications = 2, Factors = "{\"" + q + ".capacity\": [2, 4]}", Metrics = "out=" + k + ".input" };
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "simulean-runnertests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string file = System.IO.Path.Combine(dir, "study.json"), outFile = System.IO.Path.Combine(dir, "results.json");
            System.IO.File.WriteAllText(file, StudySpec.Serialize(model, spec));
            var (code, json, err) = Cli("run", file, "--quiet");
            Check(code == 0 && (string)json["kind"] == "results" && (string)json["engine"] == EngineBuild.Fingerprint, "run: kind results, engine: " + err);
            var scenarios = (List<object>)json["scenarios"];
            Check(scenarios.Count == 2, "two scenarios");

            var study = spec.Build(model, out var options, out var warnings);
            var direct = StudySpec.Answer(study.Run(), spec.Full, warnings, StudySpec.Settings(study));
            string Means(List<object> sc) => string.Join(",", sc.Select(x => MiniJson.Serialize(((Dictionary<string, object>)x)["metrics"])));
            Check(Means(scenarios) == Means((List<object>)MiniJson.Parse(MiniJson.Serialize(direct["scenarios"]))), "the same metrics as the engine's answer");

            var (code2, json2, _) = Cli("run", file, "--out", outFile, "--threads", "2", "--quiet");
            Check(code2 == 0 && json2 == null && System.IO.File.Exists(outFile), "--out writes the file, nothing on the output");
            var fromFile = (Dictionary<string, object>)MiniJson.Parse(System.IO.File.ReadAllText(outFile));
            Check(Means((List<object>)fromFile["scenarios"]) == Means(scenarios), "threads give the same results");

            var (code3, json3, _) = Cli("validate", file);
            Check(code3 == 0 && (string)json3["kind"] == "validation" && (long)json3["scenarios"] == 2 && (bool)json3["ok"], "validate");

            System.IO.File.WriteAllText(file, ConfigJson.Serialize(model));
            var (code4, json4, _) = Cli("run", file, "--quiet");
            Check(code4 == 0 && ((List<object>)json4["scenarios"]).Count == 1, "a model file runs as one scenario");
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    static void TestRunnerDowntimeMetricsAndNoProgress()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "simulean-runnertests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string file = System.IO.Path.Combine(dir, "study.json");
            // repair crews and preemption: the metrics read in the runner as in the engine
            var crew = ReferenceModels.RepairCrew();
            var spec = new StudySpec { Horizon = 3000, Metrics = "rep=downtime:machineFail@m1.repairs;pre=team:mech.preemptions;jobs=m1.interruptions" };
            System.IO.File.WriteAllText(file, StudySpec.Serialize(crew, spec));
            var (code, json, err) = Cli("run", file, "--quiet");
            Check(code == 0 && (string)json["kind"] == "results", "run: " + err);
            var metrics = (Dictionary<string, object>)((Dictionary<string, object>)((List<object>)json["scenarios"])[0])["metrics"];
            var study = spec.Build(crew, out _, out _);
            var direct = study.Run().Runs[0].Metrics;
            foreach (string m in new[] { "rep", "pre", "jobs" })
                Check(Convert.ToDouble(((Dictionary<string, object>)metrics[m])["mean"]) == direct[m], $"{m}: the runner's value is the engine's ({direct[m]})");
            // a downtime on several targets read together is valid; a misspelt statistic is not
            System.IO.File.WriteAllText(file, StudySpec.Serialize(crew, new StudySpec { Metrics = "r=downtime:machineFail.repairs" }));
            var (okCode, okJson, _) = Cli("validate", file);
            Check(okCode == 0 && okJson != null && (string)okJson["kind"] == "validation", $"validate: a downtime on all its targets (exit {okCode})");
            System.IO.File.WriteAllText(file, StudySpec.Serialize(crew, new StudySpec { Metrics = "r=downtime:machineFail.repairz" }));
            var (vcode, vjson, _) = Cli("validate", file);
            Check(vcode == 2 && vjson != null && (string)vjson["kind"] == "error" && ((string)vjson["message"]).Contains("'repairs'"),
                  $"validate (the study checked on the built model) names the downtime's statistics (exit {vcode}): {(vjson == null ? "" : MiniJson.Serialize(vjson))}");

            // the no-progress end condition: in the model file and as a study option
            var dead = ReferenceModels.DeadlockDeadEnd();
            var tick = new ReactionConfig("tick");
            tick.Set("signal", "Every");
            tick.Set("period", 7.0);
            dead.Reactions.Add(tick);
            Check(ConfigJson.Deserialize(ConfigJson.Serialize(new SimulationConfig { StopWhenNoProgressFor = 25 })).StopWhenNoProgressFor == 25,
                  "the model file keeps the setting");
            string EndReason(StudySpec s)
            {
                System.IO.File.WriteAllText(file, StudySpec.Serialize(dead, s));
                var (c, j, e) = Cli("run", file, "--quiet");
                if (c != 0) return "error: " + e + (j != null ? MiniJson.Serialize(j) : "");
                var sc = (Dictionary<string, object>)((List<object>)j["scenarios"])[0];
                var reasons = (Dictionary<string, object>)sc["endReasons"];
                return reasons.Count == 1 ? reasons.Keys.First() : "several: " + MiniJson.Serialize(reasons);
            }
            string r;
            Check((r = EndReason(new StudySpec { Metrics = "span=model.end_time" })) == "Horizon", "off: the ticks hide the deadlock: " + r);
            Check((r = EndReason(new StudySpec { Metrics = "span=model.end_time", NoProgress = 50 })) == "NoProgress", "the study option ends the run: " + r);
            dead.StopWhenNoProgressFor = 50;
            Check((r = EndReason(new StudySpec { Metrics = "span=model.no_progress_time" })) == "NoProgress", "the model's setting ends the run: " + r);
            Check((r = EndReason(new StudySpec { Metrics = "span=model.end_time", NoProgress = 0 })) == "Horizon", "noProgress 0 switches it off: " + r);
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    static void TestRunnerErrors()
    {
        void Fails(string what, string expected, params string[] args)
        {
            var (code, json, _) = Cli(args);
            Check(code == 2 && json != null && (string)json["kind"] == "error" && ((string)json["message"]).Contains(expected),
                  $"{what}: exit {code}, {(json == null ? "no JSON" : (string)json["message"])}");
        }
        Fails("unknown command", "Unknown command", "frobnicate");
        Fails("no file", "give the study file", "run");
        Fails("unknown option", "Unknown option", "run", "x.json", "--fast");
        Fails("missing file", "nowhere.json", "run", "nowhere.json");
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "simulean-runnertests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string file = System.IO.Path.Combine(dir, "bad.json");
            System.IO.File.WriteAllText(file, "{\"format\":9,\"kind\":\"study\",\"model\":{}}");
            Fails("a newer format", "format 9", "run", file);
            var model = ReferenceModels.All().First(m => m.Key == "basic_line").Value;
            System.IO.File.WriteAllText(file, StudySpec.Serialize(model, new StudySpec { Metrics = "x=nowhere.input" }));
            Fails("a study that cannot run", "cannot run", "run", file);
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    // the result set without what changes from run to run of the same work (wall-clock time, creation date)
    static string Comparable(ResultSet set)
    {
        var o = ResultSetJson.ToJson(set);
        o.Remove("created");
        foreach (Dictionary<string, object> r in (List<object>)o["runs"]) r.Remove("elapsed");
        return MiniJson.Serialize(o);
    }

    static void TestResultSetJson()
    {
        foreach (var kv in ReferenceModels.All().Take(8))
        {
            var st = new Study(kv.Value).WithRunTime(Math.Min(kv.Value.MaxSimulationTime, 400)).WithReplications(2);
            st.SeriesInterval = 50; st.RecordTimeline = true; st.Metric("end", "model.end_time");
            var set = st.Run();
            set.Candidates.Add(new ResultCandidate { Iteration = 1, Fitness = double.NaN, Error = "x" });
            set.Extra.Add(new ResultTable("history", "a", "b"));
            string json = ResultSetJson.Serialize(set);
            var back = ResultSetJson.Deserialize(json);
            Check(ResultSetJson.Serialize(back) == json, $"{kv.Key}: the set round-trips");
            Check(ResultExport.ToJson(back) == ResultExport.ToJson(set), $"{kv.Key}: every table the same");
            Check(back.Runs[0].Results.Series.Count == set.Runs[0].Results.Series.Count && back.Runs[0].Results.Timeline.Count == set.Runs[0].Results.Timeline.Count,
                  $"{kv.Key}: series and timeline");
            Check(double.IsNaN(back.Candidates[0].Fitness) && back.Created == set.Created, $"{kv.Key}: NaN and the date");
        }
    }

    static void TestStudyBatch()
    {
        var model = ReferenceModels.All().First(m => m.Key == "basic_line").Value;
        string q = model.Elements.First(e => e.Type == "Queue").Name, k = model.Elements.First(e => e.Type == "Sink").Name;
        var study = new Study(model).WithRunTime(300).WithReplications(2).WithSeed(4);
        study.Factor(q + ".capacity", "1", "3");
        study.Metric("out", k + ".input");
        study.SeriesInterval = 25;
        var direct = study.Run();
        var batch = StudyBatch.Deserialize(StudyBatch.Serialize(study.Batch()));
        Check(batch.Scenarios.Count == 2 && batch.Scenarios.All(s => s.Model != null && s.Replications == 2), "the batch: 2 scenarios applied");
        Check(Comparable(batch.Run()) == Comparable(direct), "a batch read from its file runs as the study");

        var bad = new Study(ReferenceModels.MultiServer3()) { Threads = 2 }.Factor("ms.capacity", 2, -1);   // -1 cannot be built
        var badBatch = bad.Batch();
        var badSet = StudyBatch.Deserialize(StudyBatch.Serialize(badBatch)).Run();
        Check(badSet.Runs.Count(r => r.Error != null) == 1 && badSet.Runs.Count(r => r.Error == null) == 1, "a run that fails is recorded with its error, the other kept");
        Check(Comparable(badSet) == Comparable(bad.Run()), "as the study records it");

        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "simulean-runnertests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string file = System.IO.Path.Combine(dir, "batch.json");
            System.IO.File.WriteAllText(file, StudyBatch.Serialize(study.Batch()));
            var (code, json, err) = Cli("run", file, "--quiet", "--threads", "3");
            Check(code == 0 && (string)json["kind"] == "resultset" && (string)json["engine"] == EngineBuild.Fingerprint, "runner: kind resultset " + err);
            var set = ResultSetJson.FromJson((Dictionary<string, object>)json[ResultSetJson.Key]);
            Check(Comparable(set) == Comparable(direct), "the runner's set is the study's (threads do not change it)");
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    static void TestBatchedObjective()
    {
        SimulationObjective Objective() => new SimulationObjective(ReferenceModels.ResourceSharedTeam(),
            new ModelParameter[] { ConfigParameter.Of(ReferenceModels.ResourceSharedTeam(), ParameterScope.Team, "team", ModelParameterCatalog.TeamMembers, 1, 6) },
            new[] { new ModelMetric("done", MetricScope.Element, "sink", "input"), new ModelMetric("ops", MetricScope.Team, "team", "members") },
            "done - 100 * ops") { Replications = 2, RunTime = 800 };

        // the batch sent as a file (as to the runner), run, read back
        ResultSet ViaFile(StudyBatch b, System.Threading.CancellationToken t) =>
            ResultSetJson.Deserialize(ResultSetJson.Serialize(StudyBatch.Deserialize(StudyBatch.Serialize(b)).Run(t)));

        var settings = new OptimizationSettings { MaxEvaluations = 24, Parallelism = 3, Seed = 5 };
        var classic = OptimizationEngine.Run(Objective().BuildSpace(), Objective(), new GeneticOptimizer { PopulationSize = 8, Generations = 3 }, settings);
        var objective = Objective();
        var batched = OptimizationEngine.Run(objective.BuildSpace(), new BatchedObjective(objective, ViaFile),
                                            new GeneticOptimizer { PopulationSize = 8, Generations = 3 },
                                            new OptimizationSettings { MaxEvaluations = 24, Parallelism = 3, Seed = 5 });
        Check(classic.Evaluations == batched.Evaluations && classic.Candidates.Count == batched.Candidates.Count, $"the same number of evaluations ({classic.Evaluations} / {batched.Evaluations})");
        bool same = true;
        for (int i = 0; i < Math.Min(classic.Candidates.Count, batched.Candidates.Count); i++)
        {
            var a = classic.Candidates[i];
            var b = batched.Candidates[i];
            same &= a.Iteration == b.Iteration && a.Values.SequenceEqual(b.Values) && a.Evaluation.Fitness.Equals(b.Evaluation.Fitness)
                    && a.Evaluation.Metrics.OrderBy(kv => kv.Key).SequenceEqual(b.Evaluation.Metrics.OrderBy(kv => kv.Key));
        }
        Check(same, "the same candidates, in the same order, with the same evaluations: the same search");
        Check(classic.BestEvaluation.Fitness.Equals(batched.BestEvaluation.Fitness) && classic.Best.Key == batched.Best.Key, "the same best");

        // a candidate whose model cannot be built fails alone, with the error the classic evaluation gives
        var obj2 = Objective();
        var space = obj2.BuildSpace();
        var good = new Candidate(space).SetNumber(0, 2);
        var all = new BatchedObjective(obj2, ViaFile).EvaluateAll(new[] { good }, 1, default);
        Check(all[0].Error == null && all[0].Fitness.Equals(obj2.Evaluate(good).Fitness), "one candidate: the classic evaluation");
    }

    static void TestStreaming()
    {
        var model = ReferenceModels.All().First(m => m.Key == "operator_pool").Value;
        var study = new Study(model) { Threads = 4, SeriesInterval = 100, RecordTimeline = true }.WithReplications(5).WithSeed(2);
        study.Metric("end", "model.end_time");
        var direct = study.Run();
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "simulean-runnertests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string file = System.IO.Path.Combine(dir, "batch.json");
            System.IO.File.WriteAllText(file, StudyBatch.Serialize(study.Batch()));
            var o = new System.IO.StringWriter();
            int code = SimuLean.Runner.RunnerCli.Run(new[] { "run", file, "--stream", "--quiet" }, o, new System.IO.StringWriter());
            var lines = o.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
            Check(code == 0 && lines.Count == 6, $"5 run lines and the set ({lines.Count} lines)");
            var runs = lines.Take(5).Select(l => (Dictionary<string, object>)MiniJson.Parse(l)).ToList();
            Check(runs.All(r => (string)r["kind"] == "run"), "each run on its own line");
            var last = (Dictionary<string, object>)MiniJson.Parse(lines[5]);
            Check((string)last["kind"] == "resultset" && (long)last["runs"] == 5, "the set last, with the run count");
            var set = ResultSetJson.FromJson((Dictionary<string, object>)last[ResultSetJson.Key]);
            Check(set.Runs.Count == 0, "the set without its runs (they were sent)");
            foreach (var r in runs.OrderBy(r => (long)r["index"])) set.Runs.Add(ResultSetJson.RunFromJson((Dictionary<string, object>)r["run"]));
            Check(Comparable(set) == Comparable(direct), "reassembled in index order: the study's set (series, timeline)");
            long whole = ResultSetJson.Serialize(direct).Length;
            Check(whole > 0, $"compact set: {whole / 1024} KB for 5 runs");
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    static void TestServe()
    {
        var model = ReferenceModels.All().First(m => m.Key == "operator_pool").Value;
        var a = new Study(model) { Threads = 2 }.WithReplications(3).WithSeed(1);
        a.Metric("end", "model.end_time");
        var b = new Study(ReferenceModels.All().First(m => m.Key == "basic_line").Value) { Threads = 2 }.WithReplications(2).WithSeed(4);
        b.Metric("end", "model.end_time");
        string input = StudyBatch.Serialize(a.Batch(), "a") + "\n" + "{\"kind\":\"nonsense\",\"id\":\"x\"}\n" +
                       StudyBatch.Serialize(b.Batch(), "b") + "\n{\"kind\":\"exit\"}\n";
        var o = new System.IO.StringWriter();
        int code = SimuLean.Runner.RunnerCli.Run(new[] { "serve" }, new System.IO.StringReader(input), o, new System.IO.StringWriter());
        var lines = o.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => (Dictionary<string, object>)MiniJson.Parse(l.Trim())).ToList();
        Check(code == 0 && (string)lines[0]["kind"] == "ready" && (string)lines[0]["engine"] == EngineBuild.Fingerprint, "ready first, with the engine");
        ResultSet Of(string id)
        {
            var mine = lines.Where(l => l.TryGetValue("id", out object i) && (string)i == id).ToList();
            var last = mine.Last();
            if ((string)last["kind"] != "resultset") return null;
            var set = ResultSetJson.FromJson((Dictionary<string, object>)last[ResultSetJson.Key]);
            foreach (var r in mine.Where(l => (string)l["kind"] == "run").OrderBy(l => (long)l["index"]))
                set.Runs.Add(ResultSetJson.RunFromJson((Dictionary<string, object>)r["run"]));
            return set;
        }
        var sa = Of("a");
        var sb = Of("b");
        Check(sa != null && Comparable(sa) == Comparable(a.Run()), "batch a, answered with its id: the study's set");
        Check(sb != null && Comparable(sb) == Comparable(b.Run()), "batch b in the same session: the study's set");
        var bad = lines.FirstOrDefault(l => l.TryGetValue("id", out object i) && (string)i == "x");
        Check(bad != null && (string)bad["kind"] == "error" && ((string)bad["message"]).Contains("nonsense"), "an unknown request answers an error and the session goes on");
    }

    static void TestErrors()
    {
        void Fails(string json, string expected, string what)
        {
            try { ConfigJson.Deserialize(json); Check(false, what + ": no error"); }
            catch (FormatException ex) { Check(ex.Message.Contains(expected), $"{what}: '{ex.Message}' names '{expected}'"); }
        }
        Fails("{\"format\":3,\"kind\":\"model\",\"model\":{}}", "format 3", "a newer format");
        Fails("{\"kind\":\"model\",\"model\":{}}", "\"format\"", "no format");
        Fails("{\"format\":2,\"kind\":\"study\",\"model\":{}}", "\"study\"", "another kind");
        Fails("{\"format\":2,\"kind\":\"model\",\"model\":{\"MaxTime\":5}}", "\"MaxTime\"", "an unknown member");
        Fails("{\"format\":2,\"kind\":\"model\",\"model\":{\"MaxSimulationTime\":\"long\"}}", "MaxSimulationTime", "a wrong value type");
        Fails("{\"format\":2,\"kind\":\"model\",\"model\":{\"Connections\":[{\"Parameters\":{\"port\":1}}]}}", "Parameters", "a connection's old parameter bag");
        Fails("{\"format\":2,\"kind\":\"model\",\"model\":{\"Elements\":[{\"Type\":\"Gadget\",\"Settings\":{}}]}}", "Gadget", "an unknown element type");
    }
}
