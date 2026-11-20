using System;
using System.Collections.Generic;

namespace SimuLean.Expressions
{
    /// <summary>
    /// The usual scope of an expression: the <see cref="Item"/> being decided on and the <see cref="Element"/> deciding
    /// (the station processing it, the element routing it, the station a request comes from). A bare name reads the
    /// item's property. Reusable: set <see cref="Item"/> / <see cref="Element"/> and evaluate (no allocation).
    /// </summary>
    public sealed class EntityScope : IExpressionScope, IDataScope, IRandomScope, IModelScope
    {
        /// <summary>The element's model.</summary>
        public SimuLean.SimContext Model => Element?.GetSimClock()?.Context;

        /// <summary>The element's own stream (<see cref="SimuLean.Element.ExpressionRandom"/>), or the one set in
        /// <see cref="RandomStream"/>.</summary>
        public SimuLean.Stochastics.Streams.IRandomEngine Random => RandomStream ?? Element?.ExpressionRandom;

        /// <summary>Another stream for the random functions (null = the element's): what observes the model — a tally —
        /// draws from its own, so observing does not change the model.</summary>
        public SimuLean.Stochastics.Streams.IRandomEngine RandomStream;

        /// <summary>The model's data tables: the element's clock's.</summary>
        public SimuLean.Data.ModelData Data => Element?.GetSimClock()?.Context.Data;

        public Item Item;
        public Element Element;
        /// <summary>The element the item comes from (<c>origin.x</c>): the sender in an admission condition, the carrier's
        /// origin in a milk run's filter; null elsewhere.</summary>
        public Element Origin;
        /// <summary>The output a best-score routing is scoring (<c>out.x</c>); null elsewhere.</summary>
        public Element Out;

        /// <summary>An item's built-in values (when it has no property of that name): see <see cref="OfItem"/>.</summary>
        public static readonly IReadOnlyList<string> ItemBuiltIns = new[] { "id", "type", "priority", "created", "age" };
        /// <summary>An element's built-in values (when it has no property of that name): see <see cref="OfElement"/>.</summary>
        public static readonly IReadOnlyList<string> ElementBuiltIns = new[] { "name", "state", "content", "free" };

        public EntityScope Set(Item item, Element element, Element origin = null) { Item = item; Element = element; Origin = origin; return this; }

        public double Now => Element?.GetSimClock()?.GetSimulationTime() ?? 0;

        public ExprValue Get(string root, string name)
        {
            switch (root)
            {
                case null:
                case "item": return OfItem(Item, name, Now);
                case "element": return OfElement(Element, name);
                case "origin": return OfElement(Origin, name);
                case "out": return OfElement(Out, name);
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
                case "type": return ExprValue.Of(item.Name ?? "");
                case "priority": return ExprValue.Of(item.priority);
                case "created": return ExprValue.Of(item.GetCreationTime());
                case "age": return ExprValue.Of(now - item.GetCreationTime());
            }
            return ExprValue.None;
        }

        /// <summary>An element's property, else its built-in value of that name (name, state, content, free).</summary>
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
                case "name": return ExprValue.Of(element.Name ?? "");
                // its current state's name, a text: 'IDLE', 'PROCESSING', 'BLOCKED'... (compare with == / !=, exact case)
                case "state": return ExprValue.Of(element.CurrentState.Name ?? "");
                case "content": return ExprValue.Of(element.GetQueueLength());
                case "free": return ExprValue.Of(element.GetFreeCapacity());
            }
            return ExprValue.None;
        }
    }

    /// <summary>
    /// A value given by an expression over the item and the element — process / cycle times, transport times
    /// (the common expression syntax): <c>processingTime</c>, <c>item.weight * element.secondsPerKg + 2</c>,
    /// <c>item.color == 'red' ? 5 : 3</c>. A negative result counts as 0; a NaN or infinite one stops the run
    /// (<see cref="ExpressionException"/>). Use <see cref="Samplers.Sample"/>.
    /// </summary>
    public sealed class ExpressionSampler : IRandomSampler, SimuLean.Stochastics.Streams.IRandomStreamUser
    {
        private readonly EntityScope scope = new EntityScope();
        private SimuLean.Stochastics.Streams.IRandomEngine engine;

        /// <summary>The stream of its random functions, given by the model as to any sampler (owner / purpose of the use);
        /// null = the element's fallback stream.</summary>
        public void SetRandomEngine(SimuLean.Stochastics.Streams.IRandomEngine e) => engine = e;

        public ExpressionSampler(string expression) : this(expression, null, null) { }

        /// <summary>A time written in <paramref name="owner"/>'s field <paramref name="field"/> (checked when the model is built).</summary>
        public ExpressionSampler(string expression, string owner, string field)
        { Expression = Expressions.Expression.Parse(expression, ExpressionContext.Time, owner, field); }

        public Expression Expression { get; }

        /// <summary>Without an item or element: evaluated over nothing (missing values = 0).</summary>
        public double NextValue() => NextValue(null, null);

        public double NextValue(Item item, Element element)
        {
            scope.RandomStream = engine;
            double v = Expression.Number(scope.Set(item, element));
            scope.Set(null, null);
            scope.RandomStream = null;
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
