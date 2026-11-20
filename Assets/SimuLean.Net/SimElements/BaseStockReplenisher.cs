using System;
using System.Collections.Generic;
using SimuLean.Downtime;
using SimuLean.Resources;
using SimuLean.Results;
using SimuLean.Statistics;
using SimuLean.Storage;

namespace SimuLean
{
    /// <summary>
    /// Replenishes a warehouse by truckloads of what has been consumed (base-stock policy; Warehousing pack) — a source of unit
    /// loads (compound items: <see cref="SkuSpec.PiecesPerLoad"/> pieces each; type and label <c>sku</c> = the reference,
    /// as <see cref="PickFace"/> makes them).
    /// <list type="bullet">
    /// <item><b>Consumption.</b> <see cref="NotifyConsumed"/> (one unit load of a SKU, or several), called by model logic or
    ///   automatically for every item retrieved from an observed storage (<see cref="Observe"/>: the storage's
    ///   <see cref="StorageSystem.Retrieved"/> event, i.e. pulls from its published stock list and programmatic
    ///   retrievals). SKUs not in the table are ignored (one warning).</item>
    /// <item><b>Orders.</b> Consumed loads queue in consumption order; whenever <see cref="TruckloadSize"/> have
    ///   accumulated, an order for exactly those loads (the consumed mix) is placed. It arrives after
    ///   <see cref="LeadTime"/> (sampled; null = 0) — an absolute-time event, as arrivals are.</item>
    /// <item><b>Unloading.</b> The arrived truck (an item group: <see cref="Truck.Group"/>, type <c>Truck</c>, whose
    ///   sub-items are its loads) waits at the dock; its loads are released one by one through the output link in
    ///   order. Only within the <see cref="UnloadingWindows"/> (a <see cref="WeeklyShiftPattern"/> on the model calendar:
    ///   an internal <see cref="ShiftDowntime"/> stops the element OFF_SHIFT with its output blocked outside them) and
    ///   while it is not stopped (any downtime blocking its output). With a transport use on its output
    ///   (<see cref="ResourceTransportLink"/>, e.g. the forklift team of the storage) each load is carried — load,
    ///   travel, unload times — so one team can do both the unloading and the storage's retrievals; every waiting load
    ///   is offered so several members can unload in parallel. A load whose transport was requested before the window
    ///   closed is still carried.</item>
    /// <item><b>Initial stock.</b> <see cref="SkuSpec.InitialLoads"/> per SKU are created at time 0 and delivered at
    ///   once (a <see cref="DirectLink"/> from an internal generator, so the statistics stay link-driven) to
    ///   <see cref="InitialStockTarget"/> — default the first destination of its output link.</item>
    /// </list>
    /// Statistics: a generator (content not tracked; output = loads released). Metrics (<see cref="IElementMetrics"/>):
    /// trucks ordered / received / unloaded, loads released, lead time (mean, max), unloading time, consumed, backlog.
    /// Stock-outs show on the published stock list (<c>pending_pulls_avg</c>) and at the pick faces (<c>stockouts</c>).
    /// </summary>
    public class BaseStockReplenisher : Element, IElementMetrics
    {
        /// <summary>One reference of the table.</summary>
        public sealed class SkuSpec
        {
            public string Sku = "";
            public int PiecesPerLoad = 1;
            public int InitialLoads;
        }

        /// <summary>An order: placed, arriving, unloaded.</summary>
        public sealed class Truck
        {
            public int Id;
            public double OrderTime, ArrivalTime = double.NaN, UnloadedTime = double.NaN;
            /// <summary>The ordered loads, by SKU, in consumption order.</summary>
            public List<string> Skus = new List<string>();
            /// <summary>The truck as an item group (created on arrival; its sub-items are the loads).</summary>
            public Item Group;
            public int Remaining;
        }

