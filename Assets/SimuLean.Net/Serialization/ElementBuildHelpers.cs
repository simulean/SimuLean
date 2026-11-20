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
        /// A sampler from a flat sampler's settings (<see cref="SamplerSettings"/>: the distribution and its own fields; absent
        /// fields take the distribution's defaults; null = the default Constant). <paramref name="prefix"/> is the setting's path
        /// (<c>delayStrategy</c>, <c>interArrivalDistribution</c>), named in errors. With a <paramref name="clock"/> the sampler
        /// gets its own random stream, keyed by <paramref name="owner"/> (default: the element name) and
        /// <paramref name="purpose"/> (default: <see cref="StreamPurpose"/> of the prefix — <c>delay</c>, <c>interArrival</c>,
        /// the names the sampler keys had, so seeded results are unchanged) — see <see cref="SimContext.CreateRandomStream(string, string)"/>.
        /// </summary>
        public static IRandomSampler BuildRandomProcess(SamplerSettings settings, string element, string prefix, SimClock clock = null,
                                                        string owner = null, string purpose = null)
        {
            IRandomSampler sampler = BuildRandomProcessCore(settings ?? new SamplerSettings(), element, prefix);

            // One stream per element and purpose, derived from the model seed and the stream key
            // (not from the creation order), so adding or removing elements leaves the others alone.
            if (clock != null)
                clock.Context.AssignRandomStream(sampler, owner ?? element,
                                                 string.IsNullOrEmpty(purpose) ? StreamPurpose(prefix) : purpose);

            return sampler;
        }

        /// <summary>The sampler of a distribution setting (null = none: null), with its own stream keyed by
        /// <paramref name="owner"/> (the element's name) and <paramref name="purpose"/>; <paramref name="path"/> names the
        /// setting in errors.</summary>
        public static IRandomSampler SamplerOf(SamplerSettings s, SimClock clock, string owner, string purpose, string path)
            => s == null ? null : BuildRandomProcess(s, owner, path, clock, owner, purpose);

        /// <summary>The stream purpose of a flat sampler (the name its keys had before they were renamed, which keys its stream).</summary>
        public static string StreamPurpose(string prefix)
            => prefix == "delayStrategy" ? "delay" : prefix == "interArrivalDistribution" ? "interArrival" : prefix;

        /// <summary>The key of a flat sampler's field: <c>prefix.field</c> (the field alone without a prefix).</summary>
        public static string SamplerKey(string prefix, string field) => string.IsNullOrEmpty(prefix) ? field : prefix + "." + field;

        private static IRandomSampler BuildRandomProcessCore(SamplerSettings s, string owner, string prefix)
        {
            string typeKey      = SamplerKey(prefix, "strategyType");
            string strategyType = string.IsNullOrEmpty(s.StrategyType) ? "Constant" : s.StrategyType;

            switch (strategyType)
            {
                case "Constant":
                    return new ConstantRandomProcess(
                        s.ConstantValue ?? 5.0);

                case "Uniform":
                    return new UniformDistribution(
                        s.UniformMin ?? 3.0,
                        s.UniformMax ?? 7.0);

                case "Normal":
                    return new NormalDistribution(
                        s.NormalMean ?? 5.0,
                        s.NormalStdDev ?? 1.0,
                        clampNonNegative: true);

                case "Exponential":
                    return new ExponentialDistribution(
                        s.ExponentialRate ?? 0.2);

                case "ExponentialMean":   // the same distribution given by its mean (MTBF / MTTR data)
                    return new ExponentialDistribution(
                        1.0 / Math.Max(1e-12, s.ExponentialMean ?? 5.0));

                case "Triangular":
                    return new TriangularDistribution(
                        s.TriangularMin ?? 3.0,
                        s.TriangularMode ?? 5.0,
                        s.TriangularMax ?? 8.0);

                case "Expression":
                {
                    // no hidden default an empty text read the label processingTime, 0 when no item had it)
                    string text = s.Expression ?? "";
                    if (string.IsNullOrWhiteSpace(text))
                        throw new FormatException($"Element '{owner}': its {(string.IsNullOrEmpty(prefix) ? "sampler" : prefix.TrimEnd('.'))} is an Expression with no expression text (write the time, e.g. item.weight * 2).");
                    return new SimuLean.Expressions.ExpressionSampler(text, owner, string.IsNullOrEmpty(prefix) ? "sampler" : prefix.TrimEnd('.'));
                }

                case "Gamma":
                    return new GammaDistribution(
                        s.GammaShape ?? 2.0,
                        s.GammaScale ?? 2.5);

                case "Weibull":
                    return new WeibullDistribution(
                        s.WeibullShape ?? 1.5,
                        s.WeibullScale ?? 5.0);

                case "LogNormal":
                    return new LogNormalDistribution(
                        s.LogNormalMu ?? 1.5,
                        s.LogNormalSigma ?? 0.5);

                case "Beta":
                    return new BetaDistribution(
                        s.BetaAlpha ?? 2.0,
                        s.BetaBeta ?? 5.0,
                        s.BetaMin ?? 0.0,
                        s.BetaMax ?? 1.0);

                case "ChiSquare":
                    return new ChiSquareDistribution(
                        s.ChiSquareDf ?? 5);

                case "StudentT":
                    return new StudentTDistribution(
                        s.StudentTDf ?? 10,
                        s.StudentTClamp ?? true);

                case "FDistribution":
                    return new FDistribution(
                        s.FDistD1 ?? 5,
                        s.FDistD2 ?? 10);

                case "Poisson":
                    return new PoissonDistribution(
                        s.PoissonLambda ?? 3.0);

                case "Binomial":
                    return new BinomialDistribution(
                        s.BinomialN ?? 10,
                        s.BinomialP ?? 0.5);

                case "DiscreteUniform":
                    return new IntegerUniformDistribution(
                        s.DiscreteUniformMin ?? 3,
                        s.DiscreteUniformMax ?? 7);

                default:
                    // Fail loudly: a silent Constant(5) made a typo look like a valid model.
                    throw new FormatException($"Unknown random process '{strategyType}' for element " +
                                              $"'{owner}' (parameter '{typeKey}').");
            }
        }

        public static IOutputStrategy BuildOutputStrategy(ElementConfig config)
        {
            var o = config.Settings?.OutputStrategy ?? new OutputStrategySettings();
            switch (BuilderChoices.Routing(o.StrategyType))
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
                    string ex = o.RoutingExpression ?? "";
                    return string.IsNullOrWhiteSpace(ex) ? (IOutputStrategy)new FirstAvailableStrategy() : new ExpressionRoutingStrategy(ex, null, config.Name);
                }
                case HeadlessOutputStrategyType.Probability:
                    return new ProbabilityRoutingStrategy(o.Weights ?? new double[0]);
                case HeadlessOutputStrategyType.BestScore:
                {
                    string ex = o.ScoreExpression;
                    if (string.IsNullOrWhiteSpace(ex))
                        throw new FormatException($"Element '{config.Name}': a BestScore routing needs its outputStrategy.scoreExpression.");
                    return o.WhenBestFull == BestScoreFull.Wait ? new BestScoreWaitRoutingStrategy(ex, config.Name) : new BestScoreRoutingStrategy(ex, config.Name);
                }
                case HeadlessOutputStrategyType.Rule:
                {
                    string owner = $"Element '{config.Name}'";
                    if (string.IsNullOrWhiteSpace(o.Rule)) throw new FormatException($"{owner}: a Rule routing needs its outputStrategy.rule (the rule's type name).");
                    var rule = (SimuLean.Rules.IRoutingRule)SimuLean.Rules.ChoiceRules.Create(o.Rule, SimuLean.Rules.RuleSite.Routing, config.Settings?.Rule,
                                                                                              SimuLean.Rules.ChoiceRules.RulesOf(config.Settings), owner);
                    return o.WhenBestFull == BestScoreFull.Wait ? new SimuLean.Rules.RuleWaitRoutingStrategy(rule, config.Name)
                                                                : new SimuLean.Rules.RuleRoutingStrategy(rule, config.Name);
                }
                case HeadlessOutputStrategyType.Parameterized:
                {
                    // Same as the scene (OutputStrategyConfig): no key -> FirstAvailable.
                    string paramKey = o.ParameterKey ?? "";
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
            var s = config.Settings?.InputStrategy ?? new InputStrategySettings();
            switch (BuilderChoices.Admission(s.StrategyType))
            {
                case HeadlessInputStrategyType.SingleLabel:
                {
                    string labelName     = s.SingleLabelName ?? "";
                    string labelValueStr = s.SingleLabelValue ?? "";
                    return new SingleLabelStrategy(labelName, labelValueStr);   // a number or a text
                }
                case HeadlessInputStrategyType.MultiLabel:
                {
                    var acceptedLabels = new Dictionary<string, object>();
                    if (s.MultiLabelPairs != null)
                        foreach (var pair in s.MultiLabelPairs)
                            AddLabelValue(pair, acceptedLabels);
                    return new MultiLabelStrategy(acceptedLabels);
                }
                case HeadlessInputStrategyType.Condition:
                    return new ConditionStrategy(s.Condition ?? "1", config.Name);
                case HeadlessInputStrategyType.Default:
                default:
                    return new DefaultStrategy();
            }
        }

        /// <summary>What a Combiner's component inputs accept (its pull mode).</summary>
        public static InputStrategy BuildPullModeStrategy(ElementConfig config, CombinerSettings c)
        {
            switch (c.PullModeType ?? "Default")
            {
                case "SingleLabel":
                    return new SingleLabelStrategy(c.SingleLabelName ?? "", c.SingleLabelValue ?? "");   // a number or a text
                case "MultiLabel":
                {
                    var acceptedLabels = new Dictionary<string, object>();
                    if (c.MultiLabelPairs != null)
                        foreach (var pair in c.MultiLabelPairs)
                            AddLabelValue(pair, acceptedLabels);
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


        /// <summary>The element's entry / exit label assignments (<see cref="LabelAssignments"/>), when the config has them.</summary>
        public static void ApplyLabelAssignments(Element element, ElementConfig config)
        {
            element.EntryLabels = LabelAssignments.Parse(config.Settings?.EntryLabels, config.Name, "entryLabel");
            element.ExitLabels = LabelAssignments.Parse(config.Settings?.ExitLabels, config.Name, "exitLabel");
        }

        /// <summary>The element's item statistics (<see cref="SimuLean.Statistics.ItemTallies"/>), when the config has them.</summary>
        public static void ApplyTallies(Element element, ElementConfig config)
        {
            var tallies = SimuLean.Statistics.ItemTallies.Parse(config.Settings?.Tallies, config.Settings?.TallySplit, config.Name);
            if (tallies != null && element.StatsCollector != null) element.StatsCollector.Tallies = tallies;
        }

        public static void ApplyOutputStrategy(Element element, ElementConfig config)
        {
            if (config.Settings?.OutputStrategy?.StrategyType == null) return;
            element.SetOutputStrategy(BuildOutputStrategy(config));
        }

        public static void ApplyInputStrategy(Element element, ElementConfig config)
        {
            if (config.Settings?.InputStrategy?.StrategyType == null) return;
            element.SetInputStrategy(BuildInputStrategy(config));
        }


        private static void AddLabelValue(LabelValueSettings pair, Dictionary<string, object> target)
        {
            if (pair == null || string.IsNullOrWhiteSpace(pair.Label)) return;
            string label    = pair.Label.Trim();
            string valueStr = (pair.Value ?? "").Trim();
            if (SimuLean.Data.ModelTable.TryNumber(valueStr, out double dv))   // "2,5" = 2.5
                target[label] = dv;
            else
                target[label] = valueStr;
        }
    }

    // =========================================================================
    //  Enums (formerly internal to HeadlessModelFactory)
    // =========================================================================

    internal enum HeadlessInputStrategyType { Default, SingleLabel, MultiLabel, Condition }


    internal enum HeadlessOutputStrategyType
    {
        FirstAvailable, ShortestQueue, RoundRobin,
        MostAvailableCapacity, PriorityBased, Parameterized, Expression, Probability, BestScore, Rule

    }
}
