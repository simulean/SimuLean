using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Chi-Square distribution.
    /// Represents the sum of squares of k independent standard normal random variables.
    /// Commonly used in hypothesis testing and goodness-of-fit tests.
    /// Uses ALGLIB for high-precision CDF and inverse CDF calculations.
    /// </summary>
    public class ChiSquareDistribution : ContinuousDistributionBase
    {
        private double degreesOfFreedom;

        /// <summary>
        /// Creates a Chi-Square distribution with specified degrees of freedom.
        /// </summary>
        /// <param name="degreesOfFreedom">Degrees of freedom (k). Must be positive.</param>
        public ChiSquareDistribution(double degreesOfFreedom) : base()
        {
            this.degreesOfFreedom = degreesOfFreedom;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a Chi-Square distribution with a specific random engine.
        /// </summary>
        public ChiSquareDistribution(double degreesOfFreedom, IRandomEngine randomEngine) : base(randomEngine)
        {
            this.degreesOfFreedom = degreesOfFreedom;
            ValidateParameters();
        }

        protected override void ValidateParameters()
        {
            if (degreesOfFreedom <= 0)
                throw new ArgumentException("Degrees of freedom must be positive", nameof(degreesOfFreedom));
        }

        public double DegreesOfFreedom => degreesOfFreedom;

        public void SetDegreesOfFreedom(double value)
        {
            degreesOfFreedom = value;
            ValidateParameters();
        }

        public override double Sample()
        {
            // Chi-Square is a special case of Gamma distribution
            // ChiSquare(k) = Gamma(k/2, 2)
            // We use inverse transform sampling with ALGLIB for accuracy
            double u = NextOpenUnit();
            return Probability.ChiSquareQuantile(u, degreesOfFreedom);
        }

        /// <summary>
        /// Calculates the probability density function at x.
        /// </summary>
        public double PDF(double x)
        {
            if (x < 0) return 0;
            
            double k = degreesOfFreedom;
            double logPdf = (k / 2 - 1) * Math.Log(x) - x / 2 - (k / 2) * Math.Log(2) - Probability.LogGamma(k / 2);
            return Math.Exp(logPdf);
        }

        /// <summary>
        /// Calculates the cumulative distribution function at x.
        /// </summary>
        public double CDF(double x)
        {
            return Probability.ChiSquareCdf(x, degreesOfFreedom);
        }
    }
}
