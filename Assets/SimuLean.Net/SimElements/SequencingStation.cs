using System;
using System.Collections.Generic;
using SimuLean.Expressions;
using SimuLean.Resources;
using SimuLean.Results;
using SimuLean.Statistics;

namespace SimuLean
{
    /// <summary>Who holds the operator(s) of a <see cref="SequencingStation"/>.</summary>
    public enum SequencingOperatorScope
    {
        /// <summary>One request per carrier: held from the preparation to the close (default).</summary>
        Carrier,
        /// <summary>One request per piece: held from taking the piece to placing it; preparation, gaps and close need no operator.</summary>
        Piece,
    }

    /// <summary>
    /// Order input of a <see cref="SequencingStation"/> (port <c>orders</c>): call-off orders wait here in arrival order
    /// (= the vehicle sequence). The station takes them one per carrier slot; a served order leaves through the port's
    /// output link (e.g. to a sink that counts them) or, unwired, ends here through the <see cref="ConsumeLink"/> — either
    /// way its exit is recorded by a link, so content, waiting time (= stay) and the balance are exact.
    /// </summary>
    public sealed class SequencingOrderPort : Element
    {
        private readonly LinkedList<Item> waiting = new LinkedList<Item>();
        private readonly Dictionary<Item, double> arrivedAt = new Dictionary<Item, double>();
        private readonly Action<Item> onArrived;
        private readonly Queue<Item> outgoing = new Queue<Item>();

        /// <param name="capacity">Orders it holds (waiting + in work); negative = unbounded.</param>
        public SequencingOrderPort(string name, SimClock clock, int capacity, Action<Item> onArrived, IVElement vElement = null)
            : base(name, clock, vElement)
        {
            Capacity = capacity;
            this.onArrived = onArrived;
        }

        /// <summary>Negative = unbounded.</summary>
        public int Capacity { get; set; }

        /// <summary>Orders waiting to be served (not yet taken by a slot).</summary>
        public int Waiting => waiting.Count;

        /// <summary>Orders taken by the station and not yet served.</summary>
        public int InWork { get; private set; }

        /// <summary>The waiting orders, oldest first.</summary>
        public IEnumerable<Item> Orders => waiting;

        public Item Peek() => waiting.First?.Value;

        /// <summary>When the order arrived here (NaN if unknown).</summary>
        public double ArrivalOf(Item order) => order != null && arrivedAt.TryGetValue(order, out double t) ? t : double.NaN;

        public override void Start()
        {
            base.Start();
            waiting.Clear(); arrivedAt.Clear(); outgoing.Clear();
            InWork = 0;
        }

        /// <summary>The station takes the oldest waiting order for a slot.</summary>
        internal Item TakeNext()
        {
            if (waiting.Count == 0) return null;
            Item o = waiting.First.Value;
            waiting.RemoveFirst();
            InWork++;
            return o;
        }

        /// <summary>The order has been served: it leaves through the output link, or ends here when unwired.</summary>
        internal void Served(Item order)
        {
            if (order == null) return;
            InWork = Math.Max(0, InWork - 1);
            arrivedAt.Remove(order);
            if (GetOutput() == null) { ConsumeLink.Instance.SendItem(order, this); NotifyInput(); return; }
            outgoing.Enqueue(order);
            Flush();
            NotifyInput();
        }

        private bool Flush()
        {
            while (outgoing.Count > 0)
            {
                if (!SendOut(outgoing.Peek())) return false;
                outgoing.Dequeue();
            }
            return true;
        }

        public override int GetQueueLength() => waiting.Count + InWork + outgoing.Count;
        public override int GetFreeCapacity() => Capacity < 0 ? int.MaxValue : Math.Max(0, Capacity - GetQueueLength());
        public override bool CheckAvailability(Item theItem) => Capacity < 0 || GetQueueLength() < Capacity;

