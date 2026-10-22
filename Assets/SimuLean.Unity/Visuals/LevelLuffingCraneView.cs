using System.Collections.Generic;
using UnityEngine;
using SimuLean.Resources;
using SimuLean.Transport;

namespace SimuLean.Unity
{
    /// <summary>
    /// View of a level-luffing portal crane resource ("grúa cigüeña", the library's <c>LevelLuffingCrane</c> model) — a
    /// <see cref="TaskExecutor"/> with axis travel (team Travel = Axes) whose axes move the <b>hook</b> between points.
    /// The view turns the hook point into the crane's own motions, driven by the engine and never changing it:
    /// <list type="bullet">
    /// <item><b>Travel</b> along the rails (the member's local Z from where it was spawned, i.e. the team's transform):
    /// the crane stays abreast of the hook, or moves just far enough to keep it within reach.</item>
    /// <item><b>Slew</b> (<c>Slew</c>, about local Y) to face the hook; <b>luff</b> (<c>Boom</c>, about local Z) to the
    /// angle whose jib tip is at the hook's reach — the jib (<c>Jib</c>) and the tie (<c>Tie</c>) follow the four-bar
    /// linkage, so the tip stays almost level (<see cref="Linkage"/>, the same maths as
    /// <c>Tools/Blender/assets/level_luffing_crane.py</c>).</item>
    /// <item><b>Hoist:</b> the rope falls (<c>Ropes</c>, scaled in Y) and the hook block (<c>HookBlock</c>) hang from the
    /// tip down to the hook height: <see cref="hookTravelHeight"/> while travelling; during a load / unload the hook goes
    /// down to the load (its top + <see cref="slingLength"/>), grips / releases it in the middle and goes back up.</item>
    /// <item>The item hangs <see cref="slingLength"/> under the hook from the grip until the release, turning with the jib.</item>
    /// </list>
    /// Chosen by <see cref="UnityResourceTeam"/> for a member prefab with the nodes <c>Slew</c> and <c>Jib</c>.
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Level Luffing Crane View")]
    public class LevelLuffingCraneView : MonoBehaviour
    {
        [Header("Linkage (slewing plane: x towards the jib, y up from the rail top; m) — must match the model")]
        public Vector2 boomFoot = new Vector2(2.0f, 16.0f);
        public Vector2 tiePivot = new Vector2(-6.0f, 19.1f);
        public float boomLength = 24.0f;
        public float jibTail = 5.0f;
        public float tieLength = 24.7f;
        public float jibNose = 14.2f;
        [Tooltip("Boom angle (deg from horizontal) the model was exported at.")]
        public float restBoomAngle = 56f;
        public float minBoomAngle = 38f;
        public float maxBoomAngle = 80f;

        [Header("Motion")]
        [Tooltip("Hook height above the rail top while travelling (the hook block's top).")]
        public float hookTravelHeight = 24f;
        [Tooltip("From the hook to the top of the load (slings).")]
        public float slingLength = 6f;
        [Tooltip("Travel limits along the rails, from where the crane was spawned (m).")]
        public Vector2 railRange = new Vector2(-500f, 500f);
        [Tooltip("Share of a load / unload spent going down (the same going up; the grip / release is in between).")]
        [Range(0.1f, 0.45f)] public float lowerShare = 0.4f;

        private TaskExecutor executor;
        private TransportNetwork points;
        private Transform slew, boom, jib, tie, ropes, hookBlock, hookSocket;
        private Quaternion slewRest, boomRest, jibRest, tieRest;
        private Vector3 ropesRest, hookRest;
        private float unitDrop = 1f, socketBelowPivot;
        private Vector3 railOrigin, railDir;
        private float travel, lastU;
        private float jibAngle0, tieAngle0;
        private Vector2 tip0;
        private bool warnedReach;
        private readonly Dictionary<Item, Quaternion> heldTurn = new Dictionary<Item, Quaternion>();

