using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// A station that works on one job at a time and gathers what the job needs before processing it (Combiner:
    /// components; PickingStation: picked stock). One cycle: the job item arrives (<c>RECEIVING</c>,
    /// <see cref="OnJobArrived"/>); the subclass gathers and calls <see cref="StartJob"/>; the station sets up if its
    /// <see cref="Setup"/> says so (<c>SETUP</c>, its crew first); the resources are acquired
    /// (<c>WAITING_FOR_RESOURCE</c>, optional <see cref="Resources"/>); it processes for the sampled delay
    /// (<c>PROCESSING</c>, <see cref="OnProcessingStarted"/>); the job is sent out — or waits (<c>BLOCKED</c>) until
    /// <see cref="Unblock"/> sends it; then the slot is freed and the resources released (<see cref="OnJobLeaving"/>,
    /// <see cref="OnJobLeft"/>). The element's state is the slot's, so utilisation is the time share PROCESSING.
    /// </summary>
    public abstract class SingleJobStation : Element, IWorkStation, SimuLean.Resources.IResourceUser, SimuLean.Resources.IInterruptibleStation,
                                             ISetupStation, ISetupHost
    {
        private readonly IRandomSampler delay;
        private ServerProcess process;
        private double totalServiceTime;
        private int completedJobs;

        /// <summary>Resources the station needs for each job; null = none.</summary>
        public SimuLean.Resources.StationResources Resources { get; set; }

        /// <summary>Its setup before each job's process (null = none).</summary>
        public StationSetup Setup { get; set; }

        /// <summary>The station is free and set up for <paramref name="item"/> (its last job had the item's value).</summary>
        public bool IsSetUpFor(Item item) => Setup != null && IsState(ElementState.IDLE) && Setup.Matches(process, item);

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
            waitingExit = false;
            ForgetExitReservations();
            totalServiceTime = 0;
            completedJobs = 0;
            Setup?.ResetStatistics();
        }

        public override void ResetStatistics()
        {
            base.ResetStatistics();
            totalServiceTime = 0;
            completedJobs = 0;
            Setup?.ResetStatistics();
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

        public override bool CheckAvailability(Item theItem)
        {
            return IsState(ElementState.IDLE);   // admission: the link (CanAccept)
        }

        private bool waitingExit;   // the job waits for a place downstream (BLOCKED before its process)

        public override bool Receive(Item theItem)
        {
            if (!IsState(ElementState.IDLE)) return false;
            process.SetState(ElementState.RECEIVING);
            process.SetItem(theItem);
            process.job = null;
            SetState(ElementState.RECEIVING);
            OnJobArrived(theItem);
            return true;
        }

        /// <summary>The job arrived (state RECEIVING): start gathering; call <see cref="StartJob"/> when complete.</summary>
        protected abstract void OnJobArrived(Item job);

        /// <summary>Everything is gathered: set up (if needed), acquire the resources (if any), then process.</summary>
        protected void StartJob()
        {
            if (!TryReserveExit(process.GetItem()))
            {
                // no place downstream can be promised: the job waits (BLOCKED) until one can
                waitingExit = true;
                process.SetState(ElementState.BLOCKED);
                SetState(ElementState.BLOCKED);
                return;
            }
            waitingExit = false;
            bool setup = Setup != null && Setup.Needed(process, process.GetItem());
            if (setup)
            {
                Setup.Begin(this, process, Resources, this);
                return;
            }
            BeginProcess(process);
        }

        // The job's process: its resources first (if any), then the process.
        private void BeginProcess(ServerProcess p)
        {
            if (Resources != null)
            {
                p.SetState(ElementState.WAITING_FOR_RESOURCE);
                SetState(ElementState.WAITING_FOR_RESOURCE);
                p.job = Resources.Acquire(this, p.GetItem(), () => ((IWorkStation)this).StartProcessing(p));
            }
            else ((IWorkStation)this).StartProcessing(p);
        }

        void ISetupHost.SetupState(ServerProcess slot, ElementState state)
        {
            slot.SetState(state);
            SetState(state);
        }

        void ISetupHost.SetupEnded(ServerProcess slot, bool keptResources)
        {
            if (keptResources) ((IWorkStation)this).StartProcessing(slot);   // the setup's resources stay for the process
            else BeginProcess(slot);
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
            var work = ScheduleWork(p, time);
            Resources?.ProcessStarted(p, work);   // its resources may be taken while it works
        }

        /// <summary>The job lost a resource: the station waits for it, its process (or setup) paused; then it goes on.</summary>
        void SimuLean.Resources.IInterruptibleStation.JobPaused(SimuLean.Resources.ResourceJob job, bool paused, bool restart)
        {
            var p = job.Slot;
            if (p == null) return;
            var state = paused ? ElementState.WAITING_FOR_RESOURCE : p.InSetup ? ElementState.SETUP : ElementState.PROCESSING;
            if (!paused)
            {
                if (p.InSetup) { if (restart) p.SetupStart = simClock.GetSimulationTime(); else p.SetupStart += job.LastPause; }
                else if (restart) p.loadTime = simClock.GetSimulationTime();
                else p.loadTime += job.LastPause;
            }
            p.SetState(state);
            SetState(state);
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
            if (waitingExit)
            {
                StartJob();   // try to reserve the place again
                return !waitingExit;
            }
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

        /// <summary>Progress views: shift the running job's load time (or the running setup's start) by the part of the
        /// pause it spent loaded (see <see cref="MultiServer"/>).</summary>
        protected override void OnResumed(StopToken token)
        {
            if (!ResumedWork(token)) return;
            if (IsState(ElementState.PROCESSING)) process.loadTime += PauseShiftFor(process.loadTime);
            else if (IsState(ElementState.SETUP)) process.SetupStart += PauseShiftFor(process.SetupStart);
        }

    }
}
