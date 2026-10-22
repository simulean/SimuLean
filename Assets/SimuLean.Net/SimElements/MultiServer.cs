using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Workstation with N parallel slots (<see cref="ServerProcess"/>), one delay sampler per slot.
    /// Every slot tracks its own state (per-slot utilisation); the element's state is <b>derived</b>
    /// from the slots — PROCESSING if any slot processes, else WAITING_FOR_RESOURCE, else BLOCKED, else
    /// IDLE — so the element-level and per-slot statistics come from one source.
    /// Supports input validation through InputStrategy and optional resources (<see cref="Resources"/>).
    /// </summary>
    public class MultiServer : Element, IWorkStation, SimuLean.Resources.IResourceUser
    {
        Queue<ServerProcess> idleProcesses;
        public List<ServerProcess> workInProgress;
        Queue<ServerProcess> completed;
        // Loaded slots waiting for their resources (GetItems() left their items out).
        private readonly List<ServerProcess> awaitingResource = new List<ServerProcess>();
        private readonly List<ServerProcess> slots = new List<ServerProcess>();

        int currentItems;
        int capacity;

        // Slots per state, kept as they change (the element's state was found by walking every slot on
        // every event, so a large capacity made each event slow).
        private int slotsProcessing, slotsWaiting, slotsBlocked;

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
        }

        /// <summary>Mean actual service time observed during the simulation run.</summary>
        public double GetMeanServiceTime() =>
            _completedCount > 0 ? _totalServiceTime / _completedCount : 0.0;

        /// <summary>The slots in a fixed order (index = <see cref="ServerProcess.Index"/>).</summary>
        public IReadOnlyList<ServerProcess> Slots => slots;

        /// <summary>Fraction of time slot <paramref name="slot"/> spent in <paramref name="state"/>.</summary>
        public double GetSlotStateRatio(int slot, ElementState state) => slots[slot].GetStateRatio(state);

        /// <summary>
        /// Utilisation: the mean over the slots of the time fraction spent PROCESSING (a 3-slot server
        /// with one busy slot is at 1/3, while its element state is PROCESSING the whole time). Time under an
        /// effective stop is not PROCESSING: the slots account the stop state then, as the element does (benchmark C2,
        /// 2026-10-13; a job paused by an Immediate stop was counted busy).
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
            slotsProcessing = slotsWaiting = slotsBlocked = 0;
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
            base.Start();
        }

        /// <summary>A slot changes state; the element's state follows from the slots (the busiest: PROCESSING >
        /// WAITING_FOR_RESOURCE > BLOCKED > IDLE), kept in O(1) by counting the slots per state.</summary>
        private void SetSlot(ServerProcess slot, ElementState state)
        {
            Count(slot.GetState(), -1);
            slot.SetState(state);
            Count(state, +1);
            SetState(slotsProcessing > 0 ? ElementState.PROCESSING
                   : slotsWaiting > 0    ? ElementState.WAITING_FOR_RESOURCE
                   : slotsBlocked > 0    ? ElementState.BLOCKED
                   :                       ElementState.IDLE);
        }

        private void Count(ElementState state, int delta)
        {
            if (state == ElementState.PROCESSING) slotsProcessing += delta;
            else if (state == ElementState.WAITING_FOR_RESOURCE) slotsWaiting += delta;
            else if (state == ElementState.BLOCKED) slotsBlocked += delta;
        }

        // workInProgress without a linear search (B6): a slot remembers its index, removal swaps the last one in.
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

        // The same for the slots waiting for a resource (B10).
        private void AddAwaiting(ServerProcess p)
        {
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

        string IWorkStation.GetName()
        {
            return name;
        }

        /// <summary>How the finished items are offered downstream (default FIFO).</summary>
        public ReleaseMode Release { get; set; } = ReleaseMode.FIFO;
        /// <summary><see cref="ReleaseMode.AnyAccepted"/>: items offered per attempt, oldest first (0 = every finished item).</summary>
        public int ReleaseScan { get; set; }

        public override bool Unblock()
        {
            if (completed.Count == 0)
                return false;

            if (Release == ReleaseMode.AnyAccepted)
            {
                // Offer each finished item, oldest first, until a receiver accepts one (bounded by ReleaseScan).
                int offered = 0;
                foreach (var p in new List<ServerProcess>(completed))
                {
                    if (ReleaseScan > 0 && offered++ >= ReleaseScan) break;
                    if (SendHeldItem(p.theItem)) return true;
                }
                return false;   // still BLOCKED
            }

            ServerProcess theProcess = completed.Peek();
            if (!SendOut(theProcess.theItem))
                return false;   // still BLOCKED

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

            if (Resources != null)
            {
                SetSlot(theProcess, ElementState.WAITING_FOR_RESOURCE);   // StartProcessing overwrites it
                AddAwaiting(theProcess);                                  // its item is held while it waits (B10)
                theProcess.job = AcquireFor(theProcess, theItem);
                return true;
            }

            ((IWorkStation)this).StartProcessing(theProcess);
            return true;
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
            // without having said so); the new one goes out only behind them, never ahead (it overtook
            // them, and the oldest could stay blocked for good). A list or a carrier is offered every finished item
            // (listed / requested; the list or the transport decides the order), as before.
            bool inOrder = Release == ReleaseMode.FIFO && GetOutput() is GeneralLink;
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

            return true;   // admission: the input strategy, checked by the link (CanAccept)
        }

        /// <summary>Every item the station holds: waiting for its resources, in process, finished and blocked.</summary>
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

        /// <summary>Keeps in-process progress views consistent after a stop: shift each
        /// running job's load time by the part of the pause it spent loaded, so
        /// (now − loadTime)/lastDelay stays right (jobs started during the pause shift less).</summary>
        /// <summary>The slots account the element's stop state while it is effective (utilisation without stopped time).</summary>
        protected override void OnStopShown(ElementState? stop)
        {
            foreach (var slot in slots) slot.ShowStop(stop);
        }

        protected override void OnResumed(StopToken token)
        {
            if (!ResumedWork(token)) return;
            foreach (ServerProcess sp in workInProgress)
                sp.loadTime += PauseShiftFor(sp.loadTime);
        }

        void IWorkStation.StartProcessing(ServerProcess theProcess)
        {
            RemoveAwaiting(theProcess);   // its resources arrived (no-op without resources)
            double delay = theProcess.GetDelay();
            theProcess.lastDelay = delay;
            theProcess.loadTime = simClock.GetSimulationTime();
            AddWork(theProcess);
            vElement.UnloadItem(theProcess.theItem);   // processing starts (views: e.g. the output look, S4)
            ScheduleWork(theProcess, delay);
            SetSlot(theProcess, ElementState.PROCESSING);
        }
    }
}
