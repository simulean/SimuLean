using System;
using System.Collections.Generic;
using SimuLean.Resources;

namespace SimuLean
{
    /// <summary>How a <see cref="Stacker"/> chooses the stack position of an item.</summary>
    public enum StackSelectionRule
    {
        /// <summary>Lowest-index position that can take the item.</summary>
        FirstNotFull = 0,
        /// <summary>Cycles through the positions: the next one after the last used that can take the item.</summary>
        RoundRobin = 1,
        /// <summary>The numeric item label <see cref="Stacker.PositionLabel"/> is the 0-based position index
        /// (missing or out of range: <see cref="FirstNotFull"/>).</summary>
        ByLabelIndex = 2,
        /// <summary>Stacks are formed per key (item label <see cref="Stacker.KeyLabel"/>, or the item type when
        /// empty): an item joins the open stack of its key, a new key takes an empty position.</summary>
        ByKey = 3,
    }

    /// <summary>One stacking position of a <see cref="Stacker"/> and the stack forming on it (read-only view).</summary>
    public sealed class StackPosition
    {
        internal readonly List<Item> items = new List<Item>();
        internal readonly List<double> heights = new List<double>();

        internal StackPosition(int index) { Index = index; }

        public int Index { get; }

        /// <summary>Item capacity of this position (≤ 0 = the stacker's <see cref="Stacker.Capacity"/>).</summary>
        public int Capacity { get; set; }

        /// <summary>Height limit of this position (NaN = the stacker's <see cref="Stacker.MaxHeight"/>; ≤ 0 = none).</summary>
        public double MaxHeight { get; set; } = double.NaN;

        /// <summary>Location (point) the crane places this position's items at (crane mode, <see cref="Stacker.PickLocation"/>).</summary>
        public string Location { get; set; }

        /// <summary>Items on the stack, bottom first.</summary>
        public IReadOnlyList<Item> Items => items;

        public int Count => items.Count;

        /// <summary>Sum of the item heights on the stack.</summary>
        public double Height { get; internal set; }

        /// <summary>Key of the stack (see <see cref="StackSelectionRule.ByKey"/>; set from the first item; null when empty).</summary>
        public string Key { get; internal set; }

        /// <summary>Simulation time the first item was placed (NaN when empty).</summary>
        public double StartedAt { get; internal set; } = double.NaN;

        /// <summary>The stack is complete and waiting to leave as <see cref="Compound"/> (takes no more items).</summary>
        public bool IsComplete => Compound != null;

        /// <summary>The compound item built when the stack completed (null while open).</summary>
        public Item Compound { get; internal set; }

        /// <summary>Why the stack completed: <c>full</c>, <c>height</c>, <c>forced</c> or <c>blocked</c> (null while open).</summary>
        public string CompletionReason { get; internal set; }

        /// <summary>Height of the item at <paramref name="level"/> (0 = bottom).</summary>
        public double HeightAt(int level) => heights[level];

        /// <summary>Sum of the heights of the items below <paramref name="level"/>.</summary>
        public double HeightBelow(int level)
        {
            double h = 0;
            for (int i = 0; i < level && i < heights.Count; i++) h += heights[i];
            return h;
        }

        /// <summary>Level (0 = bottom) of <paramref name="item"/> on this stack, −1 if not on it.</summary>
        public int LevelOf(Item item) => items.IndexOf(item);

        internal void Clear()
        {
            items.Clear(); heights.Clear();
            Height = 0; Key = null; StartedAt = double.NaN;
            Compound = null; CompletionReason = null;
        }
    }

    /// <summary>One pick-and-place cycle of a <see cref="Stacker"/> (event payload for views).</summary>
    public sealed class StackerCycle
    {
        internal StackerCycle(Item item, int position, int level, double start, double duration, double itemHeight, double heightBelow)
        {
            Item = item; Position = position; Level = level; Start = start; Duration = duration;
            ItemHeight = itemHeight; HeightBelow = heightBelow;
        }

