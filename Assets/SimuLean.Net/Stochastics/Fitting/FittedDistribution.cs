using System;
using System.Globalization;
using System.Linq;

namespace SimuLean.Stochastics.Fitting
{
    /// <summary>
    /// A distribution of the SimuLean catalog with fitted parameters: its CDF, density (or probability mass),
    /// mean, and the sampler spec <c>Type~p1~p2</c> the elements read (<c>SamplerSpec</c>, the inspectors' parameter
    /// order and conventions — Exponential by its rate).
    /// </summary>
    public abstract class FittedDistribution
    {
        /// <summary>The catalog type name (<c>Exponential</c>, <c>Gamma</c>…).</summary>
        public abstract string Family { get; }
        public abstract string[] ParameterNames { get; }
        public double[] Parameters { get; protected set; }
        public virtual bool Discrete => false;

        public abstract double Cdf(double x);
        /// <summary>Density (continuous) or probability mass at the integer <paramref name="x"/> (discrete).</summary>
        public abstract double Density(double x);
        public abstract double Mean { get; }

        public string ToSpec() => Family + string.Concat(Parameters.Select(p => "~" + p.ToString("R", CultureInfo.InvariantCulture)));

        public override string ToString()
            => Family + "(" + string.Join(", ", ParameterNames.Select((n, i) => n + " = " + Parameters[i].ToString("G6", CultureInfo.InvariantCulture))) + ")";
    }

    internal sealed class ExponentialFit : FittedDistribution
    {
        public ExponentialFit(double rate) { Parameters = new[] { rate }; }
        public override string Family => "Exponential";
        public override string[] ParameterNames => new[] { "rate" };
        private double Rate => Parameters[0];
        public override double Cdf(double x) => x <= 0 ? 0 : 1 - Math.Exp(-Rate * x);
        public override double Density(double x) => x < 0 ? 0 : Rate * Math.Exp(-Rate * x);
        public override double Mean => 1 / Rate;
    }

    internal sealed class NormalFit : FittedDistribution
    {
        public NormalFit(double mean, double sd) { Parameters = new[] { mean, sd }; }
        public override string Family => "Normal";
        public override string[] ParameterNames => new[] { "mean", "std dev" };
        public override double Cdf(double x) => Probability.NormalCdf(x, Parameters[0], Parameters[1]);
        public override double Density(double x)
        {
            double z = (x - Parameters[0]) / Parameters[1];
            return Math.Exp(-0.5 * z * z) / (Parameters[1] * Math.Sqrt(2 * Math.PI));
        }
        public override double Mean => Parameters[0];
    }

    internal sealed class LogNormalFit : FittedDistribution
    {
        public LogNormalFit(double mu, double sigma) { Parameters = new[] { mu, sigma }; }
        public override string Family => "LogNormal";
        public override string[] ParameterNames => new[] { "mu", "sigma" };
        public override double Cdf(double x) => x <= 0 ? 0 : Probability.NormalCdf((Math.Log(x) - Parameters[0]) / Parameters[1]);
        public override double Density(double x)
        {
            if (x <= 0) return 0;
            double z = (Math.Log(x) - Parameters[0]) / Parameters[1];
            return Math.Exp(-0.5 * z * z) / (x * Parameters[1] * Math.Sqrt(2 * Math.PI));
        }
        public override double Mean => Math.Exp(Parameters[0] + Parameters[1] * Parameters[1] / 2);
    }

    internal sealed class UniformFit : FittedDistribution
    {
        public UniformFit(double min, double max) { Parameters = new[] { min, max }; }
        public override string Family => "Uniform";
        public override string[] ParameterNames => new[] { "min", "max" };
        public override double Cdf(double x) => x <= Parameters[0] ? 0 : x >= Parameters[1] ? 1 : (x - Parameters[0]) / (Parameters[1] - Parameters[0]);
        public override double Density(double x) => x < Parameters[0] || x > Parameters[1] ? 0 : 1 / (Parameters[1] - Parameters[0]);
        public override double Mean => (Parameters[0] + Parameters[1]) / 2;
    }

    internal sealed class TriangularFit : FittedDistribution
    {
        public TriangularFit(double min, double mode, double max) { Parameters = new[] { min, mode, max }; }
        public override string Family => "Triangular";
        public override string[] ParameterNames => new[] { "min", "mode", "max" };
        public override double Cdf(double x)
        {
            double a = Parameters[0], c = Parameters[1], b = Parameters[2];
            if (x <= a) return 0;
            if (x >= b) return 1;
            return x <= c ? (x - a) * (x - a) / ((b - a) * (c - a)) : 1 - (b - x) * (b - x) / ((b - a) * (b - c));
        }
        public override double Density(double x)
        {
            double a = Parameters[0], c = Parameters[1], b = Parameters[2];
            if (x < a || x > b) return 0;
            return x <= c ? (c > a ? 2 * (x - a) / ((b - a) * (c - a)) : 2 / (b - a)) : (b > c ? 2 * (b - x) / ((b - a) * (b - c)) : 2 / (b - a));
        }
        public override double Mean => (Parameters[0] + Parameters[1] + Parameters[2]) / 3;
    }

