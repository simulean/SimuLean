namespace SimuLean.Resources
{
    /// <summary>
    /// What a request does when no member that meets its requirements is free (preemption). Larger request
    /// priorities are more important; a task is taken only from a request of <b>strictly lower</b> priority, so equal
    /// priorities (the default 1 everywhere) never preempt.
    /// </summary>
    public enum PreemptMode
    {
        /// <summary>Wait for a free member (the default).</summary>
        None,
        /// <summary>Take a member from a task of strictly lower priority; the interrupted work asks for a resource again
        /// and goes on where it was (its remaining time kept).</summary>
        Resume,
        /// <summary>The same, but the interrupted work starts again (its whole time).</summary>
        Restart,
        /// <summary>The interrupted task is cancelled — only tasks without an item (a walk, a loose inspection), never a
        /// station's job or a carrier's move.</summary>
        Abort,
    }

    /// <summary>What a station's job does when an Immediate stop (a breakdown, a shift end) takes one of its resources while
    /// it works — a team option (<c>jobOnMemberStop</c>, <see cref="Resource.OnStop"/>). It applies to jobs whose request has a preemption mode
    /// (<see cref="StationResources.Preempt"/> ≠ None); other jobs keep their resources.</summary>
    public enum ResourceStopPolicy
    {
        /// <summary>The work goes back to the queue: any member that meets the requirements takes it (the default).</summary>
        HandOver,
        /// <summary>The work waits, paused, for the same member to come back.</summary>
        WaitForSame,
    }

    /// <summary>
    /// The owner of a task that can be interrupted (a station's job, a carrier's move): it says when the task may be taken
    /// and puts the work back when it is. A task without one can only be aborted (<see cref="PreemptMode.Abort"/>).
    /// </summary>
    internal interface ITaskInterruption
    {
        /// <summary>The task may be taken now, at <paramref name="step"/>, by a request with <paramref name="mode"/>.</summary>
        bool CanInterrupt(TaskSequence task, TaskStep step, PreemptMode mode);

        /// <summary>The executor's Immediate stop interrupts the task now (else it pauses it).</summary>
        bool InterruptsOnStop(TaskSequence task, TaskStep step);

        /// <summary>The task was taken from <paramref name="executor"/> (by a request with <paramref name="mode"/>, or by its
        /// stop): the owner pauses its work and asks for a resource again.</summary>
        void Interrupted(TaskSequence task, TaskExecutor executor, PreemptMode mode, bool byStop);

        /// <summary>Wait for the same member (<see cref="ResourceStopPolicy.WaitForSame"/>): its stop paused the work
        /// (<paramref name="paused"/>) or its resume let it go on.</summary>
        void Paused(TaskSequence task, TaskExecutor executor, bool paused);
    }

    /// <summary>A station whose job's process can be paused while it waits for a resource it lost: the slot shows
    /// WAITING_FOR_RESOURCE while paused, PROCESSING again once the resources are back. Stations without it keep their
    /// resources (their jobs are not preemptable).</summary>
    public interface IInterruptibleStation
    {
        /// <param name="restart">On the resume: the process starts again (its whole time).</param>
        void JobPaused(ResourceJob job, bool paused, bool restart);
    }
}
