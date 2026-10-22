using System.Collections.Generic;
using UnityEngine;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Transport;

namespace SimuLean.Unity
{
    /// <summary>
    /// Pure view of a resource that moves as one piece — a vehicle (AGV, forklift), an operator walking: animates the
    /// GameObject of one spatial <see cref="TaskExecutor"/> by interpolating along the move the engine is making.
    ///
    /// THE ENGINE ESTIMATES DURATIONS, THIS CLASS ONLY ANIMATES: every frame it reads
    /// <see cref="TaskExecutor.CurrentLeg"/> (route of nodes or path of points + start / end in simulation time), computes
    /// the elapsed fraction from the clock and places the GameObject at that point; between moves it stands at the
    /// executor's location (loading, unloading, waiting). Open-ended moves stay at their start.
    ///
    /// <para><b>Forklift parts</b> (found by name, e.g. the library's <c>Art/Models/Forklift</c>): <c>MastInner</c> /
    /// <c>Carriage</c> lift the forks (each by half the fork height) — low to pick up, raised while carrying, lowered to
    /// put down, within the load / unload times — and the load rides on <c>LoadSocket</c>. <b>Wheels</b>: every node named
    /// <c>Wheel</c> or <c>Wheel_…</c> spins with the distance travelled, its radius read from its meshes (half their
    /// height), so a model with any number of wheel lines (an SPMT) needs no setting. <b>Lift AGV / SPMT parts</b>
    /// (<c>Art/Models/AGV</c>): <c>Lift</c> raises the deck by <see cref="liftStroke"/> while carrying (in step with the
    /// forks' timing).
    /// <b>Operator parts</b> (<c>Art/Models/Operator</c>): <c>Leg_L</c> / <c>Leg_R</c> and <c>Arm_L</c> / <c>Arm_R</c> swing
    /// with the distance walked (one stride every <see cref="strideLength"/> m), the knees (<c>Shin_L</c> / <c>Shin_R</c>,
    /// optional) bend while a foot swings forward, the arms go forward while carrying — with the elbows
    /// (<c>Forearm_L</c> / <c>Forearm_R</c>, optional) bent so the forearms hold the item — and the item rides on
    /// <c>CarrySocket</c>. Joints turn RELATIVE to their rest rotation about the model's lateral axis, so skinned rigs
    /// (bones with rest rotations) and rigid node models (identity rest rotations) animate alike. Nodes are looked up
    /// among the active objects first (a prefab may hold several looks, <see cref="OperatorAppearance"/>).
    /// Without them the carried items ride at <c>carriedItemOffset</c>. Nothing here feeds back into the simulation.</para>
    /// </summary>
    public class UnityVehicleAnimator : MonoBehaviour
    {
        [Tooltip("Fork height while carrying / while empty (m), forklift models.")]
        public float carryLift = 0.3f, emptyLift = 0.1f;
        [Tooltip("Turn added to the travel direction (degrees), for models that do not face +Z.")]
        public float yawOffset;
        [Tooltip("Lift AGV models: the top plate's stroke (m), raised while carrying.")]
        public float liftStroke = 0.06f;
        [Tooltip("Operator models: distance of one stride (m) and the swing of the legs (degrees).")]
        public float strideLength = 1.4f, legSwing = 28f;
        [Tooltip("Operator models with knees / elbows: knee bend while a foot swings forward (degrees).")]
        public float kneeBend = 35f;
        [Tooltip("Radius of a wheel without a mesh to measure (m); wheels with meshes use their own size.")]
        [Min(0.01f)] public float wheelRadius = 0.10f;

        /// <summary>The enabled animators (views read it every frame instead of searching the scene; review2 #22).</summary>
        public static IReadOnlyList<UnityVehicleAnimator> Active => active;
        private static readonly List<UnityVehicleAnimator> active = new List<UnityVehicleAnimator>();
        private void OnEnable() => active.Add(this);
        private void OnDisable() => active.Remove(this);

        private TaskExecutor executor;
        private TransportNetwork network;
        private Vector3 carriedItemOffset;

