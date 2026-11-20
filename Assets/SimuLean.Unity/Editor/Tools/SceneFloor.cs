using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The floor of a modelling scene: <see cref="SimuLeanSceneSetup"/> lays a <see cref="SimuLeanFloor"/> of
    /// <see cref="DefaultSize"/> × <see cref="DefaultSize"/> m at the origin when the scene has no ground (a SimuLean floor,
    /// an object called Floor / Ground, or a large flat surface at ground level), so elements are dragged onto something;
    /// <i>Tools ▸ SimuLean ▸ Fit Floor to Model</i> resizes it to everything drawn, with a margin, on whole grid cells.
    /// </summary>
    public static class SceneFloor
    {
        public const float DefaultSize = 100f;
        public const float MinSize = 20f;
        public const string Name = "Floor";
        public const string MaterialPath = "Assets/SimuLean.Unity/Art/Floor/FloorGrid.mat";

        /// <summary>Lays the default floor when <paramref name="scene"/> has no ground; returns the floor laid, or null.</summary>
        public static SimuLeanFloor Ensure(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || HasGround(scene)) return null;
            return Create(scene, new Vector2(DefaultSize, DefaultSize), Vector3.zero);
        }

        /// <summary>True when the scene already has a ground: a SimuLean floor, a root object called Floor or Ground, or a
        /// flat renderer at least <see cref="MinSize"/> m on both sides lying at ground level.</summary>
        public static bool HasGround(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == Name || root.name == "Ground") return true;
                if (root.GetComponentInChildren<SimuLeanFloor>(true) != null) return true;
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    Bounds b = r.bounds;
                    if (b.size.x >= MinSize && b.size.z >= MinSize && b.size.y < 0.5f && b.max.y < 0.5f) return true;
                }
            }
            return false;
        }

        /// <summary>A floor of <paramref name="size"/> (x, z) metres centred at <paramref name="center"/> (undoable).</summary>
        public static SimuLeanFloor Create(Scene scene, Vector2 size, Vector3 center)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = Name;
            Object.DestroyImmediate(go.GetComponent<Collider>());   // decoration: nothing to hit (walk grids, placement)
            if (scene.IsValid()) SceneManager.MoveGameObjectToScene(go, scene);
            go.transform.SetPositionAndRotation(new Vector3(center.x, -0.01f, center.z), Quaternion.Euler(90f, 0f, 0f));
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material != null) go.GetComponent<MeshRenderer>().sharedMaterial = material;
            else Debug.LogWarning($"[SimuLean] Floor material {MaterialPath} not found.");
            var floor = go.AddComponent<SimuLeanFloor>();
            go.transform.SetAsFirstSibling();
            Undo.RegisterCreatedObjectUndo(go, "Create floor");
            return floor;
        }

        /// <summary>
        /// Resizes the scene's floor (laid if missing) to everything drawn but the floor, plus <paramref name="margin"/>
        /// metres round it, rounded up to whole grid cells and at least <see cref="MinSize"/>; an empty scene gets the
        /// default floor at the origin. Returns the floor.
        /// </summary>
        public static SimuLeanFloor Fit(Scene scene, float margin = 5f)
        {
            SimuLeanFloor floor = null;
            foreach (GameObject root in scene.GetRootGameObjects())
                if ((floor = root.GetComponentInChildren<SimuLeanFloor>(true)) != null) break;

            Bounds? all = null;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.GetComponent<SimuLeanFloor>() != null || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer)
                        continue;
                    if (all == null) all = r.bounds; else { Bounds b = all.Value; b.Encapsulate(r.bounds); all = b; }
                }

            Vector2 size = new Vector2(DefaultSize, DefaultSize);
            Vector3 center = Vector3.zero;
            if (all != null)
            {
                float cell = floor != null ? Mathf.Max(1f, floor.gridCell) : 5f;
                Bounds box = all.Value;
                size = new Vector2(RoundUp(Mathf.Max(MinSize, box.size.x + 2 * margin), cell),
                                   RoundUp(Mathf.Max(MinSize, box.size.z + 2 * margin), cell));
                center = new Vector3(Mathf.Round(box.center.x), 0f, Mathf.Round(box.center.z));
            }
            if (floor == null) return Create(scene, size, center);
            Undo.RecordObject(floor.transform, "Fit floor to model");
            floor.transform.position = new Vector3(center.x, floor.transform.position.y, center.z);
            floor.transform.localScale = new Vector3(size.x, size.y, 1f);
            return floor;
        }

        private static float RoundUp(float v, float step) => Mathf.Ceil(v / step) * step;

        [MenuItem("Tools/SimuLean/Fit Floor to Model", priority = 105)]
        private static void FitActiveScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            Selection.activeObject = Fit(scene).gameObject;
            EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
