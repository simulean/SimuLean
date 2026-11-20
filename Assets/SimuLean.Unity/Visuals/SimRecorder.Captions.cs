using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using SimuLean.Results.Charts;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>The Recorder's captions: placeholders filled from the run, burnt into the frames.</summary>
    public sealed partial class SimRecorder
    {
        // ── captions ─────────────────────────────────────────────────────────

        private static readonly Regex Placeholder = new Regex(@"\{(\w+)(?::([^{}]+))?\}", RegexOptions.Compiled);

        /// <summary>A caption with its placeholders <c>{kind}</c> / <c>{kind:argument}</c> filled by
        /// <paramref name="value"/> (null leaves the placeholder as written).</summary>
        public static string FillTemplate(string template, Func<string, string, string> value)
        {
            if (string.IsNullOrEmpty(template)) return template ?? "";
            return Placeholder.Replace(template, m => value(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Success ? m.Groups[2].Value.Trim() : null) ?? m.Value);
        }

        /// <summary>A caption filled with the run's values now (the clock, the shot, the elements' counts).</summary>
        public string Fill(string template, int shot)
        {
            return FillTemplate(template, (kind, arg) =>
            {
                switch (kind)
                {
                    case "time": return ClockText();
                    case "shot": return shot >= 0 && shot < shots.Count ? shots[shot].title : "";
                    case "content":
                    case "input":
                    case "output":
                        var el = ElementNamed(arg);
                        if (el == null) return "?";
                        double v = kind == "content" ? el.GetStatsContentCurrent() : kind == "input" ? el.GetStatsInputCount() : el.GetStatsOutputCount();
                        return Math.Round(v).ToString(CultureInfo.InvariantCulture);
                    default: return null;
                }
            });
        }

        private string ClockText()
        {
            var sim = UnitySimClock.Instance;
            if (sim == null || sim.clock == null) return "";
            try { return sim.clock.GetDateTime().ToString(string.IsNullOrEmpty(clockFormat) ? "g" : clockFormat, cultureInfo); }
            catch (FormatException) { return sim.clock.GetDateTime().ToString("g", cultureInfo); }
        }

        private Element ElementNamed(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (elements == null)
            {
                elements = new Dictionary<string, SElement>();
                foreach (var e in UnityCompat.FindAll<SElement>()) if (e != null && !elements.ContainsKey(e.name)) elements[e.name] = e;
            }
            return elements.TryGetValue(name, out var s) && s != null ? s.GetElement() : null;
        }

        /// <summary>What the captions show on one frame (already filled).</summary>
        public struct CaptionText
        {
            public string title, subtitle, clock;
            public float titleAlpha;
            public List<string> lines;
        }

        /// <summary>
        /// Burns the captions into a frame (RGBA, rows bottom-up as Unity reads them): the title and subtitle in a band
        /// bottom left, the clock top right, the live values top left — white Inter (the engine's chart font) on dark
        /// translucent panels, sized by the frame height and <paramref name="scale"/>.
        /// </summary>
        public static void BurnCaptions(byte[] rgba, int w, int h, CaptionText text, float scale = 1f)
        {
            var canvas = new FrameCanvas(rgba, w, h);
            float u = h / 720f * Mathf.Max(0.1f, scale);
            float margin = 28f * u, pad = 12f * u;
            const int panel = 0x0B1F2A, ink = 0xFFFFFF;
            var bold = ChartFonts.SemiBold;
            var regular = ChartFonts.Regular;

            if (!string.IsNullOrEmpty(text.title) && text.titleAlpha > 0.01f)
            {
                float ts = 34f * u, ss = 22f * u;
                bool sub = !string.IsNullOrEmpty(text.subtitle);
                float tw = (float)bold.Measure(text.title, ts), sw = sub ? (float)regular.Measure(text.subtitle, ss) : 0f;
                float boxH = pad * 2 + ts * 1.1f + (sub ? ss * 1.35f : 0f);
                float boxW = Mathf.Max(tw, sw) + pad * 2.4f;
                float top = h - margin - boxH;
                canvas.FillRect(margin, top, boxW, boxH, panel, 0.66f * text.titleAlpha);
                canvas.FillRect(margin, top, 5f * u, boxH, 0x3FA7D6, text.titleAlpha);
                float baseline = top + pad + (float)bold.AscentPx(ts);
                canvas.Text(bold, text.title, margin + pad * 1.4f, baseline, ts, ink, text.titleAlpha);
                if (sub) canvas.Text(regular, text.subtitle, margin + pad * 1.4f, baseline + ss * 1.45f, ss, 0xD8E6EE, text.titleAlpha);
            }
            if (!string.IsNullOrEmpty(text.clock))
            {
                float cs = 24f * u;
                float cw = (float)bold.Measure(text.clock, cs);
                float boxW = cw + pad * 2, boxH = cs * 1.2f + pad * 2;
                canvas.FillRect(w - margin - boxW, margin, boxW, boxH, panel, 0.66f);
                canvas.Text(bold, text.clock, w - margin - boxW + pad, margin + pad + (float)bold.AscentPx(cs), cs, ink, 1f);
            }
            if (text.lines != null && text.lines.Count > 0)
            {
                float ls = 21f * u, step = ls * 1.4f;
                float lw = 0f;
                foreach (var l in text.lines) lw = Mathf.Max(lw, (float)regular.Measure(l ?? "", ls));
                float boxH = pad * 2 + step * text.lines.Count - (step - ls * 1.15f);
                canvas.FillRect(margin, margin, lw + pad * 2, boxH, panel, 0.66f);
                float baseline = margin + pad + (float)regular.AscentPx(ls);
                foreach (var l in text.lines)
                {
                    canvas.Text(regular, l ?? "", margin + pad, baseline, ls, ink, 1f);
                    baseline += step;
                }
            }
        }

        // A frame's pixels to draw on, addressed top-down (Unity's rows are bottom-up).
        private sealed class FrameCanvas
        {
            private readonly byte[] px;
            private readonly int w, h;
            public FrameCanvas(byte[] px, int w, int h) { this.px = px; this.w = w; this.h = h; }

            public void Blend(int x, int y, int rgb, float a)
            {
                if (x < 0 || y < 0 || x >= w || y >= h || a <= 0f) return;
                if (a > 1f) a = 1f;
                int i = ((h - 1 - y) * w + x) * 4;
                px[i] = (byte)(px[i] + (((rgb >> 16) & 255) - px[i]) * a);
                px[i + 1] = (byte)(px[i + 1] + (((rgb >> 8) & 255) - px[i + 1]) * a);
                px[i + 2] = (byte)(px[i + 2] + ((rgb & 255) - px[i + 2]) * a);
            }

            public void FillRect(float x, float y, float rw, float rh, int rgb, float a)
            {
                int x0 = Mathf.Max(0, Mathf.RoundToInt(x)), y0 = Mathf.Max(0, Mathf.RoundToInt(y));
                int x1 = Mathf.Min(w, Mathf.RoundToInt(x + rw)), y1 = Mathf.Min(h, Mathf.RoundToInt(y + rh));
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++) Blend(xx, yy, rgb, a);
            }

            public void Text(TrueTypeFont font, string text, float x, float baseline, float size, int rgb, float a)
                => font.Draw(text, x, baseline, size, (px2, py, c) => Blend(px2, py, rgb, c * a));
        }

        // the caption of the frame at video time t (titles fade in / out over 0.4 s where the title changes)
        private CaptionText CaptionsAt(float t, int shot)
        {
            var c = new CaptionText { lines = new List<string>() };
            if (showClock) c.clock = ClockText();
            foreach (var l in liveValues) if (!string.IsNullOrWhiteSpace(l)) c.lines.Add(Fill(l, shot));
            if (shot >= 0 && !string.IsNullOrEmpty(shots[shot].title))
            {
                c.title = Fill(shots[shot].title, shot);
                c.subtitle = Fill(shots[shot].subtitle, shot);
                const float fade = 0.4f;
                float local = t - ShotStart(shot), len = Mathf.Max(0.1f, shots[shot].seconds);
                bool sameBefore = shot > 0 && shots[shot - 1].title == shots[shot].title;
                bool sameAfter = shot + 1 < shots.Count && shots[shot + 1].title == shots[shot].title;
                float a = 1f;
                if (!sameBefore && shot > 0) a = Mathf.Min(a, local / fade);
                if (!sameAfter && shot + 1 < shots.Count) a = Mathf.Min(a, (len - local) / fade);
                c.titleAlpha = Mathf.Clamp01(a);
            }
            return c;
        }
    }
}
