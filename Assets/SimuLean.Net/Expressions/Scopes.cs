using System;

namespace SimuLean.Expressions
{
    /// <summary>
    /// The usual scope of an expression: the <see cref="Item"/> being decided on and the <see cref="Element"/> deciding
    /// (the station processing it, the element routing it, the station a request comes from). A bare name reads the
    /// item's property. Reusable: set <see cref="Item"/> / <see cref="Element"/> and evaluate (no allocation).
    /// </summary>
    public sealed class EntityScope : IExpressionScope, IDataScope, IRandomScope, IModelScope
    {
        /// <summary>The model's element of that name (<see cref="SimuLean.SimContext.FindElement"/>).</summary>
        public SimuLean.Element FindElement(string name) => Element?.GetSimClock()?.Context.FindElement(name);

        /// <summary>The element's own stream (<see cref="SimuLean.Element.ExpressionRandom"/>), or the one set in
        /// <see cref="RandomStream"/>.</summary>
        public SimuLean.Stochastics.Streams.IRandomEngine Random => RandomStream ?? Element?.ExpressionRandom;

        /// <summary>Another stream for the random functions (null = the element's): what observes the model — a tally —
        /// draws from its own, so observing does not change the model (review2 #27).</summary>
        public SimuLean.Stochastics.Streams.IRandomEngine RandomStream;

        /// <summary>The model's data tables: the element's clock's.</summary>
        public SimuLean.Data.ModelData Data => Element?.GetSimClock()?.Context.Data;

        public Item Item;
        public Element Element;

        public EntityScope Set(Item item, Element element) { Item = item; Element = element; return this; }

        public double Now => Element?.GetSimClock()?.GetSimulationTime() ?? 0;

        public ExprValue Get(string root, string name)
        {
            switch (root)
            {
                case null:
                case "item": return OfItem(Item, name, Now);
                case "element": return OfElement(Element, name);
            }
            return ExprValue.None;
        }

        /// <summary>An item's property, else its built-in value of that name (id, type, priority, created, age).</summary>
        public static ExprValue OfItem(Item item, string name, double now)
        {
            if (item == null) return ExprValue.None;
            if (item.HasProperties)
            {
                var p = item.Properties;
                if (p.TryGetDouble(name, out double d)) return ExprValue.Of(d);
                if (p.TryGetString(name, out string t)) return ExprValue.Of(t);
                if (p.Has(name)) return ExprValue.Of(p.GetText(name));
            }
            switch (name)
            {
                case "id": return ExprValue.Of(item.GetId());
                case "type": return ExprValue.Of(item.GetTypeName() ?? "");
                case "priority": return ExprValue.Of(item.priority);
                case "created": return ExprValue.Of(item.GetCreationTime());
                case "age": return ExprValue.Of(now - item.GetCreationTime());
            }
            return ExprValue.None;
        }

        /// <summary>An element's property, else its built-in value of that name (name, content, free).</summary>
        public static ExprValue OfElement(Element element, string name)
        {
            if (element == null) return ExprValue.None;
            if (element.HasProperties)
            {
                var p = element.Properties;
                if (p.TryGetDouble(name, out double d)) return ExprValue.Of(d);
                if (p.TryGetString(name, out string t)) return ExprValue.Of(t);
                if (p.Has(name)) return ExprValue.Of(p.GetText(name));
            }
            switch (name)
            {
                case "name": return ExprValue.Of(element.GetName() ?? "");
                case "content": return ExprValue.Of(element.GetQueueLength());
                case "free": return ExprValue.Of(element.GetFreeCapacity());
            }
            return ExprValue.None;
        }
    }

    /// <summary>
    /// A value given by an expression over the item and the element — process / cycle times, transport times
    /// (the common expression syntax): <c>processingTime</c>, <c>item.weight * element.secondsPerKg + 2</c>,
    /// <c>item.color == 'red' ? 5 : 3</c>. Negative / NaN results count as 0. Use <see cref="Samplers.Sample"/>.
    /// </summary>
    public sealed class ExpressionSampler : IRandomSampler
    {
        private readonly EntityScope scope = new EntityScope();

        public ExpressionSampler(string expression) { Expression = Expressions.Expression.Parse(expression); }

        public Expression Expression { get; }

        /// <summary>Without an item or element: evaluated over nothing (missing values = 0).</summary>
        public double NextValue() => NextValue(null, null);

        public double NextValue(Item item, Element element)
        {
            double v = Expression.Number(scope.Set(item, element));
            scope.Set(null, null);
            return v > 0 ? v : 0;
        }
    }

    /// <summary>Draws from any sampler, giving expression samplers the item and the element.</summary>
    public static class Samplers
    {
        public static double Sample(IRandomSampler sampler, Item item, Element element)
        {
            if (sampler == null) return 0;
            return sampler is ExpressionSampler es ? es.NextValue(item, element) : sampler.NextValue();
        }
    }
}
