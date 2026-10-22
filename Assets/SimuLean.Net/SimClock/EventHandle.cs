namespace SimuLean
{
    /// <summary>
    /// Handle returned by <see cref="SimClock.ScheduleEvent"/>. Allows a scheduled
    /// event to be cancelled before it fires (lazy deletion: the entry stays in the
    /// event heap and is skipped/purged when reached). Rescheduling = Cancel() +
    /// schedule a new event.
    /// </summary>
    public sealed class EventHandle
    {
        private readonly DoubleMinBinaryHeap owner;

        internal IEvent Event { get; }

        /// <summary>Absolute simulation time this event was scheduled for.</summary>
        public double Time { get; }

        /// <summary>True once <see cref="Cancel"/> has been called.</summary>
        public bool Canceled { get; private set; }

        /// <summary>True while the entry is still pending inside the event heap.</summary>
        internal bool InHeap;

        internal EventHandle(DoubleMinBinaryHeap owner, IEvent theEvent, double time)
        {
            this.owner = owner;
            Event      = theEvent;
            Time       = time;
            InHeap     = true;
        }

        /// <summary>
        /// Cancels the scheduled event. Returns true if the event was still pending
        /// and is now cancelled; false if it already fired, was already cancelled,
        /// </summary>
        public bool Cancel()
        {
            if (Canceled || !InHeap)
                return false;

            Canceled = true;
            owner.NotifyCanceled(this);
            return true;
        }
    }
}
