using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>A named camera view: orbit pivot, distance and angles.</summary>
    [System.Serializable]
    public class CameraPreset
    {
        public string name = "View";
        public Vector3 pivot;
        public float distance = 50f;
        [Range(-89f, 89f)] public float pitch = 50f;
        public float yaw;

        public CameraPreset() { }
        public CameraPreset(string name, Vector3 pivot, float distance, float pitch, float yaw)
        { this.name = name; this.pivot = pivot; this.distance = distance; this.pitch = pitch; this.yaw = yaw; }
    }

    /// <summary>
    /// Reusable runtime navigation camera for simulation scenes in Play mode (legacy Input Manager), with
    /// "map-like" (FlexSim-style) behaviour that does not depend on the zoom, the frame rate or the history:
    /// <list type="bullet">
    /// <item><b>Pan</b> (middle drag, or left drag when <see cref="leftDragPans"/>): the point grabbed under the cursor
    /// (ground plane <see cref="groundHeight"/>, or a collider hit) stays exactly under the cursor at any zoom.</item>
    /// <item><b>Orbit</b> (right drag): around the ground point at the screen centre (the focus), constant degrees per
    /// pixel, pitch clamped to [<see cref="minPitch"/>, <see cref="maxPitch"/>], no roll.</item>
    /// <item><b>Zoom</b> (wheel): towards the point under the cursor, each notch the same fraction
    /// (<see cref="zoomStep"/>) of the distance to it; it slows down smoothly approaching <see cref="minHeight"/> above the
    /// floor / <see cref="minDistance"/> from the point, and stops at <see cref="maxDistance"/>.</item>
    /// <item><b>W A S D / arrows</b> pan on the ground and <b>Q / E</b> move down / up at a speed proportional to the
    /// camera height (clamped to [<see cref="minKeySpeed"/>, <see cref="maxKeySpeed"/>]; Shift × <see cref="fastMultiplier"/>).</item>
    /// <item>Left click without drag selects; <b>F</b> frames the selection (or the object under the cursor); 1–9 preset
    /// views; <see cref="resetKey"/> the initial view.</item>
    /// </list>
    /// The pose is a ground focus point (always on the ground plane, at the screen centre), a distance, a pitch and a
    /// yaw. Orbit, zoom and key moves change a target pose that the camera follows with a light critically damped
    /// spring (<see cref="smoothTime"/>); grab panning moves the displayed pose directly, so it never lags. All timing
    /// uses <c>Time.unscaledDeltaTime</c> (clamped to <see cref="maxFrameTime"/>), independent of <c>Time.timeScale</c>
    /// and of the simulation frame budget. Input that starts over an IMGUI panel registered with
    /// <see cref="SimRuntimeHud.RegisterScreenRect"/> (or while an IMGUI control holds the mouse) is ignored.
    /// Picking uses colliders when present, otherwise renderer bounds (scene builders often strip colliders).
    /// The public methods <see cref="Orbit"/>, <see cref="ZoomAt"/>, <see cref="BeginPan"/>/<see cref="DragPan"/>,
    /// <see cref="MoveKeys"/> and <see cref="Step"/> drive it from scripts or tests without the Input Manager.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [SimuLean.Unity.SimuLeanTool(SimuLean.Unity.SimuLeanToolCategories.Visualization, "Camera Controller", Description = "FlexSim-style navigation camera (added to the main camera)", Singleton = true, OnMainCamera = true)]
    public class SimCameraController : MonoBehaviour
    {
        [Header("Speeds")]
        [Tooltip("Orbit: degrees per pixel of mouse movement.")]
        public float orbitSpeed = 0.25f;
        [Tooltip("W A S D: ground speed as a fraction of the camera height per second.")]
        public float panSpeed = 0.8f;
        [Tooltip("Wheel: fraction of the distance to the point under the cursor per notch.")]
        public float zoomStep = 0.15f;
        [Tooltip("Q / E: vertical speed as a fraction of the camera height per second.")]
        public float verticalSpeed = 0.5f;
        [Tooltip("Shift multiplies key speeds by this (and doubles the zoom step).")]
        public float fastMultiplier = 3f;
        [Tooltip("Key speed limits (m/s, before Shift).")]
        public float minKeySpeed = 2f, maxKeySpeed = 300f;

        [Header("Limits")]
        [Tooltip("Closest the wheel brings the camera to the point under the cursor.")]
        public float minDistance = 2f;
        [Tooltip("Farthest the wheel takes the camera from the point under the cursor.")]
        public float maxDistance = 1500f;
        [Tooltip("Lowest camera height above the ground plane; the zoom slows down smoothly approaching it.")]
        public float minHeight = 1.5f;
        [Range(1f, 89.9f)] public float minPitch = 5f;
        [Range(1f, 89.9f)] public float maxPitch = 89f;
        [Tooltip("Height of the ground plane used for focus, pan and zoom anchors.")]
        public float groundHeight = 0f;
        [Tooltip("Also anchor pan / zoom on collider hits (otherwise only on the ground plane).")]
        public bool anchorOnColliders = true;
        public LayerMask anchorMask = ~0;

        [Header("Smoothing")]
        [Tooltip("Critically damped smoothing of orbit / zoom / key moves (s, unscaled time); 0 = none. Grab panning is never smoothed.")]
        public float smoothTime = 0.07f;
        [Tooltip("Duration of a transition to a preset view / frame / reset (s, unscaled time).")]
        public float transitionTime = 0.6f;
        [Tooltip("Longest frame time used for key moves and smoothing (s); avoids jumps after a hitch.")]
        public float maxFrameTime = 0.1f;

        [Header("Mouse")]
        [Tooltip("Left drag on the scene pans (FlexSim style); a left click without drag still selects.")]
        public bool leftDragPans = true;
        [Tooltip("Pixels the mouse must move with a button down before it counts as a drag (and not a click).")]
        public float dragThreshold = 5f;

        [Header("Keys")]
        [Tooltip("Key that returns to the initial view (applications that use R for something else rebind it).")]
        public KeyCode resetKey = KeyCode.R;
        [Tooltip("Keys 1–9 go to the preset views.")]
        public bool presetKeys = true;

        [Header("Picking")]
        [Tooltip("Renderers larger than this (world units) are ignored when picking by bounds, e.g. floors.")]
        public float maxPickSize = 90f;

        [Header("Views (keys 1–9; the first is also the reset view if none is captured)")]
        public List<CameraPreset> presets = new List<CameraPreset>();

        /// <summary>Currently selected GameObject (left click), or null.</summary>
        public GameObject Selected { get; private set; }
        public event System.Action<GameObject> SelectionChanged;

        /// <summary>Ground point at the screen centre (orbit centre) of the displayed view.</summary>
        public Vector3 Pivot { get { EnsureInit(); return cur.focus; } }
        /// <summary>Camera height above the ground plane (displayed view).</summary>
        public float CameraHeight { get { EnsureInit(); return PositionOf(cur).y - groundHeight; } }
        /// <summary>True while a pan drag is active.</summary>
        public bool Panning => panning;
        /// <summary>True while moving to a preset / frame / reset view.</summary>
        public bool Transitioning => transitioning;

        private struct Pose { public Vector3 focus; public float distance, pitch, yaw; }

        private Pose cur, tgt;
        private Vector3 vFocus;
        private float vDist, vPitch, vYaw;
        private bool inited, transitioning;
        private CameraPreset initial;
        private Camera cam;

        // mouse state
        private Vector3 lastMouse, leftDownPos;
        private bool orbiting, leftCandidate, leftDragged, panning;
        private int panButton = -1;
        private Vector3 panAnchor;

        private void Awake() => EnsureInit();

        private void EnsureInit()
        {
            if (inited) return;
            inited = true;
            cam = GetComponent<Camera>();
            var e = transform.eulerAngles;
            float pitch = Mathf.Clamp(Mathf.DeltaAngle(0, e.x), minPitch, maxPitch);
            cur = tgt = FromCamera(transform.position, pitch, e.y);
            initial = ToPreset("Initial", cur);
            ApplyTransform();
        }

        /// <summary>Re-reads the pose from the transform (after moving the camera by script) and stops any motion.</summary>
        public void SyncFromTransform()
        {
            inited = false;
            EnsureInit();
            vFocus = Vector3.zero; vDist = vPitch = vYaw = 0;
            transitioning = false;
        }

        // ── Update: Input Manager → public operations ─────────────────────────────────────────

        private void Update()
        {
            EnsureInit();
            float dt = Mathf.Min(Time.unscaledDeltaTime, maxFrameTime);
            Vector3 mouse = Input.mousePosition;
            bool overHud = SimRuntimeHud.IsPointerOverHud(mouse);
            bool guiHasMouse = GUIUtility.hotControl != 0;
            bool fast = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            Vector3 mDelta = mouse - lastMouse;
            lastMouse = mouse;

            // Orbit (right drag); a drag that starts over a panel is ignored.
            if (Input.GetMouseButtonDown(1)) orbiting = !overHud;
            if (!Input.GetMouseButton(1)) orbiting = false;
            if (orbiting && !Input.GetMouseButtonDown(1) && mDelta.sqrMagnitude > 0 && !guiHasMouse) Orbit(mDelta);

            // Pan (middle drag).
            if (Input.GetMouseButtonDown(2) && !overHud && !panning && BeginPan(mouse)) panButton = 2;

            // Left: click = select, drag = pan.
            if (Input.GetMouseButtonDown(0)) { leftDownPos = mouse; leftCandidate = !overHud; leftDragged = false; }
            if (Input.GetMouseButton(0) && leftCandidate && !leftDragged
                && (mouse - leftDownPos).sqrMagnitude > dragThreshold * dragThreshold)
            {
                leftDragged = true;   // a drag never selects
                if (leftDragPans && !guiHasMouse && !panning && BeginPan(leftDownPos)) panButton = 0;
            }
            if (Input.GetMouseButtonUp(0))
            {
                if (leftCandidate && !leftDragged && !overHud && !guiHasMouse) Select(Pick(mouse));
                leftCandidate = false;
            }
            if (panning && panButton >= 0 && !Input.GetMouseButton(panButton)) EndPan();

            // Zoom towards the cursor.
            float wheel = Input.mouseScrollDelta.y;
            if (!overHud && Mathf.Abs(wheel) > 0.01f) ZoomAt(mouse, wheel, fast);

            // Keys
            {
                Vector2 planar = Vector2.zero;
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) planar.y += 1;
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) planar.y -= 1;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) planar.x += 1;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) planar.x -= 1;
                float vertical = (Input.GetKey(KeyCode.E) ? 1 : 0) - (Input.GetKey(KeyCode.Q) ? 1 : 0);
                if (planar != Vector2.zero || vertical != 0) MoveKeys(planar, vertical, fast, dt);

                if (Input.GetKeyDown(KeyCode.F))
                {
                    var target = Selected != null ? Selected : Pick(mouse);
                    if (target != null) Frame(target);
                }
                if (resetKey != KeyCode.None && Input.GetKeyDown(resetKey)) GoTo(initial);
                for (int i = 0; presetKeys && i < 9 && i < presets.Count; i++)
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i)) GoTo(presets[i]);
            }

            Step(dt);
            if (panning) DragPan(mouse);
        }

        // ── Operations (also usable from scripts / tests) ─────────────────────────────────────

        /// <summary>Orbits around the screen-centre ground point: <paramref name="pixelDelta"/> × <see cref="orbitSpeed"/> degrees.</summary>
        public void Orbit(Vector2 pixelDelta)
        {
            EnsureInit();
            StopTransition();
            tgt.yaw += pixelDelta.x * orbitSpeed;
            tgt.pitch = Mathf.Clamp(tgt.pitch - pixelDelta.y * orbitSpeed, MinPitchFor(tgt.distance), maxPitch);
        }

        /// <summary>
        /// Zooms <paramref name="notches"/> wheel notches (positive = in) towards the point under <paramref name="screenPos"/>:
        /// each notch scales the distance to that point by (1 − <see cref="zoomStep"/>), softened near the floor / the point.
        /// </summary>
        public void ZoomAt(Vector2 screenPos, float notches, bool fast = false)
        {
            EnsureInit();
            if (Mathf.Abs(notches) < 1e-4f) return;
            StopTransition();
            Vector3 p = PositionOf(tgt);
            if (!Anchor(screenPos, out Vector3 g)) g = tgt.focus;
            float step = Mathf.Clamp(zoomStep * (fast ? 2f : 1f), 0.01f, 0.9f);
            float f = ZoomFactor(p, g, Mathf.Pow(1f - step, notches), notches > 0);
            tgt = FromCamera(g + (p - g) * f, tgt.pitch, tgt.yaw);
        }

        /// <summary>Starts a grab pan at <paramref name="screenPos"/>; false if nothing can be grabbed there.</summary>
        public bool BeginPan(Vector2 screenPos)
        {
            EnsureInit();
            StopTransition();
            if (!Anchor(screenPos, out panAnchor))
            {
                // Above the horizon: grab a horizontal plane at the focus depth along the ray.
                var ray = ViewRay(screenPos);
                panAnchor = ray.GetPoint(Mathf.Min(cur.distance, maxDistance));
            }
            panning = true;
            return true;
        }

        /// <summary>Moves the view so the grabbed point is exactly under <paramref name="screenPos"/> (no smoothing).</summary>
        public void DragPan(Vector2 screenPos)
        {
            if (!panning) return;
            var ray = ViewRay(screenPos);
            if (Mathf.Abs(ray.direction.y) < 1e-5f) return;
            float t = (panAnchor.y - ray.origin.y) / ray.direction.y;
            if (t <= 0f || t > 4f * maxDistance) return;   // cursor beyond the horizon: hold
            Vector3 shift = panAnchor - ray.GetPoint(t);
            shift.y = 0;
            cur.focus += shift;
            tgt.focus += shift;
            ApplyTransform();
        }

        public void EndPan() { panning = false; panButton = -1; }

        /// <summary>
        /// Key move for <paramref name="dt"/> seconds: <paramref name="planar"/> (x = right, y = forward) on the ground,
        /// <paramref name="vertical"/> up (+) / down (−), at speeds proportional to the camera height.
        /// </summary>
        public void MoveKeys(Vector2 planar, float vertical, bool fast, float dt)
        {
            EnsureInit();
            StopTransition();
            float h = PositionOf(tgt).y - groundHeight;
            if (planar != Vector2.zero)
            {
                var yawRot = Quaternion.Euler(0, tgt.yaw, 0);
                Vector3 dir = yawRot * new Vector3(planar.x, 0, planar.y);
                tgt.focus += dir.normalized * KeySpeed(h, panSpeed, fast) * dt;
            }
            if (vertical != 0)
            {
                Vector3 p = PositionOf(tgt);
                p.y = Mathf.Clamp(p.y + Mathf.Sign(vertical) * KeySpeed(h, verticalSpeed, fast) * dt,
                                  groundHeight + minHeight, groundHeight + maxDistance);
                tgt = FromCamera(p, tgt.pitch, tgt.yaw);
            }
        }

        /// <summary>W A S D ground speed (m/s) at the current camera height.</summary>
        public float KeySpeed(bool fast = false) { EnsureInit(); return KeySpeed(PositionOf(tgt).y - groundHeight, panSpeed, fast); }

        private float KeySpeed(float height, float fraction, bool fast)
            => Mathf.Clamp(fraction * Mathf.Max(height, 0f), minKeySpeed, maxKeySpeed) * (fast ? fastMultiplier : 1f);

        /// <summary>Advances the smoothing by <paramref name="dt"/> seconds (unscaled) and updates the transform.</summary>
        public void Step(float dt)
        {
            EnsureInit();
            float st = transitioning ? Mathf.Max(0.01f, transitionTime * 0.25f) : smoothTime;
            if (st <= 0f) { cur = tgt; vFocus = Vector3.zero; vDist = vPitch = vYaw = 0; }
            else if (dt > 0f)
            {
                cur.focus = Vector3.SmoothDamp(cur.focus, tgt.focus, ref vFocus, st, Mathf.Infinity, dt);
                cur.distance = Mathf.SmoothDamp(cur.distance, tgt.distance, ref vDist, st, Mathf.Infinity, dt);
                cur.pitch = Mathf.SmoothDamp(cur.pitch, tgt.pitch, ref vPitch, st, Mathf.Infinity, dt);
                cur.yaw = Mathf.SmoothDamp(cur.yaw, tgt.yaw, ref vYaw, st, Mathf.Infinity, dt);
            }
            if (transitioning && (cur.focus - tgt.focus).sqrMagnitude < 1e-4f && Mathf.Abs(cur.distance - tgt.distance) < 0.01f
                && Mathf.Abs(cur.pitch - tgt.pitch) < 0.01f && Mathf.Abs(cur.yaw - tgt.yaw) < 0.01f)
                transitioning = false;
            ApplyTransform();
        }

        /// <summary>Jumps to the target pose (no smoothing).</summary>
        public void SnapToTarget()
        {
            EnsureInit();
            cur = tgt; vFocus = Vector3.zero; vDist = vPitch = vYaw = 0;
            transitioning = false;
            ApplyTransform();
        }

        /// <summary>Smoothly moves to a preset view (the camera sees exactly what the preset describes).</summary>
        public void GoTo(CameraPreset p)
        {
            if (p == null) return;
            EnsureInit();
            EndPan();
            float pitch = Mathf.Clamp(p.pitch, minPitch, maxPitch);
            var rot = Quaternion.Euler(pitch, p.yaw, 0);
            var next = FromCamera(p.pivot - rot * Vector3.forward * Mathf.Max(p.distance, 0.1f), pitch, p.yaw);
            next.yaw = tgt.yaw + Mathf.DeltaAngle(tgt.yaw, p.yaw);   // shortest turn
            tgt = next;
            transitioning = true;
        }

        public void GoTo(int presetIndex)
        {
            if (presetIndex >= 0 && presetIndex < presets.Count) GoTo(presets[presetIndex]);
        }

        public void ResetView() { EnsureInit(); GoTo(initial); }

        /// <summary>Frames the bounds of a GameObject (and its children) keeping the current angles.</summary>
        public void Frame(GameObject target)
        {
            EnsureInit();
            var rends = target.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) { GoTo(new CameraPreset("Frame", target.transform.position, 15f, tgt.pitch, tgt.yaw)); return; }
            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            float radius = Mathf.Max(b.extents.magnitude, 1f);
            float dist = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.2f;
            GoTo(new CameraPreset("Frame", b.center, dist, tgt.pitch, tgt.yaw));
        }

        public void Select(GameObject go)
        {
            if (go == Selected) return;
            Selected = go;
            SelectionChanged?.Invoke(go);
        }

        /// <summary>Object under a screen point, preferring simulation elements (see <see cref="PickObject"/>).</summary>
        public GameObject Pick(Vector3 screenPos)
        {
            EnsureInit();
            return PickObject(cam, screenPos, maxPickSize);
        }

        /// <summary>
        /// Object under a screen point, <b>preferring simulation elements</b>: the nearest collider hit that belongs
        /// to an element; else the nearest element renderer whose bounds the ray crosses (prefabs need no
        /// colliders); else the nearest collider hit; else the nearest renderer. A floor or wall with a collider
        /// therefore never hides the elements in front of or on it. Renderers larger than
        /// <paramref name="maxPickSize"/> (floors) are skipped in the bounds search.
        /// </summary>
        public static GameObject PickObject(Camera camera, Vector3 screenPos, float maxPickSize = 90f)
        {
            if (camera == null) return null;
            var ray = camera.ScreenPointToRay(screenPos);
            RaycastHit[] hits = Physics.RaycastAll(ray, camera.farClipPlane);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
                if (IsElementObject(h.collider.gameObject)) return h.collider.gameObject;

            GameObject bestElement = null, best = null;
            float bestElementD = float.MaxValue, bestD = float.MaxValue;
            foreach (var r in UnityCompat.FindAll<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || r is TrailRenderer || r is LineRenderer) continue;
                var b = r.bounds;
                if (b.size.x > maxPickSize || b.size.z > maxPickSize) continue;
                if (!b.IntersectRay(ray, out float d)) continue;
                if (d < bestD) { bestD = d; best = r.gameObject; }
                if (d < bestElementD && IsElementObject(r.gameObject)) { bestElementD = d; bestElement = r.gameObject; }
            }
            if (bestElement != null) return bestElement;
            return hits.Length > 0 ? hits[0].collider.gameObject : best;
        }

        private static bool IsElementObject(GameObject go) => SimRuntimeHud.FindElement(go, out _) != null;

        // ── Pose helpers ──────────────────────────────────────────────────────────────────────

        private static Quaternion RotationOf(in Pose p) => Quaternion.Euler(p.pitch, p.yaw, 0);
        private static Vector3 PositionOf(in Pose p) => p.focus - RotationOf(p) * Vector3.forward * p.distance;

        /// <summary>Pose of a camera at <paramref name="pos"/>: focus = where its view axis meets the ground plane.</summary>
        private Pose FromCamera(Vector3 pos, float pitch, float yaw)
        {
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
            pos.y = Mathf.Max(pos.y, groundHeight + minHeight);
            Vector3 fwd = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;   // fwd.y = −sin(pitch) < 0
            float d = (pos.y - groundHeight) / -fwd.y;
            return new Pose { focus = pos + fwd * d, distance = d, pitch = pitch, yaw = yaw };
        }

        private CameraPreset ToPreset(string name, in Pose p) => new CameraPreset(name, p.focus, p.distance, p.pitch, p.yaw);

        /// <summary>Lowest pitch that keeps a camera at this focus distance above <see cref="minHeight"/>.</summary>
        private float MinPitchFor(float distance)
            => Mathf.Max(minPitch, Mathf.Asin(Mathf.Clamp01(minHeight / Mathf.Max(distance, 1e-3f))) * Mathf.Rad2Deg);

        /// <summary>
        /// Scale of the vector anchor→camera for one zoom: the nominal factor <paramref name="q"/>, softened so the distance
        /// to the anchor approaches <see cref="minDistance"/> and the height approaches <see cref="minHeight"/>
        /// geometrically (never crossing them), and capped at <see cref="maxDistance"/>.
        /// </summary>
        private float ZoomFactor(Vector3 p, Vector3 g, float q, bool zoomIn)
        {
            float D = Mathf.Max((p - g).magnitude, 1e-3f);
            float fD = (minDistance + (D - minDistance) * q) / D;
            float h = p.y - groundHeight, gy = g.y - groundHeight;
            bool heightApplies = h - gy > 1e-3f;
            float fH = heightApplies ? (minHeight + (h - minHeight) * q - gy) / (h - gy) : q;
            if (zoomIn) return Mathf.Clamp(Mathf.Max(fD, fH), 0f, 1f);
            float f = Mathf.Max(1f, Mathf.Min(fD, fH));
            return Mathf.Max(1f, Mathf.Min(f, maxDistance / D));
        }

        /// <summary>
        /// View ray through a screen point, from the camera position. Built in camera space: Camera.ScreenPointToRay
        /// unprojects a near-plane point, which loses precision far from the world origin (≈ 0.5 px at 900 m) and would
        /// make the grabbed point creep under the cursor. Orthographic / physical cameras use ScreenPointToRay.
        /// </summary>
        public Ray ViewRay(Vector2 screenPos)
        {
            EnsureInit();
            if (cam.orthographic || cam.usePhysicalProperties) return cam.ScreenPointToRay(screenPos);
            Rect pr = cam.pixelRect;
            float x = (screenPos.x - pr.x) / Mathf.Max(pr.width, 1f) * 2f - 1f;
            float y = (screenPos.y - pr.y) / Mathf.Max(pr.height, 1f) * 2f - 1f;
            float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var local = new Vector3(x * tanV * cam.aspect, y * tanV, 1f);
            return new Ray(transform.position, transform.rotation * local.normalized);
        }

        /// <summary>World point under a screen position: collider hit (optional), else the ground plane.</summary>
        private bool Anchor(Vector2 screenPos, out Vector3 point)
        {
            var ray = ViewRay(screenPos);
            if (anchorOnColliders && Physics.Raycast(ray, out RaycastHit hit, 2f * maxDistance, anchorMask, QueryTriggerInteraction.Ignore))
            { point = hit.point; return true; }
            if (ray.direction.y < -1e-4f)
            {
                float t = (groundHeight - ray.origin.y) / ray.direction.y;
                if (t > 0f && t <= 2f * maxDistance) { point = ray.GetPoint(t); return true; }
            }
            point = Vector3.zero;
            return false;
        }

        private void StopTransition()
        {
            if (!transitioning) return;
            transitioning = false;
            tgt = cur;   // the user takes over where the camera is now
            vFocus = Vector3.zero; vDist = vPitch = vYaw = 0;
        }

        private void ApplyTransform()
        {
            var rot = RotationOf(cur);
            transform.SetPositionAndRotation(cur.focus - rot * Vector3.forward * cur.distance, rot);
        }
    }
}
