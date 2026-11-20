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
    ///
    /// View (read-only; the library model is the PickingStation pick-to-light workstation): the container being picked
    /// stands on <see cref="itemPosition"/> (the model's <c>WorkSocket</c>, the tote's place on the worktop); the stock
    /// waits in the model's bins (<c>StockSocket_0</c>, <c>_1</c>…, one SKU per bin in the order the SKUs first arrive,
    /// packed in <see cref="binSize"/>); during the pick time the picked items travel one after another from their bins
    /// into the container, which carries them on (children of its visual).
    /// </summary>
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Assembly, "Picking Station", Model = "PickingStation/PickingStation.fbx", Description = "Picks SKUs from a stock port into containers; surplus returns", Order = 11, Icon = "Assets/SimuLean.Unity/Art/Models/PickingStation/PickingStation.fbx")]
    public class UnityPickingStation : SElement, IVElement, IProcessResourceUser, ISetupUser
    {
        [Tooltip("Setup (changeover) before a container's process: when (by the container), how long, by whom (Setup section).")]
        public StationSetupConfig setup = new StationSetupConfig();
        public StationSetupConfig Setup => setup;
        [Tooltip("Reserve room downstream before a job starts: the job waits (BLOCKED, its item inside, no resource taken) until " +
                 "a place at its next element can be promised, so the item never waits blocked after its process. A routing rule " +
                 "is evaluated at the start and kept. Behind a general, carrier or walk link (not a list).")]
        public bool reserveExit;


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
        [Tooltip("On: the stock not picked leaves after each container (an order kitted from stock brought for it). " +
                 "Off: it stays in the bins for the next containers (a goods-to-person station).")]
        public bool returnLeftovers = true;

        [Tooltip("Destination for non-picked items after each pick cycle. " +
                 "Unwired = returned items are discarded (counted on the returns port).")]
        public SElement returnsDestination;

        [Header("Pick list — data table (1st priority)")]
        [Tooltip("A data table of the model with one row per (container key, SKU, quantity), its columns named below. " +
                 "Indexed once when each run starts; a study varies it as data:<table>.")]
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


        [Header("View (local frame)")]
        [Tooltip("Where the container being picked stands (bound to the station model's WorkSocket). Empty = on the " +
                 "station's top.")]
        public Transform itemPosition;
        [Tooltip("Inside size (x, z in m) of a stock bin (the model's StockSocket_0, _1…: one SKU each, in the order the " +
                 "SKUs first arrive): its items are packed in it, side by side, then rows, then layers.")]
        public Vector2 binSize = new Vector2(0.36f, 0.46f);
        [Tooltip("The picked items travel one after another from their bins into the container during the pick time (off = " +
                 "they jump in when the pick starts).")]
        public bool animatePicking = true;

        /// <summary>The bins' sockets in the model, in order (StockSocket_0, _1…).</summary>
        public const string StockSocketPrefix = "StockSocket_";

        private PickingStation station;

        /// <summary>The engine element of the run (null outside a run).</summary>
        public PickingStation Station => station;

        protected override void OnBound(Element built)
        {
            station = (PickingStation)built;
            bins = Bins();
            binOf.Clear();
            shownInBin.Clear();
            stockShown = -1;
            station.ProcessEnded += (s, job) => FinishMoves();
        }

        // The item views this station positioned (its stock and the container it picks into): destroyed when the run is
        // cleared, as every element's (they would stay in the scene after Restart / Stop).
        private readonly List<GameObject> itemViews = new List<GameObject>();

        public override void ClearViews()
        {
            moves.Clear();
            pickedFor = null;
            foreach (var go in itemViews) if (go != null) Destroy(go);
            itemViews.Clear();
            var job = Job();
            if (job?.vItem is GameObject jgo && jgo != null) Destroy(jgo);
            binOf.Clear();
            shownInBin.Clear();
        }

        /// <summary>The bins of the shown model (its <c>StockSocket_k</c>, k = 0, 1… while they exist); empty without one.</summary>
        public List<Transform> Bins()
        {
            var list = new List<Transform>();
            for (int k = 0; ; k++)
            {
                var t = StationModel.Position(this, StockSocketPrefix + k);
                if (t == null) return list;
                list.Add(t);
            }
        }

        /// <summary>Where the container being picked stands (the bottom of it): <see cref="itemPosition"/>, else the
        /// station's top.</summary>
        public Vector3 WorkPoint()
        {
            if (itemPosition != null) return itemPosition.position;
            ItemVisuals.Footprint(this, out _, out Vector3 top);
            return top;
        }

        private Item Job()
        {
            var p = station?.ActiveProcess;
            return p != null && p.GetState() != ElementState.IDLE ? p.GetItem() : null;
        }

        // ── VElement: stock items arrive (the containers are placed by LateUpdate) ──

        void IVElement.ReportState(string msg) { }

        object IVElement.GenerateItem(int type) => null;

        void IVElement.LoadItem(Item theItem)
        {
            var go = theItem.vItem as GameObject;
            if (go == null) return;
            if (itemViews.Count > 256) itemViews.RemoveAll(v => v == null);   // views destroyed downstream (sinks)
            if (shownInBin.Count > 256) shownInBin.RemoveWhere(v => v == null);
            itemViews.Add(go);
            stockShown = -1;   // laid out in the next LateUpdate
        }

        void IVElement.UnloadItem(Item theItem) { }

        // ── the layout (view only) ─────────────────────────────────────────

        private List<Transform> bins = new List<Transform>();
        private readonly Dictionary<string, int> binOf = new Dictionary<string, int>();
        private readonly HashSet<GameObject> shownInBin = new HashSet<GameObject>();   // stock items laid out in a bin
        private int stockShown = -1;            // the stock count last laid out (-1 = lay it out again)
        private Item pickedFor;                 // the container whose picked items are on their way / in it

        private sealed class PickMove
        {
            public Transform Part;
            public Vector3 From, ToLocal;
            public Quaternion FromRot, ToLocalRot;
            public int Index, Count;
        }

        private readonly List<PickMove> moves = new List<PickMove>();
        private double moveStart, moveDuration;

        void LateUpdate()
        {
            if (station == null) return;
            var job = Job();
            GameObject jgo = job?.vItem as GameObject;
            if (jgo != null)
            {
                jgo.transform.rotation = transform.rotation * ItemVisuals.AlongFlow;   // lying along the worktop (its marks)
                jgo.transform.position = WorkPoint() + transform.up * ItemVisuals.PivotHeight(jgo);
                if (pickedFor != job && job.subItems != null && job.subItems.Count > 0
                    && (station.ActiveProcess.GetState() == ElementState.PROCESSING || station.ActiveProcess.GetState() == ElementState.BLOCKED))
                    StartMoves(job, jgo);
            }
            else if (pickedFor != null) FinishMoves();
            AnimateMoves(jgo);
            var stock = station.GetStockPort();
            if (stock.TotalCount != stockShown) LayOutStock(stock);
        }

        // the picked items, from where they are (their bins) to their places in the container: the places are those the
        // Combiner gives a load (UnityCombiner.LayOnLoad), kept in the container's axes
        private void StartMoves(Item job, GameObject jgo)
        {
            FinishMoves();
            pickedFor = job;
            var parts = new List<GameObject>();
            var from = new List<(Vector3, Quaternion)>();
            var sizes = new List<Vector3>();
            foreach (var sub in job.subItems)
            {
                if (!(sub?.vItem is GameObject go) || go == null || go.transform.IsChildOf(jgo.transform)) continue;
                parts.Add(go);
                // an item picked in the instant it arrived was never shown in its bin: it starts from there, not from
                // where the element before left it
                if (!shownInBin.Contains(go) && bins.Count > 0)
                {
                    string sku = SkuStockPort.KeyOf(sub);
                    if (!binOf.TryGetValue(sku, out int b)) binOf[sku] = b = binOf.Count % bins.Count;
                    from.Add((bins[b].position + transform.up * ItemVisuals.PivotHeight(go), transform.rotation));
                }
                else from.Add((go.transform.position, go.transform.rotation));
                shownInBin.Remove(go);
                Vector3 s = Quaternion.Inverse(go.transform.rotation) * ItemVisuals.SizeOf(go);
                sizes.Add(new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
                go.transform.rotation = jgo.transform.rotation;
            }
            if (parts.Count == 0) return;
            UnityCombiner.LayOnLoad(jgo, parts, sizes, false);
            var p = station.ActiveProcess;
            var clock = UnitySimClock.Instance;
            bool animate = animatePicking && clock != null && p.lastDelay > 0;
            moveStart = p.loadTime;
            moveDuration = p.lastDelay;
            for (int k = 0; k < parts.Count; k++)
            {
                Transform t = parts[k].transform;
                var mv = new PickMove
                {
                    Part = t, From = from[k].Item1, FromRot = from[k].Item2,
                    ToLocal = jgo.transform.InverseTransformPoint(t.position),
                    ToLocalRot = Quaternion.Inverse(jgo.transform.rotation) * t.rotation, Index = k, Count = parts.Count,
                };
                if (animate)
                {
                    t.SetPositionAndRotation(mv.From, mv.FromRot);
                    moves.Add(mv);
                }
                else t.SetParent(jgo.transform, true);
            }
        }

        private void AnimateMoves(GameObject jgo)
        {
            if (moves.Count == 0) return;
            if (jgo == null) { FinishMoves(); return; }
            var clock = UnitySimClock.Instance;
            double now = clock != null ? clock.SmoothSimTime : double.MaxValue;
            for (int i = moves.Count - 1; i >= 0; i--)
            {
                var mv = moves[i];
                if (mv.Part == null) { moves.RemoveAt(i); continue; }
                float p = UnityCombiner.MoveProgress(now, moveStart, moveDuration, mv.Index, mv.Count);
                Vector3 to = jgo.transform.TransformPoint(mv.ToLocal);
                Quaternion toRot = jgo.transform.rotation * mv.ToLocalRot;
                mv.Part.SetPositionAndRotation(UnityCombiner.MovePose(mv.From, to, p), Quaternion.Slerp(mv.FromRot, toRot, p));
                if (p >= 1f)
                {
                    mv.Part.SetParent(jgo.transform, true);
                    moves.RemoveAt(i);
                }
            }
        }

        // the pick ended (or the container left): every picked item in its place in the container, carried by it
        private void FinishMoves()
        {
            var job = pickedFor;
            pickedFor = null;
            if (moves.Count == 0) return;
            var jgo = job?.vItem as GameObject;
            foreach (var mv in moves)
            {
                if (mv.Part == null || jgo == null) continue;
                mv.Part.SetPositionAndRotation(jgo.transform.TransformPoint(mv.ToLocal), jgo.transform.rotation * mv.ToLocalRot);
                mv.Part.SetParent(jgo.transform, true);
            }
            moves.Clear();
            if (job != null && Job() == job) pickedFor = job;   // still here (blocked): not picked again
        }

        // the stock in the bins, one SKU per bin; without bins (no model) stacked above the station
        private void LayOutStock(SkuStockPort stock)
        {
            stockShown = stock.TotalCount;
            var perBin = new Dictionary<int, List<GameObject>>();
            int n = 0;
            foreach (var it in stock.Stored)
            {
                if (!(it.vItem is GameObject go) || go == null) continue;
                if (bins.Count == 0)
                {
                    go.transform.position = transform.position + Vector3.up * (0.5f + 0.25f * n++);
                    continue;
                }
                string sku = SkuStockPort.KeyOf(it);
                if (!binOf.TryGetValue(sku, out int b)) binOf[sku] = b = binOf.Count % bins.Count;
                if (!perBin.TryGetValue(b, out var list)) perBin[b] = list = new List<GameObject>();
                list.Add(go);
            }
            var area = new Vector3(binSize.x, 0f, binSize.y);
            foreach (var kv in perBin)
            {
                Transform bin = bins[kv.Key];
                var sizes = new List<Vector3>(kv.Value.Count);
                foreach (var go in kv.Value)
                {
                    go.transform.rotation = transform.rotation;
                    Vector3 s = Quaternion.Inverse(transform.rotation) * ItemVisuals.SizeOf(go);
                    sizes.Add(new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
                }
                var at = UnityCombiner.PackOnTop(area, sizes);
                for (int k = 0; k < kv.Value.Count; k++)
                {
                    GameObject go = kv.Value[k];
                    go.transform.position = bin.position + transform.right * at[k].x + transform.forward * at[k].z
                                            + transform.up * (at[k].y + ItemVisuals.PivotHeight(go));
                    shownInBin.Add(go);
                }
            }
        }
    }
}