        public override bool Receive(Item theItem)
        {
            if (theItem == null || !CheckAvailability(theItem)) return false;
            waiting.AddLast(theItem);
            arrivedAt[theItem] = simClock.GetSimulationTime();
            SetState(ElementState.RECEIVING);
            onArrived?.Invoke(theItem);
            return true;
        }

        public override bool Unblock() => outgoing.Count > 0 && Flush();
    }

    /// <summary>
    /// Piece input of a <see cref="SequencingStation"/> (port <c>pieces</c>). With <see cref="Capacity"/> 0 (default) it
    /// takes pieces <b>on demand</b>: only while the station needs one, and only one whose key (<see cref="KeyOf"/>)
    /// equals the reference the current order needs — pulled from a list (the list's query plus this filter) or offered
    /// by a plain link (a sequenced feed). With a capacity &gt; 0 (or −1 = unbounded) it is a stock buffer that takes
    /// any piece pushed to it. A piece placed into a carrier leaves the port through the <see cref="ConsumeLink"/>.
    /// </summary>
    public sealed class SequencingPiecePort : Element
    {
        private readonly Dictionary<string, Queue<Item>> store = new Dictionary<string, Queue<Item>>();
        private readonly Action<Item> onArrived;
        private int count;

        public SequencingPiecePort(string name, SimClock clock, int capacity, Func<Item, string> keyOf, Action<Item> onArrived,
                                   IVElement vElement = null)
            : base(name, clock, vElement)
        {
            Capacity = capacity;
            KeyOf = keyOf ?? (i => i.Name);
            this.onArrived = onArrived;
        }

        /// <summary>0 = on demand (default); &gt; 0 = buffer of that size; negative = unbounded buffer.</summary>
        public int Capacity { get; set; }

        /// <summary>The reference of a piece.</summary>
        public Func<Item, string> KeyOf { get; }

        /// <summary>The reference the station waits for (null = none).</summary>
        public string Demand { get; private set; }

        /// <summary>Pieces held.</summary>
        public int Count => count;

        public override void Start()
        {
            base.Start();
            store.Clear(); count = 0; Demand = null;
        }

        /// <summary>The station needs a piece of <paramref name="key"/>: the input is asked again (pulls re-evaluated).</summary>
        internal void SetDemand(string key)
        {
            Demand = key;
            NotifyInput();
        }

        internal void ClearDemand() => Demand = null;

        /// <summary>Takes a held piece of <paramref name="key"/> (FIFO) and records its exit, or null.</summary>
        internal Item Take(string key)
        {
            if (key == null || !store.TryGetValue(key, out var q) || q.Count == 0) return null;
            Item p = q.Dequeue();
            count--;
            ConsumeLink.Instance.SendItem(p, this);   // it leaves the port into the carrier (link layer)
            if (Capacity != 0) NotifyInput();         // buffer room again
            return p;
        }

        public bool Has(string key) => key != null && store.TryGetValue(key, out var q) && q.Count > 0;

        private bool OnDemand => Capacity == 0;

        public override int GetQueueLength() => count;

        public override int GetFreeCapacity()
        {
            if (OnDemand) return Demand != null && count == 0 ? 1 : 0;
            return Capacity < 0 ? int.MaxValue : Math.Max(0, Capacity - count);
        }

        public override bool CheckAvailability(Item theItem)
        {
            if (OnDemand) return Demand != null && count == 0 && theItem != null && KeyOf(theItem) == Demand;
            return Capacity < 0 || count < Capacity;
        }

        public override bool Receive(Item theItem)
        {
            if (theItem == null || !CheckAvailability(theItem)) return false;
            string key = KeyOf(theItem) ?? "";
            if (!store.TryGetValue(key, out var q)) store[key] = q = new Queue<Item>();
            q.Enqueue(theItem);
            count++;
            onArrived?.Invoke(theItem);
            return true;
        }

        public override bool Unblock() => false;   // nothing to send: it is the station's input
    }

