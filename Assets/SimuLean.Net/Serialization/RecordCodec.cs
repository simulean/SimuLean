using System.Collections.Generic;
using System.Text;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Flat text records <c>field|field|…</c> whose fields may themselves contain <c>|</c> (expressions, choice tables,
    /// nested records): <c>\</c> and <c>|</c> inside a field are escaped as <c>\\</c> and <c>\|</c>. Used for the call-off
    /// families and paced-line stations (one record per definition, a <c>List&lt;string&gt;</c> in the config, deep-cloned
    /// by the optimiser). Engine-pure.
    /// </summary>
    public static class RecordCodec
    {
        public static string Join(IEnumerable<string> fields)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (string f in fields)
            {
                if (!first) sb.Append('|');
                first = false;
                foreach (char ch in f ?? "")
                {
                    if (ch == '\\' || ch == '|') sb.Append('\\');
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        public static List<string> Split(string record)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            string s = record ?? "";
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
                if (ch == '|') { fields.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(ch);
            }
            fields.Add(sb.ToString());
            return fields;
        }

        /// <summary>Field <paramref name="i"/> or <paramref name="def"/> when missing / empty.</summary>
        public static string Field(IReadOnlyList<string> fields, int i, string def = "")
            => fields != null && i < fields.Count && !string.IsNullOrEmpty(fields[i]) ? fields[i] : def;
    }
}
