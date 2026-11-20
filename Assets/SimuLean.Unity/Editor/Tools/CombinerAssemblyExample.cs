using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The example of the Combiner's assembly view: the scene
    /// <c>Assets/Scenes/LibraryExamples/Assembly_Kitting.unity</c>. A kitting bench (a Combiner) puts 2 cartons and 2 small
    /// bins into each empty tote; the cartons and the bins wait at their own entry beside the bench
    /// (<see cref="UnityCombiner.componentEntries"/>) and travel into the tote one after another during the 12 s process. A
    /// tote arrives every 20 s, a carton every 8 s, a bin every 9 s. Rebuilt by <see cref="RebuildScene"/> (<i>Tools ▸ SimuLean ▸ Examples</i>) or in
    /// batch mode with <c>-executeMethod SimuLean.Unity.Editor.CombinerAssemblyExample.RebuildScene</c>; <see cref="Build"/>
    /// builds the model under a root object of any scene.
    /// </summary>
    public static class CombinerAssemblyExample
    {
        public const string ScenePath = "Assets/Scenes/LibraryExamples/Assembly_Kitting.unity";

        [MenuItem("Tools/SimuLean/Developer/Rebuild Kitting Scene", priority = 201)]
        public static void RebuildScene()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var clock = SimuLeanSceneSetup.SetUpScene(scene);
            if (clock != null) clock.MaxTime = 3600;
            Build(scene);
            ExampleFloor.Add(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        /// <summary>Builds the model under a new root object <c>Model</c> of <paramref name="scene"/>; returns the root.</summary>
        public static GameObject Build(Scene scene)
        {
            var root = new GameObject("Model");
            SceneManager.MoveGameObjectToScene(root, scene);
            T Make<T>(string name, Vector3 at) where T : Component
            {
                var go = new GameObject(name);
                go.transform.SetParent(root.transform);
                go.transform.position = at;
                return go.AddComponent<T>();
            }
            RandomProcessConfig Constant(float v) => new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = v };
            GameObject Asset(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(path);

            // the kitting bench: the library's Combiner (a work bench), an entry per component on the floor at its left
            var prefab = Asset("Assets/SimuLean.Unity/Prefabs/Elements/Combiner.prefab");
            var table = prefab != null ? (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene) : new GameObject();
            table.name = "KittingBench";
            table.transform.SetParent(root.transform);
            table.transform.position = new Vector3(0, 0, 0);
            var bench = table.GetComponent<UnityCombiner>();
            if (bench == null) bench = table.AddComponent<UnityCombiner>();
            bench.requirements = new[] { 2, 2 };
            bench.delayStrategy = Constant(12);
            bench.visualMode = UnityCombiner.VisualMode.Attach;
            bench.animateAssembly = true;
            Transform Entry(string name, Vector3 local)
            {
                var e = new GameObject(name).transform;
                e.SetParent(table.transform, false);
                e.localPosition = local;
                return e;
            }
            bench.componentEntries = new List<Transform> { Entry("CartonEntry", new Vector3(-1.15f, 0f, -0.25f)), Entry("BinEntry", new Vector3(-1.15f, 0f, 0.25f)) };

            // totes, cartons and bins, each through its buffer
            UnityQueue Feed(string what, string model, float every, Vector3 at, int buffer)
            {
                var src = Make<UnitySource>(what, at + new Vector3(0, 0, -8));
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = Constant(every);
                src.itemPrefab = Asset(model);
                var q = Make<UnityQueue>(what + "Buffer", at + new Vector3(0, 0, -4));
                q.capacity = buffer;
                src.nextElements = new List<SElement> { q };
                q.nextElements = new List<SElement> { bench };
                LibraryPresets.AddModel(src.gameObject, ("Source station", "SourceStation/SourceStation.fbx"));
                LibraryPresets.AddModel(q.gameObject, ("Buffer position", "BufferPosition/BufferPosition.fbx"));
                return q;
            }
            var totes = Feed("Totes", LibraryPresets.Items + "Tote.prefab", 20, new Vector3(1.5f, 0, 0), 3);
            var cartons = Feed("Cartons", LibraryPresets.Models + "WarehouseLoadUnits/CardboardCarton_300x200x200.fbx", 8, new Vector3(-1.5f, 0, 0), 6);
            var bins = Feed("Bins", LibraryPresets.Models + "WarehouseLoadUnits/SmallLoadCarrier_300x200x147.fbx", 9, new Vector3(-4.5f, 0, 0), 6);
            bench.inputOrder = new List<SElement> { totes, cartons, bins };

            var loads = Make<UnitySink>("Kits", new Vector3(0, 0, 4));
            LibraryPresets.AddModel(loads.gameObject, ("Sink station", "SinkStation/SinkStation.fbx"));
            bench.nextElements = new List<SElement> { loads };
            return root;
        }
    }
}
