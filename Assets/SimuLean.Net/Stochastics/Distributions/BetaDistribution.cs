using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Beta distribution.
    /// Generates random values in the range [0, 1] following a Beta distribution.
    /// Commonly used for modeling probabilities, proportions, and in Bayesian statistics.
    /// </summary>
    public class BetaDistribution : ContinuousDistributionBase
    {
        private double alpha; // First shape parameter
        private double beta;  // Second shape parameter
        private readonly double minValue = 0.0;
        private readonly double scaleRange = 1.0;

        /// <summary>
        /// Creates a Beta distribution with specified shape parameters.
        /// Generates values in [0, 1].
        /// </summary>
        /// <param name="alpha">First shape parameter (alpha). Must be positive</param>
        /// <param name="beta">Second shape parameter (beta). Must be positive</param>
        public BetaDistribution(double alpha, double beta) : base()
        {
            this.alpha = alpha;
            this.beta = beta;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a Beta distribution scaled to [minValue, maxValue].
        /// Useful for modeling durations or proportions within a specific range.
        /// </summary>
        public BetaDistribution(double alpha, double beta, double minValue, double maxValue) : base()
        {
            if (minValue >= maxValue)
                throw new ArgumentException("minValue must be less than maxValue");
            this.alpha = alpha;
            this.beta = beta;
            this.minValue = minValue;
            this.scaleRange = maxValue - minValue;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a Beta distribution with a specific random engine.
        /// </summary>
        public BetaDistribution(double alpha, double beta, IRandomEngine randomEngine) : base(randomEngine)
        {
            this.alpha = alpha;
            this.beta = beta;
            ValidateParameters();
        }

        protected override void ValidateParameters()
        {
            if (alpha <= 0)
                throw new ArgumentException("Alpha must be positive", nameof(alpha));
            if (beta <= 0)
                throw new ArgumentException("Beta must be positive", nameof(beta));
        }

        public double Alpha => alpha;
        public double Beta => beta;

        /// <summary>
        /// Mean of the Beta distribution.
        /// </summary>
        public double Mean => alpha / (alpha + beta);

        /// <summary>
        /// Variance of the Beta distribution.
        /// </summary>
        public double Variance
        {
            get
            {
                double sum = alpha + beta;
                return (alpha * beta) / (sum * sum * (sum + 1.0));
            }
        }

        public void SetAlpha(double value)
        {
            alpha = value;
            ValidateParameters();
        }

        public void SetBeta(double value)
        {
            beta = value;
            ValidateParameters();
        }

        public override double Sample()
        {
            // Inverse transform sampling via alglib's inverse incomplete Beta function
            double u = NextOpenUnit();
            double rawSample = Probability.IncompleteBetaInverse(alpha, beta, u);
            return minValue + rawSample * scaleRange;
        }
    }
}
