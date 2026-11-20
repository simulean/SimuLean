using SimuLean;

namespace SimuLean.Serialization
{
    internal sealed class QueueHandler : ElementHandlerBase
    {
        public override string TypeName => "Queue";

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<QueueSettings>();
            int capacity = s.Capacity;

            InputStrategy inputStrat = ElementBuildHelpers.BuildInputStrategy(config);

            var queue = new ItemsQueue(
                capacity:      capacity,
                myName:        config.Name,
                sClock:        clock,
                inputStrategy: inputStrat,
                vElement:      vElement);

            queue.OfferOrder = s.OfferOrder;
            queue.OfferScan = System.Math.Max(0, s.OfferScan);
            queue.OfferSort = ItemOrder.Parse(s.OfferSort, config.Name);   // best first; empty = arrival order
            if (!string.IsNullOrWhiteSpace(s.OfferRule))
            {
                // a release rule instead of the order keys
                string owner = $"Element '{config.Name}'";
                if (queue.OfferSort != null)
                    throw new System.FormatException($"{owner}: give an offerSort or an offerRule, not both.");
                var rule = (SimuLean.Rules.IReleaseRule)SimuLean.Rules.ChoiceRules.Create(s.OfferRule, SimuLean.Rules.RuleSite.Release, s.Rule,
                                                                                          SimuLean.Rules.ChoiceRules.RulesOf(s), owner);
                queue.OfferSort = new SimuLean.Rules.RuleItemOrder(rule, config.Name);
            }
            queue.Gated = s.Gated;               // items leave only when released (a reaction's Release)
            queue.GroupBySetup = s.GroupBySetup;            // offer first the items a receiver is set up for
            queue.GroupLimit = System.Math.Max(0, s.GroupLimit);
            return queue;
        }
    }
}
