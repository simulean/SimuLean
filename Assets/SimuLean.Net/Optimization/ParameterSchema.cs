using System;
using System.Collections.Generic;

namespace SimuLean.Optimization
{
    /// <summary>
    /// What the keys of a config mean for parameterisation — declared once per element type (its handler's
    /// <see cref="SimuLean.Serialization.IElementHandler.Schema"/>), for resource teams and for downtimes. Numbers, bools
    /// and number arrays need no declaration (their type says it); a text or a list must be declared: an option
    /// (<see cref="Choice"/>), a sampler spec <c>Type~p1~p2</c> (<see cref="Sampler"/>), numbers in a text
    /// <c>a|b|c</c> (<see cref="NumberList"/>), a list of numbers (<see cref="Numbers"/>), an expression that is a number
    /// while constant (<see cref="Expression"/>) — or <see cref="Structural"/> (names, references, tables, other
    /// expressions: not a parameter). A key left undecided fails the catalog test, so a new
    /// element type, key or property is offered — or deliberately not — from the start.
    /// </summary>
    public sealed class ParameterSchema : SimuLean.Serialization.ISettingsSchema
    {
        internal enum Rule { Structural, Choice, NumberList, Properties, File, Expression }

        internal sealed class Entry
        {
            public Rule Rule;
            public string[] Options;
            /// <summary><see cref="ChoiceFrom"/>: the key whose <c>name=…</c> records give the options.</summary>
            public string OptionsKey;
            public bool Integer;
        }

        internal sealed class Refinement
        {
            public string Label;
            public double? Min, Max;
        }

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Refinement> refinements = new Dictionary<string, Refinement>(StringComparer.Ordinal);

        /// <summary>Not parameters: names, references, labels, tables, expressions, coordinates…</summary>
        public ParameterSchema Structural(params string[] keys)
        {
            foreach (var k in keys) entries[k] = new Entry { Rule = Rule.Structural };
            return this;
        }

        /// <summary>A data file (e.g. a schedule source's table): varied by a study / experiment with
        /// file names as its levels (<c>Owner.key=A.csv|B.csv</c>, read when each run's config is built); not offered to the
        /// numeric optimisers (no option set of its own).</summary>
        public ParameterSchema File(params string[] keys)
        {
            foreach (var k in keys) entries[k] = new Entry { Rule = Rule.File };
            return this;
        }

        /// <summary>A text that names one of the alternatives listed in another key of the same config: the
        /// records of <paramref name="optionsKey"/> are <c>name=definition</c> (e.g. a milk run's named request orders,
        /// <c>R2=|distance</c>), and the options are their names. A value that is not one of the names (a definition
        /// written in place) is not offered, as for <see cref="Choice(string, string[])"/>.</summary>
        public ParameterSchema ChoiceFrom(string key, string optionsKey)
        {
            entries[key] = new Entry { Rule = Rule.Choice, OptionsKey = optionsKey };
            return this;
        }