        public Item Item { get; }
        /// <summary>Target position index.</summary>
        public int Position { get; }
        /// <summary>Level the item will take on the stack (0 = bottom).</summary>
        public int Level { get; }
        /// <summary>Simulation time the cycle started (the item left the pick point).</summary>
        public double Start { get; }
        /// <summary>Sampled cycle time (0 = instantaneous placement). Stops pause it (see <see cref="Stacker.IsWorkPaused"/>).</summary>
        public double Duration { get; }
        /// <summary>Resolved height of the item (0 when unknown).</summary>
        public double ItemHeight { get; }
        /// <summary>Stack height under the item when the cycle started.</summary>
        public double HeightBelow { get; }
    }

    /// <summary>
    /// Stacker / palletiser: receives items of any type and builds stacks at N positions, then
    /// releases each finished stack downstream as <b>one compound item</b> that carries the stacked
    /// items as <see cref="Item.subItems"/> (the engine's batching convention, as the combiner and
    /// the picking / assembly stations use it). Generalised from a pick-and-place board stacker of an
    /// industrial application; nothing in it depends on the item type.
    ///
    /// <para><b>Flow.</b> The stacker has one <i>pick point</i> (capacity 1): an item arriving there
    /// waits until a position can take it, then a pick-and-place <i>cycle</i> moves it onto the stack
    /// (duration sampled from <see cref="CycleTime"/>, scheduled with <see cref="Element.ScheduleWork"/>
    /// so stops pause it; null = instantaneous, still as a separate same-time event). The pick point is
    /// freed when the cycle starts, so the next item can arrive while the gantry places the current one.
    /// One cycle at a time.</para>
    ///
    /// <para><b>Positions and capacity.</b> <see cref="Capacity"/> items per position (or the position's
    /// own <see cref="StackPosition.Capacity"/>) and optionally a height limit <see cref="MaxHeight"/>
    /// using the item height (<see cref="ItemHeight"/> delegate → numeric label <see cref="HeightLabel"/>
    /// → <see cref="DefaultItemHeight"/>). A stack completes when it reaches its capacity, or when another
    /// item as high as the one just placed would exceed the height limit; <see cref="ReleaseStack"/> /
    /// <see cref="ReleaseAll"/> close a partial stack on demand (end of batch, control logic). The
    /// position is chosen by <see cref="SelectionRule"/> or the <see cref="PositionSelector"/> delegate.
    /// With <see cref="ReleasePartialWhenBlocked"/> (default) an item that cannot be placed because open
    /// stacks are in its way (its stack is too high for it, or all positions hold other keys) closes the
    /// blocking stack — the oldest one when several qualify — so the stacker cannot deadlock.</para>
    ///
    /// <para><b>Release.</b> A complete stack becomes a new item of type <see cref="StackType"/> (labels of
    /// its first item copied when <see cref="CopyLabelsFromFirst"/>, plus <c>stackPosition</c>,
    /// <c>stackCount</c>, <c>stackHeight</c>, <c>stackKey</c> and <c>stackReason</c>) and is sent through the
    /// output link; refused stacks wait in completion order and are retried on <see cref="Unblock"/>.
    /// The position is free again once its stack has left.</para>
    ///
    /// <para><b>Statistics</b> come only from the Link layer: every stacked item enters through the input
    /// link, and the compound leaves through the output link. The stacker opts in to
    /// <see cref="Statistics.ElementStatsCollector.CountSubItemsOnExit"/>, so the compound's exit records
    /// each stacked item as leaving (input = output = items, exact content and stay times); downstream
    /// elements count the compound as one item. States: PROCESSING (cycle running), BLOCKED (a complete
    /// stack cannot leave, or the waiting item cannot be placed), RECEIVING (stacks forming, waiting for
    /// items), IDLE (empty). Views get C# events (<see cref="ItemArrived"/>, <see cref="CycleStarted"/>,
    /// <see cref="ItemPlaced"/>, <see cref="StackCompleted"/>, <see cref="StackReleased"/>) plus the
    /// <see cref="IVElement"/> calls. Engine-pure (no Unity dependency). See docs/architecture.md §3.14.</para>
    /// </summary>
    public class Stacker : Element, SimuLean.Resources.IResourceUser
    {
        public const string LabelPosition = "stackPosition";
        public const string LabelCount = "stackCount";
        public const string LabelHeight = "stackHeight";
        public const string LabelKey = "stackKey";
        public const string LabelReason = "stackReason";

