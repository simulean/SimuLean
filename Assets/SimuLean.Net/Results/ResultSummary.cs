using System;
using System.Collections.Generic;
using SimuLean.Stochastics;

namespace SimuLean.Results
{
    /// <summary>
    /// A statistic across replications: count, mean, sample standard deviation, min / max and the Student-t confidence
    /// interval of the mean (<c>mean ± t(1−α/2, n−1)·s/√n</c>; no interval with fewer than two values).
    /// </summary>
    public readonly struct StatSummary
    {
        public int N { get; }
        public double Mean { get; }
        public double StdDev { get; }
        public double Min { get; }
        public double Max { get; }
        /// <summary>Confidence level of <see cref="HalfWidth"/> (e.g. 0.95).</summary>
        public double Confidence { get; }
        /// <summary>Half-width of the confidence interval of the mean (NaN with fewer than two values).</summary>
        public double HalfWidth { get; }
        public double Lower => Mean - HalfWidth;
        public double Upper => Mean + HalfWidth;
        public bool HasInterval => !double.IsNaN(HalfWidth);

        private StatSummary(int n, double mean, double sd, double min, double max, double confidence, double halfWidth)
        {
            N = n; Mean = mean; StdDev = sd; Min = min; Max = max; Confidence = confidence; HalfWidth = halfWidth;
        }

        public static StatSummary Of(IReadOnlyList<double> values, double confidence = 0.95)
        {
            if (values == null || values.Count == 0) return new StatSummary(0, double.NaN, double.NaN, double.NaN, double.NaN, confidence, double.NaN);
            if (!(confidence > 0 && confidence < 1)) throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be in (0, 1).");
            int n = values.Count;
            double mean = 0, min = double.PositiveInfinity, max = double.NegativeInfinity;
            // Welford: stable for large, nearly equal values (counts, times far from 0)
            double m2 = 0;
            for (int i = 0; i < n; i++)
            {
                double v = values[i];
                double d = v - mean;
                mean += d / (i + 1);
                m2 += d * (v - mean);
                if (v < min) min = v;
                if (v > max) max = v;
            }
            double sd = n > 1 ? Math.Sqrt(Math.Max(0, m2 / (n - 1))) : 0;
            double half = n > 1 ? TQuantile(confidence, n - 1) * sd / Math.Sqrt(n) : double.NaN;
            return new StatSummary(n, mean, sd, min, max, confidence, half);
        }

        /// <summary>The two-sided Student t critical value t(1 − α/2, df).</summary>
        public static double TQuantile(double confidence, int df) => Probability.StudentTQuantile(0.5 + confidence / 2, df);

        public override string ToString()
            => N == 0 ? "—" : HasInterval ? $"{Mean:G6} ± {HalfWidth:G4} (n = {N})" : $"{Mean:G6} (n = {N})";
    }

    /// <summary>One object's statistics across replications: each statistic's values and their summary.</summary>
    public sealed class SummaryObject
    {
        private readonly List<string> order = new List<string>();
        private readonly Dictionary<string, List<double>> values = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        private readonly Dictionary<string, StatSummary> summaries = new Dictionary<string, StatSummary>(StringComparer.Ordinal);

        public ResultScope Scope { get; }
        public string Id { get; }
        public string Name { get; }
        public string Type { get; }
        public string Group { get; }

        internal SummaryObject(ResultObject first)
        {
            Scope = first.Scope; Id = first.Id; Name = first.Name; Type = first.Type; Group = first.Group;
        }

        /// <summary>The statistics, in the order of the runs' results.</summary>
        public IReadOnlyList<string> Metrics => order;
        /// <summary>The value of each replication the object appeared in, in run order.</summary>
        public IReadOnlyList<double> Values(string metric) => values.TryGetValue(metric, out var v) ? v : (IReadOnlyList<double>)Array.Empty<double>();
        public bool TryGet(string metric, out StatSummary summary) => summaries.TryGetValue(metric, out summary);
        public StatSummary this[string metric]
            => summaries.TryGetValue(metric, out var s) ? s : throw new KeyNotFoundException($"{Scope} '{Name}' has no statistic '{metric}'.");

        internal void Add(string metric, int run, double value)
        {
            if (!values.TryGetValue(metric, out var list))
            {
                values[metric] = list = new List<double>();
                order.Add(metric);
            }
            // a state / field missing from earlier runs of this object was 0 there (other statistics: only the runs that have it)
            if (ResultMetrics.IsState(metric) || ResultMetrics.IsField(metric)) while (list.Count < run) list.Add(0);
            list.Add(value);
        }

        internal void Close(int runs, double confidence)
        {
            foreach (string m in order)
            {
                var list = values[m];
                bool zeroWhenMissing = ResultMetrics.IsState(m) || ResultMetrics.IsField(m);
                if (zeroWhenMissing) while (list.Count < runs) list.Add(0);
                summaries[m] = StatSummary.Of(list, confidence);
            }
        }
    }

    /// <summary>
    /// The statistics of several replications of a model: every statistic of every object of the runs'
    /// <see cref="RunResults"/> with its <see cref="StatSummary"/> (mean, standard deviation, min / max, t-based
    /// confidence interval) and the value of each replication. A state never entered or a list field never recorded in
    /// a replication counts as 0 there; an object missing from a replication is summarised over the ones it appears in.
    /// </summary>
    public sealed class ResultSummary
    {
        private readonly List<SummaryObject> objects = new List<SummaryObject>();

        public double Confidence { get; }
        /// <summary>Number of replications summarised.</summary>
        public int Replications { get; }
        /// <summary>The first replication's run info (model, horizon, warm-up).</summary>
        public RunInfo Info { get; }
        public IReadOnlyList<SummaryObject> Objects => objects;

        private ResultSummary(double confidence, int replications, RunInfo info)
        {
            Confidence = confidence; Replications = replications; Info = info;
        }

        public IEnumerable<SummaryObject> Of(ResultScope scope)
        {
            foreach (var o in objects) if (o.Scope == scope) yield return o;
        }

        public SummaryObject Find(ResultScope scope, string idOrName)
        {
            SummaryObject byName = null;
            foreach (var o in objects)
            {
                if (o.Scope != scope) continue;
                if (o.Id == idOrName) return o;
                if (byName == null && o.Name == idOrName) byName = o;
            }
            return byName;
        }

        public static ResultSummary Of(IReadOnlyList<RunResults> runs, double confidence = 0.95)
        {
            if (runs == null) throw new ArgumentNullException(nameof(runs));
            if (!(confidence > 0 && confidence < 1)) throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be in (0, 1).");
            var summary = new ResultSummary(confidence, runs.Count, runs.Count > 0 ? runs[0].Info : new RunInfo());
            var byKey = new Dictionary<(ResultScope, string), SummaryObject>();
            var seenIn = new Dictionary<SummaryObject, int>();
            for (int r = 0; r < runs.Count; r++)
            {
                if (runs[r] == null) throw new ArgumentException($"Replication {r + 1} has no results.", nameof(runs));
                foreach (var o in runs[r].Objects)
                {
                    if (!byKey.TryGetValue((o.Scope, o.Id), out var s))
                    {
                        byKey[(o.Scope, o.Id)] = s = new SummaryObject(o);
                        summary.objects.Add(s);
                        seenIn[s] = 0;
                    }
                    int k = seenIn[s]++;   // position among the replications this object appears in
                    foreach (var m in o.Metrics) s.Add(m.Key, k, m.Value);
                }
            }
            foreach (var s in summary.objects) s.Close(seenIn[s], confidence);
            return summary;
        }
    }
}
