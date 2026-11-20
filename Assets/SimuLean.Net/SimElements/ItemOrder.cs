using System;
using System.Collections.Generic;
using SimuLean.Expressions;

namespace SimuLean
{
    /// <summary>The order a Queue offers its held items in, best first (<see cref="ItemsQueue.OfferSort"/>): order keys
    /// (<see cref="ItemOrder"/>) or a release rule (<see cref="SimuLean.Rules.RuleItemOrder"/>).</summary>
    public interface IItemOrder
    {
        /// <summary>The text it was written as (a rule: <c>rule:Type</c>).</summary>
        string Text { get; }
        /// <summary>The best of <paramref name="items"/> (held in arrival order) for <paramref name="holder"/>; ties: the
        /// earliest. Null when there are none.</summary>
        Item Best(IEnumerable<Item> items, Element holder);
        /// <summary><paramref name="items"/> best first, ties in arrival order, into <paramref name="into"/> (cleared first).</summary>
        void Sort(IEnumerable<Item> items, Element holder, List<Item> into);
    }

    /// <summary>
    /// An order over held items (a Queue's <c>offerSort</c>): order keys written as a list order —
    /// <c>key [desc|asc], key …</c>, the <see cref="SimuLean.Collections.ListQuerySpec"/> grammar — where each key is an
    /// expression in the context <see cref="ExpressionContext.OfferSort"/> over the item (a bare name or <c>item.x</c>: a
    /// label, else the built-ins <c>id</c>, <c>type</c>, <c>priority</c>, <c>created</c>, <c>age</c>) and the holding element
    /// (<c>element.x</c>), with model and table reads (<c>count(…)</c>, <c>table(…)</c>) and no random functions. Best first:
    /// ascending unless <c>desc</c>; the first key decides, ties by the next, then by arrival. A key that is a text or missing
    /// ranks as 0 (as a list's order). Keys are read live at every offer.
    /// </summary>
    public sealed class ItemOrder : IItemOrder
    {
        private readonly Expression[] keys;
        private readonly bool[] descending;
        private readonly double[] bestValues, values;
        private readonly EntityScope scope = new EntityScope();
        private readonly List<(Item item, int arrival)> sorting = new List<(Item, int)>();
        private double[] sortValues = new double[0];

        /// <summary>The text it was written as.</summary>
        public string Text { get; }

        private ItemOrder(string text, string owner)
        {
            Text = text;
            var parsed = SimuLean.Collections.ListQuerySpec.Decode("|" + text).OrderKeys();
            if (parsed.Count == 0) throw new FormatException($"'{owner}': offer order '{text}' has no key.");
            keys = new Expression[parsed.Count];
            descending = new bool[parsed.Count];
            for (int k = 0; k < parsed.Count; k++)
            {
                keys[k] = Expression.Parse(parsed[k].Key, ExpressionContext.OfferSort, owner, "offerSort");
                descending[k] = parsed[k].Value;
            }
            bestValues = new double[keys.Length];
            values = new double[keys.Length];
        }

        /// <summary>The order written as <paramref name="text"/> for the element <paramref name="owner"/>; null for an empty text.</summary>
        public static ItemOrder Parse(string text, string owner)
            => string.IsNullOrWhiteSpace(text) ? null : new ItemOrder(text.Trim(), owner);

        private void Read(Item item, Element holder, double[] into)
        {
            scope.Set(item, holder);
            try
            {
                for (int k = 0; k < keys.Length; k++)
                {
                    var v = keys[k].Evaluate(scope);
                    into[k] = v.IsText || v.Missing ? 0 : v.Number;
                }
            }
            finally { scope.Set(null, null); }
        }

        // < 0: a goes first
        private int Compare(double[] a, double[] b)
        {
            for (int k = 0; k < keys.Length; k++)
            {
                int c = a[k].CompareTo(b[k]);
                if (c != 0) return descending[k] ? -c : c;
            }
            return 0;
        }

        /// <summary>The best of <paramref name="items"/> (held in arrival order) for <paramref name="holder"/>; ties: the
        /// earliest. Null when there are none.</summary>
        public Item Best(IEnumerable<Item> items, Element holder)
        {
            Item best = null;
            foreach (var item in items)
            {
                if (best == null) { best = item; Read(item, holder, bestValues); continue; }
                Read(item, holder, values);
                if (Compare(values, bestValues) < 0) { best = item; Array.Copy(values, bestValues, values.Length); }
            }
            return best;
        }

        /// <summary><paramref name="items"/> (in arrival order) best first, ties in arrival order, into <paramref name="into"/>
        /// (cleared first).</summary>
        public void Sort(IEnumerable<Item> items, Element holder, List<Item> into)
        {
            into.Clear();
            sorting.Clear();
            foreach (var item in items) sorting.Add((item, sorting.Count));
            int n = sorting.Count, nk = keys.Length;
            if (sortValues.Length < n * nk) sortValues = new double[Math.Max(n * nk, 2 * sortValues.Length)];
            var one = new double[nk];
            for (int i = 0; i < n; i++)
            {
                Read(sorting[i].item, holder, one);
                Array.Copy(one, 0, sortValues, i * nk, nk);
            }
            var sv = sortValues;
            sorting.Sort((a, b) =>
            {
                for (int k = 0; k < nk; k++)
                {
                    int c = sv[a.arrival * nk + k].CompareTo(sv[b.arrival * nk + k]);
                    if (c != 0) return descending[k] ? -c : c;
                }
                return a.arrival.CompareTo(b.arrival);
            });
            foreach (var s in sorting) into.Add(s.item);
            sorting.Clear();
        }
    }
}
