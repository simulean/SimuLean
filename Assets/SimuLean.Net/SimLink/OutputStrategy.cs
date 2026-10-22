using System;
using System.Collections.Generic;

namespace SimuLean
{
    /// <summary>
    /// Context object passed to output strategies containing all necessary information
    /// for making routing decisions.
    /// </summary>
    public class OutputStrategyContext
    {
        public SimClock Clock { get; set; }
        public Element Source { get; set; }
        public List<Element> CandidateOutputs { get; set; }
        public Dictionary<string, ParameterValue> ModelParameters { get; set; }

        public OutputStrategyContext() { }

        public OutputStrategyContext(SimClock clock, Element source, List<Element> candidateOutputs) => Set(source, candidateOutputs, clock);

        /// <summary>Fills the context for one routing decision (links keep one context and refill it on every send,
        /// so routing allocates nothing). The model parameters are the source's model's.</summary>
        public OutputStrategyContext Set(Element source, List<Element> candidateOutputs, SimClock clock = null)
        {
            Clock = clock ?? source?.GetSimClock();
            Source = source;
            CandidateOutputs = candidateOutputs;
            ModelParameters = Clock?.Context?.ModelParameters;
            return this;
        }
    }

    /// <summary>
    /// Base interface for output routing strategies.
    /// Determines which output element should receive an item.
    /// </summary>
    public interface IOutputStrategy
    {
        /// <summary>
        /// Selects the index of the output element that should receive the item.
        /// </summary>
        /// <param name="outputs">List of candidate output elements.</param>
        /// <param name="item">Item to be routed.</param>
        /// <param name="source">Element sending the item.</param>
        /// <param name="context">Additional context for routing decisions.</param>
        /// <returns>Index of the selected output, or -1 if no valid output found.</returns>
        int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context);
    }

    /// <summary>
    /// A strategy that decides once per item and keeps that choice until the item is handed over (a blocked choice is
    /// waited for, not redrawn): the link calls <see cref="Forget"/> when the item has left.
    /// </summary>
    public interface IRoutingMemory
    {
        void Forget(Item item);
    }

    /// <summary>
    /// Percentage routing: each item goes to output <c>i</c> with probability
    /// <c>weights[i] / Σ weights</c> (missing weights = 0), drawn once from the source's stream <c>name/routing</c> and kept
    /// until the item leaves — if that output is busy the item waits for it. Case mix, test validity, a decision.
    /// </summary>
    public class ProbabilityRoutingStrategy : IOutputStrategy, IRoutingMemory, IRuleRouting
    {
        private readonly double[] weights;
        private readonly Dictionary<Item, int> chosen = new Dictionary<Item, int>();
        private SimuLean.Stochastics.Streams.IRandomEngine random;

        public ProbabilityRoutingStrategy(double[] weights)
        {
            this.weights = weights ?? throw new ArgumentNullException(nameof(weights));
            foreach (double w in weights)
                if (double.IsNaN(w) || w < 0) throw new ArgumentException("Routing weights must be numbers ≥ 0.");
        }

        public IReadOnlyList<double> Weights => weights;

        public int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
        {
            if (chosen.TryGetValue(item, out int k)) return k < outputs.Count ? k : -1;
            double total = 0;
            for (int i = 0; i < outputs.Count && i < weights.Length; i++) total += weights[i];
            if (total <= 0) return -1;
            if (random == null) random = source.GetSimClock().Context.CreateRandomStream(source.GetName(), "routing");
            double u = random.NextDouble() * total;
            int pick = 0;
            for (int i = 0; i < outputs.Count && i < weights.Length; i++)
            {
                if (weights[i] <= 0) continue;
                pick = i;
                u -= weights[i];
                if (u < 0) break;
            }
            chosen[item] = pick;
            return pick;
        }

        public void Forget(Item item) => chosen.Remove(item);

        /// <summary>The draw is over every destination (the links that filter the ones with room first must not shorten the
        /// list the weights index).</summary>
        public int Route(List<Element> outputs, Item item, Element source) => SelectOutput(outputs, item, source, null);

        /// <summary>Choices kept for items that have not left yet.</summary>
        public int Remembered => chosen.Count;
    }

    /// <summary>
    /// Default strategy: selects the first available output that can accept the item.
    /// </summary>
    public class FirstAvailableStrategy : IOutputStrategy
    {
        public int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
        {
            for (int i = 0; i < outputs.Count; i++)
            {
                if (outputs[i].CanAccept(item, source))
                    return i;
            }
            return -1;
        }
    }

    /// <summary>
    /// Round-robin strategy: cycles through outputs sequentially.
    /// </summary>
    public class RoundRobinStrategy : IOutputStrategy
    {
        private int currentIndex = 0;

        public int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
        {
            int startIndex = currentIndex;
            
            for (int i = 0; i < outputs.Count; i++)
            {
                int index = (startIndex + i) % outputs.Count;
                if (outputs[index].CanAccept(item, source))
                {
                    currentIndex = (index + 1) % outputs.Count;
                    return index;
                }
            }
            
            return -1;
        }
    }

    /// <summary>
    /// Shortest queue strategy: routes to the output with the shortest queue.
    /// </summary>
    public class ShortestQueueStrategy : IOutputStrategy
    {
        public int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
        {
            int minIndex = -1;
            int minQueue = int.MaxValue;

            for (int i = 0; i < outputs.Count; i++)
            {
                if (outputs[i].CanAccept(item, source))
                {
                    int queueLength = outputs[i].GetQueueLength();
                    if (queueLength < minQueue)
                    {
                        minQueue = queueLength;
                        minIndex = i;
                    }
                }
            }

            return minIndex;
        }
    }

    /// <summary>
    /// Most available capacity strategy: routes to the output with the most free capacity.
    /// </summary>
    public class MostAvailableCapacityStrategy : IOutputStrategy
    {
        public int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
        {
            int maxIndex = -1;
            int maxCapacity = -1;

            for (int i = 0; i < outputs.Count; i++)
            {
                if (outputs[i].CanAccept(item, source))
                {
                    int freeCapacity = outputs[i].GetFreeCapacity();
                    if (freeCapacity > maxCapacity)
                    {
                        maxCapacity = freeCapacity;
                        maxIndex = i;
                    }
                }
            }

            return maxIndex;
        }
    }

    /// <summary>
    /// Model parameter-driven routing strategy.
    /// Uses a model parameter to determine which strategy to apply.
    /// </summary>
    public class ParameterizedRoutingStrategy : IOutputStrategy
    {
        private readonly string parameterKey;
        private readonly IOutputStrategy defaultStrategy;

        // One instance per mode, kept across calls: a stateful strategy (round robin) must
        // remember its position, so it cannot be re-created on every routing decision.
        private readonly ShortestQueueStrategy shortestQueue = new ShortestQueueStrategy();
        private readonly RoundRobinStrategy roundRobin = new RoundRobinStrategy();
        private readonly MostAvailableCapacityStrategy mostCapacity = new MostAvailableCapacityStrategy();

        public ParameterizedRoutingStrategy(string parameterKey, IOutputStrategy defaultStrategy = null)
        {
            this.parameterKey = parameterKey;
            this.defaultStrategy = defaultStrategy ?? new FirstAvailableStrategy();
        }

        public int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
        {
            if (context.ModelParameters != null
                && context.ModelParameters.TryGetValue(parameterKey, out ParameterValue value)
                && value.TryGetString(out string mode))
            {
                // Only string-typed parameters select a routing mode — this preserves
                // the former `value as string` behaviour (non-string values fell
                // through to the default strategy).
                IOutputStrategy strategy = GetStrategyFromMode(mode);
                return strategy.SelectOutput(outputs, item, source, context);
            }

            return defaultStrategy.SelectOutput(outputs, item, source, context);
        }

        private IOutputStrategy GetStrategyFromMode(string mode)
        {
            switch (mode?.ToLower())
            {
                case "shortest_queue":
                    return shortestQueue;
                case "round_robin":
                    return roundRobin;
                case "most_capacity":
                    return mostCapacity;
                default:
                    return defaultStrategy;
            }
        }
    }

    /// <summary>
    /// Item priority-based routing strategy.
    /// Routes high-priority items to specific outputs.
    /// </summary>
    public class PriorityRoutingStrategy : IOutputStrategy
    {
        private readonly ShortestQueueStrategy shortestQueue = new ShortestQueueStrategy();

        public int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
        {
            // High priority items go to first available
            if (item.priority > 0)
            {
                for (int i = 0; i < outputs.Count; i++)
                {
                    if (outputs[i].CanAccept(item, source))
                        return i;
                }
            }
            
            // Low priority items use shortest queue
            return shortestQueue.SelectOutput(outputs, item, source, context);
        }
    }

    /// <summary>
    /// Routes by an expression over the item and the routing element (the common syntax): its value is the output index
    /// (0-based) — e.g. <c>item.color == 'red' ? 0 : 1</c>, or a bare label holding the index — or, with a
    /// <c>value → output</c> map, the key of the map. Unmapped / out-of-range values use <see cref="DefaultIndex"/>
    /// (−1 = wait). The chosen output must be able to accept the item, otherwise the item waits (no fallback), which
    /// keeps routing deterministic.
    /// </summary>
    public class ExpressionRoutingStrategy : IOutputStrategy, IRuleRouting
    {
        private readonly SimuLean.Expressions.Expression expression;
        private readonly SimuLean.Expressions.EntityScope scope = new SimuLean.Expressions.EntityScope();
        private readonly Dictionary<double, int> map;

        public int DefaultIndex { get; set; } = -1;

        public ExpressionRoutingStrategy(string expression, Dictionary<double, int> valueToOutput = null)
        {
            this.expression = SimuLean.Expressions.Expression.Parse(expression);
            map = valueToOutput != null && valueToOutput.Count > 0 ? new Dictionary<double, int>(valueToOutput) : null;
        }

        public int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
        {
            int index = Route(outputs, item, source);
            return index >= 0 && outputs[index].CanAccept(item, source) ? index : -1;
        }

        /// <summary>The output the expression names, whether it can take the item now or not (−1: none).</summary>
        public int Route(List<Element> outputs, Item item, Element source)
        {
            double v = expression.Number(scope.Set(item, source));
            int index = map != null ? (map.TryGetValue(v, out int i) ? i : DefaultIndex) : (int)Math.Floor(v);
            if (index < 0 || index >= outputs.Count) index = DefaultIndex;
            return index < 0 || index >= outputs.Count ? -1 : index;
        }
    }

    /// <summary>
    /// A routing by rule (an expression, a delegate): the output it names for an item, whether that output can take it now
    /// or not — what an item that sets off towards a full destination (a walking patient that waits at the door,
    /// <see cref="WalkLink.WaitAtDoor"/>) follows. <see cref="IOutputStrategy.SelectOutput"/> adds the availability check.
    /// </summary>
    public interface IRuleRouting
    {
        /// <summary>The index of the output for <paramref name="item"/> (−1 = none).</summary>
        int Route(List<Element> outputs, Item item, Element source);
    }

    /// <summary>
    /// Routing written in C#: a delegate receives (outputs, item, source) and returns the
    /// output index (−1 = wait). For model-specific rules built in code (headless builders,
    /// applications) without a new strategy class.
    /// </summary>
    public class DelegateOutputStrategy : IOutputStrategy, IRuleRouting
    {
        private readonly Func<List<Element>, Item, Element, int> select;

        public DelegateOutputStrategy(Func<List<Element>, Item, Element, int> select)
        {
            this.select = select ?? throw new ArgumentNullException(nameof(select));
        }

        public int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
        {
            int index = Route(outputs, item, source);
            return index >= 0 && outputs[index].CanAccept(item, source) ? index : -1;
        }

        public int Route(List<Element> outputs, Item item, Element source)
        {
            int index = select(outputs, item, source);
            return index >= 0 && index < outputs.Count ? index : -1;
        }
    }
}
