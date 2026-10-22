using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Statistics;

namespace SimuLean.Collections
{
    /// <summary>
    /// A query over a <see cref="SimList"/>: filters (all must hold) and an order (first key wins,
    /// ties by the next key, then FIFO). Immutable: <see cref="Where(string,ListOp,object)"/> /
    /// <see cref="OrderBy"/> return new queries.
    /// </summary>
    public sealed class ListQuery
    {
        private readonly List<Func<SimList, ListEntry, bool>> filters;
        private readonly List<KeyValuePair<string, bool>> orders;   // field, descending

        public ListQuery() : this(new List<Func<SimList, ListEntry, bool>>(), new List<KeyValuePair<string, bool>>()) { }

        private ListQuery(List<Func<SimList, ListEntry, bool>> f, List<KeyValuePair<string, bool>> o) { filters = f; orders = o; }

        /// <summary>Every entry, in the list's default order.</summary>
        public static ListQuery All => new ListQuery();

        /// <summary>Entries whose <paramref name="field"/> compares to <paramref name="value"/> (numeric when both are numbers, else text).</summary>
        public ListQuery Where(string field, ListOp op, object value)
        {
            double rhs = SimList.NumberOf(value);
            string want = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            var f = new List<Func<SimList, ListEntry, bool>>(filters) { (list, e) => list.Compare(e, field, op, rhs, want) };
            return new ListQuery(f, orders);
        }

        /// <summary>Entries accepted by a custom predicate.</summary>
        public ListQuery Where(Func<ListEntry, bool> predicate)
        {
            var f = new List<Func<SimList, ListEntry, bool>>(filters) { (list, e) => predicate(e) };
            return new ListQuery(f, orders);
        }

        /// <summary>Orders by a numeric field (ascending unless <paramref name="descending"/>).</summary>
        public ListQuery OrderBy(string field, bool descending = false)
        {
            var o = new List<KeyValuePair<string, bool>>(orders) { new KeyValuePair<string, bool>(field, descending) };
            return new ListQuery(filters, o);
        }

        /// <summary>This query's filters and orders followed by <paramref name="other"/>'s (both filter sets
        /// must hold; this query's order keys first, then the other's).</summary>
        public ListQuery Then(ListQuery other)
        {
            if (other == null) return this;
            var f = new List<Func<SimList, ListEntry, bool>>(filters); f.AddRange(other.filters);
            var o = new List<KeyValuePair<string, bool>>(orders); o.AddRange(other.orders);
            return new ListQuery(f, o);
        }

        internal bool HasOrder => orders.Count > 0;

        internal IReadOnlyList<KeyValuePair<string, bool>> Keys(IReadOnlyList<KeyValuePair<string, bool>> defaults)
        {
            var keys = orders.Count > 0 ? (IReadOnlyList<KeyValuePair<string, bool>>)orders : defaults;
            return keys == null || keys.Count == 0 ? null : keys;
        }
        internal static double OrderValueOf(SimList list, ListEntry e, string field) => OrderValue(list, e, field);

        internal Func<ListEntry, bool> Filter(SimList list)
        {
            if (filters.Count == 0) return null;
            return e => { foreach (var f in filters) if (!f(list, e)) return false; return true; };
        }

        internal Comparison<ListEntry> Sort(SimList list, IReadOnlyList<KeyValuePair<string, bool>> defaults)
        {
            var keys = orders.Count > 0 ? (IReadOnlyList<KeyValuePair<string, bool>>)orders : defaults;
            if (keys == null || keys.Count == 0) return null;
            // b is the best so far in ModelList's scan: its keys are read once, not at every comparison (review2 #18)
            ListEntry cached = null;
            var bValues = new double[keys.Count];
            return (a, b) =>
            {
                if (!ReferenceEquals(b, cached))
                {
                    for (int i = 0; i < keys.Count; i++) bValues[i] = OrderValue(list, b, keys[i].Key);
                    cached = b;
                }
                for (int i = 0; i < keys.Count; i++)
                {
                    var k = keys[i];
                    int c = OrderValue(list, a, k.Key).CompareTo(bValues[i]);
                    if (c != 0) return k.Value ? -c : c;
                }
                return 0;   // ties: ModelList keeps the earlier entry (FIFO)
            };
        }

        // An entry without the order field (or with a non-numeric value) ranks as 0, so ordering never fails.
        private static double OrderValue(SimList list, ListEntry e, string field)
        {
            double v = list.GetNumber(e, field);
            return double.IsNaN(v) ? 0 : v;
        }
    }
}