        private const double Eps = 1e-9;

        private readonly StackPosition[] positions;
        private readonly bool[] closeAfterPlace;
        private readonly List<StackPosition> releaseQueue = new List<StackPosition>();
        private Item pickItem;
        private Item carried;
        private int carriedTarget = -1;
        private StackerCycle currentCycle;
        private bool kickPending;
        private int rrNext;
        private int stackSeq;

        // ── configuration ────────────────────────────────────────────────────

        /// <summary>Default item capacity of every position (≥ 1).</summary>
        public int Capacity { get; set; }

        /// <summary>Default height limit of every position (≤ 0 = no height limit).</summary>
        public double MaxHeight { get; set; }

        /// <summary>Numeric item label read as the item height (null/empty = not used).</summary>
        public string HeightLabel { get; set; } = "height";

        /// <summary>Height used when neither <see cref="ItemHeight"/> nor <see cref="HeightLabel"/> give a positive value
        /// (0 = such items do not count towards the height limit).</summary>
        public double DefaultItemHeight { get; set; }

        /// <summary>Optional height strategy; a non-positive result falls back to the label / default.</summary>
        public Func<Item, double> ItemHeight { get; set; }

        /// <summary>An empty position takes one item even if it is higher than the limit (default true: no deadlock).</summary>
        public bool AcceptOversizeOnEmpty { get; set; } = true;

        public StackSelectionRule SelectionRule { get; set; } = StackSelectionRule.FirstNotFull;

        /// <summary>Numeric label with the 0-based target position (<see cref="StackSelectionRule.ByLabelIndex"/>).</summary>
        public string PositionLabel { get; set; } = LabelPosition;

        /// <summary>Label whose value is the stack key (<see cref="StackSelectionRule.ByKey"/>); null/empty = the item type.</summary>
        public string KeyLabel { get; set; } = "";

        /// <summary>Optional position rule overriding <see cref="SelectionRule"/>: returns the position index for the
        /// item (it waits when that position cannot take it), or −1 to wait.</summary>
        public Func<Item, Stacker, int> PositionSelector { get; set; }

        /// <summary>Close the blocking open stack when the waiting item cannot be placed (default true).</summary>
        public bool ReleasePartialWhenBlocked { get; set; } = true;

        /// <summary>Pick-and-place time per item (null = instantaneous). A <see cref="ExpressionSampler"/> gets the item.</summary>
        public IRandomSampler CycleTime { get; set; }

        /// <summary>
        /// <b>Crane mode</b>: with a pick location and <see cref="Resources"/> whose members are
        /// spatial (a gantry crane on axes), each cycle is done by the crane — it travels to this location, loads
        /// the item (the pick point frees), travels to the chosen position's <see cref="StackPosition.Location"/>
        /// and unloads it (the item is placed). <see cref="CycleTime"/> is not used then. Null = cycle by time.
        /// </summary>
        public string PickLocation { get; set; }

        private bool CraneMode => PickLocation != null && Resources != null;

        /// <summary>Item type name of the released stacks.</summary>
        public string StackType { get; set; } = "Stack";

        /// <summary>Copy the labels of the stack's first item to the compound (default true).</summary>
        public bool CopyLabelsFromFirst { get; set; } = true;

        // ── events (views; never needed for the logic) ──────────────────────

