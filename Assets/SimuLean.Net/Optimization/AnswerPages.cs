using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SimuLean.Data;

namespace SimuLean.Optimization
{
    /// <summary>
    /// Answers an agent's tool can read whole: an MCP client keeps an answer up to ~50 KB (as it shows it, indented) and
    /// spills a larger one to a file the agent may not read. A study's answer above <see cref="MaxChars"/> becomes a table of
    /// its first scenarios with the arguments that page through all of them (<see cref="Study"/>); nothing is dropped —
    /// every scenario's entry is one page away, every digit and every run with <c>detail=full</c>, and the exports are
    /// unchanged. <see cref="SizeOf"/> measures an answer as the client shows it.
    /// </summary>
    public static class AnswerPages
    {
        /// <summary>The characters of an answer (indented, as the client shows it) above which it is paged or cut.</summary>
        public const int MaxChars = 40000;

        /// <summary>The scenarios a page or a table gives when <c>top</c> is not given.</summary>
        public const int DefaultTop = 25;

        /// <summary>The length of <paramref name="value"/> (dictionaries, lists, scalars) written as indented JSON — two
        /// spaces per level, <c>"key": value</c> —, as an MCP client shows a command's answer.</summary>
        public static int SizeOf(object value)
        {
            var sb = new StringBuilder(4096);
            Indented(sb, value, 0);
            return sb.Length;
        }

        private static void Indented(StringBuilder sb, object v, int depth)
        {
            switch (v)
            {
                case IDictionary dict:
                    if (dict.Count == 0) { sb.Append("{}"); return; }
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        sb.Append(first ? "\n" : ",\n").Append(' ', 2 * (depth + 1));
                        first = false;
                        sb.Append(MiniJson.Serialize(Convert.ToString(e.Key, CultureInfo.InvariantCulture))).Append(": ");
                        Indented(sb, e.Value, depth + 1);
                    }
                    sb.Append('\n').Append(' ', 2 * depth).Append('}');
                    return;
                case IEnumerable list when !(v is string):
                    var items = list.Cast<object>().ToList();
                    if (items.Count == 0) { sb.Append("[]"); return; }
                    sb.Append('[');
                    for (int i = 0; i < items.Count; i++)
                    {
                        sb.Append(i == 0 ? "\n" : ",\n").Append(' ', 2 * (depth + 1));
                        Indented(sb, items[i], depth + 1);
                    }
                    sb.Append('\n').Append(' ', 2 * depth).Append(']');
                    return;
                default:
                    sb.Append(MiniJson.Serialize(v));
                    return;
            }
        }

