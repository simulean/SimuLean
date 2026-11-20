using System;
using System.Collections.Generic;
using SimuLean.Collections;
using SimuLean.Resources;
using SimuLean.Headless;

namespace SimuLean.Results
{
    /// <summary>
    /// The statistics of one element by their <see cref="ResultMetrics"/> names — the one place their values are read
    /// <see cref="RunResults.Capture(SimuLean.Headless.HeadlessModelFactory, string, bool)"/> reads every one
    /// through it, and the expression function <c>stat('M1', 'output')</c> reads one, live, at the current time (warm-up
    /// included until the statistics are reset). Read-only: it reads the trackers the link layer and the state tracker keep
    /// (CLAUDE.md: statistics have two sources), it never computes them again nor changes anything.
    /// </summary>
    public static class ElementMetrics
    {
        /// <summary>The value of <paramref name="metric"/> for <paramref name="e"/> now; false when the element does not have
        /// that statistic (an unknown name, a setup statistic of a station without setups, a last entry before any…).</summary>
        public static bool TryRead(Element e, string metric, out double value)
        {
            value = 0;
            if (e == null || string.IsNullOrEmpty(metric)) return false;
            var sc = e.StatsCollector;
            switch (metric)
            {
                case ResultMetrics.Input: value = e.GetStatsInputCount(); return true;
                case ResultMetrics.Output: value = e.GetStatsOutputCount(); return true;
                case ResultMetrics.Content: value = e.GetStatsContentCurrent(); return true;
                case ResultMetrics.ContentAvg: value = e.GetStatsContentAvgTW(); return true;
                case ResultMetrics.ContentMax: value = e.GetStatsContentMax(); return true;
                case ResultMetrics.StayAvg: value = e.GetStatsStayTimeMean(); return true;
                case ResultMetrics.StayStd: value = e.GetStatsStayTimeStdDev(); return true;
                case ResultMetrics.StayMin: value = e.GetStatsStayTimeMin(); return true;
                case ResultMetrics.StayMax: value = e.GetStatsStayTimeMax(); return true;
                case ResultMetrics.StayCount: value = sc != null ? sc.VarStayTime.Count : 0; return true;
                case ResultMetrics.StayP90:
                    if (sc?.StayValues == null) return false;
                    value = sc.StayValues.Count > 0 ? sc.StayValues.Percentile(0.9) : 0; return true;
                case ResultMetrics.TisAvg: case ResultMetrics.TisStd: case ResultMetrics.TisMin: case ResultMetrics.TisMax: case ResultMetrics.TisP90:
                {
                    var t = sc?.TimeInSystem;
                    if (t == null) return false;
                    value = t.Count == 0 ? 0
                          : metric == ResultMetrics.TisAvg ? t.Mean : metric == ResultMetrics.TisStd ? t.Std
                          : metric == ResultMetrics.TisMin ? t.Min : metric == ResultMetrics.TisMax ? t.Max : t.Percentile(0.9);
                    return true;
                }
                case ResultMetrics.Utilization: value = ResultMetrics.UtilizationOf(e); return true;
                case ResultMetrics.UtilizationOnShift:
                {
                    double offShift = e.GetStateRatio(ElementState.OFF_SHIFT);
                    if (!(offShift > 0 && offShift < 1)) return false;
                    value = ResultMetrics.UtilizationOf(e) / (1 - offShift);
                    return true;
                }
                case ResultMetrics.Stops: value = e.StopCount; return true;
                case ResultMetrics.LastEntry:
                    if (sc == null || double.IsNaN(sc.LastEntryTime)) return false;
                    value = sc.LastEntryTime; return true;
                case ResultMetrics.LastExit:
                    if (sc == null || double.IsNaN(sc.LastExitTime)) return false;
                    value = sc.LastExitTime; return true;
                case ResultMetrics.Interruptions: case ResultMetrics.InterruptedTime: case ResultMetrics.InterruptedTimeAvg:
                {
                    // its jobs interrupted over every use of the element — the process, its setup crew, an Assembly Station's
                    // per-task uses — when one of their teams can preempt
                    int interruptions = 0; double paused = 0; bool preemptive = false;
                    foreach (var use in UsesOf(e))
                    {
                        preemptive |= use.Dispatcher.Preemptive;
                        interruptions += use.Interruptions;
                        paused += use.InterruptedTime;   // the open pauses up to now included (as downtime_total)
                    }
                    if (!preemptive) return false;
                    value = metric == ResultMetrics.Interruptions ? interruptions
                          : metric == ResultMetrics.InterruptedTime ? paused : interruptions > 0 ? paused / interruptions : 0;
                    return true;
                }
                case ResultMetrics.Setups: case ResultMetrics.SetupTime: case ResultMetrics.SetupTimeAvg:
                {
                    if (!(e is ISetupStation ss) || ss.Setup == null || !ss.Setup.IsActive) return false;
                    value = metric == ResultMetrics.Setups ? ss.Setup.Count : metric == ResultMetrics.SetupTime ? ss.Setup.TotalTime : ss.Setup.MeanTime;
                    return true;
                }
                case ResultMetrics.Timeouts:
                    if (e.LimitedWaits.Count == 0) return false;
                    value = e.Timeouts; return true;
            }
            if (metric.StartsWith(ResultMetrics.Timeouts + ":", StringComparison.Ordinal))
            {
                foreach (var kind in e.LimitedWaits)
                    if (ResultMetrics.TimeoutsOf(kind) == metric) { value = e.TimeoutsOf(kind); return true; }
                return false;
            }
            if (ResultMetrics.IsState(metric))
            {
                string state = metric.Substring(ResultMetrics.StatePrefix.Length);
                if (!ElementState.IsKnown(state)) return false;
                value = e.GetStateRatio(ElementState.Of(state));
                return true;
            }
            if (ResultMetrics.IsTally(metric)) return TryTally(sc?.Tallies, metric, out value);
            if (e is IElementMetrics own)
            {
                bool found = false; double v = 0;
                own.ReportMetrics((name, x) => { if (!found && name == metric) { found = true; v = x; } });
                value = v;
                return found;
            }
            return false;
        }

