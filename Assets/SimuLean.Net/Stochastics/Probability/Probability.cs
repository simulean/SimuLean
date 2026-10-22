using System;

namespace SimuLean.Stochastics
{
    /// <summary>
    /// Probability functions of SimuLean (D1, they replaced ALGLIB): CDFs, quantiles and probability masses
    /// of the distributions, and the functions behind them — Γ, ln Γ, B, the regularized incomplete gamma and beta and
    /// their inverses, erf / erfc. Every quantile is the inverse of its CDF (x with CDF(x) = p).
    /// Written from the published algorithms: ln Γ by Lanczos' approximation (g = 7, 9 terms) with reflection below 1/2;
    /// the incomplete gamma by its series (x &lt; a + 1) or Legendre's continued fraction (modified Lentz); the incomplete
    /// beta by its continued fraction (Lentz) with I_x(a, b) = 1 − I_(1−x)(b, a); erf x = P(1/2, x²); the normal
    /// quantile by Acklam's approximation refined with Halley steps; the inverses by Newton / Halley steps kept inside a
    /// bracket. Checked against ALGLIB 4.07 on a grid, tails included (<c>StandaloneTests/probabilitytests</c>).
    /// </summary>
    public static class Probability
    {
        private const double Eps = 1e-16;
        private const double Tiny = 1e-300;
        private const int MaxIterations = 100000;
        private const double LnSqrt2Pi = 0.91893853320467274178;

        private static readonly double[] Lanczos =
        {
            0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313, -176.61502916214059,
            12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
        };

        // ── gamma and beta ───────────────────────────────────────────────────

        /// <summary>ln Γ(x), x &gt; 0.</summary>
        public static double LogGamma(double x)
        {
            if (!(x > 0)) throw new ArgumentOutOfRangeException(nameof(x), "LogGamma needs x > 0.");
            if (x < 0.5) return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1 - x);   // reflection
            if (x == 1 || x == 2) return 0;
            double z = x - 1;
            double sum = Lanczos[0];
            for (int i = 1; i < Lanczos.Length; i++) sum += Lanczos[i] / (z + i);
            double t = z + 7.5;
            return LnSqrt2Pi + (z + 0.5) * Math.Log(t) - t + Math.Log(sum);
        }

        /// <summary>Γ(x), x &gt; 0 (∞ beyond ~171.6).</summary>
        public static double Gamma(double x)
        {
            if (!(x > 0)) throw new ArgumentOutOfRangeException(nameof(x), "Gamma needs x > 0.");
            if (x == Math.Floor(x) && x <= 21)
            {
                double f = 1;
                for (int i = 2; i < (int)x; i++) f *= i;
                return f;
            }
            if (x < 0.5) return Math.PI / (Math.Sin(Math.PI * x) * Gamma(1 - x));
            double z = x - 1;
            double sum = Lanczos[0];
            for (int i = 1; i < Lanczos.Length; i++) sum += Lanczos[i] / (z + i);
            double t = z + 7.5;
            // √(2π) t^(z+½) e^−t · sum, with the power split so it overflows only when Γ does
            double half = Math.Pow(t, (z + 0.5) / 2);
            return Math.Sqrt(2 * Math.PI) * half * (half * Math.Exp(-t)) * sum;
        }

        /// <summary>ln B(a, b).</summary>
        public static double LogBeta(double a, double b) => LogGamma(a) + LogGamma(b) - LogGamma(a + b);

        /// <summary>B(a, b) = Γ(a) Γ(b) / Γ(a + b).</summary>
        public static double Beta(double a, double b)
        {
            if (!(a > 0) || !(b > 0)) throw new ArgumentOutOfRangeException(nameof(a), "Beta needs a, b > 0.");
            return Math.Exp(LogBeta(a, b));
        }

        // ── incomplete gamma ─────────────────────────────────────────────────

        /// <summary>Regularized lower incomplete gamma P(a, x) = γ(a, x) / Γ(a); a &gt; 0, x ≥ 0.</summary>
        public static double GammaP(double a, double x)
        {
            CheckGamma(a, x);
            if (x == 0) return 0;
            return x < a + 1 ? GammaSeries(a, x) : 1 - GammaContinuedFraction(a, x);
        }

