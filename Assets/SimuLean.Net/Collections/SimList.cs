using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Statistics;

namespace SimuLean.Collections
{
    /// <summary>
    /// A FlexSim-style list: a model-wide rendezvous where elements and
    /// model logic <b>push</b> entries (items they hold — the item does not move —, or values) and
    /// <b>pull</b> them with a <see cref="ListQuery"/>. A pull with no match becomes a back-order and is
    /// delivered by a zero-delay event at the time a matching entry is pushed (so the pusher finishes
    /// its own event first). Built on <see cref="ModelList{T}"/>; deterministic.
    /// <para>Fields of an entry, for filters and orders: the item's labels (numeric or text), the value
    /// entry's push-time fields, and the built-ins <c>age</c> (now − push time), <c>pushTime</c>,
    /// <c>itemPriority</c>, <c>owner</c> (owner name) and <c>type</c> (item type), plus named calculated
    /// fields (<see cref="DefineField"/>). Field access is the only place to change when items get a
    /// property bag.</para>
    /// <para>Statistics: content (time-weighted), waiting time per pulled entry, pushes / pulls,
    /// pending pulls (time-weighted).</para>
    /// </summary>
    public sealed class SimList
    {
        private readonly ModelList<ListEntry> inner = new ModelList<ListEntry>();
        private readonly Dictionary<string, Func<ListEntry, double>> calculated = new Dictionary<string, Func<ListEntry, double>>();
        private readonly Dictionary<string, string> aliases = new Dictionary<string, string>();   // field → label / built-in it reads
        private readonly List<ListFieldSpec> fieldSpecs = new List<ListFieldSpec>();
        private readonly Dictionary<string, ListFieldStats> fieldStats = new Dictionary<string, ListFieldStats>();
        private int evalDepth;

        /// <summary>Built-in field names, readable in every list.</summary>
        public static readonly string[] BuiltInFields = { "age", "pushTime", "itemPriority", "owner", "type" };
        private readonly List<KeyValuePair<string, bool>> defaultOrder = new List<KeyValuePair<string, bool>>();
        private long seq;

        public string Name { get; }
        public SimClock Clock { get; }

        public SimList(string name, SimClock clock)
        {
            Name = name;
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            clock.Context.RegisterList(this);
        }

        // ── statistics ───────────────────────────────────────────────────────

        /// <summary>Entries in the list over time (time-weighted average, peak).</summary>
        public StatTimeWeightedVariable Content { get; } = new StatTimeWeightedVariable();

        /// <summary>Pending (back-ordered) pulls over time.</summary>
        public StatTimeWeightedVariable PendingPulls { get; } = new StatTimeWeightedVariable();

        /// <summary>Time each pulled entry waited in the list (0 for entries that met a pending pull).</summary>
        public StatTally WaitTime { get; } = new StatTally();

        public int Pushed { get; private set; }
        public int Pulled { get; private set; }

        /// <summary>Entries currently in the list.</summary>
        public int Count => inner.Count;

        /// <summary>Pulls currently waiting for an entry.</summary>
        public int PendingPullCount => inner.BackOrderCount;

        /// <summary>Entries in insertion order (inspection only).</summary>
        public IReadOnlyList<ListEntry> Entries => inner.Snapshot();

        // ── configuration ────────────────────────────────────────────────────

        /// <summary>Order used by pulls whose query has none (empty = FIFO).</summary>
        public void SetDefaultOrder(string field, bool descending = false)
        {
            defaultOrder.Clear();
            if (!string.IsNullOrEmpty(field)) defaultOrder.Add(new KeyValuePair<string, bool>(field, descending));
        }

        /// <summary>Registers a calculated numeric field usable in queries like a stored one.</summary>
        public void DefineField(string name, Func<ListEntry, double> compute) => calculated[name] = compute;

        /// <summary>Fields defined with <see cref="DefineFields"/> (copies).</summary>
        public IReadOnlyList<ListFieldSpec> Fields => fieldSpecs;