        /// <summary>An item reached the pick point.</summary>
        public event Action<Stacker, Item> ItemArrived;
        /// <summary>A pick-and-place cycle started (the item left the pick point).</summary>
        public event Action<Stacker, StackerCycle> CycleStarted;
        /// <summary>An item was placed: (stacker, item, position, level).</summary>
        public event Action<Stacker, Item, StackPosition, int> ItemPlaced;
        /// <summary>A stack completed; <see cref="StackPosition.Compound"/> is built and about to be sent.</summary>
        public event Action<Stacker, StackPosition> StackCompleted;
        /// <summary>A complete stack left through the output link: (stacker, position now empty, compound).</summary>
        public event Action<Stacker, StackPosition, Item> StackReleased;

        // ── construction ─────────────────────────────────────────────────────

        /// <param name="positionCount">Number of stacking positions (≥ 1).</param>
        /// <param name="capacity">Items per stack (≥ 1).</param>
        public Stacker(string name, SimClock clock, int positionCount, int capacity,
                       InputStrategy inputStrategy = null, IVElement vElement = null)
            : base(name, clock, vElement)
        {
            if (positionCount < 1) throw new ArgumentException("At least one stack position is needed.", nameof(positionCount));
            if (capacity < 1) throw new ArgumentException("The stack capacity must be at least 1.", nameof(capacity));
            positions = new StackPosition[positionCount];
            for (int i = 0; i < positionCount; i++) positions[i] = new StackPosition(i);
            closeAfterPlace = new bool[positionCount];
            Capacity = capacity;
            this.inputStrategy = inputStrategy;
            StatsCollector.CountSubItemsOnExit = true;   // the compound takes its stacked items out (Link-driven)
        }

        // ── read-only views ──────────────────────────────────────────────────

        public IReadOnlyList<StackPosition> Positions => positions;
        public int PositionCount => positions.Length;

        /// <summary>Item waiting at the pick point (null = free).</summary>
        public Item PickItem => pickItem;

        /// <summary>Item being moved by the running cycle (null = no cycle).</summary>
        public Item CarriedItem => carried;

        /// <summary>The running cycle (null = none).</summary>
        public StackerCycle CurrentCycle => currentCycle;

        public bool CycleInProgress => carried != null;

        /// <summary>Complete stacks waiting to leave (completion order).</summary>
        public int WaitingStacks => releaseQueue.Count;

        /// <summary>Items placed on stacks since <see cref="Start"/>.</summary>
        public int ItemsStacked { get; private set; }

        /// <summary>Stacks released since <see cref="Start"/> (all reasons).</summary>
        public int StacksReleased { get; private set; }

        /// <summary>Released stacks that were not full (<c>height</c>, <c>forced</c> or <c>blocked</c>).</summary>
        public int PartialStacksReleased { get; private set; }

        /// <summary>Position holding <paramref name="item"/> on a stack (−1 if none).</summary>
        public int PositionOf(Item item)
        {
            foreach (var p in positions) if (p.items.Contains(item)) return p.Index;
            return -1;
        }

        /// <summary>Item capacity of a position (its own value or <see cref="Capacity"/>).</summary>
        public int CapacityOf(StackPosition p) => p.Capacity > 0 ? p.Capacity : Math.Max(1, Capacity);

        /// <summary>Height limit of a position (its own value or <see cref="MaxHeight"/>; ≤ 0 = none).</summary>
        public double MaxHeightOf(StackPosition p) => double.IsNaN(p.MaxHeight) ? MaxHeight : p.MaxHeight;

        /// <summary>Height of an item by the configured rule (strategy → label → default; 0 = unknown).</summary>
        public double HeightOf(Item item)
        {
            double h = 0;
            if (item != null)
            {
                if (ItemHeight != null) h = ItemHeight(item);
                if (!(h > 0) && !string.IsNullOrEmpty(HeightLabel)) h = item.GetLabelOrDefault(HeightLabel, 0);
            }
            return h > 0 ? h : Math.Max(0, DefaultItemHeight);
        }

