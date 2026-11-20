using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Workstation with N parallel slots (<see cref="ServerProcess"/>), one delay sampler per slot.
    /// Every slot tracks its own state (per-slot utilisation); the element's state is <b>derived</b>
    /// from the slots — PROCESSING if any slot processes, else SETUP, else WAITING_FOR_RESOURCE, else BLOCKED, else
    /// IDLE — so the element-level and per-slot statistics come from one source.
    /// Supports input validation through InputStrategy, optional resources (<see cref="Resources"/>) and an optional
    /// setup before each job's process (<see cref="Setup"/>; the previous item is tracked per slot).
    /// </summary>
    public class MultiServer : Element, IWorkStation, SimuLean.Resources.IResourceUser, SimuLean.Resources.IInterruptibleStation,
                               ISetupStation, ISetupHost, SimuLean.Resources.ITimeoutRerouting
    {
        Queue<ServerProcess> idleProcesses;
        public List<ServerProcess> workInProgress;
        Queue<ServerProcess> completed;
        // Loaded slots not processing yet: waiting for their resources or setting up (GetItems() left their items out).
        private readonly List<ServerProcess> awaitingResource = new List<ServerProcess>();
        private readonly List<ServerProcess> slots = new List<ServerProcess>();
        // Loaded slots whose job waits for room downstream (ReserveExit), oldest first.
        private readonly List<ServerProcess> awaitingExit = new List<ServerProcess>();
        // Loaded slots whose job gave up on its max wait with Reroute: their items wait for the timeout target, oldest first.
        private readonly List<ServerProcess> rerouting = new List<ServerProcess>();

        int currentItems;
        int capacity;

        // Slots per state, kept as they change (the element's state was found by walking every slot on
        // every event, so a large capacity made each event slow).
        private int slotsProcessing, slotsSetup, slotsWaiting, slotsBlocked;

        IRandomSampler[] randomTimes;
        string name;

        // ── statistics ────────────────────────────────────────────────────
        private double _totalServiceTime = 0;
        private int    _completedCount   = 0;

        public override void ResetStatistics()
        {
            base.ResetStatistics();
            foreach (var slot in slots) slot.ResetStatistics();
            _totalServiceTime = 0;
            _completedCount   = 0;
            Setup?.ResetStatistics();
        }

        /// <summary>Mean actual service time observed during the simulation run.</summary>
        public double GetMeanServiceTime() =>
            _completedCount > 0 ? _totalServiceTime / _completedCount : 0.0;

        /// <summary>Items whose job gave up with Reroute and wait for the timeout target (the diagnosis names it).</summary>
        public int ReroutingCount => rerouting.Count;

        /// <summary>The slots in a fixed order (index = <see cref="ServerProcess.Index"/>).</summary>
        public IReadOnlyList<ServerProcess> Slots => slots;

        /// <summary>Fraction of time slot <paramref name="slot"/> spent in <paramref name="state"/>.</summary>
        public double GetSlotStateRatio(int slot, ElementState state) => slots[slot].GetStateRatio(state);

        /// <summary>
        /// Utilisation: the mean over the slots of the time fraction spent PROCESSING (a 3-slot server
        /// with one busy slot is at 1/3, while its element state is PROCESSING the whole time). Time under an
        /// effective stop is not PROCESSING: the slots account the stop state then, as the element does (a job paused by an Immediate stop was counted busy). Setting up is not processing.
        /// </summary>
        public double GetUtilization()
        {
            if (slots.Count == 0) return 0;
            double sum = 0;
            foreach (var slot in slots) sum += slot.GetStateRatio(ElementState.PROCESSING);
            return sum / slots.Count;
        }

        /// <summary>Resources the station needs for each job (dispatcher, requirements, release); null = none.</summary>
        public SimuLean.Resources.StationResources Resources { get; set; }

        /// <summary>Its setup before each job's process (null = none); the previous item is tracked per slot.</summary>
        public StationSetup Setup { get; set; }

        /// <summary>The idle slot that takes the next item (the first of the idle pool) is set up for <paramref name="item"/>.</summary>
        public bool IsSetUpFor(Item item)
            => Setup != null && currentItems < capacity && idleProcesses.Count > 0 && Setup.Matches(idleProcesses.Peek(), item);

        /// <param name="randomTimes">One delay sampler per slot (its length is the capacity).</param>
        /// <param name="inputStrategy">Optional input validation strategy</param>
        /// <param name="vElement">Visual element (null = headless)</param>
        public MultiServer(IRandomSampler[] randomTimes, String name, SimClock sClock, InputStrategy inputStrategy = null, IVElement vElement = null)
            : base(name, sClock, vElement)
        {
            idleProcesses = new Queue<ServerProcess>(randomTimes.Length);
            workInProgress = new List<ServerProcess>(randomTimes.Length);
            completed = new Queue<ServerProcess>(randomTimes.Length);

            this.randomTimes = randomTimes;
            this.name = name;
            this.inputStrategy = inputStrategy;
            this.capacity = randomTimes.Length;
        }

        public override void Start()
        {
            idleProcesses.Clear();
            workInProgress.Clear();
            awaitingResource.Clear();
            awaitingExit.Clear();
            rerouting.Clear();
            ForgetExitReservations();
            slotsProcessing = slotsSetup = slotsWaiting = slotsBlocked = 0;
            completed.Clear();
            slots.Clear();

            for (int i = 0; i < capacity; i++)
            {
                var slot = new ServerProcess(this, randomTimes[i], simClock, i);
                slots.Add(slot);
                idleProcesses.Enqueue(slot);
            }

            currentItems = 0;
            _totalServiceTime = 0;
            _completedCount   = 0;
            Setup?.ResetStatistics();
            base.Start();
        }

        /// <summary>A slot changes state; the element's state follows from the slots (the busiest: PROCESSING > SETUP >
        /// WAITING_FOR_RESOURCE > BLOCKED > IDLE), kept in O(1) by counting the slots per state.</summary>
        private void SetSlot(ServerProcess slot, ElementState state)
        {
            Count(slot.GetState(), -1);
            slot.SetState(state);
            Count(state, +1);
            SetState(slotsProcessing > 0 ? ElementState.PROCESSING
                   : slotsSetup > 0      ? ElementState.SETUP
                   : slotsWaiting > 0    ? ElementState.WAITING_FOR_RESOURCE
                   : slotsBlocked > 0    ? ElementState.BLOCKED
                   :                       ElementState.IDLE);
        }

        private void Count(ElementState state, int delta)
        {
            if (state == ElementState.PROCESSING) slotsProcessing += delta;
            else if (state == ElementState.SETUP) slotsSetup += delta;
            else if (state == ElementState.WAITING_FOR_RESOURCE) slotsWaiting += delta;
            else if (state == ElementState.BLOCKED) slotsBlocked += delta;
        }

        // workInProgress without a linear search: a slot remembers its index, removal swaps the last one in.
        private void AddWork(ServerProcess p)
        {
            p.WorkIndex = workInProgress.Count;
            workInProgress.Add(p);
        }

        private void RemoveWork(ServerProcess p)
        {
            int i = p.WorkIndex;
            if (i < 0 || i >= workInProgress.Count || workInProgress[i] != p) { workInProgress.Remove(p); p.WorkIndex = -1; return; }
            int last = workInProgress.Count - 1;
            if (i != last) { workInProgress[i] = workInProgress[last]; workInProgress[i].WorkIndex = i; }
            workInProgress.RemoveAt(last);
            p.WorkIndex = -1;
        }

        // The same for the slots waiting for a resource or setting up.
        private void AddAwaiting(ServerProcess p)
        {
            if (p.WaitIndex >= 0) return;   // already there (a setup, then its process resources)
            p.WaitIndex = awaitingResource.Count;
            awaitingResource.Add(p);
        }

        private void RemoveAwaiting(ServerProcess p)
        {
            int i = p.WaitIndex;
            if (i < 0 || i >= awaitingResource.Count || awaitingResource[i] != p) { p.WaitIndex = -1; return; }
            int last = awaitingResource.Count - 1;
            if (i != last) { awaitingResource[i] = awaitingResource[last]; awaitingResource[i].WaitIndex = i; }
            awaitingResource.RemoveAt(last);
            p.WaitIndex = -1;
        }

        override public int GetQueueLength()
        {
            return currentItems;
        }
        override public int GetFreeCapacity()
        {
            return capacity - currentItems;
        }

        /// <summary>How the finished items are offered downstream (default FIFO).</summary>
        public OfferOrder OfferOrder { get; set; } = OfferOrder.FIFO;
        /// <summary><see cref="OfferOrder.AnyAccepted"/>: items offered per attempt, oldest first (0 = every finished item).</summary>
        public int OfferScan { get; set; }

        public override bool Unblock()
        {
            bool rerouted = RetryReroutes();
            bool started = RetryExits() || rerouted;
            if (completed.Count == 0)
                return started;

            if (OfferOrder == OfferOrder.AnyAccepted)
            {
                // Offer each finished item, oldest first, until a receiver accepts one (bounded by OfferScan).
                int offered = 0;
                foreach (var p in new List<ServerProcess>(completed))
                {
                    if (OfferScan > 0 && offered++ >= OfferScan) break;
                    if (SendHeldItem(p.theItem)) return true;
                }
                return started;   // still BLOCKED (a job waiting for its exit may have taken the place)
            }

            ServerProcess theProcess = completed.Peek();
            if (!SendOut(theProcess.theItem))
                return started;   // still BLOCKED

            completed.Dequeue();
            AfterBlockedSend(theProcess);
            return true;
        }

        /// <summary>Hands over a specific finished item (pulled from a list), whichever slot holds it.</summary>
        public override bool SendHeldItem(Item item)
        {
            ServerProcess theProcess = null;
            foreach (var p in completed) if (p.theItem == item) { theProcess = p; break; }
            if (theProcess == null || !SendOut(item))
                return false;

            var rest = new Queue<ServerProcess>(completed.Count);
            foreach (var p in completed) if (p != theProcess) rest.Enqueue(p);
            completed = rest;
            AfterBlockedSend(theProcess);
            return true;
        }

        // A blocked slot's item left: the slot goes back to the idle pool.
        private void AfterBlockedSend(ServerProcess theProcess)
        {
            idleProcesses.Enqueue(theProcess);
            currentItems--;
            vElement.ReportState("Exit");
            SetSlot(theProcess, ElementState.IDLE);   // the slot is back in the idle pool

            // Resources kept (ItemLeaves) while BLOCKED are released now that the item left.
            Resources?.ItemLeft(theProcess.job);
            theProcess.job = null;

            NotifyInput();
        }

        public override bool Receive(Item theItem)
        {
            // Validation with origin context is done by the link layer (CanAccept / ValidateInput).
            if (currentItems >= capacity)
                return false;

            currentItems++;
            ServerProcess theProcess = idleProcesses.Dequeue();
            theProcess.theItem = theItem;
            theProcess.loadTime = simClock.GetSimulationTime();

            vElement.LoadItem(theItem);
            theProcess.job = Resources?.TakeForEntry(theItem);   // its resources secured before it came in, or null
            StartJob(theProcess);
            return true;
        }

        // The job starts: room downstream reserved first, then its setup (if needed), its resources, its process.
        private void StartJob(ServerProcess theProcess)
        {
            Item theItem = theProcess.theItem;
            if (!TryReserveExit(theItem))
            {
                // no place downstream can be promised: the job waits, its item held (BLOCKED), until one can
                if (!awaitingExit.Contains(theProcess)) awaitingExit.Add(theProcess);
                AddAwaiting(theProcess);
                SetSlot(theProcess, ElementState.BLOCKED);
                return;
            }
            bool setup = Setup != null && Setup.Needed(theProcess, theItem);
            Resources?.Entered(theProcess.job, theItem, setup && Setup.KeepForProcess);   // its members go on
            if (setup)
            {
                AddAwaiting(theProcess);   // its item is held while it sets up
                Setup.Begin(this, theProcess, Resources, this);
                return;
            }

            BeginProcess(theProcess);
        }

        /// <summary>A job's wait for its process resources timed out with Reroute: its members are free already;
        /// the item leaves unprocessed by the timeout target (<see cref="Element.TimeoutTarget"/>), through its link — or its
        /// slot waits, BLOCKED, until the target takes it (told through that link). A place reserved downstream is
        /// given back.</summary>
        bool SimuLean.Resources.ITimeoutRerouting.JobTimedOut(SimuLean.Resources.ResourceJob job)
        {
            ServerProcess slot = null;
            foreach (var p in awaitingResource) if (p.job == job) { slot = p; break; }
            if (slot == null || TimeoutTarget == null) return false;
            slot.job = null;
            slot.setup = null;   // a setup kept for the process that never started
            DropExitReservation(slot.theItem);
            rerouting.Add(slot);
            SetSlot(slot, ElementState.BLOCKED);
            RetryReroutes();
            return true;
        }

        // The rerouted items leave by the timeout target, oldest first, while it takes them.
        private bool RetryReroutes()
        {
            bool any = false;
            while (rerouting.Count > 0)
            {
                var slot = rerouting[0];
                if (!SendToTimeoutTarget(slot.theItem)) break;
                rerouting.RemoveAt(0);
                RemoveAwaiting(slot);
                idleProcesses.Enqueue(slot);
                currentItems--;
                vElement.ReportState("Exit");
                SetSlot(slot, ElementState.IDLE);
                any = true;
                NotifyInput();
            }
            return any;
        }

        // the jobs waiting for room downstream start, oldest first, as far as places can be reserved; when the station also secures resources before entry,
        // a free slot may now take an item (its resources are asked once an exit place could be reserved).
        private bool RetryExits()
        {
            if (ReserveExit && PreparesForEntry && currentItems < capacity) NotifyInputLater();
            if (awaitingExit.Count == 0) return false;
            bool any = false;
            for (int i = 0; i < awaitingExit.Count;)
            {
                var slot = awaitingExit[i];
                if (!TryReserveExit(slot.theItem)) { i++; continue; }
                awaitingExit.RemoveAt(i);
                any = true;
                StartJob(slot);
            }
            return any;
        }

        // The job's process: its resources first (if any), then the process.
        private void BeginProcess(ServerProcess theProcess)
        {
            if (theProcess.job != null && theProcess.job.BeforeEntry)
            {
                ((IWorkStation)this).StartProcessing(theProcess);   // secured before the part came in
                return;
            }
            if (Resources != null)
            {
                SetSlot(theProcess, ElementState.WAITING_FOR_RESOURCE);   // StartProcessing overwrites it
                AddAwaiting(theProcess);                                  // its item is held while it waits
                theProcess.job = AcquireFor(theProcess, theProcess.theItem);
                return;
            }

            ((IWorkStation)this).StartProcessing(theProcess);
        }

        void ISetupHost.SetupState(ServerProcess slot, ElementState state) => SetSlot(slot, state);

        void ISetupHost.SetupEnded(ServerProcess slot, bool keptResources)
        {
            if (keptResources) ((IWorkStation)this).StartProcessing(slot);   // the setup's resources stay for the process
            else BeginProcess(slot);
        }

        private SimuLean.Resources.ResourceJob AcquireFor(ServerProcess theProcess, Item theItem)
            => Resources.Acquire(this, theItem, () => ((IWorkStation)this).StartProcessing(theProcess));

        void IWorkStation.CompleteServerProcess(ServerProcess theProcess)
        {
            Item theItem = theProcess.theItem;
            RemoveWork(theProcess);
            _totalServiceTime += theProcess.lastDelay;
            _completedCount++;
            RaiseProcessEnded(theItem);
            Resources?.ProcessEnded(theProcess.job);   // released now, or kept (HELD) until the item leaves

            // FIFO into a plain push link: the items already finished and blocked leave first (the receiver may have room
            // without having said so); the new one goes out only behind them, never ahead (it would overtake
            // them, and the oldest could stay blocked for good). A list or a carrier is offered every finished item
            // (listed / requested; the list or the transport decides the order).
            bool inOrder = OfferOrder == OfferOrder.FIFO && GetOutput() is GeneralLink;
            if (inOrder)
                while (completed.Count > 0 && Unblock()) { }

            if ((!inOrder || completed.Count == 0) && SendOut(theItem))
            {
                idleProcesses.Enqueue(theProcess);
                currentItems--;
                vElement.ReportState("Exit");
                SetSlot(theProcess, ElementState.IDLE);
                Resources?.ItemLeft(theProcess.job);
                theProcess.job = null;
                NotifyInput();
            }
            else
            {
                // Output blocked: the operator stays held until Unblock() sends the item.
                completed.Enqueue(theProcess);
                SetSlot(theProcess, ElementState.BLOCKED);
            }
        }

        public override bool CheckAvailability(Item theItem)
        {
            if (currentItems >= capacity)
                return false;
            // the part comes in only once the process resources are there (with an exit reservation: once an exit place could be
            // reserved too). A state, no side effects: the request is placed by PrepareForEntry.
            if (PreparesForEntry)
                return Resources.ReadyForEntry && (!ReserveExit || CouldReserveExit(theItem));

            return true;   // admission: the input strategy, checked by the link (CanAccept)
        }

        /// <summary>(<c>processBy.acquireBeforeEntry</c>): it secures its process resources before it takes a part.</summary>
        public override bool PreparesForEntry => Resources != null && Resources.AcquireBeforeEntry;

        /// <summary>Step 1 of entry preparation: a link could not hand <paramref name="theItem"/> over. Its admission first — not input-blocked,
        /// its input strategy, no place promised to others, a free slot, with an exit reservation an exit place it could reserve — then one
        /// job's resources are asked (<see cref="SimuLean.Resources.StationResources.RequestAhead"/>).</summary>
        public override bool PrepareForEntry(Item theItem, Element origin)
        {
            if (!PreparesForEntry || currentItems >= capacity || IsInputBlocked || !ValidateInput(theItem, origin)
                || RoomReservedForOthers(theItem) || (ReserveExit && !CouldReserveExit(theItem)))
                return false;
            Resources.RequestAhead(this, theItem, capacity - currentItems);
            return true;
        }

        /// <summary>Every item the station holds: waiting for its resources or setting up, in process, finished and blocked.</summary>
        public Queue<Item> GetItems()
        {
            Queue<Item> myItems = new Queue<Item>();
            foreach (ServerProcess sp in awaitingResource)
                myItems.Enqueue(sp.GetItem());
            foreach (ServerProcess sp in workInProgress)
                myItems.Enqueue(sp.GetItem());
            foreach (ServerProcess sp in completed)
                myItems.Enqueue(sp.GetItem());
            return myItems;
        }

        /// <summary>The slots account the element's stop state while it is effective (utilisation without stopped time).</summary>
        protected override void OnStopShown(ElementState? stop)
        {
            foreach (var slot in slots) slot.ShowStop(stop);
        }

        /// <summary>Keeps in-process progress views consistent after a stop: shift each
        /// running job's load time (and each running setup's start) by the part of the pause it spent loaded, so
        /// (now − loadTime)/lastDelay stays right (jobs started during the pause shift less).</summary>
        protected override void OnResumed(StopToken token)
        {
            if (!ResumedWork(token)) return;
            foreach (ServerProcess sp in workInProgress)
                sp.loadTime += PauseShiftFor(sp.loadTime);
            foreach (ServerProcess sp in awaitingResource)
                if (sp.GetState() == ElementState.SETUP) sp.SetupStart += PauseShiftFor(sp.SetupStart);
        }

        void IWorkStation.StartProcessing(ServerProcess theProcess)
        {
            RemoveAwaiting(theProcess);   // its resources arrived (no-op without resources)
            double delay = theProcess.GetDelay();
            theProcess.lastDelay = delay;
            theProcess.loadTime = simClock.GetSimulationTime();
            AddWork(theProcess);
            vElement.UnloadItem(theProcess.theItem);   // processing starts (views: e.g. the output look)
            var work = ScheduleWork(theProcess, delay);
            Resources?.ProcessStarted(theProcess, work);   // its resources may be taken while it works
            SetSlot(theProcess, ElementState.PROCESSING);
        }

        /// <summary>A job lost a resource: its slot waits for it, its process (or setup) paused; then it processes —
        /// or sets up — again (its progress view shifted by the pause, or started again).</summary>
        void SimuLean.Resources.IInterruptibleStation.JobPaused(SimuLean.Resources.ResourceJob job, bool paused, bool restart)
        {
            var slot = job.Slot;
            if (slot == null) return;
            if (paused) { SetSlot(slot, ElementState.WAITING_FOR_RESOURCE); return; }
            if (slot.InSetup)
            {
                if (restart) slot.SetupStart = simClock.GetSimulationTime(); else slot.SetupStart += job.LastPause;
                SetSlot(slot, ElementState.SETUP);
                return;
            }
            if (restart) slot.loadTime = simClock.GetSimulationTime();
            else slot.loadTime += job.LastPause;
            SetSlot(slot, ElementState.PROCESSING);
        }
    }
}
