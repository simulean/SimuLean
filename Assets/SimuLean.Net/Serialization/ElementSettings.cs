using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace SimuLean.Serialization
{
    /// <summary>
    /// A group of typed settings: an element's settings (<see cref="ElementSettings"/>), a group inside
    /// them (<c>outputStrategy</c>, <c>processBy</c>, a flat sampler such as <c>delayStrategy</c>…) or a record of a list of
    /// records (an assembly station's <c>tasks[i]</c>). Its public fields are the settings; each one's <b>path</b> is its name
    /// with the first letter in lower case (<c>Capacity</c> → <c>capacity</c>, <c>DelayStrategy.ConstantValue</c> →
    /// <c>delayStrategy.constantValue</c>, <c>Tasks[2].Priority</c> → <c>tasks[2].priority</c>) — the path of the scene field
    /// that holds it, the catalog key and the study path (architecture §3.5, §3.19). A null field is a setting not given (its
    /// default applies). <see cref="SettingsPaths"/> reads and writes them by path; an unknown path fails naming the closest
    /// valid one.
    /// </summary>
    [Serializable]
    public abstract class SettingsGroup
    {
    }

    /// <summary>What a setting is for the catalog (<see cref="SettingAttribute.Role"/>; the role is declared on the
    /// setting itself, <see cref="SimuLean.Optimization.ParameterSchema.Of"/>).</summary>
    public enum SettingRole
    {
        /// <summary>By its type: a number or a number array is offered, a yes / no too; an enum is a choice of its names; a
        /// text or a list must declare another role (a test fails on one left undecided).</summary>
        Auto,
        /// <summary>Not a parameter: names, references, labels, tables, expressions, coordinates.</summary>
        Structural,
        /// <summary>A text that is one of <see cref="SettingAttribute.Options"/> (an enum's names) or <see cref="SettingAttribute.Choices"/>.</summary>
        Choice,
        /// <summary>A text that names one of the <c>name=definition</c> records of <see cref="SettingAttribute.OptionsFrom"/>.</summary>
        ChoiceFrom,
        /// <summary>A flat sampler's distribution name (one of <see cref="SimuLean.Optimization.ParameterSchema.SamplerTypes"/>).</summary>
        SamplerType,
        /// <summary>Numbers in a text separated by <c>|</c>: one vector parameter.</summary>
        NumberList,
        /// <summary>User properties (a list of <see cref="PropertySettings"/> records): each number (array) a parameter
        /// <c>key.name</c>.</summary>
        Properties,
        /// <summary>A data file: varied by a study with file names as its levels.</summary>
        File,
        /// <summary>An expression: a number parameter while it is a constant.</summary>
        Expression,
        /// <summary>The type name of a choice rule of the setting's site (<see cref="SettingAttribute.Logic"/>: Routing,
        /// Order = release, Dispatch): a choice among the rules of that site.</summary>
        Rule,
    }

    /// <summary>
    /// The one declaration of a setting: its catalog role, label, units, description and range. Numbers and yes /
    /// no settings need none (their type says what they are); a text or a list declares its role. The catalog's schema of an
    /// element type is generated from these (<see cref="SimuLean.Optimization.ParameterSchema.Of"/>).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SettingAttribute : Attribute
    {
        /// <summary>A setting with this catalog label.</summary>
        public SettingAttribute(string label = null) { Label = label; }
        /// <summary>Its label in the catalog and the inspector.</summary>
        public string Label { get; }
        /// <summary>What it is for the catalog (<see cref="SettingRole"/>).</summary>
        public SettingRole Role { get; set; } = SettingRole.Auto;
        /// <summary>The units of a number (<c>m</c>, <c>m/s</c>, <c>time</c> = the model's time unit…).</summary>
        public string Units { get; set; }
        /// <summary>A longer description.</summary>
        public string Description { get; set; }
        /// <summary>A choice: the enum whose names are the options.</summary>
        public Type Options { get; set; }
        /// <summary>A choice: the options as texts.</summary>
        public string[] Choices { get; set; }
        /// <summary>A choice among the names of another setting's <c>name=definition</c> records (its path).</summary>
        public string OptionsFrom { get; set; }
        /// <summary>Numbers / a number list of integers.</summary>
        public bool Integer { get; set; }
        /// <summary>The range of a number (NaN = none).</summary>
        public double Min { get; set; } = double.NaN;
        /// <summary>The largest value (NaN = none).</summary>
        public double Max { get; set; } = double.NaN;
        /// <summary>The decision site it belongs to, for the logic viewer (<see cref="SimuLean.Logic.ModelLogic"/>):
        /// <see cref="LogicSite.None"/> = not a decision; <see cref="LogicSite.Unset"/> = not declared (a test fails on a
        /// setting that looks like a decision and declares none).</summary>
        public LogicSite Logic { get; set; }
        /// <summary>How the viewer reads its text (<see cref="LogicForm"/>; Value = shown as it is, an expression role = Expression).</summary>
        public LogicForm Form { get; set; }
        /// <summary>The expression context its text is written in (an <see cref="SimuLean.Expressions.ExpressionContext"/>
        /// name: <c>admission</c>, <c>score</c>…; null = <c>any</c>).</summary>
        public string Context { get; set; }
        /// <summary>The viewer shows it at its default too, as this text (null = only when it is given another value).</summary>
        public string LogicDefault { get; set; }
        /// <summary>The values that mean its default when they are written (an extractor writes every value: <c>0</c> for a max
        /// wait, <c>KeepWaiting</c>, <c>Default</c>), separated by <c>|</c>; compared as numbers when both are numbers, else as
        /// text ignoring case. <see cref="LogicDefault"/> counts too when it is a value (<c>FIFO</c>, <c>FirstAvailable</c>).</summary>
        public string LogicDefaultValue { get; set; }
        /// <summary>The viewer shows it only when it applies: a sibling setting has one of the values, <c>strategyType=BestScore</c>
        /// (values separated by <c>|</c>; an empty value = not given), or none of them, <c>maxWait!=|0</c>; conditions separated
        /// by <c>;</c> must all hold. A setting its owning mode does not use is neither shown nor read.</summary>
        public string LogicWhen { get; set; }
    }

    /// <summary>The decision sites of a model (the logic viewer): what each setting decides.</summary>
    public enum LogicSite
    {
        /// <summary>Not declared.</summary>
        Unset,
        /// <summary>Not a decision (a time, a size, a name).</summary>
        None,
        /// <summary>Where an item goes next.</summary>
        Routing,
        /// <summary>What an element takes (an admission, a pull, a filter).</summary>
        Admission,
        /// <summary>In which order held items, tasks or blocks are released.</summary>
        Order,
        /// <summary>Which request a team or a train serves first, which member serves it, how tours are made.</summary>
        Dispatch,
        /// <summary>What a job asks of a team: members, priority, preemption, how they are acquired and released.</summary>
        Resources,
        /// <summary>Where an item is put inside an element (a stack position, a storage lane, an agenda session).</summary>
        Placement,
        /// <summary>When stock is ordered again.</summary>
        Replenishment,
        /// <summary>How long a wait may last and what happens then.</summary>
        Wait,
        /// <summary>Item labels an element sets (what the other decisions read).</summary>
        Labels,
        /// <summary>A reaction: its signal, condition and actions.</summary>
        Reaction,
    }

    /// <summary>How the logic viewer reads a decision setting's text.</summary>
    public enum LogicForm
    {
        /// <summary>Shown as it is (an option, a number, a name).</summary>
        Value,
        /// <summary>An expression in <see cref="SettingAttribute.Context"/>: what it reads is listed.</summary>
        Expression,
        /// <summary>Order keys <c>key [desc], key …</c>, each an expression in <see cref="SettingAttribute.Context"/>.</summary>
        OrderKeys,
        /// <summary>A list query <c>conditions|order</c> (<see cref="SimuLean.Collections.ListQuerySpec"/>).</summary>
        Query,
        /// <summary>An item label's name: it reads <c>item.&lt;name&gt;</c>.</summary>
        Label,
        /// <summary>Label records <c>name = expression</c>: each writes <c>item.name</c> and reads its expression.</summary>
        Assignments,
        /// <summary>A choice rule's type name: shown as <c>rule: Type (source hash)</c> with its parameters
        /// and declared reads; it writes nothing.</summary>
        Rule,
    }

    /// <summary>The values of a config by key (an element's typed settings by path; a downtime's, a reaction's or a
    /// connection's parameter bag by key): read and written alike by the code shared between them (resource uses).</summary>
    public interface IConfigValues
    {
        /// <summary>The value at <paramref name="key"/> converted to <typeparamref name="T"/>; absent / null = <paramref name="defaultValue"/>.</summary>
        T Get<T>(string key, T defaultValue = default(T));
        /// <summary>Writes a value (converted to the setting's type).</summary>
        void Set(string key, object value);
        /// <summary>True when the key has a value.</summary>
        bool Has(string key);
        /// <summary>The raw value at the key (null = absent).</summary>
        bool TryGet(string key, out object value);
        /// <summary>Clears the key (its default applies again).</summary>
        void Remove(string key);
    }

    /// <summary>
    /// Reads and writes typed settings by path: <c>capacity</c>, <c>delayStrategy.constantValue</c>,
    /// <c>tasks[0].requirements[1].count</c>. Reflection at build time and in the editor only (never during a run).
    /// </summary>
    public static class SettingsPaths
    {
        /// <summary>One setting of a group type: its path segment, type and accessors.</summary>
        public sealed class Member
        {
            /// <summary>Its path segment (the field name with a lower-case first letter).</summary>
            public string Key;
            /// <summary>Its type.</summary>
            public Type Type;
            /// <summary>The field that holds it.</summary>
            public FieldInfo Field;
            /// <summary>Its value in <paramref name="owner"/>.</summary>
            public object Get(object owner) => Field.GetValue(owner);
            /// <summary>Writes its value in <paramref name="owner"/>.</summary>
            public void Set(object owner, object value) => Field.SetValue(owner, value);
            /// <summary>A nested group (<c>processBy</c>).</summary>
            public bool IsGroup => typeof(SettingsGroup).IsAssignableFrom(Type);
            /// <summary>A list of records (<c>tasks</c>): the record type, else null.</summary>
            public Type RecordType => RecordTypeOf(Type);
        }

        private static readonly Dictionary<Type, List<Member>> cache = new Dictionary<Type, List<Member>>();

        /// <summary>The path segment of a member name: the name with its first letter in lower case.</summary>
        public static string KeyOf(string memberName)
            => string.IsNullOrEmpty(memberName) ? memberName : char.ToLowerInvariant(memberName[0]) + memberName.Substring(1);

        /// <summary>The settings of a group type (its public instance fields, base class first).</summary>
        public static IReadOnlyList<Member> Members(Type t)
        {
            lock (cache)
            {
                if (cache.TryGetValue(t, out var list)) return list;
                list = new List<Member>();
                var chain = new List<Type>();
                for (Type x = t; x != null && x != typeof(object); x = x.BaseType) chain.Insert(0, x);
                foreach (Type x in chain)
                    foreach (var f in x.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (f.IsInitOnly) continue;
                        // an element type has only the shared groups it declares (a Sink has no processBy)
                        if (x == typeof(ElementSettings) && !ElementSettings.Offers(t, f.Name)) continue;
                        list.Add(new Member { Key = KeyOf(f.Name), Type = f.FieldType, Field = f });
                    }
                cache[t] = list;
                return list;
            }
        }

        /// <summary>The setting of a group type with this path segment, or null.</summary>
        public static Member Find(Type t, string key)
        {
            foreach (var m in Members(t)) if (m.Key == key) return m;
            return null;
        }

        private static Type RecordTypeOf(Type t)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type it = t.GetGenericArguments()[0];
                if (typeof(SettingsGroup).IsAssignableFrom(it)) return it;
            }
            return null;
        }

        /// <summary>Every path a group type declares: leaves, a list of records as <c>tasks[].field</c> (for suggestions and
        /// the schema).</summary>
        public static IEnumerable<string> DeclaredPaths(Type t, string prefix = "")
        {
            foreach (var m in Members(t))
            {
                string p = prefix + m.Key;
                if (m.IsGroup) { foreach (var q in DeclaredPaths(m.Type, p + ".")) yield return q; }
                else if (m.RecordType != null) { foreach (var q in DeclaredPaths(m.RecordType, p + "[].")) yield return q; }
                else yield return p;
            }
        }

        /// <summary>Every leaf setting a group type declares with its member: <c>(path, member)</c>, a list of records as
        /// <c>tasks[].field</c>.</summary>
        public static IEnumerable<(string path, Member member)> DeclaredMembers(Type t, string prefix = "")
        {
            foreach (var m in Members(t))
            {
                string p = prefix + m.Key;
                if (m.IsGroup) { foreach (var q in DeclaredMembers(m.Type, p + ".")) yield return q; }
                else if (m.RecordType != null) { foreach (var q in DeclaredMembers(m.RecordType, p + "[].")) yield return q; }
                else yield return (p, m);
            }
        }

        /// <summary>Every setting that differs from its default, as (path, value): leaves of groups and records (a group or a
        /// record that is there is listed by its leaves, its defaults included, so it is there again when read back).
        /// Values are as stored (an <c>int?</c> as an <c>int</c>, an enum as itself, a list as the list).</summary>
        public static IEnumerable<(string path, object value)> Values(object group, string prefix = "", bool all = true)
        {
            if (group == null) yield break;
            object fresh = all ? null : Activator.CreateInstance(group.GetType());
            foreach (var m in Members(group.GetType()))
            {
                object v = m.Get(group);
                if (v == null) continue;
                if (fresh != null && !m.IsGroup && m.RecordType == null && SameValue(v, m.Get(fresh))) continue;
                string p = prefix + m.Key;
                if (m.IsGroup) { foreach (var q in Values(v, p + ".", all)) yield return q; }
                else if (m.RecordType != null)
                {
                    var list = (IList)v;
                    for (int i = 0; i < list.Count; i++)
                        foreach (var q in Values(list[i], p + "[" + i.ToString(CultureInfo.InvariantCulture) + "].", all))
                            yield return q;
                }
                else yield return (p, v);
            }
        }

        /// <summary>Two setting values alike (arrays and lists item by item).</summary>
        public static bool SameValue(object a, object b)
        {
            if (Equals(a, b)) return true;
            if (a is IList la && b is IList lb && !(a is string))
            {
                if (la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++) if (!Equals(la[i], lb[i])) return false;
                return true;
            }
            return false;
        }

        // ── paths ────────────────────────────────────────────────────────

        private struct Step
        {
            /// <summary>Its path segment (the field name with a lower-case first letter).</summary>
            public string Key;
            public int Index;   // -1 = none
        }

        private static List<Step> Parse(string path)
        {
            var steps = new List<Step>();
            foreach (string raw in (path ?? "").Split('.'))
            {
                string seg = raw.Trim();
                int index = -1;
                int b = seg.IndexOf('[');
                if (b >= 0)
                {
                    if (!seg.EndsWith("]", StringComparison.Ordinal) ||
                        !int.TryParse(seg.Substring(b + 1, seg.Length - b - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0)
                        throw new ArgumentException($"'{path}' is not a setting path (an index is written [0], [1]…).");
                    seg = seg.Substring(0, b);
                }
                if (seg.Length == 0) throw new ArgumentException($"'{path}' is not a setting path.");
                steps.Add(new Step { Key = seg, Index = index });
            }
            return steps;
        }

        /// <summary>The member a path ends at (its type), or null when the path is not declared by <paramref name="t"/>.</summary>
        public static Member Resolve(Type t, string path)
        {
            Member m = null;
            Type at = t;
            foreach (var s in Parse(path))
            {
                if (at == null) return null;
                m = Find(at, s.Key);
                if (m == null) return null;
                if (s.Index >= 0) { at = m.RecordType; if (at == null) return null; }
                else at = m.IsGroup ? m.Type : null;
            }
            return m;
        }

        /// <summary>True when <paramref name="t"/> declares the path.</summary>
        public static bool Declares(Type t, string path)
        {
            try { return Resolve(t, path) != null; }
            catch (ArgumentException) { return false; }
        }

        /// <summary>The error for a path <paramref name="t"/> does not declare: the owner, the key and the closest declared ones.</summary>
        public static ArgumentException Unknown(Type t, string path, string owner)
        {
            string first = (path ?? "").Split('.')[0];
            int b = first.IndexOf('[');
            if (b >= 0) first = first.Substring(0, b);
            if (typeof(ElementSettings).IsAssignableFrom(t) && ElementSettings.IsMissingGroup(t, first))
                return new ArgumentException($"{owner} has no '{first}' (a {t.Name.Replace("Settings", "")} has " +
                                             $"{string.Join(", ", ElementSettings.GroupKeys(t))}; '{path}').");
            var paths = new List<string>(DeclaredPaths(t));
            string norm = System.Text.RegularExpressions.Regex.Replace(path ?? "", @"\[\d+\]", "[]");
            return new ArgumentException($"{owner} has no setting '{path}'." + NameSuggestions.DidYouMean(norm, paths));
        }

        /// <summary>The value at <paramref name="path"/> (null when absent or under an absent group / record).</summary>
        public static object GetValue(object root, string path, string owner)
        {
            object at = root;
            var steps = Parse(path);
            for (int i = 0; i < steps.Count; i++)
            {
                if (at == null) return null;
                var m = Find(at.GetType(), steps[i].Key) ?? throw Unknown(root.GetType(), path, owner);
                object v = m.Get(at);
                if (steps[i].Index >= 0)
                {
                    if (m.RecordType == null) throw Unknown(root.GetType(), path, owner);
                    var list = v as IList;
                    v = list != null && steps[i].Index < list.Count ? list[steps[i].Index] : null;
                }
                else if (i < steps.Count - 1 && !m.IsGroup) throw Unknown(root.GetType(), path, owner);
                at = v;
            }
            return at;
        }

        /// <summary>Writes <paramref name="value"/> at <paramref name="path"/>, converted to the setting's type (a number
        /// from a text, a text from a number, an enum from its name…); groups on the way are created, and a list of records
        /// grows by one when the index is its count. Null clears the setting.</summary>
        public static void SetValue(object root, string path, object value, string owner)
        {
            object at = root;
            var steps = Parse(path);
            for (int i = 0; i < steps.Count; i++)
            {
                var m = Find(at.GetType(), steps[i].Key) ?? throw Unknown(root.GetType(), path, owner);
                bool last = i == steps.Count - 1;
                if (steps[i].Index >= 0)
                {
                    Type rt = m.RecordType ?? throw Unknown(root.GetType(), path, owner);
                    var list = (IList)m.Get(at);
                    if (list == null) { list = (IList)Activator.CreateInstance(m.Type); m.Set(at, list); }
                    int k = steps[i].Index;
                    if (k > list.Count)
                        throw new ArgumentException($"{owner}: '{path}' — {steps[i].Key} has {list.Count} record(s); the next one is [{list.Count}].");
                    if (k == list.Count) list.Add(Activator.CreateInstance(rt));
                    if (last)
                    {
                        if (value == null) { list.RemoveAt(k); return; }
                        if (!rt.IsInstanceOfType(value)) throw new ArgumentException($"{owner}: '{path}' is a {rt.Name} record.");
                        list[k] = value;
                        return;
                    }
                    at = list[k];
                    continue;
                }
                // null clears a setting: its default (the value of a new group of that type) applies again
                if (last) { m.Set(at, value == null ? m.Get(Activator.CreateInstance(at.GetType())) : Convert(value, m.Type, owner, path)); return; }
                if (!m.IsGroup) throw Unknown(root.GetType(), path, owner);
                object next = m.Get(at);
                if (next == null) { next = Activator.CreateInstance(m.Type); m.Set(at, next); }
                at = next;
            }
        }

        /// <summary>A value converted to a setting's type; null for a null value (a value type: its default).</summary>
        public static object Convert(object value, Type type, string owner, string path)
        {
            Type t = Nullable.GetUnderlyingType(type) ?? type;
            if (value == null) return type.IsValueType && Nullable.GetUnderlyingType(type) == null ? Activator.CreateInstance(type) : null;
            if (type.IsInstanceOfType(value)) return value;
            try
            {
                if (t == typeof(string))
                {
                    if (value is IConvertible) return System.Convert.ToString(value, CultureInfo.InvariantCulture);
                }
                else if (t.IsEnum)
                {
                    string text = System.Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
                    foreach (string n in System.Enum.GetNames(t))
                        if (string.Equals(n, text, StringComparison.OrdinalIgnoreCase)) return System.Enum.Parse(t, n);
                    throw new FormatException($"'{text}' is not {Describe(t)}");
                }
                else if (t == typeof(bool))
                {
                    if (value is string bs) return bool.Parse(bs.Trim());
                    return System.Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                }
                else if (t.IsPrimitive)
                {
                    if (value is string ns)
                    {
                        double d = double.Parse(ns.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
                        return System.Convert.ChangeType(d, t, CultureInfo.InvariantCulture);
                    }
                    if (value is IConvertible) return System.Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
                }
                else if (t == typeof(SamplerSettings) && value is string spec)
                {
                    // a distribution written as text Type~p1~p2 (a study level, simulean_set): read strictly
                    return string.IsNullOrWhiteSpace(spec) ? null : SamplerSpec.Parse(spec);
                }
                else if (t.IsArray && value is string numbers)
                {
                    // a text of numbers: a|b|c (the catalog's and a study's form), else a;b, else a,b or a b
                    char[] seps = numbers.IndexOf('|') >= 0 ? new[] { '|' } : numbers.IndexOf(';') >= 0 ? new[] { ';', ' ' } : new[] { ',', ' ' };
                    return Convert(numbers.Split(seps, StringSplitOptions.RemoveEmptyEntries), type, owner, path);
                }
                else if (t.IsArray && value is IEnumerable items && !(value is string))
                {
                    Type et = t.GetElementType();
                    var tmp = new List<object>();
                    foreach (object o in items) tmp.Add(Convert(o, et, owner, path));
                    var arr = Array.CreateInstance(et, tmp.Count);
                    for (int i = 0; i < tmp.Count; i++) arr.SetValue(tmp[i], i);
                    return arr;
                }
                else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) && value is IEnumerable seq && !(value is string))
                {
                    Type it = t.GetGenericArguments()[0];
                    var list = (IList)Activator.CreateInstance(t);
                    foreach (object o in seq) list.Add(Convert(o, it, owner, path));
                    return list;
                }
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is OverflowException)
            {
                throw new FormatException($"{owner}: '{path}' = '{value}' is not {Describe(type)} ({e.Message}).", e);
            }
            throw new FormatException($"{owner}: '{path}' is {Describe(type)}, not a {value.GetType().Name}.");
        }

        /// <summary>A value read as <typeparamref name="T"/> (the conversions of <see cref="ConfigParameters.Get{T}"/>).</summary>
        public static T As<T>(object v, string key, T defaultValue)
        {
            if (v == null) return defaultValue;
            if (v is T t) return t;
            var bag = new Dictionary<string, object> { [key] = v is Enum ? v.ToString() : v };
            return ConfigParameters.Get(bag, key, defaultValue);
        }

        private static string Describe(Type t)
        {
            Type u = Nullable.GetUnderlyingType(t) ?? t;
            if (u == typeof(string)) return "a text";
            if (u == typeof(bool)) return "true / false";
            if (u == typeof(int) || u == typeof(long)) return "a whole number";
            if (u == typeof(double) || u == typeof(float)) return "a number";
            if (u == typeof(SamplerSettings)) return "a distribution (e.g. Uniform~1~3)";
            if (u.IsEnum)
            {
                var names = Enum.GetNames(u);
                return "one of " + (names.Length < 2 ? string.Join("", names)
                                    : string.Join(", ", names, 0, names.Length - 1) + " or " + names[names.Length - 1]);
            }
            return "a " + u.Name;
        }
    }
}
