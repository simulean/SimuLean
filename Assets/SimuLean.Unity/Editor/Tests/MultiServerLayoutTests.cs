using NUnit.Framework;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The Multi-Server's slots: one centred row up to <see cref="UnityMultiServer.MaxSlotsInARow"/>, a
    /// square-ish grid above it (a heat treatment of 253 slots stretched its items 100 m across the line).
    /// </summary>
    public class MultiServerLayoutTests
    {
        [Test]
        public void FewSlotsStandInARow()
        {
            Assert.AreEqual(new Vector2(-1f, 0f), UnityMultiServer.SlotCell(0, 3));
            Assert.AreEqual(new Vector2(1f, 0f), UnityMultiServer.SlotCell(2, 3));
            Assert.AreEqual(new Vector2(3.5f, 0f), UnityMultiServer.SlotCell(7, 8));
        }

        [Test]
        public void ManySlotsStandInAGrid()
        {
            float maxX = 0f, maxY = 0f;
            var seen = new System.Collections.Generic.HashSet<Vector2>();
            for (int i = 0; i < 253; i++)
            {
                Vector2 c = UnityMultiServer.SlotCell(i, 253);
                Assert.IsTrue(seen.Add(c), $"slot {i} shares its cell");
                maxX = Mathf.Max(maxX, Mathf.Abs(c.x)); maxY = Mathf.Max(maxY, Mathf.Abs(c.y));
            }
            Assert.AreEqual(7.5f, maxX, "16 columns");
            Assert.AreEqual(7.5f, maxY, "16 rows");
            Assert.Greater(UnityMultiServer.SlotCell(0, 253).y, UnityMultiServer.SlotCell(16, 253).y, "the first row towards +Z");
        }
    }
}
