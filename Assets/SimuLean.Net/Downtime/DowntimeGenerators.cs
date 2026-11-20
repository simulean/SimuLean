using System;
using System.Collections.Generic;

namespace SimuLean.Downtime
{
    /// <summary>One stop interval in simulation time.</summary>
    public struct DowntimeInterval
    {
        public double Start;
        public double Duration;
        public ElementState State;
        /// <summary>Stop mode of this interval; null = the generator's <see cref="DowntimeGenerator.Mode"/>
        /// (as <see cref="State"/> falls back to the generator's state when none).</summary>
        public StopMode? Mode;
        public string Code;
        public string Reason;

        public double End => Start + Duration;

        public DowntimeInterval(double start, double duration, ElementState state,
                                StopMode? mode = null, string code = null, string reason = null)
        {
            Start = start; Duration = duration; State = state; Mode = mode; Code = code; Reason = reason;
        }

        public override string ToString() => $"[{Start:F0}+{Duration:F0} {State.Name} {Code}]";
    }

    /// <summary>What to do with intervals of one generator that overlap each other.</summary>
    public enum OverlapPolicy
    {
        /// <summary>Apply as given; overlapping stops stack on the element.</summary>
        Allow,
        /// <summary>Queue them: an interval starts when the previous one ends (duration kept).</summary>
        Serialize,
        /// <summary>Merge overlapping intervals into one (state/code of the first).</summary>
        Merge,
    }

    /// <summary>Which clock drives the time to failure of an MTBF/MTTR generator.</summary>
    public enum DowntimeBasis
    {
        /// <summary>Time to failure runs on calendar (simulation) time, from the end of the last repair.</summary>
        CalendarTime,
        /// <summary>Time to failure only runs while the element is in one of the busy states.</summary>
        BusyTime,
    }

    /// <summary>
    /// Base class of the reusable downtime generators. A generator drives
    /// <see cref="Element.Stop"/>/<see cref="Element.Resume"/> on one target element,
    /// so it works with any element; what "stopped" means for the element (pause its
    /// work, refuse items) is defined by <see cref="Element"/> and its
    /// <see cref="Element.ScheduleWork"/> usage. Engine-pure; build with the model, call
    /// <see cref="Start"/> after the elements' own <c>Start()</c>.
    /// </summary>
    public abstract class DowntimeGenerator : IWarmupStatistics
    {
        public string Name { get; set; }
        public Element Target { get; }
        protected readonly SimClock Clock;

        /// <summary>State applied while stopped.</summary>
        public ElementState State { get; set; } = ElementState.BREAKDOWN;
        public StopMode Mode { get; set; } = StopMode.Immediate;
        public bool BlockInput { get; set; } = true;
        /// <summary>Null = default of the mode (see <see cref="StopRequest.BlockOutput"/>).</summary>
        public bool? BlockOutput { get; set; }

        /// <summary>Stops started by this generator since <see cref="Start"/>.</summary>
        public int StopCount { get; private set; }

        /// <summary>The time the target actually stood for this generator's stops (<c>downtime_total</c>), from
        /// <see cref="Start"/> or the warm-up up to now: the time some stop of this generator was in effect — each from the
        /// moment it took effect (<see cref="StopToken.EffectiveStart"/>: an AfterCurrent stop once the work in hand is
        /// done; one that never took effect counts nothing) to its end, a stop still running up to now, overlapping stops
        /// once. It matches the target's time in the stop's state when nothing else stops it.</summary>
        public double TotalDowntime
            => endedDowntime + (effectiveOpen > 0 ? Math.Max(0, Now - Math.Max(effectiveSince, statsFrom)) : 0);

        // the stops of this generator in effect now, and since when one has been (the union of their times)
        private int effectiveOpen;
        private double effectiveSince;

