using System.Collections.Generic;
using SimuLean.Collections;

namespace SimuLean
{
    /// <summary>
    /// Connects elements to a <see cref="SimList"/>: it is the output link of
    /// the elements that <b>push</b> their items to the list and the input link of the elements that
    /// <b>pull</b> from it.
    /// <list type="bullet">
    /// <item><b>Push.</b> Every distinct item an origin offers (<see cref="SendItem"/>) — whatever the
    /// element — is advertised once in the list and stays in the origin, which sees a refused send and
    /// keeps it (blocked, as with a full destination). Offering an advertised item again (the origin
    /// retrying, e.g. after a stop) re-evaluates the pending pulls. Entries of a stopped origin are not
    /// matched until it resumes.</item>
    /// <item><b>Pull.</b> A puller with free capacity registers a pull with its query (plus its input
    /// strategy); when an entry matches, the item is reserved for it and its origin hands over that very
    /// item (<see cref="Element.SendHeldItem"/>, after the current event), which transfers it.</item>
    /// </list>
    /// The transfer fires <c>OnExit</c> / <c>OnEntry</c> like <see cref="GeneralLink"/>, so statistics stay
    /// link-driven. Call <see cref="Start"/> after the elements' own <c>Start</c> (registers the first pulls).
    /// <para><b>Transport.</b> When the holder carries its items with resources (<see cref="SetTransport"/>,
    /// its <c>transportBy</c>, as FlexSim's <i>Use Transport</i>), the reserved item is not handed over at once: a carrier
    /// is requested (<see cref="Resources.TransportMoves"/>), travels to the holder, loads (the item leaves the holder),
    /// travels to the puller and delivers (the item enters it). The reservation keeps the puller from pulling another
    /// item meanwhile ("in transit").</para>
    /// </summary>
    public sealed class ListLink : ILink
    {
        public SimList List { get; }

        private readonly List<Element> origins = new List<Element>();
        private readonly Dictionary<Element, ListQuery> pullers = new Dictionary<Element, ListQuery>();
        private readonly List<Element> pullerOrder = new List<Element>();

        private readonly Dictionary<Item, ListEntry> entries = new Dictionary<Item, ListEntry>();
        private readonly Dictionary<Item, Element> reserved = new Dictionary<Item, Element>();   // item → puller
        private readonly Dictionary<Element, ListPull> pending = new Dictionary<Element, ListPull>();
        private readonly Dictionary<Item, Element> entryOwners = new Dictionary<Item, Element>();   // reserved item → origin
        private readonly Dictionary<Element, Resources.TransportMoves> transports = new Dictionary<Element, Resources.TransportMoves>();
        private readonly HashSet<Item> moving = new HashSet<Item>();   // a carrier is requested / on its way

        public ListLink(SimList list)
        {
            List = list ?? throw new System.ArgumentNullException(nameof(list));
        }

        /// <summary>Makes <paramref name="origin"/> push its items to the list (sets its output link).</summary>
        public void AddOrigin(Element origin)
        {
            if (!origins.Contains(origin)) origins.Add(origin);
            origin.SetOutput(this);
        }

        /// <summary>Makes <paramref name="puller"/> pull from the list with <paramref name="query"/> (sets its input link).</summary>
        public void AddPuller(Element puller, ListQuery query = null)
        {
            if (!pullers.ContainsKey(puller)) pullerOrder.Add(puller);
            pullers[puller] = query ?? ListQuery.All;
            puller.SetInput(this);
        }

        /// <summary>Registers the pullers' first pulls (after the elements' Start).</summary>
        public void Start()
        {
            foreach (var p in pullerOrder) RequestPull(p);
        }

        /// <summary>Forgets advertised items, reservations and pending pulls (new run; the list itself is reset by its owner).</summary>
        public void Reset()
        {
            foreach (var pull in pending.Values) pull.Cancel();
            pending.Clear(); entries.Clear(); reserved.Clear(); entryOwners.Clear();
            foreach (var m in transports.Values) m.Reset();
            moving.Clear();
        }

        /// <summary>The items <paramref name="origin"/> hands to the pullers are carried by <paramref name="transport"/>
        ///; returns the moves (to set their load / travel / unload times).</summary>
        public Resources.TransportMoves SetTransport(Element origin, Resources.StationResources transport)
        {
            var moves = new Resources.TransportMoves(origin, transport);
            transports[origin] = moves;
            return moves;
        }

        // ── origin side ──────────────────────────────────────────────────────

        public bool SendItem(Item theItem, Element source)
        {
            if (reserved.TryGetValue(theItem, out Element puller))
                return transports.TryGetValue(source, out var moves) ? TransferCarried(theItem, source, puller, moves)
                                                                     : Transfer(theItem, source, puller);

            ResendReserved(source);                     // e.g. after the origin resumed from a stop
            if (entries.ContainsKey(theItem))
            {
                // Already advertised: the origin is retrying (it resumed, or was asked to offer again).
                // Its entries may be matchable now, which pending pulls would not notice by themselves.
                RefreshPulls();
                return false;
            }
            entries[theItem] = List.PushItem(theItem, source);   // null: a pending pull matched it at once
            return false;                               // the item stays in its origin until pulled
        }

        /// <summary>
        /// Advertises an item an origin holds behind the one it offers (e.g. the rest of a queue), without
        /// trying to send it. Idempotent: an item already listed is left as it is.
        /// </summary>
        public void Advertise(Item item, Element origin)
        {
            if (item == null || entries.ContainsKey(item) || reserved.ContainsKey(item)) return;
            entries[item] = List.PushItem(item, origin);
        }

