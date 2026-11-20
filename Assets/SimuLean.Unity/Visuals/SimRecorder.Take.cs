using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using SimuLean.Results.Charts;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>The Recorder's take: the clock driven to the start date, the frames captured, the video encoded.</summary>
    public sealed partial class SimRecorder
    {
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
        // tiny speeds) at a speed that covers the gap in about a second, never past it in a frame. False when
        // the take gives up (a date never reached kept the take "approaching" forever).
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
            // the wall clock — record with it off, the user's speed back for the take
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
                // one buffer for the whole take: GetRawTextureData() allocated a copy of every frame (MBs)
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
                    p.Dispose();
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
    }
}
