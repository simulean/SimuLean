using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Resources;

namespace SimuLean.Naval
{
    /// <summary>
    /// A slipway or building dock: it
    /// erects ship blocks and keeps them — the hull.
    /// <list type="bullet">
    /// <item><b>Which block it takes</b>: under <see cref="ErectionRule.Sequence"/> only the next block of the erection
    /// sequence (its <c>erect_seq</c> label = the number of blocks started + 1); under
    /// <see cref="ErectionRule.Precedence"/> any block whose <c>erect_after</c> blocks are all erected — several erection
    /// fronts at once, up to <see cref="Capacity"/>. A block it cannot take yet is refused (upstream keeps it: a yard
    /// releasing any accepted item or a list lets the others pass), and the dock asks again after every erection.</item>
    /// <item><b>Erection</b>: the time of its sampler (an expression may read the block's labels, e.g.
    /// <c>2 + 0.05 * weight</c>), with its process resources (crane, erection crew; their own share with
    /// <c>processHoldFor</c>), on its own front; stops pause it as every station's work.</item>
    /// <item><b>The hull</b>: an erected block stays (at rest, <see cref="IKeepsItems"/>). With
    /// <see cref="ExpectedBlocks"/> set, the complete hull leaves as one <b>ship</b> item (the blocks as its sub-items,
    /// each counted as leaving) to the dock's output — a launch / float-out sink — and the dock starts the next hull.
    /// Without an output (or with <see cref="ExpectedBlocks"/> = 0) the hull stays and the makespan is the last
    /// erection.</item>
    /// </list>
    /// Flow statistics come from the link layer: a block enters when the dock takes it.
    /// </summary>
    public sealed class ErectionDock : Element, IWorkStation, IResourceUser, IKeepsItems
    {
        public enum ErectionRule { Sequence, Precedence }

        private readonly IRandomSampler erectionTime;
        private ServerProcess[] fronts;
        private readonly List<Item> hull = new List<Item>();
        private readonly HashSet<int> erected = new HashSet<int>();
        private readonly HashSet<int> started = new HashSet<int>();
        private Item pendingShip;
        private int shipSeq, startedCount;

        public ErectionDock(string name, SimClock clock, IRandomSampler erectionTime, int capacity = 1,
                            ErectionRule rule = ErectionRule.Sequence, IVElement vElement = null) : base(name, clock, vElement)
        {
            this.erectionTime = erectionTime ?? throw new ArgumentNullException(nameof(erectionTime));
            Capacity = Math.Max(1, capacity);
            Rule = rule;
        }

        public int Capacity { get; }
        public ErectionRule Rule { get; }
        /// <summary>Labels read from the blocks.</summary>
        public string BlockLabel { get; set; } = "block";
        public string SequenceLabel { get; set; } = "erect_seq";
        public string PredecessorsLabel { get; set; } = "erect_after";
        /// <summary>Blocks of a complete hull (0 = unknown: the hull never leaves).</summary>
        public int ExpectedBlocks { get; set; }
        /// <summary>Item name (type) of a launched ship.</summary>
        public string ShipName { get; set; } = "Ship";

        public StationResources Resources { get; set; }

        /// <summary>The erection fronts (one process each).</summary>
        public IReadOnlyList<ServerProcess> Fronts => fronts;
        /// <summary>The erected blocks of the current hull, in erection order.</summary>
        public IReadOnlyList<Item> Hull => hull;
        public int ErectedCount => hull.Count;
        /// <summary>Ships launched (complete hulls sent out) since the start.</summary>
        public int ShipsLaunched { get; private set; }
        /// <summary>When the last block was erected (NaN: none yet).</summary>
        public double LastErectionTime { get; private set; } = double.NaN;

        public int KeptCount => hull.Count;
        public double LastKeptTime => LastErectionTime;

        public override void Start()
        {
            base.Start();
            fronts = new ServerProcess[Capacity];
            for (int k = 0; k < Capacity; k++)
            {
                fronts[k] = new ServerProcess(this, erectionTime, simClock, k);
                fronts[k].SetState(ElementState.IDLE);
            }
            hull.Clear(); erected.Clear(); started.Clear();
            pendingShip = null; shipSeq = 0; startedCount = 0; ShipsLaunched = 0;
            LastErectionTime = double.NaN;
            StatsCollector.CountSubItemsOnExit = true;   // a launched ship: each block leaves (content / stay balanced)
        }

        public override void ResetStatistics()
        {
            base.ResetStatistics();
            if (fronts != null) foreach (var f in fronts) f.ResetStatistics();
        }

        /// <summary>Mean share of time the fronts erect.</summary>
        public double GetUtilization() => fronts == null || fronts.Length == 0 ? 0 : fronts.Average(f => f.GetStateRatio(ElementState.PROCESSING));

        // ------------------------------------------------------------------ admission

        /// <summary>True when <paramref name="block"/> may be erected now (its turn / its predecessors), whatever the fronts.</summary>
        public bool Erectable(Item block)
        {
            if (block == null || pendingShip != null) return false;
            if (!TryNumber(block.GetLabelValue(BlockLabel), out int number) || started.Contains(number)) return false;
            if (Rule == ErectionRule.Sequence)
                return TryNumber(block.GetLabelValue(SequenceLabel), out int seq) ? seq == startedCount + 1 : true;
            foreach (int p in Predecessors(block))
                if (!erected.Contains(p)) return false;
            return true;
        }