        /// <summary>
        /// A study's answer (<see cref="StudySpec.Answer"/>) as an agent can read it. Without <paramref name="top"/>,
        /// <paramref name="offset"/> or <paramref name="sort"/> an answer within <paramref name="maxChars"/> is returned as
        /// it is; a larger one keeps <c>settings</c>, <c>errors</c> and <c>warnings</c> and replaces <c>scenarios</c> with
        /// <c>table</c> — the first <see cref="DefaultTop"/> scenarios by the first metric (ascending; plan order without
        /// metrics), each metric's mean and half-width — and <c>page</c>, which says it is cut and how to read the rest. With
        /// them, <c>scenarios</c> holds the page: the scenarios from <paramref name="offset"/> (0-based), at most
        /// <paramref name="top"/> (0 = <see cref="DefaultTop"/>; fewer when they would not fit), in plan order or by
        /// <paramref name="sort"/> (a metric; <c>-metric</c> or <c>metric desc</c> descending, a run without a value
        /// last), whole as the answer gives them; <c>page</c> gives the total, the offset, the count and the exact
        /// arguments of the next page (<paramref name="more"/>, the command that reads the pages, followed by
        /// <c>top= offset= sort=</c>). Walking the pages from offset 0 gives every scenario of the answer once.
        /// </summary>
        public static Dictionary<string, object> Study(Dictionary<string, object> answer, int top = 0, string sort = "", int offset = 0,
                                                       string more = null, int maxChars = MaxChars)
        {
            if (answer == null) throw new ArgumentNullException(nameof(answer));
            if (top < 0) throw new ArgumentException($"top: {top} — how many scenarios a page gives (> 0; 0 = {DefaultTop}).");
            if (offset < 0) throw new ArgumentException($"offset: {offset} — the first scenario of a page, from 0.");
            bool asked = top > 0 || offset > 0 || !string.IsNullOrWhiteSpace(sort);
            var scenarios = (answer.TryGetValue("scenarios", out object s) ? s as IEnumerable : null)?.Cast<object>().ToList() ?? new List<object>();
            var metrics = MetricNames(scenarios);
            var (metric, descending) = SortOf(sort, metrics);
            int whole = asked ? 0 : SizeOf(answer);
            if (!asked && whole <= maxChars) return answer;

            string reader = string.IsNullOrWhiteSpace(more) ? "the same command" : more.Trim();
            var ordered = Ordered(scenarios, metric, descending);
            string order = metric == null ? "plan order" : $"{metric} {(descending ? "descending" : "ascending")}";
            string sortArg = metric == null ? "" : $" sort={(descending ? "-" : "")}{metric}";
            var result = answer.Where(kv => kv.Key != "scenarios").ToDictionary(kv => kv.Key, kv => kv.Value);

            if (!asked)
            {
                // too large and no page asked: a table of the first ones, sorted by the first metric
                if (metric == null && metrics.Count > 0) { metric = metrics[0]; descending = false; ordered = Ordered(scenarios, metric, false); order = $"{metric} ascending"; sortArg = $" sort={metric}"; }
                for (int rows = Math.Min(DefaultTop, ordered.Count); ; rows = Math.Max(1, rows * 3 / 4))
                {
                    result["table"] = Table(ordered.Take(rows).ToList(), metrics);
                    result["page"] = new Dictionary<string, object>
                    {
                        ["scenarios"] = scenarios.Count, ["offset"] = 0, ["shown"] = rows, ["sort"] = order,
                        ["truncated"] = true,
                        ["next"] = $"{reader} top={rows} offset=0{sortArg}",
                        ["note"] = $"The answer has {scenarios.Count} scenarios (~{whole / 1000} KB, over the ~{maxChars / 1000} KB a tool answer holds): " +
                                   $"table gives the first {rows} by {order}" + (metrics.Count > 0 ? ", each metric's mean and half-width (±)" : "") + ". " +
                                   $"Every scenario's whole entry, a page at a time: next (then offset={rows}, {2 * rows}, … up to {scenarios.Count}); " +
                                   "another order: sort=<metric> (-<metric> descending); every digit, each run's end reason and metric values: detail=full; " +
                                   "every run in files: simulean_results action=export formats=csv (or json; under SimuLeanResults/).",
                    };
                    if (rows <= 1 || SizeOf(result) <= maxChars) return result;
                }
            }

            int count = Math.Min(top > 0 ? top : DefaultTop, Math.Max(0, ordered.Count - offset));
            bool shrunk = false;
            while (true)
            {
                var slice = ordered.Skip(offset).Take(count).ToList();
                result["scenarios"] = slice;
                var page = new Dictionary<string, object>
                {
                    ["scenarios"] = scenarios.Count, ["offset"] = offset, ["shown"] = slice.Count, ["sort"] = order,
                    ["truncated"] = offset > 0 || offset + slice.Count < scenarios.Count,
                };
                if (offset + slice.Count < scenarios.Count)
                    page["next"] = $"{reader} top={Math.Max(1, slice.Count)} offset={offset + slice.Count}{sortArg}";
                var notes = new List<string>();
                if (offset >= scenarios.Count && scenarios.Count > 0) notes.Add($"offset {offset} is past the last scenario ({scenarios.Count} in all, from offset 0).");
                if (shrunk) notes.Add($"{slice.Count} scenario(s) at a time fit in ~{maxChars / 1000} KB (top={(top > 0 ? top : DefaultTop)} asked).");
                if (page.ContainsKey("next")) notes.Add("The rest: next, until a page has no next.");
                result["page"] = page;
                if (notes.Count > 0) page["note"] = string.Join(" ", notes);
                if (slice.Count <= 1 || SizeOf(result) <= maxChars)
                {
                    if (slice.Count == 1 && SizeOf(result) > maxChars)
                        page["note"] = (page.TryGetValue("note", out object n) ? n + " " : "") +
                                       $"This one scenario is ~{SizeOf(result) / 1000} KB: detail=metrics (without the elements' KPIs) is smaller; " +
                                       "every number is in simulean_results action=export.";
                    return result;
                }
                count = Math.Max(1, Math.Min(slice.Count - 1, (int)(slice.Count * (double)maxChars / SizeOf(result) * 0.95)));
                shrunk = true;
            }
        }