        /// <summary>The stop time as scheduled (<c>downtime_scheduled</c>), from <see cref="Start"/> or the warm-up up to
        /// now: each stop from the time it was due for its planned length (a timetable row's or periodic stop's duration,
        /// a failure's time to repair; with a repair crew the wait for the crew and the repair; a shift's off-shift
        /// window), whatever the work in hand delayed or cut; overlapping stops each count. Equal to
        /// <see cref="TotalDowntime"/> for Immediate stops that do not overlap.</summary>
        public double ScheduledDowntime
        {
            get
            {
                double t = endedScheduled;
                foreach (var r in open) if (r.Token.IsActive) t += Scheduled(r, Now);
                return t;
            }
        }

        private double endedDowntime, endedScheduled;
        // stops started and not ended by this generator, with their due time and planned length
        private readonly List<OpenStop> open = new List<OpenStop>(1);
        private double statsFrom;

        private sealed class OpenStop
        {
            public StopToken Token;
            public double Due;        // when the stop was due (its start, or the time it was queued)
            public double Planned;    // its planned length (NaN: until its end, measured from when it took effect)
            public bool Delayable;    // its length is timed from when it takes effect (false: a calendar stop, ended by the calendar)
            public bool Ending;       // being ended: the stops queued behind it start
            public bool Effective;    // took effect (counted in effectiveOpen)
        }

        private double Scheduled(OpenStop r, double now)
        {
            StopToken token = r.Token;
            double end = token.IsActive ? now : token.EndTime;
            double length = !r.Delayable ? end - r.Due
                          : !double.IsNaN(r.Planned) ? r.Planned
                          : token.IsEffective ? end - token.EffectiveStart : 0;
            double to = Math.Min(r.Due + Math.Max(0, length), now);
            return Math.Max(0, to - Math.Max(r.Due, statsFrom));
        }

        /// <summary>Raised after this generator starts / ends a stop.</summary>
        public event Action<DowntimeGenerator, StopToken> StopStarted, StopEnded;

        protected DowntimeGenerator(Element target, SimClock clock)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Clock  = clock ?? target.GetSimClock();
            Name   = GetType().Name + ":" + target.Name;
        }

        /// <summary>Resets counters and schedules the first stop. Call once per run.</summary>
        public void Start()
        {
            CancelPending();
            Repair?.Reset(Now);
            StopCount = 0;
            endedDowntime = endedScheduled = 0;
            effectiveOpen = 0;
            open.Clear();
            queued.Clear();
            statsFrom = Now;
            OnStart();
        }

        /// <summary>Warm-up: forgets the stops counted so far; one still running counts from now on.</summary>
        public void ResetStatistics()
        {
            StopCount = 0;
            endedDowntime = endedScheduled = 0;
            statsFrom = Now;
            Repair?.ResetStatistics(Now);
        }

        /// <summary>The crew that repairs this generator's stops (null = none: a stop lasts its own time from its start).
        /// With a crew a stop shows WAITING_FOR_REPAIR until the crew is there, then
        /// <see cref="State"/> for the repair time; MTBF / MTTR, timetable and periodic stops — a shift needs none.</summary>
        public DowntimeRepair Repair { get; set; }

        /// <summary>A stop repaired by the <see cref="Repair"/> crew: WAITING_FOR_REPAIR until the crew is there, then
        /// <paramref name="state"/> for <paramref name="repairTime"/> (drawn when the crew arrives); then
        /// <paramref name="ended"/> runs (it ends the stop).</summary>
        protected StopToken BeginRepairedStop(ElementState state, StopMode mode, string code, string reason, Func<double> repairTime,
                                              Action<StopToken> ended, double due = double.NaN)
        {
            StopToken token = BeginStop(ElementState.WAITING_FOR_REPAIR, mode, code, reason, double.NaN, delayable: true, due);
            // the crew is called when the stop takes effect (an AfterCurrent stop: once the work in hand is done)
            WhenEffective(token, t => Repair.Begin(Target, Clock, t, state, repairTime, () => ended(t)));
            return token;
        }

