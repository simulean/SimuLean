using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using SimuLean.Data;

namespace SimuLean.Serialization
{
    /// <summary>
    /// A model (<see cref="SimulationConfig"/>) as JSON, without Unity (the exchange format of the out-of-editor
    /// runner). The file is an envelope <c>{"format": 1, "kind": "model", "model": {…}}</c>; a format this engine does not
    /// know is refused. The DTOs are written by reflection (every public property and field, as <see cref="ConfigCloner"/>
    /// copies them), so a field added to a config travels without anyone having to remember it; on reading, each value
    /// takes the declared type of its member, and an unknown member fails naming it.
    /// <para>Every member is typed: the model, a study and a batch hold no <c>object</c> value, so there are no
    /// type tags (the untyped <c>$type</c> machinery of the parameter bags was removed); a member
    /// declared as <c>object</c>, an interface or an abstract class other than typed settings is refused.</para>
    /// </summary>
    public static class ConfigJson
    {
        /// <summary>The format this engine writes and reads. A change that an older engine cannot read bumps it.</summary>
        public const int FormatVersion = 2;

        // ── public API ───────────────────────────────────────────────────────

        public static string Serialize(SimulationConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var sb = new StringBuilder(4096);
            sb.Append("{\"format\":").Append(FormatVersion).Append(",\"kind\":\"model\",\"model\":");
            WriteTyped(sb, config, typeof(SimulationConfig), "model");
            sb.Append('}');
            return sb.ToString();
        }

        public static SimulationConfig Deserialize(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            if (!(MiniJson.Parse(json) is Dictionary<string, object> root))
                throw new FormatException("A model file is a JSON object {\"format\": …, \"kind\": \"model\", \"model\": {…}}.");
            CheckEnvelope(root, "model");
            if (!root.TryGetValue("model", out object model) || !(model is Dictionary<string, object>))
                throw new FormatException("The model file has no \"model\" object.");
            return (SimulationConfig)ReadTyped(model, typeof(SimulationConfig), "model");
        }

        /// <summary>Checks <c>format</c> (this engine's or older) and <c>kind</c> of a parsed envelope.</summary>
        public static void CheckEnvelope(Dictionary<string, object> root, string kind)
        {
            if (!root.TryGetValue("format", out object f) || !(f is long format))
                throw new FormatException("The file has no integer \"format\" (the exchange-format version).");
            if (format > FormatVersion)
                throw new FormatException($"Exchange format {format} is not supported by this engine (it reads {FormatVersion}): update SimuLean.");
            if (format < FormatVersion)
                throw new FormatException($"Exchange format {format} is older than this engine's ({FormatVersion}: elements keep typed " +
                                          "settings): export the model / run the study again with this version.");
            string k = root.TryGetValue("kind", out object kv) ? kv as string : null;
            if (k != kind) throw new FormatException($"Expected a \"{kind}\" file, found kind \"{k ?? "(none)"}\".");
        }

        /// <summary>Writes a typed value as JSON in the codec's conventions (a DTO by reflection).</summary>
        public static void Write(StringBuilder sb, object value, Type declared, string path) => WriteTyped(sb, value, declared ?? value?.GetType() ?? typeof(string), path);

        /// <summary>Reads a parsed JSON value (<see cref="MiniJson.Parse"/>) as <paramref name="type"/>.</summary>
        public static object Read(object node, Type type, string path) => ReadTyped(node, type ?? throw new ArgumentNullException(nameof(type)), path);

        // ── writing ──────────────────────────────────────────────────────────

