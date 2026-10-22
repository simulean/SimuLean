using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SimuLean.Results.Charts
{
    /// <summary>
    /// An RGBA image drawn by <see cref="ChartRenderer"/> (rows top-down, 4 bytes per pixel): anti-aliased drawing
    /// (rectangles, rounded bar tops, lines, dots; text through <see cref="RasterCanvas"/> and <see cref="TrueTypeFont"/>),
    /// supersampling and PNG encoding — so a chart is the same image in the editor, in Play and in the file, without Unity.
    /// </summary>
    public sealed class ChartImage
    {
        public int Width { get; }
        public int Height { get; }
        /// <summary>RGBA, rows top-down.</summary>
        public byte[] Rgba { get; }

        public ChartImage(int width, int height, int background = 0xFFFFFF)
        {
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "An image needs a size.");
            Width = width; Height = height;
            Rgba = new byte[width * height * 4];
            Clear(background);
        }

        public void Clear(int rgb)
        {
            for (int i = 0; i < Rgba.Length; i += 4) { Rgba[i] = (byte)(rgb >> 16); Rgba[i + 1] = (byte)(rgb >> 8); Rgba[i + 2] = (byte)rgb; Rgba[i + 3] = 255; }
        }

        /// <summary>The colour at (x, y) as 0xRRGGBB.</summary>
        public int Pixel(int x, int y)
        {
            int i = (y * Width + x) * 4;
            return (Rgba[i] << 16) | (Rgba[i + 1] << 8) | Rgba[i + 2];
        }

        /// <summary>Blends <paramref name="rgb"/> over (x, y) with opacity <paramref name="alpha"/> (0–1).</summary>
        public void Plot(int x, int y, int rgb, float alpha = 1)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height || alpha <= 0) return;
            int i = (y * Width + x) * 4;
            if (alpha >= 1) { Rgba[i] = (byte)(rgb >> 16); Rgba[i + 1] = (byte)(rgb >> 8); Rgba[i + 2] = (byte)rgb; return; }
            Rgba[i] = Mix(Rgba[i], (rgb >> 16) & 255, alpha);
            Rgba[i + 1] = Mix(Rgba[i + 1], (rgb >> 8) & 255, alpha);
            Rgba[i + 2] = Mix(Rgba[i + 2], rgb & 255, alpha);
        }

        private static byte Mix(byte under, int over, float a) => (byte)(under + (over - under) * a + 0.5f);

        public void FillRect(double x, double y, double w, double h, int rgb, float alpha = 1)
        {
            if (w < 0) { x += w; w = -w; }
            if (h < 0) { y += h; h = -h; }
            int x0 = (int)Math.Round(x), y0 = (int)Math.Round(y), x1 = (int)Math.Round(x + w), y1 = (int)Math.Round(y + h);
            if (x1 == x0 && w > 0) x1 = x0 + 1;
            if (y1 == y0 && h > 0) y1 = y0 + 1;
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(Width, x1); y1 = Math.Min(Height, y1);
            for (int yy = y0; yy < y1; yy++)
                for (int xx = x0; xx < x1; xx++) Plot(xx, yy, rgb, alpha);
        }

        public void StrokeRect(double x, double y, double w, double h, int rgb, int thickness = 1)
        {
            FillRect(x, y, w, thickness, rgb);
            FillRect(x, y + h - thickness, w, thickness, rgb);
            FillRect(x, y, thickness, h, rgb);
            FillRect(x + w - thickness, y, thickness, h, rgb);
        }

        /// <summary>An anti-aliased line of the given thickness (distance to the segment, one pixel of soft edge).</summary>
        public void Line(double x0, double y0, double x1, double y1, int rgb, double thickness = 1)
        {
            double r = Math.Max(0.5, thickness / 2);
            int minX = (int)Math.Floor(Math.Min(x0, x1) - r - 1), maxX = (int)Math.Ceiling(Math.Max(x0, x1) + r + 1);
            int minY = (int)Math.Floor(Math.Min(y0, y1) - r - 1), maxY = (int)Math.Ceiling(Math.Max(y0, y1) + r + 1);
            minX = Math.Max(0, minX); minY = Math.Max(0, minY); maxX = Math.Min(Width - 1, maxX); maxY = Math.Min(Height - 1, maxY);
            double dx = x1 - x0, dy = y1 - y0, len2 = dx * dx + dy * dy;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double t = len2 > 0 ? Math.Max(0, Math.Min(1, ((px - x0) * dx + (py - y0) * dy) / len2)) : 0;
                    double ex = x0 + t * dx - px, ey = y0 + t * dy - py;
                    double d = Math.Sqrt(ex * ex + ey * ey);
                    double a = r + 0.5 - d;
                    if (a > 0) Plot(x, y, rgb, (float)Math.Min(1, a));
                }
        }

        public void Dot(double cx, double cy, double radius, int rgb)
        {
            int x0 = (int)Math.Floor(cx - radius - 1), x1 = (int)Math.Ceiling(cx + radius + 1);
            int y0 = (int)Math.Floor(cy - radius - 1), y1 = (int)Math.Ceiling(cy + radius + 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                    double a = radius + 0.5 - d;
                    if (a > 0) Plot(x, y, rgb, (float)Math.Min(1, a));
                }
        }

        /// <summary>A rectangle whose top corners are rounded by <paramref name="radius"/> (bars).</summary>
        public void FillRoundedTop(double x, double y, double w, double h, double radius, int rgb, float alpha = 1)
        {
            if (w < 0) { x += w; w = -w; }
            if (h < 0) { y += h; h = -h; }
            double r = Math.Max(0, Math.Min(radius, Math.Min(w / 2, h)));
            if (r < 0.5) { FillRect(x, y, w, h, rgb, alpha); return; }
            int x0 = Math.Max(0, (int)Math.Floor(x)), x1 = Math.Min(Width, (int)Math.Ceiling(x + w));
            int y0 = Math.Max(0, (int)Math.Floor(y)), y1 = Math.Min(Height, (int)Math.Ceiling(y + h));
            for (int py = y0; py < y1; py++)
                for (int px = x0; px < x1; px++)
                {
                    double cx = px + 0.5, cy = py + 0.5;
                    // coverage of the pixel by the shape (edges and corner arcs anti-aliased)
                    double cover = Math.Min(1, Math.Min(cx - x + 0.5, x + w - cx + 0.5)) * Math.Min(1, Math.Min(cy - y + 0.5, y + h - cy + 0.5));
                    if (cover <= 0) continue;
                    if (cy < y + r)
                    {
                        double ax = cx < x + r ? x + r : cx > x + w - r ? x + w - r : double.NaN;
                        if (!double.IsNaN(ax))
                        {
                            double d = Math.Sqrt((cx - ax) * (cx - ax) + (cy - (y + r)) * (cy - (y + r)));
                            cover = Math.Min(cover, r + 0.5 - d);
                            if (cover <= 0) continue;
                        }
                    }
                    Plot(px, py, rgb, (float)Math.Min(1, cover) * alpha);
                }
        }

        /// <summary>The image shrunk by a whole factor, each pixel the mean of its block (supersampling → smooth edges).</summary>
        public ChartImage Downsample(int factor)
        {
            if (factor <= 1) return this;
            var small = new ChartImage(Math.Max(1, Width / factor), Math.Max(1, Height / factor));
            int n = factor * factor;
            for (int y = 0; y < small.Height; y++)
                for (int x = 0; x < small.Width; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < factor; dy++)
                    {
                        int i = ((y * factor + dy) * Width + x * factor) * 4;
                        for (int dx = 0; dx < factor; dx++, i += 4) { r += Rgba[i]; g += Rgba[i + 1]; b += Rgba[i + 2]; }
                    }
                    int o = (y * small.Width + x) * 4;
                    small.Rgba[o] = (byte)((r + n / 2) / n); small.Rgba[o + 1] = (byte)((g + n / 2) / n); small.Rgba[o + 2] = (byte)((b + n / 2) / n);
                }
            return small;
        }

        // ── PNG ───────────────────────────────────────────────────────────────

        /// <summary>The image as a PNG file (8-bit RGBA, zlib-compressed rows).</summary>
        public byte[] ToPng()
        {
            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var ihdr = new byte[13];
                BigEndian(ihdr, 0, (uint)Width); BigEndian(ihdr, 4, (uint)Height);
                ihdr[8] = 8; ihdr[9] = 6;   // 8 bits, RGBA
                Chunk(png, "IHDR", ihdr);

                var raw = new byte[Height * (Width * 4 + 1)];
                for (int y = 0; y < Height; y++)
                {
                    raw[y * (Width * 4 + 1)] = 0;   // filter: none
                    Buffer.BlockCopy(Rgba, y * Width * 4, raw, y * (Width * 4 + 1) + 1, Width * 4);
                }
                using (var z = new MemoryStream())
                {
                    z.WriteByte(0x78); z.WriteByte(0x9C);
                    using (var deflate = new DeflateStream(z, CompressionLevel.Optimal, true)) deflate.Write(raw, 0, raw.Length);
                    var adler = new byte[4];
                    BigEndian(adler, 0, Adler32(raw));
                    z.Write(adler, 0, 4);
                    Chunk(png, "IDAT", z.ToArray());
                }
                Chunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        public void SavePng(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, ToPng());
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var head = new byte[8];
            BigEndian(head, 0, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
            s.Write(head, 0, 8);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(head, 4, 4, 0xFFFFFFFFu);
            crc = Crc32(data, 0, data.Length, crc) ^ 0xFFFFFFFFu;
            var tail = new byte[4];
            BigEndian(tail, 0, crc);
            s.Write(tail, 0, 4);
        }

        private static void BigEndian(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

        private static uint[] crcTable;
        private static uint Crc32(byte[] data, int offset, int count, uint crc)
        {
            if (crcTable == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                crcTable = t;
            }
            for (int i = offset; i < offset + count; i++) crc = crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++) { a = (a + data[i]) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }
    }
}