        /// <summary>Statistics of the monitored fields, by field name.</summary>
        public IReadOnlyDictionary<string, ListFieldStats> FieldStatistics => fieldStats;

        /// <summary>
        /// Defines fields (labels under another name, built-ins, formulas) usable by name in queries and orders;
        /// the monitored ones record their values on push and on pull. Throws <see cref="FormatException"/> /
        /// <see cref="ArgumentException"/> for a bad formula, a duplicate or empty name, an unknown built-in
        /// or a formula reading its own field.
        /// </summary>
        public void DefineFields(IEnumerable<ListFieldSpec> specs)
        {
            if (specs == null) return;
            foreach (var spec in specs)
            {
                if (spec == null) continue;
                string name = (spec.name ?? "").Trim();
                if (name.Length == 0) throw new ArgumentException($"List '{Name}': a field has no name.");
                if (fieldSpecs.Exists(f => f.name == name)) throw new ArgumentException($"List '{Name}': field '{name}' defined twice.");
                string source = (spec.source ?? "").Trim();
                switch (spec.kind)
                {
                    case ListFieldKind.Label:
                        if (source.Length > 0 && source != name) aliases[name] = source;
                        break;
                    case ListFieldKind.BuiltIn:
                        if (Array.IndexOf(BuiltInFields, source) < 0)
                            throw new ArgumentException($"List '{Name}', field '{name}': unknown built-in '{source}' (use {string.Join(", ", BuiltInFields)}).");
                        if (source != name) aliases[name] = source;
                        break;
                    default:
                        SimuLean.Expressions.Expression ex;
                        try { ex = SimuLean.Expressions.Expression.Parse(source); }
                        catch (FormatException fe) { throw new FormatException($"List '{Name}', field '{name}': {fe.Message}", fe); }
                        foreach (string used in ex.References)
                            if (used == name) throw new ArgumentException($"List '{Name}', field '{name}': the formula reads its own field.");
                        calculated[name] = e => Evaluate(ex, e);
                        break;
                }
                var copy = spec.Clone(); copy.name = name; copy.source = source;
                fieldSpecs.Add(copy);
                if (spec.monitor) fieldStats[name] = new ListFieldStats { Field = name };
            }
        }

        private double Evaluate(SimuLean.Expressions.Expression ex, ListEntry e)
        {
            if (++evalDepth > 16)
            {
                evalDepth = 0;
                throw new InvalidOperationException($"List '{Name}': calculated fields refer to each other in a cycle ({ex.Text}).");
            }
            try { return ex.Number(new EntryScope(this, e)); }
            finally { if (evalDepth > 0) evalDepth--; }
        }

        // An entry seen by an expression: bare names = its fields (number, else text), item.x = its item's properties.
        private readonly struct EntryScope : SimuLean.Expressions.IExpressionScope, SimuLean.Expressions.IDataScope
        {
            private readonly SimList list; private readonly ListEntry e;
            public EntryScope(SimList list, ListEntry e) { this.list = list; this.e = e; }
            public SimuLean.Data.ModelData Data => list.Clock.Context.Data;
            public double Now => list.Clock.GetSimulationTime();
            public SimuLean.Expressions.ExprValue Get(string root, string name)
            {
                if (root == "item") return SimuLean.Expressions.EntityScope.OfItem(e.Item, name, Now);
                if (root != null) return SimuLean.Expressions.ExprValue.None;
                double v = list.GetNumber(e, name);
                if (!double.IsNaN(v)) return SimuLean.Expressions.ExprValue.Of(v);
                string t = list.GetText(e, name);
                return t != null ? SimuLean.Expressions.ExprValue.Of(t) : SimuLean.Expressions.ExprValue.None;
            }
        }

        private void RecordFields(ListEntry e, bool push)
        {
            foreach (var s in fieldStats.Values)
            {
                double v = GetNumber(e, s.Field);
                if (!double.IsNaN(v)) (push ? s.AtPush : s.AtPull).Update(v);
            }
        }

        // ── push / pull ──────────────────────────────────────────────────────

