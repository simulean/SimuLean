using System;
using System.Collections.Generic;
using SimuLean.Expressions;

namespace SimuLean
{
    /// <summary>
    /// Labels as <c>name = expression</c> records — the one syntax of every label a model sets: a source's
    /// <see cref="InterArrivalSource.CreateLabels"/> / <see cref="ScheduleSource.CreateLabels"/> (at creation, purpose
    /// <c>createLabel</c>), an element's entry / exit labels and a resource member's properties (evaluated once when the member
    /// is built, <see cref="ApplyTo"/>, purpose <c>label</c>). Text constants are quoted (<c>sku = '007'</c>), numbers are
    /// numbers. Entry / exit labels: records
    /// <c>name = expression</c> over the item and the element — <c>reps = (reps ?? 0) + 1</c>, <c>t_decision = now()</c>,
    /// <c>valid = rand() &lt; 0.9</c>, <c>dx = pick(0.6, 'diagnosis', 0.3, 'control', 0.1, 'other')</c>. An element's
    /// <see cref="Element.ExitLabels"/> are set when an item leaves it, its <see cref="Element.EntryLabels"/> when one enters,
    /// both <b>before</b> the receiving element's <c>Receive</c> (so its process time, routing and admission downstream read
    /// them) and undone if it refuses the item (<see cref="ItemHandOver"/>). Random functions draw from each label's own
    /// stream (owner = the element, purpose <c>exitLabel:NAME</c> / <c>entryLabel:NAME</c>): another random label
    /// or expression of the element leaves its draws as they were (common random numbers between scenarios). A number sets a
    /// numeric label, a text a text label.
    /// </summary>
    public sealed class LabelAssignments
    {
        private readonly string[] names;
        private readonly Expression[] values;
        private readonly string purpose;
        // each label's stream, created on first use for the clock it belongs to
        private SimuLean.Stochastics.Streams.IRandomEngine[] streams;
        private SimClock streamsClock;

        private LabelAssignments(string[] names, Expression[] values, string purpose)
        { this.names = names; this.values = values; this.purpose = purpose; }

        /// <summary>The records' label names, in order.</summary>
        public IReadOnlyList<string> Names => names;

        /// <summary>Parses <c>name = expression</c> records (blank ones are skipped); null when there are none. A malformed
        /// record fails naming <paramref name="owner"/> (an element's name, or a description such as
        /// <c>Resource 'Op1'</c> when <paramref name="ownerIsDescribed"/>). <paramref name="purpose"/> names the labels'
        /// streams (<c>purpose:NAME</c>): <c>exitLabel</c> / <c>entryLabel</c> / <c>createLabel</c> / <c>label</c>.</summary>
        public static LabelAssignments Parse(IEnumerable<string> records, string owner, string purpose = "label", bool ownerIsDescribed = false)
        {
            string ownerName = owner;
            if (!ownerIsDescribed) owner = $"Element '{owner}'";
            if (records == null) return null;
            var n = new List<string>();
            var v = new List<Expression>();
            foreach (string raw in records)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                int eq = raw.IndexOf('=');
                if (eq <= 0 || (eq + 1 < raw.Length && raw[eq + 1] == '='))
                    throw new FormatException($"{owner}: label record '{raw}' is not 'name = expression' (quote a text: name = 'A').");
                string name = raw.Substring(0, eq).Trim();
                string expr = raw.Substring(eq + 1).Trim();
                if (name.Length == 0 || expr.Length == 0)
                    throw new FormatException($"{owner}: label record '{raw}' is not 'name = expression' (quote a text: name = 'A').");
                if (Expression.IsConstantName(name))
                    throw new FormatException($"{owner}: label record '{raw}': '{name}' is a constant (1 / 0) in expressions, not a label name.");
                var context = purpose == "property" ? ExpressionContext.MemberProperty : ExpressionContext.Label;
                Expression parsed;
                try { parsed = Expression.Parse(expr, context, ownerName, purpose + ":" + name); }
                catch (FormatException e) { throw new FormatException($"{owner}: label '{name}': {e.Message}", e); }
                if (context == ExpressionContext.Label) parsed.WritesLabel = name;   // the build check of computed names
                n.Add(name);
                v.Add(parsed);
            }
            return n.Count == 0 ? null : new LabelAssignments(n.ToArray(), v.ToArray(), purpose);
        }

        [ThreadStatic] private static EntityScope scope;

        /// <summary>Sets the labels on <paramref name="item"/>, evaluated in order over the item and
        /// <paramref name="element"/> (a record sees the ones before it). With <paramref name="undo"/>, the former values
        /// are saved there first.</summary>
        public void Apply(Item item, Element element, List<SavedLabel> undo = null)
        {
            var s = (scope ?? (scope = new EntityScope())).Set(item, element);
            var clock = element?.GetSimClock();
            if (clock != null && !ReferenceEquals(clock, streamsClock))
            {
                streams = new SimuLean.Stochastics.Streams.IRandomEngine[names.Length];
                streamsClock = clock;
            }
            try
            {
                for (int i = 0; i < names.Length; i++)
                {
                    if (undo != null) undo.Add(SavedLabel.Of(item, names[i]));
                    if (clock != null)
                        s.RandomStream = streams[i] ?? (streams[i] = clock.Context.CreateRandomStream(element.Name, purpose + ":" + names[i]));
                    item.Properties.Set(names[i], values[i].Evaluate(s));
                }
            }
            finally
            {
                s.RandomStream = null;
                s.Set(null, null);
            }
        }

