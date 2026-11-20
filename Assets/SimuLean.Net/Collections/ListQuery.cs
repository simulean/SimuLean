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

        /// <summary>Every entry, in the list's default order. One shared instance: a query is immutable, and this one has
        /// no filter (nothing cached) — not a new one at every pull without a query.</summary>
        public static ListQuery All { get; } = new ListQuery();

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
            // the query is immutable: its filter over a list is built once and reused at every pull; one holder written at once, so a query shared by two lists never pairs a list with another's filter
            var cached = filterCache;
            if (cached != null && ReferenceEquals(cached.List, list)) return cached.Filter;
            var filter = FilterOver(list);
            filterCache = new FilterOf { List = list, Filter = filter };
            return filter;
        }

        // In its own method: a closure that captures a parameter is allocated at every call, used or not.
        private Func<ListEntry, bool> FilterOver(SimList list)
            => e => { foreach (var f in filters) if (!f(list, e)) return false; return true; };

        private sealed class FilterOf { public SimList List; public Func<ListEntry, bool> Filter; }
        private FilterOf filterCache;

        internal Order Sort(SimList list, IReadOnlyList<KeyValuePair<string, bool>> defaults)
        {
            var keys = orders.Count > 0 ? (IReadOnlyList<KeyValuePair<string, bool>>)orders : defaults;
            if (keys == null || keys.Count == 0) return null;
            return new Order(this, list, keys);
        }

        /// <summary>The order of a pull of this query over one list. The list keeps it for its next pulls with the same
        /// query and keys; <see cref="Begin"/> starts each pull.</summary>
        internal sealed class Order
        {
            internal readonly ListQuery Query;
            internal readonly IReadOnlyList<KeyValuePair<string, bool>> Keys;
            private readonly SimList list;
            private readonly double[] bValues;
            private readonly Comparison<ListEntry> compare;
            private ListEntry cached;

            internal Order(ListQuery query, SimList list, IReadOnlyList<KeyValuePair<string, bool>> keys)
            {
                Query = query; Keys = keys; this.list = list;
                bValues = new double[keys.Count];
                compare = Compare;
            }

            // the best entry's values are live (utilization, distance…): read again at every pull, never kept from the last
            internal Comparison<ListEntry> Begin() { cached = null; return compare; }

            // b is the best so far in ModelList's scan: its keys are read once, not at every comparison
            private int Compare(ListEntry a, ListEntry b)
            {
                if (!ReferenceEquals(b, cached))
                {
                    for (int i = 0; i < Keys.Count; i++) bValues[i] = OrderValue(list, b, Keys[i].Key);
                    cached = b;
                }
                for (int i = 0; i < Keys.Count; i++)
                {
                    var k = Keys[i];
                    int c = OrderValue(list, a, k.Key).CompareTo(bValues[i]);
                    if (c != 0) return k.Value ? -c : c;
                }
                return 0;   // ties: ModelList keeps the earlier entry (FIFO)
            }
        }

        // An entry without the order field (or with a non-numeric value) ranks as 0, so ordering never fails.
        private static double OrderValue(SimList list, ListEntry e, string field)
        {
            double v = list.GetNumber(e, field);
            if (!double.IsNaN(v)) return v;
            if (list.GetText(e, field) == null) list.MissingField(e, field);
            return 0;
        }
    }
}
