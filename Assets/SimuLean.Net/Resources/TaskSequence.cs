using System;
using System.Collections.Generic;

namespace SimuLean.Resources
{
    /// <summary>
    /// A task for a <see cref="TaskExecutor"/>: an ordered list of steps run one after the other —
    /// <see cref="Process"/>, <see cref="Setup"/>, <see cref="Delay"/>, <see cref="Travel"/>, <see cref="Load"/>,
    /// <see cref="Unload"/>, <see cref="Callback"/>, <see cref="Hold"/>. Built fluently by whoever asks for the work
    /// (a station, a transport request); durations are evaluated when their step starts, so they can read the
    /// item / state of that moment. <see cref="Completed"/> fires when the last step ends.
    /// </summary>
    public sealed class TaskSequence
    {
        private readonly List<TaskStep> steps = new List<TaskStep>();

        public TaskSequence(TaskType type, Element requester = null, string name = null)
        {
            Type = type;
            Requester = requester;
            Name = name ?? type.ToString();
        }

        public TaskType Type { get; }

        /// <summary>Its job gave up waiting (a max wait's GiveUp): its remaining steps pass, and it ends as not done
        /// — no <see cref="TaskExecutor.TasksDone"/> (its usage up to now is recorded).</summary>
        internal bool Withdrawn { get; set; }

        /// <summary>The element the task works for (usage statistics, dispatching fields); may be null.</summary>
        public Element Requester { get; }

        public string Name { get; }

        /// <summary>Priority of the work (default 1; larger = more important): a request with a preemption mode takes a
        /// member only from a task of strictly lower priority. The stations and carriers give their tasks their
        /// request's priority.</summary>
        public double Priority { get; set; } = 1;

        /// <summary>False: never interrupted by a request of higher priority. True by default; whether a task can be
        /// interrupted at all also depends on its owner (a station's job, a carrier's empty travel) and its step.</summary>
        public bool Preemptable { get; set; } = true;

        /// <summary>Raised when the task was cancelled by a request with <see cref="PreemptMode.Abort"/> (a task without an
        /// item and without an owner: its executor is free for the other work at once).</summary>
        public event Action<TaskSequence> Aborted;
        internal void RaiseAborted() => Aborted?.Invoke(this);

        /// <summary>True once the task was interrupted (taken by a request of higher priority, or by its executor's stop)
        /// — it never completes then; its owner, if any, carries the work on with another task.</summary>
        public bool WasInterrupted { get; internal set; }

        // Its owner, when the task may be interrupted and put back (a station's job, a carrier's move).
        internal ITaskInterruption Interruption;

        public IReadOnlyList<TaskStep> Steps => steps;

        /// <summary>The executor running it (null before it starts).</summary>
        public TaskExecutor Executor { get; internal set; }

        /// <summary>Index of the running step (−1 before the start, Steps.Count when done).</summary>
        public int CurrentStep { get; internal set; } = -1;

        public bool IsDone => CurrentStep >= steps.Count;

        public double StartTime { get; internal set; } = double.NaN;
        public double EndTime { get; internal set; } = double.NaN;

        /// <summary>Raised when the last step ends (the executor is free again right after).</summary>
        public event Action<TaskSequence> Completed;
        internal void RaiseCompleted() => Completed?.Invoke(this);

        // ── fluent building ──────────────────────────────────────────────────

        /// <summary>Productive work (state PROCESSING) lasting <paramref name="duration"/>.</summary>
        public TaskSequence Process(Func<double> duration) => Add(new TaskStep(StepKind.Process, duration));
        public TaskSequence Process(double duration) => Process(() => duration);

        /// <summary>Setup work (state SETUP).</summary>
        public TaskSequence Setup(Func<double> duration) => Add(new TaskStep(StepKind.Setup, duration));
        public TaskSequence Setup(double duration) => Setup(() => duration);

        /// <summary>Assigned but waiting (state WAITING) for <paramref name="duration"/>.</summary>
        public TaskSequence Delay(Func<double> duration) => Add(new TaskStep(StepKind.Delay, duration));
        public TaskSequence Delay(double duration) => Delay(() => duration);

        /// <summary>Goes to location <paramref name="node"/> (instant for abstract executors; spatial ones travel
        /// with their <see cref="TaskExecutor.TravelModel"/>).</summary>
        public TaskSequence Travel(string node) => Add(new TaskStep(StepKind.Travel, null) { Node = node });