        /// <summary>Advertises <paramref name="item"/>, held by <paramref name="owner"/>. Returns the entry
        /// (null if a pending pull took it straight away).</summary>
        public ListEntry PushItem(Item item, Element owner) => PushEntry(item, owner, null);

        /// <summary>Pushes any value (an order, a token, a message) with optional fields for queries.</summary>
        public ListEntry PushValue(object value, IDictionary<string, object> fields = null, Element owner = null)
            => PushEntry(value, owner, fields);

        private ListEntry PushEntry(object value, Element owner, IDictionary<string, object> fields)
        {
            var e = new ListEntry
            {
                Value = value, Owner = owner, PushTime = Now, Sequence = seq++,
                fields = fields != null ? new Dictionary<string, object>(fields) : null,
            };
            Pushed++;
            if (fieldStats.Count > 0) RecordFields(e, true);
            if (inner.Push(e)) return null;          // a pending pull matched: delivered by its event
            lastPushTime = e.PushTime;
            Content.Update(1, Now);
            return e;
        }

        /// <summary>Takes the best matching entry now; false when none matches.</summary>
        public bool TryPull(ListQuery query, out ListEntry entry)
        {
            query = query ?? ListQuery.All;
            if (!inner.TryPull(query.Filter(this), OrderOf(query), out entry)) return false;
            TookFromList(entry);
            return true;
        }

        /// <summary>The best matching entry, left in the list.</summary>
        public bool TryPeek(ListQuery query, out ListEntry entry)
        {
            query = query ?? ListQuery.All;
            return inner.TryPeek(query.Filter(this), OrderOf(query), out entry);
        }

        // The order of a pull: none (the first match wins) when it is the list's own order — oldest first by age or push
        // time, the order entries are kept in — so a pull stops at its first match instead of comparing every entry
        // (review2 #18). A put-back entry (a pull cancelled after its match) may break that order: then the full sort.
        private Comparison<ListEntry> OrderOf(ListQuery query)
        {
            var keys = query.Keys(defaultOrder);
            if (keys == null) return null;
            if (keys.Count == 1 && !pushOrderBroken)
            {
                string f = aliases.TryGetValue(keys[0].Key, out string target) ? target : keys[0].Key;
                if ((f == "age" && keys[0].Value) || (f == "pushTime" && !keys[0].Value)) return null;
            }
            return query.Sort(this, defaultOrder);
        }

        // true while an entry sits out of its push-time order (put back); cleared when the list empties
        private bool pushOrderBroken;
        private double lastPushTime = double.NegativeInfinity;

        /// <summary>
        /// Pulls the best matching entry now (callback invoked synchronously) or, if none matches,
        /// registers a pending pull delivered by a zero-delay event when a matching entry is pushed.
        /// Returns the pending pull (cancellable), or null when satisfied at once.
        /// </summary>
        public ListPull Pull(ListQuery query, Action<ListEntry> onDelivered)
        {
            if (onDelivered == null) throw new ArgumentNullException(nameof(onDelivered));
            if (TryPull(query, out ListEntry now)) { onDelivered(now); return null; }

            query = query ?? ListQuery.All;
            var pull = new ListPull { List = this, Run = run };
            pull.BackOrder = inner.Pull(query.Filter(this), null, e =>
            {
                // Matched by a push: deliver after the pusher's own event (zero-delay event). The pull is counted when
                // it is delivered: one cancelled meanwhile records nothing.
                pull.Scheduled = true;
                PendingPulls.Update(-1, Now);
                Clock.ScheduleEvent(new Delivery(this, pull, e, onDelivered), 0);
            });
            PendingPulls.Update(1, Now);
            return pull;
        }

