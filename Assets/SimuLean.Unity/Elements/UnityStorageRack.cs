using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using SimuLean.Storage;

namespace SimuLean.Unity
{
    // StorageRackStyle, RackSkuMix and RackAccessOverride are in Serialization/Extractors/ExtractionRules.cs (with the
    // semantics they map to, tested headless)

    /// <summary>
    /// Pick module in the bottom zone of a conventional rack: the storage (pallet) levels start above it and
    /// it exposes the anchors of its pick positions (children <c>Pick_B{b}_S{s}_L{l}</c>) for other elements' views.
    /// </summary>
    public enum RackPickModule
    {
        None,
        /// <summary>Sloped carton-flow shelves hung in the same frames (pick face on the aisle side, loaded from the rear).</summary>
        CartonFlow,
        /// <summary>Floor pick positions (one per slot) with optional reserve beams over them.</summary>
        PalletFloor,
    }

    /// <summary>
    /// Parametric storage element (library defaults: pallet racking, floor storage, drive-in, push-back and carton
    /// flow racks). Configure the counts (bays / slots per bay / levels / depth) and the physical pitches (bay width,
    /// slot width, level height, slot depth), then run <see cref="Rebuild"/> (Inspector button, context menu, or the
    /// GameObject → SimuLean → Storage menu) to (re)generate one child anchor per lane plus the rack visuals.
    /// Generated children carry <see cref="UnityStorageSlotMarker"/>, so a rebuild replaces only its own output.
    ///
    /// <para><b>Layout (rack axes: +X along the row, +Y up, +Z into the rack; the access / pick face is z = 0).</b>
    /// Lane (bay b, slot s, level l) is anchored at <c>(b · bayWidth + (s + 0.5) · slotWidth, LevelSurfaceY(l), 0)</c>;
    /// <see cref="LevelSurfaceY"/> is the load surface (beam top, floor or flow-shelf front): <c>l · levelHeight</c>
    /// with the default ground level, else <c>firstLevelHeight + l · levelHeight</c>. Unit k of a lane rests k
    /// positions deep (<c>(k + 0.5) · slotDepth</c>), on a carton flow rack along the shelf slope
    /// (<see cref="CartonFlowRackSpec.AlongSlope(int)"/>). Floor storage: cell (b, r) at
    /// <c>((b + 0.5) · bayWidth, 0, (r + 0.5) · slotDepth)</c>, units stacked upward.</para>
    ///
    /// <para><b>Visuals.</b> Without a <see cref="slotPrefab"/>, the rack is generated procedurally
    /// (<see cref="PalletRackMesh"/>, <see cref="CartonFlowRackMesh"/>, <see cref="FloorMarkingMesh"/>): one child
    /// <c>RackVisual</c> with chunks of <see cref="baysPerChunk"/> bays, each a LODGroup of up to three combined
    /// renderers, sub-meshes per <see cref="StorageRackSlots"/>; default materials are made in code
    /// (<see cref="StorageRackVisuals"/>). An assigned slot prefab is instantiated UNSCALED at every storage position
    /// instead (author it to match slotWidth × slotDepth); <see cref="proceduralVisuals"/> off gives the former thin
    /// plates.</para>
    ///
    /// Lanes are written into the inherited <see cref="UnityStorageSystem.lanes"/> list, so simulation behaviour,
    /// serialization (handlers, via the registered "UnityStorageRack" alias) and headless/GA runs work unchanged.
    /// The item "location view" is <see cref="GetStoredItemPosition"/> / <see cref="PlaceStoredItem"/>: units rest on
    /// their support (beam top, flow shelf, floor), centred on the slot.
    ///
    /// [SelectionBase]: clicking any generated part in the Scene view selects this root.
    /// Sizing rule: never scale this object — set the pitch parameters and rebuild.
    /// </summary>
    [SelectionBase]
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Storage, "Storage Rack", Description = "Parametric pallet / carton flow rack or floor storage built from bays and levels", Order = 0, Icon = "Assets/SimuLean.Unity/Art/Models/ASRS/Rack_Bay.fbx")]
    public class UnityStorageRack : UnityStorageSystem
    {
        [Header("Rack layout")]
        public StorageRackStyle style = StorageRackStyle.ConventionalRack;

        [Tooltip("Number of bays along X.")]
        [Min(1)] public int bays = 3;

        [Tooltip("Storage slots side-by-side inside one bay (carton flow: lanes per bay).")]
        [Min(1)] public int slotsPerBay = 3;

        [Tooltip("Load levels along Y, the ground one included (with Ground Level, level 0 stands on the floor): lanes L0 … " +
                 "L(levels − 1). Rack styles; ignored for floor storage.")]
        [SimuLeanHelp("Load levels counted from the ground one (L0 … L(levels-1)). Lane ids are 0-based B<bay>_S<slot>_L<level> " +
                      "(the first lane B0_S0_L0; floor storage B<bay>_R<row>): set one lane with lanes[B0_S0_L0].node, a group with " +
                      "lanes[B0_*].node. Setting bays, slotsPerBay, levels, depth or any other layout field through simulean_set / " +
                      "simulean_apply rebuilds the lanes at once (the answer's rebuilt lists the ids).")]
        [Min(1)] public int levels = 3;

        [Tooltip("Rack styles: positions per lane along Z (drive-in/push-back depth, carton flow: boxes per lane; keep 1 for conventional). " +
                 "Floor storage: number of ground rows along Z.")]
        [Min(1)] public int depth = 1;

        [Tooltip("Floor storage only: units stackable on one ground cell.")]
        [Min(1)] public int stackHeight = 1;

        [Header("Physical dimensions (m)")]
        [Tooltip("Pitch along X of one bay, frame centre to frame centre (clear width + upright width). Floor storage: pitch of one ground cell.")]
        public float bayWidth = 2.8f;

        [Tooltip("Width of one storage slot / lane inside a bay (rack styles).")]
        public float slotWidth = 0.9f;

        [Tooltip("Pitch along Y of one level (rack styles).")]
        public float levelHeight = 1.5f;

        [Tooltip("Pitch along Z of one storage position (the load length; carton flow: box pitch along the slope).")]
        public float slotDepth = 1.2f;

        [Tooltip("Pallet racks: the lowest level is on the floor (no beam). Off: the lowest beam top is at First Level Height.")]
        public bool groundLevel = true;

        [Tooltip("Load-surface height of the lowest level when it is not on the floor (beam top; carton flow: shelf front height).")]
        [Min(0f)] public float firstLevelHeight = 0.3f;

        [Header("Lane semantics")]
        [Tooltip("StyleDefault: conventional/floor = mixed SKUs, drive-in/push-back/carton flow = single SKU per lane.")]
        public RackSkuMix skuMix = RackSkuMix.StyleDefault;
        [Tooltip("StyleDefault: by style (conventional single-deep = direct, several units per lane = LIFO, carton flow = FIFO, …).")]
        public RackAccessOverride laneAccess = RackAccessOverride.StyleDefault;

        [Header("Rack structure")]
        [Tooltip("Upright frame depth (front of the front upright to the back of the rear one). Loads overhang it by (slotDepth − frameDepth) / 2.")]
        [Min(0.2f)] public float frameDepth = 1.1f;

        [Tooltip("Upright face width (along X, facing the aisle).")]
        [Min(0.04f)] public float uprightWidth = 0.1f;

        [Tooltip("Upright profile depth (along Z).")]
        [Min(0.03f)] public float uprightDepth = 0.07f;

        [Tooltip("Step-beam height (the beam top is the load surface).")]
        [Min(0.04f)] public float beamHeight = 0.12f;

        [Tooltip("Upright height above the top load surface.")]
        [Min(0.05f)] public float uprightExtension = 0.5f;

        [Tooltip("Frame bracing: Z (horizontal at every node, parallel diagonals) or zig-zag diagonals.")]
        public RackBracingPattern bracingPattern = RackBracingPattern.Z;

        [Header("Rack accessories")]
        [Tooltip("Pallet racks: what lies on the beams (nothing, support bars, wire-mesh decks).")]
        public RackDeckType deckType = RackDeckType.None;

        [Tooltip("Back-to-back rows: length of the row spacers behind every frame (0 = single row). Set it on one of the two racks.")]
        [Min(0f)] public float rowSpacerLength = 0f;

        [Tooltip("Yellow / black protectors on the aisle-side uprights.")]
        public bool uprightProtectors = true;

        [Tooltip("Yellow / black guard rails at both ends of the row.")]
        public bool endOfAisleGuards = true;

        [Tooltip("Location labels on the beams / lane label strips, load signs on the end frames.")]
        public bool locationLabels = true;

        [Header("Pick module (conventional racks)")]
        [Tooltip("Bottom zone of the frames used for picking (carton-flow shelves or floor positions); the storage levels start above it.")]
        public RackPickModule pickModule = RackPickModule.None;
        [Tooltip("Carton flow: shelves; pallet floor: the floor position + reserve beams.")]
        [Min(1)] public int pickLevels = 2;
        [Tooltip("Carton flow: lanes per bay (they share the bay's slot band).")]
        [Min(1)] public int pickLanesPerBay = 4;
        [Tooltip("Carton flow: boxes per lane.")]
        [Min(1)] public int pickDepth = 3;
        [Tooltip("Carton flow: box pitch along the shelf.")]
        [Min(0.05f)] public float pickPitch = 0.42f;
        [Tooltip("Carton flow: height of the lowest shelf at the pick face.")]
        [Min(0f)] public float pickFirstShelf = 0.35f;
        [Tooltip("Carton flow: shelf pitch; pallet floor: reserve beam pitch.")]
        [Min(0.1f)] public float pickLevelHeight = 0.7f;
        [Range(0f, 15f)] public float pickSlopeDegrees = 6f;
        [Tooltip("Free height above the top pick shelf / reserve beam before the first storage beam.")]
        [Min(0f)] public float pickClearance = 0.45f;

        [Header("Units in a lane")]
        [Tooltip("Units a lane holds (0 = Depth). More than one on a single-deep conventional rack makes LIFO lanes; with " +
                 "Unit Grid they are drawn as a block on the position (e.g. cases on a pallet position).")]
        [Min(0)] public int unitsPerLane = 0;
        [Tooltip("Rack lanes with several units: units per layer across (X) and deep (Z); 0 = one after the other along the lane.")]
        public Vector2Int unitGrid = Vector2Int.zero;
        [Tooltip("Pitch of the units in the block (across, layer height, deep).")]
        public Vector3 unitPitch = new Vector3(0.6f, 0.21f, 0.42f);
        [Tooltip("Rotation of the stored units about the vertical (degrees), e.g. 90 to put a load's long side along the row.")]
        public float itemYaw = 0f;

        [Header("Carton flow rack")]
        [Tooltip("Shelf inclination, falling from the rear (loading) side to the pick face (typically 4–8°).")]
        [Range(0f, 15f)] public float flowSlopeDegrees = 6f;

        [Tooltip("A flat wire-mesh deck on top for overstock.")]
        public bool overstockDeck = true;

        [Header("Generated visuals")]
        [Tooltip("Generate the procedural rack (frames, beams, decks…). Off: thin shelf / floor plates (the former look).")]
        public bool proceduralVisuals = true;

        [Tooltip("Bays per combined chunk (one renderer per LOD per chunk).")]
        [Min(1)] public int baysPerChunk = 8;

        [Tooltip("Three levels of detail under a LODGroup per chunk; off = full detail only.")]
        public bool generateLods = true;

        [Tooltip("Optional material overrides (empty = the code-generated defaults).")]
        public Material frameMaterial, beamMaterial, steelMaterial, safetyMaterial, labelMaterial, deckMaterial, trackMaterial, floorMarkingMaterial;

        [Header("Slot visuals")]
        [Tooltip("Optional 3D object instantiated UNSCALED at every storage position " +
                 "(author it to match slotWidth × slotDepth). Overrides the procedural visuals.")]
        public GameObject slotPrefab;

        [Tooltip("Thickness of the plates generated when the procedural visuals are off.")]
        public float slotVisualThickness = 0.05f;

        [Header("Item location view")]
        [Tooltip("Rest each stored unit's lowest point on its support (beam top, shelf, floor), measured from its renderers. " +
                 "Off, or a view without renderers: its pivot is Item Vertical Offset above the support.")]
        public bool restItemsOnSurface = true;

        [Tooltip("Height of a stored item's pivot above its support (beam top / shelf / floor).")]
        public float itemVerticalOffset = 0.25f;

        [Tooltip("Floor storage: vertical pitch between stacked units.")]
        public float stackPitch = 0.5f;

        [Tooltip("Floor storage: inset of each painted cell outline from the cell pitch (e.g. 0.05 for 1.0 × 0.8 cells on a 1.1 × 0.9 pitch).")]
        [Min(0f)] public float markingInset = 0.04f;

        private const string VisualRootName = "RackVisual";
        private readonly Dictionary<GameObject, float> pivotHeights = new Dictionary<GameObject, float>();

        // ── derived counts ───────────────────────────────────────────────────

        private bool IsFloor => style == StorageRackStyle.FloorStorage;
        private bool IsCarton => style == StorageRackStyle.CartonFlowRack;
        /// <summary>A conventional rack with a pick module in its bottom zone.</summary>
        public bool HasPickModule => style == StorageRackStyle.ConventionalRack && pickModule != RackPickModule.None;
        private bool UnitBlock => !IsFloor && !IsCarton && unitGrid.x > 0 && unitGrid.y > 0;

        /// <summary>Units per lane (rack styles): <see cref="unitsPerLane"/>, else <see cref="depth"/>.</summary>
        public int LaneCapacity => IsFloor ? stackHeight : (unitsPerLane > 0 ? unitsPerLane : depth);

        public int LaneCount => IsFloor ? bays * depth : bays * slotsPerBay * levels;

        public int PositionCount => LaneCount * LaneCapacity;

        // ── pick module ──────────────────────────────────────────────────────

        /// <summary>Lanes of the pick module per bay (carton flow lanes / floor slots).</summary>
        public int PickLanesPerBay => pickModule == RackPickModule.CartonFlow ? pickLanesPerBay : slotsPerBay;

        /// <summary>Width of a carton-flow pick lane: the bay's slot band shared by the lanes (frames stay where they are).</summary>
        public float PickLaneWidth => slotsPerBay * slotWidth / Mathf.Max(1, pickLanesPerBay);

        /// <summary>Top of the pick zone (where the first storage beam may be at the earliest).</summary>
        public float PickZoneTop
        {
            get
            {
                if (!HasPickModule) return 0f;
                if (pickModule == RackPickModule.PalletFloor)
                    return pickLevels * pickLevelHeight + beamHeight;
                var c = ToPickCartonSpec();
                return c.SurfaceY(Mathf.Max(0, pickLevels - 1), c.HorizontalDepth) + pickClearance + beamHeight;
            }
        }

        /// <summary>Load surface of the first storage level: Ground Level / First Level Height, raised above a pick module.</summary>
        public float EffectiveFirstLevelHeight => HasPickModule ? Mathf.Max(firstLevelHeight, PickZoneTop) : firstLevelHeight;
        private bool EffectiveGroundLevel => groundLevel && !HasPickModule;

        /// <summary>Spec of the pick module's carton-flow shelves (no frames; the lane band of the bay), or null.</summary>
        public CartonFlowRackSpec ToPickCartonSpec()
        {
            if (!HasPickModule || pickModule != RackPickModule.CartonFlow) return null;
            float lane = Mathf.Max(1, depth) * slotDepth;
            float slope = CartonFlowRackSpec.SlopeRadFor(pickSlopeDegrees);
            float front = CartonFlowRackSpec.DefaultFrontStop;
            float rear = Mathf.Max(0.02f, lane / Mathf.Cos(slope) - front - pickDepth * pickPitch);
            return new CartonFlowRackSpec
            {
                bays = bays, lanesPerBay = pickLanesPerBay, levels = pickLevels, depth = pickDepth,
                bayWidth = bayWidth, laneWidth = PickLaneWidth, levelHeight = pickLevelHeight, boxPitch = pickPitch,
                firstLevelHeight = pickFirstShelf, slopeDegrees = pickSlopeDegrees, frontStop = front, rearMargin = rear,
                uprightWidth = uprightWidth, uprightDepth = uprightDepth, bracing = bracingPattern,
                labels = locationLabels, overstockDeck = false, frames = false,
            };
        }

        /// <summary>Anchor of pick position (bay, lane, level) in rack axes: carton flow = the lane's shelf at the pick face
        /// (rotated with the shelf: local +Z up the slope); pallet floor = the slot centre at the face, on the floor (level 0)
        /// or on reserve beam <c>level</c>.</summary>
        public Vector3 PickLocalPosition(int bay, int lane, int level)
        {
            if (pickModule == RackPickModule.CartonFlow)
                return new Vector3(bay * bayWidth + (lane + 0.5f) * PickLaneWidth, pickFirstShelf + level * pickLevelHeight, 0f);
            return new Vector3(bay * bayWidth + (lane + 0.5f) * slotWidth, level * pickLevelHeight, 0f);
        }

        public Quaternion PickLocalRotation => pickModule == RackPickModule.CartonFlow ? CartonFlowRackSpec.RotationFor(pickSlopeDegrees) : Quaternion.identity;

        /// <summary>Offset, in the pick anchor's own axes, of unit <paramref name="k"/> of a pick lane (0 = at the face).</summary>
        public Vector3 PickUnitOffset(int k)
            => pickModule == RackPickModule.CartonFlow
                ? Vector3.forward * CartonFlowRackSpec.AlongSlope(CartonFlowRackSpec.DefaultFrontStop, pickPitch, k)
                : new Vector3(0f, 0f, Mathf.Max(1, depth) * slotDepth * 0.5f);

        /// <summary>The generated anchor of pick position (bay, lane, level), or null.</summary>
        public Transform PickAnchor(int bay, int lane, int level) => transform.Find($"Pick_B{bay}_S{lane}_L{level}");

        /// <summary>One-line layout summary (shown by the custom editor).</summary>
        public string GetLayoutSummary()
        {
            if (IsFloor)
                return $"{style}: {bays} bays × {depth} rows = {LaneCount} ground cells, {stackHeight} high → {PositionCount} positions.";
            if (IsCarton)
                return $"{style}: {bays} bays × {slotsPerBay} lanes × {levels} levels = {LaneCount} FIFO lanes, " +
                       $"{depth} boxes each → {PositionCount} positions; shelves {ToCartonSpec().ShelfLength:0.00} m at {flowSlopeDegrees:0.#}°.";
            return $"{style}: {bays} bays × {slotsPerBay} slots × {levels} levels = {LaneCount} lanes, depth {depth} → {PositionCount} positions.";
        }

        /// <summary>Triangles of the generated visuals per LOD (from the scene objects), for the editor.</summary>
        public string GetVisualSummary()
        {
            Transform root = transform.Find(VisualRootName);
            if (root == null) return slotPrefab != null ? "Visuals: slot prefab per position." : "Visuals: not generated (rebuild).";
            int[] t = StorageRackVisuals.TriangleCounts(root);
            int chunks = root.childCount;
            return t[1] > 0
                ? $"Visuals: {chunks} chunk(s), triangles LOD0 {t[0]:N0} / LOD1 {t[1]:N0} / LOD2 {t[2]:N0}."
                : $"Visuals: {chunks} chunk(s), {t[0]:N0} triangles.";
        }

        // ── layout ───────────────────────────────────────────────────────────

        /// <summary>Load-surface height of a level (beam top, floor or carton-flow shelf front): the anchor Y of its lanes.</summary>
        public float LevelSurfaceY(int level)
            => IsCarton ? firstLevelHeight + level * levelHeight
                        : PalletRackSpec.LevelSurfaceY(EffectiveGroundLevel, EffectiveFirstLevelHeight, levelHeight, level);

        /// <summary>Anchor of lane (bay, slot, level) in rack axes; floor storage: (bay, row) cell centre (level ignored).</summary>
        public Vector3 LocalAnchorPosition(int bay, int slot, int level)
            => IsFloor ? new Vector3((bay + 0.5f) * bayWidth, 0f, (slot + 0.5f) * slotDepth)
                       : new Vector3(bay * bayWidth + (slot + 0.5f) * slotWidth, LevelSurfaceY(level), 0f);

        /// <summary>Offset (rack axes) from a lane anchor to the support point under unit <paramref name="depthIndex"/> (0 = the face).</summary>
        public Vector3 LocalSurfaceOffset(int depthIndex)
        {
            if (IsFloor) return Vector3.zero;
            if (IsCarton)
                return CartonFlowRackSpec.AlongFor(flowSlopeDegrees)
                       * CartonFlowRackSpec.AlongSlope(CartonFlowRackSpec.DefaultFrontStop, slotDepth, depthIndex);
            if (UnitBlock) return new Vector3(0f, 0f, slotDepth * 0.5f);
            return new Vector3(0f, 0f, (depthIndex + 0.5f) * slotDepth);
        }

        /// <summary>Unit grid: offset (rack axes) of the unit at height index <paramref name="h"/> (0 = first stored, bottom).</summary>
        private Vector3 BlockOffset(int h)
        {
            int cols = Mathf.Max(1, unitGrid.x), rows = Mathf.Max(1, unitGrid.y), per = cols * rows;
            int layer = h / per, k = h % per, c = k % cols, r = k / cols;
            float zSpan = Mathf.Max(1, depth) * slotDepth;
            return new Vector3((c - (cols - 1) * 0.5f) * unitPitch.x, layer * unitPitch.y,
                               zSpan * 0.5f + (r - (rows - 1) * 0.5f) * unitPitch.z);
        }

        /// <summary>Support normal in rack axes (the carton-flow shelf normal, else up).</summary>
        public Vector3 LocalSurfaceNormal => IsCarton ? CartonFlowRackSpec.NormalFor(flowSlopeDegrees) : Vector3.up;

        /// <summary>Rotation of a stored unit relative to the rack (tilted with a carton-flow shelf).</summary>
        public Quaternion LocalItemRotation => IsCarton ? CartonFlowRackSpec.RotationFor(flowSlopeDegrees) : Quaternion.identity;

        /// <summary>World point of the support (beam top / shelf / floor) under unit <paramref name="depthIndex"/> of lane (bay, slot, level).</summary>
        public Vector3 GetLoadSurfacePoint(int bay, int slot, int level, int depthIndex)
            => transform.TransformPoint(LocalAnchorPosition(bay, slot, level)) + transform.rotation * LocalSurfaceOffset(depthIndex);

        /// <summary>
        /// Puts an item view at unit <paramref name="depthIndex"/> of lane (bay, slot, level) the way stored items are
        /// placed (resting on the support; on a flow rack tilted with the shelf). Floor storage: <paramref name="depthIndex"/>
        /// is the height index in the stack (0 = on the ground).
        /// </summary>
        public void PlaceItemView(GameObject view, int bay, int slot, int level, int depthIndex)
        {
            Vector3 anchor = transform.TransformPoint(LocalAnchorPosition(bay, slot, level));
            PlaceView(view, anchor, transform.rotation, IsFloor ? 0 : depthIndex, IsFloor ? depthIndex : 0);
        }

        /// <summary>Spec of the pallet-rack generator for the current parameters (conventional, drive-in, push-back).</summary>
        public PalletRackSpec ToPalletSpec()
        {
            bool rails = style == StorageRackStyle.DriveInRack || style == StorageRackStyle.PushBackRack;
            return new PalletRackSpec
            {
                bays = bays, slotsPerBay = slotsPerBay, levels = levels, depth = depth,
                bayWidth = bayWidth, slotWidth = slotWidth, levelHeight = levelHeight, slotDepth = slotDepth,
                groundLevel = EffectiveGroundLevel, firstLevelHeight = EffectiveFirstLevelHeight,
                extraBeamLevels = HasPickModule && pickModule == RackPickModule.PalletFloor ? PickBeamLevels() : null,
                cartonModule = ToPickCartonSpec(),
                frameDepth = frameDepth, uprightWidth = uprightWidth, uprightDepth = uprightDepth,
                uprightExtension = uprightExtension, beamHeight = beamHeight,
                support = rails ? RackSupportType.Rails : RackSupportType.Beams,
                deck = rails ? RackDeckType.None : deckType,
                bracing = bracingPattern, rowSpacerLength = rowSpacerLength,
                uprightProtectors = uprightProtectors, endGuards = endOfAisleGuards, labels = locationLabels && !rails,
            };
        }

        /// <summary>Spec of the carton-flow generator for the current parameters.</summary>
        public CartonFlowRackSpec ToCartonSpec() => new CartonFlowRackSpec
        {
            bays = bays, lanesPerBay = slotsPerBay, levels = levels, depth = depth,
            bayWidth = bayWidth, laneWidth = slotWidth, levelHeight = levelHeight, boxPitch = slotDepth,
            firstLevelHeight = firstLevelHeight, slopeDegrees = flowSlopeDegrees,
            uprightWidth = Mathf.Min(uprightWidth, 0.1f), uprightDepth = Mathf.Min(uprightDepth, 0.08f),
            uprightExtension = Mathf.Min(uprightExtension, 0.3f), bracing = bracingPattern,
            labels = locationLabels, overstockDeck = overstockDeck,
        };

        // ── generation ───────────────────────────────────────────────────────

        /// <summary>
        /// Regenerates the child location anchors + visuals and rewrites the
        /// lane list from the current parameters. Safe to run repeatedly —
        /// only marker-tagged children are replaced.
        /// </summary>
        [ContextMenu("Rebuild Storage Locations")]
        public void Rebuild()
        {
            // a lane's own node / slot size, set by hand, stays with the lane of that id (the
            // commands rebuild after every layout edit)
            var kept = new Dictionary<string, StorageLaneDefinition>();
            foreach (var l in lanes)
                if (l != null && !string.IsNullOrEmpty(l.laneId) && (l.node != null || l.slotSize != Vector3.zero)) kept[l.laneId] = l;
            ClearGenerated();
            lanes.Clear();
            pivotHeights.Clear();

            AccessDiscipline access;
            SkuConstraint sku;
            ResolveSemantics(out access, out sku);

            if (IsFloor)
                BuildFloor(access, sku);
            else
                BuildRack(access, sku);
            if (HasPickModule) BuildPickAnchors();
            foreach (var l in lanes)
                if (kept.TryGetValue(l.laneId, out var old)) { l.node = old.node; l.slotSize = old.slotSize; }

            BuildVisuals();
            Debug.Log($"[{name}] Rebuilt storage layout — {GetLayoutSummary()}");
        }

        /// <summary>
        /// Null when the lanes are those of the layout (bays × slots × levels, or floor cells); else what differs — a layout
        /// field changed without <see cref="Rebuild"/> (in the inspector; the commands rebuild at once). The extractor warns
        /// with it (lanes stale after a geometry edit, nothing said).
        /// </summary>
        public string LayoutMismatch()
        {
            var expected = new HashSet<string>();
            if (IsFloor) { for (int b = 0; b < bays; b++) for (int r = 0; r < depth; r++) expected.Add($"B{b}_R{r}"); }
            else for (int b = 0; b < bays; b++) for (int s = 0; s < slotsPerBay; s++) for (int l = 0; l < levels; l++) expected.Add($"B{b}_S{s}_L{l}");
            var actual = new HashSet<string>(lanes.Where(l => l != null).Select(l => l.laneId));
            if (actual.SetEquals(expected)) return null;
            return $"its {actual.Count} lane(s) are not those of its layout ({GetLayoutSummary()})";
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

        /// <summary>Maps the style (+ SKU / access overrides) onto engine lane semantics
        /// (<see cref="ExtractionRules.RackSemantics"/>).</summary>
        private void ResolveSemantics(out AccessDiscipline access, out SkuConstraint sku)
        {
            ExtractionRules.RackSemantics(style, LaneCapacity, laneAccess, skuMix, out access, out sku, out string warning);
            if (warning != null) Debug.LogWarning($"[{name}] {warning}");
        }

        private void BuildRack(AccessDiscipline access, SkuConstraint sku)
        {
            for (int b = 0; b < bays; b++)
                for (int s = 0; s < slotsPerBay; s++)
                    for (int l = 0; l < levels; l++)
                    {
                        Transform anchor = CreateAnchor($"B{b}_S{s}_L{l}", b, s, l, LocalAnchorPosition(b, s, l));
                        AddLaneDefinition(anchor, LaneCapacity, access, sku);
                    }
        }

        private float[] PickBeamLevels()
        {
            var y = new float[Mathf.Max(0, pickLevels - 1)];
            for (int i = 0; i < y.Length; i++) y[i] = (i + 1) * pickLevelHeight;
            return y;
        }

        // Pick positions: anchors only (no lanes) — other elements' views stand there (e.g. pick faces).
        private void BuildPickAnchors()
        {
            for (int b = 0; b < bays; b++)
                for (int s = 0; s < PickLanesPerBay; s++)
                    for (int l = 0; l < pickLevels; l++)
                    {
                        Transform a = CreateAnchor($"Pick_B{b}_S{s}_L{l}", b, s, -1 - l, PickLocalPosition(b, s, l));
                        a.localRotation = PickLocalRotation;
                    }
        }

        private void BuildFloor(AccessDiscipline access, SkuConstraint sku)
        {
            for (int b = 0; b < bays; b++)
                for (int r = 0; r < depth; r++)
                {
                    Transform anchor = CreateAnchor($"B{b}_R{r}", b, r, 0, LocalAnchorPosition(b, r, 0));
                    AddLaneDefinition(anchor, stackHeight, access, sku);
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

        private void AddLaneDefinition(Transform anchor, int capacity, AccessDiscipline access, SkuConstraint sku)
        {
            bool flow = access == AccessDiscipline.Fifo && sku == SkuConstraint.SingleSku;
            lanes.Add(new StorageLaneDefinition
            {
                laneId = anchor.name,
                anchor = anchor,
                kind = flow ? StorageLaneKind.FlowRack : StorageLaneKind.Custom,
                capacity = capacity,
                customAccess = access,
                customSkuConstraint = sku,
                node = null
            });
        }

        /// <summary>
        /// Clear room of one position of a lane on <paramref name="level"/> (across, height, along the lane; 0 = no limit),
        /// what a storage with physical dimensions checks a load against: the slot width (floor: the cell) by the pitch along
        /// the lane, under the next level's beam (its surface minus the beam; carton flow: the shelf pitch); the top level
        /// and floor stacks are open above. Cases on a position (Unit Grid): the pitch of one case of the block.
        /// </summary>
        public Vector3 SlotRoom(int level)
        {
            if (IsFloor) return new Vector3(bayWidth, 0f, slotDepth);
            if (UnitBlock) return unitPitch;
            float height = level < levels - 1
                ? IsCarton ? levelHeight - CartonShelfAllowance : LevelSurfaceY(level + 1) - LevelSurfaceY(level) - beamHeight
                : 0f;
            return new Vector3(slotWidth, Mathf.Max(0f, height), slotDepth);
        }

        // Room a carton-flow shelf (rollers, its frame) takes from the level pitch.
        private const float CartonShelfAllowance = 0.06f;

        /// <summary>The room of a generated lane follows the layout (its anchor's level): nothing to rebuild when the
        /// pitches change.</summary>
        protected override Vector3 SlotSizeOf(StorageLaneDefinition def, Transform anchor)
        {
            var marker = anchor != null ? anchor.GetComponent<UnityStorageSlotMarker>() : null;
            return marker != null && marker.level >= 0 ? SlotRoom(IsFloor ? 0 : marker.level) : def.slotSize;
        }

        // ── visuals ──────────────────────────────────────────────────────────

        private void BuildVisuals()
        {
            if (slotPrefab != null || !proceduralVisuals)
            {
                BuildSlotObjects();
                return;
            }

            var root = new GameObject(VisualRootName);
            UnityStorageSlotMarker marker = root.AddComponent<UnityStorageSlotMarker>();
            marker.bay = marker.slot = marker.level = -1;
            root.transform.SetParent(transform, false);
            root.isStatic = gameObject.isStatic;

            if (IsFloor)
            {
                Material paint = floorMarkingMaterial != null ? floorMarkingMaterial : StorageRackVisuals.DefaultFloorPaint();
                Mesh mesh = FloorMarkingMesh.Build(bays, depth, bayWidth, slotDepth, inset: markingInset, name: "FloorMarking");
                StorageRackVisuals.CreateLodChunk(root.transform, "Markings", new[] { mesh }, new[] { paint }, root.isStatic, 0f);
                return;
            }

            Material[] mats = ResolveMaterials();
            int lodCount = generateLods ? 3 : 1;
            int chunk = Mathf.Max(1, baysPerChunk);
            PalletRackSpec pallet = IsCarton ? null : ToPalletSpec();
            CartonFlowRackSpec carton = IsCarton ? ToCartonSpec() : null;
            string kind = IsCarton ? "CartonFlowRack" : "PalletRack";
            for (int b0 = 0; b0 < bays; b0 += chunk)
            {
                int b1 = Mathf.Min(bays, b0 + chunk);
                var meshes = new Mesh[lodCount];
                for (int lod = 0; lod < lodCount; lod++)
                {
                    string meshName = $"{kind}_LOD{lod}_B{b0}-{b1 - 1}";
                    meshes[lod] = IsCarton ? CartonFlowRackMesh.Build(carton, lod, b0, b1, null, meshName)
                                           : PalletRackMesh.Build(pallet, lod, b0, b1, null, meshName);
                }
                StorageRackVisuals.CreateLodChunk(root.transform, $"Bays_{b0}-{b1 - 1}", meshes, mats, root.isStatic);
            }
        }

        private Material[] ResolveMaterials()
        {
            var mats = (Material[])StorageRackVisuals.DefaultMaterials().Clone();
            void Use(int slot, Material m) { if (m != null) mats[slot] = m; }
            Use(StorageRackSlots.Frame, frameMaterial);
            Use(StorageRackSlots.Beam, beamMaterial);
            Use(StorageRackSlots.Steel, steelMaterial);
            Use(StorageRackSlots.Safety, safetyMaterial);
            Use(StorageRackSlots.Label, labelMaterial);
            Use(StorageRackSlots.Deck, deckMaterial);
            Use(StorageRackSlots.Track, trackMaterial);
            return mats;
        }

        /// <summary>Slot prefab (or the former thin plate) at every storage position, under its lane anchor.</summary>
        private void BuildSlotObjects()
        {
            foreach (StorageLaneDefinition lane in lanes)
            {
                if (lane.anchor == null) continue;
                int positions = IsFloor ? 1 : depth;
                for (int k = 0; k < positions; k++)
                    CreateSlotVisual(lane.anchor, LocalSurfaceOffset(k), LocalItemRotation);
            }
        }

        private void CreateSlotVisual(Transform anchor, Vector3 localPos, Quaternion localRot)
        {
            GameObject go;
            if (slotPrefab != null)
            {
                go = Instantiate(slotPrefab);   // deliberately UNSCALED
                go.name = "slot";
                go.transform.SetParent(anchor, false);
                go.transform.localPosition = localPos;
                go.transform.localRotation = localRot;
            }
            else
            {
                float width = IsFloor ? bayWidth : slotWidth;
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "slot";
                go.transform.SetParent(anchor, false);
                go.transform.localRotation = localRot;
                // plate top surface at the support
                go.transform.localPosition = localPos + localRot * new Vector3(0f, -slotVisualThickness * 0.5f, 0f);
                go.transform.localScale = new Vector3(width * 0.95f, slotVisualThickness, slotDepth * 0.95f);
            }
        }

        // ── item location view ───────────────────────────────────────────────

        /// <summary>
        /// Pivot of unit <paramref name="depthIndex"/> with <see cref="itemVerticalOffset"/>: rack styles i positions
        /// deep along the lane (index 0 = face), centred on the slot, above the beam top; carton flow along the shelf
        /// slope; floor storage stacked upward, oldest on the ground (index 0 = top of the stack).
        /// </summary>
        protected override Vector3 GetStoredItemPosition(StorageLane lane, Transform anchor, int depthIndex, int count)
        {
            if (IsFloor)
            {
                int heightIndex = count - 1 - depthIndex;
                return anchor.position + anchor.up * (itemVerticalOffset + stackPitch * heightIndex);
            }
            if (UnitBlock) return anchor.position + anchor.rotation * (BlockOffset(count - 1 - depthIndex) + Vector3.up * itemVerticalOffset);
            return anchor.position + anchor.rotation * (LocalSurfaceOffset(depthIndex) + LocalSurfaceNormal * itemVerticalOffset);
        }

        /// <summary>Rests the view on its support (see <see cref="restItemsOnSurface"/>); carton flow: tilted with the shelf.</summary>
        protected override void PlaceStoredItem(GameObject view, StorageLane lane, Transform anchor, int depthIndex, int count)
        {
            if (IsFloor) PlaceView(view, anchor.position, anchor.rotation, 0, count - 1 - depthIndex);
            else if (UnitBlock) PlaceView(view, anchor.position, anchor.rotation, 0, 0, BlockOffset(count - 1 - depthIndex));
            else PlaceView(view, anchor.position, anchor.rotation, depthIndex, 0);
        }

        private void PlaceView(GameObject view, Vector3 anchorPos, Quaternion rackRot, int depthIndex, int heightIndex, Vector3? block = null)
        {
            Transform t = view.transform;
            // every unit is aligned with the rack (not keeping the rotation it arrived with), turned by Item Yaw
            Quaternion yaw = Quaternion.Euler(0f, itemYaw, 0f);
            t.rotation = rackRot * yaw;                              // measured upright, then tilted with a flow shelf
            float lift = restItemsOnSurface ? PivotHeight(view) : itemVerticalOffset;
            if (IsCarton) t.rotation = rackRot * LocalItemRotation * yaw;
            Vector3 support = block.HasValue
                ? anchorPos + rackRot * block.Value
                : anchorPos + rackRot * LocalSurfaceOffset(depthIndex) + rackRot * Vector3.up * (stackPitch * heightIndex);
            t.position = support + rackRot * LocalSurfaceNormal * lift;
        }

        /// <summary>Height of the view's pivot above its lowest rendered point (cached per view); no renderer = the offset.</summary>
        private float PivotHeight(GameObject view)
        {
            if (pivotHeights.TryGetValue(view, out float h)) return h;
            Renderer[] rs = view.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0)
                h = itemVerticalOffset;
            else
            {
                Bounds b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                h = Mathf.Max(0f, view.transform.position.y - b.min.y);
            }
            if (pivotHeights.Count > 4096) pivotHeights.Clear();
            pivotHeights[view] = h;
            return h;
        }

        // ── gizmo preview of the CONFIGURED grid (before/after rebuild) ─────

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.5f);

            float sizeX = IsFloor ? bays * bayWidth : (bays - 1) * bayWidth + slotsPerBay * slotWidth;
            float sizeY = IsFloor ? stackHeight * stackPitch : LevelSurfaceY(levels - 1) + levelHeight;
            float sizeZ = IsCarton ? ToCartonSpec().HorizontalDepth : depth * slotDepth;

            Vector3 center = transform.position + transform.rotation * new Vector3(sizeX * 0.5f, sizeY * 0.5f, sizeZ * 0.5f);
            Gizmos.matrix = Matrix4x4.TRS(center, transform.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(sizeX, sizeY, sizeZ));
            Gizmos.matrix = Matrix4x4.identity;

            if (PositionCount > 2000)
                return;   // keep the editor responsive on huge racks

            if (IsFloor)
            {
                for (int b = 0; b < bays; b++)
                    for (int r = 0; r < depth; r++)
                        DrawCellGizmo(LocalAnchorPosition(b, r, 0), Quaternion.identity, new Vector3(bayWidth * 0.95f, 0.05f, slotDepth * 0.95f));
            }
            else
            {
                Quaternion rot = LocalItemRotation;
                for (int b = 0; b < bays; b++)
                    for (int s = 0; s < slotsPerBay; s++)
                        for (int l = 0; l < levels; l++)
                            for (int k = 0; k < depth; k++)
                                DrawCellGizmo(LocalAnchorPosition(b, s, l) + LocalSurfaceOffset(k), rot,
                                              new Vector3(slotWidth * 0.95f, 0.05f, slotDepth * 0.95f));
            }
        }

        private void DrawCellGizmo(Vector3 localCenter, Quaternion localRot, Vector3 size)
        {
            Gizmos.matrix = Matrix4x4.TRS(transform.position + transform.rotation * localCenter, transform.rotation * localRot, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, size);
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
