using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Poisson distribution.
    /// Generates random integer values following a Poisson distribution.
    /// Commonly used for modeling the number of events in a fixed time interval.
    /// </summary>
    public class PoissonDistribution : DiscreteDistributionBase
    {
        private double lambda; // Mean and variance

        /// <summary>
        /// Creates a Poisson distribution with specified mean (lambda).
        /// </summary>
        /// <param name="lambda">Mean and variance of the distribution. Must be positive</param>
        public PoissonDistribution(double lambda) : base()
        {
            this.lambda = lambda;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a Poisson distribution with a specific random engine.
        /// </summary>
        public PoissonDistribution(double lambda, IRandomEngine randomEngine) : base(randomEngine)
        {
            this.lambda = lambda;
            ValidateParameters();
        }

        protected override void ValidateParameters()
        {
            if (lambda <= 0)
                throw new ArgumentException("Lambda must be positive", nameof(lambda));
        }

        public double Lambda => lambda;
        public double Mean => lambda;
        public double Variance => lambda;

        public void SetLambda(double value)
        {
            lambda = value;
            ValidateParameters();
        }

        public override int Sample()
        {
            // For small lambda, use Knuth's algorithm
            if (lambda < 30)
            {
                return SampleKnuth();
            }
            else
            {
                // Exact for large lambda too: inversion searched from the mode
                // (the normal approximation had no skew)
                return SampleInversion();
            }
        }

        /// <summary>
        /// Knuth's algorithm for Poisson sampling (efficient for small lambda).
        /// </summary>
        private int SampleKnuth()
        {
            double L = Math.Exp(-lambda);
            int k = 0;
            double p = 1.0;

            do
            {
                k++;
                p *= randomEngine.NextDouble();
            }
            while (p > L);

            return k - 1;
        }

        /// <summary>
        /// Exact inversion: the smallest k with CDF(k) ≥ u, searched from the mode (about √λ steps).
        /// </summary>
        private int SampleInversion()
        {
            double u = randomEngine.NextDouble();
            int k = (int)Math.Floor(lambda);
            double c = Probability.PoissonCdf(k, lambda), p = Probability.PoissonPmf(k, lambda);
            if (u <= c)
            {
                while (k > 0 && c - p >= u) { c -= p; p = p * k / lambda; k--; }   // P(k−1) = P(k)·k/λ
                return k;
            }
            while (u > c && p > 0) { k++; p = p * lambda / k; c += p; }             // P(k+1) = P(k)·λ/(k+1)
            return k;
        }

    }
}