        /// <summary>
        /// The entries matching <paramref name="query"/>, best first (its order, else the list's default order,
        /// then FIFO) — for consumers that look further than the best entry (e.g. a dispatcher trying requests
        /// in priority order). A snapshot: take entries with <see cref="Take"/>.
        /// </summary>
        public List<ListEntry> Ordered(ListQuery query = null)
        {
            query = query ?? ListQuery.All;
            var filter = query.Filter(this);
            var keys = query.Keys(defaultOrder);
            var all = inner.Snapshot();
            var idx = new List<int>(all.Count);
            for (int i = 0; i < all.Count; i++) if (filter == null || filter(all[i])) idx.Add(i);
            if (keys != null)
            {
                // each entry's order values are read once (live fields such as utilization or distance cost a lookup or a
                // route each), then a stable sort on them — ties keep the list order (review2 #15)
                int nk = keys.Count;
                var vals = new double[all.Count * nk];
                foreach (int i in idx) for (int k = 0; k < nk; k++) vals[i * nk + k] = ListQuery.OrderValueOf(this, all[i], keys[k].Key);
                idx.Sort((a, b) =>
                {
                    for (int k = 0; k < nk; k++)
                    {
                        int c = vals[a * nk + k].CompareTo(vals[b * nk + k]);
                        if (c != 0) return keys[k].Value ? -c : c;
                    }
                    return a.CompareTo(b);
                });
            }
            var result = new List<ListEntry>(idx.Count);
            foreach (int i in idx) result.Add(all[i]);
            return result;
        }

        /// <summary>Takes a specific entry out of the list as a pull (counted, waiting time recorded). False if not listed.</summary>
        public bool Take(ListEntry entry)
        {
            if (entry == null || !inner.Remove(entry)) return false;
            TookFromList(entry);
            return true;
        }

        /// <summary>Withdraws an entry (e.g. its item left by another path). Returns false if not listed.</summary>
        public bool Remove(ListEntry entry)
        {
            if (entry == null || !inner.Remove(entry)) return false;
            Content.Update(-1, Now);
            if (inner.Count == 0) pushOrderBroken = false;
            return true;
        }

        // An entry matched for a pull that was cancelled before delivery returns as it was: its push time and fields
        // (not recorded again, not a new push); another pending pull may take it.
        private void PutBack(ListEntry e)
        {
            if (inner.Push(e)) return;
            if (e.PushTime < lastPushTime) pushOrderBroken = true;
            lastPushTime = Math.Max(lastPushTime, e.PushTime);
            Content.Update(1, Now);
        }

        private void TookFromList(ListEntry e)
        {
            Pulled++;
            Content.Update(-1, Now);
            if (inner.Count == 0) pushOrderBroken = false;
            WaitTime.Update(Now - e.PushTime);
            if (fieldStats.Count > 0) RecordFields(e, false);
        }

        private sealed class Delivery : IEvent
        {
            private readonly SimList list; private readonly ListPull pull; private readonly ListEntry entry;
            private readonly Action<ListEntry> callback;
            public Delivery(SimList l, ListPull p, ListEntry e, Action<ListEntry> c) { list = l; pull = p; entry = e; callback = c; }
            public void Execute()
            {
                if (!pull.IsPending)
                {
                    list.PutBack(entry);   // cancelled after the match: the same entry goes back
                    return;
                }
                pull.IsPending = false;
                list.Pulled++;
                list.WaitTime.Update(0);   // matched by the push: no wait
                if (list.fieldStats.Count > 0) list.RecordFields(entry, false);
                callback(entry);
            }
        }

        // ── fields ───────────────────────────────────────────────────────────

        private double Now => Clock.GetSimulationTime();

        /// <summary>Numeric value of <paramref name="field"/> for <paramref name="e"/> (NaN if absent / not numeric).</summary>
        public double GetNumber(ListEntry e, string field)
        {
            if (aliases.TryGetValue(field, out string target)) field = target;
            switch (field)
            {
                case "age": return Now - e.PushTime;
                case "pushTime": return e.PushTime;
                case "itemPriority": return e.Item != null ? e.Item.priority : double.NaN;
            }
            if (calculated.TryGetValue(field, out var f)) return f(e);
            if (e.Item != null)
            {
                double? v = e.Item.GetLabelValue(field);
                return v ?? double.NaN;
            }
            if (e.Value is IListFieldSource src)
            {
                double v = src.GetListNumber(field);
                if (!double.IsNaN(v)) return v;
            }
            if (e.fields != null && e.fields.TryGetValue(field, out object o) && o != null)
                return ToNumber(o);
            return double.NaN;
        }

