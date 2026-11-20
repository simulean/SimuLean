using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using SimuLean.Serialization;

namespace SimuLean.Optimization
{
    /// <summary>What a parameter belongs to.</summary>
    /// <summary>The scope of a factor path (<c>[scope:]Owner.key</c>, the lower-case name; element when omitted). Data =
    /// a data table's source per level (<c>data:Table.file</c> / <c>.sql</c>), not a catalog key.</summary>
    public enum ParameterScope { Element, Team, Downtime, Reaction, Data }

    /// <summary>How a parameter's value is stored in the config (how a candidate's value is written back).</summary>
    public enum ValueEncoding
    {
        /// <summary>An <c>int</c> (vector: <c>int[]</c>).</summary>
        Integer,
        /// <summary>A <c>double</c> (vector: <c>double[]</c>).</summary>
        Real,
        Bool,
        /// <summary>A text, one of the options.</summary>
        Choice,
        /// <summary>Numbers in a text separated by <c>|</c>.</summary>
        NumberList,
        /// <summary>A team's number of members (extra members copy the last one).</summary>
        Members,
        /// <summary>A number (array) of the user properties (the name after the dot of the key).</summary>
        Property,
        /// <summary>A text written as given (<see cref="TextSettingParameter"/>): a declared expression or a choice among
        /// another key's records written in place. No range and no options of its own: the texts to try are always given.</summary>
        Text,
    }

    /// <summary>
    /// Something of a model that can be a parameter, as the catalog found it in the config: the key, its kind and length
    /// (a number, a vector, a choice, a yes / no), how it is stored, its current value and a suggested range.
    /// </summary>
    public sealed class ParameterInfo
    {
        /// <summary>Unique within its owner: the config key — a field path (a distribution's number: its field,
        /// e.g. <c>setup.time.uniformMax</c>; a user property: <c>properties.name</c>).</summary>
        public string Key { get; set; }
        public string Label { get; set; }
        public VariableKind Kind { get; set; } = VariableKind.Real;
        public ValueEncoding Encoding { get; set; } = ValueEncoding.Real;
        /// <summary>Values: 1, or the items of a vector.</summary>
        public int Length { get; set; } = 1;
        /// <summary>The options of a choice.</summary>
        public IReadOnlyList<string> Options { get; set; } = Array.Empty<string>();
        /// <summary>The config key that stores it (= <see cref="Key"/> except for a user property).</summary>
        public string ConfigKey { get; set; }
        /// <summary>The current value (the first one of a vector; a choice's option index).</summary>
        public double Current { get; set; }
        /// <summary>The current value as text (all values of a vector; a choice's option).</summary>
        public string CurrentText { get; set; } = "";
        public double Min { get; set; }
        public double Max { get; set; }

        public bool IsNumber => !IsText && (Kind == VariableKind.Integer || Kind == VariableKind.Real) && Length == 1;

        /// <summary>A text setting (<see cref="ValueEncoding.Text"/>): its current text is shown, the texts to try are given
        /// by the user (a study's levels, an optimiser's options); the numeric search never builds a variable of it.</summary>
        public bool IsText => Encoding == ValueEncoding.Text;

        /// <summary>The decision variable of this parameter (numbers in [min, max]; a choice over every option; a yes / no).
        /// A text setting has none: it is varied over given texts (<see cref="TextSettingParameter"/>).</summary>
        public DecisionVariable Variable(string name, double min, double max)
        {
            if (IsText) throw new ArgumentException(TextSettingParameter.NeedsTexts(name, CurrentText));
            switch (Kind)
            {
                case VariableKind.Integer: return DecisionVariable.Integer(name, (int)Math.Ceiling(min), (int)Math.Floor(max), Length);
                case VariableKind.Real: return DecisionVariable.Real(name, min, max, Length);
                case VariableKind.Choice: return DecisionVariable.Choice(name, Options);
                case VariableKind.Binary: return DecisionVariable.Binary(name, Length);
            }
            throw new ArgumentException($"Parameter '{name}': {Kind} is not a config parameter.");
        }

        public override string ToString() => $"{Label} ({Key}) = {CurrentText}";
    }

    /// <summary>
    /// What a model offers to parameterise — every key of every element, team and downtime config, as its
    /// <see cref="ParameterSchema"/> and the value's type say (nothing to list by hand) — plus schedule tables.
    /// <see cref="Undecided"/> names the keys no schema decides (a test keeps it empty).
    /// </summary>
    public static class ModelParameterCatalog
    {
        /// <summary>The name of a member added to <paramref name="team"/>: team_n, the first n no member has
        /// (n = count + 1 could repeat a name after members were removed or renamed).</summary>
        public static string NewMemberName(ResourceTeamConfig team)
        {
            string prefix = (team.Name ?? team.Id) + "_";
            for (int n = team.Members.Count + 1; ; n++)
                if (!team.Members.Exists(m => m.Name == prefix + n)) return prefix + n;
        }

        public const string TeamMembers = "members", TeamSpeed = "speed";

        public static IReadOnlyList<ParameterInfo> ForElement(ElementConfig element, ElementHandlerRegistry registry = null)
            => element == null ? new List<ParameterInfo>()
             : Offered(Describe(ElementBag(element), SchemaOf(element, registry), null, element.Settings?.GetType() ?? typeof(ElementSettings)),
                       ElementBag(element), ParameterScope.Element, element.Settings?.GetType());

        public static IReadOnlyList<ParameterInfo> ForTeam(ResourceTeamConfig team)
            => team == null ? new List<ParameterInfo>()
             : Offered(Describe(new TeamBag(team), ParameterSchema.Team, null, typeof(ResourceTeamConfig)), new TeamBag(team), ParameterScope.Team);

        public static IReadOnlyList<ParameterInfo> ForDowntime(DowntimeConfig downtime)
            => downtime == null ? new List<ParameterInfo>()
             : Offered(Describe(new SettingsBag(downtime), ParameterSchema.Downtime, null, typeof(DowntimeSettings)), new SettingsBag(downtime), ParameterScope.Downtime,
                       typeof(DowntimeSettings));

        /// <summary>A reaction's parameters: <c>enabled</c>, its actions' plain numbers and stop durations.</summary>
        public static IReadOnlyList<ParameterInfo> ForReaction(ReactionConfig reaction)
            => reaction == null ? new List<ParameterInfo>() : Describe(new SettingsBag(reaction), ParameterSchema.Reaction, null, typeof(ReactionSettings));