    /// <summary>
    /// Just-in-sequence sequencing / kitting station: fills <b>carriers</b> (wagons, totes) with one piece per
    /// <b>order</b> in the order sequence. Inputs: the main input takes empty carriers (one at a time); the port
    /// <c>orders</c> (<see cref="SequencingOrderPort"/>) takes call-off orders, FIFO = vehicle sequence; the port
    /// <c>pieces</c> (<see cref="SequencingPiecePort"/>) gets one piece per order, by default pulled from a list on demand
    /// (<c>pieceField == the order's reference</c>). Per carrier:
    /// <list type="number">
    /// <item><b>Lot.</b> It starts when a carrier is present and at least <see cref="LotSize"/> orders wait (default =
    ///   the carrier capacity; with <see cref="OldestOrderMaxWait"/> &gt; 0 also once the oldest order has waited that long). State
    ///   WAITING while one of them is missing (IDLE when both are).</item>
    /// <item><b>Operator.</b> With <see cref="Resources"/> (scope <see cref="OperatorScope"/>), WAITING_FOR_RESOURCE until
    ///   assigned; the request carries the oldest order's time (<see cref="ResourceRequest.OldestItemTime"/>), so a
    ///   dispatcher order <c>|oldestItemTime</c> serves first the station whose lot holds the oldest order.</item>
    /// <item><b>Timeline</b> (PROCESSING, all timed work through <see cref="Element.ScheduleWork"/>): <see cref="PrepareTime"/>
    ///   (carrier) → for each slot i: the next order; if it needs a piece (<see cref="Applies"/>, default: its reference
    ///   label is not empty) the piece is taken — WAITING_FOR_MATERIAL (a stock-out, counted with its time) until one
    ///   arrives — then <see cref="PickTime"/> + <see cref="ExtraTime"/> and <see cref="PlaceTime"/> (expressions read the
    ///   piece, which carries the order's labels); the piece becomes a sub-item of the carrier with label <c>slot</c> = i;
    ///   otherwise <see cref="GapTime"/> (the order) leaves the slot empty → <see cref="CloseTime"/> (carrier) → the full
    ///   carrier leaves through the output link (BLOCKED while refused).</item>
    /// </list>
    /// An order is served (and leaves its port) when its piece is placed or its gap ends; its wait = served − its
    /// <see cref="OrderTimeLabel"/> (default <c>callOffTime</c>; else its arrival at the port). Carrier labels:
    /// <c>firstVehicle</c>, <c>lastVehicle</c> (the orders' <see cref="VehicleLabel"/>), <c>pieces</c>, <c>gaps</c>,
    /// <c>closeTime</c>. Statistics: carriers by the link layer (input / output / stay); orders and pieces by their ports'
    /// links; the counters and tallies below are reported through <see cref="IElementMetrics"/>.
    /// </summary>
    public class SequencingStation : Element, IResourceUser, IPortedElement, IElementMetrics, IInterruptibleStation
    {
        public const string PortOrders = "orders", PortPieces = "pieces";
        public const string LabelSlot = "slot", LabelFirstVehicle = "firstVehicle", LabelLastVehicle = "lastVehicle",
                            LabelPieces = "pieces", LabelGaps = "gaps", LabelCloseTime = "closeTime";

        /// <summary>The station is waiting for orders or a carrier.</summary>
        public static readonly ElementState WAITING = ElementState.Of("WAITING");
        /// <summary>The piece the current order needs is not in stock (stock-out).</summary>
        public static readonly ElementState WAITING_FOR_MATERIAL = ElementState.Of("WAITING_FOR_MATERIAL");

        private enum Phase { None, WaitingLot, WaitingResource, Preparing, WaitingOrder, WaitingPiece, PieceResource, Picking, Placing, Gap, Closing, Blocked }

        private readonly SequencingOrderPort orderPort;
        private readonly SequencingPiecePort piecePort;
        private readonly List<KeyValuePair<string, Element>> ports;
        private readonly EntityScope scope = new EntityScope();

