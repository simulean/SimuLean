using System;
using System.Collections.Generic;
using SimuLean.Collections;
using SimuLean.Stochastics.Streams;
using SimuLean.Resources;

namespace SimuLean.Storage
{
    /// <summary>
    /// A first-class storage element: a set of parametric
    /// <see cref="StorageLane"/>s, a stock list of the stored items (a private <see cref="SimList"/>: retrievals are
    /// list queries), pluggable location / relocation policies, and optional
    /// timed moves by resources (its <see cref="Transport"/> use: a team of cranes, forklifts, operators).
    ///
    /// Flow put-away arrives through the standard input <see cref="ILink"/>
    /// (<see cref="Receive"/>); retrievals are requested programmatically via
    /// <see cref="RequestRetrieval"/> and leave through the standard output
    /// link when one is connected, so link-driven statistics stay correct.
    ///
    /// Simplifications (by design):
    /// - Relocation moves are strictly sequential (each blocker move is issued
    ///   in the previous move's arrival callback); concurrent-crane
    ///   optimization is out of scope.
    /// - Blocker destinations are planned atomically up-front: if any blocker
    ///   has no valid home the retrieval fails with no state change. In
    ///   transport mode the plan is not revised mid-flight, and overlapping
    ///   retrievals from the same lane are not supported.
    /// - When no transport team is set — or neither a lane nor the storage has a node
    ///   (<see cref="Element.Location"/>) — the move is instantaneous at the
    ///   current simulation time. A lane without a node is at the storage's location (<see cref="NodeOf"/>): its
    ///   moves take the team's travel to it and the load / unload times. Retrieved items are carried to the storage's
    ///   own location (its outbound point). An unreachable node fails loudly.
    /// - No deferred put-away queue: the upstream element holds rejected items and retries (standard protocol).
    /// - **Put-away by the transport team** (<see cref="PutAwayByTransport"/>): a load taken in waits at the
    ///   storage's <see cref="Element.Location"/> (at most <see cref="InboundCapacity"/> of them; the upstream holds the
    ///   next) until a member loads it and carries it to its lane, whose place is reserved meanwhile; it joins the stock
    ///   (retrievable, published) when it is placed. Without a team, a location or a lane node it is placed at once.
    /// - **Physical dimensions** (<see cref="PhysicalDimensions"/>; off by default): a lane
    ///   takes a load only if its size labels fit the room of one position (<see cref="StorageLane.SlotWidth"/> …); a
    ///   missing label or a lane without a limit is not checked. One load per position (no packing by length).
    /// - A retrieved item the output does not take at once waits at the outbound point, still in the storage (its
    ///   <see cref="Unblock"/> / <see cref="SendHeldItem"/> send it when the receiver has room or a carrier loads it).
    ///   Overlapping retrievals never fetch the same item; a lane being emptied takes no put-away and
    ///   the places planned for relocated blockers are reserved.
    /// - **Published stock**: when its output is a model list (<see cref="PublishedList"/>) every
    ///   stored item is advertised there; a pull that matches one retrieves it (blockers re-homed, moves by the
    ///   <see cref="Transport"/> team) and delivers it at the puller's location, then hands it over through the list link.
    ///   A pull whose item cannot be fetched yet (its lane being dug, no room for a blocker) waits and is retried when a
    ///   retrieval ends. Any storage is then usable in a flow model (it was an end store: retrieval was programmatic).
    /// - **Pass-through** (<see cref="ReleaseAfterDwell"/>; off by default, a minimum dwell time): with an
    ///   output connection (not a list) each load is released once it has stayed <see cref="MinimumDwellTime"/> — retrieved as above
    ///   (blockers re-homed, moved by the team) and handed over when the receiver takes it. One release at a time: the
    ///   next starts when the previous one has left the outbound point. Off, the storage is a store: a load leaves only
    ///   when it is pulled or retrieved (connected to a sink, it fills up and nothing leaves).
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
        // Items of the published stock a puller reserved whose retrieval could not start yet (lane busy, no room).
        private readonly List<Item> waitingPulls = new List<Item>();
        private bool waitingScheduled;