        /// <summary>Regularized upper incomplete gamma Q(a, x) = 1 − P(a, x), accurate in the upper tail.</summary>
        public static double GammaQ(double a, double x)
        {
            CheckGamma(a, x);
            if (x == 0) return 1;
            return x < a + 1 ? 1 - GammaSeries(a, x) : GammaContinuedFraction(a, x);
        }

        private static void CheckGamma(double a, double x)
        {
            if (!(a > 0)) throw new ArgumentOutOfRangeException(nameof(a), "The incomplete gamma needs a > 0.");
            if (!(x >= 0)) throw new ArgumentOutOfRangeException(nameof(x), "The incomplete gamma needs x >= 0.");
        }

        private static double GammaPrefactor(double a, double x) => Math.Exp(a * Math.Log(x) - x - LogGamma(a));

        private static double GammaSeries(double a, double x)
        {
            double term = 1 / a, sum = term, ap = a;
            for (int n = 0; n < MaxIterations; n++)
            {
                ap += 1;
                term *= x / ap;
                sum += term;
                if (Math.Abs(term) < Math.Abs(sum) * Eps) break;
            }
            return sum * GammaPrefactor(a, x);
        }

        private static double GammaContinuedFraction(double a, double x)
        {
            double b = x + 1 - a, c = 1 / Tiny, d = 1 / b, h = d;
            for (int i = 1; i < MaxIterations; i++)
            {
                double an = -i * (i - a);
                b += 2;
                d = an * d + b; if (Math.Abs(d) < Tiny) d = Tiny;
                c = b + an / c; if (Math.Abs(c) < Tiny) c = Tiny;
                d = 1 / d;
                double delta = d * c;
                h *= delta;
                if (Math.Abs(delta - 1) < Eps) break;
            }
            return h * GammaPrefactor(a, x);
        }

        /// <summary>The x with P(a, x) = p (0 &lt; p &lt; 1).</summary>
        public static double GammaPInverse(double a, double p)
        {
            if (!(a > 0)) throw new ArgumentOutOfRangeException(nameof(a), "The inverse incomplete gamma needs a > 0.");
            if (!(p > 0 && p < 1)) throw new ArgumentOutOfRangeException(nameof(p), "The inverse incomplete gamma needs 0 < p < 1.");
            // first guess: Wilson–Hilferty (a > 1) or the small-a forms
            double x;
            if (a > 1)
            {
                double z = NormalQuantile(p);
                double w = 1 - 1 / (9 * a) + z / (3 * Math.Sqrt(a));
                x = a * w * w * w;
                if (!(x > 0)) x = FromLog((Math.Log(p) + LogGamma(a + 1)) / a);   // deep lower tail: P ≈ x^a / Γ(a+1)
            }
            else
            {
                double t = 1 - a * (0.253 + a * 0.12);
                x = p < t ? FromLog((Math.Log(p) - Math.Log(t)) / a) : 1 - Math.Log(1 - (p - t) / (1 - t));
            }
            // bracket, then Newton (Halley-corrected) steps inside it
            double lo = 0, hi = Math.Max(x, a) * 2 + 10;
            while (GammaP(a, hi) < p) { lo = hi; hi *= 2; }
            if (!(x > lo && x < hi)) x = (lo + hi) / 2;
            double lnGa = LogGamma(a);
            for (int i = 0; i < 200; i++)
            {
                double f = p < 0.5 ? GammaP(a, x) - p : (1 - p) - GammaQ(a, x);   // P(a, x) − p, from the small side
                if (f < 0) lo = x; else hi = x;
                double pdf = Math.Exp((a - 1) * Math.Log(x) - x - lnGa);
                double step = f / pdf;
                double next = x - step / (1 - 0.5 * Clamp(step * ((a - 1) / x - 1)));   // Halley
                if (!(next > lo && next < hi) || double.IsNaN(next)) next = lo > 0 ? Math.Sqrt(lo * hi) : hi / 16;   // geometric bisection
                if (next == x) return x;
                if (Math.Abs(next - x) <= 1e-15 * Math.Max(Math.Abs(next), 1e-300)) return next;
                x = next;
            }
            return x;
        }

        // ── incomplete beta ──────────────────────────────────────────────────

