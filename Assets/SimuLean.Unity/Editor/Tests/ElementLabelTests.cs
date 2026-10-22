using NUnit.Framework;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>The Element Label view: a zone label, or the content and name of an element.</summary>
    public class ElementLabelTests
    {
        [Test]
        public void WithoutAnElementItIsAZoneLabel()
        {
            var go = new GameObject("Zone");
            try
            {
                var label = go.AddComponent<UnityElementLabel>();
                label.text = "Waiting room";
                Assert.IsNull(label.Source, "no element on or above it");
                Assert.AreEqual("Waiting room", label.CurrentText);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void OnAnElementItFillsTheCountAndTheName()
        {
            var go = new GameObject("Waiting list");
            try
            {
                var queue = go.AddComponent<UnityQueue>();
                var child = new GameObject("Label");
                child.transform.SetParent(go.transform, false);
                var label = child.AddComponent<UnityElementLabel>();
                label.text = "{name}: {n} waiting";
                Assert.AreSame(queue, label.Source, "the element above it");
                Assert.AreEqual("Waiting list: 0 waiting", label.CurrentText, "no run yet: nothing held");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ItIsRegisteredAsAVisualizationTool()
        {
            var attr = (SimuLeanToolAttribute)System.Attribute.GetCustomAttribute(typeof(UnityElementLabel), typeof(SimuLeanToolAttribute));
            Assert.IsNotNull(attr);
            Assert.AreEqual(SimuLeanToolCategories.Visualization, attr.Category);
        }
    }
}
