using System;
using System.Collections.Generic;
using R = SimuLean.Serialization.SettingRole;
using L = SimuLean.Serialization.LogicSite;
using F = SimuLean.Serialization.LogicForm;

namespace SimuLean.Serialization
{
    /// <summary>
    /// A whole model as data: elements, connections, lists, resource teams, locations, downtimes and run settings.
    /// Extracted from a Unity scene or written in code, and rebuilt by the HeadlessModelFactory.
    /// </summary>
    [Serializable]
    public class SimulationConfig
    {
        /// <summary>The elements of the model.</summary>
        public List<ElementConfig> Elements { get; set; } = new List<ElementConfig>();
        /// <summary>The connections between its elements and lists.</summary>
        public List<ConnectionConfig> Connections { get; set; } = new List<ConnectionConfig>();
        /// <summary>The run length in model time units (also the default of a study's horizon).</summary>
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
        /// <summary>End condition: the run ends when the element <see cref="StopAt"/> has counted this many items (0 = off).</summary>
        public int StopAtCount { get; set; }
        /// <summary>End condition: the run stops when nothing is left to do — no event can move items and every
        /// item is out, at rest in a buffer without outputs, or surplus no element will ever take — or on a
        /// deadlock / stall.</summary>
        public bool StopWhenEmpty { get; set; }
        /// <summary>End condition: the run stops (<see cref="RunEndReason.NoProgress"/>) when no item has entered or left
        /// any element for this model time while items wait — a stall beside events that never run out (a periodic signal, a
        /// reaction), which the "when empty" check cannot see. 0 = off. Choose it longer than the longest process, stop or
        /// break of the model; an element off shift until its next window is never taken for a lack of progress.</summary>
        public double StopWhenNoProgressFor { get; set; }

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
        /// their process and / or the transport of their items through their <c>processBy</c> / <c>transportBy</c> settings.</summary>
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

        /// <summary>The model's calendar: its start date, time unit and holidays.</summary>
        public SimCalendar CreateCalendar()
            => string.IsNullOrWhiteSpace(StartDateTime)
                ? SimCalendar.Undated(TimeUnitSeconds > 0 ? TimeUnitSeconds : 1.0)
                : SimCalendar.Parse(StartDateTime, TimeUnitSeconds > 0 ? TimeUnitSeconds : 1.0);

        /// <summary>An empty model.</summary>
        public SimulationConfig()
        {
        }
    }

    /// <summary>
    /// One element of the model: its type (handler), name and typed settings (<see cref="ElementSettings"/>, one
    /// class per type — <see cref="ElementSettings.For"/>). Settings are read and written by path (<see cref="Get{T}"/>,
    /// <see cref="Set"/>: <c>capacity</c>, <c>delayStrategy.constantValue</c>, <c>tasks[0].priority</c>); a path the type
    /// does not declare fails, naming the element, the path and the closest valid ones.
    /// </summary>
    [Serializable]
    public class ElementConfig : IConfigValues
    {
        private string type;

        /// <summary>Its unique id (connections refer to it).</summary>
        public string Id { get; set; }                    // unique id (connections refer to it)
        /// <summary>The handler type name (e.g. "Source", "Queue"); setting it gives the config settings of that
        /// type when it has none or they are of another type — the settings the new type also has are kept.</summary>
        public string Type
        {
            get => type;
            set
            {
                type = value;
                var t = ElementSettings.TypeFor(value);
                if (t == null || (Settings != null && Settings.GetType() == t)) return;
                var old = Settings;
                Settings = ElementSettings.For(value);
                // the settings given (not the old type's defaults: a Queue's capacity 100 is not a Multi-Server's)
                if (old != null)
                    foreach (var (path, v) in new List<(string, object)>(SettingsPaths.Values(old, all: false)))
                        if (SettingsPaths.Declares(t, path)) SettingsPaths.SetValue(Settings, path, v, $"Element '{Name ?? Id}' ({value})");
            }
        }
        /// <summary>Its name (what results, studies and expressions call it).</summary>
        public string Name { get; set; }                  // element name
        /// <summary>The element's settings (null for a type the engine does not know).</summary>
        public ElementSettings Settings { get; set; }

        /// <summary>An empty element config.</summary>
        public ElementConfig()
        {
        }

        /// <summary>An element of an engine type (its settings start at their defaults).</summary>
        public ElementConfig(string id, string type, string name)
        {
            Id = id;
            Type = type;
            Name = name;
        }

        /// <summary>The settings as their type (null when they are of another one).</summary>
        public T SettingsAs<T>() where T : ElementSettings => Settings as T;

        private string Owner => $"Element '{Name ?? Id}' ({Type})";

        /// <summary>
        /// The setting at <paramref name="path"/> as <typeparamref name="T"/>; not given (or a path the type does not have) =
        /// <paramref name="defaultValue"/>. A value of another primitive type is converted (invariant culture).
        /// </summary>
        public T Get<T>(string path, T defaultValue = default(T))
            => ConfigValues.Get(Settings, Settings?.GetType(), path, defaultValue, Owner);