        private Phase phase;
        private Item carrier, order, piece;
        private int carrierCapacity, slot, placedInCarrier, gapsInCarrier;
        private string firstVehicle, lastVehicle;
        private ResourceJob carrierJob, pieceJob;
        private double materialWaitStart, lotOldest;
        private EventHandle maxWaitTimer;
        private readonly ActionEvent workDone;
        private Action afterWork;

        public SequencingStation(string name, SimClock clock, IVElement vElement = null) : base(name, clock, vElement)
        {
            orderPort = new SequencingOrderPort(name + "." + PortOrders, clock, -1, OnOrderArrived);
            piecePort = new SequencingPiecePort(name + "." + PortPieces, clock, 0, PieceKey, OnPieceArrived);
            ports = new List<KeyValuePair<string, Element>>
            {
                new KeyValuePair<string, Element>(PortOrders, orderPort),
                new KeyValuePair<string, Element>(PortPieces, piecePort),
            };
            workDone = new ActionEvent(() => { var a = afterWork; afterWork = null; a?.Invoke(); });
        }

        // ── settings ─────────────────────────────────────────────────────────

        /// <summary>Slots of a carrier without a numeric <see cref="CapacityLabel"/>.</summary>
        public int CarrierCapacity { get; set; } = 9;
        /// <summary>Carrier label with its number of slots (wins over <see cref="CarrierCapacity"/>).</summary>
        public string CapacityLabel { get; set; } = "capacity";
        /// <summary>Orders that must wait before a carrier starts (≤ 0 = the carrier's capacity).</summary>
        public int LotSize { get; set; }
        /// <summary>&gt; 0: a carrier also starts once the oldest waiting order has waited this long (with fewer orders).</summary>
        public double OldestOrderMaxWait { get; set; }
        /// <summary>True (default): a slot waits for its order; false: the carrier closes when no order waits at a slot.</summary>
        public bool WaitForFullCarrier { get; set; } = true;
        /// <summary>Order label with the part reference it needs.</summary>
        public string ReferenceLabel { get; set; } = "reference";
        /// <summary>Piece label compared with the reference (empty or <c>type</c> = the item type).</summary>
        public string PieceField { get; set; } = "sku";
        /// <summary>Order label with its call-off time (else: its arrival at the order port).</summary>
        public string OrderTimeLabel { get; set; } = "callOffTime";
        /// <summary>Order label with the vehicle (sequence number), copied to <c>firstVehicle</c> / <c>lastVehicle</c>.</summary>
        public string VehicleLabel { get; set; } = "vehicle";
        /// <summary>Whether an order needs a piece (expression over the order and the station); null = its reference label is not empty.</summary>
        public Expression Applies { get; set; }
        /// <summary>Copy the order's labels onto its piece (labels the piece already has are kept).</summary>
        public bool CopyOrderLabels { get; set; } = true;
        public SequencingOperatorScope OperatorScope { get; set; } = SequencingOperatorScope.Carrier;

        /// <summary>Samplers (null = 0). Item seen by expressions: the carrier (prepare, close), the piece (pick, extra,
        /// place), the order (gap).</summary>
        public IRandomSampler PrepareTime { get; set; }
        public IRandomSampler PickTime { get; set; }
        public IRandomSampler ExtraTime { get; set; }
        public IRandomSampler PlaceTime { get; set; }
        public IRandomSampler GapTime { get; set; }
        public IRandomSampler CloseTime { get; set; }

        /// <summary>Process resources (operators); null = none.</summary>
        public StationResources Resources { get; set; }

        // ── ports and views ─────────────────────────────────────────────────

        public SequencingOrderPort OrderPort => orderPort;
        public SequencingPiecePort PiecePort => piecePort;
        public IReadOnlyList<KeyValuePair<string, Element>> Ports => ports;

        public Element GetPort(string port)
        {
            if (string.Equals(port, PortOrders, StringComparison.OrdinalIgnoreCase)) return orderPort;
            if (string.Equals(port, PortPieces, StringComparison.OrdinalIgnoreCase)) return piecePort;
            return null;
        }

