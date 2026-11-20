using System.Collections.Generic;
using System.Globalization;

namespace SimuLean.Serialization
{
    /// <summary>
    /// The text form of a distribution, <c>Type~p1~p2…</c> (an input form — a study level, <c>simulean_set</c>, a
    /// setup matrix's time cell, a fitted distribution —; a model stores <see cref="SamplerSettings"/>). <see cref="Parse"/>
    /// reads it strictly, <see cref="Format"/> writes it, <see cref="Decode"/> builds its sampler through
    /// <see cref="ElementBuildHelpers.BuildRandomProcess"/> (a per-model random stream). Engine-pure.
    /// </summary>
    public static class SamplerSpec
    {
        /// <summary>
        /// Decodes a compact sampler spec <c>Type~p1~p2…</c> (e.g. <c>Exponential~0.001</c>,
        /// <c>Weibull~1.5~3600</c>, <c>Constant~30</c>) into a sampler with its own random
        /// stream from <paramref name="clock"/>'s context (null = global engine), keyed by
        /// <paramref name="owner"/> (element name) and <paramref name="purpose"/> — pass both so the
        /// stream does not depend on build order (<see cref="SimContext.CreateRandomStream(string, string)"/>).
        /// Parameters follow the Inspector conventions (Exponential takes a <b>rate</b>).
        /// Throws <see cref="System.FormatException"/> for a null/empty spec, an unknown type or a
        /// missing / unparseable parameter (only StudentT's clamp and Beta's min/max are optional),
        /// instead of silently substituting defaults.
        /// </summary>
        public static IRandomSampler Decode(string spec, SimClock clock, string owner = null, string purpose = null)
            => ElementBuildHelpers.BuildRandomProcess(Parse(spec), "task", "", clock, owner ?? "", purpose ?? "sampler");

        /// <summary>The fields of each distribution in the order of its text form (<c>Uniform~min~max</c>): the
        /// <see cref="SamplerSettings"/> paths, also the catalog keys after the distribution's own path.</summary>
        public static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]>
        {
            ["Constant"] = new[] { "constantValue" }, ["Uniform"] = new[] { "uniformMin", "uniformMax" },
            ["Normal"] = new[] { "normalMean", "normalStdDev" }, ["Exponential"] = new[] { "exponentialRate" },
            ["ExponentialMean"] = new[] { "exponentialMean" },
            ["Triangular"] = new[] { "triangularMin", "triangularMode", "triangularMax" },
            ["Gamma"] = new[] { "gammaShape", "gammaScale" }, ["Weibull"] = new[] { "weibullShape", "weibullScale" },
            ["LogNormal"] = new[] { "logNormalMu", "logNormalSigma" }, ["Beta"] = new[] { "betaAlpha", "betaBeta", "betaMin", "betaMax" },
            ["ChiSquare"] = new[] { "chiSquareDf" }, ["StudentT"] = new[] { "studentTDf", "studentTClamp" },
            ["FDistribution"] = new[] { "fDistD1", "fDistD2" }, ["Poisson"] = new[] { "poissonLambda" },
            ["Binomial"] = new[] { "binomialN", "binomialP" }, ["DiscreteUniform"] = new[] { "discreteUniformMin", "discreteUniformMax" },
        };

        /// <summary>A distribution as text <c>Type~p1~p2…</c> (its fields in order, an absent one ends the list; numbers in
        /// their round-trip form).</summary>
        public static string Format(SamplerSettings s)
        {
            if (s == null) return "";
            string type = string.IsNullOrEmpty(s.StrategyType) ? "Constant" : s.StrategyType;
            if (type == "Expression") return "Expression~" + (s.Expression ?? "");
            var sb = new System.Text.StringBuilder(type);
            if (Fields.TryGetValue(type, out var fields))
                foreach (string f in fields)
                {
                    object v = SettingsPaths.Find(typeof(SamplerSettings), f)?.Get(s);
                    if (v == null) break;
                    sb.Append('~').Append(v is double d ? d.ToString("R", CultureInfo.InvariantCulture)
                                          : v is bool b ? (b ? "true" : "false") : System.Convert.ToString(v, CultureInfo.InvariantCulture));
                }
            return sb.ToString();
        }