        /// <summary>Writes a setting (converted to its type); a path the element's type does not have fails, naming it and
        /// the closest valid ones.</summary>
        public void Set(string path, object value)
        {
            if (Settings == null)
                throw new ArgumentException($"{Owner}: '{Type}' is not an element type of the engine (its settings are unknown).");
            SettingsPaths.SetValue(Settings, path, value, Owner);
        }

        /// <summary>True when the setting has a value (false for a path the type does not have).</summary>
        public bool Has(string path) => TryGet(path, out _);

        /// <summary>The setting's value as stored (false when not given or not declared).</summary>
        public bool TryGet(string path, out object value) => ConfigValues.TryGet(Settings, Settings?.GetType(), path, Owner, out value);

        /// <summary>Clears a setting (its default applies again); nothing for a path the type does not have.</summary>
        public void Remove(string path) => ConfigValues.Remove(Settings, Settings?.GetType(), path, Owner);

        /// <summary>Every setting given, as (path, value) (<see cref="SettingsPaths.Values"/>).</summary>
        public IEnumerable<(string path, object value)> Values() => SettingsPaths.Values(Settings);
    }

    /// <summary>
    /// One connection between two elements (or an element and a list). Typed, with the keys <c>port</c> and <c>query</c>.
    /// </summary>
    [Serializable]
    public class ConnectionConfig
    {
        /// <summary>The ids of its ends (an element or a list).</summary>
        public string SourceId { get; set; }
        /// <summary>See <see cref="SourceId"/>.</summary>
        public string TargetId { get; set; }
        /// <summary>"General", "StockInput", "ReturnsOutput", "PushToList", "PullFromList", "PortInput", "PortOutput" (an unknown
        /// one fails the build).</summary>
        public string ConnectionType { get; set; }
        /// <summary>PortInput / PortOutput, or a list pulled into a ported element: the element's named port.</summary>
        public string Port { get; set; }
        /// <summary>PullFromList into a Combiner: its input (0 = the main input, k = component input k − 1; absent = main).</summary>
        public int? Input { get; set; }
        /// <summary>PullFromList: the query of the entries pulled (<see cref="Collections.ListQuerySpec"/> text; empty = any,
        /// the list's order).</summary>
        public string Query { get; set; }

        /// <summary>An empty connection.</summary>
        public ConnectionConfig()
        {
        }

        /// <summary>A connection between two ends.</summary>
        public ConnectionConfig(string sourceId, string targetId, string connectionType = "General")
        {
            SourceId = sourceId;
            TargetId = targetId;
            ConnectionType = connectionType;
        }

        /// <summary>The type of a connection as the model reads it: no type (a hand-written or JSON config) and
        /// <c>"Simple"</c> (the former name) are <c>"General"</c>, any other is returned as given. The one place this is
        /// decided: the factory wires by it and the model descriptions list flow by it, so they agree (a check
        /// of <c>== "General"</c> missed a connection without a type). A static method, not a property, so the
        /// config's JSON is unchanged.</summary>
        public static string EffectiveType(ConnectionConfig connection) => EffectiveType(connection?.ConnectionType);

        /// <summary>See <see cref="EffectiveType(ConnectionConfig)"/>.</summary>
        public static string EffectiveType(string connectionType) =>
            connectionType == null || connectionType == "Simple" ? "General" : connectionType;
    }

    /// <summary>A list: its name and default order (empty = FIFO).</summary>
    [Serializable]
    public class ListConfig
    {
        /// <summary>Its id (unique among its kind; what the model refers to).</summary>
        public string Id { get; set; }
        /// <summary>Its name.</summary>
        public string Name { get; set; }
        /// <summary>The default order of its entries (a field, empty = FIFO).</summary>
        public string DefaultOrderBy { get; set; } = "";
        /// <summary>The default order is descending.</summary>
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
        /// <summary>Its name.</summary>
        public string Name { get; set; }
        /// <summary>Where its rows come from (<see cref="DataSourceKind"/>).</summary>
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

    /// <summary>How a resource team's members move between places (<see cref="ResourceTeamConfig.Travel"/>).</summary>
    public enum TravelKind
    {
        /// <summary>No position: travel takes no time (the default).</summary>
        Abstract,
        /// <summary>On the model's network at the team's speed (traffic control optional).</summary>
        Network,
        /// <summary>By the distance matrix <see cref="ResourceTeamConfig.Matrix"/> at the team's speed.</summary>
        Matrix,
        /// <summary>In straight lines between the coordinates of the network's nodes (no obstacles, no tracks; a scene team's
        /// default).</summary>
        Straight,
        /// <summary>Free movement around obstacles on the walk grid <see cref="ResourceTeamConfig.WalkGrid"/> (A*).</summary>
        Grid,
        /// <summary>Kinematic, by axes between the coordinates of the network's nodes (<see cref="ResourceTeamConfig.AxisSpeed"/>,
        /// <see cref="ResourceTeamConfig.AxisAccel"/>).</summary>
        Axes,
        /// <summary>Times calibrated from Unity physics on the estimate model <see cref="ResourceTeamConfig.CalibrationBase"/>.</summary>
        Calibrated,
        /// <summary>Moves timed outside the engine (Unity physics in a scene run); distances from the estimate model.</summary>
        External,
    }

