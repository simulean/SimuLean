using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Student's t distribution.
    /// Arises when estimating the mean of a normally distributed population when sample size is small.
    /// Commonly used in hypothesis testing and confidence intervals.
    /// Uses ALGLIB for high-precision CDF and inverse CDF calculations.
    /// </summary>
    public class StudentTDistribution : ContinuousDistributionBase
    {
        private int degreesOfFreedom;
        private readonly bool clampNonNegative;

        /// <summary>
        /// Creates a Student's t distribution with specified degrees of freedom.
        /// </summary>
        /// <param name="degreesOfFreedom">Degrees of freedom (v). Must be positive integer.</param>
        public StudentTDistribution(int degreesOfFreedom) : base()
        {
            this.degreesOfFreedom = degreesOfFreedom;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a Student's t distribution with optional non-negative clamping.
        /// Useful for simulation timing where negative durations are invalid.
        /// </summary>
        public StudentTDistribution(int degreesOfFreedom, bool clampNonNegative) : base()
        {
            this.degreesOfFreedom = degreesOfFreedom;
            this.clampNonNegative = clampNonNegative;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a Student's t distribution with a specific random engine.
        /// </summary>
        public StudentTDistribution(int degreesOfFreedom, IRandomEngine randomEngine) : base(randomEngine)
        {
            this.degreesOfFreedom = degreesOfFreedom;
            ValidateParameters();
        }

        protected override void ValidateParameters()
        {
            if (degreesOfFreedom <= 0)
                throw new ArgumentException("Degrees of freedom must be positive", nameof(degreesOfFreedom));
        }

        public int DegreesOfFreedom => degreesOfFreedom;

        public void SetDegreesOfFreedom(int value)
        {
            degreesOfFreedom = value;
            ValidateParameters();
        }

        public override double Sample()
        {
            // Use inverse transform sampling with ALGLIB
            double u = NextOpenUnit();
            double value = Probability.StudentTQuantile(u, degreesOfFreedom);
            return clampNonNegative ? Math.Max(0, value) : value;
        }

        /// <summary>
        /// Calculates the probability density function at x.
        /// </summary>
        public double PDF(double x)
        {
            double v = degreesOfFreedom;
            double numerator = Probability.Gamma((v + 1) / 2);
            double denominator = Math.Sqrt(v * Math.PI) * Probability.Gamma(v / 2);
            double factor = Math.Pow(1 + (x * x) / v, -(v + 1) / 2);
            return (numerator / denominator) * factor;
        }

        /// <summary>
        /// Calculates the cumulative distribution function at x.
        /// </summary>
        public double CDF(double x)
        {
            return Probability.StudentTCdf(x, degreesOfFreedom);
        }

        /// <summary>
        /// Gets the mean of the distribution.
        /// Mean = 0 for v > 1, undefined for v = 1.
        /// </summary>
        public double Mean => degreesOfFreedom > 1 ? 0 : double.NaN;

        /// <summary>
        /// Gets the variance of the distribution.
        /// Variance = v/(v-2) for v > 2, infinite for 1 < v <= 2, undefined for v = 1.
        /// </summary>
        public double Variance
        {
            get
            {
                if (degreesOfFreedom > 2)
                    return degreesOfFreedom / (double)(degreesOfFreedom - 2);
                else if (degreesOfFreedom > 1)
                    return double.PositiveInfinity;
                else
                    return double.NaN;
            }
        }
    }
}
