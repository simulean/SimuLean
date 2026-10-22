using System;
using System.Collections.Generic;

namespace SimuLean.Serialization
{
    /// <summary>
    /// A whole model as data: elements, connections, lists, resource teams, locations, downtimes and run settings.
    /// Extracted from a Unity scene or written in code, and rebuilt by the HeadlessModelFactory.
    /// </summary>
    [Serializable]
    public class SimulationConfig
    {
        public List<ElementConfig> Elements { get; set; } = new List<ElementConfig>();
        public List<ConnectionConfig> Connections { get; set; } = new List<ConnectionConfig>();
        public double MaxSimulationTime { get; set; } = 5.0;

        /// <summary>
        /// When true, headless runs seed the model's SimContext with <see cref="Seed"/>,
        /// making every random stream reproducible (common random numbers).
        /// </summary>
        public bool UseFixedSeed { get; set; } = false;

        /// <summary>Master seed applied when <see cref="UseFixedSeed"/> is true.</summary>
        public int Seed { get; set; } = 12345;

        /// <summary>
        /// Warm-up period: at this simulation time every element's statistics are reset
        /// (<see cref="Element.ResetStatistics"/>), so results cover only the steady state.
        /// 0 = no warm-up.
        /// </summary>
        public double WarmupTime { get; set; } = 0.0;

        /// <summary>
        /// Calendar date of simulation time 0 (ISO or dd/MM/yyyy HH:mm, see
        /// <see cref="SimCalendar.TryParseDate"/>). Null/empty = <see cref="SimCalendar.Default"/>.
        /// </summary>
        public string StartDateTime { get; set; }

        /// <summary>Length of one simulation time unit in seconds (default 1).</summary>
        public double TimeUnitSeconds { get; set; } = 1.0;

        /// <summary>The model's holidays (dates): the shared working calendar — every shift of the
        /// model is off on them, on top of its own holidays (<see cref="SimContext.Holidays"/>).</summary>
        public List<string> Holidays { get; set; } = new List<string>();

        /// <summary>End condition (any elements): the run stops when the elements <see cref="StopAt"/>
        /// (ids or names: sinks, or e.g. the queues that hold the finished product) have received <see cref="StopAtCount"/>
        /// items between them (0 = off). <see cref="MaxSimulationTime"/> still caps the run.</summary>
        public List<string> StopAt { get; set; } = new List<string>();
        public int StopAtCount { get; set; }
        /// <summary>End condition: the run stops when nothing is left to do — no event can move items and every
        /// item is out, at rest in a buffer without outputs, or surplus no element will ever take — or on a
        /// deadlock / stall.</summary>
        public bool StopWhenEmpty { get; set; }

        /// <summary>Downtime generators (timetable / MTBF-MTTR / shift / periodic) applied to elements.</summary>
        public List<DowntimeConfig> Downtimes { get; set; } = new List<DowntimeConfig>();

        /// <summary>Reactions: "when an element signals, if a condition, do actions" (<see cref="ReactionConfig"/>).</summary>
        public List<ReactionConfig> Reactions { get; set; } = new List<ReactionConfig>();

        /// <summary>The model's transport network (nodes and tracks), null = none. Elements are placed on its nodes
        /// (element parameter <c>location</c>) and spatial resource teams travel on it.</summary>
        public NetworkConfig Network { get; set; }

        /// <summary>Distance matrices between locations (teams with <c>Travel = "Matrix"</c> refer to one by id).</summary>
        public List<TravelMatrixConfig> TravelMatrices { get; set; } = new List<TravelMatrixConfig>();

        /// <summary>Walkable areas (teams with <c>Travel = "Grid"</c> refer to one by id).</summary>
        public List<WalkGridConfig> WalkGrids { get; set; } = new List<WalkGridConfig>();

        /// <summary>Resource teams: a dispatcher and its members. Elements use a team for
        /// their process and / or the transport of their items through <see cref="ResourceUseKeys"/> parameters.</summary>
        public List<ResourceTeamConfig> ResourceTeams { get; set; } = new List<ResourceTeamConfig>();

        /// <summary>Lists. Elements reach them through <c>PushToList</c> (element → list id)
        /// and <c>PullFromList</c> (list id → element, parameter <c>query</c> = <see cref="Collections.ListQuerySpec"/> text)
        /// connections.</summary>
        public List<ListConfig> Lists { get; set; } = new List<ListConfig>();

        /// <summary>The model's named data tables: each with its source and the snapshot of it the
        /// run uses (<see cref="SimContext.Data"/>). A table without a snapshot is read when the model is built.</summary>
        public List<DataTableConfig> Tables { get; set; } = new List<DataTableConfig>();