        private IEnumerable<int> Predecessors(Item block)
        {
            // a number label (one predecessor) or a text "4 5" / "4,5" / "4;5" (several)
            string text = block.GetLabelText(PredecessorsLabel);
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (var tok in text.Split(new[] { ' ', ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                if (TryNumber(tok, out int p)) yield return p;
        }

        private ServerProcess FreeFront() => fronts?.FirstOrDefault(f => f.GetState() == ElementState.IDLE);

        public override bool CheckAvailability(Item theItem) => FreeFront() != null && Erectable(theItem);

        public override int GetFreeCapacity() => pendingShip != null || fronts == null ? 0 : fronts.Count(f => f.GetState() == ElementState.IDLE);

        public override int GetQueueLength() => hull.Count + (fronts?.Count(f => f.GetState() != ElementState.IDLE) ?? 0);

        public override bool Receive(Item theItem)
        {
            var front = FreeFront();
            if (front == null || !Erectable(theItem)) return false;
            TryNumber(theItem.GetLabelValue(BlockLabel), out int number);
            started.Add(number);
            startedCount++;
            front.SetItem(theItem);
            vElement.LoadItem(theItem);
            if (Resources != null)
            {
                SetFront(front, ElementState.WAITING_FOR_RESOURCE);
                front.job = Resources.Acquire(this, theItem, () => ((IWorkStation)this).StartProcessing(front));
            }
            else ((IWorkStation)this).StartProcessing(front);
            return true;
        }

        // ------------------------------------------------------------------ erection

        void IWorkStation.StartProcessing(ServerProcess front)
        {
            var block = front.GetItem();
            double time = SimuLean.Expressions.Samplers.Sample(erectionTime, block, this);
            front.loadTime = simClock.GetSimulationTime();
            front.lastDelay = time;
            SetFront(front, ElementState.PROCESSING);
            vElement.UnloadItem(block);
            ScheduleWork(front, time);
        }

        void IWorkStation.CompleteServerProcess(ServerProcess front)
        {
            var block = front.GetItem();
            Resources?.ProcessEnded(front.job);
            Resources?.ItemLeft(front.job);          // the block stays in the hull: its resources are free now
            front.job = null;
            hull.Add(block);
            if (TryNumber(block.GetLabelValue(BlockLabel), out int number)) erected.Add(number);
            LastErectionTime = simClock.GetSimulationTime();
            SetFront(front, ElementState.IDLE);
            vElement.ReportState("Erected");
            if (ExpectedBlocks > 0 && hull.Count >= ExpectedBlocks) Launch();
            NotifyInput();                            // new blocks may be erectable, a front is free
        }

        // the complete hull leaves as one ship item (its blocks as sub-items); kept here while the output refuses it
        private void Launch()
        {
            var ship = new Item(simClock.GetSimulationTime());
            ship.SetId(ShipName, ++shipSeq, 0);
            ship.SetLabelValue("ship", shipSeq);
            ship.SetLabelValue("blocks", hull.Count);
            foreach (var b in hull) ship.AddItem(b);
            ship.vItem = vElement.GenerateItem(shipSeq);   // the view gathers the hull's blocks into the ship's visual
            pendingShip = ship;
            TrySendShip();
        }

        private bool TrySendShip()
        {
            if (pendingShip == null || !SendOut(pendingShip)) { if (pendingShip != null) SetState(ElementState.BLOCKED); return false; }
            pendingShip = null;
            ShipsLaunched++;
            hull.Clear(); erected.Clear(); started.Clear(); startedCount = 0;   // the next hull
            vElement.ReportState("Launched");
            UpdateState();
            return true;
        }

        public override bool Unblock()
        {
            if (pendingShip == null) return false;
            if (!TrySendShip()) return false;
            NotifyInput();
            return true;
        }

        public override bool SendHeldItem(Item item) => item != null && item == pendingShip && TrySendShip();

        // ------------------------------------------------------------------ states

        private void SetFront(ServerProcess front, ElementState state)
        {
            front.SetState(state);
            UpdateState();
        }

        // the dock's state from its fronts: erecting > waiting for resources > (ship waiting to leave) > idle
        private void UpdateState()
        {
            if (fronts == null) return;
            if (fronts.Any(f => f.GetState() == ElementState.PROCESSING)) SetState(ElementState.PROCESSING);
            else if (fronts.Any(f => f.GetState() == ElementState.WAITING_FOR_RESOURCE)) SetState(ElementState.WAITING_FOR_RESOURCE);
            else if (pendingShip != null) SetState(ElementState.BLOCKED);
            else SetState(ElementState.IDLE);
        }

        /// <summary>The fronts account the dock's stop state while it is effective (utilisation without stopped time).</summary>
        protected override void OnStopShown(ElementState? stop)
        {
            if (fronts != null) foreach (var f in fronts) f.ShowStop(stop);
        }

        protected override void OnResumed(StopToken token)
        {
            if (!ResumedWork(token) || fronts == null) return;
            foreach (var f in fronts)
                if (f.GetState() == ElementState.PROCESSING) f.loadTime += PauseShiftFor(f.loadTime);
        }

        private static bool TryNumber(object v, out int n)
        {
            n = 0;
            switch (v)
            {
                case null: return false;
                case double d: n = (int)Math.Round(d); return true;
                case int i: n = i; return true;
                default:
                    string t = Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
                    if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) { n = (int)Math.Round(x); return true; }
                    return false;
            }
        }
    }
}
