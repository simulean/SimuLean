using System;
using System.Collections.Generic;
using SimuLean.Collections;
using SimuLean.Statistics;

namespace SimuLean.Resources
{
    /// <summary>When a milk-run train leaves the depot (<see cref="MilkRun.Departure"/>).</summary>
    public enum MilkRunDeparture
    {
        /// <summary>As soon as the next tour is ready (<see cref="MilkRun.ReadyRule"/>).</summary>
        Ready,
        /// <summary>At the scheduled times (every interval / timetable), with whatever is ready (possibly nothing).</summary>
        Schedule,
        /// <summary>At a scheduled time, once the tour is also ready (waits for both).</summary>
        ScheduleWhenReady,
        /// <summary>When the tour is ready, or at a scheduled time at the latest.</summary>
        ReadyOrSchedule,
    }

    /// <summary>A tour is ready when <b>any</b> / <b>all</b> of its flows have a ready carrier whose destination has room.</summary>
    public enum MilkRunReadyRule { Any, All }

    /// <summary>Which tour type comes next: the pattern in turn (<c>Cyclic</c>) or the first type of the pattern, from the
    /// one after the last tour, that is ready (<c>OnDemand</c>).</summary>
    public enum MilkRunPatternMode { Cyclic, OnDemand }

    /// <summary>What a train is doing (views, inspector).</summary>
    public enum MilkRunPhase { Idle, ToDepot, WaitingDeparture, Loading, Traveling, AtStop, Returning, UnloadingEmpties, WaitingAtStop }

    /// <summary><c>Fixed</c>: tours of the tour types in the pattern (depot -> stops -> depot). <c>Dynamic</c>: the train goes
    /// from stop to stop on demand - origins at any stop (<see cref="MilkRunStop.Pickups"/>), the next stop chosen by
    /// <see cref="MilkRun.RequestOrder"/> over the open transport requests.</summary>
    public enum MilkRunTourMode { Fixed, Dynamic }

    /// <summary>Dynamic tours: <c>Opportunistic</c> = at each stop unload what is due, then load what fits;
    /// <c>FillThenDeliver</c> = visit origins until full (or nothing more to pick up), then deliver everything.</summary>
    public enum MilkRunDynamicPolicy { Opportunistic, FillThenDeliver }

    /// <summary>Dynamic tours: when the chosen request is assigned to the train. <c>AtStop</c>: the request only
    /// decides where the train goes; there it serves what the stop has (unloads what is due, loads what fits). <c>BeforeTrip</c>:
    /// the chosen request is assigned at once — a pick-up is reserved before the trip, so it already counts for the room of its
    /// destination — and the train serves only that carrier (loads or unloads it), then decides again ("pull one request from
    /// the list, travel, load / unload it").</summary>
    public enum MilkRunAssignment { AtStop, BeforeTrip }

    /// <summary>Which carriers of an origin are open requests: <c>Head</c> = the one it offers (a queue: its first;
    /// the next one after each hand-over); <c>Any</c> = every carrier waiting in it (free access: picked in any order, each
    /// request pushed when its carrier entered the origin).</summary>
    public enum MilkRunOriginAccess { Head, Any }

    /// <summary>
    /// An open transport request of a dynamic milk run (an entry of <see cref="MilkRun.Requests"/>): a carrier waiting at an
    /// origin (<c>kind</c> = <c>pickup</c>) or on board for its destination (<c>delivery</c>). List fields for the order
    /// rule: <c>distance</c> (from the train deciding to the request's stop: the travel model for spatial trains, the
    /// constant leg time otherwise), <c>onBoard</c> (the deciding train's carriers), <c>originFill</c> (the origin's content /
    /// capacity, 0…1, pick-ups only); texts <c>kind</c>, <c>stop</c>; and, with the expression's names, <c>item.x</c> (the carrier: its labels, <c>type</c>, <c>priority</c>…), <c>origin.x</c> / <c>destination.x</c>
    /// (the elements: their properties, <c>name</c>, <c>content</c>, <c>free</c>, <c>state</c>; missing = 0 for orders);
    /// the list's <c>age</c> / <c>pushTime</c>. E.g. for urgency rules over near-full origins together with calculated fields
    /// of <see cref="MilkRun.Requests"/> (config key <c>requestFields</c>: <c>urgent=originFill &gt;= 0.8</c>, order
    /// <c>|urgent desc, distance</c>).
    /// </summary>
    public sealed class MilkRunRequest : IListFieldSource
    {
        internal MilkRun Owner;
        internal ListEntry Entry;
        public bool IsDelivery { get; internal set; }
        public string Kind => IsDelivery ? "delivery" : "pickup";
        /// <summary>Where the train goes for it: the origin's stop (pickup) or the destination's (delivery).</summary>
        public MilkRunStop Stop { get; internal set; }
        public Item Item { get; internal set; }
        public Element Origin { get; internal set; }
        /// <summary>Delivery: the carrier on board.</summary>
        public MilkRunCargo Cargo { get; internal set; }

        public double GetListNumber(string field)
        {
            if (TryRoot(field, out var rooted)) return rooted.Missing || rooted.IsText ? double.NaN : rooted.Number;
            switch (field)
            {
                case "distance": return Owner.DistanceForMatch(Stop);
                case "originFill":
                    {
                        if (Origin == null || IsDelivery) return double.NaN;
                        double n = Origin.GetQueueLength(), cap = n + Origin.GetFreeCapacity();
                        return cap > 0 ? n / cap : 0;
                    }
                case "onBoard": return Owner.MatchTrain != null ? Owner.MatchTrain.cargo.Count : 0;
            }
            return double.NaN;
        }

        // item.x (the carrier: labels, type, priority…), origin.x / destination.x (the elements: properties, name, content,
        // free, state) — the expression's names
        private bool TryRoot(string field, out SimuLean.Expressions.ExprValue value)
        {
            value = SimuLean.Expressions.ExprValue.None;
            int dot = field?.IndexOf('.') ?? -1;
            if (dot <= 0) return false;
            string root = field.Substring(0, dot), name = field.Substring(dot + 1);
            switch (root)
            {
                case "item": value = SimuLean.Expressions.EntityScope.OfItem(Item, name, Owner.GetSimClock().GetSimulationTime()); return true;
                case "origin": value = SimuLean.Expressions.EntityScope.OfElement(Origin, name); return true;
                case "destination": value = SimuLean.Expressions.EntityScope.OfElement(Cargo?.Destination, name); return true;
            }
            return false;
        }

        public string GetListText(string field)
        {
            if (TryRoot(field, out var rooted)) return rooted.Missing ? null : rooted.ToString();
            switch (field)
            {
                case "kind": return Kind;
                case "stop": return Stop?.Name;
            }
            return null;
        }
    }

    /// <summary>A stop of a milk run: where it is, and where empties are collected.</summary>
    public sealed class MilkRunStop
    {
        public MilkRunStop(string name) { Name = name ?? throw new ArgumentNullException(nameof(name)); }

        public string Name { get; }

        /// <summary>Location (network node / matrix point) for spatial trains; null = none.</summary>
        public string Location { get; set; }

        /// <summary>Elements whose items (empty carriers) the train collects here; their output link is the milk run.</summary>
        public List<Element> EmptiesOrigins { get; } = new List<Element>();

        /// <summary>Where this stop's empties go at the depot (null = the milk run's <see cref="MilkRun.EmptiesDestination"/>).</summary>
        public Element EmptiesDestination { get; set; }

        /// <summary>Dynamic tours: origins at this stop whose carriers the train picks up (flows give their destinations).</summary>
        public List<Element> Pickups { get; } = new List<Element>();

        /// <summary>Which empties to collect (item, origin); null = all.</summary>
        public Func<Item, Element, bool> EmptiesFilter { get; set; }

        public override string ToString() => Name;
    }

    /// <summary>A flow of a milk run: carriers from an origin at the depot to a destination at a stop.</summary>
    public sealed class MilkRunFlow
    {
        public MilkRunFlow(string name, Element origin, MilkRunStop stop, Element destination)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Origin = origin ?? throw new ArgumentNullException(nameof(origin));
            Stop = stop ?? throw new ArgumentNullException(nameof(stop));
            Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        }

        public string Name { get; }
        public Element Origin { get; }
        public MilkRunStop Stop { get; }
        public Element Destination { get; }

        /// <summary>Most carriers of this flow per tour (≤ 0 = no limit other than room and capacity).</summary>
        public int MaxPerTour { get; set; }

