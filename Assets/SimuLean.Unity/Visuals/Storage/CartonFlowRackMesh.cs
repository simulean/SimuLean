using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Parameters of a generated carton flow rack (plain data; see <see cref="CartonFlowRackMesh"/>). Local axes as
    /// <see cref="PalletRackSpec"/>: +X along the row, +Y up, +Z into the rack. The <b>pick face is z = 0</b> (the
    /// anchor face, lowest end of the shelves); boxes are loaded at the rear and roll down towards it. Lane j of bay b is
    /// centred at <c>b · bayWidth + (j + 0.5) · laneWidth</c>; its shelf surface passes through
    /// (<c>x</c>, <see cref="LevelY"/>, 0) and rises by <see cref="slopeDegrees"/> towards the rear. Box k of a lane
    /// (k = 0 at the pick face) sits <see cref="AlongSlope"/>(k) from the anchor along the slope. Sizes in metres.
    /// </summary>
    [System.Serializable]
    public class CartonFlowRackSpec
    {
        /// <summary>Along-slope distance from the pick face to the first box (front stop plate).</summary>
        public const float DefaultFrontStop = 0.04f;

        public int bays = 2, lanesPerBay = 4, levels = 4;
        /// <summary>Boxes per lane.</summary>
        public int depth = 4;
        public float bayWidth = 1.88f;
        public float laneWidth = 0.45f;
        /// <summary>Vertical pitch of the shelves (at the pick face).</summary>
        public float levelHeight = 0.45f;
        /// <summary>Along-slope pitch of one box (its length).</summary>
        public float boxPitch = 0.6f;
        /// <summary>Height of the lowest shelf at the pick face.</summary>
        public float firstLevelHeight = 0.35f;
        /// <summary>Shelf inclination (carton flow: about 4–8°).</summary>
        public float slopeDegrees = 6f;

        public float frontStop = DefaultFrontStop;
        /// <summary>Along-slope length behind the last box (loading side).</summary>
        public float rearMargin = 0.08f;
        public float uprightWidth = 0.08f, uprightDepth = 0.06f;
        public float uprightExtension = 0.1f;
        public float bracingPitch = 0.6f;
        public RackBracingPattern bracing = RackBracingPattern.Z;
        /// <summary>Wheel-bed strips per lane.</summary>
        public int tracksPerLane = 2;
        public float trackWidth = 0.045f, trackHeight = 0.03f;
        /// <summary>Height of the lane guide rails above the shelf.</summary>
        public float dividerHeight = 0.05f;
        /// <summary>Lane label strip on the front stop.</summary>
        public bool labels = true;
        /// <summary>A flat wire-mesh deck on top for overstock.</summary>
        public bool overstockDeck = true;
        /// <summary>Upright frames (false: shelves only, hung in the frames of another rack — the pick module of a pallet
        /// rack).</summary>
        public bool frames = true;

        public CartonFlowRackSpec Clone() => (CartonFlowRackSpec)MemberwiseClone();

        public float SlopeRad => SlopeRadFor(slopeDegrees);
        /// <summary>Unit vector up the shelf (towards the rear / loading side).</summary>
        public Vector3 Along => AlongFor(slopeDegrees);
        /// <summary>Shelf normal.</summary>
        public Vector3 Normal => NormalFor(slopeDegrees);
        /// <summary>Rotation of a box resting on a shelf (local +Z up the slope).</summary>
        public Quaternion ShelfRotation => RotationFor(slopeDegrees);

        public static float SlopeRadFor(float degrees) => Mathf.Clamp(degrees, 0f, 20f) * Mathf.Deg2Rad;
        public static Vector3 AlongFor(float degrees) { float a = SlopeRadFor(degrees); return new Vector3(0f, Mathf.Sin(a), Mathf.Cos(a)); }
        public static Vector3 NormalFor(float degrees) { float a = SlopeRadFor(degrees); return new Vector3(0f, Mathf.Cos(a), -Mathf.Sin(a)); }
        public static Quaternion RotationFor(float degrees) => Quaternion.Euler(-Mathf.Clamp(degrees, 0f, 20f), 0f, 0f);

        public float Gap => Mathf.Max(0f, bayWidth - lanesPerBay * laneWidth);
        public float FrameX(int f) => f * bayWidth - Gap / 2f;
        public float LaneCenterX(int bay, int lane) => bay * bayWidth + (lane + 0.5f) * laneWidth;
        public float LevelY(int level) => firstLevelHeight + level * levelHeight;
        /// <summary>Along-slope length of a shelf.</summary>
        public float ShelfLength => frontStop + Mathf.Max(1, depth) * boxPitch + rearMargin;
        /// <summary>Horizontal depth of the rack.</summary>
        public float HorizontalDepth => ShelfLength * Mathf.Cos(SlopeRad);
        /// <summary>Along-slope distance from the anchor to the centre of box <paramref name="k"/> (0 = at the pick face).</summary>
        public float AlongSlope(int k) => AlongSlope(frontStop, boxPitch, k);
        public static float AlongSlope(float frontStop, float boxPitch, int k) => frontStop + (k + 0.5f) * boxPitch;
        /// <summary>Shelf height at horizontal depth z of a level.</summary>
        public float SurfaceY(int level, float z) => LevelY(level) + z * Mathf.Tan(SlopeRad);
        public float TopRearY => SurfaceY(Mathf.Max(0, levels - 1), HorizontalDepth);
        public float OverstockY => TopRearY + levelHeight * 0.85f;
        public float UprightHeight => overstockDeck ? OverstockY + 0.03f : TopRearY + Mathf.Max(0.05f, uprightExtension);
    }

    /// <summary>
    /// Procedural carton flow rack ("live storage" for picking boxes): upright frames as the pallet rack
    /// (<see cref="RackFrameParts"/>), inclined shelves on orange front / rear beams carrying wheel-bed strips, lane
    /// guide rails, a front stop plate with the lane label strip at the pick face, an optional flat overstock deck on top.
    /// <para><b>LOD0</b> everything; <b>LOD1</b> box uprights, each shelf one striped sloped quad, beams and stop plates,
    /// end bracing only; <b>LOD2</b> box uprights, shelf quads and front beams. Sub-meshes per
    /// <see cref="StorageRackSlots"/>.</para>
    /// </summary>
    public static class CartonFlowRackMesh
    {
        /// <summary>Wheel-bed texture: one wheel row per repeat, every 1 / <c>WheelScale</c> m along the slope.</summary>
        public const float WheelScale = 20f;

        public static Mesh Build(CartonFlowRackSpec s, int lod, int bay0 = 0, int bay1 = -1, Mesh target = null, string name = null)
        {
            var g = new StorageMeshBuilder(StorageRackSlots.Count);
            Generate(s, Mathf.Clamp(lod, 0, 2), bay0, bay1 < 0 ? s.bays : bay1, g);
            return g.ToMesh(target, name);
        }

        public static int CountTriangles(CartonFlowRackSpec s, int lod)
        {
            var g = new StorageMeshBuilder(StorageRackSlots.Count);
            Generate(s, Mathf.Clamp(lod, 0, 2), 0, s.bays, g);
            return g.TriangleCount;
        }

        /// <summary>Adds level <paramref name="lod"/> of bays [<paramref name="bay0"/>, <paramref name="bay1"/>) to a builder
        /// (e.g. the shelves of a pallet rack's pick module, <see cref="CartonFlowRackSpec.frames"/> false).</summary>
        internal static void Append(CartonFlowRackSpec s, int lod, int bay0, int bay1, StorageMeshBuilder g)
            => Generate(s, Mathf.Clamp(lod, 0, 2), bay0, bay1 < 0 ? s.bays : bay1, g);

        private static void Generate(CartonFlowRackSpec s, int lod, int bay0, int bay1, StorageMeshBuilder g)
        {
            int bays = Mathf.Max(1, s.bays), levels = Mathf.Max(1, s.levels), lanes = Mathf.Max(1, s.lanesPerBay);
            bay0 = Mathf.Clamp(bay0, 0, bays);
            bay1 = Mathf.Clamp(bay1, bay0, bays);
            float uw = Mathf.Clamp(s.uprightWidth, 0.04f, 0.2f), ud = Mathf.Clamp(s.uprightDepth, 0.03f, 0.15f);
            float Hd = s.HorizontalDepth, Ls = s.ShelfLength, H = s.UprightHeight;
            float tan = Mathf.Tan(s.SlopeRad);
            Vector3 along = s.Along, up = s.Normal;
            Quaternion R = s.ShelfRotation;
            float th = Mathf.Clamp(s.trackHeight, 0.01f, 0.08f);
            const float beamH = 0.06f, beamD = 0.05f, stopT = 0.012f, stopH = 0.06f;
            float fs = Mathf.Max(stopT, s.frontStop);
            float zf = ud / 2f, zr = Mathf.Max(zf + ud, Hd - ud / 2f);

            // ---- frames
            int lastFrame = bay1 == bays ? bays : bay1 - 1;
            for (int f = bay0; f <= lastFrame && s.frames; f++)
            {
                float x = s.FrameX(f);
                RackFrameParts.Upright(g, lod, x, zf, uw, ud, H, true);
                RackFrameParts.Upright(g, lod, x, zr, uw, ud, H, false);
                if (lod == 0)
                {
                    RackFrameParts.BasePlate(g, x, zf, uw, ud);
                    RackFrameParts.BasePlate(g, x, zr, uw, ud);
                }
                RackFrameParts.Bracing(g, lod, x, zf, zr, 0.12f, H - 0.08f, s.bracingPitch, s.bracing == RackBracingPattern.Z);
            }

            // ---- shelves
            for (int b = bay0; b < bay1; b++)
            {
                float xa = s.FrameX(b) + uw / 2f, xb = s.FrameX(b + 1) - uw / 2f;
                if (xb - xa < 0.1f) continue;

                for (int l = 0; l < levels; l++)
                {
                    float Y = s.LevelY(l);
                    var origin = new Vector3(0f, Y, 0f);           // the shelf surface passes through (x, Y, 0)

                    // front and rear shelf beams under the tracks (+ a middle one on long shelves)
                    ShelfBeam(g, lod, xa, xb, fs, fs + beamD, s.SurfaceY(l, fs + beamD / 2f) - th, beamH, StorageRackSlots.Beam);
                    if (lod < 2)
                    {
                        ShelfBeam(g, lod, xa, xb, Hd - beamD, Hd, s.SurfaceY(l, Hd - beamD / 2f) - th, beamH, StorageRackSlots.Beam);
                        if (Hd > 1.4f)
                            ShelfBeam(g, lod, xa, xb, Hd / 2f - beamD / 2f, Hd / 2f + beamD / 2f, s.SurfaceY(l, Hd / 2f) - th, beamH * 0.8f, StorageRackSlots.Steel);
                    }

                    if (lod == 0)
                    {
                        // wheel-bed strips, two (or tracksPerLane) per lane
                        int n = Mathf.Max(1, s.tracksPerLane);
                        float spread = n > 1 ? s.laneWidth * 0.55f / (n - 1) : 0f;
                        float len = Ls - fs - 0.01f;
                        for (int j = 0; j < lanes; j++)
                        {
                            float xc = s.LaneCenterX(b, j);
                            for (int k = 0; k < n; k++)
                            {
                                float xt = xc + (k - (n - 1) / 2f) * spread;
                                Strip(g, StorageRackSlots.Track, new Vector3(xt, Y, 0f) + along * fs, along, up, len, s.trackWidth, th, fs);
                            }
                        }
                        // lane guide rails on every lane boundary
                        float dl = Ls - fs - 0.02f;
                        for (int j = 0; j <= lanes; j++)
                        {
                            float xd = Mathf.Clamp(b * s.bayWidth + j * s.laneWidth, xa + 0.004f, xb - 0.004f);
                            Vector3 c = new Vector3(xd, Y, 0f) + along * (fs + dl / 2f) + up * (s.dividerHeight / 2f);
                            g.Box(StorageRackSlots.Steel, c, new Vector3(0.004f, s.dividerHeight, dl), R,
                                  BoxFaces.Left | BoxFaces.Right | BoxFaces.Top | BoxFaces.Back);
                        }
                    }
                    else
                    {
                        // the shelf as one striped sloped quad (the wheel beds seen from afar)
                        Vector3 p0 = origin + new Vector3(xa + 0.01f, 0f, 0f) + along * fs - up * 0.003f;
                        Vector3 p1 = origin + new Vector3(xb - 0.01f, 0f, 0f) + along * fs - up * 0.003f;
                        float len = Ls - fs;
                        float u0 = fs * CartonFlowRackMesh.WheelScale, u1 = (fs + len) * CartonFlowRackMesh.WheelScale;
                        g.Quad(StorageRackSlots.Track, p0, p1, p1 + along * len, p0 + along * len, up,
                               new Vector2(u0, 0f), new Vector2(u0, 1f), new Vector2(u1, 1f), new Vector2(u1, 0f));
                    }

                    // front stop plate (pick face) with the lane label strip
                    if (lod < 2)
                    {
                        float yb = Y - th - beamH, yt = Y + fs * tan + stopH;
                        g.BoxMinMax(StorageRackSlots.Steel, new Vector3(xa, yb, fs - stopT), new Vector3(xb, yt, fs), lod == 0 ? BoxFaces.NoEnds : BoxFaces.Front | BoxFaces.Top);
                        if (lod == 0 && s.labels)
                        {
                            float zl = fs - stopT - 0.0015f, yl0 = Y - 0.03f, yl1 = Y + 0.015f;
                            for (int j = 0; j < lanes; j++)
                            {
                                float xc = s.LaneCenterX(b, j), hw = Mathf.Min(0.06f, s.laneWidth * 0.4f);
                                g.Quad(StorageRackSlots.Label, new Vector3(xc - hw, yl0, zl), new Vector3(xc + hw, yl0, zl), new Vector3(xc + hw, yl1, zl), new Vector3(xc - hw, yl1, zl), Vector3.back,
                                       new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f));
                            }
                        }
                    }
                }

                // overstock deck on top
                if (s.overstockDeck)
                {
                    float Yo = s.OverstockY;
                    ShelfBeam(g, lod, xa, xb, 0f, beamD, Yo, 0.1f, StorageRackSlots.Beam);
                    ShelfBeam(g, lod, xa, xb, Hd - beamD, Hd, Yo, 0.1f, StorageRackSlots.Beam);
                    if (lod < 2)
                    {
                        const float sc = 10f;
                        float yd = Yo - 0.001f, za = beamD, zb = Hd - beamD;
                        g.Quad(StorageRackSlots.Deck, new Vector3(xa, yd, za), new Vector3(xb, yd, za), new Vector3(xb, yd, zb), new Vector3(xa, yd, zb), Vector3.up,
                               new Vector2(xa, za) * sc, new Vector2(xb, za) * sc, new Vector2(xb, zb) * sc, new Vector2(xa, zb) * sc);
                        g.Quad(StorageRackSlots.Deck, new Vector3(xa, yd - 0.004f, za), new Vector3(xb, yd - 0.004f, za), new Vector3(xb, yd - 0.004f, zb), new Vector3(xa, yd - 0.004f, zb), Vector3.down,
                               new Vector2(xa, za) * sc, new Vector2(xb, za) * sc, new Vector2(xb, zb) * sc, new Vector2(xa, zb) * sc);
                    }
                }
            }
        }

        /// <summary>A beam along X between the uprights, z in [<paramref name="za"/>, <paramref name="zb"/>], top at <paramref name="top"/>.</summary>
        private static void ShelfBeam(StorageMeshBuilder g, int lod, float xa, float xb, float za, float zb, float top, float h, int sub)
        {
            g.BoxMinMax(sub, new Vector3(xa, top - h, za), new Vector3(xb, top, zb),
                        lod == 2 ? BoxFaces.Front | BoxFaces.Top : BoxFaces.NoEnds);
        }

        /// <summary>
        /// A wheel-bed strip on the slope: top face from <paramref name="start"/> (top, centre line) for
        /// <paramref name="len"/> along <paramref name="along"/>, sides and front end down to <paramref name="h"/> below;
        /// U = distance from the anchor along the slope × <see cref="WheelScale"/> (one wheel row per repeat).
        /// </summary>
        private static void Strip(StorageMeshBuilder g, int sub, Vector3 start, Vector3 along, Vector3 up, float len, float w, float h, float s0)
        {
            Vector3 r = Vector3.right * (w / 2f), d = up * h, e = along * len;
            float u0 = s0 * WheelScale, u1 = (s0 + len) * WheelScale;
            Vector3 a = start - r, b = start + r;
            g.Quad(sub, a, b, b + e, a + e, up, new Vector2(u0, 0f), new Vector2(u0, 1f), new Vector2(u1, 1f), new Vector2(u1, 0f));
            g.Quad(sub, a, a + e, a + e - d, a - d, Vector3.left, new Vector2(u0, 0f), new Vector2(u1, 0f), new Vector2(u1, 0f), new Vector2(u0, 0f));
            g.Quad(sub, b, b + e, b + e - d, b - d, Vector3.right, new Vector2(u0, 1f), new Vector2(u1, 1f), new Vector2(u1, 1f), new Vector2(u0, 1f));
            g.Quad(sub, a, b, b - d, a - d, -along, new Vector2(u0, 0f), new Vector2(u0, 1f), new Vector2(u0, 1f), new Vector2(u0, 0f));
        }
    }
}