        private ILocationPolicy locationPolicy = new FirstAvailablePolicy();
        private IRelocationPolicy relocationPolicy;    // null → falls back to the location policy

        /// <summary>Resources doing its internal moves (relocations, retrievals to its location); null = instant.</summary>
        public StationResources Transport { get; set; }

        /// <summary>Load / unload time of each move by <see cref="Transport"/> (null = the resource's own times): the
        /// element's transport load / unload samplers, so one forklift handles cartons and pallets at their own pace.</summary>
        public Func<Item, double> MoveLoadTime { get; set; }
        public Func<Item, double> MoveUnloadTime { get; set; }

        /// <summary>Raised when an item leaves the stock (any retrieval, programmatic or a pull of the published stock),
        /// at the end of its move: the consumption hook of replenishment logic.</summary>
        public event Action<StorageSystem, Item> Retrieved;

        /// <summary>The model list its stock is published to: its output, when that is a <see cref="ListLink"/> (it
        /// "pushes" to the list). Null = not published.</summary>
        public ListLink PublishedList => GetOutput() as ListLink;

        /// <summary>Pulls of the published stock waiting for their item's lane (or for room to re-home blockers).</summary>
        public int WaitingPullCount => waitingPulls.Count;

        private double refX, refY, refZ;

        /// <summary>Default size labels of a load (the convention of the length-limited lane and the stacker).</summary>
        public const string DefaultLengthLabel = "length", DefaultWidthLabel = "width", DefaultHeightLabel = "height";

        /// <summary>Label of a walled container (tote, roll cage) giving the height of its inside floor, where its load rests
        /// (from its model's <c>LoadSocket</c>): a Combiner packs the parts inside it, not on its top.</summary>
        public const string DefaultLoadFloorLabel = "loadFloor";

        /// <summary>Check each load's size against the room of a lane's position (see the class notes). Off: positions only.</summary>
        public bool PhysicalDimensions { get; set; }

        /// <summary>Numeric labels giving a load's size along the lane, across it and its height (null / empty = not checked).</summary>
        public string LengthLabel { get; set; } = DefaultLengthLabel;
        public string WidthLabel { get; set; } = DefaultWidthLabel;
        public string HeightLabel { get; set; } = DefaultHeightLabel;

        /// <summary>Put-away moves by the <see cref="Transport"/> team instead of at once (see the class notes).</summary>
        public bool PutAwayByTransport { get; set; }

        /// <summary>Loads that may wait at the inbound point for the team (put-away by transport); ≥ 1.</summary>
        public int InboundCapacity { get; set; } = 1;

        /// <summary>While set, loads taken in are placed at once (no put-away move, no inbound limit): the initial stock,
        /// already in the racks when the run starts (<see cref="SimuLean.BaseStockReplenisher"/> sets it around its delivery).</summary>
        public bool StoreAtOnce { get; set; }

        /// <summary>Pass-through: each load is released to the output connection (not a list) after
        /// <see cref="MinimumDwellTime"/> (retrieved, then handed over when the receiver takes it). Off (default): a store.</summary>
        public bool ReleaseAfterDwell { get; set; }

        /// <summary>Time a load stays at least before it is released (<see cref="ReleaseAfterDwell"/>); ≥ 0.</summary>
        public double MinimumDwellTime { get; set; }

        private readonly List<Item> due = new List<Item>();   // dwell over, waiting for their release (oldest first)
        private int releasing;          // releases on their way to the outbound point
        private bool releaseScheduled;
        private int run;                // dwell events of an earlier run are ignored

        private int waitingInbound;     // taken in, not loaded by the team yet
        private int putAwaysInProgress; // taken in, not placed yet

        /// <summary>Put-aways taken in and not placed yet (waiting at the inbound point or on their way).</summary>
        public int PutAwaysInProgress => putAwaysInProgress;

