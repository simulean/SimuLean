using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Normal (Gaussian) distribution.
    /// Generates random values following a normal distribution with specified mean and standard deviation.
    /// </summary>
    public class NormalDistribution : ContinuousDistributionBase
    {
        private double mean;
        private double stdDev;
        private readonly bool clampNonNegative;

        /// <summary>
        /// Creates a normal distribution with specified mean and standard deviation.
        /// </summary>
        /// <param name="mean">Mean (mu) of the distribution</param>
        /// <param name="stdDev">Standard deviation (sigma) of the distribution</param>
        public NormalDistribution(double mean, double stdDev) : base()
        {
            this.mean = mean;
            this.stdDev = stdDev;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a normal distribution with optional non-negative clamping.
        /// Useful for simulation timing where negative durations are invalid.
        /// </summary>
        public NormalDistribution(double mean, double stdDev, bool clampNonNegative) : base()
        {
            this.mean = mean;
            this.stdDev = stdDev;
            this.clampNonNegative = clampNonNegative;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a normal distribution with a specific random engine.
        /// </summary>
        public NormalDistribution(double mean, double stdDev, IRandomEngine randomEngine) : base(randomEngine)
        {
            this.mean = mean;
            this.stdDev = stdDev;
            ValidateParameters();
        }

        protected override void ValidateParameters()
        {
            if (stdDev < 0)
                throw new ArgumentException("Standard deviation must be non-negative", nameof(stdDev));
        }

        public double Mean => mean;
        public double StdDev => stdDev;

        public void SetMean(double value)
        {
            mean = value;
        }

        public void SetStdDev(double value)
        {
            stdDev = value;
            ValidateParameters();
        }

        public override double Sample()
        {
            if (stdDev == 0)
                return mean;

            // Inverse transform sampling via alglib's quantile function
            double u = NextOpenUnit();
            double value = Probability.NormalQuantile(u, mean, stdDev);
            return clampNonNegative ? Math.Max(0, value) : value;
        }
    }
}