        /// <summary>The parameters of <paramref name="owner"/> (an element / team name, a downtime id) in a model — those its
        /// current settings use (the chosen distribution's, the team's travel mode's), labelled by their role.</summary>
        public static IReadOnlyList<ParameterInfo> For(SimulationConfig model, ParameterScope scope, string owner)
        {
            var bag = Bag(model, scope, owner);
            return Offered(Describe(bag, Schema(model, scope, owner), null, SettingsTypeOf(model, scope, owner)), bag, scope,
                           scope == ParameterScope.Element ? FindElement(model, owner).Settings?.GetType()
                           : scope == ParameterScope.Downtime ? typeof(DowntimeSettings) : null);
        }

        /// <summary>Every parameter of <paramref name="owner"/>, also those its current settings ignore (resolving a saved
        /// parameter keeps working when, e.g., the distribution was changed).</summary>
        internal static IReadOnlyList<ParameterInfo> ForAll(SimulationConfig model, ParameterScope scope, string owner)
            => Describe(Bag(model, scope, owner), Schema(model, scope, owner), null, SettingsTypeOf(model, scope, owner));

        /// <summary>The keys of a model that no schema decides ("type.key" / "team.Property" / "downtime.key"): each must
        /// become a parameter or be declared (<see cref="ParameterSchema"/>).</summary>
        public static IReadOnlyList<string> Undecided(SimulationConfig model, ElementHandlerRegistry registry = null)
        {
            var list = new List<string>();
            foreach (var e in model.Elements) Describe(ElementBag(e), SchemaOf(e, registry), k => list.Add(e.Type + "." + k));
            foreach (var t in model.ResourceTeams) Describe(new TeamBag(t), ParameterSchema.Team, k => list.Add("team." + k));
            foreach (var d in model.Downtimes) Describe(new SettingsBag(d), ParameterSchema.Downtime, k => list.Add("downtime." + d.Kind + "." + k));
            foreach (var r in model.Reactions) Describe(new SettingsBag(r), ParameterSchema.Reaction, k => list.Add("reaction." + k));
            return list;
        }

        /// <summary>Rows of an element's schedule table (0 = none): the length of sequence / per-row parameters.</summary>
        public static int ScheduleRows(ElementConfig element)
        {
            var table = element?.Get<Dictionary<string, List<string>>>("arrivalsTable");
            if (table == null || table.Count == 0) return 0;
            foreach (var col in table.Values) return col.Count;
            return 0;
        }

        // ── what the current settings use, and labels by role ────────────

        /// <summary>The fields of each distribution (<see cref="SamplerSettings"/>): the keys after a distribution's path
        /// (<c>delayStrategy.uniformMin</c>, <c>setup.time.uniformMin</c>, a downtime's <c>timeToFailure.exponentialMean</c>).</summary>
        internal static Dictionary<string, string[]> FlatSamplerKeys => SamplerSpec.Fields;

        /// <summary>The label of a distribution setting of an element (its <see cref="SettingAttribute"/> label: "Service
        /// time", "Setup time"), else its path in words.</summary>
        private static string SamplerRole(Type settingsType, string field)
        {
            SettingsPaths.Member m = null;
            try { if (settingsType != null) m = SettingsPaths.Resolve(settingsType, field); } catch (ArgumentException) { }
            var a = m == null ? null : (SettingAttribute)Attribute.GetCustomAttribute(m.Field, typeof(SettingAttribute));
            return a?.Label ?? Humanize(System.Text.RegularExpressions.Regex.Replace(field, @"[\.\[\]]+", " ").Trim());
        }

        /// <summary>A flat sampler key <c>field.leaf</c> (a distribution's field after the sampler's own field, which has a
        /// <c>field.strategyType</c> in <paramref name="has"/>): true with both parts.</summary>
        private static bool IsFlatSamplerKey(string key, Func<string, bool> has, out string field, out string leaf)
        {
            field = leaf = null;
            int dot = key.LastIndexOf('.');
            if (dot <= 0) return false;
            field = key.Substring(0, dot); leaf = key.Substring(dot + 1);
            if (leaf == "strategyType" || !has(ElementBuildHelpers.SamplerKey(field, "strategyType"))) return false;
            foreach (var keys in FlatSamplerKeys.Values) if (Array.IndexOf(keys, leaf) >= 0) return true;
            return false;
        }

        /// <summary>The team properties each travel mode uses (the others are not offered).</summary>
        private static readonly Dictionary<string, string[]> TravelKeys = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = new string[0], ["Abstract"] = new string[0],
            ["Network"] = new[] { "speed", "traffic", "returnHome" }, ["Matrix"] = new[] { "speed", "returnHome" },
            ["Straight"] = new[] { "speed", "returnHome" }, ["Grid"] = new[] { "speed", "returnHome" }, ["Axes"] = new[] { "axisSpeed", "axisAccel", "axesSequential", "returnHome" },
            ["Calibrated"] = new[] { "speed", "returnHome" },
        };
        private static readonly string[] TravelOnly = { "speed", "traffic", "axisSpeed", "axisAccel", "axesSequential", "returnHome" };