        /// <summary>The value of a tally statistic (<see cref="ResultMetrics.TallyStatistics"/>).</summary>
        public static double TallyValue(SimuLean.Statistics.TallySamples t, string statistic)
        {
            switch (statistic)
            {
                case ResultMetrics.TallyMean: return t.Mean;
                case ResultMetrics.TallyStd: return t.Std;
                case ResultMetrics.TallyMin: return t.Min;
                case ResultMetrics.TallyMax: return t.Max;
                case ResultMetrics.TallyP50: return t.Percentile(0.5);
                case ResultMetrics.TallyP90: return t.Percentile(0.9);
                case ResultMetrics.TallyCount: return t.Count;
            }
            return double.NaN;
        }

        // tally:NAME[SPLIT]:STAT
        private static bool TryTally(SimuLean.Statistics.ItemTallies tallies, string metric, out double value)
        {
            value = 0;
            if (tallies == null) return false;
            int last = metric.LastIndexOf(':');
            if (last <= ResultMetrics.TallyPrefix.Length) return false;
            string stat = metric.Substring(last + 1), body = metric.Substring(ResultMetrics.TallyPrefix.Length, last - ResultMetrics.TallyPrefix.Length);
            foreach (var (name, split, t) in tallies.All())
            {
                string key = split != null ? name + "[" + split + "]" : name;
                if (key != body || t.Count == 0) continue;
                value = TallyValue(t, stat);
                return !double.IsNaN(value);
            }
            return false;
        }

