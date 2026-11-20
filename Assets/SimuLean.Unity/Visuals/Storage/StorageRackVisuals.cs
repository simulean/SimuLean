using UnityEngine;
using UnityEngine.Rendering;

namespace SimuLean.Unity
{
    /// <summary>
    /// Shared look and assembly of the procedural storage visuals: default materials (one per
    /// <see cref="StorageRackSlots"/> slot, URP/Lit or Standard, GPU instancing on) with code-generated textures — no
    /// texture or material assets — and the LOD chunk objects (one renderer per LOD under a <see cref="LODGroup"/>).
    /// </summary>
    public static class StorageRackVisuals
    {
        /// <summary>LODGroup reference size (m): a rack chunk switches by how large a bay looks, not the whole row.</summary>
        public const float LodReferenceSize = 6f;
        /// <summary>Relative screen heights (of the reference size) below which LOD1 / LOD2 are drawn, and culled.</summary>
        public const float Lod1Below = 0.20f, Lod2Below = 0.06f, CullBelow = 0.008f;

        private static Material[] _defaults;
        private static Material _floorPaint;

        // ── materials ────────────────────────────────────────────────────────

        /// <summary>The default material of every <see cref="StorageRackSlots"/> slot (created once per domain).</summary>
        public static Material[] DefaultMaterials()
        {
            bool ok = _defaults != null;
            if (ok) foreach (var m in _defaults) if (m == null) { ok = false; break; }
            if (ok) return _defaults;

            Texture2D paint = RollerConveyorMesh.CreatePaintTexture(128, 11);
            var mats = new Material[StorageRackSlots.Count];
            mats[StorageRackSlots.Frame] = RollerConveyorMesh.CreateMaterial("RackFrame(default)", new Color(0.13f, 0.27f, 0.47f), 0.25f, 0.45f, CreateUprightTexture());
            mats[StorageRackSlots.Beam] = RollerConveyorMesh.CreateMaterial("RackBeam(default)", new Color(0.92f, 0.38f, 0.07f), 0.1f, 0.5f, paint);
            mats[StorageRackSlots.Steel] = RollerConveyorMesh.CreateMaterial("RackGalvanised(default)", new Color(0.72f, 0.74f, 0.76f), 0.75f, 0.5f, paint);
            mats[StorageRackSlots.Safety] = RollerConveyorMesh.CreateMaterial("RackSafety(default)", Color.white, 0.05f, 0.45f, CreateStripeTexture());
            mats[StorageRackSlots.Label] = RollerConveyorMesh.CreateMaterial("RackLabel(default)", new Color(0.95f, 0.95f, 0.95f), 0f, 0.3f, CreateLabelTexture());
            mats[StorageRackSlots.Deck] = RollerConveyorMesh.CreateMaterial("RackWireDeck(default)", new Color(0.80f, 0.82f, 0.84f), 0.6f, 0.45f, CreateWireDeckTexture());
            mats[StorageRackSlots.Track] = RollerConveyorMesh.CreateMaterial("RackWheelBed(default)", new Color(0.78f, 0.79f, 0.80f), 0.4f, 0.4f,
                                                                            RollerConveyorMesh.CreateBedTexture(0.75f, 0.18f));
            _defaults = mats;
            return _defaults;
        }

        /// <summary>Yellow floor paint for block-storage lane markings.</summary>
        public static Material DefaultFloorPaint()
        {
            if (_floorPaint == null)
                _floorPaint = RollerConveyorMesh.CreateMaterial("FloorMarking(default)", new Color(0.98f, 0.78f, 0.08f), 0f, 0.3f,
                                                                RollerConveyorMesh.CreatePaintTexture(64, 23));
            return _floorPaint;
        }

        // ── textures (grey-scale unless stated: the material colour tints them) ─

