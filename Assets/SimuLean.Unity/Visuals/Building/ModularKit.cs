using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SimuLean.Unity
{
    /// <summary>
    /// Base class of the parametric kit assemblers (<see cref="UnityModularWall"/>,
    /// <see cref="UnitySteelColumn"/>, <see cref="UnityModularStrip"/>,
    /// <see cref="UnityOverheadCrane"/>). They instantiate pieces of the script-built
    /// kits in <c>Art/Models/IndustrialBuilding</c> and <c>Art/Models/OverheadCrane</c>
    /// under a child named <c>Generated</c>, which <see cref="Rebuild"/> replaces.
    ///
    /// <para><b>Never scale these objects</b> — set their size parameters and rebuild.
    /// Only the tiling pieces are stretched (along their tiling axis, by a few percent),
    /// the fixed pieces (doors, end trucks, trolley, column base/top, brackets) keep
    /// their aspect ratio.</para>
    ///
    /// Purely visual: no part in the DES engine, serialization or headless runs.
    /// </summary>
    [SelectionBase]
    public abstract class ModularAssetBehaviour : MonoBehaviour
    {
        [Header("Performance")]
        [Tooltip("Merge the static pieces into a few meshes (one LODGroup per chunk). " +
                 "Needs readable meshes (SimuLeanModelPostprocessor enables it for kit pieces).")]
        public bool combine = true;

        [Tooltip("Size of a combined chunk along the assembly (m). Smaller chunks cull better.")]
        [Min(2f)] public float chunkLength = 16f;

        /// <summary>Regenerates the assembly from the current parameters.</summary>
        public abstract void Rebuild();

        /// <summary>One-line summary for the inspector.</summary>
        public abstract string GetSummary();

        /// <summary>Fills every unassigned kit-piece field (editor only; see <see cref="ModularKit.ResolvePieces"/>).</summary>
        protected abstract void ResolvePieces();

        protected virtual void Reset()
        {
            ResolvePieces();
            Rebuild();
        }

        /// <summary>Fresh, empty <c>Generated</c> container (the previous one is destroyed).</summary>
        protected Transform BeginBuild()
        {
            ResolvePieces();
            return ModularKit.ResetContainer(transform);
        }
    }

    /// <summary>Shared placement, strip and mesh-combine helpers for the modular kits.</summary>
    public static class ModularKit
    {
        public const string ModelsRoot = "Assets/SimuLean.Unity/Art/Models/";
        public const string BuildingKit = "IndustrialBuilding";
        public const string CraneKit = "OverheadCrane";
        public const string GeneratedName = "Generated";
        const string CombinedPrefix = "SimuLeanCombined";

        // Same policy as SimuLeanModelPostprocessor: LOD0 > 25 %, LOD1 > 7 %, the last level > 1 %.
        static readonly float[] LodTransitions = { 0.25f, 0.07f };
        const float CullBelow = 0.01f;
        const int ShadowlessFromLod = 2;
        static readonly Regex LodName = new Regex(@"_LOD(\d+)$");

        // ------------------------------------------------------------------ kit lookup

        /// <summary>The kit piece prefab <c>Art/Models/&lt;kit&gt;/&lt;piece&gt;.fbx</c> (editor only; null in players).</summary>
        public static GameObject Load(string kit, string piece)
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>($"{ModelsRoot}{kit}/{piece}.fbx");
#else
            return null;
#endif
        }

        /// <summary>
        /// Assigns every null public <see cref="GameObject"/> field of <paramref name="pieces"/>
        /// from the kit, using the field name as the piece name (hence the
        /// <c>Wall_Panel</c>-style field names).
        /// </summary>
        public static void ResolvePieces(object pieces, string kit)
        {
            foreach (var f in pieces.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (f.FieldType == typeof(GameObject) && (GameObject)f.GetValue(pieces) == null)
                    f.SetValue(pieces, Load(kit, f.Name));
        }

        // ------------------------------------------------------------------ hierarchy

        public static Transform ResetContainer(Transform owner, string name = GeneratedName)
        {
            var old = owner.Find(name);
            if (old != null)
            {
                foreach (var mf in old.GetComponentsInChildren<MeshFilter>(true))
                    if (mf.sharedMesh != null && mf.sharedMesh.name.StartsWith(CombinedPrefix))
                        DestroySafe(mf.sharedMesh);
                old.gameObject.SetActive(false);     // Destroy() is deferred in play mode
                old.SetParent(null, false);
                DestroySafe(old.gameObject);
            }
            var go = new GameObject(name);
            go.transform.SetParent(owner, false);
            return go.transform;
        }

        public static void DestroySafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        public static GameObject Place(GameObject prefab, Transform parent, Vector3 position, Quaternion rotation,
                                       Vector3 scale)
        {
            if (prefab == null) return null;
            var go = Object.Instantiate(prefab, parent, false);
            go.name = prefab.name;
            var t = go.transform;
            t.localPosition = position;
            t.localRotation = rotation;
            t.localScale = scale;
            return go;
        }

        public static GameObject Place(GameObject prefab, Transform parent, Vector3 position) =>
            Place(prefab, parent, position, Quaternion.identity, Vector3.one);

        public static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            foreach (Transform c in root)
            {
                var r = FindDeep(c, name);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>Bounds of a piece's LOD0 meshes in its root space (works on unreadable meshes).</summary>
        public static Bounds LocalBounds(GameObject prefab)
        {
            var b = new Bounds();
            if (prefab == null) return b;
            bool any = false;
            var root = prefab.transform.worldToLocalMatrix;
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = LodName.Match(mf.name);
                if (mf.sharedMesh == null || (m.Success && m.Groups[1].Value != "0")) continue;
                var M = root * mf.transform.localToWorldMatrix;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = M.MultiplyPoint3x4(corner);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            return b;
        }

        // ------------------------------------------------------------------ strips

        /// <summary>
        /// Lays a run of tiling pieces along parent-local +Z from <paramref name="origin"/>:
        /// [cap][segment × n][cap turned 180° | endCap]. Segments are stretched along Z by
        /// k = run / (n·segmentLength) with n = round(run / segmentLength), so any length
        /// works; <paramref name="center"/> (e.g. the girder logo) replaces the middle
        /// segment. Caps keep their size and extend towards −Z from their joint.
        /// Mirrors <c>simulean_modular.strip</c> used by the Blender previews.
        /// </summary>
        public static List<GameObject> Strip(Transform parent, Vector3 origin, float length, GameObject segment,
                                             GameObject cap = null, GameObject endCap = null,
                                             GameObject center = null, float segmentLength = 2f)
        {
            var list = new List<GameObject>();
            float capLen = cap ? LocalBounds(cap).size.z : 0f;
            float endLen = endCap ? LocalBounds(endCap).size.z : capLen;
            float run = cap ? length - capLen - endLen : length;
            run = Mathf.Max(run, 0.1f * segmentLength);
            int n = Mathf.Max(1, Mathf.RoundToInt(run / segmentLength));
            float k = run / (n * segmentLength);
            float a0 = cap ? capLen : 0f;
            for (int j = 0; j < n; j++)
            {
                var prefab = center != null && j == n / 2 ? center : segment;
                list.Add(Place(prefab, parent, origin + Vector3.forward * (a0 + j * segmentLength * k),
                               Quaternion.identity, new Vector3(1f, 1f, k)));
            }
            if (cap)
            {
                list.Add(Place(cap, parent, origin + Vector3.forward * capLen));
                list.Add(endCap
                    ? Place(endCap, parent, origin + Vector3.forward * (length - endLen))
                    : Place(cap, parent, origin + Vector3.forward * (length - capLen),
                            Quaternion.Euler(0f, 180f, 0f), Vector3.one));
            }
            list.RemoveAll(g => g == null);
            return list;
        }

        // ------------------------------------------------------------------ combine

        /// <summary>
        /// Merges the static <paramref name="pieces"/> (instances under
        /// <paramref name="container"/>) into chunks of <paramref name="chunkLength"/> along
        /// the container's local <paramref name="axis"/> (0 = X, 1 = Y, 2 = Z). Each chunk
        /// gets one renderer per LOD level and a LODGroup; a piece with fewer levels
        /// repeats its last level (as it would under its own LODGroup). One sub-mesh per
        /// material. Pieces whose meshes are not readable are left as they are.
        /// </summary>
        public static void Combine(Transform container, IList<GameObject> pieces, float chunkLength, int axis)
        {
            var chunks = new SortedDictionary<int, List<PieceMeshes>>();
            foreach (var go in pieces)
            {
                if (go == null) continue;
                var pm = PieceMeshes.From(go);
                if (pm == null) continue;
                float a = container.InverseTransformPoint(go.transform.position)[axis];
                int key = Mathf.FloorToInt(a / Mathf.Max(chunkLength, 0.5f));
                if (!chunks.TryGetValue(key, out var list)) chunks[key] = list = new List<PieceMeshes>();
                list.Add(pm);
            }

            var toLocal = container.worldToLocalMatrix;
            foreach (var kv in chunks)
            {
                int levels = 0;
                foreach (var pm in kv.Value) levels = Mathf.Max(levels, pm.Levels);
                var chunk = new GameObject($"Chunk_{kv.Key}");
                chunk.transform.SetParent(container, false);
                var lods = new List<LOD>();
                for (int lvl = 0; lvl < levels; lvl++)
                {
                    var r = BuildLevel(chunk.transform, $"Chunk_{kv.Key}_LOD{lvl}", kv.Value, lvl, toLocal);
                    if (r == null) continue;
                    if (lvl >= ShadowlessFromLod) r.shadowCastingMode = ShadowCastingMode.Off;
                    float h = lvl == levels - 1 ? CullBelow : LodTransitions[Mathf.Min(lvl, LodTransitions.Length - 1)];
                    lods.Add(new LOD(h, new Renderer[] { r }));
                }
                if (lods.Count > 1)
                {
                    var group = chunk.AddComponent<LODGroup>();
                    group.fadeMode = LODFadeMode.None;
                    group.SetLODs(lods.ToArray());
                    group.RecalculateBounds();
                }
                foreach (var pm in kv.Value) DestroySafe(pm.Root);
            }
        }

        static MeshRenderer BuildLevel(Transform chunk, string name, List<PieceMeshes> pieces, int level,
                                       Matrix4x4 toLocal)
        {
            var byMat = new Dictionary<Material, List<CombineInstance>>();
            var order = new List<Material>();
            long verts = 0;
            foreach (var pm in pieces)
                foreach (var mr in pm.At(level))
                {
                    var mesh = mr.GetComponent<MeshFilter>().sharedMesh;
                    var mats = mr.sharedMaterials;
                    var M = toLocal * mr.transform.localToWorldMatrix;
                    verts += mesh.vertexCount;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        var mat = mats.Length > 0 ? mats[Mathf.Min(s, mats.Length - 1)] : null;
                        if (mat == null) continue;
                        if (!byMat.TryGetValue(mat, out var list))
                        {
                            byMat[mat] = list = new List<CombineInstance>();
                            order.Add(mat);
                        }
                        list.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = M });
                    }
                }
            if (order.Count == 0) return null;

            var format = verts > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            Mesh result;
            if (order.Count == 1)
            {
                result = new Mesh { indexFormat = format };
                result.CombineMeshes(byMat[order[0]].ToArray(), true, true);
            }
            else
            {
                var parts = new CombineInstance[order.Count];
                for (int i = 0; i < order.Count; i++)
                {
                    var part = new Mesh { indexFormat = format };
                    part.CombineMeshes(byMat[order[i]].ToArray(), true, true);
                    parts[i] = new CombineInstance { mesh = part, transform = Matrix4x4.identity };
                }
                result = new Mesh { indexFormat = format };
                result.CombineMeshes(parts, false, false);
                foreach (var p in parts) DestroySafe(p.mesh);
            }
            result.name = $"{CombinedPrefix}_{name}";

            var go = new GameObject(name);
            go.transform.SetParent(chunk, false);
            go.AddComponent<MeshFilter>().sharedMesh = result;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = order.ToArray();
            return renderer;
        }

        /// <summary>The renderers of one piece instance, sorted by LOD level.</summary>
        class PieceMeshes
        {
            public GameObject Root;
            readonly SortedDictionary<int, List<MeshRenderer>> byLevel = new SortedDictionary<int, List<MeshRenderer>>();
            public int Levels => byLevel.Count == 0 ? 0 : LastKey + 1;
            int LastKey { get { int k = 0; foreach (var key in byLevel.Keys) k = key; return k; } }

            public static PieceMeshes From(GameObject go)
            {
                var pm = new PieceMeshes { Root = go };
                foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (!mf.sharedMesh.isReadable) return null;
                    var m = LodName.Match(mr.name);
                    int lvl = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                    if (!pm.byLevel.TryGetValue(lvl, out var list)) pm.byLevel[lvl] = list = new List<MeshRenderer>();
                    list.Add(mr);
                }
                return pm.byLevel.Count > 0 ? pm : null;
            }

            /// <summary>Renderers shown at <paramref name="level"/>: that level, else the highest below it.</summary>
            public List<MeshRenderer> At(int level)
            {
                List<MeshRenderer> best = null;
                foreach (var kv in byLevel)
                    if (kv.Key <= level) best = kv.Value;
                return best ?? new List<MeshRenderer>();
            }
        }
    }
}
