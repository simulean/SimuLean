using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Discrete uniform distribution.
    /// Generates random integer values uniformly distributed between min and max (inclusive).
    /// All integer values in the range [min, max] are equally likely.
    /// </summary>
    public class IntegerUniformDistribution : DiscreteDistributionBase
    {
        private int min;
        private int max;

        /// <summary>
        /// Creates a discrete uniform distribution with specified min and max values.
        /// </summary>
        /// <param name="min">Minimum value (inclusive)</param>
        /// <param name="max">Maximum value (inclusive)</param>
        public IntegerUniformDistribution(int min, int max) : base()
        {
            this.min = min;
            this.max = max;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a discrete uniform distribution with a specific random engine.
        /// </summary>
        public IntegerUniformDistribution(int min, int max, IRandomEngine randomEngine) : base(randomEngine)
        {
            this.min = min;
            this.max = max;
            ValidateParameters();
        }

        protected override void ValidateParameters()
        {
            if (max < min)
                throw new ArgumentException("Maximum must be greater than or equal to minimum");
        }

        public int Min => min;
        public int Max => max;
        public double Mean => (min + max) / 2.0;
        public double Variance => Math.Pow(max - min + 1, 2) / 12.0;

        public void SetMin(int value)
        {
            min = value;
            ValidateParameters();
        }

        public void SetMax(int value)
        {
            max = value;
            ValidateParameters();
        }

        public override int Sample()
        {
            if (min == max)
                return min;

            return randomEngine.NextInt(min, max + 1);
        }
    }
}
