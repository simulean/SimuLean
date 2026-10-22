using System.Collections.Generic;
using UnityEngine;
using SimuLean.Resources;
using SimuLean.Transport;

namespace SimuLean.Unity
{
    /// <summary>
    /// View of a robot resource (a <see cref="TaskExecutor"/> with axis travel, team Travel = Axes) on the library's 6-axis
    /// robot model through its <see cref="RobotArmRig"/>. The engine moves the robot's tool centre point between points
    /// with an x / y / z axis profile; this view puts the tool there and lets the rig solve the joints. Driven by the
    /// engine, never changing it:
    /// <list type="bullet">
    /// <item><b>Travel:</b> the tool centre point follows the leg with each axis on its own profile
    /// (<see cref="AxisLegPath"/>, the times the engine computed), tool pointing down.</item>
    /// <item><b>Load / unload:</b> within the step's time the tool goes down from the point to the item (load: its top
    /// where it lies; unload: where its top will be once placed), grips / releases it in the middle and goes back.</item>
    /// <item>The item hangs from the tool from the grip until the release, and turns with it.</item>
    /// <item>A gripper tool (<c>Finger_L</c> / <c>Finger_R</c> under the tool, as <c>RobotTools/Gripper</c>) is open,
    /// closes on the item's width from the grip and opens again at the release.</item>
    /// </list>
    /// Chosen by <see cref="UnityResourceTeam"/> for a member prefab with a <see cref="RobotArmRig"/> (or the nodes A1…A6).
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Robot Arm View")]
    public class RobotArmView : MonoBehaviour
    {
        [Tooltip("Share of a load / unload spent going down (the same going back; the grip / release is in between).")]
        [Range(0.1f, 0.45f)] public float lowerShare = 0.4f;

        private TaskExecutor executor;
        private TransportNetwork points;
        private RobotArmRig rig;
        private float lastU;
        private Transform fingerL, fingerR;
        private float jawOpen;
        private readonly Dictionary<Item, Quaternion> heldTurn = new Dictionary<Item, Quaternion>();

        public TaskExecutor Executor => executor;
        public RobotArmRig Rig => rig;

        public void Bind(TaskExecutor executor, TransportNetwork points, RobotArmRig rig)
        {
            this.executor = executor;
            this.points = points;
            this.rig = rig;
            if (Node(executor.Location ?? executor.HomeNode, out Vector3 home)) rig.SetTcp(home);
            var socket = rig.ToolSocket;
            fingerL = socket != null ? FindUnder(socket, "Finger_L") : null;
            fingerR = socket != null ? FindUnder(socket, "Finger_R") : null;
            jawOpen = fingerL != null ? Mathf.Abs(fingerL.localPosition.x) : 0f;
        }

        private void Update()
        {
            if (executor == null || rig == null || !rig.HasArm || UnitySimClock.Instance == null) return;
            double now = UnitySimClock.Instance.clock.GetSimulationTime();

            // Where the engine has the tool: along the leg, or at its location.
            Vector3 tcp = rig.TcpWorld;
            var leg = executor.CurrentLeg;
            if (leg != null && Node(leg.From, out Vector3 a) && Node(leg.To, out Vector3 b))
                tcp = AxisLegPath.Along(executor.TravelModel, leg, a, b, now);
            else if (Node(executor.Location, out Vector3 at)) tcp = at;

            // Down to the item and back while loading / unloading.
            var step = executor.CurrentStep;
            Item gripped = null;
            bool releasedStepItem = false;
            if (step != null && (step.Kind == StepKind.Load || step.Kind == StepKind.Unload))
            {
                float u = StepFraction(now);
                float target = TargetHeight(step, tcp);
                float down = lowerShare, up = 1f - lowerShare;
                float y = u < down ? Mathf.Lerp(tcp.y, target, Ease(u / down))
                        : u < up ? target
                        : Mathf.Lerp(target, tcp.y, Ease((u - up) / (1f - up)));
                tcp.y = y;
                if (step.Kind == StepKind.Load && u >= 0.5f) gripped = step.Item;
                if (step.Kind == StepKind.Unload && u >= 0.5f) releasedStepItem = true;
            }
            rig.SetTcp(tcp);

            Vector3 tool = rig.TcpWorld;
            Item held = null;
            foreach (var item in executor.Carried)
                if (!(releasedStepItem && item == step.Item)) { Hang(item, tool); held = item; }
            if (gripped != null && !Contains(executor.Carried, gripped)) { Hang(gripped, tool); held = gripped; }
            Jaws(held);
            if (held == null) heldTurn.Clear();
        }

