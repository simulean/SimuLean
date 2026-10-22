using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>A deleted element leaves no empty Next Elements entry (index-based routing stays right).</summary>
    public class ConnectionCleanerTests
    {
        [Test]
        public void ADeletedElementsConnectionIsRemoved()
        {
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            try
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                T Make<T>(string n) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    return go.AddComponent<T>();
                }
                var a = Make<UnityQueue>("A");
                var b = Make<UnitySink>("B");
                var c = Make<UnitySink>("C");
                a.nextElements = new List<SElement> { b, c };
                Object.DestroyImmediate(b.gameObject);
                Assert.IsTrue(a.nextElements.Count == 2 && a.nextElements[0] == null, "before: an empty entry");

                LogAssert.Expect(LogType.Warning, new Regex("empty Next Elements entry"));
                SceneExtractor.Extract(root);   // the extraction warns (the outputs after it move up)

                Assert.AreEqual(1, ConnectionCleaner.Clean(scene));
                Assert.AreEqual(1, a.nextElements.Count);
                Assert.AreSame(c, a.nextElements[0], "C is now output 0, as the run sees it");
                Assert.AreEqual(0, ConnectionCleaner.Clean(scene), "nothing left");
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }

        /// <summary>The hook itself: an undoable delete (the Delete key, the MCP's delete_gameobject) cleans the entry
        /// once the editor publishes its change events — no explicit Clean call (the delayCall-only cleaner
        /// never ran while the editor was in the background).</summary>
        [UnityTest]
        public IEnumerator AnUndoableDeleteIsCleanedByTheHook()
        {
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            SceneManager.MoveGameObjectToScene(root, scene);
            T Make<T>(string n) where T : Component
            {
                var go = new GameObject(n);
                go.transform.SetParent(root.transform);
                return go.AddComponent<T>();
            }
            var a = Make<UnityQueue>("A");
            var b = Make<UnitySink>("B");
            var c = Make<UnitySink>("C");
            a.nextElements = new List<SElement> { b, c };

            // its own undo group, reverted at the end without a redo: the record would otherwise restore "B" into the
            // scene open in the editor at the user's next Undo
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.DestroyObjectImmediate(b.gameObject);
            for (int frame = 0; frame < 10 && a.nextElements.Count != 1; frame++) yield return null;   // events published
            try
            {
                Assert.AreEqual(1, a.nextElements.Count, "the hook removed the deleted element's entry");
                Assert.AreSame(c, a.nextElements[0]);
            }
            finally
            {
                Undo.RevertAllDownToGroup(group);
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }
    }
}