        /// <summary>Stack key of an item (<see cref="KeyLabel"/> value as text, or the item type).</summary>
        public string KeyOf(Item item)
        {
            if (item == null) return "";
            if (string.IsNullOrEmpty(KeyLabel)) return item.GetTypeName() ?? "";
            return item.GetLabelText(KeyLabel) ?? "";
        }

        /// <summary>True if position <paramref name="index"/> can take an item of height <paramref name="h"/> now.</summary>
        public bool Fits(int index, double h)
        {
            var p = positions[index];
            if (p.IsComplete || p.Count >= CapacityOf(p)) return false;
            double maxH = MaxHeightOf(p);
            if (maxH > 0 && h > 0)
            {
                if (p.Count == 0) return AcceptOversizeOnEmpty || h <= maxH + Eps;
                return p.Height + h <= maxH + Eps;
            }
            return true;
        }

        // ── Element contract ─────────────────────────────────────────────────

        /// <summary>Resources each pick-and-place cycle needs (requested when a cycle can start, released at its
        /// end); null = none. While waiting the stacker shows WAITING_FOR_RESOURCE and the item stays at the pick point.</summary>
        public SimuLean.Resources.StationResources Resources { get; set; }
        private SimuLean.Resources.ResourceJob cycleJob;

        private bool awaitingOperator;   // request issued, not granted yet
        private bool operatorHeld;       // granted, used by the next / current cycle

        private void OnOperatorGranted()
        {
            awaitingOperator = false;
            operatorHeld = true;
            TryStartCycle();
        }

        public override void Start()
        {
            awaitingOperator = false; operatorHeld = false; cycleJob = null;
            foreach (var p in positions) p.Clear();
            Array.Clear(closeAfterPlace, 0, closeAfterPlace.Length);
            releaseQueue.Clear();
            pickItem = null; carried = null; carriedTarget = -1; currentCycle = null;
            kickPending = false; rrNext = 0; stackSeq = 0;
            ItemsStacked = 0; StacksReleased = 0; PartialStacksReleased = 0;
            base.Start();
        }

        public override bool CheckAvailability(Item theItem) => pickItem == null;

        public override bool Receive(Item theItem)
        {
            if (pickItem != null || theItem == null) return false;
            pickItem = theItem;
            vElement.LoadItem(theItem);
            ItemArrived?.Invoke(this, theItem);
            // Placement always runs as its own (same-time) event, never inside the link's SendItem:
            // the item's entry is recorded first, and a stack never leaves from within Receive.
            if (!kickPending && carried == null)
            {
                kickPending = true;
                ScheduleWork(new ActionEvent(Kick), 0);
            }
            UpdateState();
            return true;
        }

        /// <summary>Retries the complete stacks waiting to leave (completion order), then the waiting item.</summary>
        public override bool Unblock()
        {
            bool released = ReleaseWaiting();
            if (carried == null && pickItem != null && !kickPending) TryStartCycle();
            else UpdateState();
            return released;
        }

        public override int GetQueueLength()
        {
            int n = (pickItem != null ? 1 : 0) + (carried != null ? 1 : 0);
            foreach (var p in positions) n += p.Count;
            return n;
        }

        /// <summary>1 when the pick point is free, else 0.</summary>
        public override int GetFreeCapacity() => pickItem == null ? 1 : 0;

        // ── control ──────────────────────────────────────────────────────────