        private readonly List<SkuSpec> skus = new List<SkuSpec>();
        private readonly Dictionary<string, SkuSpec> bySku = new Dictionary<string, SkuSpec>();
        private readonly List<KeyValuePair<Truck, Item>> pending = new List<KeyValuePair<Truck, Item>>();   // loads at the dock
        private readonly Queue<string> consumed = new Queue<string>();
        private readonly List<Truck> trucks = new List<Truck>();
        private readonly HashSet<StorageSystem> observed = new HashSet<StorageSystem>();
        private readonly HashSet<string> warned = new HashSet<string>();
        private readonly OutboundPort initialSource;
        private ShiftDowntime windows;
        private int seq, truckSeq;

        public BaseStockReplenisher(string name, SimClock simClock, IVElement vElement = null) : base(name, simClock, vElement)
        {
            StatsCollector.TracksContent = false;   // a generator: its loads never entered
            initialSource = new OutboundPort(name + "/initial", simClock, generates: true);
        }

        // ── configuration ────────────────────────────────────────────────────

        public IReadOnlyList<SkuSpec> Skus => skus;

        /// <summary>Adds (or replaces) a reference of the table.</summary>
        public void AddSku(string sku, int piecesPerLoad, int initialLoads = 0)
        {
            if (string.IsNullOrEmpty(sku)) throw new ArgumentException($"Replenisher '{Name}': a SKU has no name.");
            var spec = new SkuSpec { Sku = sku, PiecesPerLoad = Math.Max(0, piecesPerLoad), InitialLoads = Math.Max(0, initialLoads) };
            if (bySku.TryGetValue(sku, out SkuSpec old)) skus.Remove(old);
            skus.Add(spec);
            bySku[sku] = spec;
        }

        /// <summary>Unit loads per truck: an order is placed each time this many have been consumed (≥ 1).</summary>
        public int TruckloadSize { get; set; } = 1;

        /// <summary>Order → arrival time (null = 0; expressions see this element).</summary>
        public IRandomSampler LeadTime { get; set; }

        /// <summary>When trucks may be unloaded (null = always).</summary>
        public WeeklyShiftPattern UnloadingWindows { get; set; }

        /// <summary>Item type of the loads / pieces (null or empty = the SKU).</summary>
        public string LoadType { get; set; }
        public string PieceType { get; set; }

        /// <summary>Size (width, height, length) given to the loads it creates where their labels set none (their 3D model
        /// in a scene, <see cref="Item.SetSizeWhereMissing"/>); null = none.</summary>
        public double[] LoadModelSize { get; set; }

        /// <summary>Where the initial stock goes (null = the first destination of the output link).</summary>
        public Element InitialStockTarget { get; set; }

        // ── state / counters ─────────────────────────────────────────────────

        public int TrucksOrdered { get; private set; }
        public int TrucksReceived { get; private set; }
        public int TrucksUnloaded { get; private set; }
        public int LoadsReleased { get; private set; }
        public int ConsumedCount { get; private set; }
        /// <summary>Consumed loads not yet ordered.</summary>
        public int Backlog => consumed.Count;
        /// <summary>Loads waiting at the dock.</summary>
        public int AtDock => pending.Count;
        /// <summary>The loads waiting at the dock, in release order.</summary>
        public IEnumerable<Item> DockLoads { get { foreach (var p in pending) yield return p.Value; } }
        public StatTally LeadTimes { get; } = new StatTally();
        public StatTally UnloadTimes { get; } = new StatTally();
        /// <summary>Every order of the run (placed, arrived, unloaded).</summary>
        public IReadOnlyList<Truck> Trucks => trucks;

        public event Action<BaseStockReplenisher, Truck> OrderPlaced, TruckArrived, TruckUnloaded;
        /// <summary>A load was created (truck arrival or initial stock), before it leaves — a view gives it a visual here.</summary>
        public event Action<BaseStockReplenisher, Item> LoadCreated;

        // ── consumption ──────────────────────────────────────────────────────

        /// <summary>Counts every item retrieved from <paramref name="storage"/> as consumed (once per storage).</summary>
        public void Observe(StorageSystem storage)
        {
            if (storage == null || !observed.Add(storage)) return;
            storage.Retrieved += (s, item) => NotifyConsumed(PickFace.SkuOf(item));
        }

