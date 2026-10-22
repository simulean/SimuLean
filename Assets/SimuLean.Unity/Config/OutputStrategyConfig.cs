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
        public string routingExpression = "";

        [Tooltip("For Probability: the weight of each next element, in the order of Next Elements (they need not add up to 100).")]
        public System.Collections.Generic.List<float> weights = new System.Collections.Generic.List<float>();

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
        Probability
    }
}
