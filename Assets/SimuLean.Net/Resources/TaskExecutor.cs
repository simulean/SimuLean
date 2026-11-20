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
        private TaskSequence homeTrip;   // the walk back to the home node (ReturnHome): not a task of the statistics

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
            if (draining) { Drained(CurrentLeg.To); return true; }   // an interrupted open-ended move reached its end
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
            if (!IsAvailable) throw new InvalidOperationException($"Resource '{Name}' is not available (busy or stopped).");
            if (task.CurrentStep >= 0) throw new InvalidOperationException($"Task '{task.Name}' already ran.");
            CurrentTask = task;
            task.Executor = this;
            task.StartTime = simClock.GetSimulationTime();
            task.CurrentStep = -1;
            TaskStarted?.Invoke(this, task);
            if (draining) return;   // still on its way to the next node of an interrupted move: the task starts there
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
                        if (step.Expansion != null) task.ExpandCurrent(step.Expansion);
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
                        SetState(carried.Count > 0 ? ResourceStates.TRAVELING_LOADED : ResourceStates.TRAVELING_EMPTY);
                        double now = simClock.GetSimulationTime();
                        CurrentLeg = new TravelLeg
                        {
                            From = Location, To = to, Route = plan.Route, Path = plan.Path, Distance = plan.Distance,
                            StartTime = now, EndTime = now + plan.Duration, Carried = CarriedSnapshot(),
                        };
                        TravelStarted?.Invoke(this, CurrentLeg);
                        if (double.IsNaN(plan.Duration))
                        {
                            // open-ended: Arrive() goes on — from the model itself (traffic) or from outside (physics)
                            if (step.Duration == null && TravelModel is IDrivenTravel driven) driven.Begin(this, CurrentLeg);
                            return;
                        }
                        Wait(plan.Duration, step, to);
                        return;
                    }
                    case StepKind.Load:
                    {
                        double t = step.Duration != null ? step.Duration() : LoadTime;
                        if (t <= 0) { Loaded(step); if (CurrentTask != task) return; continue; }
                        SetState(ResourceStates.LOADING);
                        Wait(t, step, null);
                        return;
                    }
                    case StepKind.Unload:
                    {
                        double t = step.Duration != null ? step.Duration() : UnloadTime;
                        if (t <= 0) { Unloaded(step); if (CurrentTask != task) return; continue; }
                        SetState(ResourceStates.UNLOADING);
                        Wait(t, step, null);
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
                        Wait(t, step, null);
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
                throw new InvalidOperationException($"Spatial resource '{Name}' has no travel model.");
            if (Location == null)
                throw new InvalidOperationException($"Spatial resource '{Name}' has no location (set its home node).");
            return TravelModel.Plan(this, Location, node);
        }

        // The step takes `time`: its end is a StepEnd holding the task, the step and (a travel) its destination node.
        private void Wait(double time, TaskStep step, string to)
        {
            StepStartTime = simClock.GetSimulationTime();
            StepEndTime = StepStartTime + time;
            stepPausedAt = Frozen ? StepStartTime : double.NaN;
            pending = ScheduleWork(new StepEnd(this, CurrentTask, step, to), time);
        }

        // A timed step ended: its effect (a travel arrives, a load / unload takes effect), then the task's next step — only
        // if the executor still works on that task (one reset or re-dispatched meanwhile is not moved on).
        private void StepEnded(StepEnd end)
        {
            pending = null;
            StepStartTime = StepEndTime = double.NaN;
            switch (end.Step.Kind)
            {
                case StepKind.Travel: Log(CurrentLeg); CurrentLeg = null; if (end.To != null) Location = end.To; break;
                case StepKind.Load: Loaded(end.Step); break;
                case StepKind.Unload: Unloaded(end.Step); break;
            }
            if (CurrentTask == end.Task) Next();
        }

        private void Loaded(TaskStep step)
        {
            carried.Add(step.Item);
            carriedSnapshot = null;
            step.ItemAction?.Invoke(step.Item);
        }

        private void Unloaded(TaskStep step)
        {
            carried.Remove(step.Item);
            carriedSnapshot = null;
            step.ItemAction?.Invoke(step.Item);
        }

        // The load as a travel leg records it: one copy shared by the legs while the load does not change.
        private Item[] carriedSnapshot;
        private Item[] CarriedSnapshot() => carriedSnapshot ?? (carriedSnapshot = carried.ToArray());

        /// <summary>Traffic: waiting at a node for the next segment (state <see cref="ResourceStates.TRAFFIC_WAIT"/>) or moving again.</summary>
        internal void TrafficWaiting(bool waiting)
            => SetState(waiting ? ResourceStates.TRAFFIC_WAIT
                       : carried.Count > 0 ? ResourceStates.TRAVELING_LOADED : ResourceStates.TRAVELING_EMPTY);

        // A stop mid-way freezes the move's position (its timed work is paused by Element.Stop); the resume shifts it.
        protected override void OnStopped(StopToken token)
        {
            base.OnStopped(token);
            if (token.Mode == StopMode.Immediate) InterruptByStop();
            if (IsWorkPaused) Freeze();
        }

        // The move's position and the step's progress stand still (a stop, or the group of a joint move held).
        private void Freeze()
        {
            var leg = CurrentLeg;
            if (leg != null && double.IsNaN(leg.PausedAt))
                leg.PausedAt = simClock.GetSimulationTime();
            if (pending != null && double.IsNaN(stepPausedAt))
                stepPausedAt = simClock.GetSimulationTime();
        }

        // Its work runs again: the leg and the step times shift by the pause.
        private void Unfreeze()
        {
            if (Frozen) return;
            var leg = CurrentLeg;
            if (leg != null && !double.IsNaN(leg.PausedAt))
            {
                double shift = simClock.GetSimulationTime() - leg.PausedAt;
                leg.StartTime += shift;
                leg.EndTime += shift;
                leg.PausedAt = double.NaN;
            }
            if (!double.IsNaN(stepPausedAt))
            {
                double shift = simClock.GetSimulationTime() - stepPausedAt;
                StepStartTime += shift; StepEndTime += shift;
                stepPausedAt = double.NaN;
            }
        }

        // Stopped (Immediate) or held for its group: its timed work stands still.
        private bool Frozen => IsWorkPaused || groupHolds > 0;

        private int groupHolds;

        /// <summary>True while its work is held because another carrier of its joint move is stopped (several carriers
        /// moving one load: the whole group pauses while loaded); views halt it as for a stop.</summary>
        public bool IsGroupHeld => groupHolds > 0;

        /// <summary>A carrier of its joint move stopped while they are loaded: its timed work stands still — the whole
        /// group pauses — until as many <see cref="ReleaseForGroup"/>.</summary>
        internal void HoldForGroup()
        {
            if (groupHolds++ > 0) return;
            HoldAllWork();
            Freeze();
        }

        internal void ReleaseForGroup()
        {
            if (groupHolds == 0 || --groupHolds > 0) return;
            ReleaseAllWork();
            Unfreeze();
        }

        protected override void OnResumed(StopToken token)
        {
            Unfreeze();
            if (waitingForMe != null && !IsWorkPaused)
            {
                // wait for the same member: back, its job goes on
                var task = waitingForMe;
                waitingForMe = null;
                if (CurrentTask == task) task.Interruption.Paused(task, this, false);
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
            CurrentTask = null;
            if (task == homeTrip)
            {
                homeTrip = null;   // back home: free now (the walk was busy time, not a task)
                SetState(ElementState.IDLE);
                AfterWorkCompleted();
                BecomeAvailable();
                return;
            }
            RecordUsage(task.Requester?.Name, now - Math.Max(task.StartTime, StatsFrom));   // clipped at the warm-up
            if (!task.Withdrawn) TasksDone++;   // a job that gave up its wait did not complete its task
            SetState(ElementState.IDLE);
            AfterWorkCompleted();   // an AfterCurrent stop waiting for this task shows now
            task.RaiseCompleted();
            TaskFinished?.Invoke(this, task);
            if (CurrentTask != null) return;   // a Completed handler already gave it a new task
            if (ReturnHome && Mode == ResourceMode.Spatial && HomeNode != null && Location != HomeNode && IsAvailable)
            {
                homeTrip = new TaskSequence(TaskType.Other, null, "Return home").Travel(HomeNode);
                Execute(homeTrip);   // free only once back
                return;
            }
            BecomeAvailable();
        }

        public override void Start()
        {
            pending?.Cancel();
            pending = null;
            CurrentLeg = null;
            StepStartTime = StepEndTime = stepPausedAt = double.NaN;
            moves.Clear();
            carried.Clear();
            carriedSnapshot = null;
            homeTrip = null;
            draining = false;
            waitingForMe = null;
            groupHolds = 0;
            InterruptedRemaining = InterruptedDuration = double.NaN;
            Preemptions = Interruptions = 0;
            base.Start();
        }

        public override void ResetStatistics()
        {
            base.ResetStatistics();
            Preemptions = Interruptions = 0;
        }

        // ── preemption ──────────────────────────────────────────────────

        /// <summary>What a station's job does when an Immediate stop of this member interrupts it (a team option):
        /// hand over to any member (default) or wait for this one. Only jobs whose request has a preemption mode.</summary>
        public ResourceStopPolicy OnStop { get; set; } = ResourceStopPolicy.HandOver;

        /// <summary>Tasks taken from it by requests of higher priority (since start or warm-up).</summary>
        public int Preemptions { get; private set; }

        /// <summary>Tasks interrupted, by a request of higher priority or by its own Immediate stop (since start or
        /// warm-up).</summary>
        public int Interruptions { get; private set; }

        /// <summary>Raised when its task is interrupted (before the owner of the task puts the work back).</summary>
        public event Action<TaskExecutor, TaskSequence> TaskInterrupted;

        private bool draining;               // finishing an interrupted move to the next node of its route
        private TaskSequence waitingForMe;   // a job paused by its stop, waiting for it (ResourceStopPolicy.WaitForSame)

        /// <summary>Its current task can be taken now by a request of <paramref name="priority"/> with
        /// <paramref name="mode"/>: a task of strictly lower priority, preemptable, at a step that can stop (holding at a
        /// station, a timed step, an empty move that is not open-ended) and whose owner agrees; a task without an owner
        /// only for <see cref="PreemptMode.Abort"/>, and only without an item. Never while it is stopped or draining.</summary>
        internal bool CanBePreemptedFor(double priority, PreemptMode mode)
        {
            var task = CurrentTask;
            if (mode == PreemptMode.None || task == null || task == homeTrip || draining || waitingForMe != null || IsDown || IsInputBlocked
                || groupHolds > 0) return false;
            if (!task.Preemptable || !(task.Priority < priority) || task.CurrentStep < 0 || task.IsDone) return false;
            var step = task.Steps[task.CurrentStep];
            switch (step.Kind)
            {
                case StepKind.Hold: break;
                case StepKind.Travel:
                    // loaded: it delivers; empty: a timed move, or an open-ended one (it drains to its next node)
                    if (carried.Count > 0 || CurrentLeg == null || (pending == null && !CurrentLeg.IsOpenEnded)) return false;
                    break;
                case StepKind.Process: case StepKind.Setup: case StepKind.Delay:
                    if (pending == null) return false;
                    break;
                default: return false;   // loading / unloading
            }
            if (task.Interruption != null) return task.Interruption.CanInterrupt(task, step, mode);
            return mode == PreemptMode.Abort && task.Type != TaskType.Transport && carried.Count == 0 && !HasItem(task);
        }

        private static bool HasItem(TaskSequence task)
        {
            var steps = task.Steps;
            for (int i = 0; i < steps.Count; i++) if (steps[i].Item != null) return true;
            return false;
        }

        /// <summary>Takes its current task away (a request of higher priority): the task stops where it is (an empty
        /// move goes on to the next node of its route first, the network has no positions mid-track; the next task
        /// starts there), and its owner puts the work back (a station's job asks for a resource again) or, without an
        /// owner, the task is aborted. The executor is then free for the request that took it (not listed as free).</summary>
        internal void Preempt(PreemptMode mode)
        {
            Preemptions++;
            Interrupt(mode, false);
        }

        private void InterruptByStop()
        {
            var task = CurrentTask;
            if (task == null || task.Interruption == null || draining || waitingForMe != null || task.CurrentStep < 0 || task.IsDone) return;
            var step = task.Steps[task.CurrentStep];
            if (!task.Interruption.InterruptsOnStop(task, step)) return;
            Interruptions++;
            if (OnStop == ResourceStopPolicy.WaitForSame)
            {
                waitingForMe = task;
                task.Interruption.Paused(task, this, true);
                return;
            }
            Interrupt(PreemptMode.None, true);   // hand over: the job's own mode decides how it goes on
        }

        private void Interrupt(PreemptMode mode, bool byStop)
        {
            var task = CurrentTask;
            double now = simClock.GetSimulationTime();
            if (!byStop) Interruptions++;
            var step = task.Steps[task.CurrentStep];
            // what was left of a timed step, and its whole time (a resource's own time at a station goes on with another)
            InterruptedRemaining = pending != null ? pending.GetRemaining(now) : double.NaN;
            InterruptedDuration = pending != null && !double.IsNaN(StepStartTime) ? StepEndTime - StepStartTime : double.NaN;
            if (step.Kind == StepKind.Travel && CurrentLeg != null && pending != null) DrainToNextNode(now);
            else if (step.Kind == StepKind.Travel && CurrentLeg != null && CurrentLeg.IsOpenEnded) DrainOpenEnded();
            else
            {
                pending?.Cancel();
                pending = null;
                StepStartTime = StepEndTime = stepPausedAt = double.NaN;
                CurrentLeg = null;
                SetState(ElementState.IDLE);
            }
            CurrentTask = null;
            task.WasInterrupted = true;
            task.EndTime = now;
            RecordUsage(task.Requester?.Name, now - Math.Max(task.StartTime, StatsFrom));
            AfterWorkCompleted();
            TaskInterrupted?.Invoke(this, task);
            if (task.Interruption != null) task.Interruption.Interrupted(task, this, mode, byStop);
            else task.RaiseAborted();
        }

        /// <summary>When its last task was interrupted: the time left of the timed step it was on, and that step's whole
        /// time (NaN when it was not on a timed step).</summary>
        internal double InterruptedRemaining { get; private set; } = double.NaN;
        internal double InterruptedDuration { get; private set; } = double.NaN;

        /// <summary>The timed step it is on starts again with its whole time (a resource's own time at a station whose use
        /// is <see cref="PreemptMode.Restart"/>, back from a stop the station waited through for it).</summary>
        internal void RestartStep()
        {
            if (pending == null || draining || !(pending.Work is StepEnd end) || double.IsNaN(StepStartTime)) return;
            double duration = StepEndTime - StepStartTime;
            pending.Cancel();
            pending = null;
            Wait(duration, end.Step, end.To);
        }

        // An open-ended move (traffic, physics) interrupted: under traffic it ends at the next node of its route (the end
        // of the segment it is on, or at once where it waits for one); a move driven from outside (physics) ends where it
        // was going. Arrive() then ends it (Drained).
        private void DrainOpenEnded()
        {
            draining = true;
            StepEndTime = double.NaN;
            bool there = TravelModel is IDrivenTravel driven && driven.EndAtNextNode(this, CurrentLeg);
            if (CurrentLeg.To != null) Location = CurrentLeg.To;   // where its next task starts (planned from there)
            if (there) pending = ScheduleWork(new LegEnd(this, CurrentLeg.To), 0);   // waiting at a node: it is there now
        }

        // An empty move interrupted: it goes on to the next node of its route (the whole move when the route has no node
        // in between: a straight line, a matrix, an abstract travel time), and stays there for its next task.
        private void DrainToNextNode(double now)
        {
            var leg = CurrentLeg;
            pending.Cancel();
            pending = null;
            double duration = leg.EndTime - leg.StartTime;
            double done = duration > 0 ? Math.Max(0, Math.Min(1, (now - leg.StartTime) / duration)) * leg.Distance : 0;
            string node = leg.To;
            double at = leg.EndTime;
            var r = leg.Route;
            if (r != null && r.Count > 2 && leg.Distance > 0 && TravelModel != null)
            {
                double cum = 0;
                for (int i = 1; i < r.Count - 1; i++)
                {
                    cum += TravelModel.Distance(r[i - 1], r[i]);
                    if (cum < done - 1e-12) continue;
                    node = r[i];
                    at = leg.StartTime + duration * cum / leg.Distance;
                    var route = new List<string>(i + 1);
                    for (int k = 0; k <= i; k++) route.Add(r[k]);
                    CurrentLeg = new TravelLeg
                    {
                        From = leg.From, To = node, Route = route, Distance = cum,
                        StartTime = leg.StartTime, EndTime = at, Carried = leg.Carried,
                    };
                    TravelStarted?.Invoke(this, CurrentLeg);
                    break;
                }
            }
            if (node != null) Location = node;   // where its next task starts (planned from there)
            StepEndTime = at;
            draining = true;
            pending = ScheduleWork(new LegEnd(this, node), Math.Max(0, at - now));
        }

        // The interrupted move reached its node: the task given meanwhile starts here, or the executor is free.
        private void Drained(string node)
        {
            pending = null;
            draining = false;
            StepStartTime = StepEndTime = double.NaN;
            Log(CurrentLeg);
            CurrentLeg = null;
            if (node != null) Location = node;
            if (CurrentTask != null) { Next(); return; }
            SetState(ElementState.IDLE);
            AfterWorkCompleted();
            BecomeAvailable();
        }

        private sealed class LegEnd : IEvent
        {
            private readonly TaskExecutor executor; private readonly string node;
            public LegEnd(TaskExecutor executor, string node) { this.executor = executor; this.node = node; }
            public void Execute() => executor.Drained(node);
        }

        // The end of a timed step, with its state explicit (no closure): which executor, task and step, and where a travel
        // arrives — what an interruption of the step (pre-emption) will need to read.
        private sealed class StepEnd : IEvent
        {
            public readonly TaskExecutor Executor;
            public readonly TaskSequence Task;
            public readonly TaskStep Step;
            public readonly string To;
            public StepEnd(TaskExecutor executor, TaskSequence task, TaskStep step, string to)
            { Executor = executor; Task = task; Step = step; To = to; }
            public void Execute() => Executor.StepEnded(this);
        }
    }
}
