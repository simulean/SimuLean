using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Abstract base class for continuous probability distributions.
    /// </summary>
    public abstract class ContinuousDistributionBase : IRandomSampler, IRandomStreamUser
    {
        protected IRandomEngine randomEngine;

        /// <summary>
        /// Creates a distribution using the global random engine.
        /// </summary>
        protected ContinuousDistributionBase()
        {
            this.randomEngine = new UnassignedStreamEngine(GetType().Name);   // warns at the first draw
        }

        /// <summary>
        /// Creates a distribution with a specific random engine.
        /// </summary>
        protected ContinuousDistributionBase(IRandomEngine randomEngine)
        {
            this.randomEngine = randomEngine ?? throw new ArgumentNullException(nameof(randomEngine));
        }

        /// <summary>
        /// Replaces the random engine (e.g. with a per-model stream from
        /// <see cref="SimuLean.SimContext.CreateRandomStream(string, string)"/> for reproducible runs).
        /// </summary>
        public void SetRandomEngine(IRandomEngine engine)
        {
            this.randomEngine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        /// <summary>
        /// Generates the next random value from this distribution.
        /// Subclasses must implement the sampling logic.
        /// </summary>
        public abstract double Sample();

        /// <summary>
        /// Validates distribution parameters. Called by constructors.
        /// </summary>
        protected abstract void ValidateParameters();

        /// <summary>
        /// A uniform draw kept strictly inside (0, 1) - [1e-10, 1 - 1e-10] - for inverse-transform samplers whose quantile
        /// function rejects 0 and 1 (<c>NextDouble</c> can return exactly 0).
        /// </summary>
        protected double NextOpenUnit() => Math.Max(1e-10, Math.Min(1.0 - 1e-10, randomEngine.NextDouble()));

        /// <summary>
        /// Implements IRandomSampler.NextValue() by delegating to Sample().
        /// </summary>
        double IRandomSampler.NextValue()
        {
            return Sample();
        }
    }
}