    /// <summary>
    /// A resource team (typed settings): its members and the dispatcher's orders; elements refer to it by
    /// <see cref="Id"/>. Every field is a setting read and written by its path (<c>speed</c>, <c>axisSpeed</c>,
    /// <c>members[0].loadTime</c>, <c>calibrationPairs[1].mean</c>) — the catalog's and a study's <c>team:T.key</c>; an
    /// undeclared path or an unknown option fails when written. The catalog adds <c>members</c> (the count) and
    /// <c>loadTime</c> / <c>unloadTime</c> (every member's).
    /// </summary>
    [Serializable]
    public sealed class ResourceTeamConfig : SettingsGroup, IConfigValues
    {
        /// <summary>The id the elements' uses refer to, and the team's name (empty = the id).</summary>
        [Setting(Role = R.Structural)] public string Id;
        /// <summary>Its name (unique in its list).</summary>
        [Setting(Role = R.Structural)] public string Name;
        /// <summary>The members (<c>members[i]</c>).</summary>
        public List<ResourceMemberConfig> Members = new List<ResourceMemberConfig>();
        /// <summary>Order of the requests: the name of one of <see cref="NamedOrders"/> (a choice for a study), or a
        /// <see cref="Collections.ListQuerySpec"/> text written in place (order part used; "" = first come, first served), or
        /// <c>rule:Type</c> — a dispatch rule (<see cref="SimuLean.Rules.IDispatchRule"/>; also as a named order's query).</summary>
        [Setting(Role = R.ChoiceFrom, OptionsFrom = "namedOrders", Logic = L.Dispatch, Form = F.Query, LogicDefault = "first come, first served")] public string RequestOrder = "";
        /// <summary>Named request orders (<c>namedOrders[i]</c>: name, query) that <see cref="RequestOrder"/> may name.</summary>
        [Setting(Logic = L.Dispatch)] public List<NamedOrderSettings> NamedOrders;
        /// <summary>The parameters of its dispatch rule (a request order <c>rule:Type</c>; <c>rule[i]</c> records, catalog
        /// <c>team:T.rule.&lt;Field&gt;</c>).</summary>
        [Setting(Role = R.Properties, Logic = L.None)] public List<PropertySettings> Rule;
        /// <summary>Order of the free resources ("" = FIFO = longest idle first).</summary>
        [Setting(Role = R.Structural, Logic = L.Dispatch, Form = F.Query, LogicDefault = "longest idle first")] public string ResourceOrder = "";
        /// <summary>How the members move (<see cref="TravelKind"/>); spatial members start at <see cref="HomeNode"/>. Its
        /// design, not a parameter.</summary>
        [Setting(Role = R.Structural, Logic = L.None)] public TravelKind Travel = TravelKind.Abstract;
        /// <summary>Units of the team's speeds and times (<see cref="ResourceUnits"/>): Model = distance per time unit, time
        /// units; Physical = distance per second, seconds — converted at build with the calendar's seconds per time unit.</summary>
        [Setting(Logic = L.None)] public ResourceUnits Units = ResourceUnits.Model;
        /// <summary>The members' speed (Network, Matrix, Straight, Grid; the estimate of Calibrated / External).</summary>
        [Setting("Speed", Min = 0)] public double Speed = 1;
        /// <summary>Where spatial members start (a location of the model).</summary>
        [Setting(Role = R.Structural)] public string HomeNode;
        /// <summary>Spatial members walk back to <see cref="HomeNode"/> after each task and are free only there (default off:
        /// a free member waits where its last task ended).</summary>
        [Setting("Return home after each task")] public bool ReturnHome;
        /// <summary>What a station's job does when an Immediate stop (a breakdown, a shift) of a member interrupts it
        /// (<see cref="SimuLean.Resources.ResourceStopPolicy"/>: HandOver — the default — or WaitForSame). Only jobs
        /// whose use has a preemption mode take part.</summary>
        [Setting(Logic = L.Resources)] public SimuLean.Resources.ResourceStopPolicy JobOnMemberStop = SimuLean.Resources.ResourceStopPolicy.HandOver;
        /// <summary>Network travel under traffic control (one vehicle per track — <see cref="NetworkConfig.SegmentCapacity"/> —,
        /// waits at nodes, deadlocks resolved and counted).</summary>
        [Setting("Traffic control")] public bool Traffic;
        /// <summary>Matrix travel: id of a <see cref="SimulationConfig.TravelMatrices"/> entry.</summary>
        [Setting(Role = R.Structural)] public string Matrix;
        /// <summary>Grid travel: id of a <see cref="SimulationConfig.WalkGrids"/> entry.</summary>
        [Setting(Role = R.Structural)] public string WalkGrid;
        /// <summary>Axes travel: max speed per axis x, y, z (0 = the axis does not move).</summary>
        [Setting("Axis speeds", Min = 0)] public double[] AxisSpeed = { 1, 1, 1 };
        /// <summary>Axes travel: acceleration per axis x, y, z (0 = instant).</summary>
        [Setting("Axis accelerations", Min = 0)] public double[] AxisAccel = { 0, 0, 0 };
        /// <summary>Axes travel: axes move one after the other (true) or together (false, default).</summary>
        [Setting("Axes one after the other")] public bool AxesSequential;
        /// <summary>Calibrated / External travel: the estimate model giving the distances (Network, Matrix or Grid, with this
        /// team's fields).</summary>
        [Setting(Role = R.Structural, Logic = L.None)] public TravelKind? CalibrationBase;
        /// <summary>Calibrated travel: the fitted general model A, B, C (time = A + B · distance + C · loaded). Measured, not
        /// a parameter.</summary>
        [Setting(Role = R.Structural)] public double[] CalibrationFit = { 0, 0, 0 };
        /// <summary>Calibrated travel: the observed pairs (<c>calibrationPairs[i]</c>) and the samples a pair needs to be used.</summary>
        public List<CalibrationPairSettings> CalibrationPairs = new List<CalibrationPairSettings>();
        /// <summary>Calibration min samples; default 3.</summary>
        [Setting(Role = R.Structural)] public int CalibrationMinSamples = 3;

