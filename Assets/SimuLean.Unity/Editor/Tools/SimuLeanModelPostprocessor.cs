using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Import conventions for the script-built 3D asset library
    /// (Tools/Blender -> Assets/SimuLean.Unity/Art/Models/&lt;Name&gt;/).
    ///
    /// Each model folder holds &lt;Name&gt;.fbx plus a baked PBR set
    /// &lt;Name&gt;_Albedo / _MetallicSmoothness / _Normal / _Occlusion / _Emission .png.
    /// A modular KIT folder (IndustrialBuilding, OverheadCrane) instead holds one FBX per
    /// piece (Wall_Panel.fbx, EndTruck.fbx, ...) sharing the folder's texture set.
    /// On import this postprocessor:
    ///  - sets the texture types (normal map, linear masks, 2048 max);
    ///  - builds &lt;Name&gt;.mat (Standard, GPU instancing on) from the texture set, and
    ///    one material per extra colour set (&lt;Name&gt;_Orange_Albedo.png -> &lt;Name&gt;_Orange.mat);
    ///  - assigns that single shared material to every renderer of the model
    ///    (&lt;stem&gt;.mat, or the folder's &lt;Folder&gt;.mat for kit pieces);
    ///  - keeps kit-piece meshes readable so ModularKit.Combine can merge them;
    ///  - makes the material Cutout when the albedo has an alpha channel (hair cards, operator);
    ///  - replaces Unity's per-node auto LODGroups with ONE LODGroup on the model root,
    ///    built from every renderer named *_LOD0/1/2 anywhere in the hierarchy
    ///    (the last level is culled below 1 % of the screen height).
    ///
    /// Animate the named transforms (Mast, Carriage, Wheel_FL, Leaf, Trolley nodes, ...),
    /// never the *_LODn meshes. Only folders with their own &lt;Folder&gt;_Albedo.png are handled
    /// (<see cref="InLibrary"/>), so the pre-existing models keep their import settings.
    /// </summary>
    public class SimuLeanModelPostprocessor : AssetPostprocessor
    {
        public const string ModelsRoot = "Assets/SimuLean.Unity/Art/Models/";

        // Screen-relative height at which each level stops being used (LOD0 > 25 %, LOD1 > 7 %;
        // the LAST level, whichever it is, stays until 1 % and is culled below). Tuned for
        // scenes with dozens of vehicles. ModularKit.Combine uses the same policy.
        static readonly float[] LodTransitions = { 0.25f, 0.07f };
        const float CullBelow = 0.01f;
        const int ShadowlessFromLod = 2;
        const float EmissionIntensity = 2f;     // matches the Blender preview material

        static readonly Regex LodName = new Regex(@"_LOD(\d+)$");
        static readonly string[] MapSuffixes =
            { "_Albedo", "_MetallicSmoothness", "_Normal", "_Occlusion", "_Emission" };

        /// <summary>
        /// A library asset: under <see cref="ModelsRoot"/> AND in a folder that holds its own baked texture set
        /// (<c>&lt;Folder&gt;/&lt;Folder&gt;_Albedo.png</c>). Older hand-made models in the same root (AGV_Mouse,
        /// BufferBox, SourceFiles, ...) have no such set and keep their import settings untouched.
        /// </summary>
        static bool InLibrary(string path)
        {
            path = path.Replace('\\', '/');
            if (!path.StartsWith(ModelsRoot)) return false;
            string dir = Dir(path);
            return File.Exists($"{dir}/{Path.GetFileName(dir)}_Albedo.png");
        }

        static string Dir(string path) => Path.GetDirectoryName(path).Replace('\\', '/');
        static string FolderName(string path) => Path.GetFileName(Dir(path));

        /// A piece of a modular kit: an FBX not named after its folder (Wall_Panel.fbx in IndustrialBuilding/).
        static bool IsKitPiece(string path) =>
            Path.GetFileNameWithoutExtension(path) != FolderName(path);

        // ------------------------------------------------------------------ textures

        void OnPreprocessTexture()
        {
            if (!InLibrary(assetPath)) return;
            var ti = (TextureImporter)assetImporter;
            string stem = Path.GetFileNameWithoutExtension(assetPath);
            ti.maxTextureSize = Mathf.Min(ti.maxTextureSize, 2048);
            ti.mipmapEnabled = true;
            ti.anisoLevel = 4;
            if (stem.EndsWith("_Normal"))
            {
                ti.textureType = TextureImporterType.NormalMap;     // OpenGL (Y+) from Blender
            }
            else if (stem.EndsWith("_Albedo"))
            {
                ti.alphaIsTransparency = ti.DoesSourceTextureHaveAlpha();   // cut-out cards: no dark fringes
            }
            else if (stem.EndsWith("_MetallicSmoothness") || stem.EndsWith("_Occlusion"))
            {
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = false;
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.alphaIsTransparency = false;
            }
        }

        // ------------------------------------------------------------------ models

        void OnPreprocessModel()
        {
            if (!InLibrary(assetPath)) return;
            var mi = (ModelImporter)assetImporter;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importTangents = ModelImporterTangents.CalculateMikk;  // Blender bakes in MikkTSpace
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.isReadable = IsKitPiece(assetPath);      // ModularKit.Combine merges kit pieces on the CPU
            // The FBX materials are placeholders: the shared library material is assigned below.
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
        }

        void OnPostprocessModel(GameObject root)
        {
            if (!InLibrary(assetPath)) return;
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath(assetPath));
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (material != null)
                foreach (var r in renderers)
                    r.sharedMaterials = Enumerable.Repeat(material, r.sharedMaterials.Length).ToArray();
            BuildLodGroup(root, renderers);
        }

        static void BuildLodGroup(GameObject root, Renderer[] renderers)
        {
            var byLevel = new SortedDictionary<int, List<Renderer>>();
            foreach (var r in renderers)
            {
                var m = LodName.Match(r.name);
                if (!m.Success) continue;
                int level = int.Parse(m.Groups[1].Value);
                if (!byLevel.TryGetValue(level, out var list)) byLevel[level] = list = new List<Renderer>();
                list.Add(r);
                if (level >= ShadowlessFromLod) r.shadowCastingMode = ShadowCastingMode.Off;
            }
            if (byLevel.Count < 2) return;

            foreach (var g in root.GetComponentsInChildren<LODGroup>(true))
                Object.DestroyImmediate(g);

            var lods = new List<LOD>();
            int i = 0;
            foreach (var kv in byLevel)
            {
                float h = i == byLevel.Count - 1 ? CullBelow : LodTransitions[Mathf.Min(i, LodTransitions.Length - 1)];
                lods.Add(new LOD(h, kv.Value.ToArray()));
                i++;
            }
            var group = root.AddComponent<LODGroup>();
            group.fadeMode = LODFadeMode.None;
            group.SetLODs(lods.ToArray());
            group.RecalculateBounds();
        }

        // ------------------------------------------------------------------ materials

        /// &lt;stem&gt;.mat next to the model, else the folder's shared &lt;Folder&gt;.mat (kit pieces).
        static string MaterialPath(string modelPath)
        {
            string dir = Dir(modelPath);
            string own = $"{dir}/{Path.GetFileNameWithoutExtension(modelPath)}.mat";
            if (!IsKitPiece(modelPath) || AssetDatabase.LoadAssetAtPath<Material>(own) != null) return own;
            return $"{dir}/{FolderName(modelPath)}.mat";
        }

        /// Creating assets is only safe after import, so materials are made here; a model
        /// imported before its material existed is reimported once to pick it up.
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var modelsToReimport = new HashSet<string>();
            foreach (var path in imported)
            {
                if (!InLibrary(path)) continue;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                string stem = Path.GetFileNameWithoutExtension(path);
                string dir = Path.GetDirectoryName(path).Replace('\\', '/');

                if (ext == ".png" && stem.EndsWith("_Albedo"))
                {
                    string baseName = stem.Substring(0, stem.Length - "_Albedo".Length);
                    // a kit's shared material appearing after its pieces: reassign it to them
                    if (EnsureMaterial(dir, baseName) && baseName == Path.GetFileName(dir))
                        foreach (var fbx in Directory.GetFiles(dir, "*.fbx"))
                            if (IsKitPiece(fbx)) modelsToReimport.Add(fbx.Replace('\\', '/'));
                }
                else if (ext == ".fbx")
                {
                    bool created = EnsureMaterial(dir, IsKitPiece(path) ? FolderName(path) : stem);
                    if (created) modelsToReimport.Add(path);
                }
            }
            if (modelsToReimport.Count > 0)
                EditorApplication.delayCall += () =>
                {
                    foreach (var p in modelsToReimport)
                        AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
                };
        }

        /// Creates or refreshes &lt;dir&gt;/&lt;baseName&gt;.mat from the baked maps; returns true if created.
        static bool EnsureMaterial(string dir, string baseName)
        {
            var albedo = LoadMap(dir, baseName, "_Albedo");
            if (albedo == null) return false;

            string matPath = $"{dir}/{baseName}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            bool created = mat == null;
            if (created)
                mat = new Material(Shader.Find("Standard")) { name = baseName };

            mat.SetColor("_Color", Color.white);
            mat.SetTexture("_MainTex", albedo);
            SetCutout(mat, AlbedoHasAlpha(dir, baseName));

            var ms = LoadMap(dir, baseName, "_MetallicSmoothness");
            mat.SetTexture("_MetallicGlossMap", ms);
            SetKeyword(mat, "_METALLICGLOSSMAP", ms != null);
            mat.SetFloat("_GlossMapScale", 1f);
            mat.SetFloat("_SmoothnessTextureChannel", 0f);   // metallic alpha

            var nrm = LoadMap(dir, baseName, "_Normal");
            mat.SetTexture("_BumpMap", nrm);
            mat.SetFloat("_BumpScale", 1f);
            SetKeyword(mat, "_NORMALMAP", nrm != null);

            var ao = LoadMap(dir, baseName, "_Occlusion");
            mat.SetTexture("_OcclusionMap", ao);
            mat.SetFloat("_OcclusionStrength", 1f);

            var em = LoadMap(dir, baseName, "_Emission");
            mat.SetTexture("_EmissionMap", em);
            mat.SetColor("_EmissionColor", em != null ? Color.white * EmissionIntensity : Color.black);
            SetKeyword(mat, "_EMISSION", em != null);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;

            mat.enableInstancing = true;   // dozens of identical vehicles batch into few draws

            if (created) AssetDatabase.CreateAsset(mat, matPath);
            else EditorUtility.SetDirty(mat);
            return created;
        }

        /// An albedo saved with an alpha channel (RGBA: cut-out cards such as hair) makes the material Cutout.
        static bool AlbedoHasAlpha(string dir, string baseName) =>
            AssetImporter.GetAtPath($"{dir}/{baseName}_Albedo.png") is TextureImporter ti && ti.DoesSourceTextureHaveAlpha();

        static void SetCutout(Material m, bool cutout)
        {
            m.SetFloat("_Mode", cutout ? 1f : 0f);
            m.SetFloat("_Cutoff", 0.5f);
            m.SetOverrideTag("RenderType", cutout ? "TransparentCutout" : "");
            SetKeyword(m, "_ALPHATEST_ON", cutout);
            m.renderQueue = cutout ? (int)RenderQueue.AlphaTest : -1;
        }

        static Texture2D LoadMap(string dir, string baseName, string suffix) =>
            AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}/{baseName}{suffix}.png");

        static void SetKeyword(Material m, string keyword, bool on)
        {
            if (on) m.EnableKeyword(keyword); else m.DisableKeyword(keyword);
        }

        [MenuItem("Tools/SimuLean/Reimport 3D Model Library")]
        static void ReimportLibrary()
        {
            var paths = AssetDatabase.FindAssets("", new[] { ModelsRoot.TrimEnd('/') })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(InLibrary)
                .Where(p => MapSuffixes.Any(s => Path.GetFileNameWithoutExtension(p).EndsWith(s))
                            || p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)) // textures first
                .ToList();
            foreach (var p in paths)
                AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
            Debug.Log($"[SimuLean] Reimported {paths.Count} model-library assets.");
        }
    }
}