    internal sealed class GammaFit : FittedDistribution
    {
        public GammaFit(double shape, double scale) { Parameters = new[] { shape, scale }; }
        public override string Family => "Gamma";
        public override string[] ParameterNames => new[] { "shape", "scale" };
        public override double Cdf(double x) => x <= 0 ? 0 : Probability.GammaP(Parameters[0], x / Parameters[1]);
        public override double Density(double x)
        {
            if (x <= 0) return 0;
            double k = Parameters[0], t = Parameters[1];
            return Math.Exp((k - 1) * Math.Log(x) - x / t - Probability.LogGamma(k) - k * Math.Log(t));
        }
        public override double Mean => Parameters[0] * Parameters[1];
    }

    internal sealed class WeibullFit : FittedDistribution
    {
        public WeibullFit(double shape, double scale) { Parameters = new[] { shape, scale }; }
        public override string Family => "Weibull";
        public override string[] ParameterNames => new[] { "shape", "scale" };
        public override double Cdf(double x) => x <= 0 ? 0 : 1 - Math.Exp(-Math.Pow(x / Parameters[1], Parameters[0]));
        public override double Density(double x)
        {
            if (x < 0) return 0;
            double k = Parameters[0], l = Parameters[1], z = x / l;
            return k / l * Math.Pow(z, k - 1) * Math.Exp(-Math.Pow(z, k));
        }
        public override double Mean => Parameters[1] * Probability.Gamma(1 + 1 / Parameters[0]);
    }

    internal sealed class BetaFit : FittedDistribution
    {
        public BetaFit(double alpha, double beta, double min, double max) { Parameters = new[] { alpha, beta, min, max }; }
        public override string Family => "Beta";
        public override string[] ParameterNames => new[] { "alpha", "beta", "min", "max" };
        private double Z(double x) => (x - Parameters[2]) / (Parameters[3] - Parameters[2]);
        public override double Cdf(double x)
        {
            double z = Z(x);
            return z <= 0 ? 0 : z >= 1 ? 1 : Probability.IncompleteBeta(Parameters[0], Parameters[1], z);
        }
        public override double Density(double x)
        {
            double z = Z(x), a = Parameters[0], b = Parameters[1];
            if (z <= 0 || z >= 1) return 0;
            return Math.Exp((a - 1) * Math.Log(z) + (b - 1) * Math.Log(1 - z) - Probability.LogBeta(a, b)) / (Parameters[3] - Parameters[2]);
        }
        public override double Mean => Parameters[2] + (Parameters[3] - Parameters[2]) * Parameters[0] / (Parameters[0] + Parameters[1]);
    }

    internal sealed class PoissonFit : FittedDistribution
    {
        public PoissonFit(double lambda) { Parameters = new[] { lambda }; }
        public override string Family => "Poisson";
        public override string[] ParameterNames => new[] { "lambda" };
        public override bool Discrete => true;
        public override double Cdf(double x) => x < 0 ? 0 : Probability.PoissonCdf((int)Math.Floor(x + 1e-9), Parameters[0]);
        public override double Density(double x) => x < 0 ? 0 : Probability.PoissonPmf((int)Math.Round(x), Parameters[0]);
        public override double Mean => Parameters[0];
    }

    internal sealed class BinomialFit : FittedDistribution
    {
        public BinomialFit(int n, double p) { Parameters = new[] { (double)n, p }; }
        public override string Family => "Binomial";
        public override string[] ParameterNames => new[] { "n", "p" };
        public override bool Discrete => true;
        private int N => (int)Parameters[0];
        public override double Cdf(double x) => x < 0 ? 0 : x >= N ? 1 : Probability.BinomialCdf((int)Math.Floor(x + 1e-9), N, Parameters[1]);
        public override double Density(double x) => x < 0 || x > N ? 0 : Probability.BinomialPmf((int)Math.Round(x), N, Parameters[1]);
        public override double Mean => N * Parameters[1];
    }

    internal sealed class DiscreteUniformFit : FittedDistribution
    {
        public DiscreteUniformFit(int min, int max) { Parameters = new[] { (double)min, max }; }
        public override string Family => "DiscreteUniform";
        public override string[] ParameterNames => new[] { "min", "max" };
        public override bool Discrete => true;
        private double Count => Parameters[1] - Parameters[0] + 1;
        public override double Cdf(double x) => x < Parameters[0] ? 0 : x >= Parameters[1] ? 1 : (Math.Floor(x + 1e-9) - Parameters[0] + 1) / Count;
        public override double Density(double x) => x < Parameters[0] || x > Parameters[1] ? 0 : 1 / Count;
        public override double Mean => (Parameters[0] + Parameters[1]) / 2;
    }
}
