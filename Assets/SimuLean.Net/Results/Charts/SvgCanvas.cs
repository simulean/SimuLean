using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SimuLean.Results.Charts
{
    /// <summary>
    /// A chart as SVG: the same layout as the image (<see cref="ChartRenderer.Draw"/>), written as vector
    /// shapes and real text — sharp at any size, editable, for papers. Text is set in Inter (fallbacks: Segoe UI, Helvetica,
    /// Arial) and measured with the same font metrics as the image, so the layout matches where Inter is installed.
    /// </summary>
    public sealed class SvgCanvas : IChartCanvas
    {
        private readonly StringBuilder body = new StringBuilder();
        private readonly int background;
        private const string Family = "Inter, 'Segoe UI', Helvetica, Arial, sans-serif";

        public SvgCanvas(double width, double height, int background)
        {
            Width = width; Height = height;
            this.background = background;
        }

        public double Width { get; }
        public double Height { get; }

        /// <summary>The finished document.</summary>
        public string ToSvg()
        {
            var sb = new StringBuilder();
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(N(Width)).Append("\" height=\"").Append(N(Height))
              .Append("\" viewBox=\"0 0 ").Append(N(Width)).Append(' ').Append(N(Height)).Append("\" font-family=\"").Append(Family).Append("\">\n");
            sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"").Append(Hex(background)).Append("\"/>\n");
            sb.Append(body);
            sb.Append("</svg>\n");
            return sb.ToString();
        }

        public void Save(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, ToSvg(), new UTF8Encoding(false));
        }

        public void Rect(double x, double y, double w, double h, int rgb, double alpha = 1)
        {
            if (w < 0) { x += w; w = -w; }
            if (h < 0) { y += h; h = -h; }
            body.Append("<rect x=\"").Append(N(x)).Append("\" y=\"").Append(N(y)).Append("\" width=\"").Append(N(w)).Append("\" height=\"").Append(N(h))
                .Append("\" fill=\"").Append(Hex(rgb)).Append('"').Append(Opacity("fill-opacity", alpha)).Append("/>\n");
        }

        public void Bar(double x, double y, double w, double h, double radius, int rgb, double alpha = 1)
        {
            if (w < 0) { x += w; w = -w; }
            if (h < 0) { y += h; h = -h; }
            double r = Math.Max(0, Math.Min(radius, Math.Min(w / 2, h)));
            if (r <= 0.01) { Rect(x, y, w, h, rgb, alpha); return; }
            body.Append("<path d=\"M").Append(N(x)).Append(',').Append(N(y + h))
                .Append(" V").Append(N(y + r)).Append(" Q").Append(N(x)).Append(',').Append(N(y)).Append(' ').Append(N(x + r)).Append(',').Append(N(y))
                .Append(" H").Append(N(x + w - r)).Append(" Q").Append(N(x + w)).Append(',').Append(N(y)).Append(' ').Append(N(x + w)).Append(',').Append(N(y + r))
                .Append(" V").Append(N(y + h)).Append(" Z\" fill=\"").Append(Hex(rgb)).Append('"').Append(Opacity("fill-opacity", alpha)).Append("/>\n");
        }

        public void Line(double x0, double y0, double x1, double y1, int rgb, double width, double alpha = 1)
        {
            body.Append("<line x1=\"").Append(N(x0)).Append("\" y1=\"").Append(N(y0)).Append("\" x2=\"").Append(N(x1)).Append("\" y2=\"").Append(N(y1))
                .Append("\" stroke=\"").Append(Hex(rgb)).Append("\" stroke-width=\"").Append(N(width)).Append("\" stroke-linecap=\"round\"")
                .Append(Opacity("stroke-opacity", alpha)).Append("/>\n");
        }

        public void Polyline(IReadOnlyList<(double x, double y)> points, int rgb, double width)
        {
            if (points.Count < 2) return;
            body.Append("<polyline fill=\"none\" stroke=\"").Append(Hex(rgb)).Append("\" stroke-width=\"").Append(N(width))
                .Append("\" stroke-linejoin=\"round\" stroke-linecap=\"round\" points=\"");
            for (int i = 0; i < points.Count; i++) body.Append(i == 0 ? "" : " ").Append(N(points[i].x)).Append(',').Append(N(points[i].y));
            body.Append("\"/>\n");
        }

        public void Circle(double cx, double cy, double r, int rgb, double alpha = 1)
        {
            body.Append("<circle cx=\"").Append(N(cx)).Append("\" cy=\"").Append(N(cy)).Append("\" r=\"").Append(N(r)).Append("\" fill=\"").Append(Hex(rgb))
                .Append('"').Append(Opacity("fill-opacity", alpha)).Append("/>\n");
        }

        public void Text(string text, double x, double y, double size, int rgb, TextAlign align = TextAlign.Start, bool bold = false, bool vertical = false)
        {
            if (string.IsNullOrEmpty(text) || size <= 0) return;
            string anchor = align == TextAlign.Middle ? "middle" : align == TextAlign.End ? "end" : "start";
            body.Append("<text x=\"").Append(N(x)).Append("\" y=\"").Append(N(y)).Append("\" font-size=\"").Append(N(size)).Append("\" fill=\"").Append(Hex(rgb)).Append('"');
            if (bold) body.Append(" font-weight=\"600\"");
            if (anchor != "start") body.Append(" text-anchor=\"").Append(anchor).Append('"');
            if (vertical) body.Append(" transform=\"rotate(-90 ").Append(N(x)).Append(' ').Append(N(y)).Append(")\"");
            body.Append('>').Append(Escape(text)).Append("</text>\n");
        }

        public double Measure(string text, double size, bool bold = false) => (bold ? ChartFonts.SemiBold : ChartFonts.Regular).Measure(text, size);
        public double Ascent(double size) => ChartFonts.Regular.AscentPx(size);

        private static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        private static string Hex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture);
        private static string Opacity(string attr, double a) => a >= 1 ? "" : $" {attr}=\"{N(a)}\"";

        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default: if (c >= 0x20 || c == '\t') sb.Append(c); break;
                }
            return sb.ToString();
        }
    }
}
