using NUnit.Framework;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Where a Combiner's waiting components stand (<see cref="UnityCombiner.PartOffset"/>): Behind, one column per input
    /// behind the main item (the default, as before); On Work Point, piled on the work point — a shipyard's sub-blocks
    /// laid on the assembly bed instead of half off it.
    /// </summary>
    public class CombinerLayoutTests
    {
        private static readonly Vector3 Main = new Vector3(2f, 1f, 2f);
        private static readonly Vector3 Part = new Vector3(12f, 1.6f, 16f);

        [Test]
        public void BehindPutsEachInputInAColumnBehindTheMainItem()
        {
            var first = UnityCombiner.PartOffset(UnityCombiner.ComponentLayout.Behind, 0, 2, 0, Main, Part, 0f, 0f);
            var second = UnityCombiner.PartOffset(UnityCombiner.ComponentLayout.Behind, 1, 2, 1, Main, Part, 0f, 0f);
            float step = 16f * 1.15f;
            Assert.AreEqual(-step / 2, first.x, 1e-4f, "columns centred across the work point");
            Assert.AreEqual(step / 2, second.x, 1e-4f);
            Assert.AreEqual(-((2f + 16f) / 2 + step * 0.15f), first.z, 1e-4f, "behind the main item");
            Assert.AreEqual(0f, first.y, 1e-4f);
            Assert.AreEqual(1.6f * 1.05f, second.y, 1e-4f, "the second of an input on the first");
        }

        [Test]
        public void OnWorkPointPilesThePartsOnTheMainItem()
        {
            var o = UnityCombiner.PartOffset(UnityCombiner.ComponentLayout.OnWorkPoint, 1, 2, 0, Main, Part, 0f, 2.6f);
            Assert.AreEqual(new Vector3(0f, 2.6f, 0f), o, "centred on the work point, on what is already there");
        }

        [Test]
        public void BehindIsTheDefault()
        {
            var go = new GameObject("Combiner");
            try { Assert.AreEqual(UnityCombiner.ComponentLayout.Behind, go.AddComponent<UnityCombiner>().componentLayout); }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
