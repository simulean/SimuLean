using System;
using System.Collections.Generic;
using SimuLean.Expressions;
using SimuLean.Stochastics.Distributions;

namespace SimuLean
{
    /// <summary>
    /// One <b>call-off family</b>: what a reporting point asks for when a vehicle (main item) passes — used by
    /// <see cref="CallOffPoint"/> and by the stations of a <see cref="PacedLine"/> (one code path). For a vehicle that
    /// meets the optional <see cref="Condition"/> it emits <see cref="Quantity"/> orders (q positions, in position
    /// order) through its <see cref="Port"/>, each a new item of type <see cref="OrderType"/> carrying the vehicle's
    /// labels plus <c>callOffTime</c>, <c>family</c>, <c>position</c> (1..q) and, when the family has reference
    /// tables, the drawn reference in <see cref="ReferenceLabel"/>. The reference of position k is drawn from table k
    /// (the last table serves the remaining positions; one table = the same for all) with the family's own random
    /// stream (owner = the element, purpose = <c>&lt;family&gt;.ref</c>) and also written on the vehicle as
    /// <see cref="VehicleReferenceLabel"/> = <c>&lt;family&gt;.p&lt;k&gt;.&lt;referenceLabel&gt;</c>, so the station
    /// that fits the part can check it.
    /// </summary>
    public sealed class CallOffFamily
    {
        public const string LabelCallOffTime = "callOffTime";
        public const string LabelFamily = "family";
        public const string LabelPosition = "position";

        /// <summary>
        /// Reference value meaning <b>no part for this position</b> (optional positions, e.g. a cover fitted on part of the
        /// vehicles: table <c>C1:0.4|C2:0.3|-:0.3</c>). The order is still emitted, without a reference label (a
        /// <see cref="SequencingStation"/> leaves the slot empty), the vehicle label is set to this value and a line station
        /// fits only the positions that have a part (<see cref="PartsFor"/>).
        /// </summary>
        public const string NoPart = "-";

        private string condition = "";
        private Expression conditionExpr;
        private readonly EntityScope scope = new EntityScope();
        private int orderSeq;

        public CallOffFamily(string name, int quantity = 1)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A call-off family needs a name.", nameof(name));
            Name = name.Trim();
            Quantity = Math.Max(1, quantity);
        }

        public string Name { get; }

        /// <summary>Item type of the orders.</summary>
        public string OrderType { get; set; } = "CallOff";

        /// <summary>Pieces (positions) per vehicle, ≥ 1.</summary>
        public int Quantity { get; set; }

        /// <summary>Applicability condition on the vehicle (expression, architecture §3.18: <c>item.model == "K9"</c>; empty = every vehicle).</summary>
        public string Condition
        {
            get => condition;
            set
            {
                condition = value?.Trim() ?? "";
                conditionExpr = condition.Length == 0 ? null : Expression.ParseCondition(condition, ExpressionContext.CallOff, "callOff:" + Name, "condition");
            }
        }

        /// <summary>Reference tables (one per position, or one for all; empty = no reference drawn).</summary>
        public List<DiscreteChoice> ReferenceTables { get; } = new List<DiscreteChoice>();

        /// <summary>Label of the drawn reference on the order (and suffix of the vehicle label).</summary>
        public string ReferenceLabel { get; set; } = "reference";

        /// <summary>Where the orders leave (set by the owning element; may be unwired = orders discarded, counted).</summary>
        public OutboundPort Port { get; internal set; }

        /// <summary>Orders emitted since the owner's start.</summary>
        public int OrdersEmitted { get; private set; }

        /// <summary>An order was emitted: (family, vehicle, order).</summary>
        public event Action<CallOffFamily, Item, Item> OrderEmitted;

        /// <summary>Vehicle label holding the reference drawn for position <paramref name="position"/> (1-based).</summary>
        public string VehicleReferenceLabel(int position) => $"{Name}.p{position}.{ReferenceLabel}";

        /// <summary>The table of position <paramref name="position"/> (1-based), or null when the family has none.</summary>
        public DiscreteChoice TableFor(int position)
            => ReferenceTables.Count == 0 ? null : ReferenceTables[Math.Min(Math.Max(1, position), ReferenceTables.Count) - 1];

        /// <summary>True when position <paramref name="position"/> (1-based) of <paramref name="vehicle"/> needs a part: its
        /// drawn reference is not <see cref="NoPart"/> (a vehicle without the label needs it).</summary>
        public bool NeedsPart(Item vehicle, int position)
            => vehicle == null || vehicle.GetLabelText(VehicleReferenceLabel(position)) != NoPart;

        /// <summary>Parts the vehicle needs from this family: the quantity minus its positions drawn as <see cref="NoPart"/>.</summary>
        public int PartsFor(Item vehicle)
        {
            if (ReferenceTables.Count == 0) return Quantity;
            int n = 0;
            for (int k = 1; k <= Quantity; k++) if (NeedsPart(vehicle, k)) n++;
            return n;
        }

        /// <summary>True when the family applies to the vehicle (no condition = always).</summary>
        public bool Applies(Item vehicle, Element owner)
        {
            if (conditionExpr == null) return true;
            scope.RandomStream = owner?.RandomStreamFor("condition:" + Name);
            return conditionExpr.Condition(scope.Set(vehicle, owner));
        }

        /// <summary>Gives every table the family's stream (owner / <c>&lt;family&gt;.ref</c>): one stream, drawn in position order.</summary>
        public void AssignStreams(SimClock clock, string owner)
        {
            if (clock == null || ReferenceTables.Count == 0) return;
            var engine = clock.Context.CreateRandomStream(owner, Name + ".ref");
            foreach (var t in ReferenceTables) t.SetRandomEngine(engine);
        }

