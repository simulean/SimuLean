// Distribution fitting: SimuLean.Stochastics.Fitting against scipy's fits of fixed samples (reference.tsv,
// make_reference.py), the goodness-of-fit statistics and p-values, the ranking, the sampler specs and the number parser.
// Run: dotnet run  (from StandaloneTests/fittingtests)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SimuLean;
using SimuLean.Serialization;
using SimuLean.Stochastics;
using SimuLean.Stochastics.Fitting;

static class Program
{
    static int passed, failed;
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    static double Rel(double got, double want) => Math.Abs(got - want) / Math.Max(Math.Abs(want), 1e-12);

    static int Main()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "reference.tsv");
        if (!File.Exists(file)) file = Path.Combine(Directory.GetCurrentDirectory(), "reference.tsv");
        var samples = new Dictionary<string, FitSample>();
        var fits = new List<string[]>(); var gofs = new List<string[]>();
        foreach (string line in File.ReadLines(file))
        {
            if (line.StartsWith("#") || line.Trim().Length == 0) continue;
            string[] f = line.Split('\t');
            switch (f[0])
            {
                case "sample": samples[f[1]] = new FitSample(f[2].Split(',').Select(v => double.Parse(v, Ci))); break;
                case "fit": fits.Add(f); break;
                case "gof": gofs.Add(f); break;
                case "digamma": Check(Rel(Probability.Digamma(D(f[1])), D(f[2])) < 1e-10, $"digamma({f[1]})"); break;
                case "trigamma": Check(Rel(Probability.Trigamma(D(f[1])), D(f[2])) < 1e-10, $"trigamma({f[1]})"); break;
            }
        }
        Console.WriteLine("- digamma / trigamma against scipy (relative error < 1e-10)");

        // ── estimators against scipy's maximum likelihood ───────────────────
        foreach (var f in fits)
        {
            var s = samples[f[1]];
            var all = DistributionFitter.FitAll(s);
            var mine = all.FirstOrDefault(r => r.Distribution.Family == f[2])?.Distribution;
            Check(mine != null, $"{f[1]}: {f[2]} fitted");
            if (mine == null) continue;
            double[] want = f[3].Split(',').Select(D).ToArray();
            bool numeric = f[2] == "Gamma" || f[2] == "Weibull";   // scipy optimises numerically: agree to its tolerance
            for (int i = 0; i < want.Length; i++)
                Check(Rel(mine.Parameters[i], want[i]) < (numeric ? 1e-4 : 1e-10),
                      $"{f[1]}: {f[2]} {mine.ParameterNames[i]} = {mine.Parameters[i]:R} (scipy {want[i]:R})");
        }
        Console.WriteLine("- maximum-likelihood fits against scipy (closed forms 1e-10; Gamma / Weibull Newton vs scipy's optimiser 1e-4)");

        // ── goodness of fit: KS D and A² of the fitted distribution ────────
        foreach (var g in gofs)
        {
            var s = samples[g[1]];
            var r = DistributionFitter.FitAll(s).First(x => x.Distribution.Family == g[2]);
            bool numeric = g[2] == "Gamma" || g[2] == "Weibull";
            Check(Math.Abs(r.KS - D(g[3])) < (numeric ? 1e-4 : 1e-10), $"{g[1]}: {g[2]} KS D = {r.KS:R} (scipy {g[3]})");
            Check(Math.Abs(r.AD - D(g[4])) < (numeric ? 1e-3 : 1e-8), $"{g[1]}: {g[2]} A² = {r.AD:R} (numpy {g[4]})");
        }
        Console.WriteLine("- Kolmogorov–Smirnov D (scipy kstest) and Anderson–Darling A² of each fit");

        // ── p-values: the classical critical values ─────────────────────────
        int big = 1000;
        double sn = Math.Sqrt(big);
        Check(Math.Abs(Probability.KolmogorovSmirnovP(1.358 / (sn + 0.12 + 0.11 / sn), big) - 0.05) < 0.002, "KS: λ = 1.358 is the 5 % point");
        Check(Math.Abs(Probability.KolmogorovSmirnovP(1.628 / (sn + 0.12 + 0.11 / sn), big) - 0.01) < 0.001, "KS: λ = 1.628 is the 1 % point");
        Check(Math.Abs(Probability.AndersonDarlingP(2.492, big) - 0.05) < 0.002, "AD: A² = 2.492 is the 5 % point");
        Check(Math.Abs(Probability.AndersonDarlingP(3.857, big) - 0.01) < 0.001, "AD: A² = 3.857 is the 1 % point");
        Check(Probability.AndersonDarlingP(0.3, big) > 0.9 && Probability.KolmogorovSmirnovP(0.01, big) > 0.9, "small statistics: p close to 1");
        Console.WriteLine("- p-values: KS (Stephens) and AD (Marsaglia & Marsaglia) at their classical critical values");

        // ── ranking: the generating family comes out on top and fits ────────
        var truth = new Dictionary<string, string[]>
        {
            ["exp"] = new[] { "Exponential", "Gamma", "Weibull" }, ["norm"] = new[] { "Normal", "Gamma", "LogNormal", "Weibull", "Beta" },
            ["lognorm"] = new[] { "LogNormal", "Gamma" }, ["gamma"] = new[] { "Gamma", "Weibull", "LogNormal" },
            ["weibull"] = new[] { "Weibull", "Gamma" }, ["uniform"] = new[] { "Uniform", "Beta" }, ["poisson"] = new[] { "Poisson", "Binomial" },
        };
        foreach (var kv in truth)
        {
            foreach (var crit in new[] { FitCriterion.AndersonDarling, FitCriterion.KolmogorovSmirnov })
            {
                var ranked = DistributionFitter.FitAll(samples[kv.Key], crit);
                // a sample may happen to look more like a neighbouring family (scipy agrees: see the gof rows): the best one fits
                Check(ranked[0].Verdict == FitVerdict.Good, $"{kv.Key} by {crit}: the best ({ranked[0].Distribution.Family}) is a good fit");
                var trueFit = ranked.First(r => r.Distribution.Family == kv.Value[0]);
                Check(trueFit.Verdict != FitVerdict.Rejected, $"{kv.Key}: the true family {kv.Value[0]} is not rejected ({trueFit.VerdictText}, rank {trueFit.Rank})");
                Check(ranked.Select(r => r.Rank).SequenceEqual(Enumerable.Range(1, ranked.Count)), $"{kv.Key}: ranks 1..n");
            }
        }
        var expRanked = DistributionFitter.FitAll(samples["exp"]);
        Check(expRanked.Last().Verdict == FitVerdict.Rejected, $"exp: the worst candidate ({expRanked.Last().Distribution.Family}) is rejected");
        Check(!DistributionFitter.FitAll(samples["norm"]).Any(r => r.Distribution.Discrete), "non-integer data: no discrete family");
        var counts = DistributionFitter.FitAll(samples["poisson"]);
        Check(counts[0].Distribution.Family == "Poisson", $"count data: ranked by χ², Poisson first (got {counts[0].Distribution.Family})");
        Console.WriteLine("- ranking (AD and KS; χ² for count data): the best candidate a good fit, the generating family not rejected; bad ones rejected");

        // ── the result as a sampler spec the elements read ──────────────────
        var clock = new SimClock(7);
        foreach (var r in DistributionFitter.FitAll(samples["gamma"]))
        {
            IRandomSampler sampler = null;
            try { sampler = SamplerSpec.Decode(r.Spec, clock, "fit", r.Distribution.Family); } catch (Exception e) { Check(false, $"{r.Spec}: {e.Message}"); continue; }
            double sum = 0; int m = 40000;
            for (int i = 0; i < m; i++) sum += sampler.NextValue();
            Check(Rel(sum / m, r.Distribution.Mean) < 0.03, $"{r.Spec}: sampled mean {sum / m:F3} ≈ fitted mean {r.Distribution.Mean:F3}");
        }
        Console.WriteLine("- every fit's spec decodes (SamplerSpec) and samples its mean");

        // ── numbers pasted from a spreadsheet ───────────────────────────────
        Check(FitSample.ParseNumbers("1,5\n2,25\n3").SequenceEqual(new[] { 1.5, 2.25, 3 }), "decimal commas, one per line");
        Check(FitSample.ParseNumbers("1.5\t2\n3 4").SequenceEqual(new[] { 1.5, 2, 3, 4 }), "tabs and spaces");
        Check(FitSample.ParseNumbers("1,2,3").SequenceEqual(new[] { 1.0, 2, 3 }), "commas as separators");
        Check(FitSample.ParseNumbers("time\n4\n5;6").SequenceEqual(new[] { 4.0, 5, 6 }), "a header word skipped, semicolons");
        var summary = new FitSample(new[] { 2.0, 4, 4, 4, 5, 5, 7, 9, double.NaN });
        Check(summary.N == 8 && summary.Mean == 5 && Math.Abs(summary.Variance - 32.0 / 7) < 1e-12 && summary.AllIntegers, "summary (NaN dropped)");
        Console.WriteLine("- numbers pasted from a spreadsheet; the sample summary");

        // ── times recorded in a run ─────────────────────────────────────────
        var stays = RunData.Collect(SimuLean.Testing.ReferenceModels.BasicLine(), "srv", RunQuantity.StayTime, 2000, 5);
        var stayFit = DistributionFitter.FitAll(new FitSample(stays));
        var expo = stayFit.First(r => r.Distribution.Family == "Exponential");
        Check(stays.Count > 1000 && Rel(expo.Distribution.Parameters[0], 1.25) < 0.08 && expo.Verdict == FitVerdict.Good,
              $"run: the server's process times (Exponential rate 1.25) fit Exponential rate {expo.Distribution.Parameters[0]:F3} ({expo.VerdictText}, n = {stays.Count})");
        var gaps = RunData.Collect(SimuLean.Testing.ReferenceModels.BasicLine(), "q", RunQuantity.InterArrival, 2000, 5);
        Check(Rel(gaps.Average(), 1.0) < 0.08, $"run: inter-arrival times at the queue average {gaps.Average():F3} (source mean 1)");
        var plain = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
        plain.BuildModel(SimuLean.Testing.ReferenceModels.BasicLine());
        Check(plain.GetAllElements().Values.All(e => e.StatsCollector == null || e.StatsCollector.StaySamples == null), "raw samples are off unless asked");
        Console.WriteLine("- times recorded in a run (RunData): stay times and inter-arrivals of a reference model");

        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static double D(string s) => double.Parse(s, Ci);
}
