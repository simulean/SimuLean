using System;
using System.Collections.Generic;
using SimuLean.Collections;
using SimuLean.Data;
using SimuLean.Headless;
using SimuLean.Resources;

namespace SimuLean.Results
{
    /// <summary>What a set of statistics belongs to.</summary>
    public enum ResultScope { Model, Element, Slot, Resource, Team, List, Downtime, Traffic, Reaction }

    /// <summary>
    /// The statistic names of <see cref="RunResults"/> — the one vocabulary of the results (exports, the optimiser /
    /// Experimenter metrics, the reference baselines). Ratios are fractions (0–1), times in model time units.
    /// </summary>
    public static class ResultMetrics
    {
        public const string Input = "input", Output = "output", Content = "content", ContentAvg = "content_avg", ContentMax = "content_max";
        public const string StayAvg = "stay_avg", StayStd = "stay_std", StayMin = "stay_min", StayMax = "stay_max", StayCount = "stay_count";
        /// <summary>A queue's (<see cref="ItemsQueue"/>) 90th percentile of the stay — its wait, zero waits included (only
        /// queues keep their stay times).</summary>
        public const string StayP90 = "stay_p90";
        /// <summary>A sink's time in system of the items it received after the warm-up: each one's age as it entered (now −
        /// its creation time) — mean, standard deviation, minimum, maximum, 90th percentile; 0 before the first item, as
        /// <c>stay_*</c>. Recorded by the link layer, as the tally <c>tis = age</c> on the sink would be.</summary>
        public const string TisAvg = "tis_avg", TisStd = "tis_std", TisMin = "tis_min", TisMax = "tis_max", TisP90 = "tis_p90";
        public const string Utilization = "utilization", Stops = "stops", Tasks = "tasks", Members = "members", Served = "served";
        public const string RequestWaitAvg = "request_wait_avg", RequestWaitStd = "request_wait_std", RequestsWaitingAvg = "requests_waiting_avg";
        public const string Pushed = "pushed", Pulled = "pulled", WaitAvg = "wait_avg", WaitStd = "wait_std", PendingPullsAvg = "pending_pulls_avg";
        /// <summary>A downtime's time stopped (<c>downtime_total</c>: the time its targets actually stood, each stop from
        /// when it took effect — an AfterCurrent stop once the work in hand is done) and its stop time as scheduled
        /// (<c>downtime_scheduled</c>: each stop's planned length from when it was due); equal for Immediate stops.</summary>
        public const string DowntimeTotal = "downtime_total", DowntimeScheduled = "downtime_scheduled";
        public const string Waits = "waits", WaitTotal = "wait_total", DeadlocksResolved = "deadlocks_resolved";
        /// <summary>Preemption (only for teams some use of which has a preemption mode): a resource's tasks taken by
        /// requests of higher priority (<c>preemptions</c>) and interrupted in all (<c>interruptions</c>: also by its own
        /// Immediate stop); a team's sums; a station's interruptions of its jobs and the time its jobs' processes stood
        /// paused for them (total — a pause still open counted up to the capture, as <c>downtime_total</c> — and the total
        /// per interruption counted).</summary>
        public const string Preemptions = "preemptions", Interruptions = "interruptions",
                            InterruptedTime = "interrupted_time", InterruptedTimeAvg = "interrupted_time_avg";
        /// <summary>Repair crews (only for downtimes with a repair crew): repairs completed, crews arrived (repairs
        /// started), the mean wait for the crew over the crews arrived (stop start → crew there, its travel included; the
        /// element's WAITING_FOR_REPAIR) and the mean repair time over the repairs completed (crew there → end of the
        /// repair). Each mean's count is its weight when a downtime's targets are pooled.</summary>
        public const string Repairs = "repairs", RepairWaits = "repair_waits", RepairWaitAvg = "repair_wait_avg", RepairTimeAvg = "repair_time_avg";
        /// <summary>A station's setups (<see cref="StationSetup"/>; only for stations with a setup rule): setups completed, their
        /// sampled durations summed and the mean. The time the station spent setting up — waiting for its crew aside, stops
        /// excluded — is <c>state:SETUP</c>.</summary>
        public const string Setups = "setups", SetupTime = "setup_time", SetupTimeAvg = "setup_time_avg";
        /// <summary>Max-wait timers (only for elements with a wait limit): the timeouts of its waits, in all and per
        /// kind of wait limited (<c>timeouts:resource</c>, <c>timeouts:queue</c>, <c>timeouts:list_pull</c>), whatever the
        /// action.</summary>
        public const string Timeouts = "timeouts";
        /// <summary>The timeouts of one kind of wait: <c>timeouts:queue</c>.</summary>
        public static string TimeoutsOf(WaitKind kind) => Timeouts + ":" + WaitLimit.KindName(kind);
        /// <summary>A source's arrivals dropped while it was stopped (<see cref="SourceStopPolicy.Lose"/>; only for such
        /// sources).</summary>
        public const string LostArrivals = "lost_arrivals";
        /// <summary>Time of the last entry / exit of an element (a sink's last entry = its makespan; absent if none).</summary>
        public const string LastEntry = "last_entry", LastExit = "last_exit";
        /// <summary>A reaction's firings, and the signals its condition turned down.</summary>
        public const string Fired = "fired", NotMet = "not_met";
        /// <summary>Separator (<see cref="IElementMetrics"/>): pieces released, containers emptied (Unpack) or
        /// copies made (Split); a line-side unpacker also the pieces held over time (the line-side stock, carriers not
        /// counted) — time-weighted mean, minimum and maximum after the warm-up.</summary>
        public const string Released = "released", CarriersEmptied = "carriers_emptied", SplitCopies = "split_copies";
        public const string PiecesAvg = "pieces_avg", PiecesMin = "pieces_min", PiecesMax = "pieces_max";
        /// <summary>Pick face / base-stock replenisher (<see cref="PickFace"/>, <see cref="BaseStockReplenisher"/>): a
        /// face's pieces taken, stock-outs (episodes without a piece) and unit loads received / ordered (its emptied loads are
        /// <c>carriers_emptied</c>); a replenisher's trucks ordered, arrived and fully unloaded, unit loads released, realised
        /// lead time (order → arrival: mean, max), unloading time (arrival → last load handed over: mean), loads consumed
        /// (observed) and consumed but not yet ordered at the end (backlog). The lead time reuses the Agenda's
        /// <c>lead_time_avg</c> / <c>lead_time_max</c>.</summary>
        public const string Pieces = "pieces", StockOuts = "stockouts";
        public const string LoadsReceived = "loads_received", LoadsOrdered = "loads_ordered", LoadsReleased = "loads_released";
        public const string TrucksOrdered = "trucks_ordered", TrucksReceived = "trucks_received", TrucksUnloaded = "trucks_unloaded";
        public const string UnloadTimeAvg = "unload_time_avg", Consumed = "consumed", Backlog = "backlog";
        /// <summary>Milk run (<see cref="SimuLean.Resources.MilkRun"/>): tours (total and <c>tours:TYPE</c>), tour time
        /// (mean, max: assignment → empties unloaded), the four stages (mean: at the depot until departure incl. waiting,
        /// outbound incl. deliveries and empties pick-up, return, unloading empties), waiting for the departure rule (mean,
        /// total), carriers delivered (total and <c>delivered:FLOW</c>), empties returned, carriers on board (time-weighted
        /// mean over the trains, after the warm-up) and the distance its trains travelled.</summary>
        public const string Tours = "tours", TourTimeAvg = "tour_time_avg", TourTimeMax = "tour_time_max";
        public const string StageDepotAvg = "stage_depot_avg", StageOutboundAvg = "stage_outbound_avg", StageReturnAvg = "stage_return_avg",
                            StageEmptiesAvg = "stage_empties_avg";
        public const string DepartureWaitAvg = "departure_wait_avg", DepartureWaitTotal = "departure_wait_total";
        public const string Delivered = "delivered", EmptiesReturned = "empties_returned";
        public const string LoadAvg = "load_avg", Distance = "distance";
        /// <summary>Paced line / call-off point (<see cref="IElementMetrics"/>): line indexes, empty pitches that entered
        /// the line, time the line was STARVED, line stops caused by a station (episodes) and their time, parts fitted and
        /// fitted with a mismatching match / reference label, call-off orders emitted. (<c>line_stops</c> is not
        /// <c>stops</c>: that one counts the element's own downtime stops.)</summary>
        public const string IndexCount = "index_count", EmptyPitches = "empty_pitches", StarvedTime = "starved_time";
        public const string LineStops = "line_stops", LineStopTime = "line_stop_time", Fitted = "fitted", Mismatches = "mismatches";
        public const string CallOffs = "call_offs";
        /// <summary>Sequencing station (<see cref="SequencingStation"/>): carriers completed, pieces placed
        /// (<c>pieces</c>), empty slots, stock-out waits (<c>stockouts</c>, total time), order wait (call-off → placed: mean,
        /// max), lot wait (oldest order → carrier start: mean), orders waiting (time-weighted mean).</summary>
        public const string Carriers = "carriers", Gaps = "gaps", StockOutTime = "stockout_time";
        public const string OrderWaitAvg = "order_wait_avg", OrderWaitMax = "order_wait_max", LotWaitAvg = "lot_wait_avg", OrdersWaitingAvg = "orders_waiting_avg";
        /// <summary>Tours of one tour type: <c>tours:A</c>.</summary>
        public static string ToursOf(string tourType) => Tours + ":" + tourType;
        /// <summary>Carriers delivered by one flow: <c>delivered:F1</c>.</summary>
        public static string DeliveredBy(string flow) => Delivered + ":" + flow;