        /// <summary>Regularized incomplete beta I_x(a, b); a, b &gt; 0, 0 ≤ x ≤ 1.</summary>
        public static double IncompleteBeta(double a, double b, double x)
        {
            if (!(a > 0) || !(b > 0)) throw new ArgumentOutOfRangeException(nameof(a), "The incomplete beta needs a, b > 0.");
            if (!(x >= 0 && x <= 1)) throw new ArgumentOutOfRangeException(nameof(x), "The incomplete beta needs 0 <= x <= 1.");
            if (x == 0) return 0;
            if (x == 1) return 1;
            double front = Math.Exp(a * Math.Log(x) + b * Log1p(-x) - LogBeta(a, b));
            if (x < (a + 1) / (a + b + 2)) return front * BetaContinuedFraction(a, b, x) / a;
            return 1 - front * BetaContinuedFraction(b, a, 1 - x) / b;
        }

        private static double BetaContinuedFraction(double a, double b, double x)
        {
            double qab = a + b, qap = a + 1, qam = a - 1;
            double c = 1, d = 1 - qab * x / qap;
            if (Math.Abs(d) < Tiny) d = Tiny;
            d = 1 / d;
            double h = d;
            for (int m = 1; m < MaxIterations; m++)
            {
                int m2 = 2 * m;
                double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
                d = 1 + aa * d; if (Math.Abs(d) < Tiny) d = Tiny;
                c = 1 + aa / c; if (Math.Abs(c) < Tiny) c = Tiny;
                d = 1 / d;
                h *= d * c;
                aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
                d = 1 + aa * d; if (Math.Abs(d) < Tiny) d = Tiny;
                c = 1 + aa / c; if (Math.Abs(c) < Tiny) c = Tiny;
                d = 1 / d;
                double delta = d * c;
                h *= delta;
                if (Math.Abs(delta - 1) < Eps) break;
            }
            return h;
        }

        /// <summary>The x with I_x(a, b) = y (0 ≤ y ≤ 1).</summary>
        public static double IncompleteBetaInverse(double a, double b, double y)
        {
            if (!(a > 0) || !(b > 0)) throw new ArgumentOutOfRangeException(nameof(a), "The inverse incomplete beta needs a, b > 0.");
            if (!(y >= 0 && y <= 1)) throw new ArgumentOutOfRangeException(nameof(y), "The inverse incomplete beta needs 0 <= y <= 1.");
            if (y == 0) return 0;
            if (y == 1) return 1;
            // Solve in the tail that is small (the other side by symmetry) so 1 − x keeps its precision.
            if (y > 0.5) return 1 - InverseIncompleteBetaLower(b, a, 1 - y);
            return InverseIncompleteBetaLower(a, b, y);
        }

        private static double InverseIncompleteBetaLower(double a, double b, double y)
        {
            // first guess (Abramowitz & Stegun 26.5.22 for a, b ≥ 1; otherwise the two power-law ends)
            double x;
            if (a >= 1 && b >= 1)
            {
                double pp = y < 0.5 ? y : 1 - y;
                double t = Math.Sqrt(-2 * Math.Log(pp));
                double z = (2.30753 + t * 0.27061) / (1 + t * (0.99229 + t * 0.04481)) - t;
                if (y < 0.5) z = -z;
                double al = (z * z - 3) / 6;
                double h = 2 / (1 / (2 * a - 1) + 1 / (2 * b - 1));
                double w = z * Math.Sqrt(al + h) / h - (1 / (2 * b - 1) - 1 / (2 * a - 1)) * (al + 5.0 / 6 - 2 / (3 * h));
                x = a / (a + b * Math.Exp(2 * w));
            }
            else
            {
                double lna = Math.Log(a / (a + b)), lnb = Math.Log(b / (a + b));
                double t = Math.Exp(a * lna) / a, u = Math.Exp(b * lnb) / b, w = t + u;
                x = y < t / w ? Math.Pow(a * w * y, 1 / a) : 1 - Math.Pow(b * w * (1 - y), 1 / b);
            }
            double lo = 0, hi = 1;
            if (!(x > 0 && x < 1)) x = 0.5;
            double lnB = LogBeta(a, b);
            for (int i = 0; i < 300; i++)
            {
                double f = IncompleteBeta(a, b, x) - y;
                if (f < 0) lo = x; else hi = x;
                if (f == 0) return x;
                double pdf = Math.Exp((a - 1) * Math.Log(x) + (b - 1) * Log1p(-x) - lnB);
                double step = f / pdf;
                double next = x - step / (1 - 0.5 * Clamp(step * ((a - 1) / x - (b - 1) / (1 - x))));   // Halley
                if (!(next > lo && next < hi) || double.IsNaN(next)) next = (lo + hi) / 2;
                if (Math.Abs(next - x) <= 1e-15 * next) return next;
                x = next;
            }
            return x;
        }

