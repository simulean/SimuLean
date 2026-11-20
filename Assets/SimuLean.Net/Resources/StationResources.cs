using System;
using System.Collections.Generic;

namespace SimuLean.Resources
{
    /// <summary>When a process task gives its resources back.</summary>
    public enum ProcessRelease
    {
        /// <summary>At the end of the process (default).</summary>
        EndOfProcess,
        /// <summary>When the item leaves the station — kept while the station is BLOCKED (a shared operator stays with a blocked station).</summary>
        ItemLeaves,
        /// <summary>When the item enters another element (<see cref="StationResources.ReleaseAt"/>): the
        /// resource goes with the item — a device lent to a patient to take home, returned at the device desk; a
        /// fixture travelling with a part. HELD meanwhile; the station is free as soon as the item leaves it.</summary>
        AtElement,
    }

    /// <summary>How a use with several requirements takes them.</summary>
    public enum AcquireOrder
    {
        /// <summary>All at once (default): granted only when every requirement can be met at the same moment, across teams —
        /// nothing is held while waiting (no hold-and-wait).</summary>
        AllAtOnce,
        /// <summary>One by one in list order: each requirement is asked when the one before it was granted, and the members
        /// already granted are kept (HELD at the station) while the next one is waited for — e.g. a crane, then a driver of
        /// another pool. Hold-and-wait: two uses taking the same teams in opposite orders can deadlock (reported as a stall).</summary>
        InOrder,
    }

    /// <summary>A station whose job, given up on a max wait with <see cref="TimeoutAction.Reroute"/>, sends its item to its
    /// timeout target unprocessed (the Multi-Server).</summary>
    public interface ITimeoutRerouting
    {
        /// <summary>The job's wait for its resources timed out and was withdrawn (nothing held any more): its item leaves by
        /// the timeout target, or waits for it. False = not a job of an item it holds (it then asks again, as GiveUp).</summary>
        bool JobTimedOut(ResourceJob job);
    }

    /// <summary>An element that asks a dispatcher for resources to do its work (stations).</summary>
    public interface IResourceUser
    {
        /// <summary>Its resource requirements and dispatcher; null = it needs no resources.</summary>
        StationResources Resources { get; set; }
    }

    /// <summary>The resources one job of a station got (or is waiting for).</summary>
    public sealed class ResourceJob
    {
        internal ResourceJob(ResourceRequest request) { Request = request; }
        /// <summary>Its request (a job that gave up on a max wait and asked again has a new one).</summary>
        public ResourceRequest Request { get; internal set; }
        /// <summary>The executors working for it, in requirement order (one that lost the job to a request of higher
        /// priority is replaced by the one that takes it over).</summary>
        public IReadOnlyList<TaskExecutor> Executors => Request.Assigned;
        public bool ProcessEnded { get; internal set; }
        public bool Released { get; internal set; }
        /// <summary>The job's resources set the station up before its process (a setup kept for the process,
        /// <see cref="StationSetup.KeepForProcess"/>): their task holds in SETUP until <see cref="SetupEnded"/>, then in
        /// PROCESSING.</summary>
        public bool WithSetup { get; internal set; }
        /// <summary><see cref="WithSetup"/>: the setup is over (<see cref="StationResources.SetupEnded"/>).</summary>
        public bool SetupEnded { get; internal set; }
        /// <summary>Asked before its item entered (<see cref="StationResources.AcquireBeforeEntry"/>): its members wait
        /// HELD at the station until <see cref="StationResources.Entered"/>.</summary>
        public bool BeforeEntry { get; internal set; }
        /// <summary><see cref="BeforeEntry"/>: an item was taken in for it (<see cref="StationResources.TakeForEntry"/>) —
        /// its wait (and its max wait) ended there.</summary>
        public bool ItemIn { get; internal set; }
        /// <summary><see cref="BeforeEntry"/>: the station started the item's work (<see cref="StationResources.Entered"/>):
        /// its members set up (a setup kept for the process) or wait HELD for the process.</summary>
        public bool WorkStarted { get; internal set; }
        /// <summary><see cref="BeforeEntry"/>: the station's process started (its members go from HELD to PROCESSING).</summary>
        public bool ProcessBegun { get; internal set; }
        /// <summary>The executors' tasks of this job (a released executor may already work elsewhere).</summary>
        internal readonly List<TaskSequence> Tasks = new List<TaskSequence>();
        internal StationResources.Assignment Assignment;

        /// <summary>The station's process of this job, set by a station that can pause it (<see cref="IInterruptibleStation"/>):
        /// only then its resources can be taken while it works. Null before the process starts.</summary>
        public WorkHandle Work { get; private set; }
        /// <summary>The station's slot of the job (<see cref="ServerProcess"/>), for the station's own bookkeeping.</summary>
        public ServerProcess Slot { get; private set; }

        /// <summary>A station that can pause its job's process tells it here when the process starts.</summary>
        public void ProcessStarted(WorkHandle work, ServerProcess slot) { Work = work; Slot = slot; }

        /// <summary>Times one of its resources was taken (by a request of higher priority or a stop), and the time its
        /// process stood paused waiting for them.</summary>
        public int Interruptions { get; internal set; }
        /// <summary>The job's pauses ended, whole (the station's <see cref="StationResources.InterruptedTime"/> also counts
        /// its open ones, from the warm-up).</summary>
        public double InterruptedTime { get; internal set; }
        /// <summary>True while its process is paused for a resource it lost; since when.</summary>
        public bool IsInterrupted => missing > 0;
        public double InterruptedSince { get; private set; } = double.NaN;

        internal int missing;          // resources lost and not back yet (the process is paused meanwhile)
        internal bool restart;         // the process starts again when they are back (Restart)
        internal bool heldProcess;     // the pause holds the station's process (not with HoldFor, not HELD after the process)

        internal void Pause(double now) { InterruptedSince = now; }
        internal double Resumed(double now)
        {
            double d = now - InterruptedSince;
            InterruptedSince = double.NaN; InterruptedTime += d; LastPause = d;
            return d;
        }
        /// <summary>How long its last pause lasted (progress views shift the job's start by it).</summary>
        public double LastPause { get; private set; }
    }

    /// <summary>
    /// A station's resource needs for its process: the dispatcher, the requirements (default: one resource of the
    /// dispatcher), when they are released and the request priority. The station keeps its process time (its own
    /// stops pause it); its resources are held for the job — PROCESSING while the station processes, HELD while
    /// it keeps them after the process (<see cref="ProcessRelease.ItemLeaves"/>) — and free when released.
    /// Station side: <see cref="Acquire"/> when a job arrives (start processing in <c>onReady</c>),
    /// <see cref="ProcessEnded"/> when the process ends, <see cref="ItemLeft"/> when its item has left.
    /// </summary>
    public sealed class StationResources
    {
        private readonly List<ResourceRequirement> requirements = new List<ResourceRequirement>();