        // forklift parts (optional)
        private Transform mastInner, carriage, loadSocket, liftPlate;
        private Vector3 mastInnerRest, carriageRest, liftPlateRest;
        private readonly List<(Transform t, float r)> wheels = new List<(Transform, float)>();
        private Joint legL, legR, shinL, shinR, armL, armR, foreL, foreR;
        private float walkPhase, walkAmount;
        private Vector3 lastPosition;

        // cached per leg: the polyline and its cumulative lengths
        private TravelLeg cachedLeg;
        private Vector3[] routePoints;
        private float[] cumulativeLengths;
        private float totalLength;

        public TaskExecutor Executor => executor;

        /// <summary>Swing the operator model's legs and arms procedurally (default). A <see cref="PersonAnimator"/> playing
        /// clips turns it off; the procedural swing stays the fallback for models without clips.</summary>
        public bool ProceduralLimbs { get; set; } = true;

        /// <summary>Binds this view to its executor and the network its locations are points of.</summary>
        public void Bind(TaskExecutor executor, TransportNetwork network, Vector3 carriedItemOffset)
        {
            this.executor = executor;
            this.network = network;
            this.carriedItemOffset = carriedItemOffset;
            mastInner = FindDeep(transform, "MastInner"); carriage = FindDeep(transform, "Carriage"); loadSocket = FindDeep(transform, "LoadSocket") ?? FindDeep(transform, "CarrySocket");
            legL = Joint.Of(FindDeep(transform, "Leg_L"), transform); legR = Joint.Of(FindDeep(transform, "Leg_R"), transform);
            shinL = Joint.Of(FindDeep(transform, "Shin_L"), transform); shinR = Joint.Of(FindDeep(transform, "Shin_R"), transform);
            armL = Joint.Of(FindDeep(transform, "Arm_L"), transform); armR = Joint.Of(FindDeep(transform, "Arm_R"), transform);
            foreL = Joint.Of(FindDeep(transform, "Forearm_L"), transform); foreR = Joint.Of(FindDeep(transform, "Forearm_R"), transform);
            if (mastInner != null) mastInnerRest = mastInner.localPosition;
            if (carriage != null) carriageRest = carriage.localPosition;
            liftPlate = FindDeep(transform, "Lift");
            if (liftPlate != null) liftPlateRest = liftPlate.localPosition;
            wheels.Clear();
            wheels.AddRange(FindWheels(transform, wheelRadius));
            if (TryNode(executor.Location ?? executor.HomeNode, out Vector3 p)) transform.position = p;
            lastPosition = transform.position;
            Lift(emptyLift);
        }

        // A person at a station — working there, loading or unloading its items — stands at its staff position (StaffSocket
        // / OperatorSocket, else in front of it — StationModel.StaffPosition), not at its location node, which may be inside
        // the machine (a view, the model's times are unchanged). The walks to and from it are drawn to and from
        // that position: a machine's replaces the node; a box (an EntrySocket: its node is at the door) adds the way in from
        // the door, so nobody walks through its walls.
        private Vector3 workOffset;
        /// <summary>True while a person steps to (or from) its work position (its walk is shown).</summary>
        public bool Stepping { get; private set; }
        private PersonAnimator personView;
        private bool personLooked;

        private bool IsPerson
        {
            get
            {
                if (!personLooked) { personView = GetComponentInChildren<PersonAnimator>(true); personLooked = true; }
                return personView != null;
            }
        }

        // the stations by their location node (built once per run)
        private static Dictionary<string, SElement> stationAt;
        private static SimClock stationsOf;

        private static SElement StationAt(string nodeId)
        {
            var clock = UnitySimClock.Instance != null ? UnitySimClock.Instance.clock : null;
            if (stationAt == null || stationsOf != clock)
            {
                stationAt = new Dictionary<string, SElement>();
                stationsOf = clock;
                // several elements at one node (a buffer before its station): the one that processes
                foreach (var e in UnityCompat.FindAll<SElement>())
                {
                    if (e == null || e.location == null) continue;
                    string id = e.location.NodeId;
                    if (!stationAt.TryGetValue(id, out var had) || (!(had is IProcessResourceUser) && e is IProcessResourceUser)) stationAt[id] = e;
                }
            }
            return nodeId != null && stationAt.TryGetValue(nodeId, out var s) && s != null ? s : null;
        }