        // ── error functions and the normal ───────────────────────────────────

        /// <summary>erf x.</summary>
        public static double Erf(double x)
        {
            if (x == 0) return 0;
            if (Math.Abs(x) < 0.5)   // the series of erf itself keeps the relative precision near 0
            {
                double x2 = x * x, term = x, sum = x;
                for (int n = 1; n < 60; n++)
                {
                    term *= -x2 / n;
                    double add = term / (2 * n + 1);
                    sum += add;
                    if (Math.Abs(add) < Math.Abs(sum) * Eps) break;
                }
                return sum * 2 / Math.Sqrt(Math.PI);
            }
            double v = GammaP(0.5, x * x);
            return x < 0 ? -v : v;
        }

        /// <summary>erfc x = 1 − erf x, accurate in the tail.</summary>
        public static double Erfc(double x)
        {
            if (x < 0.5) return 1 - Erf(x);
            return GammaQ(0.5, x * x);
        }

        /// <summary>Standard normal CDF Φ(x).</summary>
        public static double NormalCdf(double x) => 0.5 * Erfc(-x / Math.Sqrt(2));

        /// <summary>Standard normal quantile Φ⁻¹(p), 0 &lt; p &lt; 1.</summary>
        public static double NormalQuantile(double p)
        {
            if (!(p > 0 && p < 1)) throw new ArgumentOutOfRangeException(nameof(p), "The normal quantile needs 0 < p < 1.");
            // Acklam's rational approximation (relative error < 1.15e-9) ...
            const double pLow = 0.02425;
            double x;
            if (p < pLow)
            {
                double q = Math.Sqrt(-2 * Math.Log(p));
                x = (((((-7.784894002430293e-03 * q - 3.223964580411365e-01) * q - 2.400758277161838e+00) * q - 2.549732539343734e+00) * q + 4.374664141464968e+00) * q + 2.938163982698783e+00)
                    / ((((7.784695709041462e-03 * q + 3.224671290700398e-01) * q + 2.445134137142996e+00) * q + 3.754408661907416e+00) * q + 1);
            }
            else if (p <= 1 - pLow)
            {
                double q = p - 0.5, r = q * q;
                x = (((((-3.969683028665376e+01 * r + 2.209460984245205e+02) * r - 2.759285104469687e+02) * r + 1.383577518672690e+02) * r - 3.066479806614716e+01) * r + 2.506628277459239e+00) * q
                    / (((((-5.447609879822406e+01 * r + 1.615858368580409e+02) * r - 1.556989798598866e+02) * r + 6.680131188771972e+01) * r - 1.328068155288572e+01) * r + 1);
            }
            else
            {
                double q = Math.Sqrt(-2 * Math.Log(1 - p));
                x = -(((((-7.784894002430293e-03 * q - 3.223964580411365e-01) * q - 2.400758277161838e+00) * q - 2.549732539343734e+00) * q + 4.374664141464968e+00) * q + 2.938163982698783e+00)
                    / ((((7.784695709041462e-03 * q + 3.224671290700398e-01) * q + 2.445134137142996e+00) * q + 3.754408661907416e+00) * q + 1);
            }
            // ... refined by Halley steps on Φ(x) − p (each roughly triples the correct digits)
            for (int i = 0; i < 2; i++)
            {
                double e = p < 0.5 ? NormalCdf(x) - p : (1 - p) - NormalCdf(-x);   // Φ(x) − p, from the small side
                double u = e * Math.Sqrt(2 * Math.PI) * Math.Exp(x * x / 2);
                x -= u / (1 + x * u / 2);
            }
            return x;
        }

