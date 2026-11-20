using SimuLean.Stochastics.Distributions;
using SimuLean.Unity;
using System.Collections.Generic;
using System.Globalization;
using System;
using UnityEngine;

namespace SimuLean.Serialization
{
    internal static class ExtractorHelpers
    {
        /// <summary>
        /// Writes a RandomProcessConfig as the element's setting at the path of its field (<c>delayStrategy</c> →
        /// <c>delayStrategy.strategyType</c>, <c>delayStrategy.constantValue</c>…; a typed
        /// <see cref="SamplerSettings"/> with the chosen distribution's fields only; the build side is
        /// <c>ElementBuildHelpers.BuildRandomProcess</c>).
        /// </summary>
        public static void ExtractRandomProcessConfig(
            RandomProcessConfig randomConfig, ElementConfig config, string field)
        {
            if (randomConfig == null) return;
            config.Set(field, randomConfig.ToSettings());
        }

        public static void ExtractOutputStrategyConfig(
            OutputStrategyConfig strategyConfig, ElementConfig elementConfig)
        {
            if (strategyConfig == null) return;
            elementConfig.Set("outputStrategy.strategyType", strategyConfig.strategyType.ToString());
            elementConfig.Set("outputStrategy.parameterKey", strategyConfig.parameterKey ?? "");
            elementConfig.Set("outputStrategy.routingExpression", strategyConfig.routingExpression ?? "");
            if (strategyConfig.strategyType == OutputStrategyType.BestScore)
            {
                elementConfig.Set("outputStrategy.scoreExpression", strategyConfig.scoreExpression ?? "");
                elementConfig.Set("outputStrategy.whenBestFull", strategyConfig.whenBestFull);
            }
            if (strategyConfig.strategyType == OutputStrategyType.Rule)
            {
                // a routing rule (a choice rule): its type name, the full policy and its parameters
                elementConfig.Set("outputStrategy.rule", (strategyConfig.rule ?? "").Trim());
                elementConfig.Set("outputStrategy.whenBestFull", strategyConfig.whenBestFull);
                RuleParameterValue.Extract(elementConfig, strategyConfig.ruleParameters);
            }
            if (strategyConfig.strategyType == OutputStrategyType.Probability)
            {
                // each weight as its shortest text, read back as a double (0.9f is 0.9, as it always was)
                var weights = new List<double>();
                if (strategyConfig.weights != null)
                    foreach (float w in strategyConfig.weights)
                        weights.Add(double.Parse(w.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture));
                elementConfig.Set("outputStrategy.weights", weights.ToArray());
            }
        }

        /// <summary>A MultiLabel admission's records, typed.</summary>
        public static List<LabelValueSettings> LabelValues(List<LabelValuePair> pairs)
        {
            var list = new List<LabelValueSettings>();
            if (pairs != null)
                foreach (var p in pairs)
                    if (p != null) list.Add(LabelValueSettings.Of(p.label, p.value));
            return list;
        }

        public static void ExtractInputStrategyConfig(
            InputStrategyConfig strategyConfig, ElementConfig elementConfig)
        {
            if (strategyConfig == null) return;
            elementConfig.Set("inputStrategy.strategyType", strategyConfig.strategyType.ToString());
            elementConfig.Set("inputStrategy.singleLabelName", strategyConfig.singleLabelName ?? "");
            elementConfig.Set("inputStrategy.condition", strategyConfig.condition ?? "");
            elementConfig.Set("inputStrategy.singleLabelValue", strategyConfig.singleLabelValue ?? "");

            if (strategyConfig.multiLabelPairs != null)
                elementConfig.Set("inputStrategy.multiLabelPairs", LabelValues(strategyConfig.multiLabelPairs));
        }
    }
}