        /// <summary>Travels for a given time (abstract transport between stations: no network, just the time);
        /// TRAVELING_LOADED / EMPTY by what it carries. <paramref name="node"/> optional (spatial location).</summary>
        public TaskSequence Travel(Func<double> duration, string node = null) => Add(new TaskStep(StepKind.Travel, duration) { Node = node });

        /// <summary>Picks up <paramref name="item"/> (state LOADING for <paramref name="duration"/>, then
        /// <paramref name="onLoaded"/> — e.g. the origin releases it).</summary>
        public TaskSequence Load(Item item, Func<double> duration = null, Action<Item> onLoaded = null)
            => Add(new TaskStep(StepKind.Load, duration) { Item = item, ItemAction = onLoaded });

        /// <summary>Drops <paramref name="item"/> (state UNLOADING, then <paramref name="onUnloaded"/>).</summary>
        public TaskSequence Unload(Item item, Func<double> duration = null, Action<Item> onUnloaded = null)
            => Add(new TaskStep(StepKind.Unload, duration) { Item = item, ItemAction = onUnloaded });

        /// <summary>Runs <paramref name="action"/> at once (no time, no state change).</summary>
        public TaskSequence Callback(Action action) => Add(new TaskStep(StepKind.Callback, null) { Action = action });

        /// <summary>Keeps the executor until <see cref="TaskExecutor.Continue"/> is called — e.g. an operator kept by
        /// a station until its item leaves. <paramref name="state"/>: what it is doing meanwhile (default HELD;
        /// PROCESSING while it works on a job whose time the station keeps).</summary>
        public TaskSequence Hold() => Add(new TaskStep(StepKind.Hold, null));
        public TaskSequence Hold(ElementState state) => Add(new TaskStep(StepKind.Hold, null) { HoldState = state });

        /// <summary>Like <see cref="Hold()"/>, but skipped when <paramref name="done"/> is already true at the step's
        /// start (e.g. a delivery that went through at once); otherwise held until <see cref="TaskExecutor.Continue"/>.</summary>
        public TaskSequence HoldUntil(Func<bool> done, ElementState? state = null)
            => Add(new TaskStep(StepKind.Hold, null) { HoldState = state, Ready = done });

        /// <summary>A step that adds steps while the task runs (milk runs): when it is reached,
        /// <paramref name="addSteps"/> is called and every step it adds (fluently, on the task it receives) is inserted
        /// right after it — e.g. "load the next ready carrier, then look again", decided at that moment. No time, no
        /// state change of its own.</summary>
        public TaskSequence Expand(Action<TaskSequence> addSteps)
            => Add(new TaskStep(StepKind.Callback, null) { Expansion = addSteps ?? throw new ArgumentNullException(nameof(addSteps)) });

        private int insertAt = -1;

        // Runs an expansion: the steps it adds go right after the running step, in the order added.
        internal void ExpandCurrent(Action<TaskSequence> addSteps)
        {
            insertAt = CurrentStep + 1;
            try { addSteps(this); }
            finally { insertAt = -1; }
        }

        private TaskSequence Add(TaskStep step)
        {
            if (insertAt >= 0) { steps.Insert(insertAt++, step); return this; }
            if (CurrentStep >= 0) throw new InvalidOperationException($"Task '{Name}' already started: no more steps.");
            steps.Add(step);
            return this;
        }

        public override string ToString() => $"{Name} ({Type}, {steps.Count} steps)";
    }

    public enum StepKind { Process, Setup, Delay, Travel, Load, Unload, Callback, Hold }

    /// <summary>One step of a <see cref="TaskSequence"/>.</summary>
    public sealed class TaskStep
    {
        internal TaskStep(StepKind kind, Func<double> duration) { Kind = kind; Duration = duration; }

        public StepKind Kind { get; }
        public Func<double> Duration { get; }
        public string Node { get; internal set; }
        public Item Item { get; internal set; }
        public Action<Item> ItemAction { get; internal set; }
        public Action Action { get; internal set; }
        public ElementState? HoldState { get; internal set; }
        public Func<bool> Ready { get; internal set; }
        /// <summary>Adds steps after this one when it is reached (<see cref="TaskSequence.Expand"/>).</summary>
        public Action<TaskSequence> Expansion { get; internal set; }

        public override string ToString() => Kind + (Node != null ? " → " + Node : "") + (Item != null ? " " + Item.GetId() : "");
    }
}
