using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Procedural storage visuals (<see cref="PalletRackMesh"/>, <see cref="CartonFlowRackMesh"/>, <see cref="FloorMarkingMesh"/>
    /// through <see cref="UnityStorageRack"/>): the lanes and anchors keep the layout formula, pallets rest on the beam
    /// tops, carton-flow boxes descend towards the pick face along the slope, the reference racks stay in the triangle
    /// budget, and every chunk has its LODGroup.
    /// </summary>
    public class StorageRackVisualsTests
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

        private UnityStorageRack NewRack(StorageRackStyle style, System.Action<UnityStorageRack> configure = null)
        {
            var go = new GameObject("rack_" + style);
            SceneManager.MoveGameObjectToScene(go, scene);
            created.Add(go);
            var rack = go.AddComponent<UnityStorageRack>();
            rack.style = style;
            configure?.Invoke(rack);
            rack.Rebuild();
            return rack;
        }

        private GameObject Track(GameObject go)
        {
            SceneManager.MoveGameObjectToScene(go, scene);
            created.Add(go);
            return go;
        }

        private static Dictionary<string, Transform> Anchors(UnityStorageRack rack)
        {
            var map = new Dictionary<string, Transform>();
            foreach (var lane in rack.lanes) map[lane.laneId] = lane.anchor;
            return map;
        }

        [Test]
        public void AnchorsFollowTheLayoutFormula()
        {
            var rack = NewRack(StorageRackStyle.ConventionalRack, r => { r.bays = 4; r.slotsPerBay = 3; r.levels = 5; });
            Assert.AreEqual(4 * 3 * 5, rack.LaneCount);
            Assert.AreEqual(rack.LaneCount, rack.lanes.Count, "one lane per slot");
            var anchors = Anchors(rack);
            for (int b = 0; b < 4; b++)
                for (int s = 0; s < 3; s++)
                    for (int l = 0; l < 5; l++)
                    {
                        Transform a = anchors[$"B{b}_S{s}_L{l}"];
                        var expected = new Vector3(b * rack.bayWidth + (s + 0.5f) * rack.slotWidth, l * rack.levelHeight, 0f);
                        Assert.Less((a.localPosition - expected).magnitude, 1e-5f, a.name);
                    }

            var raised = NewRack(StorageRackStyle.ConventionalRack, r => { r.groundLevel = false; r.firstLevelHeight = 0.25f; });
            foreach (var lane in raised.lanes)
            {
                int l = lane.anchor.GetComponent<UnityStorageSlotMarker>().level;
                Assert.AreEqual(0.25f + l * raised.levelHeight, lane.anchor.localPosition.y, 1e-5f, "raised first level");
            }

            var carton = NewRack(StorageRackStyle.CartonFlowRack, r => { r.bays = 2; r.slotsPerBay = 4; r.levels = 4; r.depth = 4; r.slotWidth = 0.45f; r.bayWidth = 1.88f; r.firstLevelHeight = 0.35f; r.levelHeight = 0.45f; });
            Assert.AreEqual(2 * 4 * 4, carton.lanes.Count);
            foreach (var lane in carton.lanes)
            {
                var m = lane.anchor.GetComponent<UnityStorageSlotMarker>();
                var expected = new Vector3(m.bay * 1.88f + (m.slot + 0.5f) * 0.45f, 0.35f + m.level * 0.45f, 0f);
                Assert.Less((lane.anchor.localPosition - expected).magnitude, 1e-5f, lane.laneId);
                Assert.AreEqual(StorageLaneKind.FlowRack, lane.kind, "carton flow lanes are FIFO, one SKU");
                Assert.AreEqual(4, lane.capacity);
            }

            var floor = NewRack(StorageRackStyle.FloorStorage, r => { r.bays = 3; r.depth = 2; r.bayWidth = 1.2f; r.slotDepth = 1.2f; });
            Assert.AreEqual(6, floor.lanes.Count);
            foreach (var lane in floor.lanes)
            {
                var m = lane.anchor.GetComponent<UnityStorageSlotMarker>();
                Assert.Less((lane.anchor.localPosition - new Vector3((m.bay + 0.5f) * 1.2f, 0f, (m.slot + 0.5f) * 1.2f)).magnitude, 1e-5f);
            }
        }

        [Test]
        public void PalletsRestOnTheBeamTop()
        {
            var rack = NewRack(StorageRackStyle.ConventionalRack, r => { r.bays = 2; r.levels = 4; r.deckType = RackDeckType.WireMesh; });
            Mesh lod0 = rack.transform.Find("RackVisual").GetComponentInChildren<LODGroup>().GetLODs()[0].renderers[0].GetComponent<MeshFilter>().sharedMesh;

            // the beam sub-mesh tops out exactly at every load surface (beam top = anchor Y), never above it
            var verts = lod0.vertices;
            var beamIdx = lod0.GetTriangles(StorageRackSlots.Beam);
            Assert.Greater(beamIdx.Length, 0, "beams generated");
            var tops = new HashSet<int>();
            foreach (int i in beamIdx)
            {
                float y = verts[i].y;
                Assert.Greater(y, 0.5f, "level 0 is on the floor: no beam there");
                for (int l = 1; l < rack.levels; l++)
                {
                    float top = rack.LevelSurfaceY(l);
                    Assert.IsFalse(y > top + 1e-4f && y < top + 0.05f, $"beam vertex {y} above the load surface {top}");
                    if (Mathf.Abs(y - top) < 1e-4f) tops.Add(l);
                }
            }
            Assert.AreEqual(rack.levels - 1, tops.Count, "a beam top at every raised level");

            // a pallet-sized cube (pivot at its centre) rests on the beam top, centred on its slot
            for (int l = 0; l < rack.levels; l++)
            {
                var cube = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
                cube.transform.localScale = new Vector3(0.8f, 1.0f, 1.2f);
                rack.PlaceItemView(cube, 1, 2, l, 0);
                Bounds bb = cube.GetComponent<Renderer>().bounds;
                Assert.AreEqual(rack.LevelSurfaceY(l), bb.min.y, 1e-3f, $"level {l}: bottom on the support");
                Assert.AreEqual(1 * rack.bayWidth + 2.5f * rack.slotWidth, bb.center.x, 1e-3f, "centred on the slot");
                Assert.AreEqual(0.5f * rack.slotDepth, bb.center.z, 1e-3f, "centred in the lane depth");
            }

            // a view without renderers: pivot at the beam top + itemVerticalOffset
            var empty = Track(new GameObject("unit"));
            rack.PlaceItemView(empty, 0, 0, 2, 0);
            Assert.AreEqual(rack.LevelSurfaceY(2) + rack.itemVerticalOffset, empty.transform.position.y, 1e-4f, "beam top + offset");
        }

        [Test]
        public void CartonFlowItemsDescendTowardsThePickFace()
        {
            var rack = NewRack(StorageRackStyle.CartonFlowRack, r => { r.bays = 1; r.slotsPerBay = 4; r.levels = 3; r.depth = 4; r.slotWidth = 0.45f; r.bayWidth = 1.88f; r.slotDepth = 0.6f; r.flowSlopeDegrees = 6f; });
            Vector3 prev = rack.GetLoadSurfacePoint(0, 1, 1, 0);
            Assert.Greater(prev.z, 0f, "the first box stands behind the pick face (z = 0)");
            float s0 = CartonFlowRackSpec.AlongSlope(CartonFlowRackSpec.DefaultFrontStop, rack.slotDepth, 0);
            Assert.AreEqual(rack.LevelSurfaceY(1) + s0 * Mathf.Sin(6f * Mathf.Deg2Rad), prev.y, 1e-4f, "the first box is on the shelf, just behind the front stop");
            for (int k = 1; k < rack.depth; k++)
            {
                Vector3 p = rack.GetLoadSurfacePoint(0, 1, 1, k);
                Assert.Greater(p.y, prev.y, "boxes deeper in the lane sit higher (the shelf falls towards the pick face)");
                Assert.Greater(p.z, prev.z);
                float slope = Mathf.Atan2(p.y - prev.y, p.z - prev.z) * Mathf.Rad2Deg;
                Assert.AreEqual(6f, slope, 1e-2f, "boxes follow the shelf slope");
                Assert.AreEqual(rack.slotDepth, (p - prev).magnitude, 1e-4f, "box pitch along the slope");
                prev = p;
            }

            var box = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            box.transform.localScale = new Vector3(0.4f, 0.3f, 0.6f);
            rack.PlaceItemView(box, 0, 1, 1, 2);
            Assert.AreEqual(6f, Vector3.Angle(box.transform.up, Vector3.up), 1e-2f, "the box is tilted with the shelf");
            Vector3 support = rack.GetLoadSurfacePoint(0, 1, 1, 2);
            float h = Vector3.Dot(box.transform.position - support, box.transform.up);
            Assert.AreEqual(0.15f, h, 1e-3f, "the box bottom lies on the shelf");

            // the LOD0 wheel beds lie in the shelf plane: their top vertices are on the surface through the anchor
            Mesh lod0 = rack.transform.Find("RackVisual").GetComponentInChildren<LODGroup>().GetLODs()[0].renderers[0].GetComponent<MeshFilter>().sharedMesh;
            var verts = lod0.vertices;
            var normals = lod0.normals;
            Vector3 n = CartonFlowRackSpec.NormalFor(6f), anchor = new Vector3(0f, rack.LevelSurfaceY(1), 0f);
            int onSurface = 0;
            foreach (int i in lod0.GetTriangles(StorageRackSlots.Track))
                if (Vector3.Dot(normals[i], n) > 0.999f && Mathf.Abs(Vector3.Dot(verts[i] - anchor, n)) < 1e-4f) onSurface++;
            Assert.Greater(onSurface, 0, "wheel-bed tops on the level-1 shelf surface");
        }

        [Test]
        public void ReferenceRacksStayInTheTriangleBudget()
        {
            // a 10-bay × 5-level double row of selective racking with every option on (one row carries the spacers)
            var front = new PalletRackSpec { bays = 10, levels = 5, deck = RackDeckType.WireMesh, rowSpacerLength = 0.3f };
            var back = front.Clone();
            back.rowSpacerLength = 0f;
            int[] row = new int[3];
            for (int lod = 0; lod < 3; lod++)
                row[lod] = PalletRackMesh.CountTriangles(front, lod) + PalletRackMesh.CountTriangles(back, lod);
            TestContext.WriteLine($"Pallet double row 10 × 5: LOD0 {row[0]}, LOD1 {row[1]}, LOD2 {row[2]} triangles");
            Assert.LessOrEqual(row[0], 60000, "LOD0 budget");
            Assert.Less(row[1], row[0] / 2, "LOD1 much lighter");
            Assert.Less(row[2], row[1], "LOD2 lighter still");

            var carton = new CartonFlowRackSpec { bays = 4, lanesPerBay = 4, levels = 5, depth = 4 };
            int[] c = new int[3];
            for (int lod = 0; lod < 3; lod++) c[lod] = CartonFlowRackMesh.CountTriangles(carton, lod);
            TestContext.WriteLine($"Carton flow 4 bays × 4 lanes × 5 levels: LOD0 {c[0]}, LOD1 {c[1]}, LOD2 {c[2]} triangles");
            Assert.LessOrEqual(c[0], 60000);
            Assert.Less(c[1], c[0] / 2);
            Assert.Less(c[2], c[1]);
        }

        [Test]
        public void EveryChunkHasALodGroup()
        {
            var rack = NewRack(StorageRackStyle.ConventionalRack, r => { r.bays = 10; r.baysPerChunk = 4; });
            Transform visual = rack.transform.Find("RackVisual");
            Assert.IsNotNull(visual, "procedural visuals generated");
            Assert.AreEqual(3, visual.childCount, "10 bays in chunks of 4");
            foreach (Transform chunk in visual)
            {
                var group = chunk.GetComponent<LODGroup>();
                Assert.IsNotNull(group, chunk.name);
                LOD[] lods = group.GetLODs();
                Assert.AreEqual(3, lods.Length);
                foreach (var lod in lods)
                {
                    Assert.AreEqual(1, lod.renderers.Length);
                    Assert.AreEqual(StorageRackSlots.Count, lod.renderers[0].sharedMaterials.Length);
                    foreach (var m in lod.renderers[0].sharedMaterials) Assert.IsNotNull(m);
                    Assert.IsTrue(lod.renderers[0].sharedMaterials[0].enableInstancing, "GPU instancing on");
                }
            }

            int children = rack.transform.childCount;
            rack.Rebuild();
            Assert.AreEqual(children, rack.transform.childCount, "a rebuild replaces its own output");

            foreach (var style in new[] { StorageRackStyle.CartonFlowRack, StorageRackStyle.FloorStorage, StorageRackStyle.DriveInRack, StorageRackStyle.PushBackRack })
            {
                var other = NewRack(style);
                Transform v = other.transform.Find("RackVisual");
                Assert.IsNotNull(v, style.ToString());
                Assert.IsNotNull(v.GetComponentInChildren<LODGroup>(), style + " LODGroup");
                Assert.IsNull(other.GetComponentInChildren<BoxCollider>(), style + ": no primitive plates");
            }
        }
    }
}