        private string Owner => "Resource team '" + (Name ?? Id) + "'";

        /// <summary>The setting at <paramref name="path"/> as <typeparamref name="T"/> (absent / not declared = the default).</summary>
        public T Get<T>(string path, T defaultValue = default(T))
            => ConfigValues.Get(this, typeof(ResourceTeamConfig), path, defaultValue, Owner);

        /// <summary>Writes a setting (converted to its type); an undeclared path fails naming the closest ones.</summary>
        public void Set(string path, object value) => SettingsPaths.SetValue(this, path, value, Owner);

        /// <summary>True when the setting has a value.</summary>
        public bool Has(string path) => TryGet(path, out _);

        /// <summary>The setting's value as stored (false when absent or not declared).</summary>
        public bool TryGet(string path, out object value) => ConfigValues.TryGet(this, typeof(ResourceTeamConfig), path, Owner, out value);

        /// <summary>Clears a setting (its default applies again).</summary>
        public void Remove(string path) => ConfigValues.Remove(this, typeof(ResourceTeamConfig), path, Owner);

        /// <summary>Every setting, as (path, value).</summary>
        public IEnumerable<(string path, object value)> Values() => SettingsPaths.Values(this);
    }

    /// <summary>A member of a resource team (<c>members[i]</c>).</summary>
    [Serializable]
    public sealed class ResourceMemberConfig : SettingsGroup
    {
        /// <summary>Its name (empty = the team's name and its number).</summary>
        [Setting(Role = R.Structural)] public string Name;
        /// <summary>The member's properties as <c>name = expression</c> records (skills, attributes): what requirement queries
        /// read.</summary>
        [Setting(Role = R.Structural)] public List<string> Properties = new List<string>();
        /// <summary>Its load and unload times (the catalog's <c>loadTime</c> / <c>unloadTime</c> set every member's).</summary>
        [Setting(Role = R.Structural)] public double LoadTime;
        /// <summary>See <see cref="LoadTime"/>.</summary>
        [Setting(Role = R.Structural)] public double UnloadTime;
    }

    /// <summary>The observed travel times of one pair of places of a calibrated team (<c>calibrationPairs[i]</c>; it was a
    /// record <c>from|to|loaded|mean|sd|n</c>).</summary>
    [Serializable]
    public sealed class CalibrationPairSettings : SettingsGroup
    {
        /// <summary>The places the moves went from and to.</summary>
        [Setting(Role = R.Structural)] public string From, To;
        /// <summary>Moves with a load (true) or empty.</summary>
        [Setting(Role = R.Structural)] public bool Loaded;
        /// <summary>The mean and standard deviation of the observed times.</summary>
        [Setting(Role = R.Structural)] public double Mean, Sd;
        /// <summary>How many moves were observed.</summary>
        [Setting(Role = R.Structural)] public int N;

        /// <summary>The engine's statistics of the pair.</summary>
        public SimuLean.Resources.CalibratedTravel.PairStat ToStat()
            => new SimuLean.Resources.CalibratedTravel.PairStat { From = From, To = To, Loaded = Loaded, Mean = Mean, Sd = Sd, N = N };

        /// <summary>The settings of the engine's statistics of a pair.</summary>
        public static CalibrationPairSettings Of(SimuLean.Resources.CalibratedTravel.PairStat p)
            => new CalibrationPairSettings { From = p.From, To = p.To, Loaded = p.Loaded, Mean = p.Mean, Sd = p.Sd, N = p.N };
    }