        /// <summary>The names of records: typed records with a <c>name</c> setting (a milk run's <c>namedOrders</c>), or
        /// <c>name=definition</c> texts (a list, or one text with records separated by new lines / <c>;</c>); in order,
        /// without duplicates.</summary>
        public static string[] RecordNames(object records)
        {
            var names = new List<string>();
            if (records is System.Collections.IList typed && !(records is IEnumerable<string>))
            {
                foreach (object r in typed)
                {
                    var m = r == null ? null : SimuLean.Serialization.SettingsPaths.Find(r.GetType(), "name");
                    string n = (m?.Get(r) as string)?.Trim();
                    if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
                }
                return names.ToArray();
            }
            IEnumerable<string> list = records as IEnumerable<string>;
            if (list == null && records is string text) list = text.Split(new[] { (char)10, ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (list != null)
                foreach (string r in list)
                {
                    int eq = r?.IndexOf('=') ?? -1;
                    if (eq <= 0) continue;
                    string n = r.Substring(0, eq).Trim();
                    if (n.Length > 0 && !names.Contains(n)) names.Add(n);
                }
            return names.ToArray();
        }

        /// <summary>A text that is one of <paramref name="options"/>.</summary>
        public ParameterSchema Choice(string key, params string[] options)
        {
            entries[key] = new Entry { Rule = Rule.Choice, Options = options };
            return this;
        }

        /// <summary>A text that is the name of a value of <typeparamref name="T"/>.</summary>
        public ParameterSchema Choice<T>(string key) where T : struct, Enum => Choice(key, Enum.GetNames(typeof(T)));

        void SimuLean.Serialization.ISettingsSchema.Choice<T>(string key) => Choice<T>(key);

        /// <summary>A text of numbers separated by <c>|</c> (e.g. <c>"1|1|0.5"</c>): one vector parameter.</summary>
        public ParameterSchema NumberList(string key, bool integer = false)
        {
            entries[key] = new Entry { Rule = Rule.NumberList, Integer = integer };
            return this;
        }

        /// <summary>An expression (e.g. a resource use's priority): a number parameter while it is a constant
        /// (<c>"5"</c>; a study's level writes the number as the expression's text), not offered while it reads the item or
        /// the model (<c>"item.prio"</c> — change it in the scene).</summary>
        public ParameterSchema Expression(params string[] keys)
        {
            foreach (var k in keys) entries[k] = new Entry { Rule = Rule.Expression };
            return this;
        }

        /// <summary>User properties (<see cref="SimuLean.Serialization.PropertySettings"/> records): each number / number
        /// array is a parameter <c>key.name</c>; texts and references are not.</summary>
        public ParameterSchema Properties(string key)
        {
            entries[key] = new Entry { Rule = Rule.Properties };
            return this;
        }

        private static readonly Dictionary<Type, ParameterSchema> generated = new Dictionary<Type, ParameterSchema>();

        /// <summary>
        /// The schema of a settings class (the catalog by reflection): every declared setting's role from its
        /// <see cref="SimuLean.Serialization.SettingAttribute"/> (an enum by its names), its label and range; then the type's
        /// own refinements (<see cref="SimuLean.Serialization.ElementSettings.Refine"/>). Cached per type.
        /// </summary>
        public static ParameterSchema Of(Type settingsType)
        {
            lock (generated)
            {
                if (generated.TryGetValue(settingsType, out var cached)) return cached;
                var s = new ParameterSchema();
                // a list of records declared as user properties: its numbers are parameters by the record's name
                foreach (var m in SimuLean.Serialization.SettingsPaths.Members(settingsType))
                {
                    var la = (SimuLean.Serialization.SettingAttribute)Attribute.GetCustomAttribute(m.Field, typeof(SimuLean.Serialization.SettingAttribute));
                    if (la?.Role == SimuLean.Serialization.SettingRole.Properties) s.Properties(m.Key);
                }
                foreach (var (path, m) in SimuLean.Serialization.SettingsPaths.DeclaredMembers(settingsType))
                {
                    var a = (SimuLean.Serialization.SettingAttribute)Attribute.GetCustomAttribute(m.Field, typeof(SimuLean.Serialization.SettingAttribute));
                    Type u = Nullable.GetUnderlyingType(m.Type) ?? m.Type;
                    var role = a?.Role ?? SimuLean.Serialization.SettingRole.Auto;
                    switch (role)
                    {
                        case SimuLean.Serialization.SettingRole.Structural: s.Structural(path); break;
                        case SimuLean.Serialization.SettingRole.Choice:
                            s.Choice(path, a.Choices ?? Enum.GetNames(a.Options ?? u)); break;
                        case SimuLean.Serialization.SettingRole.ChoiceFrom: s.ChoiceFrom(path, a.OptionsFrom); break;
                        case SimuLean.Serialization.SettingRole.SamplerType: s.Choice(path, SamplerTypes); break;
                        case SimuLean.Serialization.SettingRole.NumberList: s.NumberList(path, a.Integer); break;
                        case SimuLean.Serialization.SettingRole.File: s.File(path); break;
                        case SimuLean.Serialization.SettingRole.Expression: s.Expression(path); break;
                        case SimuLean.Serialization.SettingRole.Rule: s.Choice(path, SimuLean.Rules.ChoiceRules.Names(SimuLean.Rules.ChoiceRules.SiteOf(a.Logic))); break;
                        default: if (u.IsEnum) s.Choice(path, Enum.GetNames(u)); break;
                    }
                    if (a != null && (a.Label != null || !double.IsNaN(a.Min) || !double.IsNaN(a.Max)))
                        s.Describe(path, a.Label, double.IsNaN(a.Min) ? (double?)null : a.Min, double.IsNaN(a.Max) ? (double?)null : a.Max);
                }
                if (typeof(SimuLean.Serialization.ElementSettings).IsAssignableFrom(settingsType) && !settingsType.IsAbstract)
                    ((SimuLean.Serialization.ElementSettings)Activator.CreateInstance(settingsType)).Refine(s);
                generated[settingsType] = s;
                return s;
            }
        }

        /// <summary>The declared settings of a settings class that no role decides: a text or a list left as
        /// <see cref="SimuLean.Serialization.SettingRole.Auto"/> (each must become a parameter or be declared).</summary>
        public static List<string> Undecided(Type settingsType)
        {
            var list = new List<string>();
            var schema = Of(settingsType);
            foreach (var (path, m) in SimuLean.Serialization.SettingsPaths.DeclaredMembers(settingsType))
            {
                Type u = Nullable.GetUnderlyingType(m.Type) ?? m.Type;
                bool byType = u.IsPrimitive || u.IsEnum || (u.IsArray && u.GetElementType().IsPrimitive);
                if (!byType && schema.Find(path) == null) list.Add(path);
            }
            return list;
        }

        /// <summary>A better label and / or range bounds for a parameter (all optional).</summary>
        public ParameterSchema Describe(string key, string label = null, double? min = null, double? max = null)
        {
            refinements[key] = new Refinement { Label = label, Min = min, Max = max };
            return this;
        }

        /// <summary>A new schema with the declarations of both (this one's win).</summary>
        public ParameterSchema With(ParameterSchema other)
        {
            var s = new ParameterSchema();
            foreach (var src in new[] { other, this })
            {
                if (src == null) continue;
                foreach (var kv in src.entries) s.entries[kv.Key] = kv.Value;
                foreach (var kv in src.refinements) s.refinements[kv.Key] = kv.Value;
            }
            return s;
        }

        internal Entry Find(string key) => entries.TryGetValue(key, out var e) || entries.TryGetValue(Pattern(key), out e) ? e : null;

        /// <summary>A path with its list indexes removed (<c>tasks[2].duration</c> → <c>tasks[].duration</c>): how a schema
        /// declares the settings of a list of records once for every record.</summary>
        public static string Pattern(string key) => System.Text.RegularExpressions.Regex.Replace(key ?? "", @"\[\d+\]", "[]");

        /// <summary>Every key the schema decides, in declaration order: its rule (<c>structural</c>, <c>choice</c>,
        /// <c>sampler</c>, <c>numberList</c>, <c>numbers</c>, <c>properties</c>, <c>file</c>, or <c>number</c> for a key
        /// only described — offered by its type) and a choice's options (or the key listing them).</summary>
        public IEnumerable<(string key, string rule, string[] options, string label)> Declarations
        {
            get
            {
                foreach (var kv in entries)
                {
                    string rule = kv.Value.Rule.ToString();
                    rule = char.ToLowerInvariant(rule[0]) + rule.Substring(1);
                    string[] options = kv.Value.Options ?? (kv.Value.OptionsKey != null ? new[] { "(the names in " + kv.Value.OptionsKey + ")" } : null);
                    yield return (kv.Key, rule, options, Refine(kv.Key)?.Label);
                }
                foreach (var kv in refinements)
                    if (!entries.ContainsKey(kv.Key)) yield return (kv.Key, "number", null, kv.Value.Label);
            }
        }
        /// <summary>Every declared key with its entry (the catalog's text settings left unset).</summary>
        internal IEnumerable<KeyValuePair<string, Entry>> Entries => entries;

        internal Refinement Refine(string key) => refinements.TryGetValue(key, out var r) || refinements.TryGetValue(Pattern(key), out r) ? r : null;

        // ── the shared schemas ─────────────────────────────────────────────

        /// <summary>Distribution names of the flat sampler keys (<c>delayStrategy.strategyType</c>…; see the build helpers).</summary>
        public static readonly string[] SamplerTypes =
        {
            "Constant", "Uniform", "Normal", "Exponential", "ExponentialMean", "Triangular", "Gamma", "Weibull", "LogNormal", "Beta",
            "ChiSquare", "StudentT", "FDistribution", "Poisson", "Binomial", "DiscreteUniform",
        };

        /// <summary>The settings every element may have (routing, admission, resources, location…): the schema of
        /// <see cref="SimuLean.Serialization.ElementSettings"/> (each type's is <see cref="Of"/> its settings class).</summary>
        public static ParameterSchema Common => Of(typeof(SimuLean.Serialization.ElementSettings));

        private static ParameterSchema team;

        /// <summary>The parameters of a resource team: the schema of <see cref="SimuLean.Serialization.ResourceTeamConfig"/>
        /// (its typed settings) and the catalog's <c>members</c> (the number of members).</summary>
        public static ParameterSchema Team
            => team ?? (team = Of(typeof(SimuLean.Serialization.ResourceTeamConfig))
                                   .With(new ParameterSchema().Describe(ModelParameterCatalog.TeamMembers, "Members", min: 1)));

        /// <summary>The parameters of a downtime: the schema of <see cref="SimuLean.Serialization.DowntimeSettings"/> (every kind).</summary>
        public static ParameterSchema Downtime => Of(typeof(SimuLean.Serialization.DowntimeSettings));

        /// <summary>The parameters of a reaction: the schema of <see cref="SimuLean.Serialization.ReactionSettings"/> —
        /// <c>reactionEnabled</c>, the time signals' times and, per action, its value while a plain number
        /// (<c>actions[i].value</c>), its stop duration and its number of members; expressions, names and references are
        /// structural.</summary>
        public static ParameterSchema Reaction => Of(typeof(SimuLean.Serialization.ReactionSettings));
    }
}
