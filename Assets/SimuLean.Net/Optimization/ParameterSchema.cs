using System;
using System.Collections.Generic;

namespace SimuLean.Optimization
{
    /// <summary>
    /// What the keys of a config mean for parameterisation — declared once per element type (its handler's
    /// <see cref="SimuLean.Serialization.IElementHandler.Schema"/>), for resource teams and for downtimes. Numbers, bools
    /// and number arrays need no declaration (their type says it); a text or a list must be declared: an option
    /// (<see cref="Choice"/>), a sampler spec <c>Type~p1~p2</c> (<see cref="Sampler"/>), numbers in a text
    /// <c>a|b|c</c> (<see cref="NumberList"/>), a list of numbers (<see cref="Numbers"/>) — or <see cref="Structural"/>
    /// (names, references, tables, expressions: not a parameter). A key left undecided fails the catalog test, so a new
    /// element type, key or property is offered — or deliberately not — from the start.
    /// </summary>
    public sealed class ParameterSchema
    {
        internal enum Rule { Structural, Choice, Sampler, NumberList, Numbers, Properties, File }

        internal sealed class Entry
        {
            public Rule Rule;
            public string[] Options;
            public bool Integer;
        }

        internal sealed class Refinement
        {
            public string Label;
            public double? Min, Max;
        }

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Refinement> refinements = new Dictionary<string, Refinement>(StringComparer.Ordinal);

        /// <summary>Not parameters: names, references, labels, tables, expressions, coordinates…</summary>
        public ParameterSchema Structural(params string[] keys)
        {
            foreach (var k in keys) entries[k] = new Entry { Rule = Rule.Structural };
            return this;
        }

        /// <summary>A data file (e.g. a schedule source's table): varied by a study / experiment with
        /// file names as its levels (<c>Owner.key=A.csv|B.csv</c>, read when each run's config is built); not offered to the
        /// numeric optimisers (no option set of its own).</summary>
        public ParameterSchema File(params string[] keys)
        {
            foreach (var k in keys) entries[k] = new Entry { Rule = Rule.File };
            return this;
        }

        /// <summary>A text that is one of <paramref name="options"/>.</summary>
        public ParameterSchema Choice(string key, params string[] options)
        {
            entries[key] = new Entry { Rule = Rule.Choice, Options = options };
            return this;
        }

        /// <summary>A text that is the name of a value of <typeparamref name="T"/>.</summary>
        public ParameterSchema Choice<T>(string key) where T : struct, Enum => Choice(key, Enum.GetNames(typeof(T)));

        /// <summary>Sampler specs <c>Type~p1~p2…</c>: each number is a parameter (the distribution stays).</summary>
        public ParameterSchema Sampler(params string[] keys)
        {
            foreach (var k in keys) entries[k] = new Entry { Rule = Rule.Sampler };
            return this;
        }

        /// <summary>A text of numbers separated by <c>|</c> (e.g. <c>"1|1|0.5"</c>): one vector parameter.</summary>
        public ParameterSchema NumberList(string key, bool integer = false)
        {
            entries[key] = new Entry { Rule = Rule.NumberList, Integer = integer };
            return this;
        }

        /// <summary>A list of numbers stored as texts (e.g. lane lengths): one vector parameter.</summary>
        public ParameterSchema Numbers(string key, bool integer = false)
        {
            entries[key] = new Entry { Rule = Rule.Numbers, Integer = integer };
            return this;
        }

        /// <summary>User property records (<c>d|name|value</c>, <c>arr-d|name|a,b</c>…): each number / number array is a
        /// parameter <c>key.name</c>; texts and references are not.</summary>
        public ParameterSchema Properties(string key)
        {
            entries[key] = new Entry { Rule = Rule.Properties };
            return this;
        }

        /// <summary>A better label and / or range bounds for a parameter (all optional).</summary>
        public ParameterSchema Describe(string key, string label = null, double? min = null, double? max = null)
        {
            refinements[key] = new Refinement { Label = label, Min = min, Max = max };
            return this;
        }

