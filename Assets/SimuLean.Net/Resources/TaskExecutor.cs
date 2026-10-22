using System;
using System.Collections.Generic;

namespace SimuLean.Resources
{
    /// <summary>
    /// A <see cref="Resource"/> that runs <see cref="TaskSequence"/>s, one at a time, step by step. Every concrete
    /// kind (operator, AGV, crane, AS/RS, robot) is a task executor with its own defaults. Timed steps go through
    /// <see cref="Element.ScheduleWork"/>, so an <c>Immediate</c> stop pauses them; an <c>AfterCurrent</c> stop
    /// lets the task finish and then takes effect. Abstract executors travel instantly; spatial ones travel with
    /// their <see cref="TravelModel"/> (asynchronous: a planned duration, or open-ended until <see cref="Arrive"/>),
    /// exposing the move as <see cref="CurrentLeg"/> for views.
    /// </summary>
    public class TaskExecutor : Resource
    {
        /// <summary>A task in progress is work an AfterCurrent stop (shift end, break) waits for, also while it holds the
        /// resource in a process or walks open-ended (no timed work then).</summary>
        protected override bool HasWorkInProgress => CurrentTask != null || base.HasWorkInProgress;

        private readonly List<Item> carried = new List<Item>();
        private WorkHandle pending;

        public TaskExecutor(string name, SimClock clock, ResourceMode mode = ResourceMode.Abstract, IVElement vElement = null)
            : base(name, clock, mode, vElement) { }

        /// <summary>How a spatial executor travels between locations (network, matrix, …). Required for spatial
        /// executors that travel to a location.</summary>
        public ITravelModel TravelModel { get; set; }

        /// <summary>The move it is making (null when not travelling) — the animation contract.</summary>
        public TravelLeg CurrentLeg { get; private set; }

        /// <summary>The step it is on (null when free) — for views: what it is loading / unloading, and when.</summary>
        public TaskStep CurrentStep
            => CurrentTask != null && CurrentTask.CurrentStep >= 0 && !CurrentTask.IsDone ? CurrentTask.Steps[CurrentTask.CurrentStep] : null;

        /// <summary>Start / end (simulation time) of the current timed step (travel, load, unload, process…); NaN
        /// for instant or holding steps. Shifted by a stop like <see cref="CurrentLeg"/>.</summary>
        public double StepStartTime { get; private set; } = double.NaN;
        public double StepEndTime { get; private set; } = double.NaN;
        private double stepPausedAt = double.NaN;

        /// <summary>Record every move in <see cref="Moves"/> (calibration). Off by default.</summary>
        public bool RecordMoves { get; set; }

        /// <summary>The moves made since <see cref="Element.Start"/> (when <see cref="RecordMoves"/>).</summary>
        public IReadOnlyList<MoveRecord> Moves => moves;
        private readonly List<MoveRecord> moves = new List<MoveRecord>();

        /// <summary>Raised when a move starts (views animate it).</summary>
        public event Action<TaskExecutor, TravelLeg> TravelStarted;

        public override double DistanceTo(string location)
        {
            if (Mode == ResourceMode.Abstract || location == null || Location == null) return 0;
            return TravelModel != null ? TravelModel.Distance(Location, location) : 0;
        }

        /// <summary>Ends an open-ended move (planned duration NaN): it is at its destination now. False if it is
        /// not making one.</summary>
        public bool Arrive()
        {
            if (CurrentLeg == null || !CurrentLeg.IsOpenEnded || pending != null) return false;
            var to = CurrentLeg.To;
            Log(CurrentLeg);
            CurrentLeg = null;
            if (to != null) Location = to;
            Next();
            return true;
        }

        /// <summary>Default load / unload times when a step does not give its own.</summary>
        public double LoadTime { get; set; }
        public double UnloadTime { get; set; }

        /// <summary>Items it carries now.</summary>
        public IReadOnlyList<Item> Carried => carried;

        /// <summary>Raised when a task starts and when it completes (after <see cref="TaskSequence.Completed"/>).</summary>
        public event Action<TaskExecutor, TaskSequence> TaskStarted, TaskFinished;

