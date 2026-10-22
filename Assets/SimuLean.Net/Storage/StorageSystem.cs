using System;
using System.Collections.Generic;
using SimuLean.Collections;
using SimuLean.Stochastics.Streams;
using SimuLean.Resources;

namespace SimuLean.Storage
{
    /// <summary>
    /// A first-class storage element (proposal §4): a set of parametric
    /// <see cref="StorageLane"/>s, a stock list of the stored items (a private <see cref="SimList"/>: retrievals are
    /// list queries), pluggable location / relocation policies, and optional
    /// timed moves by resources (its <see cref="Transport"/> use: a team of cranes, forklifts, operators).
    ///
    /// Flow put-away arrives through the standard input <see cref="ILink"/>
    /// (<see cref="Receive"/>); retrievals are requested programmatically via
    /// <see cref="RequestRetrieval"/> and leave through the standard output
    /// link when one is connected, so link-driven statistics stay correct.
    ///
    /// v1 simplifications (documented by design):
    /// - Relocation moves are strictly sequential (each blocker move is issued
    ///   in the previous move's arrival callback); concurrent-crane
    ///   optimization is out of scope.
    /// - Blocker destinations are planned atomically up-front: if any blocker
    ///   has no valid home the retrieval fails with no state change. In
    ///   transport mode the plan is not revised mid-flight, and overlapping
    ///   retrievals from the same lane are not supported.
    /// - When no transport team is set — or a lane / the storage has no node
    ///   (<see cref="Element.Location"/>) — the move is instantaneous at the
    ///   current simulation time. Retrieved items are carried to the storage's
    ///   own location (its outbound point). An unreachable node fails loudly.
    /// - No deferred put-away queue: the upstream element holds rejected items and retries (standard protocol).
    /// - A retrieved item the output does not take at once waits at the outbound point, still in the storage (its
    ///   <see cref="Unblock"/> / <see cref="SendHeldItem"/> send it when the receiver has room or a carrier loads it).
    ///   Overlapping retrievals never fetch the same item; a lane being emptied takes no put-away and
    ///   the places planned for relocated blockers are reserved.
    /// </summary>
    public class StorageSystem : Element
    {
        private readonly List<StorageLane> lanes = new List<StorageLane>();
        // The stock: one list entry per stored item (private — not a model list; pulls go through RequestRetrieval,
        // which re-homes blockers) and where each item sits.
        private readonly SimList stock;
        private readonly Dictionary<Item, StoredItem> records = new Dictionary<Item, StoredItem>();
        private readonly Dictionary<Item, ListEntry> entries = new Dictionary<Item, ListEntry>();
        // Targets of retrievals in progress (never chosen again) and retrieved items waiting to leave (in order).
        private readonly HashSet<Item> retrieving = new HashSet<Item>();
        private readonly List<KeyValuePair<Item, Action<Item>>> outbound = new List<KeyValuePair<Item, Action<Item>>>();

        private ILocationPolicy locationPolicy = new FirstAvailablePolicy();
        private IRelocationPolicy relocationPolicy;    // null → falls back to the location policy

        /// <summary>Resources doing its internal moves (relocations, retrievals to its location); null = instant.</summary>
        public StationResources Transport { get; set; }

        private double refX, refY, refZ;

        /// <summary>Blockers physically re-homed since the last <see cref="Start"/>.</summary>
        public int TotalRelocations { get; private set; }

        /// <summary>Items retrieved (delivered out of storage) since the last <see cref="Start"/>.</summary>
        public int TotalRetrievals { get; private set; }

        /// <summary>Fields of the stock list, besides the item labels: <c>sku</c> (the item type), <c>depth</c> (0 = at
        /// the face of its lane), <c>storedTime</c>; and the list built-ins (<c>age</c>, <c>type</c>…).</summary>
        public static readonly string[] StockFields = { "sku", "depth", "storedTime" };