        /// <summary>The uses of resources of an element, each once: its process, its setup crew, an Assembly Station's task uses.</summary>
        public static List<StationResources> UsesOf(Element e)
        {
            var uses = new List<StationResources>();
            void Add(StationResources u) { if (u != null && !uses.Contains(u)) uses.Add(u); }
            if (e is IResourceUser user) Add(user.Resources);
            if (e is ISetupStation ss) Add(ss.Setup?.Resources);
            if (e is AssemblyStation a) foreach (var t in a.Tasks) Add(t.Resources);
            return uses;
        }

        /// <summary>True when <paramref name="metric"/> names a statistic an element can have (the build check of
        /// <c>stat()</c>): a fixed name, <c>state:X</c> of a known state, <c>tally:…</c>, <c>timeouts:…</c>, or one of
        /// <paramref name="e"/>'s own statistics.</summary>
        public static bool IsKnown(Element e, string metric)
        {
            if (string.IsNullOrEmpty(metric)) return false;
            if (((IList<string>)Names).Contains(metric)) return true;
            if (((IList<string>)ResultMetrics.QueueStay).Contains(metric)) return e?.StatsCollector?.StayValues != null;
            if (((IList<string>)ResultMetrics.TimeInSystem).Contains(metric)) return e?.StatsCollector?.TimeInSystem != null;
            if (ResultMetrics.IsState(metric)) return ElementState.IsKnown(metric.Substring(ResultMetrics.StatePrefix.Length));
            if (ResultMetrics.IsTally(metric) || metric.StartsWith(ResultMetrics.Timeouts + ":", StringComparison.Ordinal)) return true;
            if (e is IElementMetrics own)
            {
                bool found = false;
                own.ReportMetrics((name, _) => found |= name == metric);
                return found;
            }
            return false;
        }

        /// <summary>The element statistics of every element (<see cref="IsKnown"/> adds states, tallies and own ones).</summary>
        public static readonly IReadOnlyList<string> Names = new[]
        {
            ResultMetrics.Input, ResultMetrics.Output, ResultMetrics.Content, ResultMetrics.ContentAvg, ResultMetrics.ContentMax,
            ResultMetrics.StayAvg, ResultMetrics.StayStd, ResultMetrics.StayMin, ResultMetrics.StayMax, ResultMetrics.StayCount,
            ResultMetrics.Utilization, ResultMetrics.UtilizationOnShift, ResultMetrics.Stops, ResultMetrics.LastEntry, ResultMetrics.LastExit,
            ResultMetrics.Interruptions, ResultMetrics.InterruptedTime, ResultMetrics.InterruptedTimeAvg,
            ResultMetrics.Setups, ResultMetrics.SetupTime, ResultMetrics.SetupTimeAvg, ResultMetrics.Timeouts,
        };
    }

    /// <summary>A team's statistics by their <see cref="ResultMetrics"/> names (results and <c>team('T', …)</c>), plus the
    /// live counts an expression reads: <c>free</c> (members free now), <c>busy</c> (on a task), <c>waiting</c> (requests
    /// waiting). Read-only.</summary>
    public static class TeamMetrics
    {
        public const string Free = "free", Busy = "busy", Waiting = "waiting";

        /// <summary>The names <c>team()</c> reads.</summary>
        public static readonly IReadOnlyList<string> Names = new[]
        {
            Free, Busy, Waiting, ResultMetrics.Members, ResultMetrics.Utilization, ResultMetrics.Served, ResultMetrics.Tasks,
            ResultMetrics.RequestWaitAvg, ResultMetrics.RequestWaitStd, ResultMetrics.RequestsWaitingAvg,
            ResultMetrics.Preemptions, ResultMetrics.Interruptions,
        };