    /// <summary>A walkable area: a grid on the x / z plane from (<see cref="OriginX"/>, <see cref="OriginZ"/>), cells of
    /// <see cref="CellSize"/>, <see cref="Rows"/> (row = z index, char = x index; '#' blocked, '.' free).</summary>
    [Serializable]
    public class WalkGridConfig
    {
        /// <summary>Its id (unique among its kind; what the model refers to).</summary>
        public string Id { get; set; }
        /// <summary>The x of the grid's origin corner.</summary>
        public double OriginX { get; set; }
        /// <summary>The z of the grid's origin corner.</summary>
        public double OriginZ { get; set; }
        /// <summary>The height of the walkable floor.</summary>
        public double Y { get; set; }
        /// <summary>The side of a cell; default 0.5.</summary>
        public double CellSize { get; set; } = 0.5;
        /// <summary>The rows of cells (row = z index, char = x index; '#' blocked, '.' free).</summary>
        public List<string> Rows { get; set; } = new List<string>();
    }

    /// <summary>A distance matrix: its entries (<see cref="TravelMatrixEntryConfig"/>); <see cref="Symmetric"/> = a missing
    /// pair uses the reverse one.</summary>
    [Serializable]
    public class TravelMatrixConfig
    {
        /// <summary>Its id (unique among its kind; what the model refers to).</summary>
        public string Id { get; set; }
        /// <summary>A missing pair uses the reverse one; default true.</summary>
        public bool Symmetric { get; set; } = true;
        /// <summary>The distances between pairs of locations.</summary>
        public List<TravelMatrixEntryConfig> Entries { get; set; } = new List<TravelMatrixEntryConfig>();
    }

    /// <summary>The distance between two locations of a travel matrix (it was a record <c>from|to|distance</c>).</summary>
    [Serializable]
    public sealed class TravelMatrixEntryConfig
    {
        /// <summary>The locations.</summary>
        public string From, To;
        /// <summary>The distance from <see cref="From"/> to <see cref="To"/>.</summary>
        public double Distance;

        /// <summary>An entry.</summary>
        public static TravelMatrixEntryConfig Of(string from, string to, double distance)
            => new TravelMatrixEntryConfig { From = from, To = to, Distance = distance };
    }

    /// <summary>A transport network: its nodes and tracks (typed records; they were texts <c>id|x|y|z</c> and
    /// <c>from|to|length|bidirectional|type</c>).</summary>
    [Serializable]
    public class NetworkConfig
    {
        /// <summary>The nodes of the network.</summary>
        public List<NetworkNodeConfig> Nodes { get; set; } = new List<NetworkNodeConfig>();
        /// <summary>The tracks between its nodes.</summary>
        public List<NetworkTrackConfig> Tracks { get; set; } = new List<NetworkTrackConfig>();
        /// <summary>Traffic: vehicles a track holds at once (teams with <c>Traffic</c>).</summary>
        public int SegmentCapacity { get; set; } = 1;
    }

    /// <summary>A node of the transport network: its id and coordinates.</summary>
    [Serializable]
    public sealed class NetworkNodeConfig
    {
        /// <summary>The node's id (the location elements and teams name).</summary>
        public string Id;
        /// <summary>Its coordinates.</summary>
        public double X, Y, Z;

        /// <summary>A node.</summary>
        public static NetworkNodeConfig Of(string id, double x, double y, double z) => new NetworkNodeConfig { Id = id, X = x, Y = y, Z = z };
    }

    /// <summary>A track between two nodes of the transport network.</summary>
    [Serializable]
    public sealed class NetworkTrackConfig
    {
        /// <summary>The nodes it joins.</summary>
        public string From, To;
        /// <summary>Its length (&lt; 0 = the straight distance between the nodes).</summary>
        public double Length = -1;
        /// <summary>Whether it is travelled both ways.</summary>
        public bool Bidirectional = true;
        /// <summary>What it is.</summary>
        public SimuLean.Transport.TrackType Type = SimuLean.Transport.TrackType.TravelPath;

        /// <summary>A track.</summary>
        public static NetworkTrackConfig Of(string from, string to, double length, bool bidirectional, SimuLean.Transport.TrackType type)
            => new NetworkTrackConfig { From = from, To = to, Length = length, Bidirectional = bidirectional, Type = type };
    }

    /// <summary>
    /// A reaction (built by <see cref="ReactionBuilder"/>): its source elements (ids) and its typed settings
    /// (<see cref="ReactionSettings"/>), read and written by path — <c>reactionEnabled</c>, <c>signal</c>,
    /// <c>state</c>, <c>condition</c>, <c>period</c> / <c>firstTime</c> / <c>atTime</c>, <c>timing</c> and the actions
    /// <c>actions[0].kind</c>, <c>actions[0].target</c>, <c>actions[0].value</c>…; an undeclared path or an unknown option fails
    /// when written.
    /// </summary>
    [Serializable]
    public class ReactionConfig : IConfigValues
    {
        /// <summary>Its id (unique among its kind; what the model refers to).</summary>
        public string Id { get; set; }
        /// <summary>The ids of the elements it listens to (none for a time signal).</summary>
        public List<string> Sources { get; set; } = new List<string>();
        /// <summary>Its typed settings.</summary>
        public ReactionSettings Settings { get; set; } = new ReactionSettings();

