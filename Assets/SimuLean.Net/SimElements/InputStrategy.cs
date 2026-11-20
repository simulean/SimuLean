using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Decides whether an element admits an item. One question, asked by the link layer before every hand-over
    /// (<see cref="Element.CanAccept"/> / <see cref="Element.ValidateInput"/>): <see cref="Admits"/> with the receiving
    /// element and the sender (null when the item does not come through a link). Capacity is not its concern — that is
    /// the element's own <see cref="Element.CheckAvailability"/>.
    /// </summary>
    public abstract class InputStrategy
    {
        /// <summary>True when <paramref name="target"/> admits <paramref name="item"/> sent by <paramref name="origin"/>.</summary>
        public abstract bool Admits(Element target, Item item, Element origin);

        /// <summary>A main item arrived and may set what is admitted next (Combiner pull modes). Default: nothing.</summary>
        public virtual void UpdateStrategy(Item item)
        {
        }

        /// <summary>An independent copy (each assembly of a Combiner updates its own). Default: a shallow copy;
        /// strategies with mutable collections override it.</summary>
        public virtual InputStrategy Copy() => (InputStrategy)MemberwiseClone();
    }

    /// <summary>Admits every item.</summary>
    public class DefaultStrategy : InputStrategy
    {
        public override bool Admits(Element target, Item item, Element origin) => true;
    }

    /// <summary>
    /// Admits items whose label equals a value — fixed, or (without a value) taken from the last main item
    /// (<see cref="UpdateStrategy"/>, Combiner pull mode "auto-update"; nothing is admitted until then).
    /// A number matches a numeric label and a text a text label (<c>sku = 'Garments'</c>). A fixed value written as text that reads as a number
    /// (<c>"2"</c>, <c>"2,5"</c>) matches the numeric label of that value or the text label spelled the same.
    /// </summary>
    public class SingleLabelStrategy : InputStrategy
    {
        private readonly string requiredLabelName;
        private double? requiredNumber;
        private string requiredText;
        private bool warnedMissing;

        /// <summary>A numeric value (or none: taken from the main item).</summary>
        public SingleLabelStrategy(string requiredLabelName, double? requiredLabelValue = null)
        {
            this.requiredLabelName = requiredLabelName;
            requiredNumber = requiredLabelValue;
        }

        /// <summary>A value given as text: a text that reads as a number matches that numeric label too.</summary>
        public SingleLabelStrategy(string requiredLabelName, string requiredLabelValue)
        {
            this.requiredLabelName = requiredLabelName;
            if (string.IsNullOrEmpty(requiredLabelValue)) return;
            requiredText = requiredLabelValue;
            if (SimuLean.Data.ModelTable.TryNumber(requiredLabelValue, out double v)) requiredNumber = v;
        }

        public string LabelName => requiredLabelName;

        public override void UpdateStrategy(Item item)
        {
            requiredNumber = null;
            requiredText = null;
            if (item != null && item.HasProperties)
            {
                if (item.Properties.TryGetDouble(requiredLabelName, out double d)) { requiredNumber = d; return; }
                if (item.Properties.TryGetString(requiredLabelName, out string s)) { requiredText = s; return; }
            }
            if (!warnedMissing)
            {
                warnedMissing = true;
                SimLog.Warning($"[SingleLabel] main item {item?.GetId()} has no label '{requiredLabelName}': its assembly admits no component.");
            }
        }

        public override bool Admits(Element target, Item item, Element origin)
        {
            if (item == null || !item.HasProperties) return false;
            if (requiredNumber.HasValue && item.Properties.TryGetDouble(requiredLabelName, out double v) && v == requiredNumber.Value)
                return true;
            return requiredText != null && item.Properties.TryGetString(requiredLabelName, out string t)
                && string.Equals(t, requiredText, StringComparison.Ordinal);
        }
    }

    /// <summary>Admits items matching any of several label values (OR): a number matches a numeric label, a text a text label.</summary>
    public class MultiLabelStrategy : InputStrategy
    {
        private readonly Dictionary<string, object> acceptedLabels;

        /// <param name="acceptedLabels">Label name → accepted value, e.g. { "Color": "Red", "Size": 10 }.</param>
        public MultiLabelStrategy(Dictionary<string, object> acceptedLabels)
        {
            this.acceptedLabels = new Dictionary<string, object>(acceptedLabels);
        }

        public override InputStrategy Copy() => new MultiLabelStrategy(acceptedLabels);

        public override bool Admits(Element target, Item item, Element origin)
        {
            if (!item.HasProperties) return false;
            var props = item.Properties;
            foreach (var kvp in acceptedLabels)
            {
                if (kvp.Value is double d) { if (props.TryGetDouble(kvp.Key, out double v) && v.Equals(d)) return true; }
                else if (kvp.Value is string s) { if (props.TryGetString(kvp.Key, out string t) && t == s) return true; }
                else if (props.Has(kvp.Key) && props.Get(kvp.Key).Equals(kvp.Value)) return true;
            }
            return false;
        }

        public override void UpdateStrategy(Item item)
        {
            if (!item.HasProperties) return;
            foreach (var key in new List<string>(acceptedLabels.Keys))
                if (item.Properties.Has(key)) acceptedLabels[key] = item.Properties.Get(key);
        }
    }

    /// <summary>Admits only items sent by the named elements (items that do not come through a link are admitted).</summary>
    public class OriginNameInputStrategy : InputStrategy
    {
        private readonly HashSet<string> allowedOriginNames;

        public OriginNameInputStrategy(HashSet<string> allowedOriginNames)
        {
            this.allowedOriginNames = new HashSet<string>(allowedOriginNames);
        }

        public OriginNameInputStrategy(string allowedOriginName)
        {
            this.allowedOriginNames = new HashSet<string> { allowedOriginName };
        }

        public override bool Admits(Element target, Item item, Element origin)
            => origin == null || allowedOriginNames.Contains(origin.Name);
    }

    /// <summary>Admits items while the receiving element holds fewer than <c>maxQueue</c> items.</summary>
    public class MaxQueueInputStrategy : InputStrategy
    {
        private readonly int maxQueue;

        public MaxQueueInputStrategy(int maxQueue)
        {
            this.maxQueue = maxQueue;
        }

        public override bool Admits(Element target, Item item, Element origin) => target.GetQueueLength() < maxQueue;
    }

    /// <summary>
    /// Admits an item when a condition over it and the receiving element holds (the common expression syntax): e.g.
    /// <c>item.color == 'red' &amp;&amp; item.weight &lt;= element.maxWeight</c>.
    /// </summary>
    public class ConditionStrategy : InputStrategy
    {
        private readonly SimuLean.Expressions.EntityScope scope = new SimuLean.Expressions.EntityScope();

        public ConditionStrategy(string condition, string owner = null)
        { Condition = SimuLean.Expressions.Expression.ParseCondition(condition, SimuLean.Expressions.ExpressionContext.Admission, owner, "condition"); }

        public SimuLean.Expressions.Expression Condition { get; }

        public override bool Admits(Element target, Item item, Element origin)
        {
            scope.RandomStream = target?.RandomStreamFor("admission");
            return Condition.Condition(scope.Set(item, target, origin));
        }
    }
}
