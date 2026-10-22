using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// A station that works on one job at a time and gathers what the job needs before processing it (Combiner:
    /// components; PickingStation: picked stock). One cycle: the job item arrives (<c>RECEIVING</c>,
    /// <see cref="OnJobArrived"/>); the subclass gathers and calls <see cref="StartJob"/>; the resources are acquired
    /// (<c>WAITING_FOR_RESOURCE</c>, optional <see cref="Resources"/>); it processes for the sampled delay
    /// (<c>PROCESSING</c>, <see cref="OnProcessingStarted"/>); the job is sent out — or waits (<c>BLOCKED</c>) until
    /// <see cref="Unblock"/> sends it; then the slot is freed and the resources released (<see cref="OnJobLeaving"/>,
    /// <see cref="OnJobLeft"/>). The element's state is the slot's, so utilisation is the time share PROCESSING.
    /// </summary>
    public abstract class SingleJobStation : Element, IWorkStation, SimuLean.Resources.IResourceUser
    {
        private readonly IRandomSampler delay;
        private ServerProcess process;
        private double totalServiceTime;
        private int completedJobs;

        /// <summary>Resources the station needs for each job; null = none.</summary>
        public SimuLean.Resources.StationResources Resources { get; set; }

        /// <summary>The job slot (Unity views follow its item); null before <see cref="Start"/>.</summary>
        public ServerProcess ActiveProcess => process;

        /// <summary>The job being gathered / processed / held, or null.</summary>
        protected Item CurrentJob => process != null && process.GetState() != ElementState.IDLE ? process.GetItem() : null;

        protected bool IsState(ElementState state) => process != null && process.GetState() == state;

        protected SingleJobStation(string name, SimClock simClock, IRandomSampler delay, IVElement vElement)
            : base(name, simClock, vElement)
        {
            this.delay = delay;
        }

        public override void Start()
        {
            base.Start();
            process = new ServerProcess(this, delay, simClock);
            process.SetState(ElementState.IDLE);
            totalServiceTime = 0;
            completedJobs = 0;
        }

        public override void ResetStatistics()
        {
            base.ResetStatistics();
            totalServiceTime = 0;
            completedJobs = 0;
        }

        /// <summary>Time share spent processing (one slot).</summary>
        public double GetUtilization() => GetStateRatio(ElementState.PROCESSING);

        /// <summary>Mean sampled processing time of the completed jobs.</summary>
        public double GetMeanServiceTime() => completedJobs > 0 ? totalServiceTime / completedJobs : 0.0;

        /// <summary>The job item (with its sub-items), if any — for views to clear.</summary>
        public IEnumerable<Item> GetItems()
        {
            if (CurrentJob != null) yield return CurrentJob;
        }

        // ── the cycle ─────────────────────────────────────────────────────

        public override bool CheckAvailability(Item theItem) => IsState(ElementState.IDLE);   // admission: the link (CanAccept)

        public override bool Receive(Item theItem)
        {
            if (!IsState(ElementState.IDLE)) return false;
            process.SetState(ElementState.RECEIVING);
            process.SetItem(theItem);
            SetState(ElementState.RECEIVING);
            OnJobArrived(theItem);
            return true;
        }

        /// <summary>The job arrived (state RECEIVING): start gathering; call <see cref="StartJob"/> when complete.</summary>
        protected abstract void OnJobArrived(Item job);

        /// <summary>Everything is gathered: acquire the resources (if any), then process.</summary>
        protected void StartJob()
        {
            if (Resources != null)
            {
                process.SetState(ElementState.WAITING_FOR_RESOURCE);
                SetState(ElementState.WAITING_FOR_RESOURCE);
                var p = process;
                p.job = Resources.Acquire(this, p.GetItem(), () => ((IWorkStation)this).StartProcessing(p));
            }
            else ((IWorkStation)this).StartProcessing(process);
        }

        void IWorkStation.StartProcessing(ServerProcess p)
        {
            p.SetState(ElementState.PROCESSING);
            SetState(ElementState.PROCESSING);
            double time = SimuLean.Expressions.Samplers.Sample(delay, p.GetItem(), this);
            p.loadTime = simClock.GetSimulationTime();
            p.lastDelay = time;
            vElement.UnloadItem(p.GetItem());
            OnProcessingStarted(p.GetItem());
            ScheduleWork(p, time);
        }

        /// <summary>Processing starts (after the view unloaded the job).</summary>
        protected virtual void OnProcessingStarted(Item job) { }

        void IWorkStation.CompleteServerProcess(ServerProcess p)
        {
            totalServiceTime += p.lastDelay;
            completedJobs++;
            RaiseProcessEnded(p.GetItem());
            Resources?.ProcessEnded(p.job);   // released now, or kept (HELD) until the job leaves
            if (SendOut(p.GetItem())) JobLeft();
            else
            {
                // Output blocked: the resources stay held until Unblock() sends the job.
                p.SetState(ElementState.BLOCKED);
                SetState(ElementState.BLOCKED);
            }
        }

        public override bool Unblock()
        {
            if (!IsState(ElementState.BLOCKED) || !SendOut(process.GetItem())) return false;
            JobLeft();
            return true;
        }

        private void JobLeft()
        {
            OnJobLeaving();
            process.SetState(ElementState.IDLE);
            SetState(ElementState.IDLE);
            Resources?.ItemLeft(process.job);
            process.job = null;
            vElement.ReportState("Exit");
            NotifyInput();
            OnJobLeft();
        }

        /// <summary>The job was sent; the slot is not free yet.</summary>
        protected virtual void OnJobLeaving() { }

        /// <summary>The slot is free and the main input notified.</summary>
        protected virtual void OnJobLeft() { }

        public override int GetFreeCapacity() => IsState(ElementState.IDLE) ? 1 : 0;

        /// <summary>Progress views: shift the running job's load time by the part of the pause it spent loaded (see
        /// <see cref="MultiServer"/>).</summary>
        protected override void OnResumed(StopToken token)
        {
            if (!ResumedWork(token) || !IsState(ElementState.PROCESSING)) return;
            process.loadTime += PauseShiftFor(process.loadTime);
        }

        string IWorkStation.GetName() => GetName();
    }
}