        public StationResources(ResourceDispatcher dispatcher, params ResourceRequirement[] requirements)
        {
            Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            if (requirements != null) this.requirements.AddRange(requirements);
            dispatcher.AddUse(this);
        }

        /// <summary>What its requests do when no member is free (default None: wait). Resume / Restart: take a member
        /// from a task of strictly lower priority, the interrupted work goes on later where it was / from the start. It
        /// also makes its own jobs take part in interruptions by a resource's Immediate stop (the team's
        /// <see cref="ResourceStopPolicy"/>); without a mode they keep their resources.</summary>
        public PreemptMode Preempt
        {
            get => preempt;
            set { preempt = value; if (value != PreemptMode.None) Dispatcher.Preemptive = true; }
        }
        private PreemptMode preempt;

        /// <summary>False: its jobs' resources are never taken by requests of higher priority (default true).</summary>
        public bool Preemptable { get; set; } = true;

        /// <summary>Interruptions of its jobs (a resource taken by a request of higher priority or by its stop) and the
        /// time its jobs' processes stood paused for them (since start or warm-up). <see cref="InterruptedTime"/>
        /// includes every pause still open, up to the current simulation time (a running pause counts at a capture, live
        /// or at the horizon, as a running stop does in <c>downtime_total</c>); a pause open at the warm-up counts from
        /// the warm-up.</summary>
        public int Interruptions { get; private set; }
        public double InterruptedTime => InterruptedTimeAt(Dispatcher.Clock.GetSimulationTime());

        /// <summary>The time its jobs stood paused up to <paramref name="now"/>: the pauses ended plus the open ones.</summary>
        public double InterruptedTimeAt(double now)
        {
            double t = closedInterruptedTime;
            for (int i = 0; i < pausedJobs.Count; i++) t += Math.Max(0, now - Math.Max(pausedJobs[i].InterruptedSince, statisticsSince));
            return t;
        }

        private double closedInterruptedTime;   // the pauses ended (since start or warm-up)
        private double statisticsSince;         // start or warm-up: an open pause counts from here
        // The jobs whose process stands paused now (a few at most), summed at a capture: no event per pause. A list, not a
        // set: the sum runs in a fixed order, so a seeded run's result is the same to the last digit.
        private readonly List<ResourceJob> pausedJobs = new List<ResourceJob>();

        internal void ResetStatistics(double now, bool newRun)
        {
            closedInterruptedTime = 0; statisticsSince = now;
            if (newRun)
            {
                pausedJobs.Clear(); aheadPending = null; aheadGranted.Clear();
                // a new run: the timers of the last one's waits are cancelled (none may fire into this run)
                foreach (var a in timed.ToArray()) a.StopTimer();
                timed.Clear();
            }
            // a pause open at the warm-up is one interruption of the new period (its time counts from here), so that
            // interrupted_time_avg stays the time per interruption counted
            Interruptions = pausedJobs.Count;
        }

        private void PauseStarted(ResourceJob job) => pausedJobs.Add(job);
        private void PauseEnded(ResourceJob job, double now)
        {
            if (!pausedJobs.Remove(job)) return;
            closedInterruptedTime += Math.Max(0, now - Math.Max(job.InterruptedSince, statisticsSince));
        }

        private ResourceJob starting;   // the job whose resources just arrived: its station is starting its process

        /// <summary>A station that can pause its job's process (<see cref="IInterruptibleStation"/>) tells which work is the
        /// process of the job of <paramref name="slot"/> (also while <see cref="Acquire(Element, Item, Action)"/> has not
        /// returned yet: resources free at once start the process inside it). A job secured before its part goes
        /// from HELD to its process now.</summary>
        public void ProcessStarted(ServerProcess slot, WorkHandle work)
        {
            var job = slot.job ?? starting;
            if (job == null) return;
            job.ProcessStarted(work, slot);
            if (job.BeforeEntry && job.WorkStarted && !job.ProcessBegun) { job.ProcessBegun = true; Next(job); }
        }

        /// <summary>A setup's work at <paramref name="slot"/> (<see cref="StationSetup"/>) is the work of
        /// <paramref name="job"/> — the setup crew's job, or the process job that sets up (kept for the process); null = the
        /// job whose <c>onReady</c> runs now. Never the slot's process job by default: a setup crew's job needs its own work,
        /// or it could not be preempted while it sets up.</summary>
        public void SetupStarted(ResourceJob job, ServerProcess slot, WorkHandle work) => (job ?? starting)?.ProcessStarted(work, slot);

        /// <summary>The same for a job without a slot, from inside its <c>onReady</c> (a downtime's repair).</summary>
        public void ProcessStarted(WorkHandle work) => starting?.ProcessStarted(work, null);

        /// <summary>The same for a station that knows the job (<paramref name="job"/>; null = the job whose
        /// <c>onReady</c> runs now) and whose work goes in timed steps (an assembly task, a stacker's cycle, a sequencing
        /// step): each step started gives the job its work. Returns the job (null if none).</summary>
        public ResourceJob ProcessStarted(ResourceJob job, WorkHandle work)
        {
            job = job ?? starting;
            job?.ProcessStarted(work, job.Slot);
            return job;
        }

        /// <summary>Who pauses a job's process when it loses a resource, when that is not the requesting element itself
        /// (a downtime's repair — the stopped element is the requester, the repair its process). Null = the requester,
        /// if it is an <see cref="IInterruptibleStation"/>.</summary>
        public IInterruptibleStation Host { get; set; }

        /// <summary>Its requests are served while the requester is stopped (the crew repairing it); other process
        /// requests of a stopped element wait for its resume.</summary>
        public bool ServeWhileDown { get; set; }

        private IInterruptibleStation InterruptibleOf(Element station) => Host ?? station as IInterruptibleStation;

        public ResourceDispatcher Dispatcher { get; }

        /// <summary>What its requests and tasks are (<see cref="TaskType.Process"/>, the default; <see cref="TaskType.Setup"/>
        /// for a station's setup crew, whose members hold in SETUP). A Setup request of a stopped station waits as a Process
        /// one does.</summary>
        public TaskType Task { get; set; } = TaskType.Process;

        // the state its members work in at the station
        private ElementState WorkState => Task == TaskType.Setup ? ElementState.SETUP : ElementState.PROCESSING;