        /// <summary>Starts <paramref name="task"/> now. Throws if the executor is not available.</summary>
        public void Execute(TaskSequence task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (!IsAvailable) throw new InvalidOperationException($"Resource '{GetName()}' is not available (busy or stopped).");
            if (task.CurrentStep >= 0) throw new InvalidOperationException($"Task '{task.Name}' already ran.");
            CurrentTask = task;
            task.Executor = this;
            task.StartTime = simClock.GetSimulationTime();
            task.CurrentStep = -1;
            TaskStarted?.Invoke(this, task);
            Next();
        }

        /// <summary>Lets a task waiting in a <c>Hold</c> step go on. Returns false if it is not holding.</summary>
        public bool Continue()
        {
            var task = CurrentTask;
            if (task == null || task.IsDone || task.CurrentStep < 0 || task.Steps[task.CurrentStep].Kind != StepKind.Hold)
                return false;
            Next();
            return true;
        }

        // Runs steps until one takes time (or holds), then waits for it.
        private void Next()
        {
            var task = CurrentTask;
            while (true)
            {
                task.CurrentStep++;
                if (task.IsDone) { Finish(task); return; }
                var step = task.Steps[task.CurrentStep];
                switch (step.Kind)
                {
                    case StepKind.Callback:
                        step.Action?.Invoke();
                        if (CurrentTask != task) return;   // the callback re-dispatched / reset: stop here
                        continue;
                    case StepKind.Hold:
                        if (step.Ready != null && step.Ready()) continue;   // already done: no hold
                        SetState(step.HoldState ?? ResourceStates.HELD);
                        return;
                    case StepKind.Travel:
                    {
                        TravelPlan plan = PlanTravel(step);
                        string to = Mode == ResourceMode.Spatial ? step.Node : null;
                        if (plan.Duration <= 0) { if (to != null) Location = to; continue; }
                        SetState(carried.Count > 0 ? ResourceStates.TRAVELLING_LOADED : ResourceStates.TRAVELLING_EMPTY);
                        double now = simClock.GetSimulationTime();
                        CurrentLeg = new TravelLeg
                        {
                            From = Location, To = to, Route = plan.Route, Path = plan.Path, Distance = plan.Distance,
                            StartTime = now, EndTime = now + plan.Duration, Carried = carried.ToArray(),
                        };
                        TravelStarted?.Invoke(this, CurrentLeg);
                        if (double.IsNaN(plan.Duration))
                        {
                            // open-ended: Arrive() goes on — from the model itself (traffic) or from outside (physics)
                            if (step.Duration == null && TravelModel is IDrivenTravel driven) driven.Begin(this, CurrentLeg);
                            return;
                        }
                        Wait(plan.Duration, () => { Log(CurrentLeg); CurrentLeg = null; if (to != null) Location = to; });
                        return;
                    }
                    case StepKind.Load:
                    {
                        double t = step.Duration != null ? step.Duration() : LoadTime;
                        Action done = () => { carried.Add(step.Item); step.ItemAction?.Invoke(step.Item); };
                        if (t <= 0) { done(); if (CurrentTask != task) return; continue; }
                        SetState(ResourceStates.LOADING);
                        Wait(t, done);
                        return;
                    }
                    case StepKind.Unload:
                    {
                        double t = step.Duration != null ? step.Duration() : UnloadTime;
                        Action done = () => { carried.Remove(step.Item); step.ItemAction?.Invoke(step.Item); };
                        if (t <= 0) { done(); if (CurrentTask != task) return; continue; }
                        SetState(ResourceStates.UNLOADING);
                        Wait(t, done);
                        return;
                    }
                    default:
                    {
                        double t = step.Duration != null ? step.Duration() : 0;
                        if (double.IsNaN(t) || t < 0) t = 0;
                        if (t == 0) continue;
                        SetState(step.Kind == StepKind.Process ? ElementState.PROCESSING
                               : step.Kind == StepKind.Setup ? ElementState.SETUP
                               : ResourceStates.WAITING);
                        Wait(t, null);
                        return;
                    }
                }
            }
        }

