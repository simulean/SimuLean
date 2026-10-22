using System;
using System.Collections.Generic;
using System.Linq;

namespace SimuLean.Stochastics.Fitting
{
    /// <summary>The goodness-of-fit statistic that orders the candidates (lower is better).</summary>
    public enum FitCriterion { AndersonDarling, KolmogorovSmirnov }

    /// <summary>How well a fitted distribution matches the data.</summary>
    public enum FitVerdict { Good, Doubtful, Rejected, NotApplicable }

    /// <summary>One candidate: the fitted distribution and its goodness of fit.</summary>
    public sealed class FitResult
    {
        public FittedDistribution Distribution;
        /// <summary>Kolmogorov–Smirnov statistic D and its p-value.</summary>
        public double KS, KSP;
        /// <summary>Anderson–Darling statistic A² and its p-value.</summary>
        public double AD, ADP;
        /// <summary>χ² by classes, its degrees of freedom and p-value (NaN when too few classes).</summary>
        public double ChiSquare, ChiSquareP;
        public int ChiSquareDf;
        public double LogLikelihood, AIC;
        public FitVerdict Verdict;
        public int Rank;

        /// <summary>The verdict in words, by the p-value of the ranking criterion.</summary>
        public string VerdictText => Verdict == FitVerdict.Good ? "good fit"
                                   : Verdict == FitVerdict.Doubtful ? "doubtful fit"
                                   : Verdict == FitVerdict.Rejected ? "rejected" : "–";

        public string Spec => Distribution.ToSpec();
    }

    /// <summary>
    /// Fits the SimuLean catalog's distributions to a sample: closed-form maximum likelihood (Exponential,
    /// Normal, LogNormal, Uniform, Poisson), Newton maximum likelihood (Gamma, Weibull — with Probability's digamma /
    /// trigamma), moments (Triangular, Beta on the data's range, Binomial), the range (discrete uniform); each candidate's
    /// Kolmogorov–Smirnov and Anderson–Darling statistics with p-values, χ² by classes, log-likelihood and AIC; ranked by
    /// the chosen criterion — for integer (count) data by the χ² p-value instead, KS / AD being for continuous
    /// distributions. The p-values are for a fully specified distribution: with estimated parameters they are
    /// optimistic (a "good" verdict is a necessary, not a sufficient, condition). Families the data cannot have (a
    /// positive family for negative data, a discrete one for non-integer data) are skipped.
    /// </summary>
    public static class DistributionFitter
    {
        /// <summary>Every family that applies to the sample, fitted and ranked (best first).</summary>
        public static List<FitResult> FitAll(FitSample s, FitCriterion criterion = FitCriterion.AndersonDarling)
        {
            var fits = new List<FittedDistribution>();
            void Try(Func<FittedDistribution> f) { try { var d = f(); if (d != null && d.Parameters.All(p => !double.IsNaN(p) && !double.IsInfinity(p))) fits.Add(d); } catch (ArithmeticException) { } catch (ArgumentException) { } }

            Try(() => FitNormal(s));
            Try(() => FitUniform(s));
            Try(() => FitTriangular(s));
            Try(() => FitBeta(s));
            if (s.AllNonNegative) Try(() => FitExponential(s));
            if (s.AllPositive)
            {
                Try(() => FitLogNormal(s));
                Try(() => FitGamma(s));
                Try(() => FitWeibull(s));
            }
            if (s.AllIntegers && s.AllNonNegative)
            {
                Try(() => FitPoisson(s));
                Try(() => FitBinomial(s));
                Try(() => FitDiscreteUniform(s));
            }

            var results = fits.Select(d => Evaluate(s, d)).ToList();
            if (s.AllIntegers && results.Any(r => r.Distribution.Discrete))
            {
                // count data: KS and AD assume a continuous distribution (ties break them) — χ² by value decides
                foreach (var r in results) r.Verdict = Verdict(r.ChiSquareP);
                results = results.OrderByDescending(r => double.IsNaN(r.ChiSquareP) ? -1 : r.ChiSquareP).ThenBy(r => r.AIC).ToList();
            }
            else
            {
                foreach (var r in results) r.Verdict = Verdict(criterion == FitCriterion.AndersonDarling ? r.ADP : r.KSP);
                results = results.OrderBy(r => criterion == FitCriterion.AndersonDarling ? r.AD : r.KS).ThenBy(r => r.AIC).ToList();
            }
            for (int i = 0; i < results.Count; i++) results[i].Rank = i + 1;
            return results;
        }

        private static FitVerdict Verdict(double p) => double.IsNaN(p) ? FitVerdict.NotApplicable : p >= 0.10 ? FitVerdict.Good : p >= 0.01 ? FitVerdict.Doubtful : FitVerdict.Rejected;

        // ── estimators ───────────────────────────────────────────────────────

