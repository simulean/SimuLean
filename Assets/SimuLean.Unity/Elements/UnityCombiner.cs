using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Unity Component for Combiner element.
    /// Exposes Combiner strategies and configuration in Inspector.
    /// </summary>
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Assembly, "Combiner", Description = "Joins a main item with components from its other input connections; Capacity = assemblies in parallel, each matching its own main item (kitting by key: Assets/Docs/Kitting_By_Key.md)", Prefab = "Combiner.prefab", Order = 1)]
    public class UnityCombiner : SElement, IVElement, IProcessResourceUser, ISetupUser
    {
        [Tooltip("Setup (changeover) before an assembly's process: when (by the main item), how long, by whom (Setup section).")]
        public StationSetupConfig setup = new StationSetupConfig();
        public StationSetupConfig Setup => setup;
        [Tooltip("Reserve room downstream before a job starts: the job waits (BLOCKED, its item inside, no resource taken) until " +
                 "a place at its next element can be promised, so the item never waits blocked after its process. A routing rule " +
                 "is evaluated at the start and kept. Behind a general, carrier or walk link (not a list).")]
        public bool reserveExit;

        /// <summary>Each input is filtered by the pull mode; the main input admits every item.</summary>
        public override bool HasInput => false;

        /// <summary>Its main and component inputs are the incoming connections (upstream Next Elements).</summary>
        public override bool TakesConnections => true;

        // Configuration
        [Tooltip("Optional order of the incoming connections. Connect every upstream element to the " +
                 "combiner through its Next Elements; element 0 of this list feeds the MAIN input and " +
                 "elements 1..N feed component inputs 0..N-1. Upstream elements missing from the list " +
                 "follow in hierarchy order. Connecting with the Connect tool appends to this list (unless it already names the " +
                 "element), so the first element connected is the main input.")]
        [SimuLeanHelp("The upstream elements by name, input 0 (the MAIN item) first, then the component inputs 1..K in the order of " +
                      "requirements. Connecting appends the new element unless this list, set before the connection, already places it (the first one connected is the main input); set it with simulean_set " +
                      "key=inputOrder value=Bodies,Wheels,Seats. An empty slot shows as (none); elements missing from it follow in " +
                      "hierarchy order.")]
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

        [Tooltip("Components per assembly, one number per component input: K numbers need K + 1 inputs (input 0 = the main " +
                 "item, input k needs requirements[k-1] items).")]
        [SimuLeanHelp("One number per COMPONENT input: K numbers need K + 1 connected inputs — input 0 is the main item (one per " +
                      "assembly), input k gives requirements[k-1] items. E.g. requirements = 4,1 with inputOrder = Bodies,Wheels,Seats: " +
                      "a body + 4 wheels + 1 seat. The main input has no entry here.")]
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

        [Tooltip("For MultiLabel: label / value records; a component is accepted when ANY matches")]
        public List<LabelValuePair> multiLabelPairs = new List<LabelValuePair>();

        [Header("Load height")]
        [Tooltip("Label the assembled item gets as its height: its own plus the layers of components on it (empty = off). " +
                 "Set only when every component has a height (and, by footprint, a length and a width).")]
        public string heightLabel = "height";

        [Tooltip("Components per layer of the load, in arrival order. 0 = by footprint: the length / width labels of the main " +
                 "item and the components, laid out as the view does.")]
        [Min(0)] public int partsPerLayer = 0;

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
                 "On Work Point: stacked on the work point, on the main item — parts laid on the assembly bed. " +
                 "On Load: each one goes straight to its place in the main item's load (rows, then layers), so the load " +
                 "grows while it is completed, as on a palletizer (before the main item arrives they wait Behind).")]
        public ComponentLayout componentLayout = ComponentLayout.Behind;
        [Tooltip("Where each component input's parts wait, one per component input in input order (e.g. a pallet place " +
                 "beside the table for the boxes, a bin for the screws); they pile up on it. Empty / missing = the Component Layout.")]
        public List<Transform> componentEntries = new List<Transform>();
        [Tooltip("Attach: the components travel one after another from where they waited onto the main item during the " +
                 "process time (off = they jump onto it when the process starts).")]
        public bool animateAssembly = true;

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
            OnWorkPoint,  // stacked on the work point, on the main item
            OnLoad        // in its final place in the main item's load (Attach's packing); Behind until the main item is there
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
            int inputCount = theCombiner.GetInputsCount();
            for (int k = 0; k < assemblies.Count; k++)
            {
                var a = assemblies[k];
                Vector3 work = AssemblyPoint(k, assemblies.Count, spacing);
                ElementState state = a.State;
                // != null: the items of a restarted run are destroyed (ClearViews) before the new model is bound
                GameObject mainGo = a.Process.theItem?.vItem as GameObject;
                Vector3 mainSize = mainGo != null ? ItemVisuals.SizeOf(mainGo) : Vector3.zero;
                if (state == ElementState.RECEIVING && mainGo != null)
                    PlaceMain(mainGo, work);   // the main item on the work point
                // the components gathered so far wait at their entry (else behind the main item / on the work point), or
                // On Load: in their place in the main item's load
                if (arm != null && state == ElementState.RECEIVING)
                {
                    // a column arm's model (the palletizer): the components wait on its infeed, the arm carries them later
                    float behind = 0f;
                    for (int i = 0; i < inputCount; i++)
                        foreach (Item item in a.PartsOf(i))
                            if (item.vItem is GameObject go && go != null) arm.PlaceWaiting(go, transform, ref behind);
                }
                else if (componentLayout == ComponentLayout.OnLoad && state == ElementState.RECEIVING && mainGo != null)
                    LayGathered(a, mainGo, inputCount);
                else
                {
                    float stack = mainSize.y;
                    for (int i = 0; i < inputCount; i++)
                    {
                        float pile = 0f;
                        int j = 0;
                        foreach (Item item in a.PartsOf(i))
                            if (item.vItem is GameObject go && go != null) PlaceWaiting(go, i, inputCount, j++, work, mainSize, ref stack, ref pile);
                    }
                }
                if (state == ElementState.PROCESSING && serverAnimator == null)
                {
                    // default animation (smooth sim-time lerp, pause-aware)
                    var proc = a.Process;
                    if (proc.theItem != null && outItemPosition != null && mainGo != null)
                    {
                        float currentSimTime = UnitySimClock.Instance.SmoothSimTime;
                        float p = proc.lastDelay > 0
                            ? Mathf.Clamp01((currentSimTime - (float)proc.loadTime) / (float)proc.lastDelay)
                            : 1f;
                        mainGo.transform.position = Vector3.Lerp(work, outItemPosition.position, p);
                    }
                }
            }
            AdvanceMoves();
        }

        // assemblies side by side along the combiner's right axis (one assembly: on the work point)
        private Vector3 AssemblyPoint(int k, int count, float spacing) =>
            WorkPoint() + transform.right * ((k - (count - 1) * 0.5f) * spacing);

        private void PlaceMain(GameObject main, Vector3 work) =>
            main.transform.SetPositionAndRotation(work + transform.up * ItemVisuals.PivotHeight(main), transform.rotation);

        // a component waiting for its assembly: piled on its input's entry, else by the Component Layout (part j of the input)
        private void PlaceWaiting(GameObject go, int input, int inputCount, int j, Vector3 work, Vector3 mainSize,
                                  ref float stack, ref float pile)
        {
            Vector3 size = ItemVisuals.SizeOf(go);
            Transform entry = EntryOf(input);
            if (entry != null)
            {
                go.transform.SetPositionAndRotation(entry.position + entry.up * (pile + ItemVisuals.PivotHeight(go)), entry.rotation);
                pile += size.y * 1.02f;
                return;
            }
            Vector3 o = PartOffset(componentLayout, input, inputCount, j, mainSize, size, separation, stack);
            stack += size.y;
            go.transform.SetPositionAndRotation(
                work + transform.right * o.x + transform.up * (o.y + ItemVisuals.PivotHeight(go)) + transform.forward * o.z,
                transform.rotation);
        }

        // On Load: the components gathered so far in the places the assembled load gives them (AttachOnTop's packing of the
        // whole set: what the main item carried in, then each input's parts in arrival order; the parts still to come
        // count with the size of their input's last one, so a place does not move when the set is completed)
        private void LayGathered(Combiner.Assembly a, GameObject main, int inputCount)
        {
            var parts = new List<GameObject>();
            var sizes = new List<Vector3>();
            Transform m = main.transform;
            void Add(GameObject go)
            {
                go.transform.rotation = m.rotation;
                parts.Add(go);
                sizes.Add(SizeIn(go, m));
            }
            Item mainItem = a.Process.theItem;
            var carried = mainItem.GetSubItems();
            if (carried != null)
                foreach (Item sub in carried)
                    if (sub.vItem is GameObject go && go != null) Add(go);
            for (int i = 0; i < inputCount; i++)
            {
                var gathered = a.PartsOf(i);
                foreach (Item item in gathered)
                    if (item.vItem is GameObject go && go != null) Add(go);
                if (gathered.Count == 0 || !(gathered[gathered.Count - 1].vItem is GameObject last) || last == null) continue;
                Vector3 size = SizeIn(last, m);
                for (int r = gathered.Count; r < Required(mainItem, i); r++) { parts.Add(null); sizes.Add(size); }
            }
            LayOnLoad(main, parts, sizes, false);
        }

        // the components input i needs for main item: its requirement, or the main item's label (Update Requirements)
        private int Required(Item mainItem, int input)
        {
            if (updateRequirements && updateLabels != null && input < updateLabels.Length
                && mainItem.GetLabelValue(updateLabels[input]) is object v)
                try { return System.Convert.ToInt32(v); } catch (System.Exception) { }
            return requirements != null && input < requirements.Length ? requirements[input] : 0;
        }

        // the input each component came through (recorded on arrival: the engine moves them into the cycle before the view
        // sees the process start)
        private readonly Dictionary<Item, int> inputOf = new Dictionary<Item, int>();

        /// <summary>The entry where component input <paramref name="input"/>'s parts wait: its Component Entry, else the
        /// station model's parts place (<c>PartsSocket</c>: an assembly bench's parts shelf; behind the main
        /// item they stood inside the bench's back panel), else null (the layout).</summary>
        private Transform EntryOf(int input)
        {
            if (componentEntries != null && input < componentEntries.Count && componentEntries[input] != null) return componentEntries[input];
            if (!partsLooked) { partsLooked = true; partsSocket = StationModel.Position(this, PartsSocket); }
            return partsSocket;
        }
        private Transform partsSocket;
        private bool partsLooked;

        /// <summary>The station model's node where the parts wait (the library's assembly bench).</summary>
        public const string PartsSocket = "PartsSocket";

        // ── the components travelling onto the main item (view only) ────────

        private sealed class PartMove
        {
            public Transform Part, Main;
            public Vector3 FromLocal, ToLocal;
            public Quaternion FromRotLocal, ToRotLocal;
            public Quaternion FromRotWorld;   // how it waited (a column arm's infeed stays put while the main item moves)
            public double Start, Duration;
            public int Index, Count;
        }

        private readonly List<PartMove> moves = new List<PartMove>();

        /// <summary>Progress (0..1) of part <paramref name="k"/> of <paramref name="n"/> travelling onto the main item at sim
        /// time <paramref name="now"/>: the parts go one after another in the first 85 % of the process time.</summary>
        public static float MoveProgress(double now, double start, double duration, int k, int n)
        {
            if (duration <= 0 || n <= 0) return 1f;
            double span = duration * 0.85 / n;
            return Mathf.Clamp01((float)((now - (start + span * k)) / span));
        }

        /// <summary>Where a travelling part stands at progress <paramref name="p"/>: on the straight line from
        /// <paramref name="from"/> to <paramref name="to"/>, eased at both ends.</summary>
        public static Vector3 MovePose(Vector3 from, Vector3 to, float p)
        {
            float e = p * p * (3f - 2f * p);
            return Vector3.Lerp(from, to, e);
        }

        private void StartMoves(Item assembled, GameObject main, List<(Transform part, Vector3 pos, Quaternion rot)> from)
        {
            var clock = UnitySimClock.Instance;
            if (clock == null || from.Count == 0) return;
            double start = 0, duration = 0;
            foreach (var a in theCombiner.Assemblies)
                if (a.Process.GetItem() == assembled) { start = a.Process.loadTime; duration = a.Process.lastDelay; }
            if (duration <= 0) return;
            Transform m = main.transform;
            for (int k = 0; k < from.Count; k++)
            {
                var (part, pos, rot) = from[k];
                var mv = new PartMove
                {
                    Part = part, Main = m, ToLocal = part.localPosition, ToRotLocal = part.localRotation,
                    FromLocal = m.InverseTransformPoint(pos), FromRotLocal = Quaternion.Inverse(m.rotation) * rot, FromRotWorld = rot,
                    Start = start, Duration = duration, Index = k, Count = from.Count,
                };
                moves.Add(mv);
                Place(mv, 0f);
            }
        }

        private void AdvanceMoves()
        {
            if (moves.Count == 0) return;
            var clock = UnitySimClock.Instance;
            double now = clock != null ? clock.SmoothSimTime : double.MaxValue;
            for (int i = moves.Count - 1; i >= 0; i--)
            {
                var mv = moves[i];
                if (mv.Part == null || mv.Main == null) { moves.RemoveAt(i); continue; }
                float p = MoveProgress(now, mv.Start, mv.Duration, mv.Index, mv.Count);
                if (p >= 1f)
                {
                    mv.Part.localPosition = mv.ToLocal;
                    mv.Part.localRotation = mv.ToRotLocal;
                    moves.RemoveAt(i);
                }
                else Place(mv, p);
            }
        }

        // the station model's column arm (the library palletizer), found when the run binds; null = none
        private ColumnArm arm;
        // where a case of a column arm waits: its slot on the infeed (its index among those not yet carried off), moved up
        // as the cases ahead of it leave
        private Vector3 InfeedOf(PartMove mv)
        {
            var clock = UnitySimClock.Instance;
            double now = clock != null ? clock.SmoothSimTime : 0;
            var ahead = new List<float>();
            foreach (var other in moves)
                if (other.Main == mv.Main && other.Index < mv.Index)
                    ahead.Add(MoveProgress(now, other.Start, other.Duration, other.Index, other.Count));
            int gone = mv.Index - ahead.Count;   // the ones ahead already laid (their moves are over)
            float slot = ColumnArm.Advance(mv.Index - gone, ahead);
            float length = Mathf.Abs((Quaternion.Inverse(transform.rotation) * ItemVisuals.SizeOf(mv.Part.gameObject)).x);
            return arm.InfeedPoint(mv.Part.gameObject, transform, slot * (length + ColumnArm.InfeedGap));
        }

        // the parts travelling onto main reach their places now
        private void FinishMoves(GameObject main)
        {
            if (main == null) return;
            for (int i = moves.Count - 1; i >= 0; i--)
            {
                var mv = moves[i];
                if (mv.Main != main.transform) continue;
                if (mv.Part != null)
                {
                    mv.Part.localPosition = mv.ToLocal;
                    mv.Part.localRotation = mv.ToRotLocal;
                }
                moves.RemoveAt(i);
            }
        }

        private void Place(PartMove mv, float p)
        {
            Transform m = mv.Main;
            Quaternion fromRot = m.rotation * mv.FromRotLocal, toRot = m.rotation * mv.ToRotLocal;
            if (arm != null)
            {
                // the case being carried now drives the arm, picked at the pick point; the others wait on the infeed
                Vector3 pickAt = arm.InfeedPoint(mv.Part.gameObject, transform, 0f);
                if (p <= 0f) { mv.Part.position = InfeedOf(mv); mv.Part.rotation = mv.FromRotWorld; return; }
                mv.Part.position = arm.Carry(mv.Part.gameObject, pickAt, m.TransformPoint(mv.ToLocal), p, p < 1f, out float turn);
                mv.Part.rotation = Quaternion.Slerp(mv.FromRotWorld, toRot, turn);
                return;
            }
            mv.Part.position = MovePose(m.TransformPoint(mv.FromLocal), m.TransformPoint(mv.ToLocal), p);
            mv.Part.rotation = Quaternion.Slerp(fromRot, toRot, p);
        }

        /// <summary>Where a waiting component's bottom stands relative to its assembly's work point, in the combiner's
        /// axes (x right, y up, z forward). Behind (and On Load while its main item is not there): column
        /// <paramref name="input"/> of <paramref name="inputCount"/> behind the main item, <paramref name="layer"/> items high. On Work Point: centred on the work point, at
        /// <paramref name="stackHeight"/> (the main item and the parts already there).</summary>
        public static Vector3 PartOffset(ComponentLayout layout, int input, int inputCount, int layer, Vector3 mainSize,
                                           Vector3 size, float separation, float stackHeight)
        {
            if (layout == ComponentLayout.OnWorkPoint) return new Vector3(0f, stackHeight, 0f);
            float step = separation > 0f ? separation : Mathf.Max(size.x, size.z) * 1.15f;
            float x = (input - (inputCount - 1) * 0.5f) * step;
            float back = (mainSize.z + size.z) * 0.5f + step * 0.15f;   // behind the main item (+Z: the front is −Z)
            return new Vector3(x, size.y * 1.05f * layer, back);
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
        /// first (those that really connect here), then the rest in hierarchy order
        /// (<see cref="ExtractionRules.InputOrder{T}"/>).
        /// </summary>
        public List<SElement> ResolveInputOrder()
        {
            var upstream = new List<SElement>();
            foreach (SElement se in UnityCompat.FindAll<SElement>(true))
                if (se != null && se != this && se.nextElements != null && se.nextElements.Contains(this))
                    upstream.Add(se);
            // destroyed entries dropped here (Unity's null), the rule compares references
            var listed = inputOrder?.FindAll(se => se != null);
            return ExtractionRules.InputOrder(upstream, listed, se => HierarchyKey(se.transform));
        }

        /// <summary>
        /// A connection from <paramref name="upstream"/> was just made (the editor's Connect tool, <c>simulean_connect</c>):
        /// it takes the next input, as in the engine (connection order: the first one connected is the main input). The
        /// inputs already connected keep their place (<see cref="inputOrder"/> is fixed with them).
        /// </summary>
        public void NoteConnected(SElement upstream)
        {
            // an element the list placed before it was connected (inputOrder set, then the connections, in one
            // simulean_apply) keeps its place, and so do the others still waiting for theirs: the connection order replaced
            // the list the user had given
            var listed = inputOrder?.FindAll(se => se != null) ?? new List<SElement>();
            if (placedBeforeConnected.Remove(upstream)) { inputOrder = listed; return; }
            var order = ResolveInputOrder();
            if (!order.Remove(upstream)) return;
            if (placedBeforeConnected.Count > 0)
            {
                // the list's connected and still-awaited entries in its order, then the other connections
                var connected = new HashSet<SElement>(order);
                var merged = listed.FindAll(se => connected.Contains(se) || placedBeforeConnected.Contains(se));
                foreach (var se in order) if (!merged.Contains(se)) merged.Add(se);
                order = merged;
            }
            order.Add(upstream);
            inputOrder = order;
        }

        // the listed elements not connected yet when inputOrder was set (by simulean_set / simulean_apply); not serialized:
        // what matters is the connections that follow in the same edit
        [System.NonSerialized] private readonly HashSet<SElement> placedBeforeConnected = new HashSet<SElement>();

        /// <summary>inputOrder was just set: the elements it names that are not connected yet keep their place when they are
        /// (<see cref="NoteConnected"/>).</summary>
        public void NoteInputOrderSet()
        {
            placedBeforeConnected.Clear();
            var connected = new HashSet<SElement>();
            foreach (SElement se in UnityCompat.FindAll<SElement>(true))
                if (se != null && se != this && se.nextElements != null && se.nextElements.Contains(this)) connected.Add(se);
            if (inputOrder != null) foreach (var se in inputOrder) if (se != null && !connected.Contains(se)) placedBeforeConnected.Add(se);
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
            => ExtractionRules.InputSlots(ResolveInputOrder(), (requirements?.Length ?? 0) + 1, i => ListInputAt(i) != null);

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
            arm = ColumnArm.Find(this);
            // the assembled item may leave in the same frame its process ends (a fast run): its parts are in place first,
            // or the next station measures it with a part still on the way (a palletizer then packed it as wider)
            theCombiner.ProcessEnded += (_, item) => FinishMoves(item.vItem as GameObject);
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
            // components arriving: placed by LateUpdate while the combiner gathers; their input is remembered
            if (theCombiner?.Assemblies == null) return;
            foreach (var a in theCombiner.Assemblies)
                for (int i = 0; i < theCombiner.GetInputsCount(); i++)
                    foreach (Item p in a.PartsOf(i))
                        if (p == vItem) { inputOf[vItem] = i; return; }
        }

        // The process starts: the main item on its work point and the components where they wait (an assembly can start
        // in the instant its main item arrives, before any frame drew it gathering).
        private void LayOutForStart(Item assembled, GameObject main, List<Item> subs)
        {
            var assemblies = theCombiner.Assemblies;
            int k = 0;
            for (int n = 0; n < assemblies.Count; n++) if (assemblies[n].Process.GetItem() == assembled) k = n;
            Vector3 work = AssemblyPoint(k, assemblies.Count, AssemblySpacing());
            PlaceMain(main, work);
            if (subs == null) return;
            Vector3 mainSize = ItemVisuals.SizeOf(main);
            int inputCount = theCombiner.GetInputsCount();
            var piles = new float[inputCount];
            var counts = new int[inputCount];
            float stack = mainSize.y, behind = 0f;
            foreach (Item sub in subs)
            {
                if (!inputOf.TryGetValue(sub, out int i) || i >= inputCount) continue;
                inputOf.Remove(sub);
                if (!(sub.vItem is GameObject go) || go == null) continue;
                if (arm != null) arm.PlaceWaiting(go, transform, ref behind);
                else PlaceWaiting(go, i, inputCount, counts[i]++, work, mainSize, ref stack, ref piles[i]);
            }
        }

        /// <summary>Processing starts: the components join the main item (how they look depends on <see cref="visualMode"/>).</summary>
        void IVElement.UnloadItem(Item vItem)
        {
            var main = vItem.vItem as GameObject;
            var subs = vItem.GetSubItems();
            VisualMode mode = visualMode == VisualMode.NewItem && combinedItemPrefab == null ? VisualMode.Attach : visualMode;

            if (mode == VisualMode.Attach && main != null)
            {
                // where the components waited: they travel from there during the process (animateAssembly; On Load they
                // are already in their places)
                bool travel = animateAssembly && (componentLayout != ComponentLayout.OnLoad || arm != null);   // a column arm carries them
                if (travel) LayOutForStart(vItem, main, subs);
                var from = new List<(Transform, Vector3, Quaternion)>();
                if (travel && subs != null)
                    foreach (Item sub in subs)
                        if (sub.vItem is GameObject sg && sg != null) from.Add((sg.transform, sg.transform.position, sg.transform.rotation));
                AttachOnTop(main, subs);
                StartMoves(vItem, main, from);
                if (subs != null) foreach (Item sub in subs) inputOf.Remove(sub);
                return;
            }

            // MainOnly / NewItem: the components' visuals are consumed
            if (subs != null)
                foreach (Item sub in subs)
                {
                    inputOf.Remove(sub);
                    if (sub.vItem is GameObject subGItem && subGItem != null)
                    {
                        Destroy(subGItem);
                        sub.vItem = null;
                    }
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

        // the components as children of the main item, packed on its top (rows, then layers; each part its own size); a
        // share of a walled container's outer footprint that its inside floor offers (the library tote: 0.356 / 0.404)
        private const float ContainerInside = (float)Combiner.ContainerInside;

        // container model (pallet, tote, roll cage) marks where its load rests with a LoadSocket node; the parts are turned
        // a quarter when that packs them lower (cartons lengthwise in a tote)
        private static void AttachOnTop(GameObject main, List<Item> subs)
        {
            if (subs == null || subs.Count == 0) return;
            Transform m = main.transform;
            var parts = new List<GameObject>();
            var sizes = new List<Vector3>();
            foreach (Item sub in subs)
            {
                if (!(sub.vItem is GameObject go) || go == null) continue;
                go.transform.rotation = m.rotation;
                parts.Add(go);
                sizes.Add(SizeIn(go, m));
            }
            LayOnLoad(main, parts, sizes, true);
        }

        /// <summary>Lays <paramref name="parts"/> (turned as the main item, of <paramref name="sizes"/> in its axes) in
        /// their places in <paramref name="main"/>'s load: packed on its top (<see cref="PackOnTop"/>), inside a walled
        /// container, turned a quarter when that packs them lower (or as low and further inside). A null part is a place kept for a part still to come.
        /// <paramref name="attach"/>: the parts become children of the main item.</summary>
        public static void LayOnLoad(GameObject main, IList<GameObject> parts, List<Vector3> sizes, bool attach)
        {
            Transform m = main.transform;
            Vector3 mainSize = SizeIn(main, m);
            Transform socket = ItemVisuals.LoadSocket(main);
            Vector3 top = socket != null ? socket.position : m.position + m.up * ItemVisuals.TopAbovePivot(main);
            // a socket well below the model's top is the floor of a container with walls (tote, roll cage): pack inside
            // them, not on the outer footprint (a pallet's socket is its deck, the top)
            if (socket != null && ItemVisuals.TopAbovePivot(main) - Vector3.Dot(socket.position - m.position, m.up) > 0.05f)
                mainSize = new Vector3(mainSize.x * ContainerInside, mainSize.y, mainSize.z * ContainerInside);
            var at = PackOnTop(mainSize, sizes);
            var turnedSizes = sizes.ConvertAll(s => new Vector3(s.z, s.y, s.x));
            var turnedAt = PackOnTop(mainSize, turnedSizes);
            // turned when that packs them lower, or as low and further inside the footprint (a carton lengthwise in a tote)
            float loadTop = LoadTop(at, sizes), turnedTop = LoadTop(turnedAt, turnedSizes);
            bool turn = turnedTop < loadTop - 1e-4f
                        || (turnedTop < loadTop + 1e-4f && Overhang(turnedAt, turnedSizes, mainSize) < Overhang(at, sizes, mainSize) - 1e-4f);
            if (turn) at = turnedAt;
            for (int k = 0; k < parts.Count; k++)
            {
                GameObject go = parts[k];
                if (go == null) continue;
                go.transform.rotation = turn ? m.rotation * Quaternion.Euler(0f, 90f, 0f) : m.rotation;
                go.transform.position = top + m.right * at[k].x + m.forward * at[k].z + m.up * (at[k].y + ItemVisuals.PivotHeight(go));
                if (attach) go.transform.SetParent(m, true);
            }
        }

        // the height of a packed load above the main item's top
        private static float LoadTop(List<Vector3> at, List<Vector3> sizes)
        {
            float top = 0f;
            for (int k = 0; k < at.Count; k++) top = Mathf.Max(top, at[k].y + sizes[k].y);
            return top;
        }

        // how far a packed load reaches beyond the main item's top (or a container's inside), the most of any part
        private static float Overhang(List<Vector3> at, List<Vector3> sizes, Vector3 mainSize)
        {
            float over = 0f;
            for (int k = 0; k < at.Count; k++)
                over = Mathf.Max(over, Mathf.Max(Mathf.Abs(at[k].x) + sizes[k].x * 0.5f - mainSize.x * 0.5f,
                                                 Mathf.Abs(at[k].z) + sizes[k].z * 0.5f - mainSize.z * 0.5f));
            return over;
        }

        // an item's size along the axes of frame (its renderers' world bounds turned into the frame)
        private static Vector3 SizeIn(GameObject go, Transform frame)
        {
            Vector3 s = Quaternion.Inverse(frame.rotation) * ItemVisuals.SizeOf(go);
            return new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        }

        /// <summary>Where parts of <paramref name="sizes"/> rest on a main item of <paramref name="mainSize"/>: the bottom
        /// centre of each, from the centre of its top (x right, y up, z forward) — the engine's <see cref="TopPacking"/>, the
        /// packing the model's load height (<see cref="Combiner.LoadHeight"/>) counts with.</summary>
        public static List<Vector3> PackOnTop(Vector3 mainSize, IList<Vector3> sizes)
        {
            var parts = new List<(double x, double y, double z)>(sizes.Count);
            foreach (Vector3 s in sizes) parts.Add((s.x, s.y, s.z));
            var places = TopPacking.Places(mainSize.x, mainSize.z, parts);
            var at = new List<Vector3>(places.Length);
            foreach (var p in places) at.Add(new Vector3((float)p.x, (float)p.y, (float)p.z));
            return at;
        }

        public override void ClearViews()
        {
            workLocal = null;
            moves.Clear();
            inputOf.Clear();
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