        // A defined time (abstract transport) wins; else the travel model to the step's location.
        private TravelPlan PlanTravel(TaskStep step)
        {
            if (step.Duration != null)
                return new TravelPlan { Duration = Math.Max(0, step.Duration()) };
            string node = step.Node;
            if (Mode == ResourceMode.Abstract || string.IsNullOrEmpty(node) || node == Location) return new TravelPlan();
            if (TravelModel == null)
                throw new InvalidOperationException($"Spatial resource '{GetName()}' has no travel model.");
            if (Location == null)
                throw new InvalidOperationException($"Spatial resource '{GetName()}' has no location (set its home node).");
            return TravelModel.Plan(this, Location, node);
        }

        private void Wait(double time, Action atEnd)
        {
            var task = CurrentTask;
            StepStartTime = simClock.GetSimulationTime();
            StepEndTime = StepStartTime + time;
            stepPausedAt = IsWorkPaused ? StepStartTime : double.NaN;
            pending = ScheduleWork(new StepEnd(() =>
            {
                pending = null;
                StepStartTime = StepEndTime = double.NaN;
                atEnd?.Invoke();
                if (CurrentTask == task) Next();
            }), time);
        }

        /// <summary>Traffic: waiting at a node for the next segment (state <see cref="ResourceStates.TRAFFIC_WAIT"/>) or moving again.</summary>
        internal void TrafficWaiting(bool waiting)
            => SetState(waiting ? ResourceStates.TRAFFIC_WAIT
                       : carried.Count > 0 ? ResourceStates.TRAVELLING_LOADED : ResourceStates.TRAVELLING_EMPTY);

        // A stop mid-way freezes the move's position (its timed work is paused by Element.Stop); the resume shifts it.
        protected override void OnStopped(StopToken token)
        {
            base.OnStopped(token);
            var leg = CurrentLeg;
            if (leg != null && IsWorkPaused && double.IsNaN(leg.PausedAt))
                leg.PausedAt = simClock.GetSimulationTime();
            if (pending != null && IsWorkPaused && double.IsNaN(stepPausedAt))
                stepPausedAt = simClock.GetSimulationTime();
        }

        protected override void OnResumed(StopToken token)
        {
            var leg = CurrentLeg;
            if (leg != null && !double.IsNaN(leg.PausedAt) && !IsWorkPaused)
            {
                double shift = simClock.GetSimulationTime() - leg.PausedAt;
                leg.StartTime += shift;
                leg.EndTime += shift;
                leg.PausedAt = double.NaN;
            }
            if (!double.IsNaN(stepPausedAt) && !IsWorkPaused)
            {
                double shift = simClock.GetSimulationTime() - stepPausedAt;
                StepStartTime += shift; StepEndTime += shift;
                stepPausedAt = double.NaN;
            }
            base.OnResumed(token);
        }

        private void Log(TravelLeg leg)
        {
            if (!RecordMoves || leg == null) return;
            moves.Add(new MoveRecord
            {
                From = leg.From, To = leg.To, Loaded = leg.Carried != null && leg.Carried.Count > 0,
                Start = leg.StartTime, End = simClock.GetSimulationTime(), Distance = leg.Distance,
            });
        }

        private void Finish(TaskSequence task)
        {
            double now = simClock.GetSimulationTime();
            task.EndTime = now;
            RecordUsage(task.Requester?.GetName(), now - Math.Max(task.StartTime, StatsFrom));   // clipped at the warm-up
            TasksDone++;
            CurrentTask = null;
            SetState(ElementState.IDLE);
            AfterWorkCompleted();   // an AfterCurrent stop waiting for this task shows now
            task.RaiseCompleted();
            TaskFinished?.Invoke(this, task);
            if (CurrentTask == null) BecomeAvailable();   // a Completed handler may already have given it a new task
        }

        public override void Start()
        {
            pending?.Cancel();
            pending = null;
            CurrentLeg = null;
            StepStartTime = StepEndTime = stepPausedAt = double.NaN;
            moves.Clear();
            carried.Clear();
            base.Start();
        }

        private sealed class StepEnd : IEvent
        {
            private readonly Action action;
            public StepEnd(Action action) { this.action = action; }
            public void Execute() => action();
        }
    }
}