        public TaskExecutor Executor => executor;
        /// <summary>Distance travelled along the rails from the spawn point (m).</summary>
        public float Travel => travel;
        /// <summary>Current boom angle (deg from horizontal).</summary>
        public float BoomAngle { get; private set; }
        public bool HasRig => slew != null && boom != null && jib != null;

        public void Bind(TaskExecutor executor, TransportNetwork points)
        {
            this.executor = executor;
            this.points = points;
            if (!FindRig()) return;
            if (Node(executor.Location ?? executor.HomeNode, out Vector3 home))
                Solve(new Vector3(home.x, RailTop + hookTravelHeight, home.z));
        }

        /// <summary>Finds the nodes and records the rest pose (also used by tests and editor previews).</summary>
        public bool FindRig()
        {
            slew = Find(transform, "Slew");
            boom = Find(transform, "Boom");
            jib = Find(transform, "Jib");
            tie = Find(transform, "Tie");
            ropes = Find(transform, "Ropes");
            hookBlock = Find(transform, "HookBlock");
            hookSocket = Find(transform, "HookSocket");
            if (!HasRig)
            {
                Debug.LogWarning($"[SimuLean] {name}: no Slew / Boom / Jib nodes, the level-luffing crane view is off.");
                return false;
            }
            slewRest = slew.localRotation;
            boomRest = boom.localRotation;
            jibRest = jib.localRotation;
            if (tie != null) tieRest = tie.localRotation;
            if (ropes != null) ropesRest = ropes.localPosition;
            if (hookBlock != null) hookRest = hookBlock.localPosition;
            if (ropes != null && hookBlock != null) unitDrop = Mathf.Max(0.01f, ropesRest.y - hookRest.y);
            socketBelowPivot = hookBlock != null && hookSocket != null ? hookBlock.position.y - hookSocket.position.y : 0f;
            railOrigin = transform.position;
            railDir = transform.forward;
            travel = 0f;
            Linkage(restBoomAngle, out Vector2 b0, out Vector2 r0, out tip0);
            jibAngle0 = Angle(b0 - r0);
            tieAngle0 = Angle(r0 - tiePivot);
            BoomAngle = restBoomAngle;
            return true;
        }

        private float RailTop => railOrigin.y;

        private void Update()
        {
            if (executor == null || !HasRig || UnitySimClock.Instance == null) return;
            double now = UnitySimClock.Instance.clock.GetSimulationTime();

            // Plan: along the leg (axis profiles) or at the member's location.
            Vector3 plan = HookPlan();
            var leg = executor.CurrentLeg;
            if (leg != null && Node(leg.From, out Vector3 a) && Node(leg.To, out Vector3 b))
                plan = AxisLegPath.Along(executor.TravelModel, leg, a, b, now);
            else if (Node(executor.Location, out Vector3 at)) plan = at;

            // Height: up while travelling, down to the load while loading / unloading.
            float hookY = RailTop + hookTravelHeight;
            var step = executor.CurrentStep;
            Item gripped = null;
            bool releasedStepItem = false;
            if (step != null && (step.Kind == StepKind.Load || step.Kind == StepKind.Unload))
            {
                float u = StepFraction(now);
                float target = TargetHook(step, plan);
                float down = lowerShare, up = 1f - lowerShare;
                hookY = u < down ? Mathf.Lerp(hookY, target, Ease(u / down))
                      : u < up ? target
                      : Mathf.Lerp(target, hookY, Ease((u - up) / (1f - up)));
                if (step.Kind == StepKind.Load && u >= 0.5f) gripped = step.Item;
                if (step.Kind == StepKind.Unload && u >= 0.5f) releasedStepItem = true;
            }
            Solve(new Vector3(plan.x, hookY, plan.z));

            Vector3 socket = hookSocket != null ? hookSocket.position : (hookBlock != null ? hookBlock.position : slew.position);
            foreach (var item in executor.Carried)
                if (!(releasedStepItem && item == step.Item)) Hang(item, socket);
            if (gripped != null && !Contains(executor.Carried, gripped)) Hang(gripped, socket);
            if (executor.Carried.Count == 0 && gripped == null) heldTurn.Clear();
        }

