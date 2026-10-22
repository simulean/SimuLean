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
        /// Writes a RandomProcessConfig into ElementConfig.Parameters using the given prefix
        /// (e.g. "delay" → key "delayStrategyType", "interArrival" → "interArrivalStrategyType").
        /// </summary>
        public static void ExtractRandomProcessConfig(
            RandomProcessConfig randomConfig, ElementConfig config, string prefix)
        {
            if (randomConfig == null) return;

            string typeKey = prefix + "StrategyType";
            config.Parameters[typeKey] = randomConfig.GetStrategyTypeName();

            ExtractBaseRandomProcessParameters(randomConfig.strategyType, randomConfig, config);
        }

        public static void ExtractOutputStrategyConfig(
            OutputStrategyConfig strategyConfig, ElementConfig elementConfig)
        {
            if (strategyConfig == null) return;
            elementConfig.Parameters["outputStrategyType"]      = strategyConfig.strategyType.ToString();
            elementConfig.Parameters["outputStrategy_paramKey"] = strategyConfig.parameterKey ?? "";
            elementConfig.Parameters["outputStrategy_expression"] = strategyConfig.routingExpression ?? "";
            if (strategyConfig.strategyType == OutputStrategyType.Probability)
            {
                var parts = new List<string>();
                if (strategyConfig.weights != null)
                    foreach (float w in strategyConfig.weights) parts.Add(w.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                elementConfig.Parameters[ElementConfigKeys.OutputWeights] = string.Join("|", parts);
            }
        }

        public static void ExtractInputStrategyConfig(
            InputStrategyConfig strategyConfig, ElementConfig elementConfig)
        {
            if (strategyConfig == null) return;
            elementConfig.Parameters["inputStrategyType"]              = strategyConfig.strategyType.ToString();
            elementConfig.Parameters["inputStrategy_singleLabelName"]  = strategyConfig.singleLabelName ?? "";
            elementConfig.Parameters["inputStrategy_condition"]        = strategyConfig.condition ?? "";
            elementConfig.Parameters["inputStrategy_singleLabelValue"] = strategyConfig.singleLabelValue ?? "";

            if (strategyConfig.multiLabelPairs != null)
                elementConfig.Parameters["inputStrategy_multiLabelPairs"] =
                    new List<string>(strategyConfig.multiLabelPairs);
        }

        private static void ExtractBaseRandomProcessParameters(RandomProcessType t,
            RandomProcessConfig c, ElementConfig e)
        {
            switch (t)
            {
                case RandomProcessType.Constant:
                    e.Parameters["constantValue"]   = c.constantValue;    break;
                case RandomProcessType.Uniform:
                    e.Parameters["uniformMin"]      = c.uniformMin;
                    e.Parameters["uniformMax"]      = c.uniformMax;       break;
                case RandomProcessType.Normal:
                    e.Parameters["normalMean"]      = c.normalMean;
                    e.Parameters["normalStdDev"]    = c.normalStdDev;    break;
                case RandomProcessType.Exponential:
                    e.Parameters["exponentialRate"] = c.exponentialRate;  break;
                case RandomProcessType.Triangular:
                    e.Parameters["triangularMin"]   = c.triangularMin;
                    e.Parameters["triangularMode"]  = c.triangularMode;
                    e.Parameters["triangularMax"]   = c.triangularMax;   break;
                case RandomProcessType.Expression:
                    e.Parameters["expression"] = c.expression;  break;
                case RandomProcessType.Gamma:
                    e.Parameters["gammaShape"]      = c.gammaShape;
                    e.Parameters["gammaScale"]      = c.gammaScale;       break;
                case RandomProcessType.Weibull:
                    e.Parameters["weibullShape"]    = c.weibullShape;
                    e.Parameters["weibullScale"]    = c.weibullScale;     break;
                case RandomProcessType.LogNormal:
                    e.Parameters["logNormalMu"]     = c.logNormalMu;
                    e.Parameters["logNormalSigma"]  = c.logNormalSigma;  break;
                case RandomProcessType.Beta:
                    e.Parameters["betaAlpha"]       = c.betaAlpha;
                    e.Parameters["betaBeta"]        = c.betaBeta;
                    e.Parameters["betaMin"]         = c.betaMin;
                    e.Parameters["betaMax"]         = c.betaMax;          break;
                case RandomProcessType.ChiSquare:
                    e.Parameters["chiSquareDf"]     = c.chiSquareDf;      break;
                case RandomProcessType.StudentT:
                    e.Parameters["studentTDf"]      = c.studentTDf;
                    e.Parameters["studentTClamp"]   = c.studentTClamp;   break;
                case RandomProcessType.FDistribution:
                    e.Parameters["fDistD1"]         = c.fDistD1;
                    e.Parameters["fDistD2"]         = c.fDistD2;          break;
                case RandomProcessType.Poisson:
                    e.Parameters["poissonLambda"]   = c.poissonLambda;    break;
                case RandomProcessType.Binomial:
                    e.Parameters["binomialN"]       = c.binomialN;
                    e.Parameters["binomialP"]       = c.binomialP;        break;
                case RandomProcessType.DiscreteUniform:
                    e.Parameters["discreteUniformMin"] = c.discreteUniformMin;
                    e.Parameters["discreteUniformMax"] = (int)c.discreteUniformMax; break;
            }
        }
    }
}