        /// <summary>
        /// Reads a distribution's text <c>Type~p1~p2…</c> (e.g. <c>Exponential~0.001</c>, <c>ExponentialMean~3600</c>,
        /// <c>Weibull~1.5~3600</c>, <c>Constant~30</c>, <c>Expression~item.weight * 2</c>). Parameters follow the Inspector
        /// conventions (Exponential takes a <b>rate</b>, ExponentialMean a mean). Throws <see cref="System.FormatException"/>
        /// for a null/empty text, an unknown type or a missing / unparseable parameter (only StudentT's clamp and Beta's
        /// min/max are optional), instead of silently substituting defaults.
        /// </summary>
        public static SamplerSettings Parse(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec))
                throw new System.FormatException("Sampler spec is empty (expected 'Type~p1~p2…', e.g. 'Exponential~0.2').");
            spec = spec.Trim();
            int tilde   = spec.IndexOf('~');
            string type = tilde < 0 ? spec : spec.Substring(0, tilde);
            string rest = tilde < 0 ? "" : spec.Substring(tilde + 1);
            string[] p  = rest.Split('~');

            var cfg = new SamplerSettings { StrategyType = type };

            Spec = spec;
            switch (type)
            {
                case "Constant":
                    cfg.ConstantValue = D(p, 0); break;
                case "Uniform":
                    cfg.UniformMin = D(p, 0);
                    cfg.UniformMax = D(p, 1); break;
                case "Normal":
                    cfg.NormalMean = D(p, 0);
                    cfg.NormalStdDev = D(p, 1); break;
                case "Exponential":
                    cfg.ExponentialRate = D(p, 0); break;
                case "ExponentialMean":   // MTBF/MTTR data given as means
                    cfg.ExponentialMean = D(p, 0); break;
                case "Triangular":
                    cfg.TriangularMin = D(p, 0);
                    cfg.TriangularMode = D(p, 1);
                    cfg.TriangularMax = D(p, 2); break;
                case "Expression":
                    cfg.Expression = rest; break;   // full remainder
                case "Gamma":
                    cfg.GammaShape = D(p, 0);
                    cfg.GammaScale = D(p, 1); break;
                case "Weibull":
                    cfg.WeibullShape = D(p, 0);
                    cfg.WeibullScale = D(p, 1); break;
                case "LogNormal":
                    cfg.LogNormalMu = D(p, 0);
                    cfg.LogNormalSigma = D(p, 1); break;
                case "Beta":
                    cfg.BetaAlpha = D(p, 0);
                    cfg.BetaBeta = D(p, 1);
                    cfg.BetaMin = DOpt(p, 2, 0.0);
                    cfg.BetaMax = DOpt(p, 3, 1.0); break;
                case "ChiSquare":
                    cfg.ChiSquareDf = I(p, 0); break;
                case "StudentT":
                    cfg.StudentTDf = I(p, 0);
                    cfg.StudentTClamp = BOpt(p, 1, true); break;
                case "FDistribution":
                    cfg.FDistD1 = I(p, 0);
                    cfg.FDistD2 = I(p, 1); break;
                case "Poisson":
                    cfg.PoissonLambda = D(p, 0); break;
                case "Binomial":
                    cfg.BinomialN = I(p, 0);
                    cfg.BinomialP = D(p, 1); break;
                case "DiscreteUniform":
                    cfg.DiscreteUniformMin = I(p, 0);
                    cfg.DiscreteUniformMax = I(p, 1); break;
                default:
                    throw new System.FormatException($"Sampler spec '{spec}': unknown type '{type}'.");
            }

            return cfg;
        }

        // The spec being decoded, for error messages (Decode is not re-entrant per thread).
        [System.ThreadStatic] private static string Spec;

        private static string Required(string[] p, int i)
        {
            if (i >= p.Length || string.IsNullOrWhiteSpace(p[i]))
                throw new System.FormatException($"Sampler spec '{Spec}': parameter {i + 1} is missing.");
            return p[i].Trim();
        }

        private static double D(string[] p, int i)
        {
            string s = Required(p, i);
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new System.FormatException($"Sampler spec '{Spec}': parameter {i + 1} ('{s}') is not a number.");
            return v;
        }

        private static int I(string[] p, int i)
        {
            string s = Required(p, i);
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                throw new System.FormatException($"Sampler spec '{Spec}': parameter {i + 1} ('{s}') is not an integer.");
            return v;
        }

        /// <summary>Optional numeric parameter: absent → <paramref name="def"/>; present but invalid → error.</summary>
        private static double DOpt(string[] p, int i, double def)
            => i >= p.Length || string.IsNullOrWhiteSpace(p[i]) ? def : D(p, i);

        /// <summary>Optional boolean parameter: absent → <paramref name="def"/>; present but invalid → error.</summary>
        private static bool BOpt(string[] p, int i, bool def)
        {
            if (i >= p.Length || string.IsNullOrWhiteSpace(p[i])) return def;
            if (!bool.TryParse(p[i].Trim(), out bool v))
                throw new System.FormatException($"Sampler spec '{Spec}': parameter {i + 1} ('{p[i]}') is not true/false.");
            return v;
        }
    }
}
