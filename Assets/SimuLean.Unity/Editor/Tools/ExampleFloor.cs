using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The floor of an example scene: a plane with the sample floor material under everything drawn in the scene, with a
    /// margin round it (as the floors placed by hand in the other examples). Used by the example builders
    /// (<see cref="WarehouseToLineExample"/>, <see cref="CombinerAssemblyExample"/>), from <i>Tools ▸ SimuLean ▸ Developer ▸
    /// Add Example Floor to the Open Scene</i>, or in batch mode with
    /// <c>-executeMethod SimuLean.Unity.Editor.ExampleFloor.AddToScenesBatch -floorScenes "Assets/A.unity;Assets/B.unity"</c>.
    /// A scene that has a <c>Floor</c> keeps it (it is replaced).
    /// </summary>
    public static class ExampleFloor
    {
        public const string Name = "Floor";
        public const string MaterialPath = "Assets/SampleSceneAssets/Floor.mat";

        /// <summary>Adds (or replaces) the floor of <paramref name="scene"/>; <paramref name="margin"/> metres round the
        /// renderers. Returns it, or null when nothing is drawn.</summary>
        public static GameObject Add(Scene scene, float margin = 3f)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
                if (root.name == Name) UnityEngine.Object.DestroyImmediate(root);

            Bounds? all = null;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                    if (all == null) all = r.bounds; else { Bounds b = all.Value; b.Encapsulate(r.bounds); all = b; }
                }
            if (all == null) return null;

            Bounds box = all.Value;
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);   // 10 x 10 m at scale 1
            floor.name = Name;
            SceneManager.MoveGameObjectToScene(floor, scene);
            floor.transform.position = new Vector3(box.center.x, -0.01f, box.center.z);
            floor.transform.localScale = new Vector3((box.size.x + 2 * margin) / 10f, 1f, (box.size.z + 2 * margin) / 10f);
            UnityEngine.Object.DestroyImmediate(floor.GetComponent<Collider>());
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material != null) floor.GetComponent<Renderer>().sharedMaterial = material;
            else Debug.LogWarning($"[SimuLean] Floor material {MaterialPath} not found.");
            floor.transform.SetAsFirstSibling();
            return floor;
        }

        [MenuItem("Tools/SimuLean/Developer/Add Example Floor to the Open Scene", priority = 220)]
        public static void AddToOpenScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (Add(scene) != null) EditorSceneManager.MarkSceneDirty(scene);
        }

        /// <summary>Batch mode: adds the floor to every scene of <c>-floorScenes</c> (separated by ';') and saves it.</summary>
        public static void AddToScenesBatch()
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-floorScenes");
            if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("-floorScenes \"Assets/A.unity;Assets/B.unity\" is required.");
            foreach (string path in args[i + 1].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Scene scene = EditorSceneManager.OpenScene(path.Trim(), OpenSceneMode.Single);
                if (Add(scene) == null) Debug.LogWarning($"[SimuLean] {path}: nothing drawn, no floor added.");
                EditorSceneManager.SaveScene(scene);
            }
        }
    }
}