        /// <summary>Text value of <paramref name="field"/> for <paramref name="e"/> (null if absent).</summary>
        public string GetText(ListEntry e, string field)
        {
            if (aliases.TryGetValue(field, out string target)) field = target;
            switch (field)
            {
                case "owner": return e.Owner?.GetName();
                case "type": return e.Item?.GetTypeName();
            }
            if (e.Item != null)
            {
                string t = e.Item.GetLabelText(field);
                if (t != null) return t;
            }
            else if (e.Value is IListFieldSource src && src.GetListText(field) is string st)
                return st;
            else if (e.fields != null && e.fields.TryGetValue(field, out object o) && o != null)
                return Convert.ToString(o, CultureInfo.InvariantCulture);
            double n = GetNumber(e, field);
            return double.IsNaN(n) ? null : n.ToString("R", CultureInfo.InvariantCulture);
        }

        internal static double NumberOf(object o) => ToNumber(o);
        internal bool Compare(ListEntry e, string field, ListOp op, object value)
            => Compare(e, field, op, ToNumber(value), value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture));
        internal bool Compare(ListEntry e, string field, ListOp op, double rhs, string want)
        {
            double lhs = GetNumber(e, field);
            if (!double.IsNaN(rhs) && !double.IsNaN(lhs))
            {
                switch (op)
                {
                    case ListOp.Equal: return lhs == rhs;
                    case ListOp.NotEqual: return lhs != rhs;
                    case ListOp.Less: return lhs < rhs;
                    case ListOp.LessOrEqual: return lhs <= rhs;
                    case ListOp.Greater: return lhs > rhs;
                    case ListOp.GreaterOrEqual: return lhs >= rhs;
                }
            }
            string text = GetText(e, field);
            int c = string.CompareOrdinal(text, want);
            switch (op)
            {
                case ListOp.Equal: return text != null && c == 0;
                case ListOp.NotEqual: return c != 0;
                case ListOp.Less: return text != null && c < 0;
                case ListOp.LessOrEqual: return text != null && c <= 0;
                case ListOp.Greater: return text != null && c > 0;
                default: return text != null && c >= 0;
            }
        }

        private static double ToNumber(object o)
        {
            switch (o)
            {
                case null: return double.NaN;
                case double d: return d;
                case float f: return f;
                case int i: return i;
                case long l: return l;
                case bool b: return b ? 1 : 0;
                case string s:
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
                default:
                    return o is IConvertible ? Convert.ToDouble(o, CultureInfo.InvariantCulture) : double.NaN;
            }
        }

        // ── run lifecycle ────────────────────────────────────────────────────

        /// <summary>Empties the list (entries and pending pulls) and its statistics (new run).</summary>
        public void Reset()
        {
            inner.Clear();
            pushOrderBroken = false;
            lastPushTime = double.NegativeInfinity;
            seq = 0;
            run++;   // pulls of the previous run no longer count
            Content.Reset(Now); PendingPulls.Reset(Now); WaitTime.Reset();   // averages over the whole run
            foreach (var s in fieldStats.Values) s.Reset();
            Pushed = Pulled = 0;
        }

        private int run;

        /// <summary>A pending pull was withdrawn: it no longer counts as pending.</summary>
        internal void PullCancelled(ListPull pull)
        {
            if (pull.Run == run) PendingPulls.Update(-1, Now);
        }

        /// <summary>Warm-up: forgets the statistics, keeps the entries and pending pulls.</summary>
        public void ResetStatistics()
        {
            Content.Restart(Now); PendingPulls.Restart(Now); WaitTime.Reset();
            foreach (var s in fieldStats.Values) s.Reset();
            Pushed = Pulled = 0;
        }
    }
}