        // where a person's walk to / from node nodeId is drawn to / from: the station's staff position; through the node
        // first (throughDoor) when the station has a door
        private bool Entry(string nodeId, out Vector3 p, out bool throughDoor)
        {
            p = default; throughDoor = false;
            var station = IsPerson ? StationAt(nodeId) : null;
            if (station == null || !TryNode(nodeId, out Vector3 node)) return false;
            throughDoor = StationModel.Position(station, StationModel.EntrySocket) != null;
            p = StationModel.StaffPosition(station, out _);
            p.y = node.y;
            return true;
        }

        private Vector3 WorkOffset(TravelLeg leg, Vector3 at)
        {
            Stepping = false;
            if (leg != null || !IsPerson) { workOffset = Vector3.zero; return workOffset; }   // a walk: its route is drawn as is
            var station = StationAt(executor.Location);
            workOffset = Vector3.zero;
            if (station == null) return workOffset;
            Vector3 spot = StationModel.StaffPosition(station, out Vector3 face);
            workOffset = spot - at; workOffset.y = 0f;   // the walk ended there
            Vector3 look = face - spot; look.y = 0f;
            if (look.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
            return workOffset;
        }

        private void Update()
        {
            if (executor == null || UnitySimClock.Instance == null) return;
            double now = UnitySimClock.Instance.clock.GetSimulationTime();

            TravelLeg leg = executor.CurrentLeg;
            Vector3 position = transform.position;
            if (leg == null)
            {
                if (TryNode(executor.Location, out Vector3 at)) position = at;
            }
            else if (leg.IsOpenEnded && leg.SegmentIndex >= 0 && leg.Route != null && leg.SegmentIndex + 1 < leg.Route.Count)
            {
                // traffic: segment by segment, standing at the node while waiting for the next one
                TryNode(leg.Route[leg.SegmentIndex], out Vector3 a);
                TryNode(leg.Route[leg.SegmentIndex + 1], out Vector3 b);
                double d = leg.SegmentEndTime - leg.SegmentStartTime;
                if (!executor.IsWorkPaused) segFraction = d > 0 ? Mathf.Clamp01((float)((now - leg.SegmentStartTime) / d)) : 1f;
                position = Vector3.Lerp(a, b, segFraction);
                Vector3 dir = b - a; dir.y = 0;
                if (dir.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(0, yawOffset, 0);
            }
            else
            {
                if (!ReferenceEquals(leg, cachedLeg)) CacheRoute(leg);
                float t = (float)leg.Fraction(now);
                position = PointAlongRoute(t);
                Vector3 dir = PointAlongRoute(Mathf.Min(1f, t + 0.01f)) - position;
                dir.y = 0;
                if (dir.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(0, yawOffset, 0);
            }
            position += WorkOffset(leg, position);
            transform.position = position;
            SpinWheels(position);

            // Forks and the load (forklift models): low to pick up / put down, raised while carrying.
            var step = executor.CurrentStep;
            float u = StepFraction(now);
            Item riding = null;
            bool released = false;
            float lift = executor.Carried.Count > 0 ? carryLift : emptyLift;
            if (step != null && step.Kind == StepKind.Load && !float.IsNaN(u))
            {
                lift = u < 0.5f ? Mathf.Lerp(emptyLift, 0.02f, u * 2f) : Mathf.Lerp(0.02f, carryLift, (u - 0.5f) * 2f);
                if (u >= 0.5f) riding = step.Item;       // picked up half-way through the load time
            }
            else if (step != null && step.Kind == StepKind.Unload && !float.IsNaN(u))
            {
                lift = u < 0.6f ? Mathf.Lerp(carryLift, 0.02f, u / 0.6f) : Mathf.Lerp(0.02f, emptyLift, (u - 0.6f) / 0.4f);
                released = u >= 0.6f;                    // set down at 60 % of the unload time
            }
            Lift(lift);
            Walk(executor.Carried.Count > 0 || riding != null);

            foreach (Item carried in executor.Carried)
                if (!(released && carried == step.Item)) Ride(carried, position);
            if (riding != null && !Contains(executor.Carried, riding)) Ride(riding, position);
        }

        private float lastU = float.NaN;
        private float segFraction;

        private float StepFraction(double now)
        {
            if (executor.IsWorkPaused) return lastU;      // stopped: the forks freeze too
            double a = executor.StepStartTime, b = executor.StepEndTime;
            if (double.IsNaN(a) || double.IsNaN(b) || b <= a) return lastU = float.NaN;
            return lastU = Mathf.Clamp01((float)((now - a) / (b - a)));
        }

        private void Ride(Item item, Vector3 position)
        {
            if (!(item?.vItem is GameObject go) || go == null) return;
            if (loadSocket != null)
            {
                float pivotAboveBottom = go.transform.position.y - Bottom(go);
                go.transform.position = loadSocket.position + Vector3.up * pivotAboveBottom;
                go.transform.rotation = transform.rotation;
            }
            else go.transform.position = position + carriedItemOffset;
        }

        private void Lift(float h)
        {
            if (mastInner != null) mastInner.localPosition = mastInnerRest + Vector3.up * (h / 2f);
            if (carriage != null) carriage.localPosition = carriageRest + Vector3.up * (h / 2f);
            // the AGV plate: down at the pick-up / set-down height, fully up while carrying
            if (liftPlate != null)
                liftPlate.localPosition = liftPlateRest + Vector3.up * (liftStroke * Mathf.InverseLerp(Mathf.Min(0.02f, carryLift), carryLift, h));
        }

        // legs and arms of an operator model: a stride per strideLength walked, easing to standing when still.
        // Angles about the model's lateral axis: + = backwards for a limb hanging down.
        private void Walk(bool carrying)
        {
            if (!ProceduralLimbs || (legL.t == null && armL.t == null)) return;
            float dt = Time.deltaTime;
            float target = walkStep > 1e-4f ? 1f : 0f;
            walkAmount = Mathf.MoveTowards(walkAmount, target, dt * 4f);
            float s = Mathf.Sin(walkPhase) * legSwing * walkAmount;
            float c = Mathf.Cos(walkPhase) * walkAmount;
            legL.Set(s);
            legR.Set(-s);
            shinL.Set(kneeBend * Mathf.Max(0f, -c));        // the knee bends while that foot swings forward
            shinR.Set(kneeBend * Mathf.Max(0f, c));
            bool elbows = foreL.t != null;
            float arm = carrying ? (elbows ? -35f : -70f) : 0f;
            armL.Set(arm + (carrying ? 0f : -s * 0.8f));
            armR.Set(arm + (carrying ? 0f : s * 0.8f));
            float elbow = carrying ? -55f : -8f - 10f * walkAmount;
            foreL.Set(elbow);
            foreR.Set(elbow);
        }

        /// <summary>A limb joint turned relative to its rest rotation about the model's lateral axis.</summary>
        private struct Joint
        {
            public Transform t;
            private Quaternion rest;
            private Vector3 axis;

            public static Joint Of(Transform t, Transform model) => t == null ? default : new Joint
            {
                t = t, rest = t.localRotation,
                axis = Quaternion.Inverse(t.rotation) * model.right,   // the lateral axis in the joint's own frame
            };

            public void Set(float degrees)
            {
                if (t != null) t.localRotation = rest * Quaternion.AngleAxis(degrees, axis);
            }
        }

        private float walkStep;

        private void SpinWheels(Vector3 position)
        {
            Vector3 d = position - lastPosition; d.y = 0;
            float dist = d.magnitude;
            walkStep = dist;
            if (strideLength > 0) walkPhase = (walkPhase + dist / strideLength * 2f * Mathf.PI) % (2f * Mathf.PI);
            if (dist > 0 && wheels.Count > 0)
            {
                float sign = Vector3.Dot(d, transform.forward) >= 0 ? 1f : -1f;
                foreach (var w in wheels) w.t.Rotate(Vector3.right, sign * dist / w.r * Mathf.Rad2Deg, Space.Self);
            }
            lastPosition = position;
        }

        // ── helpers ──────────────────────────────────────────────────────────

        /// <summary>The model's wheels: every active node named <c>Wheel</c> / <c>Wheel_…</c> (not inside
        /// another wheel; a <c>Wheelchair</c> is not one), with its radius
        /// = half the height of its meshes (the vertical extent of a wheel standing on its axle does not change as it
        /// spins), else <paramref name="fallbackRadius"/>.</summary>
        public static List<(Transform wheel, float radius)> FindWheels(Transform root, float fallbackRadius)
        {
            var found = new List<(Transform, float)>();
            void Walk(Transform t)
            {
                if (!t.gameObject.activeInHierarchy) return;
                if (t != root && (t.name == "Wheel" || t.name.StartsWith("Wheel_", System.StringComparison.Ordinal)))
                {
                    float r = 0;
                    foreach (var rd in t.GetComponentsInChildren<Renderer>()) r = Mathf.Max(r, rd.bounds.extents.y);
                    found.Add((t, r > 0.005f ? r : fallbackRadius));
                    return;
                }
                foreach (Transform c in t) Walk(c);
            }
            Walk(root);
            return found;
        }

        private static bool Contains(IReadOnlyList<Item> list, Item it) { foreach (var x in list) if (x == it) return true; return false; }

        private static float Bottom(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return go.transform.position.y;
            float min = float.MaxValue;
            foreach (var r in rs) min = Mathf.Min(min, r.bounds.min.y);
            return min;
        }

        // the first active match (a prefab may hold several looks), else any
        private static Transform FindDeep(Transform root, string n) => FindDeep(root, n, true) ?? FindDeep(root, n, false);

        private static Transform FindDeep(Transform root, string n, bool activeOnly)
        {
            if (activeOnly && !root.gameObject.activeInHierarchy) return null;
            if (root.name == n) return root;
            foreach (Transform c in root) { var r = FindDeep(c, n, activeOnly); if (r != null) return r; }
            return null;
        }

        private bool TryNode(string id, out Vector3 p)
        {
            p = default;
            TransportNode n = id != null ? network?.GetNode(id) : null;
            if (n == null) return false;
            p = new Vector3((float)n.X, (float)n.Y, (float)n.Z);
            return true;
        }

        private void CacheRoute(TravelLeg leg)
        {
            cachedLeg = leg;
            var points = new List<Vector3>();
            if (leg.Path != null)
            {
                foreach (var w in leg.Path) points.Add(new Vector3((float)w.X, (float)w.Y, (float)w.Z));
            }
            else if (leg.Route != null)
            {
                foreach (string id in leg.Route) if (TryNode(id, out Vector3 p)) points.Add(p);
            }
            else
            {
                if (TryNode(leg.From, out Vector3 a)) points.Add(a);
                if (TryNode(leg.To, out Vector3 b)) points.Add(b);
            }
            if (points.Count == 0) points.Add(transform.position);
            // a person's walk to / from a machine starts and ends at its entry, not inside it (a view)
            if (leg.Path == null && points.Count >= 2)
            {
                if (Entry(leg.From, out Vector3 start, out bool doorOut)) { if (doorOut) points.Insert(0, start); else points[0] = start; }
                if (Entry(leg.To, out Vector3 end, out bool doorIn)) { if (doorIn) points.Add(end); else points[points.Count - 1] = end; }
            }
            routePoints = points.ToArray();
            cumulativeLengths = new float[routePoints.Length];
            totalLength = 0f;
            for (int i = 1; i < routePoints.Length; i++)
            {
                totalLength += Vector3.Distance(routePoints[i - 1], routePoints[i]);
                cumulativeLengths[i] = totalLength;
            }
        }

        /// <summary>Position at fraction <paramref name="t"/> ∈ [0,1] of the polyline length.</summary>
        private Vector3 PointAlongRoute(float t)
        {
            if (routePoints.Length == 1 || totalLength <= 0f) return routePoints[routePoints.Length - 1];
            float target = t * totalLength;
            for (int i = 1; i < routePoints.Length; i++)
            {
                if (target <= cumulativeLengths[i])
                {
                    float segStart = cumulativeLengths[i - 1];
                    float segLen = cumulativeLengths[i] - segStart;
                    return Vector3.Lerp(routePoints[i - 1], routePoints[i], segLen > 0f ? (target - segStart) / segLen : 1f);
                }
            }
            return routePoints[routePoints.Length - 1];
        }
    }
}
