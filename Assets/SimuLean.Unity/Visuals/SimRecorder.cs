using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using SimuLean.Results.Charts;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Records a demo video of a scene run (shots as data, fly-paths, captions and the editor's
    /// own H.264 encoder the same day): in Play mode it drives the clock to <see cref="startAt"/> (a model date) without
    /// recording, then renders <see cref="seconds"/> of video at <see cref="fps"/> along its <see cref="shots"/> — a fixed
    /// pose, a placed pose, an element framed from a distance and angle, or an orbit around it; cut, or glided along a
    /// smooth curve through the shots — with captions burnt into the frames (each shot's title, the model date, live
    /// values such as <c>{content:Waiting}</c>). Each frame is 1/fps of playback (<c>Time.captureFramerate</c>), whatever
    /// the machine; playback speed comes from the clock (or a <see cref="SimSpeedProfile"/>). The video is an MP4 written
    /// by Unity's encoder in the editor (nothing to install), or JPG frames encoded by ffmpeg. A view: the run is unchanged.
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Visualization, "Recorder",
        Description = "Records a demo video in Play mode: start at a model date, shots framing elements (fly-paths, orbits), captions with model time and live values, MP4.", Order = 51)]
    [AddComponentMenu("SimuLean/Recorder")]
    [DefaultExecutionOrder(10000)]   // renders after the labels and walking people have moved this frame (review2 #30)
    public sealed class SimRecorder : MonoBehaviour
    {
        public enum ShotFraming
        {
            /// <summary>A fixed camera position and rotation (taken from the Scene view in the inspector).</summary>
            Fixed,
            /// <summary>Where a placed GameObject stands and looks.</summary>
            Pose,
            /// <summary>An element seen from a distance and angle: follows the layout when the element moves.</summary>
            Element,
            /// <summary>Around an element: the angle turns by <see cref="Shot.orbitDegrees"/> during the shot.</summary>
            Orbit,
        }

        public enum VideoOutput
        {
            /// <summary>An MP4 by Unity's own encoder in the editor (H.264); a player build falls back to ffmpeg.</summary>
            Mp4,
            /// <summary>JPG frames encoded into an MP4 by ffmpeg (on the PATH or <see cref="ffmpegPath"/>).</summary>
            Ffmpeg,
            /// <summary>Only the JPG frames.</summary>
            Frames,
        }

        /// <summary>The video's quality: its bitrate (bits per pixel per frame) or ffmpeg's constant rate factor.</summary>
        public enum VideoQuality
        {
            /// <summary>Small files (0.12 bit / pixel / frame ≈ 3.3 Mbps at 1280×720, 30 fps; ffmpeg crf 23).</summary>
            Standard,
            /// <summary>Sharp detail in motion (0.3 ≈ 8.3 Mbps at 1280×720, 30 fps; crf 18) — the default.</summary>
            High,
            /// <summary>Near lossless (0.6 ≈ 16.6 Mbps at 1280×720, 30 fps; crf 14).</summary>
            Maximum,
        }

        [Serializable]
        public class Shot
        {
            public string name = "Shot";
            public ShotFraming framing = ShotFraming.Fixed;
            [Tooltip("Fixed: where the camera stands.")]
            public Vector3 position;
            [Tooltip("Fixed: the camera's rotation (Euler angles).")]
            public Vector3 rotation;
            [Tooltip("Pose: where the camera stands and looks (a placed empty GameObject).")]
            public Transform pose;
            [Tooltip("Element / Orbit: the element framed.")]
            public Transform target;
            [Tooltip("Element / Orbit: camera distance from the framed point (m).")]
            [Min(0.5f)] public float distance = 12f;
            [Tooltip("Element / Orbit: angle above the horizon (degrees).")]
            [Range(-10f, 89f)] public float elevation = 35f;
            [Tooltip("Element / Orbit: compass direction the camera looks along (degrees; 0 = looking along +Z).")]
            public float azimuth;
            [Tooltip("Orbit: degrees turned during the shot (negative = clockwise seen from above).")]
            public float orbitDegrees = 90f;
            [Tooltip("Element / Orbit: the framed point's height above the element's origin (m).")]
            public float lookHeight = 1f;
            [Tooltip("Length of the shot in seconds of video.")]
            [Min(0.1f)] public float seconds = 8f;
            [Tooltip("Glide from where the previous shot ended (a smooth curve through the shots) instead of cutting.")]
            public bool glide;
            [Tooltip("Seconds of the glide (0 = the whole shot); the shot holds — or orbits — for the rest.")]
            [Min(0f)] public float glideSeconds;
            [Tooltip("Field of view (0 = the recorder's).")]
            [Range(0f, 90f)] public float fieldOfView;
            [Tooltip("Caption title while the shot lasts (placeholders as the live values).")]
            public string title = "";
            [Tooltip("Caption subtitle.")]
            public string subtitle = "";
        }

        /// <summary>A camera position, rotation and field of view.</summary>
        public struct CameraPose
        {
            public Vector3 position;
            public Quaternion rotation;
            public float fieldOfView;
            public CameraPose(Vector3 position, Quaternion rotation, float fieldOfView)
            {
                this.position = position;
                this.rotation = rotation;
                this.fieldOfView = fieldOfView;
            }
        }

        /// <summary>Writes the frames of a take as a video (Unity's encoder in the editor: <see cref="UnityVideoWriter"/>).</summary>
        public interface IVideoWriter : IDisposable
        {
            void AddFrame(Texture2D frame);
        }

        [Tooltip("Model date of the first frame (e.g. 2026-03-10 08:45). Empty = when Play starts.")]
        public string startAt = "";
        [Tooltip("The shots, in order; their lengths add up to the video. Empty = 10 s from this object's pose.")]
        public List<Shot> shots = new List<Shot>();
        [Min(1)] public int fps = 30;
        [Tooltip("Frame width in pixels (rounded down to even, as H.264 needs).")]
        [Min(16)] public int width = 1280;
        [Min(16)] public int height = 720;
        [Tooltip("Field of view of the recording camera (a shot can set its own).")]
        [Range(10f, 90f)] public float fieldOfView = 45f;

        [Header("Captions")]
        [Tooltip("Burn the shots' titles, the clock and the live values into the frames.")]
        public bool captions = true;
        [Tooltip("Show the model date and time (top right).")]
        public bool showClock = true;
        [Tooltip("Format of {time} and the clock (.NET date format).")]
        public string clockFormat = "ddd d MMM yyyy  HH:mm";
        [Tooltip("Culture of the dates (e.g. es-ES; empty = English).")]
        public string culture = "";
        [Tooltip("Lines shown top left, with live values: {content:Element} (items in it now), {input:Element}, " +
                 "{output:Element}, {time}, {shot}.")]
        public List<string> liveValues = new List<string>();
        [Tooltip("Size of the captions (1 = the default for the frame height).")]
        [Range(0.5f, 2.5f)] public float captionScale = 1f;

        [Header("Output")]
        [Tooltip("Folder of the takes (relative = to the project folder); each take in <scene>_<timestamp>.")]
        public string outputFolder = "Recordings";
        public VideoOutput output = VideoOutput.Mp4;
        [Tooltip("Video quality: the bitrate of the Mp4 output (by the frame size and rate), ffmpeg's crf. Higher = sharper, bigger files.")]
        public VideoQuality quality = VideoQuality.High;
        [Tooltip("Mp4 bitrate in Mbps (0 = by the quality).")]
        [Min(0f)] public float bitrateMbps;
        [Range(10, 100)] public int jpgQuality = 88;
        [Tooltip("ffmpeg executable for the Ffmpeg output (empty = 'ffmpeg' on the PATH).")]
        public string ffmpegPath = "";

        /// <summary>True while a recorder drives the clock to its start (a <see cref="SimSpeedProfile"/> waits).</summary>
        public static bool Approaching { get; private set; }

        /// <summary>Unity's video encoder (the editor sets it; null in a player build: the Mp4 output then uses ffmpeg).</summary>
        public static Func<string, int, int, int, uint, IVideoWriter> UnityVideoWriter { get; set; }

        /// <summary>Called at <c>Start</c> before the take (the editor applies a take's settings from the MCP: start date,
        /// length, size).</summary>
        public static Action<SimRecorder> Configure { get; set; }

        /// <summary>A take ended (its video written, or the frames when encoding is left to ffmpeg).</summary>
        public static event Action<SimRecorder> Finished;

        /// <summary>The recorder of the running take (null when none started).</summary>
        public static SimRecorder Current { get; private set; }

        /// <summary>The camera the views turned to the viewer face (element labels, item labels): the take's camera while a
        /// recorder records, else the main camera (they faced <c>Camera.main</c>, away from the take).</summary>
        public static Camera ViewCamera => Current != null && Current.Recording && Current.cam != null ? Current.cam : Camera.main;

        /// <summary>The take's folder once it started (frames.csv, the video or the frames).</summary>
        public string TakeFolder { get; private set; }
        /// <summary>The video once written (null: not yet, frames only, or ffmpeg still encoding).</summary>
        public string VideoPath { get; private set; }
        public bool Recording { get; private set; }
        public bool Done { get; private set; }
        /// <summary>Why the take failed (null when it did not).</summary>
        public string Error { get; private set; }
        public int FramesWritten => frame;
        public int TotalFrames => Mathf.Max(1, Mathf.RoundToInt(TotalSeconds * fps));

        private Camera cam;
        private RenderTexture rt;
        private Texture2D tex;
        private IVideoWriter writer;
        private int frame;
        private double startTime = double.NaN;
        private byte[] frameBuffer;

        // the clock's driving settings, kept while the recorder drives it (the approach, the take) and given back after
        // (review2 #8: the approach left the clock at its last approach speed, a frozen video)
        private bool clockSaved;
        private double savedTimeScale;
        private float savedFrameBudget;
        private bool savedFastAsPossible;
        private double approachLastTime = double.NaN;
        private float approachStalledFor;
        /// <summary>A take that cannot reach its start date gives up after this many real seconds without progress.</summary>
        public const float ApproachTimeout = 30f;

        private void SaveClock(UnitySimClock sim)
        {
            if (clockSaved) return;
            clockSaved = true;
            savedTimeScale = sim.TimeScale;
            savedFrameBudget = sim.FrameBudgetMs;
            savedFastAsPossible = sim.RunAsFastAsPossible;
        }

        private void RestoreClock()
        {
            var sim = UnitySimClock.Instance;
            if (!clockSaved || sim == null) return;
            clockSaved = false;
            sim.RunAsFastAsPossible = savedFastAsPossible;
            sim.FrameBudgetMs = savedFrameBudget;
            sim.TimeScale = savedTimeScale;
        }
        private System.Text.StringBuilder log;
        private Dictionary<string, SElement> elements;
        private CultureInfo cultureInfo = CultureInfo.InvariantCulture;
        private int frameWidth, frameHeight;

        // ── shots and the fly-path ───────────────────────────────────────────

        /// <summary>Video length: the shots' total (or 10 s without shots).</summary>
        public float TotalSeconds
        {
            get
            {
                float s = 0f;
                foreach (var sh in shots) if (sh != null) s += Mathf.Max(0.1f, sh.seconds);
                return s > 0f ? s : 10f;
            }
        }

        /// <summary>The shot at <paramref name="time"/> seconds of video and how far into it (0–1); -1 without shots.</summary>
        public int ShotAt(float time, out float progress)
        {
            float t = 0f;
            for (int i = 0; i < shots.Count; i++)
            {
                float len = Mathf.Max(0.1f, shots[i].seconds);
                if (time < t + len || i == shots.Count - 1) { progress = Mathf.Clamp01((time - t) / len); return i; }
                t += len;
            }
            progress = 0f;
            return -1;
        }

        /// <summary>When shot <paramref name="index"/> starts, in seconds of video.</summary>
        public float ShotStart(int index)
        {
            float t = 0f;
            for (int i = 0; i < index && i < shots.Count; i++) t += Mathf.Max(0.1f, shots[i].seconds);
            return t;
        }

        /// <summary>Stretches (or shrinks) every shot so that the video lasts <paramref name="total"/> seconds.</summary>
        public void ScaleTo(float total)
        {
            float now = 0f;
            foreach (var sh in shots) if (sh != null) now += Mathf.Max(0.1f, sh.seconds);
            if (now <= 0f || total <= 0f) return;
            float f = total / now;
            foreach (var sh in shots)
                if (sh != null)
                {
                    sh.seconds = Mathf.Max(0.1f, sh.seconds * f);
                    sh.glideSeconds *= f;
                }
        }

        /// <summary>Shot <paramref name="index"/>'s own camera <paramref name="k"/> (0–1) of the way through its motion
        /// (only an orbit moves), without the glide that leads into it.</summary>
        public CameraPose Framing(int index, float k)
        {
            var sh = index >= 0 && index < shots.Count ? shots[index] : null;
            float fov = sh != null && sh.fieldOfView > 0f ? sh.fieldOfView : fieldOfView;
            if (sh == null) return new CameraPose(transform.position, transform.rotation, fov);
            switch (sh.framing)
            {
                case ShotFraming.Fixed:
                    return new CameraPose(sh.position, Quaternion.Euler(sh.rotation), fov);
                case ShotFraming.Pose:
                    var p = sh.pose != null ? sh.pose : transform;
                    return new CameraPose(p.position, p.rotation, fov);
                default:
                    Vector3 look = (sh.target != null ? sh.target.position : transform.position) + Vector3.up * sh.lookHeight;
                    float az = sh.azimuth + (sh.framing == ShotFraming.Orbit ? sh.orbitDegrees * Mathf.Clamp01(k) : 0f);
                    return Around(look, sh.distance, sh.elevation, az, fov);
            }
        }

        /// <summary>A camera <paramref name="distance"/> m from <paramref name="look"/>, <paramref name="elevation"/>° above
        /// the horizon, looking along the compass direction <paramref name="azimuth"/> (0 = +Z) at it.</summary>
        public static CameraPose Around(Vector3 look, float distance, float elevation, float azimuth, float fieldOfView)
        {
            var rot = Quaternion.Euler(elevation, azimuth, 0f);
            return new CameraPose(look - rot * Vector3.forward * Mathf.Max(0.01f, distance), rot, fieldOfView);
        }

        /// <summary>The camera at <paramref name="time"/> seconds of video: each shot's framing, glided along a smooth curve
        /// (Catmull–Rom through the shots; easing in and out where the camera stops) where a shot glides.</summary>
        public CameraPose PoseAt(float time)
        {
            if (shots.Count == 0) return new CameraPose(transform.position, transform.rotation, fieldOfView);
            int i = ShotAt(time, out _);
            float len = Mathf.Max(0.1f, shots[i].seconds);
            float local = Mathf.Clamp(time - ShotStart(i), 0f, len);
            float g = GlideLength(i);
            if (local < g)
            {
                float u = local / g;
                bool flowIn = FlowsThrough(i - 1), flowOut = FlowsThrough(i);
                float s = Ease(u, !flowIn, !flowOut);
                CameraPose a = Framing(i - 1, 1f), b = Framing(i, 0f);
                Vector3 p0 = flowIn ? Framing(i - 2, 1f).position : 2f * a.position - b.position;
                Vector3 p3 = flowOut ? Framing(i + 1, 0f).position : 2f * b.position - a.position;
                return new CameraPose(CatmullRom(p0, a.position, b.position, p3, s),
                                      Quaternion.Slerp(a.rotation, b.rotation, s), Mathf.Lerp(a.fieldOfView, b.fieldOfView, s));
            }
            float rest = len - g;
            return Framing(i, rest > 1e-4f ? (local - g) / rest : 1f);
        }

        // seconds of shot i spent gliding into it (0 = a cut)
        private float GlideLength(int i)
        {
            if (i <= 0 || i >= shots.Count || !shots[i].glide) return 0f;
            float len = Mathf.Max(0.1f, shots[i].seconds);
            return shots[i].glideSeconds > 0f ? Mathf.Min(shots[i].glideSeconds, len) : len;
        }

        // true when the camera is still moving as shot i ends into shot i+1: shot i is all glide, does not orbit, and
        // shot i+1 glides on — the curve goes through without stopping
        private bool FlowsThrough(int i)
        {
            if (i <= 0 || i + 1 >= shots.Count) return false;
            float len = Mathf.Max(0.1f, shots[i].seconds);
            return shots[i].framing != ShotFraming.Orbit && GlideLength(i) >= len - 1e-4f && shots[i + 1].glide;
        }

        /// <summary>The glide's progress: eased where the camera starts / stops, linear where it flows on (the speed is
        /// continuous across the shots).</summary>
        public static float Ease(float u, bool easeIn, bool easeOut)
        {
            u = Mathf.Clamp01(u);
            if (easeIn && easeOut) return u * u * (3f - 2f * u);
            if (easeIn) return u * u * (2f - u);
            if (easeOut) return u + u * u - u * u * u;
            return u;
        }

        /// <summary>Uniform Catmull–Rom from <paramref name="p1"/> (t = 0) to <paramref name="p2"/> (t = 1).</summary>
        public static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
        }

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

        // ── the take ─────────────────────────────────────────────────────────

        /// <summary>ffmpeg's arguments for the frames of a take (H.264, yuv420p: plays everywhere; crf by the quality).</summary>
        public static string FfmpegArguments(int fps, string framesFolder, string output, VideoQuality quality = VideoQuality.High)
            => string.Format(CultureInfo.InvariantCulture,
                "-y -framerate {0} -i \"{1}\" -c:v libx264 -preset slow -pix_fmt yuv420p -crf {3} -movflags +faststart \"{2}\"",
                fps, Path.Combine(framesFolder, "f_%05d.jpg"), output, quality == VideoQuality.Standard ? 23 : quality == VideoQuality.High ? 18 : 14);

        /// <summary>The Mp4 bitrate in bits per second: <paramref name="mbps"/> when given, else by the quality's bits per
        /// pixel per frame (at least 1 Mbps).</summary>
        public static uint BitRate(int width, int height, int fps, VideoQuality quality, float mbps = 0f)
        {
            if (mbps > 0f) return (uint)Math.Min(uint.MaxValue, mbps * 1e6);
            double bpp = quality == VideoQuality.Standard ? 0.12 : quality == VideoQuality.High ? 0.3 : 0.6;
            return (uint)Math.Min(uint.MaxValue, Math.Max(1e6, (double)width * height * Math.Max(1, fps) * bpp));
        }

        /// <summary>The frame size: at least 16 and even (H.264 encodes 2×2 blocks).</summary>
        public static Vector2Int EvenSize(int w, int h) => new Vector2Int(Mathf.Max(16, w) & ~1, Mathf.Max(16, h) & ~1);

        private void Start()
        {
            if (!Application.isPlaying) return;
            Configure?.Invoke(this);
            if (!isActiveAndEnabled) return;
            Current = this;
            if (!string.IsNullOrWhiteSpace(startAt) && UnitySimClock.Instance != null)
            {
                var cal = UnitySimClock.Instance.CreateCalendar();
                if (SimCalendar.TryParseDate(startAt, out DateTime d)) startTime = cal.ToSimTime(d);
                else Debug.LogWarning($"[SimuLean] Recorder: start '{startAt}' is not a date — recording from the start.");
            }
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || Done) return;
            var sim = UnitySimClock.Instance;
            if (sim == null || sim.clock == null) return;
            double now = sim.clock.GetSimulationTime();
            if (!Recording)
            {
                double gap = double.IsNaN(startTime) ? 0 : startTime - now;
                if (gap > 1e-3)
                {
                    if (!Approach(sim, now, gap)) return;
                    return;
                }
                try { Begin(sim); }
                catch (Exception e) { Fail("the take could not start: " + e.Message); return; }
            }
            try { Capture(now); }
            catch (Exception e) { Fail("a frame failed: " + e.Message); }
        }

        // Drives the clock to the start date: the paced loop (double-precision anchors; the legacy float timer stalled at
        // tiny speeds, review2 #10) at a speed that covers the gap in about a second, never past it in a frame. False when
        // the take gives up (review2 #11: a date never reached kept the take "approaching" forever).
        private bool Approach(UnitySimClock sim, double now, double gap)
        {
            if (startTime > sim.MaxTime + 1e-9) { Fail($"the start date (t = {startTime:0.##}) is after the end of the run (t = {sim.MaxTime:0.##})"); return false; }
            if (sim.SimStarted && !sim.SimOn) { Fail("the run ended before the start date"); return false; }
            approachStalledFor = !double.IsNaN(approachLastTime) && now <= approachLastTime ? approachStalledFor + Time.unscaledDeltaTime : 0f;
            approachLastTime = now;
            if (approachStalledFor > ApproachTimeout)
            {
                Fail(sim.SimStarted ? "the run does not advance (paused?) before the start date" : "the run did not start (Auto Start On Play off?)");
                return false;
            }
            SaveClock(sim);
            Approaching = true;
            sim.RunAsFastAsPossible = false;
            if (sim.FrameBudgetMs <= 0) sim.FrameBudgetMs = 40f;
            sim.TimeScale = Math.Max(1e-6, gap);
            return true;
        }

        private void Begin(UnitySimClock sim)
        {
            Approaching = false;
            // the take is driven frame by frame (Time.captureFramerate): the legacy loop follows game time, the paced one
            // the wall clock — record with it off (review2 #9), the user's speed back for the take
            SaveClock(sim);
            sim.RunAsFastAsPossible = false;
            sim.FrameBudgetMs = 0f;
            sim.TimeScale = savedTimeScale;
            Recording = true;
            Current = this;
            var size = EvenSize(width, height);
            frameWidth = size.x;
            frameHeight = size.y;
            try { cultureInfo = string.IsNullOrWhiteSpace(culture) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(culture.Trim()); }
            catch (CultureNotFoundException) { cultureInfo = CultureInfo.InvariantCulture; Debug.LogWarning($"[SimuLean] Recorder: unknown culture '{culture}'."); }
            string root = Path.IsPathRooted(outputFolder) ? outputFolder : Path.Combine(Directory.GetCurrentDirectory(), outputFolder);
            TakeFolder = Path.Combine(root, $"{gameObject.scene.name}_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(TakeFolder);
            var go = new GameObject("RecorderCamera") { hideFlags = HideFlags.DontSave };
            cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.fieldOfView = fieldOfView;
            cam.nearClipPlane = 0.2f;
            cam.farClipPlane = 2000f;
            rt = new RenderTexture(frameWidth, frameHeight, 24) { antiAliasing = 4 };
            cam.targetTexture = rt;
            tex = new Texture2D(frameWidth, frameHeight, TextureFormat.RGBA32, false);
            if (captions) ChartTextures.UseResourceFonts();
            if (output == VideoOutput.Mp4)
            {
                if (UnityVideoWriter != null) writer = UnityVideoWriter(Path.Combine(TakeFolder, "video.mp4"), frameWidth, frameHeight, fps,
                                                                    BitRate(frameWidth, frameHeight, fps, quality, bitrateMbps));
                else Debug.Log("[SimuLean] Recorder: no Unity video encoder outside the editor — frames for ffmpeg instead.");
            }
            log = new System.Text.StringBuilder("frame,sim_time,shot\n");
            frame = 0;
            Time.captureFramerate = fps;
        }

        private void Capture(double now)
        {
            float t = frame / (float)fps;
            int i = ShotAt(t, out _);
            var pose = PoseAt(t);
            cam.transform.SetPositionAndRotation(pose.position, pose.rotation);
            cam.fieldOfView = pose.fieldOfView;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, frameWidth, frameHeight), 0, 0);
            RenderTexture.active = prev;
            if (captions)
            {
                // one buffer for the whole take: GetRawTextureData() allocated a copy of every frame (MBs; review2 #23)
                var pixels = tex.GetRawTextureData<byte>();
                if (frameBuffer == null || frameBuffer.Length != pixels.Length) frameBuffer = new byte[pixels.Length];
                pixels.CopyTo(frameBuffer);
                BurnCaptions(frameBuffer, frameWidth, frameHeight, CaptionsAt(t, i), captionScale);
                pixels.CopyFrom(frameBuffer);
            }
            tex.Apply(false);
            if (writer != null) writer.AddFrame(tex);
            else File.WriteAllBytes(Path.Combine(TakeFolder, $"f_{frame:D5}.jpg"), tex.EncodeToJPG(jpgQuality));
            log.Append(frame).Append(',').Append(now.ToString("R", CultureInfo.InvariantCulture)).Append(',')
               .Append(i >= 0 ? shots[i].name : "").Append('\n');
            if (++frame >= TotalFrames) End();
        }

        private void End()
        {
            Recording = false;
            Done = true;
            Time.captureFramerate = 0;
            RestoreClock();
            if (log != null && TakeFolder != null) File.WriteAllText(Path.Combine(TakeFolder, "frames.csv"), log.ToString());
            if (cam != null) Destroy(cam.gameObject);
            if (rt != null) { rt.Release(); Destroy(rt); }
            if (tex != null) Destroy(tex);
            if (writer != null)
            {
                try
                {
                    writer.Dispose();
                    VideoPath = Path.Combine(TakeFolder, "video.mp4");
                }
                catch (Exception e) { Error = "the encoder failed: " + e.Message; }
                writer = null;
                Debug.Log($"[SimuLean] Recorder: {frame} frames, video {VideoPath ?? "(failed)"}");
            }
            else
            {
                Debug.Log($"[SimuLean] Recorder: {frame} frames in {TakeFolder}");
                if (output != VideoOutput.Frames) Encode();
            }
            Finished?.Invoke(this);
        }

        private void Fail(string why)
        {
            Error = why;
            Debug.LogError("[SimuLean] Recorder: " + why);
            if (Recording) End();
            else
            {
                Done = true;
                Approaching = false;
                RestoreClock();
                Finished?.Invoke(this);
            }
        }

        private void Encode()
        {
            string exe = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
            string video = Path.Combine(TakeFolder, "video.mp4");
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, FfmpegArguments(fps, TakeFolder, video, quality))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                });
                if (p == null) throw new InvalidOperationException("ffmpeg did not start");
                p.EnableRaisingEvents = true;
                p.Exited += (s, e) =>
                {
                    Debug.Log(p.ExitCode == 0 ? $"[SimuLean] Recorder: video {video}" : $"[SimuLean] Recorder: ffmpeg failed ({p.ExitCode}).");
                    p.Dispose();   // review2 #32
                };
                p.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SimuLean] Recorder: ffmpeg not found or failed ({e.Message}). Encode by hand: ffmpeg {FfmpegArguments(fps, TakeFolder, video, quality)}");
            }
        }

        private void OnDisable()
        {
            // stopped mid-take (Play ended): close the video so far
            if (Recording && !Done)
            {
                Error = "stopped before the end";
                End();
            }
            Approaching = false;
            RestoreClock();
        }

        private void OnDrawGizmosSelected()
        {
            if (shots.Count == 0) return;
            // the fly-path, and a marker where each shot starts
            Gizmos.color = new Color(0.25f, 0.65f, 0.85f);
            float total = TotalSeconds;
            Vector3 last = PoseAt(0f).position;
            for (float t = 0.1f; t <= total; t += 0.1f)
            {
                Vector3 p = PoseAt(t).position;
                Gizmos.DrawLine(last, p);
                last = p;
            }
            for (int i = 0; i < shots.Count; i++)
            {
                var pose = PoseAt(ShotStart(i) + GlideLength(i));
                Gizmos.DrawWireSphere(pose.position, 0.3f);
                Gizmos.DrawRay(pose.position, pose.rotation * Vector3.forward * 2f);
            }
        }
    }
}
