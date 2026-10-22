using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Media;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The editor side of the <see cref="SimRecorder"/>: Unity's own H.264 encoder for its MP4 output (nothing
    /// to install), and takes started from the MCP (<c>simulean_record_start</c> / <c>simulean_record_status</c>) — the
    /// take's settings (start date, length, size) are applied in Play without changing the scene, Play ends when the take
    /// does, and the take's status survives the domain reloads of entering and leaving Play (<see cref="SessionState"/>).
    /// </summary>
    [InitializeOnLoad]
    public static class RecorderSession
    {
        private const string RequestKey = "SimuLean.Record.Request", StatusKey = "SimuLean.Record.Status",
                             DisableKey = "SimuLean.Record.DisableAfter";

        [Serializable]
        private class Request
        {
            public string recorder = "", startAt = "", quality = "";
            public float seconds, bitrateMbps;
            public int fps, width, height;
            public bool exitPlay = true, disableAfter;
        }

        /// <summary>The last take's status (also of a take recorded by hand in Play).</summary>
        [Serializable]
        public class TakeStatus
        {
            public string status = "none", scene = "", recorder = "", folder = "", video = "", error = "", started = "";
            public int frames, totalFrames;
            public float seconds;
        }

        static RecorderSession()
        {
            SimRecorder.UnityVideoWriter = (path, w, h, fps, bitRate) => new UnityMp4Writer(path, w, h, fps, bitRate);
            SimRecorder.Configure = Apply;
            SimRecorder.Finished += OnFinished;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        // Unity's video encoder (editor only): H.264 High profile in an MP4 at the take's bitrate, a key frame every second
        // (the plain bitrate modes gave ~3.6 Mbps at 720p: detailed scenes pixelated in motion)
        private sealed class UnityMp4Writer : SimRecorder.IVideoWriter
        {
            private readonly MediaEncoder encoder;

            public UnityMp4Writer(string path, int width, int height, int fps, uint bitRate)
            {
                var h264 = new H264EncoderAttributes
                {
                    profile = VideoEncodingProfile.H264High,
                    gopSize = (uint)Math.Max(1, fps),
                    numConsecutiveBFrames = 2,
                };
                var video = new VideoTrackEncoderAttributes(h264)
                {
                    frameRate = new MediaRational(fps),
                    width = (uint)width,
                    height = (uint)height,
                    includeAlpha = false,
                    targetBitRate = bitRate,
                };
                encoder = new MediaEncoder(path, video);
            }

            public void AddFrame(Texture2D frame) => encoder.AddFrame(frame);
            public void Dispose() => encoder.Dispose();
        }

        /// <summary>
        /// Starts a take: opens <paramref name="scene"/> (when given), checks its recorder and enters Play; the recorder
        /// drives the clock to the start date and records. The settings given (0 / empty = the recorder's) apply to this
        /// take only. Poll <see cref="Status"/>.
        /// </summary>
        public static TakeStatus Start(string scene, string recorder, string startAt, float seconds, int fps, int width, int height, bool exitPlay,
                                       string quality = "", float bitrateMbps = 0f)
        {
            if (!string.IsNullOrWhiteSpace(quality) && !Enum.TryParse(quality.Trim(), true, out SimRecorder.VideoQuality _))
                throw new ArgumentException($"quality: '{quality}' is not one of {string.Join(", ", Enum.GetNames(typeof(SimRecorder.VideoQuality)))}.");
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("The editor is in Play: stop it first (or poll simulean_record_status).");
            if (EditorApplication.isCompiling) throw new InvalidOperationException("Unity is compiling: try again once it is idle.");
            // Play would not start with compile errors, and the take's settings would be left for the next Play (review2 #31)
            if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("The scripts have compile errors: fix them first.");
            if (!string.IsNullOrWhiteSpace(scene)) OpenScene(scene.Trim());
            var r = FindRecorder(recorder);
            if (r.shots.Count == 0) Debug.LogWarning("[SimuLean] Recorder: no shots — the take is 10 s from the recorder's own pose.");
            var req = new Request
            {
                recorder = r.name, startAt = startAt ?? "", seconds = Math.Max(0f, seconds),
                fps = Math.Max(0, fps), width = Math.Max(0, width), height = Math.Max(0, height), exitPlay = exitPlay,
                disableAfter = !r.enabled, quality = (quality ?? "").Trim(), bitrateMbps = Math.Max(0f, bitrateMbps),
            };
            // a Recorder left off (so that Play by hand does not record) records this take: on in memory only, off again
            // after Play — the scene is not marked modified
            if (!r.isActiveAndEnabled)
            {
                if (!r.gameObject.activeInHierarchy) throw new InvalidOperationException($"The Recorder '{r.name}' is on an inactive GameObject.");
                r.enabled = true;
            }
            SessionState.SetString(RequestKey, JsonUtility.ToJson(req));
            if (req.disableAfter) SessionState.SetString(DisableKey, r.name);
            else SessionState.EraseString(DisableKey);
            float length = req.seconds > 0 ? req.seconds : r.TotalSeconds;
            var status = new TakeStatus
            {
                status = "starting", scene = r.gameObject.scene.name, recorder = r.name, started = DateTime.Now.ToString("s"),
                seconds = length, totalFrames = Mathf.Max(1, Mathf.RoundToInt(length * (req.fps > 0 ? req.fps : r.fps))),
            };
            Save(status);
            EditorApplication.isPlaying = true;
            return status;
        }

        /// <summary>The take now: 'starting', 'approaching' (driving the clock to the start date), 'recording' (frames / in
        /// all), then 'completed' with the video's path, 'failed' or 'stopped'; 'none' before any take. cancel ends Play
        /// (the video so far is kept).</summary>
        public static TakeStatus Status(bool cancel = false)
        {
            var s = Load();
            if (EditorApplication.isPlaying)
            {
                var r = SimRecorder.Current;
                if (r != null && !r.Done)
                {
                    s.status = r.Recording ? "recording" : SimRecorder.Approaching ? "approaching" : "starting";
                    s.frames = r.FramesWritten;
                    s.totalFrames = r.TotalFrames;
                    s.folder = r.TakeFolder ?? "";
                }
                if (cancel)
                {
                    SessionState.EraseString(RequestKey);
                    EditorApplication.isPlaying = false;
                }
            }
            return s;
        }

        /// <summary>The scene's recorder: by name, else the only one (enabled first).</summary>
        public static SimRecorder FindRecorder(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
                return SimuLeanCommands.FindObject(name).GetComponent<SimRecorder>()
                       ?? throw new ArgumentException($"'{name}' has no Recorder.");
            var all = UnityCompat.FindAll<SimRecorder>(true).Where(x => x != null && x.gameObject.scene.IsValid()).ToList();
            var on = all.Where(x => x.isActiveAndEnabled).ToList();
            if (on.Count == 1) return on[0];
            if (all.Count == 1) return all[0];
            if (all.Count == 0) throw new InvalidOperationException("No Recorder in the scene: add shots with simulean_record_shot (it places one).");
            throw new InvalidOperationException($"Several Recorders ({string.Join(", ", all.Select(x => x.name))}): name one.");
        }

        private static void OpenScene(string scene)
        {
            string path = scene.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? scene
                : AssetDatabase.FindAssets("t:Scene " + Path.GetFileNameWithoutExtension(scene)).Select(AssetDatabase.GUIDToAssetPath)
                    .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == Path.GetFileNameWithoutExtension(scene));
            if (string.IsNullOrEmpty(path) || AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                throw new ArgumentException($"No scene '{scene}'.");
            if (EditorSceneManager.GetActiveScene().path == path) return;
            var modified = SimuLeanTestRunner.ModifiedScenes();
            if (modified.Count > 0)
                throw new InvalidOperationException($"Modified scenes are open ({string.Join(", ", modified)}): save them first.");
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        }

        // Play started (after the domain reload): the take's settings, for this take only
        private static void Apply(SimRecorder r)
        {
            string json = SessionState.GetString(RequestKey, "");
            if (string.IsNullOrEmpty(json)) return;             // Play by hand: the recorder as it is
            var req = JsonUtility.FromJson<Request>(json);
            if (!string.IsNullOrEmpty(req.recorder) && r.name != req.recorder)
            {
                r.enabled = false;                               // one take at a time
                return;
            }
            if (!string.IsNullOrWhiteSpace(req.startAt)) r.startAt = req.startAt;
            if (req.seconds > 0) r.ScaleTo(req.seconds);
            if (req.fps > 0) r.fps = req.fps;
            if (req.width > 0) r.width = req.width;
            if (req.height > 0) r.height = req.height;
            if (!string.IsNullOrEmpty(req.quality)) r.quality = (SimRecorder.VideoQuality)Enum.Parse(typeof(SimRecorder.VideoQuality), req.quality, true);
            if (req.bitrateMbps > 0) r.bitrateMbps = req.bitrateMbps;
            var s = Load();
            s.status = "approaching";
            s.totalFrames = r.TotalFrames;
            Save(s);
        }

        private static void OnFinished(SimRecorder r)
        {
            var s = Load();
            s.scene = r.gameObject.scene.name;
            s.recorder = r.name;
            s.folder = r.TakeFolder ?? "";
            s.video = r.VideoPath ?? "";
            s.frames = r.FramesWritten;
            s.totalFrames = r.TotalFrames;
            s.seconds = r.FramesWritten / (float)Math.Max(1, r.fps);
            s.error = r.Error ?? "";
            s.status = r.Error == null ? "completed" : r.Error == "stopped before the end" ? "stopped" : "failed";
            if (s.status == "completed" && string.IsNullOrEmpty(s.video))
                s.error = r.output == SimRecorder.VideoOutput.Frames ? "frames only" : "ffmpeg encodes the frames in the background";
            Save(s);
            string json = SessionState.GetString(RequestKey, "");
            if (string.IsNullOrEmpty(json)) return;
            SessionState.EraseString(RequestKey);
            if (JsonUtility.FromJson<Request>(json).exitPlay)
                EditorApplication.delayCall += () => { if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; };
        }

        private static void OnPlayMode(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredEditMode) return;
            string off = SessionState.GetString(DisableKey, "");
            if (!string.IsNullOrEmpty(off))
            {
                SessionState.EraseString(DisableKey);
                foreach (var r in UnityCompat.FindAll<SimRecorder>(true))
                    if (r != null && r.name == off && r.gameObject.scene.IsValid()) r.enabled = false;
            }
            // Play ended before the take: nothing was recorded
            if (string.IsNullOrEmpty(SessionState.GetString(RequestKey, ""))) return;
            SessionState.EraseString(RequestKey);
            var s = Load();
            if (s.status == "starting" || s.status == "approaching")
            {
                s.status = "stopped";
                s.error = "Play ended before the take started (the start date not reached, or no enabled Recorder).";
                Save(s);
            }
        }

        private static TakeStatus Load()
        {
            string json = SessionState.GetString(StatusKey, "");
            return string.IsNullOrEmpty(json) ? new TakeStatus() : JsonUtility.FromJson<TakeStatus>(json);
        }

        private static void Save(TakeStatus s) => SessionState.SetString(StatusKey, JsonUtility.ToJson(s));
    }
}