        /// <summary>An empty reaction.</summary>
        public ReactionConfig() { }
        /// <summary>A reaction listening to its source elements.</summary>
        public ReactionConfig(string id, params string[] sources) : this() { Id = id; Sources.AddRange(sources); }

        private string Owner => "Reaction '" + Id + "'";

        /// <summary>The setting at <paramref name="path"/> as <typeparamref name="T"/> (absent / not declared = the default).</summary>
        public T Get<T>(string path, T defaultValue = default(T))
            => ConfigValues.Get(Settings, typeof(ReactionSettings), path, defaultValue, Owner);

        /// <summary>Writes a setting (converted to its type); an undeclared path or an unknown option fails.</summary>
        public void Set(string path, object value)
        {
            if (Settings == null) Settings = new ReactionSettings();
            SettingsPaths.SetValue(Settings, path, value, Owner);
        }

        /// <summary>True when the setting has a value.</summary>
        public bool Has(string path) => TryGet(path, out _);

        /// <summary>The setting's value as stored (false when absent or not declared).</summary>
        public bool TryGet(string path, out object value) => ConfigValues.TryGet(Settings, typeof(ReactionSettings), path, Owner, out value);

        /// <summary>Clears a setting (its default applies again).</summary>
        public void Remove(string path) => ConfigValues.Remove(Settings, typeof(ReactionSettings), path, Owner);

        /// <summary>Every setting given, as (path, value).</summary>
        public IEnumerable<(string path, object value)> Values() => SettingsPaths.Values(Settings);

        /// <summary>Appends an action (an option by its name; a number value is kept as its text — the builder reads a plain
        /// number as a number, anything else as an expression) and returns its number (1 = first).</summary>
        public int AddAction(string kind, string target = null, string name = null, object value = null, string state = null,
                             string mode = null, double duration = 0, string team = null, int count = 0,
                             string group = null, string condition = null, string color = null, double scale = 0)
        {
            if (Settings == null) Settings = new ReactionSettings();
            if (Settings.Actions == null) Settings.Actions = new List<ReactionActionSettings>();
            int k = Settings.Actions.Count;
            string at = "actions[" + k.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
            var a = new ReactionActionSettings
            {
                Kind = (SimuLean.Signals.ReactionActionKind)SettingsPaths.Convert(kind, typeof(SimuLean.Signals.ReactionActionKind), Owner, at + ".kind"),
                Target = target, Name = name, State = state,
                Value = value == null ? null : value is string text ? text
                      : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                Duration = duration != 0 ? duration : (double?)null,
                Team = team, Count = team != null ? count : (int?)null,
                Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim(),
                OnlyIf = string.IsNullOrWhiteSpace(condition) ? null : condition.Trim(),
                Color = string.IsNullOrWhiteSpace(color) ? null : color.Trim(),
                Scale = scale != 0 ? scale : (double?)null,
            };
            if (mode != null) a.Mode = (StopMode)SettingsPaths.Convert(mode, typeof(StopMode), Owner, at + ".mode");
            Settings.Actions.Add(a);
            return k + 1;
        }
    }

    /// <summary>The settings of a reaction: when it fires (its signal, state filter, condition, the time
    /// signals' times), when its actions run and the actions, in order. What the reaction is (its signal, actions, modes) is
    /// its design, not a parameter (by decision): the catalog offers <c>reactionEnabled</c>, the time signals' times
    /// and the actions' plain numbers.</summary>
    [Serializable]
    public sealed class ReactionSettings : SettingsGroup
    {
        /// <summary>Whether it reacts at all (a study switches any reaction on / off).</summary>
        [Setting("Enabled")] public bool ReactionEnabled = true;
        /// <summary>What it listens to (an element's signal or a time signal).</summary>
        [Setting(Role = R.Structural, Logic = L.Reaction)] public SimuLean.Signals.SignalKind Signal = SimuLean.Signals.SignalKind.ItemEntered;
        /// <summary>StateChanged: the state it reacts to (absent = any).</summary>
        [Setting(Role = R.Structural, Logic = L.Reaction)] public string State;
        /// <summary>The condition that must hold for the actions to run (an expression; absent = always).</summary>
        [Setting(Role = R.Structural, Logic = L.Reaction)] public string Condition;
        /// <summary>Every: the period (required) and the first time (absent = one period after the start).</summary>
        [Setting("Every: period", Units = "time", Min = 0)] public double? Period;
        /// <summary>See <see cref="Period"/>.</summary>
        [Setting("Every: first time", Units = "time", Min = 0)] public double? FirstTime;
        /// <summary>AtTime: the time (required).</summary>
        [Setting("At time", Units = "time", Min = 0)] public double? AtTime;
        /// <summary>When the actions run (<see cref="SimuLean.Signals.SignalDelivery"/>).</summary>
        [Setting(Role = R.Structural, Logic = L.Reaction)] public SimuLean.Signals.SignalDelivery Timing = SimuLean.Signals.SignalDelivery.NextEvent;
        /// <summary>The actions, in order (<c>actions[i]</c>).</summary>
        [Setting(Logic = L.Reaction)] public List<ReactionActionSettings> Actions;
    }

