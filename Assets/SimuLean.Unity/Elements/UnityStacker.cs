using System.Collections.Generic;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Optional animator of a <see cref="UnityStacker"/> (e.g. <see cref="GantryStackerView"/>): an item the
    /// animator is still moving (<see cref="IsAnimating"/>) is not snapped by the stacker when the element places it;
    /// the animator puts it on its slot (<see cref="UnityStacker.SlotWorldPosition"/>) when its animation ends.
    /// </summary>
    public interface IStackerAnimator
    {
        /// <summary>True while <paramref name="item"/> has a pending or running animation.</summary>
        bool IsAnimating(Item item);

        /// <summary>Finish at once every pending animation that targets <paramref name="position"/>
        /// (called when that stack completes, before it is grouped and sent on).</summary>
        void Flush(int position);
    }

    /// <summary>
    /// Unity wrapper of <see cref="Stacker"/>: receives items of any type at a pick point and builds stacks at N
    /// positions; a finished stack leaves as one compound item whose GameObject is a new parent of the stacked
    /// items' GameObjects, so downstream elements move the whole stack. Standard <see cref="SElement"/> lifecycle
    /// (Start registers, the element is built in <see cref="InitializeSim"/>, wired by <see cref="SElement.ConnectSim"/>,
    /// started in <see cref="StartSim"/>); extracted for headless runs by <c>StackerHandler</c>.
    ///
    /// <para><b>Layout</b> (local frame of this transform): stack positions at the <see cref="positionAnchors"/> (if
    /// given) or at <see cref="firstPosition"/> + i × <see cref="positionStep"/>; the stack base is
    /// <see cref="stackBaseHeight"/> above each position (table / pallet top); the pick point is
    /// <see cref="pickPoint"/> or <see cref="pickOffset"/>. Items are only positioned, never scaled; each takes the
    /// item height of the element (height label) or <see cref="itemThickness"/>.</para>
    ///
    /// <para><b>Visuals.</b> Without an animator the items snap to their slot when placed (the right thing in fast
    /// mode and at high speed). Add a <see cref="GantryStackerView"/> to animate a gantry (pick → travel → place)
    /// in detailed mode (<see cref="SimVisualDetail"/>). Visual only: neither changes the simulation.</para>
    /// </summary>
    [AddComponentMenu("SimuLean/Stacker")]
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Assembly, "Stacker", Model = "BufferPosition/BufferPosition.fbx", Description = "Builds stacks at N positions, released as compound items", Order = 12, Icon = "Assets/SimuLean.Unity/Art/Models/BufferPosition/BufferPosition.fbx")]
    public class UnityStacker : SElement, IVElement, IProcessResourceUser, ISetupUser
    {
        [Tooltip("Setup (a gripper / pattern change) before an item's cycle: when, how long, by whom (Setup section).")]
        public StationSetupConfig setup = new StationSetupConfig();
        public StationSetupConfig Setup => setup;

        private Stacker theStacker;

        [Header("Stacks")]
        [Tooltip("Number of stacking positions.")]
        [Min(1)] public int positionCount = 2;

        [Tooltip("Items per stack (default of every position).")]
        [Min(1)] public int capacity = 10;

        [Tooltip("Optional per-position capacities (index = position; ≤ 0 or missing = the default capacity).")]
        public List<int> positionCapacities = new List<int>();

        [Tooltip("Height limit of a stack (0 = none). Uses the item height label below.")]
        [Min(0f)] public float maxHeight = 0f;

        [Tooltip("Numeric item label holding the item height (empty = always use the default height).")]
        public string heightLabel = "height";

        [Tooltip("Height of items without a height label (0 = they do not count towards the height limit).")]
        [Min(0f)] public float defaultItemHeight = 0f;

        [Tooltip("An empty position always takes one item, even a higher one (avoids deadlocks).")]
        public bool acceptOversizeOnEmpty = true;

        [Header("Position choice")]
        [Tooltip("Which position gets each item: first not full, round robin, a label's index or a key label.")]
        public StackSelectionRule selectionRule = StackSelectionRule.FirstNotFull;

        [Tooltip("ByLabelIndex: numeric label with the 0-based target position.")]
        public string positionLabel = Stacker.LabelPosition;

        [Tooltip("ByKey: label whose value is the stack key (empty = the item type).")]
        public string keyLabel = "";

        [Tooltip("Close the open stack in the way when the waiting item cannot be placed anywhere (no deadlock).")]
        public bool releasePartialWhenBlocked = true;

        [Header("Pick-and-place cycle")]
        [Tooltip("Model a pick-and-place time per item (off = instantaneous placement).")]
        public bool useCycleTime = false;
        [Tooltip("Time of one pick-and-place cycle (expressions read the item and the stacker).")]
        public RandomProcessConfig cycleTime = new RandomProcessConfig();

        [Header("Released stacks")]
        [Tooltip("Item type name of the released stacks.")]
        public string stackType = "Stack";
        [Tooltip("Copy the labels of each stack's first item to the stack item.")]
        public bool copyLabelsFromFirst = true;


        [Header("Layout (local frame)")]
        [Tooltip("Optional explicit stack positions (one Transform per position; empty = generated row).")]
        public List<Transform> positionAnchors = new List<Transform>();
        // the element frame: the items come in on −X (the pick point) and the row runs along X
        [Tooltip("Generated row: first position (local).")]
        public Vector3 firstPosition = new Vector3(3.5f, 0f, 0f);
        [Tooltip("Generated row: offset between positions (local).")]
        public Vector3 positionStep = new Vector3(-2.5f, 0f, 0f);
        [Tooltip("Height of the stack base above each position (table / pallet top).")]
        public float stackBaseHeight = 0.55f;
        [Tooltip("Pick point Transform (empty = pickOffset). Its position is where the item rests before being picked.")]
        public Transform pickPoint;
        [Tooltip("Pick point (local) when no Transform is given: where the incoming item rests (its base).")]
        public Vector3 pickOffset = new Vector3(-4.5f, 0.9f, 0f);

        [Header("Crane (optional)")]
        [Tooltip("Pick point as a location: with a team on axes in Resources ▸ Process, the crane does every cycle " +
                 "(travel to the pick point, load, travel to the position, unload). Empty = cycle by time.")]
        public UnityTransportNode pickNode;
        [Tooltip("One location per stack position (crane mode).")]
        public List<UnityTransportNode> positionNodes = new List<UnityTransportNode>();
        [Tooltip("Visual height of items with no height label.")]
        [Min(0.001f)] public float itemThickness = 0.05f;
        [Tooltip("Item GameObjects have their pivot at the centre (true) or at the bottom (false).")]
        public bool itemPivotAtCentre = true;

        /// <summary>The wrapped engine element (null before <see cref="InitializeSim"/>).</summary>
        public Stacker Stacker => theStacker;

        /// <summary>Optional animator (set by the view while it animates).</summary>
        public IStackerAnimator Animator { get; set; }

        public int Content => theStacker != null ? theStacker.GetQueueLength() : 0;

        private int stackGoCount;

        /// <summary>Writes its settings into the element's (the extractor's).</summary>
        public void Fill(StackerSettings s)
        {
            s.PositionCount = Mathf.Max(1, positionCount);
            s.Capacity = Mathf.Max(1, capacity);
            s.PositionCapacities = positionCapacities != null && positionCapacities.Count > 0 ? positionCapacities.ToArray() : null;
            s.MaxHeight = maxHeight;
            s.HeightLabel = heightLabel ?? "";
            s.DefaultItemHeight = defaultItemHeight;
            s.AcceptOversizeOnEmpty = acceptOversizeOnEmpty;
            s.SelectionRule = selectionRule;
            s.PositionLabel = positionLabel ?? "";
            s.KeyLabel = keyLabel ?? "";
            s.ReleasePartialWhenBlocked = releasePartialWhenBlocked;
            s.CycleTime = useCycleTime && cycleTime != null ? cycleTime.ToSettings() : null;
            s.StackType = stackType ?? "Stack";
            s.CopyLabelsFromFirst = copyLabelsFromFirst;
            string pick = pickNode != null ? pickNode.NodeId : null;
            s.PickNode = string.IsNullOrEmpty(pick) ? null : pick;
            s.PositionNodes = s.PickNode == null ? null
                : positionNodes != null ? positionNodes.ConvertAll(n => n != null ? n.NodeId : "") : new List<string>();
        }

        protected override void OnBound(Element built)
        {
            theStacker = (Stacker)built;
            theStacker.ItemPlaced += OnItemPlaced;
            theStacker.StackCompleted += OnStackCompleted;
        }

        public override void ClearViews()
        {
            if (theStacker != null)
            {
                DestroyVisual(theStacker.PickItem);
                DestroyVisual(theStacker.CarriedItem);
                foreach (var p in theStacker.Positions)
                {
                    if (p.Compound != null) DestroyVisual(p.Compound);
                    foreach (Item it in p.Items) DestroyVisual(it);
                }
            }
        }

        private static void DestroyVisual(Item it)
        {
            if (it?.vItem is GameObject go && go != null) Destroy(go);
        }

        // ── layout ───────────────────────────────────────────────────────────

        /// <summary>Local position of stack position <paramref name="index"/> (floor level).</summary>
        public Vector3 PositionLocal(int index)
        {
            if (positionAnchors != null && index < positionAnchors.Count && positionAnchors[index] != null)
                return transform.InverseTransformPoint(positionAnchors[index].position);
            return firstPosition + positionStep * index;
        }

        /// <summary>World rotation of the items on stack position <paramref name="index"/>.</summary>
        public Quaternion PositionRotation(int index)
        {
            if (positionAnchors != null && index < positionAnchors.Count && positionAnchors[index] != null)
                return positionAnchors[index].rotation;
            return transform.rotation * ItemVisuals.AlongFlow;
        }

        /// <summary>World position of the base (bottom centre) of the stack on position <paramref name="index"/>.</summary>
        /// <summary>Creates the crane locations (editor helper): a node at the pick point and one per stack position,
        /// as children of this stacker.</summary>
        public void CreateCraneNodes()
        {
            UnityTransportNode Node(string id, Vector3 at)
            {
                var go = new GameObject(id);
#if UNITY_EDITOR
                UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Create crane nodes");
#endif
                go.transform.SetParent(transform, worldPositionStays: true);
                go.transform.position = at;
                var n = go.AddComponent<UnityTransportNode>();
                n.nodeId = id;
                return n;
            }
            pickNode = Node(name + "_Pick", PickBaseWorld);
            positionNodes = new List<UnityTransportNode>();
            for (int i = 0; i < Mathf.Max(1, positionCount); i++) positionNodes.Add(Node(name + "_Pos" + i, StackBaseWorld(i)));
        }

        public Vector3 StackBaseWorld(int index) => transform.TransformPoint(PositionLocal(index) + Vector3.up * stackBaseHeight);

        /// <summary>World position of the resting base of the item at the pick point.</summary>
        public Vector3 PickBaseWorld => pickPoint != null ? pickPoint.position : transform.TransformPoint(pickOffset);

        public Quaternion PickRotation => pickPoint != null ? pickPoint.rotation : transform.rotation * ItemVisuals.AlongFlow;

        /// <summary>Visual height of an item (element height, else <see cref="itemThickness"/>).</summary>
        public float VisualHeight(Item item)
        {
            double h = theStacker != null ? theStacker.HeightOf(item) : 0;
            return h > 0 ? (float)h : itemThickness;
        }

        /// <summary>Visual height of the items below <paramref name="level"/> on position <paramref name="index"/>.</summary>
        public float VisualHeightBelow(int index, int level)
        {
            if (theStacker == null) return 0f;
            var p = theStacker.Positions[index];
            float h = 0f;
            for (int i = 0; i < level && i < p.Count; i++) h += VisualHeight(p.Items[i]);
            return h;
        }

        /// <summary>Pivot offset of an item above its base.</summary>
        public float PivotOffset(Item item) => itemPivotAtCentre ? VisualHeight(item) * 0.5f : 0f;

        /// <summary>World position of the pivot of <paramref name="item"/> at <paramref name="level"/> on position <paramref name="index"/>.</summary>
        public Vector3 SlotWorldPosition(int index, int level, Item item)
        {
            Vector3 up = transform.up;
            return StackBaseWorld(index) + up * (VisualHeightBelow(index, level) + PivotOffset(item));
        }

        /// <summary>Puts the item's GameObject on its stack slot (no-op if it is not on a stack).</summary>
        public void SnapToSlot(Item item)
        {
            if (theStacker == null || !(item?.vItem is GameObject go) || go == null) return;
            int pos = theStacker.PositionOf(item);
            if (pos < 0) return;
            int level = theStacker.Positions[pos].LevelOf(item);
            go.transform.SetPositionAndRotation(SlotWorldPosition(pos, level, item), PositionRotation(pos));
        }

        // ── engine events (visual only) ──────────────────────────────────────

        private void OnItemPlaced(Stacker s, Item item, StackPosition pos, int level)
        {
            if (Animator == null || !Animator.IsAnimating(item)) SnapToSlot(item);
        }

        private void OnStackCompleted(Stacker s, StackPosition pos)
        {
            Animator?.Flush(pos.Index);
            var go = new GameObject($"{stackType} {++stackGoCount}");
            go.transform.SetPositionAndRotation(StackBaseWorld(pos.Index), PositionRotation(pos.Index));
            foreach (Item it in pos.Items)
            {
                SnapToSlot(it);
                if (it.vItem is GameObject child && child != null) child.transform.SetParent(go.transform, true);
            }
            pos.Compound.vItem = go;
        }

        // ── VElement ─────────────────────────────────────────────────────────

        void IVElement.ReportState(string msg) { }
        object IVElement.GenerateItem(int type) => null;
        void IVElement.LoadItem(Item item)
        {
            if (item?.vItem is GameObject go && go != null)
                go.transform.SetPositionAndRotation(PickBaseWorld + transform.up * PivotOffset(item), PickRotation);
        }
        void IVElement.UnloadItem(Item item) { }

        // ── authoring gizmos ─────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            var m = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.75f, 0.1f, 0.9f);
            for (int i = 0; i < Mathf.Max(1, positionCount); i++)
            {
                Vector3 c = PositionLocal(i) + Vector3.up * stackBaseHeight;
                Gizmos.DrawWireCube(c + Vector3.up * 0.01f, new Vector3(1.2f, 0.02f, 1.2f));
            }
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
            Vector3 pk = pickPoint != null ? transform.InverseTransformPoint(pickPoint.position) : pickOffset;
            Gizmos.DrawWireCube(pk + Vector3.up * 0.01f, new Vector3(0.8f, 0.02f, 0.8f));
            Gizmos.matrix = m;
        }
    }
}