        /// <summary>The carrier at the station (null = none).</summary>
        public Item Carrier => carrier;
        /// <summary>Slots of the current carrier.</summary>
        public int CurrentCapacity => carrierCapacity;
        /// <summary>The slot being worked on.</summary>
        public int CurrentSlot => slot;
        /// <summary>The order / piece of the current slot (null = none).</summary>
        public Item CurrentOrder => order;
        public Item CurrentPiece => piece;

        /// <summary>A carrier started (after its operator was assigned): before the preparation.</summary>
        public event Action<SequencingStation, Item> CarrierStarted;
        /// <summary>A piece was taken for slot i: (station, carrier, piece, slot, pick + extra + place time).</summary>
        public event Action<SequencingStation, Item, Item, int, double> PieceTaken;
        /// <summary>A piece was placed in its slot: (station, carrier, piece, slot).</summary>
        public event Action<SequencingStation, Item, Item, int> PiecePlaced;
        /// <summary>A slot was left empty: (station, carrier, order, slot).</summary>
        public event Action<SequencingStation, Item, Item, int> GapLeft;
        /// <summary>An order was served (piece placed or gap): (station, order).</summary>
        public event Action<SequencingStation, Item> OrderServed;
        /// <summary>The carrier is closed (labels set), before it is sent.</summary>
        public event Action<SequencingStation, Item> CarrierClosed;
        /// <summary>The carrier left.</summary>
        public event Action<SequencingStation, Item> CarrierLeft;

        // ── statistics (state changes, not item flow) ─────────────────────────

        public int CarriersCompleted { get; private set; }
        public int PiecesPlaced { get; private set; }
        public int Gaps { get; private set; }
        /// <summary>Waits for a piece longer than zero (a pull from a list that hands over at once is not one); ended ones.</summary>
        public int StockOuts { get; private set; }
        /// <summary>Stock-out waits (one value per ended stock-out).</summary>
        public StatTally StockOutWait { get; } = new StatTally();
        /// <summary>Order wait: call-off (or arrival) → served.</summary>
        public StatTally OrderWait { get; } = new StatTally();
        /// <summary>Lot wait: the lot's oldest order → carrier start.</summary>
        public StatTally LotWait { get; } = new StatTally();

        public void ReportMetrics(Action<string, double> set)
        {
            double now = simClock.GetSimulationTime();
            set(ResultMetrics.Carriers, CarriersCompleted);
            set(ResultMetrics.Pieces, PiecesPlaced);
            set(ResultMetrics.Gaps, Gaps);
            double open = phase == Phase.WaitingPiece ? now - materialWaitStart : 0;   // a stock-out still going on
            set(ResultMetrics.StockOuts, StockOuts + (open > SimTime.Epsilon ? 1 : 0));
            set(ResultMetrics.StockOutTime, StockOutWait.Average * StockOutWait.Count + open);
            set(ResultMetrics.OrderWaitAvg, OrderWait.Average);
            set(ResultMetrics.OrderWaitMax, OrderWait.Max);
            set(ResultMetrics.LotWaitAvg, LotWait.Average);
            set(ResultMetrics.OrdersWaitingAvg, orderPort.GetStatsContentAvgTW());
        }

        private void ResetCounters()
        {
            CarriersCompleted = PiecesPlaced = Gaps = StockOuts = 0;
            StockOutWait.Reset(); OrderWait.Reset(); LotWait.Reset();
        }

        public override void ResetStatistics()
        {
            base.ResetStatistics();
            orderPort.ResetStatistics();
            piecePort.ResetStatistics();
            ResetCounters();
            if (phase == Phase.WaitingPiece) materialWaitStart = simClock.GetSimulationTime();
        }

