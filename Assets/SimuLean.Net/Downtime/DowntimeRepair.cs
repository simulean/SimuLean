using System;
using System.Collections.Generic;
using SimuLean.Resources;

namespace SimuLean.Downtime
{
    /// <summary>
    /// The repair crew of a downtime generator: members of a resource team who repair each stop. On a stop
    /// the element shows <see cref="ElementState.WAITING_FOR_REPAIR"/> and the crew is requested (a process request of the
    /// stopped element, served while it is down — <see cref="StationResources.ServeWhileDown"/>); the members travel to
    /// the element (spatial teams: to its work point / location, as a station's operators do) and only then the repair
    /// time starts, the element showing the downtime's own state (BREAKDOWN by default) and the crew PROCESSING; at its
    /// end the crew is free and the stop ends. The stop lasts the wait for the crew + its travel + the repair.
    /// <para>The crew's use is an ordinary <see cref="StationResources"/> (team, requirements all-or-nothing, priority,
    /// preemption mode, preemptable — a repair is not preemptable by default): with a mode it takes members from work of
    /// strictly lower priority, and a preemptable repair whose member is taken pauses (the element waits for the
    /// crew again) and goes on — Resume: its remaining time, Restart: its whole time. The repair's time is work of no
    /// element (<see cref="WorkHandle.Detached"/>): the element's own stop does not pause it. Statistics: repairs done,
    /// the wait for the crew (stop start → crew there, travel included) and the repair time (crew there → end, a pause
    /// included), counted from the start or the warm-up.</para>
    /// </summary>
    public sealed class DowntimeRepair : IInterruptibleStation
    {
        /// <param name="crew">The crew's use of its team (its own <see cref="StationResources"/>, one per generator).</param>
        /// <param name="preemptable">Work of higher priority with a preemption mode may take the crew from the repair
        /// (default false: a repair is not preemptable; it sets <see cref="StationResources.Preemptable"/>).</param>
        public DowntimeRepair(StationResources crew, bool preemptable = false)
        {
            Crew = crew ?? throw new ArgumentNullException(nameof(crew));
            crew.Host = this;
            crew.ServeWhileDown = true;
            crew.Preemptable = preemptable;
        }

        /// <summary>The crew's use of its team (dispatcher, requirements, priority, preemption).</summary>
        public StationResources Crew { get; }

        /// <summary>Repairs completed (since start or warm-up).</summary>
        public int Repairs { get; private set; }

        /// <summary>Crews that arrived (repairs started), and the mean time from a stop's start to its crew's arrival.</summary>
        public int CrewsArrived => waitCount;
        public double WaitAvg => waitCount > 0 ? waitSum / waitCount : 0;

        /// <summary>Mean time from the crew's arrival to the end of the repair (repairs completed).</summary>
        public double RepairTimeAvg => Repairs > 0 ? repairSum / Repairs : 0;

        /// <summary>Repairs in progress or waiting for their crew.</summary>
        public int Active => active.Count;

        private double waitSum, repairSum, statsFrom;
        private int waitCount;
        private readonly List<Job> active = new List<Job>(1);

        /// <summary>A new run: forgets the repairs of the last one and its statistics.</summary>
        internal void Reset(double now)
        {
            CancelAll();
            ResetStatistics(now);
        }

        internal void ResetStatistics(double now)
        {
            Repairs = waitCount = 0;
            waitSum = repairSum = 0;
            statsFrom = now;
        }

        /// <summary>Cancels the repairs in progress (their time) and the crews still requested.</summary>
        internal void CancelAll()
        {
            foreach (var j in active)
            {
                j.Work?.Cancel();
                if (j.Resources != null) Crew.Cancel(j.Resources);
                j.Cancelled = true;
            }
            active.Clear();
        }

        /// <summary>Requests the crew for <paramref name="token"/> (the element already stopped, WAITING_FOR_REPAIR); once
        /// it is there the element shows <paramref name="repairState"/> for <paramref name="repairTime"/> (drawn then),
        /// then <paramref name="done"/> runs (it ends the stop).</summary>
        internal void Begin(Element target, SimClock clock, StopToken token, ElementState repairState, Func<double> repairTime, Action done)
        {
            var job = new Job
            {
                Owner = this, Target = target, Clock = clock, Token = token, RepairState = repairState,
                RepairTime = repairTime, Done = done, StopStart = clock.GetSimulationTime(),
            };
            active.Add(job);
            job.Resources = Crew.Acquire(target, null, job.CrewReady);
        }

        /// <summary>A crew member of a preemptable repair was taken: the element waits for the crew again; back, the
        /// repair goes on.</summary>
        void IInterruptibleStation.JobPaused(ResourceJob job, bool paused, bool restart)
        {
            foreach (var j in active)
                if (j.Resources == job) { j.Target.SetStopState(j.Token, paused ? ElementState.WAITING_FOR_REPAIR : j.RepairState); return; }
        }

        private sealed class Job : IEvent
        {
            public DowntimeRepair Owner; public Element Target; public SimClock Clock; public StopToken Token;
            public ElementState RepairState; public Func<double> RepairTime; public Action Done;
            public ResourceJob Resources; public WorkHandle Work; public bool Cancelled;
            public double StopStart, RepairStart;

            // Every crew member is there: the repair starts (the element shows its repair state).
            public void CrewReady()
            {
                if (Cancelled) return;
                double now = Clock.GetSimulationTime();
                Owner.waitSum += now - Math.Max(StopStart, Owner.statsFrom);
                Owner.waitCount++;
                RepairStart = now;
                Target.SetStopState(Token, RepairState);
                double t = RepairTime != null ? RepairTime() : 0;
                if (double.IsNaN(t) || t < 0) t = 0;
                Work = WorkHandle.Detached(Clock, this, t);
                Owner.Crew.ProcessStarted(Work);   // a preemptable repair: its crew can be taken while it works
            }

            // The repair's time is over: the crew is free, then the stop ends.
            public void Execute()
            {
                if (Cancelled) return;
                Owner.active.Remove(this);
                double now = Clock.GetSimulationTime();
                Owner.Repairs++;
                Owner.repairSum += now - Math.Max(RepairStart, Owner.statsFrom);
                Owner.Crew.ProcessEnded(Resources);
                Done?.Invoke();
            }
        }
    }
}