        public static FittedDistribution FitExponential(FitSample s) => s.Mean > 0 ? new ExponentialFit(1 / s.Mean) : null;

        public static FittedDistribution FitNormal(FitSample s)
        {
            double ss = s.Values.Sum(v => (v - s.Mean) * (v - s.Mean));
            double sd = Math.Sqrt(ss / s.N);   // maximum likelihood
            return sd > 0 ? new NormalFit(s.Mean, sd) : null;
        }

        public static FittedDistribution FitLogNormal(FitSample s)
        {
            double mu = s.Values.Average(v => Math.Log(v));
            double sigma = Math.Sqrt(s.Values.Average(v => (Math.Log(v) - mu) * (Math.Log(v) - mu)));
            return sigma > 0 ? new LogNormalFit(mu, sigma) : null;
        }

        /// <summary>The unbiased endpoints: the sample's range widened by (max − min)/(n − 1) each side (the extremes
        /// themselves would put values on CDF 0 / 1 and fail every test).</summary>
        public static FittedDistribution FitUniform(FitSample s)
        {
            if (!(s.Max > s.Min)) return null;
            double g = (s.Max - s.Min) / (s.N - 1);
            return new UniformFit(s.Min - g, s.Max + g);
        }

        /// <summary>Endpoints the sample's range widened by (max − min)/(n − 1) each side, the mode from the mean
        /// (3·mean − min − max), kept inside.</summary>
        public static FittedDistribution FitTriangular(FitSample s)
        {
            if (!(s.Max > s.Min)) return null;
            double g = (s.Max - s.Min) / (s.N - 1), lo = s.Min - g, hi = s.Max + g;
            double mode = Math.Max(lo, Math.Min(hi, 3 * s.Mean - lo - hi));
            return new TriangularFit(lo, mode, hi);
        }

        /// <summary>Moments on the data's range widened by one gap each side (so no value sits on an endpoint).</summary>
        public static FittedDistribution FitBeta(FitSample s)
        {
            double gap = (s.Max - s.Min) / s.N;
            if (!(gap > 0)) return null;
            double lo = s.Min - gap, hi = s.Max + gap, w = hi - lo;
            double m = (s.Mean - lo) / w, v = s.Variance / (w * w);
            double common = m * (1 - m) / v - 1;
            if (!(common > 0)) return null;
            return new BetaFit(m * common, (1 - m) * common, lo, hi);
        }

        /// <summary>Maximum likelihood: shape k from ln k − ψ(k) = ln(mean) − mean(ln x) (Newton from the Minka start), scale = mean / k.</summary>
        public static FittedDistribution FitGamma(FitSample s)
        {
            double meanLog = s.Values.Average(v => Math.Log(v));
            double c = Math.Log(s.Mean) - meanLog;
            if (!(c > 0)) return null;
            double k = (3 - c + Math.Sqrt((c - 3) * (c - 3) + 24 * c)) / (12 * c);
            for (int i = 0; i < 100; i++)
            {
                double f = Math.Log(k) - Probability.Digamma(k) - c;
                double df = 1 / k - Probability.Trigamma(k);
                double next = k - f / df;
                if (next <= 0) next = k / 2;
                if (Math.Abs(next - k) < 1e-12 * k) { k = next; break; }
                k = next;
            }
            return new GammaFit(k, s.Mean / k);
        }

        /// <summary>Maximum likelihood: shape k from Σx^k ln x / Σx^k − 1/k − mean(ln x) = 0 (Newton), scale = (Σx^k / n)^(1/k).</summary>
        public static FittedDistribution FitWeibull(FitSample s)
        {
            double meanLog = s.Values.Average(v => Math.Log(v));
            double sdLog = Math.Sqrt(s.Values.Average(v => (Math.Log(v) - meanLog) * (Math.Log(v) - meanLog)));
            if (!(sdLog > 0)) return null;
            double k = 1.2825 / sdLog;   // moments of ln x (Gumbel) as the start
            // scale the data to its maximum so x^k does not overflow
            double max = s.Max;
            for (int i = 0; i < 200; i++)
            {
                double a = 0, b = 0, c2 = 0;
                foreach (double x in s.Values)
                {
                    double z = x / max, lz = Math.Log(z), zk = Math.Pow(z, k);
                    a += zk; b += zk * lz; c2 += zk * lz * lz;
                }
                double f = b / a - 1 / k - (meanLog - Math.Log(max));
                double df = (c2 * a - b * b) / (a * a) + 1 / (k * k);
                double next = k - f / df;
                if (next <= 0) next = k / 2;
                if (Math.Abs(next - k) < 1e-12 * k) { k = next; break; }
                k = next;
            }
            double sum = s.Values.Sum(x => Math.Pow(x / max, k));
            return new WeibullFit(k, max * Math.Pow(sum / s.N, 1 / k));
        }

