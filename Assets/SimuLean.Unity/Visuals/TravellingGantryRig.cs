using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The mechanics of a travelling-leg gantry crane — the library's Goliath crane (<c>Art/Models/GoliathCrane</c>) and
    /// welding gantry (<c>Art/Models/WeldingGantry</c>): the <c>Bridge</c> with its legs (<c>Leg_W</c> /
    /// <c>Leg_E</c>) travels along local Z on two ground rails (<c>Rail_W</c> / <c>Rail_E</c>), the <c>Trolley</c> along
    /// the girder (local X), the <c>Head</c> hangs below it on <c>Ropes</c> (a torch slides on its mast), and a load's top
    /// hangs at <c>HookSocket</c>. <see cref="Fit"/> resizes the crane (span = rail centre distance, rail length): the legs
    /// and rails move, the girder and the rails stretch. An <see cref="ICraneRig"/> for <see cref="CraneResourceView"/>.
    /// View only.
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Travelling Gantry Rig")]
    public class TravellingGantryRig : MonoBehaviour, ICraneRig
    {
        [Tooltip("Span (rail centre distance, local X) and rail length (local Z); 0 = as modelled.")]
        [Min(0f)] public float span;
        [Min(0f)] public float length;
        [Tooltip("Closest the head comes to a leg (along the girder) and to a rail end (along the runway), m.")]
        [Min(0f)] public float reachAcross = 2.5f, reachAlong = 3f;
        [Tooltip("Lowest cup height above the floor (m).")]
        [Min(0f)] public float lowestCup = 0.05f;

        private Transform bridge, trolley, head, ropes, socket, legW, legE, railW, railE, girder;
        private Vector3 bridgeRest, trolleyRest, headRest, ropesScale, railScale, girderScale;
        private float s0, l0, ropeRest;
        private Vector2 offset;      // the cup's plan beside the trolley / bridge position (a torch beside the girder)
        private bool ready;

        public float Span => span > 0 ? span : s0;
        public float Length => length > 0 ? length : l0;
        public bool HasHead => Ready() && head != null;
        public float RestCup { get; private set; }
        public Vector2 Plan { get; private set; }
        public float Cup { get; private set; }

        private bool Ready()
        {
            if (ready) return true;
            bridge = Find("Bridge"); trolley = Find("Trolley"); head = Find("Head"); ropes = Find("Ropes");
            socket = Find("HookSocket"); legW = Find("Leg_W"); legE = Find("Leg_E"); railW = Find("Rail_W"); railE = Find("Rail_E");
            girder = Find("Girder");
            if (bridge != null) bridgeRest = bridge.localPosition;
            if (trolley != null) trolleyRest = trolley.localPosition;
            if (head != null) headRest = head.localPosition;
            if (ropes != null) ropesScale = ropes.localScale;
            if (railW != null) railScale = railW.localScale;
            if (girder != null) girderScale = girder.localScale;
            s0 = legW != null && legE != null ? Mathf.Abs(legE.localPosition.x - legW.localPosition.x) : 10f;
            l0 = railW != null ? Mathf.Max(1f, LocalLength(railW)) : 20f;
            ready = true;
            if (head != null)
            {
                Vector3 c = transform.InverseTransformPoint(CupPoint());
                RestCup = c.y; Plan = new Vector2(c.x, c.z); Cup = RestCup;
                offset = new Vector2(c.x - (trolley != null ? trolleyRest.x : 0f), c.z - (bridge != null ? bridgeRest.z : 0f));
                ropeRest = ropes != null ? Mathf.Max(0.01f, ropes.position.y - head.position.y) : 0f;
            }
            if (span > 0 || length > 0) Fit(Span, Length);
            return true;
        }

        private void Awake() => Ready();

        private Vector3 CupPoint() => socket != null ? socket.position : head.position;

        // the rail's modelled length along the frame's Z (its renderers, at rest scale)
        private float LocalLength(Transform rail)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (var r in rail.GetComponentsInChildren<Renderer>(true))
            {
                var b = r.bounds;
                foreach (var z in new[] { transform.InverseTransformPoint(b.min).z, transform.InverseTransformPoint(b.max).z })
                { min = Mathf.Min(min, z); max = Mathf.Max(max, z); }
            }
            return max > min ? max - min : 0f;
        }

        private Transform Find(string n) => FindDeep(transform, n);

        private static Transform FindDeep(Transform root, string n)
        {
            if (root.name == n) return root;
            foreach (Transform c in root) { var r = FindDeep(c, n); if (r != null) return r; }
            return null;
        }

        /// <summary>Resizes the crane to <paramref name="newSpan"/> × <paramref name="newLength"/> (m).</summary>
        public void Fit(float newSpan, float newLength)
        {
            Ready();
            span = Mathf.Max(2f * reachAcross + 1f, newSpan);
            length = Mathf.Max(2f * reachAlong + 1f, newLength);
            float hs = span / 2f;
            Side(legW, hs); Side(legE, hs);
            Side(railW, hs); Side(railE, hs);
            foreach (var r in new[] { railW, railE })
                if (r != null) r.localScale = new Vector3(railScale.x, railScale.y, railScale.z * length / l0);
            if (girder != null) girder.localScale = new Vector3(girderScale.x * span / s0, girderScale.y, girderScale.z);
            SetHead(Plan, Cup);
        }

        // keeps the side of a leg / rail (the model's west may be either sign of x)
        private static void Side(Transform t, float hs)
        {
            if (t == null) return;
            float s = Mathf.Sign(t.localPosition.x == 0 ? 1f : t.localPosition.x);
            t.localPosition = new Vector3(s * hs, t.localPosition.y, t.localPosition.z);
        }

        /// <summary>Turns the runway along the longer side of the points, centres the crane on them and fits it so the
        /// head reaches each (with <paramref name="margin"/>).</summary>
        public void FitAround(IEnumerable<Vector3> worldPoints, float margin = 1f)
        {
            Ready();
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var w in worldPoints) { minX = Mathf.Min(minX, w.x); maxX = Mathf.Max(maxX, w.x); minZ = Mathf.Min(minZ, w.z); maxZ = Mathf.Max(maxZ, w.z); }
            if (minX > maxX) return;
            bool alongX = maxX - minX > maxZ - minZ;
            transform.rotation = Quaternion.Euler(0f, alongX ? 90f : 0f, 0f);
            transform.position = new Vector3((minX + maxX) / 2f, transform.position.y, (minZ + maxZ) / 2f);
            float across = alongX ? maxZ - minZ : maxX - minX, along = alongX ? maxX - minX : maxZ - minZ;
            Fit(across + 2f * (reachAcross + margin), along + 2f * (reachAlong + margin));
        }

        /// <summary>Poses the head: plan (frame x, z) and cup height (frame y), clamped to the travel limits.</summary>
        public void SetHead(Vector2 plan, float cup)
        {
            if (!Ready() || head == null) return;
            float hx = Span / 2f - reachAcross, hz = Length / 2f - reachAlong;
            plan = new Vector2(Mathf.Clamp(plan.x, -hx + offset.x, hx + offset.x), Mathf.Clamp(plan.y, -hz + offset.y, hz + offset.y));
            cup = Mathf.Clamp(cup, lowestCup, RestCup);
            Plan = plan; Cup = cup;
            if (bridge != null) bridge.localPosition = new Vector3(bridgeRest.x, bridgeRest.y, plan.y - offset.y);
            if (trolley != null) trolley.localPosition = new Vector3(plan.x - offset.x, trolleyRest.y, trolleyRest.z);
            float drop = RestCup - cup;
            head.localPosition = headRest + Vector3.down * drop;
            if (ropes != null && ropeRest > 0)
                ropes.localScale = new Vector3(ropesScale.x, ropesScale.y * (ropeRest + drop) / ropeRest, ropesScale.z);
        }

        public Vector3 CupWorld => transform.TransformPoint(new Vector3(Plan.x, Cup, Plan.y));

        public Vector2 PlanOf(Vector3 world) { var p = transform.InverseTransformPoint(world); return new Vector2(p.x, p.z); }

        public float HeightOf(Vector3 world) => transform.InverseTransformPoint(world).y;
    }
}
