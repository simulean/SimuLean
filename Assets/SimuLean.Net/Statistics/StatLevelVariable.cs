namespace SimuLean.Statistics
{
    /// <summary>
    /// Tracks a cumulative level that increases or decreases over time.
    /// Pass +1 when an item enters, -1 when it leaves.
    /// Used for: input and output counts. It has no mean: the level divided by the number of updates means nothing
    /// (for a counter it is always 1); a level over time is a <see cref="StatTimeWeightedVariable"/>.
    /// </summary>
    public class StatLevelVariable : StatVariable
    {
        /// <summary>
        /// Accumulates <paramref name="value"/> into the running level.
        /// Typically +1 (arrival) or -1 (departure).
        /// </summary>
        public override void Update(double value)
        {
            _value += value;
            _count++;

            if (_value > _max) _max = _value;
            if (_value < _min) _min = _value;
        }
    }
}