        public override void Start()
        {
            base.Start();
            orderPort.Start();
            piecePort.Start();
            ResetCounters();
            maxWaitTimer?.Cancel(); maxWaitTimer = null;
            phase = Phase.None;
            carrier = order = piece = null;
            carrierJob = pieceJob = null;
            afterWork = null;
            slot = placedInCarrier = gapsInCarrier = 0;
            firstVehicle = lastVehicle = null;
        }

        // ── keys and times ───────────────────────────────────────────────────

        private string PieceKey(Item p)
        {
            if (p == null) return null;
            if (string.IsNullOrEmpty(PieceField) || PieceField == "type") return p.Name;
            return p.HasLabel(PieceField) ? p.GetLabelText(PieceField) : p.Name;
        }

        /// <summary>The reference an order needs ("" = none).</summary>
        public string ReferenceOf(Item o) => o != null && !string.IsNullOrEmpty(ReferenceLabel) && o.HasLabel(ReferenceLabel)
            ? o.GetLabelText(ReferenceLabel) ?? "" : "";

        /// <summary>Whether the order needs a piece.</summary>
        public bool NeedsPiece(Item o)
        {
            if (Applies != null) { scope.RandomStream = RandomStreamFor("applies"); return Applies.Condition(scope.Set(o, this)); }
            return ReferenceOf(o).Length > 0;
        }

        /// <summary>The order's time: its <see cref="OrderTimeLabel"/>, else its arrival at the order port.</summary>
        public double OrderTimeOf(Item o)
        {
            if (o != null && !string.IsNullOrEmpty(OrderTimeLabel))
            {
                double? v = o.GetLabelValue(OrderTimeLabel);
                if (v.HasValue && !double.IsNaN(v.Value)) return v.Value;
            }
            double a = orderPort.ArrivalOf(o);
            return double.IsNaN(a) ? simClock.GetSimulationTime() : a;
        }

        private int CapacityOf(Item c)
        {
            if (c != null && !string.IsNullOrEmpty(CapacityLabel))
            {
                double? v = c.GetLabelValue(CapacityLabel);
                if (v.HasValue && v.Value >= 1) return (int)v.Value;
            }
            return Math.Max(1, CarrierCapacity);
        }

        private int LotTarget => Math.Max(1, LotSize > 0 ? LotSize : carrierCapacity);

        private double Sample(IRandomSampler s, Item item) => s == null ? 0 : Math.Max(0, Samplers.Sample(s, item, this));

        private void Work(double delay, Action then)
        {
            afterWork = then;
            var work = ScheduleWork(workDone, delay);
            // the operator's job (the carrier's, or the piece's) is this step now: it may be taken during the step
            if (Resources != null) Resources.ProcessStarted(OperatorScope == SequencingOperatorScope.Carrier ? carrierJob : pieceJob, work);
        }

        /// <summary>The operator's job lost its resource (preemption): the station waits for it, the current timed step
        /// paused (Restart: that step starts again); then it goes on.</summary>
        void IInterruptibleStation.JobPaused(ResourceJob job, bool paused, bool restart)
            => SetState(paused ? ElementState.WAITING_FOR_RESOURCE : ElementState.PROCESSING);

        // ── carriers (main input) ────────────────────────────────────────────

        public override bool CheckAvailability(Item theItem) => carrier == null;

        public override bool Receive(Item theItem)
        {
            if (theItem == null || carrier != null) return false;
            carrier = theItem;
            carrierCapacity = CapacityOf(theItem);
            slot = placedInCarrier = gapsInCarrier = 0;
            firstVehicle = lastVehicle = null;
            phase = Phase.WaitingLot;
            vElement.LoadItem(theItem);
            TryStart();
            return true;
        }

        public override int GetQueueLength() => carrier != null ? 1 : 0;
        public override int GetFreeCapacity() => carrier == null ? 1 : 0;

        // ── orders ───────────────────────────────────────────────────────────

        private void OnOrderArrived(Item o)
        {
            if (phase == Phase.WaitingOrder) NextSlot();
            else if (phase == Phase.None || phase == Phase.WaitingLot) TryStart();
        }

