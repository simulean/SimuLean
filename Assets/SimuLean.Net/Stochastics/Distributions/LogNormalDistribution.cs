using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Log-Normal distribution.
    /// Generates random values following a log-normal distribution.
    /// A variable X is log-normally distributed if ln(X) is normally distributed.
    /// Commonly used for modeling multiplicative growth processes, stock prices, and certain physical phenomena.
    /// </summary>
    public class LogNormalDistribution : ContinuousDistributionBase
    {
        private double mu;    // Mean of underlying normal distribution (not the mean of the lognormal!)
        private double sigma; // Std dev of underlying normal distribution

        /// <summary>
        /// Creates a log-normal distribution with specified parameters.
        /// </summary>
        /// <param name="mu">Mean of the underlying normal distribution (ln scale)</param>
        /// <param name="sigma">Standard deviation of the underlying normal distribution (ln scale)</param>
        public LogNormalDistribution(double mu, double sigma) : base()
        {
            this.mu = mu;
            this.sigma = sigma;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a log-normal distribution with a specific random engine.
        /// </summary>
        public LogNormalDistribution(double mu, double sigma, IRandomEngine randomEngine) : base(randomEngine)
        {
            this.mu = mu;
            this.sigma = sigma;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a log-normal distribution from the desired mean and standard deviation
        /// of the log-normal distribution (not the underlying normal).
        /// </summary>
        public static LogNormalDistribution FromMeanAndStdDev(double mean, double stdDev)
        {
            if (mean <= 0)
                throw new ArgumentException("Mean must be positive for log-normal distribution", nameof(mean));
            if (stdDev <= 0)
                throw new ArgumentException("Standard deviation must be positive", nameof(stdDev));

            // Convert to underlying normal parameters
            double variance = stdDev * stdDev;
            double meanSquared = mean * mean;
            
            double mu = Math.Log(meanSquared / Math.Sqrt(variance + meanSquared));
            double sigma = Math.Sqrt(Math.Log(1.0 + variance / meanSquared));

            return new LogNormalDistribution(mu, sigma);
        }

        protected override void ValidateParameters()
        {
            if (sigma < 0)
                throw new ArgumentException("Sigma must be non-negative", nameof(sigma));
        }

        public double Mu => mu;
        public double Sigma => sigma;

        /// <summary>
        /// Mean of the log-normal distribution (not the underlying normal).
        /// </summary>
        public double Mean => Math.Exp(mu + sigma * sigma / 2.0);

        /// <summary>
        /// Variance of the log-normal distribution.
        /// </summary>
        public double Variance
        {
            get
            {
                double sigmaSquared = sigma * sigma;
                return (Math.Exp(sigmaSquared) - 1.0) * Math.Exp(2.0 * mu + sigmaSquared);
            }
        }

        public void SetMu(double value)
        {
            mu = value;
        }

        public void SetSigma(double value)
        {
            sigma = value;
            ValidateParameters();
        }

        public override double Sample()
        {
            if (sigma == 0)
                return Math.Exp(mu);

            // Sample from underlying normal and exponentiate
            double z = randomEngine.NextStandardNormal();
            return Math.Exp(mu + sigma * z);
        }
    }
}