        // ── distributions ────────────────────────────────────────────────────

        /// <summary>Normal CDF, N(mean, stdDev²).</summary>
        public static double NormalCdf(double x, double mean, double stdDev)
        {
            Positive(stdDev, nameof(stdDev));
            return NormalCdf((x - mean) / stdDev);
        }

        /// <summary>Normal quantile, N(mean, stdDev²).</summary>
        public static double NormalQuantile(double p, double mean, double stdDev)
        {
            Positive(stdDev, nameof(stdDev));
            return mean + stdDev * NormalQuantile(p);
        }

        /// <summary>Exponential CDF with <paramref name="rate"/> (mean 1 / rate).</summary>
        public static double ExponentialCdf(double x, double rate)
        {
            Positive(rate, nameof(rate));
            return x < 0 ? 0 : -Expm1(-rate * x);
        }

        /// <summary>Exponential quantile.</summary>
        public static double ExponentialQuantile(double p, double rate)
        {
            Prob(p); Positive(rate, nameof(rate));
            return -Log1p(-p) / rate;
        }

        /// <summary>Weibull CDF.</summary>
        public static double WeibullCdf(double x, double shape, double scale)
        {
            Positive(shape, nameof(shape)); Positive(scale, nameof(scale));
            return x < 0 ? 0 : -Expm1(-Math.Pow(x / scale, shape));
        }

        /// <summary>Weibull quantile.</summary>
        public static double WeibullQuantile(double p, double shape, double scale)
        {
            Prob(p); Positive(shape, nameof(shape)); Positive(scale, nameof(scale));
            return scale * Math.Pow(-Log1p(-p), 1 / shape);
        }

        /// <summary>Log-normal CDF (<paramref name="mu"/>, <paramref name="sigma"/> of the underlying normal).</summary>
        public static double LogNormalCdf(double x, double mu, double sigma)
        {
            Positive(sigma, nameof(sigma));
            return x <= 0 ? 0 : NormalCdf((Math.Log(x) - mu) / sigma);
        }

        /// <summary>Log-normal quantile.</summary>
        public static double LogNormalQuantile(double p, double mu, double sigma)
        {
            Prob(p); Positive(sigma, nameof(sigma));
            return Math.Exp(mu + sigma * NormalQuantile(p));
        }

        /// <summary>χ² CDF with <paramref name="df"/> degrees of freedom.</summary>
        public static double ChiSquareCdf(double x, double df)
        {
            Positive(df, nameof(df));
            return x <= 0 ? 0 : GammaP(df / 2, x / 2);
        }

        /// <summary>χ² quantile (with ALGLIB the old <c>ChiSquareInvCDF</c> returned the upper-tail point).</summary>
        public static double ChiSquareQuantile(double p, double df)
        {
            Prob(p); Positive(df, nameof(df));
            return 2 * GammaPInverse(df / 2, p);
        }

        /// <summary>Student's t CDF with <paramref name="df"/> degrees of freedom.</summary>
        public static double StudentTCdf(double t, double df)
        {
            Positive(df, nameof(df));
            if (t == 0) return 0.5;
            double tail = 0.5 * IncompleteBeta(df / 2, 0.5, df / (df + t * t));   // P(T > |t|)
            return t > 0 ? 1 - tail : tail;
        }

        /// <summary>Student's t quantile.</summary>
        public static double StudentTQuantile(double p, double df)
        {
            Prob(p); Positive(df, nameof(df));
            if (p == 0.5) return 0;
            double tail = 2 * Math.Min(p, 1 - p);                 // P(|T| > t) = I_{df/(df+t²)}(df/2, 1/2)
            double t;
            if (tail < 0.5)
            {
                double y = IncompleteBetaInverse(df / 2, 0.5, tail);          // y = df / (df + t²)
                t = Math.Sqrt(df * (1 - y) / y);
            }
            else
            {
                double z = IncompleteBetaInverse(0.5, df / 2, 1 - tail);      // z = 1 − y, precise near 0
                t = Math.Sqrt(df * z / (1 - z));
            }
            return p < 0.5 ? -t : t;
        }

