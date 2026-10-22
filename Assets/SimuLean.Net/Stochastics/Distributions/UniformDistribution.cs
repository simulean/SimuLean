using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Uniform distribution.
    /// Generates random values uniformly distributed between min and max.
    /// All values in the range [min, max) are equally likely.
    /// </summary>
    public class UniformDistribution : ContinuousDistributionBase
    {
        private double min;
        private double max;

        /// <summary>
        /// Creates a uniform distribution with specified min and max values.
        /// </summary>
        /// <param name="min">Minimum value (inclusive)</param>
        /// <param name="max">Maximum value (exclusive)</param>
        public UniformDistribution(double min, double max) : base()
        {
            this.min = min;
            this.max = max;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a uniform distribution with a specific random engine.
        /// </summary>
        public UniformDistribution(double min, double max, IRandomEngine randomEngine) : base(randomEngine)
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

        public double Min => min;
        public double Max => max;
        public double Mean => (min + max) / 2.0;
        public double Variance => Math.Pow(max - min, 2) / 12.0;

        public void SetMin(double value)
        {
            min = value;
            ValidateParameters();
        }

        public void SetMax(double value)
        {
            max = value;
            ValidateParameters();
        }

        public override double Sample()
        {
            if (min == max)
                return min;

            return min + (max - min) * randomEngine.NextDouble();
        }
    }
}