        /// <summary>The project database (absolute path) that SQL tables read from; null / empty = none.</summary>
        public string DatabasePath { get; set; }

        /// <summary>Builds the calendar described by <see cref="StartDateTime"/>/<see cref="TimeUnitSeconds"/>.</summary>
        /// <summary>A deep copy (every list, dictionary and DTO; see <see cref="ConfigCloner"/>): change it freely, e.g. one
        /// per evaluation of an optimisation.</summary>
        public SimulationConfig Clone() => ConfigCloner.DeepClone(this);

        public SimCalendar CreateCalendar()
            => string.IsNullOrWhiteSpace(StartDateTime)
                ? SimCalendar.Undated(TimeUnitSeconds > 0 ? TimeUnitSeconds : 1.0)
                : SimCalendar.Parse(StartDateTime, TimeUnitSeconds > 0 ? TimeUnitSeconds : 1.0);

        public SimulationConfig()
        {
        }
    }

    /// <summary>
    /// One element of the model: its type (handler), name and parameters.
    /// </summary>
    [Serializable]
    public class ElementConfig
    {
        public string Id { get; set; }                    // unique id (connections refer to it)
        public string Type { get; set; }                  // handler type name (e.g. "UnitySource", "UnityQueue")
        public string Name { get; set; }                  // element name
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        public ElementConfig()
        {
        }

        public ElementConfig(string id, string type, string name)
        {
            Id = id;
            Type = type;
            Name = name;
        }

        public void SetCapacity(int capacity)
        {
            Parameters["capacity"] = capacity;
        }

        public void SetArrivalsTable(Dictionary<string, List<string>> arrivalsTable)
        {
            Parameters["arrivalsTable"] = arrivalsTable;
        }

        /// <summary>
        /// Typed parameter read. Primitive values are converted (invariant culture) when the stored
        /// type differs — e.g. an <c>int</c> read as <c>double</c>, or a JSON <c>long</c> read as
        /// <c>int</c>; other types must match exactly.
        /// </summary>
        public T GetParameter<T>(string key, T defaultValue = default(T)) => ConfigParameters.Get(Parameters, key, defaultValue);
    }

    /// <summary>
    /// One connection between two elements (or an element and a list).
    /// </summary>
    [Serializable]
    public class ConnectionConfig
    {
        public string SourceId { get; set; }
        public string TargetId { get; set; }
        public string ConnectionType { get; set; }        // "General", "StockInput", "ReturnsOutput", "PushToList", "PullFromList"
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();  // e.g. port, query

        public ConnectionConfig()
        {
        }

        public ConnectionConfig(string sourceId, string targetId, string connectionType = "General")
        {
            SourceId = sourceId;
            TargetId = targetId;
            ConnectionType = connectionType;
        }
        
        public T GetParameter<T>(string key, T defaultValue = default(T)) => ConfigParameters.Get(Parameters, key, defaultValue);
    }

