using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The floor of a modelling scene (<see cref="SceneFloor"/>): <i>Set Up Scene</i> lays one of 100 × 100 m
    /// when the scene has no ground, never a second one; <i>Fit Floor to Model</i> covers the model on whole grid cells.
    /// </summary>
    public class SceneFloorTests
    {
        private Scene active, scene;
        private bool temporary;
        private readonly System.Collections.Generic.HashSet<GameObject> before = new System.Collections.Generic.HashSet<GameObject>();

        // A scene of its own when the open one is saved; else (the test runner's untitled scene) the open one, cleaned after.
        [SetUp]
        public void SetUp()
        {
            active = SceneManager.GetActiveScene();
            temporary = !string.IsNullOrEmpty(active.path);
            scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            if (temporary) SceneManager.SetActiveScene(scene);
            before.Clear();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                // the runner's own new scene was set up (and got a floor) when it was created: start without one
                if (root.GetComponentInChildren<SimuLeanFloor>(true) != null) { Object.DestroyImmediate(root); continue; }
                before.Add(root);
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (temporary)
            {
                if (active.IsValid()) SceneManager.SetActiveScene(active);
                EditorSceneManager.CloseScene(scene, true);
                return;
            }
            foreach (GameObject root in scene.GetRootGameObjects())
                if (!before.Contains(root)) Object.DestroyImmediate(root);
        }

        private int Floors()
        {
            int n = 0;
            foreach (GameObject root in scene.GetRootGameObjects()) n += root.GetComponentsInChildren<SimuLeanFloor>(true).Length;
            return n;
        }

        [Test]
        public void SetUpLaysOneFloorWhereThereIsNoGround()
        {
            SimuLeanSceneSetup.SetUpScene(scene);
            Assert.AreEqual(1, Floors(), "a floor to drag the elements onto");
            SimuLeanFloor floor = null;
            foreach (GameObject root in scene.GetRootGameObjects()) floor = floor ?? root.GetComponentInChildren<SimuLeanFloor>();
            Assert.AreEqual(100f, floor.Size.x, 1e-3);
            Assert.AreEqual(100f, floor.Size.y, 1e-3);
            Assert.IsNull(floor.GetComponent<Collider>(), "decoration: no collider");
            SimuLeanSceneSetup.SetUpScene(scene);
            Assert.AreEqual(1, Floors(), "set up again: still one floor");
        }

        [Test]
        public void AGroundOfTheSceneIsKept()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);   // 10 × 10 m at scale 1
            SceneManager.MoveGameObjectToScene(ground, scene);
            ground.name = "Hall";
            ground.transform.localScale = new Vector3(4f, 1f, 3f);          // 40 × 30 m, flat at ground level
            SimuLeanSceneSetup.SetUpScene(scene);
            Assert.AreEqual(0, Floors(), "the scene's own ground is enough");
        }

        [Test]
        public void FitCoversTheModelOnWholeCells()
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(box, scene);
            box.transform.position = new Vector3(30f, 1f, 10f);
            box.transform.localScale = new Vector3(40f, 2f, 4f);
            SimuLeanFloor floor = SceneFloor.Fit(scene, margin: 5f);
            Assert.AreEqual(50f, floor.Size.x, 1e-4, "40 m + 2 × 5 m");
            Assert.AreEqual(SceneFloor.MinSize, floor.Size.y, 1e-4, "at least the minimum");
            Assert.AreEqual(30f, floor.transform.position.x, 1e-4);
            Assert.AreEqual(10f, floor.transform.position.z, 1e-4);
            Assert.AreSame(floor, SceneFloor.Fit(scene), "fitted again: the same floor");
            Assert.AreEqual(1, Floors());
        }
    }
}
