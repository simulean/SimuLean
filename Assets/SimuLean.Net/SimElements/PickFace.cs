using System;
using System.Collections.Generic;
using SimuLean.Results;

namespace SimuLean
{
    /// <summary>
    /// A picking location (supermarket face) of one reference (Warehousing pack): it holds up to <see cref="Positions"/> unit
    /// loads (compound items whose <see cref="Item.subItems"/> are the pieces) of its <see cref="Sku"/> — the front one
    /// <b>open</b>, the others in reserve — and hands out <b>single pieces</b>, front first, through its output link.
    /// <list type="bullet">
    /// <item><b>Admission.</b> Only unit loads of its SKU (label <c>sku</c>, else the item type), only while it has
    ///   room and has asked for them (replenishment, below): with a plain link the upstream element keeps its load until
    ///   the face orders one; with a <see cref="ListLink"/> pull (query e.g. <c>sku == X</c>) the face registers a pull
    ///   only then.</item>
    /// <item><b>Pieces.</b> The open load's pieces are offered one at a time: through a <see cref="GeneralLink"/> while
    ///   downstream accepts; through a <see cref="ListLink"/> every piece of the open load is advertised (a station
    ///   pulls by query <c>sku == X</c>) and the pulled one is handed over (<see cref="SendHeldItem"/>). Taking time is
    ///   the taker's business, not the face's.</item>
    /// <item><b>Empty load.</b> When its last piece leaves, the empty load leaves through the <see cref="EmptiesPort"/>
    ///   (an <see cref="OutboundPort"/>, port name <c>empties</c>: unwired = discarded and counted) and the next reserve
    ///   load opens.</item>
    /// <item><b>Replenishment (kanban).</b> When loads held + on order ≤ <see cref="ReorderPoint"/> it orders
    ///   <see cref="ReorderQuantity"/> loads (capped by the free positions): it accepts that many from its input (e.g. a
    ///   pull from a published storage stock list, so the forklift brings it). Default: 1 position, reorder point 0,
    ///   quantity 1 = a kanban of one. With one open + one reserve load use <c>positions 2, reorderPoint 1</c>.</item>
    /// <item><b>Initial fill.</b> <see cref="StartFull"/> creates <see cref="Positions"/> loads of
    ///   <see cref="PiecesPerLoad"/> pieces at <see cref="Start"/> (type = <see cref="LoadType"/> / <see cref="PieceType"/>,
    ///   default the SKU; label <c>sku</c> on both); otherwise it orders at time 0 like any replenishment.</item>
    /// </list>
    /// <para><b>Statistics (link layer only).</b> Loads enter through the input link and count their pieces too
    /// (<see cref="Statistics.ElementStatsCollector.CountSubItemsOnEntry"/>); pieces leave through the output link, the
    /// empty load through a <see cref="DirectLink"/> to its port. So input / output / content count <b>pieces + unit
    /// loads</b> (content = pieces available + the loads holding them; <see cref="GetQueueLength"/> = pieces only). The
    /// initial fill enters through a link from an internal generator. States: IDLE (pieces available), STARVED (none:
    /// a stock-out at the face). Counters reported with <see cref="IElementMetrics"/>.</para>
    /// </summary>
    public class PickFace : Element, IPortedElement, IElementMetrics
    {
        /// <summary>Label carrying the reference of unit loads and pieces (the interface contract with consumers).</summary>
        public const string SkuLabel = "sku";

        private readonly List<Item> loads = new List<Item>();      // [0] = open
        private readonly DirectLink emptiesLink;
        private readonly OutboundPort initialSource;                // generator of the initial fill (not a port)
        private readonly DirectLink initialLink;
        private readonly List<KeyValuePair<string, Element>> ports;
        private bool filling, offerScheduled, notifyScheduled;
        private int seq;

        public PickFace(string name, SimClock simClock, string sku, int positions = 1, IVElement vElement = null)
            : base(name, simClock, vElement)
        {
            Sku = sku ?? "";
            Positions = Math.Max(1, positions);
            StatsCollector.CountSubItemsOnEntry = true;
            EmptiesPort = new OutboundPort(name + "/empties", simClock);
            emptiesLink = new DirectLink(EmptiesPort);
            initialSource = new OutboundPort(name + "/initial", simClock, generates: true);
            initialLink = new DirectLink(this);
            ports = new List<KeyValuePair<string, Element>> { new KeyValuePair<string, Element>("empties", EmptiesPort) };
        }

        // ── configuration ────────────────────────────────────────────────────

        /// <summary>The reference it holds (unit loads of other references are refused).</summary>
        public string Sku { get; set; }

        /// <summary>Unit loads it can hold: 1 open + reserve.</summary>
        public int Positions { get; set; }

        /// <summary>Orders when loads held + on order ≤ this (default 0: when the last load is emptied).</summary>
        public int ReorderPoint { get; set; }

        /// <summary>Loads ordered each time (capped by the free positions; default 1).</summary>
        public int ReorderQuantity { get; set; } = 1;

