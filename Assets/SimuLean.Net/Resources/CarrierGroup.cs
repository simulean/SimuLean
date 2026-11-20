using System;
using System.Collections.Generic;

namespace SimuLean.Resources
{
    /// <summary>
    /// A move of one load by several carriers together — a transport request for more than one resource (two forklifts,
    /// a crane and its slinger, an SPMT pair); the first carrier is the <b>lead</b> (it loads, carries and unloads), the
    /// others accompany it. Used by the element transport (<see cref="TransportMoves"/>: the transport link and the list
    /// link), the storage moves and the stacker's crane cycles.
    /// <list type="bullet">
    /// <item><b>Gathering</b>: every carrier travels, empty, to the pick-up (spatial carriers) and waits there, HELD, until
    /// the whole group is there and none of them is stopped; then the lead loads and the others accompany it
    /// (TRAVELING_LOADED) until it delivers, and end where it ends.</item>
    /// <item><b>Preemption while gathering</b> (a request of higher priority with a mode, or a member's Immediate stop when
    /// the use has a mode and its team hands work over): only that carrier is released; the others wait at the pick-up and
    /// the move asks for a replacement of that carrier (the requirement it met, with the move's priority and mode); the
    /// move starts once the group is complete again. A replacement that never comes leaves the request pending — the
    /// stall / deadlock detection reports it (the dispatcher's waiting requests, <c>simulean_diagnose</c>).</item>
    /// <item><b>Loaded together</b>: never taken by a request of higher priority; when one carrier gets an Immediate stop
    /// the whole group pauses (the lead's timed work held, <see cref="TaskExecutor.HoldForGroup"/>) until it resumes — no
    /// hand-over with the load on.</item>
    /// </list>
    /// </summary>
    internal sealed class CarrierGroup : ITaskInterruption
    {
        private readonly Element requester;
        private readonly ResourceRequest request;     // the move's request: requirements, priority, mode
        private readonly ResourceDispatcher dispatcher;
        private readonly bool preemptable;
        private readonly string pickup;               // where the carriers meet (spatial carriers)
        private readonly string leadName, helperName;
        private readonly Action<TaskSequence, TaskExecutor> addLoaded;   // the lead's steps once the group is complete
        private readonly Action<TaskExecutor> leadIs;                    // the owner learns its lead (also a replacement)
        private readonly Action interrupted;                             // the owner counts an interruption

        private TaskExecutor[] members;
        private TaskSequence[] tasks;
        private bool[] here, stopHeld;
        private int arrived, starting, stoppedHelpers;
        private bool ready, done;
        private readonly Action<Element, StopToken> onStopped, onResumed;

        internal CarrierGroup(Element requester, ResourceRequest request, ResourceDispatcher dispatcher, bool preemptable,
                              string pickup, string leadName, string helperName, Action<TaskSequence, TaskExecutor> addLoaded,
                              Action<TaskExecutor> leadIs = null, Action interrupted = null)
        {
            this.requester = requester; this.request = request; this.dispatcher = dispatcher; this.preemptable = preemptable;
            this.pickup = pickup; this.leadName = leadName; this.helperName = helperName;
            this.addLoaded = addLoaded; this.leadIs = leadIs; this.interrupted = interrupted;
            onStopped = MemberStopped;
            onResumed = MemberResumed;
        }

        /// <summary>The group is complete at the pick-up and moving the load (no member can be taken any more).</summary>
        public bool IsTogether => ready;

        /// <summary>Gives every carrier its task (the helpers first, then the lead, as a move of one carrier did).</summary>
        internal void Start(IReadOnlyList<TaskExecutor> got)
        {
            int n = got.Count;
            members = new TaskExecutor[n];
            tasks = new TaskSequence[n];
            here = new bool[n];
            stopHeld = new bool[n];
            starting = 1;   // no gathering check before every carrier has its task (a zero walk arrives inside Execute)
            for (int k = 1; k < n; k++) Begin(k, got[k]);
            Begin(0, got[0]);
            starting = 0;
            CheckReady();
        }