        /// <summary>
        /// Closes the stack on <paramref name="position"/> now (even if partial) and sends it when the output accepts.
        /// If the running cycle targets that position, the stack closes right after the item is placed.
        /// Returns false if the position is empty.
        /// </summary>
        public bool ReleaseStack(int position)
        {
            if (position < 0 || position >= positions.Length) throw new ArgumentOutOfRangeException(nameof(position));
            if (position == carriedTarget) { closeAfterPlace[position] = true; return true; }
            var p = positions[position];
            if (p.IsComplete) return true;
            if (p.Count == 0) return false;
            Complete(p, "forced");
            ReleaseWaiting();
            if (carried == null && pickItem != null && !kickPending) TryStartCycle();
            else UpdateState();
            return true;
        }

        /// <summary>Closes every non-empty stack (end of a batch / of the run). Returns how many were closed.</summary>
        public int ReleaseAll()
        {
            int n = 0;
            for (int i = 0; i < positions.Length; i++)
                if (!positions[i].IsComplete && (positions[i].Count > 0 || i == carriedTarget) && ReleaseStack(i)) n++;
            return n;
        }

        // ── cycle ────────────────────────────────────────────────────────────

        private void Kick()
        {
            kickPending = false;
            TryStartCycle();
        }

        private void TryStartCycle()
        {
            if (pickItem == null || carried != null) { UpdateState(); return; }
            double h = HeightOf(pickItem);
            int p = Choose(pickItem, h, out int blocker);
            if (p < 0 && blocker >= 0 && ReleasePartialWhenBlocked)
            {
                Complete(positions[blocker], "blocked");
                ReleaseWaiting();
                p = Choose(pickItem, h, out _);
            }
            if (p < 0) { UpdateState(); return; }   // waits: retried on Unblock / ReleaseStack

            if (CraneMode)
            {
                if (!awaitingOperator) { awaitingOperator = true; UpdateState(); RequestCrane(); }
                return;
            }

            if (Resources != null && !operatorHeld)
            {
                if (!awaitingOperator)
                {
                    awaitingOperator = true;
                    UpdateState();
                    cycleJob = Resources.Acquire(this, pickItem, OnOperatorGranted);   // may grant (and re-enter) at once
                }
                return;
            }

            Item item = pickItem;
            pickItem = null;
            carried = item;
            carriedTarget = p;
            var pos = positions[p];
            double dt = SampleCycle(item);
            currentCycle = new StackerCycle(item, p, pos.Count, simClock.GetSimulationTime(), dt, h, pos.Height);
            ScheduleWork(new ActionEvent(FinishCycle), dt);
            UpdateState();
            CycleStarted?.Invoke(this, currentCycle);
            vElement.ReportState("Cycle");
            NotifyInput();   // the pick point is free again
        }

        // Crane mode: ask for the crane; the target position is chosen when it is assigned.
        private void RequestCrane()
        {
            var request = new ResourceRequest(this, TaskType.Transport, (r, got) => CraneAssigned(got),
                                              new List<ResourceRequirement>(Resources.Requirements).ToArray());
            request.Priority = Resources.Priority?.Invoke(pickItem) ?? 0;
            request.Location = PickLocation;
            Resources.Dispatcher.Request(request);
        }

