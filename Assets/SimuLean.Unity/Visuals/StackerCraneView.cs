using System.Collections.Generic;
using UnityEngine;
using SimuLean.Resources;
using SimuLean.Transport;

namespace SimuLean.Unity
{
    /// <summary>
    /// View of an AS/RS stacker crane: a <see cref="TaskExecutor"/> drawn with the library's
    /// <c>ASRS/StackerCrane</c> model (root on the floor at the mast, <c>Carriage</c> lifting, <c>Forks</c> telescoping
    /// sideways, <c>LoadSocket</c> at the fork top, the pallet centre 0.95 m in front of the mast — the node contract of
    /// <c>Tools/Blender/assets/asrs.py</c>). The aisle is the line the crane was placed on (its spawn point, along its +Z).
    /// Driven by the engine, never changing it:
    /// <list type="bullet">
    /// <item><b>Travel:</b> the point the member is at along its leg (<see cref="AxisLegPath"/>: axis profiles with axes
    /// travel, else straight) is projected on the aisle: the mast runs along it so the forks face the point, the carriage
    /// rises to its height.</item>
    /// <item><b>Load / unload:</b> within the step's time the forks reach out sideways to the point (up to 1.35 m, the side
    /// it lies on), the carriage lifts the load off (load) or sets it down (unload) half-way, and the forks come back.</item>
    /// <item>A carried item rides on the <c>LoadSocket</c>.</item>
    /// </list>
    /// Chosen by <see cref="UnityResourceTeam"/> for a member prefab with <c>Carriage</c> and <c>Forks</c> and no mast of a
    /// forklift (<c>Mast</c>).
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Stacker Crane View")]
    public class StackerCraneView : MonoBehaviour
    {
        /// <summary>The model's pallet centre in front of the mast (m, along −Z) and the fork top above the carriage.</summary>
        public const float SocketAhead = 0.95f, SocketHeight = 0.60f;
        /// <summary>The model as built (<c>asrs.py</c>): its carriage lift (seats 0.60 .. 6.40), its mast height and the
        /// height above which the mast is plain (above the control cabinet, 2.15 m) — the part stretched for a taller rack.</summary>
        public const float ModelLift = 5.8f, ModelMastHeight = 8.2f, MastBandBottom = 2.3f;
        /// <summary>How far a node may lie to the side of the aisle and still be one of this crane's (the forks reach
        /// 1.35 m; the racks on both sides of one aisle, not the next aisle's).</summary>
        public const float AisleHalfWidth = 2.0f;

        [Tooltip("Stretch the mast to the rack the crane serves (its highest point), the carriage lifting up to it. Off: " +
                 "the model as built (lift up to 5.8 m).")]
        public bool fitMastToRack = true;
        [Tooltip("Furthest the forks reach into the rack on either side (m).")]
        public float forkReach = 1.35f;
        [Tooltip("Share of a load / unload spent reaching out (the same coming back; the lift is in between).")]
        [Range(0.1f, 0.45f)] public float reachShare = 0.35f;
        [Tooltip("Lift of the carriage when it takes the load off its support (m).")]
        public float liftOff = 0.08f;

        private TaskExecutor executor;
        private TransportNetwork points;
        private Transform carriage, forks, socket;
        private Vector3 origin, along, side;
        private float lastU;
        private readonly List<Mesh> stretched = new List<Mesh>();
        private readonly Dictionary<MeshFilter, Mesh> originals = new Dictionary<MeshFilter, Mesh>();
        private bool framed;

        public TaskExecutor Executor => executor;

        /// <summary>Highest carriage position above the crane's floor (m): the model's 5.8 m, or more once the mast is fitted
        /// to a taller rack (<see cref="FitTo"/>).</summary>
        public float MaxLift { get; private set; } = ModelLift;

        /// <summary>The mast's height now (m): the model's 8.2 m plus the stretch.</summary>
        public float MastHeight => ModelMastHeight + (MaxLift - ModelLift);

        /// <summary>True when <paramref name="model"/> looks like a stacker crane (a carriage with forks, not a forklift).</summary>
        public static bool IsStackerCrane(Transform model)
            => Find(model, "Carriage") != null && Find(model, "Forks") != null && Find(model, "Mast") == null;

