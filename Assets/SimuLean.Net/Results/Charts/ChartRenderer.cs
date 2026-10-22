using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SimuLean.Results.Charts
{
    /// <summary>Colours of a chart (0xRRGGBB).</summary>
    public sealed class ChartTheme
    {
        public int Background = 0xFFFFFF, Text = 0x1F2328, Muted = 0x656D76, Grid = 0xEBEEF1, Axis = 0xC9CED4, Whisker = 0x3A3F45;

        public static ChartTheme Light => new ChartTheme();
        public static ChartTheme Dark => new ChartTheme { Background = 0x2B2D30, Text = 0xE6E7E9, Muted = 0xA0A5AD, Grid = 0x3A3D42, Axis = 0x5A5F66, Whisker = 0xE6E7E9 };
        /// <summary>The run-time panels (element inspector, dashboards).</summary>
        public static ChartTheme Panel => new ChartTheme { Background = 0x16191F, Text = 0xD4DAE3, Muted = 0x9AA3B0, Grid = 0x2C313A, Axis = 0x4A515C, Whisker = 0xEEF0F4 };
    }

    /// <summary>
    /// Lays out and draws <see cref="ChartData"/> on an <see cref="IChartCanvas"/> (modern style G3a): a semibold
    /// title and a subtitle, the legend as round swatches under them, a light grid without a frame, round ticks, Inter
    /// text, bars with rounded tops and thin interval whiskers, boxes, histogram bins, scatter points, lines, stacked
    /// states. Sizes are in points × <c>scale</c> (pixels per point: 1 on a normal screen, the editor's pixels per point,
    /// the HUD's scale). <see cref="Render"/> gives an image (2× supersampled); the same layout writes SVG. Unity-free.
    /// </summary>
    public static class ChartRenderer
    {
        /// <param name="scale">Pixels per point (0 = from the size: 1 at 640 × 400).</param>
        public static ChartImage Render(ChartData data, int width, int height, ChartTheme theme = null, double scale = 0)
        {
            theme = theme ?? ChartTheme.Light;
            var canvas = new RasterCanvas(Math.Max(80, width), Math.Max(60, height), theme.Background, 2);
            Draw(canvas, data, theme, scale > 0 ? scale : AutoScale(width, height));
            return canvas.ToImage();
        }

        /// <summary>The chart as an SVG document: the same layout, vector shapes and real text; sizes in points.</summary>
        public static string ToSvg(ChartData data, double width = 640, double height = 400, ChartTheme theme = null, double scale = 1)
        {
            theme = theme ?? ChartTheme.Light;
            var canvas = new SvgCanvas(Math.Max(80, width), Math.Max(60, height), theme.Background);
            Draw(canvas, data, theme, scale);
            return canvas.ToSvg();
        }

        /// <summary>Saves the chart as <c>.svg</c> (vector) or <c>.png</c> (by the file's extension); the image at 2 pixels per point.</summary>
        public static void Save(ChartData data, string path, double width = 640, double height = 400, ChartTheme theme = null)
        {
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                System.IO.File.WriteAllText(path, ToSvg(data, width, height, theme), new System.Text.UTF8Encoding(false));
            else Render(data, (int)Math.Round(width * 2), (int)Math.Round(height * 2), theme, 2).SavePng(path);
        }

        /// <summary>Pixels per point for an image of this size with no screen to follow (an export): 1 at 640 × 400.</summary>
        public static double AutoScale(int width, int height) => Math.Max(0.75, Math.Min(4, Math.Min(width / 640.0, height / 400.0)));

        /// <summary>Draws the whole chart on <paramref name="c"/>.</summary>
        public static void Draw(IChartCanvas c, ChartData data, ChartTheme theme, double u)
        {
            var f = new Frame { C = c, T = theme, U = u, Data = data };
            f.Layout();
        }

        // ── the frame: title, legend, axes, plot ──────────────────────────────

        private sealed class Frame
        {
            public IChartCanvas C;
            public ChartTheme T;
            public ChartData Data;
            public double U;
            private double plotX, plotY, plotW, plotH;

            private double TitleSize => 14 * U;
            private double LabelSize => 11 * U;
            private double TickSize => 10.5 * U;
            private double Pad => 14 * U;
            private int Color(int k) => Data.Series[k].Color >= 0 ? Data.Series[k].Color : ChartPalette.SeriesColor(k);

            public void Layout()
            {
                double y = Pad;
                double width = C.Width - 2 * Pad;
                if (!string.IsNullOrEmpty(Data.Title))
                {
                    C.Text(Fit(Data.Title, width, TitleSize, true), Pad, y + C.Ascent(TitleSize), TitleSize, T.Text, bold: true);
                    y += TitleSize * 1.3;
                }
                if (!string.IsNullOrEmpty(Data.Subtitle))
                {
                    C.Text(Fit(Data.Subtitle, width, LabelSize), Pad, y + C.Ascent(LabelSize), LabelSize, T.Muted);
                    y += LabelSize * 1.35;
                }
                if (Data.IsEmpty)
                {
                    Wrap(Data.Message, Pad, y + 10 * U, width, LabelSize, T.Muted);
                    return;
                }
                bool legend = Data.Series.Count > 1 || Data.Kind == ChartKind.States || Data.Kind == ChartKind.Stacked || Data.Kind == ChartKind.Gantt || Data.Kind == ChartKind.JobGantt;
                if (legend) y += 6 * U + Legend(Pad, y + 6 * U, width);
                double top = y + 10 * U;
                double bottom = C.Height - Pad;
                switch (Data.Kind)
                {
                    case ChartKind.Bar: case ChartKind.Box: case ChartKind.States: case ChartKind.Stacked: case ChartKind.Area:
                        Categorical(top, bottom); break;
                    case ChartKind.Gantt: case ChartKind.JobGantt: Gantt(top, bottom); break;
                    default: Numeric(top, bottom); break;
                }
                if (!string.IsNullOrEmpty(Data.XLabel))
                {
                    // under the ticks (a Gantt with few rows does not fill the height)
                    double titleY = Data.Kind == ChartKind.Gantt || Data.Kind == ChartKind.JobGantt ? Math.Min(bottom, plotY + plotH + 6 * U + TickSize * 1.4 + LabelSize * 1.2) : bottom;
                    C.Text(Fit(Data.XLabel, plotW, LabelSize), plotX + plotW / 2, titleY, LabelSize, T.Muted, TextAlign.Middle);
                }
            }

            // bottom room for the X title
            private double XTitleRoom => string.IsNullOrEmpty(Data.XLabel) ? 0 : LabelSize * 1.5;

            // ── bars, boxes, stacks: categories along X ──
            private void Categorical(double top, double bottom)
            {
                double lo, hi;
                bool percent = Data.Kind == ChartKind.States || Data.Kind == ChartKind.Stacked && Data.Normalise;
                if (percent) { lo = 0; hi = 1; }
                else if (Data.Kind == ChartKind.Stacked || Data.Kind == ChartKind.Area) { lo = 0; hi = Math.Max(1e-12, YTop(Data)); }
                else
                {
                    var values = new List<double>();
                    foreach (var s in Data.Series)
                    {
                        if (Data.Kind == ChartKind.Bar) { values.AddRange(s.Values); values.AddRange(s.Low); values.AddRange(s.High); }
                        else foreach (var b in s.Boxes) if (b.N > 0) { values.Add(b.Min); values.Add(b.Max); }
                    }
                    values = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
                    lo = values.Count == 0 ? 0 : Math.Min(0, values.Min());
                    hi = values.Count == 0 ? 1 : Math.Max(0, values.Max());
                    if (Data.Kind == ChartKind.Box && values.Count > 0) { lo = values.Min(); hi = values.Max(); }
                }
                double room = bottom - XTitleRoom - top;
                var ticks = NiceTicks(lo, hi, YTicks(room));
                lo = Math.Min(lo, ticks[0]); hi = Math.Max(hi, ticks[ticks.Count - 1]);
                var labels = ticks.Select(t => percent ? $"{t * 100:0}%" : Format(t, ticks)).ToList();

                double yAxis = labels.Max(l => C.Measure(l, TickSize)) + 8 * U + (string.IsNullOrEmpty(Data.YLabel) ? 0 : LabelSize * 1.4);
                plotX = Pad + yAxis;
                plotW = C.Width - plotX - Pad;
                int n = Math.Max(1, Columns(Data));
                double slot = plotW / n;
                bool sparse = Data.XStart != null || Data.XEnd != null || Data.XCenter != null;
                double longest = sparse || Data.Categories.Count == 0 ? 0 : Data.Categories.Max(cat => C.Measure(cat, TickSize));
                bool turned = !sparse && longest > slot - 6 * U;
                double labelRoom = turned ? Math.Min(longest, room / 3) + 8 * U : TickSize * 1.6;
                plotY = top;
                plotH = Math.Max(10 * U, bottom - XTitleRoom - labelRoom - top);
                Axes(ticks, labels, lo, hi);
                double Y(double v) => plotY + plotH - (v - lo) / (hi - lo) * plotH;

                double labelBase = plotY + plotH + 6 * U + C.Ascent(TickSize);
                if (sparse)
                {
                    if (Data.XStart != null) C.Text(Data.XStart, plotX, labelBase, TickSize, T.Muted);
                    if (Data.XCenter != null) C.Text(Data.XCenter, plotX + plotW / 2, labelBase, TickSize, T.Muted, TextAlign.Middle);
                    if (Data.XEnd != null) C.Text(Data.XEnd, plotX + plotW, labelBase, TickSize, T.Muted, TextAlign.End);
                }
                if (Data.Kind == ChartKind.Stacked || Data.Kind == ChartKind.Area) { PlotColumns(C, Data, plotX, plotY, plotW, plotH, hi, U); return; }

                int ns = Math.Max(1, Data.Series.Count);
                for (int ci = 0; ci < Data.Categories.Count; ci++)
                {
                    double x0 = plotX + ci * slot;
                    if (!sparse)
                    {
                        string label = Data.Categories[ci];
                        if (turned)
                        {
                            if (slot >= TickSize * 0.9)
                                C.Text(Fit(label, labelRoom - 8 * U, TickSize), x0 + slot / 2 - C.Ascent(TickSize) * 0.4,
                                       plotY + plotH + 6 * U, TickSize, T.Text, TextAlign.End, vertical: true);
                        }
                        else C.Text(label, x0 + slot / 2, labelBase, TickSize, T.Text, TextAlign.Middle);
                    }

                    if (Data.Kind == ChartKind.States)
                    {
                        double w = Math.Min(slot * 0.62, 64 * U), bx = x0 + (slot - w) / 2, acc = 0;
                        int lastK = -1;
                        for (int k = 0; k < Data.Series.Count; k++) if (Value(Data.Series[k], ci) > 0) lastK = k;
                        for (int k = 0; k < Data.Series.Count; k++)
                        {
                            double v = Value(Data.Series[k], ci);
                            if (!(v > 0)) continue;
                            double yTop = Y(acc + v), h = Y(acc) - yTop;
                            if (k == lastK) C.Bar(bx, yTop, w, h, 3 * U, Color(k));
                            else C.Rect(bx, yTop, w, h, Color(k));
                            acc += v;
                        }
                        continue;
                    }

                    double groupW = Math.Min(slot * 0.72, ns * 56 * U), barW = groupW / ns;
                    for (int k = 0; k < Data.Series.Count; k++)
                    {
                        var s = Data.Series[k];
                        double inner = Math.Max(1, barW * 0.82), cx = x0 + (slot - groupW) / 2 + k * barW + barW / 2;
                        if (Data.Kind == ChartKind.Bar)
                        {
                            double v = Value(s, ci);
                            if (double.IsNaN(v)) continue;
                            double top0 = Y(Math.Max(0, Math.Min(v, hi)));
                            if (v >= 0) C.Bar(cx - inner / 2, top0, inner, Math.Max(U, Y(0) - top0), Math.Min(4 * U, inner / 4), Color(k));
                            else C.Rect(cx - inner / 2, Y(0), inner, Y(v) - Y(0), Color(k));
                            double l = s.Low.Length > ci ? s.Low[ci] : double.NaN, h = s.High.Length > ci ? s.High[ci] : double.NaN;
                            bool interval = !double.IsNaN(l) && !double.IsNaN(h);
                            if (interval)
                            {
                                double cap = Math.Min(inner * 0.36, 9 * U);
                                C.Line(cx, Y(l), cx, Y(h), T.Whisker, 1.25 * U);
                                C.Line(cx - cap / 2, Y(l), cx + cap / 2, Y(l), T.Whisker, 1.25 * U);
                                C.Line(cx - cap / 2, Y(h), cx + cap / 2, Y(h), T.Whisker, 1.25 * U);
                            }
                            if (Data.ValueLabels)
                            {
                                string text = FormatValue(v) + (interval ? " ± " + FormatValue((h - l) / 2) : "");
                                if (C.Measure(text, TickSize) > barW) text = FormatValue(v);
                                if (C.Measure(text, TickSize) <= barW + 4 * U)
                                {
                                    double ty = Y(interval ? Math.Max(h, Math.Max(0, v)) : Math.Max(0, v)) - 5 * U;
                                    C.Text(text, cx, Math.Max(plotY + C.Ascent(TickSize) * 0.2, ty), TickSize, T.Text, TextAlign.Middle);
                                }
                            }
                        }
                        else
                        {
                            var b = s.Boxes.Length > ci ? s.Boxes[ci] : default;
                            if (b.N == 0) continue;
                            double cap = inner * 0.45, bh = Math.Max(U, Y(b.Q1) - Y(b.Q3));
                            C.Line(cx, Y(b.Min), cx, Y(b.Q1), T.Whisker, 1.25 * U);
                            C.Line(cx, Y(b.Q3), cx, Y(b.Max), T.Whisker, 1.25 * U);
                            C.Line(cx - cap / 2, Y(b.Min), cx + cap / 2, Y(b.Min), T.Whisker, 1.25 * U);
                            C.Line(cx - cap / 2, Y(b.Max), cx + cap / 2, Y(b.Max), T.Whisker, 1.25 * U);
                            double ex = cx - inner / 2, ey = Y(b.Q3);
                            C.Rect(ex, ey, inner, bh, Color(k), 0.3);
                            C.Line(ex, ey, ex + inner, ey, Color(k), 1.5 * U);
                            C.Line(ex, ey + bh, ex + inner, ey + bh, Color(k), 1.5 * U);
                            C.Line(ex, ey, ex, ey + bh, Color(k), 1.5 * U);
                            C.Line(ex + inner, ey, ex + inner, ey + bh, Color(k), 1.5 * U);
                            C.Line(ex, Y(b.Median), ex + inner, Y(b.Median), Color(k), 2.5 * U);
                        }
                    }
                }
            }

            private static double Value(ChartSeries s, int i) => s.Values.Length > i ? s.Values[i] : double.NaN;

            // ── histogram, scatter, line: numbers on both axes ──
            private void Numeric(double top, double bottom)
            {
                double x0, x1, y0, y1;
                if (Data.Kind == ChartKind.Histogram)
                {
                    var starts = Data.Series.SelectMany(s => s.Points.Select(p => p.x)).ToList();
                    x0 = starts.Min(); x1 = starts.Max() + Data.BinWidth;
                    y0 = 0; y1 = Math.Max(1, Data.Series.SelectMany(s => s.Points.Select(p => p.y)).Max());
                }
                else
                {
                    var pts = Data.Series.SelectMany(s => s.Points).Where(p => !double.IsNaN(p.x) && !double.IsNaN(p.y)).ToList();
                    if (pts.Count == 0) { pts.Add((0, 0)); pts.Add((1, 1)); }
                    x0 = pts.Min(p => p.x); x1 = pts.Max(p => p.x); y0 = pts.Min(p => p.y); y1 = pts.Max(p => p.y);
                    if (Data.Kind == ChartKind.Line && y0 > 0) y0 = 0;
                    if (Data.Kind == ChartKind.Scatter)
                    {
                        double mx = (x1 - x0) * 0.06, my = (y1 - y0) * 0.08;
                        x0 -= mx; x1 += mx; y0 -= my; y1 += my;
                    }
                }
                if (!(x1 > x0)) { x0 -= 0.5; x1 += 0.5; }
                if (!(y1 > y0)) { y0 -= 0.5; y1 += 0.5; }
                double room = bottom - XTitleRoom - top;
                var yt = NiceTicks(y0, y1, YTicks(room));
                if (Data.Kind != ChartKind.Scatter) { y0 = Math.Min(y0, yt[0]); y1 = Math.Max(y1, yt[yt.Count - 1]); }
                else yt = yt.Where(t => t >= y0 && t <= y1).ToList();
                var ylabels = yt.Select(t => Format(t, yt)).ToList();
                double yAxis = (ylabels.Count == 0 ? 0 : ylabels.Max(l => C.Measure(l, TickSize))) + 8 * U + (string.IsNullOrEmpty(Data.YLabel) ? 0 : LabelSize * 1.4);
                plotX = Pad + yAxis;
                plotW = C.Width - plotX - Pad - C.Measure("0000", TickSize) / 2;
                plotY = top;
                plotH = Math.Max(10 * U, bottom - XTitleRoom - TickSize * 1.6 - top);
                Axes(yt, ylabels, y0, y1);

                double span = x1 - x0;
                var xt = NiceTicks(x0, x1, Math.Max(2, (int)(plotW / (C.Measure("000000", TickSize) + 16 * U))))
                    .Where(t => t >= x0 - 1e-9 * Math.Abs(span) && t <= x1 + 1e-9 * Math.Abs(span)).ToList();
                double X(double v) => plotX + (v - x0) / (x1 - x0) * plotW;
                double Y(double v) => plotY + plotH - (v - y0) / (y1 - y0) * plotH;
                foreach (double t in xt)
                    C.Text(Format(t, xt), X(t), plotY + plotH + 6 * U + C.Ascent(TickSize), TickSize, T.Muted, TextAlign.Middle);

                int ns = Data.Series.Count;
                for (int k = 0; k < ns; k++)
                {
                    var s = Data.Series[k];
                    switch (Data.Kind)
                    {
                        case ChartKind.Histogram:
                            foreach (var p in s.Points)
                            {
                                double bw = (X(p.x + Data.BinWidth) - X(p.x)) / ns;
                                double bx = X(p.x) + k * bw;
                                if (p.y > 0) C.Bar(bx + U, Y(p.y), Math.Max(1, bw - 2 * U), Y(0) - Y(p.y), Math.Min(3 * U, bw / 4), Color(k), ns > 1 ? 0.85 : 1);
                            }
                            break;
                        case ChartKind.Scatter:
                            foreach (var p in s.Points) C.Circle(X(p.x), Y(p.y), 3.5 * U, Color(k), 0.85);
                            break;
                        case ChartKind.Line:
                            var line = s.Points.Select(p => (X(p.x), Y(p.y))).ToList();
                            if (line.Count == 1) C.Circle(line[0].Item1, line[0].Item2, 3 * U, Color(k));
                            else C.Polyline(line, Color(k), 2 * U);
                            break;
                    }
                }
            }

            // ── Gantt: a row per object, its state intervals over time ──
            private void Gantt(double top, double bottom)
            {
                int rows = Data.Categories.Count;
                double minRow = 7 * U;
                double room = bottom - XTitleRoom - TickSize * 1.6 - top;
                int shown = Math.Max(1, Math.Min(rows, (int)(room / minRow)));
                double labelW = Math.Min(C.Width * 0.3, Data.Categories.Take(shown).Max(cat => C.Measure(cat, TickSize))) + 10 * U;
                plotX = Pad + labelW;
                plotW = C.Width - plotX - Pad - C.Measure("0000", TickSize) / 2;
                double rowH = Math.Min(26 * U, room / shown);
                plotY = top;
                plotH = rowH * shown;
                double t0 = Data.TimeFrom, t1 = Data.TimeTo > Data.TimeFrom ? Data.TimeTo : Data.TimeFrom + 1;
                double X(double v) => plotX + (v - t0) / (t1 - t0) * plotW;

                var xt = NiceTicks(t0, t1, Math.Max(2, (int)(plotW / (C.Measure("000000", TickSize) + 16 * U))))
                    .Where(v => v >= t0 - 1e-9 * (t1 - t0) && v <= t1 + 1e-9 * (t1 - t0)).ToList();
                foreach (double v in xt)
                {
                    C.Rect(X(v) - 0.5 * U, plotY, U, plotH, T.Grid);
                    C.Text(Format(v, xt), X(v), plotY + plotH + 6 * U + C.Ascent(TickSize), TickSize, T.Muted, TextAlign.Middle);
                }
                for (int r = 0; r < shown; r++)
                {
                    double y = plotY + r * rowH;
                    if (r % 2 == 1) C.Rect(plotX, y, plotW, rowH, T.Grid, 0.35);
                    if (rowH >= TickSize * 0.9)
                        C.Text(Fit(Data.Categories[r], labelW - 10 * U, TickSize), plotX - 8 * U, y + rowH / 2 + C.Ascent(TickSize) * 0.36, TickSize, T.Text, TextAlign.End);
                }
                C.Rect(plotX, plotY + plotH - 0.5 * U, plotW, U, T.Axis);

                // the bars, row by row; more intervals than pixels: each pixel column takes its dominant state
                int columns = Math.Max(1, (int)Math.Round(plotW * 2));
                double colW = (t1 - t0) / columns;
                double barH = rowH * 0.72, pad = (rowH - barH) / 2;
                foreach (var group in Data.Segments.Where(g => g.Row < shown).GroupBy(g => g.Row))
                {
                    double y = plotY + group.Key * rowH + pad;
                    var segs = group.OrderBy(g => g.Start).ToList();
                    if (segs.Count <= columns)
                    {
                        foreach (var g in segs)
                            C.Rect(X(Math.Max(t0, g.Start)), y, Math.Max(0.5, X(Math.Min(t1, g.End)) - X(Math.Max(t0, g.Start))), barH, Color(g.Series));
                        continue;
                    }
                    var cover = new double[columns, Data.Series.Count];
                    foreach (var g in segs)
                    {
                        int a = Math.Max(0, (int)((g.Start - t0) / colW)), b = Math.Min(columns - 1, (int)((g.End - t0) / colW));
                        for (int col = a; col <= b; col++)
                        {
                            double lo = Math.Max(g.Start, t0 + col * colW), hi = Math.Min(g.End, t0 + (col + 1) * colW);
                            if (hi > lo) cover[col, g.Series] += hi - lo;
                        }
                    }
                    int runStart = 0, runState = -1;
                    for (int col = 0; col <= columns; col++)
                    {
                        int best = -1;
                        if (col < columns)
                        {
                            double most = 0;
                            for (int k = 0; k < Data.Series.Count; k++) if (cover[col, k] > most) { most = cover[col, k]; best = k; }
                        }
                        if (best == runState && col < columns) continue;
                        if (runState >= 0) C.Rect(plotX + runStart * plotW / columns, y, (col - runStart) * plotW / columns, barH, Color(runState));
                        runStart = col; runState = best;
                    }
                }
                if (shown < rows)
                    C.Text($"+{rows - shown} more rows (filter the objects)", plotX + plotW, plotY + plotH + 6 * U + C.Ascent(TickSize) + TickSize * 1.4, TickSize, T.Muted, TextAlign.End);
            }

            /// <summary>How many Y ticks fit a plot this tall: one per ~3 lines of tick text, 2 to 6.</summary>
            private int YTicks(double height) => Math.Max(2, Math.Min(6, (int)(height / (TickSize * 3.2))));

            /// <summary>The grid with its labels, the Y title (turned) and the zero line; no frame.</summary>
            private void Axes(List<double> ticks, List<string> labels, double lo, double hi)
            {
                for (int i = 0; i < ticks.Count; i++)
                {
                    double y = plotY + plotH - (ticks[i] - lo) / (hi - lo) * plotH;
                    C.Rect(plotX, y - 0.5 * U, plotW, U, T.Grid);
                    C.Text(labels[i], plotX - 8 * U, y + C.Ascent(TickSize) * 0.36, TickSize, T.Muted, TextAlign.End);
                }
                double zero = lo < 0 && hi > 0 ? plotY + plotH - (0 - lo) / (hi - lo) * plotH : plotY + plotH;
                C.Rect(plotX, zero - 0.5 * U, plotW, U, T.Axis);
                if (!string.IsNullOrEmpty(Data.YLabel))
                    C.Text(Fit(Data.YLabel, plotH, LabelSize), Pad + C.Ascent(LabelSize), plotY + plotH / 2, LabelSize, T.Muted, TextAlign.Middle, vertical: true);
            }

            // ── legend: round swatches, wrapped (3 rows at most); returns its height ──
            private double Legend(double x0, double y, double width)
            {
                double line = LabelSize * 1.55, x = x0, h = line;
                int rows = 1;
                for (int k = 0; k < Data.Series.Count; k++)
                {
                    string name = Fit(Data.Series[k].Name, width * 0.5, LabelSize);
                    double w = 14 * U + C.Measure(name, LabelSize) + 14 * U;
                    if (x + w > x0 + width && x > x0)
                    {
                        if (rows == 3) { C.Text("…", x, y + C.Ascent(LabelSize), LabelSize, T.Muted); break; }
                        rows++; x = x0; y += line; h += line;
                    }
                    C.Circle(x + 5 * U, y + C.Ascent(LabelSize) * 0.62, 4.5 * U, Color(k));
                    C.Text(name, x + 14 * U, y + C.Ascent(LabelSize), LabelSize, T.Text);
                    x += w;
                }
                return h;
            }

            private void Wrap(string text, double x, double y, double width, double size, int rgb)
            {
                string current = "";
                double baseline = y + C.Ascent(size);
                foreach (string w in (text ?? "").Split(' '))
                {
                    string next = current.Length == 0 ? w : current + " " + w;
                    if (current.Length > 0 && C.Measure(next, size) > width) { C.Text(current, x, baseline, size, rgb); baseline += size * 1.4; current = w; }
                    else current = next;
                }
                if (current.Length > 0) C.Text(current, x, baseline, size, rgb);
            }

            /// <summary><paramref name="text"/> shortened with "…" to fit <paramref name="maxWidth"/>.</summary>
            private string Fit(string text, double maxWidth, double size, bool bold = false)
            {
                if (string.IsNullOrEmpty(text) || C.Measure(text, size, bold) <= maxWidth) return text ?? "";
                int lo = 0, hi = text.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (C.Measure(text.Substring(0, mid) + "…", size, bold) <= maxWidth) lo = mid; else hi = mid - 1;
                }
                return lo == 0 ? "" : text.Substring(0, lo).TrimEnd() + "…";
            }
        }

        // ── columns: stacked / area (live data) ───────────────────────────────

        /// <summary>The top of the value axis of a stacked or area chart: the largest column total / value.</summary>
        private static double YTop(ChartData d)
        {
            int n = Columns(d);
            double max = 0;
            for (int c = 0; c < n; c++)
            {
                if (d.Kind == ChartKind.Stacked) max = Math.Max(max, d.Series.Sum(x => c < x.Values.Length && x.Values[c] > 0 ? x.Values[c] : 0));
                else foreach (var x in d.Series) if (c < x.Values.Length && x.Values[c] > max) max = x.Values[c];
            }
            return max;
        }

        private static int Columns(ChartData d)
            => d.Categories.Count > 0 ? d.Categories.Count : d.Series.Count == 0 ? 0 : d.Series.Max(x => x.Values.Length);

        /// <summary>Stacked columns (to 100 % when <see cref="ChartData.Normalise"/>) or an area with lines, in the plot box,
        /// the value axis from 0 to <paramref name="top"/> (1 = 100 % when normalised).</summary>
        private static void PlotColumns(IChartCanvas c, ChartData d, double px, double py, double pw, double ph, double top, double u)
        {
            int n = Columns(d);
            if (n == 0 || !(top > 0)) return;
            double Y(double v) => py + ph - v / top * ph;
            int Color(int k) => d.Series[k].Color >= 0 ? d.Series[k].Color : ChartPalette.SeriesColor(k);
            if (d.Kind == ChartKind.Stacked)
            {
                double gap = n <= 60 ? Math.Min(2 * u, pw / n * 0.15) : 0;
                for (int col = 0; col < n; col++)
                {
                    double x0 = px + col * pw / n, w = Math.Max(0.5, pw / n - gap);
                    double total = d.Series.Sum(x => col < x.Values.Length && x.Values[col] > 0 ? x.Values[col] : 0);
                    double k = d.Normalise ? (total > 0 ? 1 / total : 0) : 1, acc = 0;
                    for (int i = 0; i < d.Series.Count; i++)
                    {
                        double v = col < d.Series[i].Values.Length ? Math.Max(0, d.Series[i].Values[col]) * k : 0;
                        if (v <= 0) continue;
                        c.Rect(x0, Y(acc + v), w, Y(acc) - Y(acc + v), Color(i));
                        acc += v;
                    }
                }
                return;
            }
            var area = d.Series[0];
            for (int col = 0; col < n && col < area.Values.Length; col++)
            {
                double x0 = px + col * pw / n, w = pw / n;
                double v = Math.Max(0, Math.Min(top, area.Values[col]));
                if (v > 0) c.Rect(x0, Y(v), w, Y(0) - Y(v), Color(0), 0.85);
            }
            for (int i = 1; i < d.Series.Count; i++)
            {
                var vals = d.Series[i].Values;
                var pts = new List<(double, double)>();
                for (int col = 0; col < n && col < vals.Length; col++) pts.Add((px + (col + 0.5) * pw / n, Y(Math.Min(top, vals[col]))));
                if (pts.Count == 1) c.Rect(px, pts[0].Item2 - u, pw, 2 * u, Color(i));
                else c.Polyline(pts, Color(i), 1.75 * u);
            }
        }

        /// <summary>
        /// Only the plot of a stacked / area / states chart, filling the image, with a light grid at the quarters — for views
        /// that write their own labels in their own font (the element inspector). <paramref name="yMax"/> is the value at the
        /// top (<see cref="NiceMax"/>; 100 for a chart to 100 %).
        /// </summary>
        public static ChartImage RenderPlot(ChartData data, int width, int height, ChartTheme theme, out double yMax, double scale = 1)
        {
            theme = theme ?? ChartTheme.Panel;
            if (data.Kind != ChartKind.Stacked && data.Kind != ChartKind.Area && data.Kind != ChartKind.States)
                throw new ArgumentException($"RenderPlot draws stacked, area and state charts, not {data.Kind}.");
            var canvas = new RasterCanvas(Math.Max(1, width), Math.Max(1, height), theme.Background, 2);
            for (int k = 1; k < 4; k++) canvas.Rect(0, Math.Floor(height * k / 4.0), width, 1, theme.Grid);
            bool percent = data.Kind == ChartKind.States || data.Kind == ChartKind.Stacked && data.Normalise;
            var plot = new ChartData { Kind = data.Kind == ChartKind.States ? ChartKind.Stacked : data.Kind, Normalise = percent, Categories = data.Categories, Series = data.Series };
            yMax = percent ? 100 : NiceMax(YTop(plot));
            if (yMax > 0) PlotColumns(canvas, plot, 0, 0, width, height, percent ? 1 : yMax, scale);
            return canvas.ToImage();
        }

        // ── numbers ───────────────────────────────────────────────────────────

        /// <summary>A round axis maximum ≥ v with room to spare (1, 1.5, 2, 2.5, 3, 4, 5, 6, 8 × 10ⁿ).</summary>
        public static double NiceMax(double v)
        {
            if (!(v > 0)) return 0;
            double e = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (double m in new double[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 }) if (m * e >= v * 1.02) return m * e;
            return 10 * e;
        }

        /// <summary>Round tick values (1, 2, 5 × 10ⁿ steps) covering [lo, hi], about <paramref name="count"/> of them.</summary>
        public static List<double> NiceTicks(double lo, double hi, int count)
        {
            if (double.IsNaN(lo) || double.IsNaN(hi)) { lo = 0; hi = 1; }
            if (hi < lo) { var t = lo; lo = hi; hi = t; }
            if (hi == lo) { hi = lo + (lo == 0 ? 1 : Math.Abs(lo) * 0.1); }
            double step = NiceStep((hi - lo) / Math.Max(1, count));
            double start = Math.Floor(lo / step) * step, end = Math.Ceiling(hi / step) * step;
            var list = new List<double>();
            for (int i = 0; start + i * step <= end + step * 1e-9 && i < 1000; i++)
            {
                double v = start + i * step;
                list.Add(Math.Abs(v) < step * 1e-9 ? 0 : v);
            }
            return list;
        }

        private static double NiceStep(double raw)
        {
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / mag;
            return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * mag;
        }

        /// <summary>A tick label with as many decimals as the step needs.</summary>
        public static string Format(double v, IList<double> ticks)
        {
            double step = ticks.Count > 1 ? Math.Abs(ticks[1] - ticks[0]) : Math.Abs(v);
            double big = ticks.Count == 0 ? Math.Abs(v) : ticks.Max(t => Math.Abs(t));
            if (big >= 1e6 || (big > 0 && big < 1e-3)) return v.ToString("0.##E+0", CultureInfo.InvariantCulture);
            int decimals = 0;   // as many as the step needs (0.25 → 2)
            while (decimals < 6 && step > 0 && Math.Abs(step * Math.Pow(10, decimals) - Math.Round(step * Math.Pow(10, decimals))) > 1e-6 * step * Math.Pow(10, decimals)) decimals++;
            return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        private static string FormatValue(double v)
        {
            double a = Math.Abs(v);
            if (a >= 1e6 || (a > 0 && a < 1e-3)) return v.ToString("0.##E+0", CultureInfo.InvariantCulture);
            return v.ToString(a >= 100 ? "0" : a >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture);
        }
    }
}