        /// <summary><paramref name="quantity"/> unit loads of <paramref name="sku"/> were consumed: may place orders.</summary>
        public void NotifyConsumed(string sku, int quantity = 1)
        {
            if (sku == null || !bySku.ContainsKey(sku))
            {
                if (warned.Add(sku ?? "")) SimLog.Warning($"[BaseStockReplenisher] '{Name}': consumed SKU '{sku}' is not in its table — ignored.");
                return;
            }
            for (int i = 0; i < quantity; i++) { consumed.Enqueue(sku); ConsumedCount++; }
            int size = Math.Max(1, TruckloadSize);
            while (consumed.Count >= size)
            {
                var truck = new Truck { Id = ++truckSeq, OrderTime = simClock.GetSimulationTime() };
                for (int i = 0; i < size; i++) truck.Skus.Add(consumed.Dequeue());
                PlaceOrder(truck);
            }
        }

        private void PlaceOrder(Truck truck)
        {
            trucks.Add(truck);
            TrucksOrdered++;
            OrderPlaced?.Invoke(this, truck);
            double lead = Math.Max(0, SimuLean.Expressions.Samplers.Sample(LeadTime, null, this));
            simClock.Schedule(() => Arrive(truck), lead);
        }

        private void Arrive(Truck truck)
        {
            double now = simClock.GetSimulationTime();
            truck.ArrivalTime = now;
            TrucksReceived++;
            LeadTimes.Update(now - truck.OrderTime);
            truck.Group = new Item(now);
            truck.Group.SetId("Truck", truck.Id, 0);
            foreach (string sku in truck.Skus)
            {
                Item load = CreateLoad(bySku[sku]);
                truck.Group.AddItem(load);
                pending.Add(new KeyValuePair<Truck, Item>(truck, load));
            }
            truck.Remaining = truck.Skus.Count;
            TruckArrived?.Invoke(this, truck);
            if (truck.Remaining == 0) Unloaded(truck);
            TryRelease();
        }

        /// <summary>A full unit load of <paramref name="spec"/> (type / label <c>sku</c> = the reference, pieces likewise).</summary>
        public Item CreateLoad(SkuSpec spec)
        {
            double now = simClock.GetSimulationTime();
            var load = new Item(now);
            load.SetId(string.IsNullOrEmpty(LoadType) ? spec.Sku : LoadType, ++seq, 0);
            load.SetLabelText(PickFace.SkuLabel, spec.Sku);
            load.SetSizeWhereMissing(LoadModelSize);
            for (int p = 0; p < spec.PiecesPerLoad; p++)
            {
                var piece = new Item(now);
                piece.SetId(string.IsNullOrEmpty(PieceType) ? spec.Sku : PieceType, ++seq, 0);
                piece.SetLabelText(PickFace.SkuLabel, spec.Sku);
                load.AddItem(piece);
            }
            LoadCreated?.Invoke(this, load);
            return load;
        }

        // ── release ──────────────────────────────────────────────────────────

        private bool TryRelease()
        {
            bool any = false;
            ILink output = GetOutput();
            if (output != null && !IsOutputBlocked)
            {
                for (int i = 0; i < pending.Count;)
                {
                    var p = pending[i];
                    if (SendOut(p.Value)) { pending.RemoveAt(i); Released(p.Key); any = true; continue; }
                    if (output is ResourceTransportLink) { i++; continue; }   // each load gets its own transport request
                    break;                                                   // plain link: in order
                }
                if (output is ListLink list) foreach (var p in pending) list.Advertise(p.Value, this);
            }
            UpdateState();
            return any;
        }

        /// <summary>Hands over a specific load at the dock (its transporter loads it, or a list pull reserved it).</summary>
        public override bool SendHeldItem(Item item)
        {
            int i = -1;   // the first match, by index: no closure at every send
            for (int k = 0; k < pending.Count; k++) if (pending[k].Value == item) { i = k; break; }
            if (i < 0 || !SendOut(item)) return false;
            var truck = pending[i].Key;
            pending.RemoveAt(i);
            Released(truck);
            UpdateState();
            return true;
        }

        private void Released(Truck truck)
        {
            LoadsReleased++;
            vElement.ReportState("Exit");
            if (--truck.Remaining == 0) Unloaded(truck);
        }

