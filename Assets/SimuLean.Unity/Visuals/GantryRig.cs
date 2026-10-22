using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The mechanics of the library's gantry model (<c>Art/Models/GantryStacker</c>), shared by every view that drives
    /// it: the stacker's own cycle animation (<see cref="GantryStackerView"/>) and a crane resource
    /// (<see cref="CraneResourceView"/>). <c>Bridge</c> travels along the frame's local Z (the runway), <c>Trolley</c>
    /// along the bridge = local X, <c>Head</c> up / down; the frame is the bridge's parent. Views pose the head with
    /// <see cref="SetHead"/> (plan position in the frame x / z + cup height in the frame), clamped to the travel limits.
    /// <see cref="Fit"/> resizes the portal (span = rail centre distance, runway length): posts, runways, end beams and
    /// saddles move, girders stretch — the chunky parts are never scaled non-uniformly. View only.
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Gantry Rig")]
    public class GantryRig : MonoBehaviour, ICraneRig
    {
        [Tooltip("Span (rail centre distance, local X) and runway length (local Z); 0 = as modelled.")]
        [Min(0f)] public float span;
        [Min(0f)] public float length;
        [Tooltip("Lowest cup height above the frame floor (m).")]
        [Min(0f)] public float lowestCup = 0.05f;

        // reach of the head from a rail centre (along the bridge) / a post centre (along the runway) — see the model
        private const float ReachX = 0.55f, ReachZ = 1.05f;

        private Transform bridge, trolley, head;
        private Transform legSW, legSE, legNW, legNE, runwayW, runwayE, endS, endN, girder, saddleW, saddleE;
        private Vector3 bridgeRest, trolleyRest, headRest, runwayScale, endScale, girderScale;
        private float s0, l0;
        private bool ready;

        public Transform Bridge => Ready() ? bridge : null;
        public Transform Head => Ready() ? head : null;

        /// <summary>The frame the plan and cup heights are expressed in (the bridge's parent).</summary>
        public Transform Frame => Ready() && bridge != null && bridge.parent != null ? bridge.parent : transform;

        /// <summary>Cup height at rest = the highest (frame y).</summary>
        public float RestCup { get; private set; }

        /// <summary>Head position now: plan (frame x, z) and cup height (frame y).</summary>
        public Vector2 Plan { get; private set; }
        public float Cup { get; private set; }

        public bool HasHead => Ready() && head != null;
        public float Span => span > 0 ? span : s0;
        public float Length => length > 0 ? length : l0;

        private bool Ready()
        {
            if (ready) return true;
            bridge = Find("Bridge"); trolley = Find("Trolley"); head = Find("Head");
            legSW = Find("Leg_SW"); legSE = Find("Leg_SE"); legNW = Find("Leg_NW"); legNE = Find("Leg_NE");
            runwayW = Find("Runway_W"); runwayE = Find("Runway_E"); endS = Find("EndBeam_S"); endN = Find("EndBeam_N");
            girder = Find("Girder"); saddleW = Find("Saddle_W"); saddleE = Find("Saddle_E");
            if (bridge != null) bridgeRest = bridge.localPosition;
            if (trolley != null) trolleyRest = trolley.localPosition;
            if (head != null) headRest = head.localPosition;
            s0 = legSW != null ? 2f * Mathf.Abs(legSW.localPosition.x) : 4f;
            l0 = legSW != null ? 2f * Mathf.Abs(legSW.localPosition.z) : 12f;
            if (runwayW != null) runwayScale = runwayW.localScale;
            if (endS != null) endScale = endS.localScale;
            if (girder != null) girderScale = girder.localScale;
            ready = true;
            if (head != null && bridge != null && bridge.parent != null)
            {
                Vector3 h = bridge.parent.InverseTransformPoint(head.position);
                RestCup = h.y; Plan = new Vector2(h.x, h.z); Cup = RestCup;
            }
            if (span > 0 || length > 0) Fit(Span, Length);
            return true;
        }

        private Transform Find(string n) => FindDeep(transform, n);

        private static Transform FindDeep(Transform root, string n)
        {
            if (root.name == n) return root;
            foreach (Transform c in root) { var r = FindDeep(c, n); if (r != null) return r; }
            return null;
        }

        private void Awake() => Ready();

        /// <summary>Resizes the portal to <paramref name="newSpan"/> × <paramref name="newLength"/> (m).</summary>
        public void Fit(float newSpan, float newLength)
        {
            Ready();
            span = Mathf.Max(1.5f, newSpan); length = Mathf.Max(2.5f, newLength);
            float hs = span / 2f, hl = length / 2f;
            Place(legSW, -hs, -hl); Place(legSE, hs, -hl); Place(legNW, -hs, hl); Place(legNE, hs, hl);
            if (runwayW != null) { runwayW.localPosition = new Vector3(-hs, runwayW.localPosition.y, 0); runwayW.localScale = new Vector3(runwayScale.x, runwayScale.y, runwayScale.z * length / l0); }
            if (runwayE != null) { runwayE.localPosition = new Vector3(hs, runwayE.localPosition.y, 0); runwayE.localScale = new Vector3(runwayScale.x, runwayScale.y, runwayScale.z * length / l0); }
            if (endS != null) { endS.localPosition = new Vector3(0, endS.localPosition.y, -hl); endS.localScale = new Vector3(endScale.x * span / s0, endScale.y, endScale.z); }
            if (endN != null) { endN.localPosition = new Vector3(0, endN.localPosition.y, hl); endN.localScale = new Vector3(endScale.x * span / s0, endScale.y, endScale.z); }
            if (girder != null) girder.localScale = new Vector3(girderScale.x * span / s0, girderScale.y, girderScale.z);
            if (saddleW != null) saddleW.localPosition = new Vector3(-hs, saddleW.localPosition.y, saddleW.localPosition.z);
            if (saddleE != null) saddleE.localPosition = new Vector3(hs, saddleE.localPosition.y, saddleE.localPosition.z);
            SetHead(Plan, Cup);
        }

        private static void Place(Transform t, float x, float z) { if (t != null) t.localPosition = new Vector3(x, t.localPosition.y, z); }

        /// <summary>Fits the portal so its head reaches every world point given (with a margin), centred on them.</summary>
        public void FitAround(System.Collections.Generic.IEnumerable<Vector3> worldPoints, float margin = 1f)
        {
            Ready();
            // Runway (local Z) along the longer side of the area.
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var w in worldPoints) { minX = Mathf.Min(minX, w.x); maxX = Mathf.Max(maxX, w.x); minZ = Mathf.Min(minZ, w.z); maxZ = Mathf.Max(maxZ, w.z); }
            if (minX > maxX) return;
            transform.rotation = Quaternion.Euler(0f, (maxX - minX) > (maxZ - minZ) ? 90f : 0f, 0f);
            bool any = false; Vector2 min = Vector2.zero, max = Vector2.zero;
            Transform f = transform;
            foreach (var w in worldPoints)
            {
                Vector3 p = f.InverseTransformPoint(w);
                var q = new Vector2(p.x, p.z);
                if (!any) { min = max = q; any = true; } else { min = Vector2.Min(min, q); max = Vector2.Max(max, q); }
            }
            if (!any) return;
            Vector2 c = (min + max) / 2f;
            transform.position = f.TransformPoint(new Vector3(c.x, 0, c.y));
            Fit((max.x - min.x) + 2 * (ReachX + margin), (max.y - min.y) + 2 * (ReachZ + margin));
        }

        /// <summary>Poses the head: plan (frame x, z) and cup height (frame y), clamped to the travel limits.</summary>
        public void SetHead(Vector2 plan, float cup)
        {
            if (!Ready() || head == null) return;
            float hs = Span / 2f - ReachX, hl = Length / 2f - ReachZ;
            plan = new Vector2(Mathf.Clamp(plan.x, -hs, hs), Mathf.Clamp(plan.y, -hl, hl));
            cup = Mathf.Clamp(cup, lowestCup, RestCup);
            Plan = plan; Cup = cup;
            if (bridge != null) bridge.localPosition = new Vector3(bridgeRest.x, bridgeRest.y, plan.y);
            if (trolley != null) trolley.localPosition = new Vector3(plan.x - (bridge != null ? bridgeRest.x : 0f), trolleyRest.y, trolleyRest.z);
            head.localPosition = headRest + Vector3.up * (cup - RestCup);
        }

        /// <summary>Cup position in world space.</summary>
        public Vector3 CupWorld => Frame.TransformPoint(new Vector3(Plan.x, Cup, Plan.y));

        /// <summary>World point → plan (frame x, z) and height (frame y).</summary>
        public Vector2 PlanOf(Vector3 world) { var p = Frame.InverseTransformPoint(world); return new Vector2(p.x, p.z); }
        public float HeightOf(Vector3 world) => Frame.InverseTransformPoint(world).y;

        public void Park()
        {
            if (!Ready()) return;
            if (bridge != null) bridge.localPosition = bridgeRest;
            if (trolley != null) trolley.localPosition = trolleyRest;
            if (head != null)
            {
                head.localPosition = headRest;
                Vector3 h = Frame.InverseTransformPoint(head.position);
                Plan = new Vector2(h.x, h.z); Cup = RestCup;
            }
        }
    }
}
