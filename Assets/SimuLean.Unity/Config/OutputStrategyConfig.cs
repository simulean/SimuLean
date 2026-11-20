using System;
using UnityEngine;
using SimuLean;

namespace SimuLean.Unity
{
    /// <summary>
    /// Serializable configuration for output routing strategies.
    /// Used in Unity Inspector to configure how items are routed to multiple outputs.
    /// </summary>
    [Serializable]
    public class OutputStrategyConfig
    {
        [Tooltip("Type of output routing strategy")]
        public OutputStrategyType strategyType = OutputStrategyType.FirstAvailable;

        [Tooltip("Parameter key for parameterized routing (only used with Parameterized type)")]
        public string parameterKey = "routing_mode";

        [Tooltip("For Expression: the output index (0 = first) from the item and the element, e.g. item.color == 'red' ? 0 : 1")]
        [SimuLean.Unity.SimuLeanExpression("routing")]
        public string routingExpression = "";

        [Tooltip("For Probability: the weight of each next element, in the order of Next Elements (they need not add up to 100).")]
        public System.Collections.Generic.List<float> weights = new System.Collections.Generic.List<float>();

        [Tooltip("For BestScore: the score of each next element (out.x = the next element, item.x = the item), e.g. out.speed - out.content; the highest wins, ties by the order of Next Elements")]
        [SimuLean.Unity.SimuLeanExpression("score")]
        public string scoreExpression = "";

        [Tooltip("For BestScore: when the best next element is full, Skip it (score only those with room) or Wait for it")]
        [SimuLeanHelp("BestScore and Rule only (Probability keeps its draw and waits for that output; FirstAvailable and the others " +
                      "pass over a full one). Skip scores only the outputs that can take the item now (room, admission, not stopped); Wait scores every " +
                      "output whose admission accepts the item and waits for the best. An output whose admission refuses the item is never " +
                      "chosen in either mode.")]
        public BestScoreFull whenBestFull = BestScoreFull.Skip;

        [Tooltip("For Rule: the routing rule's type name (a class implementing IRoutingRule in Assets/SimuLean.UserLogic), e.g. ShortestQueueRule")]
        public string rule = "";

        [Tooltip("For Rule: the rule's parameters (its [RuleParameter] fields) and their values; one not listed keeps the rule's own value")]
        public System.Collections.Generic.List<RuleParameterValue> ruleParameters = new System.Collections.Generic.List<RuleParameterValue>();

    }

    /// <summary>
    /// Types of output routing strategies available in the Unity Inspector.
    /// </summary>
    public enum OutputStrategyType
    {
        [Tooltip("Sends to the first output that can accept the item")]
        FirstAvailable,

        [Tooltip("Sends to the output with the shortest queue")]
        ShortestQueue,

        [Tooltip("Cycles through outputs in sequence")]
        RoundRobin,

        [Tooltip("Sends to the output with the most available capacity")]
        MostAvailableCapacity,

        [Tooltip("Routes based on item priority (high priority goes first)")]
        PriorityBased,

        [Tooltip("Uses a model parameter to determine routing strategy at runtime")]
        Parameterized,

        [Tooltip("The output index given by an expression over the item and the element")]
        Expression,

        [Tooltip("Each item to a next element drawn by its weight (percentages)")]
        Probability,

        [Tooltip("The next element with the highest score (an expression per next element: out.x, item.x)")]
        BestScore,

        [Tooltip("A choice rule (C# in Assets/SimuLean.UserLogic) scores each next element; the highest wins")]
        Rule
    }
}
