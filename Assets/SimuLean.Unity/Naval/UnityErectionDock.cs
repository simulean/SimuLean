using System.Collections.Generic;
using SimuLean.Naval;
using UnityEngine;

namespace SimuLean.Unity.Naval
{
    /// <summary>
    /// The scene view of <see cref="ErectionDock"/>: a slipway / building dock that erects ship blocks
    /// in sequence or by precedence, keeps the hull and launches the complete ship. A block being erected hangs over its
    /// place on the hull (its position labels, from this transform: x along, y up, z across) and settles when erected; the
    /// launched ship gathers the hull's blocks into one object that travels on to the launch sink. Parameters are
    /// extracted to the engine element (one build path); this component only shows the run.
    /// </summary>
    [SimuLeanElement(SimuLeanModules.Shipbuilding, "Erection Dock",
        Description = "Slipway / building dock: erects ship blocks in sequence or by precedence, keeps the hull, launches the ship",
        Icon = "Assets/SimuLean.Unity/Art/Models/BlockWorkspace/BlockWorkspace.fbx", Order = 0)]
    public class UnityErectionDock : SElement, IVElement, IProcessResourceUser
    {
        public override bool HasInput => true;

        [Tooltip("Erection fronts: blocks erected at the same time.")]
        [Min(1)] public int capacity = 1;
        [Tooltip("Sequence: only the next block of the erection sequence (its sequence label). Precedence: any block whose " +
                 "predecessors (its predecessors label) are all erected.")]
        public ErectionDock.ErectionRule erectionRule = ErectionDock.ErectionRule.Sequence;
        [Tooltip("Erection time of a block (an expression may read its labels, e.g. 2 + 0.05 * weight).")]
        public RandomProcessConfig delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 24 };
        [Tooltip("Blocks of a complete hull: then it leaves as one ship item to the output. 0 = the hull stays.")]
        [Min(0)] public int expectedBlocks = 0;

        [Header("Block labels")]
        public string blockLabel = "block";
        public string sequenceLabel = "erect_seq";
        public string predecessorsLabel = "erect_after";
        public string shipName = "Ship";

        [Header("Visual")]
        [Tooltip("Labels of a block's place on the hull from this dock (x along, y up, z across, m).")]
        public string positionLabels = "hull_x,hull_z,hull_y";
        [Tooltip("How high a block hangs over its place while it is erected (m).")]
        [Min(0)] public float hoistHeight = 3f;

        private ErectionDock dock;
        private readonly List<GameObject> views = new List<GameObject>();

        public ErectionDock Dock => dock;

        protected override void OnBound(Element built) => dock = (ErectionDock)built;

        void IVElement.ReportState(string msg)
        {
            if (dock == null) return;
            if (msg == "Erected")
                foreach (var block in dock.Hull) Place(block, 0f);   // settled on its place
        }

        // the launched ship: one object holding the hull's block views
        object IVElement.GenerateItem(int type)
        {
            if (dock == null) return null;
            var ship = new GameObject($"{shipName} {type}");
            ship.transform.SetPositionAndRotation(transform.position, transform.rotation);
            foreach (var block in dock.Hull)
                if (block.vItem is GameObject g && g != null) { g.transform.SetParent(ship.transform, true); views.Remove(g); }
            return ship;
        }

        void IVElement.LoadItem(Item block)
        {
            if (block.vItem is GameObject g && g != null) { views.Add(g); g.transform.SetParent(transform, true); }
            Place(block, hoistHeight);
        }

        void IVElement.UnloadItem(Item block) { }

        private void Place(Item block, float lift)
        {
            if (!(block.vItem is GameObject g) || g == null) return;
            var labels = (positionLabels ?? "").Split(',');
            Vector3 local = Vector3.zero;
            if (labels.Length == 3)
                local = new Vector3((float)(block.GetLabelValue(labels[0].Trim()) ?? 0),
                                    (float)(block.GetLabelValue(labels[1].Trim()) ?? 0),
                                    (float)(block.GetLabelValue(labels[2].Trim()) ?? 0));
            g.transform.SetPositionAndRotation(transform.TransformPoint(local) + transform.up * (lift + ItemVisuals.PivotHeight(g)),
                                               transform.rotation);
        }

        public override void ClearViews()
        {
            foreach (var g in views) if (g != null) Destroy(g);
            views.Clear();
        }
    }
}
