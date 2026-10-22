using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Unity wrapper of <see cref="LengthLimitedQueue"/>: one or more parallel accumulation lanes
    /// limited by the total length of the items on them (roller-conveyor buffers, floor lanes).
    /// Follows the standard <see cref="SElement"/> lifecycle (Start registers, the element is built
    /// in <see cref="InitializeSim"/>, wired by <see cref="SElement.ConnectSim"/>, started in
    /// <see cref="StartSim"/>) and is extracted for headless runs by <c>UnityLengthLimitedQueueHandler</c>.
    ///
    /// <para><b>Layout.</b> Local +Z of <see cref="laneOrigin"/> (default: this transform) is the flow
    /// direction and the origin is the lane <b>exit</b>: items are drawn end to end backwards along −Z
    /// with their real lengths and the gap, lane <i>i</i> offset by <c>i × laneSpacing</c> along +X.
    /// Item GameObjects are only positioned, never scaled. The lanes are drawn as gizmos.</para>
    /// </summary>
    [AddComponentMenu("SimuLean/Length-Limited Queue")]
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Buffers, "Length-Limited Lane", Description = "Accumulation lane(s) limited by the total item length (conveyors)", Order = 2, Icon = "Assets/SimuLean.Unity/Editor/Icons/RollerConveyor.png")]
    public class UnityLengthLimitedQueue : SElement, IVElement
    {
        private LengthLimitedQueue theQueue;

        [Header("Lanes")]
        [Tooltip("Length of each parallel lane (model units, metres by convention). One entry = a single lane.")]
        public List<float> laneLengths = new List<float> { 10f };

        [Tooltip("Free space kept between consecutive items on a lane.")]
        public float gap = 0.2f;

        [Tooltip("Length used when an item has no positive length label.")]
        public float defaultItemLength = 1f;

        [Tooltip("Numeric item label holding the item length (empty = always use the default length).")]
        public string lengthLabel = "length";

        [Tooltip("Optional count limit on top of the length rule (0 = unlimited).")]
        public int maxItems = 0;

        [Tooltip("An empty lane always takes one item, even a longer one (avoids deadlocks with oversize items).")]
        public bool acceptOversizeOnEmptyLane = true;


        [Header("Visualization")]
        [Tooltip("Lane exit and orientation (+Z = flow direction). Empty = this transform.")]
        public Transform laneOrigin;
        [Tooltip("Distance between parallel lanes along local +X.")]
        public float laneSpacing = 1.5f;
        [Tooltip("Height of the item pivot above the lane origin.")]
        public float itemHeight = 0.5f;

        /// <summary>The wrapped engine element (null before <see cref="InitializeSim"/>).</summary>
        public LengthLimitedQueue Queue => theQueue;

#if UNITY_EDITOR
        // a conveyor's rollers (child UnityRollerConveyor, one per lane in order) follow the lanes' lengths and spacing
        // when they are edited (the Conveyor preset); visual only, after the inspector change is applied
        private void OnValidate()
        {
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null || Application.isPlaying) return;
                int lane = 0;
                foreach (Transform child in transform)
                {
                    var rollers = child.GetComponent<UnityRollerConveyor>();
                    if (rollers == null) continue;
                    if (lane >= laneLengths.Count) break;
                    rollers.FitTo(laneLengths[lane], lane * laneSpacing);
                    lane++;
                }
            };
        }
#endif

        public int Content => theQueue != null ? theQueue.GetQueueLength() : 0;

        /// <summary>Lane lengths as doubles (invalid entries replaced by 1).</summary>
        public double[] GetLaneLengths()
        {
            if (laneLengths == null || laneLengths.Count == 0) return new[] { 10.0 };
            var a = new double[laneLengths.Count];
            for (int i = 0; i < a.Length; i++) a[i] = laneLengths[i] > 0 ? laneLengths[i] : 1.0;
            return a;
        }

        protected override void OnBound(Element built)
        {
            theQueue = (LengthLimitedQueue)built;
            if (laneOrigin == null) laneOrigin = transform;
        }

        public override void ClearViews()
        {
            if (theQueue != null)
                foreach (Item it in theQueue.Items)
                    if (it.vItem is GameObject go) Destroy(go);
        }

        // ── VElement (visual only) ───────────────────────────────────────────

        void IVElement.ReportState(string msg) => Reposition();
        object IVElement.GenerateItem(int type) => null;
        void IVElement.LoadItem(Item vItem) => Reposition();
        void IVElement.UnloadItem(Item vItem) => Reposition();

        private void Reposition()
        {
            if (theQueue == null) return;
            Transform o = laneOrigin != null ? laneOrigin : transform;
            foreach (Item it in theQueue.Items)
            {
                if (!(it.vItem is GameObject go) || go == null) continue;
                int lane = theQueue.LaneOf(it);
                double z = theQueue.OffsetFromExit(it) + theQueue.LengthOf(it) / 2.0;
                go.transform.position = o.TransformPoint(new Vector3(lane * laneSpacing, itemHeight, -(float)z));
                go.transform.rotation = o.rotation;
            }
        }

        // ── authoring gizmos ─────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            Transform o = laneOrigin != null ? laneOrigin : transform;
            if (laneLengths == null) return;
            Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.8f);
            var m = Gizmos.matrix;
            Gizmos.matrix = o.localToWorldMatrix;
            for (int i = 0; i < laneLengths.Count; i++)
            {
                float len = Mathf.Max(0.01f, laneLengths[i]);
                Gizmos.DrawWireCube(new Vector3(i * laneSpacing, 0.02f, -len / 2f), new Vector3(Mathf.Max(0.1f, laneSpacing * 0.8f), 0.04f, len));
                Gizmos.DrawLine(new Vector3(i * laneSpacing - 0.2f, 0.05f, -0.3f), new Vector3(i * laneSpacing, 0.05f, 0f));   // exit arrow
                Gizmos.DrawLine(new Vector3(i * laneSpacing + 0.2f, 0.05f, -0.3f), new Vector3(i * laneSpacing, 0.05f, 0f));
            }
            Gizmos.matrix = m;
        }
    }
}