        /// <summary>Runs <paramref name="action"/> when <paramref name="token"/> takes effect (now if it has), as long as it is
        /// still this generator's open stop (not after a new <see cref="Start"/>).</summary>
        protected void WhenEffective(StopToken token, Action<StopToken> action)
            => token.WhenEffective(t => { if (open.Exists(r => r.Token == t)) action(t); });

        /// <summary>Sets the planned length of an open stop (known only once it started: a failure's time to repair).</summary>
        protected void PlanLength(StopToken token, double length)
        {
            foreach (var r in open) if (r.Token == token) r.Planned = length;
        }

        /// <summary>Stops that must not overlap (a periodic stop without overlap, a timetable that serialises or merges its
        /// intervals): a stop due while an earlier one of this generator is still delayed by the work in hand, or running
        /// past its scheduled end because of it, waits for that one to end and then lasts its whole length.</summary>
        protected bool QueueBehindDelayed { get; set; }

        private readonly Queue<(double due, Action<double> begin)> queued = new Queue<(double, Action<double>)>();

        /// <summary>Begins a stop due now (<paramref name="begin"/>, given its due time), or queues it behind a delayed one
        /// (<see cref="QueueBehindDelayed"/>).</summary>
        protected void BeginOrQueue(Action<double> begin)
        {
            if (QueueBehindDelayed && (queued.Count > 0 || HasDelayedStop())) { queued.Enqueue((Now, begin)); return; }
            begin(Now);
        }

        // An open stop that has not taken effect yet, or took effect after it was due.
        private bool HasDelayedStop()
        {
            foreach (var r in open)
                if (r.Token.IsActive && r.Delayable && !r.Ending && (!r.Token.IsEffective || r.Token.EffectiveStart > r.Due)) return true;
            return false;
        }

        protected abstract void OnStart();

        /// <summary>Cancels pending generator events (stops already applied stay until resumed).</summary>
        public virtual void CancelPending() { }

        /// <summary>True while this generator holds a stop that a pending background event will end (a shift's next
        /// window): the run monitor does not take such a pause for a stall (a stop ended by an ordinary event never
        /// reaches the monitor, which runs only when no such event is left).</summary>
        public virtual bool StopEndPending => false;

        /// <summary>Starts a stop on the target. <paramref name="planned"/>: its length when the generator times it (NaN:
        /// until its end); <paramref name="delayable"/>: the generator times it from when it takes effect
        /// (<see cref="StopToken.WhenEffective"/>; false: the calendar ends it); <paramref name="due"/>: when it was due
        /// (NaN: now).</summary>
        protected StopToken BeginStop(ElementState state, StopMode mode, string code = null, string reason = null,
                                      double planned = double.NaN, bool delayable = false, double due = double.NaN)
        {
            var request = new StopRequest(state, mode, code, reason)
            {
                BlockInput = BlockInput, BlockOutput = BlockOutput, Source = this
            };
            var record = new OpenStop { Due = double.IsNaN(due) ? Now : due, Planned = planned, Delayable = delayable };
            StopToken token = record.Token = Target.Stop(request);
            open.Add(record);
            WhenEffective(token, t =>
            {
                record.Effective = true;
                if (effectiveOpen++ == 0) effectiveSince = t.EffectiveStart;
            });
            StopCount++;
            StopStarted?.Invoke(this, token);
            return token;
        }

        protected void EndStop(StopToken token)
        {
            if (token == null || !token.IsActive) return;
            OpenStop ending = open.Find(r => r.Token == token);
            if (ending != null && queued.Count > 0)
            {
                // the stops queued behind this one (QueueBehindDelayed) start now, before it ends: the target takes no
                // new work in between
                ending.Ending = true;
                while (queued.Count > 0 && !HasDelayedStop())
                {
                    var (dueAt, begin) = queued.Dequeue();
                    begin(dueAt);
                }
            }
            if (!Target.Resume(token)) return;
            if (ending != null)
            {
                if (ending.Effective && --effectiveOpen == 0) endedDowntime += Math.Max(0, Now - Math.Max(effectiveSince, statsFrom));
                endedScheduled += Scheduled(ending, Now);
                open.Remove(ending);
            }
            StopEnded?.Invoke(this, token);
        }

