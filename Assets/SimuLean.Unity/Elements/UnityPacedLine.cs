using System;
using System.Collections.Generic;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>One fitting station of a <see cref="UnityPacedLine"/> (engine: <see cref="LineStation"/>).</summary>
    [Serializable]
    public class LineStationDef
    {
        [Tooltip("Station name (its port: <line>.<name>).")]
        public string name = "Station";
        [Tooltip("Line position (0-based pitch, 1 … positions−1) where the parts are fitted.")]
        [Min(1)] public int position = 1;
        [Tooltip("Label the part must share with the vehicle (e.g. vehicle); empty = no check. Mismatches are counted.")]
        public string matchLabel = "";
        [Tooltip("Parts held at line side (≥ the quantity).")]
        [Min(1)] public int portCapacity = 2;
        [Tooltip("Elements feeding parts to this station (leave their own Next Elements empty — the line wires them).")]
        public List<SElement> feeders = new List<SElement>();
        [Tooltip("What is fitted: quantity, condition, reference labels (the same family as the call-off point upstream; " +
                 "empty name = the station name). Its destination is used only with Call Off At Line.")]
        public CallOffFamilyDef family = new CallOffFamilyDef { name = "" };
        [Tooltip("The line itself calls off this family when a vehicle enters Call-Off Position (usually done upstream by a Call-Off Point).")]
        public bool callOffAtLine = false;
        [Tooltip("Line position where the line calls off (0 = when the vehicle enters).")]
        [Min(0)] public int callOffPosition = 0;

        [Tooltip("View: node of the vehicle's view (searched by name, e.g. ConsoleSocket) where the fitted parts are shown; " +
                 "empty = they stay where they were, children of the vehicle.")]
        public string vehicleSocket = "";
        [Tooltip("View: model shown at the socket for the fitted parts (once per vehicle) instead of the parts' own views " +
                 "(e.g. the component as fitted); empty = the parts' own views, placed at the socket.")]
        public GameObject fittedPrefab;

        public LineStationSettings ToSettings() => new LineStationSettings
        {
            Name = name ?? "",
            Position = position,
            MatchLabel = matchLabel ?? "",
            PortCapacity = Mathf.Max(1, portCapacity),
            CallOffPosition = callOffAtLine ? Mathf.Max(0, callOffPosition) : -1,
            Family = (family ?? new CallOffFamilyDef { name = "" }).ToSettings(name),
        };
    }

    /// <summary>
    /// Unity wrapper of <see cref="PacedLine"/>: an indexing assembly line of <see cref="positions"/> pitches,
    /// <see cref="pitchLength"/> apart — a straight row along the local +X axis (the model frame: the flow along
    /// +X, the line side on its right, −Z, the front), or along a <see cref="path"/>
    /// of waypoints with rounded corners (<see cref="cornerRadius"/>), e.g. a serpentine in a hall; stations are inline
    /// definitions (<see cref="LineStationDef"/>) by position index, fed by their <c>feeders</c>. Built by
    /// <c>PacedLineHandler</c>. View (read-only): vehicles move pitch by pitch along the line, facing along it,
    /// smoothly during the first <see cref="indexMotionFraction"/> of each takt after an index; fitted parts become children
    /// of their vehicle — at the station's <see cref="LineStationDef.vehicleSocket"/> node, shown as its
    /// <see cref="LineStationDef.fittedPrefab"/> when set (<see cref="ShowFitted"/>); parts at line side stand next to their
    /// station (<see cref="partsSideOffset"/> to the right of the line: a straight row's −Z). Vehicles keep the vehicle frame
    /// (their +Z along the travel).
    /// </summary>
    [AddComponentMenu("SimuLean/Paced Line")]
    [SimuLeanElement(SimuLeanModules.Assembly, "Paced Line", Description = "Indexing assembly line: takt, stations fed at line side, starvation", Order = 22, Pack = SimuLeanPacks.Manufacturing, Icon = "Assets/SimuLean.Unity/Art/Models/AssemblyLineKit/SlatConveyor_6m.fbx")]
    public class UnityPacedLine : SElement, IVElement
    {
        [Header("Line")]
        [Tooltip("Number of positions (pitches).")]
        [Min(2)] public int positions = 5;
        [Tooltip("Time between indexes.")]
        public RandomProcessConfig takt = new RandomProcessConfig { constantValue = 2.0 };
        [Tooltip("Label with the entry number given to vehicles without one (empty = none).")]
        public string sequenceLabel = "vehicle";
        [Tooltip("Fitted parts become sub-items of the vehicle (off = consumed and dropped).")]
        public bool componentsToVehicle = true;

        [Header("Stations")]
        public List<LineStationDef> stations = new List<LineStationDef>();

        [Header("View (local frame)")]
        [Tooltip("Distance between pitches (along +X, or along the path).")]
        [Min(0.01f)] public float pitchLength = 5f;
        [Tooltip("Optional path: waypoints in order (two or more). Pitch i stands i × Pitch Length along the line through them " +
                 "(vehicles face along it); empty = a straight row along local +X from this object.")]
        public List<Transform> path = new List<Transform>();
        [Tooltip("Radius of the path's rounded corners (0 = sharp corners).")]
        [Min(0f)] public float cornerRadius = 0f;
        [Tooltip("Height of the vehicles above the line origin (above the path).")]
        public float vehicleHeight = 0f;
        [Tooltip("Share of the takt the index motion takes in the view (0 = jump).")]
        [Range(0f, 1f)] public float indexMotionFraction = 0.25f;
        [Tooltip("Side offset (to the right of the line: a straight row's −Z, its front) of the parts waiting at line side.")]
        public float partsSideOffset = 2.5f;
        [Tooltip("Spacing of the waiting parts.")]
        public float partSpacing = 0.5f;

        private PacedLine line;
        // vehicles moving along the line (arc length from / to, start time); shown = where each vehicle stands
        private readonly Dictionary<Item, (float from, float to, double start)> moving = new Dictionary<Item, (float, float, double)>();
        private readonly Dictionary<Item, float> shown = new Dictionary<Item, float>();
        private double lastIndex = double.NaN, lastTakt = 1;
        private PolylinePath pathCache;

        /// <summary>The engine element of the run (null outside a run).</summary>
        public PacedLine Line => line;

        /// <summary>Writes its settings into the element's (the extractor's).</summary>
        public void Fill(PacedLineSettings s)
        {
            s.Positions = Mathf.Max(2, positions);
            s.Takt = takt != null ? takt.ToSettings() : SimuLean.Serialization.SamplerSettings.Constant(1);
            s.SequenceLabel = sequenceLabel ?? "";
            s.ComponentsToVehicle = componentsToVehicle;
            s.Stations = new List<LineStationSettings>();
            foreach (var st in stations ?? new List<LineStationDef>()) if (st != null) s.Stations.Add(st.ToSettings());
        }

        // ── geometry ─────────────────────────────────────────────────────────

        /// <summary>The line's path (world), null for a straight row. Rebuilt outside Play mode (the waypoints may move).</summary>
        public PolylinePath Path
        {
            get
            {
                if (path == null || path.Count < 2) return null;
                if (pathCache != null && Application.isPlaying) return pathCache;
                var pts = new List<Vector3>();
                foreach (var t in path) if (t != null) pts.Add(t.position);
                pathCache = pts.Count >= 2 ? PolylinePath.Build(pts, cornerRadius) : null;
                return pathCache;
            }
        }

        /// <summary>World pose <paramref name="s"/> metres along the line (vehicle height included): the position, and the
        /// rotation facing along the line (the vehicle frame, +Z along the travel; a straight row runs along this object's
        /// +X, the model frame).</summary>
        public Vector3 PoseAt(float s, out Quaternion rotation)
        {
            var p = Path;
            if (p == null)
            {
                rotation = transform.rotation * AlongX;
                return transform.TransformPoint(new Vector3(s, vehicleHeight, 0f));
            }
            Vector3 pos = p.Sample(s, out Vector3 dir);
            dir.y = 0f;
            rotation = dir.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(dir.normalized, Vector3.up) : transform.rotation * AlongX;
            return pos + Vector3.up * vehicleHeight;
        }

        // a vehicle's +Z (its travel) along the element's +X
        private static readonly Quaternion AlongX = Quaternion.Euler(0f, 90f, 0f);

        /// <summary>World pose of pitch <paramref name="i"/>.</summary>
        public Vector3 PitchPose(int i, out Quaternion rotation) => PoseAt(i * pitchLength, out rotation);

        /// <summary>World position of pitch <paramref name="i"/>.</summary>
        public Vector3 PitchWorld(int i) => PitchPose(i, out _);

        // ── run view ─────────────────────────────────────────────────────────

        protected override void OnBound(Element built)
        {
            line = (PacedLine)built;
            moving.Clear();
            shown.Clear();
            pathCache = null;
            lastIndex = double.NaN;
            line.VehicleEntered += (l, v) => Place(v, 0);
            line.VehicleLeft += (l, v) => { moving.Remove(v); shown.Remove(v); };
            line.Indexed += OnIndexed;
            line.PartsFitted += (l, st, v, parts) =>
            {
                if (!(v.vItem is GameObject vgo) || vgo == null) { foreach (var p in parts) DestroyGo(p); return; }
                var def = stations?.Find(x => x != null && x.name == st.Name);
                var views = new List<GameObject>();
                foreach (var p in parts)
                    if (p.vItem is GameObject pgo && pgo != null) views.Add(pgo);
                ShowFitted(vgo, def, views, componentsToVehicle);
                if (def != null && def.fittedPrefab != null) foreach (var p in parts) p.vItem = null;
                LayoutParts(st);
            };
            foreach (var st in line.Stations)
            {
                var s = st;
                st.Port.ItemEntered += (e, part) => LayoutParts(s);
            }
        }

        /// <summary>
        /// Shows parts fitted to a vehicle's view. With the station's <see cref="LineStationDef.fittedPrefab"/> the parts'
        /// views are removed and the prefab is shown once per station and vehicle (named <c>Fitted_&lt;station&gt;</c>) at the
        /// <see cref="LineStationDef.vehicleSocket"/> node (else at the vehicle's root); otherwise the parts' views become
        /// children of the socket, at its origin, or — without a socket — of the vehicle where they are
        /// (<paramref name="keep"/> false = removed). Returns the fitted view, if any. Read-only for the model.
        /// </summary>
        public static GameObject ShowFitted(GameObject vehicle, LineStationDef def, IList<GameObject> partViews, bool keep = true)
        {
            Transform socket = def != null && !string.IsNullOrEmpty(def.vehicleSocket) ? ModularKit.FindDeep(vehicle.transform, def.vehicleSocket) : null;
            if (def != null && def.fittedPrefab != null)
            {
                foreach (var p in partViews) if (p != null) DestroyView(p);
                Transform parent = socket != null ? socket : vehicle.transform;
                string key = "Fitted_" + def.name;
                var existing = parent.Find(key);
                if (existing != null) return existing.gameObject;
                var f = Instantiate(def.fittedPrefab, parent, false);
                f.name = key;
                f.SetActive(true);
                return f;
            }
            foreach (var p in partViews)
            {
                if (p == null) continue;
                if (!keep) { DestroyView(p); continue; }
                if (socket != null)
                {
                    p.transform.SetParent(socket, false);
                    p.transform.localPosition = Vector3.zero;
                    p.transform.localRotation = Quaternion.identity;
                }
                else p.transform.SetParent(vehicle.transform, true);
            }
            return null;
        }

        private static void DestroyView(GameObject go)
        {
            if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
        }

        private void OnIndexed(PacedLine l)
        {
            double now = l.GetSimClock().GetSimulationTime();
            if (!double.IsNaN(lastIndex) && now > lastIndex) lastTakt = now - lastIndex;
            lastIndex = now;
            double motion = indexMotionFraction * lastTakt;
            for (int i = 0; i < l.Positions; i++)
            {
                Item v = l.VehicleAt(i);
                if (!(v?.vItem is GameObject go) || go == null) continue;
                // from where the vehicle is drawn now (several indexes between two frames at high speed: not a stale position)
                float from = moving.TryGetValue(v, out var m) ? ArcAt(m, now, motion)
                           : shown.TryGetValue(v, out float s) ? s : Mathf.Max(0, i - 1) * pitchLength;
                shown[v] = from;
                moving[v] = (from, i * pitchLength, now);
            }
        }

        private static float ArcAt((float from, float to, double start) m, double now, double motion)
        {
            float u = motion <= 0 ? 1f : Mathf.Clamp01((float)((now - m.start) / motion));
            return Mathf.Lerp(m.from, m.to, Mathf.SmoothStep(0f, 1f, u));
        }

        private void Place(Item v, int i)
        {
            if (!(v?.vItem is GameObject go) || go == null) return;
            Vector3 p = PitchPose(i, out Quaternion r);
            go.transform.SetPositionAndRotation(p, r);
            shown[v] = i * pitchLength;
        }

        private void LayoutParts(LineStation st)
        {
            int k = 0;
            Vector3 basePos = PitchPose(st.Position, out Quaternion r);
            Vector3 right = r * Vector3.right, forward = r * Vector3.forward;
            foreach (var p in st.Port.Parts)
            {
                if (p.vItem is GameObject go && go != null)
                    go.transform.SetPositionAndRotation(basePos + right * partsSideOffset + forward * (k * partSpacing), r);
                k++;
            }
        }

        private void Update()
        {
            if (line == null || moving.Count == 0) return;
            var clock = UnitySimClock.Instance;
            if (clock == null) return;
            double now = clock.clock.GetSimulationTime();
            double motion = indexMotionFraction * lastTakt;
            var done = new List<Item>();
            foreach (var kv in moving)
            {
                int i = line.PositionOf(kv.Key);
                if (i < 0 || !(kv.Key.vItem is GameObject go) || go == null) { done.Add(kv.Key); continue; }
                float u = motion <= 0 ? 1f : Mathf.Clamp01((float)((now - kv.Value.start) / motion));
                float s = ArcAt((kv.Value.from, i * pitchLength, kv.Value.start), now, motion);
                Vector3 p = PoseAt(s, out Quaternion r);
                go.transform.SetPositionAndRotation(p, r);
                shown[kv.Key] = s;
                if (u >= 1f) done.Add(kv.Key);
            }
            foreach (var v in done) moving.Remove(v);
        }

        public override void ClearViews()
        {
            if (line == null) return;
            for (int i = 0; i < line.Positions; i++) DestroyGo(line.VehicleAt(i));
            foreach (var st in line.Stations) foreach (var p in st.Port.Parts) DestroyGo(p);
            moving.Clear();
            shown.Clear();
        }

        private static void DestroyGo(Item it)
        {
            if (it?.vItem is GameObject go && go != null) Destroy(go);
        }

        void IVElement.ReportState(string msg) { }
        object IVElement.GenerateItem(int type) => null;
        void IVElement.LoadItem(Item item) { }
        void IVElement.UnloadItem(Item item) { }

        private void OnDrawGizmos()
        {
            var m = Gizmos.matrix;
            Gizmos.color = new Color(0.9f, 0.9f, 0.2f, 0.9f);
            for (int i = 0; i < Mathf.Max(2, positions); i++)
            {
                Gizmos.matrix = Matrix4x4.TRS(PitchPose(i, out Quaternion r), r, Vector3.one);
                Gizmos.DrawWireCube(new Vector3(0f, 0.02f, 0f), new Vector3(2f, 0.04f, pitchLength * 0.9f));
            }
            Gizmos.color = new Color(1f, 0.4f, 0.2f, 0.9f);
            foreach (var st in stations ?? new List<LineStationDef>())
            {
                if (st == null) continue;
                Gizmos.matrix = Matrix4x4.TRS(PitchPose(st.position, out Quaternion r), r, Vector3.one);
                Gizmos.DrawWireSphere(new Vector3(partsSideOffset, 0.3f, 0f), 0.3f);
            }
            Gizmos.matrix = m;
        }
    }
}
