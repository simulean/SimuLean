using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Room reserved downstream before a job starts. A station
    /// with <see cref="ReserveExit"/> starts a job only when a place at its next destination can be promised: the place is
    /// held in the destination's promised-room ledger (<see cref="PromiseRoom"/>) and marked <b>reserved</b> — a reserved
    /// place is honoured by every link, a <see cref="GeneralLink"/> too (<see cref="CanAccept"/> refuses other items when
    /// the free room is reserved) — until the item enters it. The job's item is then sent to that destination; a carrier or
    /// walk link takes the same promise over (no double count). Defaults keep the behaviour before: nothing is reserved.
    /// </summary>
    public abstract partial class Element
    {
        // ── receiving side: places reserved by upstream jobs (a subset of the promised room) ──
        private HashSet<Item> reservedRoom;

        /// <summary>Places reserved here by upstream jobs (<see cref="ReserveExit"/>), not entered yet.</summary>
        public int RoomReserved => reservedRoom?.Count ?? 0;

        /// <summary>Promises a place here to <paramref name="item"/> and marks it reserved: every link, a general one too,
        /// keeps it for that item (idempotent).</summary>
        public void ReserveRoom(Item item)
        {
            if (item == null) return;
            PromiseRoom(item);
            (reservedRoom ??= new HashSet<Item>()).Add(item);
        }

        /// <summary>True when <paramref name="item"/> may not take a place here because the free room is reserved for other
        /// items (only when something is reserved; unlimited elements never refuse).</summary>
        public bool RoomReservedForOthers(Item item)
        {
            if (reservedRoom == null || reservedRoom.Count == 0 || (item != null && reservedRoom.Contains(item))) return false;
            int free = GetFreeCapacity();
            return free >= 0 && free - reservedRoom.Count <= 0;
        }

        // the reservation ends with the promise (the item entered, or its move was dropped)
        private void UnreserveRoom(Item item) => reservedRoom?.Remove(item);

        // ── sending side: the destination reserved for each job's item ──
        private Dictionary<Item, Element> exitReservations;
        private readonly List<Element> exitCandidates = new List<Element>();
        private readonly OutputStrategyContext exitContext = new OutputStrategyContext();

        /// <summary>A job starts only when a place at its next destination can be reserved (default false). Applies to
        /// the Multi-Server, the Combiner and the single-job stations (Picking Station) behind a general, carrier or walk
        /// link; a list or milk-run output has no destination to reserve (the job starts without a reservation).</summary>
        public bool ReserveExit { get; set; }

        /// <summary>The destination reserved for <paramref name="item"/> (null = none).</summary>
        public Element ReservedExitOf(Item item)
            => exitReservations != null && item != null && exitReservations.TryGetValue(item, out var d) ? d : null;

        /// <summary>The item's place was handed to the link that moves it (it keeps the same promise) or the item entered.</summary>
        internal void ExitHandedOver(Item item) => exitReservations?.Remove(item);

        /// <summary>Reserves a place for <paramref name="item"/> at the next destination (true; also true when nothing is to
        /// be reserved). The destination: a routing by rule (expression, probability, code) names it over every destination
        /// and the choice is kept — the item waits for room there; any other strategy chooses among the destinations with room
        /// beyond the promised ones. False = no place now: the job waits (told when the downstream frees a place, through its
        /// output link's notification, i.e. <see cref="Unblock"/>).</summary>
        protected bool TryReserveExit(Item item) => ReserveExitFor(item, true);

        /// <summary>The same check without reserving (a station securing its resources first, asks it before).</summary>
        protected bool CouldReserveExit(Item item) => ReserveExitFor(item, false);

        private bool ReserveExitFor(Item item, bool reserve)
        {
            if (!ReserveExit || item == null) return true;
            if (exitReservations != null && exitReservations.ContainsKey(item)) return true;
            IReadOnlyList<Element> dests = output is GeneralLink g ? g.Outputs
                                         : output is Resources.ResourceTransportLink t ? t.Destinations
                                         : output is WalkLink w ? w.Destinations : null;
            if (dests == null || dests.Count == 0) return true;   // a list / milk run: nothing to reserve
            Element dest = null;
            var strategy = GetOutputStrategy();
            if (strategy is IRuleRouting rule)
            {
                var all = dests as List<Element> ?? new List<Element>(dests);
                int r = rule.Route(all, item, this);
                if (r >= 0 && r < dests.Count && ExitHasRoom(dests[r], item)) dest = dests[r];
            }
            else
            {
                exitCandidates.Clear();
                foreach (var d in dests) if (ExitHasRoom(d, item)) exitCandidates.Add(d);
                if (exitCandidates.Count == 1) dest = exitCandidates[0];
                else if (exitCandidates.Count > 1)
                {
                    int i = strategy.SelectOutput(exitCandidates, item, this, exitContext.Set(this, exitCandidates));
                    if (i >= 0 && i < exitCandidates.Count) dest = exitCandidates[i];
                }
            }
            if (dest == null) return false;
            if (!reserve) return true;
            dest.ReserveRoom(item);
            (exitReservations ??= new Dictionary<Item, Element>()).Add(item, dest);
            return true;
        }

        // A place free at d beyond the promised ones (its capacity: not its CheckAvailability, which a station securing its
        // resources first answers only once they are there), that d admits for this item; a stopped destination still
        // counts (the reservation is for later). Refused only for promised room, this station is told when another owner
        // releases one.
        private bool ExitHasRoom(Element d, Item item)
            => d.GetFreeCapacity() != 0 && d.ValidateInput(item, this) && d.HasRoomBeyondPromises(this);

        /// <summary>The place reserved for <paramref name="item"/> is given back (its job gave up and the item leaves by another
        /// way: a max wait's Reroute).</summary>
        protected void DropExitReservation(Item item)
        {
            if (exitReservations == null || item == null || !exitReservations.TryGetValue(item, out var d)) return;
            exitReservations.Remove(item);
            d.ReleaseRoom(item, this);
        }

        /// <summary>A new run: the places reserved for this station's items are dropped.</summary>
        protected void ForgetExitReservations()
        {
            if (exitReservations == null) return;
            foreach (var kv in exitReservations) kv.Value.ForgetPromise(kv.Key);
            exitReservations.Clear();
        }

        /// <summary>Tells the input link, after the current event, that this element may take an item (it could not when it
        /// was offered one: its resources were not there yet).</summary>
        internal void NotifyInputLater() => simClock.Schedule(() => { if (!IsInputBlocked) NotifyInput(); }, 0);
    }
}