        /// <summary>The stored items and where they sit (including relocations in transit).</summary>
        public IReadOnlyCollection<StoredItem> StoredItems => records.Values;

        /// <summary>Lanes in declaration order.</summary>
        public IReadOnlyList<StorageLane> Lanes => lanes;

        public StorageSystem(string name, SimClock simClock, IVElement vElement = null)
            : base(name, simClock, vElement)
        {
            stock = new SimList(name + "/stock", simClock);
            stock.DefineFields(new[] { new ListFieldSpec { name = "sku", kind = ListFieldKind.BuiltIn, source = "type" } });
            stock.DefineField("depth", e => records[e.Item].DepthIndex);
            stock.DefineField("storedTime", e => records[e.Item].StoredTime);
        }

        // ── configuration ────────────────────────────────────────────────────

        /// <summary>Adds a lane. Lane ids must be unique within the system.</summary>
        public void AddLane(StorageLane lane)
        {
            if (lane == null) throw new ArgumentNullException(nameof(lane));
            if (GetLane(lane.LaneId) != null)
                throw new ArgumentException($"Duplicate lane id '{lane.LaneId}'.", nameof(lane));
            lanes.Add(lane);
        }

        /// <summary>Returns the lane with the given id, or null.</summary>
        public StorageLane GetLane(string laneId)
        {
            foreach (StorageLane lane in lanes)
                if (lane.LaneId == laneId)
                    return lane;
            return null;
        }

        /// <summary>Sets the put-away slotting policy (default <see cref="FirstAvailablePolicy"/>).</summary>
        public void SetLocationPolicy(ILocationPolicy policy)
        {
            locationPolicy = policy ?? new FirstAvailablePolicy();
        }

        /// <summary>
        /// Sets the policy used to re-home blockers during retrievals.
        /// Defaults to the location policy when unset (or when null is passed).
        /// </summary>
        public void SetRelocationPolicy(IRelocationPolicy policy)
        {
            relocationPolicy = policy;
        }

        /// <summary>Reference point (e.g. inbound dock) for distance-based policies.</summary>
        public void SetReferencePoint(double x, double y, double z)
        {
            refX = x;
            refY = y;
            refZ = z;
        }

        // ── put-away (flow, via Link) ────────────────────────────────────────

        /// <summary>
        /// Flow put-away: the location policy picks a lane among those that can
        /// accept the item. Returns false when no lane accepts (the upstream
        /// element blocks and retries via the link protocol).
        /// </summary>
        public override bool Receive(Item theItem)
        {
            var candidates = new List<StorageLane>();
            foreach (StorageLane lane in lanes)
                if (lane.CanAccept(theItem))
                    candidates.Add(lane);

            if (candidates.Count == 0)
                return false;

            StorageLane chosen = locationPolicy.SelectLane(candidates, theItem, BuildContext());
            if (chosen == null)
                return false;

            SetState(ElementState.PROCESSING);
            StoredItem record = chosen.Store(theItem, simClock.GetSimulationTime());
            records[theItem] = record;
            entries[theItem] = stock.PushItem(theItem, this);
            vElement.LoadItem(theItem);
            SetState(ElementState.IDLE);
            return true;
        }

        // ── retrieval ────────────────────────────────────────────────────────

