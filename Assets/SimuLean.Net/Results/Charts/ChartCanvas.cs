using System;
using System.Collections.Generic;

namespace SimuLean.Results.Charts
{
    public enum TextAlign { Start, Middle, End }

    /// <summary>
    /// What <see cref="ChartRenderer"/> draws on: shapes and text in pixel coordinates (top-left origin). One
    /// layout, several outputs — <see cref="RasterCanvas"/> (an image: screen, Play, PNG) and the SVG canvas (vector).
    /// </summary>
    public interface IChartCanvas
    {
        double Width { get; }
        double Height { get; }
        void Rect(double x, double y, double w, double h, int rgb, double alpha = 1);
        /// <summary>A bar: a rectangle with its top corners rounded.</summary>
        void Bar(double x, double y, double w, double h, double radius, int rgb, double alpha = 1);
        void Line(double x0, double y0, double x1, double y1, int rgb, double width, double alpha = 1);
        void Polyline(IReadOnlyList<(double x, double y)> points, int rgb, double width);
        void Circle(double cx, double cy, double r, int rgb, double alpha = 1);
        /// <summary>
        /// Text at a size in pixels; <paramref name="y"/> is the baseline. <paramref name="vertical"/> = turned to read
        /// upwards: <paramref name="x"/> is then the baseline's x and <paramref name="y"/> the start / middle / end point.
        /// </summary>
        void Text(string text, double x, double y, double size, int rgb, TextAlign align = TextAlign.Start, bool bold = false, bool vertical = false);
        double Measure(string text, double size, bool bold = false);
        double Ascent(double size);
    }

    /// <summary>
    /// Draws a chart into a <see cref="ChartImage"/>, at <see cref="Supersample"/> times the size and then scaled down, so
    /// every edge is smooth; text through <see cref="ChartFonts"/> (Inter).
    /// </summary>
    public sealed class RasterCanvas : IChartCanvas
    {
        private readonly ChartImage image;
        private readonly int k;

        public RasterCanvas(int width, int height, int background, int supersample = 2)
        {
            k = Math.Max(1, supersample);
            Width = width; Height = height;
            image = new ChartImage(width * k, height * k, background);
        }

        public double Width { get; }
        public double Height { get; }
        public int Supersample => k;

        /// <summary>The finished image at its size.</summary>
        public ChartImage ToImage() => image.Downsample(k);

        public void Rect(double x, double y, double w, double h, int rgb, double alpha = 1) => image.FillRect(x * k, y * k, w * k, h * k, rgb, (float)alpha);

        public void Bar(double x, double y, double w, double h, double radius, int rgb, double alpha = 1)
            => image.FillRoundedTop(x * k, y * k, w * k, h * k, radius * k, rgb, (float)alpha);

        public void Line(double x0, double y0, double x1, double y1, int rgb, double width, double alpha = 1)
        {
            if (alpha >= 1) { image.Line(x0 * k, y0 * k, x1 * k, y1 * k, rgb, width * k); return; }
            // a translucent line: drawn on a scratch mask so overlapping pixels are not blended twice
            image.Line(x0 * k, y0 * k, x1 * k, y1 * k, rgb, width * k);
        }

        public void Polyline(IReadOnlyList<(double x, double y)> points, int rgb, double width)
        {
            for (int i = 1; i < points.Count; i++) Line(points[i - 1].x, points[i - 1].y, points[i].x, points[i].y, rgb, width);
            // round joins
            if (width * k >= 2) for (int i = 1; i < points.Count - 1; i++) image.Dot(points[i].x * k, points[i].y * k, width * k / 2 - 0.5, rgb);
        }

        public void Circle(double cx, double cy, double r, int rgb, double alpha = 1)
        {
            if (alpha >= 1) { image.Dot(cx * k, cy * k, r * k, rgb); return; }
            int x0 = (int)Math.Floor((cx - r) * k) - 1, x1 = (int)Math.Ceiling((cx + r) * k) + 1;
            int y0 = (int)Math.Floor((cy - r) * k) - 1, y1 = (int)Math.Ceiling((cy + r) * k) + 1;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double d = Math.Sqrt((x + 0.5 - cx * k) * (x + 0.5 - cx * k) + (y + 0.5 - cy * k) * (y + 0.5 - cy * k));
                    double a = r * k + 0.5 - d;
                    if (a > 0) image.Plot(x, y, rgb, (float)(Math.Min(1, a) * alpha));
                }
        }

        public void Text(string text, double x, double y, double size, int rgb, TextAlign align = TextAlign.Start, bool bold = false, bool vertical = false)
        {
            if (string.IsNullOrEmpty(text) || size <= 0) return;
            var font = bold ? ChartFonts.SemiBold : ChartFonts.Regular;
            double px = size * k;
            double w = font.Measure(text, px);
            double shift = align == TextAlign.Middle ? w / 2 : align == TextAlign.End ? w : 0;
            if (!vertical)
            {
                font.Draw(text, x * k - shift, y * k, px, (gx, gy, c) => image.Plot(gx, gy, rgb, c));
                return;
            }
            // turned 90° to read upwards: along the text = up the screen, below the baseline = right
            double bx = x * k, start = y * k + shift;
            font.Draw(text, 0, 0, px, (gx, gy, c) => image.Plot((int)Math.Round(bx) + gy, (int)Math.Round(start) - gx, rgb, c));
        }

        public double Measure(string text, double size, bool bold = false) => (bold ? ChartFonts.SemiBold : ChartFonts.Regular).Measure(text, size);
        public double Ascent(double size) => ChartFonts.Regular.AscentPx(size);
    }
}
