using System;
using System.Collections.Generic;
using SimuLean.Collections;
using SimuLean.Headless;
using SimuLean.Resources;

namespace SimuLean.Results
{
    /// <summary>One statistic of one object per interval: <see cref="Times"/> are the interval ends.</summary>
    /// <summary>One state interval of an object: <c>[Start, End)</c> in <see cref="State"/>.</summary>
    public sealed class TimelineEntry
    {
        public ResultScope Scope { get; }
        public string Name { get; }
        public string State { get; }
        public double Start { get; }
        public double End { get; }

        public TimelineEntry(ResultScope scope, string name, string state, double start, double end)
        {
            Scope = scope; Name = name; State = state; Start = start; End = end;
        }
    }

    public sealed class TimeSeries
    {
        public ResultScope Scope { get; }
        public string Name { get; }
        public string Metric { get; }
        public List<double> Times { get; } = new List<double>();
        public List<double> Values { get; } = new List<double>();

        public TimeSeries(ResultScope scope, string name, string metric) { Scope = scope; Name = name; Metric = metric; }
    }

    /// <summary>
    /// Records time series of a headless run without touching the model: it advances the clock interval by
    /// interval (<see cref="SimClock.AdvanceClock"/> runs every event up to each boundary, so the run is the same as one
    /// call) and at each boundary reads the cumulative statistics, keeping their change over the interval:
    /// <list type="bullet">
    /// <item>elements — <c>input</c>, <c>output</c> (items in the interval), <c>content</c> (at the end), <c>content_avg</c>
    /// (time-weighted over the interval), <c>state:X</c> (share of the interval);</item>
    /// <item>resources — <c>utilization</c> and <c>state:X</c> (share of the interval);</item>
    /// <item>lists — <c>content</c>, <c>content_avg</c>, <c>pushed</c>, <c>pulled</c>.</item>
    /// </list>
    /// With a warm-up the series start at the end of the warm-up (the statistics are reset there).
    /// </summary>
    public sealed class SeriesRecorder
    {
        private readonly HeadlessModelFactory model;
        private readonly SimClock clock;
        private readonly Dictionary<(ResultScope, string, string), TimeSeries> series = new Dictionary<(ResultScope, string, string), TimeSeries>();
        private readonly List<TimeSeries> order = new List<TimeSeries>();
        private readonly Dictionary<object, Snapshot> last = new Dictionary<object, Snapshot>();
        private Dictionary<SimuLean.Resources.TaskExecutor, (string id, string team)> resourceIds;   // the results' ids (built at the first sample)
        private double lastTime = double.NaN;

        /// <summary>Length of an interval (model time units).</summary>
        public double Interval { get; }
        public IReadOnlyList<TimeSeries> Series => order;

        public SeriesRecorder(HeadlessModelFactory model, double interval)
        {
            this.model = model ?? throw new ArgumentNullException(nameof(model));
            if (!(interval > 0)) throw new ArgumentOutOfRangeException(nameof(interval), "The series interval must be positive.");
            clock = model.Clock;
            Interval = interval;
        }

        /// <summary>
        /// Runs the (started) model to <paramref name="endTime"/>, sampling at every interval boundary. With
        /// <paramref name="final"/> the run ends there and a last, shorter interval is sampled; without it the run goes on
        /// later (call again with a later time — e.g. progress slices) and intervals stay whole.
        /// </summary>
        public void RunTo(double endTime, bool final = true)
        {
            if (double.IsNaN(lastTime))
            {
                double start = clock.GetSimulationTime();
                if (model.WarmupTime > start && model.WarmupTime < endTime) clock.AdvanceClock(model.WarmupTime);
                Baseline();
            }
            double eps = 1e-9 * Math.Max(1, Math.Abs(endTime));
            while (!clock.IsStopped && lastTime + Interval <= endTime + eps)
            {
                clock.AdvanceClock(Math.Min(lastTime + Interval, endTime));
                Sample();
            }
            if (clock.IsStopped) { Sample(); return; }   // an end condition stopped the run: the last, shorter interval ends there (S2)
            if (final && lastTime < endTime - eps)
            {
                clock.AdvanceClock(endTime);
                Sample();
            }
            if (clock.GetSimulationTime() < endTime) clock.AdvanceClock(endTime);
        }

        /// <summary>Adds the recorded series to <paramref name="results"/>.</summary>
        public void AddTo(RunResults results) => results.Series.AddRange(order);