        public void Bind(TaskExecutor executor, TransportNetwork points)
        {
            this.executor = executor;
            this.points = points;
            Frame();
            if (fitMastToRack && points != null)
            {
                var served = new List<Vector3>();
                foreach (var n in points.Nodes) served.Add(new Vector3((float)n.X, (float)n.Y, (float)n.Z));
                FitTo(served);
            }
            Pose(Node(executor.Location ?? executor.HomeNode, out Vector3 home) ? home : origin, 0f, 0f);
        }

        // the aisle: the line the crane stands on, along its +Z
        private void Frame()
        {
            if (framed) return;
            framed = true;
            carriage = Find(transform, "Carriage");
            forks = Find(transform, "Forks");
            socket = Find(transform, "LoadSocket");
            origin = transform.position;
            along = transform.forward; along.y = 0f; along = along.sqrMagnitude > 1e-6f ? along.normalized : Vector3.forward;
            side = Vector3.Cross(Vector3.up, along);
        }

        /// <summary>
        /// Fits the crane to the rack it serves (a 14 m rack was served by the 8.2 m model, its carriage stopped
        /// at 5.8 m): the highest of <paramref name="served"/> within <see cref="AisleHalfWidth"/> of the aisle sets the
        /// carriage's lift (that seat height − 0.60 above the floor), and the mast above the cabinet is stretched by what
        /// the lift exceeds the model's (the mesh above <see cref="MastBandBottom"/> scaled along Y, the top carriage moved
        /// up; the textures stretch with it). Never shorter than the model. Returns <see cref="MaxLift"/>.
        /// </summary>
        public float FitTo(IEnumerable<Vector3> served)
        {
            Frame();
            float top = float.NegativeInfinity;
            foreach (var p in served)
            {
                Vector3 d = p - origin;
                if (Mathf.Abs(Vector3.Dot(d, side)) > AisleHalfWidth) continue;
                top = Mathf.Max(top, d.y - SocketHeight);
            }
            float lift = Mathf.Max(ModelLift, float.IsInfinity(top) ? ModelLift : top);
            if (Mathf.Abs(lift - MaxLift) < 1e-3f) return MaxLift;
            StretchMast(lift - ModelLift);
            MaxLift = lift;
            return MaxLift;
        }

        // The base meshes (not the carriage): y below the band kept, the band scaled, above it moved up by 'extra'.
        private void StretchMast(float extra)
        {
            var filters = GetComponentsInChildren<MeshFilter>(true);
            foreach (var mf in filters) if (!originals.ContainsKey(mf)) originals[mf] = mf.sharedMesh;   // the model's, kept
            foreach (var kv in originals) if (kv.Key != null) kv.Key.sharedMesh = kv.Value;
            ReleaseStretched();
            if (extra <= 1e-4f) return;   // the model as built
            float band = ModelMastHeight - MastBandBottom, k = (band + extra) / band;
            foreach (var mf in filters)
            {
                if (carriage != null && mf.transform.IsChildOf(carriage)) continue;
                Mesh source = originals[mf];
                mf.sharedMesh = source;
                if (source == null || !source.isReadable) continue;
                var mesh = Instantiate(source);
                mesh.name = source.name + " (fitted)";
                var v = mesh.vertices;
                Matrix4x4 toRoot = transform.worldToLocalMatrix * mf.transform.localToWorldMatrix, back = toRoot.inverse;
                for (int i = 0; i < v.Length; i++)
                {
                    Vector3 r = toRoot.MultiplyPoint3x4(v[i]);
                    r.y = WarpY(r.y, extra, k);
                    v[i] = back.MultiplyPoint3x4(r);
                }
                mesh.vertices = v;
                mesh.RecalculateBounds();
                mf.sharedMesh = mesh;
                stretched.Add(mesh);
            }
            foreach (var lod in GetComponentsInChildren<LODGroup>(true)) lod.RecalculateBounds();
        }

        private static float WarpY(float y, float extra, float k)
            => y <= MastBandBottom ? y : y < ModelMastHeight ? MastBandBottom + (y - MastBandBottom) * k : y + extra;

        private void ReleaseStretched()
        {
            foreach (var m in stretched) if (m != null) DestroyImmediate(m);
            stretched.Clear();
        }

        private void OnDestroy() => ReleaseStretched();

