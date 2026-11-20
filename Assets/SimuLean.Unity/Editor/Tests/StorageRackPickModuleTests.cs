using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Pick module of a conventional rack: carton-flow shelves in the bottom zone of the same frames (no
    /// frames of their own), the storage beams raised above it, anchors <c>Pick_B{b}_S{s}_L{l}</c> for other views, the
    /// lanes only on the storage levels; units drawn as a block on a position; triangle budget.
    /// </summary>
    public class StorageRackPickModuleTests
    {
        private Scene scene;
        private readonly List<GameObject> created = new List<GameObject>();

        [SetUp]
        public void SetUp() => scene = EditorSceneManager.NewPreviewScene();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
            created.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
        }

        private UnityStorageRack Rack(System.Action<UnityStorageRack> configure)
        {
            var go = new GameObject("rack");
            SceneManager.MoveGameObjectToScene(go, scene);
            created.Add(go);
            var r = go.AddComponent<UnityStorageRack>();
            r.style = StorageRackStyle.ConventionalRack;
            r.bays = 3; r.slotsPerBay = 2; r.slotWidth = 1.25f; r.bayWidth = 2.6f; r.levels = 3; r.levelHeight = 1.2f;
            r.slotDepth = 1.35f; r.frameDepth = 1.3f; r.firstLevelHeight = 0.3f;
            configure?.Invoke(r);
            r.Rebuild();
            return r;
        }

        private static void CartonModule(UnityStorageRack r)
        {
            r.pickModule = RackPickModule.CartonFlow;
            r.pickLevels = 2; r.pickLanesPerBay = 4; r.pickDepth = 3; r.pickPitch = 0.42f;
            r.pickFirstShelf = 0.35f; r.pickLevelHeight = 0.7f;
        }

        [Test]
        public void AnchorsAndLanes()
        {
            var r = Rack(CartonModule);
            Assert.AreEqual(3 * 2 * 3, r.lanes.Count, "lanes only on the storage levels");
            for (int b = 0; b < 3; b++)
                for (int s = 0; s < 4; s++)
                    for (int l = 0; l < 2; l++)
                    {
                        var a = r.PickAnchor(b, s, l);
                        Assert.IsNotNull(a, $"Pick_B{b}_S{s}_L{l}");
                        Vector3 expected = new Vector3(b * 2.6f + (s + 0.5f) * (2 * 1.25f / 4f), 0.35f + l * 0.7f, 0f);
                        Assert.Less((a.localPosition - expected).magnitude, 1e-4f, a.name);
                        Assert.Less(Quaternion.Angle(a.localRotation, CartonFlowRackSpec.RotationFor(r.pickSlopeDegrees)), 0.01f, "tilted with the shelf");
                    }
            Assert.IsNull(r.PickAnchor(0, 4, 0), "no more lanes than configured");
            foreach (var lane in r.lanes) Assert.IsFalse(lane.anchor.name.StartsWith("Pick_"), "pick anchors are not storage lanes");
        }

        [Test]
        public void StorageBeamsStartAboveThePickZone()
        {
            var r = Rack(CartonModule);
            var c = r.ToPickCartonSpec();
            float topShelfRear = c.SurfaceY(1, c.HorizontalDepth);
            Assert.GreaterOrEqual(r.LevelSurfaceY(0), topShelfRear + r.pickClearance, "first storage beam above the shelves");
            Assert.That(r.LevelSurfaceY(0), Is.InRange(1.7f, 2.3f), "first pallet beam at about 2 m");
            Assert.AreEqual(r.LevelSurfaceY(0), r.lanes.Find(x => x.anchor.name == "B0_S0_L0").anchor.localPosition.y, 1e-5f);
            Assert.AreEqual(c.LaneCenterX(1, 2), r.PickLocalPosition(1, 2, 0).x, 1e-5f, "shelf lanes = the pick anchors");
            Assert.AreEqual(1.35f, c.HorizontalDepth, 0.02f, "the shelves span the frame depth");

            var pallet = Rack(x => x.pickModule = RackPickModule.PalletFloor);
            Assert.IsNotNull(pallet.PickAnchor(2, 1, 1), "floor position + reserve beam anchors");
            Assert.AreEqual(0f, pallet.PickAnchor(0, 0, 0).localPosition.y, 1e-5f);
            Assert.GreaterOrEqual(pallet.LevelSurfaceY(0), 2 * pallet.pickLevelHeight, "storage above the reserve beam");
        }

        [Test]
        public void NoDuplicateFramesAndTriangleBudget()
        {
            var r = Rack(CartonModule);
            var pallet = r.ToPalletSpec();
            Assert.IsNotNull(pallet.cartonModule);
            Assert.IsFalse(pallet.cartonModule.frames, "the shelves hang in the rack's frames");
            for (int lod = 0; lod < 3; lod++)
            {
                int combined = PalletRackMesh.CountTriangles(pallet, lod);
                var bare = pallet.Clone(); bare.cartonModule = null;
                int rackOnly = PalletRackMesh.CountTriangles(bare, lod);
                var shelves = pallet.cartonModule.Clone();
                int shelvesOnly = CartonFlowRackMesh.CountTriangles(shelves, lod);
                var withFrames = shelves.Clone(); withFrames.frames = true;
                Assert.AreEqual(rackOnly + shelvesOnly, combined, $"LOD{lod}: rack + shelves, nothing else");
                Assert.Less(shelvesOnly, CartonFlowRackMesh.CountTriangles(withFrames, lod), $"LOD{lod}: no second set of frames");
            }
            // budget: a bay with a two-shelf pick module stays under 12 k triangles at LOD0 and 1.5 k at LOD2
            int t0 = PalletRackMesh.CountTriangles(pallet, 0), t1 = PalletRackMesh.CountTriangles(pallet, 1), t2 = PalletRackMesh.CountTriangles(pallet, 2);
            TestContext.WriteLine($"3 bays with a 2-shelf carton pick module: LOD0 {t0}, LOD1 {t1}, LOD2 {t2} triangles");
            Assert.Less(t0 / 3, 12000);
            Assert.Less(t2 / 3, 1500);
            Assert.Less(t1, t0 / 2, "LOD1 much lighter");
            Assert.Less(t2, t1, "LOD2 lighter still");
        }

        [Test]
        public void UnitsAsABlockOnAPosition()
        {
            var r = Rack(x =>
            {
                x.unitsPerLane = 15; x.unitGrid = new Vector2Int(2, 3); x.unitPitch = new Vector3(0.62f, 0.21f, 0.42f);
                x.laneAccess = RackAccessOverride.Direct; x.itemYaw = 90f;
            });
            Assert.AreEqual(15, r.lanes[0].capacity);
            Assert.AreEqual(SimuLean.Storage.AccessDiscipline.Direct, r.lanes[0].customAccess, "access override: any case on the position");
            Assert.AreEqual(15 * r.LaneCount, r.PositionCount);
            var anchor = r.lanes[0].anchor;
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(box, scene);
            created.Add(box);
            box.transform.localScale = new Vector3(0.4f, 0.2f, 0.6f);
            r.PlaceItemView(box, 0, 0, 0, 0);   // reference placement (face)
            var m = typeof(UnityStorageRack).GetMethod("PlaceStoredItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            // 7 units stored, the newest at index 0: the oldest (index 6) is the first of the block (bottom, front-left)
            m.Invoke(r, new object[] { box, null, anchor, 6, 7 });
            Vector3 local = r.transform.InverseTransformPoint(box.transform.position) - anchor.localPosition;
            Assert.AreEqual(-0.31f, local.x, 1e-3f);
            Assert.AreEqual(0.1f, local.y, 1e-3f, "resting on the beam");
            Assert.AreEqual(r.slotDepth * 0.5f - 0.42f, local.z, 1e-3f);
            m.Invoke(r, new object[] { box, null, anchor, 0, 7 });   // the newest (7th): second layer
            local = r.transform.InverseTransformPoint(box.transform.position) - anchor.localPosition;
            Assert.AreEqual(0.21f + 0.1f, local.y, 1e-3f, "on the second layer");
            Assert.Less(Quaternion.Angle(box.transform.rotation, r.transform.rotation * Quaternion.Euler(0f, 90f, 0f)), 0.01f, "aligned with the rack, turned by Item Yaw");
        }
    }
}