        // Stations whose Resumed event retries the dispatcher (once each).
        private readonly HashSet<Element> watched = new HashSet<Element>();
        public IReadOnlyList<ResourceRequirement> Requirements => requirements;
        public ProcessRelease Release { get; set; } = ProcessRelease.EndOfProcess;

        /// <summary>How several requirements are taken (default <see cref="AcquireOrder.AllAtOnce"/>, the behaviour
        /// of a single requirement). With <see cref="AcquireOrder.InOrder"/> or a requirement of another team
        /// (<see cref="ResourceRequirement.Team"/>) a job's requirements are asked as parts — one per requirement in order,
        /// or one per team all at once — and every part's members wait HELD at the station until the last one is there.</summary>
        public AcquireOrder Order { get; set; } = AcquireOrder.AllAtOnce;

        /// <summary>The use asks several teams, or in order: its jobs are built from parts.</summary>
        public bool UsesParts
        {
            get
            {
                if (Order == AcquireOrder.InOrder) return true;
                foreach (var r in requirements) if (r.Team != null && r.Team != Dispatcher) return true;
                return false;
            }
        }

        /// <summary>Every team the use asks (its own first).</summary>
        public IReadOnlyList<ResourceDispatcher> Teams
        {
            get
            {
                var list = new List<ResourceDispatcher> { Dispatcher };
                foreach (var r in requirements) if (r.Team != null && !list.Contains(r.Team)) list.Add(r.Team);
                return list;
            }
        }

        /// <summary><see cref="ProcessRelease.AtElement"/>: the element where the resources come back (when the item enters
        /// it). An item that never gets there keeps them.</summary>
        public Element ReleaseAt
        {
            get => releaseAt;
            set
            {
                if (releaseAt == value) return;
                if (releaseAt != null) releaseAt.ItemEntered -= OnItemAtReleasePoint;
                releaseAt = value;
                if (releaseAt != null) releaseAt.ItemEntered += OnItemAtReleasePoint;
            }
        }
        private Element releaseAt;
        // AtElement: the jobs whose resources travel with each item
        private readonly Dictionary<Item, List<ResourceJob>> traveling = new Dictionary<Item, List<ResourceJob>>();

        // AtElement: a job dropped before its work (Reroute) no longer travels with its item
        private void ForgetTraveling(ResourceJob job)
        {
            foreach (var kv in traveling)
            {
                if (!kv.Value.Remove(job)) continue;
                if (kv.Value.Count == 0) traveling.Remove(kv.Key);
                return;
            }
        }

        private void OnItemAtReleasePoint(Element at, Item item)
        {
            if (item == null || !traveling.TryGetValue(item, out var jobs)) return;
            traveling.Remove(item);
            foreach (var job in jobs)
            {
                if (job.Released) continue;
                job.Released = true;
                Next(job);
            }
        }

        /// <summary>
        /// The resources' own time at the station (null = the whole process, the default): with a sampler they work for
        /// its value (sampled per job, the item's labels readable) and are then free, while the item stays for the rest
        /// of the station's process — painting then curing, an erection lift then the joint welding.
        /// <see cref="Release"/> does not apply then.
        /// </summary>
        public IRandomSampler HoldFor { get; set; }

        /// <summary>Priority of the station's requests (null = 1, larger = more important): usually an expression over the
        /// item and the station (<see cref="SimuLean.Serialization.ResourceBuild.PriorityOf"/>).</summary>
        public Func<Item, double> Priority { get; set; }

        /// <summary>
        /// Acquire before entry: the station secures its process
        /// resources <b>before it takes the part</b> (default false; the Multi-Server only — the build refuses it elsewhere).
        /// Step 1: a link that could not hand an item over tells the station (<see cref="Element.PrepareForEntry"/>); when the
        /// station admits the item (its admission first: input strategy, places promised to others, a free slot) it asks for
        /// one job's resources (<see cref="RequestAhead"/>, priority and max wait from that item) — never from a state check
        /// (<see cref="Element.CanAccept"/> / <c>CheckAvailability</c> only read <see cref="ReadyForEntry"/>). Step 2: an item
        /// enters only once they are there and takes the grant (<see cref="TakeForEntry"/>), which ends the wait and its max
        /// wait. The grant is for the next item that enters (the offered one, or another if that one went elsewhere). The
        /// members wait HELD at the station — not preemptable — until the work starts (<see cref="Entered"/>), and HELD again
        /// while others set the station up, until the process starts.
        /// </summary>
        public bool AcquireBeforeEntry { get; set; }

        /// <summary>
        /// The max wait of its jobs for their resources (null = none): from the request until every requirement is
        /// granted — with <see cref="AcquireBeforeEntry"/>, until the item enters. On a timeout the station counts it and
        /// signals it (<see cref="Element.RecordTimeout"/>), then by <see cref="WaitLimit.OnTimeout"/>: KeepWaiting goes on;
        /// GiveUp withdraws the request (its pending parts, the take-overs asked) and frees the members already granted (held
        /// in order, or secured before the part) — a job with an item asks again at once, behind the others, with a new timer;
        /// a job secured before its part is dropped (asked again at the next offer); Reroute (a Multi-Server's job,
        /// <see cref="ITimeoutRerouting"/>) frees them the same way and the item leaves by the station's timeout target.
        /// </summary>
        public WaitLimit MaxWait { get; set; }

        // the jobs whose wait has a running timer (cancelled at a new run)
        private readonly List<Assignment> timed = new List<Assignment>();

        // a job secured before its part gave up (its members are free): forgotten; the station's input is told, so an
        // item offered again asks again
        private void DropAhead(ResourceJob job, Element station)
        {
            if (aheadPending == job) aheadPending = null;
            else if (aheadGranted.Contains(job))
            {
                var rest = new List<ResourceJob>(aheadGranted);
                rest.Remove(job);
                aheadGranted.Clear();
                foreach (var j in rest) aheadGranted.Enqueue(j);
            }
            station?.NotifyInputLater();
        }

        // GiveUp of a job with its item (or its task / cycle): the same job asks again, as a new request behind the others,
        // with a new timer — the station's onReady unchanged
        private void AskAgain(Assignment old)
        {
            var job = old.Job;
            var m = job.Request;
            var a = new Assignment { Owner = this, Station = old.Station, Item = old.Item, OnReady = old.OnReady, Job = job, LimitItem = old.LimitItem };
            job.Assignment = a;
            job.Request = new ResourceRequest(old.Station, Task, a.OnAssigned, requirements)
            {
                Priority = m.Priority, Preempt = m.Preempt, Location = m.Location, OldestItemTime = m.OldestItemTime,
                ServeWhileDown = m.ServeWhileDown,
            };
            if (UsesParts) a.AskParts();
            else Dispatcher.Request(job.Request);
            a.StartTimer();
        }

