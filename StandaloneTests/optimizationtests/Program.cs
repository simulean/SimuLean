// Standalone tests of the optimisation core (SimuLean.Optimization): search spaces and
// candidates, the run services every algorithm shares (parallel evaluation, cache, budget, cancellation, history),
// the built-in algorithms (random search, genetic algorithm) and a plug-in algorithm written here.
// Run: dotnet run  (from StandaloneTests/optimizationtests)
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
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
        Run("Space and candidates: bounds, rounding, permutations, keys", TestSpace);
        Run("Run services: cache, budget, parallel, failures, cancellation, history", TestRunServices);
        Run("A NaN fitness (metric not reached) counts as the worst, never as the best", TestNaNFitness);
        Run("The time limit and a cancellation stop a batch being evaluated, not only between batches", TestStopInsideABatch);
        Run("Random search: finds a good point, reproducible with the seed", TestRandomSearch);
        Run("Genetic algorithm: mixed space reaches the optimum; same seed = same search, also in parallel", TestGenetic);
        Run("Plug-in: an algorithm written outside the engine uses the same services", TestPlugIn);
        // the model as an optimisation problem
        Run("SimulationConfig.Clone: deep copy of every reference model (no field forgotten)", TestClone);
        Run("Catalog: every key of elements, teams and downtimes, by type or by the schema", TestCatalog);
        Run("Catalog: no key of any reference model is undecided (new types / keys must be declared)", TestEveryKeyDecided);
        Run("Config parameters: choice, bool, vector, sampler number, number list, list of numbers written back", TestEncodings);
        Run("Options a|b|c are searched over those values only, numbers and properties too; a whole-number range of a real is noted", TestOptionsParameters);
        Run("Sampler parts that are integers (Binomial n, degrees of freedom, discrete bounds) are integer variables", TestSamplerIntegerParts);
        Run("A candidate's text is exact: stored values re-run as they were", TestExactCandidateText);
        Run("Mixed space with choices and vectors: random search and GA find the optimum; text round trip", TestChoicesAndVectors);
        Run("Model parameters: element key, team members, schedule sequence and binary column", TestModelParameters);
        Run("Declared text settings (a priority expression, a request order written in place) are study factors by their texts", TestTextSettingFactors);
        Run("The catalog lists text settings as a text kind (current text, no range); structural keys stay out", TestTextSettingsInTheCatalog);
        Run("Simulation objective: metrics, fitness expression, replications, same candidate = same result", TestObjective);
        Run("Team size: the optimiser finds the best number of operators (= exhaustive search)", TestTeamSize);
        Run("Simulation objective: downtime metrics (repair crews) and preemption metrics in the fitness", TestDowntimeMetricsObjective);
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    /// <summary>f = −(x−7)² − (y−2.5)² + (order sorted bonus) + (number of ones): maximum 0 + 5 + 6 = 11.</summary>
    sealed class Mixed : IObjective
    {
        public int Calls;
        public Evaluation Evaluate(Candidate c)
        {
            Interlocked.Increment(ref Calls);
            double x = c.Number("x"), y = c.Number("y");
            int[] order = c.Vector("order"), bits = c.Vector("bits");
            double sorted = 0;
            for (int i = 0; i < order.Length; i++) if (order[i] == i) sorted++;
            double ones = 0;
            foreach (int b in bits) ones += b;
            var e = new Evaluation { Fitness = -(x - 7) * (x - 7) - (y - 2.5) * (y - 2.5) + sorted + ones };
            e.Metrics["ones"] = ones;
            return e;
        }
    }

    static SearchSpace MixedSpace() => new SearchSpace()
        .Add(DecisionVariable.Integer("x", 0, 20))
        .Add(DecisionVariable.Real("y", -5, 5))
        .Add(DecisionVariable.Permutation("order", 5))
        .Add(DecisionVariable.Binary("bits", 6));

    static void TestSpace()
    {
        var space = MixedSpace();
        var c = new Candidate(space);
        c.SetNumber(0, 25.4);
        Check(c.Number("x") == 20, "integers clamp to the range");
        c.SetNumber(0, 3.5);
        Check(c.Number("x") == 4, "integers round");
        Check(string.Join(",", c.Vector("order")) == "0,1,2,3,4", "a new permutation is the identity");
        bool threw = false;
        try { c.SetVector(2, new[] { 0, 1, 1, 3, 4 }); } catch (ArgumentException) { threw = true; }
        Check(threw, "a repeated item is not a permutation");
        threw = false;
        try { c.SetVector(3, new[] { 0, 2, 0, 0, 0, 0 }); } catch (ArgumentException) { threw = true; }
        Check(threw, "binary values are 0 / 1");
        threw = false;
        try { space.Add(DecisionVariable.Integer("x", 0, 1)); } catch (ArgumentException) { threw = true; }
        Check(threw, "variable names are unique");
        var d = c.Clone();
        Check(d.Key == c.Key, "a clone has the same key");
        d.SetNumber(1, 1.25);
        Check(d.Key != c.Key, "a different point has another key");
    }

    static void TestRunServices()
    {
        var space = MixedSpace();
        var f = new Mixed();
        // cache: evaluating the same candidates twice simulates them once
        var r = OptimizationEngine.Run(space, f, new Fixed(new[] { 3, 3, 5 }, repeat: 2));
        Check(f.Calls == 2 && r.Evaluations == 2, $"repeated candidates are not evaluated again (calls {f.Calls}, evaluations {r.Evaluations})");

        // budget
        f = new Mixed();
        r = OptimizationEngine.Run(space, f, new RandomSearch { Samples = 1000 }, new OptimizationSettings { MaxEvaluations = 37 });
        Check(r.Evaluations == 37 && f.Calls == 37 && r.StopReason == "evaluation budget", $"the evaluation budget is exact ({r.Evaluations}, {r.StopReason})");

        // parallel evaluation gives the same results as sequential
        var seq = OptimizationEngine.Run(space, new Mixed(), new RandomSearch { Samples = 60 }, new OptimizationSettings { Seed = 5 });
        var par = OptimizationEngine.Run(space, new Mixed(), new RandomSearch { Samples = 60 }, new OptimizationSettings { Seed = 5, Parallelism = 4 });
        Check(seq.Best.Key == par.Best.Key && seq.BestEvaluation.Fitness == par.BestEvaluation.Fitness, "parallel = sequential");

        // a failing objective does not stop the run
        var failing = new Failing();
        r = OptimizationEngine.Run(space, failing, new RandomSearch { Samples = 20 });
        Check(r.Evaluations == 20 && r.BestEvaluation.Error == null && r.BestEvaluation.Fitness == 1, "failed evaluations rank last, the run goes on");

        // cancellation
        var cts = new CancellationTokenSource();
        r = OptimizationEngine.Run(space, new Mixed(), new RandomSearch { Samples = 10000, BatchSize = 10 },
                                   progress: p => { if (p.Iteration == 3) cts.Cancel(); }, cancellation: cts.Token);
        Check(r.StopReason == "cancelled" && r.Evaluations <= 40, $"cancellation stops the run ({r.Evaluations} evaluations)");

        // history: one point per iteration, best never decreases
        r = OptimizationEngine.Run(space, new Mixed(), new GeneticOptimizer { PopulationSize = 10, Generations = 8 });
        bool monotone = true;
        for (int i = 1; i < r.History.Count; i++) if (r.History[i].BestFitness < r.History[i - 1].BestFitness) monotone = false;
        Check(r.History.Count == 8 && monotone, $"history: {r.History.Count} generations, best monotone");

        // candidates: every simulated candidate once, with its values and evaluation, copied into a result set
        Check(r.Candidates.Count == r.Evaluations && r.Candidates.All(c => c.Values.Count == space.Count && c.Evaluation != null)
              && r.Candidates.Max(c => c.Evaluation.Fitness) == r.BestEvaluation.Fitness && r.Candidates.Last().Iteration <= 8,
              $"candidates: one per evaluation ({r.Candidates.Count} / {r.Evaluations}), the best among them");
        var set = new SimuLean.Results.ResultSet();
        r.CopyCandidatesTo(set);
        Check(set.Candidates.Count == r.Evaluations && set.CandidatesTable().RowCount == r.Evaluations
              && set.Candidates[0].Parameters[0].Key == space.Variables[0].Name, "candidates in the result set and its table");
        r = OptimizationEngine.Run(space, failing, new RandomSearch { Samples = 20 });
        Check(r.Candidates.Count(c => c.Evaluation.Error != null) > 0, "failed candidates kept with their error");
    }

    static void TestRandomSearch()
    {
        var space = new SearchSpace().Add(DecisionVariable.Integer("x", 0, 20)).Add(DecisionVariable.Real("y", -5, 5));
        var obj = new Quadratic();
        var a = OptimizationEngine.Run(space, obj, new RandomSearch { Samples = 400 }, new OptimizationSettings { Seed = 11 });
        var b = OptimizationEngine.Run(space, obj, new RandomSearch { Samples = 400 }, new OptimizationSettings { Seed = 11 });
        Check(a.BestEvaluation.Fitness > -0.5, $"close to the optimum ({a.BestEvaluation.Fitness:F3} at {a.Best})");
        Check(a.Best.Key == b.Best.Key, "same seed, same result");
    }

    static void TestGenetic()
    {
        var space = MixedSpace();
        var ga = new GeneticOptimizer { PopulationSize = 40, Generations = 60 };
        var a = OptimizationEngine.Run(space, new Mixed(), ga, new OptimizationSettings { Seed = 3 });
        Check(a.BestEvaluation.Fitness > 10.9, $"reaches the optimum 11 ({a.BestEvaluation.Fitness:F3}: {a.Best})");
        var b = OptimizationEngine.Run(space, new Mixed(), ga, new OptimizationSettings { Seed = 3, Parallelism = 4 });
        Check(a.Best.Key == b.Best.Key && a.Evaluations == b.Evaluations, "same seed = same search (parallel too)");
        bool sameHistory = a.History.Count == b.History.Count;
        for (int i = 0; sameHistory && i < a.History.Count; i++) sameHistory = a.History[i].BestFitness == b.History[i].BestFitness;
        Check(sameHistory, "identical history");
        var c = OptimizationEngine.Run(space, new Mixed(), ga, new OptimizationSettings { Seed = 4 });
        Check(c.Evaluations != a.Evaluations || c.History[1].BestFitness != a.History[1].BestFitness, "another seed searches differently");
        Check(a.Evaluations < 40 * 60, $"cache: fewer simulations than candidates ({a.Evaluations} < 2400)");
    }

    static void TestPlugIn()
    {
        var space = new SearchSpace().Add(DecisionVariable.Integer("x", 0, 20)).Add(DecisionVariable.Real("y", -5, 5));
        var r = OptimizationEngine.Run(space, new Quadratic(), new HillClimber(), new OptimizationSettings { MaxEvaluations = 300 });
        Check(r.Algorithm == "Hill climber" && r.BestEvaluation.Fitness > -0.05, $"the plug-in found {r.BestEvaluation.Fitness:F4} at {r.Best}");
        Check(r.Evaluations <= 300 && r.History.Count > 0, "budget and history from the shared run");
    }

    static void TestNaNFitness()
    {
        // the first candidate evaluated has no fitness (NaN: e.g. a makespan not reached); every later one is a number
        var r = OptimizationEngine.Run(MixedSpace(), new NaNFirst(), new Fixed(new[] { 20, 7, 3 }, repeat: 1));
        Check(r.Best != null && r.Best.Number("x") == 7, $"the best is x = 7, not the NaN candidate (best {r.Best})");
        Check(r.BestEvaluation.Fitness == 0, $"its fitness ({r.BestEvaluation.Fitness})");
        var ga = OptimizationEngine.Run(MixedSpace(), new NaNFirst(), new GeneticOptimizer(), new OptimizationSettings { Seed = 3, MaxEvaluations = 200 });
        Check(!double.IsNaN(ga.BestEvaluation.Fitness) && ga.BestEvaluation.Fitness > -1, $"the GA ignores NaN candidates ({ga.BestEvaluation.Fitness})");
    }

    static void TestStopInsideABatch()
    {
        // One batch of 20 candidates. What matters is that a stop ends the batch where it is, not at its end: counted in
        // evaluations, not in wall-clock time (a shared CI runner can stall a thread for longer than any time margin).
        var xs = Enumerable.Range(0, 20).ToArray();

        // A 0.3 s limit with evaluations of at least 50 ms (Thread.Sleep never returns early): at most 0.3 / 0.05 = 6
        // evaluations start before the limit, + 1 for the one running when it is reached. A slow runner evaluates fewer.
        var r = OptimizationEngine.Run(MixedSpace(), new Slow(), new Fixed(xs, repeat: 1), new OptimizationSettings { MaxSeconds = 0.3, Parallelism = 1 });
        Check(r.Evaluations >= 1 && r.Evaluations <= 7, $"time limit inside the batch ({r.Evaluations} evaluations of 20, at most 7)");

        // A cancellation requested by the third evaluation: the batch stops there, with at most one more evaluation per
        // other worker already running (Parallelism 2), whatever the timing.
        var cts = new CancellationTokenSource();
        r = OptimizationEngine.Run(MixedSpace(), new Slow(cancelAfter: 3, cts), new Fixed(xs, repeat: 1), new OptimizationSettings { Parallelism = 2 }, null, cts.Token);
        Check(r.Evaluations >= 3 && r.Evaluations <= 3 + 2, $"cancellation inside the batch ({r.Evaluations} evaluations of 20, at most 5)");
        Check(r.Best != null, "the candidates evaluated before the stop are kept");
    }

    /// <summary>An evaluation of at least 50 ms; optionally cancels a token when its n-th evaluation ends.</summary>
    sealed class Slow : IObjective
    {
        private readonly int cancelAfter; private readonly CancellationTokenSource cts; private int done;
        public Slow(int cancelAfter = 0, CancellationTokenSource cts = null) { this.cancelAfter = cancelAfter; this.cts = cts; }
        public Evaluation Evaluate(Candidate c)
        {
            Thread.Sleep(50);
            if (cts != null && Interlocked.Increment(ref done) == cancelAfter) cts.Cancel();
            return new Evaluation { Fitness = -Math.Abs(c.Number("x") - 7) };
        }
    }

    sealed class NaNFirst : IObjective
    {
        public Evaluation Evaluate(Candidate c)
            => new Evaluation { Fitness = c.Number("x") > 10 ? double.NaN : -Math.Pow(c.Number("x") - 7, 2) };
    }

    sealed class Quadratic : IObjective
    {
        public Evaluation Evaluate(Candidate c)
            => new Evaluation { Fitness = -Math.Pow(c.Number("x") - 7, 2) - Math.Pow(c.Number("y") - 2.5, 2) };
    }

    sealed class Failing : IObjective
    {
        public Evaluation Evaluate(Candidate c)
        {
            if (c.Number("x") > 10) throw new InvalidOperationException("model error");
            return new Evaluation { Fitness = 1 };
        }
    }

    /// <summary>Evaluates fixed candidates (x values) a number of times.</summary>
    sealed class Fixed : IOptimizationAlgorithm
    {
        private readonly int[] xs; private readonly int repeat;
        public Fixed(int[] xs, int repeat) { this.xs = xs; this.repeat = repeat; }
        public string Name => "Fixed";
        public void Run(OptimizationRun run)
        {
            for (int k = 0; k < repeat; k++)
            {
                var batch = new List<Candidate>();
                foreach (int x in xs) batch.Add(new Candidate(run.Space).SetNumber(0, x));
                run.Evaluate(batch);
                run.EndIteration();
            }
        }
    }

    /// <summary>A plug-in written outside the engine: steepest-ascent hill climbing with random restarts.</summary>
    sealed class HillClimber : IOptimizationAlgorithm
    {
        public string Name => "Hill climber";
        public void Run(OptimizationRun run)
        {
            while (!run.ShouldStop)
            {
                var current = run.RandomCandidate();
                double fit = run.Evaluate(current).Fitness;
                double step = 1.0;
                while (!run.ShouldStop && step > 1e-3)
                {
                    var neighbours = new List<Candidate>();
                    for (int i = 0; i < run.Space.Count; i++)
                        foreach (int sign in new[] { -1, 1 })
                        {
                            var v = run.Space.Variables[i];
                            double delta = v.Kind == VariableKind.Integer ? sign : sign * step;
                            neighbours.Add(current.Clone().SetNumber(i, current.Number(i) + delta));
                        }
                    var evals = run.Evaluate(neighbours);
                    int best = -1;
                    for (int k = 0; k < evals.Count; k++)
                        if (evals[k] != null && evals[k].Fitness > fit) { fit = evals[k].Fitness; best = k; }
                    if (best >= 0) current = neighbours[best];
                    else step /= 2;
                    run.EndIteration();
                }
            }
        }
    }

    // ── the model as an optimisation problem ──────────────────────────

    static string Fingerprint(object o)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(object v)
        {
            if (v == null) { sb.Append("null;"); return; }
            var t = v.GetType();
            if (t.IsPrimitive || t.IsEnum || v is string) { sb.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)).Append(';'); return; }
            if (v is System.Collections.IDictionary d)
            {
                var keys = new List<string>();
                foreach (System.Collections.DictionaryEntry kv in d) keys.Add(kv.Key.ToString());
                keys.Sort(StringComparer.Ordinal);
                sb.Append('{');
                foreach (var k in keys) { sb.Append(k).Append('='); Walk(d[k]); }
                sb.Append('}'); return;
            }
            if (v is System.Collections.IEnumerable e) { sb.Append('['); foreach (var x in e) Walk(x); sb.Append(']'); return; }
            sb.Append(t.Name).Append('(');
            foreach (var p in t.GetProperties()) if (p.GetIndexParameters().Length == 0) { sb.Append(p.Name).Append(':'); Walk(p.GetValue(v)); }
            foreach (var f in t.GetFields()) { sb.Append(f.Name).Append(':'); Walk(f.GetValue(v)); }
            sb.Append(')');
        }
        Walk(o);
        return sb.ToString();
    }

    static void Mutate(object v)
    {
        if (v == null || v is string || v.GetType().IsPrimitive || v.GetType().IsEnum) return;
        if (v is System.Collections.IDictionary d)
        {
            var vals = new List<object>();
            foreach (System.Collections.DictionaryEntry kv in d) vals.Add(kv.Value);
            foreach (var x in vals) Mutate(x);
            try { d["__mutated__"] = "x"; } catch { }
            return;
        }
        if (v is System.Collections.IList l)
        {
            foreach (var x in l) Mutate(x);
            if (!l.IsFixedSize) { try { l.Add(l.Count > 0 ? l[0] : null); } catch { } }
            else if (l.Count > 0 && l[0] is int) l[0] = (int)l[0] + 1000;
            return;
        }
        foreach (var p in v.GetType().GetProperties())
            if (p.GetIndexParameters().Length == 0)
            {
                var x = p.GetValue(v);
                if (x is string && p.CanWrite) p.SetValue(v, x + "!");
                else Mutate(x);
            }
    }

    static void TestClone()
    {
        int n = 0;
        foreach (var kv in ReferenceModels.All())
        {
            var original = kv.Value;
            string before = Fingerprint(original);
            var clone = original.Clone();
            Check(Fingerprint(clone) == before, $"{kv.Key}: the clone equals the original");
            Mutate(clone);
            Check(Fingerprint(original) == before, $"{kv.Key}: changing the clone leaves the original intact");
            n++;
        }
        Check(n == ReferenceModels.All().Count && n >= 28, $"all reference models ({n})");
    }

    static ParameterInfo Find(IReadOnlyList<ParameterInfo> list, string key) { foreach (var p in list) if (p.Key == key) return p; return null; }

    static void TestCatalog()
    {
        var line = ReferenceModels.BasicLine();
        var server = ModelParameterCatalog.ForElement(line.Elements.Find(e => e.Id == "srv"));
        var cap = Find(server, "capacity"); var rate = Find(server, "delayStrategy.exponentialRate"); var dist = Find(server, "delayStrategy.strategyType");
        Check(cap != null && cap.Kind == VariableKind.Integer && cap.Min >= 1 && cap.Label == "Servers", "a server offers its slots (integer, at least 1)");
        Check(rate != null && rate.Kind == VariableKind.Real && dist != null && dist.Kind == VariableKind.Choice && dist.CurrentText == "Exponential",
              "its service-time parameter (real) and its distribution (a choice)");
        var src = ModelParameterCatalog.ForElement(line.Elements.Find(e => e.Id == "src"));
        var srcRate = Find(src, "interArrivalDistribution.exponentialRate");
        Check(srcRate != null && Math.Abs(srcRate.Current - 1.0) < 1e-12 && Find(src, "mode")?.Kind == VariableKind.Choice,
              "an inter-arrival source offers its rate and its mode");
        var q = Find(ModelParameterCatalog.ForElement(line.Elements.Find(e => e.Id == "q")), "capacity");
        Check(q != null && q.Current == 100 && q.Max >= 200, "a queue offers its capacity with a range around it");
        Check(rate.Label == "Service time — Exponential rate" && srcRate.Label == "Inter-arrival time — Exponential rate",
              "a sampler's parameters are labelled by its role (service / inter-arrival time)");
        Check(Find(server, "delayStrategy.uniformMin") == null && Find(server, "delayStrategy.constantValue") == null,
              "only the chosen distribution's parameters are offered");
        var teamConfig = ReferenceModels.ResourceSharedTeam().ResourceTeams[0];
        var team = ModelParameterCatalog.ForTeam(teamConfig);
        Check(team[0].Key == ModelParameterCatalog.TeamMembers && team[0].Current == 2 && Find(team, "speed") == null && Find(team, "axisSpeed") == null,
              "an abstract team offers its members, not the travel properties it does not use");
        teamConfig.Travel = TravelKind.Network;
        team = ModelParameterCatalog.ForTeam(teamConfig);
        Check(Find(team, "speed") != null && Find(team, "traffic")?.Kind == VariableKind.Binary && Find(team, "axisSpeed") == null,
              "a network team offers its speed and its traffic control (not the axes')");
        Check(Find(team, "id") == null && Find(team, "name") == null, "names and references are not parameters");

        // a resource use's priority is an expression: a number parameter while it is a constant
        {
            var prio = ReferenceModels.BasicLine();
            var srvCfg = prio.Elements.Find(e => e.Id == "srv");
            srvCfg.Set("processBy.priority", "5");
            var offered = Find(ModelParameterCatalog.ForElement(srvCfg), "processBy.priority");
            Check(offered != null && offered.Kind == VariableKind.Real && offered.Current == 5, "a constant priority is a number parameter");
            var pp = ConfigParameter.Of(prio, ParameterScope.Element, "srv", "processBy.priority", 1, 9);
            var psp = new SearchSpace(); psp.Add(pp.Variable);
            var applied = prio.Clone(); pp.Apply(applied, new Candidate(psp).SetNumber(0, 7), 0);
            Check(applied.Elements.Find(e => e.Id == "srv").Get<string>("processBy.priority") == "7", "a level is written as the expression's text");
            srvCfg.Set("processBy.priority", "item.prio");
            var reads = Find(ModelParameterCatalog.ForElement(srvCfg), "processBy.priority");
            Check(reads != null && reads.IsText && !reads.IsNumber && reads.CurrentText == "item.prio"
                  && !System.Linq.Enumerable.Any(ModelParameterCatalog.Undecided(prio), u => u.Contains("processBy.priority")),
                  "a priority that reads the item is a text setting with its current text (and not undecided)");
        }
        ParameterInfo ttf = null;
        foreach (var (name, model) in ReferenceModels.All()) foreach (var d in model.Downtimes) ttf = ttf ?? Find(ModelParameterCatalog.ForDowntime(d), "timeToFailure.exponentialMean");
        Check(ttf != null && ttf.Encoding == ValueEncoding.Real && ttf.Label.Contains("mean"), "a failure's time to failure is a number of its distribution");
    }

    static void TestEveryKeyDecided()
    {
        int models = 0;
        foreach (var (name, model) in ReferenceModels.All())
        {
            models++;
            var undecided = ModelParameterCatalog.Undecided(model);
            Check(undecided.Count == 0, $"{name}: every key decided{(undecided.Count > 0 ? " - undecided: " + string.Join(", ", undecided) : "")}");
        }
        Check(models == ReferenceModels.All().Count && models >= 28, $"all reference models ({models})");
        // the settings classes are the one declaration — every declared setting of every element type is decided
        // (a text or a list declares its role), whatever a model happens to set
        foreach (string type in ElementSettings.TypeNames)
        {
            var open = ParameterSchema.Undecided(ElementSettings.TypeFor(type));
            Check(open.Count == 0, $"{type}: every declared setting decided{(open.Count > 0 ? " - undecided: " + string.Join(", ", open) : "")}");
        }
        var e = new ElementConfig("x", "Queue", "x");
        e.Set("capacity", 3);
        bool refused = false;
        try { e.Set("newTextKey", "abc"); } catch (ArgumentException x) { refused = x.Message.Contains("newTextKey"); }
        Check(refused, "a key the type's settings do not declare cannot be written");
        // an element type has only the shared groups it uses
        string sinkError = null;
        try { new ElementConfig("k", "Sink", "k").Set("processBy.team", "ops"); } catch (ArgumentException x) { sinkError = x.Message; }
        Check(sinkError != null && sinkError.Contains("processBy") && sinkError.Contains("inputStrategy") && !sinkError.Contains("outputStrategy"),
              "a Sink has no processBy, and the error lists the groups it has: " + sinkError);
        string sourceError = null;
        try { new ElementConfig("s", "Source", "s").Set("inputStrategy.strategyType", "SingleLabel"); } catch (ArgumentException x) { sourceError = x.Message; }
        Check(sourceError != null && sourceError.Contains("outputStrategy"), "a Source has no inputStrategy: " + sourceError);
        Check(SettingsPaths.Declares(typeof(MultiServerSettings), "processBy.team") && SettingsPaths.Declares(typeof(MultiServerSettings), "reserveExit")
              && !SettingsPaths.Declares(typeof(QueueSettings), "reserveExit") && !SettingsPaths.Declares(typeof(QueueSettings), "processBy.team"),
              "a Multi-Server has processBy and reserveExit, a Queue neither");
    }

    static void TestOptionsParameters()
    {
        var cfg = new SimulationConfig();
        var st = new ElementConfig("stk", "Stacker", "stk");
        st.Set("selectionRule", "FirstNotFull");
        st.Set(PropertyRecords.Key, new List<PropertySettings> { PropertySettings.Number("beds", 2), PropertySettings.OfText("tag", "x") });
        cfg.Elements.Add(st);
        var beds = new OptionsParameter(ConfigParameter.Of(cfg, ParameterScope.Element, "stk", PropertyRecords.Key + ".beds", 1, 3),
                                        new[] { "1", "2", "3" }, "stk.properties.beds");
        var rule = new OptionsParameter(ConfigParameter.Of(cfg, ParameterScope.Element, "stk", "selectionRule"), new[] { "RoundRobin", "FirstNotFull" });
        Check(beds.Variable.Kind == VariableKind.Choice && string.Join(",", beds.Variable.Options) == "1,2,3", "a numeric property given options is a choice of those values");
        Check(rule.Variable.Options.Count == 2, "a choice key given options keeps only those");
        var space = new SearchSpace();
        space.Add(beds.Variable); space.Add(rule.Variable);
        var copy = cfg.Clone();
        var c = new Candidate(space).SetText(0, "3").SetText(1, "RoundRobin");
        beds.Apply(copy, c, 0); rule.Apply(copy, c, 1);
        var bp = copy.Elements[0].Get<List<PropertySettings>>(PropertyRecords.Key);
        Check(bp[0].Value == 3 && bp[1].Text == "x", "the option written as the property's number");
        Check(copy.Elements[0].Get<string>("selectionRule") == "RoundRobin", "the choice written as its option");
        bool whole = true;
        for (int k = 0; k < 3; k++)
        {
            var cc = cfg.Clone(); beds.Apply(cc, new Candidate(space).SetNumber(0, k), 0);
            whole &= (cc.Elements[0].Get<List<PropertySettings>>(PropertyRecords.Key))[0].Value == k + 1;
        }
        Check(whole, "each value of the choice is one of the options (never 1.25)");
        bool threw = false;
        try { new OptionsParameter(ConfigParameter.Of(cfg, ParameterScope.Element, "stk", "selectionRule"), new[] { "Nope" }); } catch (FormatException) { threw = true; }
        Check(threw, "an option the key does not have is refused");
        var ranged = ConfigParameter.Of(cfg, ParameterScope.Element, "stk", PropertyRecords.Key + ".beds", 1, 3, "stk.properties.beds");
        string note = Study.RangeNote(ranged);
        Check(note != null && note.Contains("1|2|3"), "a whole-number range of a real property is noted with the options: " + note);
        Check(Study.RangeNote(ConfigParameter.Of(cfg, ParameterScope.Element, "stk", PropertyRecords.Key + ".beds", 0.5, 3.5)) == null, "a range with fractions is not noted");
        var study = new Study(ReferenceModels.MultiServer3()) { Threads = 1 };
        var cap = study.OptionsOf("ms.capacity", "2", "4");
        Check(cap is OptionsParameter && cap.Variable.Kind == VariableKind.Choice && cap.Variable.Options.Count == 2, "Study.OptionsOf: an integer key given 2|4 is those two values, not [2, 4]");
        Check(study.ParameterOf("ms.capacity", "2", "4").Variable.Kind == VariableKind.Integer, "Study.ParameterOf: still the range");
    }

    static void TestEncodings()
    {
        var cfg = new SimulationConfig();
        var st = new ElementConfig("stk", "Stacker", "stk");
        st.Set("selectionRule", "FirstNotFull");
        st.Set("cycleTime", "Uniform~1~3");
        st.Set("positionCapacities", new[] { 4, 6 });
        st.Set("acceptOversizeOnEmpty", true);
        st.Set(PropertyRecords.Key, new List<PropertySettings> { PropertySettings.Number("batch", 12), PropertySettings.OfText("tag", "x"), PropertySettings.OfNumbers("weights", 1, 2) });
        cfg.Elements.Add(st);
        var cb = new ElementConfig("cmb", "Combiner", "cmb");
        cb.Set("requirements", new[] { 2, 1 });
        cfg.Elements.Add(cb);
        cfg.ResourceTeams.Add(new ResourceTeamConfig { Id = "crane", Name = "crane", Travel = TravelKind.Axes, AxisSpeed = new[] { 1, 2, 0.5 },
                                                       Members = new List<ResourceMemberConfig> { new ResourceMemberConfig { Name = "c1", LoadTime = 2 } } });
        var ps = new ModelParameter[]
        {
            ConfigParameter.Of(cfg, ParameterScope.Element, "stk", "selectionRule"),
            ConfigParameter.Of(cfg, ParameterScope.Element, "stk", "cycleTime.uniformMax", 1, 10),
            ConfigParameter.Of(cfg, ParameterScope.Element, "stk", "positionCapacities", 1, 20),
            ConfigParameter.Of(cfg, ParameterScope.Element, "stk", "acceptOversizeOnEmpty"),
            ConfigParameter.Of(cfg, ParameterScope.Element, "cmb", "requirements", 1, 5),
            ConfigParameter.Of(cfg, ParameterScope.Team, "crane", "axisSpeed", 0, 4),
            ConfigParameter.Of(cfg, ParameterScope.Team, "crane", "loadTime", 0, 10),
            ConfigParameter.Of(cfg, ParameterScope.Team, "crane", ModelParameterCatalog.TeamMembers, 1, 5),
            ConfigParameter.Of(cfg, ParameterScope.Element, "stk", PropertyRecords.Key + ".batch", 1, 50),
            ConfigParameter.Of(cfg, ParameterScope.Element, "stk", PropertyRecords.Key + ".weights", 0, 9),
        };
        var space = new SearchSpace();
        foreach (var p in ps) space.Add(p.Variable);
        Check(space.Variables[0].Kind == VariableKind.Choice && space.Variables[2].Length == 2 && space.Variables[4].Kind == VariableKind.Integer
              && space.Variables[4].Length == 2 && space.Variables[5].Length == 3 && space.Variables[3].Kind == VariableKind.Binary, "kinds and lengths from the config");
        var c = new Candidate(space).SetText(0, "RoundRobin").SetNumber(1, 7.5).SetText(2, "8, 12").SetText(3, "false")
                                    .SetText(4, "3, 4").SetText(5, "2, 2, 1").SetNumber(6, 4).SetNumber(7, 3).SetNumber(8, 20).SetText(9, "3, 4");
        var copy = cfg.Clone();
        for (int i = 0; i < ps.Length; i++) ps[i].Apply(copy, c, i);
        var s2 = copy.Elements[0]; var t = copy.ResourceTeams[0];
        Check(s2.Get<string>("selectionRule") == "RoundRobin", "choice written as its option");
        Check(s2.Get<SamplerSettings>("cycleTime")?.ToString() == "Uniform~1~7.5", $"a distribution's number written ({s2.Get<SamplerSettings>("cycleTime")})");
        Check(string.Join(",", s2.Get<int[]>("positionCapacities")) == "8,12", "per-position capacities written as int[]");
        Check(s2.Get<bool>("acceptOversizeOnEmpty") == false, "bool written");
        Check(string.Join(",", copy.Elements[1].Get<int[]>("requirements")) == "3,4", "integer vector written as int[]");
        Check(string.Join("|", t.AxisSpeed) == "2|2|1", $"axis speeds written as numbers ({string.Join("|", t.AxisSpeed)})");
        Check(t.Members.Count == 3 && t.Members.TrueForAll(m => m.LoadTime == 4), "members resized; every member's load time set");
        var sp2 = s2.Get<List<PropertySettings>>(PropertyRecords.Key);
        Check(sp2[0].Value == 20 && sp2[1].Text == "x" && string.Join(",", sp2[2].Numbers) == "3,4", "user properties: numbers and number arrays rewritten, texts kept");
        Check(Find(ModelParameterCatalog.ForElement(cfg.Elements[0]), PropertyRecords.Key + ".tag") == null, "a text property is not a parameter");
        Check(cfg.Elements[0].Get<SamplerSettings>("cycleTime")?.ToString() == "Uniform~1~3", "the original config is untouched");

        // added members never take the name of a member already there
        var named = new SimulationConfig();
        named.ResourceTeams.Add(new ResourceTeamConfig { Id = "crew", Name = "crew", Members = new List<ResourceMemberConfig>
            { new ResourceMemberConfig { Name = "crew_3", Properties = new List<string> { "skill = 'weld'" } },
              new ResourceMemberConfig { Name = "crew_2", Properties = new List<string> { "skill='weld'" } } } });
        var sizeP = ConfigParameter.Of(named, ParameterScope.Team, "crew", ModelParameterCatalog.TeamMembers, 1, 6);
        var labelP = new TeamPropertyMembersParameter("crew", "skill", "weld", 0, 6);
        var sp = new SearchSpace(); sp.Add(sizeP.Variable); sp.Add(labelP.Variable);
        var bySize = named.Clone(); sizeP.Apply(bySize, new Candidate(sp).SetNumber(0, 4).SetNumber(1, 0), 0);
        var byLabel = named.Clone(); labelP.Apply(byLabel, new Candidate(sp).SetNumber(0, 1).SetNumber(1, 4), 1);
        bool Unique(SimulationConfig x) { var ns = x.ResourceTeams[0].Members.ConvertAll(m => m.Name); return new HashSet<string>(ns).Count == ns.Count; }
        Check(bySize.ResourceTeams[0].Members.Count == 4 && Unique(bySize) && byLabel.ResourceTeams[0].Members.Count == 4 && Unique(byLabel),
              "added members get names no other member has (" + string.Join(",", bySize.ResourceTeams[0].Members.ConvertAll(m => m.Name)) + " / "
              + string.Join(",", byLabel.ResourceTeams[0].Members.ConvertAll(m => m.Name)) + ")");
    }

    static void TestExactCandidateText()
    {
        var c = new Candidate(MixedSpace()).SetNumber(1, 1.23456789);
        string text = c.Text(1);
        var again = new Candidate(MixedSpace()).SetText(1, text);
        Check(again.Number(1) == 1.23456789, $"text {text} gives the same value back");
        Check(c.DisplayText(1) == "1.23457", $"display rounded ({c.DisplayText(1)})");
    }

    static void TestSamplerIntegerParts()
    {
        foreach (var (spec, part, integer) in new[] { ("Binomial~10~0.5", "binomialN", true), ("Binomial~10~0.5", "binomialP", false),
                                                       ("DiscreteUniform~1~6", "discreteUniformMax", true), ("ChiSquare~4", "chiSquareDf", true),
                                                       ("FDistribution~5~20", "fDistD2", true), ("Normal~5~1", "normalMean", false) })
        {
            var cfg = new SimulationConfig();
            var st = new ElementConfig("stk", "Stacker", "stk");
            st.Set("cycleTime", spec);
            cfg.Elements.Add(st);
            var p = ConfigParameter.Of(cfg, ParameterScope.Element, "stk", "cycleTime." + part, 1, 30);
            Check((p.Variable.Kind == VariableKind.Integer) == integer, $"{spec} part {part}: {(integer ? "integer" : "real")} ({p.Variable.Kind})");
            var space = new SearchSpace().Add(p.Variable);
            var copy = cfg.Clone();
            p.Apply(copy, new Candidate(space).SetNumber(0, integer ? 7.3 : 0.3), 0);
            string written = copy.Elements[0].Get<SamplerSettings>("cycleTime")?.ToString();
            bool decodes = true;
            try { SamplerSpec.Decode(written, new SimuLean.SimClock(1), "stk", "cycleTime"); } catch (Exception) { decodes = false; }
            Check(decodes, $"{spec} part {part} (7.3 / 0.3) gives a valid spec ({written})");
        }
    }

    sealed class ChoiceVectorObjective : IObjective
    {
        public Evaluation Evaluate(Candidate c)
        {
            var e = new Evaluation();
            double f = c.Option(0) == "b" ? 10 : 0;
            foreach (double x in c.Values(1)) f += x;                    // integers 0..3: best all 3
            foreach (double x in c.Values(2)) f -= Math.Abs(x - 0.5);    // reals: best 0.5
            e.Fitness = f;
            return e;
        }
    }

    static void TestChoicesAndVectors()
    {
        var space = new SearchSpace().Add(DecisionVariable.Choice("opt", new[] { "a", "b", "c" }))
                                     .Add(DecisionVariable.Integer("ints", 0, 3, 3)).Add(DecisionVariable.Real("reals", 0, 1, 2));
        var c = new Candidate(space).SetText(0, "c").SetText(1, "1, 2, 3").SetText(2, "0.25, 0.75");
        var back = new Candidate(space).SetText(0, c.Text(0)).SetText(1, c.Text(1)).SetText(2, c.Text(2));
        Check(back.Key == c.Key && c.Option(0) == "c" && c.Text(1) == "1, 2, 3", "text round trip (choice by name, vectors by commas)");
        Check(new Candidate(space).SetText(0, "1").Option(0) == "b" && new Candidate(space).SetText(1, "9, -1, 2").Text(1) == "3, 0, 2",
              "a choice by index; vector values clamped");
        bool threw = false;
        try { new Candidate(space).SetText(0, "zzz"); } catch (FormatException) { threw = true; }
        Check(threw, "an unknown option is refused");
        var ga = OptimizationEngine.Run(space, new ChoiceVectorObjective(),
                                        new GeneticOptimizer { PopulationSize = 30, Generations = 40 }, new OptimizationSettings { Seed = 3, Parallelism = 2 });
        Check(ga.Best.Option(0) == "b" && ga.Best.Text(1) == "3, 3, 3" && ga.BestEvaluation.Fitness > 18.8,
              $"GA: the best option, every integer at its best, reals close ({ga.Best}, {ga.BestEvaluation.Fitness:F3})");
        var rs = OptimizationEngine.Run(space, new ChoiceVectorObjective(), new RandomSearch { Samples = 300 }, new OptimizationSettings { Seed = 3 });
        Check(rs.Best.Option(0) == "b", $"random search samples choices and vectors ({rs.Best})");
    }

    static void TestTextSettingFactors()
    {
        var model = ReferenceModels.ResourceRequestPriority();
        Check(ModelParameterCatalog.IsTextSetting(model, ParameterScope.Element, "high", "processBy.priority")
              && ModelParameterCatalog.IsTextSetting(model, ParameterScope.Team, "ops", "requestOrder"), "a priority expression and a request order are text settings");
        Check(!ModelParameterCatalog.IsTextSetting(model, ParameterScope.Element, "high", "capacity")
              && !ModelParameterCatalog.IsTextSetting(model, ParameterScope.Element, "high", "processBy.priorty")
              && !ModelParameterCatalog.IsTextSetting(model, ParameterScope.Element, "nobody", "processBy.priority"), "a number, a misspelt key or owner is not");

        Study Make() => new Study(ReferenceModels.ResourceRequestPriority()) { Threads = 1 }.WithRunTime(200)
            .Factor("high.processBy.priority", "prio", "1").Factor("team:ops.requestOrder", "|priority desc", "")
            .Metric("out", "sink.input");
        var study = Make();
        var plan = study.Plan();
        Check(plan.Count == 4, "the texts are the levels: 2 × 2 scenarios, " + plan.Count);
        Check(study.Validate() == null, "the study plans: " + study.Validate());
        var check = study.Check();
        Check(check.Ok, "the study checks on the built model (simulean_validate): " + string.Join(" | ", check.Errors));
        Check(study.CurrentLevel("high.processBy.priority") == "prio" && study.CurrentLevel("team:ops.requestOrder") == "|priority desc",
              "the model's current texts are the current levels");

        var fifo = plan.Find(s => s.Values[0] == "1" && s.Values[1] == "");
        var cfg = ReferenceModels.ResourceRequestPriority();
        study.Apply(cfg, fifo.Name);
        Check(cfg.Elements.Find(e => e.Name == "high").Get<string>("processBy.priority") == "1"
              && cfg.ResourceTeams.Find(t => t.Name == "ops").RequestOrder == "", "Study.Apply writes the texts to the config");
        var set = Make().Run();
        Check(set.Runs.Count == 4 && set.Runs.All(r => r.Error == null), "every scenario runs: " + string.Join(" | ", set.Runs.Select(r => r.Error)));

        // a text with quotes, commas and parentheses reaches the study whole from a JSON factors object
        var spec = new StudySpec
        {
            Factors = "{\"high.processBy.priority\": \"max(prio, 2) + (prio == 5 ? 1 : 0)\", \"team:ops.requestOrder\": \"|priority desc\"}",
            Metrics = "{\"out\": \"sink.input\"}", Horizon = 200,
        };
        var fromJson = new Study(ReferenceModels.ResourceRequestPriority()) { Threads = 1 };
        spec.ApplyTo(fromJson);
        var one = fromJson.Plan();
        Check(one.Count == 1 && one[0].Values[0] == "max(prio, 2) + (prio == 5 ? 1 : 0)" && one[0].Values[1] == "|priority desc",
              "a JSON factor's text is one level, intact: " + string.Join(" ; ", one[0].Values));
        var applied = ReferenceModels.ResourceRequestPriority();
        fromJson.Apply(applied, one[0].Name);
        Check(applied.Elements.Find(e => e.Name == "high").Get<string>("processBy.priority") == "max(prio, 2) + (prio == 5 ? 1 : 0)",
              "and is written as given");
        var built = fromJson.Run();
        Check(built.Runs.Count == 1 && built.Runs[0].Error == null, "and the run builds and runs with it: " + built.Runs[0].Error);

        // expression levels with '/' keep distinct scenario names (a file path level is shortened to its name)
        var names = new Study(ReferenceModels.ResourceRequestPriority()).Factor("high.processBy.priority", "prio / 2", "prio * 2 / 2").Plan();
        Check(names.Select(s => s.Name).Distinct().Count() == 2 && names[0].Name.Contains("prio / 2"), "expression levels name their scenarios whole: " + string.Join(" ; ", names.Select(s => s.Name)));

        // an optimiser's options a|b over a text setting search those texts
        var options = Make().OptionsOf("team:ops.requestOrder", "|priority desc", "|priority asc");
        Check(options is TextSettingParameter && options.Variable.Kind == VariableKind.Choice && options.Variable.Options.Count == 2,
              "options over a text setting are its texts");
        // a constant priority is still a number; a text among the levels makes it a text factor
        var preempt = new Study(ReferenceModels.ResourcePreemption()) { Threads = 1 };
        Check(preempt.ParameterOf("urgent.processBy.priority", "3", "8") is ConfigParameter, "a constant priority given numbers stays a number");
        Check(preempt.ParameterOf("urgent.processBy.priority", "5", "item.prio") is TextSettingParameter, "given a text it is a text factor");

        string error = new Study(ReferenceModels.ResourceRequestPriority()).Factor("high.processBy.priorty", "prio").Validate();
        Check(error != null && error.Contains("has no parameter"), "a misspelt key still fails with 'has no parameter': " + error);
        error = new Study(ReferenceModels.ResourceRequestPriority()).Factor("team:ops.requestOrdr", "priority desc").Validate();
        Check(error != null && error.Contains("has no parameter"), "and a misspelt team key: " + error);
    }

    static void TestTextSettingsInTheCatalog()
    {
        var model = ReferenceModels.ResourceRequestPriority();
        var prio = ModelParameterCatalog.ForElement(model.Elements.Find(e => e.Name == "high")).FirstOrDefault(p => p.Key == "processBy.priority");
        Check(prio != null && prio.IsText && prio.CurrentText == "prio" && !prio.IsNumber && prio.Options.Count == 0,
              "a priority expression is listed as a text setting with its current text, no range and no options");
        var order = ModelParameterCatalog.ForTeam(model.ResourceTeams[0]).FirstOrDefault(p => p.Key == "requestOrder");
        Check(order != null && order.IsText && order.CurrentText == "|priority desc", "a request order written in place is listed as a text setting");

        // a station with a process team and no priority: its priority is listed, empty
        var preempt = ReferenceModels.ResourcePreemption();
        var unset = ModelParameterCatalog.ForElement(preempt.Elements.Find(e => e.Name == "routine")).FirstOrDefault(p => p.Key == "processBy.priority");
        Check(unset != null && unset.IsText && unset.CurrentText == "", "an unset priority of a station with a process team is listed, empty");
        Check(!ModelParameterCatalog.ForElement(preempt.Elements.Find(e => e.Name == "sink")).Any(p => p.IsText && p.Key.StartsWith("processBy.")),
              "an element without the group lists none of its texts");

        // the numeric search never builds a variable of it; only given texts vary it
        bool threw = false;
        try { prio.Variable("x", 0, 1); } catch (ArgumentException e) { threw = e.Message.Contains("give the texts"); }
        Check(threw, "a text setting has no variable of its own");
        string error = null;
        try { ConfigParameter.Of(model, ParameterScope.Element, "high", "processBy.priority", 1, 9); } catch (ArgumentException e) { error = e.Message; }
        Check(error != null && error.Contains("is a text setting (now 'prio')"), "ConfigParameter.Of refuses it, naming its current text: " + error);
        Check(TextSettingParameter.Applies(model, ParameterScope.Element, "high", "processBy.priority")
              && TextSettingParameter.Applies(preempt, ParameterScope.Element, "routine", "processBy.priority")
              && !TextSettingParameter.Applies(preempt, ParameterScope.Element, "urgent", "processBy.priority"),
              "a text, or unset, priority is varied by texts; a constant one stays a number");

        // a structural key (a name, a reference) is never a factor
        Check(!ModelParameterCatalog.IsTextSetting(model, ParameterScope.Element, "high", "processBy.team"), "a team reference is not a text setting");
        Check(!ModelParameterCatalog.IsTextSetting(model, ParameterScope.Element, "high", "processBy.requirements[0].priority"),
              "nor a record's expression (a list's records are not factors)");
        error = new Study(ReferenceModels.ResourceRequestPriority()).Factor("high.processBy.team", "ops", "other").Validate();
        Check(error != null && error.Contains("has no parameter"), "a structural key is refused as a factor: " + error);
        Check(!ModelParameterCatalog.ForElement(model.Elements.Find(e => e.Name == "high")).Any(p => p.Key == "processBy.team"),
              "and is not in the catalog");

        // every key stays decided, and the listing adds no undecided key
        Check(ModelParameterCatalog.Undecided(model).Count == 0 && ModelParameterCatalog.Undecided(preempt).Count == 0, "every key decided");
    }

    static void TestModelParameters()
    {
        var cfg = ReferenceModels.ResourceSharedTeam();
        var ps = new ModelParameter[]
        {
            ConfigParameter.Of(cfg, ParameterScope.Element, "q", "capacity", 1, 50),
            ConfigParameter.Of(cfg, ParameterScope.Team, "team", ModelParameterCatalog.TeamMembers, 1, 6),
        };
        var space = new SearchSpace();
        foreach (var p in ps) space.Add(p.Variable);
        var cand = new Candidate(space).SetNumber(0, 7).SetNumber(1, 4);
        var copy = cfg.Clone();
        for (int i = 0; i < ps.Length; i++) ps[i].Apply(copy, cand, i);
        Check(copy.Elements.Find(e => e.Id == "q").Get<int>("capacity") == 7, "element key set (int)");
        var t = copy.ResourceTeams[0];
        Check(t.Members.Count == 4 && t.Members[3].Name == "team_4", "team grown to 4 members, new ones named");
        cand.SetNumber(1, 1);
        ps[1].Apply(copy, cand, 1);
        Check(copy.ResourceTeams[0].Members.Count == 1, "team shrunk to 1");
        Check(cfg.ResourceTeams[0].Members.Count == 2, "the original config is untouched");

        // schedule tables: the jobs move, the time slots stay
        var sched = new SimulationConfig();
        var src = new ElementConfig("s", "Source", "s");
        src.Set("arrivalsTable", new Dictionary<string, List<string>>
        {
            ["Time"] = new List<string> { "0", "10", "20" }, ["Name"] = new List<string> { "A", "B", "C" }, ["due"] = new List<string> { "5", "6", "7" },
        });
        sched.Elements.Add(src);
        var seq = new ScheduleSequenceParameter("s", 3);
        var bin = new ScheduleBinaryParameter("s", "urgent", 3);
        var sp = new SearchSpace().Add(seq.Variable).Add(bin.Variable);
        var c2 = new Candidate(sp).SetVector(0, new[] { 2, 0, 1 }).SetVector(1, new[] { 1, 0, 1 });
        seq.Apply(sched, c2, 0); bin.Apply(sched, c2, 1);
        var table = sched.Elements[0].Get<Dictionary<string, List<string>>>("arrivalsTable");
        Check(string.Join(",", table["Name"]) == "C,A,B" && string.Join(",", table["due"]) == "7,5,6", "rows reordered with their columns");
        Check(string.Join(",", table["Time"]) == "0,10,20", "the time slots stay in place");
        Check(string.Join(",", table["urgent"]) == "1,0,1", "binary decisions written as a column");
        Check(ModelParameterCatalog.ScheduleRows(sched.Elements[0]) == 3, "rows of the table");
    }

    static void TestDowntimeMetricsObjective()
    {
        var model = ReferenceModels.RepairCrew();
        var mechanics = ConfigParameter.Of(model, ParameterScope.Team, "mech", ModelParameterCatalog.TeamMembers, 1, 2, "mechanics");
        var obj = new SimulationObjective(model, new ModelParameter[] { mechanics },
            new[] { new ModelMetric("wait", MetricScope.Downtime, "machineFail@m1", "repair_wait_avg"),
                    new ModelMetric("repairs", MetricScope.Downtime, "machineFail@m1", "repairs"),
                    new ModelMetric("pre", MetricScope.Team, "mech", "preemptions"),
                    new ModelMetric("jobs", MetricScope.Element, "m1", "interruptions") },
            "-wait - 10 * mechanics") { RunTime = 3000 };
        var space = obj.BuildSpace();
        var one = obj.Evaluate(new Candidate(space).SetNumber(0, 1));
        var two = obj.Evaluate(new Candidate(space).SetNumber(0, 2));
        Check(one.Metrics["repairs"] > 0 && one.Metrics["wait"] > 0, $"the downtime's repairs and wait read ({one.Metrics["repairs"]}, {one.Metrics["wait"]})");
        Check(two.Metrics["wait"] < one.Metrics["wait"], $"a second mechanic shortens the wait for the repair ({one.Metrics["wait"]} → {two.Metrics["wait"]})");
        Check(Math.Abs(one.Fitness - (-one.Metrics["wait"] - 10)) < 1e-9, "the fitness over a downtime metric");
        Check(one.Metrics["pre"] >= 0 && one.Metrics["jobs"] > 0, "team preemptions and a station's interruptions read");
        // a team without a preemption use: its preemption statistics are 0, not an error
        var plain = new SimulationObjective(ReferenceModels.ResourceSharedTeam(), new ModelParameter[] { Members() },
            new[] { new ModelMetric("pre", MetricScope.Team, "team", "preemptions") }, "pre") { RunTime = 200 };
        Check(plain.Evaluate(new Candidate(plain.BuildSpace()).SetNumber(0, 2)).Metrics["pre"] == 0, "no preemption use: 0");
    }

    static ModelParameter Members(string name = null)
        => ConfigParameter.Of(ReferenceModels.ResourceSharedTeam(), ParameterScope.Team, "team", ModelParameterCatalog.TeamMembers, 1, 6, name);

    static SimulationObjective TeamObjective(string fitness, int reps = 1)
        => new SimulationObjective(ReferenceModels.ResourceSharedTeam(),
               new ModelParameter[] { Members() },
               new[] { new ModelMetric("done", MetricScope.Element, "sink", "input"),
                       new ModelMetric("ops", MetricScope.Team, "team", "members"),
                       new ModelMetric("busy", MetricScope.Team, "team", "utilization") },
               fitness) { Replications = reps, RunTime = 1000 };

    static void TestObjective()
    {
        var obj = TeamObjective("done - 100 * ops");
        var space = obj.BuildSpace();
        var c = new Candidate(space).SetNumber(0, 2);
        var a = obj.Evaluate(c); var b = obj.Evaluate(c);
        Check(a.Fitness == b.Fitness && a.Metrics["done"] > 0, $"same candidate, same result ({a.Fitness})");
        Check(Math.Abs(a.Fitness - (a.Metrics["done"] - 100 * a.Metrics["ops"])) < 1e-9 && a.Metrics["ops"] == 2, "fitness = expression over the metrics");
        Check(a.Metrics["busy"] > 0 && a.Metrics["busy"] <= 1, "team utilisation read");
        var r3 = TeamObjective("done", reps: 3).Evaluate(new Candidate(space).SetNumber(0, 2));
        Check(r3.Metrics["done"] != a.Metrics["done"], "three replications average different runs");
        int inspected = 0;
        var one = obj.Run(c, obj.BaseSeed, r => inspected = new List<SimuLean.Results.ResultObject>(r.Of(SimuLean.Results.ResultScope.Element)).Count);
        Check(one.Fitness == a.Fitness && inspected > 0, "a single run with the base seed is the one-replication evaluation; its statistics are given");
        var reps = TeamObjective("done", reps: 3);
        double mean = 0; for (int s = 0; s < 3; s++) mean += reps.Run(c, reps.BaseSeed + s).Metrics["done"] / 3;
        Check(Math.Abs(mean - r3.Metrics["done"]) < 1e-9, "the replications are the runs with seeds base, +1, +2");
        bool threw = false;
        try { TeamObjective("done - cost"); } catch (ArgumentException) { threw = true; }
        Check(threw, "a fitness naming an unknown metric is refused");
        var named = new SimulationObjective(ReferenceModels.ResourceSharedTeam(),
            new ModelParameter[] { Members("operators") },
            new[] { new ModelMetric("done", MetricScope.Element, "sink", "input") }, "done - 100 * operators") { RunTime = 1000 };
        var n = named.Evaluate(new Candidate(named.BuildSpace()).SetNumber(0, 2));
        Check(Math.Abs(n.Fitness - (a.Metrics["done"] - 200)) < 1e-9, "a numeric parameter is a variable of the fitness");
        // a study's parameters are named by their paths (simulean_optimize_start): the fitness reads the path written as
        // `where` writes it — the docs promise it, so `B1_capacity` must be accepted
        var byPath = new SimulationObjective(ReferenceModels.ResourceSharedTeam(),
            new ModelParameter[] { Members("team:team.members") },
            new[] { new ModelMetric("done", MetricScope.Element, "sink", "input") }, "done - 100 * team_team_members") { RunTime = 1000 };
        var p = byPath.Evaluate(new Candidate(byPath.BuildSpace()).SetNumber(0, 2));
        Check(Math.Abs(p.Fitness - (a.Metrics["done"] - 200)) < 1e-9, "a parameter named by its path is read as team_team_members");
        threw = false;
        try { new SimulationObjective(ReferenceModels.ResourceSharedTeam(),
                  new ModelParameter[] { Members("done") },
                  new[] { new ModelMetric("done", MetricScope.Element, "sink", "input") }, "done"); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "a parameter and a metric cannot share a name");
        var extra = new SimulationObjective(ReferenceModels.ResourceSharedTeam(),
            new ModelParameter[] { Members() },
            new[] { new ModelMetric("done", MetricScope.Element, "sink", "input"),
                    new ModelMetric("viaMember", MetricScope.Element, "sink", "member:GetStatsInputCount"),
                    new ModelMetric("now", MetricScope.Element, "sink", "content"),
                    new ModelMetric("fastest", MetricScope.Element, "sink", "stay_min") }, "done") { RunTime = 1000 };
        var x = extra.Evaluate(new Candidate(extra.BuildSpace()).SetNumber(0, 2));
        Check(x.Metrics["viaMember"] == x.Metrics["done"] && x.Metrics["now"] >= 0 && x.Metrics["fastest"] >= 0,
              "member:NAME reads a public number of the engine element; current content and min stay are offered");
        var noMember = new SimulationObjective(ReferenceModels.ResourceSharedTeam(), new ModelParameter[] { Members() },
            new[] { new ModelMetric("m", MetricScope.Element, "sink", "member:Nope") }, "m");
        var nm = OptimizationEngine.Run(noMember.BuildSpace(), noMember, new RandomSearch { Samples = 1 });
        Check(nm.BestEvaluation.Error != null && nm.BestEvaluation.Error.Contains("Nope"), "an unknown member is an evaluation error naming it");
        var bad = new SimulationObjective(ReferenceModels.ResourceSharedTeam(),
            new ModelParameter[] { new ConfigParameter(ParameterScope.Element, "nope", new ParameterInfo { Key = "capacity", Kind = VariableKind.Integer, Encoding = ValueEncoding.Integer }, 1, 5) },
            new[] { new ModelMetric("done", MetricScope.Element, "sink", "input") }, "done");
        var r = OptimizationEngine.Run(bad.BuildSpace(), bad, new RandomSearch { Samples = 3 });
        Check(r.BestEvaluation.Error != null && r.BestEvaluation.Error.Contains("nope"), "a model error is an evaluation error naming the cause");
    }

    static void TestTeamSize()
    {
        // each extra operator costs; throughput saturates: an interior optimum
        var obj = TeamObjective("done - 120 * ops");
        var space = obj.BuildSpace();
        double bestF = double.NegativeInfinity; int bestN = 0;
        var byN = new double[7];
        for (int n = 1; n <= 6; n++)
        {
            var e = obj.Evaluate(new Candidate(space).SetNumber(0, n));
            byN[n] = e.Fitness;
            if (e.Fitness > bestF) { bestF = e.Fitness; bestN = n; }
        }
        Check(bestN > 1 && bestN < 6, $"interior optimum ({bestN} operators; fitness by size: {string.Join(" ", byN)})");
        var ga = OptimizationEngine.Run(space, obj, new GeneticOptimizer { PopulationSize = 6, Generations = 6 }, new OptimizationSettings { Parallelism = 4 });
        Check((int)ga.Best.Number(0) == bestN && ga.BestEvaluation.Fitness == bestF, $"GA finds {bestN} operators (found {ga.Best})");
        Check(ga.Evaluations <= 6, $"each team size simulated once ({ga.Evaluations})");
    }
}
