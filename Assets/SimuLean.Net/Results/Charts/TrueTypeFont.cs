using System;
using System.Collections.Generic;
using System.IO;

namespace SimuLean.Results.Charts
{
    /// <summary>
    /// A TrueType font read and rasterised by the engine, so charts get real typography without Unity: the
    /// <c>cmap</c> (formats 4 and 12), <c>head</c>, <c>hhea</c>, <c>hmtx</c>, <c>maxp</c>, <c>loca</c> and <c>glyf</c> tables
    /// (simple and composite glyphs, quadratic outlines). Glyphs are drawn by exact area coverage (an accumulation
    /// rasteriser: anti-aliased, non-zero filling of non-overlapping outlines) and cached per size and quarter-pixel
    /// position. No hinting, no kerning (Inter's spacing is good without). Thread-safe.
    /// </summary>
    public sealed class TrueTypeFont
    {
        private readonly byte[] data;
        private readonly Dictionary<string, int> tables = new Dictionary<string, int>();
        private readonly int unitsPerEm, locaFormat, glyphCount, hMetrics, hmtx, loca, glyf, cmap;
        private readonly int cmapFormat;
        private readonly Dictionary<int, int> glyphOf = new Dictionary<int, int>();
        private readonly Dictionary<(int glyph, int size, int sub), GlyphBitmap> cache = new Dictionary<(int, int, int), GlyphBitmap>();
        private readonly object sync = new object();

        /// <summary>Ascender, descender (negative) and line gap, in font units.</summary>
        public int Ascender { get; }
        public int Descender { get; }
        public int LineGap { get; }
        public int UnitsPerEm => unitsPerEm;

        public TrueTypeFont(byte[] bytes)
        {
            data = bytes ?? throw new ArgumentNullException(nameof(bytes));
            if (data.Length < 12) throw new InvalidDataException("Not a TrueType font (too short).");
            uint version = U32(0);
            if (version != 0x00010000 && version != 0x74727565) throw new InvalidDataException("Not a TrueType font (CFF / unknown outlines).");
            int n = U16(4);
            for (int i = 0; i < n; i++)
            {
                int rec = 12 + 16 * i;
                string tag = new string(new[] { (char)data[rec], (char)data[rec + 1], (char)data[rec + 2], (char)data[rec + 3] });
                tables[tag] = (int)U32(rec + 8);
            }
            foreach (string required in new[] { "head", "hhea", "hmtx", "maxp", "loca", "glyf", "cmap" })
                if (!tables.ContainsKey(required)) throw new InvalidDataException($"TrueType font without a '{required}' table.");
            int head = tables["head"], hhea = tables["hhea"];
            unitsPerEm = U16(head + 18);
            locaFormat = S16(head + 50);
            Ascender = S16(hhea + 4);
            Descender = S16(hhea + 6);
            LineGap = S16(hhea + 8);
            hMetrics = U16(hhea + 34);
            glyphCount = U16(tables["maxp"] + 4);
            hmtx = tables["hmtx"]; loca = tables["loca"]; glyf = tables["glyf"];
            (cmap, cmapFormat) = FindCmap(tables["cmap"]);
        }

        public static TrueTypeFont Load(string path) => new TrueTypeFont(File.ReadAllBytes(path));

        // ── metrics ──────────────────────────────────────────────────────────

        /// <summary>The glyph of a character (0 = the missing glyph).</summary>
        public int GlyphIndex(int codepoint)
        {
            lock (sync)
            {
                if (glyphOf.TryGetValue(codepoint, out int g)) return g;
                g = LookUp(codepoint);
                glyphOf[codepoint] = g;
                return g;
            }
        }

        /// <summary>Advance of a glyph, in font units.</summary>
        public int Advance(int glyph)
        {
            int i = Math.Min(glyph, hMetrics - 1);
            return U16(hmtx + 4 * i);
        }

        /// <summary>Width of <paramref name="text"/> in pixels at <paramref name="size"/> pixels per em.</summary>
        public double Measure(string text, double size)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double w = 0;
            foreach (int cp in Codepoints(text)) w += Advance(GlyphIndex(cp));
            return w * size / unitsPerEm;
        }

