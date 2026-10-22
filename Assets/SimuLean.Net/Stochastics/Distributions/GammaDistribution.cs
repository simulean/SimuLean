using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Gamma distribution.
    /// Generates random values following a Gamma distribution with specified shape and scale parameters.
    /// Commonly used in queuing theory, reliability analysis, and Bayesian statistics.
    /// </summary>
    public class GammaDistribution : ContinuousDistributionBase
    {
        private double shape; // alpha (k) parameter
        private double scale; // theta parameter (1/beta for rate parameterization)

        /// <summary>
        /// Creates a Gamma distribution with specified shape and scale parameters.
        /// </summary>
        /// <param name="shape">Shape parameter (alpha or k). Must be positive</param>
        /// <param name="scale">Scale parameter (theta). Must be positive. Mean = shape * scale</param>
        public GammaDistribution(double shape, double scale) : base()
        {
            this.shape = shape;
            this.scale = scale;
            ValidateParameters();
        }

        /// <summary>
        /// Creates a Gamma distribution with a specific random engine.
        /// </summary>
        public GammaDistribution(double shape, double scale, IRandomEngine randomEngine) : base(randomEngine)
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
        public double Mean => shape * scale;
        public double Variance => shape * scale * scale;

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
            // Marsaglia and Tsang's method (2000) for shape >= 1
            // For shape < 1, use Ahrens-Dieter algorithm
            
            if (shape >= 1.0)
            {
                return SampleMarsagliaTsang(shape) * scale;
            }
            else
            {
                // For alpha < 1 (Marsaglia & Tsang's boost): Gamma(alpha) = Gamma(alpha + 1) * U^(1/alpha). The draw must be
                // of Gamma(alpha + 1): with Gamma(alpha) itself the mean was alpha^2/(alpha+1) and alpha < 1/3 gave NaN
                //.
                double u = randomEngine.NextDouble();
                return SampleMarsagliaTsang(shape + 1.0) * Math.Pow(u, 1.0 / shape) * scale;
            }
        }

        /// <summary>
        /// Marsaglia and Tsang's method for Gamma(<paramref name="a"/>) where a >= 1.
        /// </summary>
        private double SampleMarsagliaTsang(double a)
        {
            double d = a - 1.0 / 3.0;
            double c = 1.0 / Math.Sqrt(9.0 * d);

            while (true)
            {
                double z = randomEngine.NextStandardNormal();
                double v = 1.0 + c * z;

                if (v <= 0)
                    continue;

                v = v * v * v;
                double u = randomEngine.NextDouble();

                // Quick accept
                if (u < 1.0 - 0.0331 * z * z * z * z)
                    return d * v;

                // Squeeze
                if (Math.Log(u) < 0.5 * z * z + d * (1.0 - v + Math.Log(v)))
                    return d * v;
            }
        }
    }
}
