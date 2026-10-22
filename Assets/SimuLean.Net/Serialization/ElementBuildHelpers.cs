using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Stochastics.Distributions;

namespace SimuLean.Serialization
{
    // =========================================================================
    //  Build helpers (ElementConfig → headless Element)
    //  Moved from HeadlessModelFactory; shared by all element handlers.
    // =========================================================================

    internal static class ElementBuildHelpers
    {
        /// <summary>
        /// Builds an IRandomSampler from config parameters using the given prefix
        /// (e.g. "delay" reads "delayStrategyType", "interArrival" reads "interArrivalStrategyType").
        /// With a <paramref name="clock"/> the sampler gets its own random stream, keyed by
        /// <paramref name="owner"/> (default: the element name) and <paramref name="purpose"/>
        /// (default: the prefix) — see <see cref="SimContext.CreateRandomStream(string, string)"/>.
        /// The Unity wrappers use the same keys, so scene and headless runs draw the same numbers.
        /// </summary>
        public static IRandomSampler BuildRandomProcess(ElementConfig config, string prefix, SimClock clock = null,
                                                        string owner = null, string purpose = null)
        {
            IRandomSampler sampler = BuildRandomProcessCore(config, prefix);

            // One stream per element and purpose, derived from the model seed and the stream key
            // (not from the creation order), so adding or removing elements leaves the others alone.
            if (clock != null)
                clock.Context.AssignRandomStream(sampler, owner ?? config.Name,
                                                 string.IsNullOrEmpty(purpose) ? prefix : purpose);

            return sampler;
        }

        private static IRandomSampler BuildRandomProcessCore(ElementConfig config, string prefix)
        {
            string typeKey      = prefix + "StrategyType";
            string strategyType = config.GetParameter<string>(typeKey, "Constant");

            switch (strategyType)
            {
                case "Constant":
                    return new ConstantRandomProcess(
                        config.GetParameter<double>("constantValue", 5.0));

                case "Uniform":
                    return new UniformDistribution(
                        config.GetParameter<double>("uniformMin", 3.0),
                        config.GetParameter<double>("uniformMax", 7.0));

                case "Normal":
                    return new NormalDistribution(
                        config.GetParameter<double>("normalMean",   5.0),
                        config.GetParameter<double>("normalStdDev", 1.0),
                        clampNonNegative: true);

                case "Exponential":
                    return new ExponentialDistribution(
                        config.GetParameter<double>("exponentialRate", 0.2));

                case "Triangular":
                    return new TriangularDistribution(
                        config.GetParameter<double>("triangularMin",  3.0),
                        config.GetParameter<double>("triangularMode", 5.0),
                        config.GetParameter<double>("triangularMax",  8.0));

                case "Expression":
                    return new SimuLean.Expressions.ExpressionSampler(
                        config.GetParameter<string>("expression", "processingTime"));

                case "Gamma":
                    return new GammaDistribution(
                        config.GetParameter<double>("gammaShape", 2.0),
                        config.GetParameter<double>("gammaScale", 2.5));

                case "Weibull":
                    return new WeibullDistribution(
                        config.GetParameter<double>("weibullShape", 1.5),
                        config.GetParameter<double>("weibullScale", 5.0));

                case "LogNormal":
                    return new LogNormalDistribution(
                        config.GetParameter<double>("logNormalMu",    1.5),
                        config.GetParameter<double>("logNormalSigma", 0.5));

                case "Beta":
                    return new BetaDistribution(
                        config.GetParameter<double>("betaAlpha", 2.0),
                        config.GetParameter<double>("betaBeta",  5.0),
                        config.GetParameter<double>("betaMin",   0.0),
                        config.GetParameter<double>("betaMax",   1.0));

                case "ChiSquare":
                    return new ChiSquareDistribution(
                        config.GetParameter<int>("chiSquareDf", 5));

                case "StudentT":
                    return new StudentTDistribution(
                        config.GetParameter<int>("studentTDf",     10),
                        config.GetParameter<bool>("studentTClamp", true));

                case "FDistribution":
                    return new FDistribution(
                        config.GetParameter<int>("fDistD1", 5),
                        config.GetParameter<int>("fDistD2", 10));

                case "Poisson":
                    return new PoissonDistribution(
                        config.GetParameter<double>("poissonLambda", 3.0));

                case "Binomial":
                    return new BinomialDistribution(
                        config.GetParameter<int>("binomialN",    10),
                        config.GetParameter<double>("binomialP", 0.5));

                case "DiscreteUniform":
                    return new IntegerUniformDistribution(
                        config.GetParameter<int>("discreteUniformMin", 3),
                        config.GetParameter<int>("discreteUniformMax", 7));

                default:
                    // Fail loudly: a silent Constant(5) made a typo look like a valid model.
                    throw new FormatException($"Unknown random process '{strategyType}' for element " +
                                              $"'{config.Name}' (parameter '{typeKey}').");
            }
        }

