using SimuLean;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Unity Component for Sink Element.
    /// Collects simulation metrics for optimization.
    /// </summary>
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Sinks, "Sink", Description = "Consumes items and records statistics", Prefab = "Sink.prefab", Order = 0)]
    public class UnitySink : SElement, IVElement
    {
        public override bool HasOutput => false;


        Sink theSink;

        [Tooltip("Expected number of items (for simulation progress tracking)")]
        public int expectedItems = 0;

        [Tooltip("Keep the received items (a finished-goods area, a launched ship): they stay in the scene instead of " +
                 "disappearing, laid out by their position labels or in a row.")]
        public bool keepItems = false;
        [Tooltip("Kept items: the labels of their position from this sink (x along, y up, z across, m), e.g. a block's place " +
                 "on the hull. Items without them stand in a row along +X.")]
        public string positionLabels = "hull_x,hull_z,hull_y";

        private float keptRow;
        private readonly System.Collections.Generic.List<GameObject> keptViews = new System.Collections.Generic.List<GameObject>();


        public int Input => theSink?.GetNumberItems() ?? 0;
        
        /// <summary>
        /// Gets the total number of items received by the sink.
        /// </summary>
        public int GetNumberItems() => theSink?.GetNumberItems() ?? 0;
        
        
        protected override void OnBound(Element built) => theSink = (Sink)built;

        void IVElement.ReportState(string msg)
        {
        }

        object IVElement.GenerateItem(int type)
        {
            return null;
        }

        void IVElement.LoadItem(Item vItem)
        {
            var gItem = vItem.vItem as GameObject;
            if (gItem == null) return;
            if (!keepItems)
            {
                // a patient still drawn on its way finishes walking to the door (ItemWalkView), then goes
                if (!ItemWalkView.DestroyWhenArrived(vItem, gItem)) Destroy(gItem);
                return;
            }
            keptViews.Add(gItem);
            gItem.transform.SetParent(transform, true);
            gItem.transform.SetPositionAndRotation(KeptPosition(vItem, gItem), transform.rotation);
            // the row's length kept as it grows (it summed every kept item for each new one: O(n²))
            keptRow += ItemVisuals.SizeOf(gItem).x * 1.1f;
        }

        // by the item's position labels, else in a row along +X (each item's own length apart)
        private Vector3 KeptPosition(Item item, GameObject view)
        {
            var labels = (positionLabels ?? "").Split(',');
            if (labels.Length == 3)
            {
                double? x = item.GetLabelValue(labels[0].Trim()), y = item.GetLabelValue(labels[1].Trim()), z = item.GetLabelValue(labels[2].Trim());
                if (x.HasValue || y.HasValue || z.HasValue)
                    return transform.TransformPoint(new Vector3((float)(x ?? 0), (float)(y ?? 0), (float)(z ?? 0)))
                           + transform.up * ItemVisuals.PivotHeight(view);
            }
            return transform.position + transform.right * keptRow + transform.up * ItemVisuals.PivotHeight(view);
        }

        void IVElement.UnloadItem(Item vItem)
        {
        }

        public override void ClearViews()
        {
            foreach (var g in keptViews) if (g != null) Destroy(g);
            keptViews.Clear();
            keptRow = 0f;
        }
    }
}