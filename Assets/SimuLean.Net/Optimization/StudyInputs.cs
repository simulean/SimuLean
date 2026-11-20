using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Data;

namespace SimuLean.Optimization
{
    /// <summary>
    /// The text inputs of a study as the agents and the runner give them (moved from the editor commands to
    /// the engine, so the editor and the out-of-editor runner read them the same way).
    /// </summary>
    public static class StudyInputs
    {
        /// <summary>
        /// The factors of <c>set</c>: JSON — an object <c>{"path": level or [levels]}</c> or an array
        /// <c>[{"path": …, "values": […]}]</c>, levels as numbers, texts or booleans, so a value may hold <c>;</c> <c>=</c>
        /// <c>|</c> — or the shorthand <c>path=v1|v2;…</c>.
        /// </summary>
        public static List<(string path, string[] levels)> FactorsOf(string set)
        {
            var list = new List<(string, string[])>();
            var json = Json(set, "factors", "{\"Buffer.capacity\": [2, 4]}");
            if (json == null)
            {
                foreach (var (path, value) in Pairs(set, "factors")) list.Add((path, value.Split('|').Select(v => v.Trim()).ToArray()));
                return list;
            }
            if (json is Dictionary<string, object> o)
                foreach (var kv in o) list.Add((kv.Key, Levels(kv.Value, kv.Key)));
            else
                foreach (Dictionary<string, object> item in (List<object>)json)
                {
                    string path = item.TryGetValue("path", out object p) ? p as string : null;
                    if (path == null) throw new ArgumentException($"factors: {MiniJson.Serialize(item)} has no \"path\".");
                    object values = item.TryGetValue("values", out object vs) ? vs : item.TryGetValue("value", out object v1) ? v1 : null;
                    if (values == null) throw new ArgumentException($"factors: '{path}' has no \"values\".");
                    list.Add((path, Levels(values, path)));
                }
            return list;
        }

        /// <summary>The metrics of <c>metrics</c>: JSON <c>{"name": "path"}</c> or <c>[{"name": …, "path": …}]</c>, or the
        /// shorthand <c>name=path;…</c>.</summary>
        public static List<(string name, string path)> MetricsOf(string metrics)
        {
            var json = Json(metrics, "metrics", "{\"out\": \"Sink.input\"}");
            if (json == null) return Pairs(metrics, "metrics").ToList();
            if (json is Dictionary<string, object> o)
                return o.Select(p => (p.Key, p.Value is string s ? s
                                       : throw new ArgumentException($"metrics: '{p.Key}' must be a path text (e.g. \"Sink.input\")."))).ToList();
            return ((List<object>)json).Cast<Dictionary<string, object>>().Select(i =>
                (i.TryGetValue("name", out object n) && n is string name ? name : throw new ArgumentException("metrics: an item has no \"name\"."),
                 i.TryGetValue("path", out object p) && p is string path ? path : throw new ArgumentException("metrics: an item has no \"path\"."))).ToList();
        }

        /// <summary>A study value as the text a factor level is: a number (invariant, round-trip), true / false or a text.</summary>
        public static string LevelText(object v, string what) => v switch
        {
            long l => ((double)l).ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            string s => s,
            _ => throw new ArgumentException($"{what}: a value is a number, a text or true / false."),
        };

        // the JSON of an argument (an object or an array of objects), null for the shorthand
        internal static object Json(string text, string what, string example)
        {
            string t = (text ?? "").Trim();
            if (!(t.StartsWith("{") || t.StartsWith("["))) return null;
            object token;
            try { token = MiniJson.Parse(t); }
            catch (FormatException e)
            {
                throw new ArgumentException($"{what}: not valid JSON ({e.Message}). Expected e.g. {example}, or the shorthand.", e);
            }
            if (token is List<object> a && a.Any(x => !(x is Dictionary<string, object>)))
                throw new ArgumentException($"{what}: a JSON array holds objects (e.g. [{{\"path\": …}}]).");
            return token;
        }

        private static string[] Levels(object v, string path)
        {
            var items = v is List<object> a ? a : new List<object> { v };
            if (items.Count == 0) throw new ArgumentException($"factors: '{path}' has no level.");
            return items.Select(x => LevelText(x, $"factors: '{path}'")).ToArray();
        }

        /// <summary>The shorthand <c>name=value;…</c> as pairs, each split at its first <c>=</c> outside <c>[…]</c>: a path or a
        /// name holds no <c>=</c> but in a label filter (<c>team:W.members[skill=weld]=1|2</c>), so the value may hold more
        /// (<c>Exit.tallies=tis=age</c>, a derived <c>late=wait&gt;=60</c>).</summary>
        public static IEnumerable<(string, string)> Pairs(string text, string what)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            foreach (string part in text.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                int eq = SplitAt(part);
                if (eq <= 0) throw new ArgumentException($"{what}: '{part}' is not 'name=value'.");
                yield return (part.Substring(0, eq).Trim(), part.Substring(eq + 1).Trim());
            }
        }

        // the first '=' outside brackets (-1 = none)
        private static int SplitAt(string part)
        {
            int depth = 0;
            for (int i = 0; i < part.Length; i++)
            {
                char c = part[i];
                if (c == '[') depth++;
                else if (c == ']') depth = Math.Max(0, depth - 1);
                else if (c == '=' && depth == 0) return i;
            }
            return -1;
        }
    }
}
