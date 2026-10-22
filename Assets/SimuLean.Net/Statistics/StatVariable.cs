using System.Collections.Generic;

namespace SimuLean.Statistics
{
    /// <summary>
    /// Abstract base for all statistical variables: current value, number of updates, max and min. The mean belongs to
    /// the subclasses where it means something (<see cref="StatTally"/>, <see cref="StatTimeWeightedVariable"/>).
    /// </summary>
    public abstract class StatVariable
    {
        protected double _value;
        protected int    _count;
        protected double _max;
        protected double _min;

        protected StatVariable()
        {
            _value   = 0;
            _count   = 0;
            _max     = double.MinValue;
            _min     = double.MaxValue;
        }

        /// <summary>Records a new observation. Semantics depend on the concrete subclass.</summary>
        public abstract void Update(double value);

        /// <summary>Resets all fields back to their initial state.</summary>
        public virtual void Reset()
        {
            _value   = 0;
            _count   = 0;
            _max     = double.MinValue;
            _min     = double.MaxValue;
        }

        // ── individual getters ────────────────────────────────────────────
        public double Value   => _value;
        public int    Count   => _count;
        public double Max     => _count > 0 ? _max : 0;
        public double Min     => _count > 0 ? _min : 0;

        /// <summary>Returns all statistics as a dictionary.</summary>
        public Dictionary<string, double> GetStats()
        {
            return new Dictionary<string, double>
            {
                { "current", _value   },
                { "count",   _count   },
                { "max",     Max      },
                { "min",     Min      },
            };
        }
    }
}