        /// <summary>Create its loads at <see cref="Start"/> (full) instead of ordering them at time 0.</summary>
        public bool StartFull { get; set; }

        /// <summary>Pieces per load created by <see cref="StartFull"/>.</summary>
        public int PiecesPerLoad { get; set; } = 1;

        /// <summary>Item type of the created loads / pieces (null or empty = the SKU).</summary>
        public string LoadType { get; set; }
        public string PieceType { get; set; }

        /// <summary>Size (width, height, length) given to the loads it creates where their labels set none (their 3D model
        /// in a scene, <see cref="Item.SetSizeWhereMissing"/>); null = none.</summary>
        public double[] LoadModelSize { get; set; }

        /// <summary>Size given to the pieces of every load it opens (its own or received) where their labels set none — how
        /// the face draws them; null = none.</summary>
        public double[] PieceModelSize { get; set; }

        /// <summary>Outbound port of the emptied unit loads (port name <c>empties</c>).</summary>
        public OutboundPort EmptiesPort { get; }

        // ── state / counters ─────────────────────────────────────────────────

        /// <summary>Unit loads held, open first.</summary>
        public IReadOnlyList<Item> Loads => loads;

        /// <summary>The open load (null when empty-handed).</summary>
        public Item OpenLoad => loads.Count > 0 ? loads[0] : null;

        /// <summary>Pieces left in the open load.</summary>
        public int OpenPieces => OpenLoad?.subItems?.Count ?? 0;

        /// <summary>Loads ordered and not yet received.</summary>
        public int OnOrder { get; private set; }

        public int PiecesTaken { get; private set; }
        public int LoadsReceived { get; private set; }
        public int LoadsEmptied { get; private set; }
        /// <summary>Loads ordered (replenishment requests, in loads).</summary>
        public int LoadsOrdered { get; private set; }
        /// <summary>Stock-outs at the face: times it had no piece for a while (emptied and not refilled at the same
        /// instant), the one still open at the end included.</summary>
        public int StockOuts => stockOuts + (OpenPieces == 0 && starvedSince < simClock.GetSimulationTime() ? 1 : 0);
        private int stockOuts;
        private double starvedSince;

        /// <summary>A load was opened (its pieces are now available) — a view gives the pieces a visual here.</summary>
        public event Action<PickFace, Item> LoadOpened;
        /// <summary>A piece left (after the hand-over).</summary>
        public event Action<PickFace, Item> PieceTaken;
        /// <summary>The open load was emptied (before it leaves through the empties port).</summary>
        public event Action<PickFace, Item> LoadEmptied;
        /// <summary>Loads were ordered: (face, quantity).</summary>
        public event Action<PickFace, int> Replenishing;
        /// <summary>A load was created by the initial fill (before it enters) — a view gives it a visual here.</summary>
        public event Action<PickFace, Item> LoadCreated;

        public IReadOnlyList<KeyValuePair<string, Element>> Ports => ports;
        public Element GetPort(string port) => port == "empties" ? EmptiesPort : null;

        /// <summary>The reference of an item: its <c>sku</c> label, else its type.</summary>
        public static string SkuOf(Item item) => item?.GetLabelText(SkuLabel) ?? item?.Name;

        // ── lifecycle ────────────────────────────────────────────────────────

        public override void Start()
        {
            base.Start();
            loads.Clear();
            OnOrder = 0; seq = 0;
            PiecesTaken = LoadsReceived = LoadsEmptied = LoadsOrdered = stockOuts = 0;
            starvedSince = simClock.GetSimulationTime();
            offerScheduled = notifyScheduled = false;
            EmptiesPort.Start();
            initialSource.Start();
            if (StartFull)
            {
                filling = true;
                try
                {
                    for (int i = 0; i < Positions; i++)
                        initialLink.SendItem(CreateLoad(), initialSource);   // enters through a link (statistics)
                }
                finally { filling = false; }
            }
            CheckReorder();
            UpdateState();
        }

        /// <summary>A full unit load of this face's SKU (<see cref="PiecesPerLoad"/> pieces), as the initial fill makes it.</summary>
        public Item CreateLoad()
        {
            double now = simClock.GetSimulationTime();
            var load = new Item(now);
            load.SetId(string.IsNullOrEmpty(LoadType) ? Sku : LoadType, ++seq, 0);
            load.SetLabelText(SkuLabel, Sku);
            load.SetSizeWhereMissing(LoadModelSize);
            for (int p = 0; p < Math.Max(0, PiecesPerLoad); p++)
            {
                var piece = new Item(now);
                piece.SetId(string.IsNullOrEmpty(PieceType) ? Sku : PieceType, ++seq, 0);
                piece.SetLabelText(SkuLabel, Sku);
                load.AddItem(piece);
            }
            LoadCreated?.Invoke(this, load);
            return load;
        }

        // ── input: unit loads ────────────────────────────────────────────────

