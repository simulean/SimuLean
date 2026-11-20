using SimuLean.Statistics;
using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Base class for model element.
    /// </summary>
    /// <summary>
    /// Something whose statistics the warm-up discards (<see cref="Element.ScheduleWarmup"/>): elements and resources
    /// (they are elements), downtime generators, resource teams.
    /// </summary>
    public interface IWarmupStatistics
    {
        /// <summary>Forgets the statistics gathered so far; the model itself carries on unchanged.</summary>
        void ResetStatistics();
    }

    public abstract partial class Element : SimEntity, IWarmupStatistics
    {
        private ILink input, output;
        protected SimClock simClock;
        public IVElement vElement;
        readonly string name;

        // ── Input validation strategy ─────────────────────────────────────────
        /// <summary>
        /// Strategy for validating incoming items.
        /// Can be used by elements that receive items (queues, servers, etc.)
        /// to filter or validate items before accepting them.
        /// Default is null (accept all items).
        /// </summary>
        protected InputStrategy inputStrategy;

        // ── Output routing strategy ───────────────────────────────────────────
        /// <summary>
        /// Strategy for selecting which output should receive an item.
        /// Used when an element has multiple possible outputs.
        /// Default is null (uses FirstAvailableStrategy).
        /// </summary>
        protected IOutputStrategy outputStrategy;

        // ── Labels set on hand-over ───────────────
        /// <summary>Labels set on every item that enters this element, before its <c>Receive</c> (null = none).</summary>
        public LabelAssignments EntryLabels { get; set; }
        /// <summary>Labels set on every item that leaves this element, before the next one's <c>Receive</c> (null = none).</summary>
        public LabelAssignments ExitLabels { get; set; }

        private SimuLean.Stochastics.Streams.IRandomEngine expressionRandom;
        /// <summary>The fallback stream of the random functions (<c>rand()</c>, <c>pick()</c>…) of an expression evaluated over
        /// this element with no stream of its own: the model's stream <c>name/expr</c>, created on first use. Every random use
        /// of the library has its own (<see cref="RandomStreamFor"/>, labels, samplers).</summary>
        public SimuLean.Stochastics.Streams.IRandomEngine ExpressionRandom
            => expressionRandom ?? (expressionRandom = simClock.Context.CreateRandomStream(name, "expr"));

        private Dictionary<string, SimuLean.Stochastics.Streams.IRandomEngine> purposeStreams;
        /// <summary>The stream of one random use of this element (owner = its name, <paramref name="purpose"/>: routing,
        /// admission, noShow…), created on first use. Each use draws from its own, so a change in one leaves the draws of the
        /// others as they were: scenarios stay comparable (common random numbers).</summary>
        public SimuLean.Stochastics.Streams.IRandomEngine RandomStreamFor(string purpose)
        {
            if (purposeStreams == null) purposeStreams = new Dictionary<string, SimuLean.Stochastics.Streams.IRandomEngine>();
            if (!purposeStreams.TryGetValue(purpose, out var stream))
                purposeStreams[purpose] = stream = simClock.Context.CreateRandomStream(name, purpose);
            return stream;
        }

        // ── state tracking ────────────────────────────────────────────────────
        private ElementStateTracker _stateTracker;

        /// <summary>
        /// The element's current state as a typed <see cref="ElementState"/> token
        /// (no longer a raw string). Setting it via <see cref="SetState"/> is the
        /// single point that updates the state statistics.
        /// </summary>
        public ElementState CurrentState => _stateTracker.CurrentState;

        /// <summary>Changes of the state tracker (transitions, resets): a reader caching a value derived from the states
        /// recomputes it when this or the time changes.</summary>
        protected int StateVersion => _stateTracker.Version;

        /// <summary>
        /// Where the element is: a location id (a transport-network node), optional. Resources carry items from
        /// and to it (travel empty to the origin's location, loaded to the destination's); a spatial resource's
        /// current location changes as it travels. Null = not placed (abstract transport only). A port element (a
        /// Combiner's component input) is where its owner is.
        /// </summary>
        public virtual string Location { get; set; }

        /// <summary>
        /// Where the resources that process at this element stand: location ids around it, e.g.
        /// the welders' places beside a block, apart from <see cref="Location"/> where items are picked up and dropped.
        /// Each processing resource takes a free one (in order); empty = they work at <see cref="Location"/>.
        /// </summary>
        public List<string> WorkLocations { get; } = new List<string>();

        // ── statistics collector ──────────────────────────────────────────────
        /// <summary>
        /// Per-element statistics collector. Tracks input/output counts,
        /// current content, peak occupancy and per-item stay time.
        /// Driven automatically by the link layer.
        /// </summary>
        public ElementStatsCollector StatsCollector { get; private set; }

        // ─────────────────────────────────────────────────────────────────────
        /// <summary>The element's name.</summary>
        public override string Name => name;

        public Element(string name, SimClock simClock, IVElement vElement = null)
        {
            this.name     = name;
            this.simClock = simClock;
            this.vElement = vElement ?? new NoView();

            _stateTracker  = new ElementStateTracker(ElementState.IDLE, 0.0);
            StatsCollector = new ElementStatsCollector(this, simClock);
            
            // Default strategies
            this.inputStrategy = null;
            this.outputStrategy = null;

            // Per-model registry (SimContext) — replaces the former process-wide
            // static list, so parallel headless models never share state.
            simClock?.Context.Register(this);
        }

        // ── Input strategy API ────────────────────────────────────────────────

        /// <summary>
        /// Sets the input validation strategy for this element.
        /// </summary>
        public virtual void SetInputStrategy(InputStrategy strategy)
        {
            this.inputStrategy = strategy;
        }

        /// <summary>
        /// Gets the current input validation strategy.
        /// </summary>
        public virtual InputStrategy GetInputStrategy()
        {
            return inputStrategy;
        }

        /// <summary>
        /// Validates an item against the input strategy with origin context
        /// (<see cref="InputStrategy.Admits"/>: validity plus state limits such as MaxQueue).
        /// Returns true if the item is admitted (or if no strategy is set).
        /// </summary>
        public virtual bool ValidateInput(Item theItem, Element origin = null)
        {
            if (inputStrategy == null)
                return true;

            bool admitted = inputStrategy.Admits(this, theItem, origin);
            if (!(inputStrategy is DefaultStrategy)) Counters.Admission(admitted);   // read-only counter (logic viewer)
            return admitted;
        }

        /// <summary>An item entered (the link layer's entry, <see cref="Statistics.ElementStatsCollector.OnEntry"/>): counted as an
        /// entry through the admission when the element has a condition (<see cref="DecisionCounters.Entered"/>).</summary>
        internal void CountAdmittedEntry()
        {
            if (inputStrategy != null && !(inputStrategy is DefaultStrategy)) Counters.Entry();
        }

        private DecisionCounters decisions;

        /// <summary>The run-time counters of its decisions (routing per output, admission, wake-ups, releases), null
        /// before its first counted decision. Read-only for the model (<see cref="DecisionCounters"/>).</summary>
        public DecisionCounters Decisions => decisions;

        /// <summary>The counters, made at the first decision counted.</summary>
        internal DecisionCounters Counters => decisions ?? (decisions = new DecisionCounters());

        // ── Output strategy API ───────────────────────────────────────────────

        /// <summary>
        /// Sets the output routing strategy for this element.
        /// </summary>
        public virtual void SetOutputStrategy(IOutputStrategy strategy)
        {
            this.outputStrategy = strategy;
        }

        /// <summary>
        /// Gets the current output routing strategy.
        /// Returns FirstAvailableStrategy if none is set.
        /// </summary>
        public virtual IOutputStrategy GetOutputStrategy()
        {
            return outputStrategy ?? DefaultOutputStrategy;
        }

        /// <summary>The routing of an element without its own strategy (stateless, shared).</summary>
        private static readonly IOutputStrategy DefaultOutputStrategy = new FirstAvailableStrategy();

        // ── state API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Sets the element's state. This is the single entry point that drives the
        /// state statistics: it forwards to <see cref="ElementStateTracker.Transition"/>,
        /// which accumulates time-in-state and appends to the state log. Accepts any
        /// <see cref="ElementState"/> token, including subsystem-specific ones obtained
        /// via <see cref="ElementState.Of"/> (a raw string also converts implicitly).
        /// </summary>
        /// <remarks>While a stop is effective (see <see cref="Stop"/>) the stop state is what
        /// gets accounted; the value set here is remembered as <see cref="UnderlyingState"/>
        /// and restored when the element runs again.</remarks>
        protected void SetState(ElementState newState)
        {
            _underlyingState = newState;
            if (_downShown) return;
            ApplyTrackedState(newState);
        }

        public double GetTimeInState(ElementState state)
            => _stateTracker.GetTimeInState(state, simClock.GetSimulationTime());

        public double GetStateRatio(ElementState state)
            => _stateTracker.GetStateRatio(state, simClock.GetSimulationTime());

        /// <summary>
        /// Convenience method for the Experimenter / Optimizer metrics panel.
        /// Returns the fraction of simulation time this element spent in the PROCESSING state.
        /// Available on all element types in both Unity-clock and headless mode.
        /// </summary>
        public double GetProcessingRatio()
            => GetStateRatio(ElementState.PROCESSING);

        // ── base statistics accessors ─────────────────────────────
        // Available on EVERY element type, in both Unity-clock and headless mode,
        // so the Experimenter/Optimizer metric dropdowns can rely on them.
        // They wrap StatsCollector, whose values are driven by the Link layer.

        /// <summary>Total items received (StatsCollector.VarInput).</summary>
        public double GetStatsInputCount()     => StatsCollector.VarInput.Value;

        /// <summary>Total items sent out (StatsCollector.VarOutput).</summary>
        public double GetStatsOutputCount()    => StatsCollector.VarOutput.Value;

        /// <summary>Current content (items inside right now).</summary>
        public double GetStatsContentCurrent() => StatsCollector.VarContent.Value;

        /// <summary>Time-weighted average content (true average WIP).</summary>
        public double GetStatsContentAvgTW()   => StatsCollector.ContentAvgTimeWeighted;

        /// <summary>Peak content observed during the run.</summary>
        public double GetStatsContentMax()     => StatsCollector.VarContent.Max;

        /// <summary>Mean stay time per item (0 until an item has left).</summary>
        public double GetStatsStayTimeMean()   => StatsCollector.VarStayTime.Average;

        /// <summary>Sample standard deviation of the stay times.</summary>
        public double GetStatsStayTimeStdDev() => StatsCollector.VarStayTime.StdDev;

        /// <summary>Maximum stay time observed.</summary>
        public double GetStatsStayTimeMax()    => StatsCollector.VarStayTime.Max;

        /// <summary>Minimum stay time observed.</summary>
        public double GetStatsStayTimeMin()    => StatsCollector.VarStayTime.Min;

        public Dictionary<string, double> GetStateSnapshot()
            => _stateTracker.GetAllAccumulated(simClock.GetSimulationTime());

        public IReadOnlyList<StateLogEntry> GetStateLog()
            => _stateTracker.Log;

        /// <summary>Keep the chronological state log (default; timelines, the inspector's history). Off: only the time per
        /// state — set by a headless run that records no timeline.</summary>
        public bool KeepStateLog { get => _stateTracker.KeepLog; set => _stateTracker.KeepLog = value; }

        protected void ResetStateTracker()
        {
            ResetDowntime();
            _stateTracker.Reset(ElementState.IDLE, simClock.GetSimulationTime());
        }

        /// <summary>
        /// Warm-up reset: discards the statistics gathered so far (link counters, stay times,
        /// content history, time per state) without touching the model — items, pending work,
        /// stops and the current state carry on. Subclasses with their own counters override it
        /// and call the base. See <see cref="ScheduleWarmup"/>.
        /// </summary>
        public virtual void ResetStatistics()
        {
            StatsCollector.ResetForWarmup();
            _stateTracker.Reset(_stateTracker.CurrentState, simClock.GetSimulationTime());
            StopCount = 0;   // stops started after the warm-up, like every other figure
            ResetTimeouts();   // max-wait timeouts too
        }

        /// <summary>
        /// Schedules a warm-up: at <paramref name="warmupTime"/> everything in <paramref name="participants"/> (elements,
        /// resources, downtime generators, resource teams) runs <see cref="IWarmupStatistics.ResetStatistics"/>, and so do
        /// the model's lists, so the results only cover the steady-state period. No-op for a non-positive time.
        /// </summary>
        public static EventHandle ScheduleWarmup(SimClock clock, IEnumerable<IWarmupStatistics> participants, double warmupTime)
        {
            if (clock == null || participants == null || !(warmupTime > 0)) return null;
            return clock.ScheduleEvent(new WarmupEvent(clock, new List<IWarmupStatistics>(participants)),
                                       Math.Max(0.0, warmupTime - clock.GetSimulationTime()));
        }

        private sealed class WarmupEvent : IBackgroundEvent   // resets statistics, moves nothing
        {
            private readonly SimClock clock;
            private readonly List<IWarmupStatistics> elements;
            public WarmupEvent(SimClock clock, List<IWarmupStatistics> elements) { this.clock = clock; this.elements = elements; }
            public void Execute()
            {
                foreach (var e in elements) e.ResetStatistics();
                foreach (var l in clock.Context.Lists) l.ResetStatistics();   // the model's lists too
            }
        }

        // ── existing API ──────────────────────────────────────────────────────
        // Note: the former static GetElements()/GetInventory() moved to
        // SimContext (simClock.Context.Elements / GetItemCount()) so that each
        // model owns its registry.

        public ILink     GetInput()              => input;
        /// <summary>
        /// Adds an input link. An element fed by several links (one per upstream element, or a list plus
        /// connections) gets an <see cref="InputFanIn"/> so that, when it has room, <b>every</b> upstream is
        /// notified (with a single link the other upstream elements
        /// would stay BLOCKED forever once they had been refused).
        /// </summary>
        public void SetInput(ILink link)
        {
            if (link == null || link == input) return;
            if (input == null) input = link;
            else if (input is InputFanIn fan) fan.Add(link);
            else input = new InputFanIn(input, link);
        }

        /// <summary>Removes every input link (rewiring).</summary>
        public void ClearInput() => input = null;

        /// <summary>
        /// The element that actually receives items from <paramref name="origin"/> when a link
        /// from <paramref name="origin"/> to this element is created (<see cref="GeneralLink.CreateLink(Element, List{Element})"/>).
        /// Default: this element. Elements with several typed inputs override it — a
        /// <see cref="Combiner"/> maps its first connection to the main input and the next ones
        /// to its component inputs, so every upstream element is connected the same way.
        /// </summary>
        public virtual Element ResolveInputFor(Element origin) => this;
        public ILink     GetOutput()             => output;

        /// <summary>Sends <paramref name="item"/> through the output link; false when it is refused or there is no
        /// output link (an element not connected downstream keeps its items instead of failing).</summary>
        protected bool SendOut(Item item) => output != null && output.SendItem(item, this);

        /// <summary>True when the output carries items by resources (<see cref="Resources.ResourceTransportLink"/>):
        /// offering an item only requests its carrier and the item stays here until it is loaded.</summary>
        protected bool OutputRequestsCarriers => output is Resources.ResourceTransportLink;

        /// <summary>True when the output already requested a carrier for <paramref name="item"/>. A holder releasing its
        /// items in order passes over such an item and offers the next one, so every held item gets its own carrier
        /// (a queue as transport origin sent one truck at a time).</summary>
        protected bool AwaitsCarrier(Item item) => output is Resources.ResourceTransportLink t && t.IsRequested(item);

        /// <summary>Tells the input link this element has room (no-op without an input link).</summary>
        protected bool NotifyInput() => input != null && input.NotifyAvailable(this);

        // ── room promised to loads on their way here ──
        // One ledger per receiving element, shared by every link and carrier that sends a load before it can enter
        // (carried by a team, walking, a milk run, a list transfer): each counts the room the others promised, so two
        // origins never promise the same last place (it was counted per link: several links over-filled one place).
        private HashSet<Item> promisedRoom;
        private HashSet<object> waitingForPromisedRoom;

        /// <summary>Loads on their way here: each holds one place until it enters (or its move is dropped).</summary>
        public int RoomPromised => promisedRoom?.Count ?? 0;

        /// <summary>True when a place here is promised to <paramref name="item"/>.</summary>
        public bool IsRoomPromised(Item item) => item != null && promisedRoom != null && promisedRoom.Contains(item);

        /// <summary>Free places not promised to loads on their way (negative = unlimited, as <see cref="GetFreeCapacity"/>;
        /// never below 0 otherwise).</summary>
        public int FreeCapacityBeyondPromises()
        {
            int free = GetFreeCapacity();
            if (free < 0) return free;
            int left = free - RoomPromised;
            return left > 0 ? left : 0;
        }

        /// <summary>True when a free place is left after the promised ones. When only promises stand in the way,
        /// <paramref name="asker"/> (a link, a milk run) is remembered: it is told (this element's input is notified) when
        /// another one releases a promise — its own releases it handles itself.</summary>
        public bool HasRoomBeyondPromises(object asker)
        {
            int free = GetFreeCapacity();
            if (free < 0 || free - RoomPromised > 0) return true;
            if (free > 0) WaitForPromisedRoom(asker);
            return false;
        }

        /// <summary>Remembers <paramref name="asker"/> as refused because the free room here is promised (see
        /// <see cref="HasRoomBeyondPromises"/>).</summary>
        public void WaitForPromisedRoom(object asker)
        {
            if (asker == null) return;
            (waitingForPromisedRoom ??= new HashSet<object>()).Add(asker);
        }

        /// <summary>Promises a place here to <paramref name="item"/>, on its way (idempotent).</summary>
        public void PromiseRoom(Item item)
        {
            if (item == null) return;
            (promisedRoom ??= new HashSet<Item>()).Add(item);
        }

        /// <summary>Releases the place promised to <paramref name="item"/> (it entered, or its move was dropped); false when
        /// none was. When another asker than <paramref name="by"/> was refused for promised room, this element's input is
        /// notified after the current event, so every upstream may try again.</summary>
        public bool ReleaseRoom(Item item, object by)
        {
            if (item == null || promisedRoom == null || !promisedRoom.Remove(item)) return false;
            UnreserveRoom(item);
            if (waitingForPromisedRoom != null && waitingForPromisedRoom.Count > 0)
            {
                bool others = waitingForPromisedRoom.Count > 1 || !waitingForPromisedRoom.Contains(by);
                if (others)
                {
                    waitingForPromisedRoom.Clear();
                    simClock.Schedule(NotifyPromisedRoomFreed, 0);
                }
                else if (promisedRoom.Count == 0) waitingForPromisedRoom.Clear();
            }
            return true;
        }

        /// <summary>Drops the place promised to <paramref name="item"/> without telling anyone: a new run (the owner's
        /// reset), when nothing is on its way any more.</summary>
        public void ForgetPromise(Item item)
        {
            if (item == null || promisedRoom == null || !promisedRoom.Remove(item)) return;
            UnreserveRoom(item);
            if (promisedRoom.Count == 0) waitingForPromisedRoom?.Clear();
        }

        private void NotifyPromisedRoomFreed()
        {
            if (!IsInputBlocked) NotifyInput();
        }
        public void     SetOutput(ILink output)  { this.output = output; }
        public SimClock GetSimClock()           => simClock;

        /// <summary>
        /// Base Start — resets both the state tracker and the statistics collector.
        /// Subclass overrides should call <c>ResetStateTracker()</c> and
        /// <c>StatsCollector.Reset()</c> (or simply <c>base.Start()</c>).
        /// </summary>
        // a wake-up of the waiting senders is scheduled at the current time
        private bool wakePending;

        /// <summary>Offers this element again the items waiting for it upstream — what its admission refused: a zero-delay event at the current time, after the event that asked (a reaction's actions all run first), once
        /// however many writes ask before it. Asked when a property its admission condition reads is written, and by a
        /// reaction's <c>Retry</c> action. It asks upstream as a freed place does, while it has room.</summary>
        public void WakeInput()
        {
            if (wakePending) return;
            wakePending = true;
            Counters.WokenUp();
            simClock.Schedule(Wake, 0);
        }

        private void Wake()
        {
            wakePending = false;
            for (int i = 0; i < 100000 && !IsInputBlocked && GetFreeCapacity() != 0; i++)
                if (!NotifyInput()) break;
        }

        public virtual void Start()
        {
            wakePending = false;
            ResetTimeouts();
            ResetStateTracker();
            StatsCollector.Reset();
        }

        public abstract int  GetQueueLength();
        public abstract int  GetFreeCapacity();
        public abstract bool CheckAvailability(Item theItem);
        public abstract bool Receive(Item theItem);
        public abstract bool Unblock();

        /// <summary>
        /// Hands over one specific item this element holds for output (a list reserved it), wherever it is
        /// among its outgoing items: sends it through the output link and updates the element as after a
        /// normal send. Elements that hold several outgoing items override it (queues, servers, sources);
        /// the default returns false and the caller falls back to <see cref="Unblock"/>, which sends the
        /// element's next item — the same one when it holds a single item.
        /// </summary>
        public virtual bool SendHeldItem(Item item) => false;
    }
}
