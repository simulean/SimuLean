using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Unity wrapper of <see cref="LengthLimitedQueue"/>: one or more parallel accumulation lanes
    /// limited by the total length of the items on them (roller-conveyor buffers, floor lanes).
    /// Follows the standard <see cref="SElement"/> lifecycle (Start registers, the element is built
    /// in <see cref="InitializeSim"/>, wired by <see cref="SElement.ConnectSim"/>, started in
    /// <see cref="StartSim"/>) and is extracted for headless runs by <c>LengthLimitedQueueHandler</c>.
    ///
    /// <para><b>Layout (the library's element frame).</b> Local +X of <see cref="laneOrigin"/> (default: this
    /// transform) is the flow direction and the origin is the lane <b>exit</b>: items are drawn end to end backwards along
    /// −X with their real lengths and the gap (their +Z along the flow, <see cref="ItemVisuals.AlongFlow"/>), lane <i>i</i>
    /// offset by <c>i × laneSpacing</c> towards the front (−Z). Item GameObjects are only positioned, never scaled. The
    /// lanes are drawn as gizmos.</para>
    /// </summary>
    [AddComponentMenu("SimuLean/Length-Limited Queue")]
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Buffers, "Length-Limited Lane", InLibrary = false, Description = "Accumulation lane(s) limited by the total item length (conveyors)", Order = 2, Icon = "Assets/SimuLean.Unity/Editor/Icons/RollerConveyor.png")]
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

        [Tooltip("Conveying speed (model length units per time unit). 0 = an accumulation lane with no transit time; > 0 = a " +
                 "conveyor: an item takes the lane's length ÷ speed to reach the end, then leaves from the front.")]
        [Min(0f)] public float speed = 0f;


        [Header("Visualization")]
        [Tooltip("Lane exit and orientation (+X = flow direction). Empty = this transform.")]
        public Transform laneOrigin;
        [Tooltip("Distance between parallel lanes, towards the front (local −Z).")]
        public float laneSpacing = 1.5f;
        [Tooltip("Height of the item pivot above the lane origin.")]
        public float itemHeight = 0.5f;

        /// <summary>The wrapped engine element (null before <see cref="InitializeSim"/>).</summary>
        public LengthLimitedQueue Queue => theQueue;

        /// <summary>A conveyor's rollers (child <see cref="UnityRollerConveyor"/>, one per lane in order; the Conveyor preset)
        /// and an accumulation lane's floor marking (<see cref="UnityLaneMarking"/>) fitted to the lanes' lengths and spacing.
        /// Visual only: the inspector calls it after an edit, and so does <c>simulean_set</c>.</summary>
        public void FitLaneViews()
        {
            int lane = 0;
            foreach (Transform child in transform)
            {
                var rollers = child.GetComponent<UnityRollerConveyor>();
                var marking = child.GetComponent<UnityLaneMarking>();
                if (rollers == null && marking == null) continue;
                if (lane >= laneLengths.Count) break;
                if (rollers != null) rollers.FitTo(laneLengths[lane], lane * laneSpacing);
                else marking.FitTo(laneLengths[lane], lane * laneSpacing);
                lane++;
            }
        }

#if UNITY_EDITOR
        // the views follow an inspector edit, after the change is applied
        private void OnValidate()
        {
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null || Application.isPlaying) return;
                FitLaneViews();
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

        // A conveyor (speed > 0): the items move along the lane every frame (read-only, as every view).
        private void Update()
        {
            if (theQueue != null && theQueue.Speed > 0 && theQueue.GetQueueLength() > 0) Reposition();
        }

        private void Reposition()
        {
            if (theQueue == null) return;
            Transform o = laneOrigin != null ? laneOrigin : transform;
            foreach (Item it in theQueue.Items)
            {
                if (!(it.vItem is GameObject go) || go == null) continue;
                int lane = theQueue.LaneOf(it);
                double z = theQueue.DistanceToExit(it) + theQueue.LengthOf(it) / 2.0;
                go.transform.position = o.TransformPoint(new Vector3(-(float)z, itemHeight, -lane * laneSpacing));
                go.transform.rotation = o.rotation * ItemVisuals.AlongFlow;
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
                float z = -i * laneSpacing;
                Gizmos.DrawWireCube(new Vector3(-len / 2f, 0.02f, z), new Vector3(len, 0.04f, Mathf.Max(0.1f, laneSpacing * 0.8f)));
                Gizmos.DrawLine(new Vector3(-0.3f, 0.05f, z + 0.2f), new Vector3(0f, 0.05f, z));   // exit arrow
                Gizmos.DrawLine(new Vector3(-0.3f, 0.05f, z - 0.2f), new Vector3(0f, 0.05f, z));
            }
            Gizmos.matrix = m;
        }
    }
}