        public override bool CheckAvailability(Item theItem)
            => theItem != null && SkuOf(theItem) == Sku && loads.Count < Positions && (filling || OnOrder > 0);

        public override int GetFreeCapacity() => filling ? Positions - loads.Count : Math.Max(0, Math.Min(OnOrder, Positions - loads.Count));

        public override bool Receive(Item theItem)
        {
            if (!CheckAvailability(theItem)) return false;
            if (OpenPieces == 0 && starvedSince < simClock.GetSimulationTime()) stockOuts++;   // a stock-out ends
            loads.Add(theItem);
            if (!filling) OnOrder = Math.Max(0, OnOrder - 1);
            LoadsReceived++;
            vElement.LoadItem(theItem);
            if (loads.Count == 1) OpenFront();
            UpdateState();
            return true;
        }

        // Opens the front load (an empty one leaves at once) and offers its pieces after the current event, so the
        // load's entry is recorded before any piece leaves.
        private void OpenFront()
        {
            while (loads.Count > 0)
            {
                Item front = loads[0];
                if (front.subItems != null && front.subItems.Count > 0)
                {
                    if (PieceModelSize != null) foreach (Item piece in front.subItems) piece.SetSizeWhereMissing(PieceModelSize);
                    LoadOpened?.Invoke(this, front);
                    ScheduleOffer();
                    return;
                }
                loads.RemoveAt(0);
                ReleaseEmpty(front);
            }
        }

        // ── output: pieces ───────────────────────────────────────────────────

        private void ScheduleOffer()
        {
            if (offerScheduled) return;
            offerScheduled = true;
            simClock.Schedule(() => { offerScheduled = false; Offer(); }, 0);
        }

        // Offers the front piece while downstream takes it; a list gets every piece of the open load advertised.
        private bool Offer()
        {
            bool any = false;
            while (OpenPieces > 0 && GetOutput() != null)
            {
                Item piece = OpenLoad.subItems[0];
                if (!SendOut(piece)) break;
                TookPiece(piece);
                any = true;
            }
            if (GetOutput() is ListLink list && OpenPieces > 0)
                foreach (Item p in OpenLoad.subItems.ToArray()) list.Advertise(p, this);
            UpdateState();
            return any;
        }

        /// <summary>Hands over a piece of the open load that a list pull reserved.</summary>
        public override bool SendHeldItem(Item item)
        {
            if (OpenPieces == 0 || !OpenLoad.subItems.Contains(item) || !SendOut(item)) return false;
            TookPiece(item);
            UpdateState();
            return true;
        }

        private void TookPiece(Item piece)
        {
            Item open = OpenLoad;
            open.subItems.Remove(piece);
            PiecesTaken++;
            PieceTaken?.Invoke(this, piece);
            if (open.subItems.Count > 0) return;

            // The open load is empty: it leaves, the reserve opens, replenishment is checked.
            loads.RemoveAt(0);
            ReleaseEmpty(open);
            if (loads.Count > 0) OpenFront();
            if (OpenPieces == 0) starvedSince = simClock.GetSimulationTime();
            CheckReorder();
        }

        private void ReleaseEmpty(Item load)
        {
            LoadsEmptied++;
            LoadEmptied?.Invoke(this, load);
            vElement.UnloadItem(load);
            EmptiesPort.Accept(this, load, emptiesLink);
        }

        // ── replenishment ────────────────────────────────────────────────────

        private void CheckReorder()
        {
            int ordered = 0;
            while (loads.Count + OnOrder <= ReorderPoint && loads.Count + OnOrder < Positions)
            {
                int q = Math.Min(Math.Max(1, ReorderQuantity), Positions - loads.Count - OnOrder);
                OnOrder += q; ordered += q;
            }
            if (ordered == 0) return;
            LoadsOrdered += ordered;
            Replenishing?.Invoke(this, ordered);
            // Ask the input after the current event (an upstream element may hand a load over at once).
            if (notifyScheduled) return;
            notifyScheduled = true;
            simClock.Schedule(() => { notifyScheduled = false; if (!IsInputBlocked) NotifyInput(); }, 0);
        }

        // ── Element contract ─────────────────────────────────────────────────

        /// <summary>Pieces available (open + reserve loads).</summary>
        public override int GetQueueLength()
        {
            int n = 0;
            foreach (Item l in loads) n += l.subItems?.Count ?? 0;
            return n;
        }

        /// <summary>Downstream has room (or the face resumed): offers its pieces again.</summary>
        public override bool Unblock() => Offer();

        private void UpdateState() => SetState(OpenPieces > 0 ? ElementState.IDLE : ElementState.STARVED);

        public void ReportMetrics(Action<string, double> set)
        {
            set(ResultMetrics.Pieces, PiecesTaken);
            set(ResultMetrics.LoadsReceived, LoadsReceived);
            set(ResultMetrics.LoadsOrdered, LoadsOrdered);
            set(ResultMetrics.CarriersEmptied, LoadsEmptied);
            set(ResultMetrics.StockOuts, StockOuts);
        }
    }
}
