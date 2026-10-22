using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using SimuLean.Serialization;

namespace SimuLean.Optimization
{
    /// <summary>What a parameter belongs to.</summary>
    public enum ParameterScope { Element, Team, Downtime, Reaction }

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
        /// <summary>A list of numbers as texts.</summary>
        Numbers,
        /// <summary>One number of a sampler spec <c>Type~p1~p2</c> (<see cref="ParameterInfo.Part"/>).</summary>
        SamplerPart,
        /// <summary>A team's number of members (extra members copy the last one).</summary>
        Members,
        /// <summary>A number (array) of the user property records (<see cref="ParameterInfo.Part"/> unused; the name after
        /// the dot of the key).</summary>
        Property,
    }

    /// <summary>
    /// Something of a model that can be a parameter, as the catalog found it in the config: the key, its kind and length
    /// (a number, a vector, a choice, a yes / no), how it is stored, its current value and a suggested range.
    /// </summary>
    public sealed class ParameterInfo
    {
        /// <summary>Unique within its owner: the config key (a sampler's number: <c>key~n</c>).</summary>
        public string Key { get; set; }
        public string Label { get; set; }
        public VariableKind Kind { get; set; } = VariableKind.Real;
        public ValueEncoding Encoding { get; set; } = ValueEncoding.Real;
        /// <summary>Values: 1, or the items of a vector.</summary>
        public int Length { get; set; } = 1;
        /// <summary>The options of a choice.</summary>
        public IReadOnlyList<string> Options { get; set; } = Array.Empty<string>();
        /// <summary>The config key that stores it (= <see cref="Key"/> except for a sampler's number).</summary>
        public string ConfigKey { get; set; }
        /// <summary>A sampler's number: its position after the type (1 = first).</summary>
        public int Part { get; set; }
        /// <summary>The current value (the first one of a vector; a choice's option index).</summary>
        public double Current { get; set; }
        /// <summary>The current value as text (all values of a vector; a choice's option).</summary>
        public string CurrentText { get; set; } = "";
        public double Min { get; set; }
        public double Max { get; set; }

        public bool IsNumber => (Kind == VariableKind.Integer || Kind == VariableKind.Real) && Length == 1;

        /// <summary>The decision variable of this parameter (numbers in [min, max]; a choice over every option; a yes / no).</summary>
        public DecisionVariable Variable(string name, double min, double max)
        {
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

        public const string TeamMembers = "members", TeamSpeed = "Speed";

        public static IReadOnlyList<ParameterInfo> ForElement(ElementConfig element, ElementHandlerRegistry registry = null)
            => element == null ? new List<ParameterInfo>()
             : Offered(Describe(ElementBag(element), SchemaOf(element, registry), null), ElementBag(element), ParameterScope.Element);

        public static IReadOnlyList<ParameterInfo> ForTeam(ResourceTeamConfig team)
            => team == null ? new List<ParameterInfo>()
             : Offered(Describe(new TeamBag(team), ParameterSchema.Team, null), new TeamBag(team), ParameterScope.Team);

        public static IReadOnlyList<ParameterInfo> ForDowntime(DowntimeConfig downtime)
            => downtime == null ? new List<ParameterInfo>() : Describe(new DictionaryBag(downtime.Parameters), ParameterSchema.Downtime, null);

        /// <summary>A reaction's parameters: <c>enabled</c>, its actions' plain numbers and stop durations.</summary>
        public static IReadOnlyList<ParameterInfo> ForReaction(ReactionConfig reaction)
            => reaction == null ? new List<ParameterInfo>() : Describe(new DictionaryBag(reaction.Parameters), ParameterSchema.Reaction, null);

        /// <summary>The parameters of <paramref name="owner"/> (an element / team name, a downtime id) in a model — those its
        /// current settings use (the chosen distribution's, the team's travel mode's), labelled by their role.</summary>
        public static IReadOnlyList<ParameterInfo> For(SimulationConfig model, ParameterScope scope, string owner)
        {
            var bag = Bag(model, scope, owner);
            return Offered(Describe(bag, Schema(model, scope, owner), null), bag, scope);
        }

        /// <summary>Every parameter of <paramref name="owner"/>, also those its current settings ignore (resolving a saved
        /// parameter keeps working when, e.g., the distribution was changed).</summary>
        internal static IReadOnlyList<ParameterInfo> ForAll(SimulationConfig model, ParameterScope scope, string owner)
            => Describe(Bag(model, scope, owner), Schema(model, scope, owner), null);

        /// <summary>The keys of a model that no schema decides ("type.key" / "team.Property" / "downtime.key"): each must
        /// become a parameter or be declared (<see cref="ParameterSchema"/>).</summary>
        public static IReadOnlyList<string> Undecided(SimulationConfig model, ElementHandlerRegistry registry = null)
        {
            var list = new List<string>();
            foreach (var e in model.Elements) Describe(ElementBag(e), SchemaOf(e, registry), k => list.Add(e.Type + "." + k));
            foreach (var t in model.ResourceTeams) Describe(new TeamBag(t), ParameterSchema.Team, k => list.Add("team." + k));
            foreach (var d in model.Downtimes) Describe(new DictionaryBag(d.Parameters), ParameterSchema.Downtime, k => list.Add("downtime." + d.Kind + "." + k));
            foreach (var r in model.Reactions) Describe(new DictionaryBag(r.Parameters), ParameterSchema.Reaction, k => list.Add("reaction." + k));
            return list;
        }

        /// <summary>Rows of an element's schedule table (0 = none): the length of sequence / per-row parameters.</summary>
        public static int ScheduleRows(ElementConfig element)
        {
            var table = element?.GetParameter<Dictionary<string, List<string>>>("arrivalsTable");
            if (table == null || table.Count == 0) return 0;
            foreach (var col in table.Values) return col.Count;
            return 0;
        }

        // ── what the current settings use, and labels by role ────────────

        /// <summary>The flat sampler keys of each distribution (<see cref="ElementBuildHelpers"/>).</summary>
        private static readonly Dictionary<string, string[]> FlatSamplerKeys = new Dictionary<string, string[]>
        {
            ["Constant"] = new[] { "constantValue" }, ["Uniform"] = new[] { "uniformMin", "uniformMax" },
            ["Normal"] = new[] { "normalMean", "normalStdDev" }, ["Exponential"] = new[] { "exponentialRate" },
            ["Triangular"] = new[] { "triangularMin", "triangularMode", "triangularMax" },
            ["Gamma"] = new[] { "gammaShape", "gammaScale" }, ["Weibull"] = new[] { "weibullShape", "weibullScale" },
            ["LogNormal"] = new[] { "logNormalMu", "logNormalSigma" }, ["Beta"] = new[] { "betaAlpha", "betaBeta", "betaMin", "betaMax" },
            ["ChiSquare"] = new[] { "chiSquareDf" }, ["StudentT"] = new[] { "studentTDf", "studentTClamp" },
            ["FDistribution"] = new[] { "fDistD1", "fDistD2" }, ["Poisson"] = new[] { "poissonLambda" },
            ["Binomial"] = new[] { "binomialN", "binomialP" }, ["DiscreteUniform"] = new[] { "discreteUniformMin", "discreteUniformMax" },
        };

        /// <summary>The role of each flat sampler (its <c>&lt;prefix&gt;StrategyType</c> key) in the catalog's labels.</summary>
        private static readonly (string typeKey, string role)[] SamplerRoles =
        {
            ("delayStrategyType", "Service time"), ("interArrivalStrategyType", "Inter-arrival time"),
        };

        /// <summary>The team properties each travel mode uses (the others are not offered).</summary>
        private static readonly Dictionary<string, string[]> TravelKeys = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = new string[0], ["Abstract"] = new string[0],
            ["Network"] = new[] { "Speed", "Traffic" }, ["Matrix"] = new[] { "Speed" }, ["Grid"] = new[] { "Speed" },
            ["Axes"] = new[] { "AxisSpeed", "AxisAccel", "AxesSequential" }, ["Calibrated"] = new[] { "Speed" },
        };
        private static readonly string[] TravelOnly = { "Speed", "Traffic", "AxisSpeed", "AxisAccel", "AxesSequential" };

        /// <summary>
        /// What is offered of a described list: an element's flat sampler keys only for its chosen distribution, labelled by
        /// the sampler's role ("Service time", "Inter-arrival time — Uniform min"); a team's travel properties only for its
        /// travel mode.
        /// </summary>
        private static List<ParameterInfo> Offered(List<ParameterInfo> list, IConfigBag bag, ParameterScope scope)
        {
            if (scope == ParameterScope.Team)
            {
                string travel = bag.Get("Travel") as string ?? "";
                if (!TravelKeys.TryGetValue(travel, out var used)) return list;   // an unknown mode: offer everything
                return list.FindAll(p => Array.IndexOf(TravelOnly, p.ConfigKey ?? p.Key) < 0 || Array.IndexOf(used, p.ConfigKey ?? p.Key) >= 0);
            }
            if (scope != ParameterScope.Element) return list;

            var flat = new HashSet<string>(StringComparer.Ordinal);
            foreach (var keys in FlatSamplerKeys.Values) flat.UnionWith(keys);
            var roleOf = new Dictionary<string, string>(StringComparer.Ordinal);   // flat key → role(s)
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (typeKey, role) in SamplerRoles)
            {
                bool present = false;
                foreach (var e in bag.Entries) if (e.key == typeKey) { present = true; break; }
                if (!present) continue;
                string type = bag.Get(typeKey) as string ?? "Constant";
                if (!FlatSamplerKeys.TryGetValue(type, out var keys)) continue;
                foreach (var k in keys)
                {
                    roleOf[k] = roleOf.TryGetValue(k, out var r) ? r + " / " + role : role;
                    types[k] = type;
                }
            }
            var offered = new List<ParameterInfo>();
            foreach (var p in list)
            {
                string key = p.ConfigKey ?? p.Key;
                if (!flat.Contains(key)) { offered.Add(p); continue; }
                if (!roleOf.TryGetValue(key, out var role)) continue;   // not used by the chosen distribution
                p.Label = types[key] == "Constant" ? role : role + " — " + p.Label;
                offered.Add(p);
            }
            return offered;
        }

        /// <summary>
        /// When <paramref name="key"/> is a flat sampler key of element <paramref name="owner"/> that its chosen
        /// distribution does not use (<c>uniformMin</c> while the service time is Constant), the distribution(s) it uses;
        /// null otherwise. A study factor on such a key changes nothing (<c>simulean_set</c> warned, a study
        /// did not).
        /// </summary>
        public static string InactiveSamplerKey(SimulationConfig model, string owner, string key)
        {
            bool flat = false;
            foreach (var keys in FlatSamplerKeys.Values) if (Array.IndexOf(keys, key) >= 0) { flat = true; break; }
            var e = flat ? model.Elements.Find(x => x.Name == owner) : null;
            if (e == null) return null;
            var used = new List<string>();
            foreach (var (typeKey, _) in SamplerRoles)
            {
                if (!e.Parameters.TryGetValue(typeKey, out object t)) continue;
                string type = t as string ?? "Constant";
                if (FlatSamplerKeys.TryGetValue(type, out var keys) && Array.IndexOf(keys, key) >= 0) return null;
                used.Add(type);
            }
            return used.Count == 0 ? null : string.Join(" / ", used);
        }

        // ── the catalog ──────────────────────────────────────────────────

        private static ParameterSchema SchemaOf(ElementConfig e, ElementHandlerRegistry registry)
        {
            registry = registry ?? ElementHandlerRegistry.CreateDefault();
            var own = registry.TryGetHandler(e.Type, out var h) ? h.Schema : null;
            return own != null ? own.With(ParameterSchema.Common) : ParameterSchema.Common;
        }

        private static ParameterSchema Schema(SimulationConfig model, ParameterScope scope, string owner)
            => scope == ParameterScope.Element ? SchemaOf(FindElement(model, owner), null)
             : scope == ParameterScope.Team ? ParameterSchema.Team
             : scope == ParameterScope.Reaction ? ParameterSchema.Reaction : ParameterSchema.Downtime;

        internal static IConfigBag Bag(SimulationConfig model, ParameterScope scope, string owner)
        {
            switch (scope)
            {
                case ParameterScope.Element: return ElementBag(FindElement(model, owner));
                case ParameterScope.Team:
                    return new TeamBag(TeamNamed(model, owner));
                case ParameterScope.Reaction:
                    return new DictionaryBag((model.Reactions.Find(r => r.Id == owner)
                                              ?? throw new ArgumentException($"Parameter: no reaction '{owner}' in the model.")).Parameters);
                default:
                    return new DictionaryBag((model.Downtimes.Find(d => d.Id == owner)
                                              ?? throw new ArgumentException($"Parameter: no downtime '{owner}' in the model.")).Parameters);
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
            throw new ArgumentException($"{what}: no element '{name}' in the model." + NameSuggestions.DidYouMean(name, model.Elements.ConvertAll(x => x.Name)));
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

        private static IConfigBag ElementBag(ElementConfig e) => new DictionaryBag(e.Parameters);

        private static List<ParameterInfo> Describe(IConfigBag bag, ParameterSchema schema, Action<string> undecided)
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
                            int i = Array.IndexOf(entry.Options, value as string);
                            if (i >= 0) list.Add(Refined(schema, new ParameterInfo
                            {
                                Key = key, Kind = VariableKind.Choice, Encoding = ValueEncoding.Choice, Options = entry.Options,
                                Current = i, CurrentText = (string)value, Min = 0, Max = entry.Options.Length - 1,
                            }));
                            break;
                        }
                        case ParameterSchema.Rule.Sampler: list.AddRange(SamplerParts(schema, key, value as string)); break;
                        case ParameterSchema.Rule.Properties: list.AddRange(PropertyParts(schema, key, value)); break;
                        case ParameterSchema.Rule.NumberList:
                        case ParameterSchema.Rule.Numbers:
                        {
                            var numbers = entry.Rule == ParameterSchema.Rule.NumberList ? SplitNumbers(value as string) : ListNumbers(value);
                            if (numbers != null && numbers.Length > 0)
                                list.Add(Vector(schema, key, numbers, entry.Integer,
                                                entry.Rule == ParameterSchema.Rule.NumberList ? ValueEncoding.NumberList : ValueEncoding.Numbers));
                            break;
                        }
                    }
                    continue;
                }
                var info = Infer(schema, key, value);
                if (info != null) list.Add(info);
                else undecided?.Invoke(key);
            }
            return list;
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

        private static readonly Dictionary<string, string[]> SamplerNames = new Dictionary<string, string[]>
        {
            ["Constant"] = new[] { "value" }, ["Uniform"] = new[] { "min", "max" }, ["Normal"] = new[] { "mean", "std dev" },
            ["Exponential"] = new[] { "rate" }, ["ExponentialMean"] = new[] { "mean" }, ["Triangular"] = new[] { "min", "mode", "max" },
            ["Gamma"] = new[] { "shape", "scale" }, ["Weibull"] = new[] { "shape", "scale" }, ["LogNormal"] = new[] { "mu", "sigma" },
            ["Beta"] = new[] { "alpha", "beta", "min", "max" }, ["Poisson"] = new[] { "lambda" }, ["Binomial"] = new[] { "n", "p" },
            ["ChiSquare"] = new[] { "degrees of freedom" }, ["StudentT"] = new[] { "degrees of freedom" },
            ["FDistribution"] = new[] { "d1", "d2" }, ["DiscreteUniform"] = new[] { "min", "max" },
        };

        // The parts of a sampler spec that are integers (SamplerSpec reads them with int.Parse): optimised as integer
        // variables, so a candidate never writes "Binomial~7.3~0.5".
        private static readonly Dictionary<string, int[]> IntegerSamplerParts = new Dictionary<string, int[]>
        {
            ["Binomial"] = new[] { 1 }, ["ChiSquare"] = new[] { 1 }, ["StudentT"] = new[] { 1 },
            ["FDistribution"] = new[] { 1, 2 }, ["DiscreteUniform"] = new[] { 1, 2 },
        };

        private static IEnumerable<ParameterInfo> SamplerParts(ParameterSchema schema, string key, string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) yield break;
            string[] p = spec.Trim().Split('~');
            if (p[0] == "Expression") yield break;   // a formula, not numbers
            SamplerNames.TryGetValue(p[0], out var names);
            IntegerSamplerParts.TryGetValue(p[0], out var integers);
            string label = schema.Refine(key)?.Label ?? Humanize(key);
            for (int k = 1; k < p.Length; k++)
            {
                if (!double.TryParse(p[k], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
                bool integer = integers != null && Array.IndexOf(integers, k) >= 0;
                var (min, max) = Range(v, integer);
                yield return new ParameterInfo
                {
                    Key = key + "~" + k, ConfigKey = key, Part = k, Label = $"{label} {p[0]} {(names != null && k <= names.Length ? names[k - 1] : "p" + k)}",
                    Kind = integer ? VariableKind.Integer : VariableKind.Real, Encoding = ValueEncoding.SamplerPart,
                    Current = v, CurrentText = Text(v), Min = min, Max = max,
                };
            }
        }

        private static IEnumerable<ParameterInfo> PropertyParts(ParameterSchema schema, string key, object records)
        {
            if (!(records is IEnumerable<string> list)) yield break;
            foreach (string rec in list)
            {
                string[] f = (rec ?? "").Split(new[] { '|' }, 3);
                if (f.Length < 3) continue;
                double[] numbers;
                if (f[0] == "d") numbers = SplitNumbers(f[2]);
                else if (f[0] == "arr-d") numbers = string.IsNullOrEmpty(f[2]) ? null : ListNumbers(f[2].Split(','));
                else continue;   // texts, references
                if (numbers == null || numbers.Length == 0) continue;
                var info = Vector(schema, key + "." + f[1], numbers, false, ValueEncoding.Property);
                info.ConfigKey = key;
                info.Label = schema.Refine(info.Key)?.Label ?? "Property " + f[1];
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

        private sealed class DictionaryBag : IConfigBag
        {
            private readonly Dictionary<string, object> d;
            public DictionaryBag(Dictionary<string, object> d) { this.d = d; }
            public IEnumerable<(string key, object value)> Entries { get { foreach (var kv in new List<KeyValuePair<string, object>>(d)) yield return (kv.Key, kv.Value); } }
            public object Get(string key) => d.TryGetValue(key, out var v) ? v : null;
            public void Set(string key, object value) => d[key] = value;
        }

        /// <summary>A team's public properties, its number of members (<c>members</c>) and the members' load / unload times.</summary>
        private sealed class TeamBag : IConfigBag
        {
            private readonly ResourceTeamConfig team;
            public TeamBag(ResourceTeamConfig team) { this.team = team; }

            public IEnumerable<(string key, object value)> Entries
            {
                get
                {
                    yield return (TeamMembers, team.Members.Count);
                    if (team.Members.Count > 0)
                    {
                        yield return ("LoadTime", team.Members[0].LoadTime);
                        yield return ("UnloadTime", team.Members[0].UnloadTime);
                    }
                    foreach (var p in typeof(ResourceTeamConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        if (p.Name != nameof(ResourceTeamConfig.Members) && p.CanRead && p.CanWrite) yield return (p.Name, p.GetValue(team));
                }
            }

            public object Get(string key)
            {
                if (key == TeamMembers) return team.Members.Count;
                if (key == "LoadTime") return team.Members.Count > 0 ? team.Members[0].LoadTime : 0.0;
                if (key == "UnloadTime") return team.Members.Count > 0 ? team.Members[0].UnloadTime : 0.0;
                return Property(key).GetValue(team);
            }

            public void Set(string key, object value)
            {
                if (key == TeamMembers) { Resize(Convert.ToInt32(value, CultureInfo.InvariantCulture)); return; }
                if (key == "LoadTime") { foreach (var m in team.Members) m.LoadTime = Convert.ToDouble(value, CultureInfo.InvariantCulture); return; }
                if (key == "UnloadTime") { foreach (var m in team.Members) m.UnloadTime = Convert.ToDouble(value, CultureInfo.InvariantCulture); return; }
                var p = Property(key);
                p.SetValue(team, Convert.ChangeType(value, p.PropertyType, CultureInfo.InvariantCulture));
            }

            private static PropertyInfo Property(string key)
                => typeof(ResourceTeamConfig).GetProperty(key) ?? throw new ArgumentException($"Resource team has no property '{key}'.");

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
            foreach (var p in ModelParameterCatalog.ForAll(model, scope, owner)) if (p.Key == key) info = p;
            if (info == null) throw new ArgumentException($"'{owner}' has no parameter '{key}'.");
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
                        throw new FormatException($"'{Variable.Name}': '{parts[k].Trim()}' is not a number.");
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
                case ValueEncoding.Numbers: bag.Set(key, new List<string>(Texts(v))); break;
                case ValueEncoding.Members: bag.Set(key, (int)v[0]); break;
                case ValueEncoding.Property:
                {
                    string name = Info.Key.Substring(key.Length + 1);
                    var records = new List<string>((bag.Get(key) as IEnumerable<string>) ?? new string[0]);
                    int i = records.FindIndex(r => r != null && (r.StartsWith("d|" + name + "|") || r.StartsWith("arr-d|" + name + "|")));
                    if (i < 0) throw new ArgumentException($"'{Owner}' has no property '{name}'.");
                    records[i] = Info.Length == 1 && records[i].StartsWith("d|") ? "d|" + name + "|" + Texts(v)[0]
                                                                                : "arr-d|" + name + "|" + string.Join(",", Texts(v));
                    bag.Set(key, records);
                    break;
                }
                case ValueEncoding.SamplerPart:
                {
                    string[] p = ((bag.Get(key) as string) ?? "").Trim().Split('~');
                    if (Info.Part >= p.Length) throw new ArgumentException($"'{Owner}.{key}': the sampler has no number {Info.Part}.");
                    p[Info.Part] = Info.Kind == VariableKind.Integer ? Math.Round(v[0]).ToString("R", CultureInfo.InvariantCulture)
                                                                     : v[0].ToString("R", CultureInfo.InvariantCulture);
                    bag.Set(key, string.Join("~", p));
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
            var table = e.GetParameter<Dictionary<string, List<string>>>("arrivalsTable")
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
            e.Parameters["arrivalsTable"] = result;
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
            var table = e.GetParameter<Dictionary<string, List<string>>>("arrivalsTable")
                        ?? throw new ArgumentException($"Element '{Element}' has no schedule table.");
            int[] bits = candidate.Vector(index);
            var col = new List<string>(bits.Length);
            foreach (int bit in bits) col.Add(bit.ToString(CultureInfo.InvariantCulture));
            table[Column] = col;
        }
    }
}
