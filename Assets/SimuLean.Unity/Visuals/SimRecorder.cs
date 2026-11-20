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
    /// own H.264 encoder): in Play mode it drives the clock to <see cref="startAt"/> (a model date) without
    /// recording, then renders <see cref="seconds"/> of video at <see cref="fps"/> along its <see cref="shots"/> — a fixed
    /// pose, a placed pose, an element framed from a distance and angle, or an orbit around it; cut, or glided along a
    /// smooth curve through the shots — with captions burnt into the frames (each shot's title, the model date, live
    /// values such as <c>{content:Waiting}</c>). Each frame is 1/fps of playback (<c>Time.captureFramerate</c>), whatever
    /// the machine; playback speed comes from the clock (or a <see cref="SimSpeedProfile"/>). The video is an MP4 written
    /// by Unity's encoder in the editor (nothing to install), or JPG frames encoded by ffmpeg. A view: the run is unchanged.
    /// <para>Files: this one (settings, shot data, state, the clock kept and restored), <c>SimRecorder.Path.cs</c> (shots
    /// and the fly-path), <c>SimRecorder.Captions.cs</c> (caption text and burning), <c>SimRecorder.Take.cs</c> (the take:
    /// approach, capture, encoding).</para>
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Visualization, "Recorder",
        Description = "Records a demo video in Play mode: start at a model date, shots framing elements (fly-paths, orbits), captions with model time and live values, MP4.", Order = 51)]
    [AddComponentMenu("SimuLean/Recorder")]
    [DefaultExecutionOrder(10000)]   // renders after the labels and walking people have moved this frame
    public sealed partial class SimRecorder : MonoBehaviour
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
        // (the approach left the clock at its last approach speed, a frozen video)
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
    }
}
