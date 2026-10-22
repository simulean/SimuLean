using System;
using System.Collections.Concurrent;

namespace SimuLean.Stochastics.Streams
{
    /// <summary>
    /// The engine of a distribution built without one: it draws from <see cref="RandomEngineManager.GlobalEngine"/>
    /// and, at the first draw, warns that the run is not reproducible (once per distribution type, through
    /// <see cref="SimLog"/>). Model samplers get their own stream instead
    /// (<see cref="SimuLean.SimContext.AssignRandomStream{T}"/>), which replaces this engine.
    /// </summary>
    public sealed class UnassignedStreamEngine : IRandomEngine
    {
        private static readonly ConcurrentDictionary<string, bool> Warned = new ConcurrentDictionary<string, bool>();

        private readonly string owner;
        private bool warned;

        /// <param name="owner">The distribution type, named in the warning.</param>
        public UnassignedStreamEngine(string owner) => this.owner = owner;

        private IRandomEngine Global
        {
            get
            {
                if (!warned)
                {
                    warned = true;
                    if (Warned.TryAdd(owner, true))
                        SimLog.Warning($"A {owner} drew from the global random engine (no stream assigned): its draws are not " +
                                       "reproducible. Give model samplers a stream with SimContext.AssignRandomStream.");
                }
                return RandomEngineManager.GlobalEngine;
            }
        }

        public double NextDouble() => Global.NextDouble();
        public int NextInt(int minValue, int maxValue) => Global.NextInt(minValue, maxValue);
        public double NextStandardNormal() => Global.NextStandardNormal();
        public void SetSeed(int seed) => RandomEngineManager.GlobalEngine.SetSeed(seed);
    }
}
