using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Unity Component for Combiner element.
    /// Exposes Combiner strategies and configuration in Inspector.
    /// </summary>
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Assembly, "Combiner", Description = "Joins a main item with components from its other input connections; Capacity = assemblies in parallel, each matching its own main item (kitting by key: Assets/Docs/Kitting_By_Key.md)", Prefab = "Combiner.prefab", Order = 0)]
    public class UnityCombiner : SElement, IVElement, IProcessResourceUser
    {
        /// <summary>Each input is filtered by the pull mode; the main input admits every item.</summary>
        public override bool HasInput => false;

        /// <summary>Its main and component inputs are the incoming connections (upstream Next Elements).</summary>
        public override bool TakesConnections => true;

        // Configuration
        [Tooltip("Optional order of the incoming connections. Connect every upstream element to the " +
                 "combiner through its Next Elements; element 0 of this list feeds the MAIN input and " +
                 "elements 1..N feed component inputs 0..N-1. Upstream elements missing from the list " +
                 "follow in hierarchy order (so with an empty list the first one in the hierarchy is the main input).")]
        public List<SElement> inputOrder = new List<SElement>();

        /// <summary>An input of the combiner fed from a list instead of a connection.</summary>
        [System.Serializable]
        public class ListInput
        {
            [Tooltip("0 = main input, k = component input k − 1")]
            public int input;
            public UnitySimList list;
            public SimuLean.Collections.ListQuerySpec query = new SimuLean.Collections.ListQuerySpec();
        }

        [Tooltip("Inputs that pull from a list instead of receiving from a connected element. The connected " +
                 "elements fill the remaining inputs in order.")]
        public List<ListInput> listInputs = new List<ListInput>();

        public int[] requirements = { 2 };

        [Tooltip("Assemblies in parallel: each with its own main item, its components and its own matching (the pull mode), " +
                 "e.g. 3 assembly bays served by one yard. 1 = one assembly at a time.")]
        [Min(1)] public int capacity = 1;
        [Tooltip("Distance between the assemblies' work points along the combiner's right axis (0 = from the items' size).")]
        [Min(0)] public float assemblySpacing = 0f;

        [Tooltip("Enable batch mode: adds component items to main item")]
        public bool batchMode = false;

        [Tooltip("Enable dynamic requirements: update requirements based on main item labels")]
        public bool updateRequirements = false;

        [Tooltip("Labels to read from main item for updating requirements (one per input)")]
        public string[] updateLabels;


        //Pull Mode Strategy
        [Tooltip("Pull mode strategy type")]
        public PullModeType pullModeType = PullModeType.Default;

        [Tooltip("For SingleLabel: Label name to validate")]
        public string singleLabelName = "type";

        [Tooltip("For SingleLabel: Required label value (leave empty to update from main item)")]
        public string singleLabelValue = "";

        [Tooltip("For MultiLabel: Label-value pairs to validate (format: label=value)")]
        public string[] multiLabelPairs;

        // ── Delay Strategy ────────────────────────────────────────────
        [Tooltip("Delay strategy configuration - supports multiple distributions and label-based expressions")]
        public RandomProcessConfig delayStrategy = new RandomProcessConfig();

        // Prefab and positions
        [Tooltip("NewItem mode: the visual that replaces the assembled item (empty = Attach).")]
        public GameObject combinedItemPrefab;
        [Tooltip("Where the assembly happens. Empty = the top of the combiner's footprint.")]
        public Transform itemPosition;
        [Tooltip("Optional: the assembled item moves here during processing.")]
        public Transform outItemPosition;
        [Tooltip("Spacing of the waiting components. 0 = automatic, from the size of the items.")]
        public float separation = 0f;
        [Tooltip("Where the components wait for their assembly. Behind: one column per input behind the main item. " +
                 "On Work Point: stacked on the work point, on the main item — parts laid on the assembly bed.")]
        public ComponentLayout componentLayout = ComponentLayout.Behind;

        // Parameters

        // Animation
        public Animator serverAnimator;
        [Tooltip("Attach: the components ride on top of the main item. MainOnly: they disappear. NewItem: a new visual (Combined Item Prefab) replaces them all.")]
        public VisualMode visualMode = VisualMode.Attach;

        private Combiner theCombiner;

        public enum VisualMode
        {
            MainOnly,  // only the main item is shown; the components' visuals are destroyed
            NewItem,   // a combinedItemPrefab instance replaces the main item and the components (none set → Attach)
            Attach     // the components become children of the main item, laid out on its top (default)
        }

        public enum ComponentLayout
        {
            Behind,       // one column per input behind the main item (default)
            OnWorkPoint   // stacked on the work point, on the main item
        }

        public enum PullModeType
        {
            [Tooltip("Accept all items")]
            Default,

            [Tooltip("Validate single label value")]
            SingleLabel,

            [Tooltip("Validate multiple label-value pairs")]
            MultiLabel,

            [Tooltip("Custom strategy (requires code)")]
            Custom
        }

        // every frame after the clock's Update (it was FixedUpdate)
        void LateUpdate()
        {
            if (theCombiner == null || theCombiner.Assemblies == null)
                return;

            var assemblies = theCombiner.Assemblies;
            if (serverAnimator != null)
            {
                bool simPaused = UnitySimClock.Instance != null && UnitySimClock.Instance.SimPaused;
                serverAnimator.speed = simPaused ? 0f : 1f;
                ElementState es = theCombiner.CurrentState;
                serverAnimator.SetBool("WorkInProgress", es == ElementState.PROCESSING || es == ElementState.BLOCKED);
            }
            float spacing = AssemblySpacing();
            for (int k = 0; k < assemblies.Count; k++)
            {
                var a = assemblies[k];
                // assemblies side by side along the combiner's right axis (one assembly: on the work point)
                Vector3 work = WorkPoint() + transform.right * ((k - (assemblies.Count - 1) * 0.5f) * spacing);
                ElementState state = a.State;
                if (state == ElementState.RECEIVING)
                {
                    // the main item on the work point; the components wait behind it, one column per input, stacked
                    Vector3 mainSize = Vector3.zero;
                    // != null: the items of a restarted run are destroyed (ClearViews) before the new model is bound
                    if (a.Process.theItem?.vItem is GameObject mainGo && mainGo != null)
                    {
                        mainSize = ItemVisuals.SizeOf(mainGo);
                        mainGo.transform.SetPositionAndRotation(work + transform.up * ItemVisuals.PivotHeight(mainGo), transform.rotation);
                    }
                    int inputCount = theCombiner.GetInputsCount();
                    float stack = mainSize.y;   // On Work Point: the parts pile up on the main item
                    for (int i = 0; i < inputCount; i++)
                    {
                        int j = 0;
                        foreach (Item item in a.PartsOf(i))
                        {
                            if (item.vItem is GameObject go && go != null)
                            {
                                Vector3 size = ItemVisuals.SizeOf(go);
                                Vector3 o = PartOffset(componentLayout, i, inputCount, j, mainSize, size, separation, stack);
                                stack += size.y;
                                go.transform.SetPositionAndRotation(
                                    work + transform.right * o.x + transform.up * (o.y + ItemVisuals.PivotHeight(go))
                                         + transform.forward * o.z,
                                    transform.rotation);
                            }
                            j++;
                        }
                    }
                }
                else if (state == ElementState.PROCESSING && serverAnimator == null)
                {
                    // default animation (smooth sim-time lerp, pause-aware)
                    var proc = a.Process;
                    if (proc.theItem != null && outItemPosition != null && proc.theItem.vItem is GameObject procGo && procGo != null)
                    {
                        float currentSimTime = UnitySimClock.Instance.SmoothSimTime;
                        float p = proc.lastDelay > 0
                            ? Mathf.Clamp01((currentSimTime - (float)proc.loadTime) / (float)proc.lastDelay)
                            : 1f;
                        procGo.transform.position = Vector3.Lerp(work, outItemPosition.position, p);
                    }
                }
            }
        }

        /// <summary>Where a waiting component's bottom stands relative to its assembly's work point, in the combiner's
        /// axes (x right, y up, z forward). Behind: column <paramref name="input"/> of <paramref name="inputCount"/>
        /// behind the main item, <paramref name="layer"/> items high. On Work Point: centred on the work point, at
        /// <paramref name="stackHeight"/> (the main item and the parts already there).</summary>
        public static Vector3 PartOffset(ComponentLayout layout, int input, int inputCount, int layer, Vector3 mainSize,
                                           Vector3 size, float separation, float stackHeight)
        {
            if (layout == ComponentLayout.OnWorkPoint) return new Vector3(0f, stackHeight, 0f);
            float step = separation > 0f ? separation : Mathf.Max(size.x, size.z) * 1.15f;
            float x = (input - (inputCount - 1) * 0.5f) * step;
            float back = (mainSize.z + size.z) * 0.5f + step * 0.15f;   // behind the main item
            return new Vector3(x, size.y * 1.05f * layer, -back);
        }

        // Distance between assemblies: the field, else the widest main item seen (1.4 ×), else 2 units.
        private float lastMainWidth;
        private float AssemblySpacing()
        {
            if (assemblySpacing > 0f) return assemblySpacing;
            if (theCombiner.Assemblies.Count < 2) return 0f;
            foreach (var a in theCombiner.Assemblies)
                if (a.Process.theItem?.vItem is GameObject go && go != null)
                    lastMainWidth = Mathf.Max(lastMainWidth, ItemVisuals.SizeOf(go).x);
            return lastMainWidth > 0f ? lastMainWidth * 1.4f : 2f;
        }

        /// <summary>Where the assembly happens: <see cref="itemPosition"/> when set, else the top of the combiner's footprint.</summary>
        private Vector3 WorkPoint()
        {
            if (itemPosition != null && itemPosition != transform) return itemPosition.position;
            // the footprint measured once per run, in the element's axes
            if (workLocal == null)
            {
                ItemVisuals.Footprint(this, out _, out Vector3 top);
                workLocal = transform.InverseTransformPoint(top);
            }
            return transform.TransformPoint(workLocal.Value);
        }
        private Vector3? workLocal;

        /// <summary>
        /// Upstream elements whose Next Elements include this combiner, in connection order:
        /// [0] = main input, [k] = component input k − 1. Listed <see cref="inputOrder"/> entries
        /// first (those that really connect here), then the rest in hierarchy order.
        /// </summary>
        public List<SElement> ResolveInputOrder()
        {
            var upstream = new List<SElement>();
            foreach (SElement se in UnityCompat.FindAll<SElement>(true))
                if (se != null && se != this && se.nextElements != null && se.nextElements.Contains(this))
                    upstream.Add(se);

            var ordered = new List<SElement>();
            if (inputOrder != null)
                foreach (SElement se in inputOrder)
                    if (se != null && upstream.Contains(se) && !ordered.Contains(se)) ordered.Add(se);

            var rest = upstream.FindAll(se => !ordered.Contains(se));
            rest.Sort((a, b) => string.CompareOrdinal(HierarchyKey(a.transform), HierarchyKey(b.transform)));
            ordered.AddRange(rest);
            return ordered;
        }

        /// <summary>The list feeding input <paramref name="index"/> (0 = main), or null when a connection does.</summary>
        public ListInput ListInputAt(int index) =>
            listInputs?.Find(li => li != null && li.list != null && li.input == index);

        /// <summary>
        /// Every input in order ([0] = main, [k] = component k − 1): the connected element feeding it, or
        /// null when a list feeds it. Connections (<see cref="ResolveInputOrder"/>) fill the inputs that no
        /// list takes; extra connections are appended so the count check can report them.
        /// </summary>
        public List<SElement> ResolveInputSlots()
        {
            List<SElement> connected = ResolveInputOrder();
            int needed = (requirements?.Length ?? 0) + 1;
            var slots = new List<SElement>();
            int next = 0;
            for (int i = 0; i < needed || next < connected.Count; i++)
            {
                if (ListInputAt(i) != null) slots.Add(null);
                else if (next < connected.Count) slots.Add(connected[next++]);
                else if (i < needed) slots.Add(null);   // missing input (reported by the count check)
                else break;
            }
            return slots;
        }

        /// <summary>Number of inputs that have a feeder (connection or list).</summary>
        public int FedInputCount()
        {
            int lists = 0;
            var seen = new HashSet<int>();
            if (listInputs != null)
                foreach (var li in listInputs)
                    if (li != null && li.list != null && seen.Add(li.input)) lists++;
            return ResolveInputOrder().Count + lists;
        }

        private static string HierarchyKey(Transform t)
        {
            string key = "";
            for (; t != null; t = t.parent) key = t.GetSiblingIndex().ToString("D6") + "/" + key;
            return key;
        }

        protected override void OnBound(Element built)
        {
            theCombiner = (Combiner)built;
            if (FedInputCount() != requirements.Length + 1)
                Debug.LogError($"{name}: {FedInputCount()} input(s) are fed (connections + lists) but it needs " +
                               $"{requirements.Length + 1} (1 main + {requirements.Length} component inputs, one per requirement).");
        }

        void IVElement.ReportState(string msg)
        {
            //No Implementation   
        }

        object IVElement.GenerateItem(int myId)
        {
            throw new System.NotImplementedException("GenerateItem is not used in UnityCombiner.");

        }

        void IVElement.LoadItem(Item vItem)
        {
            // components arriving: placed by LateUpdate while the combiner gathers
        }

        /// <summary>Processing starts: the components join the main item (how they look depends on <see cref="visualMode"/>).</summary>
        void IVElement.UnloadItem(Item vItem)
        {
            var main = vItem.vItem as GameObject;
            var subs = vItem.GetSubItems();
            VisualMode mode = visualMode == VisualMode.NewItem && combinedItemPrefab == null ? VisualMode.Attach : visualMode;

            if (mode == VisualMode.Attach && main != null)
            {
                AttachOnTop(main, subs);
                return;
            }

            // MainOnly / NewItem: the components' visuals are consumed
            if (subs != null)
                foreach (Item sub in subs)
                    if (sub.vItem is GameObject subGItem && subGItem != null)
                    {
                        Destroy(subGItem);
                        sub.vItem = null;
                    }

            if (mode == VisualMode.NewItem && main != null)
            {
                GameObject newItem = Instantiate(combinedItemPrefab, main.transform.position, main.transform.rotation);
                newItem.SetActive(true);
                newItem.name = "CombinedItem" + vItem.GetId();
                Destroy(main);
                vItem.vItem = newItem;   // the new visual travels on with the item
            }
        }

        // the components as children of the main item, in a grid on its top (layers when its top is full); a
        // container model (pallet, tote, roll cage) marks where its load rests with a LoadSocket node
        private static void AttachOnTop(GameObject main, List<Item> subs)
        {
            if (subs == null || subs.Count == 0) return;
            Vector3 mainSize = ItemVisuals.SizeOf(main);
            Transform socket = ItemVisuals.LoadSocket(main);
            Vector3 top = socket != null ? socket.position
                                         : main.transform.position + main.transform.up * ItemVisuals.TopAbovePivot(main);
            int k = 0;
            foreach (Item sub in subs)
            {
                if (!(sub.vItem is GameObject go) || go == null) continue;
                Vector3 size = ItemVisuals.SizeOf(go);
                float step = Mathf.Max(0.01f, Mathf.Max(size.x, size.z) * 1.05f);
                int cols = Mathf.Max(1, Mathf.FloorToInt(Mathf.Max(mainSize.x, step) / step));
                int rows = Mathf.Max(1, Mathf.FloorToInt(Mathf.Max(mainSize.z, step) / step));
                int perLayer = cols * rows, layer = k / perLayer, cell = k % perLayer;
                int count = Mathf.Min(subs.Count - layer * perLayer, perLayer);
                int usedCols = Mathf.Min(cols, count), usedRows = (count + cols - 1) / cols;
                float x = (cell % cols - (usedCols - 1) * 0.5f) * step;
                float z = ((usedRows - 1) * 0.5f - cell / cols) * step;
                go.transform.SetPositionAndRotation(
                    top + main.transform.right * x + main.transform.forward * z + main.transform.up * (ItemVisuals.PivotHeight(go) + size.y * layer),
                    main.transform.rotation);
                go.transform.SetParent(main.transform, true);
                k++;
            }
        }

        public override void ClearViews()
        {
            workLocal = null;
            var items = new List<Item>(theCombiner.GetItems());
            foreach (Item it in items)
            {
                if (it.vItem is GameObject go)
                    Destroy(go);
                if (batchMode && it.GetSubItems() != null)
                {
                    foreach (Item sub in it.GetSubItems())
                    {
                        if (sub.vItem is GameObject subGo)
                            Destroy(subGo);
                    }
                }
            }

            if (serverAnimator != null)
            {
                serverAnimator.speed = 1f;
                serverAnimator.SetBool("WorkInProgress", false);
            }

        }
    }
}
