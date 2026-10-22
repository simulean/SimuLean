using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The mechanics of the library's 6-axis robot model (<c>Art/Models/IndustrialRobot</c>, script
    /// <c>Tools/Blender/assets/industrial_robot.py</c>): joints <c>A1</c>…<c>A6</c> found by name, link lengths measured
    /// from their rest positions, and an analytic inverse kinematics that puts <c>ToolSocket</c> (the flange face plus
    /// <see cref="toolLength"/>) at a point with the tool pointing in a direction. Used by <see cref="RobotArmView"/>.
    /// <list type="bullet">
    /// <item><b>Chain</b> (zero pose = every node's rest rotation: upper arm vertical, forearm and flange along +Z):
    /// A1 yaw about local Y; A2 shoulder, A3 elbow and A5 wrist pitch about local X; A4 forearm roll and A6 flange roll
    /// about local Z. Unity signs: +A2 leans the upper arm forward, +A3 lowers the forearm, +A5 points the flange down.</item>
    /// <item><b>Solution:</b> wrist centre = target − tool direction × (flange + tool length); A1 = its heading; A2 / A3 by the
    /// law of cosines in the arm plane (elbow up); A4 / A5 / A6 = the remaining wrist rotation split as roll · pitch · roll,
    /// choosing the wrist flip closest to the current pose. Out-of-reach targets are pulled onto the reachable shell (one
    /// warning); every joint is clamped to its range.</item>
    /// <item><b>Mount:</b> everything is solved in the frame of the model root (the parent of <c>A1</c>), so the same
    /// solution serves a floor robot and a ceiling robot (<see cref="mount"/> = Ceiling turns that root 180° about its
    /// forward axis; this transform is then the mount point on the ceiling).</item>
    /// <item><b>Tool:</b> <see cref="tool"/> (e.g. <c>Art/Models/RobotTools</c>: <c>Gripper</c>, <c>VacuumGripper</c>) is
    /// mounted on <c>ToolSocket</c> at run start; its <c>TCP</c> node is the tool centre point the kinematics place (a
    /// tool already placed under the socket, with a <c>TCP</c>, is used as it is). Without a tool the TCP is the flange
    /// plus <see cref="toolLength"/>.</item>
    /// </list>
    /// View only: it never reads or changes the simulation.
    /// </summary>
    [AddComponentMenu("SimuLean/Visuals/Robot Arm Rig")]
    public class RobotArmRig : MonoBehaviour
    {
        public enum Mount { Floor, Ceiling }

        [Tooltip("Floor: base on the floor. Ceiling: the same model upside down; this transform is the mount point.")]
        public Mount mount = Mount.Floor;
        [Tooltip("Tool mounted on the flange at run start (its root = mount face; a child 'TCP' = tool centre point). " +
                 "Empty = the bare flange (or a tool already placed under ToolSocket).")]
        public GameObject tool;
        [Tooltip("Without a TCP node: length of the tool beyond the flange (m), along the flange axis.")]
        [Min(0f)] public float toolLength;
        [Tooltip("Joint ranges (degrees, min / max) A1…A6.")]
        public Vector2[] limits =
        {
            new Vector2(-185f, 185f), new Vector2(-5f, 140f), new Vector2(-120f, 155f),
            new Vector2(-350f, 350f), new Vector2(-125f, 125f), new Vector2(-350f, 350f),
        };

        private static readonly string[] JointNames = { "A1", "A2", "A3", "A4", "A5", "A6" };
        private static readonly Vector3[] JointAxes =
            { Vector3.up, Vector3.right, Vector3.right, Vector3.forward, Vector3.right, Vector3.forward };

        private readonly Transform[] joints = new Transform[6];
        private readonly Quaternion[] rest = new Quaternion[6];
        private readonly float[] angles = new float[6];
        private Transform root, flange, socket, tcp;
        private float d1, a1Off, l2, l3, l6;
        private bool ready, warned;

        /// <summary>The model root the kinematics are solved in (the parent of A1).</summary>
        public Transform Frame => Ready() ? root : transform;
        public Transform ToolSocket => Ready() ? socket : null;

        /// <summary>The mounted tool's centre point node (null for a bare flange).</summary>
        public Transform Tcp => Ready() ? tcp : null;

        /// <summary>Tool centre point beyond the flange face (m): from the tool's TCP node, else <see cref="toolLength"/>.</summary>
        public float ToolLength => Ready() && tcp != null ? socket.InverseTransformPoint(tcp.position).z : toolLength;
        public bool HasArm => Ready();

        /// <summary>Current joint angles (degrees) A1…A6.</summary>
        public float Angle(int i) => angles[i];

        /// <summary>Distance from the A2 axis to the wrist centre at full stretch (m).</summary>
        public float Reach => Ready() ? l2 + l3 : 0f;

        /// <summary>The tool centre point now (world).</summary>
        public Vector3 TcpWorld => !Ready() ? transform.position
                                 : tcp != null ? tcp.position : socket.position + socket.forward * toolLength;

        private bool Ready()
        {
            if (ready) return true;
            for (int i = 0; i < 6; i++)
            {
                joints[i] = Find(JointNames[i]);
                if (joints[i] == null) return false;
                rest[i] = joints[i].localRotation;
            }
            flange = Find("Flange");
            socket = Find("ToolSocket") ?? flange;
            if (flange == null) return false;
            root = joints[0].parent != null ? joints[0].parent : transform;
            // Link lengths from the rest positions (each node's local offset in its parent, zero pose).
            Vector3 p2 = joints[1].localPosition;                                  // A2 in A1
            d1 = joints[0].localPosition.y + p2.y;
            a1Off = p2.z;
            l2 = joints[2].localPosition.y;                                        // A3 in A2
            l3 = joints[3].localPosition.z + joints[4].localPosition.z;            // A3 -> A4 -> A5
            l6 = joints[5].localPosition.z + flange.localPosition.z + (socket != flange ? socket.localPosition.z : 0f);
            ready = true;
            ApplyMount();
            MountTool();
            return true;
        }

        // A tool under the socket already (with a TCP) is used as it is; else `tool` is mounted (at run time only, so
        // scenes and prefabs are never changed by it).
        private void MountTool()
        {
            tcp = FindUnder(socket, "TCP");
            if (tcp != null || tool == null || !Application.isPlaying) return;
            var t = Instantiate(tool, socket, false);
            t.name = tool.name;
            t.transform.localPosition = Vector3.zero;
            t.transform.localRotation = Quaternion.identity;
            tcp = FindUnder(t.transform, "TCP");
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

        private void OnValidate() { if (ready) ApplyMount(); }

        private void ApplyMount()
        {
            if (root == null || root == transform) return;
            root.localRotation = mount == Mount.Ceiling ? Quaternion.Euler(0f, 0f, 180f) : Quaternion.identity;
        }

        /// <summary>
        /// Poses the arm so the tool centre point is at <paramref name="tcpWorld"/> with the tool pointing along
        /// <paramref name="toolDirWorld"/> (default: straight down). Returns false when the target was out of reach
        /// (the arm then stretches towards it).
        /// </summary>
        public bool SetTcp(Vector3 tcpWorld, Vector3? toolDirWorld = null)
        {
            if (!Ready()) return false;
            Vector3 p = root.InverseTransformPoint(tcpWorld);
            Vector3 dir = root.InverseTransformDirection(toolDirWorld ?? Vector3.down).normalized;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.down;

            // Wrist centre and A1.
            Vector3 w = p - dir * (l6 + ToolLength);
            float a1 = Mathf.Atan2(w.x, w.z) * Mathf.Rad2Deg;
            if (new Vector2(w.x, w.z).sqrMagnitude < 1e-6f) a1 = angles[0];       // straight above A1: keep the heading

            // A2 / A3 in the arm plane (r forward from the A2 axis, h up from it).
            float r = new Vector2(w.x, w.z).magnitude - a1Off, h = w.y - d1;
            float d = Mathf.Sqrt(r * r + h * h);
            float dMin = Mathf.Abs(l2 - l3) + 1e-3f, dMax = l2 + l3 - 1e-3f;
            bool reachable = d >= dMin && d <= dMax;
            if (!reachable)
            {
                if (!warned)
                {
                    Debug.LogWarning($"[SimuLean] Robot '{name}': a target is out of reach ({d:0.00} m from the shoulder, " +
                                     $"reach {dMin:0.00}…{dMax:0.00} m); place the robot closer to its points.", this);
                    warned = true;
                }
                d = Mathf.Clamp(d, dMin, dMax);
            }
            float phi = Mathf.Atan2(r, h);                                                // shoulder -> wrist, from vertical
            float beta = Mathf.Acos(Mathf.Clamp((l2 * l2 + d * d - l3 * l3) / (2f * l2 * d), -1f, 1f));
            float gamma = Mathf.Acos(Mathf.Clamp((l2 * l2 + l3 * l3 - d * d) / (2f * l2 * l3), -1f, 1f));
            float a2 = (phi - beta) * Mathf.Rad2Deg;                                      // elbow up
            float a3 = 90f - gamma * Mathf.Rad2Deg;

            a1 = Clamp(0, a1); a2 = Clamp(1, a2); a3 = Clamp(2, a3);

            // Wrist: the rotation left after A1..A3, as roll (A4) · pitch (A5) · roll (A6).
            Quaternion r3 = Quaternion.AngleAxis(a1, Vector3.up) * Quaternion.AngleAxis(a2, Vector3.right)
                          * Quaternion.AngleAxis(a3, Vector3.right);
            Vector3 heading = Quaternion.AngleAxis(a1, Vector3.up) * Vector3.forward;   // the flange's "up" faces away from the base
            Vector3 up = Vector3.ProjectOnPlane(heading, dir);
            if (up.sqrMagnitude < 1e-6f) up = Vector3.ProjectOnPlane(r3 * Vector3.up, dir);
            Quaternion wrist = Quaternion.Inverse(r3) * Quaternion.LookRotation(dir, up);
            SplitWrist(wrist, out float a4, out float a5, out float a6);

            Apply(a1, a2, a3, a4, a5, a6);
            return reachable;
        }

        /// <summary>Sets the joint angles directly (degrees, clamped to the ranges).</summary>
        public void Apply(float a1, float a2, float a3, float a4, float a5, float a6)
        {
            if (!Ready()) return;
            float[] a = { a1, a2, a3, a4, a5, a6 };
            for (int i = 0; i < 6; i++)
            {
                angles[i] = Clamp(i, a[i]);
                joints[i].localRotation = rest[i] * Quaternion.AngleAxis(angles[i], JointAxes[i]);
            }
        }

        // q = Rz(a4) · Rx(a5) · Rz(a6). Two solutions (a5 and −a5, rolls turned 180°): the one closest to the current pose.
        private void SplitWrist(Quaternion q, out float a4, out float a5, out float a6)
        {
            Vector3 z = q * Vector3.forward;
            float pitch = Mathf.Acos(Mathf.Clamp(z.z, -1f, 1f)) * Mathf.Rad2Deg;          // 0..180
            float roll;
            if (pitch < 0.05f) roll = angles[3];                                            // singular: keep A4
            else roll = Vector3.SignedAngle(Vector3.down, new Vector3(z.x, z.y, 0f), Vector3.forward);

            float bestCost = float.MaxValue;
            a4 = a5 = a6 = 0f;
            for (int k = 0; k < 2; k++)
            {
                float r4 = Unwrap(k == 0 ? roll : roll + 180f, angles[3]);
                float p5 = k == 0 ? pitch : -pitch;
                Quaternion rest6 = Quaternion.Inverse(Quaternion.AngleAxis(r4, Vector3.forward) * Quaternion.AngleAxis(p5, Vector3.right)) * q;
                float r6 = Unwrap(Vector3.SignedAngle(Vector3.up, rest6 * Vector3.up, Vector3.forward), angles[5]);
                float cost = Mathf.Abs(r4 - angles[3]) + Mathf.Abs(p5 - angles[4]) + Mathf.Abs(r6 - angles[5])
                           + (InRange(3, r4) && InRange(4, p5) && InRange(5, r6) ? 0f : 1000f);
                if (cost < bestCost) { bestCost = cost; a4 = r4; a5 = p5; a6 = r6; }
            }
        }

        // The angle equal to `a` (mod 360) nearest to `near`.
        private static float Unwrap(float a, float near) => near + Mathf.DeltaAngle(near, a);

        private bool InRange(int i, float a) => limits == null || i >= limits.Length || (a >= limits[i].x && a <= limits[i].y);

        private float Clamp(int i, float a) => limits == null || i >= limits.Length ? a : Mathf.Clamp(a, limits[i].x, limits[i].y);

        private Transform Find(string n) => FindDeep(transform, n);

        private static Transform FindDeep(Transform t, string n)
        {
            if (t.name == n) return t;
            foreach (Transform c in t)
            {
                var r = FindDeep(c, n);
                if (r != null) return r;
            }
            return null;
        }
    }
}