        /// <summary>Sets the labels on a model object (a resource member when it is built): evaluated once, in order, over
        /// <paramref name="target"/> (a bare name or <c>element.x</c> reads its own properties, so a record sees the ones
        /// before it); random functions draw from the target's own stream per label (owner = its name, purpose
        /// <c>label:NAME</c>).</summary>
        public void ApplyTo(Element target)
        {
            var s = new ObjectScope(target);
            var clock = target.GetSimClock();
            for (int i = 0; i < names.Length; i++)
            {
                if (clock != null) s.RandomStream = clock.Context.CreateRandomStream(target.Name, purpose + ":" + names[i]);
                ExprValue v = values[i].Evaluate(s);
                if (v.Missing && values[i].References.Count > 0)
                    throw new FormatException($"Resource '{target.Name}': label '{names[i]}' reads a name that is not set (quote a text: {names[i]} = 'value').");
                target.Properties.Set(names[i], v);
            }
        }

        // a model object's own scope: bare names are its properties (no item)
        private sealed class ObjectScope : IExpressionScope, IDataScope, IRandomScope, IModelScope
        {
            private readonly EntityScope inner;
            public ObjectScope(Element target) { inner = new EntityScope().Set(null, target); }
            public SimuLean.Stochastics.Streams.IRandomEngine RandomStream { set => inner.RandomStream = value; }
            public ExprValue Get(string root, string name) => inner.Get(root == null || root == "item" ? "element" : root, name);
            public double Now => inner.Now;
            public SimuLean.Data.ModelData Data => inner.Data;
            public SimuLean.Stochastics.Streams.IRandomEngine Random => inner.Random;
            public SimContext Model => inner.Model;
        }

        /// <summary>The value of a constant record <c>name = expression</c> (no item, element or random draw), for matching a
        /// member's label in a study path (<c>team:T.members[skill=weld]</c>); false when it is not a constant record.</summary>
        public static bool TryConstant(string record, out string name, out ExprValue value)
        {
            name = null; value = ExprValue.None;
            int eq = record?.IndexOf('=') ?? -1;
            if (eq <= 0 || (eq + 1 < record.Length && record[eq + 1] == '=')) return false;
            name = record.Substring(0, eq).Trim();
            string expr = record.Substring(eq + 1).Trim();
            if (name.Length == 0 || expr.Length == 0 || Expression.Check(expr, ExpressionContext.MemberProperty) != null) return false;
            try { value = Expression.Parse(expr, ExpressionContext.MemberProperty).Evaluate(new EntityScope()); }
            catch (Exception) { return false; }
            return !value.Missing;
        }

        /// <summary>A label's value before an assignment (to undo it).</summary>
        public readonly struct SavedLabel
        {
            private readonly Item item;
            private readonly string name;
            private readonly ExprValue value;   // None = the label was not set

            private SavedLabel(Item item, string name, ExprValue value) { this.item = item; this.name = name; this.value = value; }

            public static SavedLabel Of(Item item, string name)
            {
                if (!item.HasProperties) return new SavedLabel(item, name, ExprValue.None);
                var p = item.Properties;
                if (p.TryGetDouble(name, out double d)) return new SavedLabel(item, name, ExprValue.Of(d));
                if (p.TryGetString(name, out string t)) return new SavedLabel(item, name, ExprValue.Of(t));
                return new SavedLabel(item, name, ExprValue.None);
            }

            public void Restore()
            {
                if (value.Missing && !item.HasProperties) return;
                item.Properties.Set(name, value);
            }
        }
    }

    /// <summary>
    /// The hand-over of an item between two elements, as every link does it: the source's
    /// exit labels and the destination's entry labels (<see cref="LabelAssignments"/>), then the destination's
    /// <c>Receive</c>; the labels are undone if it refuses the item. Statistics stay in the links.
    /// </summary>
    public static class ItemHandOver
    {
        [ThreadStatic] private static List<LabelAssignments.SavedLabel> undo;

        /// <summary>Hands <paramref name="item"/> to <paramref name="target"/> (from <paramref name="source"/>, null when the
        /// item already left it — a carried item); false = refused, its labels as they were.</summary>
        public static bool Receive(Element source, Element target, Item item)
        {
            var exit = source?.ExitLabels;
            var entry = target.EntryLabels;
            if (exit == null && entry == null) return target.Receive(item);
            var saved = undo ?? (undo = new List<LabelAssignments.SavedLabel>());
            int mark = saved.Count;   // nested hand-overs (a pass-through Receive) stack their own entries
            exit?.Apply(item, source, saved);
            entry?.Apply(item, target, saved);
            bool received = false;
            try { received = target.Receive(item); }
            finally
            {
                if (!received) for (int i = saved.Count - 1; i >= mark; i--) saved[i].Restore();
                saved.RemoveRange(mark, saved.Count - mark);
            }
            return received;
        }

        /// <summary>The item left <paramref name="source"/> without entering anything yet (loaded on a carrier): its exit
        /// labels, kept.</summary>
        public static void Exit(Element source, Item item) => source.ExitLabels?.Apply(item, source);
    }
}
