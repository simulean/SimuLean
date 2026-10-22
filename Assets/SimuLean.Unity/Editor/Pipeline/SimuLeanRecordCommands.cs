using System;
using System.Globalization;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The Recorder through the MCP: <c>simulean_record_shot</c> adds or changes the shots of a scene's
    /// Recorder (placing one when there is none), <c>simulean_record_start</c> records a take in Play and
    /// <c>simulean_record_status</c> follows it until the MP4 is written — the background pattern of the headless runs.
    /// Recorder settings (captions, live values, size, output) are fields: <c>simulean_set</c>.
    /// </summary>
    public static class SimuLeanRecordCommands
    {
        private const string Tag = "simulean";

        [CliCommand("simulean_record_shot", "Add or change a shot of the scene's Recorder (one is placed when there is none): " +
                                            "framing fixed (position/rotation, or fromView = the Scene view's camera), element " +
                                            "(target seen from distance/elevation/azimuth: follows the layout), orbit (the same, " +
                                            "turning orbit degrees) or pose (a placed GameObject); seconds; glide (a smooth curve " +
                                            "from the previous shot); caption title / subtitle. Empty arguments keep the shot's " +
                                            "values (a single space clears a text). Without 'shot': the list. Undoable.", Tags = new[] { Tag })]
        public static object RecordShot(
            [CliArg("shot", "Shot name: changed when it exists, added otherwise; empty = list the shots.")] string shot = "",
            [CliArg("framing", "fixed | pose | element | orbit (empty = element when a target is given, else unchanged / fixed).")] string framing = "",
            [CliArg("target", "Element / orbit: the framed GameObject (name or path).")] string target = "",
            [CliArg("distance", "Element / orbit: metres from the framed point.")] string distance = "",
            [CliArg("elevation", "Element / orbit: degrees above the horizon.")] string elevation = "",
            [CliArg("azimuth", "Element / orbit: compass direction the camera looks along (degrees, 0 = +Z, 90 = +X).")] string azimuth = "",
            [CliArg("orbit", "Orbit: degrees turned during the shot.")] string orbit = "",
            [CliArg("lookHeight", "Element / orbit: framed point's height above the element (m).")] string lookHeight = "",
            [CliArg("position", "Fixed: camera position 'x,y,z'.")] string position = "",
            [CliArg("rotation", "Fixed: camera rotation 'x,y,z' (Euler degrees).")] string rotation = "",
            [CliArg("pose", "Pose: the GameObject whose position and rotation the camera takes.")] string pose = "",
            [CliArg("fromView", "Take the Scene view's camera: a fixed shot, or with a target its angle and distance.")] bool fromView = false,
            [CliArg("seconds", "Length in seconds of video.")] string seconds = "",
            [CliArg("glide", "true = glide from the previous shot, false = cut.")] string glide = "",
            [CliArg("glideSeconds", "Seconds of the glide (0 = the whole shot).")] string glideSeconds = "",
            [CliArg("fov", "Field of view (0 = the recorder's).")] string fov = "",
            [CliArg("title", "Caption title (placeholders {content:Element}, {time}…).")] string title = "",
            [CliArg("subtitle", "Caption subtitle.")] string subtitle = "",
            [CliArg("index", "Position in the list (0 = first) to insert or move the shot to; empty = keep / append.")] string index = "",
            [CliArg("remove", "Remove the shot.")] bool remove = false,
            [CliArg("recorder", "The Recorder (name or path; empty = the scene's).")] string recorder = "")
            => AgentReply.Of(() => RecordShotOf(shot, framing, target, distance, elevation, azimuth, orbit, lookHeight, position, rotation,
                                                pose, fromView, seconds, glide, glideSeconds, fov, title, subtitle, index, remove, recorder));

        private static object RecordShotOf(string shot, string framing, string target, string distance, string elevation, string azimuth,
                                           string orbit, string lookHeight, string position, string rotation, string pose, bool fromView,
                                           string seconds, string glide, string glideSeconds, string fov, string title, string subtitle,
                                           string index, bool remove, string recorder)
        {
            var r = RecorderFor(recorder, create: !string.IsNullOrWhiteSpace(shot));
            if (string.IsNullOrWhiteSpace(shot)) return Describe(r);
            Undo.RecordObject(r, "Record shot " + shot);
            int at = r.shots.FindIndex(s => s != null && s.name == shot.Trim());
            if (remove)
            {
                if (at < 0) throw new ArgumentException($"No shot '{shot}'. Shots: {string.Join(", ", r.shots.Select(s => s.name))}.");
                r.shots.RemoveAt(at);
                return Saved(r);
            }
            // a copy, put in place only when every argument is valid: a failing call leaves the shot as it was (review2 #12)
            var sh = at >= 0 ? CopyOf(r.shots[at]) : new SimRecorder.Shot { name = shot.Trim(), glide = r.shots.Count > 0 };

            if (!string.IsNullOrWhiteSpace(framing))
                sh.framing = (SimRecorder.ShotFraming)Enum.Parse(typeof(SimRecorder.ShotFraming), framing.Trim(), true);
            else if (!string.IsNullOrWhiteSpace(target) && sh.framing != SimRecorder.ShotFraming.Orbit)
                sh.framing = SimRecorder.ShotFraming.Element;
            else if (!string.IsNullOrWhiteSpace(pose)) sh.framing = SimRecorder.ShotFraming.Pose;
            if (!string.IsNullOrWhiteSpace(target)) sh.target = SimuLeanCommands.FindObject(target).transform;
            if (!string.IsNullOrWhiteSpace(pose)) sh.pose = SimuLeanCommands.FindObject(pose).transform;
            Number(lookHeight, v => sh.lookHeight = v, "lookHeight");
            if (fromView)
            {
                var cam = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
                if (cam == null) throw new InvalidOperationException("No Scene view to take the camera from.");
                if (sh.target != null && sh.framing != SimRecorder.ShotFraming.Fixed && sh.framing != SimRecorder.ShotFraming.Pose)
                    RecorderAuthoring.FrameFromView(sh, cam, sh.target);
                else
                {
                    var v = RecorderAuthoring.FromView(cam, sh.name);
                    sh.framing = SimRecorder.ShotFraming.Fixed;
                    sh.position = v.position;
                    sh.rotation = v.rotation;
                }
            }
            Number(distance, v => sh.distance = Mathf.Max(0.5f, v), "distance");
            Number(elevation, v => sh.elevation = Mathf.Clamp(v, -10f, 89f), "elevation");
            Number(azimuth, v => sh.azimuth = v, "azimuth");
            Number(orbit, v => sh.orbitDegrees = v, "orbit");
            Number(seconds, v => sh.seconds = Mathf.Max(0.1f, v), "seconds");
            Number(glideSeconds, v => sh.glideSeconds = Mathf.Max(0f, v), "glideSeconds");
            Number(fov, v => sh.fieldOfView = Mathf.Clamp(v, 0f, 90f), "fov");
            if (!string.IsNullOrWhiteSpace(position)) sh.position = Vector(position, "position");
            if (!string.IsNullOrWhiteSpace(rotation)) sh.rotation = Vector(rotation, "rotation");
            if (!string.IsNullOrWhiteSpace(glide)) sh.glide = bool.Parse(glide.Trim());
            if (!string.IsNullOrEmpty(title)) sh.title = title.Trim();
            if (!string.IsNullOrEmpty(subtitle)) sh.subtitle = subtitle.Trim();
            if ((sh.framing == SimRecorder.ShotFraming.Element || sh.framing == SimRecorder.ShotFraming.Orbit) && sh.target == null)
                throw new ArgumentException($"Shot '{sh.name}' frames an element: give a target.");
            if (sh.framing == SimRecorder.ShotFraming.Pose && sh.pose == null)
                throw new ArgumentException($"Shot '{sh.name}' is a pose shot: give a pose GameObject.");

            if (at >= 0) r.shots[at] = sh;
            if (at >= 0 && !string.IsNullOrWhiteSpace(index)) r.shots.RemoveAt(at);
            if (at < 0 || !string.IsNullOrWhiteSpace(index))
            {
                int i = string.IsNullOrWhiteSpace(index) ? r.shots.Count : int.Parse(index.Trim(), CultureInfo.InvariantCulture);
                r.shots.Insert(Mathf.Clamp(i, 0, r.shots.Count), sh);
            }
            return Saved(r);
        }

        private static SimRecorder.Shot CopyOf(SimRecorder.Shot shot)
            => (SimRecorder.Shot)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                                               .Invoke(shot, null);

        [CliCommand("simulean_record_start", "Record a video of the scene in Play with its Recorder: the clock is driven to the start " +
                                             "date, then the shots are rendered at a fixed frame rate with their captions into an " +
                                             "MP4 (Unity's encoder: nothing to install); Play ends with the take. Returns at once: " +
                                             "poll simulean_record_status. The settings given apply to this take only.", Tags = new[] { Tag })]
        public static object RecordStart(
            [CliArg("scene", "Scene to open (asset path or name; empty = the open one). Modified scenes must be saved first.")] string scene = "",
            [CliArg("startAt", "Model date of the first frame (e.g. '2025-03-11 08:50'; empty = the recorder's).")] string startAt = "",
            [CliArg("seconds", "Video length (0 = the shots' total; given, the shots are stretched to it).")] float seconds = 0,
            [CliArg("fps", "Frames per second (0 = the recorder's).")] int fps = 0,
            [CliArg("width", "Frame width (0 = the recorder's).")] int width = 0,
            [CliArg("height", "Frame height (0 = the recorder's).")] int height = 0,
            [CliArg("exitPlay", "Leave Play when the take ends.")] bool exitPlay = true,
            [CliArg("recorder", "The Recorder (name or path; empty = the scene's).")] string recorder = "",
            [CliArg("quality", "Standard | High | Maximum (empty = the recorder's): the MP4's bitrate by the frame size and rate.")] string quality = "",
            [CliArg("bitrateMbps", "MP4 bitrate in Mbps (0 = by the quality).")] float bitrateMbps = 0)
            => AgentReply.Of(() => RecorderSession.Start(scene, recorder, startAt, seconds, fps, width, height, exitPlay, quality, bitrateMbps));

        [CliCommand("simulean_record_status", "The take of simulean_record_start: 'starting', 'approaching' (the clock driven to the " +
                                              "start date), 'recording' (frames / in all), then 'completed' with the MP4's path, " +
                                              "'failed' or 'stopped'. cancel=true ends Play (the video so far is kept); wait=s answers as soon " +
                                              "as the take ends (at most 55 s).", Tags = new[] { Tag })]
        public static System.Threading.Tasks.Task<object> RecordStatus([CliArg("cancel", "Stop the take.")] bool cancel = false,
            [CliArg("wait", "Seconds to wait for the job to end (0 = answer at once; at most 55): the answer comes as soon as it ends, so poll with wait instead of sleeping.")] double wait = 0)
        {
            if (cancel) return System.Threading.Tasks.Task.FromResult<object>(AgentReply.Of(() => RecorderSession.Status(true)));
            return JobWait.Until(() => JobWait.Json(AgentReply.Of(() => RecorderSession.Status(false))), wait);
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private static SimRecorder RecorderFor(string name, bool create)
        {
            if (!string.IsNullOrWhiteSpace(name) || !create) return RecorderSession.FindRecorder(name);
            var all = UnityCompat.FindAll<SimRecorder>(true).Where(x => x != null && x.gameObject.scene.IsValid()).ToList();
            if (all.Count > 0) return RecorderSession.FindRecorder("");
            SimuLeanCommands.AddElement("Recorder");
            return RecorderSession.FindRecorder("");
        }

        private static object Saved(SimRecorder r)
        {
            EditorUtility.SetDirty(r);
            EditorSceneManager.MarkSceneDirty(r.gameObject.scene);
            return Describe(r);
        }

        private static object Describe(SimRecorder r) => new
        {
            recorder = r.name,
            seconds = r.TotalSeconds,
            startAt = r.startAt,
            shots = r.shots.Select((s, i) => new
            {
                name = s.name,
                framing = s.framing.ToString(),
                target = s.framing == SimRecorder.ShotFraming.Element || s.framing == SimRecorder.ShotFraming.Orbit ? s.target?.name
                       : s.framing == SimRecorder.ShotFraming.Pose ? s.pose?.name : null,
                start = r.ShotStart(i),
                seconds = s.seconds,
                glide = s.glide,
                title = s.title,
                camera = Round(r.Framing(i, 0f).position),
            }).ToList(),
        };

        private static float[] Round(Vector3 v) => new[] { (float)Math.Round(v.x, 2), (float)Math.Round(v.y, 2), (float)Math.Round(v.z, 2) };

        private static void Number(string text, Action<float> set, string what)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                throw new ArgumentException($"{what}: '{text}' is not a number.");
            set(v);
        }

        private static Vector3 Vector(string text, string what)
        {
            var parts = text.Split(',').Select(s => s.Trim()).ToArray();
            if (parts.Length != 3 || !parts.All(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                throw new ArgumentException($"{what}: '{text}' is not 'x,y,z'.");
            var v = parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            return new Vector3(v[0], v[1], v[2]);
        }
    }
}
