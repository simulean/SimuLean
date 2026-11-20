using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>What a <see cref="Separator"/> does with each item.</summary>
    public enum SeparatorMode
    {
        /// <summary>The item is a container (a pallet, a tote, a Combiner's batch): its <see cref="Item.subItems"/> leave
        /// through the output, the empty container through the <c>empties</c> port (else the output, after them).</summary>
        Unpack = 0,
        /// <summary>The item leaves as <see cref="Separator.SplitQuantity"/> items: itself and copies of it (its type and
        /// labels).</summary>
        Split = 1,
    }

    /// <summary>Order in which a <see cref="Separator"/> takes the contents out of a container.</summary>
    public enum UnpackOrder
    {
        /// <summary>The container's first sub-item first (the order it was filled in).</summary>
        Fifo = 0,
        /// <summary>The last sub-item first (the top of a stack).</summary>
        Lifo = 1,
    }

    /// <summary>
    /// <b>Separator</b> (Core; the inverse of the <see cref="Combiner"/>): holds
    /// up to <see cref="Capacity"/> items, in arrival order, and after <see cref="ProcessTime"/> (through
    /// <see cref="Element.ScheduleWork"/>, so stops pause it) either <b>unpacks</b> each one — a container whose
    /// <see cref="Item.subItems"/> are its contents, e.g. what a Combiner in batch mode made — or <b>splits</b> it into
    /// <see cref="SplitQuantity"/> items. Everything then leaves together, in order, as downstream takes it.
    /// <list type="bullet">
    /// <item><b>Unpack</b>: the contents leave in <see cref="Order"/> through the output; the empty container leaves
    /// through the <see cref="EmptiesPort"/> (port <c>empties</c>, an <see cref="OutboundPort"/>) when it is wired, else
    /// through the output after its contents.</item>
    /// <item><b>Split</b>: the item and N − 1 copies (its type and labels; <see cref="ItemSplit"/> lets a view draw them),
    /// N = <see cref="SplitQuantity"/> (an expression may read the item; at least 1).</item>
    /// </list>
    /// A subclass specialises the release (<see cref="LineSideUnpacker"/>: one piece at a time with a piece time, a
    /// container change time, empty containers keeping their place) through the protected settings below.
    /// <para><b>Statistics (link layer only).</b> Unpack opts in to
    /// <see cref="Statistics.ElementStatsCollector.CountSubItemsOnEntry"/>: a container entering records itself and its
    /// contents, so <b>input = containers + pieces</b> and <b>output = pieces + containers</b>. Split records each copy as
    /// entering when it is made (an internal generator through a <see cref="DirectLink"/>): <b>input = items + copies</b>,
    /// <b>output = items + copies</b>. Content = <see cref="GetQueueLength"/>. Own counters (<see cref="IElementMetrics"/>):
    /// <c>released</c>, <c>carriers_emptied</c> (Unpack) / <c>split_copies</c> (Split). States: IDLE (empty), PROCESSING,
    /// SETUP (container change), BLOCKED (pieces ready, downstream refuses).</para>
    /// </summary>
    public class Separator : Element, IPortedElement, IElementMetrics
    {
        public const string PortEmpties = "empties";

        // An item held: its pieces still to leave (Unpack: the container's sub-items; Split: the item and its copies).
        private sealed class Job
        {
            public Item Item;
            public List<Item> Pieces;
            public bool Changed, Started, Processed;
        }

        private readonly List<Job> jobs = new List<Job>();          // arrival order; [0] is being worked on
        private readonly OutboundPort emptiesPort;
        private readonly DirectLink toEmpties;
        private readonly OutboundPort copySource;                    // generator of the split copies (not a port)
        private readonly DirectLink copyLink;
        private readonly List<KeyValuePair<string, Element>> ports;
        private Item ready;                 // one by one: the piece prepared, waiting for downstream
        private WorkHandle work;            // the running time
        private bool changing, kickPending, firstDone, registering;
        private int copySeq;

        /// <param name="capacity">Items held at once (≥ 1).</param>
        public Separator(string name, SimClock clock, int capacity = 1, SeparatorMode mode = SeparatorMode.Unpack, IVElement vElement = null)
            : base(name, clock, vElement)
        {
            Capacity = Math.Max(1, capacity);
            Mode = mode;
            StatsCollector.CountSubItemsOnEntry = mode == SeparatorMode.Unpack;
            emptiesPort = new OutboundPort(name + "/empties", clock);
            emptiesPort.ItemLeft += (p, it, discarded) => OnEmptyLeft();
            toEmpties = new DirectLink(emptiesPort);
            copySource = new OutboundPort(name + "/copies", clock, generates: true);
            copyLink = new DirectLink(this);
            ports = new List<KeyValuePair<string, Element>> { new KeyValuePair<string, Element>(PortEmpties, emptiesPort) };
        }

        // ── configuration ────────────────────────────────────────────────────

        /// <summary>Unpack or Split (fixed when built).</summary>
        public SeparatorMode Mode { get; }

        /// <summary>Items held at once (full containers being emptied, plus empty ones waiting to be collected when
        /// <see cref="KeepEmptiesInPlace"/>).</summary>
        public int Capacity { get; set; }

        public UnpackOrder Order { get; set; } = UnpackOrder.Fifo;

        /// <summary>Time per item before anything of it leaves (null = none). An expression gets the item.</summary>
        public IRandomSampler ProcessTime { get; set; }

        /// <summary>Split: the number of items the item leaves as (itself included; null = 1). An expression gets the
        /// item.</summary>
        public IRandomSampler SplitQuantity { get; set; }

        // ── settings a subclass specialises ──────────────────────────────────

        /// <summary>One piece at a time, each after <see cref="PieceTimeSampler"/>, the next prepared when the previous has
        /// left (pull-driven); false (default) = all together when the process time ends.</summary>
        protected bool ReleaseOneByOne { get; set; }

        /// <summary>One by one: time to take each piece out (null = none). An expression gets the piece.</summary>
        protected IRandomSampler PieceTimeSampler { get; set; }

        /// <summary>Time before an item that is not the first one (a container change; null = none).</summary>
        protected IRandomSampler ChangeTimeSampler { get; set; }

        /// <summary>The change time comes before the run's first item too (default false: the first item is taken as
        /// already in place).</summary>
        protected bool ChangeBeforeFirst { get; set; }

        /// <summary>Unpack: copy the container's labels a piece does not have onto it.</summary>
        protected bool CopyContainerLabels { get; set; }

        /// <summary>An empty container keeps its place until it has left the empties port.</summary>
        protected bool KeepEmptiesInPlace { get; set; }

        // ── views ────────────────────────────────────────────────────────────

        /// <summary>The port the empty containers leave through when it is wired (else the output).</summary>
        public OutboundPort EmptiesPort => emptiesPort;

        /// <summary>Items held, arrival order (the first is being worked on).</summary>
        public IEnumerable<Item> Items { get { foreach (var j in jobs) yield return j.Item; } }

        /// <summary>Pieces still inside the items held (Unpack: the contents; Split: the copies made), not the items
        /// themselves.</summary>
        public IEnumerable<Item> HeldPieces
        {
            get
            {
                foreach (var j in jobs)
                    if (j.Pieces != null)
                        foreach (var p in j.Pieces) if (p != j.Item) yield return p;
            }
        }

        /// <summary>One by one: the piece prepared and waiting for downstream (null = none).</summary>
        public Item ReadyPiece => ready;

        /// <summary>The item being worked on (null = none).</summary>
        public Item CurrentItem => jobs.Count > 0 ? jobs[0].Item : null;

        public int Released { get; private set; }
        public int CarriersEmptied { get; private set; }
        public int SplitCopies { get; private set; }

        /// <summary>Pieces held over time (contents of the containers and pieces made; containers not counted); reset by
        /// the warm-up.</summary>
        public Statistics.StatTimeWeightedVariable PiecesHeld { get; } = new Statistics.StatTimeWeightedVariable();

        /// <summary>Empty containers still waiting in the empties port.</summary>
        public int EmptiesWaiting => emptiesPort.GetQueueLength();

        public event Action<Separator, Item> ItemArrived;
        /// <summary>The process time of an item started: (separator, item, start, duration) — a view moves the pieces off
        /// it meanwhile.</summary>
        public event Action<Separator, Item, double, double> ProcessStarted;
        /// <summary>A piece left: (separator, the item it came from, piece).</summary>
        public event Action<Separator, Item, Item> PieceReleased;
        /// <summary>A container was emptied (before it leaves).</summary>
        public event Action<Separator, Item> CarrierEmptied;
        /// <summary>Split: a copy was made, (separator, original, copy) — before it is offered; a view draws it here.</summary>
        public event Action<Separator, Item, Item> ItemSplit;

        public IReadOnlyList<KeyValuePair<string, Element>> Ports => ports;
        public Element GetPort(string port) => port == PortEmpties ? emptiesPort : null;

        // ── Element contract ─────────────────────────────────────────────────

        public override void Start()
        {
            base.Start();
            jobs.Clear();
            ready = null; work = null; changing = false; firstDone = false; kickPending = false; registering = false;
            Released = 0; CarriersEmptied = 0; SplitCopies = 0; copySeq = 0;
            PiecesHeld.Reset();
            PiecesHeld.Update(0, simClock.GetSimulationTime());
            emptiesPort.Start();
            copySource.Start();
        }

        private int Occupied => jobs.Count + (KeepEmptiesInPlace ? emptiesPort.GetQueueLength() : 0);

        /// <summary>The places of <see cref="Capacity"/> in use: the items (Unpack: the carriers) held, and the empty
        /// carriers that keep their place until collected. The content (<see cref="GetQueueLength"/>) counts the pieces
        /// inside too, so it may exceed the capacity (the diagnosis says both).</summary>
        public int PlacesUsed => Occupied;

        /// <summary>Items held (Unpack: the carriers being emptied), not their pieces.</summary>
        public int ItemsHeldCount => jobs.Count;

        public override bool CheckAvailability(Item theItem) => registering || Occupied < Capacity;

        public override int GetFreeCapacity() => Math.Max(0, Capacity - Occupied);

        /// <summary>Items held and the pieces still in them (incl. a ready piece) — equal to the content statistic.</summary>
        public override int GetQueueLength()
        {
            int n = ready != null ? 1 : 0;
            foreach (var j in jobs)
                n += Mode == SeparatorMode.Unpack ? 1 + (j.Pieces?.Count ?? 0) : (j.Processed ? j.Pieces.Count : 1);
            return n;
        }

        private bool warnedEmpty;

        public override bool Receive(Item theItem)
        {
            if (theItem == null) return false;
            if (registering) return true;   // a split copy entering (statistics only): it is already in its job
            if (!CheckAvailability(theItem)) return false;
            var job = new Job { Item = theItem };
            if (Mode == SeparatorMode.Unpack)
            {
                job.Pieces = theItem.subItems ?? new List<Item>();
                if (job.Pieces.Count > 0) PiecesHeld.Update(job.Pieces.Count, simClock.GetSimulationTime());
                else if (!warnedEmpty)
                {
                    // an empty carrier leaves at once and nothing says why: a Combiner packing pieces without batchMode
                    // detaches them, and the line then starved with no warning anywhere
                    warnedEmpty = true;
                    simClock.Context.Warn($"[SimuLean] t={simClock.GetSimulationTime().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
                                          $"'{Name}' received '{theItem.Name}' with nothing in it: it leaves empty (warned once). " +
                                          "A Combiner packs its components into the carrier only with batchMode = true.");
                }
            }
            jobs.Add(job);
            vElement.LoadItem(theItem);
            ItemArrived?.Invoke(this, theItem);
            // the work runs as its own (same-time) event, never inside the link's SendItem: the item's entry (and its
            // contents') is recorded before anything leaves
            Kick();
            UpdateState();
            return true;
        }

        public override bool Unblock()
        {
            bool sent = TrySendReady();
            Advance();
            return sent;
        }

        public virtual void ReportMetrics(Action<string, double> set)
        {
            set(Results.ResultMetrics.Released, Released);
            if (Mode == SeparatorMode.Unpack) set(Results.ResultMetrics.CarriersEmptied, CarriersEmptied);
            else set(Results.ResultMetrics.SplitCopies, SplitCopies);
        }

        /// <summary>Warm-up: counters, the pieces-held level and the empties port's statistics restart.</summary>
        public override void ResetStatistics()
        {
            base.ResetStatistics();
            Released = 0; CarriersEmptied = 0; SplitCopies = 0;
            PiecesHeld.Restart(simClock.GetSimulationTime());
            emptiesPort.ResetStatistics();
        }

        // ── flow ─────────────────────────────────────────────────────────────

        private void Kick()
        {
            if (kickPending) return;
            kickPending = true;
            ScheduleWork(new ActionEvent(() => { kickPending = false; Advance(); }), 0);
        }

        /// <summary>Moves on as far as possible: times, pieces out, empty containers out.</summary>
        private void Advance()
        {
            while (ready == null && work == null && !kickPending && jobs.Count > 0)
            {
                Job job = jobs[0];
                if (!job.Processed)
                {
                    if ((firstDone || ChangeBeforeFirst) && ChangeTimeSampler != null && !job.Changed)
                    {
                        job.Changed = true;
                        StartWork(Sample(ChangeTimeSampler, job.Item), true);
                        vElement.ReportState("Change");
                        break;
                    }
                    job.Changed = true;
                    if (ProcessTime != null && !job.Started)
                    {
                        job.Started = true;   // the pieces are released when the process time ends
                        double d = Sample(ProcessTime, job.Item);
                        ProcessStarted?.Invoke(this, job.Item, simClock.GetSimulationTime(), d);
                        StartWork(d, false, Processed);
                        break;
                    }
                    Processed();
                    continue;
                }
                if (job.Pieces.Count == 0)
                {
                    if (!Finish(job)) break;   // the empty container could not leave yet
                    continue;
                }
                if (ReleaseOneByOne)
                {
                    StartWork(PieceTimeSampler != null ? Sample(PieceTimeSampler, NextPiece(job)) : 0, false, PieceDone);
                    break;
                }
                if (!SendAll(job)) break;   // downstream refuses: the rest wait
            }
            UpdateState();
        }

        private Action afterWork;

        private void StartWork(double delay, bool change, Action done = null)
        {
            changing = change;
            afterWork = done;
            work = ScheduleWork(new ActionEvent(EndWork), delay);
        }

        private void EndWork()
        {
            work = null;
            bool wasChange = changing;
            changing = false;
            var done = afterWork;
            afterWork = null;
            if (!wasChange) done?.Invoke();
            Advance();
        }

        // The process time of the first job ended (or there is none): Split makes its copies.
        private void Processed()
        {
            if (jobs.Count == 0) return;
            Job job = jobs[0];
            job.Processed = true;
            if (Mode == SeparatorMode.Unpack) return;
            int n = SplitQuantity != null ? Math.Max(1, (int)Math.Round(Sample(SplitQuantity, job.Item))) : 1;
            job.Pieces = new List<Item> { job.Item };
            double now = simClock.GetSimulationTime();
            registering = true;
            try
            {
                for (int k = 1; k < n; k++)
                {
                    var copy = new Item(now);
                    copy.SetId(job.Item.Name, ++copySeq, 0);
                    copy.CopyLabelsFrom(job.Item);
                    job.Pieces.Add(copy);
                    ItemSplit?.Invoke(this, job.Item, copy);
                    copyLink.SendItem(copy, copySource);   // enters through a link (statistics)
                    SplitCopies++;
                }
            }
            finally { registering = false; }
            PiecesHeld.Update(n, now);
        }

        // One by one: the piece time ended — the next piece is ready.
        private void PieceDone()
        {
            if (jobs.Count == 0) return;
            Job job = jobs[0];
            Item piece = NextPiece(job);
            if (piece == null) return;
            TakeOut(job, piece);
            ready = piece;
            vElement.ReportState("PieceReady");
            TrySendReady();
        }

        // All together: every piece in order while downstream takes them; false when one is refused.
        private bool SendAll(Job job)
        {
            while (job.Pieces.Count > 0)
            {
                Item piece = NextPiece(job);
                if (GetOutput() == null || IsOutputBlocked) return false;
                TakeOut(job, piece);
                if (!SendOut(piece)) { PutBack(job, piece); return false; }
                Left(job, piece);
            }
            return true;
        }

        private Item NextPiece(Job job)
        {
            var p = job.Pieces;
            if (p == null || p.Count == 0) return null;
            return Mode == SeparatorMode.Unpack && Order == UnpackOrder.Lifo ? p[p.Count - 1] : p[0];
        }

        private void TakeOut(Job job, Item piece)
        {
            job.Pieces.Remove(piece);
            if (Mode == SeparatorMode.Unpack && CopyContainerLabels && job.Item.HasProperties)
                foreach (string label in job.Item.Properties.Names)
                    if (!piece.HasLabel(label)) piece.Properties.SetParsed(label, job.Item.Properties.GetText(label));
        }

        private void PutBack(Job job, Item piece)
        {
            if (Mode == SeparatorMode.Unpack && Order == UnpackOrder.Lifo) job.Pieces.Add(piece);
            else job.Pieces.Insert(0, piece);
        }

        private bool TrySendReady()
        {
            if (ready == null) return false;
            Item piece = ready;
            if (!SendOut(piece)) { UpdateState(); return false; }
            ready = null;
            Left(jobs.Count > 0 ? jobs[0] : null, piece);
            return true;
        }

        private void Left(Job job, Item piece)
        {
            Released++;
            PiecesHeld.Update(-1, simClock.GetSimulationTime());
            vElement.UnloadItem(piece);
            PieceReleased?.Invoke(this, job?.Item, piece);
        }

        // The job's pieces have all left: an empty container leaves (port, else the output); a split job is done.
        private bool Finish(Job job)
        {
            if (Mode == SeparatorMode.Unpack)
            {
                Item container = job.Item;
                bool wired = emptiesPort.GetOutput() != null;
                if (wired ? !emptiesPort.Accept(this, container, toEmpties) : !SendOut(container)) return false;
                CarriersEmptied++;
                CarrierEmptied?.Invoke(this, container);
                vElement.ReportState("CarrierEmptied");
                if (!wired) vElement.UnloadItem(container);
            }
            jobs.Remove(job);
            firstDone = true;
            NotifyInput();   // room for the next item (unless the empty container keeps its place)
            return true;
        }

        private void OnEmptyLeft()
        {
            if (KeepEmptiesInPlace && !IsInputBlocked) NotifyInput();
        }

        private double Sample(IRandomSampler s, Item item)
        {
            double d = Expressions.Samplers.Sample(s, item, this);
            return double.IsNaN(d) || d < 0 ? 0 : d;
        }

        private void UpdateState()
        {
            bool waitingDownstream = work == null && !kickPending &&
                (ready != null || (jobs.Count > 0 && jobs[0].Processed && !ReleaseOneByOne));
            if (waitingDownstream && jobs.Count > 0) SetState(ElementState.BLOCKED);
            else if (work != null && changing) SetState(ElementState.SETUP);
            else if (jobs.Count > 0 || ready != null) SetState(ElementState.PROCESSING);
            else SetState(ElementState.IDLE);
        }
    }
}
