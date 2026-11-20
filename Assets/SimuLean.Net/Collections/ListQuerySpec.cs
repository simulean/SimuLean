using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Statistics;

namespace SimuLean.Collections
{
    /// <summary>Comparison of a <see cref="ListCondition"/>.</summary>
    public enum ListOp { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }

    /// <summary>One filter condition (serializable: Unity inspector and headless config).</summary>
    [Serializable]
    public class ListCondition
    {
        public string field = "";
        public ListOp op = ListOp.Equal;
        public string value = "";

        public ListCondition() { }
        public ListCondition(string field, ListOp op, string value) { this.field = field; this.op = op; this.value = value; }
    }

    /// <summary>
    /// A pull query as data (serializable): all <see cref="conditions"/> must hold; the best entry by
    /// <see cref="orderBy"/> wins (empty = the list's default order, then FIFO).
    /// </summary>
    [Serializable]
    public class ListQuerySpec
    {
        public List<ListCondition> conditions = new List<ListCondition>();
        public string orderBy = "";
        public bool descending;

        public ListQuery ToQuery()
        {
            var q = new ListQuery();
            if (conditions != null)
                foreach (var c in conditions)
                    if (c != null && !string.IsNullOrEmpty(c.field)) q = q.Where(c.field, c.op, c.value);
            foreach (var k in OrderKeys()) q = q.OrderBy(k.Key, k.Value);
            return q;
        }

        /// <summary>The order keys (field, descending): one key with <see cref="descending"/>, or several, each with its own
        /// direction — "urgent desc, distance"; <c>desc</c> / <c>asc</c> in any case. The first decides, ties
        /// by the next, then FIFO. A key may be an expression (<see cref="IsExpression"/>; its commas inside parentheses or
        /// quotes do not split it).</summary>
        public List<KeyValuePair<string, bool>> OrderKeys()
        {
            var keys = new List<KeyValuePair<string, bool>>();
            if (string.IsNullOrWhiteSpace(orderBy)) return keys;
            var parts = SplitTop(orderBy, ',');
            if (parts.Count == 1)
            {
                var (key, desc) = Direction(orderBy);
                if (key.Length > 0) keys.Add(new KeyValuePair<string, bool>(key, descending || desc));
                return keys;
            }
            foreach (string part in parts)
            {
                var (key, desc) = Direction(part);
                if (key.Length > 0) keys.Add(new KeyValuePair<string, bool>(key, desc));
            }
            return keys;
        }

        // "prio DESC" -> (prio, true); "prio asc" -> (prio, false)
        private static (string key, bool desc) Direction(string part)
        {
            string key = (part ?? "").Trim();
            int sp = key.LastIndexOf(' ');
            if (sp > 0)
            {
                string dir = key.Substring(sp + 1);
                if (dir.Equals("desc", StringComparison.OrdinalIgnoreCase)) return (key.Substring(0, sp).Trim(), true);
                if (dir.Equals("asc", StringComparison.OrdinalIgnoreCase)) return (key.Substring(0, sp).Trim(), false);
            }
            return (key, false);
        }

        // a plain field: a name, or root.name (item.x, origin.x…)
        private static readonly System.Text.RegularExpressions.Regex PlainField =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$");

        /// <summary>True when an order key or a condition's field is an expression (<c>prio * 10 + age</c>,
        /// <c>has(item.code)</c>) rather than a field name (<c>prio</c>, <c>item.x</c>): in a list of items it is evaluated over
        /// the entry as a calculated field (<see cref="SimList.DefineExpressionKeys"/>).</summary>
        public static bool IsExpression(string key) => !string.IsNullOrWhiteSpace(key) && !PlainField.IsMatch(key.Trim());