        /// <summary>Poses the crane at a point with the forks in (what a travel to it shows): the mast faces it along the
        /// aisle, the carriage at its height (clamped to <see cref="MaxLift"/>).</summary>
        public void PoseAt(Vector3 point)
        {
            Frame();
            Pose(point, 0f, 0f);
        }

        /// <summary>The carriage's height above the crane's floor now (m).</summary>
        public float CarriageLift => carriage != null ? carriage.localPosition.y : 0f;

        private void Update()
        {
            if (executor == null || UnitySimClock.Instance == null || UnitySimClock.Instance.clock == null) return;
            double now = UnitySimClock.Instance.clock.GetSimulationTime();

            Vector3 at = transform.position;
            var leg = executor.CurrentLeg;
            if (leg != null && Node(leg.From, out Vector3 a) && Node(leg.To, out Vector3 b))
                at = AxisLegPath.Along(executor.TravelModel, leg, a, b, now);
            else if (Node(executor.Location, out Vector3 here)) at = here;

            float reach = 0f, lift = 0f;
            var step = executor.CurrentStep;
            Item gripped = null;
            bool released = false;
            if (step != null && (step.Kind == StepKind.Load || step.Kind == StepKind.Unload))
            {
                float u = StepFraction(now);
                float full = Mathf.Clamp(Vector3.Dot(at - origin, side), -forkReach, forkReach);
                float outEnd = reachShare, inStart = 1f - reachShare;
                float k = u < outEnd ? Ease(u / outEnd) : u < inStart ? 1f : 1f - Ease((u - inStart) / (1f - inStart));
                reach = full * k;
                bool loading = step.Kind == StepKind.Load;
                // half-way the load is taken off (lifted) or set down (lowered)
                float half = Ease(Mathf.InverseLerp(0.4f, 0.6f, u));
                lift = loading ? liftOff * half : liftOff * (1f - half);
                if (loading && u >= 0.5f) gripped = step.Item;
                if (!loading && u >= 0.5f) released = true;
            }
            Pose(at, reach, lift);

            if (socket == null) return;
            foreach (var item in executor.Carried)
                if (!(released && item == step.Item)) Ride(item);
            if (gripped != null && !Contains(executor.Carried, gripped)) Ride(gripped);
        }

        // The mast along the aisle so the pallet centre faces the point, the carriage at its height, the forks out.
        private void Pose(Vector3 point, float reach, float lift)
        {
            float s = Vector3.Dot(point - origin, along);
            transform.position = origin + along * (s + SocketAhead);
            if (carriage != null)
            {
                var p = carriage.localPosition;
                p.y = Mathf.Clamp(point.y - origin.y - SocketHeight, 0f, MaxLift) + lift;
                carriage.localPosition = p;
            }
            if (forks != null)
            {
                var f = forks.localPosition;
                f.x = Vector3.Dot(side, transform.right) >= 0f ? reach : -reach;
                forks.localPosition = f;
            }
        }

        // An item rides on the forks: its bottom on the socket.
        private void Ride(Item item)
        {
            if (!(item?.vItem is GameObject go) || go == null) return;
            float bottom = float.MaxValue; bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) { bottom = Mathf.Min(bottom, r.bounds.min.y); any = true; }
            float below = any ? go.transform.position.y - bottom : 0f;
            go.transform.SetPositionAndRotation(socket.position + Vector3.up * below, transform.rotation);
        }

        private float StepFraction(double now)
        {
            double a = executor.StepStartTime, b = executor.StepEndTime;
            if (executor.IsWorkPaused) return lastU;
            if (double.IsNaN(a) || double.IsNaN(b) || b <= a) return lastU = 1f;
            return lastU = Mathf.Clamp01((float)((now - a) / (b - a)));
        }

        private bool Node(string id, out Vector3 p)
        {
            p = default;
            TransportNode n = id != null ? points?.GetNode(id) : null;
            if (n == null) return false;
            p = new Vector3((float)n.X, (float)n.Y, (float)n.Z);
            return true;
        }

        private static Transform Find(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            foreach (Transform c in root) { var t = Find(c, name); if (t != null) return t; }
            return null;
        }

        private static bool Contains(IReadOnlyList<Item> list, Item it) { foreach (var x in list) if (x == it) return true; return false; }

        private static float Ease(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
    }
}
