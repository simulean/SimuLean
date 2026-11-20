using System.Collections.Generic;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Unity wrapper of <see cref="Separator"/> (Core; the inverse of the <see cref="UnityCombiner"/>):
    /// <b>Unpack</b> empties containers — their contents leave through the next elements, the empty container
    /// goes to <see cref="emptiesDestination"/> (none = after its contents, through the next elements) — or <b>Split</b>
    /// makes N items of each one. Built by <c>SeparatorHandler</c>. View (read-only), the Combiner's in reverse: the
    /// items held stand at slots along the local +X axis (the first on the work point: <see cref="itemPosition"/>, the
    /// library model's <c>WorkSocket</c> — the UnpackingStation's roller section); during the process time the contents
    /// travel one after another off the container to <see cref="piecesPosition"/> (else the model's <c>PiecesSocket</c>,
    /// its worktop), where they wait to leave packed in <see cref="piecesArea"/> (<see cref="animateSeparation"/>); a
    /// split copy is a copy of the original's GameObject.
    /// </summary>
    [AddComponentMenu("SimuLean/Separator")]
    [SimuLeanElement(SimuLeanModules.Processors, "Separator", Order = 2, Model = "UnpackingStation/UnpackingStation.fbx",
        Description = "The inverse of the Combiner: unpacks containers (contents out, the empty container through its own port) or splits an item into N",
        Icon = "Assets/SimuLean.Unity/Art/Models/UnpackingStation/UnpackingStation.fbx")]
    public class UnitySeparator : SElement, IVElement
    {
        [Header("Separation")]
        [Tooltip("Unpack: the contents of each container leave, then the empty container. Split: each item leaves as N items.")]
        public SeparatorMode mode = SeparatorMode.Unpack;
        [Tooltip("Items held at once.")]
        [Min(1)] public int capacity = 1;
        [Tooltip("Unpack: order in which the contents leave (FIFO = the order they were put in).")]
        public UnpackOrder unpackOrder = UnpackOrder.Fifo;
        [Tooltip("Unpack: where the empty containers go. Empty = after their contents, to the next elements.")]
        public SElement emptiesDestination;
        [Tooltip("Split: the number of items each item leaves as (itself included), e.g. 3 or an expression such as item.n.")]
        public RandomProcessConfig splitQuantity = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2 };

        [Header("Time")]
        [Tooltip("Model a process time per item, before anything of it leaves (off = none).")]
        public bool useProcessTime = false;
        public RandomProcessConfig processTime = new RandomProcessConfig();

        [Header("View (local frame)")]
        [Tooltip("The work point, where the first item held stands (bound to the station model's WorkSocket). Empty = the " +
                 "element's origin.")]
        public Transform itemPosition;
        [Tooltip("Position of the first item slot, from the work point.")]
        public Vector3 firstSlot = Vector3.zero;
        [Tooltip("Offset between item slots.")]
        public Vector3 slotStep = new Vector3(1.5f, 0f, 0f);
        [Tooltip("Where the separated pieces wait to leave, packed round it in the pieces area. Empty = the station model's " +
                 "PiecesSocket, else behind the work point.")]
        public Transform piecesPosition;
        [Tooltip("Size (x, z in m, in the pieces position's axes) of the area where the pieces wait: side by side along x, " +
                 "then rows, then layers (the UnpackingStation's worktop: 1.3 x 0.7).")]
        public Vector2 piecesArea = new Vector2(1.3f, 0.7f);
        [Tooltip("The contents travel off the container one after another during the process time (off = they stay on it " +
                 "until they leave).")]
        public bool animateSeparation = true;

        private Separator separator;

        /// <summary>The engine element of the run (null outside a run).</summary>
        public Separator Separator => separator;

        /// <summary>Writes its settings into the element's (the extractor's).</summary>
        public void Fill(SeparatorSettings s)
        {
            s.Mode = mode;
            s.Capacity = Mathf.Max(1, capacity);
            s.UnpackOrder = unpackOrder;
            s.ProcessTime = useProcessTime && processTime != null ? processTime.ToSettings() : null;
            s.SplitQuantity = mode == SeparatorMode.Split && splitQuantity != null ? splitQuantity.ToSettings() : null;
        }

        protected override void OnBound(Element built)
        {
            separator = (Separator)built;
            separator.ItemArrived += (s, it) => { AttachPieces(it); Layout(); };
            separator.ProcessStarted += (s, it, start, duration) => { if (animateSeparation) StartMoves(it, start, duration); };
            separator.PieceReleased += (s, from, p) =>
            {
                if (!(p.vItem is GameObject go) || go == null) return;
                moves.RemoveAll(m => m.Part == go.transform);
                // off its container only while it is still on it: the event comes after the hand-over, and a Combiner
                // that took the piece may have put it on its own load already (Split copies were taken off
                // their pallet and fell to the world's origin)
                if (from?.vItem is GameObject container && container != null && go.transform.IsChildOf(container.transform))
                    go.transform.SetParent(null, true);
            };
            separator.CarrierEmptied += (s, c) => Layout();
            separator.ItemSplit += (s, original, copy) =>
            {
                if (original.vItem is GameObject g && g != null)
                    copy.vItem = Instantiate(g, g.transform.position + Vector3.up * ItemVisuals.SizeOf(g).y * (1.05f * (separator.SplitCopies + 1)), g.transform.rotation);
            };
        }

        public override void ClearViews()
        {
            moves.Clear();
            if (separator == null) return;
            foreach (var p in separator.HeldPieces) Destroy(p);
            foreach (var it in separator.Items) Destroy(it);
            Destroy(separator.ReadyPiece);
            foreach (var e in separator.EmptiesPort.Pending) Destroy(e);
        }

        private static void Destroy(Item it)
        {
            if (it?.vItem is GameObject go && go != null) Object.Destroy(go);
        }

        /// <summary>World position of item slot <paramref name="i"/> (the bottom of the item there): from the work point
        /// (<see cref="itemPosition"/>, else the origin), in the element's axes.</summary>
        public Vector3 SlotWorld(int i)
        {
            Vector3 offset = firstSlot + slotStep * i;
            return itemPosition != null ? itemPosition.position + transform.rotation * Vector3.Scale(offset, transform.lossyScale)
                                        : transform.TransformPoint(offset);
        }

        /// <summary>Where the separated pieces wait: <see cref="piecesPosition"/>, else the station model's
        /// <c>PiecesSocket</c>, else null (behind the work point).</summary>
        public Transform PiecesAt() => piecesPosition != null ? piecesPosition : StationModel.Position(this, "PiecesSocket");

        /// <summary>Where separated pieces of <paramref name="sizes"/> (in the pieces position's axes) wait, from its point,
        /// in its axes (the bottom centre of each): side by side along x, then rows, then layers, packed in an area of
        /// <paramref name="area"/> (x, z) and centred (<see cref="UnityCombiner.PackOnTop"/>).</summary>
        public static List<Vector3> PieceSpots(Vector2 area, IList<Vector3> sizes)
            => UnityCombiner.PackOnTop(new Vector3(area.x, 0f, area.y), sizes);

        private static void AttachPieces(Item container)
        {
            if (!(container?.vItem is GameObject cgo) || cgo == null || container.subItems == null) return;
            foreach (var p in container.subItems)
                if (p?.vItem is GameObject pgo && pgo != null && pgo.transform.parent != cgo.transform)
                    pgo.transform.SetParent(cgo.transform, true);
        }

        private void Layout()
        {
            if (separator == null) return;
            int i = 0;
            foreach (var it in separator.Items)
            {
                if (it?.vItem is GameObject go && go != null)
                {
                    go.transform.rotation = transform.rotation * ItemVisuals.AlongFlow;   // lying along the flow (a pallet on the rollers)
                    go.transform.position = SlotWorld(i) + transform.up * ItemVisuals.PivotHeight(go);   // its bottom on the slot
                }
                i++;
            }
        }

        // ── the contents travelling off the container (view only) ────────────

        private sealed class PartMove
        {
            public Transform Part;
            public Vector3 From, To;
            public Quaternion FromRot, ToRot;
            public double Start, Duration;
            public int Index, Count;
        }

        private readonly List<PartMove> moves = new List<PartMove>();

        private void StartMoves(Item container, double start, double duration)
        {
            if (mode != SeparatorMode.Unpack || duration <= 0 || UnitySimClock.Instance == null || container.subItems == null) return;
            var parts = new List<Transform>();
            foreach (var p in container.subItems)
                if (p?.vItem is GameObject go && go != null) parts.Add(go.transform);
            if (parts.Count == 0) return;
            if (unpackOrder == UnpackOrder.Lifo) parts.Reverse();
            Transform pieces = PiecesAt();
            Transform at = pieces != null ? pieces : transform;
            Vector3 origin = pieces != null ? pieces.position : SlotWorld(0) + transform.forward * 1.2f;   // behind the work point (the front is −Z)
            var sizes = new List<Vector3>(parts.Count);
            foreach (var t in parts)
            {
                // its size in the pieces position's axes, where it will lie turned as that position
                Vector3 s = Quaternion.Inverse(t.rotation) * ItemVisuals.SizeOf(t.gameObject);
                sizes.Add(new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
            }
            var spots = PieceSpots(piecesArea, sizes);
            for (int k = 0; k < parts.Count; k++)
            {
                Transform t = parts[k];
                float pivot = ItemVisuals.PivotHeight(t.gameObject);
                t.SetParent(null, true);
                Vector3 spot = spots[k];
                moves.Add(new PartMove
                {
                    Part = t, From = t.position, FromRot = t.rotation,
                    To = origin + at.right * spot.x + at.forward * spot.z + at.up * (spot.y + pivot), ToRot = at.rotation,
                    Start = start, Duration = duration, Index = k, Count = parts.Count,
                });
            }
        }

        void LateUpdate()
        {
            if (moves.Count == 0) return;
            var clock = UnitySimClock.Instance;
            double now = clock != null ? clock.SmoothSimTime : double.MaxValue;
            for (int i = moves.Count - 1; i >= 0; i--)
            {
                var mv = moves[i];
                if (mv.Part == null) { moves.RemoveAt(i); continue; }
                float p = UnityCombiner.MoveProgress(now, mv.Start, mv.Duration, mv.Index, mv.Count);
                mv.Part.SetPositionAndRotation(UnityCombiner.MovePose(mv.From, mv.To, p), Quaternion.Slerp(mv.FromRot, mv.ToRot, p));
                if (p >= 1f) moves.RemoveAt(i);
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
                Gizmos.DrawWireCube(SlotWorld(i) + Vector3.up * 0.02f, new Vector3(1.2f, 0.04f, 0.8f));
            Transform pieces = PiecesAt();
            if (pieces != null)
            {
                Gizmos.color = new Color(1f, 0.7f, 0.2f, 0.9f);
                Gizmos.matrix = Matrix4x4.TRS(pieces.position, pieces.rotation, Vector3.one);
                Gizmos.DrawWireCube(Vector3.up * 0.02f, new Vector3(piecesArea.x, 0.04f, piecesArea.y));
                Gizmos.matrix = Matrix4x4.identity;
            }
        }
    }
}
