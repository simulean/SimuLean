using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Exponential distribution.
    /// Generates random values following an exponential distribution with specified rate parameter.
    /// Commonly used for modeling inter-arrival times and service times in queuing systems.
    /// </summary>
    public class ExponentialDistribution : ContinuousDistributionBase
    {
        private double rate; // Lambda parameter

        /// <summary>
        /// Creates an exponential distribution with specified rate parameter.
        /// </summary>
        /// <param name="rate">Rate parameter (lambda). Mean = 1/rate</param>
        public ExponentialDistribution(double rate) : base()
        {
            this.rate = rate;
            ValidateParameters();
        }

        /// <summary>
        /// Creates an exponential distribution with a specific random engine.
        /// </summary>
        public ExponentialDistribution(double rate, IRandomEngine randomEngine) : base(randomEngine)
        {
            this.rate = rate;
            ValidateParameters();
        }

        protected override void ValidateParameters()
        {
            if (rate <= 0)
                throw new ArgumentException("Rate must be positive", nameof(rate));
        }

        public double Rate => rate;
        public double Mean => 1.0 / rate;

        public void SetRate(double value)
        {
            rate = value;
            ValidateParameters();
        }

        /// <summary>
        /// Sets the mean (1/rate) of the distribution.
        /// </summary>
        public void SetMean(double mean)
        {
            if (mean <= 0)
                throw new ArgumentException("Mean must be positive", nameof(mean));
            rate = 1.0 / mean;
        }

        public override double Sample()
        {
            // Inverse transform sampling: F^(-1)(U) = -ln(1 - U) / rate
            double u = randomEngine.NextDouble();
            return -Math.Log(1.0 - u) / rate;
        }
    }
}