        // element types whose items only pass (they hold or count them, they do not work on them): their utilisation and
        // time per state say nothing (a sink is "processing" its whole run) — summaries and charts leave them out
        private static readonly HashSet<string> FlowOnlyTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "InterArrivalSource", "ScheduleSource", "RateTableSource", "ReleaseSource", "Sink", "ItemsQueue", "LengthLimitedQueue",
            "Delay", "Agenda", "StorageSystem",
        };

        /// <summary>True for an element type that only holds or counts its items (sources, queues, sinks, delays, agendas,
        /// storage): no utilisation or time per state to show.</summary>
        public static bool IsFlowOnly(string elementType) => elementType != null && FlowOnlyTypes.Contains(elementType);

        /// <summary>A short name of a time unit given in seconds ("s", "min", "h", "d", else "× n s").</summary>
        public static string TimeUnitName(double secondsPerUnit)
        {
            if (Math.Abs(secondsPerUnit - 1) < 1e-9) return "s";
            if (Math.Abs(secondsPerUnit - 60) < 1e-9) return "min";
            if (Math.Abs(secondsPerUnit - 3600) < 1e-9) return "h";
            if (Math.Abs(secondsPerUnit - 86400) < 1e-9) return "d";
            return "× " + secondsPerUnit.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " s";
        }

        /// <summary>An element's utilisation, the one definition (results and views): the mean slot utilisation of a
        /// Multi-Server, Combiner or Erection Dock, else the PROCESSING share.</summary>
        public static double UtilizationOf(Element e)
            => e is MultiServer ms ? ms.GetUtilization() : e is Combiner cb ? cb.GetUtilization() : e is ItemsQueue q ? q.GetUtilization()
             : e is SimuLean.Naval.ErectionDock dk ? dk.GetUtilization() : e.GetStateRatio(ElementState.PROCESSING);
        /// <summary>The model: end of the statistics, makespan (the last entry into a sink), deadlock flag (0 / 1)
        /// and time, time the model became empty, end condition met (0 / 1).</summary>
        public const string EndTime = "end_time", Makespan = "makespan", Deadlocked = "deadlocked", DeadlockTime = "deadlock_time",
                            Stalled = "stalled", StallTime = "stall_time", EmptyTime = "empty_time", FinishTime = "finish_time",
                            Stopped = "stopped";
        /// <summary>The time the end condition "stop when no progress for T" ended the run (absent = it did not).</summary>
        public const string NoProgressTime = "no_progress_time";

