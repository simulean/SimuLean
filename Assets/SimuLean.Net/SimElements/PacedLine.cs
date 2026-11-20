using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// One fitting station of a <see cref="PacedLine"/>: at line position <see cref="Position"/> (1 … N−1) each vehicle
    /// the <see cref="Family"/> applies to gets <see cref="CallOffFamily.Quantity"/> parts from the station's
    /// <see cref="Port"/> (a <see cref="LineStationPort"/> fed through its own input link). The family is the same
    /// definition a <see cref="CallOffPoint"/> uses (one code path): its quantity and condition say what is fitted, its
    /// reference labels let the station check the part; with <see cref="CallOffPosition"/> ≥ 0 the line itself emits the
    /// family's call-off orders when a vehicle enters that position (through <see cref="CallOffPort"/>).
    /// </summary>
    public sealed class LineStation
    {
        public LineStation(string name, int position, CallOffFamily family = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A line station needs a name.", nameof(name));
            Name = name.Trim();
            Position = position;
            Family = family ?? new CallOffFamily(Name);
        }

        public string Name { get; }

        /// <summary>Line position (0-based pitch) where the parts are fitted, 1 … N−1.</summary>
        public int Position { get; }

        /// <summary>What is fitted (quantity, condition, references) and, optionally, called off.</summary>
        public CallOffFamily Family { get; }

        /// <summary>Label whose value the part must share with the vehicle (e.g. <c>vehicle</c>; empty = no check). With a
        /// quantity q &gt; 1 the part's <c>position</c> label must also be its place 1..q.</summary>
        public string MatchLabel { get; set; } = "";

        /// <summary>Parts the station port holds (≥ the quantity).</summary>
        public int PortCapacity { get; set; } = 2;

        /// <summary>Line position at which the line emits the family's call-off for each vehicle (−1 = none: the call-off
        /// is done upstream, e.g. by a <see cref="CallOffPoint"/>).</summary>
        public int CallOffPosition { get; set; } = -1;

        /// <summary>The component buffer (built by the line).</summary>
        public LineStationPort Port { get; internal set; }

        /// <summary>The call-off port (null without a call-off at the line).</summary>
        public OutboundPort CallOffPort => CallOffPosition >= 0 ? Family.Port : null;

        public int LineStops { get; internal set; }
        public int Fitted { get; internal set; }
        public int Mismatches { get; internal set; }
        internal bool Starving;

        internal void Reset() { LineStops = 0; Fitted = 0; Mismatches = 0; Starving = false; Family.Reset(); }
    }

    /// <summary>
    /// Component buffer of a <see cref="LineStation"/>: a FIFO of up to <see cref="Capacity"/> parts fed by its own input
    /// link (pulls from upstream when it has room). The line consumes the parts through the port's output link, a
    /// <see cref="ConsumeLink"/> (their exit is recorded, stay time = time at line side). States: IDLE (empty), PROCESSING
    /// (holding parts), STARVED (the line waits for this station's part).
    /// </summary>
    public sealed class LineStationPort : Element, IElementMetrics
    {
        private readonly Queue<Item> parts = new Queue<Item>();
        private readonly PacedLine line;
        private bool starved;

        internal LineStationPort(PacedLine line, LineStation station, SimClock clock)
            : base(line.Name + "." + station.Name, clock)
        {
            this.line = line;
            Station = station;
            SetOutput(ConsumeLink.Instance);
        }

        public LineStation Station { get; }

        public int Capacity => Math.Max(Math.Max(1, Station.Family.Quantity), Station.PortCapacity);

        public int Count => parts.Count;

        /// <summary>Parts at line side, oldest first.</summary>
        public IEnumerable<Item> Parts => parts;

        public bool IsStarved => starved;

        public override void Start()
        {
            base.Start();
            parts.Clear();
            starved = false;
        }

        public override bool CheckAvailability(Item theItem) => parts.Count < Capacity;
        public override int GetFreeCapacity() => Math.Max(0, Capacity - parts.Count);
        public override int GetQueueLength() => parts.Count;

        public override bool Receive(Item theItem)
        {
            if (theItem == null || parts.Count >= Capacity) return false;
            parts.Enqueue(theItem);
            UpdateState();
            line.OnPartArrived(this, theItem);
            return true;
        }

        public override bool Unblock() => NotifyInput();

        internal List<Item> Take(int n)
        {
            var taken = new List<Item>(n);
            while (taken.Count < n && parts.Count > 0)
            {
                Item p = parts.Dequeue();
                SendOut(p);   // ConsumeLink: the exit is recorded
                taken.Add(p);
            }
            UpdateState();
            return taken;
        }

        internal void Refill()
        {
            for (int i = 0; i < Capacity && parts.Count < Capacity && !IsInputBlocked; i++)
                if (!NotifyInput()) break;
        }

        internal void SetStarved(bool value)
        {
            if (starved == value) return;
            starved = value;
            UpdateState();
        }

        private void UpdateState()
            => SetState(starved ? ElementState.STARVED : parts.Count > 0 ? ElementState.PROCESSING : ElementState.IDLE);

        public void ReportMetrics(Action<string, double> set)
        {
            set(Results.ResultMetrics.LineStops, Station.LineStops);
            set(Results.ResultMetrics.LineStopTime, GetTimeInState(ElementState.STARVED));
            set(Results.ResultMetrics.Fitted, Station.Fitted);
            set(Results.ResultMetrics.Mismatches, Station.Mismatches);
            if (Station.CallOffPort != null) set(Results.ResultMetrics.CallOffs, Station.Family.OrdersEmitted);
        }
    }

    /// <summary>
    /// <b>Paced (indexing) assembly line</b> with <see cref="Positions"/> pitches and a takt (<see cref="Takt"/>, fixed or
    /// sampled per index). Vehicles (main items) enter position 0 from the input link whenever it is empty. Every takt
    /// the line indexes if it can: the vehicle at the last position leaves through the output link, every other one
    /// moves one position on, position 0 is free again (an empty position 0 moving on is an <i>empty pitch</i>,
    /// counted). The takt runs through <see cref="Element.ScheduleWork"/>, so downtime (breaks, shifts: the downtime
    /// generators) pauses it, and the line does not index while stopped.
    /// <para><b>Starvation.</b> At an index every <see cref="LineStation"/> whose position will receive a vehicle that
    /// needs its part must hold the quantity in its port; otherwise the line does not index: it is STARVED (an
    /// <see cref="ElementState"/>), each missing station counts a line stop and its port is STARVED until its parts are
    /// there; the line indexes at once when the last part arrives, and the next takt starts then. If the vehicle at the
    /// last position cannot leave, the line is BLOCKED and retries on <see cref="Unblock"/>.</para>
    /// <para><b>Fitting.</b> A vehicle arriving at a station's position takes its parts (FIFO) from the port — they leave
    /// the port through its <see cref="ConsumeLink"/> and become sub-items of the vehicle (or are dropped,
    /// <see cref="ComponentsToVehicle"/> false). A part whose <see cref="LineStation.MatchLabel"/> (and, for q &gt; 1, its
    /// <c>position</c>) differs from the vehicle's, or whose reference label differs from the reference the family wrote
    /// on the vehicle (<see cref="CallOffFamily.VehicleReferenceLabel"/>), is still fitted and counted as a mismatch.</para>
    /// <para><b>Sequence and call-off.</b> A vehicle without <see cref="SequenceLabel"/> gets the line's entry number.
    /// Stations with <see cref="LineStation.CallOffPosition"/> ≥ 0 emit their family's call-off orders when a vehicle
    /// enters that position (port <c>&lt;station&gt;.callOff</c>); usually the call-off is done upstream by a
    /// <see cref="CallOffPoint"/> before a vehicle buffer.</para>
    /// <para><b>Ports</b> (<see cref="IPortedElement"/>): <c>&lt;station&gt;</c> (component input) and
    /// <c>&lt;station&gt;.callOff</c>. <b>Statistics:</b> vehicles through the line's links (content = vehicles on the
    /// line); metrics <c>index_count</c>, <c>empty_pitches</c>, <c>starved_time</c>, <c>line_stops</c>, <c>fitted</c>,
    /// <c>mismatches</c>, <c>call_offs</c>; per station port <c>line_stops</c>, <c>line_stop_time</c>, <c>fitted</c>,
    /// <c>mismatches</c>. States: IDLE (empty line), PROCESSING (takt running with vehicles), STARVED, BLOCKED.</para>
    /// </summary>
    public class PacedLine : Element, IPortedElement, IElementMetrics
    {
        public const string CallOffSuffix = ".callOff";

        private readonly Item[] pitch;
        private readonly List<LineStation> stations = new List<LineStation>();
        private readonly List<KeyValuePair<string, Element>> ports = new List<KeyValuePair<string, Element>>();
        private readonly List<LineStation> missing = new List<LineStation>();
        private bool indexDue, starvedOpen, checkPending;
        private int seq;

        /// <param name="positions">Number of positions (pitches), ≥ 1.</param>
        /// <param name="takt">Time between indexes (&gt; 0); an expression gets the line.</param>
        public PacedLine(string name, SimClock clock, int positions, IRandomSampler takt, IVElement vElement = null)
            : base(name, clock, vElement)
        {
            if (positions < 1) throw new ArgumentException("A paced line needs at least one position.", nameof(positions));
            pitch = new Item[positions];
            Takt = takt ?? throw new ArgumentNullException(nameof(takt));
        }

        // ── configuration ────────────────────────────────────────────────────

        public int Positions => pitch.Length;

        public IRandomSampler Takt { get; set; }

        /// <summary>Label with the entry number given to vehicles without it (empty = none).</summary>
        public string SequenceLabel { get; set; } = "vehicle";

        /// <summary>Fitted parts become sub-items of the vehicle (default true); false = consumed and dropped.</summary>
        public bool ComponentsToVehicle { get; set; } = true;

        public IReadOnlyList<LineStation> Stations => stations;

        /// <summary>Adds a station and builds its port(s). Position must be 1 … N−1 (a part is fitted when the vehicle
        /// arrives there by an index); the call-off position 0 … N−1 or −1.</summary>
        public LineStation AddStation(LineStation station)
        {
            if (station == null) throw new ArgumentNullException(nameof(station));
            if (station.Position < 1 || station.Position >= pitch.Length)
                throw new ArgumentException($"Paced line '{Name}': station '{station.Name}' is at position {station.Position}; " +
                                            $"stations go at positions 1 … {pitch.Length - 1}.");
            if (station.CallOffPosition >= pitch.Length)
                throw new ArgumentException($"Paced line '{Name}': station '{station.Name}' calls off at position {station.CallOffPosition} " +
                                            $"of a {pitch.Length}-position line.");
            if (GetPort(station.Name) != null) throw new ArgumentException($"Paced line '{Name}': two stations named '{station.Name}'.");
            station.Port = new LineStationPort(this, station, simClock);
            stations.Add(station);
            ports.Add(new KeyValuePair<string, Element>(station.Name, station.Port));
            if (station.CallOffPosition >= 0)
            {
                station.Family.Port = new OutboundPort(Name + "." + station.Name + CallOffSuffix, simClock, generates: true);
                ports.Add(new KeyValuePair<string, Element>(station.Name + CallOffSuffix, station.Family.Port));
            }
            return station;
        }

        // ── views ────────────────────────────────────────────────────────────

        /// <summary>The vehicle at position <paramref name="i"/> (null = empty pitch).</summary>
        public Item VehicleAt(int i) => pitch[i];

        /// <summary>Position of a vehicle on the line (−1 if not on it).</summary>
        public int PositionOf(Item vehicle) => Array.IndexOf(pitch, vehicle);

        public bool IndexDue => indexDue;
        public int IndexCount { get; private set; }
        public int EmptyPitches { get; private set; }
        /// <summary>Starvation episodes (the line waited for parts at an index).</summary>
        public int LineStops { get; private set; }
        /// <summary>Simulation time of the last index (NaN before the first).</summary>
        public double LastIndexTime { get; private set; } = double.NaN;

        public IReadOnlyList<KeyValuePair<string, Element>> Ports => ports;

        public Element GetPort(string port)
        {
            foreach (var kv in ports) if (kv.Key == port) return kv.Value;
            return null;
        }

        public event Action<PacedLine, Item> VehicleEntered;
        /// <summary>The line indexed (vehicles moved one position on).</summary>
        public event Action<PacedLine> Indexed;
        /// <summary>The vehicle that left from the last position.</summary>
        public event Action<PacedLine, Item> VehicleLeft;
        /// <summary>Parts fitted: (line, station, vehicle, parts).</summary>
        public event Action<PacedLine, LineStation, Item, IReadOnlyList<Item>> PartsFitted;

        // ── Element contract ─────────────────────────────────────────────────

        /// <summary>Warm-up: the line's and its stations' counters and the station ports' statistics restart.</summary>
        public override void ResetStatistics()
        {
            base.ResetStatistics();
            IndexCount = 0; EmptyPitches = 0; LineStops = 0;
            foreach (var st in stations)
            {
                st.LineStops = 0; st.Fitted = 0; st.Mismatches = 0;
                st.Port.ResetStatistics();
                st.Family.Port?.ResetStatistics();
            }
        }

        public override void Start()
        {
            base.Start();
            Array.Clear(pitch, 0, pitch.Length);
            indexDue = false; starvedOpen = false; checkPending = false; seq = 0;
            IndexCount = 0; EmptyPitches = 0; LineStops = 0; LastIndexTime = double.NaN;
            foreach (var st in stations)
            {
                st.Reset();
                st.Port.Start();
                st.Family.Port?.Start();
            }
            StartTakt();
            UpdateState();
        }

        public override bool CheckAvailability(Item theItem) => pitch[0] == null;
        public override int GetFreeCapacity() => pitch[0] == null ? 1 : 0;

        public override int GetQueueLength()
        {
            int n = 0;
            foreach (var v in pitch) if (v != null) n++;
            return n;
        }

        public override bool Receive(Item theItem)
        {
            if (theItem == null || pitch[0] != null) return false;
            seq++;
            if (!string.IsNullOrEmpty(SequenceLabel) && !theItem.HasLabel(SequenceLabel)) theItem.SetLabelValue(SequenceLabel, seq);
            pitch[0] = theItem;
            vElement.LoadItem(theItem);
            VehicleEntered?.Invoke(this, theItem);
            EmitCallOffs(0, theItem);
            UpdateState();
            return true;
        }

        /// <summary>Retries a pending index (output refused, stop ended).</summary>
        public override bool Unblock() => TryIndex();

        public void ReportMetrics(Action<string, double> set)
        {
            int fitted = 0, mismatches = 0, orders = 0;
            foreach (var st in stations)
            {
                fitted += st.Fitted; mismatches += st.Mismatches;
                if (st.CallOffPort != null) orders += st.Family.OrdersEmitted;
            }
            set(Results.ResultMetrics.IndexCount, IndexCount);
            set(Results.ResultMetrics.EmptyPitches, EmptyPitches);
            set(Results.ResultMetrics.StarvedTime, GetTimeInState(ElementState.STARVED));
            set(Results.ResultMetrics.LineStops, LineStops);
            set(Results.ResultMetrics.Fitted, fitted);
            set(Results.ResultMetrics.Mismatches, mismatches);
            set(Results.ResultMetrics.CallOffs, orders);
        }

        // ── takt and index ───────────────────────────────────────────────────

        private void StartTakt()
        {
            double t = Expressions.Samplers.Sample(Takt, null, this);
            if (!(t > 0)) throw new InvalidOperationException($"Paced line '{Name}': the takt must be positive (got {t}).");
            ScheduleWork(new ActionEvent(OnTakt), t);
        }

        private void OnTakt()
        {
            indexDue = true;
            TryIndex();
        }

        internal void OnPartArrived(LineStationPort port, Item part)
        {
            // Checked as its own (same-time) event: the part's entry into the port is recorded before it is used.
            if (!indexDue || checkPending) return;
            checkPending = true;
            ScheduleWork(new ActionEvent(() => { checkPending = false; TryIndex(); }), 0);
        }

        private bool Needs(LineStation st, Item vehicle) => vehicle != null && st.Family.Applies(vehicle, this);

        private bool TryIndex()
        {
            if (!indexDue) return false;
            if (IsDown) { UpdateState(); return false; }

            // 1. every station must have the parts for the vehicle that is about to reach it
            missing.Clear();
            foreach (var st in stations)
                if (Needs(st, pitch[st.Position - 1]) && st.Port.Count < st.Family.PartsFor(pitch[st.Position - 1])) missing.Add(st);
            foreach (var st in stations)
            {
                bool isMissing = missing.Contains(st);
                if (isMissing && !st.Starving) { st.Starving = true; st.LineStops++; }
                else if (!isMissing && st.Starving) st.Starving = false;
                st.Port.SetStarved(st.Starving);
            }
            if (missing.Count > 0)
            {
                if (!starvedOpen) { starvedOpen = true; LineStops++; }
                SetState(ElementState.STARVED);
                return false;
            }

            // 2. the last vehicle must leave
            Item last = pitch[pitch.Length - 1];
            if (last != null)
            {
                if (!SendOut(last)) { SetState(ElementState.BLOCKED); return false; }
                pitch[pitch.Length - 1] = null;
                vElement.UnloadItem(last);
                VehicleLeft?.Invoke(this, last);
            }

            // 3. index
            starvedOpen = false;
            indexDue = false;
            if (pitch[0] == null) EmptyPitches++;
            for (int i = pitch.Length - 1; i > 0; i--) pitch[i] = pitch[i - 1];
            pitch[0] = null;
            IndexCount++;
            LastIndexTime = simClock.GetSimulationTime();

            // 4. vehicles arriving at stations get their parts; call-offs at positions > 0
            foreach (var st in stations)
            {
                Item v = pitch[st.Position];
                if (Needs(st, v)) Fit(st, v);
            }
            for (int i = 1; i < pitch.Length; i++)
                if (pitch[i] != null) EmitCallOffs(i, pitch[i]);

            Indexed?.Invoke(this);
            vElement.ReportState("Indexed");
            StartTakt();
            UpdateState();
            foreach (var st in stations) st.Port.Refill();
            NotifyInput();   // position 0 is free
            return true;
        }

        private void Fit(LineStation st, Item vehicle)
        {
            var fam = st.Family;
            // Positions with a part (a position drawn as CallOffFamily.NoPart is skipped), in position order.
            var needed = new List<int>(fam.Quantity);
            for (int k0 = 1; k0 <= fam.Quantity; k0++) if (fam.ReferenceTables.Count == 0 || fam.NeedsPart(vehicle, k0)) needed.Add(k0);
            List<Item> parts = st.Port.Take(needed.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                Item part = parts[i];
                int k = needed[i];
                bool bad = false;
                if (!string.IsNullOrEmpty(st.MatchLabel))
                {
                    if (part.GetLabelText(st.MatchLabel) != vehicle.GetLabelText(st.MatchLabel)) bad = true;
                    if (fam.Quantity > 1 && part.HasLabel(CallOffFamily.LabelPosition)
                        && Math.Abs(part.GetLabelOrDefault(CallOffFamily.LabelPosition) - k) > 1e-9) bad = true;
                }
                string want = vehicle.GetLabelText(fam.VehicleReferenceLabel(k));
                string got = part.GetLabelText(fam.ReferenceLabel);
                if (want != null && got != null && want != got) bad = true;
                if (bad) st.Mismatches++;
                st.Fitted++;
                if (ComponentsToVehicle) vehicle.AddItem(part);
            }
            PartsFitted?.Invoke(this, st, vehicle, parts);
        }

        private void EmitCallOffs(int position, Item vehicle)
        {
            double now = simClock.GetSimulationTime();
            foreach (var st in stations)
                if (st.CallOffPosition == position) st.Family.Emit(this, vehicle, now);
        }

        private void UpdateState()
        {
            if (indexDue && !IsDown)
            {
                foreach (var st in stations) if (st.Starving) { SetState(ElementState.STARVED); return; }
                if (pitch[pitch.Length - 1] != null) { SetState(ElementState.BLOCKED); return; }
            }
            SetState(GetQueueLength() > 0 ? ElementState.PROCESSING : ElementState.IDLE);
        }
    }
}