        /// <summary>A new schema with the declarations of both (this one's win).</summary>
        public ParameterSchema With(ParameterSchema other)
        {
            var s = new ParameterSchema();
            foreach (var src in new[] { other, this })
            {
                if (src == null) continue;
                foreach (var kv in src.entries) s.entries[kv.Key] = kv.Value;
                foreach (var kv in src.refinements) s.refinements[kv.Key] = kv.Value;
            }
            return s;
        }

        internal Entry Find(string key) => entries.TryGetValue(key, out var e) ? e : null;
        internal Refinement Refine(string key) => refinements.TryGetValue(key, out var r) ? r : null;

        // ── the shared schemas ─────────────────────────────────────────────

        /// <summary>Distribution names of the flat sampler keys (<c>delayStrategyType</c>…; see the build helpers).</summary>
        public static readonly string[] SamplerTypes =
        {
            "Constant", "Uniform", "Normal", "Exponential", "Triangular", "Gamma", "Weibull", "LogNormal", "Beta",
            "ChiSquare", "StudentT", "FDistribution", "Poisson", "Binomial", "DiscreteUniform",
        };

        /// <summary>
        /// Keys every element may have (routing, admission, resources, location, the flat sampler keys): merged with each
        /// handler's schema.
        /// </summary>
        public static readonly ParameterSchema Common = new ParameterSchema()
            .Choice<SimuLean.Serialization.HeadlessOutputStrategyType>("outputStrategyType")
            .Choice<SimuLean.Serialization.HeadlessInputStrategyType>("inputStrategyType")
            .Choice("delayStrategyType", SamplerTypes)
            .Choice("interArrivalStrategyType", SamplerTypes)
            .Choice<SimuLean.Resources.ProcessRelease>(SimuLean.Serialization.ResourceUseKeys.ProcessRelease)
            .Properties(SimuLean.Serialization.PropertyRecords.Key)
            .Sampler(SimuLean.Serialization.ResourceUseKeys.TransportLoad, SimuLean.Serialization.ResourceUseKeys.TransportTravel,
                     SimuLean.Serialization.ResourceUseKeys.TransportUnload, SimuLean.Serialization.ResourceUseKeys.ProcessHoldFor)
            .NumberList(SimuLean.Serialization.ElementBuildHelpers.OutputWeightsKey)
            .Structural(SimuLean.Serialization.ElementBuildHelpers.EntryLabelsKey, SimuLean.Serialization.ElementBuildHelpers.ExitLabelsKey,
                        SimuLean.Serialization.ElementConfigKeys.Tallies, SimuLean.Serialization.ElementConfigKeys.TallySplit,
                        SimuLean.Serialization.ResourceUseKeys.ProcessReleaseAt)
            .Structural("outputStrategy_paramKey", "outputStrategy_expression", "inputStrategy_singleLabelName",
                        "inputStrategy_singleLabelValue", "inputStrategy_condition", "inputStrategy_multiLabelPairs",
                        "inputOrder", "port", "query", "location", "workNodes", "expression",
                        SimuLean.Serialization.ResourceUseKeys.ProcessTeam, SimuLean.Serialization.ResourceUseKeys.ProcessRequirements,
                        SimuLean.Serialization.ResourceUseKeys.ProcessPriority, SimuLean.Serialization.ResourceUseKeys.TransportTeam,
                        SimuLean.Serialization.ResourceUseKeys.TransportRequirements, SimuLean.Serialization.ResourceUseKeys.TransportPriority,
                        SimuLean.Serialization.ResourceUseKeys.WalkGrid, SimuLean.Serialization.ResourceUseKeys.WalkMatrix)
            .Choice(SimuLean.Serialization.ResourceUseKeys.WalkTravel, "Network", "Grid", "Matrix")
            .Describe(SimuLean.Serialization.ResourceUseKeys.WalkSpeed, "Walking speed (m/s)", min: 0)
            .Describe("constantValue", "Value").Describe("uniformMin", "Uniform min").Describe("uniformMax", "Uniform max")
            .Describe("normalMean", "Normal mean").Describe("normalStdDev", "Normal std dev", min: 0)
            .Describe("exponentialRate", "Exponential rate").Describe("triangularMin", "Triangular min")
            .Describe("triangularMode", "Triangular mode").Describe("triangularMax", "Triangular max")
            .Describe("gammaShape", "Gamma shape").Describe("gammaScale", "Gamma scale")
            .Describe("weibullShape", "Weibull shape").Describe("weibullScale", "Weibull scale")
            .Describe("logNormalMu", "LogNormal mu").Describe("logNormalSigma", "LogNormal sigma")
            .Describe("poissonLambda", "Poisson lambda").Describe("binomialP", "Binomial p", 0, 1);

