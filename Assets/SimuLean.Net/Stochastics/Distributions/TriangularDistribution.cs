using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Triangular distribution.
    /// Generates random values following a triangular distribution with min, mode (peak), and max values.
    /// Use when: Expert estimates available (most likely, min, max scenarios).
    /// </summary>
    public class TriangularDistribution : ContinuousDistributionBase
    {
        private double min;
        private double mode;
        private double max;

        /// <summary>
        /// Creates a triangular distribution with specified min, mode, and max values.
        /// </summary>
        /// <param name="min">Minimum value</param>
        /// <param name="mode">Most likely value (peak of the triangle)</param>
        /// <param name="max">Maximum value</param>
        public TriangularDistribution(double min, double mode, double max) : base()
        {
            this.min = min;
            this.mode = mode;
            this.max = max;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a triangular distribution with a specific random engine.
        /// </summary>
        public TriangularDistribution(double min, double mode, double max, IRandomEngine randomEngine) : base(randomEngine)
        {
            this.min = min;
            this.mode = mode;
            this.max = max;
            ValidateParameters();
        }

        protected override void ValidateParameters()
        {
            if (min > mode || mode > max)
                throw new ArgumentException("Must satisfy: min <= mode <= max");
        }

        public double Min => min;
        public double Mode => mode;
        public double Max => max;

        public double Mean => (min + mode + max) / 3.0;
        public double Variance => (min * min + mode * mode + max * max - min * mode - min * max - mode * max) / 18.0;

        public override double Sample()
        {
            double u = randomEngine.NextDouble();
            double fc = (max - min) > 0 ? (mode - min) / (max - min) : 0.5;

            if (u < fc)
                return min + Math.Sqrt(u * (max - min) * (mode - min));
            else
                return max - Math.Sqrt((1 - u) * (max - min) * (max - mode));
        }
    }
}