        /// <summary>Prefix of the share of time in a state: <c>state:PROCESSING</c>.</summary>
        public const string StatePrefix = "state:";
        /// <summary>Prefix of a list field statistic: <c>field:weight:push_avg</c>.</summary>
        public const string FieldPrefix = "field:";
        /// <summary>Prefix of an item statistic of an element: <c>tally:time_in_system:p90</c>, split by a label
        /// <c>tally:time_in_system[priority]:mean</c>.</summary>
        public const string TallyPrefix = "tally:";
        /// <summary>Prefix of an element's user property read at the end of the run: <c>Rush Queue.property:late</c>.
        /// Read from the run model, not stored in the results.</summary>
        public const string PropertyPrefix = "property:";
        public static bool IsProperty(string metric) => metric != null && metric.StartsWith(PropertyPrefix, StringComparison.Ordinal);
        /// <summary>The statistics of a tally.</summary>
        public const string TallyMean = "mean", TallyStd = "std", TallyMin = "min", TallyMax = "max", TallyP50 = "p50", TallyP90 = "p90", TallyCount = "count";
        /// <summary>Utilisation over the time the element was on shift: the utilisation divided by the share of
        /// time not OFF_SHIFT; only for elements with a shift.</summary>
        public const string UtilizationOnShift = "utilization_onshift";
        /// <summary>An agenda's statistics: slots whose time came, booked among them, reserved ones nobody
        /// took, no-shows, booking → appointment of the patients seen (all and priority), time to the first free slot now.</summary>
        public const string SlotsOffered = "slots_offered", SlotsBooked = "slots_booked", SlotsReservedUnused = "slots_reserved_unused",
                            NoShows = "no_shows", LeadTimeAvg = "lead_time_avg", LeadTimeP90 = "lead_time_p90", LeadTimeMax = "lead_time_max",
                            LeadTimePriorityAvg = "lead_time_priority_avg", BookingHorizon = "booking_horizon",
                            Cancellations = "cancellations", Overbooked = "overbooked";

        public static string State(string state) => StatePrefix + state;
        public static string Field(string field, string statistic) => FieldPrefix + field + ":" + statistic;
        public static bool IsState(string metric) => metric != null && metric.StartsWith(StatePrefix, StringComparison.Ordinal);
        public static bool IsField(string metric) => metric != null && metric.StartsWith(FieldPrefix, StringComparison.Ordinal);
        public static string Tally(string tally, string statistic, string split = null)
            => TallyPrefix + tally + (split != null ? "[" + split + "]" : "") + ":" + statistic;
        public static bool IsTally(string metric) => metric != null && metric.StartsWith(TallyPrefix, StringComparison.Ordinal);

        public static readonly IReadOnlyList<string> Model = new[] { EndTime, Makespan, Deadlocked, DeadlockTime, Stalled, StallTime, EmptyTime, FinishTime, Stopped, NoProgressTime };
        public static readonly IReadOnlyList<string> Element = new[]
            { Input, Output, Content, ContentAvg, ContentMax, StayAvg, StayStd, StayMin, StayMax, StayCount, Utilization, Stops, LastEntry, LastExit };
        public static readonly IReadOnlyList<string> Resource = new[] { Utilization, Tasks };
        public static readonly IReadOnlyList<string> Team = new[]
            { Members, Utilization, Served, Tasks, RequestWaitAvg, RequestWaitStd, RequestsWaitingAvg };
        public static readonly IReadOnlyList<string> List = new[]
            { Pushed, Pulled, Content, ContentAvg, ContentMax, WaitAvg, WaitStd, PendingPullsAvg };
        public static readonly IReadOnlyList<string> Downtime = new[] { Stops, DowntimeTotal, DowntimeScheduled };
        public static readonly IReadOnlyList<string> Traffic = new[] { Waits, WaitTotal, DeadlocksResolved };
        public static readonly IReadOnlyList<string> Reaction = new[] { Fired, NotMet };
        /// <summary>A queue's stay percentile (<see cref="StayP90"/>).</summary>
        public static readonly IReadOnlyList<string> QueueStay = new[] { StayP90 };
        /// <summary>A sink's time in system (<see cref="TisAvg"/>…).</summary>
        public static readonly IReadOnlyList<string> TimeInSystem = new[] { TisAvg, TisStd, TisMin, TisMax, TisP90 };
        /// <summary>Repair crews: a downtime's, per target.</summary>
        public static readonly IReadOnlyList<string> Repair = new[] { Repairs, RepairWaits, RepairWaitAvg, RepairTimeAvg };
        /// <summary>Preemption: a team's / a resource's (preemptions, interruptions) and a station's (interruptions and
        /// the time its jobs stood paused).</summary>
        public static readonly IReadOnlyList<string> Preemption = new[] { Preemptions, Interruptions, InterruptedTime, InterruptedTimeAvg };
        /// <summary>Setups (a station with a setup rule).</summary>
        public static readonly IReadOnlyList<string> Setup = new[] { Setups, SetupTime, SetupTimeAvg };
        /// <summary>Max-wait timeouts (an element with a wait limit): in all, and per kind.</summary>
        public static readonly IReadOnlyList<string> Timeout = new[]
            { Timeouts, TimeoutsOf(WaitKind.Resource), TimeoutsOf(WaitKind.Queue), TimeoutsOf(WaitKind.ListPull) };
        /// <summary>The statistics of a tally (<see cref="Tally"/>).</summary>
        public static readonly IReadOnlyList<string> TallyStatistics = new[] { TallyMean, TallyStd, TallyMin, TallyMax, TallyP50, TallyP90, TallyCount };