        /// <summary>Fixed tours, departure rule: the flow counts as ready only with at least this many carriers at
        /// its origin (the one it offers and the ones queued behind it) and room for as many at its destination — a full
        /// load, e.g. 2 = the train never leaves with one wagon. ≤ 1 = one ready carrier with room (the default).</summary>
        public int MinPerTour { get; set; }

        /// <summary>Fixed tours, departure rule: an optional flow never makes a tour ready nor holds it back
        /// (the ready rule looks at the other flows); its carriers still go when the tour leaves, if ready, with room. A tour
        /// whose flows are all optional treats them as required.</summary>
        public bool Optional { get; set; }

        /// <summary>Which items of the origin belong to this flow (item, origin); null = all.</summary>
        public Func<Item, Element, bool> Filter { get; set; }

        /// <summary>Load time at the depot / unload time at the stop per carrier; null = the milk run's default.</summary>
        public Func<Item, double> LoadTime { get; set; }
        public Func<Item, double> UnloadTime { get; set; }

        public override string ToString() => Name;
    }

    /// <summary>A kind of tour: the flows it serves and the stops it visits, in order.</summary>
    public sealed class MilkRunTourType
    {
        public MilkRunTourType(string name) { Name = name ?? throw new ArgumentNullException(nameof(name)); }
        public string Name { get; }
        public List<MilkRunFlow> Flows { get; } = new List<MilkRunFlow>();
        public List<MilkRunStop> Stops { get; } = new List<MilkRunStop>();
        public override string ToString() => Name;
    }

    /// <summary>A carrier on (or reserved for) a train.</summary>
    public sealed class MilkRunCargo
    {
        public Item Item { get; internal set; }
        /// <summary>The flow it is delivered by; null for an empty carrier going back.</summary>
        public MilkRunFlow Flow { get; internal set; }
        /// <summary>Where it was picked up (an empty: its stop).</summary>
        public MilkRunStop CollectedAt { get; internal set; }
        public Element Origin { get; internal set; }
        public Element Destination { get; internal set; }
        public bool Loaded { get; internal set; }
        public bool Delivered { get; internal set; }
        internal MilkRunTrain Train;
        internal MilkRunRequest Request;
        public bool IsEmpty => Flow == null;
    }

    /// <summary>One train of a milk run (one member of its team at a time).</summary>
    public sealed class MilkRunTrain
    {
        internal readonly List<MilkRunCargo> cargo = new List<MilkRunCargo>();
        internal TaskSequence Task;
        internal MilkRunTourType NextType;
        internal int NextPatternPosition;
        internal int RoundRobin;
        internal double WaitStart, DecisionTime, DepartTime, LeftLastStop, BackTime;
        internal Func<bool> HoldCondition;
        internal bool Delivering;
        /// <summary>Fill-then-deliver: when the first carrier of the cycle finished loading (NaN = none yet).</summary>
        internal double FirstLoadedAt = double.NaN;

        /// <summary>The stop it is at or last left (dynamic tours start where the last one ended).</summary>
        public MilkRunStop Position { get; internal set; }

        internal MilkRunTrain(int index) { Index = index; }

        public int Index { get; }
        /// <summary>The member running its tours (the last one assigned).</summary>
        public TaskExecutor Executor { get; internal set; }
        public MilkRunPhase Phase { get; internal set; }
        /// <summary>The tour type of the current tour (null before the first departure).</summary>
        public MilkRunTourType TourType { get; internal set; }
        /// <summary>The leg it is on / last made (stops; the depot is <see cref="MilkRun.Depot"/>).</summary>
        public MilkRunStop LegFrom { get; internal set; }
        public MilkRunStop LegTo { get; internal set; }
        /// <summary>The stop it is at (null while travelling).</summary>
        public MilkRunStop At { get; internal set; }
        /// <summary>Simulation time the current tour started (assignment of the member).</summary>
        public double TourStart { get; internal set; }
        /// <summary>Carriers reserved / loaded, in loading order.</summary>
        public IReadOnlyList<MilkRunCargo> Cargo => cargo;
    }

    /// <summary>
    /// A <b>milk run</b> (multi-load vehicles): a tugger train — or any vehicle carrying several
    /// carriers at once — run by a member of a resource team, that loads full carriers at a depot, delivers them along an
    /// ordered list of stops, collects empties at the stops and unloads them back at the depot.
    /// <list type="bullet">
    /// <item><b>Flows</b> (origin at the depot → destination at a stop, filter, maximum per tour), <b>tour types</b>
    /// (subsets of flows / stops) and a <b>pattern</b> (cyclic, e.g. A, B, B, or on demand).</item>
    /// <item><b>Departure</b>: when the next tour is ready (any / all of its flows have a ready carrier whose destination
    /// has room, counting the carriers already on their way), at scheduled times, or a combination; the train waits at
    /// the depot meanwhile (member state WAITING_DEPARTURE: reserved, not busy). It then takes, flow by flow in turn, as many ready carriers as fit
    /// (flow maximum, destination room, train capacity — total and per carrier type).</item>
    /// <item><b>Item movement is link-driven</b>: the milk run is the output link of every origin (flow origins and
    /// empties origins) and an input link of every destination. Origins keep their carriers (refused send → BLOCKED)
    /// until the train loads them: then they hand them over and the carriers leave (<c>OnExit</c>); at the stop the
    /// train unloads and delivers them (<c>Receive</c>, <c>OnEntry</c>), waiting HELD if the destination is full.</item>
    /// <item><b>The tour is one task</b> of the member (<see cref="TaskSequence"/> of Travel / Load / Unload steps, built
    /// as it goes with <see cref="TaskSequence.Expand"/>), so its utilisation, time per state and downtime work as for
    /// any resource.</item>
    /// </list>
    /// Travel between stops: a leg time (<see cref="SetLegTime"/>, from → to), else the member's travel model to the
    /// stop's location (spatial teams), else <see cref="DefaultLegTime"/>, else none (abstract).
    /// The milk run is an element for its identity and statistics (tours, stage times, deliveries), not part of the item
    /// flow (it receives nothing). Its state: PROCESSING while a train is on tour, WAITING_DEPARTURE while one waits.
    /// </summary>
    public sealed class MilkRun : Element, IAdvertisingLink, IElementMetrics
    {
        public static readonly ElementState WAITING_DEPARTURE = ResourceStates.WAITING_DEPARTURE;   // the member's state too

        private readonly Dictionary<Element, List<Item>> ready = new Dictionary<Element, List<Item>>();
        // carriers an origin holds behind the one it offers (advertised; counted by MinPerTour whatever the origin access)
        private readonly Dictionary<Element, List<Item>> held = new Dictionary<Element, List<Item>>();
        private readonly HashSet<Item> reserved = new HashSet<Item>();
        private readonly Dictionary<Element, int> inTransit = new Dictionary<Element, int>();
        private readonly List<MilkRunCargo> waiting = new List<MilkRunCargo>();
        private readonly List<MilkRunTrain> trains = new List<MilkRunTrain>();
        private readonly Dictionary<string, Func<double>> legTimes = new Dictionary<string, Func<double>>(StringComparer.Ordinal);
        private readonly ActionEvent checkEvent, scheduleEvent;
        private bool checkPending, scheduleDue;
        private int patternIndex, timetableIndex;
        private double timetableBase;
        private Item loadingNow;

        public MilkRun(string name, SimClock clock, IVElement vElement = null) : base(name, clock, vElement)
        {
            Depot = new MilkRunStop("Depot");
            checkEvent = new ActionEvent(() => { checkPending = false; CheckDepartures(); });
            scheduleEvent = new ActionEvent(OnScheduledDeparture);
            Requests = new SimList(name + ".requests", clock);
        }

        // ── definition ───────────────────────────────────────────────────────

        /// <summary>Team and requirements of the members that run the tours (and default load / unload times).</summary>
        public StationResources Transport { get; set; }

        /// <summary>The loading area: where tours start and end, and empties are unloaded.</summary>
        public MilkRunStop Depot { get; set; }
        public List<MilkRunStop> Stops { get; } = new List<MilkRunStop>();
        public List<MilkRunFlow> Flows { get; } = new List<MilkRunFlow>();
        public List<MilkRunTourType> TourTypes { get; } = new List<MilkRunTourType>();
        /// <summary>The order of the tour types (a type may appear several times: A, B, B).</summary>
        public List<MilkRunTourType> Pattern { get; } = new List<MilkRunTourType>();
        public MilkRunPatternMode PatternMode { get; set; } = MilkRunPatternMode.Cyclic;

        /// <summary>Fixed tours (pattern) or dynamic (stop to stop on demand).</summary>
        public MilkRunTourMode TourMode { get; set; } = MilkRunTourMode.Fixed;
        public MilkRunDynamicPolicy Policy { get; set; } = MilkRunDynamicPolicy.Opportunistic;
        /// <summary>Dynamic tours: at a stop where it loads, the train waits up to this long (from its arrival) for more
        /// carriers while it is not full (0 = leaves at once).</summary>
        public double LoadTimeout { get; set; }
        /// <summary>Dynamic tours: which request decides the next stop (an order over <see cref="Requests"/>; null = FIFO =
        /// the oldest request). E.g. <c>ListQuerySpec.Decode("|distance").ToQuery()</c> for the nearest stop.</summary>
        public ListQuery RequestOrder { get; set; }
        /// <summary>Dynamic tours: the open transport requests (pickups and deliveries). Its wait statistics: pickups,
        /// from the carrier being offered to its assignment (its loading, unless assigned before the trip).</summary>
        public SimList Requests { get; }

        /// <summary>Dynamic tours: the request order once the train delivers — fill-then-deliver's delivery
        /// phase, and the opportunistic train while it carries something (deliveries and pick-ups that fit, e.g.
        /// <c>|distance</c> = the nearest stop). Null = <see cref="RequestOrder"/>. <c>|pushTime</c> = loading order.</summary>
        public ListQuery DeliveryOrder { get; set; }
        /// <summary>Dynamic tours: when a request is assigned (<see cref="MilkRunAssignment"/>).</summary>
        public MilkRunAssignment Assignment { get; set; } = MilkRunAssignment.AtStop;
        /// <summary>Which carriers of an origin are requests: the one it offers, or every one it holds (free access).</summary>
        public MilkRunOriginAccess OriginAccess { get; set; } = MilkRunOriginAccess.Head;
        /// <summary>Fill-then-deliver: when nothing more can be picked up, the train waits for more carriers (where it is)
        /// until it is full or this long after the first carrier of the cycle was loaded. 0 = it delivers at
        /// once (default); &lt; 0 = it waits until it is full.</summary>
        public double FillTimeout { get; set; }

        public MilkRunDeparture Departure { get; set; } = MilkRunDeparture.Ready;
        public MilkRunReadyRule ReadyRule { get; set; } = MilkRunReadyRule.Any;
        /// <summary>Once the train has waited this long at the depot for the due tour type it leaves as soon as any flow is
        /// ready — of the due type, or of the next type of the pattern that has one (≤ 0 = never: it waits for the due type
        /// under <see cref="ReadyRule"/>). Keeps a train from waiting for a flow that cannot become ready, e.g. a full line side
        /// while the line is stopped by another part.</summary>
        public double ReadyMaxWait { get; set; }
        /// <summary>Scheduled departures every <see cref="DepartureInterval"/> from <see cref="FirstDeparture"/> (when no timetable).</summary>
        public double DepartureInterval { get; set; }
        public double FirstDeparture { get; set; }
        /// <summary>Departure times (absolute); repeated every <see cref="TimetableCycle"/> when it is &gt; 0.</summary>
        public List<double> Timetable { get; } = new List<double>();
        public double TimetableCycle { get; set; }

        /// <summary>Carriers a train holds at once (≤ 0 = no limit), and per carrier type (<see cref="CarrierType"/>).</summary>
        public int Capacity { get; set; }
        public Dictionary<string, int> CapacityByType { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>A carrier's type for <see cref="CapacityByType"/>: this item label's text, or the item type when null.</summary>
        public string TypeLabel { get; set; }

        /// <summary>Trains running tours at once (each one member at a time).</summary>
        public int Trains { get; set; } = 1;

        /// <summary>Where empties go at the depot (unless their stop has its own).</summary>
        public Element EmptiesDestination { get; set; }

        /// <summary>Per-carrier defaults: null = the member's own <see cref="TaskExecutor.LoadTime"/> / <c>UnloadTime</c>.</summary>
        public Func<Item, double> DefaultLoadTime { get; set; }
        public Func<Item, double> DefaultUnloadTime { get; set; }
        /// <summary>Load time of an empty at a stop / unload time at the depot; null = the defaults above.</summary>
        public Func<Item, double> EmptiesLoadTime { get; set; }
        public Func<Item, double> EmptiesUnloadTime { get; set; }
        /// <summary>Leg time when the leg table has none and the train is abstract; null = instant.</summary>
        public Func<double> DefaultLegTime { get; set; }
        /// <summary>A leg defined one way also serves the way back.</summary>
        public bool SymmetricLegs { get; set; }

        /// <summary>Travel time from stop <paramref name="from"/> to stop <paramref name="to"/> (the depot by its name).</summary>
        public void SetLegTime(string from, string to, Func<double> time, double distance = double.NaN)
        {
            legTimes[from + "\u0001" + to] = time;
            if (!double.IsNaN(distance)) legDistances[from + "\u0001" + to] = distance;
        }

        private readonly Dictionary<string, double> legDistances = new Dictionary<string, double>(StringComparer.Ordinal);

        public Func<double> LegTime(string from, string to)
        {
            if (legTimes.TryGetValue(from + "\u0001" + to, out var t)) return t;
            if (SymmetricLegs && legTimes.TryGetValue(to + "\u0001" + from, out t)) return t;
            return null;
        }

        public MilkRunStop Stop(string name)
        {
            if (Depot != null && Depot.Name == name) return Depot;
            foreach (var s in Stops) if (s.Name == name) return s;
            return null;
        }

        public MilkRunTourType TourType(string name) => TourTypes.Find(t => t.Name == name);

        /// <summary>Connects the origins (output link) and destinations (input link) to this milk run. Call once the
        /// flows and stops are defined (the factory does it).</summary>
        public void Connect()
        {
            foreach (var o in Origins()) o.SetOutput(this);
            foreach (var d in Destinations()) d.SetInput(this);
        }

        public IEnumerable<Element> Origins()
        {
            var seen = new HashSet<Element>();
            foreach (var f in Flows) if (seen.Add(f.Origin)) yield return f.Origin;
            foreach (var s in AllStops()) foreach (var o in s.Pickups) if (o != null && seen.Add(o)) yield return o;
            foreach (var s in Stops) foreach (var o in s.EmptiesOrigins) if (o != null && seen.Add(o)) yield return o;
        }

        /// <summary>The depot and the stops.</summary>
        public IEnumerable<MilkRunStop> AllStops()
        {
            if (Depot != null) yield return Depot;
            foreach (var s in Stops) yield return s;
        }

        public IEnumerable<Element> Destinations()
        {
            var seen = new HashSet<Element>();
            foreach (var f in Flows) if (seen.Add(f.Destination)) yield return f.Destination;
            foreach (var s in Stops)
            {
                var d = s.EmptiesDestination ?? EmptiesDestination;
                if (s.EmptiesOrigins.Count > 0 && d != null && seen.Add(d)) yield return d;
            }
        }

        // ── run state (views) ────────────────────────────────────────────────

        public IReadOnlyList<MilkRunTrain> TrainStates => trains;

        /// <summary>Carriers ready (offered, not reserved) at <paramref name="origin"/>.</summary>
        public int ReadyAt(Element origin)
        {
            if (!ready.TryGetValue(origin, out var l)) return 0;
            int n = 0;
            foreach (var it in l) if (!reserved.Contains(it)) n++;
            return n;
        }

        /// <summary>Carriers on their way to <paramref name="destination"/> (reserved, on a train or waiting to be delivered).</summary>
        public int InTransitTo(Element destination) => inTransit.TryGetValue(destination, out int n) ? n : 0;

        // ── statistics (RunResults reads them, ResultMetrics names) ──────────

        public int Tours { get; private set; }
        public IReadOnlyDictionary<string, int> ToursByType => toursByType;
        private readonly Dictionary<string, int> toursByType = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>Whole tour: from the member's assignment to the end of the empties' unloading (= the four stages).</summary>
        public StatTally TourTime { get; } = new StatTally();
        /// <summary>Stages: at the depot until departure (waiting + loading), outbound (to the last stop's end, deliveries
        /// and empties included), return to the depot, unloading the empties.</summary>
        public StatTally DepotStage { get; } = new StatTally();
        public StatTally OutboundStage { get; } = new StatTally();
        public StatTally ReturnStage { get; } = new StatTally();
        public StatTally EmptiesStage { get; } = new StatTally();
        /// <summary>Waiting at the depot for the departure rule (per tour).</summary>
        public StatTally DepartureWait { get; } = new StatTally();
        public int Delivered { get; private set; }
        public IReadOnlyDictionary<string, int> DeliveredByFlow => deliveredByFlow;
        private readonly Dictionary<string, int> deliveredByFlow = new Dictionary<string, int>(StringComparer.Ordinal);
        public int EmptiesReturned { get; private set; }
        /// <summary>Carriers on board of all trains over time (loaded until delivered; time-weighted).</summary>
        public StatTimeWeightedVariable LoadOnBoard { get; } = new StatTimeWeightedVariable();
        /// <summary>Distance travelled by the trains on their tours (travel model distances, or the legs' constant times'
        /// distances for abstract trains).</summary>
        public double DistanceTraveled { get; private set; }

        /// <summary>Raised when a tour ends (views, logs).</summary>
        public event Action<MilkRun, MilkRunTrain> TourEnded;

        private void ClearStatistics()
        {
            Tours = 0; Delivered = 0; EmptiesReturned = 0; DistanceTraveled = 0;
            toursByType.Clear(); deliveredByFlow.Clear();
            foreach (var t in TourTypes) toursByType[t.Name] = 0;
            foreach (var f in Flows) deliveredByFlow[f.Name] = 0;
            TourTime.Reset(); DepotStage.Reset(); OutboundStage.Reset(); ReturnStage.Reset(); EmptiesStage.Reset(); DepartureWait.Reset();
        }

        /// <summary>The milk run's statistics for <see cref="SimuLean.Results.RunResults"/> (names: <see cref="SimuLean.Results.ResultMetrics"/>).</summary>
        public void ReportMetrics(Action<string, double> set)
        {
            set(SimuLean.Results.ResultMetrics.Tours, Tours);
            foreach (var t in TourTypes) set(SimuLean.Results.ResultMetrics.ToursOf(t.Name), toursByType.TryGetValue(t.Name, out int n) ? n : 0);
            set(SimuLean.Results.ResultMetrics.TourTimeAvg, TourTime.Average);
            set(SimuLean.Results.ResultMetrics.TourTimeMax, TourTime.Max);
            set(SimuLean.Results.ResultMetrics.StageDepotAvg, DepotStage.Average);
            set(SimuLean.Results.ResultMetrics.StageOutboundAvg, OutboundStage.Average);
            set(SimuLean.Results.ResultMetrics.StageReturnAvg, ReturnStage.Average);
            set(SimuLean.Results.ResultMetrics.StageEmptiesAvg, EmptiesStage.Average);
            set(SimuLean.Results.ResultMetrics.DepartureWaitAvg, DepartureWait.Average);
            set(SimuLean.Results.ResultMetrics.DepartureWaitTotal, DepartureWait.Average * DepartureWait.Count);
            set(SimuLean.Results.ResultMetrics.Delivered, Delivered);
            foreach (var f in Flows) set(SimuLean.Results.ResultMetrics.DeliveredBy(f.Name), deliveredByFlow.TryGetValue(f.Name, out int n) ? n : 0);
            set(SimuLean.Results.ResultMetrics.EmptiesReturned, EmptiesReturned);
            if (TourMode == MilkRunTourMode.Dynamic)
            {
                set(SimuLean.Results.ResultMetrics.RequestWaitAvg, Requests.WaitTime.Average);   // pick-up: offered → assigned
                set(SimuLean.Results.ResultMetrics.LoadAvg, LoadOnBoard.GetTimeWeightedAverage(Now));
                set(SimuLean.Results.ResultMetrics.Distance, DistanceTraveled);
            }
        }

        public override void ResetStatistics()
        {
            base.ResetStatistics();
            ClearStatistics();
            LoadOnBoard.Restart(Now);
        }

        // ── lifecycle ────────────────────────────────────────────────────────

        public override void Start()
        {
            base.Start();
            Validate();
            foreach (var t in trains) foreach (var c in t.cargo) c.Destination?.ForgetPromise(c.Item);   // a new run
            ready.Clear(); held.Clear(); reserved.Clear(); inTransit.Clear(); waiting.Clear(); trains.Clear();
            loadingNow = null; checkPending = false; scheduleDue = false; patternIndex = 0; timetableIndex = 0; timetableBase = 0;
            foreach (var o in Origins()) { ready[o] = new List<Item>(); held[o] = new List<Item>(); }
            pickupRequests.Clear();
            Requests.Reset();
            originStop.Clear();
            if (TourMode == MilkRunTourMode.Dynamic)
            {
                foreach (var s in AllStops()) foreach (var o in s.Pickups) if (!originStop.ContainsKey(o)) originStop[o] = s;
                foreach (var f in Flows) if (!originStop.ContainsKey(f.Origin)) originStop[f.Origin] = Depot;
            }
            ClearStatistics();
            LoadOnBoard.Reset();
            LoadOnBoard.Update(0, Now);
            hooked.Clear();
            for (int i = 0; i < Math.Max(1, Trains); i++) trains.Add(new MilkRunTrain(i));
            SetState(ElementState.IDLE);
            // The members are started after the elements: ask for them once the model is started.
            simClock.Schedule(() => { foreach (var t in trains) RequestTrain(t); }, 0);
            StartSchedule();
        }

        private void Validate()
        {
            if (Transport == null) throw new InvalidOperationException($"Milk run '{Name}' has no team (its Transport resources).");
            if (TourMode == MilkRunTourMode.Fixed && Pattern.Count == 0) throw new InvalidOperationException($"Milk run '{Name}' has no tour pattern.");
            if (TourMode == MilkRunTourMode.Dynamic && Flows.Count == 0) throw new InvalidOperationException($"Milk run '{Name}': dynamic tours need flows.");
            foreach (var s in Stops)
                if (s.EmptiesOrigins.Count > 0 && (s.EmptiesDestination ?? EmptiesDestination) == null)
                    throw new InvalidOperationException($"Milk run '{Name}': stop '{s.Name}' collects empties but there is no empties destination.");
            bool scheduled = Departure != MilkRunDeparture.Ready;
            if (scheduled && Timetable.Count == 0 && !(DepartureInterval > 0))
                throw new InvalidOperationException($"Milk run '{Name}': a scheduled departure needs an interval > 0 or a timetable.");
            if (scheduled && Timetable.Count > 0 && TimetableCycle > 0)
                foreach (double t in Timetable)
                    if (t >= TimetableCycle) throw new InvalidOperationException($"Milk run '{Name}': timetable time {t} is not within the cycle {TimetableCycle}.");
        }

        // ── departures ───────────────────────────────────────────────────────

        private void StartSchedule()
        {
            if (Departure == MilkRunDeparture.Ready) return;
            if (Timetable.Count > 0)
            {
                Timetable.Sort();
                ScheduleNextTimetable();
            }
            else simClock.ScheduleEvent(scheduleEvent, Math.Max(0, FirstDeparture - simClock.GetSimulationTime()));
        }

        private void ScheduleNextTimetable()
        {
            if (timetableIndex >= Timetable.Count)
            {
                if (!(TimetableCycle > 0)) return;
                timetableIndex = 0;
                timetableBase += TimetableCycle;
            }
            double at = timetableBase + Timetable[timetableIndex++];
            simClock.ScheduleEvent(scheduleEvent, Math.Max(0, at - simClock.GetSimulationTime()));
        }

        private void OnScheduledDeparture()
        {
            scheduleDue = true;
            if (Timetable.Count > 0) ScheduleNextTimetable();
            else simClock.ScheduleEvent(scheduleEvent, DepartureInterval);
            CheckDepartures();
        }

        // Departures and deliveries are re-checked after the current event (never inside an origin's own send).
        private void ScheduleCheck()
        {
            if (checkPending) return;
            checkPending = true;
            simClock.ScheduleEvent(checkEvent, 0);
        }

        private void CheckDepartures()
        {
            foreach (var t in trains)
            {
                if ((t.Phase != MilkRunPhase.WaitingDeparture && t.Phase != MilkRunPhase.WaitingAtStop)
                    || t.Executor == null || t.Executor.CurrentTask != t.Task || t.HoldCondition == null) continue;
                var step = t.Executor.CurrentStep;
                if (step == null || step.Kind != StepKind.Hold) continue;
                if (t.HoldCondition()) t.Executor.Continue();
            }
        }

        private bool CanDepart(MilkRunTrain train)
        {
            var type = ChooseType(ReadyRule, out bool isReady, out int position);
            if (!isReady && ReadyMaxWait > 0
                && train.Phase == MilkRunPhase.WaitingDeparture && Now - train.WaitStart >= ReadyMaxWait - SimTime.Epsilon)
                type = ChooseType(MilkRunReadyRule.Any, out isReady, out position, firstReady: true);
            train.NextType = type;
            train.NextPatternPosition = position;
            switch (Departure)
            {
                case MilkRunDeparture.Schedule: return scheduleDue;
                case MilkRunDeparture.ScheduleWhenReady: return scheduleDue && isReady;
                case MilkRunDeparture.ReadyOrSchedule: return isReady || scheduleDue;
                default: return isReady;
            }
        }

        // firstReady: the first type of the pattern (from the next one) that is ready, whatever the pattern mode.
        private MilkRunTourType ChooseType(MilkRunReadyRule rule, out bool isReady, out int position, bool firstReady = false)
        {
            int n = Pattern.Count;
            position = patternIndex % n;
            if (PatternMode == MilkRunPatternMode.OnDemand || firstReady)
                for (int k = 0; k < n; k++)
                {
                    int p = (patternIndex + k) % n;
                    if (IsReady(Pattern[p], rule)) { isReady = true; position = p; return Pattern[p]; }
                }
            isReady = PatternMode == MilkRunPatternMode.Cyclic && IsReady(Pattern[position], rule);
            return Pattern[position];
        }

        /// <summary>The tour has something to do now: any / all flows with a ready carrier and room at the destination
        /// (a tour without flows: an empty ready at one of its stops).</summary>
        public bool IsReady(MilkRunTourType type) => IsReady(type, ReadyRule);

        private bool IsReady(MilkRunTourType type, MilkRunReadyRule rule)
        {
            if (type.Flows.Count == 0)
            {
                foreach (var s in type.Stops)
                    foreach (var o in s.EmptiesOrigins)
                        if (FirstReady(o, it => FitsEmptyTrain(it) && (s.EmptiesFilter == null || s.EmptiesFilter(it, o))) != null) return true;
                return false;
            }
            bool allOptional = type.Flows.TrueForAll(x => x.Optional);
            int ok = 0;
            foreach (var f in type.Flows)
            {
                if (f.Optional && !allOptional) continue;
                bool r = FlowReady(f);
                if (r) ok++;
                else if (rule == MilkRunReadyRule.All) return false;
            }
            return ok > 0;
        }

        // A flow is ready with MinPerTour (≥ 1) carriers at its origin and room for as many at its destination.
        private bool FlowReady(MilkRunFlow f)
        {
            int need = Math.Max(1, f.MinPerTour);
            if (Room(f.Destination, need) < need) return false;
            if (need == 1) return FirstReady(f.Origin, it => Accepts(f, it) && FitsEmptyTrain(it)) != null;
            return CountAvailable(f.Origin, it => Accepts(f, it) && FitsEmptyTrain(it)) >= need;
        }

        /// <summary>Carriers at <paramref name="origin"/> not yet reserved: the one it offers and the ones it holds behind it
        /// (advertised), whatever the <see cref="OriginAccess"/>.</summary>
        private int CountAvailable(Element origin, Func<Item, bool> ok)
        {
            int n = 0;
            List<Item> l = null;
            if (origin != null && ready.TryGetValue(origin, out l))
                foreach (var it in l) if (!reserved.Contains(it) && ok(it)) n++;
            if (origin != null && held.TryGetValue(origin, out var h))
                foreach (var it in h)
                    if (!reserved.Contains(it) && (l == null || !l.Contains(it)) && ok(it)) n++;
            return n;
        }

        // Free places at a destination not promised to loads on their way — by this milk run or any other link or carrier
        // into it (counted per milk run alone, two of them, or a milk run and a transport link, over-filled it).
        private int Room(Element destination, int need = 1)
        {
            int free = destination.GetFreeCapacity();
            if (free < 0) return int.MaxValue;   // unlimited
            int left = free - destination.RoomPromised;
            if (free > 0 && left < need) destination.WaitForPromisedRoom(this);   // told when another one releases its promise
            return left;
        }

        private static bool Accepts(MilkRunFlow f, Item item) => f.Filter == null || f.Filter(item, f.Origin);

        private Item FirstReady(Element origin, Func<Item, bool> ok)
        {
            if (origin == null || !ready.TryGetValue(origin, out var l)) return null;
            foreach (var it in l) if (!reserved.Contains(it) && ok(it)) return it;
            return null;
        }

        private string TypeOf(Item item) => string.IsNullOrEmpty(TypeLabel) ? item.Name : item.GetLabelText(TypeLabel) ?? "";

        private bool FitsEmptyTrain(Item item)
        {
            if (Capacity == 0 && CapacityByType.Count == 0) return true;
            if (CapacityByType.TryGetValue(TypeOf(item), out int n) && n <= 0) return false;
            return true;
        }

        private bool Fits(MilkRunTrain train, Item item)
        {
            if (Capacity > 0 && train.cargo.Count >= Capacity) return false;
            if (CapacityByType.Count > 0 && CapacityByType.TryGetValue(TypeOf(item), out int n))
            {
                string type = TypeOf(item);
                int same = 0;
                foreach (var c in train.cargo) if (TypeOf(c.Item) == type) same++;
                return same < n;
            }
            return true;
        }

        // ── trains and tours ─────────────────────────────────────────────────

        private void RequestTrain(MilkRunTrain train)
        {
            var request = new ResourceRequest(this, TaskType.Transport, (r, got) => StartTour(train, got),
                                              new List<ResourceRequirement>(Transport.Requirements).ToArray());
            request.Location = Depot?.Location;
            Transport.Dispatcher.Request(request);
        }

        private void StartTour(MilkRunTrain train, IReadOnlyList<TaskExecutor> got)
        {
            var ex = got[0];
            train.Executor = ex;
            train.TourStart = Now;
            if (hooked.Add(ex)) ex.TravelStarted += OnTravelStarted;
            if (TourMode == MilkRunTourMode.Dynamic) { StartDynamic(train, got); return; }
            train.TourType = null;
            train.Phase = MilkRunPhase.ToDepot;
            train.At = null;
            var task = new TaskSequence(TaskType.Transport, this, Name + " tour");
            if (ex.Mode == ResourceMode.Spatial && Depot.Location != null && ex.Location != Depot.Location)
            {
                train.LegFrom = null; train.LegTo = Depot;
                task.Travel(Depot.Location);
            }
            task.Callback(() =>
                {
                    train.Phase = MilkRunPhase.WaitingDeparture;
                    train.At = Depot;
                    train.WaitStart = Now;
                    if (ReadyMaxWait > 0)
                        simClock.ScheduleEvent(new ActionEvent(ScheduleCheck), ReadyMaxWait);
                    UpdateState();
                })
                .HoldUntil(train.HoldCondition = () => CanDepart(train), ResourceStates.WAITING_DEPARTURE)
                .Expand(t => PlanTour(train, t));
            Launch(train, task, got);
        }

        // Helpers accompany the lead; when the task ends the train asks for its member again.
        private void Launch(MilkRunTrain train, TaskSequence task, IReadOnlyList<TaskExecutor> got)
        {
            var ex = got[0];
            for (int k = 1; k < got.Count; k++)
            {
                var helper = got[k];
                helper.Execute(new TaskSequence(TaskType.Transport, this, Name + " tour (helper)").Hold(ResourceStates.TRAVELING_LOADED));
                task.Completed += _ => helper.Continue();
            }
            // the crew moves with the train all along: a helper's breakdown pauses the train (no hand-over with a load on)
            if (got.Count > 1)
            {
                var helpers = new List<TaskExecutor>(got.Count - 1);
                for (int k = 1; k < got.Count; k++) helpers.Add(got[k]);
                CarrierGroup.Together(ex, helpers, task);
            }
            task.Completed += _ =>
            {
                train.Phase = MilkRunPhase.Idle;
                train.At = null;
                UpdateState();
                RequestTrain(train);
            };
            train.Task = task;
            ex.Execute(task);
        }

        private double Now => simClock.GetSimulationTime();

        private readonly HashSet<TaskExecutor> hooked = new HashSet<TaskExecutor>();

        // Distance of the trains' moves on this milk run's tasks (travel models; constant legs add theirs in Leg).
        private void OnTravelStarted(TaskExecutor ex, TravelLeg leg)
        {
            if (leg != null && ex.CurrentTask != null && ex.CurrentTask.Requester == this && !double.IsNaN(leg.Distance))
                DistanceTraveled += leg.Distance;
        }

        private bool TryLegDistance(string from, string to, out double d)
            => legDistances.TryGetValue(from + "\u0001" + to, out d) || (SymmetricLegs && legDistances.TryGetValue(to + "\u0001" + from, out d));

        private void PlanTour(MilkRunTrain train, TaskSequence t)
        {
            var type = train.NextType ?? ChooseType(ReadyRule, out _, out train.NextPatternPosition);
            patternIndex = train.NextPatternPosition + 1;
            if (Departure != MilkRunDeparture.Ready) scheduleDue = false;
            train.TourType = type;
            train.DecisionTime = Now;
            train.RoundRobin = 0;
            DepartureWait.Update(Now - train.WaitStart);
            train.Phase = MilkRunPhase.Loading;
            UpdateState();

            if (type.Flows.Count > 0) t.Expand(tt => LoadNext(train, tt));
            t.Callback(() => { train.DepartTime = Now; });
            MilkRunStop prev = Depot;
            foreach (var stop in type.Stops)
            {
                var from = prev; var to = stop;
                t.Callback(() => BeginLeg(train, from, to, MilkRunPhase.Traveling));
                Leg(t, train, from, to);
                t.Callback(() => { train.At = to; train.Phase = MilkRunPhase.AtStop; });
                t.Expand(tt => UnloadAt(train, to, tt));
                t.Expand(tt => CollectNext(train, to, tt));
                prev = stop;
            }
            var last = prev;
            t.Callback(() => { train.LeftLastStop = Now; BeginLeg(train, last, Depot, MilkRunPhase.Returning); });
            Leg(t, train, last, Depot);
            t.Callback(() => { train.BackTime = Now; train.At = Depot; train.Phase = MilkRunPhase.UnloadingEmpties; });
            t.Expand(tt => UnloadEmpties(train, tt));
            t.Callback(() => EndTour(train));
        }

        private void BeginLeg(MilkRunTrain train, MilkRunStop from, MilkRunStop to, MilkRunPhase phase)
        {
            train.LegFrom = from; train.LegTo = to; train.At = null; train.Phase = phase;
        }

        private void Leg(TaskSequence t, MilkRunTrain train, MilkRunStop from, MilkRunStop to)
        {
            bool spatial = train.Executor.Mode == ResourceMode.Spatial;
            var time = LegTime(from.Name, to.Name);
            if (time != null)
            {
                if (TryLegDistance(from.Name, to.Name, out double d)) t.Callback(() => DistanceTraveled += d);
                t.Travel(time, spatial ? to.Location : null);
                return;
            }
            if (spatial)
            {
                if (to.Location == null)
                    throw new InvalidOperationException($"Milk run '{Name}': stop '{to.Name}' has no location and leg {from.Name} → {to.Name} has no time.");
                t.Travel(to.Location);
                return;
            }
            if (DefaultLegTime != null) t.Travel(DefaultLegTime);
        }

        private Func<double> ItemTime(Func<Item, double> specific, Func<Item, double> fallback, Item item, TaskExecutor ex, bool load)
            => () => specific != null ? specific(item) : fallback != null ? fallback(item) : load ? ex.LoadTime : ex.UnloadTime;

        // Loading at the depot: one carrier at a time, the tour's flows in turn, while any fits.
        private void LoadNext(MilkRunTrain train, TaskSequence t)
        {
            var flows = train.TourType.Flows;
            int n = flows.Count;
            for (int k = 0; k < n; k++)
            {
                int i = (train.RoundRobin + k) % n;
                var f = flows[i];
                if (f.MaxPerTour > 0 && CountFor(train, f) >= f.MaxPerTour) continue;
                if (Room(f.Destination) <= 0) continue;
                var item = FirstReady(f.Origin, it => Accepts(f, it) && Fits(train, it));
                if (item == null) continue;
                train.RoundRobin = i + 1;
                var cargo = Reserve(train, item, f.Origin, f.Destination, f, null);
                t.Load(item, ItemTime(f.LoadTime, DefaultLoadTime, item, train.Executor, true), _ => Loaded(cargo))
                 .Expand(tt => LoadNext(train, tt));
                return;
            }
        }

        private static int CountFor(MilkRunTrain train, MilkRunFlow f)
        {
            int c = 0;
            foreach (var x in train.cargo) if (x.Flow == f) c++;
            return c;
        }

        private MilkRunCargo Reserve(MilkRunTrain train, Item item, Element origin, Element destination, MilkRunFlow flow, MilkRunStop at)
        {
            reserved.Add(item);
            inTransit[destination] = InTransitTo(destination) + 1;
            destination.PromiseRoom(item);
            var c = new MilkRunCargo { Item = item, Origin = origin, Destination = destination, Flow = flow, CollectedAt = at, Train = train };
            train.cargo.Add(c);
            if (pickupRequests.TryGetValue(item, out var pick))
            {
                Requests.Take(pick.Entry);   // waited from the offer until now
                pickupRequests.Remove(item);
            }
            if (TourMode == MilkRunTourMode.Dynamic && flow != null)
            {
                var d = new MilkRunRequest { Owner = this, IsDelivery = true, Stop = flow.Stop, Item = item, Origin = origin, Cargo = c };
                d.Entry = Requests.PushValue(d, null, this);
                c.Request = d;
            }
            return c;
        }

        private void Loaded(MilkRunCargo c)
        {
            if (HandOver(c.Origin, c.Item)) { AfterLoaded(c); return; }
            // Loaded inside the origin's own send: hand it over after the current event, at the same time.
            simClock.Schedule(() =>
            {
                if (!HandOver(c.Origin, c.Item))
                    throw new InvalidOperationException($"'{c.Origin.Name}' did not hand item {c.Item.GetId()} over to milk run '{Name}'.");
                AfterLoaded(c);
            }, 0);
        }

        private bool HandOver(Element origin, Item item)
        {
            loadingNow = item;
            if (!origin.SendHeldItem(item) && loadingNow == item) origin.NotifyUnblock();
            bool done = loadingNow != item;
            loadingNow = null;
            return done;
        }

        private void AfterLoaded(MilkRunCargo c)
        {
            c.Loaded = true;
            LoadOnBoard.Update(1, Now);
            if (c.Flow != null && double.IsNaN(c.Train.FirstLoadedAt)) c.Train.FirstLoadedAt = Now;
            reserved.Remove(c.Item);
            if (ready.TryGetValue(c.Origin, out var l)) l.Remove(c.Item);
            if (held.TryGetValue(c.Origin, out var h)) h.Remove(c.Item);
            // the origin offers its next carrier (a queue offers only its head)
            if (!c.Origin.IsOutputBlocked) c.Origin.NotifyUnblock();
        }

        // At a stop: unload and deliver this stop's carriers one by one (waiting HELD while a destination is full).
        private void UnloadAt(MilkRunTrain train, MilkRunStop stop, TaskSequence t)
        {
            foreach (var c in train.cargo.ToArray())
            {
                if (c.Flow == null || c.Flow.Stop != stop || !c.Loaded) continue;
                var cargo = c;
                t.Unload(c.Item, ItemTime(c.Flow.UnloadTime, DefaultUnloadTime, c.Item, train.Executor, false))
                 .Callback(() => TryDeliver(cargo))
                 .HoldUntil(() => cargo.Delivered);
            }
        }

        // Then collect empties while they fit.
        private void CollectNext(MilkRunTrain train, MilkRunStop stop, TaskSequence t)
        {
            foreach (var o in stop.EmptiesOrigins)
            {
                var origin = o;
                var item = FirstReady(origin, it => (stop.EmptiesFilter == null || stop.EmptiesFilter(it, origin)) && Fits(train, it));
                if (item == null) continue;
                var cargo = Reserve(train, item, origin, stop.EmptiesDestination ?? EmptiesDestination, null, stop);
                t.Load(item, ItemTime(EmptiesLoadTime, DefaultLoadTime, item, train.Executor, true), _ => Loaded(cargo))
                 .Expand(tt => CollectNext(train, stop, tt));
                return;
            }
        }

        private void UnloadEmpties(MilkRunTrain train, TaskSequence t)
        {
            foreach (var c in train.cargo.ToArray())
            {
                if (c.Flow != null) continue;
                var cargo = c;
                t.Unload(c.Item, ItemTime(EmptiesUnloadTime, DefaultUnloadTime, c.Item, train.Executor, false))
                 .Callback(() => TryDeliver(cargo))
                 .HoldUntil(() => cargo.Delivered);
            }
        }

        private void TryDeliver(MilkRunCargo c)
        {
            if (!Deliver(c)) waiting.Add(c);
        }

        private bool Deliver(MilkRunCargo c)
        {
            Element d = c.Destination;
            if (d.IsInputBlocked || !d.CheckAvailability(c.Item) || !d.ValidateInput(c.Item, c.Origin) || !d.Receive(c.Item))
            {
                d.PrepareForEntry(c.Item, c.Origin);   // a station may prepare
                return false;
            }
            d.StatsCollector.OnEntry(c.Item);
            inTransit[d] = InTransitTo(d) - 1;
            d.ReleaseRoom(c.Item, this);
            c.Delivered = true;
            if (c.Loaded) LoadOnBoard.Update(-1, Now);
            c.Train.cargo.Remove(c);
            if (c.Request != null) { Requests.Remove(c.Request.Entry); c.Request = null; }
            if (c.Flow != null)
            {
                Delivered++;
                deliveredByFlow[c.Flow.Name] = (deliveredByFlow.TryGetValue(c.Flow.Name, out int n) ? n : 0) + 1;
            }
            else EmptiesReturned++;
            ScheduleCheck();   // room promised to this carrier is free again: a waiting train may go
            return true;
        }

        private void EndTour(MilkRunTrain train)
        {
            double now = Now;
            TourTime.Update(now - train.TourStart);
            DepotStage.Update(train.DepartTime - train.TourStart);
            OutboundStage.Update(train.LeftLastStop - train.DepartTime);
            ReturnStage.Update(train.BackTime - train.LeftLastStop);
            EmptiesStage.Update(now - train.BackTime);
            Tours++;
            string name = train.TourType.Name;
            toursByType[name] = (toursByType.TryGetValue(name, out int n) ? n : 0) + 1;
            TourEnded?.Invoke(this, train);
        }

        private void UpdateState()
        {
            bool onTour = false, waits = false;
            foreach (var t in trains)
            {
                if (t.Phase == MilkRunPhase.WaitingDeparture) waits = true;
                else if (t.Phase != MilkRunPhase.Idle && t.Phase != MilkRunPhase.ToDepot) onTour = true;
            }
            SetState(onTour ? ElementState.PROCESSING : waits ? WAITING_DEPARTURE : ElementState.IDLE);
        }

        // ── dynamic tours (stop to stop on demand) ───────────────────────────

        private readonly Dictionary<Item, MilkRunRequest> pickupRequests = new Dictionary<Item, MilkRunRequest>();
        private readonly Dictionary<Element, MilkRunStop> originStop = new Dictionary<Element, MilkRunStop>();
        private readonly List<Element> routeCandidates = new List<Element>();
        private readonly List<MilkRunFlow> routeFlows = new List<MilkRunFlow>();
        private readonly OutputStrategyContext routeContext = new OutputStrategyContext();

        /// <summary>The train whose next stop is being chosen (the <c>distance</c> / <c>onBoard</c> fields refer to it).</summary>
        internal MilkRunTrain MatchTrain;

        internal double DistanceForMatch(MilkRunStop stop)
        {
            var t = MatchTrain;
            if (t == null || stop == null) return 0;
            var from = t.Position ?? Depot;
            if (from == stop) return 0;
            if (t.Executor != null && t.Executor.Mode == ResourceMode.Spatial && stop.Location != null) return t.Executor.DistanceTo(stop.Location);
            if (legDistances.TryGetValue(from.Name + "\u0001" + stop.Name, out double d)) return d;
            if (SymmetricLegs && legDistances.TryGetValue(stop.Name + "\u0001" + from.Name, out d)) return d;
            return 0;
        }

        private void StartDynamic(MilkRunTrain train, IReadOnlyList<TaskExecutor> got)
        {
            if (train.Position == null) train.Position = Depot;
            train.TourType = null;
            var task = new TaskSequence(TaskType.Transport, this, Name + " trip");
            task.Callback(() =>
                {
                    train.Phase = MilkRunPhase.WaitingDeparture;
                    train.At = train.Position;
                    train.WaitStart = Now;
                    UpdateState();
                })
                .HoldUntil(train.HoldCondition = () => ChooseRequest(train) != null, ResourceStates.WAITING_DEPARTURE)
                .Callback(() =>
                {
                    DepartureWait.Update(Now - train.WaitStart);
                    train.DecisionTime = train.DepartTime = Now;
                    train.HoldCondition = null;
                    train.Phase = MilkRunPhase.AtStop;
                    UpdateState();
                })
                .Expand(t => DynamicStep(train, t))
                .Callback(() =>
                {
                    TourTime.Update(Now - train.TourStart);
                    Tours++;
                    TourEnded?.Invoke(this, train);
                });
            Launch(train, task, got);
        }

        // One move: choose the next request, go to its stop and serve it (the stop, or only that carrier when assigned before
        // the trip), then decide again. Nothing to do: the trip ends — unless carriers are on board (a fill-then-deliver train
        // that is not full yet), which wait with the train where it is until something can be done.
        private void DynamicStep(MilkRunTrain train, TaskSequence t)
        {
            var r = ChooseRequest(train);
            if (r == null)
            {
                if (train.cargo.Count > 0) WaitOnBoard(train, t);
                return;
            }
            var next = r.Stop;
            var from = train.Position ?? Depot;
            MilkRunCargo cargo = null;
            if (Assignment == MilkRunAssignment.BeforeTrip)
                cargo = r.IsDelivery ? r.Cargo : Assign(train, r);   // reserved now: counts for its destination's room
            if (next != from)
            {
                t.Callback(() => BeginLeg(train, from, next, MilkRunPhase.Traveling));
                Leg(t, train, from, next);
            }
            double[] arrival = new double[1];
            t.Callback(() => { train.At = next; train.Position = next; train.Phase = MilkRunPhase.AtStop; arrival[0] = Now; });
            if (cargo == null) t.Expand(tt => ServeStop(train, next, tt, arrival[0]));
            else if (r.IsDelivery) UnloadOne(train, cargo, t);
            else
            {
                var c = cargo;
                t.Load(c.Item, ItemTime(c.Flow.LoadTime, DefaultLoadTime, c.Item, train.Executor, true), _ => Loaded(c));
            }
            t.Expand(tt => DynamicStep(train, tt));
        }

        // A pick-up assigned before the trip: reserved for this train (the flow its origin's route chooses now).
        private MilkRunCargo Assign(MilkRunTrain train, MilkRunRequest r)
        {
            var f = PickupFlow(train, r.Origin, r.Item);
            return Reserve(train, r.Item, r.Origin, f.Destination, f, r.Stop);
        }

        private void UnloadOne(MilkRunTrain train, MilkRunCargo cargo, TaskSequence t)
        {
            t.Unload(cargo.Item, ItemTime(cargo.Flow?.UnloadTime, DefaultUnloadTime, cargo.Item, train.Executor, false))
             .Callback(() => TryDeliver(cargo))
             .HoldUntil(() => cargo.Delivered);
        }

        // Carriers on board and nothing to do now: wait (WAITING) where it is until a request becomes actionable — a new
        // carrier, room at a destination, or the fill time-out.
        private void WaitOnBoard(MilkRunTrain train, TaskSequence t)
        {
            if (FillTimeout > 0 && !double.IsNaN(train.FirstLoadedAt) && train.FirstLoadedAt + FillTimeout > Now)
                simClock.Schedule(ScheduleCheck, train.FirstLoadedAt + FillTimeout - Now);
            t.Callback(() => { train.Phase = MilkRunPhase.WaitingAtStop; UpdateState(); })
             .HoldUntil(train.HoldCondition = () => ChooseRequest(train) != null, ResourceStates.WAITING)
             .Callback(() => { train.Phase = MilkRunPhase.AtStop; train.HoldCondition = null; })
             .Expand(tt => DynamicStep(train, tt));
        }

        /// <summary>The stop of the request this train would act on now (null: none) — see <see cref="ChooseRequest"/>.</summary>
        public MilkRunStop NextStop(MilkRunTrain train) => ChooseRequest(train)?.Stop;

        // The first request (in the order of the phase) this train can act on now, or null. Fill-then-deliver: pick-ups by
        // RequestOrder until the fill ends (full; nothing more to pick up, or the time-out), then its deliveries by
        // DeliveryOrder. Opportunistic: the request order while empty, the delivery order while it carries something; assigned
        // before the trip, a carrier due at the train's stop goes first.
        private MilkRunRequest ChooseRequest(MilkRunTrain train)
        {
            bool flowsOnBoard = false;
            foreach (var c in train.cargo) if (c.Flow != null) { flowsOnBoard = true; break; }
            bool pickups = true, deliveries = true;
            var order = RequestOrder;
            if (Policy == MilkRunDynamicPolicy.FillThenDeliver)
            {
                if (train.Delivering && !flowsOnBoard) { train.Delivering = false; train.FirstLoadedAt = double.NaN; }
                if (!train.Delivering && flowsOnBoard && FillDone(train)) train.Delivering = true;
                pickups = !train.Delivering;
                deliveries = train.Delivering;
                if (train.Delivering) order = DeliveryOrder ?? RequestOrder;
            }
            else
            {
                if (!flowsOnBoard) train.FirstLoadedAt = double.NaN;
                if (train.cargo.Count > 0)
                {
                    order = DeliveryOrder ?? RequestOrder;
                    if (Assignment == MilkRunAssignment.BeforeTrip)
                        foreach (var c in train.cargo)
                            if (c.Loaded && c.Request != null && c.Request.Stop == (train.Position ?? Depot)) return c.Request;
                }
            }
            MatchTrain = train;
            try
            {
                foreach (var e in Requests.Ordered(order))
                {
                    var r = (MilkRunRequest)e.Value;
                    if (r.IsDelivery) { if (deliveries && r.Cargo.Train == train && r.Cargo.Loaded) return r; }
                    else if (pickups && PickupFlow(train, r.Origin, r.Item) != null) return r;
                }
            }
            finally { MatchTrain = null; }
            return null;
        }

        // Fill-then-deliver: the fill ends when the train is full, when the time-out since the first load has passed, or
        // (no time-out) when nothing more can be picked up.
        private bool FillDone(MilkRunTrain train)
        {
            if (Full(train)) return true;
            if (FillTimeout > 0) return !double.IsNaN(train.FirstLoadedAt) && Now >= train.FirstLoadedAt + FillTimeout;
            return FillTimeout == 0 && !AnyPickup(train);
        }

        private bool AnyPickup(MilkRunTrain train)
        {
            foreach (var r in pickupRequests.Values) if (PickupFlow(train, r.Origin, r.Item) != null) return true;
            return false;
        }

        private bool Full(MilkRunTrain train) => Capacity > 0 && train.cargo.Count >= Capacity;

        /// <summary>The flow that would carry <paramref name="item"/> from <paramref name="origin"/> now: flows of the origin
        /// that accept it and whose destination has room, chosen by the origin's output strategy (its route). Null when it
        /// cannot be taken (reserved, does not fit the train, no room).</summary>
        private MilkRunFlow PickupFlow(MilkRunTrain train, Element origin, Item item)
        {
            if (item == null || reserved.Contains(item) || !Fits(train, item)) return null;
            routeFlows.Clear(); routeCandidates.Clear();
            foreach (var f in Flows)
                if (f.Origin == origin && Accepts(f, item) && Room(f.Destination) > 0 && !routeCandidates.Contains(f.Destination))
                {
                    routeFlows.Add(f);
                    routeCandidates.Add(f.Destination);
                }
            if (routeFlows.Count == 0) return null;
            if (routeFlows.Count == 1) return routeFlows[0];
            int i = origin.GetOutputStrategy().SelectOutput(routeCandidates, item, origin, routeContext.Set(origin, routeCandidates));
            return i >= 0 && i < routeFlows.Count ? routeFlows[i] : routeFlows[0];
        }

        private void ServeStop(MilkRunTrain train, MilkRunStop stop, TaskSequence t, double arrival)
        {
            if (Policy == MilkRunDynamicPolicy.FillThenDeliver)
            {
                if (train.Delivering) { UnloadAt(train, stop, t); return; }
                t.Expand(tt => LoadAtStop(train, stop, tt));
            }
            else t.Expand(tt => ServeOpportunistic(train, stop, tt));
            if (LoadTimeout > 0) t.Expand(tt => WaitForMore(train, stop, tt, arrival));
        }

        // Opportunistic: unload what is due here, then load one carrier that fits and look again — so a carrier loaded for
        // this very stop is unloaded at once, before the next is loaded.
        private void ServeOpportunistic(MilkRunTrain train, MilkRunStop stop, TaskSequence t)
        {
            UnloadAt(train, stop, t);
            t.Expand(tt => { if (LoadOne(train, stop, tt)) tt.Expand(t3 => ServeOpportunistic(train, stop, t3)); });
        }

        // Fill-then-deliver at a stop: load what fits, one carrier at a time.
        private void LoadAtStop(MilkRunTrain train, MilkRunStop stop, TaskSequence t)
        {
            if (LoadOne(train, stop, t)) t.Expand(tt => LoadAtStop(train, stop, tt));
        }

        // Loads the first carrier of the stop's origins (in their offer order) that this train can take; false if none.
        private bool LoadOne(MilkRunTrain train, MilkRunStop stop, TaskSequence t)
        {
            foreach (var origin in stop.Pickups)
            {
                if (!ready.TryGetValue(origin, out var l)) continue;
                foreach (var item in l)
                {
                    var f = PickupFlow(train, origin, item);
                    if (f == null) continue;
                    var cargo = Reserve(train, item, origin, f.Destination, f, stop);
                    t.Load(item, ItemTime(f.LoadTime, DefaultLoadTime, item, train.Executor, true), _ => Loaded(cargo));
                    return true;
                }
            }
            return false;
        }

        private bool CanLoadAt(MilkRunTrain train, MilkRunStop stop)
        {
            foreach (var origin in stop.Pickups)
                if (ready.TryGetValue(origin, out var l))
                    foreach (var item in l) if (PickupFlow(train, origin, item) != null) return true;
            return false;
        }

        // Time-out policy: wait at the stop (WAITING) for more carriers until full or the time-out from the arrival.
        private void WaitForMore(MilkRunTrain train, MilkRunStop stop, TaskSequence t, double arrival)
        {
            double deadline = arrival + LoadTimeout;
            if (Full(train) || Now >= deadline) return;
            simClock.Schedule(ScheduleCheck, deadline - Now);
            t.Callback(() => train.Phase = MilkRunPhase.WaitingAtStop)
             .HoldUntil(train.HoldCondition = () => Full(train) || Now >= deadline || CanLoadAt(train, stop), ResourceStates.WAITING)
             .Callback(() => { train.Phase = MilkRunPhase.AtStop; train.HoldCondition = null; })
             .Expand(tt => { if (Policy == MilkRunDynamicPolicy.FillThenDeliver) LoadAtStop(train, stop, tt); else ServeOpportunistic(train, stop, tt); })
             .Expand(tt => WaitForMore(train, stop, tt, arrival));
        }

        // ── ILink: origins offer carriers, destinations report room ──────────

        public bool SendItem(Item theItem, Element source)
        {
            if (theItem != null && theItem == loadingNow)
            {
                // the train loads it now: it leaves the origin
                loadingNow = null;
                source.StatsCollector.OnExit(theItem);
                return true;
            }
            Offer(theItem, source);
            return false;   // stays in the origin until a train loads it
        }

        /// <summary>An origin holds <paramref name="item"/> behind the one it offers: with <see cref="OriginAccess"/>
        /// <c>Any</c> it is a request too (free access); with <c>Head</c> it waits for its turn.</summary>
        public void Advertise(Item item, Element origin)
        {
            if (item != null && origin != null && held.TryGetValue(origin, out var h) && !h.Contains(item)) h.Add(item);
            if (OriginAccess == MilkRunOriginAccess.Any) Offer(item, origin);
            else ScheduleCheck();   // a tour waiting for more carriers (MinPerTour) may go
        }

        private void Offer(Item theItem, Element source)
        {
            if (theItem == null || source == null || !ready.TryGetValue(source, out var l) || l.Contains(theItem)) return;
            l.Add(theItem);
            if (TourMode == MilkRunTourMode.Dynamic && originStop.TryGetValue(source, out var stop))
            {
                var r = new MilkRunRequest { Owner = this, Stop = stop, Item = theItem, Origin = source };
                r.Entry = Requests.PushValue(r, null, source);
                pickupRequests[theItem] = r;
            }
            ScheduleCheck();
        }

        public bool NotifyAvailable(Element source)
        {
            for (int i = 0; i < waiting.Count; i++)
            {
                var c = waiting[i];
                if (c.Destination != source) continue;
                if (!Deliver(c)) return false;
                waiting.RemoveAt(i);
                c.Train.Executor.Continue();
                return true;
            }
            ScheduleCheck();
            return false;
        }

        // ── not part of the item flow ────────────────────────────────────────

        public override int GetQueueLength()
        {
            int n = 0;
            foreach (var t in trains) foreach (var c in t.cargo) if (c.Loaded) n++;
            return n;
        }

        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item theItem) => false;
        public override bool Receive(Item theItem) => false;
        public override bool Unblock() => false;
    }
}