        private void Unloaded(Truck truck)
        {
            truck.UnloadedTime = simClock.GetSimulationTime();
            TrucksUnloaded++;
            UnloadTimes.Update(truck.UnloadedTime - truck.ArrivalTime);
            TruckUnloaded?.Invoke(this, truck);
        }

        private void UpdateState() => SetState(pending.Count > 0 ? ElementState.BLOCKED : ElementState.IDLE);

        // ── lifecycle ────────────────────────────────────────────────────────

        public override void Start()
        {
            base.Start();
            pending.Clear(); consumed.Clear(); trucks.Clear(); warned.Clear();
            seq = truckSeq = 0;
            TrucksOrdered = TrucksReceived = TrucksUnloaded = LoadsReleased = ConsumedCount = 0;
            LeadTimes.Reset(); UnloadTimes.Reset();
            initialSource.Start();
            windows?.CancelPending();
            windows = null;
            if (UnloadingWindows != null)
            {
                windows = new ShiftDowntime(this, UnloadingWindows, simClock)
                {
                    Name = Name + "/unloading", State = ElementState.OFF_SHIFT, Mode = StopMode.Immediate,
                    BlockInput = false, BlockOutput = true,
                };
                windows.Start();
            }
            bool initial = false;
            foreach (var s in skus) initial |= s.InitialLoads > 0;
            if (initial) simClock.Schedule(DeliverInitialStock, 0);   // after every element has started (the storage too)
            UpdateState();
        }

        private void DeliverInitialStock()
        {
            Element target = InitialStockTarget ?? FirstDestination();
            var link = target != null ? new DirectLink(target) : null;
            var storage = target as SimuLean.Storage.StorageSystem;
            if (storage != null) storage.StoreAtOnce = true;   // the initial stock is in place at the start, not put away
            try
            {
                DeliverInitialLoads(link);
            }
            finally
            {
                if (storage != null) storage.StoreAtOnce = false;
            }
        }

        private void DeliverInitialLoads(DirectLink link)
        {
            foreach (var spec in skus)
                for (int k = 0; k < spec.InitialLoads; k++)
                {
                    Item load = CreateLoad(spec);
                    if (link != null && link.SendItem(load, initialSource)) continue;
                    // No target, or it refused (full): released through the output like a truck's load.
                    SimLog.Warning($"[BaseStockReplenisher] '{Name}': initial load of '{spec.Sku}' could not be stored at once — released through the output.");
                    var t = new Truck { Id = 0, OrderTime = 0, ArrivalTime = 0, Remaining = 1 };
                    pending.Add(new KeyValuePair<Truck, Item>(t, load));
                }
            TryRelease();
        }

        private Element FirstDestination()
        {
            switch (GetOutput())
            {
                case GeneralLink g: return g.Outputs.Count > 0 ? g.Outputs[0] : null;
                case ResourceTransportLink r: return r.Destinations.Count > 0 ? r.Destinations[0] : null;
                default: return null;
            }
        }

        // ── Element contract ─────────────────────────────────────────────────

        /// <summary>Loads at the dock (arrived, not yet handed over).</summary>
        public override int GetQueueLength() => pending.Count;
        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item theItem) => false;
        public override bool Receive(Item theItem) => false;
        /// <summary>Downstream has room, or a window opened / a stop ended: releases the waiting loads.</summary>
        public override bool Unblock() => TryRelease();

        public void ReportMetrics(Action<string, double> set)
        {
            set(ResultMetrics.TrucksOrdered, TrucksOrdered);
            set(ResultMetrics.TrucksReceived, TrucksReceived);
            set(ResultMetrics.TrucksUnloaded, TrucksUnloaded);
            set(ResultMetrics.LoadsReleased, LoadsReleased);
            set(ResultMetrics.LeadTimeAvg, LeadTimes.Average);
            set(ResultMetrics.LeadTimeMax, LeadTimes.Max);
            set(ResultMetrics.UnloadTimeAvg, UnloadTimes.Average);
            set(ResultMetrics.Consumed, ConsumedCount);
            set(ResultMetrics.Backlog, Backlog);
        }
    }
}
