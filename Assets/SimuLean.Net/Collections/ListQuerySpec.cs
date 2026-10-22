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
            if (!string.IsNullOrEmpty(orderBy)) q = q.OrderBy(orderBy, descending);
            return q;
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
            string[] halves = text.Split('|');
            foreach (string cond in halves[0].Split(';'))
            {
                string[] t = cond.Trim().Split(new[] { ' ' }, 3);
                if (t.Length < 3 || !TryParseOp(t[1], out ListOp op))
                {
                    if (cond.Trim().Length > 0) throw new FormatException($"List query condition '{cond}': expected 'field op value'.");
                    continue;
                }
                spec.conditions.Add(new ListCondition(t[0], op, t[2]));
            }
            if (halves.Length > 1)
            {
                string o = halves[1].Trim();
                if (o.EndsWith(" desc", StringComparison.Ordinal)) { spec.descending = true; o = o.Substring(0, o.Length - 5).Trim(); }
                spec.orderBy = o;
            }
            return spec;
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