        private void Baseline()
        {
            lastTime = clock.GetSimulationTime();
            foreach (var e in model.GetAllElements().Values) last[e] = Snapshot.Of(e, lastTime);
            foreach (var r in model.Executors) last[r] = Snapshot.Of(r, lastTime);
            foreach (var l in model.Lists.Values) last[l] = Snapshot.Of(l, lastTime);
        }

        private void Sample()
        {
            double now = clock.GetSimulationTime();
            double dt = now - lastTime;
            if (dt <= 0) return;
            foreach (var e in model.GetAllElements().Values)
            {
                var prev = last[e];
                var cur = Snapshot.Of(e, now);
                string n = e.GetName();
                Add(ResultScope.Element, n, ResultMetrics.Input, now, cur.In - prev.In);
                Add(ResultScope.Element, n, ResultMetrics.Output, now, cur.Out - prev.Out);
                Add(ResultScope.Element, n, ResultMetrics.Content, now, cur.Content);
                Add(ResultScope.Element, n, ResultMetrics.ContentAvg, now, (cur.Integral - prev.Integral) / dt);
                States(ResultScope.Element, n, prev, cur, now, dt);
                last[e] = cur;
            }
            resourceIds = resourceIds ?? RunResults.ResourceIds(model);
            foreach (var r in model.Executors)
            {
                var prev = last[r];
                var cur = Snapshot.Of(r, now);
                string n = resourceIds[r].id;   // as in the results: "team/member" for a shared name
                double busy = 0;
                foreach (var s in ResourceStates.Busy) busy += cur.State(s.Name) - prev.State(s.Name);
                Add(ResultScope.Resource, n, ResultMetrics.Utilization, now, busy / dt);
                States(ResultScope.Resource, n, prev, cur, now, dt);
                last[r] = cur;
            }
            foreach (var kv in model.Lists)
            {
                var l = kv.Value;
                var prev = last[l];
                var cur = Snapshot.Of(l, now);
                string n = l.Name ?? kv.Key;
                Add(ResultScope.List, n, ResultMetrics.Pushed, now, cur.In - prev.In);
                Add(ResultScope.List, n, ResultMetrics.Pulled, now, cur.Out - prev.Out);
                Add(ResultScope.List, n, ResultMetrics.Content, now, cur.Content);
                Add(ResultScope.List, n, ResultMetrics.ContentAvg, now, (cur.Integral - prev.Integral) / dt);
                last[l] = cur;
            }
            lastTime = now;
        }

        // Every state seen so far gets a value in every interval (0 when not entered in it).
        private void States(ResultScope scope, string name, Snapshot prev, Snapshot cur, double now, double dt)
        {
            if (cur.States == null) return;
            foreach (var st in cur.States)
                if (st.Value > 0 || series.ContainsKey((scope, name, ResultMetrics.State(st.Key))))
                    Add(scope, name, ResultMetrics.State(st.Key), now, (st.Value - prev.State(st.Key)) / dt);
        }

        private void Add(ResultScope scope, string name, string metric, double time, double value)
        {
            var key = (scope, name, metric);
            if (!series.TryGetValue(key, out var s))
            {
                series[key] = s = new TimeSeries(scope, name, metric);
                order.Add(s);
                // a series that starts late (a state entered for the first time) was 0 before
                if (order.Count > 1)
                    foreach (var other in order)
                        if (other != s && other.Scope == scope && other.Name == name)
                        {
                            for (int i = 0; i < other.Times.Count && other.Times[i] < time; i++) { s.Times.Add(other.Times[i]); s.Values.Add(0); }
                            break;
                        }
            }
            s.Times.Add(time);
            s.Values.Add(value);
        }

        private readonly struct Snapshot
        {
            public readonly double In, Out, Content, Integral;
            public readonly Dictionary<string, double> States;

            private Snapshot(double input, double output, double content, double integral, Dictionary<string, double> states)
            {
                In = input; Out = output; Content = content; Integral = integral; States = states;
            }

            public double State(string name) => States != null && States.TryGetValue(name, out double v) ? v : 0;

            public static Snapshot Of(Element e, double now)
            {
                var sc = e.StatsCollector;
                return sc == null
                    ? new Snapshot(0, 0, 0, 0, e.GetStateSnapshot())
                    : new Snapshot(sc.TotalInput, sc.TotalOutput, sc.ContentCurrent, sc.VarContent.GetIntegral(now), e.GetStateSnapshot());
            }

            public static Snapshot Of(TaskExecutor r, double now) => new Snapshot(0, 0, 0, 0, r.GetStateSnapshot());

            public static Snapshot Of(SimList l, double now)
                => new Snapshot(l.Pushed, l.Pulled, l.Content.Value, l.Content.GetIntegral(now), null);
        }
    }
}
