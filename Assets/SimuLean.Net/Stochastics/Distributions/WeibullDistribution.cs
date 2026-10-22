using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Weibull distribution.
    /// Generates random values following a Weibull distribution with specified shape and scale parameters.
    /// Commonly used for reliability analysis and modeling failure times.
    /// </summary>
    public class WeibullDistribution : ContinuousDistributionBase
    {
        private double shape; // k parameter
        private double scale; // lambda parameter

        /// <summary>
        /// Creates a Weibull distribution with specified shape and scale parameters.
        /// </summary>
        /// <param name="shape">Shape parameter (k). k=1 gives exponential, k=2 gives Rayleigh</param>
        /// <param name="scale">Scale parameter (lambda). Related to the characteristic life</param>
        public WeibullDistribution(double shape, double scale) : base()
        {
            this.shape = shape;
            this.scale = scale;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a Weibull distribution with a specific random engine.
        /// </summary>
        public WeibullDistribution(double shape, double scale, IRandomEngine randomEngine) : base(randomEngine)
        {
            this.shape = shape;
            this.scale = scale;
            ValidateParameters();
        }

        protected override void ValidateParameters()
        {
            if (shape <= 0)
                throw new ArgumentException("Shape parameter must be positive", nameof(shape));
            if (scale <= 0)
                throw new ArgumentException("Scale parameter must be positive", nameof(scale));
        }

        public double Shape => shape;
        public double Scale => scale;

        public void SetShape(double value)
        {
            shape = value;
            ValidateParameters();
        }

        public void SetScale(double value)
        {
            scale = value;
            ValidateParameters();
        }

        public override double Sample()
        {
            // Inverse transform sampling: F^(-1)(U) = scale * (-ln(1 - U))^(1/shape)
            double u = NextOpenUnit();
            return Probability.WeibullQuantile(u, shape, scale);
        }
    }
}
