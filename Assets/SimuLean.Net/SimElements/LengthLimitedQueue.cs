using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Accumulation lane(s) limited by <b>length</b> instead of item count: roller or chain
    /// conveyor buffers, floor lanes, gravity lanes. Each item occupies its own length plus
    /// a <see cref="Gap"/> to the item in front; one element can model several parallel
    /// lanes that share an entry and an exit (e.g. a multi-lane accumulation buffer).
    ///
    /// <para><b>Item length.</b> Taken, in this order, from <see cref="ItemLength"/> (a strategy
    /// delegate), the numeric item label <see cref="LengthLabel"/> (default <c>"length"</c>), or
    /// <see cref="DefaultItemLength"/>. Units are whatever the model uses (metres by convention).</para>
    ///
    /// <para><b>Entry.</b> An item goes to the first lane (lowest index) where the occupied
    /// length + <see cref="Gap"/> + its length fits the lane length. With
    /// <see cref="AcceptOversizeOnEmptyLane"/> (default true) an empty lane always takes one item,
    /// however long, so an item longer than every lane cannot deadlock the flow.
    /// <see cref="MaxItems"/> adds an optional count limit.</para>
    ///
    /// <para><b>Exit.</b> Global FIFO. Each lane is FIFO and items are assigned in arrival
    /// order, so the oldest item is always at the front of its lane: global FIFO equals the
    /// physical per-lane order. An item that can pass straight through (lane empty and the
    /// downstream element accepts it) never occupies a lane, as in <see cref="ItemsQueue"/>.</para>
    ///
    /// <para><b>Statistics</b> are driven by the Link layer only (every item crosses the
    /// element's input/output links); the element only sets its state (IDLE / PROCESSING when
    /// holding items / BLOCKED when the output refused its head item). There is no transit
    /// time: combine it with a <see cref="MultiServer"/> if the conveyor travel time matters.
    /// Engine-pure (no Unity dependency). See docs/architecture.md §3.13.</para>
    /// </summary>
    public class LengthLimitedQueue : Element
    {
        private readonly double[] laneLength;
        private readonly double[] used;
        private readonly int[] count;
        private readonly List<Item> items = new List<Item>();
        private readonly Dictionary<Item, int> laneOf = new Dictionary<Item, int>();
        private readonly Dictionary<Item, double> lengthOf = new Dictionary<Item, double>();

        /// <summary>Free space kept between two consecutive items on a lane.</summary>
        public double Gap { get; set; } = 0.2;

        /// <summary>Length used when neither <see cref="ItemLength"/> nor <see cref="LengthLabel"/> give a positive value,
        /// and the reference length of <see cref="GetFreeCapacity"/>.</summary>
        public double DefaultItemLength { get; set; } = 1.0;

        /// <summary>Numeric item label read as the item length (null/empty = not used).</summary>
        public string LengthLabel { get; set; } = "length";

        /// <summary>Optional length strategy; a non-positive result falls back to the label / default.</summary>
        public Func<Item, double> ItemLength { get; set; }

        /// <summary>Optional count limit on top of the length rule (default unlimited).</summary>
        public int MaxItems { get; set; } = int.MaxValue;

        /// <summary>An empty lane takes one item even if it is longer than the lane (default true: no deadlock).</summary>
        public bool AcceptOversizeOnEmptyLane { get; set; } = true;

        /// <param name="laneLengths">Length of each lane (at least one lane, each &gt; 0).</param>
        public LengthLimitedQueue(string name, SimClock clock, double[] laneLengths,
                                  InputStrategy inputStrategy = null, IVElement vElement = null)
            : base(name, clock, vElement)
        {
            if (laneLengths == null || laneLengths.Length == 0)
                throw new ArgumentException("At least one lane is needed.", nameof(laneLengths));
            foreach (double l in laneLengths)
                if (!(l > 0)) throw new ArgumentException("Lane lengths must be positive.", nameof(laneLengths));
            laneLength = (double[])laneLengths.Clone();
            used = new double[laneLength.Length];
            count = new int[laneLength.Length];
            this.inputStrategy = inputStrategy;
        }

        /// <summary>Single-lane convenience constructor.</summary>
        public LengthLimitedQueue(string name, SimClock clock, double laneLength,
                                  InputStrategy inputStrategy = null, IVElement vElement = null)
            : this(name, clock, new[] { laneLength }, inputStrategy, vElement) { }

        // ── read-only views (reports, inspectors, visuals) ───────────────────

        public int LaneCount => laneLength.Length;
        public IReadOnlyList<double> LaneLengths => laneLength;

        /// <summary>Sum of the lane lengths.</summary>
        public double TotalLength { get { double s = 0; foreach (var l in laneLength) s += l; return s; } }

        /// <summary>Length occupied on a lane (item lengths + gaps between them).</summary>
        public double UsedLength(int lane) => used[lane];

        /// <summary>Length occupied over all lanes.</summary>
        public double TotalUsedLength { get { double s = 0; foreach (var u in used) s += u; return s; } }

        /// <summary>Free length summed over the lanes (an empty lane counts its full length; a gap is reserved otherwise).</summary>
        public double FreeLength
        {
            get
            {
                double f = 0;
                for (int i = 0; i < laneLength.Length; i++)
                    f += count[i] == 0 ? laneLength[i] : Math.Max(0, laneLength[i] - used[i] - Gap);
                return f;
            }
        }

        /// <summary>Items held, front (oldest) first.</summary>
        public IReadOnlyList<Item> Items => items;

        /// <summary>Number of items on a lane.</summary>
        public int CountOnLane(int lane) => count[lane];

        /// <summary>Lane holding <paramref name="item"/> (−1 if not held).</summary>
        public int LaneOf(Item item) => item != null && laneOf.TryGetValue(item, out int l) ? l : -1;

        /// <summary>Length the item occupies (as resolved when it entered, or now if not held).</summary>
        public double LengthOf(Item item) => item != null && lengthOf.TryGetValue(item, out double l) ? l : ResolveLength(item);

        /// <summary>Items on a lane, front first.</summary>
        public IEnumerable<Item> ItemsOnLane(int lane)
        {
            foreach (var it in items) if (laneOf[it] == lane) yield return it;
        }

        /// <summary>
        /// Distance from the lane exit to the leading edge of <paramref name="item"/>: 0 for the front item,
        /// otherwise the lengths + gaps of the items ahead of it on its lane. For visuals (items drawn end to end).
        /// </summary>
        public double OffsetFromExit(Item item)
        {
            int lane = LaneOf(item);
            if (lane < 0) return 0;
            double off = 0;
            foreach (var it in items)
            {
                if (laneOf[it] != lane) continue;
                if (it == item) return off;
                off += lengthOf[it] + Gap;
            }
            return off;
        }

        // ── Element contract ─────────────────────────────────────────────────

        public override void Start()
        {
            items.Clear(); laneOf.Clear(); lengthOf.Clear();
            Array.Clear(used, 0, used.Length);
            Array.Clear(count, 0, count.Length);
            base.Start();
        }

        /// <summary>Length of an item by the configured rule (strategy → label → default).</summary>
        public double ResolveLength(Item item)
        {
            double l = 0;
            if (item != null)
            {
                if (ItemLength != null) l = ItemLength(item);
                if (!(l > 0) && !string.IsNullOrEmpty(LengthLabel)) l = item.GetLabelOrDefault(LengthLabel, 0);
            }
            return l > 0 ? l : DefaultItemLength;
        }

        /// <summary>First lane where an item of length <paramref name="len"/> fits now (−1: none).</summary>
        public int FindLane(double len)
        {
            for (int i = 0; i < laneLength.Length; i++)
            {
                if (count[i] == 0)
                {
                    if (AcceptOversizeOnEmptyLane || len <= laneLength[i] + 1e-9) return i;
                    continue;
                }
                if (used[i] + Gap + len <= laneLength[i] + 1e-9) return i;
            }
            return -1;
        }

        /// <summary>True if the item fits a lane now (length and count rules; input strategy not checked).</summary>
        public bool Fits(Item item) => items.Count < MaxItems && FindLane(ResolveLength(item)) >= 0;

        public override bool CheckAvailability(Item theItem) => Fits(theItem);

        public override bool Receive(Item theItem)
        {
            double len = ResolveLength(theItem);
            int lane = items.Count < MaxItems ? FindLane(len) : -1;
            if (lane < 0) return false;

            // Pass straight through only when nobody is waiting (FIFO; items waiting for their carrier were offered
            // already), otherwise join a lane.
            if (!HasItemToOffer() && GetOutput() != null && SendOut(theItem))
                return true;

            used[lane] += (count[lane] > 0 ? Gap : 0) + len;
            count[lane]++;
            items.Add(theItem);
            laneOf[theItem] = lane;
            lengthOf[theItem] = len;
            vElement.LoadItem(theItem);
            SetState(ElementState.PROCESSING);   // holding items
            // Pushing to a list: every item is advertised, not only the head.
            if (GetOutput() is ListLink list) list.Advertise(theItem, this);
            return true;
        }

        /// <summary>Hands over a specific held item (pulled from a list), whichever lane holds it.</summary>
        public override bool SendHeldItem(Item item)
        {
            if (!items.Contains(item) || GetOutput() == null || !SendOut(item))
                return false;
            Remove(item);
            vElement.ReportState("Exit");
            SetState(items.Count == 0 ? ElementState.IDLE : ElementState.PROCESSING);
            NotifyInput();
            return true;
        }

        /// <summary>
        /// Releases items from the front, in FIFO order, <b>while the output accepts them</b> (not only one:
        /// after a stop ends, or behind an always-free element such as a sink, the whole lane drains), then
        /// wakes the input. Returns true if at least one item left.
        /// </summary>
        public override bool Unblock()
        {
            if (items.Count == 0) return false;
            bool any = false, refused = false;
            if (OutputRequestsCarriers)
            {
                // Every held item gets its own carrier, in order; one refused keeps the rest waiting.
                foreach (Item it in new List<Item>(items))
                {
                    if (!laneOf.ContainsKey(it) || AwaitsCarrier(it)) continue;
                    if (SendOut(it)) { Remove(it); vElement.ReportState("Exit"); any = true; continue; }
                    if (!laneOf.ContainsKey(it)) { any = true; continue; }   // loaded inside its own request
                    if (!AwaitsCarrier(it)) { refused = true; break; }
                }
                if (!any)
                {
                    SetState(ElementState.BLOCKED);
                    return false;
                }
                SetState(items.Count == 0 ? ElementState.IDLE : ElementState.BLOCKED);
                NotifyInput();
                return true;
            }
            while (items.Count > 0)
            {
                Item head = items[0];
                if (GetOutput() == null || !SendOut(head)) { refused = true; break; }
                Remove(head);
                vElement.ReportState("Exit");
                any = true;
            }
            if (!any)
            {
                SetState(ElementState.BLOCKED);   // tried to send, output refused
                return false;
            }
            SetState(items.Count == 0 ? ElementState.IDLE : refused ? ElementState.BLOCKED : ElementState.PROCESSING);
            NotifyInput();
            return true;
        }

        // An earlier item still to be offered; items waiting for their carrier have been offered already.
        private bool HasItemToOffer()
        {
            if (!OutputRequestsCarriers) return items.Count > 0;
            foreach (Item it in items) if (!AwaitsCarrier(it)) return true;
            return false;
        }

        private void Remove(Item it)
        {
            items.Remove(it);
            int lane = laneOf[it];
            double len = lengthOf[it];
            laneOf.Remove(it); lengthOf.Remove(it);
            count[lane]--;
            used[lane] = count[lane] == 0 ? 0 : Math.Max(0, used[lane] - len - Gap);
        }

        public override int GetQueueLength() => items.Count;

        /// <summary>
        /// Estimate of how many more items of <see cref="DefaultItemLength"/> would fit (used by
        /// capacity-based output strategies and views); the exact admission rule is <see cref="Fits"/>.
        /// </summary>
        public override int GetFreeCapacity()
        {
            long free = 0;
            double unit = DefaultItemLength + Gap;
            for (int i = 0; i < laneLength.Length; i++)
            {
                double room = count[i] == 0 ? laneLength[i] + Gap : laneLength[i] - used[i];
                int n = unit > 0 ? (int)Math.Floor((room + 1e-9) / unit) : 0;
                if (count[i] == 0 && n < 1 && AcceptOversizeOnEmptyLane) n = 1;
                free += Math.Max(0, n);
            }
            return (int)Math.Min(free, (long)MaxItems - items.Count);
        }
    }
}