        private void CraneAssigned(IReadOnlyList<TaskExecutor> got)
        {
            awaitingOperator = false;
            if (pickItem == null || carried != null) { UpdateState(); return; }   // nothing to do: the crane goes back
            double h = HeightOf(pickItem);
            int p = Choose(pickItem, h, out int blocker);
            if (p < 0 && blocker >= 0 && ReleasePartialWhenBlocked)
            {
                Complete(positions[blocker], "blocked");
                ReleaseWaiting();
                p = Choose(pickItem, h, out _);
            }
            if (p < 0) { UpdateState(); return; }                                 // retried on Unblock / ReleaseStack
            var pos = positions[p];
            if (pos.Location == null)
                throw new InvalidOperationException($"Stacker '{GetName()}': position {p} has no location for the crane.");

            Item item = pickItem;
            carried = item;
            carriedTarget = p;
            var crane = got[0];
            double dt = crane.LoadTime + crane.UnloadTime;   // expected cycle (views); the crane's steps decide
            if (crane.TravelModel != null)
                dt += SafeTime(crane, crane.Location ?? PickLocation, PickLocation) + SafeTime(crane, PickLocation, pos.Location);
            currentCycle = new StackerCycle(item, p, pos.Count, simClock.GetSimulationTime(), dt, h, pos.Height);
            var task = new TaskSequence(TaskType.Transport, this, "Stack " + item.GetId())
                .Travel(PickLocation)
                .Load(item, null, _ => { pickItem = null; NotifyInput(); })   // the pick point is free again
                .Travel(pos.Location)
                .Unload(item, null, _ => FinishCycle());
            for (int k = 1; k < got.Count; k++)                 // helpers accompany the crane
            {
                var helper = got[k];
                helper.Execute(new TaskSequence(TaskType.Transport, this, "Help " + item.GetId()).Hold(ResourceStates.TRAVELLING_LOADED));
                task.Completed += _ => helper.Continue();
            }
            UpdateState();
            CycleStarted?.Invoke(this, currentCycle);
            vElement.ReportState("Cycle");
            crane.Execute(task);
        }

        private static double SafeTime(TaskExecutor crane, string from, string to)
        {
            if (from == to) return 0;
            var plan = crane.TravelModel.Plan(crane, from, to);
            return double.IsNaN(plan.Duration) ? 0 : plan.Duration;
        }

        private void FinishCycle()
        {
            Item item = carried;
            int p = carriedTarget;
            carried = null; carriedTarget = -1; currentCycle = null;
            if (operatorHeld)
            {
                operatorHeld = false;
                Resources?.ProcessEnded(cycleJob);   // a cycle's resources are released at its end
                Resources?.ItemLeft(cycleJob);
                cycleJob = null;
            }
            if (item == null) { UpdateState(); return; }

            var pos = positions[p];
            if (pos.Count == 0) { pos.StartedAt = simClock.GetSimulationTime(); pos.Key = KeyOf(item); }
            double h = HeightOf(item);
            int level = pos.Count;
            pos.items.Add(item);
            pos.heights.Add(h);
            pos.Height += h;
            ItemsStacked++;
            ItemPlaced?.Invoke(this, item, pos, level);
            vElement.ReportState("Placed");

            double maxH = MaxHeightOf(pos);
            string reason = null;
            if (pos.Count >= CapacityOf(pos)) reason = "full";
            else if (maxH > 0 && h > 0 && pos.Height + h > maxH + Eps) reason = "height";
            else if (closeAfterPlace[p]) reason = "forced";
            closeAfterPlace[p] = false;
            if (reason != null) Complete(pos, reason);

            ReleaseWaiting();
            TryStartCycle();
        }

        private double SampleCycle(Item item)
        {
            if (CycleTime == null) return 0;
            double d = SimuLean.Expressions.Samplers.Sample(CycleTime, item, this);
            return double.IsNaN(d) || d < 0 ? 0 : d;
        }

        // ── position choice ──────────────────────────────────────────────────

