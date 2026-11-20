using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The Place tool's turn (<see cref="SimuLeanPlaceTool"/>): the selection turns about the vertical through
    /// the pivot, positions and headings together, undoable; the SimuLean toolbar is a Scene view overlay.
    /// </summary>
    public class PlaceToolTests
    {
        private Scene scene;
        private GameObject a, b;

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            a = new GameObject("A"); b = new GameObject("B");
            SceneManager.MoveGameObjectToScene(a, scene);
            SceneManager.MoveGameObjectToScene(b, scene);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(a); Object.DestroyImmediate(b);
            EditorSceneManager.ClosePreviewScene(scene);
        }

        [Test]
        public void TurningMovesAndTurnsTheSelectionAboutThePivot()
        {
            a.transform.position = new Vector3(2f, 0f, 0f);
            b.transform.position = new Vector3(-2f, 0f, 0f);
            Undo.IncrementCurrentGroup();
            SimuLeanPlaceTool.Turn(new[] { a.transform, b.transform }, Vector3.zero, 90f, "Turn");
            Assert.Less(Vector3.Distance(new Vector3(0f, 0f, -2f), a.transform.position), 1e-4f, "+x turned 90° clockwise seen from above: −z");
            Assert.Less(Vector3.Distance(new Vector3(0f, 0f, 2f), b.transform.position), 1e-4f);
            Assert.AreEqual(90f, a.transform.eulerAngles.y, 1e-3f);
            Undo.PerformUndo();
            Assert.Less(Vector3.Distance(new Vector3(2f, 0f, 0f), a.transform.position), 1e-4f, "undone");
            Assert.AreEqual(0f, a.transform.eulerAngles.y, 1e-3f);
        }

        [Test]
        public void TheSimuLeanBarIsASceneViewOverlay()
        {
            var attr = (UnityEditor.Overlays.OverlayAttribute)System.Attribute.GetCustomAttribute(
                typeof(SimuLeanSceneOverlay), typeof(UnityEditor.Overlays.OverlayAttribute));
            Assert.IsNotNull(attr);
            Assert.AreEqual(typeof(SceneView), attr.editorWindowType);
            Assert.IsTrue(attr.defaultDisplay, "shown by default");
        }
    }
}
