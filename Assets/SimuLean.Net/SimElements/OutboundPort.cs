using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Outbound port of an element: items its owner hands over (emptied carriers of an <see cref="Separator"/>,
    /// orders an element emits) wait here in FIFO order and leave through the port's own
    /// output link when downstream accepts (retried on <see cref="Unblock"/>). Unwired = items are discarded and
    /// counted (<see cref="DiscardedCount"/>). It accepts nothing from links.
    /// <para><b>Statistics.</b> Two ways in: <see cref="Accept"/> (the owner hands it an item it held, through a
    /// <see cref="DirectLink"/>, so the owner records the exit and the port the entry — content and waiting time are
    /// exact) or <see cref="Dispatch"/> for items created here (orders): with <see cref="Generates"/> the
    /// port is a generator like a source (content not tracked; output counted).</para>
    /// </summary>
    public class OutboundPort : Element
    {
        private readonly Queue<Item> pending = new Queue<Item>();
        private bool accepting;

        /// <param name="generates">True when the port creates its items (orders): content is not tracked.</param>
        public OutboundPort(string name, SimClock simClock, bool generates = false, IVElement vElement = null)
            : base(name, simClock, vElement)
        {
            Generates = generates;
            if (generates) StatsCollector.TracksContent = false;
        }

        public bool Generates { get; }

        /// <summary>Items dropped because the port has no output link.</summary>
        public int DiscardedCount { get; private set; }

        /// <summary>Items that left through the output link.</summary>
        public int SentCount { get; private set; }

        /// <summary>Items waiting to leave (FIFO).</summary>
        public IEnumerable<Item> Pending => pending;

        /// <summary>An item left (sent or discarded): (port, item, discarded).</summary>
        public event Action<OutboundPort, Item, bool> ItemLeft;

        public override void Start()
        {
            base.Start();
            pending.Clear();
            DiscardedCount = 0; SentCount = 0; accepting = false;
        }

        /// <summary>Takes an item its owner held, through a <see cref="DirectLink"/> (statistics), then tries to send it.</summary>
        public bool Accept(Element owner, Item item, DirectLink link)
        {
            accepting = true;
            bool ok;
            try { ok = link.SendItem(item, owner); }
            finally { accepting = false; }
            if (ok) Flush();
            return ok;
        }

        /// <summary>Queues an item created here (an order) and tries to send it.</summary>
        public void Dispatch(Item item)
        {
            if (item == null) return;
            pending.Enqueue(item);
            UpdateState();
            Flush();
        }

        /// <summary>Sends the waiting items while downstream accepts. True when nothing is left.</summary>
        public bool Flush()
        {
            while (pending.Count > 0)
            {
                Item head = pending.Peek();
                if (GetOutput() == null)
                {
                    pending.Dequeue();
                    ConsumeLink.Instance.SendItem(head, this);   // its exit is recorded (link layer)
                    DiscardedCount++;
                    ItemLeft?.Invoke(this, head, true);
                    continue;
                }
                if (!SendOut(head)) { UpdateState(); return false; }
                pending.Dequeue();
                SentCount++;
                ItemLeft?.Invoke(this, head, false);
            }
            UpdateState();
            return true;
        }

        private void UpdateState() => SetState(pending.Count > 0 ? ElementState.BLOCKED : ElementState.IDLE);

        public override int GetQueueLength() => pending.Count;
        public override int GetFreeCapacity() => int.MaxValue;
        /// <summary>Only its owner hands items over (<see cref="Accept"/>); links never deliver to it.</summary>
        public override bool CheckAvailability(Item theItem) => accepting;
        public override bool Receive(Item theItem)
        {
            if (!accepting || theItem == null) return false;
            pending.Enqueue(theItem);
            vElement.LoadItem(theItem);
            return true;
        }
        public override bool Unblock() => Flush();
    }
}