        /// <summary>
        /// Punched upright face, one 0.1 × 0.1 m tile (U across the 0.1 m face, V up): two columns of teardrop beam
        /// slots at U ≈ 0.2 / 0.8 on a 50 mm pitch, plain in the middle (U = 0.5, used by boxes and bracing).
        /// </summary>
        public static Texture2D CreateUprightTexture(int size = 64)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "RackUpright", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
            var rnd = new System.Random(5);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
                    float c = 0.93f + 0.05f * ((float)rnd.NextDouble() - 0.5f);
                    // slot: centre column at 0.2 / 0.8, centre rows at 0.25 / 0.75, 30 mm long, wider at the top (teardrop)
                    float du = Mathf.Min(Mathf.Abs(u - 0.2f), Mathf.Abs(u - 0.8f));
                    float vv = Mathf.Repeat(v, 0.5f) - 0.25f;                       // −0.25..0.25 around the slot centre
                    if (Mathf.Abs(vv) < 0.15f)
                    {
                        float t = (vv + 0.15f) / 0.3f;                              // 0 bottom .. 1 top
                        float hw = 0.035f + 0.025f * t;
                        if (du < hw) c = 0.10f;                                     // hole
                        else if (du < hw + 0.02f) c *= 0.7f;                        // pressed rim
                    }
                    else if (Mathf.Abs(vv) < 0.17f && du < 0.06f) c *= 0.7f;
                    px[y * size + x] = new Color(c, c, c, 1f);
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>Yellow / black 45° safety stripes (coloured), one 0.2 m tile.</summary>
        public static Texture2D CreateStripeTexture(int size = 64)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "RackSafetyStripes", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
            var yellow = new Color(0.98f, 0.78f, 0.05f);
            var black = new Color(0.06f, 0.06f, 0.06f);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float w = Mathf.Repeat((x + y + 1f) / size, 1f);
                    px[y * size + x] = w < 0.5f ? yellow : black;
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>White location label: border, a dark text block and a barcode (U 0..1 across, V 0..1 up).</summary>
        public static Texture2D CreateLabelTexture()
        {
            const int w = 128, h = 64;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "RackLabel", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var rnd = new System.Random(3);
            var bars = new bool[w];
            for (int x = 0; x < w; x++) bars[x] = rnd.NextDouble() < 0.45;
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float c = 1f;
                    if (x < 3 || x >= w - 3 || y < 3 || y >= h - 3) c = 0.1f;                          // border
                    else if (y > 38 && y < 56 && x > 10 && x < 70) c = 0.15f;                            // location code
                    else if (y > 42 && y < 52 && x > 80 && x < 116) c = 0.35f;                           // level / bay text
                    else if (y > 10 && y < 34 && x > 10 && x < w - 10 && bars[x]) c = 0.08f;             // barcode
                    px[y * w + x] = new Color(c, c, c, 1f);
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>Wire-mesh deck, one 0.1 m tile: light wires on a 50 × 100 mm grid over dark gaps.</summary>
        public static Texture2D CreateWireDeckTexture(int size = 64)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "RackWireDeck", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
                    float du = Mathf.Min(u, 1f - u) * 2f;                         // wire every 100 mm along U
                    float dv = Mathf.Min(Mathf.Repeat(v, 0.5f), 0.5f - Mathf.Repeat(v, 0.5f)) * 2f;   // every 50 mm along V
                    float wire = Mathf.Max(Mathf.Clamp01(1f - du * size / 4f), Mathf.Clamp01(1f - dv * size / 3f));
                    float c = Mathf.Lerp(0.16f, 0.95f, wire);
                    px[y * size + x] = new Color(c, c, c, 1f);
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        // ── assembly ─────────────────────────────────────────────────────────

        /// <summary>
        /// Creates <paramref name="name"/> under <paramref name="parent"/> with one child "LOD<i>" (MeshFilter +
        /// MeshRenderer) per mesh of <paramref name="lods"/> and, with more than one, a <see cref="LODGroup"/> on the chunk
        /// (thresholds <see cref="Lod1Below"/> / <see cref="Lod2Below"/> / <see cref="CullBelow"/> of a
        /// <paramref name="referenceSize"/> object; ≤ 0 = the chunk's own bounds). A single mesh also gets a LODGroup that
        /// only culls it when tiny. The last LOD casts no shadows.
        /// </summary>
        public static GameObject CreateLodChunk(Transform parent, string name, Mesh[] lods, Material[] materials,
                                                bool isStatic, float referenceSize = LodReferenceSize)
        {
            var chunk = new GameObject(name);
            chunk.transform.SetParent(parent, false);
            chunk.isStatic = isStatic;
            var renderers = new Renderer[lods.Length];
            for (int i = 0; i < lods.Length; i++)
            {
                var go = new GameObject("LOD" + i);
                go.transform.SetParent(chunk.transform, false);
                go.isStatic = isStatic;
                go.AddComponent<MeshFilter>().sharedMesh = lods[i];
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = materials;
                if (i == lods.Length - 1 && lods.Length > 1) r.shadowCastingMode = ShadowCastingMode.Off;
                renderers[i] = r;
            }

            var group = chunk.AddComponent<LODGroup>();
            group.fadeMode = LODFadeMode.None;
            float[] below = { Lod1Below, Lod2Below, CullBelow };
            var levels = new LOD[lods.Length];
            for (int i = 0; i < lods.Length; i++)
                levels[i] = new LOD(i == lods.Length - 1 ? CullBelow : below[Mathf.Min(i, below.Length - 1)], new[] { renderers[i] });
            group.SetLODs(levels);
            group.RecalculateBounds();
            if (referenceSize > 0f) group.size = referenceSize;
            return chunk;
        }

        /// <summary>Triangles per LOD summed over every chunk under <paramref name="root"/> (children named "LOD0", "LOD1"…).</summary>
        public static int[] TriangleCounts(Transform root, int lodCount = 3)
        {
            var counts = new int[lodCount];
            if (root == null) return counts;
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                string n = mf.gameObject.name;
                if (n.Length != 4 || !n.StartsWith("LOD")) continue;
                int i = n[3] - '0';
                if (i >= 0 && i < lodCount) counts[i] += RollerConveyorMesh.TriangleCount(mf.sharedMesh);
            }
            return counts;
        }
    }
}
