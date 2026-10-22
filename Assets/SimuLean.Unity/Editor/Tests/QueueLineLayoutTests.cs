using NUnit.Framework;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>The queue's <see cref="QueueLayoutMode.Line"/> layout (waiting lines) and its pad switch.</summary>
    public class QueueLineLayoutTests
    {
        [Test]
        public void AStraightLineRunsBackFromTheHead()
        {
            Assert.AreEqual(Vector2.zero, ItemLayout.LineOffset(0, 0.7f, 0f, out bool r0), "the head stands at the item position");
            Assert.IsFalse(r0);
            Vector2 fourth = ItemLayout.LineOffset(3, 0.7f, 0f, out bool r3);
            Assert.AreEqual(0f, fourth.x, 1e-5f);
            Assert.AreEqual(-2.1f, fourth.y, 1e-5f, "three steps behind the head");
            Assert.IsFalse(r3, "everyone faces the head");
        }

        [Test]
        public void ALongLineFoldsBackBesideItself()
        {
            // runs of 1.4 m with items 0.7 m apart: 3 items per run
            Vector2 lastOfFirst = ItemLayout.LineOffset(2, 0.7f, 1.4f, out bool r2);
            Assert.AreEqual(-1.4f, lastOfFirst.y, 1e-5f);
            Assert.IsFalse(r2);
            Vector2 firstOfSecond = ItemLayout.LineOffset(3, 0.7f, 1.4f, out bool r3);
            Assert.AreEqual(1.05f, firstOfSecond.x, 1e-5f, "the second run beside the first, 1.5 steps to the right");
            Assert.AreEqual(-1.4f, firstOfSecond.y, 1e-5f, "it starts at the back, where the first run ended");
            Assert.IsTrue(r3, "and walks the other way");
            Vector2 lastOfSecond = ItemLayout.LineOffset(5, 0.7f, 1.4f, out _);
            Assert.AreEqual(0f, lastOfSecond.y, 1e-5f, "back level with the head");
            Vector2 firstOfThird = ItemLayout.LineOffset(6, 0.7f, 1.4f, out bool r6);
            Assert.AreEqual(2.1f, firstOfThird.x, 1e-5f);
            Assert.AreEqual(0f, firstOfThird.y, 1e-5f);
            Assert.IsFalse(r6);
        }

        [Test]
        public void ThePadCanBeHidden()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var q = go.AddComponent<UnityQueue>();
                var r = go.GetComponent<Renderer>();
                Assert.IsTrue(r.enabled);
                q.showFootprint = false;
                q.ApplyFootprintVisibility();
                Assert.IsFalse(r.enabled, "the pad is hidden");
                q.showFootprint = true;
                q.ApplyFootprintVisibility();
                Assert.IsTrue(r.enabled, "and shown again");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
