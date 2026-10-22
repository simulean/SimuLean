using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SimuLean.Unity
{
    /// <summary>Side-frame cross-section of a generated roller conveyor.</summary>
    public enum RollerFrameProfile
    {
        /// <summary>Folded sheet C-channel: web towards the rollers, flanges outwards (the roller axles show in the channel).</summary>
        CChannel = 0,
        /// <summary>Solid rectangular bar (the pre-2026-10 look).</summary>
        Box = 1,
    }

    /// <summary>Side of a generated roller conveyor that carries the chain guard and gear motor.</summary>
    public enum RollerDriveSide { None = 0, Left = -1, Right = 1 }

    /// <summary>
    /// Parameters of one generated roller-conveyor section (plain data; see <see cref="RollerConveyorMesh"/>).
    /// Local axes: +Z = run / flow direction (length), +X = width, +Y = up; roller tops at <see cref="bedHeight"/>.
    /// All sizes in metres.
    /// </summary>
    [System.Serializable]
    public class RollerConveyorSpec
    {
        public float length = 3.0f;
        /// <summary>Outer width, frame to frame.</summary>
        public float width = 0.6f;
        /// <summary>Roller-top (conveying surface) height above the floor.</summary>
        public float bedHeight = 0.5f;
        /// <summary>Run starts at z = −length/2 (centred) instead of z = 0.</summary>
        public bool centred;

        public float rollerPitch = 0.15f;
        public float rollerRadius = 0.03f;
        public int rollerSides = 12;
        /// <summary>Hexagonal axle ends just proud of the frame web, visible inside the C-channel.</summary>
        public bool rollerStubs = true;

        public RollerFrameProfile frameProfile = RollerFrameProfile.CChannel;
        /// <summary>Depth of each side frame across the width (C-channel flange width / bar width).</summary>
        public float frameDepth = 0.04f;
        public float frameHeight = 0.12f;
        /// <summary>Sheet thickness of the C-channel.</summary>
        public float frameThickness = 0.005f;
        /// <summary>Frame top below the roller tops (the load must clear the frame).</summary>
        public float frameTopGap = 0.015f;

        public bool legs = true;
        /// <summary>Maximum spacing between leg stands.</summary>
        public float legSpacing = 2.0f;
        /// <summary>Square-tube size of the legs.</summary>
        public float legSize = 0.05f;
        public bool legFeet = true;
        /// <summary>Leg stand at the start (−Z) / end (+Z); turn one off when sections are joined end to end.</summary>
        public bool legsAtStart = true, legsAtEnd = true;
        /// <summary>Low transverse tie between the two legs of every stand.</summary>
        public bool crossBraces = true;
        /// <summary>Diagonal brace in the side planes every N bays (0 = none).</summary>
        public int diagonalBraceEvery = 3;

        public RollerDriveSide driveSide = RollerDriveSide.None;

        public int RollerCount => Mathf.Max(1, Mathf.RoundToInt(length / Mathf.Max(0.02f, rollerPitch)));
        public float ActualPitch => length / RollerCount;
        public RollerConveyorSpec Clone() => (RollerConveyorSpec)MemberwiseClone();
    }

    /// <summary>
    /// Procedural roller-conveyor geometry shared by <see cref="UnityRollerConveyor"/> and scene builders.
    /// <para><b>LOD0</b>: smooth-shaded lathed rollers with tapered ends and hexagonal axle ends in the channel,
    /// C-channel side frames, square-tube legs with foot plates, cross ties under the frame and low,
    /// diagonal side braces, optional chain guard + gear motor. <b>LOD1</b>: the rollers become one flat
    /// bed quad textured with roller stripes (<see cref="CreateBedTexture"/>), the frame boxes and legs stay,
    /// the small parts go — about 5 % of the LOD0 triangles.</para>
    /// <para>Both levels have two sub-meshes: 0 = painted frame (frame material), 1 = rollers (LOD0, roller
    /// material) / roller bed (LOD1, bed material). UVs are planar, in metres (for a small tiling detail
    /// texture such as <see cref="CreatePaintTexture"/>). Rollers sit at <c>(i + 0.5) · pitch</c>, so sections of
    /// the same pitch joined end to end continue the lattice exactly.</para>
    /// </summary>
    public static class RollerConveyorMesh
    {
        /// <summary>Default LODGroup reference size (m): LOD switches by how large the rollers look, not the run length.</summary>
        public const float LodReferenceSize = 3f;

        // ── public API ───────────────────────────────────────────────────────

        /// <summary>Builds level <paramref name="lod"/> (0 or 1) into <paramref name="target"/> (cleared) or a new mesh.</summary>
        public static Mesh Build(RollerConveyorSpec s, int lod, Mesh target = null, string name = null)
        {
            var g = new Geo();
            Generate(s, lod, g);
            Mesh m = target != null ? target : new Mesh();
            m.Clear();
            if (name != null) m.name = name;
            m.indexFormat = g.v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m.SetVertices(g.v);
            m.SetNormals(g.n);
            m.SetUVs(0, g.uv);
            m.subMeshCount = 2;
            m.SetTriangles(g.t[0], 0, false);
            m.SetTriangles(g.t[1], 1, false);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// Adds (or reuses) a LODGroup on <paramref name="root"/> with <paramref name="lod0"/> / <paramref name="lod1"/>.
        /// The reference size is fixed (<paramref name="referenceSize"/>, default <see cref="LodReferenceSize"/>) so a
        /// long run switches when its rollers get small, not when the whole run does. <paramref name="lod1Below"/> =
        /// relative screen height where LOD1 takes over (0.10 ≈ 50 m at a 60° FOV and LOD bias 2);
        /// <paramref name="cullBelow"/> = 0 keeps LOD1 at any distance.
        /// </summary>
        public static LODGroup SetupLodGroup(GameObject root, Renderer lod0, Renderer lod1,
                                             float lod1Below = 0.10f, float cullBelow = 0.002f, float referenceSize = LodReferenceSize)
        {
            var group = root.GetComponent<LODGroup>();
            if (group == null) group = root.AddComponent<LODGroup>();
            group.fadeMode = LODFadeMode.None;
            group.SetLODs(new[] { new LOD(lod1Below, new[] { lod0 }), new LOD(cullBelow, new[] { lod1 }) });
            group.RecalculateBounds();
            group.size = referenceSize;
            return group;
        }

        /// <summary>
        /// Roller-stripe texture for the LOD1 bed: one roller per texture repeat along U (roller centre at
        /// U = 0.5, <paramref name="rollerFraction"/> = diameter / pitch), shaded like a lit cylinder over the gap
        /// (<paramref name="gapShade"/>: what shows between the rollers — the floor of an open conveyor, so mid grey
        /// keeps far conveyors as light as the real ones; use a low value over a deck plate).
        /// Grey-scale: the bed material colour tints it. Repeat along U, clamp along V, trilinear + aniso.
        /// </summary>
        public static Texture2D CreateBedTexture(float rollerFraction = 0.3f, float gapShade = 0.5f, int width = 64)
        {
            var tex = new Texture2D(width, 4, TextureFormat.RGBA32, true) { name = "RollerBed", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
            var px = new Color[width * 4];
            float half = Mathf.Clamp(rollerFraction, 0.05f, 0.95f) / 2f;
            for (int x = 0; x < width; x++)
            {
                float u = (x + 0.5f) / width;
                float d = Mathf.Abs(u - 0.5f) / half;
                float c;
                if (d < 1f)
                {
                    float nz = Mathf.Sqrt(1f - d * d);                       // cylinder normal · up
                    float spec = Mathf.Pow(Mathf.Max(0f, 1f - Mathf.Abs(d - 0.25f) * 3.5f), 3f);
                    c = 0.38f + 0.52f * nz + 0.10f * spec;
                }
                else c = gapShade * (0.85f + 0.15f * Mathf.Clamp01((d - 1f) * 2f));   // gap, darker next to the roller (contact shadow)
                for (int y = 0; y < 4; y++) px[y * width + x] = new Color(c, c, c, 1f);
            }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>
        /// Small tileable detail texture (grey 0.84–1.0: soft blotches + fine grain) to break up flat paint and
        /// galvanised steel. Use as the albedo map with the paint / steel colour as the material colour, tiling 1 / m.
        /// </summary>
        public static Texture2D CreatePaintTexture(int size = 128, int seed = 7)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "RollerConveyorPaint", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var rnd = new System.Random(seed);
            const int waves = 9;
            var fx = new int[waves]; var fy = new int[waves]; var ph = new float[waves]; var amp = new float[waves];
            for (int k = 0; k < waves; k++)
            {
                fx[k] = rnd.Next(-6, 7); fy[k] = rnd.Next(-6, 7);
                if (fx[k] == 0 && fy[k] == 0) fx[k] = 1;
                ph[k] = (float)rnd.NextDouble() * 6.2832f;
                amp[k] = 1f / Mathf.Sqrt(fx[k] * fx[k] + fy[k] * fy[k]);
            }
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (float)x / size, v = (float)y / size, w = 0f, a = 0f;
                    for (int k = 0; k < waves; k++) { w += amp[k] * Mathf.Sin(6.2832f * (fx[k] * u + fy[k] * v) + ph[k]); a += amp[k]; }
                    w = w / a;                                                   // ≈ −1..1
                    float grain = (float)rnd.NextDouble() - 0.5f;
                    float c = Mathf.Clamp(0.93f + 0.05f * w + 0.04f * grain, 0.84f, 1f);
                    px[y * size + x] = new Color(c, c, c, 1f);
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>Lit material (URP/Lit, else Standard) with GPU instancing on.</summary>
        public static Material CreateMaterial(string name, Color color, float metallic, float smoothness, Texture2D albedo = null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Diffuse");
            var m = new Material(shader) { name = name, enableInstancing = true };
            ApplyLook(m, color, metallic, smoothness, albedo);
            return m;
        }

        /// <summary>Sets colour, metallic, smoothness and (optionally) the albedo map on a URP/Lit or Standard material.</summary>
        public static void ApplyLook(Material m, Color color, float metallic, float smoothness, Texture2D albedo)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
            if (albedo != null)
            {
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", albedo);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", albedo);
            }
            m.enableInstancing = true;
        }

        /// <summary>Number of LOD0 / LOD1 triangles of a mesh (all sub-meshes).</summary>
        public static int TriangleCount(Mesh m)
        {
            if (m == null) return 0;
            long t = 0;
            for (int i = 0; i < m.subMeshCount; i++) t += m.GetIndexCount(i) / 3;
            return (int)t;
        }

        // ── generation ───────────────────────────────────────────────────────

        private const int FrameSub = 0, RollerSub = 1;

        private static void Generate(RollerConveyorSpec s, int lod, Geo g)
        {
            float L = Mathf.Max(0.05f, s.length);
            float z0 = s.centred ? -L / 2f : 0f, z1 = z0 + L;
            float hw = Mathf.Max(0.05f, s.width / 2f);
            float d = Mathf.Clamp(s.frameDepth, 0.005f, hw * 0.45f);
            float r = Mathf.Max(0.004f, s.rollerRadius);
            float top = s.bedHeight - Mathf.Max(0f, s.frameTopGap);          // frame top
            float bot = Mathf.Max(0.02f, top - Mathf.Max(0.02f, s.frameHeight));
            float t = Mathf.Clamp(s.frameThickness, 0.001f, d * 0.5f);
            float xw = hw - d;                                                  // web inner face (roller side)
            const float clr = 0.006f;                                           // roller end ↔ web
            float xr = Mathf.Max(0.01f, xw - clr);                              // roller half length
            int n = s.RollerCount;
            float pitch = L / n;
            float yc = s.bedHeight - r;                                         // roller axis

            // ---- side frames
            foreach (int side in new[] { -1, 1 })
            {
                if (lod == 0 && s.frameProfile == RollerFrameProfile.CChannel)
                {
                    var prof = new[]
                    {
                        new Vector2(xw, bot), new Vector2(hw, bot), new Vector2(hw, bot + t), new Vector2(xw + t, bot + t),
                        new Vector2(xw + t, top - t), new Vector2(hw, top - t), new Vector2(hw, top), new Vector2(xw, top),
                    };
                    for (int i = 0; i < prof.Length; i++) prof[i].x *= side;
                    g.Extrude(FrameSub, prof, z0, z1, new[] { new[] { 0, 1, 2, 3 }, new[] { 3, 4, 7, 0 }, new[] { 4, 5, 6, 7 } });
                }
                else
                    g.Extrude(FrameSub, Rect(side * xw, bot, side * hw, top), z0, z1, new[] { new[] { 0, 1, 2, 3 } }, skipEdge: 0);
            }

            // ---- rollers / bed
            if (lod == 0)
            {
                int sides = Mathf.Clamp(s.rollerSides, 5, 32);
                float re = r * 0.82f, e = Mathf.Min(0.006f, xr * 0.1f);
                float sr = Mathf.Clamp(r * 0.28f, 0.005f, 0.03f);              // axle stub radius
                for (int i = 0; i < n; i++)
                {
                    float z = z0 + (i + 0.5f) * pitch;
                    var axis = Matrix4x4.Translate(new Vector3(0f, yc, z));      // lathe axis = local X
                    g.Lathe(RollerSub, axis, new[] { new Vector2(-xr, re), new Vector2(-xr + e, r), new Vector2(xr - e, r), new Vector2(xr, re) },
                            sides, smooth: true, capStart: false, capEnd: false);
                    if (s.rollerStubs && s.frameProfile == RollerFrameProfile.CChannel)
                        foreach (int side in new[] { -1, 1 })                // axle end: hexagon just proud of the web, inside the channel
                            g.Disc(RollerSub, axis, side * (xw + t + 0.008f), sr, 6, side);
                }
            }
            else
            {
                float yb = s.bedHeight - r * 0.4f;
                float za = z0 + 0.0f, zb = z1;
                int b = g.v.Count;
                foreach (var (x, z) in new[] { (-xw, za), (xw, za), (xw, zb), (-xw, zb) })
                {
                    g.v.Add(new Vector3(x, yb, z));
                    g.n.Add(Vector3.up);
                    g.uv.Add(new Vector2((z - z0) / pitch, (x + xw) / (2f * xw)));   // roller i centred at u = i + 0.5
                }
                g.Quad(RollerSub, b, b + 1, b + 2, b + 3, Vector3.up);
            }

            // ---- legs, ties, braces
            var stands = new List<float>();
            float ls = Mathf.Clamp(s.legSize, 0.015f, 0.3f);
            if (s.legs && bot > 0.06f)
            {
                float inset = ls / 2f + 0.03f;
                int m = Mathf.Max(2, Mathf.CeilToInt((L - 2f * inset) / Mathf.Max(0.3f, s.legSpacing)) + 1);
                for (int k = 0; k < m; k++)
                {
                    if (k == 0 && !s.legsAtStart) continue;
                    if (k == m - 1 && !s.legsAtEnd) continue;
                    stands.Add(z0 + inset + (L - 2f * inset) * k / (m - 1));
                }
            }
            float xl = hw - ls / 2f;                                            // leg centre (outer face flush with the frame)
            float tieH = Mathf.Min(0.05f, bot * 0.25f), tieY = Mathf.Min(0.16f, bot * 0.35f);
            foreach (float z in stands)
            {
                foreach (int side in new[] { -1, 1 })
                {
                    g.Box(FrameSub, new Vector3(side * xl, bot / 2f, z), new Vector3(ls, bot, ls), Quaternion.identity, Faces.Sides);
                    if (lod == 0 && s.legFeet)
                        g.Box(FrameSub, new Vector3(side * xl, 0.006f, z), new Vector3(ls * 2.4f, 0.012f, ls * 2.4f), Quaternion.identity, Faces.All & ~Faces.Bottom);
                }
                // cross member right under the frames (carries them) — kept at LOD1
                g.Box(FrameSub, new Vector3(0f, bot - tieH / 2f, z), new Vector3(2f * xl - ls, tieH, ls * 0.8f), Quaternion.identity, Faces.All & ~(Faces.Left | Faces.Right));
                if (lod == 0 && s.crossBraces && bot - tieH > tieY + tieH)
                    g.Box(FrameSub, new Vector3(0f, tieY, z), new Vector3(2f * xl - ls, tieH * 0.8f, ls * 0.6f), Quaternion.identity, Faces.All & ~(Faces.Left | Faces.Right));
            }
            if (lod == 0 && s.diagonalBraceEvery > 0 && stands.Count > 1)
            {
                int every = s.diagonalBraceEvery;
                for (int j = 0; j < stands.Count - 1; j++)
                {
                    if (j % every != every / 2) continue;
                    float za = stands[j] + ls / 2f, zb = stands[j + 1] - ls / 2f;
                    float ya = tieY, yb = bot - tieH - 0.02f;
                    if (yb - ya < 0.08f || zb - za < 0.1f) continue;
                    bool flip = (j / every) % 2 == 1;
                    var p0 = new Vector3(0f, ya, flip ? zb : za);
                    var p1 = new Vector3(0f, yb, flip ? za : zb);
                    foreach (int side in new[] { -1, 1 })
                    {
                        Vector3 a = p0, b = p1; a.x = b.x = side * (xl - ls / 2f - 0.008f);
                        Vector3 dir = (b - a).normalized;
                        var rot = Quaternion.LookRotation(dir, Vector3.Cross(dir, Vector3.right));
                        g.Box(FrameSub, (a + b) / 2f, new Vector3(0.012f, 0.035f, (b - a).magnitude), rot, Faces.All);
                    }
                }
            }

            // ---- drive: chain guard along the outside of one frame + gear motor under it
            if (s.driveSide != RollerDriveSide.None)
            {
                float sd = (int)s.driveSide;
                float gw = Mathf.Clamp(d * 1.6f, 0.05f, 0.12f);
                float ga = z0 + Mathf.Min(0.12f, L * 0.05f), gb = z1 - Mathf.Min(0.12f, L * 0.05f);
                float gy0 = bot - 0.03f, gy1 = top - 0.008f, ch = gw * 0.35f;
                var prof = new[] { new Vector2(hw, gy0), new Vector2(hw + gw, gy0), new Vector2(hw + gw, gy1 - ch), new Vector2(hw + gw - ch, gy1), new Vector2(hw, gy1) };
                for (int i = 0; i < prof.Length; i++) prof[i].x *= sd;
                if (lod == 0) g.Extrude(FrameSub, prof, ga, gb, new[] { new[] { 0, 1, 2, 3, 4 } }, skipEdge: 4);
                else g.Extrude(FrameSub, Rect(sd * hw, gy0, sd * (hw + gw), gy1), ga, gb, new[] { new[] { 0, 1, 2, 3 } }, skipEdge: 0);

                // motor in the middle of the bay nearest the centre (clear of the legs)
                float zm = (z0 + z1) / 2f;
                if (stands.Count > 1)
                {
                    float best = float.MaxValue;
                    for (int j = 0; j < stands.Count - 1; j++)
                    {
                        float c = (stands[j] + stands[j + 1]) / 2f;
                        if (Mathf.Abs(c - zm) < Mathf.Abs(best - zm)) best = c;
                    }
                    zm = best;
                }
                float bh = Mathf.Min(0.22f, bot * 0.5f), bx = sd * (hw + gw * 0.5f);
                if (bh > 0.06f)
                {
                    g.Box(FrameSub, new Vector3(bx, gy0 - bh / 2f, zm), new Vector3(gw * 1.4f, bh, bh), Quaternion.identity, Faces.All);
                    if (lod == 0)
                    {
                        float mr = bh * 0.38f, ml = bh * 1.5f;
                        var axis = Matrix4x4.TRS(new Vector3(bx, gy0 - bh * 0.55f, zm + bh / 2f), Quaternion.Euler(0f, -90f, 0f), Vector3.one);   // local X → +Z
                        g.Lathe(FrameSub, axis, new[] { new Vector2(0f, mr), new Vector2(ml, mr) }, 10, smooth: true, capStart: false, capEnd: true);
                    }
                }
            }
        }

        private static Vector2[] Rect(float xa, float ya, float xb, float yb) =>
            new[] { new Vector2(xa, ya), new Vector2(xb, ya), new Vector2(xb, yb), new Vector2(xa, yb) };

        [System.Flags]
        private enum Faces { Right = 1, Left = 2, Top = 4, Bottom = 8, Front = 16, Back = 32, All = 63, Sides = Right | Left | Front | Back }

        /// <summary>Vertex / triangle accumulator. Triangles are oriented from a wanted normal (Unity: Cross(b − a, c − a) faces out).</summary>
        private sealed class Geo
        {
            public readonly List<Vector3> v = new List<Vector3>();
            public readonly List<Vector3> n = new List<Vector3>();
            public readonly List<Vector2> uv = new List<Vector2>();
            public readonly List<int>[] t = { new List<int>(), new List<int>() };

            public void Tri(int sub, int a, int b, int c, Vector3 want)
            {
                if (Vector3.Dot(Vector3.Cross(v[b] - v[a], v[c] - v[a]), want) < 0f) { int x = b; b = c; c = x; }
                t[sub].Add(a); t[sub].Add(b); t[sub].Add(c);
            }

            public void Quad(int sub, int a, int b, int c, int d, Vector3 want)
            {
                Tri(sub, a, b, c, want);
                Tri(sub, a, c, d, want);
            }

            private int Add(Vector3 p, Vector3 nn, Vector2 u) { v.Add(p); n.Add(nn); uv.Add(u); return v.Count - 1; }

            /// <summary>Extrudes the closed XY polygon <paramref name="prof"/> along Z (flat-shaded); <paramref name="caps"/> = convex index polygons for both ends.</summary>
            public void Extrude(int sub, Vector2[] prof, float z0, float z1, int[][] caps, int skipEdge = -1)
            {
                float area = 0f;
                for (int i = 0; i < prof.Length; i++) { var p = prof[i]; var q = prof[(i + 1) % prof.Length]; area += p.x * q.y - q.x * p.y; }
                float o = area >= 0f ? 1f : -1f;                                // CCW: outward normal of edge (dx, dy) is (dy, −dx)
                float along = 0f;
                for (int i = 0; i < prof.Length; i++)
                {
                    var p = prof[i]; var q = prof[(i + 1) % prof.Length];
                    float len = (q - p).magnitude;
                    if (i == skipEdge || len < 1e-5f) { along += len; continue; }
                    var nn = new Vector3(o * (q.y - p.y), -o * (q.x - p.x), 0f).normalized;
                    int a = Add(new Vector3(p.x, p.y, z0), nn, new Vector2(z0, along));
                    int b = Add(new Vector3(q.x, q.y, z0), nn, new Vector2(z0, along + len));
                    int c = Add(new Vector3(q.x, q.y, z1), nn, new Vector2(z1, along + len));
                    int d = Add(new Vector3(p.x, p.y, z1), nn, new Vector2(z1, along));
                    Quad(sub, a, b, c, d, nn);
                    along += len;
                }
                foreach (float z in new[] { z0, z1 })
                {
                    var nn = new Vector3(0f, 0f, z == z0 ? -1f : 1f);
                    foreach (var poly in caps)
                    {
                        int b0 = v.Count;
                        foreach (int k in poly) Add(new Vector3(prof[k].x, prof[k].y, z), nn, prof[k]);
                        for (int k = 1; k < poly.Length - 1; k++) Tri(sub, b0, b0 + k, b0 + k + 1, nn);
                    }
                }
            }

            /// <summary>
            /// Surface of revolution around the local X axis of <paramref name="m"/> (rigid transform): profile = (x, radius).
            /// Smooth = radial normals (round); flat = one normal per side (faceted).
            /// </summary>
            public void Lathe(int sub, Matrix4x4 m, Vector2[] prof, int sides, bool smooth, bool capStart, bool capEnd)
            {
                float step = 2f * Mathf.PI / sides;
                if (smooth)
                {
                    int b0 = v.Count;                                           // no seam duplicate: rings wrap (UV U folds back on the last side)
                    foreach (var pr in prof)
                        for (int k = 0; k < sides; k++)
                        {
                            float a = k * step;
                            var rn = new Vector3(0f, Mathf.Cos(a), Mathf.Sin(a));
                            Add(m.MultiplyPoint3x4(new Vector3(pr.x, 0f, 0f) + rn * pr.y), m.MultiplyVector(rn), new Vector2(a * pr.y, pr.x));
                        }
                    for (int j = 0; j < prof.Length - 1; j++)
                        for (int k = 0; k < sides; k++)
                        {
                            int k1 = (k + 1) % sides;
                            int a = b0 + j * sides + k, b = b0 + j * sides + k1, c = b0 + (j + 1) * sides + k1, d = b0 + (j + 1) * sides + k;
                            var want = m.MultiplyVector(new Vector3(0f, Mathf.Cos((k + 0.5f) * step), Mathf.Sin((k + 0.5f) * step)));
                            Quad(sub, a, b, c, d, want);
                        }
                }
                else
                {
                    for (int j = 0; j < prof.Length - 1; j++)
                        for (int k = 0; k < sides; k++)
                        {
                            float a0 = k * step, a1 = (k + 1) * step, am = (k + 0.5f) * step;
                            var nn = m.MultiplyVector(new Vector3(0f, Mathf.Cos(am), Mathf.Sin(am)));
                            Vector3 P(float x, float r, float ang) => m.MultiplyPoint3x4(new Vector3(x, Mathf.Cos(ang) * r, Mathf.Sin(ang) * r));
                            var p0 = prof[j]; var p1 = prof[j + 1];
                            int a = Add(P(p0.x, p0.y, a0), nn, new Vector2(a0 * p0.y, p0.x));
                            int b = Add(P(p0.x, p0.y, a1), nn, new Vector2(a1 * p0.y, p0.x));
                            int c = Add(P(p1.x, p1.y, a1), nn, new Vector2(a1 * p1.y, p1.x));
                            int d = Add(P(p1.x, p1.y, a0), nn, new Vector2(a0 * p1.y, p1.x));
                            Quad(sub, a, b, c, d, nn);
                        }
                }
                if (capStart) Cap(sub, m, prof[0], sides, -1f);
                if (capEnd) Cap(sub, m, prof[prof.Length - 1], sides, 1f);
            }

            /// <summary>Flat polygon of radius <paramref name="r"/> across the local X axis of <paramref name="m"/> at x, facing ±X.</summary>
            public void Disc(int sub, Matrix4x4 m, float x, float r, int sides, float dir) => Cap(sub, m, new Vector2(x, r), sides, dir);

            private void Cap(int sub, Matrix4x4 m, Vector2 pr, int sides, float dir)
            {
                var nn = m.MultiplyVector(new Vector3(dir, 0f, 0f));
                float step = 2f * Mathf.PI / sides;
                int b0 = v.Count;
                for (int k = 0; k < sides; k++)
                {
                    float a = k * step;
                    Add(m.MultiplyPoint3x4(new Vector3(pr.x, Mathf.Cos(a) * pr.y, Mathf.Sin(a) * pr.y)), nn, new Vector2(Mathf.Cos(a) * pr.y, Mathf.Sin(a) * pr.y));
                }
                for (int k = 1; k < sides - 1; k++) Tri(sub, b0, b0 + k, b0 + k + 1, nn);
            }

            private static readonly Vector3[] FaceDirs = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            private static readonly Vector2[] Corners = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };

            /// <summary>Box of <paramref name="size"/> at <paramref name="c"/>, rotated by <paramref name="rot"/>; <paramref name="faces"/> picks the faces (local axes).</summary>
            public void Box(int sub, Vector3 c, Vector3 size, Quaternion rot, Faces faces)
            {
                for (int f = 0; f < 6; f++)
                {
                    if (((int)faces & (1 << f)) == 0) continue;
                    Vector3 fd = FaceDirs[f];
                    Vector3 u = Mathf.Abs(fd.y) > 0.5f ? Vector3.right : Vector3.up;
                    Vector3 w = Vector3.Cross(fd, u);
                    var nn = rot * fd;
                    int b0 = v.Count;
                    foreach (var q in Corners)
                    {
                        Vector3 local = Vector3.Scale(fd + u * q.x + w * q.y, size * 0.5f);
                        Vector3 p = c + rot * local;
                        Add(p, nn, new Vector2(Vector3.Dot(local, u) + Vector3.Dot(c, rot * u), Vector3.Dot(local, w) + Vector3.Dot(c, rot * w)));
                    }
                    Quad(sub, b0, b0 + 1, b0 + 2, b0 + 3, nn);
                }
            }
        }
    }
}
