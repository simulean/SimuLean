using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SimuLean.Unity
{
    /// <summary>
    /// Sub-mesh (material slot) indices shared by the procedural storage generators
    /// (<see cref="PalletRackMesh"/>, <see cref="CartonFlowRackMesh"/>), so every rack mesh takes the same material array
    /// (<see cref="StorageRackVisuals.DefaultMaterials"/>). A generator leaves the slots it does not use empty.
    /// </summary>
    public static class StorageRackSlots
    {
        /// <summary>Upright frames: uprights (punched-slot texture), base plates, frame bracing.</summary>
        public const int Frame = 0;
        /// <summary>Step beams, beam connectors, drive-in rails, shelf beams (safety orange).</summary>
        public const int Beam = 1;
        /// <summary>Galvanised parts: support bars, deck channels, row spacers, anchor bolts, lane dividers, end stops.</summary>
        public const int Steel = 2;
        /// <summary>Yellow / black safety parts: upright protectors, end-of-aisle guards.</summary>
        public const int Safety = 3;
        /// <summary>Location labels and load signs (white plates).</summary>
        public const int Label = 4;
        /// <summary>Wire-mesh decks (grid texture).</summary>
        public const int Deck = 5;
        /// <summary>Carton-flow wheel beds (wheel-stripe texture).</summary>
        public const int Track = 6;
        /// <summary>Number of material slots.</summary>
        public const int Count = 7;
    }

    /// <summary>Faces of a generated box, in its local axes.</summary>
    [System.Flags]
    internal enum BoxFaces
    {
        Right = 1, Left = 2, Top = 4, Bottom = 8, Front = 16, Back = 32,
        All = 63, Sides = Right | Left | Front | Back, NoBottom = All & ~Bottom, NoEnds = Right | Left | Top | Bottom
    }

    /// <summary>
    /// Vertex / triangle accumulator of the storage generators (flat-shaded boxes, extruded profiles, quads), with
    /// one triangle list per material slot. Triangles are oriented from a wanted normal (Unity: Cross(b − a, c − a)
    /// faces out), so mirrored profiles and reflection frames need no special care.
    /// </summary>
    internal sealed class StorageMeshBuilder
    {
        public readonly List<Vector3> v = new List<Vector3>();
        public readonly List<Vector3> n = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<int>[] t;

        public StorageMeshBuilder(int subMeshes)
        {
            t = new List<int>[subMeshes];
            for (int i = 0; i < subMeshes; i++) t[i] = new List<int>();
        }

        public int TriangleCount
        {
            get { int c = 0; foreach (var l in t) c += l.Count / 3; return c; }
        }

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

        public int Add(Vector3 p, Vector3 nn, Vector2 u) { v.Add(p); n.Add(nn); uv.Add(u); return v.Count - 1; }

        /// <summary>Flat quad p0..p3 (in order around it) facing <paramref name="nrm"/>, with explicit UVs.</summary>
        public void Quad(int sub, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 nrm,
                         Vector2 u0, Vector2 u1, Vector2 u2, Vector2 u3)
        {
            int a = Add(p0, nrm, u0), b = Add(p1, nrm, u1), c = Add(p2, nrm, u2), d = Add(p3, nrm, u3);
            Quad(sub, a, b, c, d, nrm);
        }

        private static readonly Vector3[] FaceDirs = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        private static readonly Vector2[] Corners = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };

        /// <summary>
        /// Box of <paramref name="size"/> centred at <paramref name="c"/>, rotated by <paramref name="rot"/>. UVs are
        /// planar in metres × <paramref name="uvScale"/> (continuous across boxes), or all <paramref name="fixedUv"/>
        /// (a plain spot of a patterned texture).
        /// </summary>
        public void Box(int sub, Vector3 c, Vector3 size, Quaternion rot, BoxFaces faces, float uvScale = 1f, Vector2? fixedUv = null)
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
                    Vector2 tex = fixedUv ?? new Vector2(Vector3.Dot(local, u) + Vector3.Dot(c, rot * u),
                                                         Vector3.Dot(local, w) + Vector3.Dot(c, rot * w)) * uvScale;
                    Add(p, nn, tex);
                }
                Quad(sub, b0, b0 + 1, b0 + 2, b0 + 3, nn);
            }
        }

        /// <summary>Axis-aligned box between two corners.</summary>
        public void BoxMinMax(int sub, Vector3 min, Vector3 max, BoxFaces faces, float uvScale = 1f, Vector2? fixedUv = null)
            => Box(sub, (min + max) * 0.5f, max - min, Quaternion.identity, faces, uvScale, fixedUv);

        /// <summary>
        /// A straight member from <paramref name="a"/> to <paramref name="b"/>: <paramref name="sideSize"/> along
        /// <paramref name="sideAxis"/> (must not be parallel to the member), <paramref name="otherSize"/> across both.
        /// End faces are left out (members end inside the part they join).
        /// </summary>
        public void Member(int sub, Vector3 a, Vector3 b, Vector3 sideAxis, float sideSize, float otherSize,
                           float uvScale = 1f, Vector2? fixedUv = null)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-4f) return;
            Vector3 dir = d / len;
            Vector3 up = Vector3.Cross(dir, sideAxis);
            if (up.sqrMagnitude < 1e-8f) return;
            var rot = Quaternion.LookRotation(dir, up.normalized);
            Box(sub, (a + b) * 0.5f, new Vector3(sideSize, otherSize, len), rot, BoxFaces.NoEnds, uvScale, fixedUv);
        }

        /// <summary>
        /// Extrudes the closed polygon <paramref name="prof"/> (local XY) along local Z from <paramref name="z0"/> to
        /// <paramref name="z1"/>, mapped by the rigid (possibly mirroring) matrix <paramref name="m"/>; flat-shaded.
        /// <paramref name="caps"/> = convex index polygons for the end caps (start / end each optional). UVs: U = distance
        /// round the profile, V = position along the extrusion, both × <paramref name="uvScale"/>.
        /// </summary>
        public void Extrude(int sub, Vector2[] prof, Matrix4x4 m, float z0, float z1, int[][] caps,
                            bool capStart, bool capEnd, float uvScale = 1f)
        {
            float area = 0f;
            for (int i = 0; i < prof.Length; i++) { var p = prof[i]; var q = prof[(i + 1) % prof.Length]; area += p.x * q.y - q.x * p.y; }
            float o = area >= 0f ? 1f : -1f;                                // CCW: outward normal of edge (dx, dy) is (dy, −dx)
            float along = 0f;
            for (int i = 0; i < prof.Length; i++)
            {
                var p = prof[i]; var q = prof[(i + 1) % prof.Length];
                float len = (q - p).magnitude;
                if (len < 1e-6f) continue;
                var nl = new Vector3(o * (q.y - p.y), -o * (q.x - p.x), 0f).normalized;
                var nn = m.MultiplyVector(nl).normalized;
                int a = Add(m.MultiplyPoint3x4(new Vector3(p.x, p.y, z0)), nn, new Vector2(along, z0) * uvScale);
                int b = Add(m.MultiplyPoint3x4(new Vector3(q.x, q.y, z0)), nn, new Vector2(along + len, z0) * uvScale);
                int c = Add(m.MultiplyPoint3x4(new Vector3(q.x, q.y, z1)), nn, new Vector2(along + len, z1) * uvScale);
                int d = Add(m.MultiplyPoint3x4(new Vector3(p.x, p.y, z1)), nn, new Vector2(along, z1) * uvScale);
                Quad(sub, a, b, c, d, nn);
                along += len;
            }
            if (caps == null) return;
            for (int e = 0; e < 2; e++)
            {
                if (e == 0 ? !capStart : !capEnd) continue;
                float z = e == 0 ? z0 : z1;
                var nn = m.MultiplyVector(new Vector3(0f, 0f, e == 0 ? -1f : 1f)).normalized;
                foreach (var poly in caps)
                {
                    int b0 = v.Count;
                    foreach (int k in poly) Add(m.MultiplyPoint3x4(new Vector3(prof[k].x, prof[k].y, z)), nn, prof[k] * uvScale);
                    for (int k = 1; k < poly.Length - 1; k++) Tri(sub, b0, b0 + k, b0 + k + 1, nn);
                }
            }
        }

        /// <summary>Writes the accumulated geometry into <paramref name="target"/> (cleared) or a new mesh.</summary>
        public Mesh ToMesh(Mesh target, string name)
        {
            Mesh m = target != null ? target : new Mesh();
            m.Clear();
            if (name != null) m.name = name;
            m.indexFormat = v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m.SetVertices(v);
            m.SetNormals(n);
            m.SetUVs(0, uv);
            m.subMeshCount = t.Length;
            for (int i = 0; i < t.Length; i++) m.SetTriangles(t[i], i, false);
            m.RecalculateBounds();
            return m;
        }
    }

    /// <summary>
    /// Frame parts shared by the rack generators: perforated upright (open C / omega profile with lips), base plate
    /// with anchor bolts, and the frame bracing between two uprights (Z or zig-zag pattern).
    /// </summary>
    internal static class RackFrameParts
    {
        /// <summary>Upright texture tile (m): the punched-slot texture repeats every 0.1 m across the face and up.</summary>
        public const float UprightTile = 0.1f;
        /// <summary>A plain (unpunched) spot of the upright texture, for boxes and bracing in the frame slot.</summary>
        public static readonly Vector2 PlainUv = new Vector2(0.5f, 0.5f);

        /// <summary>
        /// One upright, <paramref name="width"/> along X × <paramref name="depth"/> along Z, from the floor to
        /// <paramref name="height"/>, centred at (<paramref name="x"/>, <paramref name="zc"/>). LOD0: folded open
        /// profile (web on the −Z side when <paramref name="opensPositiveZ"/>, i.e. facing the aisle, flanges and lips
        /// towards the frame interior) with the punched-slot UVs; LOD1/2: a box with the plain UV.
        /// </summary>
        public static void Upright(StorageMeshBuilder g, int lod, float x, float zc, float width, float depth, float height,
                                   bool opensPositiveZ)
        {
            if (lod > 0)
            {
                g.Box(StorageRackSlots.Frame, new Vector3(x, height / 2f, zc), new Vector3(width, height, depth), Quaternion.identity,
                      BoxFaces.Sides | BoxFaces.Top, 1f, PlainUv);
                return;
            }
            float a = width / 2f, b = depth / 2f, th = Mathf.Clamp(width * 0.03f, 0.002f, 0.004f), lip = Mathf.Min(width * 0.22f, 0.025f);
            // local (u = X offset, w = Z offset): web at w = −b, flanges at u = ±a, lips turned inwards at w = +b
            var prof = new[]
            {
                new Vector2(-a, -b), new Vector2(a, -b), new Vector2(a, b), new Vector2(a - lip, b),
                new Vector2(a - lip, b - th), new Vector2(a - th, b - th), new Vector2(a - th, -b + th), new Vector2(-a + th, -b + th),
                new Vector2(-a + th, b - th), new Vector2(-a + lip, b - th), new Vector2(-a + lip, b), new Vector2(-a, b),
            };
            float s = opensPositiveZ ? 1f : -1f;
            // local x → world X, local y → world Z (mirrored for a rear upright), local z (extrusion) → world Y
            var m = new Matrix4x4(new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 0f, s, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(x, 0f, zc, 1f));
            var caps = new[] { new[] { 0, 1, 6, 7 }, new[] { 1, 2, 5, 6 }, new[] { 2, 3, 4, 5 }, new[] { 11, 0, 7, 8 }, new[] { 10, 11, 8, 9 } };
            // U from point 0: the first edge is the aisle face (web), one texture tile wide for a 0.1 m upright
            g.Extrude(StorageRackSlots.Frame, prof, m, 0f, height, caps, false, true, 1f / UprightTile);
        }

        /// <summary>Base (foot) plate under an upright, with two anchor bolts (LOD0 only).</summary>
        public static void BasePlate(StorageMeshBuilder g, float x, float zc, float width, float depth)
        {
            float pw = width + 0.05f, pd = depth + 0.06f, pt = 0.008f;
            g.Box(StorageRackSlots.Frame, new Vector3(x, pt / 2f, zc), new Vector3(pw, pt, pd), Quaternion.identity, BoxFaces.NoBottom, 1f, PlainUv);
            foreach (int side in new[] { -1, 1 })
            {
                var c = new Vector3(x + side * (width / 2f + 0.013f), pt, zc);
                g.Box(StorageRackSlots.Steel, c + new Vector3(0f, 0.006f, 0f), new Vector3(0.026f, 0.012f, 0.026f), Quaternion.Euler(0f, 30f, 0f), BoxFaces.NoBottom);  // nut
                g.Box(StorageRackSlots.Steel, c + new Vector3(0f, 0.02f, 0f), new Vector3(0.014f, 0.018f, 0.014f), Quaternion.identity, BoxFaces.Sides | BoxFaces.Top); // bolt end
            }
        }

        /// <summary>
        /// Bracing of the frame plane x = <paramref name="x"/> between uprights centred at <paramref name="za"/> and
        /// <paramref name="zb"/>, from <paramref name="yBottom"/> to <paramref name="yTop"/>. LOD0: horizontals and
        /// diagonals (<paramref name="zPattern"/>: a horizontal at every node and parallel diagonals, else zig-zag
        /// diagonals with horizontals at both ends); LOD1: the two end horizontals; LOD2: none.
        /// </summary>
        public static void Bracing(StorageMeshBuilder g, int lod, float x, float za, float zb, float yBottom, float yTop,
                                   float pitch, bool zPattern)
        {
            if (lod >= 2 || yTop - yBottom < 0.1f || zb - za < 0.05f) return;
            const float side = 0.022f, other = 0.035f;
            int panels = Mathf.Max(1, Mathf.RoundToInt((yTop - yBottom) / Mathf.Max(0.2f, pitch)));
            float p = (yTop - yBottom) / panels;
            for (int i = 0; i <= panels; i++)
            {
                bool end = i == 0 || i == panels;
                if (!end && (lod > 0 || !zPattern)) continue;
                float y = yBottom + i * p;
                g.Member(StorageRackSlots.Frame, new Vector3(x, y, za), new Vector3(x, y, zb), Vector3.right, side, other, 1f, PlainUv);
            }
            if (lod > 0) return;
            for (int i = 0; i < panels; i++)
            {
                float y0 = yBottom + i * p, y1 = y0 + p;
                bool flip = !zPattern && (i % 2 == 1);
                var a = new Vector3(x, y0, flip ? zb : za);
                var b = new Vector3(x, y1, flip ? za : zb);
                g.Member(StorageRackSlots.Frame, a, b, Vector3.right, side, other, 1f, PlainUv);
            }
        }
    }
}