        public static bool TryRead(ResourceDispatcher d, string metric, double now, out double value)
        {
            value = 0;
            if (d == null) return false;
            switch (metric)
            {
                case Free: { int n = 0; foreach (var m in d.Members) if (m.IsAvailable) n++; value = n; return true; }
                case Busy: { int n = 0; foreach (var m in d.Members) if (m.CurrentTask != null) n++; value = n; return true; }
                case Waiting: value = d.Requests.Count; return true;
                case ResultMetrics.Members: value = d.Members.Count; return true;
                case ResultMetrics.Utilization:
                {
                    double u = 0;
                    foreach (var m in d.Members) u += m.Utilization;
                    value = d.Members.Count > 0 ? u / d.Members.Count : 0;
                    return true;
                }
                case ResultMetrics.Served: value = d.Served; return true;
                case ResultMetrics.Tasks: { double t = 0; foreach (var m in d.Members) t += m.TasksDone; value = t; return true; }
                case ResultMetrics.RequestWaitAvg: value = d.Requests.WaitTime.Average; return true;
                case ResultMetrics.RequestWaitStd: value = d.Requests.WaitTime.StdDev; return true;
                case ResultMetrics.RequestsWaitingAvg: value = d.Requests.Content.GetTimeWeightedAverage(now); return true;
                case ResultMetrics.Preemptions: case ResultMetrics.Interruptions:
                {
                    if (!d.Preemptive) return false;
                    int n = 0;
                    foreach (var m in d.Members) n += metric == ResultMetrics.Preemptions ? m.Preemptions : m.Interruptions;
                    value = n;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>A list's statistics by their <see cref="ResultMetrics"/> names (results and <c>list('L', …)</c>), plus
    /// <c>pending</c> (pulls waiting now). Read-only.</summary>
    public static class ListMetrics
    {
        public const string Pending = "pending";

        /// <summary>The names <c>list()</c> reads (and <c>field:F:push_avg|pull_avg|pull_max</c> of a monitored field).</summary>
        public static readonly IReadOnlyList<string> Names = new[]
        {
            ResultMetrics.Content, Pending, ResultMetrics.Pushed, ResultMetrics.Pulled, ResultMetrics.ContentAvg, ResultMetrics.ContentMax,
            ResultMetrics.WaitAvg, ResultMetrics.WaitStd, ResultMetrics.PendingPullsAvg,
        };

        public static bool TryRead(SimList l, string metric, double now, out double value)
        {
            value = 0;
            if (l == null) return false;
            switch (metric)
            {
                case ResultMetrics.Content: value = l.Content.Value; return true;
                case Pending: value = l.PendingPullCount; return true;
                case ResultMetrics.Pushed: value = l.Pushed; return true;
                case ResultMetrics.Pulled: value = l.Pulled; return true;
                case ResultMetrics.ContentAvg: value = l.Content.GetTimeWeightedAverage(now); return true;
                case ResultMetrics.ContentMax: value = l.Content.Max; return true;
                case ResultMetrics.WaitAvg: value = l.WaitTime.Average; return true;
                case ResultMetrics.WaitStd: value = l.WaitTime.StdDev; return true;
                case ResultMetrics.PendingPullsAvg: value = l.PendingPulls.GetTimeWeightedAverage(now); return true;
            }
            if (ResultMetrics.IsField(metric))
            {
                int last = metric.LastIndexOf(':');
                string field = metric.Substring(ResultMetrics.FieldPrefix.Length, Math.Max(0, last - ResultMetrics.FieldPrefix.Length)), stat = metric.Substring(last + 1);
                if (!l.FieldStatistics.TryGetValue(field, out var fs)) return false;
                switch (stat)
                {
                    case "push_avg": value = fs.AtPush.Average; return true;
                    case "pull_avg": value = fs.AtPull.Average; return true;
                    case "pull_max": value = fs.AtPull.Max; return true;
                }
            }
            return false;
        }

        public static bool IsKnown(SimList l, string metric)
        {
            if (((IList<string>)Names).Contains(metric)) return true;
            return ResultMetrics.IsField(metric) && TryRead(l, metric, 0, out _);
        }
    }
}