        private void Begin(int k, TaskExecutor ex)
        {
            members[k] = ex;
            ex.Stopped += onStopped;
            ex.Resumed += onResumed;
            var t = new TaskSequence(TaskType.Transport, requester, k == 0 ? leadName : helperName)
            {
                Priority = request.Priority, Preemptable = preemptable,
            };
            t.Interruption = this;
            if (ex.Mode == ResourceMode.Spatial && pickup != null) t.Travel(pickup);   // empty, to the pick-up
            t.Callback(new Arrival(this, k).Arrived)
             .HoldUntil(IsReady, ResourceStates.HELD);                                 // waits there for the group
            if (k == 0)
            {
                addLoaded(t, ex);
                t.Completed += Finished;
                leadIs?.Invoke(ex);
            }
            else t.Hold(ResourceStates.TRAVELING_LOADED);                             // accompanies the lead
            tasks[k] = t;
            ex.Execute(t);
        }

        private sealed class Arrival
        {
            private readonly CarrierGroup group; private readonly int slot;
            public Arrival(CarrierGroup group, int slot) { this.group = group; this.slot = slot; }
            public void Arrived() => group.Arrived(slot);
        }

        private bool IsReady() => ready;

        private void Arrived(int k)
        {
            if (here[k]) return;
            here[k] = true;
            arrived++;
            CheckReady();
        }

        // Complete at the pick-up and nobody stopped: they move off together (the lead loads, the others accompany it).
        private void CheckReady()
        {
            if (ready || done || starting > 0 || arrived < members.Length) return;
            foreach (var m in members) if (m == null || m.IsWorkPaused) return;
            ready = true;
            for (int k = 1; k < members.Length; k++) members[k].Continue();
            members[0].Continue();   // (the last to arrive is not holding yet: it goes on by itself)
        }

        private void Finished(TaskSequence lead)
        {
            done = true;
            if (stoppedHelpers > 0) { stoppedHelpers = 0; members[0].ReleaseForGroup(); }
            var leader = members[0];
            for (int k = 0; k < members.Length; k++)
            {
                var m = members[k];
                m.Stopped -= onStopped;
                m.Resumed -= onResumed;
                if (k == 0) continue;
                if (m.Mode == ResourceMode.Spatial && leader.Location != null) m.Location = leader.Location;   // they came along
                m.Continue();
            }
        }

        // ── stops ────────────────────────────────────────────────────────────

        private int SlotOf(Element e)
        {
            for (int k = 0; k < members.Length; k++) if (members[k] == e) return k;
            return -1;
        }

        // Loaded together: a helper's Immediate stop pauses the whole group (the lead's own stop pauses its work anyway).
        private void MemberStopped(Element e, StopToken token)
        {
            int k = SlotOf(e);
            if (k <= 0 || done || !ready || token.Mode != StopMode.Immediate || stopHeld[k]) return;
            stopHeld[k] = true;
            if (stoppedHelpers++ == 0) members[0].HoldForGroup();
        }

        private void MemberResumed(Element e, StopToken token)
        {
            int k = SlotOf(e);
            if (k < 0 || done) return;
            if (!ready) { CheckReady(); return; }   // gathering: the group may be complete now
            if (!stopHeld[k] || e.IsWorkPaused) return;
            stopHeld[k] = false;
            if (--stoppedHelpers == 0) members[0].ReleaseForGroup();
        }

        // ── interruption ─────────────────────────────────────────────────────

        // Only while gathering: on its way, empty, to the pick-up or waiting there. Loaded together: never.
        public bool CanInterrupt(TaskSequence task, TaskStep step, PreemptMode mode)
            => mode != PreemptMode.Abort && !ready && !done && (step.Kind == StepKind.Travel || step.Kind == StepKind.Hold);

        public bool InterruptsOnStop(TaskSequence task, TaskStep step)
            => request.Preempt != PreemptMode.None && !ready && !done && (step.Kind == StepKind.Travel || step.Kind == StepKind.Hold);