        // the work points the job's members took are free again
        private void FreeWorkPoints(ResourceJob job)
        {
            if (occupied.Count == 0) return;
            List<string> mine = null;
            foreach (var kv in occupied) if (kv.Value.job == job) (mine ?? (mine = new List<string>())).Add(kv.Key);
            if (mine != null) foreach (string p in mine) occupied.Remove(p);
        }

        private ResourceJob aheadPending;                                   // asked, not granted yet (one at a time)
        private readonly Queue<ResourceJob> aheadGranted = new Queue<ResourceJob>();   // there, waiting for an item

        /// <summary>Jobs whose resources are there, waiting for an item.</summary>
        public int GrantedAhead => aheadGranted.Count;

        /// <summary>A job's resources asked before the part and not granted yet.</summary>
        public bool AskedAhead => aheadPending != null;

        /// <summary>True when an item may enter now (resources there, or the option off). A state: no side effects.</summary>
        public bool ReadyForEntry => !AcquireBeforeEntry || aheadGranted.Count > 0;

        /// <summary>Step 1: the station admits <paramref name="offered"/> but its resources are not there — it asks for
        /// one job's resources when none is asked yet and its <paramref name="freeSlots"/> are not all covered by grants
        /// (priority and max wait evaluated for the offered item). The station's input is told when they are there.</summary>
        public void RequestAhead(Element station, Item offered, int freeSlots)
        {
            if (!AcquireBeforeEntry || aheadPending != null || freeSlots <= aheadGranted.Count) return;
            bool now = false;
            ResourceJob job = null;
            job = Acquire(station, null, () => { if (job == null) now = true; else AheadGranted(station, job); }, double.NaN, false, true, offered);
            if (now) { aheadGranted.Enqueue(job); station.NotifyInputLater(); return; }   // there at once: offered again now
            aheadPending = job;
        }

        private void AheadGranted(Element station, ResourceJob job)
        {
            if (aheadPending == job) aheadPending = null;
            aheadGranted.Enqueue(job);
            station.NotifyInputLater();   // an item may come in now
        }

        /// <summary>Step 2: the job secured for <paramref name="item"/>, entering now (null = none: the option is off,
        /// or no grant). Its wait ends here: its max wait stops, so it can no longer give up while the item is in
        /// (it would run on while the station waited for room downstream, and a GiveUp would leave the station without its
        /// operator).</summary>
        public ResourceJob TakeForEntry(Item item)
        {
            if (!AcquireBeforeEntry || aheadGranted.Count == 0) return null;
            var job = aheadGranted.Dequeue();
            job.ItemIn = true;
            job.Assignment?.StopTimer();
            if (job.Assignment != null) job.Assignment.Item = item;
            return job;
        }

        /// <summary>The work of the job's item starts at the station (with <paramref name="withSetup"/> its members set
        /// the station up first: a setup kept for the process; otherwise they wait HELD until the process starts).</summary>
        public void Entered(ResourceJob job, Item item, bool withSetup)
        {
            if (job == null || !job.BeforeEntry || job.WorkStarted) return;
            job.WithSetup = withSetup;
            job.ItemIn = true;
            job.WorkStarted = true;
            job.Assignment?.StopTimer();
            if (job.Assignment != null) job.Assignment.Item = item;
            if (Release == ProcessRelease.AtElement && HoldFor == null && item != null)
            {
                if (!traveling.TryGetValue(item, out var jobs)) traveling[item] = jobs = new List<ResourceJob>();
                jobs.Add(job);
            }
            Next(job);
        }

        /// <summary>Asks for the job's resources; <paramref name="onReady"/> runs when they are assigned (maybe at once).</summary>
        public ResourceJob Acquire(Element station, Item item, Action onReady) => Acquire(station, item, onReady, double.NaN);

        /// <summary>As <see cref="Acquire(Element, Item, Action)"/>, with the time of the oldest item the job works for
        /// (<see cref="ResourceRequest.OldestItemTime"/>, list field <c>oldestItemTime</c>; NaN = the request time).</summary>
        public ResourceJob Acquire(Element station, Item item, Action onReady, double oldestItemTime)
            => Acquire(station, item, onReady, oldestItemTime, false);

        /// <summary>As <see cref="Acquire(Element, Item, Action)"/> for a job whose resources first set the station up
        /// (<see cref="ResourceJob.WithSetup"/>): <paramref name="onReady"/> starts the setup; the station calls
        /// <see cref="SetupEnded"/> when it is over, then processes with the same resources (no new request).</summary>
        public ResourceJob AcquireWithSetup(Element station, Item item, Action onReady)
            => Acquire(station, item, onReady, double.NaN, true);

        private ResourceJob Acquire(Element station, Item item, Action onReady, double oldestItemTime, bool withSetup,
                                    bool beforeEntry = false, Item priorityOf = null)
        {
            if (onReady == null) throw new ArgumentNullException(nameof(onReady));
            var assignment = new Assignment { Owner = this, Station = station, Item = item, OnReady = onReady, LimitItem = priorityOf ?? item };
            var request = new ResourceRequest(station, Task, assignment.OnAssigned, requirements);   // Setup for a setup crew
            request.Priority = Priority?.Invoke(priorityOf ?? item) ?? 1;
            request.Preempt = preempt;
            request.Location = station.Location;   // "nearest operator" rules
            request.OldestItemTime = oldestItemTime;
            request.ServeWhileDown = ServeWhileDown;
            var job = assignment.Job = new ResourceJob(request) { WithSetup = withSetup, Assignment = assignment, BeforeEntry = beforeEntry };
            if (Release == ProcessRelease.AtElement && HoldFor == null && item != null)
            {
                if (!traveling.TryGetValue(item, out var jobs)) traveling[item] = jobs = new List<ResourceJob>();
                jobs.Add(job);
            }
            // a stopped station's request waits: the dispatcher tries again when the station resumes
            if (station != null && !ServeWhileDown && watched.Add(station))
            {
                if (UsesParts) { var teams = Teams; station.Resumed += (e, token) => { foreach (var d in teams) d.Retry(); }; }
                else station.Resumed += (e, token) => Dispatcher.Retry();
            }
            if (UsesParts) assignment.AskParts();   // per team all at once, or in order
            else Dispatcher.Request(request);
            if (MaxWait != null) assignment.StartTimer();   // still waiting: its max wait starts now
            return job;
        }

