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
            if (random == null) random = source.GetSimClock().Context.CreateRandomStream(source.Name, "routing");
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
    /// Shortest queue strategy: routes to the output (that can accept the item) with the shortest queue. <b>Ties</b> are
    /// broken in turn: among the outputs tied for the shortest queue, the first one after the output chosen
    /// last, in connection order, wins — so equal outputs share the work instead of the first taking every tie (two
    /// equal aisles got 90 % / 10 %). Deterministic: the first decision goes to the first tied output.
    /// </summary>
    public class ShortestQueueStrategy : IOutputStrategy
    {
        private int lastChosen = -1;

        public int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
        {
            int n = outputs.Count;
            if (n == 0) return -1;
            int start = lastChosen + 1 < n && lastChosen >= 0 ? lastChosen + 1 : 0;
            int minIndex = -1;
            int minQueue = int.MaxValue;

            for (int k = 0; k < n; k++)
            {
                int i = (start + k) % n;
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

            if (minIndex >= 0) lastChosen = minIndex;
            return minIndex;
        }
    }

    /// <summary>
    /// Most available capacity strategy: routes to the output with the most free capacity (not counting the places
    /// promised to loads on their way: <see cref="Element.RoomPromised"/>).
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
                    int freeCapacity = outputs[i].FreeCapacityBeyondPromises();   // places promised to loads on their way are taken
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
    /// <para><b>Random functions draw once per item.</b> An expression is evaluated again each time its item is
    /// offered (the chosen output was full, the item waits and is offered again). Its random draws (<c>rand()</c>,
    /// <c>uniform</c>, <c>pick</c>…) are recorded per item and replayed on every later evaluation until the item leaves
    /// (<see cref="IRoutingMemory.Forget"/>), so a blocked item keeps its draw — before, it drew anew on every retry and the
    /// realised split leaned towards the less blocked output. The rest of the expression (content of other elements, labels,
    /// <c>now()</c>) is read live; the first evaluation draws exactly what it drew before.</para>
    /// </summary>
    public class ExpressionRoutingStrategy : IOutputStrategy, IRuleRouting, IRoutingMemory
    {
        private readonly SimuLean.Expressions.Expression expression;
        private readonly SimuLean.Expressions.EntityScope scope = new SimuLean.Expressions.EntityScope();
        private readonly Dictionary<double, int> map;
        private readonly Dictionary<Item, List<double>> draws = new Dictionary<Item, List<double>>();
        private readonly ReplayedDraws replay = new ReplayedDraws();

        public int DefaultIndex { get; set; } = -1;

        public ExpressionRoutingStrategy(string expression, Dictionary<double, int> valueToOutput = null, string owner = null)
        {
            this.expression = SimuLean.Expressions.Expression.Parse(expression, SimuLean.Expressions.ExpressionContext.Routing, owner, "routing");
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
            var stream = source?.RandomStreamFor("routing");
            double v;
            if (stream == null || item == null) { scope.RandomStream = stream; v = expression.Number(scope.Set(item, source)); }
            else
            {
                draws.TryGetValue(item, out var recorded);
                replay.Begin(stream, recorded);
                scope.RandomStream = replay;
                try { v = expression.Number(scope.Set(item, source)); }
                finally { scope.RandomStream = null; }
                if (recorded == null && replay.Recorded != null) draws[item] = replay.Recorded;
                replay.End();
            }
            bool mapped = map == null || map.ContainsKey(v);
            int index = map != null ? (map.TryGetValue(v, out int i) ? i : DefaultIndex) : (int)Math.Floor(v);
            if (index < 0 || index >= outputs.Count) index = DefaultIndex;
            if (index < 0 || index >= outputs.Count)
            {
                Unroutable(outputs.Count, item, source, v, mapped);
                return -1;
            }
            Unrouted = null;
            return index;
        }

        /// <summary>Why the last item could not be routed: "routing gives output 5 of 2 (...)", or null when the last
        /// evaluation named an output. The diagnosis reports it instead of "waits for" the outputs.</summary>
        public string Unrouted { get; private set; }

        private HashSet<double> warned;

        // an index that names no output holds the item for ever: say so once per element and value
        private void Unroutable(int outputs, Item item, Element source, double v, bool mapped)
        {
            string value = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            Unrouted = (mapped ? $"routing gives output {value} of {outputs} (0 to {outputs - 1})" : $"routing gives {value}, which is not mapped to an output")
                       + $" for '{expression.Text}': the item waits";
            if (warned == null) warned = new HashSet<double>();
            if (!warned.Add(v)) return;
            var clock = source?.GetSimClock();
            string at = clock != null ? "t=" + clock.GetSimulationTime().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " " : "";
            string message = $"[SimuLean] {at}Element '{source?.Name}': {Unrouted} (item {item?.GetId().ToString() ?? "-"}); later items with this value wait too, without another warning.";
            if (clock != null) clock.Context.Warn(message); else SimLog.Warning(message);
        }

        public void Forget(Item item) => draws.Remove(item);

        /// <summary>Items whose draws are kept (they have not left yet).</summary>
        public int Remembered => draws.Count;

        /// <summary>A stream that gives the draws recorded for an item first, then draws (and records) new ones.</summary>
        private sealed class ReplayedDraws : SimuLean.Stochastics.Streams.IRandomEngine
        {
            private SimuLean.Stochastics.Streams.IRandomEngine stream;
            private int next;
            public List<double> Recorded { get; private set; }

            public void Begin(SimuLean.Stochastics.Streams.IRandomEngine s, List<double> recorded) { stream = s; Recorded = recorded; next = 0; }
            public void End() { stream = null; Recorded = null; }

            public double NextDouble()
            {
                if (Recorded != null && next < Recorded.Count) return Recorded[next++];
                double u = stream.NextDouble();
                (Recorded ?? (Recorded = new List<double>())).Add(u);
                next++;
                return u;
            }

            public int NextInt(int minValue, int maxValue) => minValue + (int)Math.Floor(NextDouble() * (maxValue - minValue));
            public void SetSeed(int seed) => throw new InvalidOperationException("A routing's replayed draws have no seed.");
            public double NextStandardNormal()
            {
                double u1 = 1.0 - NextDouble(), u2 = NextDouble();
                return SimuLean.Stochastics.Distributions.BoxMuller.Standard(u1, u2);
            }
        }
    }

    /// <summary>What a best-score routing does when the output with the best score cannot take the item now.</summary>
    public enum BestScoreFull
    {
        /// <summary>Only the outputs that can take the item now are scored; the best of them wins (none: the item waits).</summary>
        Skip,
        /// <summary>Every output that admits the item is scored; the item waits for the best one when it is full (scored
        /// again each time the item is offered, so the best may change while it waits).</summary>
        Wait,
    }

    /// <summary>
    /// Best-score routing: <c>scoreExpression</c> is evaluated once per output
    /// over the item (<c>item.x</c>, bare names), the routing element (<c>element.x</c>) and the output scored
    /// (<c>out.x</c>: its properties and built-ins <c>name</c>, <c>state</c>, <c>content</c>, <c>free</c>; and model reads
    /// such as <c>count(out.name, 'family', item.family)</c>, <c>stat(out.name, 'utilization')</c>); the highest score wins,
    /// <b>ties go to the first in connection order</b>, a missing value scores 0. <see cref="BestScoreFull.Skip"/> (this
    /// class) scores only the outputs that can take the item now; <see cref="BestScoreWaitRoutingStrategy"/> scores every
    /// output that admits it and waits for the best. To keep an item from an output, use that output's admission. No random
    /// functions (the context refuses them): the score is evaluated again each time the item is offered.
    /// </summary>
    public class BestScoreRoutingStrategy : IOutputStrategy
    {
        private readonly SimuLean.Expressions.Expression score;
        private readonly SimuLean.Expressions.EntityScope scope = new SimuLean.Expressions.EntityScope();

        public BestScoreRoutingStrategy(string expression, string owner = null)
        {
            score = SimuLean.Expressions.Expression.Parse(expression, SimuLean.Expressions.ExpressionContext.Score, owner, "scoreExpression");
        }

        /// <summary>The score's text.</summary>
        public string Expression => score.Text;

        public virtual int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
            => Best(outputs, item, source, true);

        /// <summary>The index of the best-scored output among those that can take the item now (<paramref name="needRoom"/>)
        /// or that admit it, the first one on a tie (−1: none).</summary>
        protected int Best(List<Element> outputs, Item item, Element source, bool needRoom)
        {
            int best = -1;
            double bestScore = 0;
            var counters = source?.Counters;   // read-only: the scores of the choice (logic viewer)
            counters?.BeginScores(outputs.Count);
            for (int i = 0; i < outputs.Count; i++)
            {
                var d = outputs[i];
                if (needRoom ? !d.CanAccept(item, source) : !d.ValidateInput(item, source)) continue;
                double v = Score(item, source, d);
                counters?.Score(i, v);
                if (best < 0 || v > bestScore) { best = i; bestScore = v; }
            }
            counters?.EndScores(best);
            return best;
        }

        /// <summary>The score of <paramref name="output"/> for <paramref name="item"/> routed by <paramref name="source"/>.</summary>
        public double Score(Item item, Element source, Element output)
        {
            scope.Set(item, source);
            scope.Out = output;
            try { return score.Number(scope); }
            finally { scope.Set(null, null); scope.Out = null; }
        }
    }

    /// <summary>Best-score routing that waits for the best output (<see cref="BestScoreFull.Wait"/>): a routing by rule —
    /// the output it names over every destination that admits the item, full or not.</summary>
    public sealed class BestScoreWaitRoutingStrategy : BestScoreRoutingStrategy, IRuleRouting
    {
        public BestScoreWaitRoutingStrategy(string expression, string owner = null) : base(expression, owner) { }

        public override int SelectOutput(List<Element> outputs, Item item, Element source, OutputStrategyContext context)
        {
            int r = Route(outputs, item, source);
            if (r >= 0 && outputs[r].CanAccept(item, source)) return r;
            if (r >= 0) source?.Counters.HeldBack();   // read-only counter: held for its full best output
            return -1;
        }

        public int Route(List<Element> outputs, Item item, Element source) => Best(outputs, item, source, false);
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
