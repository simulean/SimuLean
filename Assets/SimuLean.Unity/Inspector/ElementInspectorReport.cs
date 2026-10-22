using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean.Results.Charts;
using SimuLean.Storage;

namespace SimuLean.Unity
{
    /// <summary>
    /// Builds the generic inspector content of any <see cref="Element"/> from what the engine already records:
    /// the Link-layer <see cref="Statistics.ElementStatsCollector"/> (content, input / output, stay times), the
    /// state tracker and log (state split, utilisation), the downtime API (active stops) and the
    /// <see cref="ElementLiveHistory"/>; then lets the <see cref="IElementInspectorContributor"/>s add their rows.
    /// <b>Read-only</b>: it never changes the simulation.
    /// </summary>
    public static class ElementInspectorReport
    {
        public static InspectorView Build(Element e, ElementInspectorContext c, bool withCharts,
                                          IEnumerable<IElementInspectorContributor> contributors = null,
                                          Dictionary<Element, ElementLiveHistory.PeriodStates> stateCache = null)
        {
            string state = e.CurrentState.Name;
            var v = new InspectorView
            {
                Title = e.GetName(),
                Subtitle = ((object)e).GetType().Name + (c.Owner != null && c.Owner.name != e.GetName() ? " · " + c.Owner.name : ""),
                StateText = InspectorPalette.StateText(state),
                StateColor = InspectorPalette.StateColor(state),
            };
            Kpis(e, c, v.Kpis);
            Reactions(e, c, v.Kpis);
            Detail(e, c, v.Detail);
            if (withCharts) Charts(e, c, v.Charts, stateCache);

            if (contributors != null)
                foreach (var ctb in contributors)
                {
                    if (ctb == null || !ctb.Supports(e)) continue;
                    ctb.AddKpis(e, c, v.Kpis);
                    ctb.AddDetail(e, c, v.Detail);
                    if (withCharts) ctb.AddCharts(e, c, v.Charts);
                }
            return v;
        }

        // ── KPIs ─────────────────────────────────────────────────────────────

        private static void Kpis(Element e, ElementInspectorContext c, List<InspectorRow> k)
        {
            double now = c.Now;
            var st = e.StatsCollector;
            string state = e.CurrentState.Name;

            k.Add(InspectorRow.H("Now"));
            k.Add(new InspectorRow("State", InspectorPalette.StateText(state), null, InspectorPalette.StateColor(state)));
            if (e.IsDown)
                foreach (var stop in e.ActiveStops)
                {
                    string what = string.Join(" · ", new[] { stop.Request.Code, stop.Request.Reason }.Where(x => !string.IsNullOrEmpty(x)));
                    k.Add(new InspectorRow("Active stop", InspectorPalette.StateText(stop.State.Name) + (what.Length > 0 ? " · " + what : ""),
                                           "since " + c.Instant(stop.StartTime) + " (" + c.Duration(now - stop.StartTime) + ")", InspectorPalette.Down));
                }
            k.Add(new InspectorRow("Content", ElementInspectorContext.N(e.GetQueueLength()) + " items"));
            if (e is LengthLimitedQueue lane)
            {
                k.Add(new InspectorRow("Occupied length", ElementInspectorContext.N(lane.TotalUsedLength, "N1") + " of " + ElementInspectorContext.N(lane.TotalLength, "N1"),
                                       ElementInspectorContext.Pct(lane.TotalUsedLength, lane.TotalLength)));
                if (lane.LaneCount > 1)
                    for (int i = 0; i < lane.LaneCount; i++)
                        k.Add(new InspectorRow($"   lane {i + 1}", ElementInspectorContext.N(lane.CountOnLane(i)) + " items",
                                               ElementInspectorContext.N(lane.UsedLength(i), "N1") + " / " + ElementInspectorContext.N(lane.LaneLengths[i], "N1")));
            }
            else if (!(e is Sink))
            {
                int free = e.GetFreeCapacity();
                if (free >= 0 && free < int.MaxValue / 2) k.Add(new InspectorRow("Free capacity", ElementInspectorContext.N(free)));
            }

            if (st != null)
            {
                k.Add(InspectorRow.H("Totals"));
                k.Add(new InspectorRow("Input", ElementInspectorContext.N(st.TotalInput), c.Rate(st.TotalInput)));
                k.Add(new InspectorRow("Output", ElementInspectorContext.N(st.TotalOutput), c.Rate(st.TotalOutput)));
                k.Add(new InspectorRow("Content avg (time-weighted)", ElementInspectorContext.N(st.ContentAvgTimeWeighted, "N2"),
                                       "max " + ElementInspectorContext.N(st.ContentMax)));
                if (st.TotalOutput > 0)
                    k.Add(new InspectorRow("Stay time avg", c.Duration(st.AvgStayTime), "min " + c.Duration(st.MinStayTime) + " · max " + c.Duration(st.MaxStayTime)));
            }

            var snap = e.GetStateSnapshot();
            // the results' definition (an Erection Dock was shown as its PROCESSING share)
            double util = SimuLean.Results.ResultMetrics.UtilizationOf(e);
            if (now > 0 && snap.Count > 0)
            {
                k.Add(new InspectorRow("Utilisation", ElementInspectorContext.N(100 * util, "N1") + " %", "processing"));
                if (e.StopCount > 0) k.Add(new InspectorRow("Stops", ElementInspectorContext.N(e.StopCount)));
                k.Add(InspectorRow.H("Time per state"));
                foreach (string s in Ordered(snap.Keys))
                    if (snap.TryGetValue(s, out double x) && x > 0)
                        k.Add(new InspectorRow(InspectorPalette.StateText(s), ElementInspectorContext.Pct(x, now), c.Duration(x), InspectorPalette.StateColor(s)));
            }
        }