        // gripper jaws: on the held item's width (across the tool's X axis), else open
        private void Jaws(Item held)
        {
            if (fingerL == null || fingerR == null) return;
            float half = jawOpen;
            if (held?.vItem is GameObject go && go != null && rig.ToolSocket != null)
            {
                Vector3 r = rig.ToolSocket.right;
                float w = 0f;
                foreach (var rd in go.GetComponentsInChildren<Renderer>())
                {
                    Vector3 e = rd.bounds.extents;
                    w = Mathf.Max(w, Mathf.Abs(e.x * r.x) + Mathf.Abs(e.y * r.y) + Mathf.Abs(e.z * r.z));
                }
                half = Mathf.Clamp(w + 0.01f, 0.035f, jawOpen);
            }
            Vector3 l = fingerL.localPosition, rr = fingerR.localPosition;
            fingerL.localPosition = new Vector3(l.x < 0 ? -half : half, l.y, l.z);
            fingerR.localPosition = new Vector3(rr.x > 0 ? half : -half, rr.y, rr.z);
        }

        private static Transform FindUnder(Transform t, string n)
        {
            foreach (Transform c in t)
            {
                if (c.name == n) return c;
                var r = FindUnder(c, n);
                if (r != null) return r;
            }
            return null;
        }

        // The tool height for the step: the item's top where it lies (load) or where it will be once placed (unload:
        // its height above the point).
        private float TargetHeight(TaskStep step, Vector3 at)
        {
            var go = step.Item?.vItem as GameObject;
            if (step.Kind == StepKind.Load && go != null && !Contains(executor.Carried, step.Item)) return Top(go);
            float h = go != null ? Top(go) - Bottom(go) : 0f;
            return at.y + h;
        }

        private float StepFraction(double now)
        {
            double a = executor.StepStartTime, b = executor.StepEndTime;
            if (executor.IsWorkPaused) return lastU;
            if (double.IsNaN(a) || double.IsNaN(b) || b <= a) return lastU = 1f;
            return lastU = Mathf.Clamp01((float)((now - a) / (b - a)));
        }

        // The item hangs under the tool: its top at the tool centre point, turning with the tool (its yaw relative to
        // the tool's heading is kept from the grip on).
        private void Hang(Item item, Vector3 tool)
        {
            if (!(item?.vItem is GameObject go) || go == null) return;
            if (rig.ToolSocket != null)
            {
                Vector3 h = Vector3.ProjectOnPlane(rig.ToolSocket.up, Vector3.up);
                if (h.sqrMagnitude > 1e-6f)
                {
                    Quaternion heading = Quaternion.LookRotation(h.normalized, Vector3.up);
                    if (!heldTurn.TryGetValue(item, out Quaternion rel))
                        heldTurn[item] = rel = Quaternion.Inverse(heading) * go.transform.rotation;
                    go.transform.rotation = heading * rel;
                }
            }
            float topAbovePivot = Top(go) - go.transform.position.y;
            go.transform.position = tool - Vector3.up * topAbovePivot;
        }

        private bool Node(string id, out Vector3 p)
        {
            p = default;
            TransportNode n = id != null ? points?.GetNode(id) : null;
            if (n == null) return false;
            p = new Vector3((float)n.X, (float)n.Y, (float)n.Z);
            return true;
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
