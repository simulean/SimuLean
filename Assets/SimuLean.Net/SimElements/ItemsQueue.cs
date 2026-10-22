using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Standard queue: items leave in arrival order (<see cref="ReleaseMode.FIFO"/>), or any item a receiver accepts
    /// (<see cref="ReleaseMode.AnyAccepted"/>). Supports input validation through InputStrategy.
    /// </summary>
    public class ItemsQueue : Element
    {
        int capacity;
        int currentItems;

        // Held items, oldest first, each findable and removable in O(1): "any accepted" release offers and takes any of
        // them (it copied and rebuilt the whole queue per item, n² per attempt).
        readonly LinkedList<Item> itemsQ = new LinkedList<Item>();
        readonly Dictionary<Item, LinkedListNode<Item>> nodes = new Dictionary<Item, LinkedListNode<Item>>();

        /// <param name="vElement">The view (null = headless).</param>
        public ItemsQueue(int capacity, String myName, SimClock sClock, InputStrategy inputStrategy = null, IVElement vElement = null) 
            : base(myName, sClock, vElement)
        {
            this.capacity = capacity;
            this.inputStrategy = inputStrategy;

        }

        public override void Start()
        {
            itemsQ.Clear();
            nodes.Clear();
            currentItems = 0;
            base.Start();
        }

        override public int GetQueueLength()
        {
            return currentItems;
        }
        override public int GetFreeCapacity()
        {
            return capacity - currentItems;
        }

        /// <summary>How the held items are offered downstream (default FIFO).</summary>
        public ReleaseMode Release { get; set; } = ReleaseMode.FIFO;
        /// <summary><see cref="ReleaseMode.AnyAccepted"/>: items offered per attempt, oldest first (0 = every held item).</summary>
        public int ReleaseScan { get; set; }

        public override bool Unblock()
        {
            if (itemsQ.Count > 0 && Release == ReleaseMode.AnyAccepted)
            {
                // Offer each held item, oldest first, until a receiver accepts one (bounded by ReleaseScan).
                int offered = 0;
                int n = ReleaseScan > 0 ? Math.Min(ReleaseScan, itemsQ.Count) : itemsQ.Count;
                var candidates = new Item[n];
                var node = itemsQ.First;
                for (int k = 0; k < n && node != null; k++, node = node.Next) candidates[k] = node.Value;
                foreach (Item candidate in candidates)
                {
                    if (candidate == null) break;
                    offered++;
                    if (!nodes.ContainsKey(candidate)) continue;   // left meanwhile (a nested send)
                    if (SendHeldItem(candidate)) return true;
                }
                SetState(ElementState.BLOCKED);
                return false;
            }
            if (itemsQ.Count > 0 && OutputRequestsCarriers)
                return OfferToCarriers();
            if (itemsQ.Count > 0)
            {
                // Peek first: a refused head item must keep its place (FIFO).
                Item theItem = itemsQ.First.Value;

                if (!SendOut(theItem))
                {
                    SetState(ElementState.BLOCKED);  // tried to send, output refused
                    return false;
                }
                Take(theItem);

                currentItems--;
                vElement.ReportState("Exit");
                NotifyInput();

                SetState(currentItems == 0 ? ElementState.IDLE : ElementState.PROCESSING);
                return true;
            }
            else if (capacity == 0)
            {
                return NotifyInput();
            }
            else
            {
                return false;
            }
        }

        public override bool Receive(Item theItem)
        {
            // Note: Validation with origin context is done in GeneralLink.SendItem
            // This method assumes the item has already passed validation
            if (currentItems < capacity)
            {
                // FIFO: pass straight through only when nobody is waiting; AnyAccepted: a new item may leave at once
                // if a receiver takes it (the waiting ones were refused).
                bool waiting = HasItemToOffer();
                if ((waiting && Release == ReleaseMode.FIFO) || !SendOut(theItem))
                {
                    nodes[theItem] = itemsQ.AddLast(theItem);
                    currentItems++;
                    vElement.LoadItem(theItem);
                    SetState(ElementState.PROCESSING);   // item sitting in queue
                    // Pushing to a list: every item is advertised, not only the head.
                    if (waiting && GetOutput() is ListLink list) list.Advertise(theItem, this);
                }
                // if SendItem succeeded the item passed straight through — no state change
                return true;
            }
            else if (capacity == 0)
            {
                return SendOut(theItem);
            }
            else
            {
                return false;
            }
        }

        /// <summary>Hands over a specific held item (pulled from a list), wherever it is in the queue.</summary>
        public override bool SendHeldItem(Item item)
        {
            if (item == null || !nodes.ContainsKey(item) || !SendOut(item))
                return false;

            Take(item);
            currentItems--;
            vElement.ReportState("Exit");
            SetState(currentItems == 0 ? ElementState.IDLE : ElementState.PROCESSING);
            NotifyInput();
            return true;
        }

        public override bool CheckAvailability(Item theItem)
        {
            if (currentItems >= capacity)
                return false;
            
            return true;   // admission: the input strategy, checked by the link (CanAccept)
        }

        /// <summary>The held items, oldest first: a live read-only view (never replaced).</summary>
        public IReadOnlyCollection<Item> GetItems() => itemsQ;

        // Behind a carrier link every held item gets its own carrier, in order: the items whose carrier is
        // already requested are passed over; the first one refused (no destination with room) keeps the rest waiting.
        private bool OfferToCarriers()
        {
            bool left = false;
            foreach (Item candidate in new List<Item>(itemsQ))
            {
                if (!nodes.ContainsKey(candidate) || AwaitsCarrier(candidate)) continue;
                if (SendHeldItem(candidate)) return true;
                if (!nodes.ContainsKey(candidate)) { left = true; continue; }   // loaded inside its own request
                if (!AwaitsCarrier(candidate)) break;
            }
            if (!left) SetState(ElementState.BLOCKED);
            return left;
        }

        // An earlier item still to be offered (FIFO: a new item queues behind it). Items waiting for their carrier have
        // been offered already.
        private bool HasItemToOffer()
        {
            if (!OutputRequestsCarriers) return itemsQ.Count > 0;
            foreach (Item it in itemsQ) if (!AwaitsCarrier(it)) return true;
            return false;
        }

        private void Take(Item item)
        {
            if (nodes.TryGetValue(item, out var node)) { itemsQ.Remove(node); nodes.Remove(item); }
        }

        public void SetCapacity(int capacity)
        {
            this.capacity = capacity;
        }
    }
}
