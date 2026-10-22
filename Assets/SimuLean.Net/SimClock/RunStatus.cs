using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>Why a run ended.</summary>
    public enum RunEndReason
    {
        /// <summary>Still running, or ran to its horizon (max time).</summary>
        Horizon,
        /// <summary>The end condition's elements (<c>StopAt</c>: sinks, or any elements) received the end count.</summary>
        StopAtCount,
        /// <summary>Nothing left to do: no event can move items and no item waits (sources exhausted, model empty).</summary>
        Empty,
        /// <summary>Stopped on a deadlock (with the end condition "when empty").</summary>
        Deadlock,
        /// <summary>Stopped when items were left waiting with nothing able to happen, none blocked (end condition "when empty").</summary>
        Stalled,
        /// <summary>Nothing left to do with items still in the model: at rest in buffers without outputs (the finished
        /// product) or surplus no element will take (an unlimited source behind a full rack) — end condition "when empty".</summary>
        Finished,
    }

    /// <summary>
    /// The state of a run as the run monitor sees it (<see cref="SimContext.Run"/>): why it ended and when, and
    /// whether it <b>stalled</b> — no event can move items while items wait in elements that are not stopped (a kit that
    /// will never be complete, stock left at the end) — or <b>deadlocked</b>: stalled with an element blocked or waiting
    /// for a resource. Read by the results (<c>RunResults</c>, scope <c>model</c>).
    /// </summary>
    public sealed class RunStatus
    {
        public RunEndReason EndReason { get; internal set; } = RunEndReason.Horizon;
        /// <summary>Time the end condition stopped the run (NaN = not stopped).</summary>
        public double EndTime { get; internal set; } = double.NaN;
        /// <summary>True once the run stalled: items waiting, nothing able to happen (it stays set).</summary>
        public bool Stalled { get; internal set; }
        /// <summary>Time the run stalled (NaN = never).</summary>
        public double StallTime { get; internal set; } = double.NaN;
        /// <summary>True once a deadlock was detected: stalled with an element BLOCKED or WAITING_FOR_RESOURCE (it stays set).</summary>
        public bool Deadlocked { get; internal set; }
        /// <summary>Time of the first deadlock (NaN = none).</summary>
        public double DeadlockTime { get; internal set; } = double.NaN;
        /// <summary>The waiting elements when the run stalled and what each waits for ("Yard: BLOCKED, 2 items → BA_Bay1, BA_Bay2").</summary>
        public List<string> Waiting { get; } = new List<string>();
        /// <summary>Time the model became empty (NaN = never): every source exhausted, no item left outside sinks.</summary>
        public double EmptyTime { get; internal set; } = double.NaN;
        /// <summary>Time nothing was left to do (NaN = never): empty, or every item left at rest in a buffer without outputs
        /// or surplus no element will take. Not a stall.</summary>
        public double FinishTime { get; internal set; } = double.NaN;
    }
}