        /// <summary>
        /// Requests the retrieval of the best stored item matching <paramref name="query"/> (a list query over the
        /// stock: e.g. <c>ListQuery.All.Where("sku", ListOp.Equal, "A")</c>; FIFO when it has no order, FEFO with
        /// <c>.OrderBy("expiry")</c>; null = the oldest item).
        ///
        /// Blockers in front of the target are re-homed by the relocation
        /// policy; destinations are planned atomically before anything moves.
        /// With a transport system, blocker moves and the final target move are
        /// chained sequentially at realistic times; otherwise everything
        /// happens instantly at the current simulation time.
        ///
        /// When the target finally leaves it is delivered through the output
        /// link if one is connected (so exit/entry statistics fire normally),
        /// and <paramref name="onRetrieved"/> is invoked.
        ///
        /// Returns false when no item matches or a blocker cannot be re-homed
        /// (no state is changed in that case).
        /// </summary>
        public bool RequestRetrieval(ListQuery query, Action<Item> onRetrieved)
        {
            // an item another retrieval is already fetching is not a candidate
            ListQuery free = (query ?? ListQuery.All).Where(e => !retrieving.Contains(e.Item));
            if (!stock.TryPeek(free, out ListEntry found))
                return false;
            StoredItem target = records[found.Item];

            StorageLane sourceLane = GetLane(target.LaneId);
            if (sourceLane == null)
                return false;      // in transit (a blocker being relocated) — not retrievable now

            List<StoredItem> blockers = sourceLane.GetBlockers(target);
            foreach (StoredItem blocker in blockers)
                if (retrieving.Contains(blocker.Item))
                    return false;  // behind another retrieval's target in the same lane: not supported (v1)

            // Plan every blocker's destination up-front so the retrieval is
            // atomic: any unplaceable blocker fails the request with no moves.
            List<PlannedMove> plan = PlanRelocations(blockers, sourceLane);
            if (plan == null)
                return false;

            retrieving.Add(target.Item);
            sourceLane.RetrievalsInProgress++;
            foreach (PlannedMove move in plan) move.Destination.Reserve(move.Blocker.Item);
            ExecuteChain(plan, 0, target, sourceLane, onRetrieved);
            return true;
        }

        /// <summary>
        /// Picks a destination for each blocker (face-first order), accounting
        /// for the capacity and SKU consequences of earlier planned moves.
        /// Returns null when any blocker has no valid home.
        /// </summary>
        private List<PlannedMove> PlanRelocations(List<StoredItem> blockers, StorageLane sourceLane)
        {
            var plan = new List<PlannedMove>();
            var plannedCount = new Dictionary<StorageLane, int>();
            var plannedSku = new Dictionary<StorageLane, string>();

            LocationContext ctx = BuildContext();
            IRelocationPolicy policy = GetEffectiveRelocationPolicy();

            foreach (StoredItem blocker in blockers)
            {
                var candidates = new List<StorageLane>();
                foreach (StorageLane lane in lanes)
                    if (!ReferenceEquals(lane, sourceLane) && lane.RetrievalsInProgress == 0 &&
                        CanAcceptPlanned(lane, blocker.Item, plannedCount, plannedSku))
                        candidates.Add(lane);

                StorageLane destination = policy.SelectLane(candidates, blocker.Item, ctx);
                if (destination == null)
                    return null;

                plan.Add(new PlannedMove(blocker, destination));

                int extra;
                plannedCount.TryGetValue(destination, out extra);
                plannedCount[destination] = extra + 1;

                if (destination.SkuConstraint == SkuConstraint.SingleSku &&
                    destination.Count == 0 && !plannedSku.ContainsKey(destination))
                    plannedSku[destination] = blocker.Item.GetTypeName();
            }
            return plan;
        }