        protected double Now => Clock.GetSimulationTime();

    }

    // =========================================================================

    /// <summary>
    /// Replays a list of stop intervals at absolute simulation times (a historical
    /// trace, planned maintenance, a table in the project database). Only one start
    /// event is pending at a time, so long traces cost nothing up front.
    /// Intervals already finished at <see cref="DowntimeGenerator.Start"/> are skipped;
    /// one in progress is applied for its remaining part.
    /// </summary>
    public sealed class TimetableDowntime : DowntimeGenerator
    {
        private readonly List<DowntimeInterval> intervals;
        private readonly IEvent startEvent;
        private EventHandle pending;
        private int next;
        private double pendingStart, pendingDuration;
        // End events of the stops this timetable started and has not ended yet (cancelled by CancelPending).
        private readonly Dictionary<StopToken, EventHandle> pendingEnds = new Dictionary<StopToken, EventHandle>();

        public IReadOnlyList<DowntimeInterval> Intervals => intervals;

        public TimetableDowntime(Element target, IEnumerable<DowntimeInterval> intervals,
                                 OverlapPolicy overlap = OverlapPolicy.Allow, SimClock clock = null)
            : base(target, clock)
        {
            this.intervals = Normalize(intervals, overlap);
            QueueBehindDelayed = overlap != OverlapPolicy.Allow;   // serialised / merged intervals never overlap, delayed or not
            startEvent = new BackgroundActionEvent(OnIntervalStart);   // a stop's start never moves items itself
        }

        protected override void OnStart()
        {
            next = 0;
            ScheduleNext();
        }

        public override void CancelPending()
        {
            pending?.Cancel();
            pending = null;
            foreach (var h in pendingEnds.Values) h.Cancel();
            pendingEnds.Clear();
            Repair?.CancelAll();
        }

        private void ScheduleNext()
        {
            double now = Now;
            while (next < intervals.Count)
            {
                DowntimeInterval iv = intervals[next];
                if (iv.Duration <= 0 || iv.End <= now) { next++; continue; }
                pendingStart = Math.Max(iv.Start, now);
                pendingDuration = iv.End - pendingStart;
                pending = Clock.ScheduleEvent(startEvent, pendingStart - now);
                return;
            }
            pending = null;
        }

        private void OnIntervalStart()
        {
            pending = null;
            DowntimeInterval iv = intervals[next];
            double length = pendingDuration;
            BeginOrQueue(due =>
            {
                if (Repair != null)
                {
                    // with a crew: the interval's (remaining) length is the repair time, from the crew's arrival
                    BeginRepairedStop(iv.State.IsNone ? State : iv.State, iv.Mode ?? Mode, iv.Code, iv.Reason, () => length, EndStop, due);
                }
                else
                {
                    // its length from when it takes effect: an AfterCurrent stop starts when the work in hand ends, whole
                    StopToken token = BeginStop(iv.State.IsNone ? State : iv.State, iv.Mode ?? Mode, iv.Code, iv.Reason, length, delayable: true, due);
                    WhenEffective(token, t => pendingEnds[t] = Clock.Schedule(() => { pendingEnds.Remove(t); EndStop(t); }, length));
                }
            });
            next++;
            ScheduleNext();
        }

        /// <summary>Sorts intervals by start and applies <paramref name="overlap"/>.</summary>
        public static List<DowntimeInterval> Normalize(IEnumerable<DowntimeInterval> source, OverlapPolicy overlap)
        {
            var list = new List<DowntimeInterval>(source ?? Array.Empty<DowntimeInterval>());
            // Stable sort by start (keeps input order for ties → deterministic).
            var indexed = new List<KeyValuePair<int, DowntimeInterval>>(list.Count);
            for (int i = 0; i < list.Count; i++) indexed.Add(new KeyValuePair<int, DowntimeInterval>(i, list[i]));
            indexed.Sort((a, b) => a.Value.Start != b.Value.Start ? a.Value.Start.CompareTo(b.Value.Start) : a.Key.CompareTo(b.Key));
            list.Clear();
            foreach (var kv in indexed) list.Add(kv.Value);

            if (overlap == OverlapPolicy.Allow || list.Count < 2) return list;

            var result = new List<DowntimeInterval>(list.Count);
            foreach (var iv in list)
            {
                if (result.Count == 0) { result.Add(iv); continue; }
                DowntimeInterval last = result[result.Count - 1];
                if (iv.Start >= last.End) { result.Add(iv); continue; }

                if (overlap == OverlapPolicy.Serialize)
                {
                    var moved = iv;
                    moved.Start = last.End;
                    result.Add(moved);
                }
                else // Merge
                {
                    last.Duration = Math.Max(last.End, iv.End) - last.Start;
                    result[result.Count - 1] = last;
                }
            }
            return result;
        }
    }

    // =========================================================================

    /// <summary>
    /// A repeating planned stop: every <see cref="Period"/> the target stops for <see cref="Duration"/>, the first stop at
    /// <see cref="First"/>, optionally only stops that start before <see cref="Until"/> (preventive maintenance, a break
    /// every shift, a cleaning cycle). Absolute times on the model clock, like a timetable: one start event is pending at
    /// a time (the next stop is scheduled when the current one starts; nothing is materialised up front). A start after
    /// <see cref="First"/> skips the stops already over and applies a running one for its remaining part. Defaults:
    /// state SCHEDULED_DOWN, mode Immediate. Invalid settings throw <see cref="ArgumentException"/> at construction.
    /// </summary>
    public sealed class PeriodicDowntime : DowntimeGenerator
    {
        /// <summary>Time between the starts of two stops (&gt; 0).</summary>
        public double Period { get; }
        /// <summary>Length of each stop (&gt; 0; at most <see cref="Period"/> unless <see cref="AllowOverlap"/>).</summary>
        public double Duration { get; }
        /// <summary>Start of the first stop (&gt;= 0).</summary>
        public double First { get; }
        /// <summary>Only stops that start before this time happen (+infinity = no end).</summary>
        public double Until { get; }
        /// <summary>Stops longer than the period overlap (they stack on the element, counted).</summary>
        public bool AllowOverlap { get; }

        /// <summary>Code attached to the generated stops.</summary>
        public string Code { get; set; } = "PERIODIC";

        private readonly IEvent startEvent;
        private EventHandle pending;
        private long next;               // index of the next stop: it starts at First + next * Period
        private double pendingDuration;  // its (remaining) length
        // End events of the stops this generator started and has not ended yet (cancelled by CancelPending).
        private readonly Dictionary<StopToken, EventHandle> pendingEnds = new Dictionary<StopToken, EventHandle>();

        public PeriodicDowntime(Element target, double period, double duration, double first = 0,
                                double until = double.PositiveInfinity, bool allowOverlap = false, SimClock clock = null)
            : base(target, clock)
        {
            Validate(period, duration, first, until, allowOverlap);
            Period = period; Duration = duration; First = first; Until = until; AllowOverlap = allowOverlap;
            QueueBehindDelayed = !allowOverlap;   // without overlap a stop due while a delayed one runs follows it
            State = ElementState.SCHEDULED_DOWN;
            startEvent = new BackgroundActionEvent(OnStopStart);   // a stop's start never moves items itself
        }

        /// <summary>Throws <see cref="ArgumentException"/> when the settings cannot describe a periodic stop.</summary>
        public static void Validate(double period, double duration, double first, double until, bool allowOverlap)
        {
            if (!(period > 0) || double.IsInfinity(period))
                throw new ArgumentException($"the period must be a positive number (got {period}).");
            if (!(duration > 0) || double.IsInfinity(duration))
                throw new ArgumentException($"the stop duration must be a positive number (got {duration}).");
            if (!allowOverlap && duration > period)
                throw new ArgumentException($"the stop duration ({duration}) is longer than the period ({period}): stops would overlap (allow overlap to stack them).");
            if (!(first >= 0) || double.IsInfinity(first))
                throw new ArgumentException($"the first stop must be at a time >= 0 (got {first}).");
            if (double.IsNaN(until) || until <= first)
                throw new ArgumentException($"'until' ({until}) must be after the first stop ({first}).");
        }

        /// <summary>Start time of stop <paramref name="k"/> (0-based), computed from <see cref="First"/> (no drift).</summary>
        public double StartOf(long k) => First + k * Period;

        protected override void OnStart()
        {
            double now = Now;
            // skip the stops already over at the start (a late start; also the warm-up-free normal case: k = 0)
            next = now > First + Duration ? Math.Max(0L, (long)Math.Floor((now - First - Duration) / Period)) : 0L;
            ScheduleNext();
        }

        public override void CancelPending()
        {
            pending?.Cancel();
            pending = null;
            foreach (var h in pendingEnds.Values) h.Cancel();
            pendingEnds.Clear();
            Repair?.CancelAll();
        }

        private void ScheduleNext()
        {
            double now = Now;
            while (true)
            {
                double start = StartOf(next);
                if (start >= Until) { pending = null; return; }
                double end = start + Duration;
                if (end <= now) { next++; continue; }
                double at = Math.Max(start, now);
                pendingDuration = end - at;
                pending = Clock.ScheduleEvent(startEvent, at - now);
                return;
            }
        }

        private void OnStopStart()
        {
            pending = null;
            double length = pendingDuration;
            BeginOrQueue(due =>
            {
                if (Repair != null)
                {
                    // with a crew: the stop's (remaining) length is the repair time, from the crew's arrival; the next
                    // stop still starts at First + k·Period
                    BeginRepairedStop(State, Mode, Code, "Periodic stop", () => length, EndStop, due);
                }
                else
                {
                    // its length from when it takes effect: an AfterCurrent stop starts when the work in hand ends, whole
                    StopToken token = BeginStop(State, Mode, Code, "Periodic stop", length, delayable: true, due);
                    WhenEffective(token, t => pendingEnds[t] = Clock.Schedule(() => { pendingEnds.Remove(t); EndStop(t); }, length));
                }
            });
            next++;
            ScheduleNext();
        }
    }

    // =========================================================================

    /// <summary>
    /// Random failures: time to failure and time to repair drawn from any
    /// <see cref="IRandomSampler"/> (constant, exponential, Weibull, a table-driven
    /// sampler…). The time to failure counts calendar time from the end of the last
    /// repair, or only busy time (<see cref="DowntimeBasis.BusyTime"/>).
    /// </summary>
    public sealed class MtbfMttrDowntime : DowntimeGenerator
    {
        public IRandomSampler TimeToFailure { get; }
        public IRandomSampler TimeToRepair { get; }

        /// <summary>Optional sampler for the first failure only (e.g. to avoid all machines failing together).</summary>
        public IRandomSampler FirstFailure { get; set; }

        public DowntimeBasis Basis { get; set; } = DowntimeBasis.CalendarTime;

        /// <summary>States that count as busy for <see cref="DowntimeBasis.BusyTime"/> (default PROCESSING).</summary>
        public HashSet<ElementState> BusyStates { get; } = new HashSet<ElementState> { ElementState.PROCESSING };

        /// <summary>Code attached to generated stops.</summary>
        public string Code { get; set; } = "MTBF";

        private readonly IEvent failEvent, repairEvent;
        private EventHandle pendingFail, pendingRepair;
        private StopToken current;
        private double remainingBusy;
        private double armedAt;
        private bool subscribed;
        private bool firstDraw;

        public MtbfMttrDowntime(Element target, IRandomSampler timeToFailure, IRandomSampler timeToRepair,
                                SimClock clock = null) : base(target, clock)
        {
            TimeToFailure = timeToFailure ?? throw new ArgumentNullException(nameof(timeToFailure));
            TimeToRepair  = timeToRepair  ?? throw new ArgumentNullException(nameof(timeToRepair));
            failEvent   = new BackgroundActionEvent(OnFailure);   // the next failure: background; the repair is not
            repairEvent = new ActionEvent(OnRepair);
        }

        protected override void OnStart()
        {
            current = null;
            firstDraw = true;
            if (Basis == DowntimeBasis.BusyTime)
            {
                if (!subscribed) { Target.StateChanged += OnTargetStateChanged; subscribed = true; }
                remainingBusy = DrawTimeToFailure();
                if (BusyStates.Contains(Target.CurrentState)) Arm();
            }
            else
            {
                pendingFail = Clock.ScheduleEvent(failEvent, DrawTimeToFailure());
            }
        }

        public override void CancelPending()
        {
            pendingFail?.Cancel();   pendingFail = null;
            pendingRepair?.Cancel(); pendingRepair = null;
            Repair?.CancelAll();
        }

        private double DrawTimeToFailure()
        {
            IRandomSampler s = firstDraw && FirstFailure != null ? FirstFailure : TimeToFailure;
            firstDraw = false;
            return Math.Max(0.0, SimuLean.Expressions.Samplers.Sample(s, null, Target));   // expressions: the target's stream
        }

        private void Arm()
        {
            if (pendingFail != null || current != null) return;
            armedAt = Now;
            pendingFail = Clock.ScheduleEvent(failEvent, remainingBusy);
        }

        private void Disarm()
        {
            if (pendingFail == null) return;
            pendingFail.Cancel();
            pendingFail = null;
            remainingBusy = Math.Max(0.0, remainingBusy - (Now - armedAt));
        }

        private void OnTargetStateChanged(Element e, ElementState oldState, ElementState newState)
        {
            if (Basis != DowntimeBasis.BusyTime || current != null) return;
            bool wasBusy = BusyStates.Contains(oldState), isBusy = BusyStates.Contains(newState);
            if (wasBusy && !isBusy) Disarm();
            else if (!wasBusy && isBusy) Arm();
        }

        private void OnFailure()
        {
            pendingFail = null;
            remainingBusy = 0;
            if (Repair != null)
            {
                // with a crew: the time to repair is drawn when the crew arrives and counts from then; the next time to
                // failure from the end of the repair, as without one
                current = BeginRepairedStop(State, Mode, Code, null, repairTime ?? (repairTime = DrawTimeToRepair), RepairedByCrew);
                return;
            }
            current = BeginStop(State, Mode, Code, null, double.NaN, delayable: true);
            double ttr = Math.Max(0.0, SimuLean.Expressions.Samplers.Sample(TimeToRepair, null, Target));   // drawn after the stop, as always
            PlanLength(current, ttr);
            // the repair time counts from when the stop takes effect: an AfterCurrent failure waits for the work in hand
            WhenEffective(current, t => { if (t == current) pendingRepair = Clock.ScheduleEvent(repairEvent, ttr); });
        }

        private Func<double> repairTime;
        private double DrawTimeToRepair() => Math.Max(0.0, SimuLean.Expressions.Samplers.Sample(TimeToRepair, null, Target));
        private void RepairedByCrew(StopToken token) { if (token == current) OnRepair(); }

        private void OnRepair()
        {
            pendingRepair = null;
            StopToken token = current;
            current = null;

            // Draw the next time to failure before resuming: the resume changes the
            // element's state and OnTargetStateChanged may arm the failure at once.
            if (Basis == DowntimeBasis.BusyTime) remainingBusy = DrawTimeToFailure();
            EndStop(token);

            if (Basis == DowntimeBasis.BusyTime)
            {
                if (BusyStates.Contains(Target.CurrentState)) Arm();
            }
            else
            {
                pendingFail = Clock.ScheduleEvent(failEvent, DrawTimeToFailure());
            }
        }
    }

    // =========================================================================

    /// <summary>
    /// Stops the element outside the working windows of a weekly shift pattern,
    /// using the model <see cref="SimCalendar"/>. Defaults: state OFF_SHIFT, mode
    /// AfterCurrent (work in progress finishes, no new items are taken).
    /// <para>The end of a working window takes effect a hair early (<see cref="CloseTolerance"/>): a job that ends at
    /// the closing time up to rounding must not let the next item start before the stop (13 patients in
    /// a 12-slot session). Two roundings meet there: the drift of slot times summed from the window's start, and slot
    /// lengths typed in single precision (the Unity fields are <c>float</c>: 20 min as 0.3333333 h is 0.12 ms short, so
    /// twelve slots end 1.4 ms early).</para>
    /// </summary>
    public sealed class ShiftDowntime : DowntimeGenerator
    {
        public WeeklyShiftPattern Pattern { get; }
        private readonly IEvent tick;
        private EventHandle pending;
        private StopToken current;
        private DateTime? due;   // the calendar change the pending tick stands for (it may fire a hair early)

        /// <summary>Calendar time by which a window's end is applied early: no job starts in the last 10 ms of a window.</summary>
        public const double CloseToleranceSeconds = 0.01;

        /// <summary>How early (model time units) a window's end at <paramref name="changeTime"/> is applied: the larger of
        /// <see cref="CloseToleranceSeconds"/> of calendar time and <see cref="SimTime.ToleranceAt"/> the change time (double-precision drift).</summary>
        public static double CloseTolerance(double changeTime, double secondsPerUnit)
            => Math.Max(CloseToleranceSeconds / (secondsPerUnit > 0 ? secondsPerUnit : 1.0), SimTime.ToleranceAt(changeTime));

        public ShiftDowntime(Element target, WeeklyShiftPattern pattern, SimClock clock = null) : base(target, clock)
        {
            Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
            State = ElementState.OFF_SHIFT;
            Mode  = StopMode.AfterCurrent;
            tick  = new BackgroundActionEvent(OnTick);   // calendar tick
        }

        protected override void OnStart()
        {
            current = null;
            due = null;
            OnTick();
        }

        public override void CancelPending()
        {
            pending?.Cancel();
            pending = null;
            due = null;
        }

        /// <summary>Off shift with the next window's tick pending (a background event): the stop will end.</summary>
        public override bool StopEndPending => current != null && pending != null;

        private void OnTick()
        {
            pending = null;
            SimCalendar cal = Clock.Context.Calendar;
            // the change this tick stands for (it may run a hair before it), else the clock's time
            DateTime now = due ?? cal.ToDateTime(Now);
            due = null;
            bool working = Pattern.IsWorking(now);

            if (working && current != null) { EndStop(current); current = null; }
            else if (!working && current == null) current = BeginStop(State, Mode, "SHIFT", "Off shift");

            DateTime change = Pattern.NextChange(now);
            if (change == DateTime.MaxValue) return;
            double at = cal.ToSimTime(change);
            // a window's end is applied a hair early, so a job ending at the closing time (up to rounding) is the last
            // one; a window's start stays exact
            if (working && !Pattern.IsWorking(change)) at -= CloseTolerance(at, cal.SecondsPerUnit);
            due = change;
            pending = Clock.ScheduleEvent(tick, Math.Max(0.0, at - Now));
        }
    }
}
