using System;
using System.Collections.Generic;
using SimuLean.Expressions;

namespace SimuLean.Statistics
{
    /// <summary>
    /// Values recorded for every item that enters an element: records
    /// <c>name = expression</c> over the item and the element — <c>time_in_system = age</c>,
    /// <c>to_decision = t_decision - created</c>, <c>within_target = age &lt;= 30</c> (its mean is the share) — optionally
    /// split by a label (<c>priority</c>: one set per value). Each gives mean, std, min, max, P50, P90 and count
    /// (<see cref="SimuLean.Results.ResultMetrics.Tally"/>). Driven by the link layer: <see cref="ElementStatsCollector.OnEntry"/>
    /// records them, so they follow the item flow like the other element statistics; reset with them (warm-up included).
    /// </summary>
    public sealed class ItemTallies
    {
        private readonly string[] names;
        private readonly Expression[] values;
        private readonly string splitBy;
        private readonly TallySamples[] all;
        private readonly Dictionary<string, TallySamples>[] bySplit;

        private ItemTallies(string[] names, Expression[] values, string splitBy)
        {
            this.names = names; this.values = values; this.splitBy = string.IsNullOrWhiteSpace(splitBy) ? null : splitBy.Trim();
            all = new TallySamples[names.Length];
            bySplit = new Dictionary<string, TallySamples>[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                all[i] = new TallySamples();
                if (this.splitBy != null) bySplit[i] = new Dictionary<string, TallySamples>();
            }
        }

        /// <summary>The tallies' names, in order.</summary>
        public IReadOnlyList<string> Names => names;
        /// <summary>The label the values are split by (null = none).</summary>
        public string SplitBy => splitBy;

        /// <summary>Parses <c>name = expression</c> records (blank ones skipped); null when there are none. A malformed
        /// record fails naming <paramref name="owner"/>.</summary>
        public static ItemTallies Parse(IEnumerable<string> records, string splitBy, string owner)
        {
            if (records == null) return null;
            var n = new List<string>();
            var v = new List<Expression>();
            foreach (string raw in records)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                int eq = raw.IndexOf('=');
                if (eq <= 0 || (eq + 1 < raw.Length && raw[eq + 1] == '='))
                    throw new FormatException($"Element '{owner}': statistic record '{raw}' is not 'name = expression'.");
                string name = raw.Substring(0, eq).Trim(), expr = raw.Substring(eq + 1).Trim();
                if (name.Length == 0 || expr.Length == 0 || name.IndexOf(':') >= 0)
                    throw new FormatException($"Element '{owner}': statistic record '{raw}' is not 'name = expression' (no ':' in the name).");
                string error = Expression.Check(expr);
                if (error != null) throw new FormatException($"Element '{owner}': statistic '{name}': {error}");
                n.Add(name);
                v.Add(Expression.Parse(expr));
            }
            return n.Count == 0 ? null : new ItemTallies(n.ToArray(), v.ToArray(), splitBy);
        }

        [ThreadStatic] private static EntityScope scope;
        private SimuLean.Stochastics.Streams.IRandomEngine random;

        /// <summary>Records every tally for <paramref name="item"/> (a missing value is skipped). Random functions draw from
        /// the tallies' own stream <c>element/tally</c>, not the element's (review2 #27: observing changed the model).</summary>
        public void Record(Item item, Element element)
        {
            var s = (scope ?? (scope = new EntityScope())).Set(item, element);
            if (random == null && element?.GetSimClock() != null) random = element.GetSimClock().Context.CreateRandomStream(element.GetName(), "tally");
            s.RandomStream = random;
            string split = null;
            if (splitBy != null)
            {
                var sv = EntityScope.OfItem(item, splitBy, s.Now);
                split = sv.Missing ? "" : sv.IsText ? sv.Text : sv.AsNumber().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            for (int i = 0; i < names.Length; i++)
            {
                ExprValue v = values[i].Evaluate(s);
                if (v.Missing || v.IsText) continue;
                double x = v.AsNumber();
                if (double.IsNaN(x)) continue;
                all[i].Add(x);
                if (split != null)
                {
                    if (!bySplit[i].TryGetValue(split, out var t)) bySplit[i][split] = t = new TallySamples();
                    t.Add(x);
                }
            }
            s.Set(null, null);
            s.RandomStream = null;
        }

        public void Reset()
        {
            for (int i = 0; i < names.Length; i++) { all[i].Clear(); bySplit[i]?.Clear(); }
        }

        /// <summary>The samples of tally <paramref name="name"/> (<paramref name="split"/> = a value of the split label, or
        /// null for all items); null when unknown.</summary>
        public TallySamples Get(string name, string split = null)
        {
            int i = Array.IndexOf(names, name);
            if (i < 0) return null;
            if (split == null) return all[i];
            return bySplit[i] != null && bySplit[i].TryGetValue(split, out var t) ? t : null;
        }

        /// <summary>Every tally (name, split value or null, samples), splits in ordinal order.</summary>
        public IEnumerable<(string name, string split, TallySamples samples)> All()
        {
            for (int i = 0; i < names.Length; i++)
            {
                yield return (names[i], null, all[i]);
                if (bySplit[i] == null) continue;
                var keys = new List<string>(bySplit[i].Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (var k in keys) yield return (names[i], k, bySplit[i][k]);
            }
        }
    }

    /// <summary>The values of one tally: kept (percentiles need them), summarised on demand.</summary>
    public sealed class TallySamples
    {
        private readonly List<double> values = new List<double>();
        private double sum;
        private bool sorted = true;

        public int Count => values.Count;
        public void Add(double x)
        {
            if (values.Count > 0 && x < values[values.Count - 1]) sorted = false;
            values.Add(x); sum += x;
        }
        public void Clear() { values.Clear(); sum = 0; sorted = true; }

        public double Mean => values.Count > 0 ? sum / values.Count : double.NaN;
        /// <summary>Sample standard deviation (0 with one value).</summary>
        public double Std
        {
            get
            {
                int n = values.Count;
                if (n < 2) return n == 1 ? 0 : double.NaN;
                double m = sum / n, ss = 0;
                foreach (double x in values) ss += (x - m) * (x - m);   // two passes: no cancellation on long times
                return Math.Sqrt(ss / (n - 1));
            }
        }
        public double Min { get { Sort(); return values.Count > 0 ? values[0] : double.NaN; } }
        public double Max { get { Sort(); return values.Count > 0 ? values[values.Count - 1] : double.NaN; } }

        /// <summary>The <paramref name="p"/> quantile (0–1), linear between order statistics (type 7, as R / NumPy).</summary>
        public double Percentile(double p)
        {
            int n = values.Count;
            if (n == 0) return double.NaN;
            Sort();
            double h = (n - 1) * Math.Max(0, Math.Min(1, p));
            int lo = (int)Math.Floor(h);
            int hi = Math.Min(n - 1, lo + 1);
            return values[lo] + (h - lo) * (values[hi] - values[lo]);
        }

        private void Sort()
        {
            if (sorted) return;
            values.Sort();
            sorted = true;
        }
    }
}