        public static IOutputStrategy BuildOutputStrategy(ElementConfig config)
        {
            string typeStr = config.GetParameter<string>("outputStrategyType", "FirstAvailable");
            if (!Enum.TryParse<HeadlessOutputStrategyType>(typeStr, out var strategyType))
                strategyType = HeadlessOutputStrategyType.FirstAvailable;

            switch (strategyType)
            {
                case HeadlessOutputStrategyType.FirstAvailable:
                    return new FirstAvailableStrategy();
                case HeadlessOutputStrategyType.ShortestQueue:
                    return new ShortestQueueStrategy();
                case HeadlessOutputStrategyType.RoundRobin:
                    return new RoundRobinStrategy();
                case HeadlessOutputStrategyType.MostAvailableCapacity:
                    return new MostAvailableCapacityStrategy();
                case HeadlessOutputStrategyType.PriorityBased:
                    return new PriorityRoutingStrategy();
                case HeadlessOutputStrategyType.Expression:
                {
                    string ex = config.GetParameter<string>("outputStrategy_expression", "");
                    return string.IsNullOrWhiteSpace(ex) ? (IOutputStrategy)new FirstAvailableStrategy() : new ExpressionRoutingStrategy(ex);
                }
                case HeadlessOutputStrategyType.Probability:
                    return new ProbabilityRoutingStrategy(ParseNumberList(config.GetParameter<string>(OutputWeightsKey, ""), config.Name, OutputWeightsKey));
                case HeadlessOutputStrategyType.Parameterized:
                {
                    // Same as the scene (OutputStrategyConfig): no key -> FirstAvailable.
                    string paramKey = config.GetParameter<string>("outputStrategy_paramKey", "");
                    return string.IsNullOrEmpty(paramKey)
                        ? (IOutputStrategy)new FirstAvailableStrategy()
                        : new ParameterizedRoutingStrategy(paramKey);
                }
                default:
                    return new FirstAvailableStrategy();
            }
        }

        public static InputStrategy BuildInputStrategy(ElementConfig config)
        {
            string typeStr = config.GetParameter<string>("inputStrategyType", "Default");
            if (!Enum.TryParse<HeadlessInputStrategyType>(typeStr, out var strategyType))
                strategyType = HeadlessInputStrategyType.Default;

            switch (strategyType)
            {
                case HeadlessInputStrategyType.SingleLabel:
                {
                    string labelName     = config.GetParameter<string>("inputStrategy_singleLabelName",  "");
                    string labelValueStr = config.GetParameter<string>("inputStrategy_singleLabelValue", "");
                    double? requiredValue = null;
                    if (!string.IsNullOrEmpty(labelValueStr)) requiredValue = LabelNumber(labelValueStr, labelName);
                    return new SingleLabelStrategy(labelName, requiredValue);
                }
                case HeadlessInputStrategyType.MultiLabel:
                {
                    var acceptedLabels = new Dictionary<string, object>();
                    var pairs = config.GetParameter<List<string>>("inputStrategy_multiLabelPairs");
                    if (pairs != null)
                        foreach (string pair in pairs)
                            ParseLabelPair(pair, acceptedLabels);
                    return new MultiLabelStrategy(acceptedLabels);
                }
                case HeadlessInputStrategyType.Condition:
                    return new ConditionStrategy(config.GetParameter<string>("inputStrategy_condition", "1"));
                case HeadlessInputStrategyType.Default:
                default:
                    return new DefaultStrategy();
            }
        }

        public static InputStrategy BuildPullModeStrategy(ElementConfig config)
        {
            string pullModeType = config.GetParameter<string>("pullModeType", "Default");

            switch (pullModeType)
            {
                case "SingleLabel":
                {
                    string labelName     = config.GetParameter<string>("singleLabelName",  "type");
                    string labelValueStr = config.GetParameter<string>("singleLabelValue", "");
                    double? requiredValue = null;
                    if (!string.IsNullOrEmpty(labelValueStr)) requiredValue = LabelNumber(labelValueStr, labelName);
                    return new SingleLabelStrategy(labelName, requiredValue);
                }
                case "MultiLabel":
                {
                    var acceptedLabels = new Dictionary<string, object>();
                    var pairs = config.GetParameter<List<string>>("multiLabelPairs");
                    if (pairs != null)
                        foreach (string pair in pairs)
                            ParseLabelPair(pair, acceptedLabels);
                    return new MultiLabelStrategy(acceptedLabels);
                }
                case "Custom":
                    SimLog.Warning($"[ElementBuildHelpers] Custom pull mode not implemented for '{config.Name}'. Using Default.");
                    return new DefaultStrategy();
                case "Default":
                default:
                    return new DefaultStrategy();
            }
        }

        public const string OutputWeightsKey = ElementConfigKeys.OutputWeights;
        public const string EntryLabelsKey = ElementConfigKeys.EntryLabels, ExitLabelsKey = ElementConfigKeys.ExitLabels;