        /// <summary>
        /// <see cref="StorageLane.CanAccept"/> extended with the capacity/SKU
        /// effects of moves already planned in this retrieval.
        /// </summary>
        private static bool CanAcceptPlanned(StorageLane lane, Item item,
                                             Dictionary<StorageLane, int> plannedCount,
                                             Dictionary<StorageLane, string> plannedSku)
        {
            int extra;
            plannedCount.TryGetValue(lane, out extra);
            if (lane.FreeCapacity - extra <= 0)
                return false;

            if (lane.SkuConstraint == SkuConstraint.SingleSku)
            {
                string existing = lane.Count > 0 ? lane.Contents[0].Item.GetTypeName() : lane.ReservedSku;
                if (existing == null)
                    plannedSku.TryGetValue(lane, out existing);
                if (existing != null && existing != item.GetTypeName())
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Executes move <paramref name="index"/> of the plan, then recurses
        /// from the arrival callback; after the last blocker, moves the target
        /// out and delivers it. With no transport (or missing node ids) every
        /// callback runs synchronously — the whole chain is instantaneous.
        /// </summary>
        private void ExecuteChain(List<PlannedMove> plan, int index, StoredItem target,
                                  StorageLane sourceLane, Action<Item> onRetrieved)
        {
            if (index < plan.Count)
            {
                PlannedMove move = plan[index];
                sourceLane.Remove(move.Blocker);            // leaves the face when its move starts
                Action<Item> onArrival = delegate
                {
                    move.Destination.PlaceReserved(move.Blocker);   // its reserved place; StoredTime preserved
                    TotalRelocations++;
                    ExecuteChain(plan, index + 1, target, sourceLane, onRetrieved);
                };

                if (!Move(move.Blocker.Item, sourceLane.NodeId, move.Destination.NodeId, onArrival))
                    onArrival(move.Blocker.Item);           // instantaneous fallback
            }
            else
            {
                sourceLane.Remove(target);
                Action<Item> onArrival = delegate
                {
                    stock.Take(entries[target.Item]);   // leaves the stock (its storage time is the list's wait)
                    entries.Remove(target.Item);
                    records.Remove(target.Item);
                    retrieving.Remove(target.Item);
                    sourceLane.RetrievalsInProgress--;
                    TotalRetrievals++;
                    Deliver(target.Item, onRetrieved);
                };

                if (!Move(target.Item, sourceLane.NodeId, Location, onArrival))
                    onArrival(target.Item);
            }
        }

        /// <summary>A timed move by its transport team: travel to <paramref name="from"/>, load, travel to
        /// <paramref name="to"/>, unload, then <paramref name="onArrival"/>. False (nothing requested) without a
        /// team or a node — the caller moves it at once.</summary>
        private bool Move(Item item, string from, string to, Action<Item> onArrival)
        {
            if (Transport == null || from == null || to == null) return false;
            var request = new ResourceRequest(this, TaskType.Transport, (r, got) =>
            {
                var lead = new TaskSequence(TaskType.Transport, this, "Move " + item.GetId())
                    .Travel(from).Load(item).Travel(to).Unload(item)
                    .Callback(() => onArrival(item));
                for (int k = 1; k < got.Count; k++)      // helpers accompany the lead
                {
                    var helper = got[k];
                    helper.Execute(new TaskSequence(TaskType.Transport, this, "Help " + item.GetId()).Hold(ResourceStates.TRAVELLING_LOADED));
                    lead.Completed += _ => helper.Continue();
                }
                got[0].Execute(lead);
            }, new List<ResourceRequirement>(Transport.Requirements).ToArray());
            request.Priority = Transport.Priority?.Invoke(item) ?? 0;
            request.Location = from;
            Transport.Dispatcher.Request(request);
            return true;
        }

        /// <summary>
        /// Hands a retrieved item downstream: it waits at the outbound point, in order, and leaves through the output
        /// link (stats fire via the link layer) when the receiver takes it — at once, when it has room later
        /// (<see cref="Unblock"/>) or when a carrier loads it (<see cref="SendHeldItem"/>); the requester's callback
        /// runs when it leaves. Without an output link it leaves at once, to the callback. Before 2026-10-09 an item
        /// the output refused left anyway, without its exit, and a carrier could never take it.
        /// </summary>
        private void Deliver(Item item, Action<Item> onRetrieved)
        {
            vElement.UnloadItem(item);
            if (GetOutput() == null)
            {
                Left(item, onRetrieved);
                return;
            }
            outbound.Add(new KeyValuePair<Item, Action<Item>>(item, onRetrieved));
            Unblock();
        }

        private void Left(Item item, Action<Item> onRetrieved)
        {
            ILink input = GetInput();
            if (input != null)
                input.NotifyAvailable(this);
            onRetrieved?.Invoke(item);
        }

        private bool draining;

        /// <summary>Offers the retrieved items waiting at the outbound point, oldest first: into a plain push link in
        /// order, while it takes them; a carrier or a list is offered every one (each gets its request / listing).
        /// True when at least one left.</summary>
        public override bool Unblock()
        {
            if (draining) return false;   // a retrieval completed inside a send: the running loop takes its item too
            draining = true;
            bool any = false;
            try
            {
                bool inOrder = GetOutput() is GeneralLink;
                for (int i = 0; i < outbound.Count;)
                {
                    var next = outbound[i];
                    if (SendOut(next.Key))
                    {
                        outbound.Remove(next);
                        any = true;
                        Left(next.Key, next.Value);
                        continue;
                    }
                    if (inOrder) break;
                    i++;
                }
            }
            finally { draining = false; }
            return any;
        }

        /// <summary>Hands a specific waiting item over (its carrier loads it, a list hands it to a puller).</summary>
        public override bool SendHeldItem(Item item)
        {
            int i = outbound.FindIndex(p => p.Key == item);
            if (i < 0 || !SendOut(item)) return false;
            var held = outbound[i];
            outbound.RemoveAt(i);
            Left(held.Key, held.Value);
            return true;
        }

        // ── Element contract ─────────────────────────────────────────────────

        /// <summary>True when any lane can accept the item.</summary>
        public override bool CheckAvailability(Item theItem)
        {
            foreach (StorageLane lane in lanes)
                if (lane.CanAccept(theItem))
                    return true;
            return false;
        }

        /// <summary>Items in the storage: stored (including in-transit relocations and retrievals on their way out) and
        /// retrieved ones waiting at the outbound point.</summary>
        public override int GetQueueLength() => stock.Count + outbound.Count;

        /// <summary>Sum of free positions over all lanes.</summary>
        public override int GetFreeCapacity()
        {
            int free = 0;
            foreach (StorageLane lane in lanes)
                free += lane.FreeCapacity;
            return free;
        }

        /// <summary>Model reset: empties every lane, the entity list and the counters.</summary>
        public override void Start()
        {
            foreach (StorageLane lane in lanes)
                lane.Clear();
            stock.Reset();
            records.Clear();
            entries.Clear();
            retrieving.Clear();
            outbound.Clear();
            TotalRelocations = 0;
            TotalRetrievals = 0;
            base.Start();
        }

        // ── internals ────────────────────────────────────────────────────────

        private LocationContext BuildContext()
        {
            return new LocationContext
            {
                System = this,
                Clock = simClock,
                RandomStream = CreateRandomStream,
                RefX = refX,
                RefY = refY,
                RefZ = refZ
            };
        }

        private IRandomEngine CreateRandomStream()
        {
            return simClock.Context.CreateRandomStream(GetName(), "locationPolicy");
        }

        private IRelocationPolicy GetEffectiveRelocationPolicy()
        {
            if (relocationPolicy != null)
                return relocationPolicy;

            var asRelocation = locationPolicy as IRelocationPolicy;
            return asRelocation ?? new LocationPolicyAdapter(locationPolicy);
        }

        /// <summary>Adapts a pure <see cref="ILocationPolicy"/> to the relocation role.</summary>
        private sealed class LocationPolicyAdapter : IRelocationPolicy
        {
            private readonly ILocationPolicy inner;
            public LocationPolicyAdapter(ILocationPolicy inner) { this.inner = inner; }

            public StorageLane SelectLane(IReadOnlyList<StorageLane> candidates, Item item, LocationContext ctx)
                => inner.SelectLane(candidates, item, ctx);
        }

        private struct PlannedMove
        {
            public readonly StoredItem Blocker;
            public readonly StorageLane Destination;

            public PlannedMove(StoredItem blocker, StorageLane destination)
            {
                Blocker = blocker;
                Destination = destination;
            }
        }
    }
}