        /// <summary>F CDF with (<paramref name="d1"/>, <paramref name="d2"/>) degrees of freedom.</summary>
        public static double FCdf(double x, double d1, double d2)
        {
            Positive(d1, nameof(d1)); Positive(d2, nameof(d2));
            if (x <= 0) return 0;
            double w = d1 * x / (d1 * x + d2);
            // near 1, use the small complementary argument (1 − w would lose its digits)
            return w <= 0.5 ? IncompleteBeta(d1 / 2, d2 / 2, w) : 1 - IncompleteBeta(d2 / 2, d1 / 2, d2 / (d1 * x + d2));
        }

        /// <summary>F quantile (with ALGLIB the old <c>FInvCDF</c> returned the upper-tail point).</summary>
        public static double FQuantile(double p, double d1, double d2)
        {
            Prob(p); Positive(d1, nameof(d1)); Positive(d2, nameof(d2));
            if (p <= 0.5)
            {
                double y = IncompleteBetaInverse(d1 / 2, d2 / 2, p);
                return d2 * y / (d1 * (1 - y));
            }
            double z = IncompleteBetaInverse(d2 / 2, d1 / 2, 1 - p);           // z = 1 − y
            return d2 * (1 - z) / (d1 * z);
        }

        /// <summary>Poisson probability P(X = k).</summary>
        public static double PoissonPmf(int k, double lambda)
        {
            Positive(lambda, nameof(lambda));
            return k < 0 ? 0 : Math.Exp(k * Math.Log(lambda) - lambda - LogGamma(k + 1));
        }

        /// <summary>Poisson CDF P(X ≤ k).</summary>
        public static double PoissonCdf(int k, double lambda)
        {
            Positive(lambda, nameof(lambda));
            return k < 0 ? 0 : GammaQ(k + 1, lambda);
        }

        /// <summary>Binomial probability P(X = k) for <paramref name="n"/> trials with success probability <paramref name="p"/>.</summary>
        public static double BinomialPmf(int k, int n, double p)
        {
            Trials(n, p);
            if (k < 0 || k > n) return 0;
            if (p == 0) return k == 0 ? 1 : 0;
            if (p == 1) return k == n ? 1 : 0;
            double logChoose = LogGamma(n + 1) - LogGamma(k + 1) - LogGamma(n - k + 1);
            return Math.Exp(logChoose + k * Math.Log(p) + (n - k) * Log1p(-p));
        }

        /// <summary>Binomial CDF P(X ≤ k).</summary>
        public static double BinomialCdf(int k, int n, double p)
        {
            Trials(n, p);
            if (k < 0) return 0;
            if (k >= n) return 1;
            if (p <= 0) return 1;
            if (p >= 1) return 0;
            return IncompleteBeta(n - k, k + 1, 1 - p);
        }

        // ── checks and helpers ───────────────────────────────────────────────

        private static void Positive(double v, string name)
        {
            if (!(v > 0)) throw new ArgumentOutOfRangeException(name, $"{name} must be positive (got {v}).");
        }

        private static void Prob(double p)
        {
            if (!(p > 0 && p < 1)) throw new ArgumentOutOfRangeException(nameof(p), $"A quantile needs 0 < p < 1 (got {p}).");
        }