        // Re-registers every pending pull so the entries already listed are evaluated again.
        private bool refreshing;
        private void RefreshPulls()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                foreach (var p in pullerOrder)
                {
                    if (!pending.TryGetValue(p, out ListPull old)) continue;
                    pending.Remove(p);
                    old.Cancel();
                    RequestPull(p);
                }
            }
            finally { refreshing = false; }
        }

        // Reserved items of an origin whose send had to wait (its output was blocked): try them again.
        private void ResendReserved(Element source)
        {
            if (source.IsOutputBlocked) return;
            foreach (var kv in reserved)
                if (entryOwners.TryGetValue(kv.Key, out Element owner) && owner == source && !moving.Contains(kv.Key))   // a carrier is on its way: nothing to resend
                {
                    Item item = kv.Key;
                    source.GetSimClock().Schedule(() => SendFromHolder(source, item), 0);
                }
        }

        private bool Transfer(Item item, Element source, Element puller)
        {
            if (source.IsOutputBlocked) return false;   // keeps the reservation; resent when the origin resumes

            bool received = false;
            if (!puller.IsInputBlocked && puller.CheckAvailability(item) && puller.ValidateInput(item, source))
            {
                source.EnterSend();
                try { received = ItemHandOver.Receive(source, puller, item); }   // exit / entry labels first
                finally { source.ExitSend(); }
            }

            reserved.Remove(item);
            entryOwners.Remove(item);
            if (!received)
            {
                // The puller cannot take it any more: advertise it again, the puller asks again later.
                entries.Remove(item);
                SendItem(item, source);
                // Ask again after this event (an immediate retry could match the same entry forever).
                puller.GetSimClock().Schedule(() => RequestPull(puller), 0);
                return false;
            }

            source.StatsCollector.OnExit(item);
            puller.StatsCollector.OnEntry(item);
            entries.Remove(item);
            RequestPull(puller);   // the puller may have more room
            return true;
        }

        // A reserved item of a holder that carries its items: request a carrier; the item leaves the holder when loaded.
        private bool TransferCarried(Item item, Element source, Element puller, Resources.TransportMoves moves)
        {
            if (moves.TakeLoading(item))
            {
                ItemHandOver.Exit(source, item);
                source.StatsCollector.OnExit(item);   // loaded: it left the holder (the reservation stays until delivered)
                return true;
            }
            if (moving.Contains(item) || source.IsOutputBlocked) return false;
            moving.Add(item);
            moves.Request(item, puller, (it, dest) => DeliverCarried(it, source, dest));
            return false;                             // stays in the holder until the carrier loads it
        }

        // The carrier unloaded at the puller: it enters (false = it cannot take it now, the carrier waits).
        private bool DeliverCarried(Item item, Element origin, Element puller)
        {
            if (puller.IsInputBlocked || !puller.CheckAvailability(item) || !puller.ValidateInput(item, origin) || !ItemHandOver.Receive(null, puller, item))
                return false;
            puller.StatsCollector.OnEntry(item);
            reserved.Remove(item);
            entryOwners.Remove(item);
            entries.Remove(item);
            moving.Remove(item);
            RequestPull(puller);   // the puller may have more room
            return true;
        }

        // ── puller side ──────────────────────────────────────────────────────

        /// <summary>
        /// A puller has room or changed state: its pull is registered again, so the entries already in the
        /// list are re-evaluated against what it can accept now (a pending pull is only matched by new pushes).
        /// </summary>
        public bool NotifyAvailable(Element source)
        {
            if (!pullers.ContainsKey(source)) return false;
            foreach (var moves in transports.Values)
            {
                bool? delivered = moves.DeliverWaiting(source);   // a carrier waiting at this puller delivers first
                if (delivered.HasValue) return delivered.Value;
            }
            if (pending.TryGetValue(source, out ListPull old))
            {
                pending.Remove(source);
                old.Cancel();
            }
            RequestPull(source);
            return false;   // transfers happen through the origin, never synchronously here
        }

        private void RequestPull(Element puller)
        {
            if (pending.ContainsKey(puller) || puller.IsInputBlocked || puller.GetFreeCapacity() == 0) return;
            foreach (var p in reserved.Values) if (p == puller) return;   // a transfer to it is already under way

            // Only entries the puller can take now: its availability (e.g. a combiner port accepts components
            // only while a main item is being assembled) and its input strategy with the origin.
            ListQuery q = pullers[puller]
                .Where(e => e.Item != null && e.Owner != null && !e.Owner.IsOutputBlocked && !puller.IsInputBlocked
                            && puller.CheckAvailability(e.Item) && puller.ValidateInput(e.Item, e.Owner));
            ListPull pull = null;
            pull = List.Pull(q, entry =>
            {
                if (pull != null) pending.Remove(puller);
                Matched(puller, entry);
            });
            if (pull != null) pending[puller] = pull;
        }

        private void Matched(Element puller, ListEntry entry)
        {
            Item item = entry.Item;
            Element owner = entry.Owner;
            reserved[item] = puller;
            entryOwners[item] = owner;
            // The origin hands over this very item (it may not be its first), after the current event.
            owner.GetSimClock().Schedule(() => SendFromHolder(owner, item), 0);
        }

        private void SendFromHolder(Element owner, Item item)
        {
            if (!reserved.ContainsKey(item) || owner.IsOutputBlocked || moving.Contains(item)) return;   // done already / waits for resume / being carried
            // Elements holding one outgoing item use the default (false): they offer it again on Unblock.
            if (!owner.SendHeldItem(item)) owner.NotifyUnblock();
        }

    }
}
