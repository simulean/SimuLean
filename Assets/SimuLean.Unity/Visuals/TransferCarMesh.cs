using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Parameters of a generated <b>transfer car</b> (rail shuttle with roller decks; plain data, see
    /// <see cref="TransferCarMesh"/>). Local axes: <b>+Z = along the track</b> (travel), +X = across the track = the run
    /// direction of the lane decks (loads transfer sideways onto / off the car), +Y up; origin on the floor at the car's
    /// centre. <see cref="lanes"/> decks lie side by side along the track, each holding <see cref="depth"/> loads one
    /// behind the other. Metres.
    /// </summary>
    [System.Serializable]
    public class TransferCarSpec
    {
        [Min(1)] public int lanes = 3;
        [Min(1)] public int depth = 1;
        [Tooltip("Clear width of a lane between its frames (a 800-wide pallet travelling long side first: ≈ 1.0 m).")]
        public float laneWidth = 1.0f;
        [Tooltip("Load pitch along a lane (a 1200-long pallet: ≈ 1.3 m).")]
        public float positionPitch = 1.3f;
        [Tooltip("Roller-top (deck) height above the floor.")]
        public float deckHeight = 0.5f;
        [Tooltip("Gap between two lane decks.")]
        public float laneGap = 0.18f;
        public float rollerPitch = 0.16f, rollerRadius = 0.045f;
        public float wheelRadius = 0.16f;
        [Tooltip("Depth of the bumpers at both ends (along the track).")]
        public float bumper = 0.2f;
        [Tooltip("Rail head height above the floor (the rails of the generated track).")]
        public float railTop = 0.05f;

        /// <summary>Lanes and depth for a car that carries <paramref name="capacity"/> loads: lanes = min(C, maxLanes),
        /// depth = ceil(C / lanes).</summary>
        public static TransferCarSpec ForCapacity(int capacity, int maxLanes = 3)
        {
            int c = Mathf.Max(1, capacity);
            int lanes = Mathf.Clamp(c, 1, Mathf.Max(1, maxLanes));
            return new TransferCarSpec { lanes = lanes, depth = Mathf.CeilToInt(c / (float)lanes) };
        }

        public int Positions => lanes * depth;
        /// <summary>Length of a lane deck (across the track).</summary>
        public float LaneLength => depth * positionPitch + 0.2f;
        /// <summary>Distance between two lanes' centre lines (along the track).</summary>
        public float LaneSpacing => laneWidth + 0.1f + laneGap;
        /// <summary>Car length along the track, bumpers included.</summary>
        public float Length => lanes * LaneSpacing + 2f * bumper + 0.3f;
        /// <summary>Car width across the track.</summary>
        public float Width => LaneLength + 0.3f;
        /// <summary>Rail gauge (centre to centre): follows the car width.</summary>
        public float RailGauge => Width - 0.35f;

        /// <summary>Centre of the load at lane <paramref name="lane"/>, position <paramref name="d"/> (on the deck top).</summary>
        public Vector3 SlotPosition(int lane, int d)
            => new Vector3((d - (depth - 1) * 0.5f) * positionPitch, deckHeight, (lane - (lanes - 1) * 0.5f) * LaneSpacing);

        public TransferCarSpec Clone() => (TransferCarSpec)MemberwiseClone();
    }

    /// <summary>
    /// Geometry of a transfer car (<see cref="TransferCarSpec"/>; component <see cref="UnityTransferCar"/>): the chassis
    /// (frame, wheels on the rails, drive units, yellow / black bumpers, beacon post) as one mesh with
    /// <see cref="SubMeshes"/> material slots, each lane deck as a <see cref="RollerConveyorMesh"/> section, and the
    /// matching floor track (<see cref="BuildTrack"/>: bed plate and two rails at <see cref="TransferCarSpec.RailGauge"/>).
    /// LOD1 = fewer wheel sides and no small hardware. Generated in code, no assets.
    /// </summary>
    public static class TransferCarMesh
    {
        public const int Paint = 0, Steel = 1, Safety = 2, Dark = 3, Light = 4, SubMeshes = 5;

        public static Mesh BuildChassis(TransferCarSpec s, int lod, Mesh target = null, string name = "TransferCar_Chassis")
        {
            var g = new StorageMeshBuilder(SubMeshes);
            float L = s.Length, W = s.Width, halfL = L * 0.5f, halfW = W * 0.5f;
            float top = s.deckHeight - 0.12f;                  // the lane decks' frames sit on it
            float axleY = s.railTop + s.wheelRadius, gauge = s.RailGauge;
            float bodyBottom = axleY + 0.02f;
            float zEnd = halfL - s.bumper;
            // frame: side members along the track, end beams, cross members between the lanes, a deck plate
            g.BoxMinMax(Paint, new Vector3(-halfW, bodyBottom, -zEnd), new Vector3(-halfW + 0.16f, top, zEnd), BoxFaces.All);
            g.BoxMinMax(Paint, new Vector3(halfW - 0.16f, bodyBottom, -zEnd), new Vector3(halfW, top, zEnd), BoxFaces.All);
            g.BoxMinMax(Paint, new Vector3(-halfW, bodyBottom, -zEnd), new Vector3(halfW, top, -zEnd + 0.14f), BoxFaces.All);
            g.BoxMinMax(Paint, new Vector3(-halfW, bodyBottom, zEnd - 0.14f), new Vector3(halfW, top, zEnd), BoxFaces.All);
            for (int k = 1; k < s.lanes; k++)
            {
                float z = (k - s.lanes * 0.5f) * s.LaneSpacing;
                g.BoxMinMax(Paint, new Vector3(-halfW + 0.16f, top - 0.12f, z - 0.05f), new Vector3(halfW - 0.16f, top, z + 0.05f), BoxFaces.All);
            }
            g.BoxMinMax(Dark, new Vector3(-halfW + 0.16f, bodyBottom + 0.02f, -zEnd + 0.14f), new Vector3(halfW - 0.16f, bodyBottom + 0.06f, zEnd - 0.14f), BoxFaces.Top);
            // wheels on the rails (axles across the track) and their housings
            float wz = zEnd - 0.32f;
            int sides = lod == 0 ? 16 : 8;
            foreach (float x in new[] { -gauge * 0.5f, gauge * 0.5f })
                foreach (float z in new[] { -wz, wz })
                {
                    Cylinder(g, Steel, new Vector3(x, axleY, z), s.wheelRadius, 0.07f, sides);
                    if (lod == 0) Cylinder(g, Dark, new Vector3(x, axleY, z), s.wheelRadius * 0.45f, 0.11f, 8);
                    g.BoxMinMax(Dark, new Vector3(x - 0.09f, axleY + s.wheelRadius * 0.4f, z - s.wheelRadius - 0.06f),
                                      new Vector3(x + 0.09f, top, z + s.wheelRadius + 0.06f), BoxFaces.NoBottom);
                }
            // drive units (gear motors) at one end, beside the wheels
            if (lod == 0)
                foreach (float x in new[] { -gauge * 0.5f + 0.32f, gauge * 0.5f - 0.32f })
                {
                    g.BoxMinMax(Dark, new Vector3(x - 0.14f, bodyBottom, wz - 0.18f), new Vector3(x + 0.14f, bodyBottom + 0.22f, wz + 0.18f), BoxFaces.NoBottom);
                    Cylinder(g, Steel, new Vector3(x, bodyBottom + 0.11f, wz + 0.24f), 0.07f, 0.12f, 10, alongZ: true);
                }
            // bumpers: yellow / black stripes across both ends
            foreach (float sign in new[] { -1f, 1f })
            {
                float z0 = sign * zEnd, z1 = sign * (zEnd + s.bumper * 0.8f);
                int stripes = Mathf.Max(3, Mathf.RoundToInt(W / 0.25f));
                for (int i = 0; i < stripes; i++)
                {
                    float x0 = -halfW + i * W / stripes, x1 = -halfW + (i + 1) * W / stripes;
                    g.BoxMinMax(i % 2 == 0 ? Safety : Dark, new Vector3(x0, bodyBottom + 0.06f, Mathf.Min(z0, z1)), new Vector3(x1, top - 0.02f, Mathf.Max(z0, z1)), BoxFaces.All);
                }
            }
            // beacon post at a corner (the beacon itself is the Light slot)
            var post = new Vector3(halfW - 0.1f, 0f, zEnd - 0.1f);
            g.BoxMinMax(Steel, new Vector3(post.x - 0.03f, top, post.z - 0.03f), new Vector3(post.x + 0.03f, s.deckHeight + 0.85f, post.z + 0.03f), BoxFaces.NoBottom);
            Cylinder(g, Light, new Vector3(post.x, s.deckHeight + 0.92f, post.z), 0.07f, 0.14f, sides, vertical: true);
            if (lod == 0)
                g.BoxMinMax(Paint, new Vector3(post.x - 0.12f, top, post.z - 0.32f), new Vector3(post.x + 0.08f, s.deckHeight + 0.55f, post.z - 0.08f), BoxFaces.All);
            return g.ToMesh(target, name + (lod == 0 ? "_LOD0" : "_LOD1"));
        }

        /// <summary>Beacon position (local), for a light / pulse node.</summary>
        public static Vector3 BeaconPosition(TransferCarSpec s)
            => new Vector3(s.Width * 0.5f - 0.1f, s.deckHeight + 0.92f, s.Length * 0.5f - s.bumper - 0.1f);

        /// <summary>The roller section of one lane deck, centred, run along its own +Z (rotate it 90° about Y onto the car's X).</summary>
        public static RollerConveyorSpec LaneDeck(TransferCarSpec s) => new RollerConveyorSpec
        {
            length = s.LaneLength, width = s.laneWidth + 0.1f, bedHeight = s.deckHeight, centred = true,
            rollerPitch = s.rollerPitch, rollerRadius = s.rollerRadius, rollerSides = 12, frameHeight = 0.12f,
            frameDepth = 0.05f, legs = false, crossBraces = false, driveSide = RollerDriveSide.None,
        };

        /// <summary>The floor track for the car, along local +Z from 0 to <paramref name="length"/>: a steel bed plate
        /// (slot <see cref="Dark"/>), anchor plates (<see cref="Paint"/>), two rails (<see cref="Steel"/>) at the car's gauge.</summary>
        public static Mesh BuildTrack(TransferCarSpec s, float length, Mesh target = null, string name = "TransferCar_Track")
        {
            var g = new StorageMeshBuilder(SubMeshes);
            float gauge = s.RailGauge, half = gauge * 0.5f;
            g.BoxMinMax(Dark, new Vector3(-half - 0.3f, 0f, 0f), new Vector3(half + 0.3f, 0.006f, length), BoxFaces.Top);
            for (float z = 0.3f; z < length; z += 0.75f)
                g.BoxMinMax(Paint, new Vector3(-half - 0.14f, 0.006f, z - 0.08f), new Vector3(half + 0.14f, 0.012f, z + 0.08f), BoxFaces.NoBottom);
            foreach (float x in new[] { -half, half })
            {
                g.BoxMinMax(Steel, new Vector3(x - 0.06f, 0.006f, 0f), new Vector3(x + 0.06f, 0.016f, length), BoxFaces.NoBottom);
                g.BoxMinMax(Steel, new Vector3(x - 0.012f, 0.016f, 0f), new Vector3(x + 0.012f, s.railTop - 0.012f, length), BoxFaces.NoBottom);
                g.BoxMinMax(Steel, new Vector3(x - 0.028f, s.railTop - 0.012f, 0f), new Vector3(x + 0.028f, s.railTop, length), BoxFaces.NoBottom);
            }
            return g.ToMesh(target, name);
        }

        /// <summary>Default lit materials for the five slots (instanced): blue paint, steel, safety yellow, black, amber light.</summary>
        public static Material[] DefaultMaterials()
        {
            if (defaults != null && defaults[0] != null) return defaults;
            var light = RollerConveyorMesh.CreateMaterial("TransferCar_Beacon", new Color(1f, 0.62f, 0.08f), 0f, 0.7f);
            if (light.HasProperty("_EmissionColor")) { light.EnableKeyword("_EMISSION"); light.SetColor("_EmissionColor", new Color(0.9f, 0.45f, 0.02f)); }
            defaults = new[]
            {
                RollerConveyorMesh.CreateMaterial("TransferCar_Paint", new Color(0.11f, 0.29f, 0.55f), 0.3f, 0.5f),
                RollerConveyorMesh.CreateMaterial("TransferCar_Steel", new Color(0.55f, 0.57f, 0.60f), 0.85f, 0.55f),
                RollerConveyorMesh.CreateMaterial("TransferCar_Safety", new Color(0.97f, 0.74f, 0.05f), 0.1f, 0.5f),
                RollerConveyorMesh.CreateMaterial("TransferCar_Dark", new Color(0.07f, 0.07f, 0.08f), 0.2f, 0.35f),
                light,
            };
            return defaults;
        }

        private static Material[] defaults;

        // a prism along X (default), Z or Y
        private static void Cylinder(StorageMeshBuilder g, int sub, Vector3 c, float r, float w, int sides, bool alongZ = false, bool vertical = false)
        {
            Vector3 axis = vertical ? Vector3.up : alongZ ? Vector3.forward : Vector3.right;
            Vector3 u = vertical ? Vector3.right : Vector3.up;
            Vector3 v = Vector3.Cross(axis, u);
            Vector3 a0 = c - axis * (w * 0.5f), a1 = c + axis * (w * 0.5f);
            for (int i = 0; i < sides; i++)
            {
                float t0 = i * Mathf.PI * 2f / sides, t1 = (i + 1) * Mathf.PI * 2f / sides;
                Vector3 d0 = (u * Mathf.Cos(t0) + v * Mathf.Sin(t0)) * r, d1 = (u * Mathf.Cos(t1) + v * Mathf.Sin(t1)) * r;
                Vector3 nrm = (d0 + d1).normalized;
                g.Quad(sub, a0 + d0, a1 + d0, a1 + d1, a0 + d1, nrm, Vector2.zero, Vector2.right, Vector2.one, Vector2.up);
                g.Tri(sub, g.Add(a0, -axis, Vector2.zero), g.Add(a0 + d0, -axis, Vector2.right), g.Add(a0 + d1, -axis, Vector2.up), -axis);
                g.Tri(sub, g.Add(a1, axis, Vector2.zero), g.Add(a1 + d1, axis, Vector2.right), g.Add(a1 + d0, axis, Vector2.up), axis);
            }
        }
    }
}
