// The logic view reads a choice text (a routing, an admission, a timeout action) the way the model builder reads it: a
// number, spaces around a name or another case mean what the built model does, never what the text looks like.
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Logic;
using SimuLean.Serialization;

static partial class Program
{
    // src -> router (the routing under test) -> a | b -> sink
    static SimulationConfig RoutedModel(string strategyType)
    {
        var c = new SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
        c.Elements.Add(Arrivals("src", 0, 1));
        var router = Cfg("router", "Queue");
        router.Set("outputStrategy.strategyType", strategyType);
        router.Set("outputStrategy.parameterKey", "dest");
        router.Set("outputStrategy.routingExpression", "item.k");
        router.Set("outputStrategy.weights", new[] { 1.0, 3.0 });
        router.Set("outputStrategy.scoreExpression", "-out.content");
        c.Elements.Add(router);
        c.Elements.Add(Station("a", 1));
        c.Elements.Add(Station("b", 1));
        c.Elements.Add(Cfg("sink", "Sink"));
        c.Connections.Add(new ConnectionConfig("src", "router"));
        foreach (var to in new[] { "a", "b" }) { c.Connections.Add(new ConnectionConfig("router", to)); c.Connections.Add(new ConnectionConfig(to, "sink")); }
        return c;
    }

    static void TestChoicesReadAsBuilt()
    {
        // routing: the sentence of the strategy the builder builds
        var routingSays = new Dictionary<Type, string>
        {
            [typeof(FirstAvailableStrategy)] = "Route first available next element",
            [typeof(ShortestQueueStrategy)] = "Route to the shortest queue",
            [typeof(RoundRobinStrategy)] = "Route in turn (round robin)",
            [typeof(MostAvailableCapacityStrategy)] = "Route to the most free room",
            [typeof(PriorityRoutingStrategy)] = "Route by the item's priority",
            [typeof(ParameterizedRoutingStrategy)] = "Route by item.dest",
            [typeof(ExpressionRoutingStrategy)] = "Route to output number item.k",
            [typeof(ProbabilityRoutingStrategy)] = "Route by weights 1, 3",
            [typeof(BestScoreRoutingStrategy)] = "Route best score: -out.content · if best is full: skip",
        };
        foreach (string text in new[] { "ShortestQueue", "1", " 2 ", "RoundRobin ", "3", "4", "5", "6", "7", "8", "99", "-1", "1,2",
                                        "shortestqueue", "parameterized", "Probability", "", "0", "FirstAvailable" })
        {
            var c = RoutedModel(text);
            var built = ElementBuildHelpers.BuildOutputStrategy(c.Elements.First(e => e.Id == "router")).GetType();
            var routing = ModelLogic.Of(c).Find("router").Decisions.FirstOrDefault(d => d.Site == "routing");
            Check(routing != null && routing.Sentence == routingSays[built],
                  $"strategyType '{text}': the builder builds {built.Name}, the logic says '{routing?.Sentence}'");
        }

        // admission: the sentence of the admission the builder builds (the default one: no decision, or every item)
        var admissionSays = new Dictionary<Type, string>
        {
            [typeof(SingleLabelStrategy)] = "Admit items with item.family = A",
            [typeof(MultiLabelStrategy)] = "Admit items with any of",
            [typeof(ConditionStrategy)] = "Admit when item.k > 0",
        };
        foreach (string text in new[] { "Condition", "3", " 3", "condition", "1", "2", "9", "SingleLabel", "Default", "0" })
        {
            var c = RoutedModel("FirstAvailable");
            var a = c.Elements.First(e => e.Id == "a");
            a.Set("inputStrategy.strategyType", text);
            a.Set("inputStrategy.singleLabelName", "family");
            a.Set("inputStrategy.singleLabelValue", "A");
            a.Set("inputStrategy.multiLabelPairs", new List<LabelValueSettings> { new LabelValueSettings { Label = "family", Value = "B" } });
            a.Set("inputStrategy.condition", "item.k > 0");
            var built = ElementBuildHelpers.BuildInputStrategy(a).GetType();
            var admission = ModelLogic.Of(c).Find("a").Decisions.FirstOrDefault(d => d.Site == "admission");
            bool agrees = built == typeof(DefaultStrategy)
                ? admission == null || admission.Sentence == "Admit every item"
                : admission != null && admission.Sentence.StartsWith(admissionSays[built], StringComparison.Ordinal);
            Check(agrees, $"inputStrategy.strategyType '{text}': the builder builds {built.Name}, the logic says '{admission?.Sentence}'");

            // the wake-up map follows the built admission: a Condition one reading a property is woken by its writes
            c.Elements.First(e => e.Id == "a").Set("inputStrategy.condition", "element.open == 1");
            bool woken = ModelLogic.Of(c).WakeUps.Any(w => w.Element == "a");
            Check(woken == (built == typeof(ConditionStrategy)), $"inputStrategy.strategyType '{text}': in the wake-up map {woken}, built {built.Name}");
        }

        // timeout action: what the builder reads (WaitLimit.ActionOf) is what the wait says
        var waitSays = new Dictionary<TimeoutAction, string>
        {
            [TimeoutAction.KeepWaiting] = "Wait at most 5 · then keep waiting",
            [TimeoutAction.GiveUp] = "Wait at most 5 · then give up → b",   // a Queue that gives up sends the item to its target
            [TimeoutAction.Reroute] = "Wait at most 5 · then leave → b",
        };
        foreach (string text in new[] { "GiveUp", "giveup", " GiveUp", "1", "2", "Reroute", "reroute", "KeepWaiting", "0" })
        {
            var c = RoutedModel("FirstAvailable");
            var router = c.Elements.First(e => e.Id == "router");
            router.Set("maxWait", "5");
            router.Set("onTimeout", text);
            router.Set("timeoutTarget", "b");
            var built = WaitLimit.ActionOf(text, "router");
            var wait = ModelLogic.Of(c).Find("router").Decisions.FirstOrDefault(d => d.Site == "wait");
            bool agrees = built == TimeoutAction.KeepWaiting
                ? wait != null && (wait.Sentence == "Wait at most 5" || wait.Sentence == waitSays[built])
                : wait != null && wait.Sentence == waitSays[built];
            Check(agrees, $"onTimeout '{text}': the builder reads {built}, the logic says '{wait?.Sentence}'");
        }
    }
}