        // One job's assignment: its state, read by the resources' task steps through these methods (no closures).
        // It is also the owner of the job's tasks when one is interrupted.
        internal sealed class Assignment : ITaskInterruption
        {
            public StationResources Owner; public Element Station; public Item Item; public Action OnReady; public ResourceJob Job;
            public Item LimitItem;                    // the item its max wait is evaluated for (the offered one)
            private int walking;

            // ── max wait ──
            private WaitTimer timer;
            private bool withdrawn;                   // gave up: its members go free, its steps pass, nothing comes back

            // still waiting: for its resources (until every requirement is granted), and, before entry, for its item
            private bool Waiting => !withdrawn && !cancelled && (Job.BeforeEntry ? !Job.ItemIn : Job.Request.State == RequestState.Pending);

            internal void StartTimer()
            {
                if (timer != null || !Waiting) return;
                timer = WaitTimer.Start(Owner.Dispatcher.Clock, Owner.MaxWait, LimitItem, Station, null, TimedOut);
                if (timer != null) Owner.timed.Add(this);
            }

            internal void StopTimer()
            {
                if (timer == null) return;
                timer.Cancel();
                timer = null;
                Owner.timed.Remove(this);
            }

            private void TimedOut(WaitTimer t)
            {
                if (t != timer) return;
                timer = null;
                Owner.timed.Remove(this);
                if (!Waiting) return;
                var action = Owner.MaxWait.OnTimeout;
                Station?.RecordTimeout(WaitKind.Resource, LimitItem, Owner.Dispatcher.Clock.GetSimulationTime() - t.Started, action);
                if (action == TimeoutAction.KeepWaiting) return;
                Withdraw();
                if (Job.BeforeEntry) { Owner.DropAhead(Job, Station); return; }   // no item yet: asked again at the next offer
                if (action == TimeoutAction.Reroute && Item != null && Station is ITimeoutRerouting rerouting && rerouting.JobTimedOut(Job))
                {
                    Owner.ForgetTraveling(Job);   // its item left without it: nothing comes back at the release point
                    return;
                }
                Owner.AskAgain(this);
            }

            // GiveUp: the pending requests withdrawn, the members already granted free — at once when they hold at the
            // station, when they get there if they are on their way (their remaining steps pass)
            private void Withdraw()
            {
                withdrawn = true;
                StopTimer();
                if (parts != null)
                {
                    foreach (var part in parts)
                        if (part.Request.State == RequestState.Pending) partTeams[part].Cancel(part.Request);
                }
                else if (Job.Request.State == RequestState.Pending) Owner.Dispatcher.Cancel(Job.Request);
                if (takeOvers != null)
                {
                    foreach (var r in takeOvers) (r.Dispatcher ?? Owner.Dispatcher).Cancel(r);
                    takeOvers.Clear();
                }
                Owner.FreeWorkPoints(Job);
                var old = Job.Tasks.ToArray();
                Job.Tasks.Clear();
                foreach (var task in old)
                {
                    if (task != null) task.Withdrawn = true;   // not a completed task
                    var ex = task?.Executor;
                    for (int guard = 0; ex != null && ex.CurrentTask == task && guard < 16; guard++)
                        if (!ex.Continue()) break;
                }
            }
            private bool ready;                       // every resource arrived once: the station started the job
            private List<ResourceRequest> takeOvers;  // requests for resources in place of lost ones, not served yet

            // ── parts: requirements of several teams, or taken in order ──
            private sealed class Part
            {
                public Assignment Owner; public ResourceRequest Request; public int[] Slots;
                public void OnAssigned(ResourceRequest r, IReadOnlyList<TaskExecutor> got) => Owner.PartAssigned(this, got);
            }
            private List<Part> parts;
            private int nextPart, partsAssigned;
            private bool cancelled;
            private Func<bool> allReady;              // the hold of a part's members until every part is there

            private bool AllReady() => ready || withdrawn;
            private bool IsAllReadyStep(TaskStep step) => allReady != null && ReferenceEquals(step.Ready, allReady);

            // Splits the job's requirements into parts and asks for them: one per requirement in order (InOrder), or one
            // per team, all served together (AllAtOnce: a joint request).
            internal void AskParts()
            {
                var reqs = Owner.requirements.Count > 0 ? (IReadOnlyList<ResourceRequirement>)Owner.requirements
                                                         : new[] { new ResourceRequirement() };
                int total = 0;
                var offset = new int[reqs.Count];
                for (int i = 0; i < reqs.Count; i++) { offset[i] = total; total += reqs[i].Count; }
                var assigned = new List<TaskExecutor>(total);
                for (int i = 0; i < total; i++) { assigned.Add(null); Job.Tasks.Add(null); }
                Job.Request.Assigned = assigned;
                Job.Request.RequestTime = Owner.Dispatcher.Clock.GetSimulationTime();
                allReady = AllReady;
                walking = total + 1;   // one per member, plus one released once every part is assigned
                parts = new List<Part>();
                if (Owner.Order == AcquireOrder.InOrder)
                {
                    for (int i = 0; i < reqs.Count; i++)
                        parts.Add(NewPart(reqs[i].Team ?? Owner.Dispatcher, new List<ResourceRequirement> { reqs[i] }, Range(offset[i], reqs[i].Count)));
                    nextPart = 0;
                    Post(parts[nextPart++]);
                    return;
                }
                // all at once: one part per team, in the order of the first requirement of each
                var teams = new List<ResourceDispatcher>();
                foreach (var r in reqs) { var d = r.Team ?? Owner.Dispatcher; if (!teams.Contains(d)) teams.Add(d); }
                var joint = new JointRequest { Posting = true };
                foreach (var d in teams)
                {
                    var list = new List<ResourceRequirement>(); var slots = new List<int>();
                    for (int i = 0; i < reqs.Count; i++)
                        if ((reqs[i].Team ?? Owner.Dispatcher) == d) { list.Add(reqs[i]); slots.AddRange(Range(offset[i], reqs[i].Count)); }
                    var part = NewPart(d, list, slots.ToArray());
                    part.Request.Joint = joint;
                    joint.Parts.Add(part.Request);
                    parts.Add(part);
                }
                nextPart = parts.Count;
                for (int i = 0; i < parts.Count; i++) Post(parts[i]);
                joint.Posting = false;
                parts[0].Request.Dispatcher.Retry();   // served now if every team has its members free
            }

