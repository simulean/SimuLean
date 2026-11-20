using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Standard queue: items leave in arrival order (<see cref="OfferOrder.FIFO"/>), or any item a receiver accepts
    /// (<see cref="OfferOrder.AnyAccepted"/>). Supports input validation through InputStrategy.
    /// <para><b>Gated</b> (<see cref="Gated"/>; it was the separate Gate Queue element): items leave only
    /// against release credits — <see cref="ReleaseItems"/> adds n and sends up to n at once, a credit not used yet is
    /// kept (releasing 5 on an empty queue lets the next 5 arrivals through). Every way out (a plain or a carrier's link,
    /// a list puller) takes a credit when the item leaves; behind carriers no more items ask for one than there are
    /// credits. Items waiting for a credit are not BLOCKED.</para>
    /// <para><b>Grouped by setup</b> (<see cref="GroupBySetup"/>): before its normal release the queue offers
    /// the held items a receiver is already set up for (<see cref="ISetupStation.IsSetUpFor"/>: a station changing over on a
    /// label change whose next slot last had the item's value), oldest first, so that items of one value follow each other
    /// and the station avoids setups; at most <see cref="GroupLimit"/> items in a row overtake the oldest one, then the
    /// normal release offers it (no starvation).</para>
    /// <para><b>Max wait — reneging</b> (<see cref="MaxWait"/>): each queued item's wait (from its entry until it
    /// leaves) may carry a limit. On a timeout the queue counts it (<c>timeouts:queue</c>) and signals it; KeepWaiting goes
    /// on; Reroute (and GiveUp: an item has nothing to give back) sends it to the queue's
    /// <see cref="Element.TimeoutTarget"/> through its own link (a Sink = a lost customer). While the target cannot take it
    /// the item keeps its place in the line (if its turn comes first it leaves normally) and goes as soon as the target has
    /// room. Reroute needs plain connections as the output (not a list or carriers: the build refuses it).</para>
    /// </summary>
    public class ItemsQueue : Element
    {
        int capacity;
        int currentItems;
        // State: IDLE when empty; BLOCKED while it holds items none of which can leave now —
        // behind ordinary links, no destination could take the head (any of the first items with AnyAccepted), checked at
        // every arrival, departure and refusal; behind carriers or other links, from a refused offer until an item leaves;
        // PROCESSING otherwise. Items waiting in a list for a puller are not blocked. It showed PROCESSING after each
        // arrival (≈ 99 % behind a busy station), so BLOCKED time was understated.
        bool refused;
        const int StateScan = 64;   // AnyAccepted: items checked for the state (not for the release)

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
            StatsCollector.KeepStayValues();   // its stay is the wait: stay_p90
        }

        public override void Start()
        {
            if (waitTimers != null) { foreach (var t in waitTimers.Values) t.Cancel(); waitTimers.Clear(); }
            reneging?.Clear();
            itemsQ.Clear();
            nodes.Clear();
            currentItems = 0;
            refused = false;
            credits = 0;
            overtakes = 0;
            base.Start();
        }

        /// <summary>Items leave only against release credits (<see cref="ReleaseItems"/>); false = a plain queue.</summary>
        public bool Gated { get; set; }

        // ── max wait: reneging ──────────────────────────────────

        /// <summary>The max wait of each queued item (null = none); see the class remarks.</summary>
        public WaitLimit MaxWait { get; set; }

        private Dictionary<Item, WaitTimer> waitTimers;
        private List<Item> reneging;   // timed out (Reroute / GiveUp), waiting for the timeout target, oldest first

        /// <summary>Items that timed out and wait for the timeout target (still in the line).</summary>
        public int Reneging => reneging?.Count ?? 0;

        private void StartWait(Item item)
        {
            if (MaxWait == null) return;
            var t = WaitTimer.Start(simClock, MaxWait, item, this, item, WaitTimedOutFor);
            if (t != null) (waitTimers ?? (waitTimers = new Dictionary<Item, WaitTimer>()))[item] = t;
        }

        // the item left: its wait ended
        private void EndWait(Item item)
        {
            if (waitTimers != null && waitTimers.TryGetValue(item, out var t)) { t.Cancel(); waitTimers.Remove(item); }
            reneging?.Remove(item);
        }

        private void WaitTimedOutFor(WaitTimer t)
        {
            var item = (Item)t.Token;
            if (waitTimers == null || !waitTimers.TryGetValue(item, out var mine) || mine != t) return;
            waitTimers.Remove(item);
            if (!nodes.ContainsKey(item)) return;
            var action = MaxWait.OnTimeout;
            RecordTimeout(WaitKind.Queue, item, simClock.GetSimulationTime() - t.Started, action);
            if (action == TimeoutAction.KeepWaiting) return;
            (reneging ?? (reneging = new List<Item>())).Add(item);
            SendReneging();
        }

        // The items that timed out leave by the timeout target, oldest first, while it takes them.
        private bool SendReneging()
        {
            if (reneging == null || reneging.Count == 0) return false;
            bool any = false;
            while (reneging.Count > 0)
            {
                Item item = reneging[0];
                if (!nodes.ContainsKey(item)) { reneging.RemoveAt(0); continue; }
                if (!SendToTimeoutTarget(item)) break;
                Take(item);   // ends its wait (out of the reneging list)
                currentItems--;
                refused = false;
                vElement.ReportState("Exit");
                any = true;
                NotifyInput();
            }
            if (any) SetHoldState();
            return any;
        }

        private int credits;

        /// <summary>Release credits not used yet (gated): items that may still leave.</summary>
        public int PendingRelease => credits;

        private bool GateClosed => Gated && credits <= 0;

        /// <summary>Gated: lets <paramref name="quantity"/> more items go, oldest first, as many now as can leave; the rest of
        /// the credit is kept for the next items. Returns how many left now.</summary>
        public int ReleaseItems(int quantity)
        {
            if (quantity <= 0) return 0;
            credits += quantity;
            int sent = 0;
            while (credits > 0 && itemsQ.Count > 0 && Unblock()) sent++;
            SetHoldState();
            return sent;
        }

        // Every hand-over goes through here: a gate without credit keeps the item; one that leaves takes a credit.
        private bool Send(Item item)
        {
            if (GateClosed) return false;
            if (!SendOut(item)) return false;
            if (Gated) credits--;
            return true;
        }

        private void SetHoldState() => SetState(currentItems == 0 ? ElementState.IDLE : !GateClosed && HeadBlocked() ? ElementState.BLOCKED : ElementState.PROCESSING);

        private bool HeadBlocked()
        {
            var output = GetOutput();
            if (output is IAdvertisingLink || itemsQ.Count == 0) return false;   // a list: waiting for a puller
            if (!(output is GeneralLink link) || OutputRequestsCarriers) return refused;
            if (OfferOrder != OfferOrder.AnyAccepted) return !link.CouldTake(Head(), this);
            int k = 0;
            foreach (var item in OfferSort == null ? (IEnumerable<Item>)itemsQ : InOfferOrder())
            {
                if (link.CouldTake(item, this)) return false;
                if (++k >= StateScan) break;
            }
            return true;
        }

        /// <summary>Share of time not empty (holding items, PROCESSING or BLOCKED) — as 100 % − empty in a queue's
        /// state chart; it was the PROCESSING share, which leaves the blocked time out now that it is accounted.</summary>
        public double GetUtilization() => GetStateRatio(ElementState.PROCESSING) + GetStateRatio(ElementState.BLOCKED);

        private void Refused()
        {
            refused = !(GetOutput() is IAdvertisingLink);
            SetHoldState();
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
        public OfferOrder OfferOrder { get; set; } = OfferOrder.FIFO;

        /// <summary>The order the held items are offered in (null = arrival order, the default): FIFO offers
        /// only the best item, AnyAccepted offers them best first (the first <see cref="OfferScan"/>); ties by arrival. The
        /// receiver still decides (its admission). Read live at every offer. Grouping by setup and reneging keep their order.</summary>
        public IItemOrder OfferSort { get; set; }

        private readonly List<Item> ordered = new List<Item>();

        // the item offered first: the oldest, or the best by the offer order
        private Item Head() => OfferSort == null ? itemsQ.First.Value : OfferSort.Best(itemsQ, this);

        // the held items in offer order (a copy: a send may change the queue)
        private List<Item> InOfferOrder()
        {
            if (OfferSort != null) OfferSort.Sort(itemsQ, this, ordered);
            else { ordered.Clear(); ordered.AddRange(itemsQ); }
            return new List<Item>(ordered);
        }
        /// <summary><see cref="OfferOrder.AnyAccepted"/>: items offered per attempt, oldest first (0 = every held item); with
        /// <see cref="GroupBySetup"/>, the items scanned for one a receiver is set up for, too.</summary>
        public int OfferScan { get; set; }

        /// <summary>Offer first the held items a receiver is set up for (<see cref="ISetupStation.IsSetUpFor"/>; off = the
        /// default). Behind an ordinary link only (a list's puller or a carrier decides the order itself).</summary>
        public bool GroupBySetup { get; set; }
        /// <summary><see cref="GroupBySetup"/>: the most items in a row that may leave ahead of the oldest one (then it is
        /// offered first); 0 = no limit.</summary>
        public int GroupLimit { get; set; }

        /// <summary>Items that left ahead of the oldest one since it last left first (<see cref="GroupLimit"/>).</summary>
        public int Overtakes => overtakes;
        private int overtakes;

        // Grouping by setup: the oldest held item (within the release scan) that a destination of the output link could take
        // now and is set up for, sent ahead of the others while the limit allows it.
        private bool SendGrouped()
        {
            if (!GroupBySetup || itemsQ.Count == 0 || OutputRequestsCarriers || !(GetOutput() is GeneralLink link)) return false;
            if (GroupLimit > 0 && overtakes >= GroupLimit) return false;   // the oldest goes first now
            int n = OfferScan > 0 ? Math.Min(OfferScan, itemsQ.Count) : itemsQ.Count;
            Item head = itemsQ.First.Value, chosen = null;
            var node = itemsQ.First;
            for (int k = 0; k < n && node != null && chosen == null; k++, node = node.Next)
                foreach (var d in link.Outputs)
                    if (d is ISetupStation s && s.IsSetUpFor(node.Value) && d.CanAccept(node.Value, this)) { chosen = node.Value; break; }
            if (chosen == null || chosen == head) return false;   // the head leaves by the normal release
            if (!SendHeldItem(chosen)) return false;
            overtakes++;
            return true;
        }

        public override bool Unblock()
        {
            bool rerouted = SendReneging();   // the items that timed out first, to the timeout target (max wait)
            return ReleaseDownstream() || rerouted;
        }

        // The normal release downstream.
        private bool ReleaseDownstream()
        {
            if (GateClosed && itemsQ.Count > 0) return false;   // waiting for a release, not for the receiver
            if (SendGrouped()) return true;
            if (itemsQ.Count > 0 && OfferOrder == OfferOrder.AnyAccepted)
            {
                // Offer each held item, oldest (or best: OfferSort) first, until a receiver accepts one (bounded by OfferScan).
                int offered = 0;
                int n = OfferScan > 0 ? Math.Min(OfferScan, itemsQ.Count) : itemsQ.Count;
                var candidates = new Item[n];
                if (OfferSort != null)
                {
                    var sorted = InOfferOrder();
                    for (int k = 0; k < n; k++) candidates[k] = sorted[k];
                }
                else
                {
                    var node = itemsQ.First;
                    for (int k = 0; k < n && node != null; k++, node = node.Next) candidates[k] = node.Value;
                }
                foreach (Item candidate in candidates)
                {
                    if (candidate == null) break;
                    offered++;
                    if (!nodes.ContainsKey(candidate)) continue;   // left meanwhile (a nested send)
                    if (SendHeldItem(candidate)) return true;
                }
                Refused();
                return false;
            }
            if (itemsQ.Count > 0 && OutputRequestsCarriers)
                return OfferToCarriers();
            if (itemsQ.Count > 0)
            {
                // Peek first: a refused head item must keep its place (FIFO; the best one with an offer order).
                Item theItem = Head();

                if (!Send(theItem))
                {
                    Refused();   // tried to send, output refused
                    return false;
                }
                Take(theItem);

                currentItems--;
                refused = false;
                vElement.ReportState("Exit");
                NotifyInput();

                // gated: every released item that can leave goes now (a receiver that frees up calls once)
                while (Gated && credits > 0 && itemsQ.Count > 0)
                {
                    Item next = Head();
                    if (!Send(next)) { refused = true; break; }
                    Take(next);
                    currentItems--;
                    vElement.ReportState("Exit");
                    NotifyInput();
                }
                SetHoldState();
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
                bool behind = (waiting && OfferOrder == OfferOrder.FIFO) || GateClosed;   // behind the waiting ones / the gate
                // grouped by setup: a new item never passes the waiting ones straight through — it joins them and the
                // grouping offers it (it may overtake them only within the limit)
                if (waiting && GroupBySetup) behind = true;
                if (behind || !Send(theItem))
                {
                    nodes[theItem] = itemsQ.AddLast(theItem);
                    currentItems++;
                    StartWait(theItem);   // its max wait (none = no timer)
                    vElement.LoadItem(theItem);
                    if (behind) SetHoldState();   // item sitting in queue: BLOCKED stays BLOCKED
                    else Refused();               // offered and refused
                    // an offer order: a new best item is offered at once (FIFO offers only the best; the old head was refused)
                    if (behind && OfferSort != null && OfferOrder == OfferOrder.FIFO && !GroupBySetup && !GateClosed
                        && !OutputRequestsCarriers && !(GetOutput() is IAdvertisingLink) && Head() == theItem)
                        ReleaseDownstream();
                    // Pushing to a list: every item is advertised, not only the head.
                    if (waiting && GetOutput() is IAdvertisingLink list) list.Advertise(theItem, this);
                }
                else Counters.PassThrough();   // read-only counter (logic viewer): left without a release decision
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
            if (item == null || !nodes.ContainsKey(item) || !Send(item))
                return false;

            Take(item);
            currentItems--;
            refused = false;
            vElement.ReportState("Exit");
            SetHoldState();
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
            int asking = 0;   // gated: no more carrier requests than credits
            if (Gated) foreach (Item it in itemsQ) if (AwaitsCarrier(it)) asking++;
            foreach (Item candidate in InOfferOrder())
            {
                if (!nodes.ContainsKey(candidate) || AwaitsCarrier(candidate)) continue;
                if (Gated && asking >= credits) break;
                if (Gated) asking++;
                if (SendHeldItem(candidate)) return true;
                if (!nodes.ContainsKey(candidate)) { left = true; continue; }   // loaded inside its own request
                if (!AwaitsCarrier(candidate)) break;
            }
            if (!left) Refused();
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
            if (!nodes.TryGetValue(item, out var node)) return;
            if (node == itemsQ.First) overtakes = 0;   // the oldest left: the items behind may overtake again
            itemsQ.Remove(node); nodes.Remove(item);
            if (MaxWait != null) EndWait(item);
            Counters.Release(item, simClock.GetSimulationTime());   // read-only counter (logic viewer)
        }

        public void SetCapacity(int capacity)
        {
            this.capacity = capacity;
        }
    }
}
