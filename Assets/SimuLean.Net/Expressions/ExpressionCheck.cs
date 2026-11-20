using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimuLean.Expressions
{
    /// <summary>
    /// The build-time check of a model's expressions: while
    /// <see cref="SimuLean.Headless.HeadlessModelFactory.BuildModel"/> builds a model, every expression parsed with an owner
    /// (<see cref="Expression.Parse(string, ExpressionContext, string, string)"/>) is collected; once the model is built,
    /// <see cref="Problems"/> resolves the names each one reads — elements, data tables and their columns, element properties
    /// (built-ins, declared ones and those a reaction sets) — and the build fails listing every unknown one with a "did you
    /// mean". Item labels are not checked here (they are set during the run): their first missing read warns once. A name
    /// computed from an item label (<c>content(item.dest)</c>) is checked when every write of the label the build sees gives
    /// values known then (<see cref="LabelWrites"/>); otherwise at run time.
    /// Unity (<c>simulean_validate</c>, the scene run) and the runner (<c>validate</c>) build through the same factory, so
    /// they report the same thing.
    /// </summary>
    public static class ExpressionCheck
    {
        [ThreadStatic] private static List<Expression> collected;

        /// <summary>Starts collecting the expressions parsed on this thread (a model build).</summary>
        public static void Begin() => collected = new List<Expression>();

        /// <summary>Stops collecting and returns what was parsed since <see cref="Begin"/>.</summary>
        public static List<Expression> End()
        {
            var c = collected ?? new List<Expression>();
            collected = null;
            return c;
        }

        internal static void Collect(Expression e) => collected?.Add(e);

        /// <summary>The messages of the names <paramref name="expressions"/> read that <paramref name="model"/> does not have
        /// (empty = all known). <paramref name="reactionProperties"/>: the element properties the model's reactions set.
        /// <paramref name="labels"/>: the item-label writes the build saw besides the expressions' own (a source's arrivals
        /// table, a reaction's number); the label records among <paramref name="expressions"/> are added to it.</summary>
        public static List<string> Problems(IEnumerable<Expression> expressions, SimContext model, ICollection<string> reactionProperties,
                                            LabelWrites labels = null)
        {
            var errors = new List<string>();
            if (expressions == null || model == null) return errors;
            reactionProperties = reactionProperties ?? new HashSet<string>();
            labels = labels ?? new LabelWrites();
            foreach (var e in expressions)
                if (e.WritesLabel != null) labels.Add(e.WritesLabel, e, model);
            foreach (var e in expressions)
            {
                var owner = e.Owner != null ? model.FindElement(e.Owner) : null;
                e.Resolve(model, owner, reactionProperties, errors);
                e.CheckLabelNames(model, labels, errors);
            }
            return errors;
        }
    }

    /// <summary>
    /// The item-label writes a model build sees, per label: the values each gives when they are known at
    /// build — a label record or a reaction's value that is a constant, a choice of constants (<c>?:</c>, <c>if</c>,
    /// <c>pick</c>) or a constant table's column, a source's arrivals-table column, a reaction's number — or "unknown" when one
    /// write is not. A name computed from a label (<c>content(item.dest)</c>) is checked only when every write of the label is
    /// known; labels set by element code (not by a record, a reaction or a table) are not seen and stay a run-time check.
    /// </summary>
    public sealed class LabelWrites
    {
        private sealed class Writes { public bool Unknown; public readonly List<(string value, string by)> Values = new List<(string, string)>(); }
        private readonly Dictionary<string, Writes> labels = new Dictionary<string, Writes>(StringComparer.Ordinal);

        private Writes Of(string label)
        {
            if (!labels.TryGetValue(label, out var w)) labels[label] = w = new Writes();
            return w;
        }

        /// <summary>A write of <paramref name="label"/> with the value <paramref name="value"/>, made by
        /// <paramref name="by"/> (for messages: "'src' (arrivals table)").</summary>
        public void Add(string label, string value, string by)
        {
            if (string.IsNullOrEmpty(label) || value == null) return;
            Of(label).Values.Add((value, by));
        }

        /// <summary>A write of a number.</summary>
        public void Add(string label, double value, string by) => Add(label, value.ToString("R", CultureInfo.InvariantCulture), by);

        /// <summary>A write of <paramref name="label"/> whose values are not known when the model is built.</summary>
        public void Unknown(string label) { if (!string.IsNullOrEmpty(label)) Of(label).Unknown = true; }

        /// <summary>A write by an expression: its values when they are known at build, else unknown.</summary>
        internal void Add(string label, Expression e, SimContext model)
        {
            var values = e.StaticValues(model);
            if (values == null) { Unknown(label); return; }
            foreach (var v in values) if (!v.Missing) Add(label, v.ToString(), e.Where);
        }

        /// <summary>Every (value, writer) of <paramref name="label"/> when all its writes are known, else null.</summary>
        internal IReadOnlyList<(string value, string by)> Known(string label)
            => labels.TryGetValue(label, out var w) && !w.Unknown && w.Values.Count > 0 ? w.Values : null;
    }
}
