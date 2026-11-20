using System;
using UnityEngine;
using SimuLean;
using SimuLean.Stochastics.Distributions;

namespace SimuLean.Unity
{
    /// <summary>
    /// Base class for reusable random process configuration components.
    /// Can be embedded in any DES element that needs random value generation.
    /// This is a general-purpose class for modeling random quantities (delays, quantities, durations, etc.).
    /// Supports both continuous and discrete distributions.
    /// </summary>
    [Serializable]
    public class RandomProcessConfig
    {
        [Tooltip("Type of random process strategy")]
        public RandomProcessType strategyType = RandomProcessType.Constant;

        // ── Constant ──────────────────────────────────────────────────
        [Tooltip("For Constant: Fixed value")]
        public double constantValue = 5.0;

        // ── Uniform ───────────────────────────────────────────────────
        [Tooltip("For Uniform: Minimum value")]
        public double uniformMin = 3.0;

        [Tooltip("For Uniform: Maximum value")]
        public double uniformMax = 7.0;

        // ── Normal ────────────────────────────────────────────────────
        [Tooltip("For Normal: Mean value")]
        public double normalMean = 5.0;

        [Tooltip("For Normal: Standard deviation")]
        public double normalStdDev = 1.0;

        // ── Exponential ───────────────────────────────────────────────
        [Tooltip("For Exponential: Rate (lambda) - Note: Mean = 1/rate")]
        public double exponentialRate = 0.2;

        // ── Triangular ────────────────────────────────────────────────
        [Tooltip("For Triangular: Minimum value")]
        public double triangularMin = 3.0;

        [Tooltip("For Triangular: Mode (most likely value)")]
        public double triangularMode = 5.0;

        [Tooltip("For Triangular: Maximum value")]
        public double triangularMax = 8.0;

        // ── Expression (the common syntax: item.x, element.x, functions…) ──
        [Tooltip("For Expression: value from the item and the element, e.g. processingTime or item.weight * element.secondsPerKg")]
                [SimuLean.Unity.SimuLeanExpression("time")]
        public string expression = "";

        // ── Gamma ─────────────────────────────────────────────────────
        [Tooltip("For Gamma: Shape parameter (k)")]
        public double gammaShape = 2.0;

        [Tooltip("For Gamma: Scale parameter (theta) - Mean = shape * scale")]
        public double gammaScale = 2.5;

        // ── Weibull ───────────────────────────────────────────────────
        [Tooltip("For Weibull: Shape parameter (k)")]
        public double weibullShape = 1.5;

        [Tooltip("For Weibull: Scale parameter (lambda)")]
        public double weibullScale = 5.0;

        // ── Log-Normal ────────────────────────────────────────────────
        [Tooltip("For LogNormal: Mu parameter")]
        public double logNormalMu = 1.5;

        [Tooltip("For LogNormal: Sigma parameter")]
        public double logNormalSigma = 0.5;

        // ── Beta ──────────────────────────────────────────────────────
        [Tooltip("For Beta: Alpha parameter")]
        public double betaAlpha = 2.0;

        [Tooltip("For Beta: Beta parameter")]
        public double betaBeta = 5.0;

        [Tooltip("For Beta: Minimum value")]
        public double betaMin = 0.0;

        [Tooltip("For Beta: Maximum value")]
        public double betaMax = 1.0;

        // ── Chi-Square ────────────────────────────────────────────────
        [Tooltip("For ChiSquare: Degrees of freedom")]
        public int chiSquareDf = 5;

        // ── Student T ─────────────────────────────────────────────────
        [Tooltip("For StudentT: Degrees of freedom")]
        public int studentTDf = 10;

        [Tooltip("For StudentT: Clamp to non-negative values")]
        public bool studentTClamp = true;

        // ── F-Distribution ────────────────────────────────────────────
        [Tooltip("For FDistribution: Degrees of freedom 1")]
        public int fDistD1 = 5;

        [Tooltip("For FDistribution: Degrees of freedom 2")]
        public int fDistD2 = 10;

        // ── Poisson ───────────────────────────────────────────────────
        [Tooltip("For Poisson: Lambda parameter (average number of events)")]
        public double poissonLambda = 3.0;

        // ── Binomial ──────────────────────────────────────────────────
        [Tooltip("For Binomial: Number of trials")]
        public int binomialN = 10;

        [Tooltip("For Binomial: Probability of success")]
        public double binomialP = 0.5;

        // ── Discrete Uniform ──────────────────────────────────────────────────
        [Tooltip("For Discrete Uniform: Minimum value")]
        public int discreteUniformMin = 3;

        [Tooltip("For Discrete Uniform: Minimum valuess")]
        public double discreteUniformMax = 7;

        /// <summary>The engine settings of this distribution (<see cref="SimuLean.Serialization.SamplerSettings"/>: its type
        /// and the chosen distribution's fields only; the build side is <c>ElementBuildHelpers.BuildRandomProcess</c>).</summary>
        public SimuLean.Serialization.SamplerSettings ToSettings()
        {
            var s = new SimuLean.Serialization.SamplerSettings { StrategyType = GetStrategyTypeName() };
            switch (strategyType)
            {
                case RandomProcessType.Constant: s.ConstantValue = constantValue; break;
                case RandomProcessType.Uniform: s.UniformMin = uniformMin; s.UniformMax = uniformMax; break;
                case RandomProcessType.Normal: s.NormalMean = normalMean; s.NormalStdDev = normalStdDev; break;
                case RandomProcessType.Exponential: s.ExponentialRate = exponentialRate; break;
                case RandomProcessType.Triangular: s.TriangularMin = triangularMin; s.TriangularMode = triangularMode; s.TriangularMax = triangularMax; break;
                case RandomProcessType.Expression: s.Expression = expression; break;
                case RandomProcessType.Gamma: s.GammaShape = gammaShape; s.GammaScale = gammaScale; break;
                case RandomProcessType.Weibull: s.WeibullShape = weibullShape; s.WeibullScale = weibullScale; break;
                case RandomProcessType.LogNormal: s.LogNormalMu = logNormalMu; s.LogNormalSigma = logNormalSigma; break;
                case RandomProcessType.Beta: s.BetaAlpha = betaAlpha; s.BetaBeta = betaBeta; s.BetaMin = betaMin; s.BetaMax = betaMax; break;
                case RandomProcessType.ChiSquare: s.ChiSquareDf = chiSquareDf; break;
                case RandomProcessType.StudentT: s.StudentTDf = studentTDf; s.StudentTClamp = studentTClamp; break;
                case RandomProcessType.FDistribution: s.FDistD1 = fDistD1; s.FDistD2 = fDistD2; break;
                case RandomProcessType.Poisson: s.PoissonLambda = poissonLambda; break;
                case RandomProcessType.Binomial: s.BinomialN = binomialN; s.BinomialP = binomialP; break;
                case RandomProcessType.DiscreteUniform: s.DiscreteUniformMin = discreteUniformMin; s.DiscreteUniformMax = (int)discreteUniformMax; break;
            }
            return s;
        }

        public virtual string GetStrategyTypeName()
        {
            return strategyType.ToString();
        }

        public virtual bool ValidateConfiguration(out string errorMessage)
        {
            errorMessage = null;
            return true;
        }
    }

    public enum RandomProcessType
    {
        // Continuous
        Constant,
        Uniform,
        Normal,
        Exponential,
        Triangular,
        Expression,
        Gamma,
        Weibull,
        LogNormal,
        Beta,
        ChiSquare,
        StudentT,
        FDistribution,
        // Discrete
        Poisson,
        Binomial,
        DiscreteUniform
    }
}
