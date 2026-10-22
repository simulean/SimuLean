using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// F distribution (Fisher-Snedecor distribution).
    /// Ratio of two chi-squared distributions divided by their degrees of freedom.
    /// Commonly used in ANOVA and regression analysis.
    /// Uses ALGLIB for high-precision CDF and inverse CDF calculations.
    /// </summary>
    public class FDistribution : ContinuousDistributionBase
    {
        private int degreesOfFreedom1;
        private int degreesOfFreedom2;

        /// <summary>
        /// Creates an F distribution with specified degrees of freedom.
        /// </summary>
        /// <param name="degreesOfFreedom1">Numerator degrees of freedom (d1). Must be positive integer.</param>
        /// <param name="degreesOfFreedom2">Denominator degrees of freedom (d2). Must be positive integer.</param>
        public FDistribution(int degreesOfFreedom1, int degreesOfFreedom2) : base()
        {
            this.degreesOfFreedom1 = degreesOfFreedom1;
            this.degreesOfFreedom2 = degreesOfFreedom2;
            ValidateParameters();
        }

        /// <summary>
        /// Creates an F distribution with a specific random engine.
        /// </summary>
        public FDistribution(int degreesOfFreedom1, int degreesOfFreedom2, IRandomEngine randomEngine) : base(randomEngine)
        {
            this.degreesOfFreedom1 = degreesOfFreedom1;
            this.degreesOfFreedom2 = degreesOfFreedom2;
            ValidateParameters();
        }

        protected override void ValidateParameters()
        {
            if (degreesOfFreedom1 <= 0)
                throw new ArgumentException("Degrees of freedom 1 must be positive", nameof(degreesOfFreedom1));
            if (degreesOfFreedom2 <= 0)
                throw new ArgumentException("Degrees of freedom 2 must be positive", nameof(degreesOfFreedom2));
        }

        public int DegreesOfFreedom1 => degreesOfFreedom1;
        public int DegreesOfFreedom2 => degreesOfFreedom2;

        public void SetDegreesOfFreedom(int df1, int df2)
        {
            degreesOfFreedom1 = df1;
            degreesOfFreedom2 = df2;
            ValidateParameters();
        }

        public override double Sample()
        {
            // Use inverse transform sampling with ALGLIB
            double u = NextOpenUnit();
            return Probability.FQuantile(u, degreesOfFreedom1, degreesOfFreedom2);
        }

        /// <summary>
        /// Calculates the probability density function at x.
        /// </summary>
        public double PDF(double x)
        {
            if (x < 0) return 0;

            int d1 = degreesOfFreedom1;
            int d2 = degreesOfFreedom2;

            double numerator = Math.Pow(d1 * x, d1) * Math.Pow(d2, d2);
            numerator = Math.Sqrt(numerator) / (x * Probability.Beta(d1 / 2.0, d2 / 2.0));
            
            double denominator = Math.Pow(d1 * x + d2, (d1 + d2) / 2.0);
            
            return numerator / denominator;
        }

        /// <summary>
        /// Calculates the cumulative distribution function at x.
        /// </summary>
        public double CDF(double x)
        {
            return Probability.FCdf(x, degreesOfFreedom1, degreesOfFreedom2);
        }

        /// <summary>
        /// Gets the mean of the distribution.
        /// Mean = d2/(d2-2) for d2 > 2, undefined otherwise.
        /// </summary>
        public double Mean
        {
            get
            {
                if (degreesOfFreedom2 > 2)
                    return degreesOfFreedom2 / (double)(degreesOfFreedom2 - 2);
                else
                    return double.NaN;
            }
        }

        /// <summary>
        /// Gets the variance of the distribution.
        /// Defined for d2 > 4, infinite for 2 < d2 <= 4, undefined otherwise.
        /// </summary>
        public double Variance
        {
            get
            {
                int d1 = degreesOfFreedom1;
                int d2 = degreesOfFreedom2;

                if (d2 > 4)
                {
                    double numerator = 2.0 * d2 * d2 * (d1 + d2 - 2);
                    double denominator = d1 * (d2 - 2) * (d2 - 2) * (d2 - 4);
                    return numerator / denominator;
                }
                else if (d2 > 2)
                    return double.PositiveInfinity;
                else
                    return double.NaN;
            }
        }
    }
}
