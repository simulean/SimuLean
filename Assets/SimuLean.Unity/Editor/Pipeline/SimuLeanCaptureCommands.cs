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
    /// A picture of the model through the MCP, without placing cameras through <c>eval</c>.
    /// <c>simulean_capture</c> frames named
    /// objects, the whole model (<see cref="ModelLayout.OfModel"/>: elements, nodes, teams' members, lists, downtimes,
    /// reactions — not the floor) or a recorder shot (<see cref="SimRecorder.Around"/>, <see cref="SimRecorder.Framing"/>),
    /// renders a PNG with a temporary camera (the main camera's settings) and draws the connections and the markers of the
    /// objects without a mesh over it (<see cref="ConnectionOverlay"/>); the scene is not changed.
    /// </summary>
    public static partial class SimuLeanCommands
    {
        [CliCommand("simulean_capture", "Render a PNG of the scene: the named objects (empty = the whole model: its elements, " +
                                        "transport nodes, teams' members, lists, downtimes and reactions — not the floor or decoration) " +
                                        "seen from an elevation and azimuth at the distance that fits them, or a shot of the scene's " +
                                        "Recorder. The connections are drawn as coloured bands with arrows (flow, lists and the network's " +
                                        "tracks by default) and an object without a mesh as a labelled disc. Returns the file's path (open " +
                                        "it to see the picture), what was framed and drawn, and the camera used. The scene is not changed.",
                     Tags = new[] { Tag })]
        public static object Capture(
            [CliArg("objects", "Objects to frame 'A;B' (names or paths; empty = the whole model).")] string objects = "",
            [CliArg("elevation", "Degrees above the horizon (90 = from above).")] float elevation = 40,
            [CliArg("azimuth", "Compass direction the camera looks along (0 = +Z, 90 = +X).")] float azimuth = 30,
            [CliArg("distance", "Distance from the centre (0 = fit the targets).")] float distance = 0,
            [CliArg("fov", "Vertical field of view in degrees.")] float fov = 40,
            [CliArg("shot", "A shot of the scene's Recorder instead (its name): its framing at its start.")] string shot = "",
            [CliArg("width", "Image width in pixels.")] int width = 1280,
            [CliArg("height", "Image height in pixels.")] int height = 720,
            [CliArg("path", "PNG file (empty = Recordings/Captures/capture_<time>.png in the project).")] string path = "",
            [CliArg("connections", "Connections drawn: groups comma-separated (flow, lists, resources, locations, network, downtimes, " +
                                   "logic), all or none (empty = flow,lists,network). resources = every use of a team: process " +
                                   "(and its requirements' teams), setup crew, transport and a downtime's repair crew.")] string connections = "")
            => AgentReply.Of(() => CaptureOf(objects, elevation, azimuth, distance, fov, shot, width, height, path, connections));

        /// <summary>The connections a capture draws by default: the flow, the lists and the network's tracks.</summary>
        internal const string DefaultConnections = "flow,lists,network";

        private static object CaptureOf(string targets, float elevation, float azimuth, float distance, float fov, string shot,
                                        int width, int height, string path, string connections)
        {
            if (width < 16 || height < 16 || width > 7680 || height > 4320)
                throw new ArgumentException($"width / height: {width} × {height} is not between 16 × 16 and 7680 × 4320.");
            var groups = ModelLayout.Groups(string.IsNullOrWhiteSpace(connections) ? DefaultConnections : connections);
            if (!Application.isPlaying) TeamMemberPreviews.EnsureAll();   // a scene opened in this tick has no previews yet
            var named = string.IsNullOrWhiteSpace(targets) ? new List<string>()
                      : targets.Split(';').Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
            var model = ModelLayout.OfModel();
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
                var content = named.Count == 0 ? model : ModelLayout.OfObjects(named.Select(FindObject).ToList());
                if (content.IsEmpty) throw new ArgumentException("objects: the scene has no model element to frame; name the objects.");
                pose = Frame(content, elevation, azimuth, distance, fov, (float)width / height);
                framed = named.Count == 0 ? content.Describe() : string.Join(", ", named);
            }

            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(ResultsOutput.ProjectRoot, "Recordings", "Captures", $"capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            else if (!Path.IsPathRooted(path)) path = Path.Combine(ResultsOutput.ProjectRoot, path);
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // every marker of the model: those outside the picture are simply not seen
            int drawn = RenderPng(pose, fov, width, height, path, groups, model.Markers);
            AgentWrote(path);
            return new
            {
                path = path.Replace('\\', '/'),
                width, height,
                framed,
                drawn = new { connections = drawn, markers = model.Markers.Count,
                              groups = groups.Select(g => g.ToString().ToLowerInvariant()).OrderBy(g => g).ToList() },
                camera = new { position = Round3(pose.position), rotation = Round3(pose.rotation.eulerAngles), fov = pose.fieldOfView },
            };
        }

        /// <summary>Margin kept free on each side of the picture (a share of its half-size).</summary>
        private const float FrameMargin = 0.08f;
        /// <summary>The whole model is framed at least this wide and deep (m): a few elements are not zoomed on.</summary>
        internal const float MinModelSpan = 8f;
        /// <summary>Named objects are framed at least this wide and deep (m).</summary>
        private const float MinObjectSpan = 2f;

        /// <summary>The pose that frames <paramref name="content"/> from an elevation and azimuth: at <paramref name="distance"/>,
        /// or (0) at the closest distance at which every box corner and point fits the picture (<paramref name="aspect"/> =
        /// width / height) with a margin, the picture centred on them. Also the model thumbnails' (<see cref="SimuLeanModels"/>).</summary>
        internal static SimRecorder.CameraPose Frame(ModelLayout.Content content, float elevation, float azimuth, float distance, float fov, float aspect)
        {
            var pts = new List<Vector3>(content.Points);
            Bounds all = content.Boxes.Count > 0 ? content.Boxes[0] : new Bounds(content.Points[0], Vector3.zero);
            foreach (var b in content.Boxes)
            {
                all.Encapsulate(b);
                for (int i = 0; i < 8; i++)
                    pts.Add(new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z));
            }
            foreach (var p in content.Points) all.Encapsulate(p);
            // a minimum span on the floor, about the centre, when the content is smaller in both directions (a line of
            // elements keeps its own depth: no empty floor in front of it)
            float span = content.WholeModel ? MinModelSpan : MinObjectSpan, half = span * 0.5f;
            if (Mathf.Max(all.size.x, all.size.z) < span)
            {
                Vector3 c0 = all.center;
                foreach (var dx in new[] { -half, half })
                    foreach (var dz in new[] { -half, half })
                        pts.Add(new Vector3(c0.x + dx, all.min.y, c0.z + dz));
            }

            var rot = Quaternion.Euler(elevation, azimuth, 0f);
            Vector3 right = rot * Vector3.right, up = rot * Vector3.up, fwd = rot * Vector3.forward;
            float tanV = Mathf.Tan(Mathf.Deg2Rad * Mathf.Clamp(fov, 5f, 120f) * 0.5f) * (1f - FrameMargin);
            float tanH = tanV * Mathf.Max(0.1f, aspect);
            Vector3 centre = all.center;
            float d = distance;
            for (int pass = 0; pass < 4; pass++)
            {
                if (distance <= 0)
                {
                    d = 0.5f;
                    foreach (var p in pts)
                    {
                        Vector3 rel = p - centre;
                        float x = Vector3.Dot(rel, right), y = Vector3.Dot(rel, up), z = Vector3.Dot(rel, fwd);
                        d = Mathf.Max(d, Mathf.Max(Mathf.Abs(x) / tanH - z, Mathf.Abs(y) / tanV - z, 0.5f - z));
                    }
                }
                // centre the picture on what it shows (perspective puts the near side lower than the box centre)
                float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
                foreach (var p in pts)
                {
                    Vector3 rel = p - centre;
                    float depth = Mathf.Max(0.05f, Vector3.Dot(rel, fwd) + d);
                    float u = Vector3.Dot(rel, right) / depth, v = Vector3.Dot(rel, up) / depth;
                    minU = Mathf.Min(minU, u); maxU = Mathf.Max(maxU, u); minV = Mathf.Min(minV, v); maxV = Mathf.Max(maxV, v);
                }
                centre += right * ((minU + maxU) * 0.5f * d) + up * ((minV + maxV) * 0.5f * d);
            }
            return SimRecorder.Around(centre, d, elevation, azimuth, fov);
        }

        /// <summary>Renders the open scenes from <paramref name="pose"/> with a temporary hidden camera (the main camera's
        /// settings) into the PNG <paramref name="path"/> (its folder must exist), then draws over it the connections of
        /// <paramref name="groups"/> and the <paramref name="markers"/> (<see cref="ConnectionOverlay"/>; their names as
        /// temporary labels, never saved). The scene is not changed. Returns the number of connections drawn.</summary>
        internal static int RenderPng(SimRecorder.CameraPose pose, float fov, int width, int height, string path,
                                      ICollection<ConnectionGroup> groups = null,
                                      IList<(Vector3 at, Color color, string name)> markers = null)
        {
            var go = new GameObject("SimuLean capture") { hideFlags = HideFlags.HideAndDontSave };
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            var labels = new List<GameObject>();
            int drawn = 0;
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
                cam.aspect = (float)width / height;
                if (markers != null) foreach (var m in markers) labels.Add(Label(m.name, m.at, pose));
                cam.Render();
                RenderTexture.active = rt;
                if ((groups != null && groups.Count > 0) || (markers != null && markers.Count > 0))
                    drawn = DrawOverlay(cam, groups, markers);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                foreach (var l in labels) if (l != null) Object.DestroyImmediate(l);
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(image);
            }
            return drawn;
        }

        // the connections and markers over the rendered picture (in the active render texture)
        private static int DrawOverlay(Camera cam, ICollection<ConnectionGroup> groups, IList<(Vector3 at, Color color, string name)> markers)
        {
            int drawn = 0;
            Vector3 eye = cam.transform.position;
            GL.PushMatrix();
            GL.LoadProjectionMatrix(cam.projectionMatrix);   // GL converts it for the render texture itself
            GL.modelview = cam.worldToCameraMatrix;
            ConnectionOverlay.Material.SetPass(0);
            GL.Begin(GL.QUADS);
            if (groups != null && groups.Count > 0)
                foreach (var c in SceneConnections.All())
                {
                    if (!c.IsValid) continue;
                    var g = SceneConnections.GroupOf(c.Kind);
                    if (!groups.Contains(g) || !c.From.gameObject.activeInHierarchy || !c.To.gameObject.activeInHierarchy) continue;
                    Color col = SceneConnections.ColorOf(g);
                    col.a = 0.9f;
                    ConnectionOverlay.Ribbon(eye, SceneConnections.Anchor(c.From), SceneConnections.Anchor(c.To), col, SceneConnections.IsDirected(c));
                    drawn++;
                }
            if (markers != null)
                foreach (var m in markers) ConnectionOverlay.Disc(eye, m.at, new Color(m.color.r, m.color.g, m.color.b, 0.95f));
            GL.End();
            GL.PopMatrix();
            return drawn;
        }

        // a temporary label (never saved) above a marker, facing the camera, its letters about 2 % of the distance high
        private static GameObject Label(string text, Vector3 at, SimRecorder.CameraPose pose)
        {
            float dist = Vector3.Distance(pose.position, at);
            var go = new GameObject("SimuLean capture label") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetPositionAndRotation(at + pose.rotation * Vector3.up * (dist * 0.022f), pose.rotation);
            var tm = go.AddComponent<TextMesh>();
            var font = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            tm.font = font;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            tm.text = text;
            tm.fontSize = 64;
            tm.characterSize = dist * 0.018f / 6.4f;
            tm.anchor = TextAnchor.LowerCenter;
            tm.color = new Color(0.12f, 0.12f, 0.14f, 1f);
            return go;
        }

        private static float[] Round3(Vector3 v) => new[] { (float)Math.Round(v.x, 2), (float)Math.Round(v.y, 2), (float)Math.Round(v.z, 2) };
    }
}
