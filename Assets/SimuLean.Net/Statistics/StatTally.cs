using System;

namespace SimuLean.Statistics
{
    /// <summary>
    /// Independent observations (a tally): stay times, waiting times, values seen on push / pull. Count, min, max,
    /// mean and sample variance with Welford's algorithm (numerically stable, one pass):
    ///   mean_n = mean_(n-1) + (x - mean_(n-1)) / n,   M2_n = M2_(n-1) + (x - mean_(n-1)) (x - mean_n).
    /// </summary>
    public class StatTally : StatVariable
    {
        private double _mean;
        private double _m2;

        /// <summary>Records one observation (e.g. the stay of one item).</summary>
        public override void Update(double value)
        {
            _value = value;
            _count++;
            if (value > _max) _max = value;
            if (value < _min) _min = value;
            double delta = value - _mean;
            _mean += delta / _count;
            _m2 += delta * (value - _mean);
        }

        public override void Reset()
        {
            base.Reset();
            _mean = 0;
            _m2 = 0;
        }

        /// <summary>Mean of the observations (0 before the first one).</summary>
        public double Average => _mean;

        /// <summary>Sample variance (n − 1; 0 with fewer than two observations).</summary>
        public double Variance => _count > 1 ? _m2 / (_count - 1) : 0;

        /// <summary>Sample standard deviation.</summary>
        public double StdDev => Math.Sqrt(Variance);
    }
}
