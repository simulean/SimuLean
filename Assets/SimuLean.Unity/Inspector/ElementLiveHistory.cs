using System;
using System.Collections.Generic;
using SimuLean.Statistics;

namespace SimuLean.Unity
{
    /// <summary>
    /// Read-only history of every element of a running model for the inspector charts: one bucket
    /// (<see cref="Bucket"/> simulation units long) per element with the time-weighted average content
    /// (Δ∫content·dt / Δt from <see cref="StatTimeWeightedVariable.GetIntegral"/>), the highest content seen at
    /// a sample and the cumulative input / output counts. Sampled from the Link-layer
    /// <see cref="ElementStatsCollector"/> once per rendered frame by <see cref="ElementInspectorPanel"/>:
    /// <b>nothing is scheduled on the simulation clock</b>, so runs are unchanged. When one frame covers several
    /// buckets (high speed) that frame's average is spread over them; the maximum is a frame-sampled maximum.
    /// Per-period state splits come from the exact state logs (<see cref="StateSplit"/>).
    /// </summary>
    public sealed class ElementLiveHistory
    {
        public sealed class Series
        {
            public readonly List<float> Avg = new List<float>();   // time-weighted mean content in the bucket
            public readonly List<float> Max = new List<float>();   // highest content sampled in the bucket
            public readonly List<int> In = new List<int>();        // cumulative input at the end of the bucket
            public readonly List<int> Out = new List<int>();       // cumulative output at the end of the bucket
            internal double lastT, lastIntegral;
            internal bool started;
        }

        /// <summary>Bucket length in simulation time units.</summary>
        public double Bucket { get; private set; } = 3600.0;
        public SimClock Clock { get; private set; }

        private readonly Dictionary<Element, Series> series = new Dictionary<Element, Series>();
        private readonly List<Element> elements = new List<Element>();
        private double lastSample = -1;
        private int samplesSinceRefresh;

        /// <summary>Starts recording the elements of <paramref name="clock"/>'s model (clears previous data).</summary>
        public void Attach(SimClock clock, double bucket)
        {
            Clock = clock;
            Bucket = bucket > 0 ? bucket : 3600.0;
            // the next sample refreshes the element list (int.MaxValue wrapped to int.MinValue on ++ and the list stayed
            // empty for ~4 billion samples: the history never recorded anything)
            series.Clear(); elements.Clear(); lastSample = -1; samplesSinceRefresh = RefreshEvery;
        }

        public Series Of(Element e) => e != null && series.TryGetValue(e, out var s) ? s : null;

        /// <summary>Samples every element at the current simulation time (call once per frame; cheap when time did not move).</summary>
        public void Sample()
        {
            if (Clock == null) return;
            double now = Clock.GetSimulationTime();
            if (now < lastSample) { Attach(Clock, Bucket); }    // clock reset / restarted run
            if (now <= lastSample) return;
            lastSample = now;
            if (++samplesSinceRefresh > RefreshEvery) RefreshElements();
            foreach (var e in elements)
            {
                var st = e.StatsCollector;
                if (st == null) continue;
                Push(series[e], now, st.VarContent, st.VarContent.Value, st.TotalInput, st.TotalOutput);
            }
        }

        private const int RefreshEvery = 30;   // samples between looks for elements created during the run

        private void RefreshElements()
        {
            samplesSinceRefresh = 0;
            foreach (var e in Clock.Context.Elements)
                if (e != null && !series.ContainsKey(e)) { series[e] = new Series(); elements.Add(e); }
        }

        private void Push(Series s, double now, StatTimeWeightedVariable v, double value, int cin, int cout)
        {
            double integral = v.GetIntegral(now);
            if (!s.started || integral < s.lastIntegral - 1e-9 || now < s.lastT)
            {
                s.Avg.Clear(); s.Max.Clear(); s.In.Clear(); s.Out.Clear();
                s.started = true; s.lastT = 0; s.lastIntegral = 0;
            }
            double dt = now - s.lastT;
            if (dt <= 0) return;
            float avg = (float)((integral - s.lastIntegral) / dt);
            int b0 = (int)(s.lastT / Bucket), b1 = (int)(now / Bucket);
            if (b1 - b0 > 100000) b0 = b1 - 100000;   // guard against absurd bucket counts
            for (int b = b0; b <= b1; b++)
            {
                double from = Math.Max(s.lastT, b * Bucket), to = Math.Min(now, (b + 1) * Bucket);
                double w = to - from;
                if (w <= 0 && b != b1) continue;
                while (s.Avg.Count <= b) { s.Avg.Add(0); s.Max.Add(0); s.In.Add(cin); s.Out.Add(cout); }
                double covered = from - b * Bucket;   // part of this bucket already accumulated
                if (w > 0) s.Avg[b] = (float)((s.Avg[b] * covered + avg * w) / Math.Max(1e-12, covered + w));
                s.Max[b] = Math.Max(s.Max[b], (float)value);
                s.In[b] = cin; s.Out[b] = cout;
            }
            s.lastT = now; s.lastIntegral = integral;
        }

