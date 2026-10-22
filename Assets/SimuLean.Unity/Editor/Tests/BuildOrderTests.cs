using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// A scene builds its elements in hierarchy order, not in Unity's (unsorted) object-lookup order, so
    /// simultaneous events break ties the same way after a save and in a scene run and a headless run of the same model.
    /// </summary>
    public class BuildOrderTests
    {
        private const string Tag = "__cs3_";

        [Test]
        public void ElementsAreExtractedInHierarchyOrderWithOrWithoutARoot()
        {
            var root = new GameObject(Tag + "Root");
            try
            {
                // created in one order, arranged in the hierarchy in another: Group (Inner) ▸ First ▸ Last
                var last = Element("Last", root);
                var first = Element("First", root);
                var group = new GameObject(Tag + "Group");
                group.transform.SetParent(root.transform);
                var inner = Element("Inner", group);
                group.transform.SetSiblingIndex(0);
                first.transform.SetSiblingIndex(1);
                last.transform.SetSiblingIndex(2);

                string[] expected = { Tag + "Inner", Tag + "First", Tag + "Last" };
                CollectionAssert.AreEqual(expected, Names(SceneExtractor.Extract()), "scene (no root)");
                CollectionAssert.AreEqual(expected, Names(SceneExtractor.Extract(root)), "under the model root");

                inner.transform.SetParent(root.transform);   // moved to the end: now built last
                CollectionAssert.AreEqual(new[] { Tag + "First", Tag + "Last", Tag + "Inner" }, Names(SceneExtractor.Extract()));
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static GameObject Element(string name, GameObject parent)
        {
            var go = new GameObject(Tag + name);
            go.transform.SetParent(parent.transform);
            go.AddComponent<UnityDelay>();
            return go;
        }

        private static string[] Names(SceneExtraction x) =>
            x.Config.Elements.Select(e => e.Name).Where(n => n.StartsWith(Tag)).ToArray();
    }
}