        /// <summary>
        /// Poses the crane so that the hook block's top is at <paramref name="hook"/> (world): travel, slew, luff and
        /// rope drop. A point out of reach is approached as close as the crane gets.
        /// </summary>
        public void Solve(Vector3 hook)
        {
            if (!HasRig) return;
            Vector3 lateralDir = Vector3.Cross(Vector3.up, railDir).normalized;     // the member's local +X at spawn
            Vector3 rel = hook - railOrigin;
            float along = Vector3.Dot(rel, railDir);
            float side = Vector3.Dot(rel, lateralDir);
            Linkage(maxBoomAngle, out _, out _, out Vector2 tipMin);
            Linkage(minBoomAngle, out _, out _, out Vector2 tipMax);
            float rMin = tipMin.x, rMax = tipMax.x;

            // travel: abreast of the hook, or just far enough along the rails to keep it beyond the minimum reach
            float s = along;
            if (Mathf.Abs(side) < rMin)
            {
                float off = Mathf.Sqrt(rMin * rMin - side * side);
                s = Mathf.Abs(along - off - travel) <= Mathf.Abs(along + off - travel) ? along - off : along + off;
            }
            travel = Mathf.Clamp(s, railRange.x, railRange.y);
            transform.position = railOrigin + railDir * travel;

            // slew: the jib (slew local +X) towards the hook
            Vector3 toHook = transform.InverseTransformDirection(hook - slew.position);
            float reach = new Vector2(toHook.x, toHook.z).magnitude;
            if (reach > 1e-3f)
                slew.localRotation = slewRest * Quaternion.Euler(0f, Mathf.Atan2(-toHook.z, toHook.x) * Mathf.Rad2Deg, 0f);
            if ((reach > rMax + 0.05f || reach < rMin - 0.05f) && !warnedReach)
            {
                warnedReach = true;
                Debug.LogWarning($"[SimuLean] {name}: hook point {reach:F1} m from the slewing axis is out of reach " +
                                 $"({rMin:F1}…{rMax:F1} m); the crane stops at its limit.");
            }

            // luff: the boom angle whose tip is at that reach; the jib and the tie follow the linkage
            float theta = AngleForReach(Mathf.Clamp(reach, rMin, rMax));
            BoomAngle = theta;
            Linkage(theta, out Vector2 b, out Vector2 r, out Vector2 tip);
            boom.localRotation = boomRest * Quaternion.Euler(0f, 0f, theta - restBoomAngle);
            jib.localRotation = jibRest * Quaternion.Euler(0f, 0f, (Angle(b - r) - jibAngle0) - (theta - restBoomAngle));
            if (tie != null) tie.localRotation = tieRest * Quaternion.Euler(0f, 0f, Angle(r - tiePivot) - tieAngle0);

            // hoist: the falls from the tip sheave down to the hook block
            if (ropes != null)
            {
                Vector3 ropeTop = ropesRest + new Vector3(tip.x - tip0.x, tip.y - tip0.y, 0f);
                ropes.localPosition = ropeTop;
                float topY = slew.TransformPoint(ropeTop).y;
                float drop = Mathf.Max(0.5f, topY - hook.y);
                ropes.localScale = new Vector3(1f, drop / unitDrop, 1f);
                if (hookBlock != null) hookBlock.localPosition = ropeTop + (hookRest - ropesRest) - new Vector3(0f, drop - unitDrop, 0f);
            }
        }