        private void ShowWaiting()
        {
            if (carrier == null && orderPort.Waiting == 0) SetState(ElementState.IDLE);
            else SetState(WAITING);
        }

        private void TryStart()
        {
            if (carrier == null || phase != Phase.WaitingLot) { if (phase == Phase.None) ShowWaiting(); return; }
            int n = orderPort.Waiting;
            bool ready = n >= LotTarget;
            if (!ready && OldestOrderMaxWait > 0 && n > 0)
            {
                double due = OrderTimeOf(orderPort.Peek()) + OldestOrderMaxWait;
                double now = simClock.GetSimulationTime();
                if (now >= due - SimTime.Epsilon) ready = true;
                else if (maxWaitTimer == null) maxWaitTimer = simClock.Schedule(() => { maxWaitTimer = null; TryStart(); }, due - now);
            }
            if (!ready) { ShowWaiting(); return; }
            maxWaitTimer?.Cancel(); maxWaitTimer = null;

            double oldest = OrderTimeOf(orderPort.Peek());
            lotOldest = oldest;
            if (Resources != null && OperatorScope == SequencingOperatorScope.Carrier)
            {
                phase = Phase.WaitingResource;
                SetState(ElementState.WAITING_FOR_RESOURCE);
                carrierJob = Resources.Acquire(this, carrier, Prepare, oldest);
            }
            else Prepare();
        }

        // ── the carrier's timeline ───────────────────────────────────────────

        private void Prepare()
        {
            LotWait.Update(simClock.GetSimulationTime() - lotOldest);   // oldest order of the lot → the carrier starts
            phase = Phase.Preparing;
            SetState(ElementState.PROCESSING);
            CarrierStarted?.Invoke(this, carrier);
            vElement.ReportState("Prepare");
            Work(Sample(PrepareTime, carrier), () => { slot = 0; NextSlot(); });
        }

        private void NextSlot()
        {
            if (slot >= carrierCapacity) { Close(); return; }
            if (orderPort.Waiting == 0)
            {
                if (!WaitForFullCarrier && slot > 0) { Close(); return; }
                phase = Phase.WaitingOrder;
                SetState(WAITING);
                return;
            }
            order = orderPort.TakeNext();
            if (!NeedsPiece(order))
            {
                phase = Phase.Gap;
                SetState(ElementState.PROCESSING);
                Work(Sample(GapTime, order), GapDone);
                return;
            }
            if (Resources != null && OperatorScope == SequencingOperatorScope.Piece)
            {
                phase = Phase.PieceResource;
                SetState(ElementState.WAITING_FOR_RESOURCE);
                pieceJob = Resources.Acquire(this, order, GetPiece, OrderTimeOf(order));
            }
            else GetPiece();
        }

        private void GetPiece()
        {
            string key = ReferenceOf(order);
            Item p = piecePort.Take(key);
            if (p != null) { Pick(p); return; }
            phase = Phase.WaitingPiece;
            materialWaitStart = simClock.GetSimulationTime();
            SetState(WAITING_FOR_MATERIAL);
            piecePort.SetDemand(key);
        }

        private void OnPieceArrived(Item p)
        {
            if (phase != Phase.WaitingPiece || !piecePort.Has(ReferenceOf(order))) return;
            piecePort.ClearDemand();
            double waited = simClock.GetSimulationTime() - materialWaitStart;
            if (waited > SimTime.Epsilon) { StockOuts++; StockOutWait.Update(waited); }   // a pull from a list hands over at once: no stock-out
            phase = Phase.Picking;
            // Take it after the hand-over: the link records the port's entry when Receive has returned.
            simClock.Schedule(() => Pick(piecePort.Take(ReferenceOf(order))), 0);
        }

