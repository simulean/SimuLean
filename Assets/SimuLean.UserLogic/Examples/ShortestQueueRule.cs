using SimuLean.Rules;

namespace SimuLean.UserLogic.Examples
{
    /// <summary>
    /// Example routing rule: the output with the fewest items, plus <see cref="FreeWeight"/> times its free
    /// room. With <c>FreeWeight = 0</c> it routes exactly as the declarative <c>BestScore</c> routing with
    /// <c>scoreExpression = -out.content</c> — which is what a model should use: this rule is here to show the contract.
    /// </summary>
    [ChoiceRule("Shortest queue", Why = "Example of the contract; score = -out.content + FreeWeight * out.free. With FreeWeight = 0 the declarative form is " +
                                     "BestScore with scoreExpression = -out.content (with a weight w: -out.content + w * out.free).",
                Reads = new[] { "out.content", "out.free" },
                Equivalent = "outputStrategy.strategyType = BestScore, outputStrategy.scoreExpression = -out.content")]
    public sealed class ShortestQueueRule : IRoutingRule
    {
        /// <summary>How much a unit of free room is worth against an item waiting.</summary>
        [RuleParameter("Weight of free room", Min = 0)] public double FreeWeight = 0;

        public double Score(in ChoiceContext context, in ElementView output) => -output.Content + FreeWeight * output.Free;
    }
}
