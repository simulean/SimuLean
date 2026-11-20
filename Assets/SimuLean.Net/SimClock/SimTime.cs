using System;

namespace SimuLean
{
    /// <summary>
    /// The tolerance of time comparisons. Model times are sums of sampled durations, so two instants that are the same
    /// in the model can differ in the last bits of a double: a comparison of times allows <see cref="Epsilon"/>.
    /// Lengths, quantities and other non-time values keep their own tolerances.
    /// </summary>
    public static class SimTime
    {
        /// <summary>Two times (or a duration and 0) closer than this are the same.</summary>
        public const double Epsilon = 1e-9;

        /// <summary>The tolerance at time <paramref name="t"/>: <see cref="Epsilon"/> relative to its size (at least
        /// <see cref="Epsilon"/>), for times far from 0 where an absolute 1e-9 is below a double's resolution.</summary>
        public static double ToleranceAt(double t) => Epsilon * Math.Max(1.0, Math.Abs(t));
    }
}