            private static int[] Range(int from, int count)
            {
                var a = new int[count];
                for (int i = 0; i < count; i++) a[i] = from + i;
                return a;
            }

            private Part NewPart(ResourceDispatcher team, List<ResourceRequirement> reqs, int[] slots)
            {
                var part = new Part { Owner = this, Slots = slots };
                var m = Job.Request;
                part.Request = new ResourceRequest(Station, Owner.Task, part.OnAssigned, reqs)
                {
                    Priority = m.Priority, Preempt = m.Preempt, Location = m.Location, OldestItemTime = m.OldestItemTime,
                    ServeWhileDown = m.ServeWhileDown,
                };
                partTeams[part] = team;
                return part;
            }
            private readonly Dictionary<Part, ResourceDispatcher> partTeams = new Dictionary<Part, ResourceDispatcher>();

            private void Post(Part part) => partTeams[part].Request(part.Request);

            // A part's members: each gets its task — walk to the station (spatial), wait there (HELD) until every part is
            // there, then the job's work as for one request.
            private void PartAssigned(Part part, IReadOnlyList<TaskExecutor> got)
            {
                if (cancelled) return;
                var assigned = (List<TaskExecutor>)Job.Request.Assigned;
                for (int j = 0; j < got.Count && j < part.Slots.Length; j++)
                {
                    int k = part.Slots[j];
                    var ex = got[j];
                    assigned[k] = ex;
                    string at = Owner.WorkPointFor(Station, ex, Job);
                    var task = NewTask(Job.Request.Priority);
                    bool walks = Walks(ex, at);
                    if (walks) task.Travel(at).Callback(Arrived);
                    task.HoldUntil(allReady, ResourceStates.HELD);
                    AddWork(task);
                    Job.Tasks[k] = task;
                    ex.Execute(task);
                    if (!walks) Arrived();
                }
                partsAssigned++;
                if (nextPart < parts.Count) { Post(parts[nextPart++]); return; }   // in order: the next one now
                if (partsAssigned < parts.Count) return;
                Job.Request.State = RequestState.Assigned;
                Job.Request.AssignTime = Owner.Dispatcher.Clock.GetSimulationTime();
                if (!Job.BeforeEntry) StopTimer();   // every requirement granted: its wait ended
                Arrived();   // the guard: the job may start once every part is there
            }

            // The job is withdrawn (its station reset) while some parts wait: the pending parts are withdrawn and the
            // members already granted go free.
            internal void CancelParts()
            {
                cancelled = true;
                StopTimer();
                foreach (var part in parts)
                    if (part.Request.State == RequestState.Pending) partTeams[part].Cancel(part.Request);
                Job.ProcessEnded = Job.Released = Job.SetupEnded = true;
                ready = true;
                foreach (var task in Job.Tasks)
                {
                    var ex = task?.Executor;
                    for (int guard = 0; ex != null && ex.CurrentTask == task && guard < 16; guard++)
                        if (!ex.Continue()) break;
                }
            }

            internal bool HasPendingParts
            {
                get
                {
                    if (parts == null) return false;
                    foreach (var part in parts) if (part.Request.State == RequestState.Pending) return true;
                    return false;
                }
            }

            public void OnAssigned(ResourceRequest r, IReadOnlyList<TaskExecutor> got)
            {
                if (!Job.BeforeEntry) StopTimer();   // granted: its wait ended (before entry it waits for its item still)
                // Spatial resources first walk / travel to the station (if it has a location); the process starts
                // when every one of them has arrived.
                var at = new string[got.Count];
                for (int k = 0; k < got.Count; k++) at[k] = Owner.WorkPointFor(Station, got[k], Job);
                // One count per walker plus one for this loop, released after it: a walk of zero length ends inside its
                // own Execute, and the process must start once, after every resource has its task.
                walking = 1;
                for (int k = 0; k < got.Count; k++) if (Walks(got[k], at[k])) walking++;
                for (int k = 0; k < got.Count; k++)
                {
                    var ex = got[k];
                    var task = NewTask(r.Priority);
                    if (Walks(ex, at[k]))
                        task.Travel(at[k]).Callback(Arrived);
                    AddWork(task);
                    Job.Tasks.Add(task);
                    ex.Execute(task);
                }
                Arrived();
            }

            private TaskSequence NewTask(double priority)
            {
                var task = new TaskSequence(Owner.Task, Station) { Priority = priority, Preemptable = Owner.Preemptable };
                task.Interruption = this;
                return task;
            }

            private void AddWork(TaskSequence task, double ownTime = double.NaN)
            {
                if (Job.BeforeEntry && !Job.WorkStarted) task.HoldUntil(WorkStarted, ResourceStates.HELD);   // waiting for the part
                if (Owner.HoldFor != null)
                {
                    // their own time (a resource taking over goes on with what was left, or starts it again: Restart)
                    if (double.IsNaN(ownTime)) task.Process(HoldTime);
                    else task.Process(() => withdrawn ? 0 : ownTime);
                }
                else
                {
                    if (Job.BeforeEntry)
                    {
                        task.HoldUntil(SetupDoneOrNone, ElementState.SETUP);   // decided when the work starts
                        task.HoldUntil(ProcessBegun, ResourceStates.HELD);     // while others (or the machine) set up
                    }
                    else if (Job.WithSetup) task.HoldUntil(SetupEnded, ElementState.SETUP);   // they set the station up first
                    task.HoldUntil(ProcessEnded, Owner.WorkState);
                    if (Owner.Release == ProcessRelease.ItemLeaves || Owner.Release == ProcessRelease.AtElement)
                        task.HoldUntil(Released, ResourceStates.HELD);
                }
            }

            private void Arrived()
            {
                if (cancelled || withdrawn || --walking != 0) return;
                ready = true;
                if (parts != null)
                    // every part is there: the members waiting for the others go on to the job's work
                    foreach (var t in Job.Tasks)
                        if (t != null && t.Executor != null && t.Executor.CurrentTask == t && t.CurrentStep >= 0 && !t.IsDone
                            && IsAllReadyStep(t.Steps[t.CurrentStep]))
                            t.Executor.Continue();
                // the station starts its process inside: it gives this job its work (ProcessStarted) before Acquire returns
                var before = Owner.starting;
                Owner.starting = Job;
                try { OnReady(); }
                finally { Owner.starting = before; }
            }

            // ── interruption ────────────────────────────────────────────

            private bool Pausable => Owner.HoldFor == null && Job.Work != null && !Job.Work.IsDone && !Job.Work.IsCancelled
                                     && Owner.Dispatcher != null && Owner.InterruptibleOf(Station) != null;

