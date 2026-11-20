using System.Collections.Generic;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>One reference of a <see cref="UnityBaseStockReplenisher"/> table.</summary>
    [System.Serializable]
    public class ReplenishmentSkuRow
    {
        public string sku = "";
        [Min(0)] public int piecesPerLoad = 1;
        [Tooltip("Unit loads of this reference stored at time 0.")]
        [Min(0)] public int initialLoads = 0;
    }

    /// <summary>
    /// Unity wrapper of <see cref="BaseStockReplenisher"/>: orders truckloads of the consumed mix (consumption = the
    /// retrievals of the observed storages), receives them after the lead time and releases their unit loads through its
    /// output only within the unloading windows — give it a Transport team (e.g. the forklifts) to time the unloading.
    /// The SKU table comes from <see cref="skuTableFile"/> (CSV / Excel with columns <c>sku</c>, <c>piecesPerLoad</c>,
    /// <c>initialLoads</c>; any path, so confidential data can stay outside the project) or, when no file is set, from
    /// <see cref="skus"/>. Built by <c>BaseStockReplenisherHandler</c>.
    /// <para><b>View</b> (read-only): loads waiting at the dock stand in a row along <see cref="dockStep"/>; loads get a
    /// box of <see cref="loadSize"/> (or an instance of <see cref="loadPrefab"/>).</para>
    /// </summary>
    [AddComponentMenu("SimuLean/Base-Stock Replenisher")]
    [SimuLeanElement(SimuLeanModules.Sources, "Base-Stock Replenisher", Description = "Truckloads of the consumed mix: lead time, unloading windows, initial stock", Pack = SimuLeanPacks.Warehousing, Order = 5, Icon = "Assets/SimuLean.Unity/Art/Models/TruckTrailer/SemiTrailer.fbx")]
    public class UnityBaseStockReplenisher : SElement, IVElement
    {
        public override bool HasInput => false;

        [Header("References")]
        [Tooltip("CSV / Excel file with the SKU table (columns sku, piecesPerLoad, initialLoads). Any path (keep confidential data " +
                 "outside the project). Empty = the table below.")]
        public string skuTableFile = "";
        [Tooltip("Excel sheet (empty = the first).")]
        public string sheetName = "";
        [Tooltip("SKU table used when no file is set.")]
        public List<ReplenishmentSkuRow> skus = new List<ReplenishmentSkuRow>();

        [Header("Orders")]
        [Tooltip("Unit loads per truck: an order is placed each time this many have been consumed.")]
        [Min(1)] public int truckloadSize = 26;
        [Tooltip("Model a lead time (order → arrival).")]
        public bool useLeadTime = true;
        public RandomProcessConfig leadTime = new RandomProcessConfig();
        [Tooltip("When trucks may be unloaded, e.g. 'Mon-Fri 06:00-22:00' (model calendar). Empty = always.")]
        public string unloadingWindows = "";

        [Header("Consumption and initial stock")]
        [Tooltip("Storages whose retrievals count as consumption. Empty = the storages it delivers to.")]
        [SimuLeanHelp("Storages only (a Storage Rack / Storage System, not a pick face or a queue). Consumption counts when a load " +
                      "is retrieved from one of them (a pull from its stock list), not when its pieces are used.")]
        public List<UnityStorageSystem> observe = new List<UnityStorageSystem>();
        [Tooltip("Where the initial stock is stored at time 0. Empty = its first next element.")]
        public SElement initialStockTarget;
        [Tooltip("Item type of the loads / pieces (empty = the SKU).")]
        public string loadType = "";
        public string pieceType = "";

        [Header("View")]
        [Tooltip("Prefab of a unit load (empty = a box of Load Size).")]
        public GameObject loadPrefab;
        [Tooltip("The loads get the size of the Load Prefab (labels width / height / length, metres) where their labels set " +
                 "none — what a storage with physical dimensions reads.")]
        public bool sizeFromModel = true;
        public Vector3 loadSize = new Vector3(1.2f, 1.0f, 0.8f);
        [Tooltip("Where the loads of an arrived truck wait (local), and the offset between them.")]
        public Vector3 dockOffset = Vector3.zero;
        public Vector3 dockStep = new Vector3(1.4f, 0f, 0f);

        private BaseStockReplenisher replenisher;

        /// <summary>The engine element of the run (null outside a run).</summary>
        public BaseStockReplenisher Replenisher => replenisher;

        /// <summary>The SKU rows: from the table file when set (read now), else the inline table.</summary>
        public List<ReplenisherSkuSettings> ResolveSkus()
        {
            if (!string.IsNullOrWhiteSpace(skuTableFile))
            {
                string path = skuTableFile;
                if (!System.IO.File.Exists(path)) path = System.IO.Path.Combine(Application.dataPath, skuTableFile);
                if (!System.IO.File.Exists(path))
                    throw new System.IO.FileNotFoundException($"[{name}] SKU table file '{skuTableFile}' not found.");
                return ReplenishmentBuilders.SkusFromTable(TableFiles.Read(path, string.IsNullOrEmpty(sheetName) ? null : sheetName), name);
            }
            var rows = new List<ReplenisherSkuSettings>();
            foreach (var r in skus)
                if (r != null && !string.IsNullOrWhiteSpace(r.sku))
                    rows.Add(new ReplenisherSkuSettings { Sku = r.sku.Trim(), PiecesPerLoad = Mathf.Max(0, r.piecesPerLoad), InitialLoads = Mathf.Max(0, r.initialLoads) });
            return rows;
        }

        /// <summary>Writes its settings into the element's (the extractor's), the SKU table's rows and the load model's
        /// size included.</summary>
        public void Fill(ElementConfig config)
        {
            var s = config.SettingsAs<ReplenisherSettings>();
            s.Skus = ResolveSkus();
            s.TruckloadSize = Mathf.Max(1, truckloadSize);
            s.LeadTime = useLeadTime && leadTime != null ? leadTime.ToSettings() : null;
            s.UnloadingWindows = unloadingWindows ?? "";
            s.InitialStockTarget = initialStockTarget != null ? initialStockTarget.name : "";
            s.LoadType = loadType ?? "";
            s.PieceType = pieceType ?? "";
            s.Observe = new List<string>();
            foreach (var o in observe) if (o != null) s.Observe.Add(o.name);
            if (sizeFromModel) ReplenishmentBuilders.EncodeModelSize(config, "", ItemVisuals.ModelSize(loadPrefab));
        }

        protected override void OnBound(Element built)
        {
            replenisher = (BaseStockReplenisher)built;
            replenisher.LoadCreated += (r, load) =>
            {
                if (load.vItem != null) return;
                GameObject go = loadPrefab != null ? Instantiate(loadPrefab)
                              : UnitLoadVisuals.Box(load.Name, loadSize, UnitLoadVisuals.ColorOf(PickFace.SkuOf(load)) * 0.6f);
                go.SetActive(true);
                go.transform.position = transform.TransformPoint(dockOffset);
                load.vItem = go;
            };
            replenisher.TruckArrived += (r, t) => LayoutDock(t);
        }

        private void LayoutDock(BaseStockReplenisher.Truck t)
        {
            if (t?.Group?.subItems == null) return;
            for (int i = 0; i < t.Group.subItems.Count; i++)
                if (t.Group.subItems[i].vItem is GameObject go && go != null)
                    go.transform.SetPositionAndRotation(transform.TransformPoint(dockOffset + dockStep * i), transform.rotation);
        }

        public override void ClearViews()
        {
            if (replenisher == null) return;
            // Only the loads still at the dock: released loads belong to the elements they went to.
            foreach (var l in replenisher.DockLoads)
                if (l.vItem is GameObject go && go != null) Destroy(go);
        }

        void IVElement.ReportState(string msg) { }
        object IVElement.GenerateItem(int type) => null;
        void IVElement.LoadItem(Item item) { }
        void IVElement.UnloadItem(Item item) { }

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.4f, 0.9f, 0.4f, 0.9f);
            for (int i = 0; i < 3; i++)
                Gizmos.DrawWireCube(transform.TransformPoint(dockOffset + dockStep * i) + Vector3.up * loadSize.y * 0.5f, loadSize);
        }
    }
}
