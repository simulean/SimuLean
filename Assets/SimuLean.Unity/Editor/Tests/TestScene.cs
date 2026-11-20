using System;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// A fresh scene for a test that builds a model: an empty scene opened additively (the Test Runner's untitled scene
    /// when it runs in one: no additive scene can be added to it) with a root object for the model. Dispose closes the
    /// scene without saving (or destroys the root in the untitled scene). Optionally the scene is made active (objects the
    /// commands create land in it) and the undo operations of the test are reverted first, with no redo: a removed object's
    /// record would otherwise recreate it in the scene open in the editor at the user's next Undo.
    /// </summary>
    public sealed class TestScene : IDisposable
    {
        private readonly Scene active;
        private readonly bool temporary, activated;
        private readonly int undoGroup = -1;
        private bool disposed;

        public Scene Scene { get; }
        public GameObject Root { get; }

        /// <param name="rootName">The root object's name (a model's elements go under it).</param>
        /// <param name="activate">Make the scene the active one while the test runs.</param>
        /// <param name="revertUndo">Revert the undo operations recorded after the scene was opened, before it is closed.</param>
        public TestScene(string rootName = "Model", bool activate = false, bool revertUndo = false)
        {
            active = SceneManager.GetActiveScene();
            temporary = !string.IsNullOrEmpty(active.path);
            Scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            activated = temporary && activate;
            if (activated) SceneManager.SetActiveScene(Scene);
            Root = new GameObject(rootName);
            SceneManager.MoveGameObjectToScene(Root, Scene);
            if (revertUndo)
            {
                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
            }
        }

        /// <summary>A new object under the root with a component <typeparamref name="T"/>.</summary>
        public T Make<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            go.transform.SetParent(Root.transform);
            return go.AddComponent<T>();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (undoGroup >= 0) Undo.RevertAllDownToGroup(undoGroup);
            if (temporary)
            {
                if (activated && active.IsValid()) SceneManager.SetActiveScene(active);
                EditorSceneManager.CloseScene(Scene, true);
            }
            else Object.DestroyImmediate(Root);
        }
    }

    /// <summary>
    /// A fixture whose every test gets its own <see cref="TestScene"/>, active, with the test's undo operations reverted at
    /// the end. A derived fixture's own [SetUp] runs after the scene is open and its [TearDown] before it is closed.
    /// </summary>
    public abstract class SceneTestFixture
    {
        /// <summary>The scene of the running test.</summary>
        protected TestScene Test { get; private set; }

        /// <summary>The name of the scene's root object.</summary>
        protected virtual string RootName => "Model";

        [SetUp]
        public void OpenTestScene() => Test = new TestScene(RootName, activate: true, revertUndo: true);

        [TearDown]
        public void CloseTestScene()
        {
            Test?.Dispose();
            Test = null;
        }
    }
}