        /// <summary>
        /// The four-bar linkage in the slewing plane: boom angle (deg from horizontal) → boom head <paramref name="b"/>,
        /// jib tail <paramref name="r"/> and jib tip <paramref name="tip"/> (x = reach from the slewing axis, y = height
        /// above the rail top).
        /// </summary>
        public void Linkage(float thetaDeg, out Vector2 b, out Vector2 r, out Vector2 tip)
        {
            float th = thetaDeg * Mathf.Deg2Rad;
            b = boomFoot + boomLength * new Vector2(Mathf.Cos(th), Mathf.Sin(th));
            Vector2 d = tiePivot - b;
            float dist = d.magnitude;
            float a = (jibTail * jibTail - tieLength * tieLength + dist * dist) / (2f * dist);
            float h = Mathf.Sqrt(Mathf.Max(0f, jibTail * jibTail - a * a));
            Vector2 m = b + a * d / dist;
            r = new Vector2(m.x + h * d.y / dist, m.y - h * d.x / dist);
            Vector2 u = (b - r) / jibTail;
            tip = b + jibNose * u;
        }

        /// <summary>The boom angle whose jib tip is at <paramref name="reach"/> (the reach falls as the boom rises).</summary>
        public float AngleForReach(float reach)
        {
            float lo = minBoomAngle, hi = maxBoomAngle;
            for (int i = 0; i < 40; i++)
            {
                float mid = 0.5f * (lo + hi);
                Linkage(mid, out _, out _, out Vector2 t);
                if (t.x > reach) lo = mid; else hi = mid;
            }
            return 0.5f * (lo + hi);
        }

        // Hook block top for the step: above the load's top where it lies (load), or above where it will rest (unload).
        private float TargetHook(TaskStep step, Vector3 at)
        {
            var go = step.Item?.vItem as GameObject;
            float top;
            if (step.Kind == StepKind.Load && go != null && !Contains(executor.Carried, step.Item)) top = Top(go);
            else top = at.y + (go != null ? Top(go) - Bottom(go) : 1f);
            return top + slingLength + socketBelowPivot;
        }

        private Vector3 HookPlan() => hookBlock != null ? hookBlock.position : transform.position;

        private float StepFraction(double now)
        {
            double a = executor.StepStartTime, b = executor.StepEndTime;
            if (executor.IsWorkPaused) return lastU;
            if (double.IsNaN(a) || double.IsNaN(b) || b <= a) return lastU = 1f;
            return lastU = Mathf.Clamp01((float)((now - a) / (b - a)));
        }

        // The item hangs on its slings under the hook, turning with the jib (its yaw relative to the jib is kept from
        // the grip on).
        private void Hang(Item item, Vector3 socket)
        {
            if (!(item?.vItem is GameObject go) || go == null) return;
            Vector3 h = Vector3.ProjectOnPlane(slew.right, Vector3.up);
            if (h.sqrMagnitude > 1e-6f)
            {
                Quaternion heading = Quaternion.LookRotation(h.normalized, Vector3.up);
                if (!heldTurn.TryGetValue(item, out Quaternion rel))
                    heldTurn[item] = rel = Quaternion.Inverse(heading) * go.transform.rotation;
                go.transform.rotation = heading * rel;
            }
            float topAbovePivot = Top(go) - go.transform.position.y;
            go.transform.position = socket - Vector3.up * (slingLength + topAbovePivot);
        }

        private bool Node(string id, out Vector3 p)
        {
            p = default;
            TransportNode n = id != null ? points?.GetNode(id) : null;
            if (n == null) return false;
            p = new Vector3((float)n.X, (float)n.Y, (float)n.Z);
            return true;
        }

        private static float Angle(Vector2 v) => Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;

        private static Transform Find(Transform t, string n)
        {
            if (t.name == n) return t;
            foreach (Transform c in t)
            {
                var r = Find(c, n);
                if (r != null) return r;
            }
            return null;
        }

        private static bool Contains(IReadOnlyList<Item> list, Item it) { foreach (var x in list) if (x == it) return true; return false; }

        private static float Top(GameObject go)
        {
            float max = float.MinValue; bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) { max = Mathf.Max(max, r.bounds.max.y); any = true; }
            return any ? max : go.transform.position.y;
        }

        private static float Bottom(GameObject go)
        {
            float min = float.MaxValue; bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) { min = Mathf.Min(min, r.bounds.min.y); any = true; }
            return any ? min : go.transform.position.y;
        }

        private static float Ease(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
    }
}
