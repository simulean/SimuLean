using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// A source driven by external logic: controllers (planners, order-release
    /// rules, initial-stock loaders) <see cref="Enqueue"/> ready-made items, and the
    /// source pushes them downstream in FIFO order as soon as the downstream element
    /// accepts them. When downstream is full the head item waits and is retried on the
    /// standard <see cref="Unblock"/> notification, so a finite downstream buffer acts
    /// as a kanban limit on released work.
    ///
    /// <para>Items leave through the output link, so statistics are recorded as for any
    /// other element (content stays 0, see <see cref="Statistics.ElementStatsCollector.TracksContent"/>).
    /// Pending (not yet released) items are counted by <see cref="PendingCount"/>, not as content.</para>
    /// </summary>
    public class ReleaseSource : Element
    {
        private Queue<Item> pending = new Queue<Item>();

        /// <summary>Items released downstream since <see cref="Start"/>.</summary>
        public int ReleasedCount { get; private set; }

        /// <summary>Items waiting to be released.</summary>
        public int PendingCount => pending.Count;

        /// <summary>Raised when an item is released downstream.</summary>
        public event System.Action<ReleaseSource, Item> Released;

        public ReleaseSource(string name, SimClock clock, IVElement vElement = null) : base(name, clock, vElement)
        {
            StatsCollector.TracksContent = false;   // generator: released items never entered (content stays 0)
        }

        public override void Start()
        {
            pending.Clear();
            ReleasedCount = 0;
            base.Start();
        }

        /// <summary>Adds an item at the end of the release queue and tries to release.</summary>
        public void Enqueue(Item item)
        {
            if (item == null) return;
            pending.Enqueue(item);
            TryRelease();
        }

        /// <summary>Adds several items (in order) and tries to release.</summary>
        public void Enqueue(IEnumerable<Item> items)
        {
            foreach (var it in items) if (it != null) pending.Enqueue(it);
            TryRelease();
        }

        /// <summary>Removes every pending item (e.g. when a plan is replaced) and returns them.</summary>
        public List<Item> ClearPending()
        {
            var list = new List<Item>(pending);
            pending.Clear();
            UpdateState();
            return list;
        }

        /// <summary>Snapshot of the pending items in release order.</summary>
        public IReadOnlyCollection<Item> Pending => pending;

        private bool TryRelease()
        {
            bool any = false;
            ILink output = GetOutput();
            while (pending.Count > 0 && output != null)
            {
                Item head = pending.Peek();
                if (!output.SendItem(head, this)) break;
                pending.Dequeue();
                ReleasedCount++;
                any = true;
                vElement.ReportState("Exit");
                Released?.Invoke(this, head);
            }
            // Pushing to a list: every pending item is advertised, not only the head.
            if (output is ListLink list)
                foreach (var it in pending) list.Advertise(it, this);
            UpdateState();
            return any;
        }

        /// <summary>Hands over a specific pending item (pulled from a list), wherever it is in the queue.</summary>
        public override bool SendHeldItem(Item item)
        {
            if (!pending.Contains(item) || GetOutput() == null || !SendOut(item))
                return false;
            var rest = new Queue<Item>(pending.Count);
            foreach (var it in pending) if (it != item) rest.Enqueue(it);
            pending = rest;
            ReleasedCount++;
            vElement.ReportState("Exit");
            Released?.Invoke(this, item);
            UpdateState();
            return true;
        }

        private void UpdateState()
            => SetState(pending.Count == 0 ? ElementState.IDLE : ElementState.BLOCKED);

        public override bool Unblock() => TryRelease();

        public override int GetQueueLength() => 0;
        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item theItem) => false;
        public override bool Receive(Item theItem) => false;
    }
}
