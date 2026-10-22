using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Unity component of a <see cref="Delay"/>: an activity without capacity where every item
    /// waits its own delay at once — a patient at home for a night, a therapy of three months, a curing time, a transit.
    /// Its items are laid out as a queue's (<see cref="ItemLayout"/>), and can be hidden behind a count with an
    /// <see cref="UnityElementLabel"/>.
    /// </summary>
    [SimuLeanElement(SimuLeanModules.Processors, "Delay", Icon = "Assets/SimuLean.Unity/Editor/Icons/Delay.png",
        Description = "Every item waits its own delay, all at once (no capacity): a night at home, a therapy, a curing time", Order = 5)]
    public class UnityDelay : SElement, IVElement
    {
        private Delay theDelay;

        [Tooltip("The delay of each item, drawn when it enters (an expression reads the item's labels).")]
        public RandomProcessConfig delayStrategy = new RandomProcessConfig();

        // ── Visual Configuration (as a queue's) ─────────────────────────────
        [Tooltip("How the items are drawn: a grid on the footprint, a stack, the Seat nodes or a single line.")]
        public QueueLayoutMode layoutMode = QueueLayoutMode.Grid;
        [Tooltip("Distance between item centres. 0 = automatic, from the size of the items.")]
        public float separation = 0f;
        [Tooltip("Grid area (width X, depth Z). 0 = the element's footprint.")]
        public Vector2 gridArea = Vector2.zero;
        [Tooltip("Where the items go. Empty = the top of the element's footprint.")]
        public Transform itemPosition;
        [Tooltip("Line layout: length of one run before it folds back. 0 = one straight line.")]
        [Min(0f)] public float lineLength = 0f;

        /// <summary>Items held now (in their delay or waiting to leave).</summary>
        public int Content => theDelay?.GetQueueLength() ?? 0;

        protected override void OnBound(Element built) => theDelay = (Delay)built;

        private void Reposition()
        {
            if (theDelay != null) ItemLayout.Arrange(this, theDelay.Items, layoutMode, separation, gridArea, itemPosition, lineLength);
        }

        void IVElement.ReportState(string msg) => Reposition();
        object IVElement.GenerateItem(int type) => null;
        void IVElement.LoadItem(Item vItem) => Reposition();
        void IVElement.UnloadItem(Item vItem) => Reposition();

        public override void ClearViews()
        {
            if (theDelay == null) return;
            foreach (Item it in new List<Item>(theDelay.Items))
                if (it.vItem is GameObject g && g != null) Destroy(g);
        }
    }
}