        /// <summary>True when the load fits one position of <paramref name="lane"/> (always without
        /// <see cref="PhysicalDimensions"/>).</summary>
        public bool Fits(StorageLane lane, Item item)
        {
            if (!PhysicalDimensions || item == null) return true;
            return Within(item, LengthLabel, lane.SlotLength) && Within(item, WidthLabel, lane.SlotWidth) && Within(item, HeightLabel, lane.SlotHeight);
        }

        private static bool Within(Item item, string label, double room)
            => !(room > 0) || string.IsNullOrEmpty(label) || !(item.GetLabelOrDefault(label, 0) > room + 1e-9);

        // A lane that can take the load now: room, SKU rule, not being emptied, and its size.
        private bool Accepts(StorageLane lane, Item item) => lane.CanAccept(item) && Fits(lane, item);

        // Put-away by the team, at its limit: the upstream holds the next load.
        private bool InboundFull => PutAwayByTransport && !StoreAtOnce && Transport != null && waitingInbound >= Math.Max(1, InboundCapacity);

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
            stock.DefineFields(new[] { new ListFieldSpec { name = "sku", kind = ListFieldKind.BuiltIn, source = "item.type" } });
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
            if (InboundFull) return false;
            var candidates = new List<StorageLane>();
            foreach (StorageLane lane in lanes)
                if (Accepts(lane, theItem))
                    candidates.Add(lane);

            if (candidates.Count == 0)
                return false;

            StorageLane chosen = locationPolicy.SelectLane(candidates, theItem, BuildContext());
            if (chosen == null)
                return false;

            if (PutAwayByTransport && !StoreAtOnce && Transport != null && Location != null && NodeOf(chosen) != null)
            {
                PutAway(theItem, chosen);
                return true;
            }

