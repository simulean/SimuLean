using System;
using System.Collections.Generic;
using UnityEngine;
using SimuLean;
using SimuLean.Collections;
using SimuLean.Storage;

namespace SimuLean.Unity
{
    /// <summary>Industry lane types (StoragePresets) + Custom for free combination.</summary>
    public enum StorageLaneKind { Selective, MultiDeep, DriveIn, PushBack, FloorBlock, FlowRack, Custom }

    /// <summary>Put-away slotting policies selectable in the Inspector.</summary>
    public enum StoragePolicyKind { FirstAvailable, ClosestSpot, Random, DedicatedBySku }

    /// <summary>Relocation policy for blockers; SameAsLocation reuses the put-away policy.</summary>
    public enum RelocationPolicyKind { SameAsLocation, FirstAvailable, ClosestSpot, Random, DedicatedBySku }

    /// <summary>One lane of the storage system, authored in the Inspector.</summary>
    [Serializable]
    public class StorageLaneDefinition
    {
        [Tooltip("Unique lane id. Empty = the anchor's name (or an auto index).")]
        public string laneId = "";

        [Tooltip("Transform giving the lane's position (closest-spot policies, visuals). " +
                 "Empty = this component's transform.")]
        public Transform anchor;

        [Tooltip("Lane type: access discipline and capacity defaults (selective, drive-in, push-back, floor stack…).")]
        public StorageLaneKind kind = StorageLaneKind.Selective;

        [Tooltip("Positions in the lane (depth for LIFO/FIFO lanes).")]
        [Min(1)] public int capacity = 1;

        [Tooltip("Custom kind only: access discipline.")]
        public AccessDiscipline customAccess = AccessDiscipline.Direct;

        [Tooltip("Custom kind only: SKU mixing rule.")]
        public SkuConstraint customSkuConstraint = SkuConstraint.Mixed;

        [Tooltip("Optional transport-network node serving this lane (timed moves). " +
                 "Empty = moves from/to this lane are instantaneous.")]
        public UnityTransportNode node;
    }

    /// <summary>One SKU → lane assignment for the DedicatedBySku policy.</summary>
    [Serializable]
    public class SkuLaneMapping
    {
        [Tooltip("SKU the lane is dedicated to (empty = any).")]
        public string sku;
        [Tooltip("Lane id in the model (empty = generated).")]
        public string laneId;
    }

