using System;
using System.Collections.Generic;
using SimuLean.Resources;
using R = SimuLean.Serialization.SettingRole;
using L = SimuLean.Serialization.LogicSite;
using F = SimuLean.Serialization.LogicForm;

namespace SimuLean.Serialization
{
    // =========================================================================
    //  The typed settings of each element type. Every field is one setting; its path is the field name with the
    //  first letter in lower case (SettingsPaths). Each field's [Setting] is its one declaration for the catalog
    //  (ParameterSchema.Of): its role (a number, a yes / no or an option by its type; a text or a list declared — structural,
    //  an expression…), label, units and range. A distribution is a SamplerSettings group (its fields are the parameters).
    //  DEFAULTS: a field starts at the build's default (a Queue's capacity is 100, a use's preemption None), so a
    //  setting not given and one given at its default build the same element, and clearing a setting (null) puts its default
    //  back. A field stays NULLABLE only where "not given" means something of its own: a group whose presence switches a
    //  feature on (processBy, transportBy, setup, walkBy, outputStrategy, inputStrategy, a flat sampler), a flat sampler's
    //  distribution fields (a scene exports the chosen distribution's only), a per-task setting that takes the station's when
    //  absent (tasks[i].processBy), a size given only when the items have a model, and a text whose absence is "none" (an
    //  expression, a distribution, a table, a label to read).
    // =========================================================================

    /// <summary>
    /// The settings every element may have (its routing, admission, resource uses, place, user properties, labels and
    /// tallies), and the base of each element type's settings. <see cref="For"/> gives the settings class of a type.
    /// </summary>
    [Serializable]
    public abstract class ElementSettings : SettingsGroup
    {
        /// <summary>Its network node or travel-matrix location (<c>location</c>).</summary>
        [Setting(Role = R.Structural)] public string Location;
        /// <summary>The nodes where its walking operators work.</summary>
        [Setting(Role = R.Structural)] public List<string> WorkNodes;
        /// <summary>User properties (<c>properties[i]</c>, <see cref="PropertySettings"/>); each number (array) is a catalog
        /// parameter <c>properties.&lt;name&gt;</c>.</summary>
        [Setting(Role = R.Properties)] public List<PropertySettings> Properties;
        /// <summary>The parameters of its choice rules (<c>rule[i]</c>, <see cref="PropertySettings"/> records by the rule's
        /// <see cref="SimuLean.Rules.RuleParameterAttribute"/> field names): each number is a catalog parameter
        /// <c>rule.&lt;Field&gt;</c>. A missing one keeps the field's initial value; a routing and a release rule of one element
        /// share a name's value.</summary>
        [Setting(Role = R.Properties, Logic = L.None)] public List<PropertySettings> Rule;
        /// <summary>Labels set on the items that enter / leave it (<c>name = expression</c> records).</summary>
        [Setting(Role = R.Structural, Logic = L.Labels, Form = F.Assignments, Context = "label")] public List<string> EntryLabels, ExitLabels;
        /// <summary>Values recorded per entering item (<c>name = expression</c> records) and the label that splits them.</summary>
        [Setting(Role = R.Structural)] public List<string> Tallies;
        /// <summary>The item label that splits the tallies (one statistic per value; empty = none).</summary>
        [Setting(Role = R.Structural)] public string TallySplit;
        /// <summary>Its routing to its next elements (absent = the element's own).</summary>
        public OutputStrategySettings OutputStrategy;
        /// <summary>Its admission of items (absent = the element's own).</summary>
        public InputStrategySettings InputStrategy;
        /// <summary>The resources of its process (stations).</summary>
        public ProcessUseSettings ProcessBy;
        /// <summary>The carriers of its items to its next elements.</summary>
        public TransportUseSettings TransportBy;
        /// <summary>Its items walk by themselves to its next elements.</summary>
        public WalkSettings WalkBy;
        /// <summary>A station's setup before its process.</summary>
        public SetupSettings Setup;
        /// <summary>A job starts only when a place at its next destination can be reserved.</summary>
        [Setting("Reserve room downstream before a job", Logic = L.Resources)] public bool ReserveExit;
        /// <summary>A list puller's max wait for an entry (a number or an expression).</summary>
        [Setting(Role = R.Expression, Units = "time", Logic = L.Wait, Context = "maxWait", LogicDefaultValue = "0")] public string PullMaxWait;

        // the group of each shared setting: a type declares only the groups it has ([SettingGroupsOf])
        private static readonly Dictionary<string, SettingGroups> groupOf = new Dictionary<string, SettingGroups>(StringComparer.Ordinal)
        {
            [nameof(OutputStrategy)] = SettingGroups.Routing, [nameof(InputStrategy)] = SettingGroups.Admission,
            [nameof(TransportBy)] = SettingGroups.Transport, [nameof(WalkBy)] = SettingGroups.Walk, [nameof(PullMaxWait)] = SettingGroups.Pull,
            [nameof(ProcessBy)] = SettingGroups.Process, [nameof(Setup)] = SettingGroups.Setup, [nameof(ReserveExit)] = SettingGroups.ReserveExit,
        };

        /// <summary>The groups of shared settings a settings type has (<see cref="SettingGroupsOfAttribute"/>; none given =
        /// <see cref="SettingGroups.Flow"/>; the base class itself has every group).</summary>
        public static SettingGroups GroupsOf(Type settingsType)
        {
            if (settingsType == null || settingsType == typeof(ElementSettings)) return SettingGroups.All;
            var a = (SettingGroupsOfAttribute)Attribute.GetCustomAttribute(settingsType, typeof(SettingGroupsOfAttribute));
            return a?.Groups ?? SettingGroups.Flow;
        }

        /// <summary>True when <paramref name="settingsType"/> has the shared setting <paramref name="fieldName"/> (a field
        /// of another class, or one of no group, always).</summary>
        public static bool Offers(Type settingsType, string fieldName)
            => !groupOf.TryGetValue(fieldName, out var g) || (GroupsOf(settingsType) & g) != 0;

        /// <summary>The path keys of the shared groups a settings type has (for an error that lists them).</summary>
        public static IEnumerable<string> GroupKeys(Type settingsType)
        {
            foreach (var kv in groupOf)
                if (Offers(settingsType, kv.Key)) yield return SettingsPaths.KeyOf(kv.Key);
        }

        /// <summary>True when <paramref name="key"/> is a shared group's key that <paramref name="settingsType"/> does not have.</summary>
        public static bool IsMissingGroup(Type settingsType, string key)
        {
            foreach (var kv in groupOf)
                if (SettingsPaths.KeyOf(kv.Key) == key) return !Offers(settingsType, kv.Key);
            return false;
        }

        /// <summary>What the attributes cannot say for this type (a choice with other options than the shared field's):
        /// changes the generated schema. Default: nothing.</summary>
        protected internal virtual void Refine(ISettingsSchema schema) { }

        // ── the settings class of each type ──────────────────────────────

        private static readonly Dictionary<string, Type> types = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["Source"] = typeof(SourceSettings),
            ["Queue"] = typeof(QueueSettings),
            ["LengthLimitedQueue"] = typeof(LengthLimitedQueueSettings),
            ["Stacker"] = typeof(StackerSettings),
            ["MultiServer"] = typeof(MultiServerSettings),
            ["Delay"] = typeof(DelaySettings),
            ["Agenda"] = typeof(AgendaSettings),
            ["Combiner"] = typeof(CombinerSettings),
            ["Sink"] = typeof(SinkSettings),
            ["Separator"] = typeof(SeparatorSettings),
            ["LineSideUnpacker"] = typeof(LineSideUnpackerSettings),
            ["PickFace"] = typeof(PickFaceSettings),
            ["BaseStockReplenisher"] = typeof(ReplenisherSettings),
            ["MilkRun"] = typeof(MilkRunSettings),
            ["CallOffPoint"] = typeof(CallOffPointSettings),
            ["PacedLine"] = typeof(PacedLineSettings),
            ["SequencingStation"] = typeof(SequencingStationSettings),
            ["StorageSystem"] = typeof(StorageSystemSettings),
            ["PickingStation"] = typeof(PickingStationSettings),
            ["AssemblyStation"] = typeof(AssemblyStationSettings),
            ["ErectionDock"] = typeof(ErectionDockSettings),
        };

        /// <summary>The settings class of an element type, null for a type the engine does not know.</summary>
        public static Type TypeFor(string typeName)
            => typeName != null && types.TryGetValue(typeName, out var t) ? t : null;

        /// <summary>New, empty settings of an element type (null for a type the engine does not know).</summary>
        public static ElementSettings For(string typeName)
        {
            var t = TypeFor(typeName);
            return t == null ? null : (ElementSettings)Activator.CreateInstance(t);
        }