        /// <summary>Target position for the item (−1 = none now); <paramref name="blocker"/> = open stack whose
        /// closing would make room (−1 = none).</summary>
        private int Choose(Item item, double h, out int blocker)
        {
            blocker = -1;
            int n = positions.Length;

            if (PositionSelector != null)
            {
                int i = PositionSelector(item, this);
                if (i >= 0 && i < n)
                {
                    if (Fits(i, h)) return i;
                    if (!positions[i].IsComplete && positions[i].Count > 0) blocker = i;
                }
                return -1;
            }

            switch (SelectionRule)
            {
                case StackSelectionRule.ByLabelIndex:
                {
                    double? v = string.IsNullOrEmpty(PositionLabel) ? null : item.GetLabelValue(PositionLabel);
                    if (v.HasValue)
                    {
                        int i = (int)Math.Round(v.Value);
                        if (i >= 0 && i < n)
                        {
                            if (Fits(i, h)) return i;
                            if (!positions[i].IsComplete && positions[i].Count > 0) blocker = i;
                            return -1;
                        }
                    }
                    goto default;   // missing / out of range: first not full
                }

                case StackSelectionRule.ByKey:
                {
                    string key = KeyOf(item);
                    for (int i = 0; i < n; i++)
                    {
                        var p = positions[i];
                        if (p.IsComplete || p.Count == 0 || p.Key != key) continue;
                        if (Fits(i, h)) return i;
                        blocker = i;   // its own stack is in the way (height)
                        return -1;
                    }
                    for (int i = 0; i < n; i++)
                        if (positions[i].Count == 0 && Fits(i, h)) return i;
                    blocker = OldestOpen();
                    return -1;
                }

                case StackSelectionRule.RoundRobin:
                    for (int k = 0; k < n; k++)
                    {
                        int i = (rrNext + k) % n;
                        if (!Fits(i, h)) continue;
                        rrNext = (i + 1) % n;
                        return i;
                    }
                    blocker = OldestOpen();
                    return -1;

                default:
                    for (int i = 0; i < n; i++)
                        if (Fits(i, h)) return i;
                    blocker = OldestOpen();
                    return -1;
            }
        }

        /// <summary>Open, non-empty position whose stack started first (ties: lowest index); −1 if none.</summary>
        private int OldestOpen()
        {
            int best = -1;
            for (int i = 0; i < positions.Length; i++)
            {
                var p = positions[i];
                if (p.IsComplete || p.Count == 0) continue;
                if (best < 0 || p.StartedAt < positions[best].StartedAt) best = i;
            }
            return best;
        }

        // ── completion and release ───────────────────────────────────────────

        private void Complete(StackPosition pos, string reason)
        {
            if (pos.IsComplete || pos.Count == 0) return;
            var stack = new Item(simClock.GetSimulationTime());
            stack.SetId(StackType, ++stackSeq, pos.items[0].priority);
            if (CopyLabelsFromFirst) stack.CopyLabelsFrom(pos.items[0]);
            stack.SetLabelValue(LabelPosition, pos.Index);
            stack.SetLabelValue(LabelCount, pos.Count);
            stack.SetLabelValue(LabelHeight, pos.Height);
            stack.SetLabelText(LabelKey, pos.Key ?? "");
            stack.SetLabelText(LabelReason, reason);
            foreach (var it in pos.items) stack.AddItem(it);
            pos.Compound = stack;
            pos.CompletionReason = reason;
            releaseQueue.Add(pos);
            StackCompleted?.Invoke(this, pos);
            vElement.ReportState("StackComplete");
        }

        /// <summary>Sends the waiting stacks in completion order while the output accepts them.</summary>
        private bool ReleaseWaiting()
        {
            bool any = false;
            while (releaseQueue.Count > 0)
            {
                var pos = releaseQueue[0];
                Item stack = pos.Compound;
                if (GetOutput() == null || !SendOut(stack)) break;
                releaseQueue.RemoveAt(0);
                if (pos.CompletionReason != "full") PartialStacksReleased++;
                StacksReleased++;
                pos.Clear();
                StackReleased?.Invoke(this, pos, stack);
                vElement.ReportState("StackReleased");
                any = true;
            }
            UpdateState();
            return any;
        }

        private void UpdateState()
        {
            if (carried != null) SetState(ElementState.PROCESSING);
            else if (awaitingOperator) SetState(ElementState.WAITING_FOR_RESOURCE);
            else if (releaseQueue.Count > 0 || (pickItem != null && !kickPending)) SetState(ElementState.BLOCKED);
            else if (pickItem != null || HoldsItems()) SetState(ElementState.RECEIVING);
            else SetState(ElementState.IDLE);
        }

        private bool HoldsItems()
        {
            foreach (var p in positions) if (p.Count > 0) return true;
            return false;
        }

    }
}