        public double AscentPx(double size) => Ascender * size / unitsPerEm;
        public double DescentPx(double size) => -Descender * size / unitsPerEm;

        // ── drawing ──────────────────────────────────────────────────────────

        /// <summary>A rasterised glyph: coverage 0–1, its top-left offset from the pen (x) and the baseline (y, down).</summary>
        public sealed class GlyphBitmap
        {
            public int Width, Height, Left, Top;
            public float[] Coverage;
        }

        /// <summary>
        /// Calls <paramref name="plot"/>(x, y, coverage) for every covered pixel of <paramref name="text"/> drawn with its
        /// baseline at (<paramref name="x"/>, <paramref name="baseline"/>), at <paramref name="size"/> pixels per em.
        /// </summary>
        public void Draw(string text, double x, double baseline, double size, Action<int, int, float> plot)
        {
            if (string.IsNullOrEmpty(text)) return;
            int sizeKey = (int)Math.Round(size * 4);
            double px = size / unitsPerEm;
            double pen = x;
            int by = (int)Math.Round(baseline);
            foreach (int cp in Codepoints(text))
            {
                int g = GlyphIndex(cp);
                int ix = (int)Math.Floor(pen);
                int sub = (int)((pen - ix) * 4) & 3;
                var bmp = Bitmap(g, sizeKey, sub);
                if (bmp != null)
                    for (int yy = 0; yy < bmp.Height; yy++)
                        for (int xx = 0; xx < bmp.Width; xx++)
                        {
                            float c = bmp.Coverage[yy * bmp.Width + xx];
                            if (c > 0.002f) plot(ix + bmp.Left + xx, by + bmp.Top + yy, c);
                        }
                pen += Advance(g) * px;
            }
        }

        private GlyphBitmap Bitmap(int glyph, int sizeKey, int sub)
        {
            var key = (glyph, sizeKey, sub);
            lock (sync)
            {
                if (cache.TryGetValue(key, out var hit)) return hit;
            }
            var bmp = Rasterise(glyph, sizeKey / 4.0, sub / 4.0);
            lock (sync)
            {
                if (cache.Count > 20000) cache.Clear();
                cache[key] = bmp;
            }
            return bmp;
        }