        /// <summary>Every element type name with typed settings.</summary>
        public static IEnumerable<string> TypeNames => types.Keys;
    }

    /// <summary>The groups of shared settings (on <see cref="ElementSettings"/>) an element type may have:
    /// writing a group the type does not have fails, naming the ones it has (a Sink has no <c>processBy</c>).</summary>
    [Flags]
    public enum SettingGroups
    {
        None = 0,
        /// <summary><c>outputStrategy</c>: the routing to its next elements.</summary>
        Routing = 1,
        /// <summary><c>inputStrategy</c>: the admission of items.</summary>
        Admission = 2,
        /// <summary><c>transportBy</c>: the carriers of its items (or, for a milk run, its train).</summary>
        Transport = 4,
        /// <summary><c>walkBy</c>: its items walk by themselves to the next elements.</summary>
        Walk = 8,
        /// <summary><c>pullMaxWait</c>: the max wait of its pull from a list.</summary>
        Pull = 16,
        /// <summary><c>processBy</c>: the resources of its process (stations).</summary>
        Process = 32,
        /// <summary><c>setup</c>: a setup before its process.</summary>
        Setup = 64,
        /// <summary><c>reserveExit</c>: a job starts only when a place downstream is reserved (job stations).</summary>
        ReserveExit = 128,
        /// <summary>An element items flow through: routing, admission, transport, walking, pull.</summary>
        Flow = Routing | Admission | Transport | Walk | Pull,
        /// <summary>Every group.</summary>
        All = Flow | Process | Setup | ReserveExit,
    }

    /// <summary>The groups of shared settings an element type has (<see cref="SettingGroups"/>; absent = <see cref="SettingGroups.Flow"/>).</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class SettingGroupsOfAttribute : Attribute
    {
        /// <summary>The groups the settings class has.</summary>
        public SettingGroupsOfAttribute(SettingGroups groups) { Groups = groups; }
        /// <summary>The groups.</summary>
        public SettingGroups Groups { get; }
    }

    // ── shared groups ────────────────────────────────────────────────────

    /// <summary>The kind of a user property's value.</summary>
    public enum PropertyValueKind
    {
        /// <summary>A number (<see cref="PropertySettings.Value"/>).</summary>
        Number,
        /// <summary>A text (<see cref="PropertySettings.Text"/>).</summary>
        Text,
        /// <summary>Another element, by name (<see cref="PropertySettings.Text"/>), resolved when the model is built.</summary>
        Reference,
        /// <summary>Numbers (<see cref="PropertySettings.Numbers"/>).</summary>
        Numbers,
        /// <summary>Texts (<see cref="PropertySettings.Texts"/>).</summary>
        Texts,
    }

    /// <summary>A user property of an element (<c>properties[i]</c>):
    /// its name, kind and value. Its fields are not catalog keys of their own: a number (array) is the parameter
    /// <c>properties.&lt;name&gt;</c> (<see cref="SettingRole.Properties"/>).</summary>
    [Serializable]
    public sealed class PropertySettings : SettingsGroup
    {
        /// <summary>The property's name (read as <c>element.name</c> in expressions).</summary>
        [Setting(Role = R.Structural)] public string Name;
        /// <summary>What its value is.</summary>
        [Setting(Role = R.Structural, Logic = L.None)] public PropertyValueKind Kind = PropertyValueKind.Number;
        /// <summary>A number's value.</summary>
        [Setting(Role = R.Structural)] public double Value;
        /// <summary>A text's value, or the name of the referenced element.</summary>
        [Setting(Role = R.Structural)] public string Text;
        /// <summary>The values of Numbers / Texts.</summary>
        [Setting(Role = R.Structural)] public double[] Numbers;
        /// <summary>The values of a texts property.</summary>
        [Setting(Role = R.Structural)] public List<string> Texts;

        /// <summary>A number property.</summary>
        public static PropertySettings Number(string name, double value) => new PropertySettings { Name = name, Kind = PropertyValueKind.Number, Value = value };
        /// <summary>A text property.</summary>
        public static PropertySettings OfText(string name, string text) => new PropertySettings { Name = name, Kind = PropertyValueKind.Text, Text = text ?? "" };
        /// <summary>A reference to the element named <paramref name="target"/>.</summary>
        public static PropertySettings Reference(string name, string target) => new PropertySettings { Name = name, Kind = PropertyValueKind.Reference, Text = target ?? "" };
        /// <summary>A number array property.</summary>
        public static PropertySettings OfNumbers(string name, params double[] values) => new PropertySettings { Name = name, Kind = PropertyValueKind.Numbers, Numbers = values ?? new double[0] };
        /// <summary>A text array property.</summary>
        public static PropertySettings OfTexts(string name, params string[] values) => new PropertySettings { Name = name, Kind = PropertyValueKind.Texts, Texts = new List<string>(values ?? new string[0]) };
    }

    /// <summary>A distribution: its type (<see cref="StrategyType"/>; absent = Constant) and the chosen distribution's
    /// own fields (the others absent; an absent field takes the distribution's default). Every random time or quantity of a
    /// model is one: a station's service time (<c>delayStrategy</c>), a source's <c>interArrivalDistribution</c>, a setup's
    /// <c>setup.time</c>, a task's <c>tasks[i].duration</c>, a leg's <c>legs[i].time</c>… (null = none, or the element's
    /// default). Each distribution field is a catalog parameter at its path (<c>setup.time.uniformMax</c>). The sampler is built
    /// with its own random stream (<c>ElementBuildHelpers.BuildRandomProcess</c>). A text <c>Type~p1~p2</c> written to
    /// it (a study level, <c>simulean_set</c>) is read as the distribution (<see cref="Parse"/>: every parameter required).</summary>
    [Serializable]
    public sealed class SamplerSettings : SettingsGroup
    {
        /// <summary>The distribution: one of <see cref="SimuLean.Optimization.ParameterSchema.SamplerTypes"/> or Expression.</summary>
        [Setting(Role = R.SamplerType, Logic = L.None)] public string StrategyType;
        /// <summary>Value.</summary>
        [Setting("Value")] public double? ConstantValue;
        /// <summary>Uniform min.</summary>
        [Setting("Uniform min")] public double? UniformMin;
        /// <summary>Uniform max.</summary>
        [Setting("Uniform max")] public double? UniformMax;
        /// <summary>Normal mean.</summary>
        [Setting("Normal mean")] public double? NormalMean;
        /// <summary>Normal std dev.</summary>
        [Setting("Normal std dev", Min = 0)] public double? NormalStdDev;
        /// <summary>Exponential rate (1/time).</summary>
        [Setting("Exponential rate", Units = "1/time")] public double? ExponentialRate;
        /// <summary>ExponentialMean: the exponential distribution given by its mean (1 / rate; MTBF / MTTR data).</summary>
        [Setting("Exponential mean", Min = 0)] public double? ExponentialMean;
        /// <summary>Triangular min.</summary>
        [Setting("Triangular min")] public double? TriangularMin;
        /// <summary>Triangular mode.</summary>
        [Setting("Triangular mode")] public double? TriangularMode;
        /// <summary>Triangular max.</summary>
        [Setting("Triangular max")] public double? TriangularMax;
        /// <summary>Gamma shape.</summary>
        [Setting("Gamma shape")] public double? GammaShape;
        /// <summary>Gamma scale.</summary>
        [Setting("Gamma scale")] public double? GammaScale;
        /// <summary>Weibull shape.</summary>
        [Setting("Weibull shape")] public double? WeibullShape;
        /// <summary>Weibull scale.</summary>
        [Setting("Weibull scale")] public double? WeibullScale;
        /// <summary>LogNormal mu.</summary>
        [Setting("LogNormal mu")] public double? LogNormalMu;
        /// <summary>LogNormal sigma.</summary>
        [Setting("LogNormal sigma")] public double? LogNormalSigma;
        /// <summary>Beta: the shape parameters alpha and beta and the range (min, max).</summary>
        public double? BetaAlpha, BetaBeta, BetaMin, BetaMax;
        /// <summary>Poisson lambda.</summary>
        [Setting("Poisson lambda")] public double? PoissonLambda;
        /// <summary>Binomial p.</summary>
        [Setting("Binomial p", Min = 0, Max = 1)] public double? BinomialP;
        /// <summary>The integer parameters: the degrees of freedom of a chi-square, Student t or F distribution, the binomial n and the discrete uniform range.</summary>
        public int? ChiSquareDf, StudentTDf, FDistD1, FDistD2, BinomialN, DiscreteUniformMin, DiscreteUniformMax;
        /// <summary>Student t: clamp the draws to non-negative values.</summary>
        public bool? StudentTClamp;
        /// <summary>An Expression distribution's formula.</summary>
        [Setting(Role = R.Structural, Logic = L.None)] public string Expression;

        /// <summary>A constant.</summary>
        public static SamplerSettings Constant(double value) => new SamplerSettings { StrategyType = "Constant", ConstantValue = value };
        /// <summary>A uniform distribution on [min, max].</summary>
        public static SamplerSettings Uniform(double min, double max) => new SamplerSettings { StrategyType = "Uniform", UniformMin = min, UniformMax = max };
        /// <summary>An exponential distribution of this rate (mean 1 / rate).</summary>
        public static SamplerSettings Exponential(double rate) => new SamplerSettings { StrategyType = "Exponential", ExponentialRate = rate };
        /// <summary>An exponential distribution of this mean.</summary>
        public static SamplerSettings ExponentialOfMean(double mean) => new SamplerSettings { StrategyType = "ExponentialMean", ExponentialMean = mean };
        /// <summary>A triangular distribution.</summary>
        public static SamplerSettings Triangular(double min, double mode, double max)
            => new SamplerSettings { StrategyType = "Triangular", TriangularMin = min, TriangularMode = mode, TriangularMax = max };
        /// <summary>A distribution written as text <c>Type~p1~p2…</c> (<c>Uniform~1~3</c>, <c>ExponentialMean~3600</c>,
        /// <c>Expression~item.weight * 2</c>): every parameter required (<see cref="SamplerSpec.Parse"/>).</summary>
        public static SamplerSettings Parse(string spec) => SamplerSpec.Parse(spec);
        /// <summary>The distribution as text <c>Type~p1~p2…</c> (its fields in order; an absent one ends the list).</summary>
        public override string ToString() => SamplerSpec.Format(this);
    }

    /// <summary>The routing of an element (its field <c>outputStrategy</c>).</summary>
    [Serializable]
    public sealed class OutputStrategySettings : SettingsGroup
    {
        /// <summary>The rule, by name.</summary>
        [Setting(Role = R.Choice, Options = typeof(HeadlessOutputStrategyType), Logic = L.Routing, LogicDefault = "FirstAvailable")] public string StrategyType;
        /// <summary>Parameterized: the item label naming the output.</summary>
        [Setting(Role = R.Structural, Logic = L.Routing, Form = F.Label, LogicWhen = "strategyType=Parameterized")] public string ParameterKey;
        /// <summary>Expression: the formula giving the output.</summary>
        [Setting(Role = R.Structural, Logic = L.Routing, Form = F.Expression, Context = "routing", LogicWhen = "strategyType=Expression")] public string RoutingExpression;
        /// <summary>Probability: the weights of the outputs, in connection order (each ≥ 0; a text <c>w1|w2|…</c> written
        /// to it is read as the numbers).</summary>
        [Setting("Routing weights", Min = 0, Logic = L.Routing, LogicWhen = "strategyType=Probability")] public double[] Weights;
        /// <summary>BestScore: the score of each output (<c>out.x</c> the output, <c>item.x</c> the item); the highest wins, ties
        /// by connection order.</summary>
        [Setting(Role = R.Expression, Logic = L.Routing, Context = "score", LogicWhen = "strategyType=BestScore")] public string ScoreExpression;
        /// <summary>BestScore: when the best output is full, skip it (score only the outputs with room) or wait for it.</summary>
        [Setting(Logic = L.Routing, LogicDefault = "Skip", LogicWhen = "strategyType=BestScore|Rule")] public BestScoreFull WhenBestFull = BestScoreFull.Skip;
        /// <summary>Rule: the routing rule's type name (an <see cref="SimuLean.Rules.IRoutingRule"/> of <c>SimuLean.UserLogic</c>):
        /// it scores each output, the highest wins, ties by connection order (<see cref="WhenBestFull"/> as for
        /// BestScore).</summary>
        [Setting(Role = R.Rule, Logic = L.Routing, Form = F.Rule, LogicWhen = "strategyType=Rule")] public string Rule;
    }

    /// <summary>The admission of an element (its field <c>inputStrategy</c>).</summary>
    [Serializable]
    public sealed class InputStrategySettings : SettingsGroup
    {
        /// <summary>The rule, by name.</summary>
        [Setting(Role = R.Choice, Options = typeof(HeadlessInputStrategyType), Logic = L.Admission, LogicDefaultValue = "Default")] public string StrategyType;
        /// <summary>SingleLabel admission: the label and the value an item must have.</summary>
        [Setting(Role = R.Structural, Logic = L.Admission, LogicWhen = "strategyType=SingleLabel")] public string SingleLabelName, SingleLabelValue;
        /// <summary>MultiLabel: the label / value records (<c>multiLabelPairs[i]</c>); admitted when any matches.</summary>
        [Setting(Role = R.Structural, Logic = L.Admission, LogicWhen = "strategyType=MultiLabel")] public List<LabelValueSettings> MultiLabelPairs;
        /// <summary>Condition: the expression an item must meet.</summary>
        [Setting(Role = R.Structural, Logic = L.Admission, Form = F.Expression, Context = "admission", LogicWhen = "strategyType=Condition")] public string Condition;
    }

    /// <summary>A station's process resources (its field <c>processBy</c>).</summary>
    [Serializable]
    public sealed class ProcessUseSettings : SettingsGroup
    {
        /// <summary>The team (id).</summary>
        [Setting(Role = R.Structural, Logic = L.Resources)] public string Team;
        /// <summary>The resources each job needs (empty = one member of the team).</summary>
        [Setting(Logic = L.Resources)] public List<RequirementSettings> Requirements;
        /// <summary>EndOfProcess / ItemLeaves / AtElement; with AtElement the element where they come back.</summary>
        [Setting(Logic = L.Resources)] public ProcessRelease Release = ProcessRelease.EndOfProcess;
        /// <summary>AtElement release: the element where the resources are released (its name or id).</summary>
        [Setting(Role = R.Structural, Logic = L.Resources, LogicWhen = "release=AtElement")] public string ReleaseAt;
        /// <summary>The request's priority (an expression).</summary>
        [Setting(Role = R.Expression, Logic = L.Resources, Context = "priority", LogicDefaultValue = "1")] public string Priority;
        /// <summary>How a request of higher priority takes its members (<see cref="PreemptMode"/>).</summary>
        [Setting(Logic = L.Resources)] public PreemptMode Preemption = PreemptMode.None;
        /// <summary>Process resources can be preempted; default true.</summary>
        [Setting("Process resources can be preempted", Logic = L.Resources)] public bool Preemptable = true;
        /// <summary>AllAtOnce / InOrder.</summary>
        [Setting(Logic = L.Resources)] public AcquireOrder Acquire = AcquireOrder.AllAtOnce;
        /// <summary>The resources secured before the part.</summary>
        [Setting("Process resources secured before the part", Logic = L.Resources)] public bool AcquireBeforeEntry;
        /// <summary>Max wait for the resources (a number or an expression), what a job does then (Reroute: a Multi-Server's
        /// only, <see cref="MultiServerSettings.Refine"/>), and where it goes.</summary>
        [Setting(Role = R.Expression, Units = "time", Logic = L.Wait, Context = "maxWait", LogicDefaultValue = "0")] public string MaxWait;
        [Setting(Role = R.Choice, Choices = new[] { nameof(TimeoutAction.KeepWaiting), nameof(TimeoutAction.GiveUp) }, Logic = L.Wait, LogicDefaultValue = "KeepWaiting", LogicWhen = "maxWait!=|0")] public string OnTimeout;
        /// <summary>Reroute on a timeout: the element the item goes to.</summary>
        [Setting(Role = R.Structural, Logic = L.Wait, LogicWhen = "maxWait!=|0;onTimeout=Reroute")] public string TimeoutTarget;
        /// <summary>The resources' own time (absent = they are held for the process).</summary>
        [Setting("Resources' own time")] public SamplerSettings HoldFor;
    }

    /// <summary>The carriers of an element's items (its field <c>transportBy</c>).</summary>
    [Serializable]
    public sealed class TransportUseSettings : SettingsGroup
    {
        /// <summary>The resource team (by id or name).</summary>
        [Setting(Role = R.Structural, Logic = L.Resources)] public string Team;
        /// <summary>The kinds of members needed (records; absent = one member of the team).</summary>
        [Setting(Logic = L.Resources)] public List<RequirementSettings> Requirements;
        /// <summary>The priority of its requests: an expression (a number, or over the item and the element).</summary>
        [Setting(Role = R.Expression, Logic = L.Resources, Context = "priority", LogicDefaultValue = "1")] public string Priority;
        /// <summary>How a request of higher priority takes its members (<see cref="PreemptMode"/>).</summary>
        [Setting(Logic = L.Resources)] public PreemptMode Preemption = PreemptMode.None;
        /// <summary>Carriers can be preempted; default true.</summary>
        [Setting("Carriers can be preempted", Logic = L.Resources)] public bool Preemptable = true;
        /// <summary>The times of a move (absent: the carriers' own load / unload time; the travel by the team's travel model).</summary>
        [Setting("Load time")] public SamplerSettings LoadTime;
        /// <summary>Travel time.</summary>
        [Setting("Travel time")] public SamplerSettings TravelTime;
        /// <summary>Unload time.</summary>
        [Setting("Unload time")] public SamplerSettings UnloadTime;
    }

    /// <summary>One kind of resource a job needs (<c>...requirements[i]</c>, a record
    /// <c>count#query#label#team</c>): how many members, which (a list query over the members, e.g. <c>skill == welding</c>;
    /// empty = any), a label for lists and reports, and the team they come from (its id; empty = the use's team).</summary>
    [Serializable]
    public sealed class RequirementSettings : SettingsGroup
    {
        /// <summary>Members required; default 1.</summary>
        [Setting("Members required", Min = 1, Logic = L.Resources)] public int Count = 1;
        /// <summary>Which members (a query over their fields and properties), the label the item gets with them, and their team (absent = the use's).</summary>
        [Setting(Role = R.Structural, Logic = L.Resources)] public string Query, Label, Team;

        /// <summary>A record of the given values.</summary>
        public static RequirementSettings Of(int count, string query = "", string label = "", string team = null)
            => new RequirementSettings { Count = count, Query = query, Label = label, Team = string.IsNullOrEmpty(team) ? null : team };
    }

    /// <summary>The resource settings of one assembly task (<c>tasks[i].processBy</c>): each one not
    /// given is the station's (its <c>processBy</c>).</summary>
    [Serializable]
    public sealed class TaskUseSettings : SettingsGroup
    {
        /// <summary>The team of the task's use (empty = the station's process team, else its first requirement's).</summary>
        [Setting(Role = R.Structural, Logic = L.Resources)] public string Team;
        /// <summary>The task's own requirements (none = the station's).</summary>
        [Setting(Logic = L.Resources)] public List<RequirementSettings> Requirements;
        /// <summary>The request's priority (an expression) and preemption mode, whether its members can be taken, the order of
        /// several requirements, the max wait and its action (KeepWaiting / GiveUp).</summary>
        [Setting(Role = R.Expression, Logic = L.Resources, Context = "priority", LogicDefaultValue = "1")] public string Priority;
        /// <summary>How a request of higher priority takes its members (<see cref="PreemptMode"/>).</summary>
        [Setting(Logic = L.Resources)] public PreemptMode? Preemption;
        /// <summary>Task: members can be preempted.</summary>
        [Setting("Task: members can be preempted", Logic = L.Resources)] public bool? Preemptable;
        /// <summary>How the members are acquired (<see cref="AcquireOrder"/>).</summary>
        [Setting(Logic = L.Resources)] public AcquireOrder? Acquire;
        /// <summary>The longest wait for the resources: a number or an expression (absent = no limit).</summary>
        [Setting(Role = R.Expression, Units = "time", Logic = L.Wait, Context = "maxWait", LogicDefaultValue = "0")] public string MaxWait;
        [Setting(Role = R.Choice, Choices = new[] { nameof(TimeoutAction.KeepWaiting), nameof(TimeoutAction.GiveUp) }, Logic = L.Wait, LogicDefaultValue = "KeepWaiting", LogicWhen = "maxWait!=|0")] public string OnTimeout;

        /// <summary>True when the task sets anything of its own.</summary>
        public bool Any => Team != null || (Requirements != null && Requirements.Count > 0) || Priority != null || Preemption != null
                           || Preemptable != null || Acquire != null || MaxWait != null || OnTimeout != null;
    }

    /// <summary>Items that walk by themselves to the next elements (its field <c>walkBy</c>).</summary>
    [Serializable]
    public sealed class WalkSettings : SettingsGroup
    {
        /// <summary>m/s (physical; 0 = they do not walk).</summary>
        [Setting("Walking speed (m/s)", Units = "m/s", Min = 0)] public double Speed;
        /// <summary>Network (default), Grid or Matrix, with the grid's / matrix's id.</summary>
        [Setting(Role = R.Choice, Choices = new[] { "Network", "Grid", "Matrix" }, Logic = L.None)] public string Route;
        /// <summary>The walk grid or travel matrix the items walk on (by its id; with the route Grid / Matrix).</summary>
        [Setting(Role = R.Structural)] public string Grid, Matrix;
        /// <summary>Set off even to a full destination and wait at its door.</summary>
        public bool WaitAtDoor;
    }

    /// <summary>A station's setup (its field <c>setup</c>).</summary>
    [Serializable]
    public sealed class SetupSettings : SettingsGroup
    {
        /// <summary>None / EachItem / LabelChange. The setup's settings are one decision of the logic view:
        /// when, the label, the first item, the time or matrix, the crew.</summary>
        [Setting(Logic = L.Resources)] public SetupWhen When = SetupWhen.None;
        /// <summary>The item label whose change asks a setup (LabelChange).</summary>
        [Setting(Role = R.Structural, Logic = L.Resources, Form = F.Label, LogicWhen = "when=LabelChange", LogicDefault = "type")] public string Label = "type";
        /// <summary>Setup for a slot's first item; default true.</summary>
        [Setting("Setup for a slot's first item", Logic = L.Resources, LogicWhen = "when!=None")] public bool SetupFirst = true;
        /// <summary>The setup's time (absent = none, unless the matrix gives it).</summary>
        [Setting("Setup time", Logic = L.Resources, LogicWhen = "when!=None")] public SamplerSettings Time;
        /// <summary>A data table <c>from</c> / <c>to</c> / <c>time</c> (its content varied as <c>data:</c>).</summary>
        [Setting(Role = R.Structural, Logic = L.Resources, LogicWhen = "when!=None")] public string Matrix;
        /// <summary>Setup crew stays for the process.</summary>
        [Setting("Setup crew stays for the process", Logic = L.Resources, LogicWhen = "when!=None")] public bool KeepForProcess;
        /// <summary>The setup crew.</summary>
        public SetupCrewSettings SetupBy;
    }

    /// <summary>A setup crew (<c>setup.setupBy</c>).</summary>
    [Serializable]
    public sealed class SetupCrewSettings : SettingsGroup
    {
        /// <summary>The resource team (by id or name).</summary>
        [Setting(Role = R.Structural, Logic = L.Resources)] public string Team;
        /// <summary>The kinds of members needed (records; absent = one member of the team).</summary>
        [Setting(Logic = L.Resources)] public List<RequirementSettings> Requirements;
        /// <summary>The priority of its requests: an expression (a number, or over the item and the element).</summary>
        [Setting(Role = R.Expression, Logic = L.Resources, Context = "priority", LogicDefaultValue = "1")] public string Priority;
        /// <summary>How a request of higher priority takes its members (<see cref="PreemptMode"/>).</summary>
        [Setting(Logic = L.Resources)] public PreemptMode Preemption = PreemptMode.None;
        /// <summary>Setup crew can be preempted; default true.</summary>
        [Setting("Setup crew can be preempted", Logic = L.Resources)] public bool Preemptable = true;
        /// <summary>How the members are acquired (<see cref="AcquireOrder"/>).</summary>
        [Setting(Logic = L.Resources)] public AcquireOrder Acquire = AcquireOrder.AllAtOnce;
        /// <summary>The longest wait for the resources: a number or an expression (absent = no limit).</summary>
        [Setting(Role = R.Expression, Units = "time", Logic = L.Wait, Context = "maxWait", LogicDefaultValue = "0")] public string MaxWait;
        [Setting(Role = R.Choice, Choices = new[] { nameof(TimeoutAction.KeepWaiting), nameof(TimeoutAction.GiveUp) }, Logic = L.Wait, LogicDefaultValue = "KeepWaiting", LogicWhen = "maxWait!=|0")] public string OnTimeout;
    }

    // ── element types ────────────────────────────────────────────────────

    /// <summary>The settings of a source (<see cref="Source"/>): arrivals by an inter-arrival distribution or a schedule table.</summary>
    [Serializable, SettingGroupsOf(SettingGroups.Routing | SettingGroups.Transport | SettingGroups.Walk)]
    public sealed class SourceSettings : ElementSettings
    {
        /// <summary>Schedule (its arrivals table), InterArrival (its inter-arrival distribution) or RateTable (a Poisson
        /// process whose rate changes by period: <see cref="RatePeriods"/> or a table with the columns Start and Rate).</summary>
        [Setting(Role = R.Choice, Choices = new[] { "Schedule", "InterArrival", "RateTable" }, Logic = L.None)] public string Mode = "Schedule";
        /// <summary>Inter-arrival time.</summary>
        [Setting("Inter-arrival time")] public SamplerSettings InterArrivalDistribution;
        /// <summary>RateTable: the periods (start, rate per model time unit, labels), by start; empty = the table (the data
        /// table / file / table source, columns Start, Rate, optional Name, the others labels).</summary>
        public List<RatePeriodSettings> RatePeriods;
        /// <summary>The schedule (column → cells), as loaded.</summary>
        [Setting(Role = R.Structural)] public Dictionary<string, List<string>> ArrivalsTable;
        /// <summary>The table's file / a table read at build time: a study varies them (<c>Owner.table=A.csv|B.csv</c>).</summary>
        [Setting(Role = R.File)] public string FileName, TableSource;
        /// <summary>Schedule arrivals: the Excel sheet, the database and the data table they are read from.</summary>
        [Setting(Role = R.Structural)] public string SheetName, TableDatabase, DataTable;
        /// <summary>Labels set on each new item (name = expression records).</summary>
        [Setting(Role = R.Structural, Logic = L.Labels, Form = F.Assignments, Context = "label")] public List<string> CreateLabels;
        /// <summary>Demand factor; default 1.0.</summary>
        [Setting("Demand factor", Min = 0.01)] public double DemandFactor = 1.0;
        /// <summary>Arrivals on stop (<see cref="SourceStopPolicy"/>); default Hold.</summary>
        [Setting(Logic = L.None)] public SourceStopPolicy ArrivalsOnStop = SourceStopPolicy.Hold;
        /// <summary>The size of the items' 3D model (m).</summary>
        [Setting("Item width (m)", Units = "m", Min = 0)] public double? ModelWidth;
        /// <summary>Item height (m).</summary>
        [Setting("Item height (m)", Units = "m", Min = 0)] public double? ModelHeight;
        /// <summary>Item length (m).</summary>
        [Setting("Item length (m)", Units = "m", Min = 0)] public double? ModelLength;
        /// <summary>The height of the load floor of its items' model (m).</summary>
        [Setting(Units = "m", Min = 0)] public double? ModelLoadFloor;
    }

    /// <summary>One period of a rate-table source (<c>ratePeriods[i]</c>): from <c>start</c> (model time) the
    /// arrivals come at <c>rate</c> per model time unit (0 = none) until the next period's start — the last one's rate holds
    /// to the end; its items get <c>labels</c> (<c>name = expression</c> records).</summary>
    [Serializable]
    public sealed class RatePeriodSettings : SettingsGroup
    {
        /// <summary>When the period starts (model time, ≥ 0, increasing).</summary>
        [Setting("Period start", Units = "time", Min = 0)] public double Start;
        /// <summary>Arrivals per model time unit in the period (0 = none).</summary>
        [Setting("Arrival rate", Units = "1/time", Min = 0)] public double Rate;
        /// <summary>Labels set on the period's items (name = expression records).</summary>
        [Setting(Role = R.Structural, Logic = L.Labels, Form = F.Assignments, Context = "label")] public List<string> Labels;

        /// <summary>A period of the given values.</summary>
        public static RatePeriodSettings Of(double start, double rate, params string[] labels)
            => new RatePeriodSettings { Start = start, Rate = rate, Labels = labels != null && labels.Length > 0 ? new List<string>(labels) : null };
    }

    /// <summary>The settings of a queue (<see cref="ItemsQueue"/>): its capacity, offer order, gate, setup grouping and max wait.</summary>
    [Serializable]
    public sealed class QueueSettings : ElementSettings
    {
        /// <summary>Capacity; default 100.</summary>
        [Setting("Capacity", Min = 1)] public int Capacity = 100;
        /// <summary>Offer order (<see cref="OfferOrder"/>); default FIFO.</summary>
        [Setting(Logic = L.Order, LogicDefault = "FIFO")] public OfferOrder OfferOrder = OfferOrder.FIFO;
        /// <summary>The order the held items are offered in: order keys <c>key [desc], key …</c>, each a label or an expression
        /// over the item and the queue (<see cref="ItemOrder"/>); empty = arrival order.</summary>
        [Setting(Role = R.Structural, Logic = L.Order, Form = F.OrderKeys, Context = "offerSort")] public string OfferSort;
        /// <summary>The release rule's type name (an <see cref="SimuLean.Rules.IReleaseRule"/>) instead of
        /// <see cref="OfferSort"/>: it scores each held item, offered best first, ties in arrival order. Not with OfferSort.</summary>
        [Setting(Role = R.Rule, Logic = L.Order, Form = F.Rule)] public string OfferRule;
        /// <summary>Items looked at.</summary>
        [Setting("Items looked at", Min = 0, Logic = L.Order, LogicWhen = "offerOrder=AnyAccepted")] public int OfferScan;
        /// <summary>Released on command: items leave only when a reaction releases them.</summary>
        [Setting(Logic = L.Order)] public bool Gated;
        /// <summary>Group items by the receiver's setup.</summary>
        [Setting("Group items by the receiver's setup", Logic = L.Order)] public bool GroupBySetup;
        /// <summary>Most items in a row ahead of the oldest.</summary>
        [Setting("Most items in a row ahead of the oldest (0 = no limit)", Min = 0, Logic = L.Order, LogicWhen = "groupBySetup=true")] public int GroupLimit;
        /// <summary>Reneging: an item's max wait (a number or an expression), what it does then, where it goes.</summary>
        [Setting(Role = R.Expression, Units = "time", Logic = L.Wait, Context = "maxWait", LogicDefaultValue = "0")] public string MaxWait;
        /// <summary>What a timeout does (<see cref="TimeoutAction"/>).</summary>
        [Setting(Role = R.Choice, Options = typeof(TimeoutAction), Logic = L.Wait, LogicDefaultValue = "KeepWaiting", LogicWhen = "maxWait!=|0")] public string OnTimeout;
        /// <summary>Reroute on a timeout: the element the item goes to (a Queue's GiveUp leaves by it too).</summary>
        [Setting(Role = R.Structural, Logic = L.Wait, LogicWhen = "maxWait!=|0;onTimeout=Reroute|GiveUp")] public string TimeoutTarget;
    }

    /// <summary>The settings of a length-limited queue (<see cref="LengthLimitedQueue"/>): lanes filled by the items' length.</summary>
    [Serializable]
    public sealed class LengthLimitedQueueSettings : ElementSettings
    {
        /// <summary>The length of each lane (absent = one lane of 10); each one positive.</summary>
        [Setting("Lane lengths", Units = "m", Min = 0)] public double[] LaneLengths;
        /// <summary>The room between two items.</summary>
        [Setting("Gap", Units = "m", Min = 0)] public double Gap = 0.2;
        /// <summary>The length of an item without the length label.</summary>
        [Setting("Default item length", Units = "m", Min = 0)] public double DefaultItemLength = 1.0;
        /// <summary>The conveying speed (0 = no transit time; above 0 the lanes are conveyors).</summary>
        [Setting("Conveying speed (0 = no transit time)", Units = "m/time", Min = 0)] public double Speed;
        /// <summary>The item label holding its length.</summary>
        [Setting(Role = R.Structural)] public string LengthLabel = "length";
        /// <summary>The most items held (0 = no limit).</summary>
        [Setting("Max items (0 = no limit)", Min = 0)] public int MaxItems;
        /// <summary>An item longer than a lane still enters it when the lane is empty.</summary>
        public bool AcceptOversizeOnEmptyLane = true;
    }

    /// <summary>The settings of a stacker (<see cref="Stacker"/>): it builds stacks (pallets, wagons…) at its positions and
    /// releases each one as a single item.</summary>
    [Serializable, SettingGroupsOf(SettingGroups.Flow | SettingGroups.Process | SettingGroups.Setup)]
    public sealed class StackerSettings : ElementSettings
    {
        /// <summary>The stacking positions.</summary>
        [Setting("Positions", Min = 1)] public int PositionCount = 2;
        /// <summary>The items per stack.</summary>
        [Setting("Items per stack", Min = 1)] public int Capacity = 10;
        /// <summary>The items per stack of each position (0 or absent = <see cref="Capacity"/>).</summary>
        [Setting("Items per stack, per position", Min = 1)] public int[] PositionCapacities;
        /// <summary>The highest stack (0 = no limit) and the height of an item without the height label.</summary>
        [Setting("Max stack height (0 = none)", Units = "m", Min = 0)] public double MaxHeight;
        /// <summary>The height of an item without a height label (m).</summary>
        [Setting(Units = "m", Min = 0)] public double DefaultItemHeight;
        /// <summary>How the position of an arriving item is chosen.</summary>
        [Setting(Logic = L.Placement, LogicDefault = "FirstNotFull")] public StackSelectionRule SelectionRule = StackSelectionRule.FirstNotFull;
        /// <summary>The item labels of its height, its position (written) and its key (ByKey); the stack's item type.</summary>
        [Setting(Role = R.Structural)] public string HeightLabel = "height", PositionLabel = Stacker.LabelPosition, KeyLabel = "", StackType = "Stack";
        /// <summary>An item higher than the limit still enters an empty position.</summary>
        public bool AcceptOversizeOnEmpty = true;
        /// <summary>A part stack leaves when the stacker is blocked.</summary>
        [Setting(Logic = L.Order)] public bool ReleasePartialWhenBlocked = true;
        /// <summary>The stack takes the labels of its first item.</summary>
        public bool CopyLabelsFromFirst = true;
        /// <summary>The time to place an item (absent = instantaneous).</summary>
        [Setting("Cycle time")] public SamplerSettings CycleTime;
        /// <summary>Crane mode: the network node where items are picked and one per position (absent = cycle by time).</summary>
        [Setting(Role = R.Structural)] public string PickNode;
        /// <summary>The network node of each position (for the carriers).</summary>
        [Setting(Role = R.Structural)] public List<string> PositionNodes;
    }

    /// <summary>The settings of a multi-server (<see cref="MultiServer"/>): its servers, service time and offer order.</summary>
    [Serializable, SettingGroupsOf(SettingGroups.Flow | SettingGroups.Process | SettingGroups.Setup | SettingGroups.ReserveExit)]
    public sealed class MultiServerSettings : ElementSettings
    {
        /// <summary>Servers; default 1.</summary>
        [Setting("Servers", Min = 1)] public int Capacity = 1;
        /// <summary>Service time.</summary>
        [Setting("Service time")] public SamplerSettings DelayStrategy;
        /// <summary>Offer order (<see cref="OfferOrder"/>); default FIFO.</summary>
        [Setting(Logic = L.Order)] public OfferOrder OfferOrder = OfferOrder.FIFO;
        /// <summary>Items looked at.</summary>
        [Setting("Items looked at", Min = 0, Logic = L.Order, LogicWhen = "offerOrder=AnyAccepted")] public int OfferScan;

        /// <summary>A job that gives up may send its item to the timeout target: Reroute is the Multi-Server's only.</summary>
        protected internal override void Refine(ISettingsSchema schema)
            => schema.Choice<TimeoutAction>("processBy.onTimeout");
    }

    /// <summary>The settings of a delay: every item waits its service time, with no capacity limit.</summary>
    [Serializable]
    public sealed class DelaySettings : ElementSettings
    {
        /// <summary>Service time.</summary>
        [Setting("Service time")] public SamplerSettings DelayStrategy;
    }

    /// <summary>The settings of an agenda (healthcare): appointments booked into the sessions of its templates.</summary>
    [Serializable]
    public sealed class AgendaSettings : ElementSettings
    {
        /// <summary>The sessions table, the no-show and cancellation probabilities (expressions) and the table of templates.</summary>
        [Setting(Role = R.Structural)] public string Sessions, NoShow, Cancel, TemplateTable;
        /// <summary>Priority label; default "priority".</summary>
        [Setting(Role = R.Structural, Logic = L.Placement, Form = F.Label)] public string PriorityLabel = "priority";
        /// <summary>Overflow (<see cref="SimuLean.Healthcare.AgendaOverflow"/>).</summary>
        [Setting(Logic = L.Placement)] public SimuLean.Healthcare.AgendaOverflow Overflow = SimuLean.Healthcare.AgendaOverflow.Ordinary;
        /// <summary>Slot (minutes); default 20.0.</summary>
        [Setting("Slot (minutes)", Units = "minutes", Min = 0.01)] public double SlotMinutes = 20.0;
        /// <summary>Lead time (days).</summary>
        [Setting("Lead time (days)", Units = "days", Min = 0)] public double LeadDays;
        /// <summary>Reserved slots per session.</summary>
        [Setting("Reserved slots per session", Min = 0)] public int ReservedPerSession;
        /// <summary>Extra slots per session; default 1.</summary>
        [Setting("Extra slots per session", Min = 0)] public int OverbookPerSession = 1;
        /// <summary>Patients arrive at their appointment time (else the source's times are kept).</summary>
        public bool ArriveOnTime;
        /// <summary>The weekly templates of the sessions and the dates without sessions.</summary>
        [Setting(Role = R.Structural)] public List<string> Templates, Holidays;
    }

    /// <summary>The settings of a combiner (<see cref="Combiner"/>): a main item and its components, assembled.</summary>
    [Serializable, SettingGroupsOf(SettingGroups.Flow | SettingGroups.Process | SettingGroups.Setup | SettingGroups.ReserveExit)]
    public sealed class CombinerSettings : ElementSettings
    {
        /// <summary>Components required; default { 2 }.</summary>
        [Setting("Components required", Min = 1, Logic = L.None)] public int[] Requirements = { 2 };
        /// <summary>Components travel with the item.</summary>
        [Setting("Components travel with the item")] public bool BatchMode;
        /// <summary>Requirements from the main item's labels.</summary>
        [Setting("Requirements from the main item's labels", Logic = L.None)] public bool UpdateRequirements;
        /// <summary>Assemblies in parallel; default 1.</summary>
        [Setting("Assemblies in parallel", Min = 1)] public int Capacity = 1;
        /// <summary>Components per layer of the load (0 = by footprint).</summary>
        [Setting("Components per layer of the load (0 = by footprint)", Min = 0)] public int PartsPerLayer;
        /// <summary>The labels of the main item that set the requirements (with requirements from the labels).</summary>
        [Setting(Role = R.Structural, Logic = L.None)] public List<string> UpdateLabels;
        /// <summary>Height label.</summary>
        [Setting(Role = R.Structural)] public string HeightLabel = SimuLean.Storage.StorageSystem.DefaultHeightLabel;
        /// <summary>What a component input accepts: Default / SingleLabel / MultiLabel.</summary>
        [Setting(Role = R.Choice, Choices = new[] { "Default", "SingleLabel", "MultiLabel", "Custom" }, Logic = L.Admission)] public string PullModeType = "Default";
        /// <summary>The single label name and single label value.</summary>
        [Setting(Role = R.Structural, Logic = L.Admission, LogicWhen = "pullModeType=SingleLabel")] public string SingleLabelName = "type", SingleLabelValue = "";
        /// <summary>MultiLabel admission of the components: the label / value records (<c>multiLabelPairs[i]</c>).</summary>
        [Setting(Role = R.Structural, Logic = L.Admission, LogicWhen = "pullModeType=MultiLabel")] public List<LabelValueSettings> MultiLabelPairs;
        /// <summary>The element ids feeding its inputs, in input order.</summary>
        [Setting(Role = R.Structural, Logic = L.None)] public List<string> InputOrder;
        /// <summary>Service time.</summary>
        [Setting("Service time")] public SamplerSettings DelayStrategy;
    }

    /// <summary>The settings of a sink: the items that end here.</summary>
    [Serializable, SettingGroupsOf(SettingGroups.Admission | SettingGroups.Pull)]
    public sealed class SinkSettings : ElementSettings
    {
        /// <summary>Keep the items that end here (for reading them after the run) instead of dropping them.</summary>
        public bool KeepItems;
    }

    /// <summary>The settings of a separator (<see cref="Separator"/>): it unpacks a container's sub-items or splits an item into
    /// copies.</summary>
    [Serializable]
    public sealed class SeparatorSettings : ElementSettings
    {
        /// <summary>Unpack (the sub-items leave, the container through port <c>empties</c>) or Split (copies).</summary>
        [Setting(Logic = L.None)] public SeparatorMode Mode = SeparatorMode.Unpack;
        /// <summary>The order the sub-items leave in (Fifo = as they were put in).</summary>
        [Setting(Logic = L.Order, LogicDefault = "Fifo")] public UnpackOrder UnpackOrder = UnpackOrder.Fifo;
        /// <summary>The items it holds at once.</summary>
        [Setting("Items held", Min = 1)] public int Capacity = 1;
        /// <summary>The process time and (Split) the number of items out, itself included (absent = no process time / 1).</summary>
        [Setting("Process time")] public SamplerSettings ProcessTime;
        /// <summary>Split quantity.</summary>
        [Setting("Split quantity")] public SamplerSettings SplitQuantity;
    }

    /// <summary>The settings of a line-side unpacker (<see cref="LineSideUnpacker"/>): it holds carriers at the line and hands
    /// their pieces one at a time.</summary>
    [Serializable]
    public sealed class LineSideUnpackerSettings : ElementSettings
    {
        /// <summary>The carriers it holds at once.</summary>
        [Setting("Carriers held", Min = 1)] public int Capacity = 2;
        /// <summary>The order its pieces leave a carrier in.</summary>
        [Setting(Logic = L.Order, LogicDefault = "Fifo")] public UnpackOrder UnpackOrder = UnpackOrder.Fifo;
        /// <summary>The time per piece and to change carrier (absent = no time).</summary>
        [Setting("Time per piece")] public SamplerSettings PieceTime;
        /// <summary>Carrier change time.</summary>
        [Setting("Carrier change time")] public SamplerSettings ChangeTime;
        /// <summary>The change time comes before the first carrier too (default false: the first is already in place).</summary>
        [Setting("Change time before the first carrier too", Logic = L.None)] public bool ChangeTimeFirst;
        /// <summary>The pieces take the carrier's labels.</summary>
        [Setting(Logic = L.None)] public bool CopyCarrierLabels;
        /// <summary>An emptied carrier keeps its place until it leaves.</summary>
        public bool EmptiesUseCapacity = true;
    }

    /// <summary>The settings of a pick face (<see cref="SimuLean.Storage.PickFace"/>): it holds unit loads of one
    /// SKU, hands out their pieces and reorders loads by a kanban rule; its emptied loads leave through port
    /// <c>empties</c>.</summary>
    [Serializable]
    public sealed class PickFaceSettings : ElementSettings
    {
        /// <summary>Its SKU (required) and the item types of its loads and pieces (empty = the SKU).</summary>
        [Setting(Role = R.Structural)] public string Sku, LoadType, PieceType;
        /// <summary>The unit loads it holds (open + reserve).</summary>
        [Setting("Unit loads held (open + reserve)", Min = 1)] public int Positions = 1;
        /// <summary>It orders when the loads held + on order fall to this.</summary>
        [Setting("Reorder point (loads held + on order)", Min = 0, Logic = L.Replenishment)] public int ReorderPoint;
        /// <summary>The loads ordered each time.</summary>
        [Setting("Loads per replenishment", Min = 1, Logic = L.Replenishment)] public int ReorderQuantity = 1;
        /// <summary>The pieces of each of its own loads (the initial fill).</summary>
        [Setting("Pieces per load (initial fill)", Min = 0)] public int PiecesPerLoad = 1;
        /// <summary>It starts with its positions full.</summary>
        public bool StartFull;
        /// <summary>The size of the loads' 3D model (m; absent = no model): given to loads without size labels.</summary>
        [Setting("Load width (m)", Units = "m", Min = 0)] public double? ModelWidth;
        /// <summary>Load height (m).</summary>
        [Setting("Load height (m)", Units = "m", Min = 0)] public double? ModelHeight;
        /// <summary>Load length (m).</summary>
        [Setting("Load length (m)", Units = "m", Min = 0)] public double? ModelLength;
        /// <summary>The height of the load floor of its items' model (m).</summary>
        [Setting(Units = "m", Min = 0)] public double? ModelLoadFloor;
        /// <summary>The size of the pieces' 3D model (m; absent = no model).</summary>
        [Setting("Piece width (m)", Units = "m", Min = 0)] public double? PieceModelWidth;
        /// <summary>Piece height (m).</summary>
        [Setting("Piece height (m)", Units = "m", Min = 0)] public double? PieceModelHeight;
        /// <summary>Piece length (m).</summary>
        [Setting("Piece length (m)", Units = "m", Min = 0)] public double? PieceModelLength;
        /// <summary>The height of the load floor of the pieces' model (m).</summary>
        [Setting(Units = "m", Min = 0)] public double? PieceModelLoadFloor;
    }

    /// <summary>The settings of a base-stock replenisher (<see cref="SimuLean.Storage.BaseStockReplenisher"/>): it
    /// orders truckloads of unit loads to keep each SKU's base stock.</summary>
    [Serializable]
    public sealed class ReplenisherSettings : ElementSettings
    {
        /// <summary>The SKUs it replenishes (<c>skus[i]</c>).</summary>
        public List<ReplenisherSkuSettings> Skus = new List<ReplenisherSkuSettings>();
        /// <summary>The storages whose retrievals count as consumption (element names; empty = those it delivers to).</summary>
        [Setting(Role = R.Structural)] public List<string> Observe;
        /// <summary>The unit loads per truck.</summary>
        [Setting("Unit loads per truck", Min = 1)] public int TruckloadSize = 1;
        /// <summary>The time from an order to its truck (absent = 0).</summary>
        [Setting("Lead time")] public SamplerSettings LeadTime;
        /// <summary>When trucks can be unloaded (a weekly shift pattern; absent = always), where the initial stock goes (an
        /// element name; absent = its first destination), and the item types of its loads and pieces (empty = the SKU).</summary>
        [Setting(Role = R.Structural)] public string UnloadingWindows, InitialStockTarget, LoadType, PieceType;
        /// <summary>The size of the loads' 3D model (m; absent = no model).</summary>
        [Setting("Load width (m)", Units = "m", Min = 0)] public double? ModelWidth;
        /// <summary>Load height (m).</summary>
        [Setting("Load height (m)", Units = "m", Min = 0)] public double? ModelHeight;
        /// <summary>Load length (m).</summary>
        [Setting("Load length (m)", Units = "m", Min = 0)] public double? ModelLength;
        /// <summary>The height of the load floor of its items' model (m).</summary>
        [Setting(Units = "m", Min = 0)] public double? ModelLoadFloor;
    }

    /// <summary>A SKU of a replenisher (<c>skus[i]</c>).</summary>
    [Serializable]
    public sealed class ReplenisherSkuSettings : SettingsGroup
    {
        /// <summary>The SKU.</summary>
        [Setting(Role = R.Structural)] public string Sku;
        /// <summary>The pieces of each of its unit loads.</summary>
        [Setting("Pieces per load", Min = 0)] public int PiecesPerLoad = 1;
        /// <summary>The loads in stock at the start.</summary>
        [Setting("Initial loads", Min = 0)] public int InitialLoads;
    }

    /// <summary>The settings of a milk run (<see cref="SimuLean.Resources.MilkRun"/>): a train of its Transport team
    /// (<c>transportBy</c>) carrying items along flows from origins to stops, on fixed tours (tour types, a pattern, a
    /// departure rule) or dynamic ones (requests served by a policy and an order). Element references are config ids or
    /// element names.</summary>
    [Serializable, SettingGroupsOf(SettingGroups.Transport)]
    public sealed class MilkRunSettings : ElementSettings
    {
        /// <summary>The depot's name and network node.</summary>
        [Setting(Role = R.Structural)] public string DepotName = "Depot", DepotNode;
        /// <summary>Fixed tours: the tour types in order (<c>A, B, B</c>; empty = every type once) and how it is followed.</summary>
        [Setting(Role = R.Structural, Logic = L.Dispatch, LogicWhen = "tourMode=Fixed")] public List<string> Pattern;
        /// <summary>The item label naming a carrier's type; where empties go.</summary>
        [Setting(Role = R.Structural)] public string TypeLabel, EmptiesDestination;
        /// <summary>Pattern mode (<see cref="SimuLean.Resources.MilkRunPatternMode"/>).</summary>
        [Setting(Logic = L.Dispatch, LogicWhen = "tourMode=Fixed")] public SimuLean.Resources.MilkRunPatternMode PatternMode = SimuLean.Resources.MilkRunPatternMode.Cyclic;
        /// <summary>When a tour leaves (Ready / Schedule / Timetable) and what makes it ready.</summary>
        [Setting(Logic = L.Dispatch)] public SimuLean.Resources.MilkRunDeparture Departure = SimuLean.Resources.MilkRunDeparture.Ready;
        /// <summary>Ready rule (<see cref="SimuLean.Resources.MilkRunReadyRule"/>); default Any.</summary>
        [Setting(Logic = L.Dispatch, LogicWhen = "tourMode=Fixed;departure!=Schedule")] public SimuLean.Resources.MilkRunReadyRule ReadyRule = SimuLean.Resources.MilkRunReadyRule.Any;
        /// <summary>Fixed or dynamic tours; a dynamic one's policy, assignment of requests and access to an origin's items.</summary>
        [Setting(Logic = L.Dispatch)] public SimuLean.Resources.MilkRunTourMode TourMode = SimuLean.Resources.MilkRunTourMode.Fixed;
        /// <summary>Policy (<see cref="SimuLean.Resources.MilkRunDynamicPolicy"/>).</summary>
        [Setting(Logic = L.Dispatch, LogicWhen = "tourMode=Dynamic")] public SimuLean.Resources.MilkRunDynamicPolicy Policy = SimuLean.Resources.MilkRunDynamicPolicy.Opportunistic;
        /// <summary>Assignment (<see cref="SimuLean.Resources.MilkRunAssignment"/>).</summary>
        [Setting(Logic = L.Dispatch, LogicWhen = "tourMode=Dynamic")] public SimuLean.Resources.MilkRunAssignment Assignment = SimuLean.Resources.MilkRunAssignment.AtStop;
        /// <summary>Origin access (<see cref="SimuLean.Resources.MilkRunOriginAccess"/>).</summary>
        [Setting(Logic = L.Dispatch, LogicWhen = "tourMode=Dynamic")] public SimuLean.Resources.MilkRunOriginAccess OriginAccess = SimuLean.Resources.MilkRunOriginAccess.Head;
        /// <summary>The order requests are taken in and the order they are delivered in (absent = the request order): the
        /// name of one of <see cref="NamedOrders"/>, or a list query text (<c>|distance</c>).</summary>
        [Setting(Role = R.ChoiceFrom, OptionsFrom = "namedOrders", Logic = L.Dispatch, Form = F.Query, LogicWhen = "tourMode=Dynamic")] public string RequestOrder, DeliveryOrder;
        /// <summary>Departure times of a timetable (in its cycle).</summary>
        [Setting(Units = "time", Min = 0)] public double[] Timetable;
        /// <summary>The time to load / unload an empty carrier (absent = the team's).</summary>
        [Setting("Empties load time")] public SamplerSettings EmptiesLoadTime;
        /// <summary>Empties unload time.</summary>
        [Setting("Empties unload time")] public SamplerSettings EmptiesUnloadTime;
        /// <summary>The longest wait for a ready tour (ReadyRule All).</summary>
        [Setting("Max wait for a ready tour", Units = "time", Min = 0, Logic = L.Dispatch, LogicWhen = "tourMode=Fixed")] public double ReadyMaxWait;
        /// <summary>Departure interval (time).</summary>
        [Setting("Departure interval", Units = "time", Min = 0)] public double DepartureInterval;
        /// <summary>First departure (time).</summary>
        [Setting("First departure", Units = "time", Min = 0)] public double FirstDeparture;
        /// <summary>Timetable cycle (time).</summary>
        [Setting("Timetable cycle", Units = "time", Min = 0)] public double TimetableCycle;
        /// <summary>Load time-out (dynamic).</summary>
        [Setting("Load time-out (dynamic)", Units = "time", Min = 0, Logic = L.Dispatch, LogicWhen = "tourMode=Dynamic")] public double LoadTimeout;
        /// <summary>Fill time-out (fill then deliver; 0 = deliver at once, < 0 = until full).</summary>
        [Setting("Fill time-out (fill then deliver; 0 = deliver at once, < 0 = until full)", Units = "time", Logic = L.Dispatch, LogicWhen = "tourMode=Dynamic;policy=FillThenDeliver")] public double FillTimeout;
        /// <summary>The carriers a train takes (0 = no limit).</summary>
        [Setting("Train capacity (carriers, 0 = no limit)", Min = 0)] public int Capacity;
        /// <summary>Trains; default 1.</summary>
        [Setting("Trains", Min = 1)] public int Trains = 1;
        /// <summary>A leg's time also holds the other way.</summary>
        [Setting("Legs both ways")] public bool SymmetricLegs;
        /// <summary>Its stops (<c>stops[i]</c>), flows, the departure rule of some flows, tour types, capacities per carrier
        /// type, leg times, the origins served at each stop (dynamic), the named orders and the requests' calculated fields.</summary>
        public List<MilkRunStopSettings> Stops;
        /// <summary>Its flows (<c>flows[i]</c>): what goes from where to which stop.</summary>
        public List<MilkRunFlowSettings> Flows;
        /// <summary>When a flow is ready for a tour.</summary>
        public List<MilkRunReadinessSettings> FlowReadiness;
        /// <summary>The kinds of tour (flows and stops).</summary>
        public List<MilkRunTourTypeSettings> TourTypes;
        /// <summary>Carriers per train by item type.</summary>
        public List<MilkRunTypeCapacitySettings> CapacityByType;
        /// <summary>The time of each leg between two stops.</summary>
        public List<MilkRunLegSettings> Legs;
        /// <summary>Where the train picks up at each stop.</summary>
        public List<MilkRunPickupSettings> Pickups;
        /// <summary>Named request orders a dynamic milk run can choose from.</summary>
        [Setting(Logic = L.Dispatch, LogicWhen = "tourMode=Dynamic")] public List<NamedOrderSettings> NamedOrders;
        /// <summary>Fields computed per request (for orders and filters).</summary>
        [Setting(Logic = L.Dispatch, LogicWhen = "tourMode=Dynamic")] public List<MilkRunRequestFieldSettings> RequestFields;
    }

    /// <summary>A stop of a milk run (<c>stops[i]</c>).</summary>
    [Serializable]
    public sealed class MilkRunStopSettings : SettingsGroup
    {
        /// <summary>Its name, its network node (absent = where its destinations stand), where its empties come from and go
        /// (absent = the milk run's) and which empties it collects (an expression over the item and the origin; absent = all).</summary>
        [Setting(Role = R.Structural)] public string Name, Location;
        /// <summary>The elements whose empties the train takes back from this stop.</summary>
        [Setting(Role = R.Structural)] public List<string> EmptiesOrigins;
        /// <summary>The empties destination and empties filter.</summary>
        [Setting(Role = R.Structural, Logic = L.Routing)] public string EmptiesDestination, EmptiesFilter;

        /// <summary>A record of the given values.</summary>
        public static MilkRunStopSettings Of(string name, string location, IEnumerable<string> emptiesOrigins, string emptiesDestination = "", string emptiesFilter = "")
            => new MilkRunStopSettings
            {
                Name = name, Location = Blank(location), EmptiesOrigins = emptiesOrigins != null ? new List<string>(emptiesOrigins) : null,
                EmptiesDestination = Blank(emptiesDestination), EmptiesFilter = Blank(emptiesFilter),
            };

        internal static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>A flow of a milk run (<c>flows[i]</c>; it was a record <c>name|origin|stop|destination|max|load|unload|filter</c>).</summary>
    [Serializable]
    public sealed class MilkRunFlowSettings : SettingsGroup
    {
        /// <summary>Its name, origin, stop and destination.</summary>
        [Setting(Role = R.Structural)] public string Name, Origin, Stop, Destination;
        /// <summary>The most carriers per tour (0 = no limit).</summary>
        [Setting("Carriers per tour (0 = no limit)", Min = 0)] public int MaxPerTour;
        /// <summary>The time to load / unload one of its carriers (absent = the team's).</summary>
        [Setting("Flow load time")] public SamplerSettings LoadTime;
        /// <summary>Flow unload time.</summary>
        [Setting("Flow unload time")] public SamplerSettings UnloadTime;
        /// <summary>The items it carries (an expression over the item and the origin; absent = all).</summary>
        [Setting(Role = R.Structural, Logic = L.Routing, Form = F.Expression, Context = "milkRunFilter")] public string Filter;

        /// <summary>A record of the given values.</summary>
        public static MilkRunFlowSettings Of(string name, string origin, string stop, string destination, int maxPerTour = 0,
                                             SamplerSettings loadTime = null, SamplerSettings unloadTime = null, string filter = "")
            => new MilkRunFlowSettings
            {
                Name = name, Origin = origin, Stop = stop, Destination = destination, MaxPerTour = maxPerTour,
                LoadTime = loadTime, UnloadTime = unloadTime, Filter = MilkRunStopSettings.Blank(filter),
            };
    }

    /// <summary>The departure rule of a flow on fixed tours (<c>flowReadiness[i]</c>): a full load of <c>minPerTour</c>
    /// carriers; an optional flow neither makes a tour ready nor holds it back.</summary>
    [Serializable]
    public sealed class MilkRunReadinessSettings : SettingsGroup
    {
        /// <summary>The flow it is about (by name).</summary>
        [Setting(Role = R.Structural)] public string Flow;
        /// <summary>Carriers for a ready flow.</summary>
        [Setting("Carriers for a ready flow", Min = 0)] public int MinPerTour;
        /// <summary>Not needed for the tour to be ready.</summary>
        public bool Optional;

        /// <summary>A record of the given values.</summary>
        public static MilkRunReadinessSettings Of(string flow, int minPerTour, bool optional)
            => new MilkRunReadinessSettings { Flow = flow, MinPerTour = minPerTour, Optional = optional };
    }

    /// <summary>A tour type (<c>tourTypes[i]</c>): its flows (empty = all) and stops (empty = the stops of its flows).</summary>
    [Serializable]
    public sealed class MilkRunTourTypeSettings : SettingsGroup
    {
        /// <summary>Its name (unique in its list).</summary>
        [Setting(Role = R.Structural)] public string Name;
        /// <summary>The flows the tour serves and its stops (by name).</summary>
        [Setting(Role = R.Structural)] public List<string> Flows, Stops;

        /// <summary>A record of the given values.</summary>
        public static MilkRunTourTypeSettings Of(string name, IEnumerable<string> flows, IEnumerable<string> stops = null)
            => new MilkRunTourTypeSettings
            {
                Name = name, Flows = flows != null ? new List<string>(flows) : null, Stops = stops != null ? new List<string>(stops) : null,
            };
    }

    /// <summary>The carriers of one type a train takes (<c>capacityByType[i]</c>).</summary>
    [Serializable]
    public sealed class MilkRunTypeCapacitySettings : SettingsGroup
    {
        /// <summary>The item type.</summary>
        [Setting(Role = R.Structural)] public string Type;
        /// <summary>Carriers of the type per train.</summary>
        [Setting("Carriers of the type per train", Min = 0)] public int Capacity;
    }

    /// <summary>A leg's time (<c>legs[i]</c>): from, to and its time (required; a constant one is also the leg's distance for
    /// the nearest-stop rules of an abstract train).</summary>
    [Serializable]
    public sealed class MilkRunLegSettings : SettingsGroup
    {
        /// <summary>The stops it joins.</summary>
        [Setting(Role = R.Structural)] public string From, To;
        /// <summary>Leg time.</summary>
        [Setting("Leg time")] public SamplerSettings Time;

        /// <summary>A record of the given values.</summary>
        public static MilkRunLegSettings Of(string from, string to, SamplerSettings time) => new MilkRunLegSettings { From = from, To = to, Time = time };
    }

    /// <summary>The origins served at a stop on dynamic tours (<c>pickups[i]</c>; the depot may be a stop).</summary>
    [Serializable]
    public sealed class MilkRunPickupSettings : SettingsGroup
    {
        /// <summary>The stop where it picks up.</summary>
        [Setting(Role = R.Structural)] public string Stop;
        /// <summary>The elements it picks up from.</summary>
        [Setting(Role = R.Structural)] public List<string> Origins;

        /// <summary>A record of the given values.</summary>
        public static MilkRunPickupSettings Of(string stop, params string[] origins)
            => new MilkRunPickupSettings { Stop = stop, Origins = new List<string>(origins ?? new string[0]) };
    }

    /// <summary>A named order (<c>namedOrders[i]</c>) of a milk run or a resource team: its <c>requestOrder</c> (a milk run's
    /// <c>deliveryOrder</c> too) may name it, which makes the order a choice for the optimiser / Experimenter.</summary>
    [Serializable]
    public sealed class NamedOrderSettings : SettingsGroup
    {
        /// <summary>The order's name and its query (a list query / order over the requests: <c>|priority desc</c>).</summary>
        [Setting(Role = R.Structural, Logic = L.Dispatch)] public string Name, Query;

        /// <summary>A record of the given values.</summary>
        public static NamedOrderSettings Of(string name, string query) => new NamedOrderSettings { Name = name, Query = query ?? "" };
    }

    /// <summary>A calculated field of the requests (<c>requestFields[i]</c>): a formula over their fields, usable in the orders
    /// (e.g. <c>urgent</c> = <c>originFill &gt;= 0.8</c>, then <c>|urgent desc, distance</c>).</summary>
    [Serializable]
    public sealed class MilkRunRequestFieldSettings : SettingsGroup
    {
        /// <summary>The field's name and the expression that computes it per request.</summary>
        [Setting(Role = R.Structural, Logic = L.Dispatch)] public string Name, Formula;

        /// <summary>A record of the given values.</summary>
        public static MilkRunRequestFieldSettings Of(string name, string formula) => new MilkRunRequestFieldSettings { Name = name, Formula = formula };
    }

    /// <summary>The settings of a call-off point (<see cref="CallOffPoint"/>): each passing vehicle gets its sequence number
    /// and the orders of every family are sent through the family's port.</summary>
    [Serializable]
    public sealed class CallOffPointSettings : ElementSettings
    {
        /// <summary>What is called off for each vehicle, one record per family (<c>families[i]</c>).</summary>
        public List<CallOffFamilySettings> Families;
        /// <summary>The label with the sequence number given to vehicles without one (empty = none).</summary>
        [Setting(Role = R.Structural)] public string SequenceLabel = "vehicle";
        /// <summary>Renumber vehicles that already carry the sequence label.</summary>
        public bool OverwriteSequence;
    }

    /// <summary>A call-off family (<c>families[i]</c> of a call-off point, <c>stations[i].family</c> of a paced line): what is
    /// ordered or fitted per vehicle.</summary>
    [Serializable]
    public sealed class CallOffFamilySettings : SettingsGroup
    {
        /// <summary>Its name, also its port (empty = the default: <c>Family&lt;n&gt;</c>, a line station's name).</summary>
        [Setting(Role = R.Structural)] public string Name;
        /// <summary>The type of its orders.</summary>
        [Setting(Role = R.Structural, Logic = L.None)] public string OrderType = "CallOff";
        /// <summary>Pieces per vehicle (positions 1..quantity, one order each).</summary>
        [Setting("Pieces per vehicle", Min = 1)] public int Quantity = 1;
        /// <summary>Only vehicles meeting this expression need the family (empty = all).</summary>
        [Setting(Role = R.Structural, Logic = L.Admission, Form = F.Expression, Context = "callOff")] public string Condition;
        /// <summary>The label of the drawn reference on each order.</summary>
        [Setting(Role = R.Structural)] public string ReferenceLabel = "reference";
        /// <summary>Reference choice tables, one per position or one for all (none = no reference): <c>referenceTables[i]</c>.</summary>
        public List<ReferenceTableSettings> ReferenceTables;
    }

    /// <summary>A reference choice table of a call-off family (<c>referenceTables[i]</c>):
    /// the references and their weights (<c>choices[j]</c>).</summary>
    [Serializable]
    public sealed class ReferenceTableSettings : SettingsGroup
    {
        /// <summary>The references and their weights.</summary>
        public List<ReferenceChoiceSettings> Choices = new List<ReferenceChoiceSettings>();

        /// <summary>A table written as text <c>R1:0.3|R2:0.7</c> (a weight left out = 1; the inspector's form).</summary>
        public static ReferenceTableSettings Parse(string table)
        {
            var t = new ReferenceTableSettings();
            foreach (var (value, weight) in SimuLean.Stochastics.Distributions.DiscreteChoice.ParseEntries(table))
                t.Choices.Add(new ReferenceChoiceSettings { Reference = value, Weight = weight });
            return t;
        }
    }

    /// <summary>A reference of a choice table (<c>choices[j]</c>) and its weight (relative; ≥ 0).</summary>
    [Serializable]
    public sealed class ReferenceChoiceSettings : SettingsGroup
    {
        /// <summary>The reference drawn.</summary>
        [Setting(Role = R.Structural)] public string Reference;
        /// <summary>Its weight (the weights need not add up to 1).</summary>
        [Setting("Reference weight", Min = 0)] public double Weight = 1;
    }

    /// <summary>The settings of a paced line (<see cref="PacedLine"/>): an indexing line of positions with stations fed at
    /// line side.</summary>
    [Serializable]
    public sealed class PacedLineSettings : ElementSettings
    {
        /// <summary>The line's positions (pitches).</summary>
        [Setting("Line positions", Min = 2)] public int Positions = 5;
        /// <summary>The time between indexes (required).</summary>
        [Setting("Takt")] public SamplerSettings Takt = SamplerSettings.Constant(1);
        /// <summary>The label with the entry number given to vehicles without one (empty = none).</summary>
        [Setting(Role = R.Structural)] public string SequenceLabel = "vehicle";
        /// <summary>Fitted parts become sub-items of the vehicle (off = consumed and dropped).</summary>
        public bool ComponentsToVehicle = true;
        /// <summary>Its stations (<c>stations[i]</c>).</summary>
        public List<LineStationSettings> Stations;
    }

    /// <summary>A station of a paced line (<c>stations[i]</c>).</summary>
    [Serializable]
    public sealed class LineStationSettings : SettingsGroup
    {
        /// <summary>Its name, also its input port (empty = <c>Station&lt;n&gt;</c>).</summary>
        [Setting(Role = R.Structural)] public string Name;
        /// <summary>The line position it works at.</summary>
        [Setting("Station position", Min = 0)] public int Position = 1;
        /// <summary>The label a part must share with the vehicle (empty = any part).</summary>
        [Setting(Role = R.Structural, Logic = L.Admission, Form = F.Label)] public string MatchLabel;
        /// <summary>The parts waiting at line side.</summary>
        [Setting("Parts at line side", Min = 1)] public int PortCapacity = 2;
        /// <summary>The line position where the line calls off its family (-1 = no call-off at the line).</summary>
        [Setting(Role = R.Structural)] public int CallOffPosition = -1;
        /// <summary>What is fitted / called off (absent = one piece; an empty name = the station's).</summary>
        public CallOffFamilySettings Family;
    }

    /// <summary>The settings of a sequencing station (<see cref="SequencingStation"/>): it fills carriers with the pieces of
    /// its orders in sequence (orders by port <c>orders</c>, pieces by port <c>pieces</c>, served orders out by port
    /// <c>orders</c>).</summary>
    [Serializable, SettingGroupsOf(SettingGroups.Flow | SettingGroups.Process)]
    public sealed class SequencingStationSettings : ElementSettings
    {
        /// <summary>The slots of a carrier (a carrier's own <see cref="CapacityLabel"/> wins).</summary>
        [Setting("Slots per carrier", Min = 1)] public int CarrierCapacity = 9;
        /// <summary>The orders of a lot (0 = the carrier capacity).</summary>
        [Setting("Orders per lot (0 = carrier capacity)", Min = 0)] public int LotSize;
        /// <summary>The orders waiting (-1 = unbounded).</summary>
        [Setting("Order buffer capacity (-1 = unbounded)", Logic = L.None)] public int OrderCapacity = -1;
        /// <summary>The pieces in stock (0 = on demand, -1 = an unbounded buffer).</summary>
        [Setting("Piece stock (0 = on demand, -1 = unbounded)")] public int PieceStock;
        /// <summary>A partial carrier starts when its oldest order has waited this long (0 = off).</summary>
        [Setting("Max wait of the oldest order (0 = off)", Units = "time", Min = 0, Logic = L.Order)] public double OldestOrderMaxWait;
        /// <summary>A carrier waits until it is full.</summary>
        public bool WaitForFullCarrier = true;
        /// <summary>The pieces take their order's labels.</summary>
        [Setting(Logic = L.None)] public bool CopyOrderLabels = true;
        /// <summary>The labels of a carrier's capacity, an order's reference, the piece field the reference matches, the
        /// order's call-off time and its vehicle.</summary>
        [Setting(Role = R.Structural, Logic = L.None)] public string CapacityLabel = "capacity", ReferenceLabel = "reference", PieceField = "sku",
                                                     OrderTimeLabel = "callOffTime", VehicleLabel = "vehicle";
        /// <summary>The orders it serves (an expression over the order; absent = those with a reference).</summary>
        [Setting(Role = R.Structural, Logic = L.Admission, Form = F.Expression, Context = "sequencing")] public string Applies;
        /// <summary>What its operator is taken for: a whole carrier or each piece.</summary>
        [Setting(Logic = L.Resources)] public SequencingOperatorScope OperatorScope = SequencingOperatorScope.Carrier;
        /// <summary>The times to prepare a carrier, pick a piece, the extra time, place it, between pieces and to close the
        /// carrier (absent = 0).</summary>
        [Setting("Prepare time")] public SamplerSettings PrepareTime;
        /// <summary>Pick time.</summary>
        [Setting("Pick time")] public SamplerSettings PickTime;
        /// <summary>Extra time.</summary>
        [Setting("Extra time")] public SamplerSettings ExtraTime;
        /// <summary>Place time.</summary>
        [Setting("Place time")] public SamplerSettings PlaceTime;
        /// <summary>Gap time.</summary>
        [Setting("Gap time")] public SamplerSettings GapTime;
        /// <summary>Close time.</summary>
        [Setting("Close time")] public SamplerSettings CloseTime;
    }

    /// <summary>The settings of a storage (<see cref="SimuLean.Storage.StorageSystem"/>): lanes of positions, slotting
    /// policies, physical dimensions, put-away by its transport team, pass-through release.</summary>
    [Serializable]
    public sealed class StorageSystemSettings : ElementSettings
    {
        /// <summary>Its lanes (<c>lanes[i]</c>).</summary>
        public List<StorageLaneSettings> Lanes;
        /// <summary>Its reference point (distances of ClosestSpot).</summary>
        [Setting(Role = R.Structural)] public double RefX, RefY, RefZ;
        /// <summary>Loads are checked against each lane's slot room by their size labels.</summary>
        public bool PhysicalDimensions;
        /// <summary>A load waits at the storage's location until its Transport team puts it away.</summary>
        public bool PutAwayByTransport;
        /// <summary>Pass-through: loads are released to the output after <see cref="MinimumDwellTime"/>.</summary>
        [Setting(Logic = L.Order)] public bool ReleaseAfterDwell;
        /// <summary>The item labels of a load's length, width and height.</summary>
        [Setting(Role = R.Structural)] public string LengthLabel = SimuLean.Storage.StorageSystem.DefaultLengthLabel,
                                                      WidthLabel = SimuLean.Storage.StorageSystem.DefaultWidthLabel,
                                                      HeightLabel = SimuLean.Storage.StorageSystem.DefaultHeightLabel;
        /// <summary>The policy that places a load and the one that relocates it (absent: none / the location policy).</summary>
        [Setting(Role = R.Choice, Choices = new[] { "FirstAvailable", "ClosestSpot", "Random", "DedicatedBySku" }, Logic = L.Placement, LogicDefaultValue = "FirstAvailable")] public string LocationPolicy, RelocationPolicy;
        /// <summary>The loads waiting at the inbound point (put-away by transport).</summary>
        [Setting("Loads waiting at the inbound point", Min = 1)] public int InboundCapacity = 1;
        /// <summary>The minimum time a load stays before it is released (pass-through).</summary>
        [Setting("Minimum dwell time before a load is released (pass-through)", Units = "time", Min = 0)] public double MinimumDwellTime;
        /// <summary>DedicatedBySku: the lane of each SKU (<c>dedicatedSkuMappings[i]</c>).</summary>
        [Setting(Logic = L.Placement)] public List<SkuLaneSettings> DedicatedSkuMappings;
    }

    /// <summary>A SKU's dedicated lane (<c>dedicatedSkuMappings[i]</c>).</summary>
    [Serializable]
    public sealed class SkuLaneSettings : SettingsGroup
    {
        /// <summary>The SKU and the id of its lane.</summary>
        [Setting(Role = R.Structural)] public string Sku, Lane;

        /// <summary>A record of the given values.</summary>
        public static SkuLaneSettings Of(string sku, string lane) => new SkuLaneSettings { Sku = sku, Lane = lane };
    }

    /// <summary>A lane of a storage (<c>lanes[i]</c>).</summary>
    [Serializable]
    public sealed class StorageLaneSettings : SettingsGroup
    {
        /// <summary>Its id.</summary>
        [Setting(Role = R.Structural)] public string Id;
        /// <summary>Its position (m).</summary>
        [Setting(Role = R.Structural)] public double X, Y, Z;
        /// <summary>The loads it holds.</summary>
        [Setting("Lane capacity", Min = 1)] public int Capacity = 1;
        /// <summary>Which load can be taken out (Direct = any; Fifo / Lifo = a lane's first / last in).</summary>
        [Setting(Logic = L.Placement)] public SimuLean.Storage.AccessDiscipline Access = SimuLean.Storage.AccessDiscipline.Direct;
        /// <summary>Whether a lane mixes SKUs.</summary>
        [Setting(Logic = L.Placement)] public SimuLean.Storage.SkuConstraint Sku = SimuLean.Storage.SkuConstraint.Mixed;
        /// <summary>Its network node (absent = none).</summary>
        [Setting(Role = R.Structural)] public string Node;
        /// <summary>The clear room of one position, across, high and along the lane (0 = no limit; physical dimensions).</summary>
        [Setting("Slot width", Units = "m", Min = 0)] public double SlotWidth;
        /// <summary>Slot height (m).</summary>
        [Setting("Slot height", Units = "m", Min = 0)] public double SlotHeight;
        /// <summary>Slot length (m).</summary>
        [Setting("Slot length", Units = "m", Min = 0)] public double SlotLength;

        /// <summary>A lane (in code).</summary>
        public static StorageLaneSettings Of(string id, double x, double y, double z, int capacity,
                                             SimuLean.Storage.AccessDiscipline access = SimuLean.Storage.AccessDiscipline.Direct,
                                             SimuLean.Storage.SkuConstraint sku = SimuLean.Storage.SkuConstraint.Mixed, string node = null,
                                             double slotWidth = 0, double slotHeight = 0, double slotLength = 0)
            => new StorageLaneSettings
            {
                Id = id, X = x, Y = y, Z = z, Capacity = capacity, Access = access, Sku = sku,
                Node = string.IsNullOrEmpty(node) ? null : node, SlotWidth = slotWidth, SlotHeight = slotHeight, SlotLength = slotLength,
            };
    }

    /// <summary>The settings of a picking station (<see cref="PickingStation"/>): it picks the SKUs of each order from its stock into the order's container.</summary>
    [Serializable, SettingGroupsOf(SettingGroups.Flow | SettingGroups.Process | SettingGroups.Setup | SettingGroups.ReserveExit)]
    public sealed class PickingStationSettings : ElementSettings
    {
        /// <summary>-1 = unbounded.</summary>
        [Setting("Stock buffer capacity", Min = -1)] public int StockCapacity = -1;
        /// <summary>Send what is left in a container back by the returns port; default true.</summary>
        public bool ReturnLeftovers = true;
        /// <summary>Read the pick list from the container's labels; default true.</summary>
        [Setting(Role = R.Structural, Logic = L.None)] public bool UseContainerLabels = true;
        /// <summary>The pick list table and its order-key column.</summary>
        [Setting(Role = R.Structural)] public string PickTable, KeyColumn;
        /// <summary>The sku column, qty column, container key label and label prefix.</summary>
        [Setting(Role = R.Structural)] public string SkuColumn = "sku", QtyColumn = "qty", ContainerKeyLabel = "order_id", LabelPrefix = "pick_";
        /// <summary>The fallback pick list (<c>staticPickList[i]</c>: <c>sku</c>, <c>qty</c>).</summary>
        public List<PickListEntrySettings> StaticPickList;
        /// <summary>Service time.</summary>
        [Setting("Service time")] public SamplerSettings DelayStrategy;
    }

    /// <summary>A label and the value an item must have for it (a MultiLabel admission's <c>multiLabelPairs[i]</c>): a value
    /// that reads as a number (<c>2,5</c> = 2.5) matches a numeric label, else a text label.</summary>
    [Serializable]
    public sealed class LabelValueSettings : SettingsGroup
    {
        /// <summary>The label.</summary>
        [Setting(Role = R.Structural)] public string Label;
        /// <summary>The value (a number or a text).</summary>
        [Setting(Role = R.Structural)] public string Value;

        /// <summary>A record of the given values.</summary>
        public static LabelValueSettings Of(string label, string value) => new LabelValueSettings { Label = label, Value = value };
    }

    /// <summary>A line of a picking station's fallback pick list (<c>staticPickList[i]</c>): a SKU and its quantity (0 = none).</summary>
    [Serializable]
    public sealed class PickListEntrySettings : SettingsGroup
    {
        /// <summary>The SKU.</summary>
        [Setting(Role = R.Structural)] public string Sku;
        /// <summary>Pick quantity; default 1.</summary>
        [Setting("Pick quantity", Min = 0)] public int Qty = 1;

        /// <summary>A record of the given values.</summary>
        public static PickListEntrySettings Of(string sku, int qty) => new PickListEntrySettings { Sku = sku, Qty = qty };
    }

    /// <summary>The settings of an assembly station (<see cref="AssemblyStation"/>): tasks with precedences, materials and workers.</summary>
    [Serializable, SettingGroupsOf(SettingGroups.Flow | SettingGroups.Process | SettingGroups.Setup)]
    public sealed class AssemblyStationSettings : ElementSettings
    {
        /// <summary>The workers that take its tasks.</summary>
        [Setting("Workers", Min = 1)] public int WorkerCount = 1;
        /// <summary>The material buffer (-1 = unbounded).</summary>
        [Setting("Material buffer capacity", Min = -1)] public int StockCapacity = -1;
        /// <summary>The precedence network: one record per task.</summary>
        public List<AssemblyTaskSettings> Tasks;
    }

    /// <summary>One task of an assembly station (<c>tasks[i]</c>).</summary>
    [Serializable]
    public sealed class AssemblyTaskSettings : SettingsGroup
    {
        /// <summary>Its id (unique in the station).</summary>
        [Setting(Role = R.Structural)] public string Id;
        /// <summary>The ids of the tasks it follows.</summary>
        [Setting(Role = R.Structural)] public List<string> Predecessors;
        /// <summary>The materials it consumes.</summary>
        public List<MaterialSettings> Materials;
        /// <summary>Task priority.</summary>
        [Setting("Task priority", Logic = L.Order)] public int Priority;
        /// <summary>The task's time (absent = no time).</summary>
        [Setting("Task duration")] public SamplerSettings Duration;
        /// <summary>Its own resource use and per-task settings): its requirements, priority, preemption, acquisition
        /// order and max wait; what it does not set is the station's process use.</summary>
        public TaskUseSettings ProcessBy;

        /// <summary>A task (in code): its id, duration, priority, the tasks it follows and its materials (<c>sku:qty</c>).</summary>
        public static AssemblyTaskSettings Of(string id, SamplerSettings duration, int priority = 0, IEnumerable<string> predecessors = null,
                                              params string[] materials)
        {
            var t = new AssemblyTaskSettings { Id = id, Duration = duration, Priority = priority };
            if (predecessors != null) t.Predecessors = new List<string>(predecessors);
            foreach (string m in materials ?? new string[0])
            {
                int sep = m.LastIndexOf(':');
                (t.Materials = t.Materials ?? new List<MaterialSettings>()).Add(new MaterialSettings
                {
                    Sku = sep > 0 ? m.Substring(0, sep) : m,
                    Qty = sep > 0 ? int.Parse(m.Substring(sep + 1), System.Globalization.CultureInfo.InvariantCulture) : 1,
                });
            }
            return t;
        }
    }

    /// <summary>A material a task consumes (<c>tasks[i].materials[j]</c>).</summary>
    [Serializable]
    public sealed class MaterialSettings : SettingsGroup
    {
        /// <summary>The SKU.</summary>
        [Setting(Role = R.Structural)] public string Sku;
        /// <summary>Material quantity; default 1.</summary>
        [Setting("Material quantity", Min = 1)] public int Qty = 1;
    }

    /// <summary>The settings of an erection dock (<see cref="SimuLean.Naval.ErectionDock"/>): hull blocks erected in sequence or by precedence.</summary>
    [Serializable, SettingGroupsOf(SettingGroups.Flow | SettingGroups.Process | SettingGroups.Setup)]
    public sealed class ErectionDockSettings : ElementSettings
    {
        /// <summary>Erection fronts; default 1.</summary>
        [Setting("Erection fronts", Min = 1)] public int Capacity = 1;
        /// <summary>Blocks per hull.</summary>
        [Setting("Blocks per hull", Min = 0)] public int ExpectedBlocks;
        /// <summary>Erection rule (<see cref="SimuLean.Naval.ErectionDock.ErectionRule"/>).</summary>
        [Setting(Logic = L.Order, LogicDefault = "Sequence")] public SimuLean.Naval.ErectionDock.ErectionRule ErectionRule = SimuLean.Naval.ErectionDock.ErectionRule.Sequence;
        /// <summary>The block label, sequence label, predecessors label and ship name.</summary>
        [Setting(Role = R.Structural)] public string BlockLabel, SequenceLabel, PredecessorsLabel, ShipName;
        /// <summary>Service time.</summary>
        [Setting("Service time")] public SamplerSettings DelayStrategy;
    }

    // ── downtimes ────────────────────────────────────────────────────────

    /// <summary>
    /// The settings of a downtime (<see cref="DowntimeConfig.Settings"/>): one class
    /// for every kind (MtbfMttr, Shift, Periodic, Timetable) — a kind reads its own, the common ones (state, mode, blocking)
    /// and the repair crew (<c>repairBy</c>). A path its class does not declare fails when written, naming the closest ones.
    /// Null = not given: the generator's default (a failure's times <c>ExponentialMean 3600 / 300</c>, the
    /// mode and state of the generator, no end of a periodic stop).
    /// </summary>
    [Serializable]
    public sealed class DowntimeSettings : SettingsGroup
    {
        /// <summary>Whether it stops its targets at all (a study switches a downtime on / off: <c>downtime:Id.enabled</c>).
        /// Off, its generators are built but never started: no stop, its statistics 0.</summary>
        [Setting("Enabled")] public bool Enabled = true;
        /// <summary>The state an element is in while stopped (absent = the generator's: STOPPED, a shift's off-shift state).</summary>
        [Setting(Role = R.Structural)] public string StateName;
        /// <summary>How a stop takes the work in progress (absent = the generator's).</summary>
        [Setting(Logic = L.None)] public StopMode? Mode;
        /// <summary>Whether a stopped element refuses items (input) and holds its finished ones (output; absent = by the mode).</summary>
        [Setting("Block the input")] public bool? BlockInput;
        /// <summary>Block the output.</summary>
        [Setting("Block the output")] public bool? BlockOutput;

        /// <summary>MtbfMttr: the time to the next failure and to repair it (absent: ExponentialMean 3600 / 300), and the
        /// first failure's time (absent = drawn like the others).</summary>
        [Setting("Time to failure")] public SamplerSettings TimeToFailure;
        /// <summary>Time to repair.</summary>
        [Setting("Time to repair")] public SamplerSettings TimeToRepair;
        /// <summary>First failure.</summary>
        [Setting("First failure")] public SamplerSettings FirstFailure;
        /// <summary>MtbfMttr: the clock the time to failure runs on (absent = the generator's), the states that count as busy
        /// (Busy basis) and the failure's code.</summary>
        [Setting(Logic = L.None)] public SimuLean.Downtime.DowntimeBasis? Basis;
        /// <summary>Busy basis: the states that count as busy.</summary>
        [Setting(Role = R.Structural)] public List<string> BusyStates;
        /// <summary>The code of a failure.</summary>
        [Setting(Role = R.Structural)] public string FailureCode;

        /// <summary>Shift: the weekly pattern (<c>Mon-Fri 06:00-14:00</c>) and the holidays (dates).</summary>
        [Setting(Role = R.Structural, Logic = L.None)] public string ShiftPattern;
        /// <summary>Shift: the dates without shifts.</summary>
        [Setting(Role = R.Structural)] public List<string> Holidays;

        /// <summary>Periodic: the first stop (absent = 0), the period and the stop's duration (required), the time from which no
        /// stop starts (absent = never), whether stops may overlap and their code.</summary>
        [Setting("Periodic: first stop", Units = "time", Min = 0)] public double? PeriodicFirst;
        /// <summary>Periodic: period (time).</summary>
        [Setting("Periodic: period", Units = "time", Min = 0)] public double? PeriodicPeriod;
        /// <summary>Periodic: stop duration (time).</summary>
        [Setting("Periodic: stop duration", Units = "time", Min = 0)] public double? PeriodicDuration;
        /// <summary>Periodic: no stop from (time).</summary>
        [Setting("Periodic: no stop from", Units = "time", Min = 0)] public double? PeriodicUntil;
        /// <summary>Periodic: stops may overlap.</summary>
        [Setting("Periodic: stops may overlap")] public bool AllowOverlap;
        /// <summary>Periodic: the code of a stop.</summary>
        [Setting(Role = R.Structural)] public string PeriodicCode;

        /// <summary>Timetable: what overlapping intervals do (absent = Allow) and the intervals (<c>rows[i]</c>).</summary>
        [Setting(Logic = L.None)] public SimuLean.Downtime.OverlapPolicy? Overlap;
        /// <summary>The intervals of a timetable (<c>rows[i]</c>).</summary>
        public List<DowntimeIntervalSettings> Rows;
        /// <summary>Timetable read from a data table: the table, its columns (absent: start <c>start</c>, duration
        /// <c>duration</c>), the duration's scale, the rules per code and the target column's value of each target.</summary>
        [Setting(Role = R.Structural)] public string DataTable, TargetColumn, StartColumn, EndColumn, DurationColumn, CodeColumn, ReasonColumn;
        /// <summary>Table: duration scale; default 1.0.</summary>
        [Setting("Table: duration scale", Min = 0)] public double DurationScale = 1.0;
        /// <summary>Rules per code of a table's rows.</summary>
        [Setting(Logic = L.None)] public List<DowntimeCodeRuleSettings> CodeRules;
        /// <summary>The target column's value of each target.</summary>
        public List<DowntimeTargetKeySettings> TargetKeys;

        /// <summary>The crew that repairs a stop (absent = none).</summary>
        public RepairCrewSettings RepairBy;
    }

    /// <summary>An interval of a timetable downtime (<c>rows[i]</c>).</summary>
    [Serializable]
    public sealed class DowntimeIntervalSettings : SettingsGroup
    {
        /// <summary>The target's id (absent = every target of the downtime).</summary>
        [Setting(Role = R.Structural)] public string Target;
        /// <summary>The start: a model time or a date (<c>2026-03-02 06:00</c>, read by the model's calendar).</summary>
        [Setting(Role = R.Structural)] public string Start;
        /// <summary>The duration.</summary>
        [Setting(Role = R.Structural)] public double Duration;
        /// <summary>The state and mode (absent = the downtime's), the code and the reason.</summary>
        [Setting(Role = R.Structural)] public string State;
        /// <summary>The stop mode (absent = the downtime's).</summary>
        [Setting(Role = R.Structural, Logic = L.None)] public StopMode? Mode;
        /// <summary>The interval's code and reason.</summary>
        [Setting(Role = R.Structural)] public string Code, Reason;

        /// <summary>An interval of <paramref name="target"/> (empty = every target).</summary>
        public static DowntimeIntervalSettings Of(string target, double start, double duration, string state = null, StopMode? mode = null,
                                                  string code = null, string reason = null)
            => new DowntimeIntervalSettings
            {
                Target = string.IsNullOrEmpty(target) ? null : target, Start = start.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                Duration = duration, State = string.IsNullOrEmpty(state) ? null : state, Mode = mode,
                Code = string.IsNullOrEmpty(code) ? null : code, Reason = string.IsNullOrEmpty(reason) ? null : reason,
            };
    }

    /// <summary>A rule of a downtime table's code (<c>codeRules[i]</c>; it was a record <c>code|state|mode|factor|ignore</c>):
    /// rows with the code take this state and mode, their duration times the factor, or are dropped.</summary>
    [Serializable]
    public sealed class DowntimeCodeRuleSettings : SettingsGroup
    {
        /// <summary>The code and the state its rows take.</summary>
        [Setting(Role = R.Structural)] public string Code, State;
        /// <summary>The stop mode (absent = the downtime's).</summary>
        [Setting(Role = R.Structural, Logic = L.None)] public StopMode? Mode;
        /// <summary>Code: duration factor; default 1.0.</summary>
        [Setting("Code: duration factor", Min = 0)] public double Factor = 1.0;
        /// <summary>Drop the rows with this code.</summary>
        [Setting(Role = R.Structural)] public bool Ignore;
    }

    /// <summary>The value of a downtime table's target column for one target (<c>targetKeys[i]</c>; it was a record
    /// <c>key|elementId</c>).</summary>
    [Serializable]
    public sealed class DowntimeTargetKeySettings : SettingsGroup
    {
        /// <summary>The target column's value and the target (element id) it stands for.</summary>
        [Setting(Role = R.Structural)] public string Key, Target;

        /// <summary>A record of the given values.</summary>
        public static DowntimeTargetKeySettings Of(string key, string target) => new DowntimeTargetKeySettings { Key = key, Target = target };
    }

    /// <summary>A downtime's repair crew (<c>repairBy</c>): a repair is not preemptable unless it says so.</summary>
    [Serializable]
    public sealed class RepairCrewSettings : SettingsGroup
    {
        /// <summary>The resource team (by id or name).</summary>
        [Setting(Role = R.Structural, Logic = L.Resources)] public string Team;
        /// <summary>The kinds of members needed (records; absent = one member of the team).</summary>
        [Setting(Logic = L.Resources)] public List<RequirementSettings> Requirements;
        /// <summary>The priority of its requests: an expression (a number, or over the item and the element).</summary>
        [Setting(Role = R.Expression, Logic = L.Resources, Context = "priority", LogicDefaultValue = "1")] public string Priority;
        /// <summary>How a request of higher priority takes its members (<see cref="PreemptMode"/>).</summary>
        [Setting(Logic = L.Resources)] public PreemptMode Preemption = PreemptMode.None;
        /// <summary>Repair crew can be preempted.</summary>
        [Setting("Repair crew can be preempted", Logic = L.Resources)] public bool Preemptable;
        /// <summary>How the members are acquired (<see cref="AcquireOrder"/>).</summary>
        [Setting(Logic = L.Resources)] public AcquireOrder Acquire = AcquireOrder.AllAtOnce;
        /// <summary>The longest wait for the resources: a number or an expression (absent = no limit).</summary>
        [Setting(Role = R.Expression, Units = "time", Logic = L.Wait, Context = "maxWait", LogicDefaultValue = "0")] public string MaxWait;
        [Setting(Role = R.Choice, Choices = new[] { nameof(TimeoutAction.KeepWaiting), nameof(TimeoutAction.GiveUp) }, Logic = L.Wait, LogicDefaultValue = "KeepWaiting", LogicWhen = "maxWait!=|0")] public string OnTimeout;
    }
}
