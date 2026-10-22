using System;
using System.Collections.Generic;
using SimuLean;
using SimuLean.Data;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>One SKU→qty row of a static pick list.</summary>
    [Serializable]
    public class PickRow
    {
        [Tooltip("SKU to pick.")]
        public string sku;
        [Tooltip("Quantity of the SKU per container.")]
        public int qty = 1;
    }

    /// <summary>
    /// Unity Component for the <see cref="PickingStation"/> element.
    ///
    /// Flow: container/order items arrive via upstream elements' nextElements;
    /// stock items flow from <see cref="stockSources"/> into the station's SKU
    /// stock port. The pick list per container is resolved in priority order:
    /// SQLite table → container labels (prefix) → static Inspector list.
    /// After each pick cycle, non-picked stock is flushed to
    /// <see cref="returnsDestination"/>.
    /// </summary>
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Assembly, "Picking Station", Description = "Picks SKUs from a stock port into containers; surplus returns", Order = 1, Icon = "Assets/SimuLean.Unity/Art/Models/LoadUnits/Tote.fbx")]
    public class UnityPickingStation : SElement, IVElement, IProcessResourceUser
    {

        [Header("Pick cycle duration")]
        [Tooltip("Time to pick one container (expressions read the container and the station).")]
        public RandomProcessConfig delayStrategy = new RandomProcessConfig();

        [Header("Stock input")]
        [Tooltip("Upstream elements feeding stock/material items into the stock port. " +
                 "Leave their own nextElements empty — this station wires them.")]
        public List<SElement> stockSources = new List<SElement>();

        [Tooltip("Max items held in the stock buffer; -1 = unbounded.")]
        public int stockCapacity = -1;

        [Header("Returns")]
        [Tooltip("Destination for non-picked items after each pick cycle. " +
                 "Unwired = returned items are discarded (counted on the returns port).")]
        public SElement returnsDestination;

        [Header("Pick list — data table (1st priority)")]
        [Tooltip("A data table of the model with one row per (container key, SKU, quantity), its columns named below (Phase " +
                 "D3c). Indexed once when each run starts; a study varies it as data:<table>.")]
        public UnityDataTable pickTable;

        [Tooltip("Column with the SKU.")]
        public string skuColumn = "sku";
        [Tooltip("Column with the quantity.")]
        public string qtyColumn = "qty";
        [Tooltip("Empty = the whole table is one shared list. Otherwise rows are " +
                 "matched per container on this column.")]
        public string keyColumn = "";
        [Tooltip("Container label holding the key value (falls back to the container's type name).")]
        public string containerKeyLabel = "order_id";

        [Header("Pick list — container labels (2nd priority)")]
        [Tooltip("Read the pick list from the container's labels pick_<SKU> = quantity.")]
        public bool useContainerLabels = true;
        [Tooltip("Labels named '<prefix><SKU>' = qty define the container's own pick list.")]
        public string labelPrefix = "pick_";

        [Header("Pick list — static fallback")]
        public List<PickRow> staticPickList = new List<PickRow>();

        private PickingStation station;



        protected override void OnBound(Element built) => station = (PickingStation)built;

        // The item views this station positioned (its jobs / containers and stock): destroyed when the run is cleared,
        // as every element's (they stayed in the scene after Restart / Stop).
        private readonly List<GameObject> itemViews = new List<GameObject>();

        public override void ClearViews()
        {
            foreach (var go in itemViews) if (go != null) Destroy(go);
            itemViews.Clear();
        }
        // ── VElement (minimal visuals: stack stored items above the station) ──

        void IVElement.ReportState(string msg) { }

        object IVElement.GenerateItem(int type) => null;

        void IVElement.LoadItem(Item theItem)
        {
            var go = theItem.vItem as GameObject;
            if (go == null) return;
            if (itemViews.Count > 256) itemViews.RemoveAll(v => v == null);   // views destroyed downstream (sinks)
            itemViews.Add(go);
            int n = station != null ? station.GetQueueLength() : 0;
            go.transform.position = transform.position + Vector3.up * (0.5f + 0.25f * n);
        }

        void IVElement.UnloadItem(Item theItem) { }
    }
}
