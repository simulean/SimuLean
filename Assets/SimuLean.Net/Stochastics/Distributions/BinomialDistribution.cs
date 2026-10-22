using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Binomial distribution.
    /// Generates random integer values following a Binomial distribution.
    /// Models the number of successes in n independent Bernoulli trials with probability p.
    /// </summary>
    public class BinomialDistribution : DiscreteDistributionBase
    {
        private int n;      // Number of trials
        private double p;   // Probability of success

        /// <summary>
        /// Creates a Binomial distribution with specified parameters.
        /// </summary>
        /// <param name="n">Number of trials. Must be non-negative</param>
        /// <param name="p">Probability of success in each trial. Must be in [0, 1]</param>
        public BinomialDistribution(int n, double p) : base()
        {
            this.n = n;
            this.p = p;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a Binomial distribution with a specific random engine.
        /// </summary>
        public BinomialDistribution(int n, double p, IRandomEngine randomEngine) : base(randomEngine)
        {
            this.n = n;
            this.p = p;
            ValidateParameters();
        }

        protected override void ValidateParameters()
        {
            if (n < 0)
                throw new ArgumentException("Number of trials must be non-negative", nameof(n));
            if (p < 0 || p > 1)
                throw new ArgumentException("Probability must be in [0, 1]", nameof(p));
        }

        public int N => n;
        public double P => p;
        public double Mean => n * p;
        public double Variance => n * p * (1.0 - p);

        public void SetN(int value)
        {
            n = value;
            ValidateParameters();
        }

        public void SetP(double value)
        {
            p = value;
            ValidateParameters();
        }

        public override int Sample()
        {
            // Edge cases
            if (n == 0 || p == 0)
                return 0;
            if (p == 1)
                return n;

            // For large n and moderate p, use normal approximation
            if (n > 50 && n * p > 5 && n * (1 - p) > 5)
            {
                // exact for large n too (the normal approximation had no skew)
                return SampleInversion();
            }
            else
            {
                // Direct method: sum of n Bernoulli trials
                return SampleDirect();
            }
        }

        /// <summary>
        /// Direct method: sum n independent Bernoulli trials.
        /// </summary>
        private int SampleDirect()
        {
            int successes = 0;
            for (int i = 0; i < n; i++)
            {
                if (randomEngine.NextDouble() < p)
                    successes++;
            }
            return successes;
        }

        /// <summary>
        /// Exact inversion: the smallest k with CDF(k) ≥ u, searched from the mode (about √(np(1−p)) steps).
        /// </summary>
        private int SampleInversion()
        {
            double u = randomEngine.NextDouble();
            int k = Math.Min(n, (int)Math.Floor((n + 1) * p));
            double c = Probability.BinomialCdf(k, n, p), pk = Probability.BinomialPmf(k, n, p);
            double r = p / (1 - p);
            if (u <= c)
            {
                while (k > 0 && c - pk >= u) { c -= pk; pk = pk * k / ((n - k + 1) * r); k--; }   // P(k−1)
                return k;
            }
            while (u > c && k < n && pk > 0) { pk = pk * (n - k) * r / (k + 1); k++; c += pk; }   // P(k+1)
            return k;
        }

    }
}