            // HELD after the process (ItemLeaves): the member keeps the job until its item leaves; taken, another replaces it.
            private bool HeldAfter(TaskStep step)
                => step.Kind == StepKind.Hold && Job.ProcessEnded && !Job.Released && Owner.Release == ProcessRelease.ItemLeaves;

            // Walking to the station; working while the station's process runs (a station that can pause that job); its
            // own time (HoldFor); HELD after the process, replaced (ItemLeaves). Never Abort: a station's job has an item.
            public bool CanInterrupt(TaskSequence task, TaskStep step, PreemptMode mode)
            {
                if (mode == PreemptMode.Abort || withdrawn) return false;
                if (IsAllReadyStep(step)) return true;   // HELD waiting for the job's other parts: another one comes
                if (Owner.HoldFor != null) return step.Kind == StepKind.Travel || step.Kind == StepKind.Process;
                if (Job.ProcessEnded) return HeldAfter(step);
                if (step.Kind == StepKind.Travel) return true;
                return step.Kind == StepKind.Hold && Pausable;
            }

            // A member's Immediate stop interrupts its work at the station (not its walk there: the walk pauses).
            public bool InterruptsOnStop(TaskSequence task, TaskStep step)
            {
                if (withdrawn || Job.Request.Preempt == PreemptMode.None || IsAllReadyStep(step)) return false;
                if (Owner.HoldFor != null) return step.Kind == StepKind.Process;
                if (Job.ProcessEnded) return HeldAfter(step);
                return step.Kind == StepKind.Hold && Pausable;
            }

            public void Interrupted(TaskSequence task, TaskExecutor executor, PreemptMode mode, bool byStop)
            {
                if (withdrawn) return;
                int k = Job.Tasks.IndexOf(task);
                if (k < 0) return;
                Owner.FreeWorkPoint(executor, Job);
                Job.Interruptions++;
                Owner.Interruptions++;
                if (byStop) mode = Job.Request.Preempt;   // a stop: the job's own mode decides how it goes on
                var kind = task.Steps[task.CurrentStep].Kind;
                if (IsAllReadyStep(task.Steps[task.CurrentStep])) walking++;   // it had arrived: the one taking over must arrive
                double ownTime = double.NaN;
                if (Owner.HoldFor != null)
                {
                    // its own time: the one taking over works what was left (Resume) or the whole time again (Restart);
                    // the station's process goes on meanwhile (the item stays for it)
                    if (kind == StepKind.Travel && ownTimes != null && k < ownTimes.Length) ownTime = ownTimes[k];   // taken on its way
                    if (kind == StepKind.Process)
                    {
                        ownTime = mode == PreemptMode.Restart ? executor.InterruptedDuration : executor.InterruptedRemaining;
                        if (double.IsNaN(ownTime) || ownTime < 0) ownTime = 0;
                        Pause();
                    }
                }
                else if (Job.ProcessEnded) Pause();   // HELD after the process: another member replaces it (no time to resume)
                else if (Job.Work != null)
                {
                    if (mode == PreemptMode.Restart) Job.restart = true;
                    // working at the station: its process is paused until a resource is back (one taking over that is
                    // interrupted on its way there was already missing)
                    if (kind == StepKind.Hold) Pause();
                }
                // else: still walking to the station; the one taking over arrives in its place
                Owner.RequestAgain(this, k, ownTime);
            }

            public void Paused(TaskSequence task, TaskExecutor executor, bool paused)
            {
                if (withdrawn) return;
                bool restart = Job.Request.Preempt == PreemptMode.Restart;
                if (paused)
                {
                    Job.Interruptions++;
                    Owner.Interruptions++;
                    if (restart && Owner.HoldFor == null) Job.restart = true;
                    Pause();
                }
                else
                {
                    // its own time (HoldFor), paused by its stop: Restart starts it again now
                    if (restart && Owner.HoldFor != null && executor.CurrentTask == task) executor.RestartStep();
                    Back();
                }
            }

            private void Pause()
            {
                if (Job.missing++ > 0) return;
                var clock = Owner.Dispatcher.Clock;
                Job.Pause(clock.GetSimulationTime());
                Owner.PauseStarted(Job);
                // the station's process waits for the resource; not its own time (HoldFor) nor HELD after the process
                Job.heldProcess = Owner.HoldFor == null && !Job.ProcessEnded && Job.Work != null;
                if (!Job.heldProcess) return;
                Job.Work.Hold(clock);
                Owner.InterruptibleOf(Station).JobPaused(Job, true, false);
            }

            // A lost resource is back (or another one in its place): when all are, the process goes on.
            private void Back()
            {
                if (Job.missing == 0 || --Job.missing > 0) return;
                var clock = Owner.Dispatcher.Clock;
                double now = clock.GetSimulationTime();
                Owner.PauseEnded(Job, now);   // before Resumed: it reads the pause start
                Job.Resumed(now);
                bool restart = Job.restart;
                Job.restart = false;
                if (!Job.heldProcess) return;
                Job.heldProcess = false;
                Job.Work.Release(clock, restart);
                Owner.InterruptibleOf(Station).JobPaused(Job, false, restart);
            }

            // The job is released (its item left): a pause still open ends, the resources asked for in place of lost ones
            // are not needed any more (HELD after the process, replaced). Not with HoldFor: their own time is still due.
            internal void ReleasedNow()
            {
                if (Owner.HoldFor != null) return;
                if (takeOvers != null)
                {
                    foreach (var r in takeOvers) (r.Dispatcher ?? Owner.Dispatcher).Cancel(r);
                    takeOvers.Clear();
                }
                if (Job.missing == 0) return;
                Job.missing = 1;
                Back();
            }

            internal void Asked(ResourceRequest r) => (takeOvers ?? (takeOvers = new List<ResourceRequest>())).Add(r);

            // The resource taking over slot k of the job: it travels to the station (spatial) and works for the job as
            // the one it replaces did.
            internal void TakeOver(int k, TaskExecutor ex, ResourceRequest r, double ownTime)
            {
                takeOvers?.Remove(r);
                var assigned = Job.Request.Assigned as List<TaskExecutor>;
                if (assigned != null && k < assigned.Count) assigned[k] = ex;
                string at = Owner.WorkPointFor(Station, ex, Job);
                bool started = ready;
                var task = NewTask(Job.Request.Priority);
                bool walks = Walks(ex, at);
                if (walks) { if (started) task.Travel(at).Callback(Back); else task.Travel(at).Callback(Arrived); }
                if (parts != null && !started) task.HoldUntil(allReady, ResourceStates.HELD);   // the other parts are not all there
                AddWork(task, ownTime);
                Job.Tasks[k] = task;
                if (Owner.HoldFor != null) SlotOwnTime(k, ownTime);
                ex.Execute(task);
                if (!walks) { if (started) Back(); else Arrived(); }
            }

