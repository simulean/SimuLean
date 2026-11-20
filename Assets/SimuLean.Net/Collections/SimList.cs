using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Statistics;

namespace SimuLean.Collections
{
    /// <summary>
    /// A model list a model-wide rendezvous where elements and
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

        /// <summary>Built-in field names, readable in every list: the entry's <c>age</c> and <c>pushTime</c>, and the most
        /// used values of its item and its origin (any <c>item.x</c> / <c>origin.x</c> is readable too).</summary>
        public static readonly string[] BuiltInFields = { "age", "pushTime", "item.type", "item.priority", "item.id", "origin.name" };
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
            order = null;   // its keys changed in place
            if (!string.IsNullOrEmpty(field)) defaultOrder.Add(new KeyValuePair<string, bool>(field, descending));
        }

        /// <summary>The order keys and conditions of <paramref name="spec"/> written as expressions (<c>prio * 10 + age desc</c>, <c>has(item.code)</c>) become calculated fields named by their text, parsed now in the
        /// list's field context (checked at build: call it while the model is built) and drawing from <c>key:TEXT</c>.</summary>
        public void DefineExpressionKeys(ListQuerySpec spec, string where)
        {
            if (spec == null) return;
            var texts = new List<string>();
            foreach (var c in spec.conditions ?? new List<ListCondition>()) if (c != null && ListQuerySpec.IsExpression(c.field)) texts.Add(c.field);
            foreach (var k in spec.OrderKeys()) if (ListQuerySpec.IsExpression(k.Key)) texts.Add(k.Key);
            foreach (string text in texts)
            {
                if (calculated.ContainsKey(text)) continue;
                SimuLean.Expressions.Expression ex;
                try { ex = SimuLean.Expressions.Expression.Parse(text, FieldContext, Name, where); }
                catch (FormatException fe) { throw new FormatException($"List '{Name}', {where}: {fe.Message}", fe); }
                var field = new FieldStream(this, "key:" + text);
                calculated[text] = e => Evaluate(ex, e, field);
            }
        }

        /// <summary>Registers a calculated numeric field usable in queries like a stored one.</summary>
        public void DefineField(string name, Func<ListEntry, double> compute) => calculated[name] = compute;

        /// <summary>Where its calculated fields are written (a list's, or a milk run's request fields): the roots they read.</summary>
        public SimuLean.Expressions.ExpressionContext FieldContext { get; set; } = SimuLean.Expressions.ExpressionContext.ListField;

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
                        if (Array.IndexOf(BuiltInFields, source) < 0 && !source.StartsWith("item.", StringComparison.Ordinal)
                            && !source.StartsWith("origin.", StringComparison.Ordinal))
                            throw new ArgumentException($"List '{Name}', field '{name}': unknown built-in '{source}' (use {string.Join(", ", BuiltInFields)}, or item.x / origin.x).");
                        if (source != name) aliases[name] = source;
                        break;
                    default:
                        SimuLean.Expressions.Expression ex;
                        try { ex = SimuLean.Expressions.Expression.Parse(source, FieldContext, Name, "field " + name); }
                        catch (FormatException fe) { throw new FormatException($"List '{Name}', field '{name}': {fe.Message}", fe); }
                        foreach (string used in ex.References)
                            if (used == name) throw new ArgumentException($"List '{Name}', field '{name}': the formula reads its own field.");
                        var field = new FieldStream(this, name);
                        calculated[name] = e => Evaluate(ex, e, field);
                        break;
                }
                var copy = spec.Clone(); copy.name = name; copy.source = source;
                fieldSpecs.Add(copy);
                if (spec.monitor) fieldStats[name] = new ListFieldStats { Field = name };
            }
        }

        private double Evaluate(SimuLean.Expressions.Expression ex, ListEntry e, FieldStream field)
        {
            if (++evalDepth > 16)
            {
                evalDepth = 0;
                throw new InvalidOperationException($"List '{Name}': calculated fields refer to each other in a cycle ({ex.Text}).");
            }
            try { return ex.Number(new EntryScope(this, e, field)); }
            finally { if (evalDepth > 0) evalDepth--; }
        }

        // A calculated field's own random stream (CLAUDE.md: one stream per use, keyed by name): owner = the list,
        // purpose field:NAME; created on its first draw.
        private sealed class FieldStream
        {
            private readonly SimList list; private readonly string name;
            private SimuLean.Stochastics.Streams.IRandomEngine stream;
            public FieldStream(SimList list, string name) { this.list = list; this.name = name; }
            public SimuLean.Stochastics.Streams.IRandomEngine Stream
                => stream ?? (stream = list.Clock.Context.CreateRandomStream(list.Name, "field:" + name));
        }

        // An entry seen by an expression: bare names = its fields (number, else text), item.x = its item's labels; it reads
        // the model's elements and tables, and draws from its field's own stream.
        private readonly struct EntryScope : SimuLean.Expressions.IExpressionScope, SimuLean.Expressions.IDataScope,
                                             SimuLean.Expressions.IModelScope, SimuLean.Expressions.IRandomScope
        {
            private readonly SimList list; private readonly ListEntry e; private readonly FieldStream field;
            public EntryScope(SimList list, ListEntry e, FieldStream field) { this.list = list; this.e = e; this.field = field; }
            public SimuLean.Data.ModelData Data => list.Clock.Context.Data;
            public SimContext Model => list.Clock.Context;
            public SimuLean.Stochastics.Streams.IRandomEngine Random => field.Stream;
            public double Now => list.Clock.GetSimulationTime();
            public SimuLean.Expressions.ExprValue Get(string root, string name)
            {
                // item.x / origin.x / destination.x: the entry's item, origin, destination; entry.x and a bare name: its field
                string field = root == null || root == "entry" ? name : root + "." + name;
                double v = list.GetNumber(e, field);
                if (!double.IsNaN(v)) return SimuLean.Expressions.ExprValue.Of(v);
                string t = list.GetText(e, field);
                return t != null ? SimuLean.Expressions.ExprValue.Of(t) : SimuLean.Expressions.ExprValue.None;
            }

            private SimuLean.Expressions.ExprValue Unused(string name)
            {
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
        // time, the order entries are kept in — so a pull stops at its first match instead of comparing every entry. A put-back entry (a pull cancelled after its match) may break that order: then the full sort.
        private Comparison<ListEntry> OrderOf(ListQuery query)
        {
            var keys = query.Keys(defaultOrder);
            if (keys == null) return null;
            if (keys.Count == 1 && !pushOrderBroken)
            {
                string f = aliases.TryGetValue(keys[0].Key, out string target) ? target : keys[0].Key;
                if ((f == "age" && keys[0].Value) || (f == "pushTime" && !keys[0].Value)) return null;
            }
            var o = order;
            if (o == null || !ReferenceEquals(o.Query, query) || !ReferenceEquals(o.Keys, keys)) order = o = query.Sort(this, defaultOrder);
            return o.Begin();
        }

        // the last pull's order, reused by the next pull with the same query; one slot, so
        // a query built at every pull costs what it did before and nothing piles up
        private ListQuery.Order order;

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
            return BackOrder(query ?? ListQuery.All, onDelivered);
        }

        // A pull that waits for a matching push.
        // In its own method: a closure that captures a parameter is allocated at every call, used or not.
        private ListPull BackOrder(ListQuery query, Action<ListEntry> onDelivered)
        {
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
            if (keys != null) SortByKeys(all, idx, keys);
            var result = new List<ListEntry>(idx.Count);
            foreach (int i in idx) result.Add(all[i]);
            return result;
        }

        // Each entry's order values are read once (live fields such as utilization or distance cost a lookup or a route each),
        // then a stable sort on them — ties keep the list order
        // In its own method: a closure that captures a parameter is allocated at every call, used or not.
        private void SortByKeys(IReadOnlyList<ListEntry> all, List<int> idx, IReadOnlyList<KeyValuePair<string, bool>> keys)
        {
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
            }
            if (calculated.TryGetValue(field, out var f)) return f(e);
            if (field.IndexOf('.') > 0 && !(e.Value is IListFieldSource))
            {
                var pv = Prefixed(e, field);
                return pv.Missing || pv.IsText ? double.NaN : pv.Number;
            }
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
            if (field.IndexOf('.') > 0 && !(e.Value is IListFieldSource) && !calculated.ContainsKey(field))
            {
                var pv = Prefixed(e, field);
                return pv.Missing ? null : pv.ToString();
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

        // item.x (the entry's item: its labels and built-ins) / origin.x (the element it comes from: properties and built-ins)
        private SimuLean.Expressions.ExprValue Prefixed(ListEntry e, string field)
        {
            int dot = field.IndexOf('.');
            string root = field.Substring(0, dot), name = field.Substring(dot + 1);
            switch (root)
            {
                case "item": return SimuLean.Expressions.EntityScope.OfItem(e.Item, name, Now);
                case "origin": return SimuLean.Expressions.EntityScope.OfElement(e.Owner, name);
            }
            return SimuLean.Expressions.ExprValue.None;
        }

        private HashSet<string> warnedFields;

        /// <summary>An order or a filter read <paramref name="field"/> of an item entry that does not have it (a label set
        /// during the run): warned once per list and field, as an expression's missing label.</summary>
        internal void MissingField(ListEntry e, string field)
        {
            if (e.Item == null) return;
            if (warnedFields == null) warnedFields = new HashSet<string>();
            if (!warnedFields.Add(field)) return;
            Clock.Context.Warn($"[SimuLean] t={Now.ToString("0.###", CultureInfo.InvariantCulture)} list '{Name}': an entry has no field '{field}' " +
                               "(read as 0 / no text by an order or a query); warned once.");
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
            if (text == null && double.IsNaN(lhs)) MissingField(e, field);
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
