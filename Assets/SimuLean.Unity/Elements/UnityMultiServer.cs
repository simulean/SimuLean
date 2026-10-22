using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Unity Component for MultiServer (infinite servers) Element.
    /// Supports input validation through configurable InputStrategy.
    /// </summary>
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Processors, "Multi-Server", Description = "Workstation with N parallel slots, per-slot statistics, optional operators", Prefab = "Multiserver.prefab", Order = 0)]
    public class UnityMultiServer : SElement, IVElement, IProcessResourceUser
    {
        IRandomSampler[] cycleTime;
        MultiServer theWorkstation;

        // where items are shown: one slot per server, side by side (in = start, out = end of the processing path)
        [Tooltip("Centre of the slots (where processing starts). Empty = the top of the element's footprint.")]
        public Transform inItemPosition;
        [Tooltip("Optional: each item moves from its slot towards here (same slot offset) while it is processed.")]
        public Transform outItemPosition;
        [Tooltip("Distance between the slots. 0 = automatic, from the size of the items.")]
        public float separation = 0f;

        // ── Delay Strategy ────────────────────────────────────────────
        [Tooltip("Delay strategy configuration - supports multiple distributions and label-based expressions")]
        public RandomProcessConfig delayStrategy = new RandomProcessConfig();

        [Tooltip("Number of servers (capacity)")]
        public int capacity = 1;

        [Tooltip("The look of the items it processes: an instance of this prefab replaces the item's visual when its " +
                 "processing starts — e.g. a plate kit becomes a panel. Empty = the item keeps its look.")]
        public GameObject outputItemPrefab;

        [Tooltip("How the held items are offered downstream. FIFO: only the first (a refused first item holds the rest back). " +
                 "Any Accepted: each item, oldest first, until a receiver accepts one — the receiver chooses with its input " +
                 "strategy (e.g. a Combiner's pull mode: the sub-block of its own block).")]
        public SimuLean.ReleaseMode releaseMode = SimuLean.ReleaseMode.FIFO;
        [Tooltip("Any Accepted: items offered per attempt (0 = every held item).")]
        [Min(0)] public int releaseScan = 0;




        // optional animator: "WorkInProgress" while a slot processes
        public Animator serverAnimator;

        // the items here at the last frame: a new one comes in through the entry (a box's door), one gone leaves by it
        private readonly HashSet<Item> present = new HashSet<Item>(), stillHere = new HashSet<Item>();

        // every frame after the clock's Update (it was FixedUpdate: 50 Hz, items stepping at higher frame rates)
        void LateUpdate()
        {
            if (theWorkstation == null)
                return;
            var entry = StationModel.Position(this, StationModel.EntrySocket);
            stillHere.Clear();

            bool anyProcessing = false;
            foreach (ServerProcess sProcess in theWorkstation.workInProgress)
            {
                ElementState state = sProcess.GetState();
                if (state == ElementState.PROCESSING || state == ElementState.BLOCKED) anyProcessing = true;
                // != null: the items of a restarted run are destroyed (ClearViews) before the new model is bound
                if (!(sProcess?.theItem?.vItem is GameObject go) || go == null) continue;
                stillHere.Add(sProcess.theItem);
                if (entry != null && !present.Contains(sProcess.theItem))
                    go.GetComponent<PersonAnimator>()?.WalkVia(entry.position);   // in through the door, not the wall
                // a patient at the couch of a consultation model lies on it (the people clips)
                PersonAnimator.SetPose(go, inItemPosition != null && inItemPosition.name == "PatientSocket"
                                           ? PersonAnimator.Pose.Lie : PersonAnimator.Pose.Stand);
                Vector3 slot = SlotPosition(sProcess.Index, go);
                if (state == ElementState.PROCESSING && outItemPosition != null && serverAnimator == null)
                {
                    // default animation: from the slot towards the out position (smooth sim-time lerp, pause-aware)
                    float now = UnitySimClock.Instance.SmoothSimTime;
                    float p = sProcess.lastDelay > 0
                        ? Mathf.Clamp01((now - (float)sProcess.loadTime) / (float)sProcess.lastDelay)
                        : 1f;
                    go.transform.position = Vector3.Lerp(slot, slot + (outItemPosition.position - WorkPoint()), p);
                }
                else if (state == ElementState.BLOCKED && outItemPosition != null)
                    go.transform.position = slot + (outItemPosition.position - WorkPoint());
                else go.transform.position = slot;
            }

            // the ones gone: out through the door too (their walk starts from here)
            if (entry != null)
                foreach (var it in present)
                    if (!stillHere.Contains(it) && it.vItem is GameObject g && g != null)
                        g.GetComponent<PersonAnimator>()?.LeaveVia(entry.position);
            present.Clear();
            present.UnionWith(stillHere);

            if (serverAnimator != null)
            {
                bool simPaused = UnitySimClock.Instance != null && UnitySimClock.Instance.SimPaused;
                serverAnimator.speed = simPaused ? 0f : 1f;
                serverAnimator.SetBool("WorkInProgress", anyProcessing);
            }
        }

        /// <summary>Centre of the slots: <see cref="inItemPosition"/> when set, else the top of the footprint.</summary>
        private Vector3 WorkPoint()
        {
            if (inItemPosition != null && inItemPosition != transform) return inItemPosition.position;
            // the footprint measured once per run, in the element's axes (~15 µs per call, per item and frame)
            if (workLocal == null)
            {
                ItemVisuals.Footprint(this, out _, out Vector3 top);
                workLocal = transform.InverseTransformPoint(top);
            }
            return transform.TransformPoint(workLocal.Value);
        }
        private Vector3? workLocal;

        /// <summary>Where slot <paramref name="index"/>'s item stands (its bottom on the work point): the slots in a
        /// row along the element's X, centred, spaced by the item's size (or <see cref="separation"/>).</summary>
        private Vector3 SlotPosition(int index, GameObject item)
        {
            Vector3 size = ItemVisuals.SizeOf(item);
            float step = separation > 0f ? separation : Mathf.Max(size.x, size.z) * 1.15f;
            int n = Mathf.Max(1, capacity);
            return WorkPoint() + transform.right * ((index - (n - 1) * 0.5f) * step) + transform.up * ItemVisuals.PivotHeight(item);
        }

        // ------------------------------------------------------------
        // VElement (explicit): views only, nothing in headless runs
        // ------------------------------------------------------------

        protected override void OnBound(Element built)
        {
            theWorkstation = (MultiServer)built;
        }

        void IVElement.ReportState(string msg)
        {
        }

        object IVElement.GenerateItem(int type)
        {
            return null;
        }

        void IVElement.LoadItem(Item vItem)
        {
            // placed by LateUpdate in its server's slot
        }

        void IVElement.UnloadItem(Item vItem)
        {
            // processing starts: the item takes the station's output look (S4)
            if (outputItemPrefab != null) ItemVisuals.ReplaceLook(vItem, outputItemPrefab, outputItemPrefab.name + vItem.GetId());
        }

        /// <summary>
        /// Returns the mean actual service time observed during the last simulation run.
        /// Read by the Experimenter / Optimizer as an observation metric.
        /// </summary>
        public double GetMeanServiceTime() => theWorkstation?.GetMeanServiceTime() ?? 0.0;

        /// <summary>Mean per-slot fraction of time spent processing (see MultiServer.GetUtilization).</summary>
        public double GetUtilization() => theWorkstation?.GetUtilization() ?? 0.0;

        /// <summary>
        /// Returns the fraction of simulation time the server spent in the PROCESSING state
        /// (= empirical utilisation ρ). This is the correct metric for M/M/1 validation.
        /// </summary>
        public double GetProcessingRatio() =>
            theWorkstation?.GetStateRatio(SimuLean.ElementState.PROCESSING) ?? 0.0;

        public override void ClearViews()
        {
            workLocal = null;
            present.Clear();
            var items = theWorkstation.GetItems();
            foreach (Item it in items)
                Destroy((GameObject)it.vItem);

            if (serverAnimator != null)
            {
                serverAnimator.speed = 1f;
                serverAnimator.SetBool("WorkInProgress", false);
            }
        }
    }
}
