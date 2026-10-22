using System.Globalization;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Compact string codec for random samplers used inside flat-list configs
    /// (assembly task durations, downtime MTBF/MTTR): <c>Type~p1~p2…</c>.
    /// Rebuilt through <see cref="ElementBuildHelpers.BuildRandomProcess"/>, so every
    /// decoded sampler gets a per-model random stream. Engine-pure.
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
        {
            if (string.IsNullOrWhiteSpace(spec))
                throw new System.FormatException("Sampler spec is empty (expected 'Type~p1~p2…', e.g. 'Exponential~0.2').");
            spec = spec.Trim();
            int tilde   = spec.IndexOf('~');
            string type = tilde < 0 ? spec : spec.Substring(0, tilde);
            string rest = tilde < 0 ? "" : spec.Substring(tilde + 1);
            string[] p  = rest.Split('~');

            // Rebuild through the shared codec (also assigns a per-model random
            // stream) by translating into the standard unprefixed parameter keys.
            var cfg = new ElementConfig("task", "task", "task");
            cfg.Parameters["StrategyType"] = type;

            Spec = spec;
            switch (type)
            {
                case "Constant":
                    cfg.Parameters["constantValue"] = D(p, 0); break;
                case "Uniform":
                    cfg.Parameters["uniformMin"] = D(p, 0);
                    cfg.Parameters["uniformMax"] = D(p, 1); break;
                case "Normal":
                    cfg.Parameters["normalMean"]   = D(p, 0);
                    cfg.Parameters["normalStdDev"] = D(p, 1); break;
                case "Exponential":
                    cfg.Parameters["exponentialRate"] = D(p, 0); break;
                case "ExponentialMean":   // convenience for MTBF/MTTR data given as means
                    cfg.Parameters["StrategyType"]    = "Exponential";
                    cfg.Parameters["exponentialRate"] = 1.0 / System.Math.Max(1e-12, D(p, 0)); break;
                case "Triangular":
                    cfg.Parameters["triangularMin"]  = D(p, 0);
                    cfg.Parameters["triangularMode"] = D(p, 1);
                    cfg.Parameters["triangularMax"]  = D(p, 2); break;
                case "Expression":
                    cfg.Parameters["expression"] = rest; break;   // full remainder
                case "Gamma":
                    cfg.Parameters["gammaShape"] = D(p, 0);
                    cfg.Parameters["gammaScale"] = D(p, 1); break;
                case "Weibull":
                    cfg.Parameters["weibullShape"] = D(p, 0);
                    cfg.Parameters["weibullScale"] = D(p, 1); break;
                case "LogNormal":
                    cfg.Parameters["logNormalMu"]    = D(p, 0);
                    cfg.Parameters["logNormalSigma"] = D(p, 1); break;
                case "Beta":
                    cfg.Parameters["betaAlpha"] = D(p, 0);
                    cfg.Parameters["betaBeta"]  = D(p, 1);
                    cfg.Parameters["betaMin"]   = DOpt(p, 2, 0.0);
                    cfg.Parameters["betaMax"]   = DOpt(p, 3, 1.0); break;
                case "ChiSquare":
                    cfg.Parameters["chiSquareDf"] = I(p, 0); break;
                case "StudentT":
                    cfg.Parameters["studentTDf"]    = I(p, 0);
                    cfg.Parameters["studentTClamp"] = BOpt(p, 1, true); break;
                case "FDistribution":
                    cfg.Parameters["fDistD1"] = I(p, 0);
                    cfg.Parameters["fDistD2"] = I(p, 1); break;
                case "Poisson":
                    cfg.Parameters["poissonLambda"] = D(p, 0); break;
                case "Binomial":
                    cfg.Parameters["binomialN"] = I(p, 0);
                    cfg.Parameters["binomialP"] = D(p, 1); break;
                case "DiscreteUniform":
                    cfg.Parameters["discreteUniformMin"] = I(p, 0);
                    cfg.Parameters["discreteUniformMax"] = I(p, 1); break;
                default:
                    throw new System.FormatException($"Sampler spec '{spec}': unknown type '{type}'.");
            }

            return ElementBuildHelpers.BuildRandomProcess(cfg, prefix: "", clock: clock,
                                                          owner: owner ?? "", purpose: purpose ?? "sampler");
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
