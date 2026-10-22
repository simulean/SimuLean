using System;

namespace SimuLean.Stochastics.Streams
{
    /// <summary>
    /// Global, process-wide random engine: the fallback of distributions built without an engine.
    /// Model code must not rely on it — samplers of a model get their own stream from
    /// <see cref="SimuLean.SimContext.CreateRandomStream(string, string)"/> (keyed by element), which is
    /// reproducible per model seed and safe with parallel models; this engine is shared by every
    /// thread and its draws depend on call order.
    /// </summary>
    public static class RandomEngineManager
    {
        private static IRandomEngine globalEngine = new DefaultRandomEngine();

        /// <summary>
        /// Gets the global random engine used by all distributions by default.
        /// </summary>
        public static IRandomEngine GlobalEngine => globalEngine;

        /// <summary>
        /// Sets a new global random engine. Useful for testing or using different RNG implementations.
        /// </summary>
        public static void SetGlobalEngine(IRandomEngine engine)
        {
            globalEngine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        /// <summary>
        /// Seeds the global random engine for deterministic simulations.
        /// </summary>
        public static void SetGlobalSeed(int seed)
        {
            globalEngine.SetSeed(seed);
        }

        /// <summary>
        /// Resets the global engine to a new default instance with time-based seed.
        /// </summary>
        public static void ResetGlobalEngine()
        {
            globalEngine = new DefaultRandomEngine();
        }

        /// <summary>
        /// Creates a new independent random engine with a specific seed.
        /// Useful for distributions that need their own isolated RNG.
        /// </summary>
        public static IRandomEngine CreateSeededEngine(int seed)
        {
            return new DefaultRandomEngine(seed);
        }
    }
}