        // the named metrics of the answer's scenarios, in their order
        private static List<string> MetricNames(List<object> scenarios)
        {
            var names = new List<string>();
            foreach (var sc in scenarios)
                if (sc is IDictionary d && d["metrics"] is IDictionary m)
                    foreach (DictionaryEntry e in m)
                    {
                        string k = Convert.ToString(e.Key, CultureInfo.InvariantCulture);
                        if (!names.Contains(k)) names.Add(k);
                    }
            return names;
        }

        /// <summary>The metric and the direction of <paramref name="sort"/>: <c>out</c>, <c>-out</c> or <c>out desc</c>
        /// (<c>out asc</c> ascending); empty = none (plan order). An unknown metric fails, naming the metrics.</summary>
        private static (string metric, bool descending) SortOf(string sort, List<string> metrics)
        {
            string t = (sort ?? "").Trim();
            if (t.Length == 0) return (null, false);
            bool desc = false;
            if (t.StartsWith("-", StringComparison.Ordinal)) { desc = true; t = t.Substring(1).Trim(); }
            else if (t.StartsWith("+", StringComparison.Ordinal)) t = t.Substring(1).Trim();
            if (t.EndsWith(" desc", StringComparison.OrdinalIgnoreCase)) { desc = true; t = t.Substring(0, t.Length - 5).Trim(); }
            else if (t.EndsWith(" asc", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 4).Trim();
            if (metrics.Count == 0)
                throw new ArgumentException($"sort: '{sort}' — the study has no named metrics to sort by (give metrics=, or page in plan order with top / offset).");
            string found = metrics.FirstOrDefault(m => m == t) ?? metrics.FirstOrDefault(m => string.Equals(m, t, StringComparison.OrdinalIgnoreCase));
            if (found == null)
                throw new ArgumentException($"sort: no metric '{t}' — one of {string.Join(", ", metrics)} (-metric or 'metric desc' descending)." +
                                            Serialization.NameSuggestions.DidYouMean(t, metrics));
            return (found, desc);
        }

        private static List<object> Ordered(List<object> scenarios, string metric, bool descending)
        {
            if (metric == null) return scenarios.ToList();
            double? Key(object sc) => sc is IDictionary d && d["metrics"] is IDictionary m && m[metric] is IDictionary iv && iv["mean"] != null
                ? Convert.ToDouble(iv["mean"], CultureInfo.InvariantCulture) : (double?)null;
            var withKey = scenarios.Select(sc => (sc, key: Key(sc))).ToList();
            var known = withKey.Where(x => x.key.HasValue);
            known = descending ? known.OrderByDescending(x => x.key.Value) : known.OrderBy(x => x.key.Value);   // stable
            return known.Concat(withKey.Where(x => !x.key.HasValue)).Select(x => x.sc).ToList();
        }

        // the table of a cut answer: a row per scenario — its name, then each metric's mean and half-width (or its completed
        // runs when there are no metrics), to the compact answer's digits
        private static Dictionary<string, object> Table(List<object> scenarios, List<string> metrics)
        {
            var columns = new List<object> { "scenario" };
            if (metrics.Count == 0) columns.Add("runs ok");
            foreach (string m in metrics) { columns.Add(m); columns.Add(m + " ±"); }
            var rows = new List<object>();
            foreach (IDictionary sc in scenarios.OfType<IDictionary>())
            {
                var row = new List<object> { sc["scenario"] };
                if (metrics.Count == 0) row.Add(sc["runs"] is IDictionary r ? r["ok"] : null);
                foreach (string m in metrics)
                {
                    var iv = sc["metrics"] is IDictionary ms && ms.Contains(m) ? ms[m] as IDictionary : null;
                    row.Add(Short(iv?["mean"]));
                    row.Add(Short(iv?["halfWidth"]));
                }
                rows.Add(row);
            }
            return new Dictionary<string, object> { ["columns"] = columns, ["rows"] = rows };
        }

        private static object Short(object v) => v is double d ? StudySpec.Significant(d, StudySpec.CompactDigits) : v;
    }
}
