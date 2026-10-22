using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// One assembly operation of an <see cref="AssemblyStation"/> job:
    /// identifier, precedence constraints, material needs and duration.
    /// </summary>
    public class AssemblyTaskSpec
    {
        /// <summary>Unique task id within the station.</summary>
        public string Id;

        /// <summary>Ids of tasks that must complete before this one may start.</summary>
        public List<string> Predecessors = new List<string>();

        /// <summary>Material needs: SKU (item type string) → quantity.</summary>
        public Dictionary<string, int> Materials = new Dictionary<string, int>();

        /// <summary>Duration sampler for this task.</summary>
        public IRandomSampler Duration;

        /// <summary>Tie-break weight: among equally-ready tasks, higher runs first.</summary>
        public int Priority = 0;
    }

    /// <summary>
    /// Assembly workstation executing a precedence network (a small CPM graph)
    /// of tasks per job. A job item arrives on the main input; materials arrive
    /// in a shared <see cref="SkuStockPort"/> and are matched by SKU. Up to
    /// <c>workerCount</c> tasks run concurrently. A task becomes READY at the
    /// first instant its predecessors are complete AND its materials are in
    /// stock; dispatch order is first-ready-first, tie-broken by descending
    /// <see cref="AssemblyTaskSpec.Priority"/> then declaration order.
    /// Consumed materials are attached to the job as sub-items; when every task
    /// has completed the job is sent onward.
    /// </summary>
    public class AssemblyStation : Element, SimuLean.Resources.IResourceUser
    {
        // ── task run = one scheduled Eventcs per executing task ─────────────
        private class TaskRun : IEvent
        {
            private readonly AssemblyStation station;
            internal readonly AssemblyTaskSpec Spec;
            internal double ReadyTime;
            internal long   ReadySeq;
            internal int    DeclIndex;
            internal SimuLean.Resources.ResourceJob ResJob;

            internal TaskRun(AssemblyStation station, AssemblyTaskSpec spec, int declIndex)
            {
                this.station = station;
                Spec         = spec;
                DeclIndex    = declIndex;
            }

            void IEvent.Execute() => station.TaskCompleted(this);
        }

        private readonly string name;
        private readonly List<AssemblyTaskSpec> specs;
        private readonly int workerCount;
        private readonly SkuStockPort stockPort;

        // per-job state
        private Item job;
        private bool jobBlocked;   // finished job waiting for the output (independent of stop masking)
        private readonly HashSet<string> completed   = new HashSet<string>();
        private readonly List<TaskRun> waiting       = new List<TaskRun>(); // preds or materials missing
        private readonly List<TaskRun> ready         = new List<TaskRun>(); // dispatchable
        private int running;              // tasks started or waiting for their operator
        private long readySeqCounter;

        /// <summary>Resources each task needs (one request per task); null = none. With
        /// <see cref="SimuLean.Resources.ProcessRelease.ItemLeaves"/> the last task's resources are kept until the job leaves.</summary>
        public SimuLean.Resources.StationResources Resources { get; set; }
        private SimuLean.Resources.ResourceJob lastTaskJob;   // the last task's resources (ItemLeaves), released in FinishJob

        /// <param name="specs">Task definitions; validated (unknown predecessors / cycles throw).</param>
        /// <param name="workerCount">Max concurrently running tasks (≥1).</param>
        /// <param name="stockCapacity">Material buffer size; negative = unbounded.</param>
        public AssemblyStation(
            string name,
            SimClock simClock,
            List<AssemblyTaskSpec> specs,
            int workerCount = 1,
            int stockCapacity = -1,
            InputStrategy stockStrategy = null,
            IVElement vElement = null)
            : base(name, simClock, vElement)
        {
            this.name        = name;
            this.specs       = specs ?? new List<AssemblyTaskSpec>();
            this.workerCount = Math.Max(1, workerCount);

            ValidateSpecs(this.specs);

            stockPort = new SkuStockPort(name + ".Materials", simClock, stockCapacity,
                                         OnMaterialArrived, stockStrategy, vElement);
        }

        /// <summary>Material input port — wire upstream SKU flows into this element.</summary>
        public SkuStockPort GetStockPort() => stockPort;

        /// <summary>Throws when a predecessor id is unknown or the precedence graph has a cycle.</summary>
        private static void ValidateSpecs(List<AssemblyTaskSpec> specs)
        {
            var ids = new HashSet<string>();
            foreach (var s in specs)
                if (!ids.Add(s.Id))
                    throw new ArgumentException($"AssemblyStation: duplicate task id '{s.Id}'.");

            foreach (var s in specs)
                foreach (var p in s.Predecessors)
                    if (!ids.Contains(p))
                        throw new ArgumentException(
                            $"AssemblyStation: task '{s.Id}' references unknown predecessor '{p}'.");

            // Kahn topological check for cycles.
            var indeg = new Dictionary<string, int>();
            foreach (var s in specs) indeg[s.Id] = s.Predecessors.Count;
            var queue = new Queue<string>();
            foreach (var kv in indeg) if (kv.Value == 0) queue.Enqueue(kv.Key);
            int visited = 0;
            while (queue.Count > 0)
            {
                string id = queue.Dequeue();
                visited++;
                foreach (var s in specs)
                    if (s.Predecessors.Contains(id) && --indeg[s.Id] == 0)
                        queue.Enqueue(s.Id);
            }
            if (visited != specs.Count)
                throw new ArgumentException("AssemblyStation: precedence graph contains a cycle.");
        }

        public override void Start()
        {
            base.Start();
            job = null;
            jobBlocked = false;
            lastTaskJob = null;
            completed.Clear();
            waiting.Clear();
            ready.Clear();
            running = 0;
            readySeqCounter = 0;
            stockPort.Start();
        }

        // ── main input (jobs) ────────────────────────────────────────────────

        public override bool CheckAvailability(Item theItem)
            => job == null;

        public override bool Receive(Item theItem)
        {
            if (job != null)
                return false;

            job = theItem;
            completed.Clear();
            waiting.Clear();
            ready.Clear();
            running = 0;

            SetState(ElementState.RECEIVING);
            vElement.LoadItem(theItem);

            for (int i = 0; i < specs.Count; i++)
                waiting.Add(new TaskRun(this, specs[i], i));

            stockPort.NotifyUpstream();

            // Degenerate configuration (no tasks): pass the job straight through
            // instead of deadlocking.
            if (specs.Count == 0)
            {
                CompleteJob();
                return true;
            }

            PromoteAndDispatch();
            return true;
        }

        // ── readiness / dispatch ─────────────────────────────────────────────

        private void OnMaterialArrived(Item theItem)
        {
            if (job != null)
                PromoteAndDispatch();
        }

        private bool PredsDone(AssemblyTaskSpec spec)
        {
            foreach (var p in spec.Predecessors)
                if (!completed.Contains(p))
                    return false;
            return true;
        }

        /// <summary>
        /// Moves waiting tasks whose predecessors AND materials are satisfied
        /// into the ready list (stamping first-ready time), then dispatches
        /// while workers are free.
        /// </summary>
        private void PromoteAndDispatch()
        {
            double now = simClock.GetSimulationTime();

            for (int i = waiting.Count - 1; i >= 0; i--)
            {
                TaskRun t = waiting[i];
                if (PredsDone(t.Spec) && stockPort.Has(t.Spec.Materials))
                {
                    t.ReadyTime = now;
                    t.ReadySeq  = readySeqCounter++;
                    ready.Add(t);
                    waiting.RemoveAt(i);
                }
            }

            while (running < workerCount && ready.Count > 0)
            {
                TaskRun next = PopBestReady();

                // Materials may have been consumed by a task dispatched above.
                if (!stockPort.Has(next.Spec.Materials))
                {
                    waiting.Add(next);   // back to waiting; re-promoted on next arrival
                    continue;
                }

                foreach (var kv in next.Spec.Materials)
                    foreach (var item in stockPort.Draw(kv.Key, kv.Value))
                        job.AddItem(item);

                running++;
                if (Resources != null)
                {
                    TaskRun task = next;
                    if (running == 1) SetState(ElementState.WAITING_FOR_RESOURCE);
                    task.ResJob = Resources.Acquire(this, job, () => StartTask(task));
                }
                else
                    StartTask(next);
            }
        }

        private void StartTask(TaskRun task)
        {
            SetState(ElementState.PROCESSING);
            IRandomSampler d = task.Spec.Duration;
            double duration = SimuLean.Expressions.Samplers.Sample(d, job, this);   // expressions: per job
            ScheduleWork(task, duration);
        }

        /// <summary>First-ready-first; ties: higher Priority, then declaration order.</summary>
        private TaskRun PopBestReady()
        {
            int best = 0;
            for (int i = 1; i < ready.Count; i++)
            {
                TaskRun a = ready[i], b = ready[best];
                bool better =
                    a.ReadyTime < b.ReadyTime ||
                    (a.ReadyTime == b.ReadyTime &&
                        (a.Spec.Priority > b.Spec.Priority ||
                        (a.Spec.Priority == b.Spec.Priority &&
                            (a.DeclIndex < b.DeclIndex ||
                            (a.DeclIndex == b.DeclIndex && a.ReadySeq < b.ReadySeq)))));
                if (better) best = i;
            }
            TaskRun chosen = ready[best];
            ready.RemoveAt(best);
            return chosen;
        }

        private void TaskCompleted(TaskRun t)
        {
            running--;
            completed.Add(t.Spec.Id);

            Resources?.ProcessEnded(t.ResJob);   // EndOfProcess: released now
            if (completed.Count == specs.Count && running == 0)
            {
                lastTaskJob = t.ResJob;                    // ItemLeaves: kept until the job leaves
                t.ResJob = null;
                CompleteJob();
                return;
            }

            Resources?.ItemLeft(t.ResJob);             // tasks before the last one never keep their resources
            t.ResJob = null;
            PromoteAndDispatch();
        }

        // ── job completion ───────────────────────────────────────────────────

        private void CompleteJob()
        {
            RaiseProcessEnded(job);
            vElement.UnloadItem(job);

            if (SendOut(job))
                FinishJob();
            else
            {
                jobBlocked = true;
                SetState(ElementState.BLOCKED);
            }
        }

        private void FinishJob()
        {
            job = null;
            jobBlocked = false;
            Resources?.ItemLeft(lastTaskJob);
            lastTaskJob = null;
            SetState(ElementState.IDLE);
            vElement.ReportState("Exit");

            NotifyInput();
            stockPort.NotifyUpstream();
        }

        public override bool Unblock()
        {
            // Own flag, not CurrentState: during a stop the visible state is the stop's,
            // even when the finished job is waiting for the output.
            if (job == null || !jobBlocked)
                return false;

            if (SendOut(job))
            {
                FinishJob();
                return true;
            }
            return false;
        }

        // ── Element contract ─────────────────────────────────────────────────

        public override int GetQueueLength()
            => stockPort.GetQueueLength() + (job != null ? 1 : 0);

        public override int GetFreeCapacity() => job == null ? 1 : 0;

        /// <summary>Completed / total task counts for dashboards.</summary>
        public int CompletedTaskCount => completed.Count;
        public int TotalTaskCount     => specs.Count;
        public int RunningTaskCount   => running;
    }
}