    /// <summary>
    /// Unity wrapper for the core <see cref="StorageSystem"/> (storage S5).
    ///
    /// Lanes come from the Inspector list; when the list is empty, one lane
    /// per direct child of <see cref="lanesParent"/> (or of this transform) is
    /// generated with the default kind/capacity — child position = lane
    /// coordinate, child name = lane id.
    ///
    /// Put-away arrives through upstream elements' nextElements (standard
    /// link). Retrievals are programmatic: call
    /// <see cref="RequestRetrievalBySku"/> (or use
    /// <see cref="StorageSystem.RequestRetrieval"/> directly); retrieved items
    /// leave through this element's nextElements link.
    /// </summary>
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Storage, "Storage System", Description = "Lanes with location / relocation policies, optional transport", Order = 0, Icon = "Assets/SimuLean.Unity/Art/Models/ASRS/Rack_Bay.fbx")]
    public class UnityStorageSystem : SElement, IVElement
    {
        [Header("Lanes")]
        [Tooltip("Explicit lane definitions. Empty = auto-generate one lane per child Transform.")]
        public List<StorageLaneDefinition> lanes = new List<StorageLaneDefinition>();

        [Tooltip("Parent whose direct children become lanes when the list above is empty. " +
                 "Empty = this transform.")]
        public Transform lanesParent;

        [Tooltip("Lane kind for auto-generated lanes.")]
        public StorageLaneKind defaultKind = StorageLaneKind.Selective;

        [Tooltip("Capacity for auto-generated lanes.")]
        [Min(1)] public int defaultCapacity = 1;

        [Header("Policies")]
        public StoragePolicyKind locationPolicy = StoragePolicyKind.FirstAvailable;

        [Tooltip("Where blocking items are moved when a retrieval needs to dig (same as the location policy by default).")]
        public RelocationPolicyKind relocationPolicy = RelocationPolicyKind.SameAsLocation;

        [Tooltip("SKU → lane assignments consumed by the DedicatedBySku policy.")]
        public List<SkuLaneMapping> dedicatedSkuMappings = new List<SkuLaneMapping>();

        [Tooltip("Reference point for distance-based policies (e.g. the inbound dock). " +
                 "Empty = this transform.")]
        public Transform referencePoint;

        [Header("Visualization")]
        [Tooltip("Vertical spacing of stored item GameObjects above their lane anchor.")]
        public float itemStackSeparation = 0.4f;

        private StorageSystem theSystem;
        private readonly Dictionary<string, Transform> laneAnchors = new Dictionary<string, Transform>();

        /// <summary>A lane after resolving ids/anchors/presets — shared by
        /// InitializeSim and the serialization handler so both see identical lanes.</summary>
        public struct ResolvedLane
        {
            public string LaneId;
            public Vector3 Position;
            public int Capacity;
            public AccessDiscipline Access;
            public SkuConstraint Sku;
            public string NodeId;      // null = no transport binding
            public Transform Anchor;   // may be null (headless extraction keeps Position only)
        }

        // ── lane resolution ──────────────────────────────────────────────────

        /// <summary>Resolves the effective lane set (Inspector list, or one lane
        /// per direct child of lanesParent). Duplicate ids get a numeric suffix.</summary>
        public List<ResolvedLane> ResolveLanes()
        {
            var resolved = new List<ResolvedLane>();
            var usedIds = new HashSet<string>();

            if (lanes != null && lanes.Count > 0)
            {
                for (int i = 0; i < lanes.Count; i++)
                {
                    StorageLaneDefinition def = lanes[i];
                    if (def == null) continue;

                    Transform anchor = def.anchor != null ? def.anchor : transform;
                    string id = !string.IsNullOrEmpty(def.laneId) ? def.laneId
                              : def.anchor != null ? def.anchor.name
                              : $"{name}_lane{i}";

                    AccessDiscipline access;
                    SkuConstraint sku;
                    MapKind(def.kind, def.customAccess, def.customSkuConstraint, out access, out sku);

                    resolved.Add(new ResolvedLane
                    {
                        LaneId = UniqueId(id, usedIds),
                        Position = anchor.position,
                        Capacity = Mathf.Max(1, def.capacity),
                        Access = access,
                        Sku = sku,
                        NodeId = def.node != null ? def.node.NodeId : null,
                        Anchor = anchor
                    });
                }
            }
            else
            {
                Transform parent = lanesParent != null ? lanesParent : transform;
                AccessDiscipline access;
                SkuConstraint sku;
                MapKind(defaultKind, AccessDiscipline.Direct, SkuConstraint.Mixed, out access, out sku);

                foreach (Transform child in parent)
                {
                    resolved.Add(new ResolvedLane
                    {
                        LaneId = UniqueId(child.name, usedIds),
                        Position = child.position,
                        Capacity = Mathf.Max(1, defaultCapacity),
                        Access = access,
                        Sku = sku,
                        NodeId = null,
                        Anchor = child
                    });
                }
            }

            if (resolved.Count == 0)
                Debug.LogWarning($"[{name}] UnityStorageSystem has no lanes (empty list and no child Transforms).");

            return resolved;
        }

        private static void MapKind(StorageLaneKind kind,
                                    AccessDiscipline customAccess, SkuConstraint customSku,
                                    out AccessDiscipline access, out SkuConstraint sku)
        {
            switch (kind)
            {
                case StorageLaneKind.Selective:  access = AccessDiscipline.Direct; sku = SkuConstraint.Mixed;     break;
                case StorageLaneKind.MultiDeep:  access = AccessDiscipline.Lifo;   sku = SkuConstraint.SingleSku; break;
                case StorageLaneKind.DriveIn:    access = AccessDiscipline.Lifo;   sku = SkuConstraint.SingleSku; break;
                case StorageLaneKind.PushBack:   access = AccessDiscipline.Lifo;   sku = SkuConstraint.SingleSku; break;
                case StorageLaneKind.FloorBlock: access = AccessDiscipline.Lifo;   sku = SkuConstraint.Mixed;     break;
                case StorageLaneKind.FlowRack:   access = AccessDiscipline.Fifo;   sku = SkuConstraint.SingleSku; break;
                default:                         access = customAccess;            sku = customSku;               break;
            }
        }

        private string UniqueId(string id, HashSet<string> used)
        {
            string candidate = id;
            int n = 1;
            while (!used.Add(candidate))
            {
                candidate = $"{id}_{n++}";
                Debug.LogWarning($"[{name}] Duplicate lane id '{id}' — renamed to '{candidate}'.");
            }
            return candidate;
        }

        // ── policy building (shared shape with the headless handler) ────────

        /// <summary>"sku=laneId" pairs for the serialization handler.</summary>
        public List<string> GetDedicatedSkuPairs()
        {
            var pairs = new List<string>();
            foreach (SkuLaneMapping m in dedicatedSkuMappings)
                if (m != null && !string.IsNullOrEmpty(m.sku) && !string.IsNullOrEmpty(m.laneId))
                    pairs.Add($"{m.sku}={m.laneId}");
            return pairs;
        }

        /// <summary>Maps the relocation enum onto the shared policy enum;
        /// false for SameAsLocation (no explicit relocation policy).</summary>
        public static bool TryMapRelocation(RelocationPolicyKind kind, out StoragePolicyKind mapped)
        {
            switch (kind)
            {
                case RelocationPolicyKind.FirstAvailable: mapped = StoragePolicyKind.FirstAvailable; return true;
                case RelocationPolicyKind.ClosestSpot:    mapped = StoragePolicyKind.ClosestSpot;    return true;
                case RelocationPolicyKind.Random:         mapped = StoragePolicyKind.Random;         return true;
                case RelocationPolicyKind.DedicatedBySku: mapped = StoragePolicyKind.DedicatedBySku; return true;
                default:                                  mapped = StoragePolicyKind.FirstAvailable; return false;
            }
        }

        // ── lifecycle ────────────────────────────────────────────────────────

        protected override void OnBound(Element built)
        {
            theSystem = (StorageSystem)built;
            laneAnchors.Clear();
            foreach (ResolvedLane lane in ResolveLanes())
                if (lane.Anchor != null) laneAnchors[lane.LaneId] = lane.Anchor;
        }

        public override void ClearViews()
        {
            foreach (StorageLane lane in theSystem.Lanes)
                foreach (StoredItem stored in lane.Contents)
                    if (stored.Item != null && stored.Item.vItem is GameObject go)
                        Destroy(go);
        }

        public StorageSystem GetStorageSystem() => theSystem;

        // ── convenience API ──────────────────────────────────────────────────

        /// <summary>Retrieves the oldest stored unit of a SKU (FIFO). Returns
        /// false when none is stored or a blocker cannot be re-homed.</summary>
        public bool RequestRetrievalBySku(string sku)
            => theSystem != null &&
               theSystem.RequestRetrieval(ListQuery.All.Where("sku", ListOp.Equal, sku), null);

        // ── VElement (stack stored items above their lane anchor) ───────────

        void IVElement.ReportState(string msg) { }
        object IVElement.GenerateItem(int type) => null;
        void IVElement.LoadItem(Item theItem) => RepositionStoredItems();
        void IVElement.UnloadItem(Item theItem) => RepositionStoredItems();

        private void RepositionStoredItems()
        {
            if (theSystem == null)
                return;

            foreach (StorageLane lane in theSystem.Lanes)
            {
                Transform anchor;
                if (!laneAnchors.TryGetValue(lane.LaneId, out anchor) || anchor == null)
                    continue;

                for (int depth = 0; depth < lane.Contents.Count; depth++)
                {
                    if (lane.Contents[depth].Item?.vItem is GameObject go)
                        go.transform.position =
                            GetStoredItemPosition(lane, anchor, depth, lane.Contents.Count);
                }
            }
        }

        /// <summary>
        /// Where the unit at <paramref name="depthIndex"/> (0 = accessible face)
        /// of a lane sits visually. Default: stacked above the lane anchor.
        /// Rack/floor subclasses override this to place items in their exact
        /// storage position (the "location view").
        /// </summary>
        protected virtual Vector3 GetStoredItemPosition(StorageLane lane, Transform anchor,
                                                        int depthIndex, int count)
            => anchor.position + new Vector3(0f, itemStackSeparation * (depthIndex + 1), 0f);

        // ── gizmos: lanes + transport bindings ───────────────────────────────

        private void OnDrawGizmosSelected()
        {
            foreach (ResolvedLane lane in ResolveLanes())
            {
                Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.7f);
                Gizmos.DrawWireCube(lane.Position, new Vector3(0.6f, 0.2f, 0.6f));
            }

            if (location != null)
            {
                Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.8f);
                Gizmos.DrawLine(transform.position, location.transform.position);
            }
        }
    }
}