        /// <summary>The fixed statistics of a scope (states and fields come on top, by prefix).</summary>
        public static IReadOnlyList<string> Of(ResultScope scope)
        {
            switch (scope)
            {
                case ResultScope.Model: return Model;
                case ResultScope.Element: return Element;
                case ResultScope.Resource: return Resource;
                case ResultScope.Team: return Team;
                case ResultScope.List: return List;
                case ResultScope.Downtime: return Downtime;
                case ResultScope.Reaction: return Reaction;
                case ResultScope.Traffic: return Traffic;
                default: return Array.Empty<string>();
            }
        }
    }

    /// <summary>The statistics of one thing of the model (an element, a server slot, a resource, a team, a list…).</summary>
    public sealed class ResultObject
    {
        private readonly List<KeyValuePair<string, double>> metrics = new List<KeyValuePair<string, double>>();
        private readonly Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);

        public ResultScope Scope { get; }
        /// <summary>Config id (elements, teams, lists, downtimes); the name otherwise.</summary>
        public string Id { get; }
        public string Name { get; }
        /// <summary>Engine type (elements) or kind; may be empty.</summary>
        public string Type { get; }
        /// <summary>What it belongs to: the team of a resource, the server of a slot, the target of a downtime.</summary>
        public string Group { get; }

        public ResultObject(ResultScope scope, string id, string name, string type = "", string group = "")
        {
            Scope = scope; Id = id ?? name; Name = name ?? id; Type = type ?? ""; Group = group ?? "";
        }

        /// <summary>The statistics in a fixed order (the scope's statistics, then states / fields).</summary>
        public IReadOnlyList<KeyValuePair<string, double>> Metrics => metrics;

        public bool TryGet(string metric, out double value)
        {
            if (metric != null && index.TryGetValue(metric, out int i)) { value = metrics[i].Value; return true; }
            value = 0; return false;
        }

        public double this[string metric] => TryGet(metric, out double v) ? v : throw new KeyNotFoundException($"{Scope} '{Name}' has no statistic '{metric}'.");

        public void Set(string metric, double value)
        {
            if (index.TryGetValue(metric, out int i)) metrics[i] = new KeyValuePair<string, double>(metric, value);
            else { index[metric] = metrics.Count; metrics.Add(new KeyValuePair<string, double>(metric, value)); }
        }
    }

    /// <summary>An element's raw samples (every stay time, entry time and exit time) when it recorded them
    /// (<see cref="Statistics.ElementStatsCollector.RecordSamples"/>): what the Fit tab fits.</summary>
    public sealed class ElementSamples
    {
        public List<double> StayTimes { get; }
        public List<double> EntryTimes { get; }
        public List<double> ExitTimes { get; }

        public ElementSamples(IEnumerable<double> stay, IEnumerable<double> entries, IEnumerable<double> exits)
        {
            StayTimes = new List<double>(stay); EntryTimes = new List<double>(entries); ExitTimes = new List<double>(exits);
        }
    }

    /// <summary>What the run was: model, seed, horizon, warm-up.</summary>
    public sealed class RunInfo
    {
        public string Model { get; set; } = "";
        public int Seed { get; set; }
        /// <summary>Simulated time at the end of the run.</summary>
        public double EndTime { get; set; }
        /// <summary>Statistics were reset at this time (0 = no warm-up).</summary>
        public double WarmupTime { get; set; }
        /// <summary>Seconds per model time unit (the calendar's; 1 = seconds): the unit of every time in the results.</summary>
        public double TimeUnitSeconds { get; set; } = 1.0;
        /// <summary>Why the run ended (<see cref="RunEndReason"/>: Horizon, StopAtCount, Empty, Deadlock, Stalled, Finished, NoProgress).</summary>
        public string EndReason { get; set; } = nameof(RunEndReason.Horizon);
        /// <summary>True when the run deadlocked (<see cref="RunStatus.Deadlocked"/>); <see cref="Waiting"/> says who waited for what.</summary>
        public bool Deadlocked { get; set; }
        public List<string> Waiting { get; } = new List<string>();
        /// <summary>What the model warned about while it ran (<see cref="SimContext.ModelWarnings"/>: an unpacker given an
        /// empty carrier, a label read that the item did not have…), in order: a run outside the editor carries them back
        /// in its results, where its log is not the answer's.</summary>
        public List<string> Warnings { get; } = new List<string>();
        /// <summary>Time the statistics cover (<see cref="EndTime"/> − <see cref="WarmupTime"/>).</summary>
        public double ObservedTime => Math.Max(0, EndTime - WarmupTime);
    }

    /// <summary>
    /// The statistics snapshot of a run: every element (and server slot), resource, team, list, downtime and
    /// the traffic of a built and run model, read once with the names of <see cref="ResultMetrics"/>. The only place the
    /// engine's statistics are read for results — the optimiser / Experimenter metrics (<c>MetricCatalog</c>), the
    /// reports and exports, the scene's headless run and the reference baselines all go through it. Engine-pure.
    /// </summary>
    public sealed class RunResults
    {
        private readonly List<ResultObject> objects = new List<ResultObject>();

        public RunInfo Info { get; } = new RunInfo();
        public IReadOnlyList<ResultObject> Objects => objects;
        /// <summary>Time series of the run, when recorded (<see cref="SeriesRecorder"/>); empty otherwise.</summary>
        public List<TimeSeries> Series { get; } = new List<TimeSeries>();
        /// <summary>The state intervals of elements and resources (the Gantt), when asked for
        /// (<see cref="Capture(HeadlessModelFactory, string, bool)"/> with <c>timeline</c>); empty otherwise.</summary>
        public List<TimelineEntry> Timeline { get; } = new List<TimelineEntry>();
        /// <summary>Where each item was and when (the job Gantt), when the run recorded its timeline with a
        /// <see cref="JobRecorder"/> attached; empty otherwise.</summary>
        public List<JobEntry> Jobs { get; } = new List<JobEntry>();
        /// <summary>The raw samples of the elements that recorded them, by element name; empty otherwise.</summary>
        public Dictionary<string, ElementSamples> Samples { get; } = new Dictionary<string, ElementSamples>(StringComparer.Ordinal);

        /// <summary>At most this many timeline rows per run (a longer timeline is cut at that row, with a warning).</summary>
        public const int MaxTimelineRows = 1000000;

        public IEnumerable<ResultObject> Of(ResultScope scope)
        {
            foreach (var o in objects) if (o.Scope == scope) yield return o;
        }

        /// <summary>The object of a scope by id or name (null if none).</summary>
        public ResultObject Find(ResultScope scope, string idOrName)
        {
            ResultObject byName = null;
            foreach (var o in objects)
            {
                if (o.Scope != scope) continue;
                if (o.Id == idOrName) return o;
                if (byName == null && o.Name == idOrName) byName = o;
            }
            return byName;
        }

        /// <summary>The object of a scope by id, else by name (null if none) — an error when several objects carry the name
        /// (a metric read the first of two silently).</summary>
        public ResultObject FindUnique(ResultScope scope, string idOrName)
        {
            ResultObject byName = null;
            int named = 0;
            foreach (var o in objects)
            {
                if (o.Scope != scope) continue;
                if (o.Id == idOrName) return o;
                if (o.Name == idOrName) { if (byName == null) byName = o; named++; }
            }
            if (named > 1)
                throw new ArgumentException($"{named} objects of scope {scope} are named '{idOrName}': give them different names.");
            return byName;
        }

        public ResultObject Add(ResultObject o) { objects.Add(o); return o; }

        /// <summary>The statistics present in a scope, in column order: the scope's statistics (<see cref="ResultMetrics.Of"/>)
        /// then the states / fields seen, sorted.</summary>
        public static List<string> Columns(IEnumerable<ResultObject> objects, ResultScope scope)
        {
            var names = new List<string>();
            foreach (var o in objects) foreach (var m in o.Metrics) names.Add(m.Key);
            return Order(names, scope);
        }

        /// <summary>Statistic names in column order (see <see cref="Columns"/>), each once.</summary>
        public static List<string> Order(IEnumerable<string> metrics, ResultScope scope)
        {
            var seen = new HashSet<string>(metrics, StringComparer.Ordinal);
            var columns = new List<string>();
            foreach (string m in ResultMetrics.Of(scope)) if (seen.Remove(m)) columns.Add(m);
            var rest = new List<string>(seen);
            rest.Sort(StringComparer.Ordinal);
            columns.AddRange(rest);
            return columns;
        }

        /// <summary>One scope as a wide table: <c>name, type, group</c> and a column per statistic (empty = not measured).</summary>
        public ResultTable ToTable(ResultScope scope)
        {
            var list = new List<ResultObject>(Of(scope));
            var columns = Columns(list, scope);
            var head = new List<string> { "name", "type", "group" };
            head.AddRange(columns);
            var table = new ResultTable(scope.ToString().ToLowerInvariant(), head.ToArray());
            foreach (var o in list)
            {
                var row = new List<object> { o.Name, o.Type, o.Group };
                foreach (string c in columns) row.Add(o.TryGet(c, out double v) ? (object)v : "");
                table.Add(row.ToArray());
            }
            return table;
        }

        /// <summary>The canonical long table: one row per statistic — <c>scope, name, type, group, metric, value</c>.</summary>
        public ResultTable ToLongTable()
        {
            var table = new ResultTable("results", "scope", "name", "type", "group", "metric", "value");
            foreach (var o in objects)
                foreach (var m in o.Metrics)
                    table.Add(o.Scope.ToString().ToLowerInvariant(), o.Name, o.Type, o.Group, m.Key, m.Value);
            return table;
        }

        /// <summary>The time series as a long table: <c>scope, name, metric, time, value</c> (time = end of the interval).</summary>
        public ResultTable ToSeriesTable()
        {
            var table = new ResultTable("series", "scope", "name", "metric", "time", "value");
            foreach (var ts in Series)
                for (int i = 0; i < ts.Times.Count; i++)
                    table.Add(ts.Scope.ToString().ToLowerInvariant(), ts.Name, ts.Metric, ts.Times[i], ts.Values[i]);
            return table;
        }

        /// <summary>
        /// The id of every resource of <paramref name="model"/> in the results, the timeline and the time series (one place):
        /// its name, or "team/member" when two members of different teams share a name (the
        /// time series used the bare name and merged them), with its team.
        /// </summary>
        public static Dictionary<TaskExecutor, (string id, string team)> ResourceIds(HeadlessModelFactory model)
        {
            var teamOf = new Dictionary<TaskExecutor, string>();
            foreach (var kv in model.Teams) foreach (var m in kv.Value.Members) teamOf[m] = kv.Value.Name ?? kv.Key;
            var nameCount = new Dictionary<string, int>();
            foreach (var ex in model.Executors) nameCount[ex.Name] = nameCount.TryGetValue(ex.Name, out int n) ? n + 1 : 1;
            var ids = new Dictionary<TaskExecutor, (string, string)>();
            foreach (var ex in model.Executors)
            {
                string team = teamOf.TryGetValue(ex, out string t) ? t : "";
                ids[ex] = (nameCount[ex.Name] > 1 ? team + "/" + ex.Name : ex.Name, team);
            }
            return ids;
        }

        /// <summary>Reads the statistics of <paramref name="model"/> at its clock's current time; with
        /// <paramref name="timeline"/> also every state interval (<see cref="Timeline"/>).</summary>
        public static RunResults Capture(HeadlessModelFactory model, string modelName = null, bool timeline = false)
        {
            var r = CaptureStatistics(model, modelName);
            if (timeline) AddTimeline(model, r);
            return r;
        }

        /// <summary>
        /// The state intervals of every element and resource from their state logs (the state trackers: the one source of
        /// the time per state), up to now; each log starts at the run's start or its warm-up. Read-only.
        /// </summary>
        public static void AddTimeline(HeadlessModelFactory model, RunResults r)
        {
            double now = model.Clock.GetSimulationTime();
            bool cut = false;
            void Of(ResultScope scope, string name, IReadOnlyList<StateLogEntry> log)
            {
                if (cut || log == null) return;
                // consecutive intervals in one state are merged (a state left and re-entered at the same instant)
                string state = null;
                double from = 0, to = 0;
                for (int i = 0; i <= log.Count; i++)
                {
                    if (i < log.Count)
                    {
                        double start = log[i].Time, end = i + 1 < log.Count ? log[i + 1].Time : now;
                        if (!(end > start)) continue;
                        string st = log[i].State.Name;
                        if (st == state && start <= to) { to = end; continue; }
                        if (state != null && !Emit()) return;
                        state = st; from = start; to = end;
                    }
                    else if (state != null) Emit();
                }

                bool Emit()
                {
                    if (r.Timeline.Count >= MaxTimelineRows) { cut = true; SimLog.Warning($"[SimuLean] Timeline cut at {MaxTimelineRows} rows."); return false; }
                    r.Timeline.Add(new TimelineEntry(scope, name, state, from, to));
                    return true;
                }
            }
            model.Jobs?.AddTo(r);
            foreach (var kv in model.GetAllElements()) Of(ResultScope.Element, kv.Value.Name, kv.Value.GetStateLog());
            var ids = ResourceIds(model);
            foreach (var ex in model.Executors) Of(ResultScope.Resource, ids[ex].id, ex.GetStateLog());
        }

        private static RunResults CaptureStatistics(HeadlessModelFactory model, string modelName)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var clock = model.Clock;
            double now = clock.GetSimulationTime();
            var r = new RunResults();
            r.Info.Model = modelName ?? "";
            r.Info.Seed = clock.Context.Seed;
            r.Info.EndTime = now;
            r.Info.TimeUnitSeconds = clock.Context.Calendar != null ? clock.Context.Calendar.SecondsPerUnit : 1.0;
            r.Info.WarmupTime = model.WarmupTime > 0 && model.WarmupTime <= now ? model.WarmupTime : 0;
            var run = clock.Context.Run;
            r.Info.EndReason = run.EndReason.ToString();
            r.Info.Deadlocked = run.Deadlocked;
            r.Info.Waiting.AddRange(run.Waiting);
            r.Info.Warnings.AddRange(clock.Context.ModelWarnings);
            var modelObject = r.Add(new ResultObject(ResultScope.Model, "model", "model", "Model"));
            double makespan = double.NaN;

            foreach (var kv in model.GetAllElements())
            {
                Element e = kv.Value;
                var o = ElementObject(r, e, kv.Key, null);
                var sc = e.StatsCollector;
                // named ports (IPortedElement): each port element as an element object `owner/port`, group = owner
                if (e is IPortedElement ported)
                    foreach (var p in ported.Ports)
                        if (p.Value != null) ElementObject(r, p.Value, $"{kv.Key}/{p.Key}", e.Name);
                // makespan: the last entry into a terminal element — a sink, or a buffer / station without outputs that
                // holds the finished product; without a run monitor, the sinks
                bool terminal = model.Monitor != null ? model.Monitor.IsTerminal(e) && !(e is ScheduleSource || e is InterArrivalSource || e is RateTableSource) : e is Sink;
                if (terminal && sc != null && !double.IsNaN(sc.LastEntryTime)) makespan = double.IsNaN(makespan) ? sc.LastEntryTime : Math.Max(makespan, sc.LastEntryTime);
                // an element keeping items finishes its work after they arrive (an erection dock: the last erection)
                if (terminal && e is IKeepsItems keeps && !double.IsNaN(keeps.LastKeptTime))
                    makespan = double.IsNaN(makespan) ? keeps.LastKeptTime : Math.Max(makespan, keeps.LastKeptTime);
                if (e is SimuLean.Healthcare.Agenda agenda) AgendaStatistics(o, agenda);
                if (e is Combiner cb && cb.Capacity > 1 && cb.Assemblies != null)   // assemblies in parallel: one slot each
                    foreach (var asm in cb.Assemblies)
                    {
                        var so = r.Add(new ResultObject(ResultScope.Slot, $"{kv.Key}/slot{asm.Index}", $"{e.Name}/slot{asm.Index}", "Slot", e.Name));
                        States(so, asm.Process.GetStateSnapshot(), s => asm.Process.GetStateRatio(ElementState.Of(s)));
                    }
                if (e is SimuLean.Naval.ErectionDock dock && dock.Capacity > 1)   // erection fronts in parallel
                    foreach (var front in dock.Fronts)
                    {
                        var so = r.Add(new ResultObject(ResultScope.Slot, $"{kv.Key}/front{front.Index}", $"{e.Name}/front{front.Index}", "Slot", e.Name));
                        States(so, front.GetStateSnapshot(), s => front.GetStateRatio(ElementState.Of(s)));
                    }
                if (e is MultiServer ms)
                    foreach (var slot in ms.Slots)
                    {
                        string slotName = $"{e.Name}/slot{slot.Index}";
                        var so = r.Add(new ResultObject(ResultScope.Slot, $"{kv.Key}/slot{slot.Index}", slotName, "Slot", e.Name));
                        States(so, slot.GetStateSnapshot(), s => slot.GetStateRatio(ElementState.Of(s)));
                    }
            }

            // the model: end of the statistics, makespan (last entry into a terminal element), deadlock, empty, finished,
            // end condition
            modelObject.Set(ResultMetrics.EndTime, now);
            if (!double.IsNaN(makespan)) modelObject.Set(ResultMetrics.Makespan, makespan);
            modelObject.Set(ResultMetrics.Deadlocked, run.Deadlocked ? 1 : 0);
            if (run.Deadlocked) modelObject.Set(ResultMetrics.DeadlockTime, run.DeadlockTime);
            modelObject.Set(ResultMetrics.Stalled, run.Stalled ? 1 : 0);
            if (run.Stalled) modelObject.Set(ResultMetrics.StallTime, run.StallTime);
            if (!double.IsNaN(run.EmptyTime)) modelObject.Set(ResultMetrics.EmptyTime, run.EmptyTime);
            if (!double.IsNaN(run.FinishTime)) modelObject.Set(ResultMetrics.FinishTime, run.FinishTime);
            modelObject.Set(ResultMetrics.Stopped, clock.IsStopped ? 1 : 0);
            if (run.NoProgress) modelObject.Set(ResultMetrics.NoProgressTime, run.NoProgressTime);   // only when it ended the run

            var ids = ResourceIds(model);
            // a member's team by membership: the team's key is its id, which differs from its name in a scene model
            var dispatcherOf = new Dictionary<TaskExecutor, SimuLean.Resources.ResourceDispatcher>();
            foreach (var kv in model.Teams) foreach (var m in kv.Value.Members) dispatcherOf[m] = kv.Value;
            foreach (var ex in model.Executors)
            {
                var (id, team) = ids[ex];
                var o = r.Add(new ResultObject(ResultScope.Resource, id, ex.Name, "Resource", team));
                o.Set(ResultMetrics.Utilization, ex.Utilization);
                o.Set(ResultMetrics.Tasks, ex.TasksDone);
                if (dispatcherOf.TryGetValue(ex, out var exTeam) && exTeam.Preemptive)
                {
                    o.Set(ResultMetrics.Preemptions, ex.Preemptions);
                    o.Set(ResultMetrics.Interruptions, ex.Interruptions);
                }
                if (ex.LimitedWaits.Count > 0)
                {
                    // a repair crew's max wait on this member's downtime: only with a limit
                    o.Set(ResultMetrics.Timeouts, ex.Timeouts);
                    foreach (var kind in ex.LimitedWaits) o.Set(ResultMetrics.TimeoutsOf(kind), ex.TimeoutsOf(kind));
                }
                States(o, ex.GetStateSnapshot(), s => ex.GetStateRatio(ElementState.Of(s)));
            }
            foreach (var kv in model.Teams)
            {
                var d = kv.Value;
                var o = r.Add(new ResultObject(ResultScope.Team, kv.Key, d.Name ?? kv.Key, "Team"));
                // through TeamMetrics, the one reader (team() reads it too)
                void Put(string metric) { if (TeamMetrics.TryRead(d, metric, now, out double v)) o.Set(metric, v); }
                foreach (string m in ResultMetrics.Team) Put(m);
                TeamStates(o, d.Members);
                Put(ResultMetrics.Preemptions); Put(ResultMetrics.Interruptions);
            }

            foreach (var kv in model.Lists)
            {
                SimList l = kv.Value;
                var o = r.Add(new ResultObject(ResultScope.List, kv.Key, l.Name ?? kv.Key, "List"));
                // through ListMetrics, the one reader (list() reads it too)
                void Put(string metric) { if (ListMetrics.TryRead(l, metric, now, out double v)) o.Set(metric, v); }
                foreach (string m in ResultMetrics.List) Put(m);
                foreach (var fs in l.FieldStatistics.Values)
                    foreach (string stat in new[] { "push_avg", "pull_avg", "pull_max" }) Put(ResultMetrics.Field(fs.Field, stat));
            }

            foreach (var kv in model.DowntimesById)
                foreach (var g in kv.Value)
                {
                    string target = g.Target?.Name ?? "";
                    string name = kv.Value.Count > 1 ? $"{g.Name ?? kv.Key}@{target}" : g.Name ?? kv.Key;
                    var o = r.Add(new ResultObject(ResultScope.Downtime, kv.Value.Count > 1 ? $"{kv.Key}@{target}" : kv.Key,
                                                   name, ((object)g).GetType().Name, target));
                    o.Set(ResultMetrics.Stops, g.StopCount);
                    o.Set(ResultMetrics.DowntimeTotal, g.TotalDowntime);
                    o.Set(ResultMetrics.DowntimeScheduled, g.ScheduledDowntime);
                    if (g.Repair != null)   // only with a repair crew: the other models' results keep their shape
                    {
                        o.Set(ResultMetrics.Repairs, g.Repair.Repairs);
                        o.Set(ResultMetrics.RepairWaits, g.Repair.CrewsArrived);
                        o.Set(ResultMetrics.RepairWaitAvg, g.Repair.WaitAvg);
                        o.Set(ResultMetrics.RepairTimeAvg, g.Repair.RepairTimeAvg);
                    }
                }

            foreach (var kv in model.ReactionsById)
            {
                var o = r.Add(new ResultObject(ResultScope.Reaction, kv.Key, kv.Value.Name, kv.Value.Signal.ToString()));
                o.Set(ResultMetrics.Fired, kv.Value.Fired);
                o.Set(ResultMetrics.NotMet, kv.Value.Skipped);
            }

            var traffic = model.Space?.Traffic;
            if (traffic != null)
            {
                var o = r.Add(new ResultObject(ResultScope.Traffic, "traffic", "traffic", "Traffic"));
                o.Set(ResultMetrics.Waits, traffic.Waits);
                o.Set(ResultMetrics.WaitTotal, traffic.TotalWait);
                o.Set(ResultMetrics.DeadlocksResolved, traffic.DeadlocksResolved);
            }
            return r;
        }

        // The share of time in each state the object has been in (states never entered are left out: 0).
        // an agenda's own statistics
        // Link / state statistics of one element (or port), its tallies and its own counters (IElementMetrics).
        private static ResultObject ElementObject(RunResults r, Element e, string id, string group)
        {
            var o = r.Add(group == null ? new ResultObject(ResultScope.Element, id, e.Name, ((object)e).GetType().Name)
                                        : new ResultObject(ResultScope.Element, id, e.Name, ((object)e).GetType().Name, group));
            var sc = e.StatsCollector;
            // every value through ElementMetrics, the one reader of an element's statistics (stat() reads it too); a statistic
            // the element does not have (no setup rule, no max wait, no preemptive team…) is left out, so results keep their shape
            void Put(string metric) { if (ElementMetrics.TryRead(e, metric, out double v)) o.Set(metric, v); }
            Put(ResultMetrics.Input); Put(ResultMetrics.Output); Put(ResultMetrics.Content); Put(ResultMetrics.ContentAvg);
            Put(ResultMetrics.ContentMax); Put(ResultMetrics.StayAvg); Put(ResultMetrics.StayStd); Put(ResultMetrics.StayMin);
            Put(ResultMetrics.StayMax); Put(ResultMetrics.StayCount);
            foreach (string m in ResultMetrics.QueueStay) Put(m);      // only an element that keeps its stays (a queue)
            foreach (string m in ResultMetrics.TimeInSystem) Put(m);   // only a sink
            if (sc?.StaySamples != null) r.Samples[e.Name] = new ElementSamples(sc.StaySamples, sc.EntryTimes, sc.ExitTimes);
            Put(ResultMetrics.Utilization); Put(ResultMetrics.UtilizationOnShift); Put(ResultMetrics.Stops);
            Put(ResultMetrics.LastEntry); Put(ResultMetrics.LastExit);
            if (e is IElementMetrics own) own.ReportMetrics((name, value) => o.Set(name, value));
            Put(ResultMetrics.Interruptions); Put(ResultMetrics.InterruptedTime); Put(ResultMetrics.InterruptedTimeAvg);
            Put(ResultMetrics.Setups); Put(ResultMetrics.SetupTime); Put(ResultMetrics.SetupTimeAvg);
            Put(ResultMetrics.Timeouts);
            foreach (var kind in e.LimitedWaits) Put(ResultMetrics.TimeoutsOf(kind));
            States(o, e.GetStateSnapshot(), s => e.GetStateRatio(ElementState.Of(s)));
            if (sc?.Tallies != null) Tallies(o, sc.Tallies);
            return o;
        }

        private static void AgendaStatistics(ResultObject o, SimuLean.Healthcare.Agenda a)
        {
            var (offered, booked, unused) = a.SlotCounts();
            o.Set(ResultMetrics.SlotsOffered, offered);
            o.Set(ResultMetrics.SlotsBooked, booked);
            o.Set(ResultMetrics.SlotsReservedUnused, unused);
            o.Set(ResultMetrics.NoShows, a.NoShows);
            o.Set(ResultMetrics.Cancellations, a.Cancellations);
            o.Set(ResultMetrics.Overbooked, a.Overbooked);
            if (a.LeadTimes.Count > 0)
            {
                o.Set(ResultMetrics.LeadTimeAvg, a.LeadTimes.Mean);
                o.Set(ResultMetrics.LeadTimeP90, a.LeadTimes.Percentile(0.9));
                o.Set(ResultMetrics.LeadTimeMax, a.LeadTimes.Max);
            }
            if (a.PriorityLeadTimes.Count > 0) o.Set(ResultMetrics.LeadTimePriorityAvg, a.PriorityLeadTimes.Mean);
            double horizon = a.BookingHorizon();
            if (!double.IsNaN(horizon)) o.Set(ResultMetrics.BookingHorizon, horizon);
        }

        // the item statistics of an element: every tally with values, then its splits
        private static void Tallies(ResultObject o, SimuLean.Statistics.ItemTallies tallies)
        {
            foreach (var (name, split, t) in tallies.All())
            {
                if (t.Count == 0) continue;
                foreach (string stat in ResultMetrics.TallyStatistics)
                    o.Set(ResultMetrics.Tally(name, stat, split), ElementMetrics.TallyValue(t, stat));
            }
        }

        /// <summary>A team's share of time per state: the mean over its members of each member's
        /// <c>state:X</c> — so they add up to 1 like a member's, and the busy ones to the team's <c>utilization</c>.</summary>
        private static void TeamStates(ResultObject o, IReadOnlyList<TaskExecutor> members)
        {
            if (members.Count == 0) return;
            var sum = new Dictionary<string, double>();
            foreach (var m in members)
            {
                var snapshot = m.GetStateSnapshot();
                if (snapshot == null) continue;
                foreach (var st in snapshot)
                {
                    if (!(st.Value > 0)) continue;
                    sum.TryGetValue(st.Key, out double v);
                    sum[st.Key] = v + m.GetStateRatio(ElementState.Of(st.Key));
                }
            }
            var names = new List<string>(sum.Keys);
            names.Sort(StringComparer.Ordinal);
            foreach (string s in names) o.Set(ResultMetrics.State(s), sum[s] / members.Count);
        }

        private static void States(ResultObject o, Dictionary<string, double> snapshot, Func<string, double> ratio)
        {
            if (snapshot == null) return;
            var names = new List<string>();
            foreach (var st in snapshot) if (st.Value > 0) names.Add(st.Key);
            names.Sort(StringComparer.Ordinal);
            foreach (string s in names) o.Set(ResultMetrics.State(s), ratio(s));
        }
    }
}
