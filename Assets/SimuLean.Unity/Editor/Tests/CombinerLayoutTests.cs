using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Where a Combiner's waiting components stand (<see cref="UnityCombiner.PartOffset"/>): Behind, one column per input
    /// behind the main item (the default); On Work Point, piled on the work point — a shipyard's sub-blocks
    /// laid on the assembly bed instead of half off it; with an entry per input, piled on it. During the process the
    /// components travel onto the main item one after another (<see cref="UnityCombiner.MoveProgress"/>,
    /// <see cref="UnityCombiner.MovePose"/> (straight, eased)). On Load, each goes straight to
    /// its place in the finished load (<see cref="UnityCombiner.LayOnLoad"/>).
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
            Assert.AreEqual((2f + 16f) / 2 + step * 0.15f, first.z, 1e-4f, "behind the main item (+Z: the front is −Z)");
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
        public void ThePartsTravelOneAfterAnotherDuringTheProcess()
        {
            // 3 parts, process 0..10: each moves in its own third of the first 8.5
            Assert.AreEqual(0f, UnityCombiner.MoveProgress(0, 0, 10, 0, 3), 1e-5f, "the first starts with the process");
            Assert.AreEqual(1f, UnityCombiner.MoveProgress(8.5 / 3, 0, 10, 0, 3), 1e-5f, "and has arrived when the second starts");
            Assert.AreEqual(0f, UnityCombiner.MoveProgress(8.5 / 3, 0, 10, 1, 3), 1e-5f);
            Assert.AreEqual(0.5f, UnityCombiner.MoveProgress(8.5 * 2.5 / 3, 0, 10, 2, 3), 1e-5f, "the last half-way");
            Assert.AreEqual(1f, UnityCombiner.MoveProgress(8.5, 0, 10, 2, 3), 1e-5f, "all on the main item before the process ends");
            Assert.AreEqual(1f, UnityCombiner.MoveProgress(0, 0, 0, 0, 3), "no process time: they are there at once");
        }

        [Test]
        public void ATravelingPartGoesStraightEased()
        {
            var from = new Vector3(-2f, 0f, 1f);
            var to = new Vector3(0f, 1f, 0f);
            Assert.AreEqual(from, UnityCombiner.MovePose(from, to, 0f), "starts where it waited");
            Assert.That(Vector3.Distance(to, UnityCombiner.MovePose(from, to, 1f)), Is.LessThan(1e-5f), "ends on the main item");
            Assert.That(Vector3.Distance(Vector3.Lerp(from, to, 0.5f), UnityCombiner.MovePose(from, to, 0.5f)), Is.LessThan(1e-5f),
                        "half-way along the straight line, no arc");
            Assert.Less(Vector3.Distance(from, UnityCombiner.MovePose(from, to, 0.1f)), 0.1f * Vector3.Distance(from, to), "eased at the start");
        }

        [Test]
        public void PartsArePackedInRowsThenLayersEachWithItsSize()
        {
            // a 1.2 × 0.8 pallet: 4 cartons 0.4 × 0.3, then 2 totes 0.6 × 0.4
            var pallet = new Vector3(1.2f, 0.15f, 0.8f);
            var carton = new Vector3(0.4f, 0.25f, 0.3f);
            var tote = new Vector3(0.6f, 0.28f, 0.4f);
            var at = UnityCombiner.PackOnTop(pallet, new[] { carton, carton, carton, carton, tote, tote });
            Assert.AreEqual(at[0].z, at[1].z, 1e-5f, "three cartons in the first row");
            Assert.AreEqual(at[0].z, at[2].z, 1e-5f);
            Assert.AreEqual(0f, at[1].x, 1e-5f, "the row centred");
            Assert.Less(at[3].z, at[0].z, "the fourth starts the next row, in front");
            Assert.AreEqual(0f, at[4].y, 1e-5f, "the first tote beside it, on the pallet");
            Assert.AreEqual(0.28f, at[5].y, 1e-5f, "the second tote on a new layer (the top is full), above the tallest part");
            for (int i = 0; i < 5; i++)
                for (int j = i + 1; j < 5; j++)
                {
                    Vector3 si = i < 4 ? carton : tote, sj = j < 4 ? carton : tote;
                    bool apart = Mathf.Abs(at[i].x - at[j].x) >= (si.x + sj.x) * 0.5f - 1e-4f ||
                                 Mathf.Abs(at[i].z - at[j].z) >= (si.z + sj.z) * 0.5f - 1e-4f;
                    Assert.IsTrue(apart, $"parts {i} and {j} of the first layer do not overlap");
                }
        }

        [Test]
        public void ALargePartGetsALayerOfItsOwn()
        {
            var at = UnityCombiner.PackOnTop(new Vector3(1f, 0.2f, 1f), new[] { new Vector3(2f, 0.5f, 2f), new Vector3(2f, 0.5f, 2f) });
            Assert.AreEqual(Vector3.zero, at[0], "centred on the top");
            Assert.AreEqual(new Vector3(0f, 0.5f, 0f), at[1], "the next on it");
        }

        [Test]
        public void TheAssemblyIsAnimatedByDefault()
        {
            var go = new GameObject("Combiner");
            try
            {
                var c = go.AddComponent<UnityCombiner>();
                Assert.IsTrue(c.animateAssembly);
                Assert.IsEmpty(c.componentEntries, "no entries: the component layout");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void BehindIsTheDefault()
        {
            var go = new GameObject("Combiner");
            try { Assert.AreEqual(UnityCombiner.ComponentLayout.Behind, go.AddComponent<UnityCombiner>().componentLayout); }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void OnLoadIsAppendedSoSavedScenesKeepTheirLayout()
        {
            Assert.AreEqual(0, (int)UnityCombiner.ComponentLayout.Behind);
            Assert.AreEqual(1, (int)UnityCombiner.ComponentLayout.OnWorkPoint);
            Assert.AreEqual(2, (int)UnityCombiner.ComponentLayout.OnLoad);
        }

        private static GameObject Spawn(string path)
        {
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(asset, path);
            return Object.Instantiate(asset);
        }

        // n parts of the model at partPath laid On Load on the main item at mainPath: the first `gathered` in their places,
        // the rest still to come (null places); then all n attached as the assembly does
        private static (List<Vector3> early, List<Vector3> final, GameObject main) Lay(string mainPath, string partPath, int n, int gathered)
        {
            var main = Spawn(mainPath);
            var parts = new List<GameObject>();
            for (int k = 0; k < n; k++) parts.Add(Spawn(partPath));
            var sizes = parts.ConvertAll(p => ItemVisuals.SizeOf(p));
            var some = new List<GameObject>();
            for (int k = 0; k < n; k++) some.Add(k < gathered ? parts[k] : null);
            UnityCombiner.LayOnLoad(main, some, sizes, false);
            var early = parts.GetRange(0, gathered).ConvertAll(p => p.transform.position);
            UnityCombiner.LayOnLoad(main, parts, sizes, true);
            Assert.IsTrue(parts.TrueForAll(p => p.transform.parent == main.transform), "attached: children of the main item");
            return (early, parts.ConvertAll(p => p.transform.position), main);
        }

        [Test]
        public void OnLoadAPartGoesStraightToItsPlaceInTheFinishedLoad()
        {
            var (early, final, main) = Lay(LibraryPresets.Items + "EuroPallet.prefab",
                LibraryPresets.Models + "WarehouseLoadUnits/CardboardCarton_400x300x250.fbx", 12, 5);
            try
            {
                for (int k = 0; k < early.Count; k++)
                    Assert.Less(Vector3.Distance(early[k], final[k]), 1e-4f, $"carton {k}: the place it got while the load grew is its final one");
                // 2 layers of 3 × 2 on the 1200 × 800 pallet
                var layers = final.Select(p => Mathf.Round(p.y * 1000f)).Distinct().OrderBy(y => y).ToList();
                Assert.AreEqual(2, layers.Count, "two layers");
                foreach (float y in layers)
                {
                    var layer = final.Where(p => Mathf.Round(p.y * 1000f) == y).ToList();
                    Assert.AreEqual(6, layer.Count, "six cartons a layer");
                    Assert.AreEqual(2, layer.Select(p => Mathf.Round(p.x * 100f)).Distinct().Count(), "2 across the 800 side");
                    Assert.AreEqual(3, layer.Select(p => Mathf.Round(p.z * 100f)).Distinct().Count(), "3 along the 1200 side");
                }
            }
            finally { Object.DestroyImmediate(main); }
        }

        [Test]
        public void AWalledContainersModelGivesItsLoadFloor()
        {
            // the tote's LoadSocket is its inside floor (well below its top): its items get the label loadFloor, and the
            // Combiner's load height packs the parts inside it; a pallet's socket is its deck (its top): none
            var tote = ItemVisuals.ModelSize(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(LibraryPresets.Items + "Tote.prefab"));
            Assert.AreEqual(4, tote.Length, "a walled container: width, height, length and its load floor");
            Assert.AreEqual(0.014, tote[3], 2e-3, "the tote's floor");
            var pallet = ItemVisuals.ModelSize(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(LibraryPresets.Items + "EuroPallet.prefab"));
            Assert.AreEqual(3, pallet.Length, "a pallet: its load rests on its top");
        }

        [Test]
        public void APartAcrossTheToteIsTurnedToFitInside()
        {
            // a carton 400 wide × 300 long in a tote 400 × 600 (inside ≈ 356 × 556): as low either way, so it is turned
            // lengthwise rather than left through the walls
            var tote = Spawn(LibraryPresets.Items + "Tote.prefab");
            var carton = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                carton.transform.localScale = new Vector3(0.4f, 0.25f, 0.3f);
                UnityCombiner.LayOnLoad(tote, new List<GameObject> { carton }, new List<Vector3> { new Vector3(0.4f, 0.25f, 0.3f) }, true);
                Vector3 drawn = ItemVisuals.SizeOf(carton);
                Assert.AreEqual(0.3f, drawn.x, 1e-3f, "turned: 300 across the tote");
                Assert.AreEqual(0.4f, drawn.z, 1e-3f, "400 along it");
                Assert.AreEqual(0.404f, ItemVisuals.SizeOf(tote).x, 2e-3f, "nothing through the tote's walls");
            }
            finally { Object.DestroyImmediate(tote); }
        }

        [Test]
        public void OnLoadFourSmallCartonsMakeOneLayerInsideTheTote()
        {
            var (_, final, main) = Lay(LibraryPresets.Items + "Tote.prefab", LibraryPresets.Models + "SmallCarton/SmallCarton.fbx", 4, 1);
            try
            {
                var socket = ItemVisuals.LoadSocket(main);
                Assert.AreEqual(1, final.Select(p => Mathf.Round(p.y * 1000f)).Distinct().Count(), "one layer");
                Vector3 carton = ItemVisuals.SizeOf(main.transform.GetChild(main.transform.childCount - 1).gameObject);
                foreach (var p in final)
                {
                    Assert.LessOrEqual(Mathf.Abs(p.x) + carton.x / 2, 0.356f / 2 + 1e-3f, "inside the tote's 356 mm width");
                    Assert.LessOrEqual(Mathf.Abs(p.z) + carton.z / 2, 0.556f / 2 + 1e-3f, "inside its 556 mm length");
                }
                Assert.AreEqual(2, final.Select(p => Mathf.Round(p.x * 100f)).Distinct().Count(), "2 × 2");
                Assert.AreEqual(2, final.Select(p => Mathf.Round(p.z * 100f)).Distinct().Count());
                Assert.Less(Mathf.Abs(final.Min(p => p.y) - ItemVisuals.PivotHeight(main.transform.GetChild(main.transform.childCount - 1).gameObject) - socket.position.y),
                            1e-3f, "on the tote's floor");
            }
            finally { Object.DestroyImmediate(main); }
        }
    }
}