        public void Interrupted(TaskSequence task, TaskExecutor executor, PreemptMode mode, bool byStop)
        {
            int k = Array.IndexOf(tasks, task);
            if (k < 0) return;
            if (here[k]) { here[k] = false; arrived--; }
            executor.Stopped -= onStopped;
            executor.Resumed -= onResumed;
            members[k] = null;
            tasks[k] = null;
            interrupted?.Invoke();
            Ask(k);
        }

        public void Paused(TaskSequence task, TaskExecutor executor, bool paused)
        {
            if (!paused) CheckReady();   // the member waited for is back
        }

        // A replacement for slot k: the requirement it met, the move's priority and mode, measured to the pick-up.
        private void Ask(int k)
        {
            ResourceRequirement need = null;
            int n = 0;
            var reqs = request.Requirements;
            for (int i = 0; i < reqs.Count && need == null; i++)
            {
                n += reqs[i].Count;
                if (k < n) need = reqs[i];
            }
            var take = new Replacement(this, k);
            var r = new ResourceRequest(requester, TaskType.Transport, take.OnAssigned, new ResourceRequirement(1, need?.Query, need?.Label))
            {
                Priority = request.Priority, Preempt = request.Preempt, Location = pickup ?? request.Location,
            };
            dispatcher.Request(r);
        }

        private sealed class Replacement
        {
            private readonly CarrierGroup group; private readonly int slot;
            public Replacement(CarrierGroup group, int slot) { this.group = group; this.slot = slot; }
            public void OnAssigned(ResourceRequest r, IReadOnlyList<TaskExecutor> got) => group.Begin(slot, got[0]);
        }

        // ── carriers that move together all along (a milk run's train) ───────

        /// <summary>A lead and its helpers that move together for the whole task (a milk-run train with its crew): a helper's
        /// Immediate stop pauses the lead's work until it resumes; the watch ends with the lead's task.</summary>
        internal static void Together(TaskExecutor lead, IReadOnlyList<TaskExecutor> helpers, TaskSequence leadTask)
        {
            if (helpers.Count == 0) return;
            var held = new HashSet<Element>();
            Action<Element, StopToken> stopped = (e, token) =>
            {
                if (token.Mode == StopMode.Immediate && held.Add(e) && held.Count == 1) lead.HoldForGroup();
            };
            Action<Element, StopToken> resumed = (e, token) =>
            {
                if (!e.IsWorkPaused && held.Remove(e) && held.Count == 0) lead.ReleaseForGroup();
            };
            foreach (var h in helpers) { h.Stopped += stopped; h.Resumed += resumed; }
            leadTask.Completed += _ =>
            {
                foreach (var h in helpers) { h.Stopped -= stopped; h.Resumed -= resumed; }
                if (held.Count > 0) { held.Clear(); lead.ReleaseForGroup(); }
            };
        }
    }
}

namespace SimuLean.Resources
{
    /// <summary>The owner of a one-carrier move whose carrier can be taken on its way, empty, to the pick-up (its first
    /// step; a timed or an open-ended move): the move asks for a carrier again (<paramref name="again"/>). Loaded, it
    /// delivers. Used by the storage moves and the stacker's crane cycles (the element transport has its own).</summary>
    internal sealed class EmptyLegMove : ITaskInterruption
    {
        private readonly Action again;
        public EmptyLegMove(Action again) { this.again = again; }

        public bool CanInterrupt(TaskSequence task, TaskStep step, PreemptMode mode)
            => mode != PreemptMode.Abort && step.Kind == StepKind.Travel && task.CurrentStep == 0;
        public bool InterruptsOnStop(TaskSequence task, TaskStep step) => false;   // a stop pauses its travel
        public void Interrupted(TaskSequence task, TaskExecutor executor, PreemptMode mode, bool byStop) => again();
        public void Paused(TaskSequence task, TaskExecutor executor, bool paused) { }
    }
}
