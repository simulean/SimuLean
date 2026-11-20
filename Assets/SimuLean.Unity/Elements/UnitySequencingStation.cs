using System;
using System.Collections.Generic;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>A list the station's piece input pulls from, with the pull query (sibling <c>list</c> for the query builder).</summary>
    [Serializable]
    public class SequencingPieceList
    {
        [Tooltip("List the pieces are pulled from, one at a time, when an order needs one (empty = pieces come from Piece Sources).")]
        public UnitySimList list;
        [Tooltip("Extra conditions / order of the pull (the reference match is added by the station).")]
        public SimuLean.Collections.ListQuerySpec query = new SimuLean.Collections.ListQuerySpec();
    }

    /// <summary>
    /// Unity wrapper of <see cref="SequencingStation"/> (just-in-sequence sequencing / kitting into carriers). Empty
    /// carriers come through the ordinary connections (upstream Next Elements / a list); call-off orders from
    /// <see cref="orderSources"/>; pieces from <see cref="pieceList"/> (pulled on demand by reference) or from
    /// <see cref="pieceSources"/>; served orders go to <see cref="servedOrdersTo"/> (empty = they end at the station).
    /// The engine element is built by <c>SequencingStationHandler</c> (one build path); this component is its view.
    /// <para><b>View.</b> The carrier stands at <see cref="carrierPoint"/>; each placed piece goes into the carrier's child
    /// transform <c>Slot_i</c> if its model has one, else onto a grid on top of the carrier (<see cref="gridColumns"/>,
    /// <see cref="slotSpacing"/>) — parented to the carrier, so it leaves with it. A taken piece waits at
    /// <see cref="pickPoint"/>; waiting orders line up at <see cref="orderPoint"/>. <see cref="stateIndicator"/> (optional
    /// renderer) shows the state colour. Operators: give the station a Location and a spatial team — the members walk
    /// to it (the team's own views); abstract members are not shown. Read-only: the views never change the run.</para>
    /// </summary>
    [AddComponentMenu("SimuLean/Sequencing Station")]
    [SimuLeanElement(SimuLeanModules.Assembly, "Sequencing Station", Model = "AssemblyWorkstation/AssemblyWorkstation.fbx",
        Description = "Fills carriers with one piece per call-off order, in sequence (JIS kitting), lot by lot", Order = 4, Pack = SimuLeanPacks.Manufacturing, Icon = "Assets/SimuLean.Unity/Art/Models/TuggerTrain/SteeringWheelWagon.fbx")]
    public class UnitySequencingStation : SElement, IVElement, IProcessResourceUser
    {
        [Header("Carriers")]
        [Tooltip("Slots of a carrier without a numeric capacity label.")]
        [Min(1)] public int carrierCapacity = 9;
        [Tooltip("Carrier label with its number of slots (wins over Carrier Capacity).")]
        public string capacityLabel = "capacity";

        [Header("Orders")]
        [Tooltip("Elements sending call-off orders to this station (leave their own Next Elements empty).")]
        public List<SElement> orderSources = new List<SElement>();
        [Tooltip("Orders that must wait before a carrier starts (0 = the carrier capacity).")]
        [Min(0)] public int lotSize = 0;
        [Tooltip("> 0: a carrier also starts when the oldest order has waited this long (with fewer orders).")]
        [Min(0f)] public float oldestOrderMaxWait = 0f;
        [Tooltip("On: a slot waits for its order. Off: the carrier closes when no order waits at a slot.")]
        public bool waitForFullCarrier = true;
        [Tooltip("Order buffer capacity (-1 = unbounded).")]
        public int orderCapacity = -1;
        [Tooltip("Where served orders go (empty = they end here).")]
        public SElement servedOrdersTo;
        [Tooltip("Order label with the part reference it needs.")]
        public string referenceLabel = "reference";
        [Tooltip("Order label with its call-off time (else its arrival at the station).")]
        public string orderTimeLabel = "callOffTime";
        [Tooltip("Order label with the vehicle (sequence) number.")]
        public string vehicleLabel = "vehicle";
        [Tooltip("Whether an order needs a piece (expression over the order); empty = its reference label is not empty.")]
        [SimuLean.Unity.SimuLeanExpression("sequencing")]
        public string applies = "";

        [Header("Pieces")]
        [Tooltip("Pull pieces from a list by reference (on demand).")]
        public SequencingPieceList pieceList = new SequencingPieceList();
        [Tooltip("Or: elements sending pieces to this station (leave their own Next Elements empty).")]
        public List<SElement> pieceSources = new List<SElement>();
        [Tooltip("Piece label compared with the order's reference (empty or 'type' = the item type).")]
        public string pieceField = "sku";
        [Tooltip("0 = take a piece only when an order needs it (on demand); > 0 = stock buffer of that size; -1 = unbounded.")]
        public int pieceStock = 0;
        [Tooltip("Copy the order's labels (vehicle, call-off time…) onto its piece.")]
        public bool copyOrderLabels = true;

        [Header("Times (expressions read the carrier / piece / order)")]
        public RandomProcessConfig prepareTime = Zero();
        public RandomProcessConfig pickTime = Zero();
        [Tooltip("Extra time per piece, e.g. an assembly before placing (expression on the piece).")]
        public RandomProcessConfig extraTime = Zero();
        public RandomProcessConfig placeTime = Zero();
        [Tooltip("Time of an empty slot (order without a piece).")]
        public RandomProcessConfig gapTime = Zero();
        [Tooltip("Time to close the carrier (print the list, close).")]
        public RandomProcessConfig closeTime = Zero();

        [Header("Operators")]
        [Tooltip("Carrier: the operator is held from preparation to close. Piece: one request per piece.")]
        public SequencingOperatorScope operatorScope = SequencingOperatorScope.Carrier;

        [Header("View (local frame)")]
        [Tooltip("Where the carrier stands (empty = Carrier Offset).")]
        public Transform carrierPoint;
        public Vector3 carrierOffset = new Vector3(0f, 0f, 0f);
        [Tooltip("Where a taken piece waits while it is picked (empty = Pick Offset).")]
        public Transform pickPoint;
        public Vector3 pickOffset = new Vector3(1.2f, 1f, 0f);
        [Tooltip("Where waiting orders line up (empty = Order Offset); they step along Order Step.")]
        public Transform orderPoint;
        public Vector3 orderOffset = new Vector3(-1.5f, 0.5f, 1f);     // behind, on the in side (the element frame: front −Z, flow +X)
        public Vector3 orderStep = new Vector3(0f, 0f, -0.3f);
        [Tooltip("Carrier without Slot_i children: slots on a grid on top of it.")]
        [Min(1)] public int gridColumns = 3;
        public Vector2 slotSpacing = new Vector2(0.35f, 0.35f);
        [Tooltip("Optional renderer coloured by the station state.")]
        public Renderer stateIndicator;

        private SequencingStation station;
        private MaterialPropertyBlock block;

        private static RandomProcessConfig Zero() => new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0 };

        /// <summary>The engine element (null before the run).</summary>
        public SequencingStation Station => station;

        private static SimuLean.Serialization.SamplerSettings Spec(RandomProcessConfig r)
        {
            if (r == null) return null;
            if (r.strategyType == RandomProcessType.Constant && r.constantValue == 0) return null;
            return r.ToSettings();
        }

        /// <summary>Writes its settings into the element's (the extractor's).</summary>
        public void Fill(SequencingStationSettings s)
        {
            s.CarrierCapacity = Mathf.Max(1, carrierCapacity);
            s.CapacityLabel = capacityLabel ?? "";
            s.LotSize = lotSize;
            s.OldestOrderMaxWait = oldestOrderMaxWait;
            s.WaitForFullCarrier = waitForFullCarrier;
            s.ReferenceLabel = referenceLabel ?? "";
            s.PieceField = pieceField ?? "";
            s.OrderTimeLabel = orderTimeLabel ?? "";
            s.VehicleLabel = vehicleLabel ?? "";
            s.Applies = string.IsNullOrWhiteSpace(applies) ? null : applies;
            s.CopyOrderLabels = copyOrderLabels;
            s.OperatorScope = operatorScope;
            s.OrderCapacity = orderCapacity;
            s.PieceStock = pieceStock;
            s.PrepareTime = Spec(prepareTime); s.PickTime = Spec(pickTime); s.ExtraTime = Spec(extraTime);
            s.PlaceTime = Spec(placeTime); s.GapTime = Spec(gapTime); s.CloseTime = Spec(closeTime);
        }

        // ── binding ──────────────────────────────────────────────────────────

        protected override void OnBound(Element built)
        {
            station = (SequencingStation)built;
            station.PieceTaken += OnPieceTaken;
            station.PiecePlaced += OnPiecePlaced;
            station.OrderServed += OnOrderServed;
            station.OrderPort.ItemEntered += (e, it) => LayOutOrders();
            station.StateChanged += (e, o, n) => ShowState(n);
            ShowState(station.CurrentState);
        }

        public override void ClearViews()
        {
            if (station == null) return;
            Destroy(station.Carrier);
            Destroy(station.CurrentPiece);
            foreach (Item o in station.OrderPort.Orders) Destroy(o);
            Destroy(station.CurrentOrder);
        }

        private static void Destroy(Item it)
        {
            if (it?.vItem is GameObject go && go != null) UnityEngine.Object.Destroy(go);
        }

        // ── layout ───────────────────────────────────────────────────────────

        private Vector3 CarrierWorld => carrierPoint != null ? carrierPoint.position : transform.TransformPoint(carrierOffset);
        private Vector3 PickWorld => pickPoint != null ? pickPoint.position : transform.TransformPoint(pickOffset);
        private Vector3 OrderWorld(int i) => (orderPoint != null ? orderPoint.position : transform.TransformPoint(orderOffset))
                                             + transform.TransformVector(orderStep) * i;

        /// <summary>The carrier's child named <c>Slot_i</c> (searched at any depth), or null.</summary>
        public static Transform FindSlot(Transform carrier, int slot)
        {
            if (carrier == null) return null;
            string name = "Slot_" + slot;
            foreach (Transform t in carrier.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        /// <summary>Local position (in the carrier's frame) of slot <paramref name="slot"/> on the grid on top of the carrier.</summary>
        public Vector3 GridSlotLocal(Transform carrier, int slot, int capacity)
        {
            int cols = Mathf.Max(1, gridColumns);
            int rows = Mathf.Max(1, Mathf.CeilToInt(capacity / (float)cols));
            int c = slot % cols, r = slot / cols;
            float top = 0f;
            var rend = carrier != null ? carrier.GetComponentInChildren<Renderer>() : null;
            if (rend != null) top = carrier.InverseTransformPoint(rend.bounds.max).y;
            return new Vector3((c - (cols - 1) * 0.5f) * slotSpacing.x, top, (r - (rows - 1) * 0.5f) * slotSpacing.y);
        }

        private void PutInSlot(Item carrier, Item piece, int slot)
        {
            if (!(carrier?.vItem is GameObject cgo) || cgo == null || !(piece?.vItem is GameObject pgo) || pgo == null) return;
            Transform anchor = FindSlot(cgo.transform, slot);
            if (anchor != null)
            {
                pgo.transform.SetParent(anchor, false);
                pgo.transform.localPosition = Vector3.zero;
                pgo.transform.localRotation = Quaternion.identity;
            }
            else
            {
                pgo.transform.SetParent(cgo.transform, true);
                pgo.transform.localPosition = GridSlotLocal(cgo.transform, slot, station != null ? station.CurrentCapacity : carrierCapacity);
                pgo.transform.localRotation = Quaternion.identity;
            }
        }

        private void LayOutOrders()
        {
            if (station == null) return;
            int i = 0;
            foreach (Item o in station.OrderPort.Orders)
                if (o.vItem is GameObject go && go != null) go.transform.position = OrderWorld(i++);
        }

        private void ShowState(ElementState s)
        {
            if (stateIndicator == null) return;
            if (block == null) block = new MaterialPropertyBlock();
            Color c = InspectorPalette.StateColor(s.Name);
            stateIndicator.GetPropertyBlock(block);
            block.SetColor("_BaseColor", c);
            block.SetColor("_Color", c);
            block.SetColor("_BaseColor", c);
            stateIndicator.SetPropertyBlock(block);
        }

        // ── engine events (visual only) ──────────────────────────────────────

        private void OnPieceTaken(SequencingStation s, Item carrier, Item piece, int slot, double duration)
        {
            if (piece?.vItem is GameObject go && go != null) { go.transform.SetParent(null, true); go.transform.position = PickWorld; }
            LayOutOrders();
        }

        private void OnPiecePlaced(SequencingStation s, Item carrier, Item piece, int slot) => PutInSlot(carrier, piece, slot);

        private void OnOrderServed(SequencingStation s, Item order)
        {
            // An order that ends here (no Served Orders destination) has no further view.
            if (s.OrderPort.GetOutput() == null) Destroy(order);
            LayOutOrders();
        }

        // ── IVElement ────────────────────────────────────────────────────────

        void IVElement.ReportState(string msg) { }
        object IVElement.GenerateItem(int type) => null;

        void IVElement.LoadItem(Item item)
        {
            if (item?.vItem is GameObject go && go != null) go.transform.SetPositionAndRotation(CarrierWorld, transform.rotation);
        }

        void IVElement.UnloadItem(Item item) { }

        // ── gizmos ───────────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 0.9f, 0.9f);
            Gizmos.DrawWireCube(CarrierWorld + Vector3.up * 0.25f, new Vector3(1f, 0.5f, 0.8f));
            Gizmos.color = new Color(1f, 0.7f, 0.2f, 0.9f);
            Gizmos.DrawWireSphere(PickWorld, 0.15f);
            Gizmos.color = new Color(0.6f, 0.6f, 1f, 0.9f);
            for (int i = 0; i < 3; i++) Gizmos.DrawWireCube(OrderWorld(i), Vector3.one * 0.15f);
        }
    }
}
