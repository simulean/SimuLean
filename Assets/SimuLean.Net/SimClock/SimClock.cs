namespace SimuLean
{

    /// <summary>
    /// SimClock simulates the clock of the simulation: one clock per model and per run (a new run builds its model
    /// on a new clock). It is independent of the Unity clock. The clock owns the model's
    /// <see cref="SimContext"/> (element registry + seeded random streams); construct it
    /// with a seed for reproducible runs.
    /// </summary>
    public class SimClock
    {
        double simTime;

        DoubleMinBinaryHeap events;

        /// <summary>Events executed since the clock was created (performance measurements: events per second).</summary>
        public long EventsExecuted { get; private set; }

        /// <summary>Per-model context: element registry and seeded random streams.</summary>
        public SimContext Context { get; }

        public SimClock() : this(null)
        {
        }

        /// <summary>
        /// Creates a clock whose <see cref="SimContext"/> derives all random streams from
        /// <paramref name="seed"/>. Pass null for a distinct nondeterministic seed.
        /// </summary>
        public SimClock(int? seed)
        {
            events = new DoubleMinBinaryHeap(10);
            simTime = 0;
            Context = new SimContext(this, seed);
        }

        /// <summary>
        /// Default method to schedule an <paramref name="theEvent"/> at time (<paramref name="time"/> + simTime).
        /// Returns a handle that can cancel the event before it fires; reschedule = Cancel() + ScheduleEvent again.
        /// </summary>
        /// <param name="theEvent"></param>
        /// <param name="time"></param>
        public EventHandle ScheduleEvent(IEvent theEvent, double time)
        {
            return events.Add(simTime + time, theEvent);
        }

        /// <summary>Schedules <paramref name="action"/> after <paramref name="delay"/> (a one-off <see cref="ActionEvent"/>).
        /// Timed work of an element goes through <see cref="Element.ScheduleWork"/> instead, so downtime can pause it.</summary>
        public EventHandle Schedule(System.Action action, double delay) => ScheduleEvent(new ActionEvent(action), delay);

        /// <summary>
        /// Advances sim time until <paramref name="time"/>, executing every pending
        /// (non-cancelled) event in (time, insertion-order) sequence, then leaves the
        /// clock AT <paramref name="time"/> (so time-weighted statistics and open
        /// state intervals are measured up to the requested horizon). Returns false
        /// when no events remain.
        /// </summary>
        public bool AdvanceClock(double time)
        {
            double t;
            IEvent nextEvent;

            if (IsStopped) return false;   // the run ended (end condition): the clock stays at the stop time
            if (events.ForegroundCount() == 0) Idle?.Invoke();   // nothing can move items from the start (S2)
            if (IsStopped) return false;

            while (events.Count() > 0)
            {
                t = events.GetMinValue();
                if (t > time)
                {
                    MoveTo(time);
                    return true;
                }

                simTime = t;
                nextEvent = (IEvent)events.RetrieveFirst();
                EventsExecuted++;
                nextEvent.Execute();
                if (!IsStopped && Idle != null && events.ForegroundCount() == 0) Idle();
                if (IsStopped) return false;
            }

            MoveTo(time);
            return false;
        }

        /// <summary>
        /// Ends the run now (an end condition was met): <see cref="AdvanceClock"/> returns at once and from
        /// then on does not move the clock, so the statistics are measured up to <see cref="StopTime"/>.
        /// </summary>
        public void Stop()
        {
            if (IsStopped) return;
            IsStopped = true;
            StopTime = simTime;
        }

        /// <summary>True once <see cref="Stop"/> was called.</summary>
        public bool IsStopped { get; private set; }

        /// <summary>Simulation time at which the run was stopped (NaN while running).</summary>
        public double StopTime { get; private set; } = double.NaN;

        /// <summary>
        /// Raised when no event that can move items is left (only <see cref="IBackgroundEvent"/>s, or none): after an
        /// event, and when <see cref="AdvanceClock"/> starts. The run monitor tells an empty model from a deadlock.
        /// </summary>
        public event System.Action Idle;

        /// <summary>Pending events that can move items (not <see cref="IBackgroundEvent"/>s).</summary>
        public int ForegroundEventCount => events.ForegroundCount();

        private void MoveTo(double time)
        {
            if (time > simTime && !double.IsInfinity(time) && !double.IsNaN(time))
                simTime = time;
        }

        /// <summary>
        /// Returns current simulation time in seconds.
        /// </summary>
        /// <returns></returns>
        public double GetSimulationTime()
        {
            return simTime;
        }

        /// <summary>Calendar date of the current simulation time (see <see cref="SimContext.Calendar"/>).</summary>
        public System.DateTime GetDateTime() => Context.Calendar.ToDateTime(simTime);

        /// <summary>Number of pending (non-cancelled) events.</summary>
        public int PendingEventCount => events.Count();
    }
}