    /// <summary>
    /// One action of a reaction (<c>actions[i]</c>; it was the keys <c>actions[N].field</c> of a parameter bag). One record for
    /// every kind: each kind reads the fields it needs (<see cref="ReactionBuilder"/>) and the others stay absent. A new kind
    /// is a value of <see cref="SimuLean.Signals.ReactionActionKind"/> with its case in the builder and the run; a field it
    /// needs that no kind has yet is one more field here.
    /// </summary>
    [Serializable]
    public sealed class ReactionActionSettings : SettingsGroup
    {
        /// <summary>What it does.</summary>
        [Setting(Role = R.Structural, Logic = L.Reaction)] public SimuLean.Signals.ReactionActionKind Kind;
        /// <summary>The element it acts on (absent = the signal's element).</summary>
        [Setting(Role = R.Structural)] public string Target;
        /// <summary>The label, property or list it writes.</summary>
        [Setting(Role = R.Structural)] public string Name;
        /// <summary>The value: a plain number (a catalog parameter) or an expression.</summary>
        [Setting("Action value", Role = R.Expression, Logic = L.Reaction)] public string Value;
        /// <summary>A condition of this action alone (an expression; absent = always).</summary>
        [Setting(Role = R.Structural, Logic = L.Reaction)] public string OnlyIf;
        /// <summary>Stop: the state, the mode and the duration (absent = until resumed).</summary>
        [Setting(Role = R.Structural)] public string State;
        /// <summary>See <see cref="State"/>.</summary>
        [Setting(Role = R.Structural, Logic = L.Reaction)] public StopMode Mode = StopMode.Immediate;
        /// <summary>See <see cref="State"/>.</summary>
        [Setting("Action: stop duration", Units = "time", Min = 0)] public double? Duration;
        /// <summary>Stop / resume members of a team: the team and how many (0 = all).</summary>
        [Setting(Role = R.Structural)] public string Team;
        /// <summary>See <see cref="Team"/>.</summary>
        [Setting("Action: members", Min = 0)] public int? Count;
        /// <summary>Stop / resume: the group of stops a resume ends.</summary>
        [Setting(Role = R.Structural)] public string Group;
        /// <summary>Set Item Look: the colour given to the item's visual (<c>#RRGGBB</c>, <c>#RRGGBBAA</c>, <c>#RGB</c> or a name;
        /// absent = its own). View-only, so not a parameter.</summary>
        [Setting("Action: item colour", Role = R.Structural,
                 Description = "Set Item Look: the colour of the item's visual (#RRGGBB, #RRGGBBAA, #RGB or a name: red, green, blue…); absent = unchanged. View only.")]
        public string Color;
        /// <summary>Set Item Look: the scale of the item's visual, a factor of its own size (absent = unchanged). View-only:
        /// the item's size labels (lanes, packing) stay as they are.</summary>
        [Setting("Action: item scale", Role = R.Structural, Min = 0,
                 Description = "Set Item Look: the scale of the item's visual, a factor of its own size (absent = unchanged). View only: the item's size labels do not change.")]
        public double? Scale;
    }

    /// <summary>
    /// Serializable description of one downtime generator applied to one or more elements (one generator instance per
    /// target). Built by <see cref="DowntimeBuilder"/>. Parameter keys (<see cref="DowntimeKeys"/>, the component's field paths):
    /// <list type="bullet">
    /// <item>common: <c>stateName</c> (state name), <c>mode</c> (Immediate|AfterCurrent), <c>blockInput</c> (bool),
    ///   <c>blockOutput</c> (bool, absent = mode default); a repair crew's <c>repairBy.*</c>.</item>
    /// <item><c>Kind = "Timetable"</c>: <c>rows</c> List&lt;string&gt; of <c>"targetId|start|duration|state|mode|code|reason"</c>
    ///   (empty targetId = every target; start may be a number or a date), <c>overlap</c> (Allow|Serialize|Merge),
    ///   or a <c>dataTable</c> with its columns.</item>
    /// <item><c>Kind = "MtbfMttr"</c>: <c>timeToFailure</c>, <c>timeToRepair</c>, optional <c>firstFailure</c>
    ///   (<see cref="SamplerSpec"/> strings), <c>basis</c> (CalendarTime|BusyTime), <c>busyStates</c> List&lt;string&gt;,
    ///   <c>failureCode</c>.</item>
    /// <item><c>Kind = "Shift"</c>: <c>shiftPattern</c> (<see cref="Downtime.WeeklyShiftPattern"/> text), <c>holidays</c>
    ///   List&lt;string&gt; of dates.</item>
    /// <item><c>Kind = "Periodic"</c>: <c>periodicPeriod</c>, <c>periodicDuration</c>, <c>periodicFirst</c> (default 0),
    ///   optional <c>periodicUntil</c> (numbers; stops start at first, first + period, … before until), <c>allowOverlap</c>
    ///   (bool), <c>periodicCode</c>; default state SCHEDULED_DOWN.</item>
    /// </list>
    /// An unknown <c>mode</c>, <c>basis</c> or <c>overlap</c> fails the build (<see cref="ConfigParameters.Enum{T}"/>).
    /// </summary>
    [Serializable]
    public class DowntimeConfig : IConfigValues
    {
        /// <summary>Its id (unique among its kind; what the model refers to).</summary>
        public string Id { get; set; }
        /// <summary>Its kind: MtbfMttr, Timetable, Shift or Periodic.</summary>
        public string Kind { get; set; } = "MtbfMttr";
        /// <summary>The elements or resource teams it stops (by id).</summary>
        public List<string> TargetIds { get; set; } = new List<string>();
        /// <summary>Its typed settings, read and written by path (<c>timeToFailure</c>,
        /// <c>repairBy.team</c>, <c>rows[0].start</c>); an undeclared path fails naming the closest ones.</summary>
        public DowntimeSettings Settings { get; set; } = new DowntimeSettings();

