using System;

namespace SimuLean.Stochastics.Streams
{
    /// <summary>
    /// Default implementation of IRandomEngine using System.Random.
    /// Thread-safe and supports deterministic seeding.
    /// </summary>
    public class DefaultRandomEngine : IRandomEngine
    {
        private Random random;
        private readonly object lockObject = new object();
        
        // For Box-Muller transform (standard normal generation)
        private double? spareNormal = null;

        /// <summary>
        /// Creates a new random engine with a time-based seed.
        /// </summary>
        public DefaultRandomEngine()
        {
            // Guid-derived seed: distinct even when many engines are created within
            // the same millisecond (e.g. parallel headless GA evaluations), which
            // DateTime.Now.Millisecond could not guarantee.
            random = new Random(Guid.NewGuid().GetHashCode());
        }

        /// <summary>
        /// Creates a new random engine with a specific seed for deterministic behavior.
        /// </summary>
        public DefaultRandomEngine(int seed)
        {
            random = new Random(seed);
        }

        public double NextDouble()
        {
            lock (lockObject)
            {
                return random.NextDouble();
            }
        }

        public int NextInt(int minValue, int maxValue)
        {
            lock (lockObject)
            {
                return random.Next(minValue, maxValue);
            }
        }

        public void SetSeed(int seed)
        {
            lock (lockObject)
            {
                random = new Random(seed);
                spareNormal = null; // Reset spare for Box-Muller
            }
        }

        public double NextStandardNormal()
        {
            lock (lockObject)
            {
                // Box-Muller transform for standard normal
                if (spareNormal.HasValue)
                {
                    double value = spareNormal.Value;
                    spareNormal = null;
                    return value;
                }

                double u1 = 1.0 - random.NextDouble(); // Uniform(0,1] - avoid log(0)
                double u2 = 1.0 - random.NextDouble();
                
                double magnitude = Math.Sqrt(-2.0 * Math.Log(u1));
                double z0 = magnitude * Math.Cos(2.0 * Math.PI * u2);
                double z1 = magnitude * Math.Sin(2.0 * Math.PI * u2);
                
                spareNormal = z1; // Save for next call
                return z0;
            }
        }
    }
}