        private void Pick(Item p)
        {
            piece = p;
            if (CopyOrderLabels) CopyLabelsKeeping(order, piece);
            piece.SetLabelValue(LabelSlot, slot);
            phase = Phase.Picking;
            SetState(ElementState.PROCESSING);
            double pick = Sample(PickTime, piece) + Sample(ExtraTime, piece);
            double place = Sample(PlaceTime, piece);
            PieceTaken?.Invoke(this, carrier, piece, slot, pick + place);
            Work(pick, () =>
            {
                phase = Phase.Placing;
                Work(place, Placed);
            });
        }

        private static void CopyLabelsKeeping(Item from, Item to)
        {
            if (from == null || !from.HasProperties) return;
            var names = new List<string>(from.Properties.Names);
            foreach (string n in names)
            {
                if (to.HasLabel(n)) continue;
                switch (from.Properties.KindOf(n))
                {
                    case PropertyKind.Double: to.Properties.SetDouble(n, from.Properties.GetDouble(n)); break;
                    case PropertyKind.String: to.Properties.SetString(n, from.Properties.TryGetString(n, out string s) ? s : ""); break;
                    case PropertyKind.Reference: to.Properties.SetRef(n, from.Properties.GetRef(n)); break;
                }
            }
        }

        private void Placed()
        {
            carrier.AddItem(piece);
            PiecesPlaced++;
            placedInCarrier++;
            Item p = piece, o = order;
            piece = null;
            PiecePlaced?.Invoke(this, carrier, p, slot);
            vElement.ReportState("Placed");
            if (pieceJob != null) { Resources.ProcessEnded(pieceJob); Resources.ItemLeft(pieceJob); pieceJob = null; }
            Serve(o);
            slot++;
            NextSlot();
        }

        private void GapDone()
        {
            Gaps++;
            gapsInCarrier++;
            Item o = order;
            GapLeft?.Invoke(this, carrier, o, slot);
            Serve(o);
            slot++;
            NextSlot();
        }

        private void Serve(Item o)
        {
            order = null;
            if (o == null) return;
            OrderWait.Update(simClock.GetSimulationTime() - OrderTimeOf(o));
            if (!string.IsNullOrEmpty(VehicleLabel) && o.HasLabel(VehicleLabel))
            {
                string v = o.GetLabelText(VehicleLabel);
                if (firstVehicle == null) firstVehicle = v;
                lastVehicle = v;
            }
            OrderServed?.Invoke(this, o);
            orderPort.Served(o);
        }

        private void Close()
        {
            phase = Phase.Closing;
            SetState(ElementState.PROCESSING);
            Work(Sample(CloseTime, carrier), Closed);
        }

        private void Closed()
        {
            if (firstVehicle != null) carrier.SetLabelValue(LabelFirstVehicle, firstVehicle);
            if (lastVehicle != null) carrier.SetLabelValue(LabelLastVehicle, lastVehicle);
            carrier.SetLabelValue(LabelPieces, placedInCarrier);
            carrier.SetLabelValue(LabelGaps, gapsInCarrier);
            carrier.SetLabelValue(LabelCloseTime, simClock.GetSimulationTime());
            if (carrierJob != null) Resources.ProcessEnded(carrierJob);   // released now, or HELD until the carrier leaves
            CarrierClosed?.Invoke(this, carrier);
            vElement.ReportState("Closed");
            if (SendOut(carrier)) Left();
            else { phase = Phase.Blocked; SetState(ElementState.BLOCKED); }
        }

        public override bool Unblock()
        {
            if (phase != Phase.Blocked || carrier == null || !SendOut(carrier)) return false;
            Left();
            return true;
        }

        private void Left()
        {
            Item c = carrier;
            CarriersCompleted++;
            if (carrierJob != null) { Resources.ItemLeft(carrierJob); carrierJob = null; }
            carrier = null;
            phase = Phase.None;
            ShowWaiting();
            vElement.ReportState("Exit");
            CarrierLeft?.Invoke(this, c);
            NotifyInput();
        }

        /// <summary>Progress views: the remaining work of the current step (NaN if none).</summary>
        public string PhaseName => phase.ToString();
    }
}
