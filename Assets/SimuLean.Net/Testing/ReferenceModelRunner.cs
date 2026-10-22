using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean.Data;
using SimuLean.Headless;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Testing
{
    /// <summary>
    /// Runs a reference model headless and compares its statistics with a stored baseline
    ///. Shared by the dotnet runner (<c>StandaloneTests/referencemodels</c>) and the
    /// Unity EditMode tests, so both check the same numbers. Engine-pure (no Unity types).
    /// <para>Baseline = JSON object: <c>model</c>, <c>elements</c> (count), <c>connections</c> (count) and
    /// <c>stats</c>: element id → { metric → value }. Structural differences (element / connection
    /// counts, missing elements or metrics) always fail; numbers are compared with a relative
    /// tolerance.</para>
    /// </summary>
    public static class ReferenceModelRunner
    {
        /// <summary>Default relative tolerance (runs are seeded, so differences mean behaviour changed).</summary>
        public const double DefaultTolerance = 1e-6;

        /// <summary>Result of one run: per-element metrics, sorted by element id.</summary>
        public sealed class Result
        {
            public string Model;
            public int Elements;
            public int Connections;
            public SortedDictionary<string, SortedDictionary<string, double>> Stats =
                new SortedDictionary<string, SortedDictionary<string, double>>(StringComparer.Ordinal);
        }

        /// <summary>Builds <paramref name="config"/> headless (its fixed seed), runs it to its horizon
        /// and collects its statistics (<see cref="RunResults"/>: elements, slots, resources, teams, lists, downtimes, traffic).</summary>
        public static Result Run(string name, SimulationConfig config)
        {
            var clock = new SimClock(config.UseFixedSeed ? (int?)config.Seed : null);
            var factory = new HeadlessModelFactory(clock, enableLogging: false);
            Dictionary<string, Element> elements = factory.BuildModel(config);
            factory.StartModel();
            clock.AdvanceClock(config.MaxSimulationTime);

            var r = new Result { Model = name, Elements = elements.Count, Connections = config.Connections.Count };
            foreach (var o in RunResults.Capture(factory, name).Objects)
            {
                var m = new SortedDictionary<string, double>(StringComparer.Ordinal);
                foreach (var kv in o.Metrics) m[kv.Key] = kv.Value;
                r.Stats[Key(o)] = m;
            }
            return r;
        }

        /// <summary>Baseline key of a result object: the element id, or <c>scope:id</c> (e.g. <c>team:operators</c>; the model's <c>traffic</c>).</summary>
        public static string Key(ResultObject o)
            => o.Scope == ResultScope.Element ? o.Id
             : o.Scope == ResultScope.Traffic ? "traffic" : o.Scope == ResultScope.Model ? "model"
             : o.Scope.ToString().ToLowerInvariant() + ":" + o.Id;

        /// <summary>Serializes a result as a baseline JSON document.</summary>
        public static string ToJson(Result r)
        {
            var stats = new Dictionary<string, object>();
            foreach (var el in r.Stats)
            {
                var m = new Dictionary<string, object>();
                foreach (var kv in el.Value) m[kv.Key] = Round(kv.Value);
                stats[el.Key] = m;
            }
            return MiniJson.Serialize(new Dictionary<string, object>
            {
                ["model"] = r.Model, ["elements"] = r.Elements, ["connections"] = r.Connections, ["stats"] = stats,
            });
        }

        // 12 significant digits: stable text, far below the comparison tolerance.
        private static double Round(double v)
            => double.Parse(v.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        /// <summary>Compares a run with a baseline; returns the differences (empty = pass).</summary>
        public static List<string> Compare(string baselineJson, Result r, double tolerance = DefaultTolerance)
        {
            var diffs = new List<string>();
            if (!(MiniJson.Parse(baselineJson) is Dictionary<string, object> b))
            {
                diffs.Add("baseline is not a JSON object");
                return diffs;
            }
            if (b.GetLong("elements") != r.Elements) diffs.Add($"element count {r.Elements} (baseline {b.GetLong("elements")})");
            if (b.GetLong("connections") != r.Connections) diffs.Add($"connection count {r.Connections} (baseline {b.GetLong("connections")})");

            var bs = b.GetObject("stats") ?? new Dictionary<string, object>();
            foreach (string id in bs.Keys.Union(r.Stats.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!r.Stats.TryGetValue(id, out var got)) { diffs.Add($"{id}: missing in the run"); continue; }
                if (!(bs.TryGetValue(id, out object o) && o is Dictionary<string, object> exp)) { diffs.Add($"{id}: not in the baseline"); continue; }
                foreach (string metric in exp.Keys.Union(got.Keys).OrderBy(k => k, StringComparer.Ordinal))
                {
                    bool inRun = got.TryGetValue(metric, out double actual);
                    bool inBase = exp.ContainsKey(metric);
                    double expected = inBase ? exp.GetDouble(metric) : 0;
                    // A state that appears / disappears with a tiny ratio is a numeric, not structural, change.
                    bool stateMetric = ResultMetrics.IsState(metric);
                    if (!inRun) actual = 0;
                    if (!inBase && !stateMetric) { diffs.Add($"{id}.{metric}: not in the baseline"); continue; }
                    if (!inRun && !stateMetric) { diffs.Add($"{id}.{metric}: missing in the run"); continue; }
                    double scale = Math.Max(1.0, Math.Abs(expected));
                    if (Math.Abs(actual - expected) > tolerance * scale)
                        diffs.Add($"{id}.{metric}: {actual.ToString("G8", CultureInfo.InvariantCulture)} (baseline {expected.ToString("G8", CultureInfo.InvariantCulture)})");
                }
            }
            return diffs;
        }
    }
}
