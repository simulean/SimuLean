using System;
using SimuLean.Stochastics.Streams;

namespace SimuLean.Stochastics.Distributions
{
    /// <summary>
    /// Abstract base class for discrete probability distributions: an integer provider and, for the elements'
    /// delays and other numeric uses, an <see cref="IRandomSampler"/> (the integer as a double) — no wrapper needed.
    /// </summary>
    public abstract class DiscreteDistributionBase : IIntegerProvider, IRandomSampler, IRandomStreamUser
    {
        protected IRandomEngine randomEngine;

        /// <summary>
        /// Creates a distribution using the global random engine.
        /// </summary>
        protected DiscreteDistributionBase()
        {
            this.randomEngine = new UnassignedStreamEngine(GetType().Name);   // warns at the first draw
        }

        /// <summary>
        /// Creates a distribution with a specific random engine.
        /// </summary>
        protected DiscreteDistributionBase(IRandomEngine randomEngine)
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
        public abstract int Sample();

        /// <summary>
        /// Validates distribution parameters. Called by constructors.
        /// </summary>
        protected abstract void ValidateParameters();

        /// <summary>
        /// Implements IntegerProvider.ProvideValue() by delegating to Sample().
        /// </summary>
        int IIntegerProvider.ProvideValue()
        {
            return Sample();
        }

        /// <summary>The next value as a sampler (an integer, as a double).</summary>
        double IRandomSampler.NextValue() => Sample();
    }
}
