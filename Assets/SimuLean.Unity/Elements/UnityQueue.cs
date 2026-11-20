using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>How items are visually distributed in the queue.</summary>
    public enum QueueLayoutMode
    {
        /// <summary>One column of items stacked upwards from the queue's top surface.</summary>
        Stack,

        /// <summary>
        /// Items fill a grid centred on the queue's footprint (its mesh, in its own axes), front row first; when the
        /// footprint is full the next items form a layer on top.
        /// </summary>
        Grid,

        /// <summary>
        /// Items take the queue's <c>Seat</c> nodes in hierarchy order (e.g. the library's waiting chairs): each stands
        /// on the floor just in front of its seat, facing the queue's front, −Z (lay the seats out facing it; the model
        /// frame); items beyond the last seat fall back to the grid.
        /// </summary>
        Seats,

        /// <summary>
        /// A single file (a waiting line): the head at the item position, the others behind it along the queue's −X,
        /// all facing the head (the flow, +X: the model frame). With a <c>lineLength</c> the line folds back
        /// beside itself when it reaches that length (a serpentine, as at an airport desk) instead of growing further.
        /// </summary>
        Line
    }

    /// <summary>
    /// Unity Component for ItemsQueue Element.
    /// Supports input validation through configurable InputStrategy.
    /// </summary>
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Buffers, "Queue", Description = "FIFO buffer with capacity", Prefab = "Queue.prefab", Order = 0)]
    public class UnityQueue : SElement, IVElement
    {

        ItemsQueue theQueue;

        // ── Queue Configuration ────────────────────────────────────────────
        public int capacity = 100;   // a buffer that holds one item surprises users

        [Tooltip("How the held items are offered downstream. FIFO: only the first (a refused first item holds the rest back). " +
                 "Any Accepted: each item, oldest first, until a receiver accepts one — the receiver chooses with its input " +
                 "strategy (e.g. a Combiner's pull mode: the sub-block of its own block).")]
        public SimuLean.OfferOrder offerOrder = SimuLean.OfferOrder.FIFO;
        [Tooltip("The order the held items are offered in (empty = oldest first): order keys 'key [desc], key ...', each a label " +
                 "or an expression over the item and the queue, e.g. 'priority desc, due' or 'item.prio * 10 + age desc'. FIFO " +
                 "offers only the best item; Any Accepted offers them best first. Ties: the oldest.")]
        public string offerSort = "";
        [Tooltip("Instead of Offer by: a release rule's type name (a class implementing IReleaseRule in Assets/SimuLean.UserLogic) " +
                 "that scores each held item; offered best first, ties by arrival.")]
        public string offerRule = "";
        [Tooltip("The release rule's parameters (its [RuleParameter] fields) and their values.")]
        public System.Collections.Generic.List<RuleParameterValue> ruleParameters = new System.Collections.Generic.List<RuleParameterValue>();
        [Tooltip("Any Accepted: items offered per attempt (0 = every held item). Group By Setup: the items looked through for " +
                 "one the receiver is set up for, too.")]
        [Min(0)] public int offerScan = 0;

        [Tooltip("Offer first the items the receiving station is already set up for (a station with a setup on label change): " +
                 "items of one type follow each other and the station avoids changeovers.")]
        public bool groupBySetup = false;
        [Tooltip("Group By Setup: the most items in a row that may leave ahead of the oldest one, then it goes first (so no item " +
                 "waits for ever). 0 = no limit.")]
        [Min(0)] public int groupLimit = 0;

        [Tooltip("Reneging: the longest an item waits in the queue (a time, or an expression over the item and the queue " +
                 "evaluated when it enters); empty or 0 = no limit.")]
        [SimuLean.Unity.SimuLeanExpression("maxWait")]
        public string maxWait = "";
        [Tooltip("When an item's max wait is over (always counted as a timeout and signalled: WaitTimedOut). Keep Waiting: only " +
                 "record it. Reroute / Give Up: the item leaves by the Timeout Target (a Sink = a lost customer); while the target " +
                 "is full it keeps its place in the line. Needs plain Next Elements (not a list or a transport team).")]
        public SimuLean.TimeoutAction onTimeout = SimuLean.TimeoutAction.KeepWaiting;
        [Tooltip("Where items that waited too long go (Reroute / Give Up), e.g. a Sink for the lost ones. Not one of the Next " +
                 "Elements: the normal routing never sends items there.")]
        public SElement timeoutTarget;

        [Tooltip("Items leave only when released: a reaction's Release action (orders every period, a kanban: one per item " +
                 "leaving the station) or the Release button in Play. A release not used yet is kept for the next items.")]
        public bool gated = false;

        /// <summary>Released on command: lets <paramref name="quantity"/> more items go (Play mode).</summary>
        public void ReleaseItems(int quantity)
        {
            if (theQueue != null && theQueue.Gated && quantity > 0) theQueue.ReleaseItems(quantity);
        }

        /// <summary>Releases not used yet (released on command).</summary>
        public int PendingRelease => theQueue?.PendingRelease ?? 0;




        // ── Visual Configuration ────────────────────────────────────────────
        [Tooltip("Grid: a grid centred on the queue's footprint, layers on top when full. Stack: one column upwards.")]
        public QueueLayoutMode layoutMode = QueueLayoutMode.Grid;

        [Tooltip("Distance between item centres. 0 = automatic, from the size of the items (a small gap between them).")]
        public float separation = 0f;

        [Tooltip("Grid area (width X, depth Z, in the queue's axes). 0 = the queue's footprint (its mesh).")]
        public Vector2 gridArea = Vector2.zero;

        [Tooltip("Where the items go: the centre of the grid / the base of the stack / the head of the line. Empty = the top of the queue's footprint.")]
        public Transform itemPosition;

        [Tooltip("Line layout: length of one run of the line (m) before it folds back beside itself. 0 = one straight line.")]
        [Min(0f)] public float lineLength = 0f;

        [Tooltip("Show the queue's own model (its pad). Off for a waiting line or a waiting list drawn by other means; the " +
                 "footprint still sizes the grid.")]
        public bool showFootprint = true;

        private void OnValidate()
        {
            ApplyFootprintVisibility();
#if UNITY_EDITOR
            // a waiting line's floor marking follows its length when it is edited (visual only, after the change is applied)
            UnityEditor.EditorApplication.delayCall += () => { if (this != null && !Application.isPlaying) FitLineMarking(); };
#endif
        }
        private void Awake() => ApplyFootprintVisibility();

        /// <summary>Default length of a waiting line's floor marking when the line has no <see cref="lineLength"/> (m).</summary>
        public const float DefaultMarkedLine = 4f;

        /// <summary>Fits the queue's floor marking (a child <see cref="UnityLaneMarking"/>, the Waiting Line preset)
        /// to its line: from a little ahead of the head (the item position) back along −X over <see cref="lineLength"/>
        /// (else <see cref="DefaultMarkedLine"/>), the exit chevron at the head. Visual only.</summary>
        public void FitLineMarking()
        {
            const float ahead = 0.4f;
            var marking = GetComponentInChildren<UnityLaneMarking>(true);
            if (marking == null) return;
            float length = (lineLength > 0f ? lineLength : DefaultMarkedLine) + 2f * ahead;
            marking.FitTo(length, 0f);
            Vector3 head = itemPosition != null && itemPosition != transform ? transform.InverseTransformPoint(itemPosition.position) : Vector3.zero;
            marking.transform.localPosition = new Vector3(head.x + ahead, marking.transform.localPosition.y, head.z);
        }

        /// <summary>Shows or hides the pad per <see cref="showFootprint"/>: the renderers of this GameObject only, never the
        /// items or the children (chairs, decor).</summary>
        public void ApplyFootprintVisibility()
        {
            var sm = GetComponent<StationModel>();
            bool show = sm != null ? StationModel.OwnModelShown(sm.look, gameObject) : showFootprint;   // one owner
            foreach (var r in GetComponents<Renderer>())
                if (r.enabled != show) r.enabled = show;
        }

        public int Capacity { get => capacity; set => capacity = value; }
        public int Content { get => theQueue.GetQueueLength(); }

        protected override void OnBound(Element built)
        {
            theQueue = (ItemsQueue)built;
        }

        void IVElement.ReportState(string msg)    => RepositionAllItems();
        object IVElement.GenerateItem(int type)     => null;
        void IVElement.LoadItem(Item vItem)         => RepositionAllItems();
        void IVElement.UnloadItem(Item vItem)       => RepositionAllItems();

        // ── Layout (shared with the other holding elements: ItemLayout) ─────

        private void RepositionAllItems()
            => ItemLayout.Arrange(this, theQueue.GetItems(), layoutMode, separation, gridArea, itemPosition, lineLength);

        /// <summary>The queue's <c>Seat</c> nodes in hierarchy order (<see cref="QueueLayoutMode.Seats"/>).</summary>
        public List<Transform> Seats() => ItemLayout.Seats(transform);

        /// <summary>The cells of one grid layer (columns × rows) for an item of the given size (zero = unknown).</summary>
        public Vector2Int GridCells(Vector3 itemSize) => ItemLayout.GridCells(this, separation, gridArea, itemSize);

        public override void ClearViews()
        {
            var items = new List<Item>(theQueue.GetItems());
            int i = 0;

            foreach (Item it in items)
            {
                Destroy((GameObject)it.vItem);
                i++;
            }

        }
    }
}
