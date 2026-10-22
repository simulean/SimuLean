using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// One processing slot of a workstation (a <see cref="MultiServer"/> slot, the single assembly
    /// process of a <see cref="Combiner"/> or <see cref="PickingStation"/>): the item it holds, its
    /// delay sampler and its own state. Each slot keeps an <see cref="ElementStateTracker"/>, so time
    /// per state is known per slot (per-slot utilisation) — the same <see cref="ElementState"/>
    /// vocabulary the element statistics use (the former separate <c>State</c> enum was removed).
    /// Scheduled through <see cref="Element.ScheduleWork"/>; on completion it calls
    /// <see cref="IWorkStation.CompleteServerProcess"/>.
    /// </summary>
    public class ServerProcess : IEvent
    {
        private readonly IWorkStation myServer;
        private readonly SimClock clock;
        private readonly IRandomSampler delay;
        private readonly ElementStateTracker tracker;

        public Item theItem;

        /// <summary>Resources assigned to the current job (stations with <c>StationResources</c>); null otherwise.</summary>
        public SimuLean.Resources.ResourceJob job;

        /// <summary>Time the current job started (progress views: (now − loadTime) / lastDelay).</summary>
        public double loadTime = 0.0;

        /// <summary>Sampled duration of the current / last job.</summary>
        public double lastDelay = 0.0;

        /// <param name="index">Position of the slot in its station (0-based), for reports.</param>
        public ServerProcess(IWorkStation myServer, IRandomSampler randomDelay, SimClock clock, int index = 0)
        {
            this.myServer = myServer;
            this.clock = clock;
            delay = randomDelay;
            Index = index;
            tracker = new ElementStateTracker(ElementState.IDLE, clock.GetSimulationTime(), keepLog: false);   // totals only (#42)
        }

        /// <summary>Position of the slot in its station.</summary>
        public int Index { get; }

        public Item GetItem() => theItem;
        public void SetItem(Item item) => theItem = item;

        public double GetDelay()
        {
            // Expressions read the item being processed and the station.
            return SimuLean.Expressions.Samplers.Sample(delay, theItem, myServer as Element);
        }

        void IEvent.Execute() => myServer.CompleteServerProcess(this);

        // ── state (per slot) ─────────────────────────────────────────────────

        /// <summary>Current state of the slot.</summary>
        /// <summary>Its index in the owner's work-in-progress list (MultiServer, −1 = not in it).</summary>
        internal int WorkIndex = -1;
        /// <summary>Its index in the owner's list of loaded slots waiting for a resource (MultiServer, −1 = not in it).</summary>
        internal int WaitIndex = -1;

        // The slot's own state (what the station's logic reads), and the stop state its station shows, if any: while the
        // station's stop is effective the slot accounts the stop state, like the element (benchmark C2).
        private ElementState state = ElementState.IDLE;
        private ElementState? stopShown;

        /// <summary>The slot's own state (regardless of a stop of its station; the statistics account the stop state).</summary>
        public ElementState GetState() => state;

        /// <summary>Changes the slot state, accumulating the time spent in the previous one.</summary>
        public void SetState(ElementState newState)
        {
            state = newState;
            if (stopShown == null) tracker.Transition(newState, clock.GetSimulationTime());
        }

        /// <summary>The station's effective stop state (null = running): accounted instead of the slot's own state while
        /// set, so a slot paused by an Immediate stop is not counted PROCESSING (<see cref="Element.OnStopShown"/>).</summary>
        public void ShowStop(ElementState? stop)
        {
            stopShown = stop;
            tracker.Transition(stop ?? state, clock.GetSimulationTime());
        }

        /// <summary>Time this slot spent in <paramref name="state"/> since its last reset.</summary>
        public double GetTimeInState(ElementState state) => tracker.GetTimeInState(state, clock.GetSimulationTime());

        /// <summary>Fraction of the observed time this slot spent in <paramref name="state"/>.</summary>
        public double GetStateRatio(ElementState state) => tracker.GetStateRatio(state, clock.GetSimulationTime());

        /// <summary>State name → accumulated time for this slot.</summary>
        public Dictionary<string, double> GetStateSnapshot() => tracker.GetAllAccumulated(clock.GetSimulationTime());

        /// <summary>Warm-up: forget the time accounted so far, keeping the current state.</summary>
        public void ResetStatistics() => tracker.Reset(tracker.CurrentState, clock.GetSimulationTime());
    }
}
