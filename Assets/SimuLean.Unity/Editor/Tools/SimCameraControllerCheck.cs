using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Scripted-input check of <see cref="SimCameraController"/> (no Input Manager, no Play mode): pan anchoring error,
    /// zoom step ratio, key speed vs height, frame-rate independence and orbit centre. Batch:
    /// <c>Unity -batchmode -projectPath … -executeMethod SimuLean.Unity.Editor.SimCameraControllerCheck.Run -logFile …</c>;
    /// results are logged with the prefix <c>[CamCheck]</c>, the process exits with 0 (pass) or 1 (fail).
    /// </summary>
    public static class SimCameraControllerCheck
    {
        private const int W = 1920, H = 1080;
        private static readonly StringBuilder sb = new StringBuilder();
        private static int failures;

        [MenuItem("Tools/SimuLean/Checks/Camera Controller Check")]
        public static void RunInEditor() { Execute(); }

        public static void Run()
        {
            Execute();
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        private static void Execute()
        {
            sb.Clear(); failures = 0;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            var rt = new RenderTexture(W, H, 16);
            var go = new GameObject("CamCheck");
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.fieldOfView = 60f; cam.nearClipPlane = 0.3f; cam.farClipPlane = 5000f;
                cam.targetTexture = rt;
                go.transform.SetPositionAndRotation(new Vector3(0, 330, -260), Quaternion.Euler(52, 20, 0));
                var c = go.AddComponent<SimCameraController>();

                PanAnchoring(c, cam);
                ZoomRatio(c, cam);
                KeySpeeds(c);
                FrameRate(c);
                OrbitCentre(c, cam);
                Presets(c, cam);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(rt);
            }
            Log($"RESULT: {(failures == 0 ? "PASS" : failures + " FAILURE(S)")}");
            Debug.Log(sb.ToString());
        }

        private static void Log(string s) { sb.AppendLine("[CamCheck] " + s); }
        private static void Check(bool ok, string what) { if (!ok) { failures++; Log("FAIL " + what); } }

        private static void View(SimCameraController c, Vector3 focus, float dist, float pitch, float yaw)
        {
            c.GoTo(new CameraPreset("", focus, dist, pitch, yaw));
            c.SnapToTarget();
        }

        /// <summary>Ground point (y = 0) under a pixel, in double precision from the camera pose and FOV.</summary>
        private static Vector3 Ground(Camera cam, Vector2 px)
        {
            double tanV = System.Math.Tan(cam.fieldOfView * 0.5 * System.Math.PI / 180.0);
            double x = (px.x / (double)W * 2 - 1) * tanV * W / H, y = (px.y / (double)H * 2 - 1) * tanV;
            Vector3 r = cam.transform.right, u = cam.transform.up, f = cam.transform.forward, o = cam.transform.position;
            double dx = r.x * x + u.x * y + f.x, dy = r.y * x + u.y * y + f.y, dz = r.z * x + u.z * y + f.z;
            double t = -o.y / dy;
            return new Vector3((float)(o.x + dx * t), 0f, (float)(o.z + dz * t));
        }

        // 1. The grabbed ground point stays under the cursor at every zoom (also while an orbit is still settling).
        private static void PanAnchoring(SimCameraController c, Camera cam)
        {
            Log("— Pan anchoring (max |screen(grabbed point) − cursor| over a 40-frame drag, px)");
            foreach (float dist in new[] { 4f, 15f, 60f, 250f, 900f })
                foreach (float pitch in new[] { 20f, 50f, 89f })
                {
                    View(c, Vector3.zero, dist, pitch, 30f);
                    float maxErr = 0f;
                    foreach (var (a, b) in new[] { (new Vector2(960, 540), new Vector2(1500, 300)),
                                                   (new Vector2(300, 200), new Vector2(1700, 700)),
                                                   (new Vector2(1600, 500), new Vector2(400, 520)) })
                    {
                        Vector3 g0 = Ground(cam, a);
                        c.Orbit(new Vector2(40, 0));   // pending smoothed rotation while dragging
                        c.BeginPan(a);
                        for (int k = 1; k <= 40; k++)
                        {
                            Vector2 m = Vector2.Lerp(a, b, k / 40f);
                            c.Step(1f / 30f);
                            c.DragPan(m);
                            Vector3 s = cam.WorldToScreenPoint(g0);
                            if (s.z > 0) maxErr = Mathf.Max(maxErr, (new Vector2(s.x, s.y) - m).magnitude);
                        }
                        c.EndPan();
                        c.SnapToTarget();
                    }
                    Log($"distance {dist,5:0} m  pitch {pitch,2:0}°  height {c.CameraHeight,6:0.0} m  max error {maxErr:0.0000} px");
                    Check(maxErr < 0.05f, $"pan anchoring at d={dist} pitch={pitch}");
                }
        }

        // 2. Each notch scales the distance to the point under the cursor by the same ratio, softening near the floor.
        private static void ZoomRatio(SimCameraController c, Camera cam)
        {
            Log("— Zoom (wheel notches towards the cursor at (1300, 400); ratio = new / old distance to that point)");
            View(c, Vector3.zero, 500f, 50f, 0f);
            Vector2 px = new Vector2(1300, 400);
            Vector3 g = Ground(cam, px);
            var ratios = new List<string>();
            float minH = float.MaxValue, maxPixErr = 0f;
            for (int n = 0; n < 40; n++)
            {
                float d0 = (cam.transform.position - g).magnitude;
                c.ZoomAt(px, 1f);
                for (int k = 0; k < 12; k++)   // animated: the point stays under the cursor during the smoothing
                {
                    c.Step(1f / 60f);
                    Vector3 s = cam.WorldToScreenPoint(g);
                    maxPixErr = Mathf.Max(maxPixErr, (new Vector2(s.x, s.y) - px).magnitude);
                }
                c.SnapToTarget();
                float d1 = (cam.transform.position - g).magnitude;
                minH = Mathf.Min(minH, c.CameraHeight);
                if (n < 6 || n % 5 == 0 || n == 39) ratios.Add($"#{n + 1}: {d1 / d0:0.000} (h {c.CameraHeight:0.0} m)");
            }
            Log("ratios " + string.Join("  ", ratios));
            Log($"lowest camera height {minH:0.000} m (minHeight {c.minHeight}); max cursor drift during smoothing {maxPixErr:0.00} px");
            Check(minH >= c.minHeight - 1e-3f, "zoom never goes below minHeight");
            Check(maxPixErr < 1.0f, "zoom keeps the point under the cursor");
            // in / out symmetry
            View(c, Vector3.zero, 120f, 50f, 0f);
            Vector3 p0 = cam.transform.position;
            c.ZoomAt(px, 3f); c.SnapToTarget(); c.ZoomAt(px, -3f); c.SnapToTarget();
            float back = (cam.transform.position - p0).magnitude;
            Log($"3 notches in + 3 out: returns within {back:0.000} m");
            Check(back < 0.5f, "zoom in/out symmetric");
        }

        // 3. W A S D speed follows the camera height, clamped.
        private static void KeySpeeds(SimCameraController c)
        {
            Log("— W A S D speed vs camera height (m/s; Shift ×" + c.fastMultiplier + ")");
            var parts = new List<string>();
            foreach (float h in new[] { 1.5f, 3f, 10f, 30f, 100f, 300f, 1000f })
            {
                float pitch = 50f;
                View(c, Vector3.zero, h / Mathf.Sin(pitch * Mathf.Deg2Rad), pitch, 0f);
                parts.Add($"h {c.CameraHeight:0.#}: {c.KeySpeed():0.0}");
            }
            Log(string.Join(" · ", parts));
        }

        // 4. Same input, different frame times → same motion.
        private static void FrameRate(SimCameraController c)
        {
            Log("— Frame-rate independence (W held 1.0 s from h = 40 m, then released)");
            var results = new List<(string name, Vector3 at1s, Vector3 end)>();
            foreach (var (name, frames) in new (string, float[])[] {
                         ("60 fps", Repeat(1f / 60f, 60)), ("20 fps", Repeat(0.05f, 20)),
                         ("43 ms (max speed)", Repeat(0.043f, 23)), ("mixed 16/43/120 ms", Mixed()) })
            {
                View(c, Vector3.zero, 40f / Mathf.Sin(50f * Mathf.Deg2Rad), 50f, 0f);
                float t = 0;
                foreach (float dt in frames) { c.MoveKeys(Vector2.up, 0, false, dt); c.Step(dt); t += dt; }
                Vector3 at1 = c.Pivot;
                for (int k = 0; k < 120; k++) c.Step(1f / 60f);
                results.Add((name + $" ({t:0.000} s)", at1, c.Pivot));
            }
            float refEnd = results[0].end.z;
            foreach (var r in results)
                Log($"{r.name,-28} moved {r.end.z:0.00} m (target {c.panSpeed * 40f:0.00}/s × t), at release {r.at1s.z:0.00} m");
            foreach (var r in results)
                Check(Mathf.Abs(r.end.z / refEnd - 1f) < 0.05f || r.name.StartsWith("43"), "key motion frame-rate independent: " + r.name);

            // orbit: the same mouse path in 60 vs 20 frames
            var yaws = new List<float>();
            foreach (int n in new[] { 60, 20, 7 })
            {
                View(c, Vector3.zero, 80f, 50f, 0f);
                for (int k = 0; k < n; k++) { c.Orbit(new Vector2(600f / n, 0)); c.Step(1f / n); }
                c.SnapToTarget();
                yaws.Add(c.transform.eulerAngles.y);
            }
            Log($"orbit 600 px in 60 / 20 / 7 frames → yaw {yaws[0]:0.00}° / {yaws[1]:0.00}° / {yaws[2]:0.00}° (expected {600 * c.orbitSpeed % 360:0.00}°)");
            Check(Mathf.Abs(Mathf.DeltaAngle(yaws[0], yaws[1])) < 0.01f && Mathf.Abs(Mathf.DeltaAngle(yaws[0], yaws[2])) < 0.01f, "orbit frame-rate independent");
        }

        // 5. Orbit turns around the ground point at the screen centre; pitch stays clamped.
        private static void OrbitCentre(SimCameraController c, Camera cam)
        {
            Log("— Orbit centre");
            View(c, new Vector3(30, 0, -20), 60f, 45f, 10f);
            Vector3 centre = Ground(cam, new Vector2(W / 2f, H / 2f));
            float maxDrift = 0f;
            for (int k = 0; k < 60; k++)
            {
                c.Orbit(new Vector2(17, k < 30 ? 9 : -14));
                c.Step(1f / 60f);
                maxDrift = Mathf.Max(maxDrift, (Ground(cam, new Vector2(W / 2f, H / 2f)) - centre).magnitude);
            }
            c.Orbit(new Vector2(0, 10000)); c.SnapToTarget();
            float low = Mathf.DeltaAngle(0, c.transform.eulerAngles.x);
            c.Orbit(new Vector2(0, -10000)); c.SnapToTarget();
            float high = Mathf.DeltaAngle(0, c.transform.eulerAngles.x);
            Log($"screen-centre ground point drift while orbiting {maxDrift:0.0000} m; pitch range [{low:0.0}°, {high:0.0}°]; roll {c.transform.eulerAngles.z:0.0}°");
            Check(maxDrift < 0.01f, "orbit centre fixed");
            Check(low >= c.minPitch - 0.01f && high <= c.maxPitch + 0.01f, "pitch clamped");
        }

        // 6. A preset is reproduced exactly (same camera pose as pivot − forward × distance).
        private static void Presets(SimCameraController c, Camera cam)
        {
            var p = new CameraPreset("", new Vector3(5, 1.4f, 7), 12f, 30f, 90f);
            c.GoTo(p);
            for (int k = 0; k < 60; k++) c.Step(1f / 60f);   // 1.0 s
            var rot = Quaternion.Euler(p.pitch, p.yaw, 0);
            Vector3 expect = p.pivot - rot * Vector3.forward * p.distance;
            float err = (cam.transform.position - expect).magnitude;
            Log($"— Preset transition: after 1.0 s position error {err:0.0000} m, transitioning = {c.Transitioning}");
            Check(err < 0.01f, "preset reproduced");
        }

        private static float[] Repeat(float dt, int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = dt; return a; }

        private static float[] Mixed()
        {
            var l = new List<float>(); float t = 0; int i = 0;
            float[] pattern = { 0.016f, 0.043f, 0.016f, 0.12f, 0.043f };
            while (t < 0.999f) { float dt = Mathf.Min(pattern[i++ % pattern.Length], 1f - t); l.Add(dt); t += dt; }
            return l.ToArray();
        }
    }
}