        // The reactions that listen to the element or act on it: how many times each fired, when last, with which
        // item. Read from the model's signal table (none = nothing shown).
        private static void Reactions(Element e, ElementInspectorContext c, List<InspectorRow> k)
        {
            var hub = c.Clock?.Context.SignalsIfAny;
            if (hub == null || hub.Reactions.Count == 0) return;
            bool header = false;
            foreach (var r in hub.Reactions)
            {
                if (!r.Involves(e)) continue;
                if (!header) { k.Add(InspectorRow.H("Reactions")); header = true; }
                bool listens = false;
                foreach (var s in r.Sources) if (s == e) { listens = true; break; }
                string fired = r.Enabled ? "fired " + ElementInspectorContext.N(r.Fired) : "off";
                if (r.Skipped > 0) fired += " · " + ElementInspectorContext.N(r.Skipped) + " not met";
                string last = double.IsNaN(r.LastFired) ? (listens ? "listens: " : "acts on it: ") + Signal(r.Signal)
                            : "last " + c.Instant(r.LastFired) + (r.LastItem != null ? " · " + r.LastItem : "")
                              + (r.LastSource != null && r.LastSource != e ? " · at " + r.LastSource.GetName() : "");
                k.Add(new InspectorRow(r.Name, fired, last));
            }
        }

        private static string Signal(SimuLean.Signals.SignalKind kind)
        {
            switch (kind)
            {
                case SimuLean.Signals.SignalKind.ItemEntered: return "an item enters";
                case SimuLean.Signals.SignalKind.ItemExited: return "an item leaves";
                case SimuLean.Signals.SignalKind.StateChanged: return "the state changes";
                case SimuLean.Signals.SignalKind.Stopped: return "a stop starts";
                default: return "a stop ends";
            }
        }

        private static IEnumerable<string> Ordered(IEnumerable<string> names)
        {
            var list = names.ToList();
            foreach (string s in InspectorPalette.StateOrder) if (list.Contains(s)) yield return s;
            foreach (string s in list) if (Array.IndexOf(InspectorPalette.StateOrder, s) < 0) yield return s;
        }

        // ── detail (static parameters) ───────────────────────────────────────

        private static void Detail(Element e, ElementInspectorContext c, List<InspectorRow> d)
        {
            d.Add(InspectorRow.H("Element"));
            d.Add(new InspectorRow("Name", e.GetName()));
            d.Add(new InspectorRow("Engine type", ((object)e).GetType().Name));
            if (c.Owner != null)
            {
                var se = c.Owner.GetComponent<SElement>();
                d.Add(new InspectorRow("Scene object", c.Owner.name, se != null ? se.GetType().Name : null));
            }
            if (e is LengthLimitedQueue lane)
            {
                d.Add(new InspectorRow("Lanes", ElementInspectorContext.N(lane.LaneCount),
                                       string.Join(" · ", lane.LaneLengths.Select(x => ElementInspectorContext.N(x, "N1")))));
                d.Add(new InspectorRow("Gap between items", ElementInspectorContext.N(lane.Gap, "N2")));
                d.Add(new InspectorRow("Item length", string.IsNullOrEmpty(lane.LengthLabel) ? "default" : "label '" + lane.LengthLabel + "'",
                                       "default " + ElementInspectorContext.N(lane.DefaultItemLength, "N2")));
                if (lane.MaxItems < int.MaxValue) d.Add(new InspectorRow("Max items", ElementInspectorContext.N(lane.MaxItems)));
            }
            else if (!(e is Sink) && !(e is StorageSystem))
            {
                int cap = e.GetQueueLength() + e.GetFreeCapacity();
                if (cap >= 0 && cap < int.MaxValue / 2) d.Add(new InspectorRow("Capacity", ElementInspectorContext.N(cap)));
            }
            var input = e.GetInputStrategy();
            if (input != null) d.Add(new InspectorRow("Input strategy", ((object)input).GetType().Name));
            var output = e.GetOutputStrategy();
            if (output != null) d.Add(new InspectorRow("Output strategy", ((object)output).GetType().Name));
            if (c.HasCalendar)
            {
                d.Add(InspectorRow.H("Calendar"));
                d.Add(new InspectorRow("Simulation start", c.Calendar.Format(0, "yyyy-MM-dd HH:mm")));
                d.Add(new InspectorRow("Seconds per time unit", ElementInspectorContext.N(c.Calendar.SecondsPerUnit, "G4")));
            }
        }

