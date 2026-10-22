using System;

namespace SimuLean
{
    /// <summary>
    /// An event that runs an action: the one-line events of the engine (a retry, a deferred send, a generator's tick)
    /// without a class of their own. Keep an instance to fire the same action repeatedly without allocating; for a
    /// one-off use <see cref="SimClock.Schedule(Action, double)"/>.
    /// </summary>
    public sealed class ActionEvent : IEvent
    {
        private readonly Action action;
        public ActionEvent(Action action) { this.action = action ?? throw new ArgumentNullException(nameof(action)); }
        public void Execute() => action();
    }

    /// <summary>
    /// Marks an event that never moves items by itself — a calendar / shift tick, the next failure of an MTBF
    /// generator, a timetable stop start, the warm-up reset. While only such events remain the model cannot
    /// progress unless an element is stopped (its stop's end is a normal event): the clock raises
    /// <see cref="SimClock.Idle"/> for the run monitor (model empty / deadlock).
    /// </summary>
    public interface IBackgroundEvent : IEvent { }

    /// <summary>An <see cref="ActionEvent"/> that is a <see cref="IBackgroundEvent"/>.</summary>
    public sealed class BackgroundActionEvent : IBackgroundEvent
    {
        private readonly Action action;
        public BackgroundActionEvent(Action action) { this.action = action ?? throw new ArgumentNullException(nameof(action)); }
        public void Execute() => action();
    }
}