        private static void Trials(int n, double p)
        {
            if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), "n must be non-negative.");
            if (!(p >= 0 && p <= 1)) throw new ArgumentOutOfRangeException(nameof(p), "p must be in [0, 1].");
        }

        // e^v, kept at the smallest positive double instead of underflowing to 0 (a start point for a bracketed search)
        private static double FromLog(double v) => v < -744 ? double.Epsilon : Math.Exp(v);

        // A Halley correction is only used while small: a large one means the curvature estimate is poor.
        private static double Clamp(double v) => v > 1 ? 1 : v < -1 ? -1 : v;

        // ── for fitting ──────────────────────────────────────────

        /// <summary>Digamma ψ(x) = d ln Γ(x) / dx, x &gt; 0 (recurrence to x ≥ 6, then the asymptotic series).</summary>
        public static double Digamma(double x)
        {
            if (!(x > 0)) throw new ArgumentOutOfRangeException(nameof(x), "Digamma needs x > 0.");
            double r = 0;
            while (x < 6) { r -= 1 / x; x += 1; }
            double f = 1 / (x * x);
            return r + Math.Log(x) - 0.5 / x
                   - f * (1.0 / 12 - f * (1.0 / 120 - f * (1.0 / 252 - f * (1.0 / 240 - f * (1.0 / 132)))));
        }

        /// <summary>Trigamma ψ′(x), x &gt; 0 (recurrence to x ≥ 6, then the asymptotic series).</summary>
        public static double Trigamma(double x)
        {
            if (!(x > 0)) throw new ArgumentOutOfRangeException(nameof(x), "Trigamma needs x > 0.");
            double r = 0;
            while (x < 6) { r += 1 / (x * x); x += 1; }
            double f = 1 / (x * x);
            return r + 1 / x + f / 2
                   + f / x * (1.0 / 6 - f * (1.0 / 30 - f * (1.0 / 42 - f * (1.0 / 30 - f * 5.0 / 66))));
        }

        /// <summary>
        /// P-value of the Kolmogorov–Smirnov statistic <paramref name="d"/> for a sample of <paramref name="n"/>
        /// (fully specified distribution): Stephens' approximation Q((√n + 0.12 + 0.11/√n)·d) with the Kolmogorov
        /// series Q(λ) = 2 Σ (−1)^(k−1) e^(−2k²λ²). With estimated parameters it overstates the p-value.
        /// </summary>
        public static double KolmogorovSmirnovP(double d, int n)
        {
            if (n <= 0 || !(d > 0)) return 1;
            double sn = Math.Sqrt(n), lambda = (sn + 0.12 + 0.11 / sn) * d;
            if (lambda < 0.2) return 1;
            double sum = 0, sign = 1;
            for (int k = 1; k <= 100; k++)
            {
                double term = Math.Exp(-2 * k * k * lambda * lambda);
                sum += sign * term;
                if (term < 1e-16) break;
                sign = -sign;
            }
            return Math.Max(0, Math.Min(1, 2 * sum));
        }

        /// <summary>
        /// P-value of the Anderson–Darling statistic <paramref name="a2"/> for a sample of <paramref name="n"/> (fully
        /// specified distribution): Marsaglia &amp; Marsaglia's (2004) asymptotic adinf with their finite-n correction.
        /// With estimated parameters it overstates the p-value.
        /// </summary>
        public static double AndersonDarlingP(double a2, int n)
        {
            if (n <= 0 || !(a2 > 0)) return 1;
            double cdf;   // P(A² < a2), asymptotic
            if (a2 < 2)
                cdf = Math.Exp(-1.2337141 / a2) / Math.Sqrt(a2)
                      * (2.00012 + (0.247105 - (0.0649821 - (0.0347962 - (0.011672 - 0.00168691 * a2) * a2) * a2) * a2) * a2);
            else
                cdf = Math.Exp(-Math.Exp(1.0776 - (2.30695 - (0.43424 - (0.082433 - (0.008056 - 0.0003146 * a2) * a2) * a2) * a2) * a2));
            cdf = Math.Max(0, Math.Min(1, cdf));
            // finite-n correction (errfix)
            double c = 0.01265 + 0.1757 / n, e;
            if (cdf < c)
            {
                double u = cdf / c;
                e = (0.0037 / (n * (double)n) + 0.00078 / n + 0.00006) * Math.Sqrt(u) * (1 - u) * (49 * u - 102);
            }
            else if (cdf < 0.8)
            {
                double u = (cdf - c) / (0.8 - c);
                e = (0.04213 / n + 0.01365 / (n * (double)n)) * (-0.00022633 + (6.54034 - (14.6538 - (14.458 - (8.259 - 1.91864 * u) * u) * u) * u) * u);
            }
            else
            {
                double u = cdf;
                e = (-130.2137 + (745.2337 - (1705.091 - (1950.646 - (1116.360 - 255.7844 * u) * u) * u) * u) * u) / n;
            }
            return Math.Max(0, Math.Min(1, 1 - (cdf + e)));
        }

        private static double Expm1(double x) => Math.Abs(x) < 1e-5 ? x * (1 + x * (0.5 + x / 6)) : Math.Exp(x) - 1;

        private static double Log1p(double x) => Math.Abs(x) < 1e-4 ? x * (1 - x * (0.5 - x * (1.0 / 3 - x / 4))) : Math.Log(1 + x);
    }
}