        /// <summary>A <c>a|b|c</c> text of numbers (invariant culture; <c>;</c> and <c>,</c> also separate when there is no
        /// <c>|</c>).</summary>
        public static double[] ParseNumberList(string text, string owner, string key)
        {
            if (string.IsNullOrWhiteSpace(text)) return new double[0];
            char[] seps = text.IndexOf('|') >= 0 ? new[] { '|' } : new[] { ';', ',', ' ' };
            var parts = text.Split(seps, StringSplitOptions.RemoveEmptyEntries);
            var values = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out values[i]))
                    throw new FormatException($"Element '{owner}': '{parts[i]}' in '{key}' is not a number.");
            return values;
        }

        /// <summary>The element's entry / exit label assignments (<see cref="LabelAssignments"/>), when the config has them.</summary>
        public static void ApplyLabelAssignments(Element element, ElementConfig config)
        {
            element.EntryLabels = LabelAssignments.Parse(config.GetParameter<List<string>>(EntryLabelsKey), config.Name);
            element.ExitLabels = LabelAssignments.Parse(config.GetParameter<List<string>>(ExitLabelsKey), config.Name);
        }

        /// <summary>The element's item statistics (<see cref="SimuLean.Statistics.ItemTallies"/>), when the config has them.</summary>
        public static void ApplyTallies(Element element, ElementConfig config)
        {
            var tallies = SimuLean.Statistics.ItemTallies.Parse(config.GetParameter<List<string>>(ElementConfigKeys.Tallies),
                                                                config.GetParameter<string>(ElementConfigKeys.TallySplit, null), config.Name);
            if (tallies != null && element.StatsCollector != null) element.StatsCollector.Tallies = tallies;
        }

        public static void ApplyOutputStrategy(Element element, ElementConfig config)
        {
            if (!config.Parameters.ContainsKey("outputStrategyType")) return;
            element.SetOutputStrategy(BuildOutputStrategy(config));
        }

        public static void ApplyInputStrategy(Element element, ElementConfig config)
        {
            if (!config.Parameters.ContainsKey("inputStrategyType")) return;
            element.SetInputStrategy(BuildInputStrategy(config));
        }

        /// <summary>
        /// A label value typed as text: a decimal comma is a decimal comma ("2,5" = 2.5; <c>NumberStyles.Any</c> read it as
        /// a thousands separator, 25), and text that is not a number fails, naming it
        /// (it silently admitted nothing).
        /// </summary>
        private static double LabelNumber(string text, string label)
        {
            if (SimuLean.Data.ModelTable.TryNumber(text, out double v)) return v;
            throw new FormatException($"Input strategy: the value '{text}' of label '{label}' is not a number.");
        }

        private static void ParseLabelPair(string pair, Dictionary<string, object> target)
        {
            if (string.IsNullOrEmpty(pair)) return;
            string[] parts = pair.Split('=');
            if (parts.Length != 2) return;
            string label    = parts[0].Trim();
            string valueStr = parts[1].Trim();
            if (SimuLean.Data.ModelTable.TryNumber(valueStr, out double dv))   // "2,5" = 2.5
                target[label] = dv;
            else
                target[label] = valueStr;
        }
        /// <summary>Config keys of the release mode (Queue, Multi-Server).</summary>
        public const string ReleaseModeKey = "releaseMode", ReleaseScanKey = "releaseScan";

        /// <summary>Reads <c>releaseMode</c> (FIFO / AnyAccepted) and <c>releaseScan</c> (0 = every held item).</summary>
        public static (ReleaseMode mode, int scan) BuildRelease(ElementConfig config)
        {
            string text = config.GetParameter<string>(ReleaseModeKey, "") ?? "";
            ReleaseMode mode = ReleaseMode.FIFO;
            if (text.Trim().Length > 0 && !System.Enum.TryParse(text.Trim(), true, out mode))
                throw new System.ArgumentException($"Element '{config.Name}': unknown release mode '{text}' (FIFO or AnyAccepted).");
            return (mode, System.Math.Max(0, config.GetParameter<int>(ReleaseScanKey, 0)));
        }
    }

    // =========================================================================
    //  Enums (formerly internal to HeadlessModelFactory)
    // =========================================================================

    internal enum HeadlessInputStrategyType { Default, SingleLabel, MultiLabel, Condition }

    /// <summary>Config keys common to every element that the scene side writes too.</summary>
    public static class ElementConfigKeys
    {
        /// <summary>Probability routing: the weights of the outputs, in connection order, as <c>w1|w2|…</c>.</summary>
        public const string OutputWeights = "outputStrategy_weights";
        /// <summary>Labels set on the items that enter / leave the element (<c>name = expression</c> records).</summary>
        public const string EntryLabels = "entryLabels", ExitLabels = "exitLabels";
        /// <summary>Values recorded per entering item (<c>name = expression</c> records) and the label that splits them.</summary>
        public const string Tallies = "tallies", TallySplit = "tallySplit";
    }

    internal enum HeadlessOutputStrategyType
    {
        FirstAvailable, ShortestQueue, RoundRobin,
        MostAvailableCapacity, PriorityBased, Parameterized, Expression, Probability

    }
}