        /// <summary>Merges consecutive buckets so at most <paramref name="maxColumns"/> columns remain (means / maxima).</summary>
        public static void Downsample(Series s, int first, int count, int maxColumns, out float[] avg, out float[] max, out int bucketsPerColumn)
        {
            count = Math.Max(0, Math.Min(count, s.Avg.Count - first));
            bucketsPerColumn = Math.Max(1, (count + maxColumns - 1) / Math.Max(1, maxColumns));
            int cols = (count + bucketsPerColumn - 1) / bucketsPerColumn;
            avg = new float[cols]; max = new float[cols];
            for (int c = 0; c < cols; c++)
            {
                int n = 0;
                for (int i = c * bucketsPerColumn; i < Math.Min(count, (c + 1) * bucketsPerColumn); i++)
                {
                    avg[c] += s.Avg[first + i]; n++;
                    max[c] = Math.Max(max[c], Math.Max(s.Max[first + i], s.Avg[first + i]));
                }
                if (n > 0) avg[c] /= n;
            }
        }

        /// <summary>Items that entered (or left) in the last <paramref name="window"/> sim units, from the bucket counts (NaN if unknown).</summary>
        public double CountLast(Series s, double now, double window, bool input)
        {
            if (s == null || s.In.Count == 0) return double.NaN;
            var c = input ? s.In : s.Out;
            int last = c.Count - 1;
            int back = Math.Max(0, (int)((now - window) / Bucket));
            if (back >= last) return double.NaN;
            return c[last] - c[back];
        }

        // ── per-period state split (exact, from the element's state log) ─────

        /// <summary>Time per state and period. Incremental: pass the same object back to process only new log entries.</summary>
        public sealed class PeriodStates
        {
            public readonly Dictionary<string, List<double>> ByState = new Dictionary<string, List<double>>();
            public int Periods;
            public double Origin, Length;
            internal int consumed;
            internal double openT;
            internal string openState;
        }

        /// <summary>Column index of a time for periods of <paramref name="length"/> starting at <paramref name="origin"/>.</summary>
        public static int PeriodOf(double t, double origin, double length) => (int)Math.Floor((t - origin) / length);

        public static PeriodStates StateSplit(Element e, double now, double origin, double length, PeriodStates cache = null)
        {
            var log = e.GetStateLog();
            var c = cache;
            if (c == null || c.Length != length || c.Origin != origin || log.Count < c.consumed)
                c = new PeriodStates { Origin = origin, Length = length };
            for (; c.consumed < log.Count; c.consumed++)
            {
                var entry = log[c.consumed];
                if (c.openState != null) Add(c, c.openState, c.openT, entry.Time);
                c.openState = entry.State.Name ?? "?"; c.openT = entry.Time;
            }
            int periods = Math.Max(1, PeriodOf(Math.Max(origin, now - 1e-9), origin, length) + 1);
            if (periods > 5000) periods = 5000;
            c.Periods = periods;
            foreach (var l in c.ByState.Values) while (l.Count < periods) l.Add(0);
            return c;
        }

        /// <summary>Per-period time including the still-open state up to <paramref name="now"/> (does not modify the cache).</summary>
        public static Dictionary<string, double[]> WithOpen(PeriodStates c, double now)
        {
            var r = new Dictionary<string, double[]>();
            foreach (var kv in c.ByState)
            {
                var a = new double[c.Periods];
                for (int i = 0; i < a.Length && i < kv.Value.Count; i++) a[i] = kv.Value[i];
                r[kv.Key] = a;
            }
            if (c.openState != null && now > c.openT)
            {
                if (!r.TryGetValue(c.openState, out var a)) { a = new double[c.Periods]; r[c.openState] = a; }
                Spread(c.openT, now, c.Origin, c.Length, (p, dt) => { if (p >= 0 && p < a.Length) a[p] += dt; });
            }
            return r;
        }

        private static void Add(PeriodStates c, string state, double from, double to)
        {
            if (!c.ByState.TryGetValue(state, out var l)) { l = new List<double>(); c.ByState[state] = l; }
            Spread(from, to, c.Origin, c.Length, (p, dt) =>
            {
                if (p < 0 || p >= 5000) return;
                while (l.Count <= p) l.Add(0);
                l[p] += dt;
            });
        }

        private static void Spread(double from, double to, double origin, double length, Action<int, double> add)
        {
            int guard = 0;
            for (double t = from; t < to && guard++ < 10000;)
            {
                int p = PeriodOf(t, origin, length);
                double end = Math.Min(to, origin + (p + 1) * length);
                if (end <= t) end = to;
                add(p, end - t);
                t = end;
            }
        }
    }
}
