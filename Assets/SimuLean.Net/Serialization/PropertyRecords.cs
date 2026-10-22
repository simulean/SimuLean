using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimuLean.Serialization
{
    /// <summary>
    /// A <see cref="PropertyBag"/> as flat records for configs: <c>kind|name|value</c> with kind
    /// <c>d</c> (number), <c>s</c> (text — may contain '|'), <c>ref</c> (the referenced entity's name), <c>arr-d</c> /
    /// <c>arr-s</c> (comma-separated numbers / texts). References are runtime objects: they are written by name and
    /// resolved when the model is rebuilt, in a second pass once every element exists (<see cref="Apply"/> returns them).
    /// </summary>
    public static class PropertyRecords
    {
        /// <summary>Element parameter holding its property records.</summary>
        public const string Key = "properties";

        public static List<string> Encode(PropertyBag bag)
        {
            var list = new List<string>();
            if (bag == null) return list;
            foreach (string name in bag.Names)
            {
                switch (bag.KindOf(name))
                {
                    case PropertyKind.Double: list.Add(Double(name, bag.GetDouble(name))); break;
                    case PropertyKind.String: bag.TryGetString(name, out string s); list.Add(Text(name, s)); break;
                    case PropertyKind.Reference: list.Add(Ref(name, bag.GetRef(name)?.Name)); break;
                    case PropertyKind.Array:
                        var d = bag.GetArray<double>(name);
                        if (d != null) { list.Add("arr-d|" + name + "|" + string.Join(",", Array.ConvertAll(d, x => x.ToString("R", CultureInfo.InvariantCulture)))); break; }
                        var t = bag.GetArray<string>(name);
                        if (t != null) list.Add("arr-s|" + name + "|" + string.Join(",", Array.ConvertAll(t, EscapeItem)));
                        break;
                }
            }
            return list;
        }

        public static string Double(string name, double v) => "d|" + name + "|" + v.ToString("R", CultureInfo.InvariantCulture);
        public static string Text(string name, string v) => "s|" + name + "|" + (v ?? "");
        public static string Ref(string name, string target) => "ref|" + name + "|" + (target ?? "");

        // a text array item: '\' and ',' are escaped with '\' ("a,b" came back as two items)
        private static string EscapeItem(string v) => (v ?? "").Replace("\\", "\\\\").Replace(",", "\\,");

        private static string[] SplitItems(string value)
        {
            var items = new List<string>();
            var cur = new System.Text.StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\' && i + 1 < value.Length) cur.Append(value[++i]);
                else if (c == ',') { items.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            items.Add(cur.ToString());
            return items.ToArray();
        }

        /// <summary>Applies <paramref name="records"/> to <paramref name="bag"/>; references are returned as
        /// (property, target name) for the caller to resolve once every entity exists.</summary>
        public static List<(string name, string target)> Apply(IEnumerable<string> records, PropertyBag bag, string owner)
        {
            var refs = new List<(string, string)>();
            if (records == null) return refs;
            foreach (string rec in records)
            {
                if (string.IsNullOrWhiteSpace(rec)) continue;
                string[] f = rec.Split(new[] { '|' }, 3);
                if (f.Length < 3 || string.IsNullOrEmpty(f[1]))
                    throw new FormatException($"'{owner}': bad property record '{rec}' (expected 'kind|name|value').");
                string name = f[1], value = f[2];
                switch (f[0])
                {
                    case "d":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                            throw new FormatException($"'{owner}': property '{name}' is not a number ('{value}').");
                        bag.SetDouble(name, d); break;
                    case "s": bag.SetString(name, value); break;
                    case "ref": refs.Add((name, value)); break;
                    case "arr-d":
                        var parts = value.Length == 0 ? new string[0] : value.Split(',');
                        var arr = new double[parts.Length];
                        for (int i = 0; i < parts.Length; i++)
                            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out arr[i]))
                                throw new FormatException($"'{owner}': property '{name}' has a value that is not a number ('{parts[i]}').");
                        bag.SetArray(name, arr); break;
                    case "arr-s": bag.SetArray(name, value.Length == 0 ? new string[0] : SplitItems(value)); break;
                    default: throw new FormatException($"'{owner}': unknown property kind '{f[0]}' in '{rec}'.");
                }
            }
            return refs;
        }
    }
}