        // the parts of text split at sep outside parentheses and quotes ('|' not at '||')
        internal static List<string> SplitTop(string text, char sep)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == sep && depth == 0)
                {
                    if (sep == '|' && ((i + 1 < text.Length && text[i + 1] == '|') || (i > 0 && text[i - 1] == '|'))) continue;
                    parts.Add(text.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(text.Substring(start));
            return parts;
        }

        /// <summary>Compact text form <c>field op value;…|orderBy[ desc]</c> (headless config).</summary>
        public string Encode()
        {
            var parts = new List<string>();
            if (conditions != null)
                foreach (var c in conditions)
                    if (c != null && !string.IsNullOrEmpty(c.field)) parts.Add($"{c.field} {OpText(c.op)} {c.value}");
            return string.Join(";", parts) + "|" + (orderBy ?? "") + (descending ? " desc" : "");
        }

        public static ListQuerySpec Decode(string text)
        {
            var spec = new ListQuerySpec();
            if (string.IsNullOrWhiteSpace(text)) return spec;
            var halves = SplitTop(text, '|');
            if (halves.Count > 2) throw new FormatException($"List query '{text}': one '|' separates the conditions from the order.");
            string conditions = halves[0];
            foreach (string cond in SplitTop(conditions, ';'))
            {
                if (cond.Trim().Length == 0) continue;
                var m = Condition.Match(cond);
                // 'field op value'; anything else is a condition written as an expression, true when not 0: kept as
                // 'expression != 0'
                if (!m.Success || !TryParseOp(m.Groups[2].Value, out ListOp op))
                {
                    string problem = SimuLean.Expressions.Expression.Check(cond.Trim(), SimuLean.Expressions.ExpressionContext.ListField);
                    if (problem != null)
                        throw new FormatException($"List query condition '{cond.Trim()}': expected 'field op value' (op: == != < <= > >=; a text may be " +
                                                  $"quoted, 'red') or an expression ({problem}).");
                    spec.conditions.Add(new ListCondition(cond.Trim(), ListOp.NotEqual, "0"));
                }
                else spec.conditions.Add(new ListCondition(m.Groups[1].Value, op, Unquote(m.Groups[3].Value)));
            }
            if (halves.Count == 2)
            {
                string o = halves[1].Trim();
                // several keys keep their own directions in orderBy; a single key's direction goes to descending
                if (SplitTop(o, ',').Count == 1)
                {
                    var (key, desc) = Direction(o);
                    spec.descending = desc; o = key;
                }
                spec.orderBy = o;
            }
            return spec;
        }

        // field (letters, digits, _ and . for item.x / origin.x), an operator with or without spaces, the value
        private static readonly System.Text.RegularExpressions.Regex Condition =
            new System.Text.RegularExpressions.Regex(@"^\s*([A-Za-z_][A-Za-z0-9_.]*)\s*(==|!=|<=|>=|=|<|>)\s*(.*?)\s*$");

        // a value quoted as in an expression ('red', "red", 'it''s') is the text inside
        private static string Unquote(string v)
        {
            if (v.Length >= 2 && (v[0] == '\'' || v[0] == '"') && v[v.Length - 1] == v[0])
            {
                char q = v[0];
                return v.Substring(1, v.Length - 2).Replace(new string(q, 2), new string(q, 1));
            }
            return v;
        }

        public static string OpText(ListOp op)
        {
            switch (op)
            {
                case ListOp.NotEqual: return "!=";
                case ListOp.Less: return "<";
                case ListOp.LessOrEqual: return "<=";
                case ListOp.Greater: return ">";
                case ListOp.GreaterOrEqual: return ">=";
                default: return "==";
            }
        }

        public static bool TryParseOp(string s, out ListOp op)
        {
            switch (s)
            {
                case "==": case "=": op = ListOp.Equal; return true;
                case "!=": op = ListOp.NotEqual; return true;
                case "<": op = ListOp.Less; return true;
                case "<=": op = ListOp.LessOrEqual; return true;
                case ">": op = ListOp.Greater; return true;
                case ">=": op = ListOp.GreaterOrEqual; return true;
                default: op = ListOp.Equal; return false;
            }
        }
    }
}