        /// <summary>An empty downtime.</summary>
        public DowntimeConfig() { }

        /// <summary>A downtime of a kind on its targets.</summary>
        public DowntimeConfig(string id, string kind, params string[] targetIds)
        {
            Id = id; Kind = kind;
            TargetIds.AddRange(targetIds);
        }

        private string Owner => "Downtime '" + Id + "'";

        /// <summary>The setting at the path as <typeparamref name="T"/> (absent or not declared = the default).</summary>
        public T Get<T>(string path, T defaultValue = default(T))
            => ConfigValues.Get(Settings, typeof(DowntimeSettings), path, defaultValue, Owner);

        /// <summary>Writes a setting (converted to its type); an undeclared path or an unknown option fails.</summary>
        public void Set(string path, object value)
        {
            if (Settings == null) Settings = new DowntimeSettings();
            SettingsPaths.SetValue(Settings, path, value, Owner);
        }

        /// <summary>True when the setting has a value.</summary>
        public bool Has(string path) => TryGet(path, out _);

        /// <summary>The setting's value as stored (false when absent or not declared).</summary>
        public bool TryGet(string path, out object value) => ConfigValues.TryGet(Settings, typeof(DowntimeSettings), path, Owner, out value);

        /// <summary>Clears a setting (its default applies again).</summary>
        public void Remove(string path) => ConfigValues.Remove(Settings, typeof(DowntimeSettings), path, Owner);

        /// <summary>Every setting given, as (path, value).</summary>
        public IEnumerable<(string path, object value)> Values() => SettingsPaths.Values(Settings);
    }

    /// <summary>
    /// Reads a value from a config parameter bag, the same way for elements, connections and downtimes: missing or
    /// null = <c>defaultValue</c>; a value of another primitive type is converted (configs loaded from JSON carry
    /// <c>long</c> / <c>double</c>, invariant culture); any other mismatch throws <see cref="FormatException"/>
    /// naming the key, instead of silently using the default.
    /// </summary>
    public static class ConfigParameters
    {
        /// <summary>An option of a config by name (one rule for every enum key): empty = <paramref name="fallback"/>,
        /// any case; an unknown name fails the build naming <paramref name="owner"/>, the key and the options.</summary>
        public static T Enum<T>(string text, T fallback, string owner, string key) where T : struct
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            if (System.Enum.TryParse(text.Trim(), true, out T v) && System.Enum.IsDefined(typeof(T), v)) return v;
            throw new FormatException($"{owner}: unknown {key} '{text}' ({string.Join(", ", System.Enum.GetNames(typeof(T)))}).");
        }

        /// <summary>A value of a dictionary converted to <typeparamref name="T"/> (absent or null = the default).</summary>
        public static T Get<T>(IDictionary<string, object> parameters, string key, T defaultValue = default(T))
        {
            if (parameters == null || !parameters.TryGetValue(key, out object v) || v == null) return defaultValue;
            if (v is T t) return t;
            try
            {
                if (v is IConvertible && (typeof(T).IsPrimitive || typeof(T) == typeof(string)))
                    return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
                // an array of another element type (a model read from JSON: [1, 2] is an int[]): converted item by item
                if (typeof(T).IsArray && typeof(T).GetElementType().IsPrimitive && v is System.Collections.IList items && !(v is string))
                {
                    Type et = typeof(T).GetElementType();
                    var arr = Array.CreateInstance(et, items.Count);
                    for (int i = 0; i < items.Count; i++)
                        arr.SetValue(Convert.ChangeType(items[i], et, System.Globalization.CultureInfo.InvariantCulture), i);
                    return (T)(object)arr;
                }
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is OverflowException)
            {
                throw new FormatException($"Parameter '{key}': '{v}' is not a {typeof(T).Name}.", e);
            }
            throw new FormatException($"Parameter '{key}': expected {typeof(T).Name}, found {v.GetType().Name}.");
        }
    }
}
