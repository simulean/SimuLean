using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Unity wrapper of <see cref="LineSideUnpacker"/> (Manufacturing pack, line supply): carriers (wagons,
    /// totes) arrive with their contents as sub-items and are emptied piece by piece as the line takes them; the empty
    /// carriers leave to <see cref="emptiesDestination"/> (e.g. the queue where a milk run collects them). Built by
    /// <c>LineSideUnpackerHandler</c>. View (read-only): the carriers stand at slots along the local +X axis; the
    /// pieces' GameObjects are children of their carrier's GameObject while inside it, and are detached when they leave (the
    /// next element places them).
    /// </summary>
    [AddComponentMenu("SimuLean/Line-Side Unpacker")]
    [SimuLeanElement(SimuLeanModules.Assembly, "Line-Side Unpacker", Model = "BufferPosition/BufferPosition.fbx",
        Description = "Empties carriers (wagons, totes) at the line piece by piece as the line takes them; the empty carriers wait to be collected",
        Order = 21, Pack = SimuLeanPacks.Manufacturing, Icon = "Assets/SimuLean.Unity/Art/Models/TuggerTrain/ToteTrolley.fbx")]
    public class UnityLineSideUnpacker : SElement, IVElement
    {
        [Header("Carriers")]
        [Tooltip("Carriers held at once (e.g. 2 wagons at a line-side point).")]
        [Min(1)] public int capacity = 2;
        [Tooltip("Order in which the pieces are taken out (FIFO = the order they were put in).")]
        public UnpackOrder unpackOrder = UnpackOrder.Fifo;
        [Tooltip("Copy the carrier's labels the piece does not have onto each piece.")]
        public bool copyCarrierLabels = false;
        [Tooltip("An empty carrier keeps its place until it has been collected (left to Empties Destination).")]
        public bool emptiesUseCapacity = true;
        [Tooltip("Where the empty carriers go (e.g. a queue a milk run collects from). Empty = after their pieces, to the next elements.")]
        [SimuLeanHelp("Where the empty carriers go (its empties port): with a milk run, a Queue at the train's stop listed in its " +
                      "stops[i].emptiesOrigins — never the unpacker itself.")]
        public SElement emptiesDestination;

        [Header("Times")]
        [Tooltip("Model a time to take each piece out (off = as soon as the line takes the previous one).")]
        public bool usePieceTime = false;
        public RandomProcessConfig pieceTime = new RandomProcessConfig();
        [Tooltip("Model a time to change to the next carrier (off = none).")]
        public bool useChangeTime = false;
        public RandomProcessConfig changeTime = new RandomProcessConfig();
        [Tooltip("The change time comes before the first carrier too (off = the line starts with the first carrier in place).")]
        [SimuLeanHelp("On = the change time is a handling time for every carrier, the first included (off, the default: " +
                      "the run's first carrier is taken as already in place, so it has no change time).")]
        public bool changeTimeFirst = false;

        [Header("View (local frame)")]
        [Tooltip("Position of the first carrier slot.")]
        public Vector3 firstSlot = Vector3.zero;
        [Tooltip("Offset between carrier slots.")]
        public Vector3 slotStep = new Vector3(1.5f, 0f, 0f);

        private LineSideUnpacker unpacker;

        /// <summary>The engine element of the run (null outside a run).</summary>
        public LineSideUnpacker Unpacker => unpacker;

        /// <summary>Writes its settings into the element's (the extractor's).</summary>
        public void Fill(LineSideUnpackerSettings s)
        {
            s.Capacity = Mathf.Max(1, capacity);
            s.UnpackOrder = unpackOrder;
            s.PieceTime = usePieceTime && pieceTime != null ? pieceTime.ToSettings() : null;
            s.ChangeTime = useChangeTime && changeTime != null ? changeTime.ToSettings() : null;
            s.ChangeTimeFirst = changeTimeFirst;
            s.CopyCarrierLabels = copyCarrierLabels;
            s.EmptiesUseCapacity = emptiesUseCapacity;
        }

        protected override void OnBound(Element built)
        {
            unpacker = (LineSideUnpacker)built;
            unpacker.ItemArrived += (u, c) => { AttachPieces(c); Layout(); };
            // off its carrier only while it is still on it (the event comes after the hand-over: the receiver may hold it)
            unpacker.PieceReleased += (u, c, p) =>
            {
                if (p.vItem is GameObject go && go != null && c?.vItem is GameObject carrier && carrier != null
                    && go.transform.IsChildOf(carrier.transform))
                    go.transform.SetParent(null, true);
            };
            unpacker.CarrierEmptied += (u, c) => Layout();
        }

        public override void ClearViews()
        {
            if (unpacker == null) return;
            foreach (var c in unpacker.Items) Destroy(c);
            Destroy(unpacker.ReadyPiece);
            foreach (var e in unpacker.EmptiesPort.Pending) Destroy(e);
        }

        private static void Destroy(Item it)
        {
            if (it?.vItem is GameObject go && go != null) Object.Destroy(go);
        }

        /// <summary>World position of carrier slot <paramref name="i"/>.</summary>
        public Vector3 SlotWorld(int i) => transform.TransformPoint(firstSlot + slotStep * i);

        private static void AttachPieces(Item carrier)
        {
            if (!(carrier?.vItem is GameObject cgo) || cgo == null || carrier.subItems == null) return;
            foreach (var p in carrier.subItems)
                if (p?.vItem is GameObject pgo && pgo != null && pgo.transform.parent != cgo.transform)
                    pgo.transform.SetParent(cgo.transform, true);
        }

        private void Layout()
        {
            if (unpacker == null) return;
            int i = 0;
            foreach (var c in unpacker.Items)
            {
                if (c?.vItem is GameObject go && go != null) go.transform.SetPositionAndRotation(SlotWorld(i), transform.rotation);
                i++;
            }
        }

        void IVElement.ReportState(string msg) { }
        object IVElement.GenerateItem(int type) => null;
        void IVElement.LoadItem(Item item) { }
        void IVElement.UnloadItem(Item item) { }

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
            for (int i = 0; i < Mathf.Max(1, capacity); i++)
                Gizmos.DrawWireCube(transform.TransformPoint(firstSlot + slotStep * i) + Vector3.up * 0.02f, new Vector3(1.2f, 0.04f, 0.8f));
        }
    }
}
