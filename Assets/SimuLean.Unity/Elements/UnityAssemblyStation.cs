using System;
using System.Collections.Generic;
using SimuLean;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>One SKU→qty material need of an assembly task.</summary>
    [Serializable]
    public class MaterialRow
    {
        [Tooltip("Material SKU (item type).")]
        public string sku;
        [Tooltip("Quantity of the material the task consumes.")]
        public int qty = 1;
    }

    /// <summary>Inspector definition of one assembly operation.</summary>
    [Serializable]
    public class AssemblyTaskConfig
    {
        [Tooltip("Unique task id (referenced by other tasks' predecessors).")]
        public string id;

        [Tooltip("Comma-separated ids of tasks that must finish before this one starts.")]
        public string predecessors = "";

        [Tooltip("Material needs (SKU → qty) drawn from the shared stock buffer.")]
        public List<MaterialRow> materials = new List<MaterialRow>();

        [Tooltip("Duration sampler for this task.")]
        public RandomProcessConfig duration = new RandomProcessConfig();

        [Tooltip("Tie-break among equally-ready tasks: higher runs first.")]
        public int priority = 0;

        [Tooltip("Resources of this task (empty = the station's Process requirements): e.g. a crane of another team and a " +
                 "fitter. The station's Process block gives the rest (default team, priority, preemption, release, order).")]
        public List<ResourceRequirementSpec> requirements = new List<ResourceRequirementSpec>();

        [Tooltip("This task's own request settings (off = the station's Process block): its priority, preemption, " +
                 "acquisition order and max wait.")]
        public TaskResourceSettings resourceSettings = new TaskResourceSettings();
    }

    /// <summary>An assembly task's own request settings (per task instead of one per station); off =
    /// the station's Process block. Exported as <c>tasks[i].processBy.*</c>.</summary>
    [Serializable]
    public class TaskResourceSettings
    {
        [Tooltip("Use these settings for this task instead of the station's Process block.")]
        public bool own;
        [Tooltip("Priority of the task's requests: an expression over the item and the station (empty = the station's).")]
        [SimuLean.Unity.SimuLeanExpression("priority")]
        public string priority = "";
        [Tooltip("When no member is free (None / Resume / Restart / Abort), as the station's Process block.")]
        public SimuLean.Resources.PreemptMode preemption = SimuLean.Resources.PreemptMode.None;
        [Tooltip("Requests of higher priority with a preemption mode may take this task's members.")]
        public bool preemptable = true;
        [Tooltip("With several requirements: All At Once or In Order.")]
        public SimuLean.Resources.AcquireOrder acquire = SimuLean.Resources.AcquireOrder.AllAtOnce;
        [Tooltip("The longest the task waits for its resources (a time or an expression; empty or 0 = no limit).")]
        [SimuLean.Unity.SimuLeanExpression("maxWait")]
        public string maxWait = "";
        [Tooltip("When the max wait is over: Keep Waiting or Give Up (a task cannot Reroute).")]
        public SimuLean.TimeoutAction onTimeout = SimuLean.TimeoutAction.KeepWaiting;
    }

    /// <summary>
    /// Unity Component for the <see cref="AssemblyStation"/> element: a
    /// workstation executing a precedence network of tasks per job, with up to
    /// <see cref="workerCount"/> tasks running concurrently and materials
    /// drawn by SKU from a shared stock buffer. Dispatch order is
    /// first-ready-first, tie-broken by priority then declaration order.
    /// </summary>
    [SimuLean.Unity.SimuLeanElement(SimuLean.Unity.SimuLeanModules.Assembly, "Assembly Station", Description = "Precedence network of tasks consuming materials by SKU", Prefab = "AssemblyStation.prefab", Order = 10)]
    public class UnityAssemblyStation : SElement, IVElement, IProcessResourceUser, ISetupUser
    {
        [Tooltip("Setup (changeover) per job, before its first task: when, how long, by whom (Setup section).")]
        public StationSetupConfig setup = new StationSetupConfig();
        public StationSetupConfig Setup => setup;


        [Header("Execution")]
        [Tooltip("Max tasks running concurrently (1 = strictly sequential).")]
        public int workerCount = 1;

        [Header("Materials")]
        [Tooltip("Upstream elements feeding material items into the stock port. " +
                 "Leave their own nextElements empty — this station wires them.")]
        public List<SElement> materialSources = new List<SElement>();

        [Tooltip("Max items held in the material buffer; -1 = unbounded.")]
        public int stockCapacity = -1;

        [Header("Tasks (precedence network)")]
        [Tooltip("The tasks of each job: id, predecessors, materials, duration, priority.")]
        public List<AssemblyTaskConfig> tasks = new List<AssemblyTaskConfig>();

        private AssemblyStation station;

        /// <summary>No station process team, but tasks whose requirements name their own teams: those tasks'
        /// uses take the Process block's priority, preemption, preemptable, acquisition order and max wait, so the
        /// inspector shows these settings and the extractor exports them.</summary>
        public bool TasksUseOwnTeams
        {
            get
            {
                if (processBy != null && processBy.IsUsed || tasks == null) return false;
                foreach (var t in tasks)
                    if (t?.requirements != null)
                        foreach (var r in t.requirements) if (r?.team != null) return true;
                return false;
            }
        }



        protected override void OnBound(Element built) => station = (AssemblyStation)built;

        // The item views this station positioned (its jobs / containers and stock): destroyed when the run is cleared,
        // as every element's (they stayed in the scene after Restart / Stop).
        private readonly List<GameObject> itemViews = new List<GameObject>();

        public override void ClearViews()
        {
            foreach (var go in itemViews) if (go != null) Destroy(go);
            itemViews.Clear();
        }
        // ── VElement (minimal visuals) ────────────────────────────────────────

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

        void IVElement.UnloadItem(Item theItem)
        {
            // Consumed materials live on as sub-items of the job; hide their visuals.
            if (theItem.GetSubItems() == null) return;
            foreach (var sub in theItem.GetSubItems())
            {
                var go = sub.vItem as GameObject;
                if (go != null)
                    go.SetActive(false);
            }
        }
    }
}
