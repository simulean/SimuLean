using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Outbound port through which a <see cref="PickingStation"/> sends the
    /// non-picked (surplus) items back into the model. Wire its output link to
    /// wherever returns should go (storage, a queue, a sink). If left unwired,
    /// returned items are discarded and counted in <see cref="DiscardedCount"/>.
    /// Sending through this port keeps link-layer statistics correct.
    /// </summary>
    public class ReturnsPort : Element
    {
        private readonly Queue<Item> pending = new Queue<Item>();

        /// <summary>Items dropped because no returns link was wired.</summary>
        public int DiscardedCount { get; private set; }

        public ReturnsPort(string name, SimClock simClock, IVElement vElement = null)
            : base(name, simClock, vElement)
        {
        }

        public override void Start()
        {
            base.Start();
            pending.Clear();
            DiscardedCount = 0;
        }

        /// <summary>Queues an item for return and tries to push it out.</summary>
        public void Dispatch(Item theItem)
        {
            pending.Enqueue(theItem);
            TryFlush();
        }

        /// <summary>Pushes pending returns until the downstream blocks. True when drained.</summary>
        public bool TryFlush()
        {
            while (pending.Count > 0)
            {
                ILink outLink = GetOutput();
                if (outLink == null)
                {
                    // No returns destination wired — discard (counted).
                    pending.Dequeue();
                    DiscardedCount++;
                    continue;
                }

                if (outLink.SendItem(pending.Peek(), this))
                    pending.Dequeue();
                else
                    return false;   // downstream blocked; retried on Unblock()
            }
            return true;
        }

        public override int  GetQueueLength()               => pending.Count;
        public override int  GetFreeCapacity()              => int.MaxValue;
        public override bool CheckAvailability(Item theItem) => false;   // outbound-only port
        public override bool Receive(Item theItem)           => false;
        public override bool Unblock()                       => TryFlush();
    }

    /// <summary>
    /// Picking / kitting station. A container (order) item arrives on the main
    /// input; stock items of various SKUs accumulate in a <see cref="SkuStockPort"/>.
    /// The pick list (SKU → qty) for each container is resolved through an
    /// <see cref="IPickListSource"/> chain (e.g. SQLite table → container labels
    /// → static table). When the whole list is satisfiable from stock the
    /// station picks the items into the container (as sub-items), processes for
    /// a sampled delay, and sends the container out (one container at a time,
    /// <see cref="SingleJobStation"/>, optional resources). AFTER each completed pick
    /// cycle, all remaining (non-picked) stock is flushed in batch through the
    /// <see cref="ReturnsPort"/>.
    /// </summary>
    public class PickingStation : SingleJobStation
    {
        private readonly IPickListSource pickListSource;

        private readonly SkuStockPort stockPort;
        private readonly ReturnsPort  returnsPort;

        private Dictionary<string, int> currentPickList;

        /// <param name="delayStrategy">Pick-cycle duration sampler (supports ExpressionSampler on the container).</param>
        /// <param name="pickListSource">Resolves the SKU→qty list per container; null/empty list → container passes through unpicked.</param>
        /// <param name="stockCapacity">Max items held in the stock buffer; negative = unbounded.</param>
        /// <param name="stockStrategy">Optional admission strategy for stock items.</param>
        public PickingStation(
            string name,
            SimClock simClock,
            IRandomSampler delayStrategy,
            IPickListSource pickListSource,
            int stockCapacity = -1,
            InputStrategy stockStrategy = null,
            IVElement vElement = null)
            : base(name, simClock, delayStrategy, vElement)
        {
            this.pickListSource = pickListSource ?? new StaticPickListSource(null);

            stockPort   = new SkuStockPort(name + ".Stock", simClock, stockCapacity,
                                           OnStockArrived, stockStrategy, vElement);
            returnsPort = new ReturnsPort(name + ".Returns", simClock, vElement);
        }

        /// <summary>Stock input port — wire upstream material flows into this element.</summary>
        public SkuStockPort GetStockPort() => stockPort;

        /// <summary>Returns port — wire its output link to the returns destination.</summary>
        public ReturnsPort GetReturnsPort() => returnsPort;

        public override void Start()
        {
            base.Start();
            currentPickList = null;
            stockPort.Start();
            returnsPort.Start();
        }

        // ── main input (containers) ──────────────────────────────────────────

        protected override void OnJobArrived(Item theItem)
        {
            currentPickList = pickListSource.GetPickList(theItem) ?? new Dictionary<string, int>();

            // Let upstream stock flows push while we wait for the pick to fill.
            stockPort.NotifyUpstream();

            TryStartPick();
        }

        // ── stock arrivals ───────────────────────────────────────────────────

        private void OnStockArrived(Item theItem)
        {
            TryStartPick();
        }

        private void TryStartPick()
        {
            if (!IsState(ElementState.RECEIVING) || !stockPort.Has(currentPickList))
                return;

            // Move the picked items into the container.
            Item container = CurrentJob;
            foreach (var kv in currentPickList)
                foreach (var item in stockPort.Draw(kv.Key, kv.Value))
                    container.AddItem(item);

            StartJob();
        }

        /// <summary>The container left: batch-return all non-picked stock (before the slot is free).</summary>
        protected override void OnJobLeaving()
        {
            foreach (var leftover in stockPort.DrainAll())
                returnsPort.Dispatch(leftover);
            currentPickList = null;
        }

        protected override void OnJobLeft() => stockPort.NotifyUpstream();

        public override int GetQueueLength()
            => stockPort.GetQueueLength() + (CurrentJob != null ? 1 : 0);
    }
}
