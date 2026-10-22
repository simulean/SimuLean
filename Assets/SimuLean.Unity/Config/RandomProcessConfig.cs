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
        [TextArea(3, 10)]
        public string expression = "processingTime";

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

        private static string Inv(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Encodes this distribution in the compact <see cref="SimuLean.Serialization.SamplerSpec"/> format (<c>Type~p1~p2</c>).</summary>
        public string ToSamplerSpec()
        {
            switch (strategyType)
            {
                case RandomProcessType.Constant:        return $"Constant~{Inv(constantValue)}";
                case RandomProcessType.Uniform:         return $"Uniform~{Inv(uniformMin)}~{Inv(uniformMax)}";
                case RandomProcessType.Normal:          return $"Normal~{Inv(normalMean)}~{Inv(normalStdDev)}";
                case RandomProcessType.Exponential:     return $"Exponential~{Inv(exponentialRate)}";
                case RandomProcessType.Triangular:      return $"Triangular~{Inv(triangularMin)}~{Inv(triangularMode)}~{Inv(triangularMax)}";
                case RandomProcessType.Expression: return $"Expression~{expression}";
                case RandomProcessType.Gamma:           return $"Gamma~{Inv(gammaShape)}~{Inv(gammaScale)}";
                case RandomProcessType.Weibull:         return $"Weibull~{Inv(weibullShape)}~{Inv(weibullScale)}";
                case RandomProcessType.LogNormal:       return $"LogNormal~{Inv(logNormalMu)}~{Inv(logNormalSigma)}";
                case RandomProcessType.Beta:            return $"Beta~{Inv(betaAlpha)}~{Inv(betaBeta)}~{Inv(betaMin)}~{Inv(betaMax)}";
                case RandomProcessType.ChiSquare:       return $"ChiSquare~{chiSquareDf}";
                case RandomProcessType.StudentT:        return $"StudentT~{studentTDf}~{studentTClamp}";
                case RandomProcessType.FDistribution:   return $"FDistribution~{fDistD1}~{fDistD2}";
                case RandomProcessType.Poisson:         return $"Poisson~{Inv(poissonLambda)}";
                case RandomProcessType.Binomial:        return $"Binomial~{binomialN}~{Inv(binomialP)}";
                case RandomProcessType.DiscreteUniform: return $"DiscreteUniform~{discreteUniformMin}~{(int)discreteUniformMax}";
                default:                                return $"Constant~{Inv(constantValue)}";
            }
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
