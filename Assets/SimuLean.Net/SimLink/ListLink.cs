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
    /// its <c>transportBy</c>), the reserved item is not handed over at once: a carrier
    /// is requested (<see cref="Resources.TransportMoves"/>), travels to the holder, loads (the item leaves the holder),
    /// travels to the puller and delivers (the item enters it). The reservation keeps the puller from pulling another
    /// item meanwhile ("in transit").</para>
    /// <para><b>Room promised.</b> A reserved item holds a place at its puller
    /// (<see cref="Element.PromiseRoom"/>) until it enters or is listed again, and a puller pulls only while a place is
    /// left after the promises of every link into it (a transport link, a milk run, another list).</para>
    /// <para><b>Max wait of a puller.</b> A puller may carry a limit (<see cref="SetPullLimit"/>): its wait runs
    /// from its pull until an entry matches (re-registering a pull does not restart it; a puller that stops pulling — full,
    /// stopped, a transfer under way — ends it). On a timeout the puller counts it (<c>timeouts:list_pull</c>) and signals it;
    /// the pull goes on (KeepWaiting: a puller holds nothing to give back, and no item to send elsewhere).</para>
    /// </summary>
    public sealed class ListLink : IAdvertisingLink
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
            pullers[puller] = PullQuery(puller, query ?? ListQuery.All);
            puller.SetInput(this);
        }

        // The puller's query with the test of what it can take now (read live at each match): built once per puller, not
        // at every pull request. Only entries the puller can take now: its availability
        // (e.g. a combiner port accepts components only while a main item is being assembled) and its input strategy
        // with the origin, and a place not promised to another load on its way.
        private ListQuery PullQuery(Element puller, ListQuery query)
            => query.Where(e => e.Item != null && e.Owner != null && !e.Owner.IsOutputBlocked && !puller.IsInputBlocked
                                && puller.CheckAvailability(e.Item) && puller.ValidateInput(e.Item, e.Owner)
                                && (puller.RoomPromised == 0 || puller.HasRoomBeyondPromises(this)));

        /// <summary>Registers the pullers' first pulls (after the elements' Start).</summary>
        public void Start()
        {
            foreach (var p in pullerOrder) RequestPull(p);
        }

        // ── max wait of the pullers ──
        private Dictionary<Element, WaitLimit> pullLimits;
        private Dictionary<Element, WaitTimer> pullTimers;

        /// <summary>The max wait of <paramref name="puller"/>'s pulls (null = none). Only KeepWaiting: the timeout is counted
        /// and signalled, the pull goes on.</summary>
        public void SetPullLimit(Element puller, WaitLimit limit)
        {
            if (puller == null) return;
            if (limit != null && limit.OnTimeout != TimeoutAction.KeepWaiting)
                throw new System.ArgumentException($"'{puller.Name}': a list pull's max wait can only keep waiting (it holds nothing to give " +
                                                   $"back and no item to send elsewhere), not {limit.OnTimeout}.");
            if (pullLimits == null) pullLimits = new Dictionary<Element, WaitLimit>();
            if (limit == null) pullLimits.Remove(puller); else pullLimits[puller] = limit;
        }

        // A pull is registered (or not): the puller's wait starts if none runs, or ends when it does not pull any more.
        private void PullWaitCheck(Element puller)
        {
            if (!pullLimits.TryGetValue(puller, out var limit)) return;
            if (!pending.ContainsKey(puller)) { PullWaitEnds(puller); return; }
            if (pullTimers != null && pullTimers.ContainsKey(puller)) return;
            var t = WaitTimer.Start(puller.GetSimClock(), limit, null, puller, puller, PullTimedOut);
            if (t != null) (pullTimers ?? (pullTimers = new Dictionary<Element, WaitTimer>()))[puller] = t;
        }

        private void PullWaitEnds(Element puller)
        {
            if (pullTimers == null || !pullTimers.TryGetValue(puller, out var t)) return;
            t.Cancel();
            pullTimers.Remove(puller);
        }

        private void PullTimedOut(WaitTimer t)
        {
            var puller = (Element)t.Token;
            if (pullTimers == null || !pullTimers.TryGetValue(puller, out var mine) || mine != t) return;
            pullTimers.Remove(puller);
            puller.RecordTimeout(WaitKind.ListPull, null, puller.GetSimClock().GetSimulationTime() - t.Started, TimeoutAction.KeepWaiting);
        }

        /// <summary>Forgets advertised items, reservations and pending pulls (new run; the list itself is reset by its owner).</summary>
        public void Reset()
        {
            if (pullTimers != null) { foreach (var t in pullTimers.Values) t.Cancel(); pullTimers.Clear(); }
            foreach (var pull in pending.Values) pull.Cancel();
            foreach (var kv in reserved) kv.Value.ForgetPromise(kv.Key);
            pending.Clear(); entries.Clear(); reserved.Clear(); entryOwners.Clear();
            foreach (var m in transports.Values) m.Reset();
            moving.Clear();
        }

        /// <summary>The items <paramref name="origin"/> hands to the pullers are carried by <paramref name="transport"/>; returns the moves (to set their load / travel / unload times).</summary>
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

        /// <summary>The puller an item is reserved for (a pull matched it, the hand-over is under way), or null. An origin
        /// whose hand-over takes time (a storage publishing its stock: the item is retrieved and carried to the puller's
        /// location first) uses it to know where the item goes.</summary>
        public Element ReservedFor(Item item)
            => item != null && reserved.TryGetValue(item, out Element puller) ? puller : null;

        /// <summary>Withdraws an advertised (not reserved) item from the list, e.g. it left its origin by another path.
        /// False when it was not listed or is reserved.</summary>
        public bool Withdraw(Item item)
        {
            if (item == null || reserved.ContainsKey(item) || !entries.TryGetValue(item, out ListEntry entry)) return false;
            entries.Remove(item);
            if (entry != null) List.Remove(entry);
            return true;
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

            // The item leaves the list before the hand-over: a puller that also pushes to this list offers it again
            // while receiving it, and that offer must find it unlisted (it recursed or was lost).
            Forget(item);
            bool received = false;
            if (!puller.IsInputBlocked && puller.CheckAvailability(item) && puller.ValidateInput(item, source))
            {
                source.EnterSend();
                try { received = ItemHandOver.Receive(source, puller, item); }   // exit / entry labels first
                finally { source.ExitSend(); }
            }

            if (!received)
            {
                // The puller cannot take it any more: advertise it again, the puller asks again later.
                SendItem(item, source);
                RequestPullLater(puller);
                return false;
            }

            source.StatsCollector.OnExit(item);
            puller.StatsCollector.OnEntry(item);
            RequestPull(puller);   // the puller may have more room
            return true;
        }

        // Ask again after this event (an immediate retry could match the same entry forever).
        // In its own method: a closure that captures a parameter is allocated at every call, used or not.
        private void RequestPullLater(Element puller) => puller.GetSimClock().Schedule(() => RequestPull(puller), 0);

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
            RequestCarrier(item, source, puller, moves);
            return false;                             // stays in the holder until the carrier loads it
        }

        // In its own method: a closure that captures a parameter is allocated at every call, used or not.
        private void RequestCarrier(Item item, Element source, Element puller, Resources.TransportMoves moves)
            => moves.Request(item, puller, (it, dest) => DeliverCarried(it, source, dest));

        // The carrier unloaded at the puller: it enters (false = it cannot take it now, the carrier waits).
        private bool DeliverCarried(Item item, Element origin, Element puller)
        {
            if (puller.IsInputBlocked || !puller.CheckAvailability(item) || !puller.ValidateInput(item, origin)) return false;
            // As in Transfer: unlisted before it enters, in case the puller pushes it back to this list.
            entries.TryGetValue(item, out ListEntry entry);
            Forget(item);
            if (!ItemHandOver.Receive(null, puller, item))
            {
                reserved[item] = puller; entryOwners[item] = origin; entries[item] = entry; moving.Add(item);   // the carrier waits
                puller.PromiseRoom(item);
                return false;
            }
            puller.StatsCollector.OnEntry(item);
            RequestPull(puller);   // the puller may have more room
            return true;
        }

        // The item is no longer listed, reserved or being carried.
        private void Forget(Item item)
        {
            if (reserved.TryGetValue(item, out Element puller)) puller.ReleaseRoom(item, this);
            reserved.Remove(item);
            entryOwners.Remove(item);
            entries.Remove(item);
            moving.Remove(item);
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
            RequestPullNow(puller);
            if (pullLimits != null) PullWaitCheck(puller);   // its max wait starts, goes on or ends
        }

        private void RequestPullNow(Element puller)
        {
            if (pending.ContainsKey(puller) || puller.IsInputBlocked || puller.GetFreeCapacity() == 0) return;
            foreach (var p in reserved.Values) if (p == puller) return;   // a transfer to it is already under way
            if (puller.RoomPromised > 0 && !puller.HasRoomBeyondPromises(this)) return;   // its room is promised
            Pull(puller);
        }

        // The pull itself, once it is due.
        // In its own method: a closure that captures a parameter is allocated at every call, used or not.
        private void Pull(Element puller)
        {
            ListPull pull = null;
            pull = List.Pull(pullers[puller], entry =>
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
            if (pullTimers != null) PullWaitEnds(puller);   // an entry matched: its wait ended
            reserved[item] = puller;
            entryOwners[item] = owner;
            puller.PromiseRoom(item);                   // its place at the puller, until it enters
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
