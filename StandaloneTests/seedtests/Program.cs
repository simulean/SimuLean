// Seeding and per-element random streams: one stream per element, derived from the
// model seed and the element's identity (name + purpose), so adding / removing / reordering other
// elements does not change what an element draws (common random numbers), runs are reproducible for a
// seed (also in parallel threads) and unseeded runs record the fresh seed they drew.
// Run: dotnet run  (from StandaloneTests/seedtests)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SimuLean;
using SimuLean.Serialization;
using SimuLean.Stochastics.Distributions;
using SimuLean.Stochastics.Streams;
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
        Run("stream seed derivation is stable (pinned values) and key / seed sensitive", TestDerivation);
        Run("same seed -> identical results; different seeds differ", TestSameSeed);
        Run("adding an unrelated branch does not change the existing branch", TestUnrelatedBranch);
        Run("element order in the config does not change the results", TestOrderIndependence);
        Run("parallel runs are independent and match the sequential ones", TestParallel);
        Run("unseeded runs draw a fresh seed that reproduces the run", TestUnseeded);
        Run("Reseed on a reused clock = a new clock with that seed", TestReseed);
        Run("duplicate keys get distinct streams; deterministic samplers consume none", TestDuplicateKeys);
        Run("discrete process strategies draw from their element stream", TestDiscreteStrategies);

        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    static ReferenceModelRunner.Result RunModel(SimulationConfig c) => ReferenceModelRunner.Run("m", c);

    static string Fingerprint(ReferenceModelRunner.Result r, IEnumerable<string> ids)
        => string.Join(";", ids.Select(id => id + ":" + string.Join(",",
               r.Stats[id].Select(kv => kv.Key + "=" + kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))));

    static readonly string[] LineIds = { "src", "q", "srv", "sink" };

    /// <summary>The basic line's elements and connections with every id / name suffixed (an independent copy).</summary>
    static (List<ElementConfig>, List<ConnectionConfig>) Branch(string suffix)
    {
        var c = ReferenceModels.BasicLine();
        foreach (var e in c.Elements) { e.Id += suffix; e.Name += suffix; }
        foreach (var k in c.Connections) { k.SourceId += suffix; k.TargetId += suffix; }
        return (c.Elements, c.Connections);
    }

    static SimulationConfig BasicLine(int seed)
    {
        var c = ReferenceModels.BasicLine();
        c.Seed = seed;
        return c;
    }

    // ── tests ────────────────────────────────────────────────────────────────

    static void TestDerivation()
    {
        // Pinned: a change here changes every seeded result (and Unity ↔ headless parity) — on purpose only.
        int a = SimContext.DeriveStreamSeed(12345, "Lathe/delay");
        int b = SimContext.DeriveStreamSeed(12345, "Src/interArrival");
        Console.WriteLine($"  DeriveStreamSeed(12345, Lathe/delay) = {a}, (12345, Src/interArrival) = {b}");
        Check(a == PinnedLathe, $"pinned Lathe/delay value ({a} vs {PinnedLathe})");
        Check(b == PinnedSrc, $"pinned Src/interArrival value ({b} vs {PinnedSrc})");
        Check(a >= 0 && b >= 0, "non-negative");
        Check(a != b, "different keys differ");
        Check(SimContext.DeriveStreamSeed(12346, "Lathe/delay") != a, "different model seeds differ");
        Check(SimContext.DeriveStreamSeed(12345, "Lathe/delay") == a, "repeatable");
        Check(SimContext.StreamKey("Lathe", "delay") == "Lathe/delay" && SimContext.StreamKey("X", null) == "X/sampler", "stream keys");
    }

    // Values of DeriveStreamSeed(12345, …) — FNV-1a 64 + SplitMix64, see SimContext.
    const int PinnedLathe = 310627052;   // cross-checked with an independent Python implementation
    const int PinnedSrc = 446228524;

    static void TestSameSeed()
    {
        var all = LineIds;
        string a1 = Fingerprint(RunModel(BasicLine(7)), all);
        string a2 = Fingerprint(RunModel(BasicLine(7)), all);
        string b = Fingerprint(RunModel(BasicLine(8)), all);
        Check(a1 == a2, "same seed reproducible");
        Check(a1 != b, "different seeds differ");
    }

    static void TestUnrelatedBranch()
    {
        string alone = Fingerprint(RunModel(BasicLine(42)), LineIds);

        // A second, independent source → queue → server → sink added in FRONT of the existing elements
        // (it is built and creates its streams first).
        var c = BasicLine(42);
        var (els, cons) = Branch("_2");
        c.Elements.InsertRange(0, els);
        c.Connections.InsertRange(0, cons);
        var withBranch = RunModel(c);
        Check(Fingerprint(withBranch, LineIds) == alone, "existing branch statistics unchanged by the added branch");
        Check(withBranch.Stats["sink_2"]["input"] > 0, "the added branch runs too");
        Check(withBranch.Stats["q_2"]["stay_avg"] != withBranch.Stats["q"]["stay_avg"], "the added branch draws its own numbers");

        // Removing a branch is the same check backwards; also add two branches (after the existing ones).
        var c3 = BasicLine(42);
        var (e2, k2) = Branch("_2"); var (e3, k3) = Branch("_3");
        c3.Elements.AddRange(e2); c3.Elements.AddRange(e3); c3.Connections.AddRange(k2); c3.Connections.AddRange(k3);
        Check(Fingerprint(RunModel(c3), LineIds) == alone, "two added branches: existing branch unchanged");
    }

    static void TestOrderIndependence()
    {
        var c = BasicLine(99);
        var (els, cons) = Branch("_2");
        c.Elements.AddRange(els); c.Connections.AddRange(cons);
        var ids = LineIds.Concat(LineIds.Select(i => i + "_2")).ToList();
        string fwd = Fingerprint(RunModel(c), ids);

        var r = BasicLine(99);
        var (els2, cons2) = Branch("_2");
        r.Elements.AddRange(els2); r.Connections.AddRange(cons2);
        r.Elements.Reverse(); r.Connections.Reverse();
        Check(Fingerprint(RunModel(r), ids) == fwd, "reversed element / connection order gives the same results");
    }

    static void TestParallel()
    {
        int[] seeds = Enumerable.Range(1, 12).ToArray();
        var seq = seeds.Select(s => Fingerprint(RunModel(BasicLine(s)), LineIds)).ToArray();
        var par = Task.WhenAll(seeds.Select(s => Task.Run(() => Fingerprint(RunModel(BasicLine(s)), LineIds)))).Result;
        Check(seq.SequenceEqual(par), "parallel runs equal the sequential runs, seed by seed");
        Check(seq.Distinct().Count() == seq.Length, "every seed gives different results");
        var same = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Fingerprint(RunModel(BasicLine(5)), LineIds)))).Result;
        Check(same.All(x => x == seq[4]), "the same seed in 8 parallel threads is identical");
    }

    static void TestUnseeded()
    {
        var c1 = new SimClock(); var c2 = new SimClock();
        Check(c1.Context.Seed != c2.Context.Seed, $"fresh seeds differ ({c1.Context.Seed} vs {c2.Context.Seed})");
        Check(c1.Context.Seed > 0 && c2.Context.Seed > 0, "fresh seeds are positive (0 is never drawn)");

        // An unseeded headless run: record its seed, rerun with it → identical.
        var cfg = ReferenceModels.BasicLine();
        cfg.UseFixedSeed = false;
        var clock = new SimClock();
        var f = new SimuLean.Headless.HeadlessModelFactory(clock, enableLogging: false);
        var el = f.BuildModel(cfg); f.StartModel(); clock.AdvanceClock(cfg.MaxSimulationTime);
        double out1 = el["sink"].GetStatsInputCount(), stay1 = el["q"].GetStatsStayTimeMean();
        Check(Fingerprint(RunModel(BasicLine(clock.Context.Seed)), new[] { "sink" }).Contains("input=" + out1.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
              "rerun with the recorded seed reproduces the unseeded run");
        Check(Math.Abs(RunModel(BasicLine(clock.Context.Seed)).Stats["q"]["stay_avg"] - stay1) < 1e-12, "same queue stay");
    }

    static double[] Draw(SimClock clock, string owner, string purpose, int n)
    {
        var u = clock.Context.AssignRandomStream(new UniformDistribution(0, 1), owner, purpose);
        return Enumerable.Range(0, n).Select(_ => u.Sample()).ToArray();
    }

    static void TestReseed()
    {
        var fresh = Draw(new SimClock(77), "A", "delay", 5);
        var reused = new SimClock(1);
        Draw(reused, "A", "delay", 3);          // a previous run used the key
        reused.Context.Reseed(77);
        Check(reused.Context.Seed == 77, "Seed updated");
        Check(Draw(reused, "A", "delay", 5).SequenceEqual(fresh), "reseeded clock draws like a new clock (key counters reset)");
    }

    static void TestDuplicateKeys()
    {
        var clock = new SimClock(3);
        var first = Draw(clock, "MS", "delay", 4);
        var second = Draw(clock, "MS", "delay", 4);      // second slot of a multi-server
        Check(!first.SequenceEqual(second), "second request of a key gets its own stream (key#1)");
        var c2 = new SimClock(3);
        var cst = c2.Context.AssignRandomStream(new ConstantRandomProcess(2), "MS", "delay");   // no stream consumed
        Check(cst.NextValue() == 2, "constant untouched");
        Check(Draw(c2, "MS", "delay", 4).SequenceEqual(first), "a deterministic sampler does not shift the key counter");
        var e = new SimClock(3).Context.CreateRandomStream("MS", "delay");
        var u = new UniformDistribution(0, 1, e);
        Check(Enumerable.Range(0, 4).Select(_ => u.Sample()).SequenceEqual(first), "CreateRandomStream = AssignRandomStream stream");
    }

    static void TestDiscreteStrategies()
    {
        IRandomSampler Make(int seed, string owner)
        {
            var cfg = new ElementConfig("x", "T", owner);
            cfg.Parameters["delayStrategyType"] = "Poisson";
            cfg.Parameters["poissonLambda"] = 4.0;
            return ElementBuildHelpers.BuildRandomProcess(cfg, "delay", new SimClock(seed));
        }
        double[] D(IRandomSampler s) => Enumerable.Range(0, 30).Select(_ => s.NextValue()).ToArray();
        Check(D(Make(11, "P")).SequenceEqual(D(Make(11, "P"))), "Poisson reproducible for a seed (was on the global engine)");
        Check(!D(Make(11, "P")).SequenceEqual(D(Make(12, "P"))), "Poisson differs across seeds");
        Check(Make(11, "P") is IRandomStreamUser, "the Poisson sampler accepts a stream");
    }
}
