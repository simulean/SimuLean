namespace SimuLean.Statistics
{
    /// <summary>
    /// Tracks a level over simulation time and reports its TIME-WEIGHTED average:
    /// the integral of the level over time divided by the observed span.
    /// Use for content / WIP / queue-length variables, where the average must account
    /// for how LONG the level held each value, not how many times it changed.
    ///
    /// The statistics family:
    ///  - <see cref="StatLevelVariable"/>       — event counters (input/output totals).
    ///  - <see cref="StatTally"/>               — independent observations (stay times, waits).
    ///  - <see cref="StatTimeWeightedVariable"/> — levels integrated over time (this class).
    /// </summary>
    public class StatTimeWeightedVariable : StatVariable
    {
        private double _integral;   // ∫ value·dt accumulated up to _lastTime
        private double _startTime;
        private double _lastTime;
        private bool   _started;

        /// <summary>
        /// Applies <paramref name="delta"/> (typically +1/-1) to the level at simulation
        /// time <paramref name="simTime"/>, accumulating the time-weighted integral.
        /// </summary>
        public void Update(double delta, double simTime)
        {
            if (!_started)
            {
                _startTime = simTime;
                _lastTime  = simTime;
                _started   = true;
            }
            else if (simTime > _lastTime)
            {
                _integral += _value * (simTime - _lastTime);
                _lastTime  = simTime;
            }

            _value += delta;
            _count++;

            if (_value > _max) _max = _value;
            if (_value < _min) _min = _value;
        }

        /// <summary>
        /// Time-less update: applies the delta at the last known time (no integral
        /// advance). Prefer <see cref="Update(double,double)"/> whenever sim time is
        /// available.
        /// </summary>
        public override void Update(double value) => Update(value, _lastTime);

        /// <summary>
        /// Time-weighted average as of <paramref name="now"/>, including the time spent
        /// at the current level since the last update.
        /// </summary>
        public double GetTimeWeightedAverage(double now)
        {
            if (!_started)
                return 0;

            double integral = _integral;
            if (now > _lastTime)
                integral += _value * (now - _lastTime);

            double end  = now > _lastTime ? now : _lastTime;
            double span = end - _startTime;
            return span > 0 ? integral / span : _value;
        }

        /// <summary>
        /// ∫ value·dt from the first update up to <paramref name="now"/> (read-only; lets observers such as the Unity
        /// inspector compute the average over any interval as ΔIntegral / Δt without changing the variable).
        /// </summary>
        public double GetIntegral(double now)
        {
            if (!_started) return 0;
            return now > _lastTime ? _integral + _value * (now - _lastTime) : _integral;
        }

        /// <summary>
        /// Warm-up restart: forgets the history but keeps the current level, which becomes the
        /// first observation at <paramref name="simTime"/> (peak / min = current level, average
        /// integrated from <paramref name="simTime"/> on).
        /// </summary>
        public void Restart(double simTime)
        {
            _integral  = 0;
            _startTime = simTime;
            _lastTime  = simTime;
            _started   = true;
            _count     = 1;
            _max       = _value;
            _min       = _value;
        }

        /// <summary>
        /// New run: clears everything; the averaging window then starts at the first update. A level tracked over a run
        /// uses <see cref="Reset(double)"/>, so the time before the first change counts (at level 0).
        /// </summary>
        public override void Reset()
        {
            base.Reset();
            _integral  = 0;
            _startTime = 0;
            _lastTime  = 0;
            _started   = false;
        }

        /// <summary>
        /// New run starting at <paramref name="startTime"/>: clears everything and opens the averaging window there, so
        /// the time before the first change counts at level 0 (an element whose first item arrives late is not averaged
        /// over the time since that arrival only).
        /// </summary>
        public void Reset(double startTime)
        {
            Reset();
            _startTime = startTime;
            _lastTime  = startTime;
            _started   = true;
        }
    }
}