            // HoldFor: the own time a resource taking over slot k goes on with (NaN = sampled), kept if it is taken on its way.
            private double[] ownTimes;
            private void SlotOwnTime(int k, double t)
            {
                if (ownTimes == null) { ownTimes = new double[Job.Tasks.Count]; for (int i = 0; i < ownTimes.Length; i++) ownTimes[i] = double.NaN; }
                if (k < ownTimes.Length) ownTimes[k] = t;
            }
            private double HoldTime() => withdrawn ? 0 : SimuLean.Expressions.Samplers.Sample(Owner.HoldFor, Item, Station);
            // a withdrawn assignment's steps pass (its members go free; the job has a new assignment)
            private bool ProcessEnded() => withdrawn || Job.ProcessEnded;
            private bool SetupEnded() => withdrawn || Job.SetupEnded;
            private bool SetupDoneOrNone() => withdrawn || !Job.WithSetup || Job.SetupEnded;
            private bool WorkStarted() => withdrawn || Job.WorkStarted;
            private bool ProcessBegun() => withdrawn || Job.ProcessBegun || Job.ProcessEnded;
            private bool Released() => withdrawn || Job.Released;
        }

        private static bool Walks(TaskExecutor ex, string at)
            => ex.Mode == ResourceMode.Spatial && at != null && ex.Location != at;

        // An interrupted job asks again for the resource of its slot k (the requirement it met), with its own priority
        // and mode.
        private void RequestAgain(Assignment assignment, int k, double ownTime)
        {
            var job = assignment.Job;
            ResourceRequirement need = null;
            int n = 0;
            var reqs = job.Request.Requirements;
            for (int i = 0; i < reqs.Count && need == null; i++)
            {
                n += reqs[i].Count;
                if (k < n) need = reqs[i];
            }
            var team = need?.Team ?? Dispatcher;   // the requirement's own team
            var again = new TakeOverRequest { Assignment = assignment, Slot = k, OwnTime = ownTime };
            var request = new ResourceRequest(assignment.Station, Task, again.OnAssigned,
                                              new ResourceRequirement(1, need?.Query, need?.Label))
            {
                Priority = job.Request.Priority, Preempt = job.Request.Preempt, Location = job.Request.Location,
                OldestItemTime = job.Request.OldestItemTime, ServeWhileDown = job.Request.ServeWhileDown,
            };
            assignment.Asked(request);
            team.Request(request);
        }

        private sealed class TakeOverRequest
        {
            public Assignment Assignment; public int Slot; public double OwnTime;
            public void OnAssigned(ResourceRequest r, IReadOnlyList<TaskExecutor> got) => Assignment.TakeOver(Slot, got[0], r, OwnTime);
        }

        // The work point an interrupted resource held for the job is free for the one taking over.
        private void FreeWorkPoint(TaskExecutor ex, ResourceJob job)
        {
            if (occupied.Count == 0) return;
            string point = null;
            foreach (var kv in occupied) if (kv.Value.who == ex && kv.Value.job == job) { point = kv.Key; break; }
            if (point != null) occupied.Remove(point);
        }

        // Work points taken: point → the resource working there and the job it works for, until that job releases
        // it (keyed by job: with HoldFor a resource may already work there for a later job when an earlier one is
        // released, and the point must stay taken).
        private readonly Dictionary<string, (TaskExecutor who, ResourceJob job)> occupied = new Dictionary<string, (TaskExecutor, ResourceJob)>();

        // Where a resource of this station stands: a free work point (in order; all taken → shared, in turn), else the
        // station's location.
        private string WorkPointFor(Element station, TaskExecutor ex, ResourceJob job)
        {
            var points = station.WorkLocations;
            if (points.Count == 0 || ex.Mode != ResourceMode.Spatial) return station.Location;
            foreach (string p in points)
                if (!occupied.TryGetValue(p, out var taken) || taken.who == ex) { occupied[p] = (ex, job); return p; }
            string shared = points[occupied.Count % points.Count];
            return shared;
        }

        /// <summary>The station's process ended: resources released now (<see cref="ProcessRelease.EndOfProcess"/>)
        /// or kept, HELD, until <see cref="ItemLeft"/>.</summary>
        public void ProcessEnded(ResourceJob job)
        {
            if (job == null || job.ProcessEnded || job.Request.State != RequestState.Assigned) return;
            job.ProcessEnded = true;
            if (Release == ProcessRelease.EndOfProcess) job.Released = true;
            Next(job);
        }

        /// <summary>The setup of a job acquired with <see cref="AcquireWithSetup"/> is over: its resources go on to the
        /// process (their hold in SETUP ends; the next one, in PROCESSING, lasts until <see cref="ProcessEnded"/>).</summary>
        public void SetupEnded(ResourceJob job)
        {
            if (job == null || !job.WithSetup || job.SetupEnded || job.Request.State != RequestState.Assigned) return;
            job.SetupEnded = true;
            Next(job);
        }

        /// <summary>The job's item left the station: resources kept until now are released.</summary>
        public void ItemLeft(ResourceJob job)
        {
            if (job == null || !job.ProcessEnded || job.Released || Release == ProcessRelease.AtElement) return;
            job.Released = true;
            Next(job);
        }

        /// <summary>Withdraws a job still waiting for resources (e.g. the station was reset).</summary>
        public void Cancel(ResourceJob job)
        {
            job?.Assignment?.StopTimer();
            if (job == null || job.Request.State != RequestState.Pending) return;
            if (job.Assignment != null && job.Assignment.HasPendingParts) job.Assignment.CancelParts();
            else Dispatcher.Cancel(job.Request);
        }

        private void Next(ResourceJob job)
        {
            if (job.Released) job.Assignment?.ReleasedNow();
            if (job.Released && occupied.Count > 0)   // its work points are free again (not one taken by a later job)
            {
                List<string> mine = null;
                foreach (var kv in occupied) if (kv.Value.job == job) (mine ?? (mine = new List<string>())).Add(kv.Key);
                if (mine != null) foreach (string p in mine) occupied.Remove(p);
            }
            // only an executor still on this job's task (with HoldFor it may have finished and taken another job)
            foreach (var task in job.Tasks)
                if (task != null && task.Executor != null && task.Executor.CurrentTask == task) task.Executor.Continue();
        }
    }
}
