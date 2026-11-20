using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The fields part of <c>simulean_types</c> / <c>simulean_params</c>:
    /// <list type="bullet">
    /// <item><b>compact</b> (the default): the type's own fields; the groups every element shares (Next Elements, routing,
    /// admission, labels, lists, resources, transport, walking…) are left out — a <c>simulean_params</c> answer keeps those
    /// not at their defaults — and counted in <c>shared</c>; a distribution is one entry, its text <c>Type~p1~p2</c>;
    /// <c>detail=full</c> lists every field, <c>fields=processBy,outputStrategy</c> only the fields under those paths;</item>
    /// <item><c>distributions</c>: every distribution's keys, one line per type, and the text form (once per answer);</item>
    /// <item><c>listItems</c>: the fields of a list's items (<c>actions[i]</c>, <c>flows[i]</c>, <c>processBy.requirements[i]</c>…),
    /// from the item type.</item>
    /// </list>
    /// </summary>
    public static partial class SimuLeanCommands
    {
        internal const string DetailHelp = "compact (default: the type's own fields, shared groups left out unless changed, a " +
                                           "distribution as one text) or full (every field); full or fields= also give the " +
                                           "inspector's tooltip of each field without a help note, under tips";

        /// <summary>The fields answer of <paramref name="c"/>: <paramref name="defaults"/> is a fresh component of its type
        /// (null = <paramref name="c"/> is one already: no shared field differs).</summary>
        private static Dictionary<string, object> FieldsView(Component c, Component defaults, string detail, string filter)
        {
            bool full = string.Equals((detail ?? "").Trim(), "full", StringComparison.OrdinalIgnoreCase);
            if (!full && !string.IsNullOrWhiteSpace(detail) && !string.Equals(detail.Trim(), "compact", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"detail: '{detail}' is not compact or full.");
            var prefixes = SplitList(filter).Select(x => x.Trim().TrimEnd('.')).Where(x => x.Length > 0).ToList();
            var all = Fields(c);
            var answer = new Dictionary<string, object>();
            List<FieldInfo> shown;
            var hidden = new Dictionary<string, int>();
            if (prefixes.Count > 0)
            {
                shown = all.Where(f => prefixes.Any(p => f.path == p || f.path.StartsWith(p + ".", StringComparison.Ordinal))).ToList();
                if (shown.Count == 0)
                    throw new ArgumentException($"fields: no field under '{string.Join(", ", prefixes)}'." +
                                                SimuLean.Serialization.NameSuggestions.DidYouMean(prefixes[0], all.Select(f => f.path.Split('.')[0]).Distinct()));
            }
            else if (full) shown = all;
            else
            {
                var defaultValues = defaults == null ? null : Fields(defaults).ToDictionary(f => f.path, f => f.value);
                var sharedNames = SharedFieldNames(c.GetType());
                shown = new List<FieldInfo>();
                foreach (var f in all)
                {
                    string top = f.path.Split('.')[0];
                    bool shared = sharedNames.Contains(top);
                    bool changed = defaultValues != null && (!defaultValues.TryGetValue(f.path, out string d) || d != f.value);
                    if (!shared || changed) shown.Add(f);
                    else hidden[top] = hidden.TryGetValue(top, out int n) ? n + 1 : 1;
                }
            }
            var listed = full ? shown : Collapsed(shown);
            answer["fields"] = listed;
            if (hidden.Count > 0)
            {
                answer["shared"] = hidden;
                answer["more"] = "Shared groups at their defaults are left out (counted in 'shared'): detail=full lists every field, " +
                                 "fields=<group>,… the fields under those paths.";
            }
            if (shown.Any(f => f.path.EndsWith("strategyType", StringComparison.Ordinal) && f.type.StartsWith("enum(Constant|", StringComparison.Ordinal)))
                answer["distributions"] = new Dictionary<string, object> { ["text"] = DistributionTextHelp, ["keys"] = DistributionKeys() };
            var help = new SortedDictionary<string, string>(StringComparer.Ordinal);
            // the inspector's tooltips, asked for (detail=full or fields=): some answers are written only
            // there; not by default, which would double a compact answer
            var tips = full || prefixes.Count > 0 ? new SortedDictionary<string, string>(StringComparer.Ordinal) : null;
            var items = ListItems(c.GetType(), shown.Where(f => f.type == "list").Select(f => f.path), help, tips);
            if (items.Count > 0) answer["listItems"] = items;
            // the notes of the fields shown ([SimuLeanHelp]: a trap and the pattern that avoids it)
            foreach (var f in shown)
                if (FieldOf(c.GetType(), f.path)?.GetCustomAttribute<SimuLeanHelpAttribute>() is SimuLeanHelpAttribute h) help[f.path] = h.Text;
            // a shared list left out at its default still says its item syntax (tallies = name = expression)
            foreach (var f in all)
                if (f.type == "list" && !help.ContainsKey(f.path) && !shown.Contains(f)
                    && FieldOf(c.GetType(), f.path)?.GetCustomAttribute<SimuLeanHelpAttribute>() is SimuLeanHelpAttribute listHelp) help[f.path] = listHelp.Text;
            if (help.Count > 0) answer["help"] = help;
            if (tips != null)
            {
                foreach (var f in listed)
                    if (!help.ContainsKey(f.path) && TipOf(FieldOf(c.GetType(), f.path)) is string tip) tips[f.path] = tip;
                if (tips.Count > 0) answer["tips"] = tips;
            }
            return answer;
        }

        /// <summary>A field's <c>[Tooltip]</c> when it has no <c>[SimuLeanHelp]</c> (which says it for agents); null for a
        /// distribution's own keys (the same text under every distribution: <c>distributions</c> gives their keys).</summary>
        private static string TipOf(System.Reflection.FieldInfo f)
        {
            if (f == null || f.IsDefined(typeof(SimuLeanHelpAttribute), true) || IsDistributionType(f.DeclaringType)) return null;
            string text = f.GetCustomAttribute<TooltipAttribute>(true)?.tooltip;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        /// <summary>The top-level fields an element type shares with every element (declared by <see cref="SElement"/>).</summary>
        private static HashSet<string> SharedFieldNames(Type type)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (!typeof(SElement).IsAssignableFrom(type)) return names;
            foreach (var f in typeof(SElement).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                names.Add(f.Name);
            return names;
        }

        // a distribution as one entry, its text (delayStrategy = 'Triangular~1~2~4'), in place of its strategyType + fields
        private static List<FieldInfo> Collapsed(List<FieldInfo> list)
        {
            var parents = list.Where(f => f.path.EndsWith("strategyType", StringComparison.Ordinal) && f.type.StartsWith("enum(Constant|", StringComparison.Ordinal))
                              .Select(f => f.path.Substring(0, f.path.Length - "strategyType".Length)).ToList();
            if (parents.Count == 0) return list;
            var result = new List<FieldInfo>();
            foreach (var f in list)
            {
                string parent = parents.FirstOrDefault(p => p.Length > 0 && f.path.StartsWith(p, StringComparison.Ordinal) && f.path.IndexOf('.', p.Length) < 0);
                if (parent == null) { result.Add(f); continue; }
                if (!f.path.EndsWith("strategyType", StringComparison.Ordinal)) continue;
                var parts = list.Where(x => x.path.StartsWith(parent, StringComparison.Ordinal) && x.path.IndexOf('.', parent.Length) < 0 && x != f).Select(x => x.value);
                string text = f.value == "Expression" ? "Expression~" + string.Join("", parts) : string.Join("~", new[] { f.value }.Concat(parts));
                result.Add(new FieldInfo { path = parent.TrimEnd('.'), type = "distribution", value = text });
            }
            return result;
        }

        /// <summary>For each list path whose items are records, the item's fields: <c>actions[i]</c> → <c>kind: enum(…), …</c>
        /// (nested lists as <c>tourTypes[i].stops[j]</c>).</summary>
        private static Dictionary<string, string> ListItems(Type root, IEnumerable<string> paths, IDictionary<string, string> help = null,
                                                            IDictionary<string, string> tips = null)
        {
            var items = new Dictionary<string, string>();
            foreach (string path in paths)
            {
                var type = FieldTypeOf(root, path);
                var item = type == null ? null : ItemType(type);
                if (item != null) AddItem(items, path + "[i]", item, 0, help, tips);
            }
            return items;
        }

        private static void AddItem(Dictionary<string, string> into, string path, Type item, int depth, IDictionary<string, string> help = null,
                                    IDictionary<string, string> tips = null)
        {
            if (!IsRecord(item) || depth > 2 || into.ContainsKey(path)) return;
            var parts = new List<string>();
            foreach (var f in Serialized(item))
            {
                if (help != null && f.GetCustomAttribute<SimuLeanHelpAttribute>() is SimuLeanHelpAttribute h) help[path + "." + f.Name] = h.Text;
                else if (tips != null && TipOf(f) is string tip) tips[path + "." + f.Name] = tip;
                var inner = ItemType(f.FieldType);
                if (inner == null && IsRecord(f.FieldType) && !IsDistributionType(f.FieldType))
                {
                    // a group inside the item: its fields by path (query.orderBy), its lists' items too
                    foreach (var g in Serialized(f.FieldType))
                    {
                        parts.Add($"{f.Name}.{g.Name}: {Describe(g.FieldType)}");
                        var gi = ItemType(g.FieldType);
                        if (gi != null) AddItem(into, $"{path}.{f.Name}.{g.Name}[{(depth == 0 ? "j" : "k")}]", gi, depth + 1);
                    }
                    continue;
                }
                parts.Add(f.Name + ": " + Describe(f.FieldType));
                if (inner != null) AddItem(into, $"{path}.{f.Name}[{(depth == 0 ? "j" : "k")}]", inner, depth + 1, help, tips);
            }
            into[path] = string.Join(", ", parts);
        }

        private static string Describe(Type t)
        {
            if (t.IsEnum) return "enum(" + string.Join("|", Enum.GetNames(t)) + ")";
            if (t == typeof(string)) return "string";
            if (t == typeof(bool)) return "boolean";
            if (t == typeof(int) || t == typeof(long)) return "integer";
            if (t == typeof(float) || t == typeof(double)) return "float";
            if (typeof(Object).IsAssignableFrom(t)) return "object(" + t.Name + ")";
            if (IsDistributionType(t)) return "distribution";
            if (ItemType(t) != null) return "list";
            if (t == typeof(Vector3)) return "vector3";
            if (t == typeof(Color)) return "color";
            return t.Name;
        }

        private static bool IsDistributionType(Type t) => typeof(RandomProcessConfig).IsAssignableFrom(t);

        private static bool IsRecord(Type t)
            => t != null && !t.IsPrimitive && !t.IsEnum && t != typeof(string) && !typeof(Object).IsAssignableFrom(t)
               && (t.IsClass || (t.IsValueType && !t.IsPrimitive)) && t.IsSerializable && t.Namespace != "UnityEngine";

        // the element type of a List<T> / T[] (null otherwise)
        private static Type ItemType(Type t)
        {
            if (t.IsArray) return t.GetElementType();
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return t.GetGenericArguments()[0];
            return null;
        }

        // the fields Unity serializes: public or [SerializeField], not static, not [NonSerialized], not hidden
        private static IEnumerable<System.Reflection.FieldInfo> Serialized(Type t)
            => t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => (f.IsPublic || f.IsDefined(typeof(SerializeField), true)) && !f.IsNotSerialized
                            && !f.IsDefined(typeof(HideInInspector), true));

        // the C# field of a serialized path (null when a part is not a field: a list item)
        private static System.Reflection.FieldInfo FieldOf(Type root, string path)
        {
            Type t = root;
            System.Reflection.FieldInfo f = null;
            foreach (string part in path.Split('.'))
            {
                if (t == null) return null;
                f = null;
                for (var x = t; x != null && f == null; x = x.BaseType)
                    f = x.GetField(part, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                t = f?.FieldType;
            }
            return f;
        }

        // the C# type of a serialized path ('pullQuery.conditions', 'processBy.requirements')
        private static Type FieldTypeOf(Type root, string path)
        {
            Type t = root;
            foreach (string part in path.Split('.'))
            {
                if (t == null) return null;
                System.Reflection.FieldInfo f = null;
                for (var x = t; x != null && f == null; x = x.BaseType)
                    f = x.GetField(part, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                t = f?.FieldType;
            }
            return t;
        }
    }
}
