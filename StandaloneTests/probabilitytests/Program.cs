// Probability functions (D1): SimuLean's own implementation (SimuLean.Stochastics.Probability) against ALGLIB 4.07's values (reference.tsv,
// generated once before ALGLIB was removed), plus round trips of every inverse through its CDF.
// Run: dotnet run  (from StandaloneTests/probabilitytests)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SimuLean;
using SimuLean.Stochastics;
using SimuLean.Stochastics.Distributions;
using SimuLean.Stochastics.Streams;

static class Program
{
    static int passed, failed;
    static readonly Dictionary<string, (double worst, string at, int n)> worst = new Dictionary<string, (double, string, int)>();

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    // relative error, with an absolute floor for values near 0 (lngamma near 1 and 2, erf near 0)
    static double Err(double got, double want, double floor) => Math.Abs(got - want) / Math.Max(Math.Abs(want), floor);

    static int Main()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "reference.tsv");
        if (!File.Exists(file)) file = Path.Combine(Directory.GetCurrentDirectory(), "reference.tsv");
        var ci = CultureInfo.InvariantCulture;
        foreach (string line in File.ReadLines(file))
        {
            if (line.StartsWith("#") || line.Trim().Length == 0) continue;
            string[] f = line.Split('\t');
            string name = f[0];
            double a = double.Parse(f[1], ci), b = double.Parse(f[2], ci), c = double.Parse(f[3], ci), want = double.Parse(f[4], ci);
            double got, tol, floor = 1e-300;
            switch (name)
            {
                case "lngamma": got = Probability.LogGamma(a); tol = 1e-13; floor = 1; break;
                case "gamma": got = Probability.Gamma(a); tol = 1e-12; break;
                case "gammap": got = Probability.GammaP(a, b); tol = 1e-11; break;
                case "gammaq": got = Probability.GammaQ(a, b); tol = 1e-11; break;
                case "erf": got = Probability.Erf(a); tol = 1e-13; break;
                case "erfc": got = Probability.Erfc(a); tol = 1e-12; break;
                case "normalcdf": got = Probability.NormalCdf(a); tol = 1e-12; break;
                case "invnormal": got = Probability.NormalQuantile(a); tol = 1e-12; floor = 1e-12; break;
                case "beta": got = Probability.Beta(a, b); tol = 1e-12; break;
                case "incbeta": got = Probability.IncompleteBeta(a, b, c); tol = 1e-11; break;
                case "invincbeta": got = Probability.IncompleteBetaInverse(a, b, c); tol = 1e-9; break;
                case "chisqcdf": got = Probability.ChiSquareCdf(b, a); tol = 1e-11; break;
                case "tcdf": got = Probability.StudentTCdf(b, a); tol = 1e-11; break;
                case "invt": got = Probability.StudentTQuantile(b, a); tol = 1e-9; break;
                case "fcdf": got = Probability.FCdf(c, a, b); tol = 1e-11; break;
                case "poissoncdf": got = Probability.PoissonCdf((int)a, b); tol = 1e-11; break;
                case "binomialcdf": got = Probability.BinomialCdf((int)a, (int)b, c); tol = 1e-11; break;
                // ALGLIB inverts the complemented CDF here: the reference used 1 − p, which loses digits when p is
                // small; the round trips below check those. Compared where 1 − p is exact enough.
                case "invchisq": if (b < 1e-3) continue; got = Probability.ChiSquareQuantile(b, a); tol = 1e-9; break;
                case "invf": if (c < 1e-3) continue; got = Probability.FQuantile(c, a, b); tol = 1e-9; break;
                default: Check(false, "unknown reference row " + name); continue;
            }
            double e = Err(got, want, floor);
            // Where they disagree, ALGLIB is the less accurate one in these places (checked: x^200 = 1e-10 gives
            // x = 0.891250938133746 exactly; erfc(5) = 1.5374597944280349e-12): accept when ours is at least as good.
            if (e > tol)
            {
                Func<double, double> cdf = null; double p = double.NaN;
                switch (name)
                {
                    case "invincbeta": cdf = x => Probability.IncompleteBeta(a, b, x); p = c; break;
                    case "invt": cdf = x => Probability.StudentTCdf(x, a); p = b; break;
                    case "invchisq": cdf = x => Probability.ChiSquareCdf(x, a); p = b; break;
                    case "invf": cdf = x => Probability.FCdf(x, a, b); p = c; break;
                    case "invnormal": cdf = Probability.NormalCdf; p = a; break;
                }
                if (cdf != null && Math.Abs(cdf(got) - p) <= Math.Abs(cdf(want) - p)) e = 0;
                // ALGLIB flushes erfc / Φ to 0 (or loses digits) far in the tail, and I_x below 1e-290: the
                // published values below pin those tails.
                if ((name == "erfc" && a > 3.5) || (name == "normalcdf" && a < -4)) e = 0;
                if (name == "incbeta" && Math.Abs(want) < 1e-290 && Math.Abs(got) < 1e-290) e = 0;
            }
            if (!worst.TryGetValue(name, out var w) || e > w.worst) w = (e, $"({f[1]}, {f[2]}, {f[3]}) got {got:R}, want {want:R}", w.n);
            worst[name] = (w.worst, w.at, w.n + 1);
            Check(e <= tol, $"{name}({f[1]}, {f[2]}, {f[3]}): got {got:R}, ALGLIB {want:R}, relative error {e:E2} > {tol:E0}");
        }
        Console.WriteLine("- against ALGLIB 4.07 (worst relative error per function):");
        foreach (var kv in worst) Console.WriteLine($"    {kv.Key,-12} {kv.Value.n,4} values, worst {kv.Value.worst:E2}");

        Published();
        RoundTrips();
        QuantileSemantics();
        SamplerMoments();
        ZeroUniformDraw();
        LargeCountSkew();
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // tails where ALGLIB flushes to 0 or loses digits: high-precision published values
    static void Published()
    {
        Console.WriteLine("- tails against published values");
        void Near(double got, double want, string what, double tol = 1e-12)
            => Check(Math.Abs(got - want) <= tol * Math.Abs(want), $"{what}: got {got:R}, want {want:R}");
        Near(Probability.Erfc(4), 1.5417257900280018852e-08, "erfc(4)");
        Near(Probability.Erfc(5), 1.5374597944280348502e-12, "erfc(5)");
        Near(Probability.Erfc(6), 2.1519736712498913117e-17, "erfc(6)");
        Near(Probability.Erfc(8), 1.1224297172982927079e-29, "erfc(8)");
        Near(Probability.Erfc(10), 2.0884875837625447570e-45, "erfc(10)");
        Near(Probability.NormalCdf(-5), 2.8665157187919391e-07, "Phi(-5)", 1e-10);
        Near(Probability.NormalCdf(-6), 9.8658764503769814e-10, "Phi(-6)", 1e-10);
        Near(Probability.NormalCdf(-10), 7.6198530241604735e-24, "Phi(-10)");
        Near(Probability.NormalCdf(-20), 2.7536241186062336e-89, "Phi(-20)");
        Near(Probability.NormalCdf(-30), 4.906713927148187e-198, "Phi(-30)");
        Near(Probability.IncompleteBetaInverse(200, 1, 1e-10), Math.Pow(1e-10, 1.0 / 200), "I^-1(200, 1, 1e-10) = (1e-10)^(1/200)");
    }

    // every inverse through its own (ALGLIB-checked) CDF, tails included
    static void RoundTrips()
    {
        Console.WriteLine("- round trips: CDF(inverse(p)) = p");
        double[] P = { 1e-300, 1e-100, 1e-20, 1e-10, 1e-5, 1e-3, 0.01, 0.1, 0.3, 0.5, 0.7, 0.9, 0.99, 0.999, 1 - 1e-5, 1 - 1e-10 };
        double worstRt = 0;
        // Good when CDF(x) is within 1e-8 of p relative to the small side, or when p lies between the CDF at the
        // doubles next to x (the quantile is as close as a double gets — near 1, x itself cannot be finer).
        void Rt(string what, double p, Func<double, double> cdf, double x)
        {
            double back = cdf(x);
            double Safe(double v) { try { return cdf(v); } catch (ArgumentOutOfRangeException) { return back; } }
            double below = Safe(Math.BitDecrement(x)), above = Safe(Math.BitIncrement(x));
            double e = Math.Abs(back - p) / Math.Min(p, 1 - p);
            bool best = Math.Min(below, above) <= p + 1e-16 && p - 1e-16 <= Math.Max(below, above);
            if (!best) worstRt = Math.Max(worstRt, e);
            Check(e <= 1e-8 || best, $"{what}: CDF(inverse({p:R})) = {back:R}");
        }
        foreach (double p in P) Rt("normal", p, Probability.NormalCdf, Probability.NormalQuantile(p));
        foreach (double df in new[] { 0.5, 1, 2, 3, 5, 10, 30, 100 })
            foreach (double p in P)
            {
                if (p < 1e-100) continue;
                Rt($"chi2({df})", p, x => Probability.ChiSquareCdf(x, df), Probability.ChiSquareQuantile(p, df));
                Rt($"t({df})", p, x => Probability.StudentTCdf(x, df), Probability.StudentTQuantile(p, df));
            }
        foreach (double d1 in new[] { 1, 2, 5, 30 })
            foreach (double d2 in new[] { 1, 2, 5, 30 })
                foreach (double p in P)
                    if (p >= 1e-20) Rt($"F({d1},{d2})", p, x => Probability.FCdf(x, d1, d2), Probability.FQuantile(p, d1, d2));
        foreach (double a in new[] { 0.1, 0.5, 2, 10, 200 })
            foreach (double b in new[] { 0.1, 0.5, 2, 10, 200 })
                foreach (double p in P)
                    if (p >= 1e-20 && p <= 1 - 1e-10)
                        Rt($"beta({a},{b})", p, x => Probability.IncompleteBeta(a, b, x), Probability.IncompleteBetaInverse(a, b, p));
        Console.WriteLine($"    worst {worstRt:E2} (where the quantile is not already the closest double)");
    }

    // the quantiles return x with CDF(x) = p (with ALGLIB, ChiSquareInvCDF / FInvCDF returned the upper-tail point)
    static void QuantileSemantics()
    {
        Console.WriteLine("- quantiles are CDF inverses");
        Check(Math.Abs(Probability.ChiSquareQuantile(0.95, 3) - 7.814727903251178) < 1e-9, "chi2(3) 95% quantile = 7.8147");
        Check(Math.Abs(Probability.FQuantile(0.95, 5, 10) - 3.3258345304130112) < 1e-8, "F(5,10) 95% quantile = 3.3258");
        Check(Math.Abs(Probability.StudentTQuantile(0.975, 10) - 2.2281388519649385) < 1e-9, "t(10) 97.5% quantile = 2.2281");
        Check(Math.Abs(Probability.NormalQuantile(0.975) - 1.959963984540054) < 1e-12, "normal 97.5% quantile = 1.96");
        Check(Math.Abs(Probability.BinomialPmf(3, 10, 0.5) - 120.0 / 1024) < 1e-14, "binomial pmf");
        Check(Math.Abs(Probability.PoissonPmf(2, 3) - 4.5 * Math.Exp(-3)) < 1e-15, "poisson pmf");
    }

    // every sampler draws its own distribution: sample mean within 5 standard errors, variance within 5 %
    // (Gamma with shape < 1 drew a mean of k^2/[k+1], NaN below 1/3)
    static void SamplerMoments()
    {
        Console.WriteLine("- sampler moments (200 000 draws each)");
        const int n = 200000;
        void M(string what, Func<IRandomEngine, IRandomSampler> make, double mean, double variance, bool checkVariance = true)
        {
            IRandomSampler d = make(new DefaultRandomEngine(12345));
            double sum = 0, sum2 = 0;
            bool finite = true;
            for (int i = 0; i < n; i++)
            {
                double x = d.NextValue();
                if (double.IsNaN(x) || double.IsInfinity(x)) { finite = false; break; }
                sum += x; sum2 += x * x;
            }
            if (!finite) { Check(false, $"{what}: drew NaN / infinity"); return; }
            double m = sum / n, v = (sum2 - n * m * m) / (n - 1);
            Check(Math.Abs(m - mean) <= 5 * Math.Sqrt(variance / n) + 1e-12, $"{what}: mean {m:G6}, want {mean:G6}");
            if (checkVariance) Check(Math.Abs(v - variance) <= 0.05 * variance, $"{what}: variance {v:G6}, want {variance:G6}");
        }
        foreach (double k in new[] { 0.1, 0.3, 0.5, 0.9, 1.0, 2.5, 20 })
            M($"Gamma({k}, 2)", e => new GammaDistribution(k, 2, e), 2 * k, 4 * k);
        M("Exponential(rate 0.5)", e => new ExponentialDistribution(0.5, e), 2, 4);
        M("Uniform(2, 5)", e => new UniformDistribution(2, 5, e), 3.5, 0.75);
        M("Triangular(0, 1, 4)", e => new TriangularDistribution(0, 1, 4, e), 5.0 / 3, (0 + 1 + 16 - 0 - 0 - 4) / 18.0);
        M("Normal(10, 2)", e => new NormalDistribution(10, 2, e), 10, 4);
        M("LogNormal(0, 0.5)", e => new LogNormalDistribution(0, 0.5, e), Math.Exp(0.125), (Math.Exp(0.25) - 1) * Math.Exp(0.25));
        M("Weibull(1.5, 3)", e => new WeibullDistribution(1.5, 3, e), 3 * Probability.Gamma(1 + 1 / 1.5),
          9 * (Probability.Gamma(1 + 2 / 1.5) - Math.Pow(Probability.Gamma(1 + 1 / 1.5), 2)));
        M("Beta(2, 5)", e => new BetaDistribution(2, 5, e), 2.0 / 7, 10.0 / (49 * 8));
        M("ChiSquare(4)", e => new ChiSquareDistribution(4, e), 4, 8);
        M("StudentT(10)", e => new StudentTDistribution(10, e), 0, 10.0 / 8);
        M("F(5, 20)", e => new FDistribution(5, 20, e), 20.0 / 18, 2 * 400.0 * 23 / (5 * 18 * 18 * 16), checkVariance: false);
        M("Poisson(4)", e => new PoissonDistribution(4, e), 4, 4);
        M("Binomial(20, 0.3)", e => new BinomialDistribution(20, 0.3, e), 6, 4.2);
        M("IntegerUniform(1, 6)", e => new IntegerUniformDistribution(1, 6, e), 3.5, 35.0 / 12);
    }

    // Poisson / Binomial with large parameters are exact: their skewness is kept (the normal
    // approximation they used had none). Third central moment within 5 standard errors.
    static void LargeCountSkew()
    {
        Console.WriteLine("- large-parameter Poisson / Binomial keep their skewness");
        const int n = 200000;
        void Skew(string what, IRandomSampler d, double mean, double variance, double mu3)
        {
            var x = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++) { x[i] = d.NextValue(); sum += x[i]; }
            double m = sum / n, m2 = 0, m3 = 0;
            foreach (double v in x) { double dv = v - m; m2 += dv * dv; m3 += dv * dv * dv; }
            m2 /= n; m3 /= n;
            double se = Math.Sqrt(15 * variance * variance * variance / n);
            Check(Math.Abs(m - mean) <= 5 * Math.Sqrt(variance / n) && Math.Abs(m2 - variance) <= 0.05 * variance, $"{what}: mean {m:G6}, variance {m2:G6}");
            Check(Math.Abs(m3 - mu3) <= 5 * se, $"{what}: third central moment {m3:G6}, want {mu3:G6} (± {5 * se:G3})");
        }
        Skew("Poisson(100)", new PoissonDistribution(100, new DefaultRandomEngine(7)), 100, 100, 100);
        Skew("Binomial(200, 0.3)", new BinomialDistribution(200, 0.3, new DefaultRandomEngine(7)), 60, 42, 200 * 0.3 * 0.7 * 0.4);
    }

    sealed class ZeroEngine : IRandomEngine
    {
        public double NextDouble() => 0.0;
        public int NextInt(int minValue, int maxValue) => minValue;
        public void SetSeed(int seed) { }
        public double NextStandardNormal() => 0.0;
    }

    // NextDouble() can return exactly 0: the inverse-transform samplers must not throw
    static void ZeroUniformDraw()
    {
        Console.WriteLine("- samplers survive a uniform draw of exactly 0");
        var e = new ZeroEngine();
        var samplers = new (string, IRandomSampler)[]
        {
            ("Weibull", new WeibullDistribution(1.5, 3, e)), ("ChiSquare", new ChiSquareDistribution(4, e)),
            ("StudentT", new StudentTDistribution(10, e)), ("F", new FDistribution(5, 20, e)),
            ("Normal", new NormalDistribution(10, 2, e)), ("Beta", new BetaDistribution(2, 5, e)),
            ("Exponential", new ExponentialDistribution(0.5, e)), ("Triangular", new TriangularDistribution(0, 1, 4, e)),
        };
        foreach (var (name, d) in samplers)
        {
            try { double x = d.NextValue(); Check(!double.IsNaN(x) && !double.IsInfinity(x), $"{name}: u = 0 gave {x}"); }
            catch (Exception ex) { Check(false, $"{name}: u = 0 threw {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
