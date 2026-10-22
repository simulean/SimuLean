using System.Collections.Generic;
using UnityEngine;
using SimuLean.Storage;

namespace SimuLean.Unity
{
    /// <summary>Industry storage element presets built by <see cref="UnityStorageRack"/>.</summary>
    public enum StorageRackStyle
    {
        /// <summary>Selective pallet racking: bays × slots × levels, depth 1, every position directly accessible.</summary>
        ConventionalRack,

        /// <summary>Ground grid of bays × depth cells; each cell is a LIFO stack of up to stackHeight units.</summary>
        FloorStorage,

        /// <summary>Drive-in (DIN) rack: deep LIFO lanes, one SKU per lane.</summary>
        DriveInRack,

        /// <summary>Push-back rack: shallow LIFO lanes (typically 2–5 deep), one SKU per lane.</summary>
        PushBackRack
    }

    /// <summary>SKU-mixing override for generated lanes.</summary>
    public enum RackSkuMix { StyleDefault, SingleSku, Mixed }

    /// <summary>
    /// Parametric storage element (library defaults: conventional racking,
    /// floor storage, drive-in and push-back racks). Configure the counts
    /// (bays / slots per bay / levels / depth) and the physical pitches
    /// (bay width, slot width, level height, slot depth), then run
    /// <see cref="Rebuild"/> (Inspector button, context menu, or the
    /// GameObject → SimuLean → Storage menu) to (re)generate one child anchor
    /// per lane plus its slot visuals. Generated children carry
    /// <see cref="UnityStorageSlotMarker"/>, so a rebuild replaces only its
    /// own output.
    ///
    /// Lanes are written into the inherited <see cref="UnityStorageSystem.lanes"/>
    /// list, so simulation behaviour, serialization (P4 handlers, via the
    /// registered "UnityStorageRack" alias) and headless/GA runs work
    /// unchanged. The item "location view" is this class's
    /// <see cref="GetStoredItemPosition"/> override: rack styles place units
    /// along the lane depth (face outward), floor storage stacks them upward
    /// with the oldest unit on the ground.
    ///
    /// [SelectionBase]: clicking any generated slot in the Scene view selects
    /// this root (click again to drill into the child).
    ///
    /// Sizing rule: never scale this object — set the pitch parameters and
    /// rebuild. An assigned <see cref="slotPrefab"/> is instantiated UNSCALED
    /// at every storage position (author it to match slotWidth × slotDepth);
    /// without a prefab, thin shelf/floor plates are generated.
    /// </summary>
    [SelectionBase]
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Storage, "Storage Rack", Description = "Parametric rack / floor storage built from bays and levels", Order = 1, Icon = "Assets/SimuLean.Unity/Art/Models/ASRS/Rack_Bay.fbx")]
    public class UnityStorageRack : UnityStorageSystem
    {
        [Header("Rack layout")]
        public StorageRackStyle style = StorageRackStyle.ConventionalRack;

        [Tooltip("Number of bays along X.")]
        [Min(1)] public int bays = 3;

        [Tooltip("Pallet/storage slots side-by-side inside one bay (rack styles).")]
        [Min(1)] public int slotsPerBay = 3;

        [Tooltip("Levels along Y (rack styles; ignored for floor storage).")]
        [Min(1)] public int levels = 3;

        [Tooltip("Rack styles: positions per lane along Z (drive-in/push-back depth; keep 1 for conventional). " +
                 "Floor storage: number of ground rows along Z.")]
        [Min(1)] public int depth = 1;

        [Tooltip("Floor storage only: units stackable on one ground cell.")]
        [Min(1)] public int stackHeight = 1;

        [Header("Physical dimensions (m)")]
        [Tooltip("Pitch along X of one bay (frame to frame). Floor storage: pitch of one ground cell.")]
        public float bayWidth = 2.7f;

        [Tooltip("Width of one storage slot inside a bay (rack styles).")]
        public float slotWidth = 0.9f;

        [Tooltip("Pitch along Y of one level (rack styles).")]
        public float levelHeight = 1.5f;

        [Tooltip("Pitch along Z of one storage position.")]
        public float slotDepth = 1.2f;

        [Header("Lane semantics")]
        [Tooltip("StyleDefault: conventional/floor = mixed SKUs, drive-in/push-back = single SKU per lane.")]
        public RackSkuMix skuMix = RackSkuMix.StyleDefault;

        [Header("Slot visuals")]
        [Tooltip("Optional 3D object instantiated UNSCALED at every storage position " +
                 "(author it to match slotWidth × slotDepth). Empty = generated shelf/floor plates.")]
        public GameObject slotPrefab;

        [Tooltip("Thickness of the generated shelf/floor plates.")]
        public float slotVisualThickness = 0.05f;

        [Header("Item location view")]
        [Tooltip("Vertical offset of a stored item above its shelf/floor plate.")]
        public float itemVerticalOffset = 0.25f;

        [Tooltip("Floor storage: vertical pitch between stacked units.")]
        public float stackPitch = 0.5f;

        // ── derived counts ───────────────────────────────────────────────────

        public int LaneCount => style == StorageRackStyle.FloorStorage
            ? bays * depth
            : bays * slotsPerBay * levels;

        public int PositionCount => LaneCount * (style == StorageRackStyle.FloorStorage ? stackHeight : depth);

        /// <summary>One-line layout summary (shown by the custom editor).</summary>
        public string GetLayoutSummary()
        {
            return style == StorageRackStyle.FloorStorage
                ? $"{style}: {bays} bays × {depth} rows = {LaneCount} ground cells, " +
                  $"{stackHeight} high → {PositionCount} positions."
                : $"{style}: {bays} bays × {slotsPerBay} slots × {levels} levels = {LaneCount} lanes, " +
                  $"depth {depth} → {PositionCount} positions.";
        }

        // ── generation ───────────────────────────────────────────────────────

        /// <summary>
        /// Regenerates the child location anchors + visuals and rewrites the
        /// lane list from the current parameters. Safe to run repeatedly —
        /// only marker-tagged children are replaced.
        /// </summary>
        [ContextMenu("Rebuild Storage Locations")]
        public void Rebuild()
        {
            ClearGenerated();
            lanes.Clear();

            AccessDiscipline access;
            SkuConstraint sku;
            ResolveSemantics(out access, out sku);

            if (style == StorageRackStyle.FloorStorage)
                BuildFloor(access, sku);
            else
                BuildRack(access, sku);

            Debug.Log($"[{name}] Rebuilt storage layout — {GetLayoutSummary()}");
        }

        private void ClearGenerated()
        {
            UnityStorageSlotMarker[] markers = GetComponentsInChildren<UnityStorageSlotMarker>(true);
            foreach (UnityStorageSlotMarker marker in markers)
            {
                if (marker == null || marker.gameObject == gameObject)
                    continue;
                if (Application.isPlaying)
                    Destroy(marker.gameObject);
                else
                    DestroyImmediate(marker.gameObject);
            }
        }

        /// <summary>Maps the style (+ SKU override) onto engine lane semantics.</summary>
        private void ResolveSemantics(out AccessDiscipline access, out SkuConstraint sku)
        {
            switch (style)
            {
                case StorageRackStyle.ConventionalRack:
                    if (depth > 1)
                    {
                        Debug.LogWarning($"[{name}] Conventional racking is single-deep; depth {depth} " +
                                         "makes the lanes LIFO (multi-deep behaviour, relocations apply).");
                        access = AccessDiscipline.Lifo;
                    }
                    else
                    {
                        access = AccessDiscipline.Direct;
                    }
                    sku = SkuConstraint.Mixed;
                    break;

                case StorageRackStyle.DriveInRack:
                case StorageRackStyle.PushBackRack:
                    access = AccessDiscipline.Lifo;
                    sku = SkuConstraint.SingleSku;
                    break;

                default: // FloorStorage
                    access = AccessDiscipline.Lifo;
                    sku = SkuConstraint.Mixed;
                    break;
            }

            if (skuMix == RackSkuMix.SingleSku) sku = SkuConstraint.SingleSku;
            else if (skuMix == RackSkuMix.Mixed) sku = SkuConstraint.Mixed;
        }

        private void BuildRack(AccessDiscipline access, SkuConstraint sku)
        {
            for (int b = 0; b < bays; b++)
            {
                for (int s = 0; s < slotsPerBay; s++)
                {
                    for (int l = 0; l < levels; l++)
                    {
                        Transform anchor = CreateAnchor($"B{b}_S{s}_L{l}", b, s, l,
                            new Vector3(b * bayWidth + (s + 0.5f) * slotWidth, l * levelHeight, 0f));

                        for (int k = 0; k < depth; k++)
                            CreateSlotVisual(anchor, new Vector3(0f, 0f, (k + 0.5f) * slotDepth));

                        AddLaneDefinition(anchor, depth, access, sku);
                    }
                }
            }
        }

        private void BuildFloor(AccessDiscipline access, SkuConstraint sku)
        {
            for (int b = 0; b < bays; b++)
            {
                for (int r = 0; r < depth; r++)
                {
                    Transform anchor = CreateAnchor($"B{b}_R{r}", b, r, 0,
                        new Vector3((b + 0.5f) * bayWidth, 0f, (r + 0.5f) * slotDepth));

                    CreateSlotVisual(anchor, Vector3.zero);
                    AddLaneDefinition(anchor, stackHeight, access, sku);
                }
            }
        }

        private Transform CreateAnchor(string anchorName, int bay, int slot, int level, Vector3 localPos)
        {
            var go = new GameObject(anchorName);
            UnityStorageSlotMarker marker = go.AddComponent<UnityStorageSlotMarker>();
            marker.bay = bay;
            marker.slot = slot;
            marker.level = level;

            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            return go.transform;
        }

        private void CreateSlotVisual(Transform anchor, Vector3 localPos)
        {
            GameObject go;
            if (slotPrefab != null)
            {
                go = Instantiate(slotPrefab);   // deliberately UNSCALED
                go.name = "slot";
                go.transform.SetParent(anchor, false);
                go.transform.localPosition = localPos;
                go.transform.localRotation = Quaternion.identity;
            }
            else
            {
                float width = style == StorageRackStyle.FloorStorage ? bayWidth : slotWidth;
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "slot";
                go.transform.SetParent(anchor, false);
                // plate top surface at the anchor's Y
                go.transform.localPosition = localPos + new Vector3(0f, -slotVisualThickness * 0.5f, 0f);
                go.transform.localScale = new Vector3(width * 0.95f, slotVisualThickness, slotDepth * 0.95f);
            }
        }

        private void AddLaneDefinition(Transform anchor, int capacity,
                                       AccessDiscipline access, SkuConstraint sku)
        {
            lanes.Add(new StorageLaneDefinition
            {
                laneId = anchor.name,
                anchor = anchor,
                kind = StorageLaneKind.Custom,
                capacity = capacity,
                customAccess = access,
                customSkuConstraint = sku,
                node = null
            });
        }

        // ── item location view ───────────────────────────────────────────────

        /// <summary>
        /// Rack styles: unit i of a lane sits i positions deep along the lane
        /// (index 0 = face). Floor storage: units stack upward, oldest on the
        /// ground (index 0 = top of the stack).
        /// </summary>
        protected override Vector3 GetStoredItemPosition(StorageLane lane, Transform anchor,
                                                         int depthIndex, int count)
        {
            if (style == StorageRackStyle.FloorStorage)
            {
                int heightIndex = count - 1 - depthIndex;
                return anchor.position + new Vector3(0f, itemVerticalOffset + stackPitch * heightIndex, 0f);
            }

            return anchor.position
                   + anchor.forward * ((depthIndex + 0.5f) * slotDepth)
                   + new Vector3(0f, itemVerticalOffset, 0f);
        }

        // ── gizmo preview of the CONFIGURED grid (before/after rebuild) ─────

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.5f);

            bool floor = style == StorageRackStyle.FloorStorage;
            float sizeX = floor ? bays * bayWidth : (bays - 1) * bayWidth + slotsPerBay * slotWidth;
            float sizeY = floor ? stackHeight * stackPitch : levels * levelHeight;
            float sizeZ = depth * slotDepth;

            // overall bounds
            Vector3 center = transform.position + transform.rotation *
                new Vector3(sizeX * 0.5f, sizeY * 0.5f, sizeZ * 0.5f);
            Gizmos.DrawWireCube(center, new Vector3(sizeX, sizeY, sizeZ));

            if (PositionCount > 2000)
                return;   // keep the editor responsive on huge racks

            if (floor)
            {
                for (int b = 0; b < bays; b++)
                    for (int r = 0; r < depth; r++)
                        DrawCellGizmo(new Vector3((b + 0.5f) * bayWidth, 0f, (r + 0.5f) * slotDepth),
                                      new Vector3(bayWidth * 0.95f, 0.05f, slotDepth * 0.95f));
            }
            else
            {
                for (int b = 0; b < bays; b++)
                    for (int s = 0; s < slotsPerBay; s++)
                        for (int l = 0; l < levels; l++)
                            for (int k = 0; k < depth; k++)
                                DrawCellGizmo(
                                    new Vector3(b * bayWidth + (s + 0.5f) * slotWidth,
                                                l * levelHeight,
                                                (k + 0.5f) * slotDepth),
                                    new Vector3(slotWidth * 0.95f, 0.05f, slotDepth * 0.95f));
            }
        }

        private void DrawCellGizmo(Vector3 localCenter, Vector3 size)
        {
            Gizmos.DrawWireCube(transform.position + transform.rotation * localCenter, size);
        }
    }
}
