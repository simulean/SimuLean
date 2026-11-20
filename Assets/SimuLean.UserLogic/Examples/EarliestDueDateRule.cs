using SimuLean.Rules;

namespace SimuLean.UserLogic.Examples
{
    /// <summary>
    /// Example release rule: a Queue offers first the item with the earliest due date (the item label
    /// <see cref="DueLabel"/>; a missing one counts as 0, as in an order key). It releases exactly as the declarative
    /// <c>offerSort = due</c> — which is what a model should use: this rule is here to show the contract.
    /// </summary>
    [ChoiceRule("Earliest due date", Why = "Example of the contract; the declarative form is the Queue's offerSort = due.",
                Reads = new[] { "item.due" }, Equivalent = "the Queue's offerSort = due")]
    public sealed class EarliestDueDateRule : IReleaseRule
    {
        /// <summary>The item label holding the due date.</summary>
        [RuleParameter("Due-date label")] public string DueLabel = "due";

        public double Score(in ChoiceContext context, in ItemView item)
        {
            double due = item.Get(DueLabel);
            return double.IsNaN(due) ? 0 : -due;
        }
    }
}