            SetState(ElementState.PROCESSING);
            StoredItem record = chosen.Store(theItem, simClock.GetSimulationTime());
            records[theItem] = record;
            entries[theItem] = stock.PushItem(theItem, this);
            vElement.LoadItem(theItem);
            SetState(ElementState.IDLE);
            PublishedList?.Advertise(theItem, this);   // published stock: pullable from the model list
            StartDwell(theItem);
            return true;
        }

        // A put-away by the team: the place is reserved, the load waits at the inbound point until a member loads it (the
        // upstream may then send the next) and joins the stock where it is placed.
        private void PutAway(Item item, StorageLane lane)
        {
            lane.Reserve(item);
            waitingInbound++;
            putAwaysInProgress++;
            var record = new StoredItem { Item = item, StoredTime = simClock.GetSimulationTime() };
            records[item] = record;   // in no lane while it travels (as a relocated blocker)
            Move(item, Location, NodeOf(lane), placed =>
            {
                putAwaysInProgress--;
                record.StoredTime = simClock.GetSimulationTime();
                lane.PlaceReserved(record);
                entries[placed] = stock.PushItem(placed, this);
                vElement.LoadItem(placed);
                PublishedList?.Advertise(placed, this);
                StartDwell(placed);
                ScheduleWaitingPulls();   // its lane takes retrievals again
                ScheduleRelease();
            }, loaded =>
            {
                waitingInbound--;
                GetInput()?.NotifyAvailable(this);   // room at the inbound point
            });
        }

        // A lane that is not Direct and has a place promised to a load on its way (a put-away or a relocated blocker): the
        // load will land at the face, so no retrieval digs in it meanwhile.
        private static bool AwaitingArrival(StorageLane lane) => lane.Reserved > 0 && lane.AccessDiscipline != AccessDiscipline.Direct;

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
            // an item another retrieval is already fetching is not a candidate, nor one a puller of the
            // published stock has reserved or is waiting for
            ListLink published = PublishedList;
            if (!stock.TryPeek(FreeQuery(query ?? ListQuery.All), out ListEntry found))
                return false;
            StoredItem target = records[found.Item];

            StorageLane sourceLane = GetLane(target.LaneId);
            if (sourceLane == null)
                return false;      // in transit (a blocker being relocated) — not retrievable now
            if (AwaitingArrival(sourceLane))
                return false;      // a load is on its way to its face

            List<StoredItem> blockers = sourceLane.GetBlockers(target);
            foreach (StoredItem blocker in blockers)
                if (retrieving.Contains(blocker.Item))
                    return false;  // behind another retrieval's target in the same lane: not supported

            // Plan every blocker's destination up-front so the retrieval is
            // atomic: any unplaceable blocker fails the request with no moves.
            List<PlannedMove> plan = PlanRelocations(blockers, sourceLane);
            if (plan == null)
                return false;

            published?.Withdraw(target.Item);   // no longer pullable from the model list
            StartChain(plan, target, sourceLane, Location, item => Deliver(item, onRetrieved));
            return true;
        }

        // The query of a retrieval: the caller's, over the free stock. Built once per caller's query and reused while the
        // next request brings the same one; one slot, so nothing piles up.
        private ListQuery freeOf, freeQuery;

        private ListQuery FreeQuery(ListQuery query)
        {
            if (!ReferenceEquals(freeOf, query))
            {
                freeQuery = query.Where(e => !retrieving.Contains(e.Item) && !waitingPulls.Contains(e.Item) && IsUnreserved(e.Item));
                freeOf = query;
            }
            return freeQuery;
        }

        private bool IsUnreserved(Item item)
        {
            ListLink published = PublishedList;
            return published == null || published.ReservedFor(item) == null;
        }

        // A retrieval: marks the target, its lane and the reserved destinations, then runs the moves.
        private void StartChain(List<PlannedMove> plan, StoredItem target, StorageLane sourceLane, string deliverTo, Action<Item> onDone)
        {
            retrieving.Add(target.Item);
            sourceLane.RetrievalsInProgress++;
            foreach (PlannedMove move in plan) move.Destination.Reserve(move.Blocker.Item);
            ExecuteChain(plan, 0, target, sourceLane, deliverTo, onDone);
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
                    if (!ReferenceEquals(lane, sourceLane) && lane.RetrievalsInProgress == 0 && Fits(lane, blocker.Item) &&
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
                    plannedSku[destination] = blocker.Item.Name;
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
                string existing = lane.Count > 0 ? lane.Contents[0].Item.Name : lane.ReservedSku;
                if (existing == null)
                    plannedSku.TryGetValue(lane, out existing);
                if (existing != null && existing != item.Name)
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
                                  StorageLane sourceLane, string deliverTo, Action<Item> onDone)
        {
            if (index < plan.Count)
            {
                PlannedMove move = plan[index];
                sourceLane.Remove(move.Blocker);            // leaves the face when its move starts
                Action<Item> onArrival = delegate
                {
                    move.Destination.PlaceReserved(move.Blocker);   // its reserved place; StoredTime preserved
                    TotalRelocations++;
                    ExecuteChain(plan, index + 1, target, sourceLane, deliverTo, onDone);
                };

                if (!Move(move.Blocker.Item, NodeOf(sourceLane), NodeOf(move.Destination), onArrival))
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
                    Retrieved?.Invoke(this, target.Item);
                    onDone(target.Item);
                    ScheduleWaitingPulls();   // its lane is free again: waiting pulls may start
                    ScheduleRelease();
                };

                if (!Move(target.Item, NodeOf(sourceLane), deliverTo, onArrival))
                    onArrival(target.Item);
            }
        }

        /// <summary>The node a lane's loads are moved from / to: its own, else the storage's <see cref="Element.Location"/>
        /// (a rack whose lanes had no node moved every load at once although it had a
        /// transport team and a location).</summary>
        public string NodeOf(StorageLane lane) => lane?.NodeId ?? Location;

        /// <summary>A timed move by its transport team: travel to <paramref name="from"/>, load, travel to
        /// <paramref name="to"/>, unload, then <paramref name="onArrival"/>. False (nothing requested) without a
        /// team or a node — the caller moves it at once.</summary>
        private bool Move(Item item, string from, string to, Action<Item> onArrival, Action<Item> onLoaded = null)
        {
            if (Transport == null || from == null || to == null) return false;
            var request = new ResourceRequest(this, TaskType.Transport, (r, got) =>
            {
                Func<double> load = MoveLoadTime != null ? () => MoveLoadTime(item) : (Func<double>)null;
                Func<double> unload = MoveUnloadTime != null ? () => MoveUnloadTime(item) : (Func<double>)null;
                if (got.Count > 1)
                {
                    // several carriers together: they gather at `from`, the lead loads and carries, the others with it
                    new CarrierGroup(this, r, Transport.Dispatcher, Transport.Preemptable, from, "Move " + item.GetId(), "Help " + item.GetId(),
                        (lead, carrier) => lead.Load(item, load, onLoaded).Travel(to).Unload(item, unload).Callback(() => onArrival(item)))
                        .Start(got);
                    return;
                }
                var task = new TaskSequence(TaskType.Transport, this, "Move " + item.GetId())
                {
                    Priority = r.Priority, Preemptable = Transport.Preemptable,
                };
                // its carrier can be taken on its way, empty, to `from` (preemption): the move asks for a carrier again
                task.Interruption = new EmptyLegMove(() => Move(item, from, to, onArrival, onLoaded));
                task.Travel(from).Load(item, load, onLoaded)
                    .Travel(to).Unload(item, unload)
                    .Callback(() => onArrival(item));
                got[0].Execute(task);
            }, new List<ResourceRequirement>(Transport.Requirements).ToArray());
            request.Priority = Transport.Priority?.Invoke(item) ?? 1;
            request.Preempt = Transport.Preempt;
            request.Location = from;
            Transport.Dispatcher.Request(request);
            return true;
        }

        /// <summary>
        /// Hands a retrieved item downstream: it waits at the outbound point, in order, and leaves through the output
        /// link (stats fire via the link layer) when the receiver takes it — at once, when it has room later
        /// (<see cref="Unblock"/>) or when a carrier loads it (<see cref="SendHeldItem"/>); the requester's callback
        /// runs when it leaves. Without an output link it leaves at once, to the callback. An item the
        /// output refuses waits at the outbound point: it never leaves without its exit, so a carrier can take it.
        /// </summary>
        private void Deliver(Item item, Action<Item> onRetrieved)
        {
            vElement.UnloadItem(item);
            // No output, or the published stock list (not a destination: the item would be listed again): a programmatic
            // retrieval hands the item to its callback only.
            if (GetOutput() == null || GetOutput() is ListLink)
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
            ScheduleRelease();   // the outbound point is free: the next due load may go
        }

        // ── pass-through (minimum dwell time) ────────────────────────────────

        // A load joined the stock: with a pass-through output its release is due after the dwell.
        private void StartDwell(Item item)
        {
            if (!ReleaseAfterDwell || GetOutput() == null || GetOutput() is ListLink) return;   // a connection (a carrier's too), not a list
            int thisRun = run;
            simClock.Schedule(() =>
            {
                if (thisRun != run || !records.ContainsKey(item)) return;
                due.Add(item);
                ReleaseDue();
            }, Math.Max(0, MinimumDwellTime));
        }

        private void ScheduleRelease()
        {
            if (due.Count == 0 || releaseScheduled) return;
            releaseScheduled = true;
            simClock.Schedule(() => { releaseScheduled = false; ReleaseDue(); }, 0);
        }

        // Starts the release of the oldest due load that can be fetched now (its lane not waiting for a load, its
        // blockers re-homed), one at a time: none while another is on its way or waits at the outbound point.
        private void ReleaseDue()
        {
            if (releasing > 0 || outbound.Count > 0) return;
            for (int i = 0; i < due.Count; i++)
            {
                Item item = due[i];
                if (!records.TryGetValue(item, out StoredItem target) || retrieving.Contains(item)) { due.RemoveAt(i--); continue; }
                StorageLane lane = GetLane(target.LaneId);
                if (lane == null || AwaitingArrival(lane)) continue;
                List<StoredItem> blockers = lane.GetBlockers(target);
                bool behindAnother = false;
                foreach (StoredItem b in blockers) if (retrieving.Contains(b.Item)) { behindAnother = true; break; }
                if (behindAnother) continue;
                List<PlannedMove> plan = PlanRelocations(blockers, lane);
                if (plan == null) continue;   // no room to re-home its blockers yet
                due.RemoveAt(i);
                releasing++;
                StartChain(plan, target, lane, Location, released => { releasing--; Deliver(released, null); });
                return;
            }
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
            ProcessWaitingPulls();
            return any;
        }

        /// <summary>Hands a specific waiting item over (its carrier loads it, a list hands it to a puller). An item of the
        /// published stock a puller reserved starts its retrieval (or waits for its lane); at the end of the move it is
        /// handed over through the list link.</summary>
        public override bool SendHeldItem(Item item)
        {
            int i = -1;   // the first match, by index: no closure at every send
            for (int k = 0; k < outbound.Count; k++) if (outbound[k].Key == item) { i = k; break; }
            if (i >= 0)
            {
                if (!SendOut(item)) return false;
                var held = outbound[i];
                outbound.RemoveAt(i);
                Left(held.Key, held.Value);
                return true;
            }
            if (item == null || !records.ContainsKey(item)) return false;
            if (retrieving.Contains(item) || waitingPulls.Contains(item)) return true;   // on its way / already waiting
            waitingPulls.Add(item);
            ProcessWaitingPulls();
            return true;
        }

        // ── published stock (pulls from the model list) ─────────────────────

        private void ScheduleWaitingPulls()
        {
            if (waitingPulls.Count == 0 || waitingScheduled) return;
            waitingScheduled = true;
            simClock.Schedule(() => { waitingScheduled = false; ProcessWaitingPulls(); }, 0);
        }

        // Starts the waiting published retrievals that can start now, in the order they were pulled.
        private void ProcessWaitingPulls()
        {
            for (int i = 0; i < waitingPulls.Count;)
            {
                if (TryStartPublished(waitingPulls[i])) waitingPulls.RemoveAt(i);
                else i++;
            }
        }

        private bool TryStartPublished(Item item)
        {
            if (!records.TryGetValue(item, out StoredItem target) || retrieving.Contains(item)) return true;   // gone / started
            StorageLane lane = GetLane(target.LaneId);
            if (lane == null || AwaitingArrival(lane)) return false; // a blocker in transit / a load on its way here: later
            List<StoredItem> blockers = lane.GetBlockers(target);
            foreach (StoredItem b in blockers) if (retrieving.Contains(b.Item)) return false;   // behind another retrieval
            List<PlannedMove> plan = PlanRelocations(blockers, lane);
            if (plan == null) return false;                          // no room to re-home the blockers yet
            Element puller = PublishedList?.ReservedFor(item);
            StartChain(plan, target, lane, puller?.Location ?? Location, HandOverRetrieved);
            return true;
        }

        // End of a published retrieval: the item waits at the puller's location and leaves through the list link to the
        // puller that reserved it (if it refuses now, the link lists it again and its next pull takes it).
        private void HandOverRetrieved(Item item)
        {
            vElement.UnloadItem(item);
            outbound.Add(new KeyValuePair<Item, Action<Item>>(item, null));
            if (SendOut(item))
            {
                outbound.RemoveAll(p => p.Key == item);
                Left(item, null);
            }
        }

        // ── Element contract ─────────────────────────────────────────────────

        /// <summary>True when any lane can accept the item.</summary>
        public override bool CheckAvailability(Item theItem)
        {
            if (InboundFull) return false;
            foreach (StorageLane lane in lanes)
                if (Accepts(lane, theItem))
                    return true;
            return false;
        }

        /// <summary>Items in the storage: stored (including in-transit relocations and retrievals on their way out), put-aways
        /// not placed yet and retrieved ones waiting at the outbound point.</summary>
        public override int GetQueueLength() => stock.Count + outbound.Count + putAwaysInProgress;

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
            waitingPulls.Clear();
            waitingScheduled = false;
            waitingInbound = 0;
            putAwaysInProgress = 0;
            due.Clear();
            releasing = 0;
            releaseScheduled = false;
            run++;
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
            return simClock.Context.CreateRandomStream(Name, "locationPolicy");
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