        // ── charts ───────────────────────────────────────────────────────────

        private static void Charts(Element e, ElementInspectorContext c, List<ChartData> charts,
                                   Dictionary<Element, ElementLiveHistory.PeriodStates> cache)
        {
            StateCharts(e, c, charts, cache);
            ContentCharts(e, c, charts);
        }

        private static void StateCharts(Element e, ElementInspectorContext c, List<ChartData> charts,
                                        Dictionary<Element, ElementLiveHistory.PeriodStates> cache)
        {
            if (c.Period <= 0 || e.GetStateLog().Count == 0) return;
            double origin = c.HasCalendar ? c.Calendar.StartOfDay(0) : 0;
            ElementLiveHistory.PeriodStates ps = null;
            cache?.TryGetValue(e, out ps);
            ps = ElementLiveHistory.StateSplit(e, c.Now, origin, c.Period, ps);
            if (cache != null) cache[e] = ps;
            var byPeriod = ElementLiveHistory.WithOpen(ps, c.Now);

            var spec = new ChartData { Kind = ChartKind.Stacked, Normalise = true, Title = $"State split per {c.PeriodLabel} (%)", Unit = "%" };
            var bars = new ChartData { Kind = ChartKind.Bar, Title = "Cumulative state split" };
            var snap = e.GetStateSnapshot();
            foreach (string s in Ordered(byPeriod.Keys))
            {
                int color = ChartPalette.StateColor(s);
                spec.Series.Add(new ChartSeries { Name = InspectorPalette.StateText(s), Color = color, Values = byPeriod[s].ToArray() });
                double tot = snap.TryGetValue(s, out double x2) ? x2 : 0;
                bars.Series.Add(new ChartSeries { Name = InspectorPalette.StateText(s), Color = color, Values = new[] { c.Now > 0 ? tot / c.Now : 0 },
                                                  Note = ElementInspectorContext.Pct(tot, c.Now) + "  ·  " + c.Duration(tot) });
            }
            int n = spec.Series.Count == 0 ? 0 : spec.Series[0].Values.Length;
            if (n > 0)
            {
                spec.XStart = c.HasCalendar ? c.Calendar.Format(origin, "dd/MM") : ElementInspectorContext.N(origin);
                spec.XEnd = c.HasCalendar ? c.Calendar.Format(origin + (n - 1) * c.Period, "dd/MM") : ElementInspectorContext.N(origin + n * c.Period);
                spec.XCenter = n + " " + c.PeriodLabel + (n == 1 ? "" : "s");
                charts.Add(spec);
            }
            if (bars.Series.Count > 0) charts.Add(bars);
        }

        private static void ContentCharts(Element e, ElementInspectorContext c, List<ChartData> charts)
        {
            var hs = c.History?.Of(e);
            if (hs == null || hs.Avg.Count == 0) return;
            double bucket = c.History.Bucket;

            ElementLiveHistory.Downsample(hs, 0, hs.Avg.Count, 120, out var avg, out var max, out int per);
            var all = ContentChart("Content over the run: mean and max", avg, max);
            all.XStart = c.Instant(0, "dd/MM HH:mm");
            all.XEnd = c.Instant(hs.Avg.Count * bucket, "dd/MM HH:mm");
            all.XCenter = "column = " + c.Duration(per * bucket);
            charts.Add(all);

            int recent = Math.Min(hs.Avg.Count, 48);
            if (hs.Avg.Count > recent)
            {
                int first = hs.Avg.Count - recent;
                ElementLiveHistory.Downsample(hs, first, recent, recent, out var ra, out var rm, out _);
                var last = ContentChart("Recent content (last " + recent + " buckets)", ra, rm);
                last.XStart = c.Instant(first * bucket, "dd/MM HH:mm");
                last.XEnd = c.Instant(hs.Avg.Count * bucket, "dd/MM HH:mm");
                last.XCenter = "bucket = " + c.Duration(bucket);
                charts.Add(last);
            }
        }

        // content per time column: the mean as an area, the max as a line
        private static ChartData ContentChart(string title, float[] avg, float[] max)
        {
            var d = new ChartData { Kind = ChartKind.Area, Title = title, Unit = "items" };
            d.Series.Add(new ChartSeries { Name = "mean", Color = InspectorPalette.Rgb(InspectorPalette.Content), Values = avg.Select(v => (double)v).ToArray() });
            d.Series.Add(new ChartSeries { Name = "max", Color = InspectorPalette.Rgb(InspectorPalette.Max), Values = max.Select(v => (double)v).ToArray() });
            return d;
        }
    }
}
