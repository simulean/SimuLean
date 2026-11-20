using System.Collections.Generic;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Simple primitive visuals of unit loads and pieces, for loads created without a prefab: a load is an
    /// empty parent with a scaled box child (so pieces parented to it are not scaled), a piece a small box. Colliders are
    /// removed; colours through a property block (shared material). Read-only views.
    /// </summary>
    public static class UnitLoadVisuals
    {
        private static MaterialPropertyBlock block;

        /// <summary>A box of <paramref name="size"/> whose pivot is at the bottom centre.</summary>
        public static GameObject Box(string name, Vector3 size, Color color, Transform parent = null)
        {
            var root = new GameObject(name);
            if (parent != null) root.transform.SetParent(parent, false);
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "Body";
            var col = box.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            box.transform.SetParent(root.transform, false);
            box.transform.localScale = size;
            box.transform.localPosition = new Vector3(0f, size.y * 0.5f, 0f);
            Tint(box, color);
            return root;
        }

        public static void Tint(GameObject go, Color c)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            if (block == null) block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            block.SetColor("_BaseColor", c);
            block.SetColor("_Color", c);
            block.SetColor("_BaseColor", c);
            r.SetPropertyBlock(block);
        }

        /// <summary>A stable colour per reference (hue from the SKU text).</summary>
        public static Color ColorOf(string sku)
        {
            int h = 17;
            foreach (char ch in sku ?? "") h = h * 31 + ch;
            return Color.HSVToRGB((h & 0x7fffffff) % 360 / 360f, 0.55f, 0.85f);
        }
    }

    /// <summary>
    /// Unity wrapper of <see cref="PickFace"/> (picking location / supermarket face of one SKU): holds unit loads (open +
    /// reserve), hands out single pieces through its output (a connection or a list — stations pull <c>sku == X</c>),
    /// sends the emptied loads to <see cref="emptiesDestination"/> and orders loads from its input (kanban: e.g. Receive
    /// From = List, the published stock of a storage, query <c>sku == X</c>, so the forklift brings them). Built by
    /// <c>PickFaceHandler</c>.
    /// <para><b>View</b> (read-only): the loads stand at slots along the local <see cref="slotStep"/> (open first); the
    /// open load's pieces are small boxes (or <see cref="piecePrefab"/>) laid out in a grid inside the load's visual bounds, stacked, the next piece to
    /// go on top, so the stack visibly goes down as pieces are taken; each piece leaves with its box (the next element
    /// places it). Loads without a GameObject (the initial fill) get a box of <see cref="loadSize"/>.</para>
    /// </summary>
    [AddComponentMenu("SimuLean/Pick Face")]
    [SimuLeanElement(SimuLeanModules.Storage, "Pick Face", Model = "BufferBox/BufferBox.fbx", Description = "Picking location of one SKU: single pieces out, kanban replenishment of unit loads", Pack = SimuLeanPacks.Warehousing, Order = 10, Icon = "Assets/SimuLean.Unity/Art/Models/WarehouseLoadUnits/PalletLoad_Cartons.fbx")]
    public class UnityPickFace : SElement, IVElement
    {
        [Header("Reference")]
        [Tooltip("The part reference (SKU) it holds; unit loads of other references are refused.")]
        public string sku = "SKU";

        [Header("Positions and replenishment")]
        [Tooltip("Unit loads held: 1 open + reserve.")]
        [Min(1)] public int positions = 1;
        [Tooltip("Orders when loads held + on order ≤ this (0 = when the last load is emptied: a kanban of one; " +
                 "with one reserve load use positions 2, reorder point 1).")]
        [SimuLeanHelp("positions = the loads held (1 open + reserve). It orders reorderQuantity loads (capped by the free positions) " +
                      "whenever loads held + on order <= reorderPoint: 0 (default) = when the last load is emptied; one open + one " +
                      "reserve = positions 2, reorderPoint 1.")]
        [Min(0)] public int reorderPoint = 0;
        [Tooltip("Loads ordered each time (capped by the free positions).")]
        [SimuLeanHelp("Loads ordered each time the stock reaches reorderPoint, capped by the free positions.")]
        [Min(1)] public int reorderQuantity = 1;

        [Header("Initial fill")]
        [Tooltip("Start with every position full (loads created here) instead of ordering them at time 0.")]
        [SimuLeanHelp("Off (default): the face starts empty and orders at time 0. On: it starts with positions loads of piecesPerLoad " +
                      "pieces, created here.")]
        public bool startFull = false;
        [Tooltip("Pieces per load of the initial fill.")]
        [Min(0)] public int piecesPerLoad = 10;
        [Tooltip("Item type of the created loads / pieces (empty = the SKU).")]
        public string loadType = "";
        public string pieceType = "";

        [Header("Empties")]
        [Tooltip("Where the emptied unit loads go (e.g. a queue for empties, a milk run). Empty = discarded (counted).")]
        [SimuLeanHelp("Unset = each emptied load is discarded at once (counted in carriers_emptied; nothing reaches a Sink). The empty " +
                      "load moves with no transport and no time: to model its transport, send it to a Queue that a transport element serves.")]
        public SElement emptiesDestination;

        [Header("View (local frame)")]
        [Tooltip("Position of the open load.")]
        public Vector3 firstSlot = Vector3.zero;
        [Tooltip("Offset of each reserve load.")]
        public Vector3 slotStep = new Vector3(0f, 0f, -1.4f);
        [Tooltip("Size of the loads drawn here (initial fill).")]
        public Vector3 loadSize = new Vector3(1.2f, 0.15f, 0.8f);
        [Tooltip("Prefab of the loads created here (initial fill); empty = a box of Load Size.")]
        public GameObject loadPrefab;
        [Tooltip("Size of one piece (its cell in the load's grid) when there is no Piece Prefab; with one, its own size.")]
        public Vector3 pieceSize = new Vector3(0.2f, 0.12f, 0.2f);
        [Tooltip("Prefab of a piece (e.g. the part's model; 2026-10-08); empty = a box of Piece Size.")]
        public GameObject piecePrefab;
        [Tooltip("Rotation of the pieces in the load (Euler angles, e.g. 90, 0, 0 to lay a wheel flat).")]
        public Vector3 pieceRotation = Vector3.zero;
        [Tooltip("Pieces are stacked on the load's top (pallet) instead of from its bottom (tote).")]
        public bool piecesOnTop = true;
        [Tooltip("The loads it creates get the size of the Load Prefab, and the pieces of every load it opens the size of the " +
                 "Piece Prefab as it lays them (turned by Piece Rotation) — labels width / height / length, metres — where " +
                 "their labels set none.")]
        public bool sizeFromModel = true;

        private PickFace face;

        /// <summary>The engine element of the run (null outside a run).</summary>
        public PickFace Face => face;

        /// <summary>Writes its settings into the element's (the extractor's), the models' sizes included.</summary>
        public void Fill(ElementConfig config)
        {
            var s = config.SettingsAs<PickFaceSettings>();
            s.Sku = sku ?? "";
            s.Positions = Mathf.Max(1, positions);
            s.ReorderPoint = Mathf.Max(0, reorderPoint);
            s.ReorderQuantity = Mathf.Max(1, reorderQuantity);
            s.StartFull = startFull;
            s.PiecesPerLoad = Mathf.Max(0, piecesPerLoad);
            s.LoadType = loadType ?? "";
            s.PieceType = pieceType ?? "";
            if (!sizeFromModel) return;
            ReplenishmentBuilders.EncodeModelSize(config, "", ItemVisuals.ModelSize(loadPrefab));
            ReplenishmentBuilders.EncodeModelSize(config, ReplenishmentBuilders.PieceSizePrefix,
                                                  ItemVisuals.ModelSize(piecePrefab, Quaternion.Euler(pieceRotation)));
        }

        protected override void OnBound(Element built)
        {
            face = (PickFace)built;
            face.LoadCreated += (f, load) =>
            {
                if (load.vItem != null) return;
                if (loadPrefab != null) { var go = Instantiate(loadPrefab); go.SetActive(true); load.vItem = go; }
                else load.vItem = UnitLoadVisuals.Box(load.Name, loadSize, UnitLoadVisuals.ColorOf(f.Sku) * 0.6f);
                if (load.vItem is GameObject own) ownLooks.Add(own);
            };
            face.LoadOpened += (f, load) => { OpenLook(load); CreatePieces(load); Layout(); };
            face.PieceTaken += (f, piece) =>
            {
                if (piece.vItem is GameObject go && go != null) go.transform.SetParent(null, true);
                LayoutPieces(face.OpenLoad);
            };
            face.EmptiesPort.ItemLeft += (port, item, discarded) => { if (discarded) Destroy(item); };
        }

        public override void ClearViews()
        {
            if (face == null) return;
            foreach (var l in face.Loads) Destroy(l);
            foreach (var e in face.EmptiesPort.Pending) Destroy(e);
            ownLooks.Clear(); loadBounds.Clear(); pieceCell = null;
        }

        private static void Destroy(Item it)
        {
            if (it?.vItem is GameObject go && go != null) Object.Destroy(go);
        }

        // ── layout ───────────────────────────────────────────────────────────

        /// <summary>World position of load slot <paramref name="i"/> (0 = open).</summary>
        public Vector3 SlotWorld(int i) => transform.TransformPoint(firstSlot + slotStep * i);

        private readonly Dictionary<Item, Bounds> loadBounds = new Dictionary<Item, Bounds>();
        private readonly HashSet<GameObject> ownLooks = new HashSet<GameObject>();

        // A load received with another look (e.g. a full pallet drawn by the replenisher) is opened as this face draws its
        // loads: its Load Prefab, the pieces laid on it.
        private void OpenLook(Item load)
        {
            if (loadPrefab == null || !(load?.vItem is GameObject go) || go == null || ownLooks.Contains(go)) return;
            if (load.subItems != null)
                foreach (var p in load.subItems) if (p.vItem is GameObject pg && pg != null) return;   // its pieces are drawn
            loadBounds.Remove(load);
            var look = ItemVisuals.ReplaceLook(load, loadPrefab, go.name);
            ownLooks.Add(look);
        }

        // The cell of a piece in the load's grid: the Piece Prefab's size as laid (turned by Piece Rotation), else Piece Size.
        private Vector3 PieceCell()
        {
            if (pieceCell.HasValue) return pieceCell.Value;
            double[] m = piecePrefab != null ? ItemVisuals.ModelSize(piecePrefab, Quaternion.Euler(pieceRotation)) : null;
            pieceCell = m != null ? new Vector3((float)m[0], (float)m[1], (float)m[2]) : pieceSize;
            return pieceCell.Value;
        }
        private Vector3? pieceCell;

        private void Layout()
        {
            if (face == null) return;
            for (int i = 0; i < face.Loads.Count; i++)
                if (face.Loads[i]?.vItem is GameObject go && go != null)
                    go.transform.SetPositionAndRotation(SlotWorld(i), transform.rotation);
            LayoutPieces(face.OpenLoad);
        }

        // Gives the pieces of a load a box each (those without a view), children of the load's GameObject.
        private void CreatePieces(Item load)
        {
            if (!(load?.vItem is GameObject lgo) || lgo == null || load.subItems == null) return;
            if (!loadBounds.ContainsKey(load)) loadBounds[load] = LocalBounds(lgo, load);
            Color c = UnitLoadVisuals.ColorOf(face.Sku);
            foreach (var p in load.subItems)
            {
                if (p.vItem is GameObject existing && existing != null) { existing.transform.SetParent(lgo.transform, true); continue; }
                GameObject pgo;
                if (piecePrefab != null)
                {
                    pgo = Instantiate(piecePrefab, lgo.transform, false);
                    pgo.name = p.Name;
                    pgo.SetActive(true);
                }
                else pgo = UnitLoadVisuals.Box(p.Name, pieceSize, c, lgo.transform);
                p.vItem = pgo;
            }
        }

        // Grid inside the load's bounds (local frame of the load), the next piece to leave on top.
        private void LayoutPieces(Item load)
        {
            if (!(load?.vItem is GameObject lgo) || lgo == null || load.subItems == null) return;
            if (!loadBounds.TryGetValue(load, out Bounds b)) loadBounds[load] = b = LocalBounds(lgo, load);
            int n = load.subItems.Count;
            Vector3 cell = PieceCell();
            int cols = Mathf.Max(1, Mathf.FloorToInt(b.size.x / Mathf.Max(0.01f, cell.x)));
            int rows = Mathf.Max(1, Mathf.FloorToInt(b.size.z / Mathf.Max(0.01f, cell.z)));
            float baseY = piecesOnTop ? b.max.y : b.min.y;
            for (int j = 0; j < n; j++)
            {
                if (!(load.subItems[j].vItem is GameObject pgo) || pgo == null) continue;
                int s = n - 1 - j;                       // slot from the bottom: index 0 (next to go) is the highest
                int layer = s / (cols * rows), k = s % (cols * rows);
                float x = b.min.x + ((k % cols) + 0.5f) * (b.size.x / cols);
                float z = b.min.z + ((k / cols) + 0.5f) * (b.size.z / rows);
                pgo.transform.SetParent(lgo.transform, true);
                pgo.transform.localPosition = new Vector3(x, baseY + layer * cell.y, z);
                pgo.transform.localRotation = Quaternion.Euler(pieceRotation);
            }
        }

        // Bounds of a load's renderers (its pieces' views excluded; a model's renderers may sit deep under the load)
        // in its local frame.
        private static Bounds LocalBounds(GameObject go, Item load)
        {
            var pieces = new HashSet<Transform>();
            if (load?.subItems != null)
                foreach (var p in load.subItems) if (p.vItem is GameObject pg && pg != null) pieces.Add(pg.transform);
            bool any = false;
            var b = new Bounds(Vector3.zero, Vector3.zero);
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                bool piece = false;
                for (var up = r.transform; up != null && up != go.transform; up = up.parent) if (pieces.Contains(up)) { piece = true; break; }
                if (piece) continue;
                var lb = r.localBounds;
                var t = r.transform;
                for (int i = 0; i < 8; i++)
                {
                    var corner = lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = go.transform.InverseTransformPoint(t.TransformPoint(corner));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return any ? b : new Bounds(new Vector3(0f, 0.075f, 0f), new Vector3(1.2f, 0.15f, 0.8f));
        }

        // ── VElement ─────────────────────────────────────────────────────────

        void IVElement.ReportState(string msg) { }
        object IVElement.GenerateItem(int type) => null;
        void IVElement.LoadItem(Item item) => Layout();
        void IVElement.UnloadItem(Item item)
        {
            loadBounds.Remove(item);
            Layout();
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.95f, 0.75f, 0.2f, 0.9f);
            for (int i = 0; i < Mathf.Max(1, positions); i++)
                Gizmos.DrawWireCube(transform.TransformPoint(firstSlot + slotStep * i) + Vector3.up * loadSize.y * 0.5f, loadSize);
        }
    }
}
