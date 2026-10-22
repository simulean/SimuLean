using System;

namespace SimuLean.Stochastics.Streams
{
    /// <summary>
    /// Interface for random number generation engine.
    /// Provides thread-safe, seedable random number generation for distributions.
    /// </summary>
    public interface IRandomEngine
    {
        /// <summary>
        /// Returns a random double in the range [0, 1).
        /// </summary>
        double NextDouble();

        /// <summary>
        /// Returns a random integer in the range [minValue, maxValue).
        /// </summary>
        int NextInt(int minValue, int maxValue);

        /// <summary>
        /// Sets the random seed for deterministic behavior.
        /// </summary>
        void SetSeed(int seed);

        /// <summary>
        /// Returns a random double from a standard normal distribution (mean=0, stddev=1).
        /// </summary>
        double NextStandardNormal();
    }
}
