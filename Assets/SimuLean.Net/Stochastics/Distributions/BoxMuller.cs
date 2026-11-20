using System;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// The Box–Muller transform: two uniforms into a normal number. Each sampler draws its own uniforms (in its own order,
    /// from its own stream) and passes them here, so the formula is written once while every caller keeps its sequence of
    /// draws. The operations keep the order of the callers' former inline formulas (<c>mean + sd · r · cos θ</c>), so the
    /// results are bit-identical.
    /// </summary>
    public static class BoxMuller
    {
        /// <summary>The radius √(−2 ln u1); <paramref name="u1"/> in (0, 1] (a draw of 1 − U, so never ln 0).</summary>
        public static double Radius(double u1) => Math.Sqrt(-2.0 * Math.Log(u1));

        /// <summary>The angle 2π·u2.</summary>
        public static double Angle(double u2) => 2.0 * Math.PI * u2;

        /// <summary>A standard normal number from <paramref name="u1"/> in (0, 1] and <paramref name="u2"/> in [0, 1).</summary>
        public static double Standard(double u1, double u2) => Radius(u1) * Math.Cos(Angle(u2));

        /// <summary>A normal number of this mean and standard deviation (evaluated as <c>mean + sd · r · cos θ</c>).</summary>
        public static double Normal(double mean, double sd, double u1, double u2) => mean + sd * Radius(u1) * Math.Cos(Angle(u2));
    }
}