        internal void Reset() { OrdersEmitted = 0; orderSeq = 0; }

        /// <summary>Draws the references and emits the orders for <paramref name="vehicle"/> (none when the family does
        /// not apply). Returns the number of orders.</summary>
        public int Emit(Element owner, Item vehicle, double now)
        {
            if (vehicle == null || !Applies(vehicle, owner)) return 0;
            for (int k = 1; k <= Quantity; k++)
            {
                string reference = TableFor(k)?.Sample();
                if (reference != null) vehicle.SetLabelText(VehicleReferenceLabel(k), reference);
                var order = new Item(now);
                order.SetId(OrderType, ++orderSeq, vehicle.priority);
                order.CopyLabelsFrom(vehicle);
                order.SetLabelValue(LabelCallOffTime, now);
                order.SetLabelText(LabelFamily, Name);
                order.SetLabelValue(LabelPosition, k);
                if (reference != null && reference != NoPart) order.SetLabelText(ReferenceLabel, reference);
                OrdersEmitted++;
                OrderEmitted?.Invoke(this, vehicle, order);
                Port?.Dispatch(order);
            }
            return Quantity;
        }
    }

    /// <summary>
    /// <b>Call-off (reporting) point</b>: a pass-through for vehicles (main items) that, for every vehicle reaching it,
    /// gives the vehicle its sequence number (<see cref="SequenceLabel"/>, default <c>vehicle</c>, when it has none) and
    /// emits the orders of each <see cref="CallOffFamily"/> through that family's port (port name = family name), then
    /// sends the vehicle on through its output link. When downstream refuses, it holds the vehicle (BLOCKED, one at a
    /// time) and retries on <see cref="Unblock"/>; the orders are emitted when the vehicle arrives, not when it leaves.
    /// Typical use: source → call-off point → vehicle buffer → <see cref="PacedLine"/>, so the requisition time
    /// (call-off → fitting) = time in the buffer + positions × takt. Statistics: the vehicle's pass through the links;
    /// the orders leave through the family ports (generators: their output counts the orders).
    /// </summary>
    public class CallOffPoint : Element, IPortedElement, IElementMetrics
    {
        private readonly List<CallOffFamily> families = new List<CallOffFamily>();
        private readonly List<KeyValuePair<string, Element>> ports = new List<KeyValuePair<string, Element>>();
        private Item held;
        private int seq;

        public CallOffPoint(string name, SimClock clock, IEnumerable<CallOffFamily> families = null, IVElement vElement = null)
            : base(name, clock, vElement)
        {
            if (families != null) foreach (var f in families) AddFamily(f);
        }

        /// <summary>Label with the vehicle sequence number (1, 2, …) given to vehicles without it; empty = none.</summary>
        public string SequenceLabel { get; set; } = "vehicle";

        /// <summary>Overwrite a sequence label the vehicle already has (default false: an upstream number is kept).</summary>
        public bool OverwriteSequence { get; set; }

        public IReadOnlyList<CallOffFamily> Families => families;

        /// <summary>Vehicles that passed (were reported) since the start.</summary>
        public int VehiclesReported { get; private set; }

        /// <summary>The vehicle waiting to leave (null = none).</summary>
        public Item Held => held;

        /// <summary>A vehicle was reported (its orders emitted).</summary>
        public event Action<CallOffPoint, Item> VehicleReported;

        /// <summary>Adds a family and creates its port (<c>&lt;point&gt;.&lt;family&gt;</c>).</summary>
        public CallOffFamily AddFamily(CallOffFamily family)
        {
            if (family == null) throw new ArgumentNullException(nameof(family));
            if (GetPort(family.Name) != null) throw new ArgumentException($"Call-off point '{Name}': two families named '{family.Name}'.");
            family.Port = new OutboundPort(Name + "." + family.Name, simClock, generates: true);
            families.Add(family);
            ports.Add(new KeyValuePair<string, Element>(family.Name, family.Port));
            return family;
        }

        public IReadOnlyList<KeyValuePair<string, Element>> Ports => ports;

        public Element GetPort(string port)
        {
            foreach (var kv in ports) if (kv.Key == port) return kv.Value;
            return null;
        }

        public override void Start()
        {
            base.Start();
            held = null; seq = 0; VehiclesReported = 0;
            foreach (var f in families) { f.Reset(); f.Port.Start(); }
        }

        public override bool CheckAvailability(Item theItem) => held == null;

        public override bool Receive(Item theItem)
        {
            if (held != null || theItem == null) return false;
            seq++;
            if (!string.IsNullOrEmpty(SequenceLabel) && (OverwriteSequence || !theItem.HasLabel(SequenceLabel)))
                theItem.SetLabelValue(SequenceLabel, seq);
            double now = simClock.GetSimulationTime();
            foreach (var f in families) f.Emit(this, theItem, now);
            VehiclesReported++;
            VehicleReported?.Invoke(this, theItem);
            held = theItem;
            vElement.LoadItem(theItem);
            Forward();
            return true;
        }

        public override bool Unblock() => Forward();

        private bool Forward()
        {
            if (held == null) { SetState(ElementState.IDLE); return false; }
            Item v = held;
            if (!SendOut(v)) { SetState(ElementState.BLOCKED); return false; }
            held = null;
            vElement.UnloadItem(v);
            SetState(ElementState.IDLE);
            NotifyInput();
            return true;
        }

        public override int GetQueueLength() => held != null ? 1 : 0;
        public override int GetFreeCapacity() => held == null ? 1 : 0;

        public void ReportMetrics(Action<string, double> set)
        {
            int orders = 0;
            foreach (var f in families) orders += f.OrdersEmitted;
            set(Results.ResultMetrics.CallOffs, orders);
        }
    }
}