        /// <summary>The properties of a resource team (<see cref="SimuLean.Serialization.ResourceTeamConfig"/>).</summary>
        public static readonly ParameterSchema Team = new ParameterSchema()
            .Structural("Id", "Name", "HomeNode", "Matrix", "Grid", "Travel", "RequestOrder", "ResourceOrder",
                        "CalibrationBase", "CalibrationFit", "CalibrationPairs", "CalibrationMinSamples")
            .NumberList("AxisSpeed").NumberList("AxisAccel")
            .Choice<SimuLean.Serialization.ResourceUnits>("Units")
            .Describe(ModelParameterCatalog.TeamMembers, "Members", min: 1)
            .Describe("Speed", "Speed", min: 0)
            .Describe("Traffic", "Traffic control").Describe("AxesSequential", "Axes one after the other");

        /// <summary>The parameters of a downtime (<see cref="SimuLean.Serialization.DowntimeConfig"/>, every kind).</summary>
        public static readonly ParameterSchema Downtime = new ParameterSchema()
            .Structural("state", "busyStates", "code", "intervals", "pattern", "holidays",
                        "dataTable", "targetColumn", "startColumn", "endColumn", "durationColumn", "codeColumn", "reasonColumn",
                        "codeRules", "targetKeys")
            .Choice<StopMode>("mode")
            .Choice<SimuLean.Downtime.DowntimeBasis>("basis")
            .Choice<SimuLean.Downtime.OverlapPolicy>("overlap")
            .Sampler("ttf", "ttr", "firstFailure")
            .Describe("blockInput", "Block the input").Describe("blockOutput", "Block the output")
            .Describe("period", "Periodic: period", min: 0).Describe("duration", "Periodic: stop duration", min: 0)
            .Describe("first", "Periodic: first stop", min: 0).Describe("until", "Periodic: no stop from", min: 0)
            .Describe("allowOverlap", "Periodic: stops may overlap");

        /// <summary>The most actions a reaction's schema declares (<see cref="Reaction"/>).</summary>
        public const int MaxReactionActions = 64;

        /// <summary>The parameters of a reaction (<see cref="SimuLean.Serialization.ReactionConfig"/>): <c>enabled</c>
        /// and, per action, its plain number (<c>aN.number</c>) and stop duration; expressions, names and references are
        /// structural.</summary>
        public static readonly ParameterSchema Reaction = BuildReaction();

        private static ParameterSchema BuildReaction()
        {
            // what the reaction is (its signal, actions, modes) is its design, not a parameter
            var s = new ParameterSchema()
                .Structural("signal", "delivery", "state", "condition")
                .Describe("enabled", "Enabled")
                .Describe("period", "Every: period", min: 0).Describe("first", "Every: first time", min: 0).Describe("at", "At time", min: 0);
            for (int n = 1; n <= MaxReactionActions; n++)
            {
                string K(string field) => SimuLean.Serialization.ReactionConfig.ActionKey(n, field);
                s.Structural(K("kind"), K("mode"), K("target"), K("name"), K("expression"), K("state"), K("team"), K("group"), K("condition"))
                 .Describe(K("count"), $"Action {n}: members", min: 0)
                 .Describe(K("number"), $"Action {n}: value")
                 .Describe(K("duration"), $"Action {n}: stop duration", min: 0);
            }
            return s;
        }
    }
}
