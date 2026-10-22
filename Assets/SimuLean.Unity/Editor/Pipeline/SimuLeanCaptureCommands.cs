using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Pipeline.Commands;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// A picture of the model through the MCP: the agents placed cameras,
    /// framed and captured through <c>eval</c> (76–95 calls per presentation pass). <c>simulean_capture</c> frames named
    /// elements, the whole model or a recorder shot (<see cref="SimRecorder.Around"/>, <see cref="SimRecorder.Framing"/>) and
    /// renders a PNG with a temporary camera (the main camera's settings); the scene is not changed.
    /// </summary>
    public static partial class SimuLeanCommands
    {
        [CliCommand("simulean_capture", "Render a PNG of the scene: the named elements (empty = the whole model) seen from an " +
                                        "elevation and azimuth at the distance that fits them, or a shot of the scene's Recorder. " +
                                        "Returns the file's path (open it to see the picture) and the camera used. The scene is not " +
                                        "changed.", Tags = new[] { Tag })]
        public static object Capture(
            [CliArg("targets", "Objects to frame 'A;B' (names or paths; empty = every model element).")] string targets = "",
            [CliArg("elevation", "Degrees above the horizon (90 = from above).")] float elevation = 40,
            [CliArg("azimuth", "Compass direction the camera looks along (0 = +Z, 90 = +X).")] float azimuth = 30,
            [CliArg("distance", "Distance from the centre (0 = fit the targets).")] float distance = 0,
            [CliArg("fov", "Vertical field of view in degrees.")] float fov = 40,
            [CliArg("shot", "A shot of the scene's Recorder instead (its name): its framing at its start.")] string shot = "",
            [CliArg("width", "Image width in pixels.")] int width = 1280,
            [CliArg("height", "Image height in pixels.")] int height = 720,
            [CliArg("path", "PNG file (empty = Recordings/Captures/capture_<time>.png in the project).")] string path = "")
            => AgentReply.Of(() => CaptureOf(targets, elevation, azimuth, distance, fov, shot, width, height, path));

        private static object CaptureOf(string targets, float elevation, float azimuth, float distance, float fov, string shot,
                                        int width, int height, string path)
        {
            if (width < 16 || height < 16 || width > 7680 || height > 4320)
                throw new ArgumentException($"width / height: {width} × {height} is not between 16 × 16 and 7680 × 4320.");
            SimRecorder.CameraPose pose;
            string framed;
            if (!string.IsNullOrWhiteSpace(shot))
            {
                var recorder = RecorderSession.FindRecorder("");
                int index = recorder.shots.FindIndex(s => s != null && s.name == shot.Trim());
                if (index < 0)
                    throw new ArgumentException($"shot: the Recorder has no shot '{shot}' (it has {string.Join(", ", recorder.shots.Where(s => s != null).Select(s => s.name))}).");
                pose = recorder.Framing(index, 0f);
                framed = "shot " + shot.Trim();
            }
            else
            {
                var objects = string.IsNullOrWhiteSpace(targets)
                    ? UnityCompat.FindAllInHierarchyOrder<SElement>().Select(e => e.gameObject).ToList()
                    : targets.Split(';').Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => FindObject(t.Trim())).ToList();
                if (objects.Count == 0) throw new ArgumentException("targets: the scene has no model element to frame; name the objects.");
                var bounds = BoundsOf(objects);
                float radius = Mathf.Max(0.5f, bounds.extents.magnitude);
                float fit = radius / Mathf.Sin(Mathf.Deg2Rad * Mathf.Clamp(fov, 5f, 120f) * 0.5f) * 1.05f;
                pose = SimRecorder.Around(bounds.center, distance > 0 ? distance : fit, elevation, azimuth, fov);
                framed = string.IsNullOrWhiteSpace(targets) ? $"the model ({objects.Count} elements)" : string.Join(", ", objects.Select(o => o.name));
            }

            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(ResultsOutput.ProjectRoot, "Recordings", "Captures", $"capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            else if (!Path.IsPathRooted(path)) path = Path.Combine(ResultsOutput.ProjectRoot, path);
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var go = new GameObject("SimuLean capture") { hideFlags = HideFlags.HideAndDontSave };
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                var cam = go.AddComponent<Camera>();
                var main = Camera.main;
                if (main != null) cam.CopyFrom(main);
                cam.enabled = false;
                cam.transform.SetPositionAndRotation(pose.position, pose.rotation);
                cam.fieldOfView = pose.fieldOfView > 0 ? pose.fieldOfView : fov;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = Mathf.Max(cam.farClipPlane, 5000f);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(image);
            }
            return new
            {
                path = path.Replace('\\', '/'),
                width, height,
                framed,
                camera = new { position = Round3(pose.position), rotation = Round3(pose.rotation.eulerAngles), fov = pose.fieldOfView },
            };
        }

        // the renderers' bounds of the objects (their children included), else their positions
        private static Bounds BoundsOf(List<GameObject> objects)
        {
            Bounds? b = null;
            foreach (var o in objects)
                foreach (var r in o.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled) continue;
                    if (b == null) b = r.bounds; else { var x = b.Value; x.Encapsulate(r.bounds); b = x; }
                }
            if (b != null) return b.Value;
            var p = new Bounds(objects[0].transform.position, Vector3.one);
            foreach (var o in objects) p.Encapsulate(o.transform.position);
            return p;
        }

        private static float[] Round3(Vector3 v) => new[] { (float)Math.Round(v.x, 2), (float)Math.Round(v.y, 2), (float)Math.Round(v.z, 2) };
    }
}
