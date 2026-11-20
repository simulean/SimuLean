using SimuLean.Rules;

namespace SimuLean.UserLogic.Examples
{
    /// <summary>
    /// Example dispatch rule: a team serves first the request with the highest priority plus
    /// <see cref="AgeWeight"/> times how long it has waited (ageing, so low priorities are not starved). With
    /// <c>AgeWeight = 0</c> it dispatches exactly as the request order <c>|priority desc</c>.
    /// </summary>
    [ChoiceRule("Highest priority, with ageing", Why = "Example of the contract; with AgeWeight = 0 the declarative form is the request order |priority desc.",
                Reads = new[] { "request.priority", "request.age" }, Equivalent = "the team's request order |priority desc")]
    public sealed class HighestPriorityRule : IDispatchRule
    {
        /// <summary>Priority points gained per time unit of waiting.</summary>
        [RuleParameter("Priority per time waited", Min = 0)] public double AgeWeight = 0;

        public double Score(in ChoiceContext context, in RequestView request) => request.Priority + AgeWeight * request.Age;
    }
}
