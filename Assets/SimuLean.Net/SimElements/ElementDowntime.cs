using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>How a stop affects the work an element has in progress.</summary>
    public enum StopMode
    {
        /// <summary>Pre-empt: pending work is paused now and resumed later with its remaining time.</summary>
        Immediate,

        /// <summary>Finish what is in progress; the element refuses new input and shows the
        /// stop state once its pending work is done (typical for shift ends).</summary>
        AfterCurrent,
    }

    /// <summary>
    /// Parameters of one stop applied to an element through <see cref="Element.Stop"/>.
    /// Reusable: a downtime generator can keep one request and apply it repeatedly.
    /// <see cref="Element.Stop"/> snapshots State / Mode / BlockInput / BlockOutput into the
    /// <see cref="StopToken"/>, so changing the request while a stop is active does not affect it.
    /// </summary>
    public sealed class StopRequest
    {
        /// <summary>State shown (and accounted) while the stop is effective.</summary>
        public ElementState State = ElementState.STOPPED;

        public StopMode Mode = StopMode.Immediate;

        /// <summary>Refuse incoming items while stopped (default true).</summary>
        public bool BlockInput = true;

        /// <summary>Refuse to release items while stopped. Null = true for
        /// <see cref="StopMode.Immediate"/>, false for <see cref="StopMode.AfterCurrent"/>.</summary>
        public bool? BlockOutput;

        /// <summary>Free-text reason (e.g. "Conveyor jam").</summary>
        public string Reason;

        /// <summary>Short category code (e.g. "MECH", "E-01").</summary>
        public string Code;

        /// <summary>The generator that issued the stop (for statistics); optional.</summary>
        public object Source;

        public StopRequest() { }

        public StopRequest(ElementState state, StopMode mode = StopMode.Immediate, string code = null, string reason = null)
        {
            State = state; Mode = mode; Code = code; Reason = reason;
        }

        internal bool BlocksOutput => BlockOutput ?? Mode == StopMode.Immediate;
    }

    /// <summary>An active (or finished) stop on an element, returned by <see cref="Element.Stop"/>.</summary>
    public sealed class StopToken
    {
        public StopRequest Request { get; }
        public Element Target { get; }
        public double StartTime { get; }
        public double EndTime { get; internal set; } = double.NaN;
        public bool IsActive { get; internal set; } = true;

        // Snapshot of the request taken when the stop started: Resume and the state masking use
        // these, never the (mutable, reusable) request, so the element's block counters stay exact.
        public ElementState State { get; }
        public StopMode Mode { get; }
        public bool BlocksInput { get; }
        public bool BlocksOutput { get; }

        internal StopToken(Element target, StopRequest request, double startTime)
        {
            Target = target; Request = request; StartTime = startTime;
            State = request.State;
            Mode = request.Mode;
            BlocksInput = request.BlockInput;
            BlocksOutput = request.BlocksOutput;
        }

        public override string ToString()
            => $"Stop[{Target?.GetName()} {State.Name} {Request.Code} t={StartTime:F1}{(IsActive ? "" : $"..{EndTime:F1}")}]";
    }

    /// <summary>
    /// A unit of timed work scheduled through <see cref="Element.ScheduleWork"/>. Unlike a
    /// raw <see cref="SimClock.ScheduleEvent"/> it is paused automatically while its
    /// element is stopped (<see cref="StopMode.Immediate"/>) and resumed with its
    /// remaining time afterwards.
    /// </summary>
    public sealed class WorkHandle : IEvent
    {
        private readonly Element owner;
        private readonly IEvent work;
        private EventHandle handle;

        /// <summary>Absolute time the work completes (meaningful while not paused).</summary>
        public double DueTime { get; private set; }

        /// <summary>Remaining duration captured when the work was paused.</summary>
        public double RemainingWhenPaused { get; private set; }

        public bool IsPaused { get; private set; }
        public bool IsDone { get; private set; }
        public bool IsCancelled { get; private set; }

        /// <summary>The wrapped event.</summary>
        public IEvent Work => work;

        // Its place in the owner's pending work: the handle is the list node (removed in O(1), no node allocated per job;
        // review2 #20). InList: linked into the owner's list.
        internal WorkHandle Prev, Next;
        internal bool InList;

        internal WorkHandle(Element owner, IEvent work)
        {
            this.owner = owner; this.work = work;
        }

        internal void Start(SimClock clock, double delay, bool paused)
        {
            if (delay < 0) delay = 0;
            if (paused)
            {
                IsPaused = true;
                RemainingWhenPaused = delay;
                DueTime = double.NaN;
            }
            else
            {
                DueTime = clock.GetSimulationTime() + delay;
                handle = clock.ScheduleEvent(this, delay);
            }
        }

        internal void Pause(double now)
        {
            if (IsPaused || IsDone || IsCancelled) return;
            handle?.Cancel();
            handle = null;
            RemainingWhenPaused = Math.Max(0.0, DueTime - now);
            IsPaused = true;
        }

        internal void Resume(SimClock clock)
        {
            if (!IsPaused || IsDone || IsCancelled) return;
            IsPaused = false;
            DueTime = clock.GetSimulationTime() + RemainingWhenPaused;
            handle = clock.ScheduleEvent(this, RemainingWhenPaused);
        }

        /// <summary>Remaining duration of the work at <paramref name="now"/>.</summary>
        public double GetRemaining(double now) => IsPaused ? RemainingWhenPaused : Math.Max(0.0, DueTime - now);

        /// <summary>Cancels the work (it will never fire). Returns false if already done/cancelled.</summary>
        public bool Cancel()
        {
            if (IsDone || IsCancelled) return false;
            IsCancelled = true;
            handle?.Cancel();
            handle = null;
            owner.ForgetWork(this);
            owner.AfterWorkCompleted();   // an AfterCurrent stop may now become visible
            return true;
        }

        void IEvent.Execute()
        {
            if (IsDone || IsCancelled) return;
            IsDone = true;
            handle = null;
            owner.ForgetWork(this);
            work.Execute();
            owner.AfterWorkCompleted();
        }
    }

    /// <summary>
    /// Downtime support shared by every element (stop / resume, pausable work,
    /// state masking, item and state events). See docs/architecture.md §3.12.
    /// </summary>
    public abstract partial class Element
    {
        private List<StopToken> _stops;
        // Pending work in start order (a linked list: a finished job leaves in O(1), whatever the capacity, and the
        // pause / resume order stays the start order).
        // pending work in scheduling order (intrusive list through WorkHandle.Prev / Next)
        private WorkHandle _workFirst, _workLast;
        private int _workCount;
        private int _immediateStops;
        private int _inputBlocks;
        private int _outputBlocks;
        private bool _downShown;
        private double _pauseStartedAt;
        private ElementState _underlyingState = ElementState.IDLE;

        /// <summary>True while at least one stop is active on this element.</summary>
        public bool IsDown => _stops != null && _stops.Count > 0;

        /// <summary>True while an active stop refuses incoming items.</summary>
        public bool IsInputBlocked => _inputBlocks > 0;

        /// <summary>True while an active stop refuses to release items.</summary>
        public bool IsOutputBlocked => _outputBlocks > 0;

        /// <summary>True while an <see cref="StopMode.Immediate"/> stop holds the element's work paused.</summary>
        public bool IsWorkPaused => _immediateStops > 0;

        /// <summary>Active stops, oldest first (empty when running).</summary>
        public IReadOnlyList<StopToken> ActiveStops => (IReadOnlyList<StopToken>)_stops ?? Array.Empty<StopToken>();

        /// <summary>Number of stops started since the last reset (run start or warm-up).</summary>
        public int StopCount { get; private set; }

        /// <summary>Duration of the most recent complete pause of the element's work
        /// (used by views that interpolate progress, e.g. to shift a load time).</summary>
        public double LastPauseDuration { get; private set; }

        /// <summary>The state the element's own logic last set, regardless of any stop masking it.</summary>
        public ElementState UnderlyingState => _underlyingState;

        /// <summary>Number of pending work units (running or paused).</summary>
        public int PendingWorkCount => _workCount;

        /// <summary>Raised after a stop starts on this element.</summary>
        public event Action<Element, StopToken> Stopped;

        /// <summary>Raised after a stop ends on this element.</summary>
        public event Action<Element, StopToken> Resumed;

        /// <summary>Raised when the accounted (visible) state changes: (element, old, new).</summary>
        public event Action<Element, ElementState, ElementState> StateChanged;

        /// <summary>Raised by the link layer after an item entered this element.</summary>
        public event Action<Element, Item> ItemEntered;

        /// <summary>Raised by the link layer after an item left this element.</summary>
        public event Action<Element, Item> ItemExited;

        /// <summary>Raised when a station finishes processing an item (before it tries to send it on): Multi-Server,
        /// single-job station, Combiner, assembly station (the reactions' <c>ProcessEnded</c>).</summary>
        public event Action<Element, Item> ProcessEnded;

        // ── accepting items ───────────────────────────────────────────────────

        /// <summary>
        /// True if the element can take <paramref name="theItem"/> sent by <paramref name="origin"/>
        /// now: not input-blocked by a stop, <see cref="CheckAvailability"/> agrees and the input
        /// strategy admits it (<see cref="ValidateInput"/>: origin filters, MaxQueue, …). Output
        /// strategies use this, so a destination that would refuse the item is skipped while
        /// choosing instead of making the item wait.
        /// </summary>
        public bool CanAccept(Item theItem, Element origin = null)
            => !IsInputBlocked && CheckAvailability(theItem) && ValidateInput(theItem, origin);

        // ── stop / resume ─────────────────────────────────────────────────────

        /// <summary>
        /// Stops the element. Stops may overlap; the element runs again when every stop
        /// has been resumed. While stopped with <see cref="StopMode.Immediate"/> all work
        /// scheduled through <see cref="ScheduleWork"/> is paused and later resumed with its
        /// remaining time. Input/output blocking is enforced by the link layer.
        /// </summary>
        public StopToken Stop(StopRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            double now = simClock.GetSimulationTime();
            var token = new StopToken(this, request, now);

            if (_stops == null) _stops = new List<StopToken>(2);
            _stops.Add(token);
            StopCount++;

            if (token.BlocksInput) _inputBlocks++;
            if (token.BlocksOutput) _outputBlocks++;
            if (token.Mode == StopMode.Immediate && _immediateStops++ == 0)
                PauseAllWork(now);

            RefreshDownState();
            OnStopped(token);
            Stopped?.Invoke(this, token);
            return token;
        }

        /// <summary>Ends a stop started with <see cref="Stop"/>. Returns false if it was not active.</summary>
        public bool Resume(StopToken token)
        {
            if (token == null || !token.IsActive || _stops == null || !_stops.Remove(token))
                return false;

            token.IsActive = false;
            token.EndTime = simClock.GetSimulationTime();

            if (token.BlocksInput) _inputBlocks--;
            if (token.BlocksOutput) _outputBlocks--;
            if (token.Mode == StopMode.Immediate && --_immediateStops == 0)
                ResumeAllWork();

            RefreshDownState();
            OnResumed(token);
            Resumed?.Invoke(this, token);

            // Restart the flow that the stop held back: release the items waiting to leave,
            // then pull items waiting upstream — repeatedly, while upstream keeps handing items
            // over and this element still has room (a single notification moved only one item,
            // e.g. an N-slot MultiServer restarted one slot at a time). Negative free capacity
            // means unbounded (sinks). The cap guards against a misbehaving element.
            // Every item waiting to leave that the receivers now take, not one: their "room" notifications during the
            // stop skipped this element.
            for (int i = 0; i < MaxResumePulls && !IsOutputBlocked; i++)
                if (!Unblock()) break;
            for (int i = 0; i < MaxResumePulls && !IsInputBlocked && GetFreeCapacity() != 0; i++)
                if (NotifyInput() != true) break;
            return true;
        }

        private const int MaxResumePulls = 100000;

        /// <summary>Hook for subclasses: a stop started (after work was paused and state updated).</summary>
        protected virtual void OnStopped(StopToken token) { }

        /// <summary>Hook for subclasses: a stop ended (after work resumed, before flow restarts).</summary>
        protected virtual void OnResumed(StopToken token) { }

        /// <summary>True when <paramref name="token"/> ending has just resumed the element's paused
        /// work (an Immediate stop and no other Immediate stop still active).</summary>
        protected bool ResumedWork(StopToken token) => token.Mode == StopMode.Immediate && !IsWorkPaused;

        /// <summary>
        /// For progress views after <see cref="ResumedWork"/>: how far to shift the load time of a
        /// job loaded at <paramref name="loadTime"/> so (now − loadTime) counts only working time —
        /// the part of the last pause during which the job was loaded (now − max(loadTime, pauseStart)).
        /// </summary>
        protected double PauseShiftFor(double loadTime)
            => Math.Max(0.0, simClock.GetSimulationTime() - Math.Max(loadTime, _pauseStartedAt));

        // ── pausable work ─────────────────────────────────────────────────────

        /// <summary>
        /// Schedules <paramref name="work"/> after <paramref name="delay"/> as work of this
        /// element: it is paused while the element is stopped (Immediate mode) and resumed
        /// with its remaining time. Use it instead of <c>simClock.ScheduleEvent</c> for
        /// processing / setup / travel delays; keep <c>ScheduleEvent</c> for events that must
        /// happen at absolute times regardless of stops (arrivals, timetables).
        /// </summary>
        public WorkHandle ScheduleWork(IEvent work, double delay)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            var handle = new WorkHandle(this, work);
            handle.Prev = _workLast; handle.Next = null; handle.InList = true;
            if (_workLast != null) _workLast.Next = handle; else _workFirst = handle;
            _workLast = handle;
            _workCount++;
            handle.Start(simClock, delay, paused: IsWorkPaused);
            return handle;
        }

        // a copy of the pending work (resuming / cancelling one may change the list)
        private List<WorkHandle> PendingWork()
        {
            var all = new List<WorkHandle>(_workCount);
            for (var w = _workFirst; w != null; w = w.Next) all.Add(w);
            return all;
        }

        internal void ForgetWork(WorkHandle handle)
        {
            if (!handle.InList) return;
            if (handle.Prev != null) handle.Prev.Next = handle.Next; else _workFirst = handle.Next;
            if (handle.Next != null) handle.Next.Prev = handle.Prev; else _workLast = handle.Prev;
            handle.Prev = handle.Next = null;
            handle.InList = false;
            _workCount--;
        }

        /// <summary>
        /// Work in progress that an AfterCurrent stop waits for: the pending <see cref="ScheduleWork"/> jobs. An element
        /// whose work is not timed that way (a resource held in a process, an open-ended walk) adds its own and calls
        /// <see cref="AfterWorkCompleted"/> when it ends.
        /// </summary>
        protected virtual bool HasWorkInProgress => PendingWorkCount > 0;

        internal protected void AfterWorkCompleted()
        {
            // An AfterCurrent stop becomes visible once the element has no work left.
            if (IsDown && !_downShown) RefreshDownState();
        }

        private void PauseAllWork(double now)
        {
            _pauseStartedAt = now;
            for (var w = _workFirst; w != null; w = w.Next) w.Pause(now);
        }

        private void ResumeAllWork()
        {
            LastPauseDuration = simClock.GetSimulationTime() - _pauseStartedAt;
            foreach (var w in PendingWork()) w.Resume(simClock);
        }

        // ── state masking ─────────────────────────────────────────────────────

        /// <summary>
        /// Picks the state to account: the most recent effective stop (Immediate stops
        /// are effective at once, AfterCurrent ones when no work is pending), else the
        /// element's own state.
        /// </summary>
        private void RefreshDownState()
        {
            ElementState? shown = null;
            if (_stops != null)
            {
                bool idle = !HasWorkInProgress;
                for (int i = _stops.Count - 1; i >= 0; i--)
                {
                    var t = _stops[i];
                    if (t.Mode == StopMode.Immediate || idle) { shown = t.State; break; }
                }
            }

            if (shown.HasValue)
            {
                _downShown = true;
                ApplyTrackedState(shown.Value);
                OnStopShown(shown.Value);
            }
            else if (_downShown)
            {
                _downShown = false;
                ApplyTrackedState(_underlyingState);
                OnStopShown(null);
            }
        }

        /// <summary>Hook for stations with slots (<see cref="ServerProcess"/>): the stop state the element now accounts, or
        /// null when it runs again. They pass it to their slots (<see cref="ServerProcess.ShowStop"/>), so the per-slot
        /// statistics — and the utilisation, their PROCESSING share — leave out the time under a stop, as the element's
        /// do (benchmark C2).</summary>
        protected virtual void OnStopShown(ElementState? stop) { }

        private void ApplyTrackedState(ElementState state)
        {
            ElementState old = _stateTracker.CurrentState;
            if (old == state) return;
            _stateTracker.Transition(state, simClock.GetSimulationTime());
            StateChanged?.Invoke(this, old, state);
        }

        /// <summary>Clears stops and pending work (new run). Called from <see cref="ResetStateTracker"/>.</summary>
        private void ResetDowntime()
        {
            _stops?.Clear();   // first, so cancelling the work below does not refresh a stop state
            foreach (var w in PendingWork()) w.Cancel();
            for (var w = _workFirst; w != null; ) { var next = w.Next; w.Prev = w.Next = null; w.InList = false; w = next; }
            _workFirst = _workLast = null;
            _workCount = 0;
            _immediateStops = _inputBlocks = _outputBlocks = 0;
            _downShown = false;
            StopCount = 0;
            LastPauseDuration = 0;
            _underlyingState = ElementState.IDLE;
            _sendDepth = 0;
            _deferredUnblock?.Clear();
        }

        // ── link-layer notifications ─────────────────────────────────────────

        internal void RaiseItemEntered(Item item) => ItemEntered?.Invoke(this, item);
        internal void RaiseItemExited(Item item)  => ItemExited?.Invoke(this, item);
        protected void RaiseProcessEnded(Item item) => ProcessEnded?.Invoke(this, item);

        // ── re-entrancy guard for Unblock ─────────────────────────────────────
        // While this element is inside its own SendItem (the destination's Receive is running),
        // its pending item is still recorded as pending (lastItem / queue head / BLOCKED slot).
        // A destination that notifies upstream from inside Receive (CombinerInput, forwarding
        // queues) would re-enter Unblock and send that same item twice. Instead the Unblock is
        // deferred to a zero-delay event at the same simulation time, when the element has
        // finished updating its own state.

        private int _sendDepth;
        private DeferredUnblockEvent _deferredUnblock;

        internal void EnterSend() => _sendDepth++;
        internal void ExitSend()  { if (_sendDepth > 0) _sendDepth--; }
        /// <summary>True while this element is sending (a notification to it is deferred).</summary>
        internal bool IsSending => _sendDepth > 0;

        /// <summary>Called by the link layer to unblock this origin. Returns true if it sent
        /// something now; false if it could not (or the call was deferred, see above).</summary>
        internal bool NotifyUnblock()
        {
            if (_sendDepth == 0) return Unblock();
            if (_deferredUnblock == null) _deferredUnblock = new DeferredUnblockEvent(this);
            _deferredUnblock.Schedule(simClock);
            return false;
        }

        private sealed class DeferredUnblockEvent : IEvent
        {
            private readonly Element owner;
            private EventHandle handle;
            public DeferredUnblockEvent(Element owner) { this.owner = owner; }

            public void Schedule(SimClock clock)
            {
                if (handle == null) handle = clock.ScheduleEvent(this, 0);
            }

            public void Clear() { handle?.Cancel(); handle = null; }

            void IEvent.Execute()
            {
                handle = null;
                if (!owner.IsOutputBlocked) owner.NotifyUnblock();
            }
        }
    }
}