        public static FittedDistribution FitPoisson(FitSample s) => s.Mean > 0 ? new PoissonFit(s.Mean) : null;

        /// <summary>Moments (n = mean² / (mean − variance), at least the maximum; p = mean / n); only for under-dispersed data.</summary>
        public static FittedDistribution FitBinomial(FitSample s)
        {
            if (!(s.Variance < s.Mean) || s.Mean <= 0) return null;
            int n = Math.Max((int)Math.Round(s.Max), (int)Math.Round(s.Mean * s.Mean / (s.Mean - s.Variance)));
            if (n <= 0) return null;
            return new BinomialFit(n, Math.Min(1, s.Mean / n));
        }

        public static FittedDistribution FitDiscreteUniform(FitSample s)
            => s.Max > s.Min ? new DiscreteUniformFit((int)Math.Round(s.Min), (int)Math.Round(s.Max)) : null;

        // ── goodness of fit ──────────────────────────────────────────────────

        public static FitResult Evaluate(FitSample s, FittedDistribution d)
        {
            int n = s.N;
            var r = new FitResult { Distribution = d };

            // Kolmogorov–Smirnov: largest gap between the empirical and the fitted CDF (just before and at each value)
            double dmax = 0;
            for (int i = 0; i < n; i++)
            {
                double f = d.Cdf(s.Values[i]);
                double before = d.Discrete ? d.Cdf(s.Values[i] - 1) : f;
                dmax = Math.Max(dmax, Math.Max((i + 1.0) / n - f, before - (double)i / n));
            }
            r.KS = dmax;
            r.KSP = Probability.KolmogorovSmirnovP(dmax, n);

            // Anderson–Darling
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double fi = Clamp(d.Cdf(s.Values[i])), fj = Clamp(d.Cdf(s.Values[n - 1 - i]));
                sum += (2 * i + 1) * (Math.Log(fi) + Math.Log(1 - fj));
            }
            r.AD = -n - sum / n;
            r.ADP = Probability.AndersonDarlingP(r.AD, n);

            // log-likelihood and AIC
            double ll = 0;
            foreach (double v in s.Values) ll += Math.Log(Math.Max(d.Density(v), 1e-300));
            r.LogLikelihood = ll;
            r.AIC = 2 * d.Parameters.Length - 2 * ll;

            ChiSquareTest(s, d, r);
            return r;
        }

        private static double Clamp(double f) => Math.Max(1e-300, Math.Min(1 - 1e-16, f));

        /// <summary>χ² over equal-width classes of the data's range (classes merged until each expects at least 5).</summary>
        private static void ChiSquareTest(FitSample s, FittedDistribution d, FitResult r)
        {
            int n = s.N;
            int k = Math.Max(3, (int)Math.Ceiling(2 * Math.Pow(n, 0.4)));
            var edges = new List<double>();
            if (d.Discrete)
                for (double v = Math.Floor(s.Min); v <= s.Max; v++) edges.Add(v + 0.5);   // one class per value
            else
                for (int i = 1; i < k; i++) edges.Add(s.Min + (s.Max - s.Min) * i / k);
            // classes: (-inf, e0], (e0, e1], …, (e_last, inf)
            int classes = edges.Count + 1;
            var observed = new double[classes];
            foreach (double v in s.Values)
            {
                int c = 0;
                while (c < edges.Count && v > edges[c]) c++;
                observed[c]++;
            }
            var expected = new double[classes];
            double prev = 0;
            for (int c = 0; c < classes; c++)
            {
                double cdf = c < edges.Count ? d.Cdf(edges[c]) : 1;
                expected[c] = n * Math.Max(0, cdf - prev);
                prev = cdf;
            }
            // merge neighbours until every class expects at least 5
            var o = new List<double>(); var e = new List<double>();
            double oa = 0, ea = 0;
            for (int c = 0; c < classes; c++)
            {
                oa += observed[c]; ea += expected[c];
                if (ea >= 5) { o.Add(oa); e.Add(ea); oa = 0; ea = 0; }
            }
            if (ea > 0 || oa > 0)
            {
                if (e.Count > 0) { o[o.Count - 1] += oa; e[e.Count - 1] += ea; }
                else { o.Add(oa); e.Add(ea); }
            }
            int df = e.Count - 1 - d.Parameters.Length;
            r.ChiSquareDf = df;
            if (df < 1) { r.ChiSquare = double.NaN; r.ChiSquareP = double.NaN; return; }
            double chi = 0;
            for (int c = 0; c < e.Count; c++) chi += e[c] > 0 ? (o[c] - e[c]) * (o[c] - e[c]) / e[c] : 0;
            r.ChiSquare = chi;
            r.ChiSquareP = 1 - Probability.ChiSquareCdf(chi, df);
        }
    }
}