        private GlyphBitmap Rasterise(int glyph, double size, double subX)
        {
            var contours = new List<List<(double x, double y, bool on)>>();
            Outline(glyph, contours, 1, 0, 0, 1, 0, 0, 0);
            if (contours.Count == 0) return null;
            double s = size / unitsPerEm;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var c in contours)
                foreach (var p in c)
                {
                    double X = p.x * s + subX, Y = -p.y * s;
                    if (X < minX) minX = X; if (X > maxX) maxX = X;
                    if (Y < minY) minY = Y; if (Y > maxY) maxY = Y;
                }
            int left = (int)Math.Floor(minX) - 1, top = (int)Math.Floor(minY) - 1;
            int w = (int)Math.Ceiling(maxX) - left + 2, h = (int)Math.Ceiling(maxY) - top + 2;
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
            var acc = new float[w * h + 3];
            void LineTo(double x0, double y0, double x1, double y1) => Accumulate(acc, w, h, x0 - left, y0 - top, x1 - left, y1 - top);
            foreach (var c in contours) Path(c, s, subX, LineTo);
            var cov = new float[w * h];
            float sum = 0;
            for (int i = 0; i < w * h; i++)
            {
                sum += acc[i];
                float a = Math.Abs(sum);
                cov[i] = a > 1 ? 1 : a;
            }
            return new GlyphBitmap { Width = w, Height = h, Left = left, Top = top, Coverage = cov };
        }

        // a contour of on / off-curve points as lines and flattened quadratic curves: the implied on-curve points between two
        // control points are inserted, then the closed contour is walked from an on-curve point
        private static void Path(List<(double x, double y, bool on)> pts, double s, double subX, Action<double, double, double, double> line)
        {
            int n = pts.Count;
            if (n < 2) return;
            var e = new List<(double x, double y, bool on)>(n * 2);
            for (int i = 0; i < n; i++)
            {
                var p = pts[i];
                var q = pts[(i + 1) % n];
                e.Add((p.x * s + subX, -p.y * s, p.on));
                if (!p.on && !q.on) e.Add(((p.x + q.x) / 2 * s + subX, -(p.y + q.y) / 2 * s, true));
            }
            int k = e.FindIndex(p => p.on);
            if (k < 0) return;
            int m = e.Count;
            var cur = (e[k].x, e[k].y);
            for (int j = 1; j <= m; j++)
            {
                var p = e[(k + j) % m];
                if (p.on) { line(cur.Item1, cur.Item2, p.x, p.y); cur = (p.x, p.y); continue; }
                var q = e[(k + j + 1) % m];   // on-curve (implied points inserted)
                Quad(cur, (p.x, p.y), (q.x, q.y), line);
                cur = (q.x, q.y);
                j++;
            }
        }

        private static void Quad((double x, double y) p0, (double x, double y) p1, (double x, double y) p2, Action<double, double, double, double> line)
        {
            double dx = p0.x - 2 * p1.x + p2.x, dy = p0.y - 2 * p1.y + p2.y;
            int n = 1 + (int)Math.Sqrt(Math.Sqrt(dx * dx + dy * dy) * 3);
            double px = p0.x, py = p0.y;
            for (int k = 1; k <= n; k++)
            {
                double t = (double)k / n, u = 1 - t;
                double x = u * u * p0.x + 2 * u * t * p1.x + t * t * p2.x, y = u * u * p0.y + 2 * u * t * p1.y + t * t * p2.y;
                line(px, py, x, y);
                px = x; py = y;
            }
        }

        /// <summary>Signed-area accumulation of one edge (as font-rs): each row then sums to the coverage of its pixels.</summary>
        private static void Accumulate(float[] acc, int w, int h, double x0, double y0, double x1, double y1)
        {
            if (y0 == y1) return;
            double dir = 1;
            if (y0 > y1) { dir = -1; var tx = x0; x0 = x1; x1 = tx; var ty = y0; y0 = y1; y1 = ty; }
            double dxdy = (x1 - x0) / (y1 - y0);
            double x = x0;
            if (y0 < 0) { x -= y0 * dxdy; y0 = 0; }
            if (y1 > h) y1 = h;
            for (int y = (int)y0; y < Math.Ceiling(y1); y++)
            {
                int row = y * w;
                double dy = Math.Min(y + 1, y1) - Math.Max(y, y0);
                double xnext = x + dxdy * dy;
                double d = dy * dir;
                double xa = Math.Min(x, xnext), xb = Math.Max(x, xnext);
                double xaf = Math.Floor(xa);
                int xai = (int)xaf;
                double xbc = Math.Ceiling(xb);
                int xbi = (int)xbc;
                if (xai < 0 || xbi > w) { x = xnext; continue; }   // outside the (padded) box: never with a glyph's own bounds
                if (xbi <= xai + 1)
                {
                    double xmf = 0.5 * (x + xnext) - xaf;
                    acc[row + xai] += (float)(d - d * xmf);
                    acc[row + xai + 1] += (float)(d * xmf);
                }
                else
                {
                    double s = 1 / (xb - xa);
                    double x0f = xa - xaf;
                    double a0 = 0.5 * s * (1 - x0f) * (1 - x0f);
                    double x1f = xb - xbc + 1;
                    double am = 0.5 * s * x1f * x1f;
                    acc[row + xai] += (float)(d * a0);
                    if (xbi == xai + 2) acc[row + xai + 1] += (float)(d * (1 - a0 - am));
                    else
                    {
                        double a1 = s * (1.5 - x0f);
                        acc[row + xai + 1] += (float)(d * (a1 - a0));
                        for (int xi = xai + 2; xi < xbi - 1; xi++) acc[row + xi] += (float)(d * s);
                        double a2 = a1 + (xbi - xai - 3) * s;
                        acc[row + xbi - 1] += (float)(d * (1 - a2 - am));
                    }
                    acc[row + xbi] += (float)(d * am);
                }
                x = xnext;
            }
        }

        // ── glyf ─────────────────────────────────────────────────────────────

        private void Outline(int glyph, List<List<(double x, double y, bool on)>> contours, double a, double b, double c, double d, double dx, double dy, int depth)
        {
            if (glyph < 0 || glyph >= glyphCount || depth > 8) return;
            int start = locaFormat == 0 ? U16(loca + 2 * glyph) * 2 : (int)U32(loca + 4 * glyph);
            int end = locaFormat == 0 ? U16(loca + 2 * glyph + 2) * 2 : (int)U32(loca + 4 * glyph + 4);
            if (end <= start) return;   // no outline (space)
            int g = glyf + start;
            int numberOfContours = S16(g);
            if (numberOfContours >= 0) Simple(g, numberOfContours, contours, a, b, c, d, dx, dy);
            else Composite(g, contours, a, b, c, d, dx, dy, depth);
        }

        private void Simple(int g, int count, List<List<(double, double, bool)>> contours, double a, double b, double c, double d, double dx, double dy)
        {
            var ends = new int[count];
            for (int i = 0; i < count; i++) ends[i] = U16(g + 10 + 2 * i);
            int points = count == 0 ? 0 : ends[count - 1] + 1;
            int p = g + 10 + 2 * count;
            p += 2 + U16(p);   // instructions
            var flags = new byte[points];
            for (int i = 0; i < points;)
            {
                byte f = data[p++];
                flags[i++] = f;
                if ((f & 8) != 0) { int rep = data[p++]; while (rep-- > 0 && i < points) flags[i++] = f; }
            }
            var xs = new int[points];
            int v = 0;
            for (int i = 0; i < points; i++)
            {
                byte f = flags[i];
                if ((f & 2) != 0) { int dv = data[p++]; v += (f & 16) != 0 ? dv : -dv; }
                else if ((f & 16) == 0) { v += S16(p); p += 2; }
                xs[i] = v;
            }
            var ys = new int[points];
            v = 0;
            for (int i = 0; i < points; i++)
            {
                byte f = flags[i];
                if ((f & 4) != 0) { int dv = data[p++]; v += (f & 32) != 0 ? dv : -dv; }
                else if ((f & 32) == 0) { v += S16(p); p += 2; }
                ys[i] = v;
            }
            int first = 0;
            for (int k = 0; k < count; k++)
            {
                var contour = new List<(double, double, bool)>();
                for (int i = first; i <= ends[k]; i++)
                    contour.Add((a * xs[i] + c * ys[i] + dx, b * xs[i] + d * ys[i] + dy, (flags[i] & 1) != 0));
                if (contour.Count > 1) contours.Add(contour);
                first = ends[k] + 1;
            }
        }

        private void Composite(int g, List<List<(double, double, bool)>> contours, double a, double b, double c, double d, double dx, double dy, int depth)
        {
            int p = g + 10;
            while (true)
            {
                int flags = U16(p), glyph = U16(p + 2);
                p += 4;
                double ox, oy;
                if ((flags & 1) != 0) { ox = S16(p); oy = S16(p + 2); p += 4; }
                else { ox = (sbyte)data[p]; oy = (sbyte)data[p + 1]; p += 2; }
                if ((flags & 2) == 0) { ox = 0; oy = 0; }   // point matching: not supported (rare)
                double ca = 1, cb = 0, cc = 0, cd = 1;
                if ((flags & 8) != 0) { ca = cd = F2Dot14(p); p += 2; }
                else if ((flags & 0x40) != 0) { ca = F2Dot14(p); cd = F2Dot14(p + 2); p += 4; }
                else if ((flags & 0x80) != 0) { ca = F2Dot14(p); cb = F2Dot14(p + 2); cc = F2Dot14(p + 4); cd = F2Dot14(p + 6); p += 8; }
                // the component's transform, then the parent's
                double na = a * ca + c * cb, nb = b * ca + d * cb, nc = a * cc + c * cd, nd = b * cc + d * cd;
                double ndx = a * ox + c * oy + dx, ndy = b * ox + d * oy + dy;
                Outline(glyph, contours, na, nb, nc, nd, ndx, ndy, depth + 1);
                if ((flags & 0x20) == 0) break;
            }
        }

        // ── cmap ─────────────────────────────────────────────────────────────

        private (int offset, int format) FindCmap(int table)
        {
            int n = U16(table + 2);
            int best = -1, bestFormat = 0, bestRank = -1;
            for (int i = 0; i < n; i++)
            {
                int rec = table + 4 + 8 * i;
                int platform = U16(rec), encoding = U16(rec + 2);
                int offset = table + (int)U32(rec + 4);
                int format = U16(offset);
                int rank = platform == 3 && encoding == 10 && format == 12 ? 4
                         : platform == 0 && format == 12 ? 3
                         : platform == 3 && encoding == 1 && format == 4 ? 2
                         : platform == 0 && format == 4 ? 1 : -1;
                if (rank > bestRank) { bestRank = rank; best = offset; bestFormat = format; }
            }
            if (best < 0) throw new InvalidDataException("TrueType font without a Unicode cmap (format 4 or 12).");
            return (best, bestFormat);
        }

        private int LookUp(int cp)
        {
            if (cmapFormat == 12)
            {
                uint groups = U32(cmap + 12);
                for (int i = 0; i < groups; i++)
                {
                    int rec = cmap + 16 + 12 * i;
                    uint s0 = U32(rec), e0 = U32(rec + 4);
                    if (cp >= s0 && cp <= e0) return (int)(U32(rec + 8) + (cp - s0));
                }
                return 0;
            }
            if (cp > 0xFFFF) return 0;
            int segX2 = U16(cmap + 6);
            int ends = cmap + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
            for (int i = 0; i < segX2 / 2; i++)
            {
                int end = U16(ends + 2 * i);
                if (cp > end) continue;
                int start = U16(starts + 2 * i);
                if (cp < start) return 0;
                int delta = S16(deltas + 2 * i), range = U16(ranges + 2 * i);
                if (range == 0) return (cp + delta) & 0xFFFF;
                int gi = U16(ranges + 2 * i + range + 2 * (cp - start));
                return gi == 0 ? 0 : (gi + delta) & 0xFFFF;
            }
            return 0;
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private static IEnumerable<int> Codepoints(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { yield return char.ConvertToUtf32(text[i], text[i + 1]); i++; }
                else yield return text[i];
            }
        }

        private int U16(int at) => (data[at] << 8) | data[at + 1];
        private int S16(int at) => (short)((data[at] << 8) | data[at + 1]);
        private uint U32(int at) => ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];
        private double F2Dot14(int at) => S16(at) / 16384.0;
    }

    /// <summary>
    /// The charts' fonts (Inter, SIL OFL; <c>Assets/SimuLean.Unity/Resources/SimuLean/Fonts</c>): found by a host
    /// <see cref="Provider"/> (Unity: <c>Resources</c>, so a player build has them) or on disk — <c>SIMULEAN_FONTS</c>, or
    /// that folder found walking up from the working / application folder (the editor, the standalone tests).
    /// </summary>
    public static class ChartFonts
    {
        public const string RelativeFolder = "Assets/SimuLean.Unity/Resources/SimuLean/Fonts";

        /// <summary>Set by a host to supply the font bytes by name (<c>Inter-Regular</c>, <c>Inter-SemiBold</c>); null = disk.</summary>
        public static Func<string, byte[]> Provider { get; set; }

        private static TrueTypeFont regular, semiBold;
        private static readonly object sync = new object();

        public static TrueTypeFont Regular { get { lock (sync) return regular ?? (regular = Load("Inter-Regular")); } }
        public static TrueTypeFont SemiBold { get { lock (sync) return semiBold ?? (semiBold = Load("Inter-SemiBold")); } }

        private static TrueTypeFont Load(string name)
        {
            byte[] bytes = null;
            try { bytes = Provider?.Invoke(name); } catch { bytes = null; }
            if (bytes == null)
            {
                string path = Find(name + ".bytes");
                if (path != null) bytes = File.ReadAllBytes(path);
            }
            if (bytes == null) throw new FileNotFoundException($"Chart font '{name}' not found ({RelativeFolder}/{name}.bytes; or set SIMULEAN_FONTS).");
            return new TrueTypeFont(bytes);
        }

        private static string Find(string file)
        {
            string env = Environment.GetEnvironmentVariable("SIMULEAN_FONTS");
            if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, file))) return Path.Combine(env, file);
            foreach (string startDir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var dir = string.IsNullOrEmpty(startDir) ? null : new DirectoryInfo(startDir);
                for (int up = 0; dir != null && up < 10; up++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, RelativeFolder, file);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }
    }
}
