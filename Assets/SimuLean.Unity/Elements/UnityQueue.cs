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
        /// on the floor just in front of its seat, facing the queue's +Z (lay the seats out facing it); items beyond the
        /// last seat fall back to the grid.
        /// </summary>
        Seats,

        /// <summary>
        /// A single file (a waiting line): the head at the item position, the others behind it along the queue's −Z,
        /// all facing the head (+Z: turn the queue towards what it feeds). With a <c>lineLength</c> the line folds back
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
        public int capacity = 100;   // a buffer that holds one item surprised users (review2 #43)

        [Tooltip("How the held items are offered downstream. FIFO: only the first (a refused first item holds the rest back). " +
                 "Any Accepted: each item, oldest first, until a receiver accepts one — the receiver chooses with its input " +
                 "strategy (e.g. a Combiner's pull mode: the sub-block of its own block).")]
        public SimuLean.ReleaseMode releaseMode = SimuLean.ReleaseMode.FIFO;
        [Tooltip("Any Accepted: items offered per attempt (0 = every held item).")]
        [Min(0)] public int releaseScan = 0;




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

        private void OnValidate() => ApplyFootprintVisibility();
        private void Awake() => ApplyFootprintVisibility();

        /// <summary>Shows or hides the pad per <see cref="showFootprint"/>: the renderers of this GameObject only, never the
        /// items or the children (chairs, decor).</summary>
        public void ApplyFootprintVisibility()
        {
            var sm = GetComponent<StationModel>();
            bool show = sm != null ? StationModel.OwnModelShown(sm.look, gameObject) : showFootprint;   // one owner (review2 #34)
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
