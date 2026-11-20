using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>How the loads of a generated pallet rack are carried.</summary>
    public enum RackSupportType
    {
        /// <summary>Selective racking: front and rear step beams at every level.</summary>
        Beams = 0,
        /// <summary>Drive-in / push-back: L-rails along the lane under the slot edges (no front beam), a top tie beam.</summary>
        Rails = 1,
    }

    /// <summary>What lies on the beams of a generated pallet rack.</summary>
    public enum RackDeckType
    {
        /// <summary>Nothing: pallets rest on the front and rear beams.</summary>
        None = 0,
        /// <summary>Two front-to-back pallet support bars per slot.</summary>
        SupportBars = 1,
        /// <summary>Galvanised wire-mesh decks over the whole bay, on support channels.</summary>
        WireMesh = 2,
    }

    /// <summary>Frame bracing pattern of a generated rack.</summary>
    public enum RackBracingPattern
    {
        /// <summary>A horizontal at every node and parallel diagonals (Z pattern).</summary>
        Z = 0,
        /// <summary>Alternating diagonals, horizontals only at the bottom and top.</summary>
        ZigZag = 1,
    }

    /// <summary>
    /// Parameters of a generated pallet rack (plain data; see <see cref="PalletRackMesh"/>). The layout fields are those
    /// of <see cref="UnityStorageRack"/>, so the geometry matches its lanes exactly. Local axes: +X along the row
    /// (bays), +Y up, +Z into the rack: the aisle face is z = 0 and a lane runs from z = 0 to
    /// <see cref="LaneLength"/>. Slot s of bay b is centred at <see cref="SlotCenterX"/>; its load surface (beam top)
    /// is at <see cref="LevelY"/>. All sizes in metres.
    /// </summary>
    [System.Serializable]
    public class PalletRackSpec
    {
        public int bays = 3, slotsPerBay = 3, levels = 4, depth = 1;
        /// <summary>Bay pitch (frame centre to frame centre): clear width + upright width.</summary>
        public float bayWidth = 2.8f;
        public float slotWidth = 0.9f;
        public float levelHeight = 1.5f;
        /// <summary>Pitch of one storage position along the lane (the load length).</summary>
        public float slotDepth = 1.2f;

        /// <summary>Level 0 is the floor (no beam); otherwise the lowest beam top is at <see cref="firstLevelHeight"/>.</summary>
        public bool groundLevel = true;
        public float firstLevelHeight = 0.3f;

        /// <summary>Upright frame depth (front face of the front upright to the back of the rear one).</summary>
        public float frameDepth = 1.1f;
        /// <summary>Upright face width (along X, facing the aisle) and profile depth (along Z).</summary>
        public float uprightWidth = 0.1f, uprightDepth = 0.07f;
        /// <summary>Upright height above the top load surface.</summary>
        public float uprightExtension = 0.5f;
        public float beamHeight = 0.12f, beamDepth = 0.05f;

        public RackSupportType support = RackSupportType.Beams;
        public RackDeckType deck = RackDeckType.None;
        public RackBracingPattern bracing = RackBracingPattern.Z;
        /// <summary>Target height of one bracing panel.</summary>
        public float bracingPitch = 1.0f;
        /// <summary>Deep lanes: maximum distance between uprights of one frame (intermediate uprights are added).</summary>
        public float maxFrameSpan = 1.6f;
        /// <summary>Back-to-back rows: length of the row spacers behind every frame (0 = none).</summary>
        public float rowSpacerLength = 0f;
        public bool uprightProtectors = true;
        public bool endGuards = true;
        /// <summary>Location labels on the beams and a load sign on both end frames.</summary>
        public bool labels = true;
        /// <summary>Pick module: extra beam levels (load-surface heights) below the storage levels, e.g. the
        /// reserve beam over floor pick positions; no lanes are implied.</summary>
        public float[] extraBeamLevels;
        /// <summary>Pick module: carton-flow shelves in the bottom zone of the same frames (its
        /// <see cref="CartonFlowRackSpec.frames"/> off; bays, bay width and lane band equal to this rack's).</summary>
        public CartonFlowRackSpec cartonModule;

        public PalletRackSpec Clone() => (PalletRackSpec)MemberwiseClone();

        // ── layout (the formulas UnityStorageRack uses for its anchors) ──────

        /// <summary>Free width of a bay beside its slots, where the upright stands.</summary>
        public float Gap => Mathf.Max(0f, bayWidth - slotsPerBay * slotWidth);
        /// <summary>Centre X of frame f (0..bays): in the middle of the gap left of bay f.</summary>
        public float FrameX(int f) => f * bayWidth - Gap / 2f;
        public float SlotCenterX(int bay, int slot) => bay * bayWidth + (slot + 0.5f) * slotWidth;
        public float LevelY(int level) => LevelSurfaceY(groundLevel, firstLevelHeight, levelHeight, level);
        /// <summary>True when level <paramref name="level"/> is carried by beams / rails (false: on the floor).</summary>
        public bool HasSupport(int level) => LevelY(level) >= beamHeight * 0.75f;
        public float LaneLength => Mathf.Max(1, depth) * slotDepth;
        /// <summary>How far a load overhangs the frame at the front (and at the back).</summary>
        public float Overhang => Mathf.Clamp((slotDepth - frameDepth) / 2f, 0f, slotDepth * 0.25f);
        public float FrameZ0 => Overhang;
        public float FrameZ1 => LaneLength - Overhang;
        public float UprightHeight => Mathf.Max(1.0f, LevelY(Mathf.Max(0, levels - 1)) + Mathf.Max(0.05f, uprightExtension));

        /// <summary>Load-surface height of a level (beam top / floor): the anchor Y of its lanes.</summary>
        public static float LevelSurfaceY(bool groundLevel, float firstLevelHeight, float levelHeight, int level)
            => groundLevel ? level * levelHeight : firstLevelHeight + level * levelHeight;

        /// <summary>Z of the uprights of one frame (centres), front first.</summary>
        public float[] UprightZ()
        {
            float ud = Mathf.Clamp(uprightDepth, 0.03f, 0.2f);
            float za = FrameZ0 + ud / 2f, zb = Mathf.Max(za + ud, FrameZ1 - ud / 2f);
            int spans = Mathf.Max(1, Mathf.CeilToInt((zb - za) / Mathf.Max(0.5f, maxFrameSpan) - 1e-4f));
            var z = new float[spans + 1];
            for (int k = 0; k <= spans; k++) z[k] = za + (zb - za) * k / spans;
            return z;
        }
    }

    /// <summary>
    /// Procedural selective pallet racking (and drive-in / push-back racks with rails) built from real components:
    /// perforated upright frames (open profile with lips, punched-slot texture), base plates with anchor bolts,
    /// horizontal + diagonal frame bracing, safety-orange step beams with connectors at every level (level 0 on the
    /// floor optional), pallet support bars or wire-mesh decks, row spacers, upright protectors, end-of-aisle guards
    /// and location labels.
    /// <para>Three levels of detail: <b>LOD0</b> everything; <b>LOD1</b> uprights and beams as boxes, end horizontals
    /// only (no diagonals), decks and guards kept as boxes, no bolts / connectors / labels; <b>LOD2</b> uprights and
    /// beams (or rails) as plain boxes. One mesh per LOD for a range of bays (a chunk), sub-meshes per
    /// <see cref="StorageRackSlots"/>.</para>
    /// </summary>
    public static class PalletRackMesh
    {
        /// <summary>Builds level <paramref name="lod"/> (0..2) of bays [<paramref name="bay0"/>, <paramref name="bay1"/>)
        /// (frames of those bays; the last frame when <paramref name="bay1"/> = bays).</summary>
        public static Mesh Build(PalletRackSpec s, int lod, int bay0 = 0, int bay1 = -1, Mesh target = null, string name = null)
        {
            var g = new StorageMeshBuilder(StorageRackSlots.Count);
            Generate(s, Mathf.Clamp(lod, 0, 2), bay0, bay1 < 0 ? s.bays : bay1, g);
            return g.ToMesh(target, name);
        }

        /// <summary>Triangle count of a level without building a Mesh (whole rack).</summary>
        public static int CountTriangles(PalletRackSpec s, int lod)
        {
            var g = new StorageMeshBuilder(StorageRackSlots.Count);
            Generate(s, Mathf.Clamp(lod, 0, 2), 0, s.bays, g);
            return g.TriangleCount;
        }

        private const float StripeScale = 5f;      // safety stripes: texture tile 0.2 m
        private const float DeckScale = 10f;       // wire deck: texture tile 0.1 m

        private static void Generate(PalletRackSpec s, int lod, int bay0, int bay1, StorageMeshBuilder g)
        {
            int bays = Mathf.Max(1, s.bays), levels = Mathf.Max(1, s.levels), slots = Mathf.Max(1, s.slotsPerBay);
            bay0 = Mathf.Clamp(bay0, 0, bays);
            bay1 = Mathf.Clamp(bay1, bay0, bays);
            float uw = Mathf.Clamp(s.uprightWidth, 0.04f, 0.3f), ud = Mathf.Clamp(s.uprightDepth, 0.03f, 0.2f);
            float H = s.UprightHeight;
            float[] uz = s.UprightZ();
            float z0 = s.FrameZ0, z1 = s.FrameZ1, L = s.LaneLength;
            float bh = Mathf.Clamp(s.beamHeight, 0.04f, 0.3f), bd = Mathf.Clamp(s.beamDepth, 0.03f, 0.12f);
            bool rails = s.support == RackSupportType.Rails;

            // ---- frames
            int lastFrame = bay1 == bays ? bays : bay1 - 1;
            for (int f = bay0; f <= lastFrame; f++)
            {
                float x = s.FrameX(f);
                for (int k = 0; k < uz.Length; k++)
                {
                    RackFrameParts.Upright(g, lod, x, uz[k], uw, ud, H, k < uz.Length - 1);
                    if (lod == 0) RackFrameParts.BasePlate(g, x, uz[k], uw, ud);
                }
                for (int k = 0; k < uz.Length - 1; k++)
                    RackFrameParts.Bracing(g, lod, x, uz[k], uz[k + 1], 0.15f, H - 0.1f, s.bracingPitch, s.bracing == RackBracingPattern.Z);

                // row spacers towards a back-to-back row
                if (lod < 2 && s.rowSpacerLength > 0.01f)
                    for (float y = 0.6f; y <= H - 0.2f; y += 2.0f)
                        g.BoxMinMax(StorageRackSlots.Steel, new Vector3(x - 0.02f, y - 0.025f, z1), new Vector3(x + 0.02f, y + 0.025f, z1 + s.rowSpacerLength), BoxFaces.NoEnds);

                // upright protector on the aisle-side upright
                if (lod < 2 && s.uprightProtectors)
                {
                    float pd = ud * 0.6f;
                    g.BoxMinMax(StorageRackSlots.Safety, new Vector3(x - uw / 2f - 0.015f, 0f, z0 - 0.015f), new Vector3(x + uw / 2f + 0.015f, 0.4f, z0 - 0.015f + pd),
                                lod == 0 ? BoxFaces.NoBottom : BoxFaces.Sides | BoxFaces.Top, StripeScale);
                }

                bool endFrame = f == 0 || f == bays;
                if (endFrame && lod < 2 && s.endGuards)
                {
                    float sd = f == 0 ? -1f : 1f;
                    float xg = x + sd * (uw / 2f + 0.14f);
                    float za = -0.15f, zb = L + Mathf.Max(0f, s.rowSpacerLength) + 0.15f;
                    int posts = Mathf.Max(2, Mathf.CeilToInt((zb - za) / 1.5f) + 1);
                    for (int i = 0; i < posts; i++)
                    {
                        float zp = za + 0.05f + (zb - za - 0.1f) * i / (posts - 1);
                        g.BoxMinMax(StorageRackSlots.Safety, new Vector3(xg - 0.05f, 0f, zp - 0.05f), new Vector3(xg + 0.05f, 0.45f, zp + 0.05f), BoxFaces.NoBottom, StripeScale);
                        if (lod == 0)
                            g.BoxMinMax(StorageRackSlots.Steel, new Vector3(xg - 0.09f, 0f, zp - 0.09f), new Vector3(xg + 0.09f, 0.008f, zp + 0.09f), BoxFaces.NoBottom);
                    }
                    g.BoxMinMax(StorageRackSlots.Safety, new Vector3(xg - 0.03f + sd * 0.05f, 0.22f, za), new Vector3(xg + 0.03f + sd * 0.05f, 0.38f, zb), BoxFaces.All, StripeScale);
                }

                // load sign on the outer face of each end frame
                if (endFrame && lod == 0 && s.labels)
                {
                    float sd = f == 0 ? -1f : 1f;
                    float xs = x + sd * (uw / 2f + 0.002f), zm = (uz[0] + uz[uz.Length > 1 ? 1 : 0]) / 2f;
                    float y0 = Mathf.Min(1.4f, H - 0.5f), y1 = y0 + 0.4f, za = zm - 0.15f, zb = zm + 0.15f;
                    var nrm = new Vector3(sd, 0f, 0f);
                    g.Quad(StorageRackSlots.Label, new Vector3(xs, y0, za), new Vector3(xs, y0, zb), new Vector3(xs, y1, zb), new Vector3(xs, y1, za), nrm,
                           new Vector2(sd < 0 ? 1f : 0f, 0f), new Vector2(sd < 0 ? 0f : 1f, 0f), new Vector2(sd < 0 ? 0f : 1f, 1f), new Vector2(sd < 0 ? 1f : 0f, 1f));
                    g.BoxMinMax(StorageRackSlots.Steel, new Vector3(xs - 0.02f, y0 + 0.15f, za + 0.03f), new Vector3(xs + 0.02f, y0 + 0.25f, za + 0.05f), BoxFaces.NoEnds); // bracket
                }
            }

            // ---- bays: beams / rails, decks, labels
            for (int b = bay0; b < bay1; b++)
            {
                float xa = s.FrameX(b) + uw / 2f, xb = s.FrameX(b + 1) - uw / 2f;
                if (xb - xa < 0.1f) continue;

                for (int l = 0; l < levels; l++)
                {
                    if (!s.HasSupport(l)) continue;
                    float Y = s.LevelY(l);
                    if (rails) RailLevel(g, s, lod, b, xa, xb, Y, z0, z1);
                    else BeamLevel(g, s, lod, b, xa, xb, Y, z0, z1, bh, bd);
                }

                if (!rails && s.extraBeamLevels != null)
                    foreach (float y in s.extraBeamLevels)
                        if (y >= bh * 0.75f) BeamLevel(g, s, lod, b, xa, xb, y, z0, z1, bh, bd);

                if (rails && lod < 2)
                {
                    // top tie beam at the entry face (keeps the frames square over the drive-in lanes)
                    float yt = H - 0.08f;
                    g.BoxMinMax(StorageRackSlots.Beam, new Vector3(xa, yt - bh, z0), new Vector3(xb, yt, z0 + bd), BoxFaces.NoEnds);
                    g.BoxMinMax(StorageRackSlots.Beam, new Vector3(xa, yt - bh, z1 - bd), new Vector3(xb, yt, z1), BoxFaces.NoEnds);
                }
            }

            // ---- pick module: carton-flow shelves hung in these frames (no frames of their own)
            if (s.cartonModule != null) CartonFlowRackMesh.Append(s.cartonModule, lod, bay0, bay1, g);
        }

        private static void BeamLevel(StorageMeshBuilder g, PalletRackSpec s, int lod, int b, float xa, float xb, float Y,
                                      float z0, float z1, float bh, float bd)
        {
            float step = lod == 0 ? Mathf.Min(0.016f, bd * 0.35f) : 0f;
            for (int side = 0; side < 2; side++)
            {
                bool front = side == 0;
                float zo = front ? z0 : z1;                              // outer face
                float zi = front ? z0 + bd : z1 - bd;                   // inner face (towards the load)
                if (lod == 2)
                {
                    g.BoxMinMax(StorageRackSlots.Beam, new Vector3(xa, Y - bh, Mathf.Min(zo, zi)), new Vector3(xb, Y, Mathf.Max(zo, zi)),
                                BoxFaces.Front | BoxFaces.Back | BoxFaces.Top);
                    continue;
                }
                // step-beam profile in (z, y), extruded along X; the step is on the inner top edge (decks sit flush)
                float dz = front ? 1f : -1f;
                Vector2[] prof = step > 0f
                    ? new[]
                    {
                        new Vector2(zo, Y - bh), new Vector2(zi, Y - bh), new Vector2(zi, Y - step),
                        new Vector2(zi - dz * step, Y - step), new Vector2(zi - dz * step, Y), new Vector2(zo, Y),
                    }
                    : new[] { new Vector2(zo, Y - bh), new Vector2(zi, Y - bh), new Vector2(zi, Y), new Vector2(zo, Y) };
                // local x → world Z, local y → world Y, local z (extrusion) → world X
                var m = new Matrix4x4(new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 0f, 0f, 1f));
                g.Extrude(StorageRackSlots.Beam, prof, m, xa, xb, null, false, false);

                if (lod == 0)
                {
                    // beam connectors: plates welded to the beam ends, hooked into the uprights
                    float ch = bh + 0.07f, zc0 = Mathf.Min(zo, zi), zc1 = zc0 + Mathf.Max(bd, 0.06f);
                    if (!front) { zc1 = Mathf.Max(zo, zi); zc0 = zc1 - Mathf.Max(bd, 0.06f); }
                    g.BoxMinMax(StorageRackSlots.Beam, new Vector3(xa, Y - ch, zc0), new Vector3(xa + 0.006f, Y - 0.002f, zc1), BoxFaces.Right | BoxFaces.Top | BoxFaces.Bottom | BoxFaces.Front | BoxFaces.Back);
                    g.BoxMinMax(StorageRackSlots.Beam, new Vector3(xb - 0.006f, Y - ch, zc0), new Vector3(xb, Y - 0.002f, zc1), BoxFaces.Left | BoxFaces.Top | BoxFaces.Bottom | BoxFaces.Front | BoxFaces.Back);
                }
            }

            // decks
            float dz0 = z0 + bd - step, dz1 = z1 - bd + step;
            if (s.deck == RackDeckType.SupportBars && lod < 2)
            {
                for (int k = 0; k < s.slotsPerBay; k++)
                {
                    float xc = s.SlotCenterX(b, k);
                    foreach (int side in new[] { -1, 1 })
                    {
                        float xs = xc + side * s.slotWidth * 0.3f;
                        g.BoxMinMax(StorageRackSlots.Steel, new Vector3(xs - 0.02f, Y - 0.03f, z0 + 0.005f), new Vector3(xs + 0.02f, Y, z1 - 0.005f),
                                    lod == 0 ? BoxFaces.All & ~BoxFaces.Bottom : BoxFaces.Top | BoxFaces.Left | BoxFaces.Right);
                    }
                }
            }
            else if (s.deck == RackDeckType.WireMesh && lod < 2)
            {
                float yd = Y - 0.001f;
                float xa2 = xa + 0.005f, xb2 = xb - 0.005f;
                g.Quad(StorageRackSlots.Deck, new Vector3(xa2, yd, dz0), new Vector3(xb2, yd, dz0), new Vector3(xb2, yd, dz1), new Vector3(xa2, yd, dz1), Vector3.up,
                       new Vector2(xa2, dz0) * DeckScale, new Vector2(xb2, dz0) * DeckScale, new Vector2(xb2, dz1) * DeckScale, new Vector2(xa2, dz1) * DeckScale);
                g.Quad(StorageRackSlots.Deck, new Vector3(xa2, yd - 0.004f, dz0), new Vector3(xb2, yd - 0.004f, dz0), new Vector3(xb2, yd - 0.004f, dz1), new Vector3(xa2, yd - 0.004f, dz1), Vector3.down,
                       new Vector2(xa2, dz0) * DeckScale, new Vector2(xb2, dz0) * DeckScale, new Vector2(xb2, dz1) * DeckScale, new Vector2(xa2, dz1) * DeckScale);
                if (lod == 0)
                {
                    int channels = Mathf.Max(2, s.slotsPerBay + 1);
                    for (int k = 0; k < channels; k++)
                    {
                        float xc = xa + 0.08f + (xb - xa - 0.16f) * k / (channels - 1);
                        g.BoxMinMax(StorageRackSlots.Steel, new Vector3(xc - 0.015f, yd - 0.045f, dz0), new Vector3(xc + 0.015f, yd - 0.005f, dz1), BoxFaces.Left | BoxFaces.Right | BoxFaces.Bottom);
                    }
                }
            }

            // location labels on the front beam, one per slot
            if (lod == 0 && s.labels)
            {
                float zl = z0 - 0.0015f, yl0 = Y - bh / 2f - 0.03f, yl1 = yl0 + 0.06f;
                for (int k = 0; k < s.slotsPerBay; k++)
                {
                    float xc = s.SlotCenterX(b, k);
                    if (xc - 0.06f < xa || xc + 0.06f > xb) continue;
                    g.Quad(StorageRackSlots.Label, new Vector3(xc - 0.06f, yl0, zl), new Vector3(xc + 0.06f, yl0, zl), new Vector3(xc + 0.06f, yl1, zl), new Vector3(xc - 0.06f, yl1, zl), Vector3.back,
                           new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f));
                }
            }
        }

        private static void RailLevel(StorageMeshBuilder g, PalletRackSpec s, int lod, int b, float xa, float xb, float Y, float z0, float z1)
        {
            const float leg = 0.08f, flange = 0.06f, th = 0.006f;
            for (int k = 0; k < s.slotsPerBay; k++)
            {
                float left = Mathf.Max(xa, b * s.bayWidth + k * s.slotWidth), right = Mathf.Min(xb, b * s.bayWidth + (k + 1) * s.slotWidth);
                if (right - left < 2f * flange) continue;
                foreach (int side in new[] { -1, 1 })
                {
                    float xe = side < 0 ? left : right;     // rail back against the slot edge, flange towards the slot
                    float dir = side < 0 ? 1f : -1f;
                    if (lod > 0)
                    {
                        float xi = xe + dir * flange;
                        g.BoxMinMax(StorageRackSlots.Beam, new Vector3(Mathf.Min(xe, xi), Y - leg, z0), new Vector3(Mathf.Max(xe, xi), Y, z1),
                                    lod == 1 ? BoxFaces.Top | BoxFaces.Left | BoxFaces.Right | BoxFaces.Bottom : BoxFaces.Top | BoxFaces.Left | BoxFaces.Right);
                        continue;
                    }
                    var prof = new[]
                    {
                        new Vector2(xe, Y - leg), new Vector2(xe + dir * th, Y - leg), new Vector2(xe + dir * th, Y - th),
                        new Vector2(xe + dir * flange, Y - th), new Vector2(xe + dir * flange, Y), new Vector2(xe, Y),
                    };
                    var caps = new[] { new[] { 0, 1, 2, 5 }, new[] { 5, 2, 3, 4 } };
                    g.Extrude(StorageRackSlots.Beam, prof, Matrix4x4.identity, z0, z1, caps, true, true);
                }
            }
        }
    }
}