        private static void WriteTyped(StringBuilder sb, object v, Type declared, string path)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is SettingsGroup group) { WriteSettings(sb, group, path); return; }
            if (declared == typeof(object) || declared.IsInterface || declared.IsAbstract)
                throw new NotSupportedException($"{path}: a value declared as {declared.Name} cannot be written (the exchange format is typed: every member has a concrete type).");
            Type t = Nullable.GetUnderlyingType(declared) ?? declared;
            if (t == typeof(string)) { Str(sb, (string)v); return; }
            if (t == typeof(bool)) { sb.Append((bool)v ? "true" : "false"); return; }
            if (t.IsEnum) { Str(sb, v.ToString()); return; }
            if (t == typeof(DateTime)) { Str(sb, ((DateTime)v).ToString("o", CultureInfo.InvariantCulture)); return; }
            // a double always shows a point or an exponent, so an untagged array of them reads back as double[]
            if (t == typeof(double) || t == typeof(float))
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) Str(sb, d.ToString("R", CultureInfo.InvariantCulture));   // "NaN", "Infinity"
                else Number(sb, d, path, true);
                return;
            }
            if (t.IsPrimitive || t == typeof(decimal)) { sb.Append(((IFormattable)v).ToString(null, CultureInfo.InvariantCulture)); return; }
            if (t.IsArray) { Items(sb, (IEnumerable)v, t.GetElementType(), path); return; }
            if (v is IDictionary dict) { Dict(sb, dict, DictValueType(t), path); return; }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                var args = t.GetGenericArguments();
                sb.Append("{\"Key\":");
                WriteTyped(sb, t.GetProperty("Key").GetValue(v), args[0], path + ".Key");
                sb.Append(",\"Value\":");
                WriteTyped(sb, t.GetProperty("Value").GetValue(v), args[1], path + ".Value");
                sb.Append('}');
                return;
            }
            if (v is IList list) { Items(sb, list, ListItemType(t), path); return; }
            WriteObject(sb, v, path);
        }

        private static void WriteObject(StringBuilder sb, object v, string path)
        {
            Type t = v.GetType();
            if (t.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null) == null)
                throw new NotSupportedException($"{path}: a {t.Name} cannot be written as JSON (no parameterless constructor).");
            sb.Append('{');
            bool first = true;
            foreach (var m in Members(t))
            {
                if (!first) sb.Append(',');
                first = false;
                Str(sb, m.Name);
                sb.Append(':');
                WriteTyped(sb, m.Get(v), m.Type, path + "." + m.Name);
            }
            sb.Append('}');
        }

        /// <summary>Typed settings: an object of the settings given, under their path keys (<c>capacity</c>,
        /// <c>delayStrategy</c> → <c>{"strategyType": …}</c>, <c>tasks</c> → an array of records); absent ones left out.</summary>
        private static void WriteSettings(StringBuilder sb, SettingsGroup group, string path)
        {
            sb.Append('{');
            bool first = true;
            foreach (var m in SettingsPaths.Members(group.GetType()))
            {
                object value = m.Get(group);
                if (value == null) continue;
                if (!first) sb.Append(',');
                first = false;
                Str(sb, m.Key);
                sb.Append(':');
                WriteTyped(sb, value, m.Type, path + "." + m.Key);
            }
            sb.Append('}');
        }

        private static void Items(StringBuilder sb, IEnumerable items, Type itemType, string path)
        {
            sb.Append('[');
            int k = 0;
            foreach (object it in items)
            {
                if (k > 0) sb.Append(',');
                WriteTyped(sb, it, itemType, path + "[" + k + "]");
                k++;
            }
            sb.Append(']');
        }

        private static void Dict(StringBuilder sb, IDictionary dict, Type valueType, string path)
        {
            sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry e in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                string key = Convert.ToString(e.Key, CultureInfo.InvariantCulture);
                Str(sb, key);
                sb.Append(':');
                WriteTyped(sb, e.Value, valueType, path + "." + key);
            }
            sb.Append('}');
        }

        private static void Number(StringBuilder sb, double d, string path, bool markDouble)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new NotSupportedException($"{path}: {d} cannot be written as a JSON number.");
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            sb.Append(s);
            if (markDouble && s.IndexOfAny(new[] { '.', 'E', 'e' }) < 0) sb.Append(".0");
        }

        private static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ── reading ──────────────────────────────────────────────────────────

        private static object ReadTyped(object node, Type type, string path)
        {
            if (node == null) return null;
            if (type == typeof(object) || type.IsInterface || type.IsAbstract)
                throw new FormatException($"{path}: a member declared as {type.Name} cannot be read (the exchange format is typed).");
            Type t = Nullable.GetUnderlyingType(type) ?? type;
            try
            {
                if (t == typeof(string)) return node is string s ? s : throw Wrong(path, "a string", node);
                if (t == typeof(bool)) return node is bool b ? b : throw Wrong(path, "true / false", node);
                if (t.IsEnum)
                {
                    if (node is string es && Enum.IsDefined(t, es)) return Enum.Parse(t, es);
                    if (node is long el) return Enum.ToObject(t, el);
                    throw Wrong(path, "one of " + string.Join(", ", Enum.GetNames(t)), node);
                }
                if (t == typeof(DateTime))
                    return node is string ds ? DateTime.Parse(ds, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : throw Wrong(path, "a date text", node);
                if (t.IsPrimitive || t == typeof(decimal))
                {
                    if (node is long || node is double) return Convert.ChangeType(node, t, CultureInfo.InvariantCulture);
                    if ((t == typeof(double) || t == typeof(float)) && node is string special &&
                        (special == "NaN" || special == "Infinity" || special == "-Infinity"))
                        return Convert.ChangeType(double.Parse(special, NumberStyles.Float, CultureInfo.InvariantCulture), t, CultureInfo.InvariantCulture);
                    throw Wrong(path, "a number", node);
                }
                if (t.IsArray)
                {
                    var list = node as List<object> ?? throw Wrong(path, "an array", node);
                    var arr = Array.CreateInstance(t.GetElementType(), list.Count);
                    for (int i = 0; i < list.Count; i++) arr.SetValue(ReadTyped(list[i], t.GetElementType(), path + "[" + i + "]"), i);
                    return arr;
                }
                if (typeof(IDictionary).IsAssignableFrom(t))
                {
                    var obj = node as Dictionary<string, object> ?? throw Wrong(path, "an object", node);
                    var dict = (IDictionary)Activator.CreateInstance(t);
                    Type vt = DictValueType(t);
                    foreach (var kv in obj) dict[kv.Key] = ReadTyped(kv.Value, vt, path + "." + kv.Key);
                    return dict;
                }
                if (typeof(IList).IsAssignableFrom(t))
                {
                    var list = node as List<object> ?? throw Wrong(path, "an array", node);
                    var result = (IList)Activator.CreateInstance(t);
                    Type it = ListItemType(t);
                    for (int i = 0; i < list.Count; i++) result.Add(ReadTyped(list[i], it, path + "[" + i + "]"));
                    return result;
                }
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                {
                    var obj = node as Dictionary<string, object> ?? throw Wrong(path, "an object {\"Key\", \"Value\"}", node);
                    var args = t.GetGenericArguments();
                    return Activator.CreateInstance(t,
                        ReadTyped(obj.TryGetValue("Key", out object k) ? k : null, args[0], path + ".Key"),
                        ReadTyped(obj.TryGetValue("Value", out object v) ? v : null, args[1], path + ".Value"));
                }
                if (t == typeof(ElementConfig)) return ReadElement(node, path);
                if (typeof(SettingsGroup).IsAssignableFrom(t)) return ReadSettings(node, t, path, path);
                return ReadObject(node, t, path);
            }
            catch (Exception e) when (e is InvalidCastException || e is OverflowException)
            {
                throw new FormatException($"{path}: {e.Message}", e);
            }
        }

        private static object ReadObject(object node, Type t, string path)
        {
            var obj = node as Dictionary<string, object> ?? throw Wrong(path, "an object", node);
            var ctor = t.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)
                       ?? throw new FormatException($"{path}: a {t.Name} cannot be read from JSON (no parameterless constructor).");
            object target = ctor.Invoke(null);
            var members = new Dictionary<string, Member>(StringComparer.Ordinal);
            foreach (var m in Members(t)) members[m.Name] = m;
            foreach (var kv in obj)
            {
                if (!members.TryGetValue(kv.Key, out Member m))
                    throw new FormatException($"{path}: a {t.Name} has no member \"{kv.Key}\".");
                object value = ReadTyped(kv.Value, m.Type, path + "." + kv.Key);
                m.Set(target, value);
            }
            return target;
        }

        /// <summary>An element: its type first, then its settings read as that type's settings class.</summary>
        private static ElementConfig ReadElement(object node, string path)
        {
            var obj = node as Dictionary<string, object> ?? throw Wrong(path, "an object", node);
            var e = new ElementConfig();
            foreach (var kv in obj)
            {
                switch (kv.Key)
                {
                    case nameof(ElementConfig.Id): e.Id = (string)ReadTyped(kv.Value, typeof(string), path + ".Id"); break;
                    case nameof(ElementConfig.Type): e.Type = (string)ReadTyped(kv.Value, typeof(string), path + ".Type"); break;
                    case nameof(ElementConfig.Name): e.Name = (string)ReadTyped(kv.Value, typeof(string), path + ".Name"); break;
                    case nameof(ElementConfig.Settings): break;
                    default: throw new FormatException($"{path}: an element has no member \"{kv.Key}\" (Id, Type, Name, Settings).");
                }
            }
            if (obj.TryGetValue(nameof(ElementConfig.Settings), out object settings) && settings != null)
            {
                Type st = ElementSettings.TypeFor(e.Type)
                          ?? throw new FormatException($"{path}: '{e.Type}' is not an element type of this engine.");
                e.Settings = (ElementSettings)ReadSettings(settings, st, path + ".Settings", $"Element '{e.Name ?? e.Id}' ({e.Type})");
            }
            return e;
        }

        /// <summary>Typed settings: each key a setting of the group's type (an unknown one fails naming the closest).</summary>
        private static object ReadSettings(object node, Type t, string path, string owner)
        {
            var obj = node as Dictionary<string, object> ?? throw Wrong(path, "an object", node);
            object target = Activator.CreateInstance(t);
            foreach (var kv in obj)
            {
                var m = SettingsPaths.Find(t, kv.Key);
                if (m == null)
                    throw new FormatException($"{owner}: no setting '{kv.Key}' in {path}." +
                                              NameSuggestions.DidYouMean(kv.Key, SettingsPaths.Members(t).Select(x => x.Key)));
                string p = path + "." + kv.Key;
                if (kv.Value == null) continue;
                if (m.IsGroup) m.Set(target, ReadSettings(kv.Value, m.Type, p, owner));
                else if (m.RecordType != null)
                {
                    var list = kv.Value as List<object> ?? throw Wrong(p, "an array", kv.Value);
                    var records = (IList)Activator.CreateInstance(m.Type);
                    for (int i = 0; i < list.Count; i++) records.Add(ReadSettings(list[i], m.RecordType, p + "[" + i + "]", owner));
                    m.Set(target, records);
                }
                else m.Set(target, ReadTyped(kv.Value, m.Type, p));
            }
            return target;
        }

        private static Exception Wrong(string path, string expected, object node)
            => new FormatException($"{path}: expected {expected}, found {Describe(node)}.");

        private static string Describe(object node) => node switch
        {
            string s => "\"" + s + "\"",
            List<object> _ => "an array",
            Dictionary<string, object> _ => "an object",
            bool b => b ? "true" : "false",
            _ => Convert.ToString(node, CultureInfo.InvariantCulture),
        };

        // ── types and members ────────────────────────────────────────────────

        private static Type ListItemType(Type t)
        {
            for (Type x = t; x != null; x = x.BaseType)
                if (x.IsGenericType && x.GetGenericTypeDefinition() == typeof(List<>)) return x.GetGenericArguments()[0];
            foreach (var i in t.GetInterfaces())
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>)) return i.GetGenericArguments()[0];
            return typeof(object);
        }

        private static Type DictValueType(Type t)
        {
            foreach (var i in t.GetInterfaces())
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)) return i.GetGenericArguments()[1];
            return typeof(object);
        }

        private sealed class Member
        {
            public string Name;
            public Type Type;
            public Func<object, object> Get;
            public Action<object, object> Set;
        }

        private static readonly Dictionary<Type, List<Member>> members = new Dictionary<Type, List<Member>>();

        /// <summary>The members written for a DTO: public read / write properties, public get-only collection properties
        /// (filled on reading) and public non-readonly fields — what <see cref="ConfigCloner"/> copies.</summary>
        private static List<Member> Members(Type t)
        {
            lock (members)
            {
                if (members.TryGetValue(t, out var cached)) return cached;
                var list = new List<Member>();
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0 || !p.CanRead || p.GetGetMethod() == null) continue;
                    var prop = p;
                    if (p.CanWrite && p.GetSetMethod() != null)
                        list.Add(new Member { Name = p.Name, Type = p.PropertyType, Get = prop.GetValue, Set = prop.SetValue });
                    else if (typeof(IList).IsAssignableFrom(p.PropertyType) || typeof(IDictionary).IsAssignableFrom(p.PropertyType))
                        list.Add(new Member { Name = p.Name, Type = p.PropertyType, Get = prop.GetValue, Set = (o, v) => Fill(prop.GetValue(o), v) });
                }
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.IsInitOnly) continue;
                    var field = f;
                    list.Add(new Member { Name = f.Name, Type = f.FieldType, Get = field.GetValue, Set = field.SetValue });
                }
                members[t] = list;
                return list;
            }
        }

        private static void Fill(object target, object value)
        {
            if (target is IDictionary td && value is IDictionary vd) { td.Clear(); foreach (DictionaryEntry kv in vd) td[kv.Key] = kv.Value; }
            else if (target is IList tl && value is IList vl) { tl.Clear(); foreach (var item in vl) tl.Add(item); }
        }
    }
}