    /// <summary>
    /// Serializable description of one downtime generator applied to one or more
    /// elements (one generator instance per target). Built by
    /// <see cref="DowntimeBuilder"/>. Parameter keys:
    /// <list type="bullet">
    /// <item>common: <c>state</c> (state name), <c>mode</c> (Immediate|AfterCurrent),
    ///   <c>blockInput</c> (bool), <c>blockOutput</c> (bool, absent = mode default).</item>
    /// <item><c>Kind = "Timetable"</c>: <c>intervals</c> List&lt;string&gt; of
    ///   <c>"targetId|start|duration|state|mode|code|reason"</c> (empty targetId = every
    ///   target; start may be a number or a date), <c>overlap</c> (Allow|Serialize|Merge).</item>
    /// <item><c>Kind = "MtbfMttr"</c>: <c>ttf</c>, <c>ttr</c>, optional <c>firstFailure</c>
    ///   (<see cref="SamplerSpec"/> strings), <c>basis</c> (CalendarTime|BusyTime),
    ///   <c>busyStates</c> List&lt;string&gt;, <c>code</c>.</item>
    /// <item><c>Kind = "Shift"</c>: <c>pattern</c> (<see cref="Downtime.WeeklyShiftPattern"/> text),
    ///   <c>holidays</c> List&lt;string&gt; of dates.</item>
    /// <item><c>Kind = "Periodic"</c>: <c>period</c>, <c>duration</c>, <c>first</c> (default 0), optional <c>until</c>
    ///   (numbers; stops start at first, first + period, … before until), <c>allowOverlap</c> (bool), <c>code</c>;
    ///   default state SCHEDULED_DOWN.</item>
    /// </list>
    /// </summary>
    /// <summary>A list: its name and default order (empty = FIFO).</summary>
    [Serializable]
    public class ListConfig
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string DefaultOrderBy { get; set; } = "";
        public bool DefaultDescending { get; set; }
        /// <summary>Fields defined on the list (labels, built-ins, formulas; monitored ones record statistics).</summary>
        public List<SimuLean.Collections.ListFieldSpec> Fields { get; set; } = new List<SimuLean.Collections.ListFieldSpec>();
    }

    /// <summary>Where a data table's rows come from.</summary>
    public enum DataSourceKind
    {
        /// <summary>Rows typed into the model (the snapshot is the data).</summary>
        Inline,
        /// <summary>A file: CSV / <c>.data</c> (read by the engine) or Excel (read in Unity).</summary>
        File,
        /// <summary>A SELECT on the project database (<see cref="SimulationConfig.DatabasePath"/>).</summary>
        Sql,
    }

    /// <summary>
    /// A named data table: its source and the snapshot the run uses. The Unity layer takes the
    /// snapshot when it extracts the scene (every run start); a config written in code may leave <see cref="Columns"/>
    /// null and the factory reads the source when it builds the model (CSV / <c>.data</c> / SQL). Never read during a run.
    /// </summary>
    [Serializable]
    public class DataTableConfig
    {
        public string Name { get; set; }
        public DataSourceKind Source { get; set; } = DataSourceKind.Inline;
        /// <summary>File source: the path (absolute, or relative to the working directory / project).</summary>
        public string File { get; set; }
        /// <summary>File source: the Excel sheet (empty = the first).</summary>
        public string Sheet { get; set; }
        /// <summary>SQL source: the SELECT.</summary>
        public string Sql { get; set; }
        /// <summary>The snapshot (column → cells); null = not read yet.</summary>
        public Dictionary<string, List<string>> Columns { get; set; }
    }

    /// <summary>A resource team: its members and the dispatcher's orders; elements refer to it by <see cref="Id"/>.</summary>
    [Serializable]
    public class ResourceTeamConfig
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<ResourceMemberConfig> Members { get; set; } = new List<ResourceMemberConfig>();
        /// <summary>Order of the requests (<see cref="Collections.ListQuerySpec"/> text, order part used; "" = FIFO).</summary>
        public string RequestOrder { get; set; } = "";
        /// <summary>Order of the free resources ("" = FIFO = longest idle first).</summary>
        public string ResourceOrder { get; set; } = "";
        /// <summary>"" / "Abstract" = no position, travel takes no time; "Network" = members travel on the model's
        /// network at <see cref="Speed"/>; "Matrix" = by the distance matrix <see cref="Matrix"/> at <see cref="Speed"/>;
        /// "Axes" = kinematic, by axes between the coordinates of the network's nodes (<see cref="AxisSpeed"/>,
        /// <see cref="AxisAccel"/>). Spatial members start at <see cref="HomeNode"/>.</summary>
        public string Travel { get; set; } = "";
        /// <summary>Units of the team's speeds and times (<see cref="ResourceUnits"/>): "" / "Model" = model units
        /// (distance per time unit, time units); "Physical" = distance per second, seconds — converted at build with the
        /// calendar's seconds per time unit.</summary>
        public string Units { get; set; } = "";
        public double Speed { get; set; } = 1;
        public string HomeNode { get; set; }
        /// <summary>Network travel under traffic control (one vehicle per track — <see cref="NetworkConfig.SegmentCapacity"/> —,
        /// waits at nodes, deadlocks resolved and counted).</summary>
        public bool Traffic { get; set; }
        /// <summary>Matrix travel: id of a <see cref="SimulationConfig.TravelMatrices"/> entry.</summary>
        public string Matrix { get; set; }
        /// <summary>Grid travel ("Grid": free movement around obstacles, A*): id of a <see cref="SimulationConfig.WalkGrids"/> entry.</summary>
        public string Grid { get; set; }
        /// <summary>Axes travel: max speed per axis <c>"vx|vy|vz"</c> (0 = the axis does not move).</summary>
        public string AxisSpeed { get; set; } = "1|1|1";
        /// <summary>Axes travel: acceleration per axis <c>"ax|ay|az"</c> (0 = instant).</summary>
        public string AxisAccel { get; set; } = "0|0|0";
        /// <summary>Axes travel: axes move one after the other (true) or together (false, default).</summary>
        public bool AxesSequential { get; set; }
        /// <summary>Calibrated travel ("Calibrated", from Unity physics): the estimate model giving the distances
        /// ("Network", "Matrix" or "Grid", with this team's fields), the general model <c>"A|B|C"</c>
        /// (time = A + B · distance + C · loaded), the observed pairs (<c>from|to|loaded|mean|sd|n</c>) and the samples
        /// a pair needs to be used.</summary>
        public string CalibrationBase { get; set; }
        public string CalibrationFit { get; set; } = "0|0|0";
        public List<string> CalibrationPairs { get; set; } = new List<string>();
        public int CalibrationMinSamples { get; set; } = 3;
    }

    /// <summary>A walkable area: a grid on the x / z plane from (<see cref="OriginX"/>, <see cref="OriginZ"/>), cells of
    /// <see cref="CellSize"/>, <see cref="Rows"/> (row = z index, char = x index; '#' blocked, '.' free).</summary>
    [Serializable]
    public class WalkGridConfig
    {
        public string Id { get; set; }
        public double OriginX { get; set; }
        public double OriginZ { get; set; }
        public double Y { get; set; }
        public double CellSize { get; set; } = 0.5;
        public List<string> Rows { get; set; } = new List<string>();
    }

    /// <summary>A distance matrix: records <c>from|to|distance</c>; <see cref="Symmetric"/> = a missing pair uses
    /// the reverse one.</summary>
    [Serializable]
    public class TravelMatrixConfig
    {
        public string Id { get; set; }
        public bool Symmetric { get; set; } = true;
        public List<string> Entries { get; set; } = new List<string>();
    }

    /// <summary>A transport network as flat records (<see cref="TransportStorageConfigCodec"/> formats):
    /// nodes <c>id|x|y|z</c>, tracks <c>from|to|length|bidirectional|type</c>.</summary>
    [Serializable]
    public class NetworkConfig
    {
        public List<string> Nodes { get; set; } = new List<string>();
        public List<string> Tracks { get; set; } = new List<string>();
        /// <summary>Traffic: vehicles a track holds at once (teams with <c>Traffic</c>).</summary>
        public int SegmentCapacity { get; set; } = 1;
    }

    [Serializable]
    public class ResourceMemberConfig
    {
        public string Name { get; set; }
        /// <summary>"label=value" records (skills, attributes).</summary>
        public List<string> Labels { get; set; } = new List<string>();
        public double LoadTime { get; set; }
        public double UnloadTime { get; set; }
    }

    /// <summary>
    /// Element parameters for its use of resources. Process (stations): <c>processTeam</c> (team id),
    /// <c>processRequirements</c> (list of <c>count#query#label</c>, query = <see cref="Collections.ListQuerySpec"/> text;
    /// empty = one resource), <c>processRelease</c> (EndOfProcess / ItemLeaves), <c>processPriority</c> (item label
    /// giving the request priority). Transport (any element: its items are carried to its next elements; a storage's
    /// internal moves too):
    /// <c>transportTeam</c>, <c>transportRequirements</c>, <c>transportPriority</c>, and the move's
    /// <c>transportLoad</c> / <c>transportTravel</c> / <c>transportUnload</c> sampler specs ("" = the resource's own
    /// load / unload time; no travel = 0).
    /// </summary>
    public static class ResourceUseKeys
    {
        public const string ProcessTeam = "processTeam", ProcessRequirements = "processRequirements",
                            ProcessRelease = "processRelease", ProcessPriority = "processPriority",
                            ProcessHoldFor = "processHoldFor",   // sampler spec: the resources' own time (naval §6.2)
                            ProcessReleaseAt = "processReleaseAt";   // AtElement: the element (name or id) where they come back (I6)
        public const string TransportTeam = "transportTeam", TransportRequirements = "transportRequirements",
                            TransportPriority = "transportPriority", TransportLoad = "transportLoad",
                            TransportTravel = "transportTravel", TransportUnload = "transportUnload";
        /// <summary>Its items walk by themselves to its next elements (<see cref="WalkLink"/>): <c>walkSpeed</c> (m/s,
        /// physical: converted with the calendar's seconds per unit; 0 = they do not walk), <c>walkTravel</c> (Network —
        /// the default —, Grid or Matrix: the resources' travel models), <c>walkGrid</c> / <c>walkMatrix</c> (its id).</summary>
        public const string WalkSpeed = "walkSpeed", WalkTravel = "walkTravel", WalkGrid = "walkGrid", WalkMatrix = "walkMatrix",
                            WalkWaitAtDoor = "walkWaitAtDoor";   // bool: set off even to a full destination and wait at its door
    }

    /// <summary>
    /// A reaction (built by <see cref="ReactionBuilder"/>): its source elements (ids) and a parameter bag —
    /// <c>enabled</c> (bool), <c>signal</c> (<see cref="SimuLean.Signals.SignalKind"/>), <c>state</c> (state filter, optional),
    /// time signals' <c>period</c> / <c>first</c> (Every) and <c>at</c> (AtTime),
    /// <c>condition</c> (expression, optional), <c>delivery</c> (<see cref="SimuLean.Signals.SignalDelivery"/>) and its actions
    /// in order, <c>a1.kind</c>, <c>a1.target</c>, <c>a1.name</c>, <c>a1.number</c> (a plain number, a catalog parameter) or
    /// <c>a1.expression</c>, <c>a1.state</c>, <c>a1.mode</c>, <c>a1.duration</c>, <c>a1.team</c> / <c>a1.count</c> (stop / resume
    /// members of a team); then <c>a2.*</c>…
    /// </summary>
    [Serializable]
    public class ReactionConfig
    {
        public string Id { get; set; }
        public List<string> Sources { get; set; } = new List<string>();
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        // enabled is always there: a study switches any reaction on / off (catalog)
        public ReactionConfig() { Parameters["enabled"] = true; }
        public ReactionConfig(string id, params string[] sources) : this() { Id = id; Sources.AddRange(sources); }

        public T GetParameter<T>(string key, T defaultValue = default(T)) => ConfigParameters.Get(Parameters, key, defaultValue);

        /// <summary>The key of field <paramref name="field"/> of action <paramref name="index"/> (1 = first).</summary>
        public static string ActionKey(int index, string field) => "a" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + field;

        /// <summary>Appends an action (its fields by name; null values are left out) and returns its index.</summary>
        public int AddAction(string kind, string target = null, string name = null, object value = null, string state = null,
                             string mode = null, double duration = 0, string team = null, int count = 0,
                             string group = null, string condition = null)
        {
            int n = 1;
            while (Parameters.ContainsKey(ActionKey(n, "kind"))) n++;
            Parameters[ActionKey(n, "kind")] = kind;
            if (target != null) Parameters[ActionKey(n, "target")] = target;
            if (name != null) Parameters[ActionKey(n, "name")] = name;
            if (value is string text) Parameters[ActionKey(n, "expression")] = text;
            else if (value != null) Parameters[ActionKey(n, "number")] = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            if (state != null) Parameters[ActionKey(n, "state")] = state;
            if (mode != null) Parameters[ActionKey(n, "mode")] = mode;
            if (duration != 0) Parameters[ActionKey(n, "duration")] = duration;
            if (team != null) { Parameters[ActionKey(n, "team")] = team; Parameters[ActionKey(n, "count")] = (double)count; }
            if (!string.IsNullOrWhiteSpace(group)) Parameters[ActionKey(n, "group")] = group.Trim();
            if (!string.IsNullOrWhiteSpace(condition)) Parameters[ActionKey(n, "condition")] = condition.Trim();
            return n;
        }
    }

    [Serializable]
    public class DowntimeConfig
    {
        public string Id { get; set; }
        public string Kind { get; set; } = "MtbfMttr";
        public List<string> TargetIds { get; set; } = new List<string>();
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        public DowntimeConfig() { }

        public DowntimeConfig(string id, string kind, params string[] targetIds)
        {
            Id = id; Kind = kind;
            TargetIds.AddRange(targetIds);
        }

        public T GetParameter<T>(string key, T defaultValue = default(T)) => ConfigParameters.Get(Parameters, key, defaultValue);
    }

    /// <summary>
    /// Reads a value from a config parameter bag, the same way for elements, connections and downtimes: missing or
    /// null = <c>defaultValue</c>; a value of another primitive type is converted (configs loaded from JSON carry
    /// <c>long</c> / <c>double</c>, invariant culture); any other mismatch throws <see cref="FormatException"/>
    /// naming the key, instead of silently using the default.
    /// </summary>
    public static class ConfigParameters
    {
        public static T Get<T>(IDictionary<string, object> parameters, string key, T defaultValue = default(T))
        {
            if (parameters == null || !parameters.TryGetValue(key, out object v) || v == null) return defaultValue;
            if (v is T t) return t;
            try
            {
                if (v is IConvertible && (typeof(T).IsPrimitive || typeof(T) == typeof(string)))
                    return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is OverflowException)
            {
                throw new FormatException($"Parameter '{key}': '{v}' is not a {typeof(T).Name}.", e);
            }
            throw new FormatException($"Parameter '{key}': expected {typeof(T).Name}, found {v.GetType().Name}.");
        }
    }
}