        /// <summary>
        /// What is offered of a described list: an element's flat sampler keys only for its chosen distribution, labelled by
        /// the sampler's role ("Service time", "Inter-arrival time — Uniform min"); a team's travel properties only for its
        /// travel mode.
        /// </summary>
        private static List<ParameterInfo> Offered(List<ParameterInfo> list, IConfigBag bag, ParameterScope scope, Type settingsType = null)
        {
            if (scope == ParameterScope.Team)
            {
                string travel = bag.Get("travel") as string ?? "";
                if (!TravelKeys.TryGetValue(travel, out var used)) return list;   // an unknown mode: offer everything
                return list.FindAll(p => Array.IndexOf(TravelOnly, p.ConfigKey ?? p.Key) < 0 || Array.IndexOf(used, p.ConfigKey ?? p.Key) >= 0);
            }
            if (scope != ParameterScope.Element && scope != ParameterScope.Downtime) return list;

            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in bag.Entries) present.Add(e.key);
            var offered = new List<ParameterInfo>();
            foreach (var p in list)
            {
                string key = p.ConfigKey ?? p.Key;
                if (!IsFlatSamplerKey(key, present.Contains, out string field, out string leaf)) { offered.Add(p); continue; }
                string type = bag.Get(ElementBuildHelpers.SamplerKey(field, "strategyType")) as string ?? "Constant";
                if (!FlatSamplerKeys.TryGetValue(type, out var keys) || Array.IndexOf(keys, leaf) < 0) continue;   // not used by the chosen distribution
                string role = SamplerRole(settingsType, field);
                p.Label = type == "Constant" ? role : role + " — " + p.Label;
                offered.Add(p);
            }
            return offered;
        }

        /// <summary>
        /// When <paramref name="key"/> is a flat sampler key of element <paramref name="owner"/> that its chosen
        /// distribution does not use (<c>delayStrategy.uniformMin</c> while the service time is Constant), the distribution
        /// it uses; null otherwise. A study factor on such a key changes nothing (<c>simulean_set</c> warned, a
        /// study did not).
        /// </summary>
        public static string InactiveSamplerKey(SimulationConfig model, string owner, string key)
        {
            var e = model.Elements.Find(x => x.Name == owner);
            if (e == null || !IsFlatSamplerKey(key, e.Has, out string field, out string leaf)) return null;
            string type = e.Get<string>(ElementBuildHelpers.SamplerKey(field, "strategyType")) ?? "Constant";
            return FlatSamplerKeys.TryGetValue(type, out var keys) && Array.IndexOf(keys, leaf) >= 0 ? null : type;
        }

        // ── the catalog ──────────────────────────────────────────────────

        private static ParameterSchema SchemaOf(ElementConfig e, ElementHandlerRegistry registry)
        {
            // the settings class is the one declaration: its schema by reflection
            return ParameterSchema.Of(e?.Settings?.GetType() ?? typeof(ElementSettings));
        }

        private static ParameterSchema Schema(SimulationConfig model, ParameterScope scope, string owner)
            => scope == ParameterScope.Element ? SchemaOf(FindElement(model, owner), null)
             : scope == ParameterScope.Team ? ParameterSchema.Team
             : scope == ParameterScope.Reaction ? ParameterSchema.Reaction
             : scope == ParameterScope.Downtime ? ParameterSchema.Downtime
             : throw new ArgumentException($"Parameter: a {scope} factor has no catalog keys (data:Table.file / data:Table.sql).");

        /// <summary>
        /// True when <paramref name="key"/> of <paramref name="owner"/> is a declared text setting a study may vary by its
        /// texts (<see cref="TextSettingParameter"/>): a text whose schema rule is an expression (a resource use's priority)
        /// or a choice among another key's records (a team's <c>requestOrder</c>, whose value may be a definition written in
        /// place). Never a structural key (a name, a reference: a study or an optimiser must not vary those), a fixed-option
        /// choice (its options are the levels a study may give), a file (varied by its own parameters) or a key that is not
        /// a text. False for an unknown owner or key.
        /// </summary>
        public static bool IsTextSetting(SimulationConfig model, ParameterScope scope, string owner, string key)
        {
            try
            {
                if (scope == ParameterScope.Data || string.IsNullOrWhiteSpace(key)) return false;
                var entry = Schema(model, scope, owner).Find(key);
                if (entry == null) return false;
                if (!IsTextRule(key, entry)) return false;
                var member = SettingsPaths.Resolve(SettingsTypeOf(model, scope, owner), key);
                return member != null && member.Type == typeof(string);
            }
            catch (ArgumentException) { return false; }
        }

        // the typed settings a scope's keys are paths of (as its schema)
        private static Type SettingsTypeOf(SimulationConfig model, ParameterScope scope, string owner)
            => scope == ParameterScope.Element ? FindElement(model, owner).Settings?.GetType() ?? typeof(ElementSettings)
             : scope == ParameterScope.Team ? typeof(ResourceTeamConfig)
             : scope == ParameterScope.Reaction ? typeof(ReactionSettings)
             : typeof(DowntimeSettings);

        /// <summary>The current text of a text setting (<see cref="IsTextSetting"/>; empty when it is not set).</summary>
        public static string TextSetting(SimulationConfig model, ParameterScope scope, string owner, string key)
            => Bag(model, scope, owner).Get(key) as string ?? "";

        /// <summary>Writes a text setting (<see cref="IsTextSetting"/>) of <paramref name="owner"/> in <paramref name="config"/>.</summary>
        internal static void SetTextSetting(SimulationConfig config, ParameterScope scope, string owner, string key, string text)
            => Bag(config, scope, owner).Set(key, text);

        internal static IConfigBag Bag(SimulationConfig model, ParameterScope scope, string owner)
        {
            switch (scope)
            {
                case ParameterScope.Element: return ElementBag(FindElement(model, owner));
                case ParameterScope.Team:
                    return new TeamBag(TeamNamed(model, owner));
                case ParameterScope.Reaction:
                    return new SettingsBag(model.Reactions.Find(r => r.Id == owner)
                                           ?? throw new ArgumentException($"Parameter: no reaction '{owner}' in the model."));
                case ParameterScope.Data:
                    throw new ArgumentException("Parameter: a data factor has no catalog keys (data:Table.file / data:Table.sql).");
                default:
                    return new SettingsBag(model.Downtimes.Find(d => d.Id == owner)
                                           ?? throw new ArgumentException($"Parameter: no downtime '{owner}' in the model."));
            }
        }

        private static ElementConfig FindElement(SimulationConfig model, string name)
            => ElementNamed(model, name, "Parameter");

        /// <summary>The element of this name — an error when there is none (with the closest names) or several
        /// (a factor changed the first of two silently).</summary>
        public static ElementConfig ElementNamed(SimulationConfig model, string name, string what = "Parameter")
        {
            var all = model.Elements.FindAll(x => x.Name == name);
            if (all.Count == 1) return all[0];
            if (all.Count > 1) throw new ArgumentException($"{what}: {all.Count} elements are named '{name}' ({string.Join(", ", all.ConvertAll(x => x.Id))}): give them different names.");
            throw new ArgumentException($"{what}: no element '{name}' in the model." + ScopeHint(model, name) +
                                        NameSuggestions.DidYouMean(name, model.Elements.ConvertAll(x => x.Name)));
        }

        /// <summary>" 'X' is a resource team: …" when the name is a team, downtime or reaction: their keys are reached
        /// through the scope prefix (<c>team:X.members</c>); a bare owner only finds elements.</summary>
        private static string ScopeHint(SimulationConfig model, string name)
        {
            var team = model.ResourceTeams?.Find(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) || t.Id == name);
            if (team != null) return $" '{name}' is a resource team: write 'team:{team.Name}.<key>' (team:{team.Name}.members).";
            var downtime = model.Downtimes?.Find(d => string.Equals(d.Id, name, StringComparison.OrdinalIgnoreCase));
            if (downtime != null) return $" '{name}' is a downtime: write 'downtime:{downtime.Id}.<key>'.";
            var reaction = model.Reactions?.Find(r => string.Equals(r.Id, name, StringComparison.OrdinalIgnoreCase));
            if (reaction != null) return $" '{name}' is a reaction: write 'reaction:{reaction.Id}.<key>'.";
            return "";
        }

        /// <summary>The resource team of this id, else of this name (unique).</summary>
        public static ResourceTeamConfig TeamNamed(SimulationConfig model, string name, string what = "Parameter")
        {
            var byId = model.ResourceTeams.Find(t => t.Id == name);
            if (byId != null) return byId;
            var all = model.ResourceTeams.FindAll(t => t.Name == name);
            if (all.Count == 1) return all[0];
            if (all.Count > 1) throw new ArgumentException($"{what}: {all.Count} resource teams are named '{name}': give them different names.");
            throw new ArgumentException($"{what}: no resource team '{name}' in the model." + NameSuggestions.DidYouMean(name, model.ResourceTeams.ConvertAll(t => t.Name)));
        }

        private static IConfigBag ElementBag(ElementConfig e) => new SettingsBag(e);

        /// <param name="settingsType">The typed settings the keys are paths of: with it, the declared text settings left
        /// unset are listed too (<see cref="UnsetTexts"/>); without it (<see cref="Undecided"/>), only the keys given.</param>
        private static List<ParameterInfo> Describe(IConfigBag bag, ParameterSchema schema, Action<string> undecided, Type settingsType = null)
        {
            var list = new List<ParameterInfo>();
            foreach (var (key, value) in bag.Entries)
            {
                var entry = schema.Find(key);
                if (entry != null)
                {
                    switch (entry.Rule)
                    {
                        case ParameterSchema.Rule.Structural:
                        case ParameterSchema.Rule.File: break;   // a file is varied by its levels (Study), not by the catalog
                        case ParameterSchema.Rule.Choice:
                        {
                            // fixed options, or the names of another key's records (ChoiceFrom)
                            var options = entry.Options ?? ParameterSchema.RecordNames(bag.Get(entry.OptionsKey));
                            int i = Array.IndexOf(options, value as string);
                            if (i >= 0) list.Add(Refined(schema, new ParameterInfo
                            {
                                Key = key, Kind = VariableKind.Choice, Encoding = ValueEncoding.Choice, Options = options,
                                Current = i, CurrentText = (string)value, Min = 0, Max = options.Length - 1,
                            }));
                            // a definition written in place (a request order '|priority desc', none): a text setting
                            else if (entry.OptionsKey != null && value is string inPlace && IsTextRule(key, entry)) list.Add(TextInfo(schema, key, inPlace));
                            break;
                        }
                        case ParameterSchema.Rule.Expression:
                        {
                            // a number already (a reaction's value): offered by its type
                            if (!(value is string))
                            {
                                var number = Infer(schema, key, value);
                                if (number != null) list.Add(number);
                                break;
                            }
                            // a constant: one number written back as the expression's text (NumberList of one)
                            if (value is string text && double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                                                                        System.Globalization.CultureInfo.InvariantCulture, out double constant))
                                list.Add(Vector(schema, key, new[] { constant }, false, ValueEncoding.NumberList));
                            // one that reads the item or the model: a text setting
                            else if (value is string expression && IsTextRule(key, entry)) list.Add(TextInfo(schema, key, expression));
                            break;
                        }
                        case ParameterSchema.Rule.Properties: list.AddRange(PropertyParts(schema, key, value)); break;
                        case ParameterSchema.Rule.NumberList:
                        {
                            var numbers = SplitNumbers(value as string);
                            if (numbers != null && numbers.Length > 0)
                                list.Add(Vector(schema, key, numbers, entry.Integer, ValueEncoding.NumberList));
                            break;
                        }
                    }
                    continue;
                }
                var info = Infer(schema, key, value);
                if (info != null) list.Add(info);
                else undecided?.Invoke(key);
            }
            if (settingsType != null && undecided == null) list.AddRange(UnsetTexts(bag, schema, settingsType, list));
            return list;
        }

        private static ParameterInfo TextInfo(ParameterSchema schema, string key, string current)
            => Refined(schema, new ParameterInfo { Key = key, Kind = VariableKind.Choice, Encoding = ValueEncoding.Text, CurrentText = current ?? "" });

        /// <summary>True for a key whose value is a text setting (<see cref="IsTextSetting"/>): an expression or a choice
        /// among another key's records, of the owner itself or one of its groups. Never a structural key (a name, a
        /// reference: no study varies those) nor a record's field (<c>actions[0].value</c>, <c>tasks[1].…</c>: a list's
        /// records are not factors).</summary>
        private static bool IsTextRule(string key, ParameterSchema.Entry entry)
            => entry != null && !(key ?? "").Contains("[")
               && (entry.Rule == ParameterSchema.Rule.Expression || (entry.Rule == ParameterSchema.Rule.Choice && entry.OptionsKey != null));

        /// <summary>
        /// The declared text settings left unset (empty) where they would apply: a key of a group the owner has (an
        /// element's <c>processBy.priority</c> once it has a process team), and a team's own (<c>requestOrder</c>) — so a
        /// study, the Experimenter and the Optimizer can name them. Not a record's (<c>tasks[].…</c>) nor an element's
        /// top-level keys (conditions and labels the element does not use).
        /// </summary>
        private static IEnumerable<ParameterInfo> UnsetTexts(IConfigBag bag, ParameterSchema schema, Type settingsType, List<ParameterInfo> listed)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (k, _) in bag.Entries) keys.Add(k);
            foreach (var p in listed) keys.Add(p.ConfigKey ?? p.Key);
            foreach (var kv in schema.Entries)
            {
                string key = kv.Key;
                if (keys.Contains(key) || !IsTextRule(key, kv.Value)) continue;
                int dot = key.LastIndexOf('.');
                if (dot < 0 ? settingsType != typeof(ResourceTeamConfig)
                            : !keys.Any(k => k.StartsWith(key.Substring(0, dot + 1), StringComparison.Ordinal))) continue;
                SettingsPaths.Member member = null;
                try { member = SettingsPaths.Resolve(settingsType, key); } catch (ArgumentException) { }
                if (member?.Type != typeof(string)) continue;
                yield return TextInfo(schema, key, "");
            }
        }

        /// <summary>A key nobody declared: its value's type decides (numbers, bools, number arrays), else null.</summary>
        private static ParameterInfo Infer(ParameterSchema schema, string key, object value)
        {
            switch (value)
            {
                case int i: return Number(schema, key, i, true);
                case long l: return Number(schema, key, l, true);
                case double d: return Number(schema, key, d, false);
                case float f: return Number(schema, key, f, false);
                case bool b:
                    return Refined(schema, new ParameterInfo
                    {
                        Key = key, Kind = VariableKind.Binary, Encoding = ValueEncoding.Bool, Current = b ? 1 : 0,
                        CurrentText = b ? "true" : "false", Min = 0, Max = 1,
                    });
                case int[] ia when ia.Length > 0:
                {
                    var d = new double[ia.Length]; for (int k = 0; k < d.Length; k++) d[k] = ia[k];
                    return Vector(schema, key, d, true, ValueEncoding.Integer);
                }
                case double[] da when da.Length > 0: return Vector(schema, key, da, false, ValueEncoding.Real);
            }
            return null;
        }

        private static ParameterInfo Number(ParameterSchema schema, string key, double current, bool integer)
        {
            var (min, max) = Range(current, integer);
            return Refined(schema, new ParameterInfo
            {
                Key = key, Kind = integer ? VariableKind.Integer : VariableKind.Real, Encoding = integer ? ValueEncoding.Integer : ValueEncoding.Real,
                Current = current, CurrentText = Text(current), Min = min, Max = max,
            });
        }

        private static ParameterInfo Vector(ParameterSchema schema, string key, double[] values, bool integer, ValueEncoding encoding)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (double v in values) { var (a, b) = Range(v, integer); lo = Math.Min(lo, a); hi = Math.Max(hi, b); }
            var parts = new string[values.Length];
            for (int k = 0; k < parts.Length; k++) parts[k] = Text(values[k]);
            return Refined(schema, new ParameterInfo
            {
                Key = key, Kind = integer ? VariableKind.Integer : VariableKind.Real, Encoding = encoding, Length = values.Length,
                Current = values[0], CurrentText = string.Join(", ", parts), Min = lo, Max = hi,
            });
        }

        /// <summary>Suggested range around a value: integers 1 (0 for a zero or negative value) … max(10, 2 × value); reals
        /// ½ … 2 × value (0 … 1 for zero).</summary>
        private static (double min, double max) Range(double current, bool integer)
        {
            if (integer) return (current >= 1 ? 1 : 0, Math.Max(10, 2 * Math.Ceiling(current)));
            if (current == 0) return (0, 1);
            return (Math.Min(current / 2, current * 2), Math.Max(current / 2, current * 2));
        }

        private static IEnumerable<ParameterInfo> PropertyParts(ParameterSchema schema, string key, object records)
        {
            if (!(records is IEnumerable<PropertySettings> list)) yield break;
            foreach (var rec in list)
            {
                if (rec == null || string.IsNullOrWhiteSpace(rec.Name)) continue;
                double[] numbers = rec.Kind == PropertyValueKind.Number ? new[] { rec.Value }
                                 : rec.Kind == PropertyValueKind.Numbers ? rec.Numbers : null;   // texts, references: not parameters
                if (numbers == null || numbers.Length == 0) continue;
                var info = Vector(schema, key + "." + rec.Name.Trim(), numbers, false, ValueEncoding.Property);
                info.ConfigKey = key;
                info.Label = schema.Refine(info.Key)?.Label ?? "Property " + rec.Name.Trim();
                yield return info;
            }
        }

        private static ParameterInfo Refined(ParameterSchema schema, ParameterInfo info)
        {
            var r = schema.Refine(info.Key);
            info.ConfigKey = info.ConfigKey ?? info.Key;
            info.Label = r?.Label ?? info.Label ?? Humanize(info.Key);
            if (r?.Min != null) info.Min = Math.Max(info.Min, r.Min.Value) <= info.Max ? Math.Max(info.Min, r.Min.Value) : r.Min.Value;
            if (r?.Max != null) info.Max = Math.Max(info.Min, Math.Min(info.Max, r.Max.Value));
            if (info.Max < info.Min) info.Max = info.Min;
            return info;
        }

        private static double[] SplitNumbers(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string[] p = text.Split('|');
            var d = new double[p.Length];
            for (int k = 0; k < p.Length; k++)
                if (!double.TryParse(p[k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d[k])) return null;
            return d;
        }

        private static double[] ListNumbers(object value)
        {
            if (!(value is IEnumerable items) || value is string) return null;
            var d = new List<double>();
            foreach (var o in items)
            {
                if (!double.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)) return null;
                d.Add(x);
            }
            return d.ToArray();
        }

        internal static string Text(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

        /// <summary><c>maxItems</c> → "Max items", <c>AxisSpeed</c> → "Axis speed".</summary>
        private static string Humanize(string key)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in key.Replace('_', ' '))
            {
                if (char.IsUpper(c) && sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                sb.Append(sb.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // ── config access ────────────────────────────────────────────────

        /// <summary>The keys and values of a config object, read and written alike (element / downtime bags, team properties).</summary>
        internal interface IConfigBag
        {
            IEnumerable<(string key, object value)> Entries { get; }
            object Get(string key);
            void Set(string key, object value);
        }

        /// <summary>Typed settings by path (an element's, a downtime's, a reaction's): every setting given, read and written
        /// through the config's <c>Get</c> / <c>Set</c> (a value is converted to the setting's type).</summary>
        private sealed class SettingsBag : IConfigBag
        {
            private readonly IConfigValues c;
            private readonly ElementConfig e;
            private readonly Func<IEnumerable<(string, object)>> all;
            public SettingsBag(ElementConfig e) { this.e = e; c = e; all = e.Values; }
            public SettingsBag(DowntimeConfig d) { c = d; all = d.Values; }
            public SettingsBag(ReactionConfig r) { c = r; all = r.Values; }
            private IEnumerable<(string, object)> All => all();
            // an option (an enum setting) reads as its name, as a text choice does; the user properties are also one entry
            // (their records, offered by name: properties.<name>)
            public IEnumerable<(string key, object value)> Entries
            {
                get
                {
                    foreach (var (k, v) in new List<(string, object)>(All)) yield return (k, v is Enum ? v.ToString() : v);
                    if (e?.Settings?.Properties != null && e.Settings.Properties.Count > 0) yield return (PropertyRecords.Key, e.Settings.Properties);
                    // the parameters of its choice rules, every one of them (absent ones at the rule's default)
                    if (e?.Settings != null)
                    {
                        var rules = SimuLean.Rules.ChoiceRules.RulesOf(e.Settings);
                        if (rules.Count > 0) e.Settings.Rule = SimuLean.Rules.ChoiceRules.Complete(e.Settings.Rule, rules);
                        if (e.Settings.Rule != null && e.Settings.Rule.Count > 0) yield return (SimuLean.Rules.ChoiceRules.Key, e.Settings.Rule);
                    }
                }
            }
            public object Get(string key) => c.TryGet(key, out object v) ? (v is Enum ? v.ToString() : v) : null;
            public void Set(string key, object value) => c.Set(key, value);
        }

        /// <summary>A team's typed settings by path (<c>team:T.speed</c>, <c>homeNode</c>, <c>jobOnMemberStop</c>, <c>axisSpeed</c>),
        /// its number of members (<c>members</c>) and the members' load / unload times (<c>loadTime</c>,
        /// <c>unloadTime</c>: every member's). The members' own records are not catalog keys.</summary>
        private sealed class TeamBag : IConfigBag
        {
            private readonly ResourceTeamConfig team;
            public TeamBag(ResourceTeamConfig team) { this.team = team; }

            public const string LoadTime = "loadTime", UnloadTime = "unloadTime";

            public IEnumerable<(string key, object value)> Entries
            {
                get
                {
                    yield return (TeamMembers, team.Members.Count);
                    if (team.Members.Count > 0)
                    {
                        yield return (LoadTime, team.Members[0].LoadTime);
                        yield return (UnloadTime, team.Members[0].UnloadTime);
                    }
                    foreach (var (k, v) in new List<(string, object)>(team.Values()))
                        if (!k.StartsWith("members[", StringComparison.Ordinal)) yield return (k, v is Enum ? v.ToString() : v);
                    // its dispatch rule's parameters
                    string rule = SimuLean.Rules.ChoiceRules.RuleOf(team);
                    if (rule != null) team.Rule = SimuLean.Rules.ChoiceRules.Complete(team.Rule, new[] { rule });
                    if (team.Rule != null && team.Rule.Count > 0) yield return (SimuLean.Rules.ChoiceRules.Key, team.Rule);
                }
            }

            public object Get(string key)
            {
                if (key == TeamMembers) return team.Members.Count;
                if (key == LoadTime) return team.Members.Count > 0 ? team.Members[0].LoadTime : 0.0;
                if (key == UnloadTime) return team.Members.Count > 0 ? team.Members[0].UnloadTime : 0.0;
                if (!SettingsPaths.Declares(typeof(ResourceTeamConfig), key))
                    throw SettingsPaths.Unknown(typeof(ResourceTeamConfig), key, "Resource team '" + (team.Name ?? team.Id) + "'");
                return team.TryGet(key, out object v) ? (v is Enum ? v.ToString() : v) : null;
            }

            public void Set(string key, object value)
            {
                if (key == TeamMembers) { Resize(Convert.ToInt32(value, CultureInfo.InvariantCulture)); return; }
                if (key == LoadTime) { foreach (var m in team.Members) m.LoadTime = Convert.ToDouble(value, CultureInfo.InvariantCulture); return; }
                if (key == UnloadTime) { foreach (var m in team.Members) m.UnloadTime = Convert.ToDouble(value, CultureInfo.InvariantCulture); return; }
                team.Set(key, value);
            }

            /// <summary>Extra members copy the last one (named team_n); at least one member.</summary>
            private void Resize(int n)
            {
                n = Math.Max(1, n);
                if (team.Members.Count == 0) team.Members.Add(new ResourceMemberConfig { Name = (team.Name ?? team.Id) + "_1" });
                while (team.Members.Count > n) team.Members.RemoveAt(team.Members.Count - 1);
                while (team.Members.Count < n)
                {
                    var copy = ConfigCloner.DeepClone(team.Members[team.Members.Count - 1]);
                    copy.Name = NewMemberName(team);
                    team.Members.Add(copy);
                }
            }
        }
    }

    /// <summary>
    /// A decision variable bound to the model: applies the candidate's value to a copy of the config before it is built.
    /// </summary>
    public abstract class ModelParameter
    {
        public abstract DecisionVariable Variable { get; }

        /// <summary>Applies the value of variable <paramref name="index"/> of <paramref name="candidate"/> to <paramref name="config"/>.</summary>
        public abstract void Apply(SimulationConfig config, Candidate candidate, int index);

        protected static ElementConfig ElementNamed(SimulationConfig config, string name)
        {
            return ModelParameterCatalog.ElementNamed(config, name, "Optimisation parameter");
        }
    }

    /// <summary>
    /// Any catalog parameter (<see cref="ModelParameterCatalog"/>) of an element, a resource team or a downtime: the
    /// candidate's value is written back the way the config stores it (<see cref="ValueEncoding"/>).
    /// </summary>
    public sealed class ConfigParameter : ModelParameter
    {
        public ParameterScope Scope { get; }
        /// <summary>The element / team name or the downtime id.</summary>
        public string Owner { get; }
        public ParameterInfo Info { get; }
        public override DecisionVariable Variable { get; }

        public ConfigParameter(ParameterScope scope, string owner, ParameterInfo info, double min, double max, string name = null)
        {
            Scope = scope; Owner = owner; Info = info ?? throw new ArgumentNullException(nameof(info));
            Variable = info.Variable(string.IsNullOrWhiteSpace(name) ? owner + "." + info.Key : name, min, max);
        }

        /// <summary>The parameter <paramref name="key"/> of <paramref name="owner"/> in <paramref name="model"/> (its catalog
        /// entry), with the range [min, max] (null = the suggested one). Throws when the catalog does not offer it.</summary>
        public static ConfigParameter Of(SimulationConfig model, ParameterScope scope, string owner, string key,
                                         double? min = null, double? max = null, string name = null)
        {
            ParameterInfo info = null;
            var all = ModelParameterCatalog.ForAll(model, scope, owner);
            foreach (var p in all) if (p.Key == key) info = p;
            // a distribution key (transportBy.loadTime = "Constant~8") stands for its number when it has only one
            // (Rack.transportBy.loadTime, the key simulean_params lists, was "no parameter" — its catalog entry is
            // transportBy.loadTime.constantValue)
            // a distribution (a SamplerSettings): the numbers its chosen distribution uses
            List<ParameterInfo> parts = null;
            if (info == null && (scope == ParameterScope.Element || scope == ParameterScope.Downtime))
                parts = ModelParameterCatalog.For(model, scope, owner)
                    .Where(p => p.Key.StartsWith(key + ".", StringComparison.Ordinal) && p.Key.IndexOf('.', key.Length + 1) < 0
                                && p.Kind != VariableKind.Choice && p.Kind != VariableKind.Binary).ToList();
            if (parts != null && parts.Count == 1) info = parts[0];
            if (info == null)
            {
                if (parts != null && parts.Count > 1)
                    throw new ArgumentException($"'{owner}.{key}' is a distribution of {parts.Count} numbers: vary one of them, " +
                                                string.Join(", ", parts.Select(p => $"{p.Key} ({p.Label})")) + ".");
                string hint = key == "transportBy.loadTime" || key == "transportBy.unloadTime"
                    ? " The element's carriers use their own load / unload times: vary the team's (team:<Team>.loadTime / unloadTime), " +
                      "or give the element its own (transportBy.overrideLoadTimes)."
                    : SimuLean.Serialization.NameSuggestions.DidYouMean(key, all.Select(p => p.Key));
                throw new ArgumentException($"'{owner}' has no parameter '{key}'." + hint);
            }
            if (info.IsText) throw new ArgumentException(TextSettingParameter.NeedsTexts(owner + "." + key, info.CurrentText));
            return new ConfigParameter(scope, owner, info, min ?? info.Min, max ?? info.Max, name);
        }

        public override void Apply(SimulationConfig config, Candidate candidate, int index) => ApplyValues(config, candidate.Values(index));

        /// <summary>Writes one value given as text (a number, numbers separated by <c>,</c>, an option, true / false) — an
        /// <see cref="OptionsParameter"/>'s option.</summary>
        public void ApplyText(SimulationConfig config, string text) => ApplyValues(config, ParseText(text));

        /// <summary>A value given as text as the candidate values it stands for (checked: an option of a choice, numbers of
        /// the parameter's length).</summary>
        public double[] ParseText(string text)
        {
            text = (text ?? "").Trim();
            double[] v;
            if (Info.Kind == VariableKind.Choice)
            {
                int o = -1;
                for (int k = 0; k < Info.Options.Count; k++) if (string.Equals(Info.Options[k], text, StringComparison.OrdinalIgnoreCase)) o = k;
                if (o < 0) throw new FormatException($"'{Variable.Name}': '{text}' is not one of {string.Join(", ", Info.Options)}.");
                v = new double[] { o };
            }
            else if (Info.Kind == VariableKind.Binary && bool.TryParse(text, out bool b)) v = new double[] { b ? 1 : 0 };
            else
            {
                string[] parts = text.Trim('[', ']').Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                v = new double[parts.Length];
                for (int k = 0; k < parts.Length; k++)
                    if (!double.TryParse(parts[k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]))
                        throw new FormatException($"'{Variable.Name}': '{parts[k].Trim()}' is not a number." + Study.RangeHint(parts[k]));
                if (v.Length != Info.Length)
                    throw new FormatException($"'{Variable.Name}': '{text}' has {v.Length} number(s), the parameter {Info.Length}.");
            }
            return v;
        }

        private void ApplyValues(SimulationConfig config, double[] v)
        {
            var bag = ModelParameterCatalog.Bag(config, Scope, Owner);
            string key = Info.ConfigKey ?? Info.Key;
            switch (Info.Encoding)
            {
                case ValueEncoding.Integer:
                    if (Info.Length == 1) bag.Set(key, (int)v[0]);
                    else { var a = new int[v.Length]; for (int k = 0; k < a.Length; k++) a[k] = (int)v[k]; bag.Set(key, a); }
                    break;
                case ValueEncoding.Real:
                    bag.Set(key, Info.Length == 1 ? (object)v[0] : (double[])v.Clone());
                    break;
                case ValueEncoding.Bool: bag.Set(key, v[0] >= 0.5); break;
                case ValueEncoding.Choice: bag.Set(key, Info.Options[(int)v[0]]); break;
                case ValueEncoding.NumberList: bag.Set(key, string.Join("|", Texts(v))); break;
                case ValueEncoding.Members: bag.Set(key, (int)v[0]); break;
                case ValueEncoding.Property:
                {
                    string name = Info.Key.Substring(key.Length + 1);
                    var records = bag.Get(key) as List<PropertySettings>;
                    var rec = records?.Find(r => r != null && (r.Name ?? "").Trim() == name
                                                 && (r.Kind == PropertyValueKind.Number || r.Kind == PropertyValueKind.Numbers));
                    if (rec == null) throw new ArgumentException($"'{Owner}' has no number property '{name}'.");
                    if (Info.Length == 1 && rec.Kind == PropertyValueKind.Number) rec.Value = v[0];
                    else { rec.Kind = PropertyValueKind.Numbers; rec.Numbers = (double[])v.Clone(); }
                    break;
                }
            }
        }

        private static string[] Texts(double[] v)
        {
            var s = new string[v.Length];
            for (int k = 0; k < s.Length; k++) s[k] = v[k].ToString("R", CultureInfo.InvariantCulture);
            return s;
        }
    }

    /// <summary>
    /// A catalog parameter searched over a given list of values only (<c>Buffer.capacity=2|4|8</c>,
    /// <c>Bed.properties.beds=1|2|3</c>): a <see cref="VariableKind.Choice"/> of the values as given, each written as the
    /// config stores the parameter (<see cref="ConfigParameter.ApplyText"/>). A numeric parameter given options is searched
    /// over those values only, never over the range between them.
    /// </summary>
    public sealed class OptionsParameter : ModelParameter
    {
        public ConfigParameter Parameter { get; }
        public override DecisionVariable Variable { get; }

        public OptionsParameter(ConfigParameter parameter, IEnumerable<string> options, string name = null)
        {
            Parameter = parameter ?? throw new ArgumentNullException(nameof(parameter));
            var list = new List<string>();
            foreach (string o in options ?? new string[0])
                if (!string.IsNullOrWhiteSpace(o) && !list.Contains(o.Trim())) list.Add(o.Trim());
            if (list.Count == 0) throw new ArgumentException($"'{parameter.Variable.Name}' needs at least one option.");
            Variable = DecisionVariable.Choice(name ?? parameter.Variable.Name, list);
            foreach (string o in list) Parameter.ParseText(o);   // each option is a valid value (throws otherwise)
        }

        public override void Apply(SimulationConfig config, Candidate candidate, int index)
            => Parameter.ApplyText(config, candidate.Option(index));
    }

    /// <summary>
    /// A declared text setting varied by its texts (<see cref="ModelParameterCatalog.IsTextSetting"/>): a resource use's
    /// priority expression (<c>Station.processBy.priority</c> = <c>prio</c> | <c>table('T', item.k, 'p')</c>), a team's
    /// request order written in place (<c>team:Ops.requestOrder</c> = <c>priority desc</c>), a reference. A
    /// <see cref="VariableKind.Choice"/> over the texts as given; <see cref="Apply"/> writes the chosen text, and the run's
    /// build checks it (an expression that does not parse, an order over an unknown field fail there). The catalog does not
    /// offer these keys — a study or an optimiser's options name them with their texts.
    /// </summary>
    public sealed class TextSettingParameter : ModelParameter
    {
        public ParameterScope Scope { get; }
        public string Owner { get; }
        public string Key { get; }
        public override DecisionVariable Variable { get; }

        public TextSettingParameter(ParameterScope scope, string owner, string key, IEnumerable<string> texts, string name = null)
        {
            Scope = scope; Owner = owner; Key = key;
            var list = new List<string>();
            foreach (string t in texts ?? new string[0])
            {
                string text = (t ?? "").Trim();   // a level is matched trimmed (Candidate.SetText)
                if (!list.Contains(text)) list.Add(text);
            }
            if (list.Count == 0) throw new ArgumentException($"'{name ?? owner + "." + key}' needs at least one text.");
            Variable = DecisionVariable.Choice(name ?? owner + "." + key, list);
        }

        public override void Apply(SimulationConfig config, Candidate candidate, int index)
            => ModelParameterCatalog.SetTextSetting(config, Scope, Owner, Key, candidate.Option(index));

        /// <summary>True when <paramref name="key"/> of <paramref name="owner"/> is varied by texts: a text setting the
        /// catalog lists as one (<see cref="ParameterInfo.IsText"/>) or does not list at all, not one it offers as a number
        /// (a constant priority) or a choice (a named order's name), which keep their kind.</summary>
        public static bool Applies(SimulationConfig model, ParameterScope scope, string owner, string key)
        {
            if (!ModelParameterCatalog.IsTextSetting(model, scope, owner, key)) return false;
            foreach (var p in ModelParameterCatalog.ForAll(model, scope, owner)) if (p.Key == key) return p.IsText;
            return true;
        }

        /// <summary>The error for a text setting used without its texts.</summary>
        public static string NeedsTexts(string name, string current)
            => $"'{name}' is a text setting (now '{current}'): give the texts to try (a study's levels, an optimiser's options); " +
               "the numeric search does not vary it.";
    }

    /// <summary>
    /// The order of the rows of a schedule table (a source's arrivals table): the jobs are assigned to the existing time
    /// slots in the candidate's order — the <see cref="FixedColumns"/> (by default <c>Time</c>) keep their place, every
    /// other column moves with its row.
    /// </summary>
    public sealed class ScheduleSequenceParameter : ModelParameter
    {
        public string Element { get; }
        public ISet<string> FixedColumns { get; }
        public override DecisionVariable Variable { get; }

        public ScheduleSequenceParameter(string element, int rows, IEnumerable<string> fixedColumns = null, string name = null)
        {
            Element = element;
            FixedColumns = new HashSet<string>(fixedColumns ?? new[] { "Time" });
            Variable = DecisionVariable.Permutation(name ?? element + ".sequence", rows);
        }

        public override void Apply(SimulationConfig config, Candidate candidate, int index)
        {
            var e = ElementNamed(config, Element);
            var table = e.Get<Dictionary<string, List<string>>>("arrivalsTable")
                        ?? throw new ArgumentException($"Element '{Element}' has no schedule table.");
            int[] order = candidate.Vector(index);
            var result = new Dictionary<string, List<string>>();
            foreach (var kv in table)
            {
                if (kv.Value.Count != order.Length)
                    throw new ArgumentException($"Element '{Element}': the table has {kv.Value.Count} rows, the sequence {order.Length}.");
                if (FixedColumns.Contains(kv.Key)) { result[kv.Key] = new List<string>(kv.Value); continue; }
                var col = new List<string>(order.Length);
                foreach (int row in order) col.Add(kv.Value[row]);
                result[kv.Key] = col;
            }
            e.Set("arrivalsTable", result);
        }
    }

    /// <summary>One yes / no decision per row of a schedule table, written to a column (0 / 1) the model reads as a label.</summary>
    public sealed class ScheduleBinaryParameter : ModelParameter
    {
        public string Element { get; }
        public string Column { get; }
        public override DecisionVariable Variable { get; }

        public ScheduleBinaryParameter(string element, string column, int rows, string name = null)
        {
            Element = element; Column = column;
            Variable = DecisionVariable.Binary(name ?? element + "." + column, rows);
        }

        public override void Apply(SimulationConfig config, Candidate candidate, int index)
        {
            var e = ElementNamed(config, Element);
            var table = e.Get<Dictionary<string, List<string>>>("arrivalsTable")
                        ?? throw new ArgumentException($"Element '{Element}' has no schedule table.");
            int[] bits = candidate.Vector(index);
            var col = new List<string>(bits.Length);
            foreach (int bit in bits) col.Add(bit.ToString(CultureInfo.InvariantCulture));
            table[Column] = col;
        }
    }
}
