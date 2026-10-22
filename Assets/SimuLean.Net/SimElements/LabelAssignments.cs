using System;
using System.Collections.Generic;
using SimuLean.Expressions;

namespace SimuLean
{
    /// <summary>
    /// Labels an element sets on the items it hands over or takes: records
    /// <c>name = expression</c> over the item and the element — <c>reps = reps + 1</c>, <c>t_decision = now()</c>,
    /// <c>valid = rand() &lt; 0.9</c>, <c>dx = pick(0.6, 'diagnosis', 0.3, 'control', 0.1, 'other')</c>. An element's
    /// <see cref="Element.ExitLabels"/> are set when an item leaves it, its <see cref="Element.EntryLabels"/> when one enters,
    /// both <b>before</b> the receiving element's <c>Receive</c> (so its process time, routing and admission downstream read
    /// them) and undone if it refuses the item (<see cref="ItemHandOver"/>). Random functions draw from the element's own
    /// stream (<see cref="Element.ExpressionRandom"/>). A number sets a numeric label, a text a text label.
    /// </summary>
    public sealed class LabelAssignments
    {
        private readonly string[] names;
        private readonly Expression[] values;

        private LabelAssignments(string[] names, Expression[] values) { this.names = names; this.values = values; }

        /// <summary>The records' label names, in order.</summary>
        public IReadOnlyList<string> Names => names;

        /// <summary>Parses <c>name = expression</c> records (blank ones are skipped); null when there are none. A malformed
        /// record fails naming <paramref name="owner"/>.</summary>
        public static LabelAssignments Parse(IEnumerable<string> records, string owner)
        {
            if (records == null) return null;
            var n = new List<string>();
            var v = new List<Expression>();
            foreach (string raw in records)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                int eq = raw.IndexOf('=');
                if (eq <= 0 || (eq + 1 < raw.Length && raw[eq + 1] == '='))
                    throw new FormatException($"Element '{owner}': label record '{raw}' is not 'name = expression'.");
                string name = raw.Substring(0, eq).Trim();
                string expr = raw.Substring(eq + 1).Trim();
                if (name.Length == 0 || expr.Length == 0)
                    throw new FormatException($"Element '{owner}': label record '{raw}' is not 'name = expression'.");
                string error = Expression.Check(expr);
                if (error != null) throw new FormatException($"Element '{owner}': label '{name}': {error}");
                n.Add(name);
                v.Add(Expression.Parse(expr));
            }
            return n.Count == 0 ? null : new LabelAssignments(n.ToArray(), v.ToArray());
        }

        [ThreadStatic] private static EntityScope scope;

        /// <summary>Sets the labels on <paramref name="item"/>, evaluated in order over the item and
        /// <paramref name="element"/> (a record sees the ones before it). With <paramref name="undo"/>, the former values
        /// are saved there first.</summary>
        public void Apply(Item item, Element element, List<SavedLabel> undo = null)
        {
            var s = (scope ?? (scope = new EntityScope())).Set(item, element);
            for (int i = 0; i < names.Length; i++)
            {
                if (undo != null) undo.Add(SavedLabel.Of(item, names[i]));
                ExprValue v = values[i].Evaluate(s);
                if (v.Missing) item.Properties.Remove(names[i]);
                else if (v.IsText) item.SetLabelText(names[i], v.Text);
                else item.SetLabelValue(names[i], v.AsNumber());
            }
            s.Set(null, null);
        }

        /// <summary>A label's value before an assignment (to undo it).</summary>
        public readonly struct SavedLabel
        {
            private readonly Item item;
            private readonly string name;
            private readonly PropertyKind kind;
            private readonly double number;
            private readonly string text;

            private SavedLabel(Item item, string name, PropertyKind kind, double number, string text)
            { this.item = item; this.name = name; this.kind = kind; this.number = number; this.text = text; }

            public static SavedLabel Of(Item item, string name)
            {
                if (!item.HasProperties) return new SavedLabel(item, name, PropertyKind.None, 0, null);
                var p = item.Properties;
                if (p.TryGetDouble(name, out double d)) return new SavedLabel(item, name, PropertyKind.Double, d, null);
                if (p.TryGetString(name, out string t)) return new SavedLabel(item, name, PropertyKind.String, 0, t);
                return new SavedLabel(item, name, PropertyKind.None, 0, null);
            }

            public void Restore()
            {
                switch (kind)
                {
                    case PropertyKind.Double: item.Properties.SetDouble(name, number); break;
                    case PropertyKind.String: item.Properties.SetString(name, text); break;
                    default: if (item.HasProperties) item.Properties.Remove(name); break;
                }
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
