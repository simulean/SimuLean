namespace SimuLean.Statistics
{
    /// <summary>
    /// Base class for all statistics collectors.
    /// Holds a reference to the owning <see cref="Element"/> and the shared
    /// <see cref="SimClock"/> so subclasses can timestamp observations.
    /// </summary>
    public abstract class StatisticsCollector
    {
        protected readonly Element  Element;
        protected readonly SimClock Clock;

        protected StatisticsCollector(Element element, SimClock clock)
        {
            Element = element;
            Clock   = clock;
        }

        /// <summary>Resets all statistics. Called when a simulation run restarts.</summary>
        public abstract void Reset();
    }
}
