using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SimuLean.Serialization;
using SimuLean.Storage;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Physical dimensions of a storage: the room of a rack's position follows its layout (under the next
    /// beam, the top level open; floor cells open above), the scene's options and each lane's room reach the engine.
    /// </summary>
    public class StoragePhysicalDimensionsTests
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

        private UnityStorageRack Rack(StorageRackStyle style)
        {
            var go = new GameObject("Rack");
            SceneManager.MoveGameObjectToScene(go, scene);
            created.Add(go);
            var r = go.AddComponent<UnityStorageRack>();
            r.style = style;
            r.bays = 1; r.slotsPerBay = 2; r.levels = 3; r.depth = 1;
            r.slotWidth = 1.0f; r.levelHeight = 1.5f; r.beamHeight = 0.12f; r.slotDepth = 1.2f; r.groundLevel = true;
            r.Rebuild();
            return r;
        }

        [Test]
        public void ARackPositionHasTheRoomUnderTheNextBeam()
        {
            var rack = Rack(StorageRackStyle.ConventionalRack);
            Assert.Less(Vector3.Distance(new Vector3(1.0f, 1.38f, 1.2f), rack.SlotRoom(0)), 1e-5f, "ground level: up to the next beam's underside");
            Assert.AreEqual(1.38f, rack.SlotRoom(1).y, 1e-5f);
            Assert.AreEqual(0f, rack.SlotRoom(2).y, "the top level is open above");
            foreach (var lane in rack.ResolveLanes())
            {
                int level = int.Parse(lane.LaneId.Substring(lane.LaneId.LastIndexOf('L') + 1));
                Assert.AreEqual(rack.SlotRoom(level), lane.SlotSize, $"{lane.LaneId}: its room from the layout");
            }
            var floor = Rack(StorageRackStyle.FloorStorage);
            Assert.AreEqual(0f, floor.SlotRoom(0).y, "a floor cell is open above");
            Assert.AreEqual(floor.bayWidth, floor.SlotRoom(0).x);
        }

        [Test]
        public void TheOptionsAndEachLanesRoomReachTheEngine()
        {
            var rack = Rack(StorageRackStyle.ConventionalRack);
            Assert.IsTrue(rack.putAwayByTransport, "put-away by the Transport team by default (when there is one)");
            Assert.IsFalse(rack.physicalDimensions, "physical dimensions are an option");
            Assert.IsFalse(rack.releaseAfterDwell, "a store by default; pass-through is an option");
            rack.physicalDimensions = true;
            rack.heightLabel = "h";
            rack.inboundCapacity = 2;
            rack.releaseAfterDwell = true;
            rack.minimumDwellTime = 12f;

            var cfg = SceneExtractor.Extract(rack.gameObject).Config;
            var el = cfg.Elements.Single(e => e.Name == "Rack");
            var built = (StorageSystem)TransportStorageBuilders.BuildStorageSystem(el, new SimClock(1), null);
            Assert.IsTrue(built.PhysicalDimensions && built.PutAwayByTransport && built.InboundCapacity == 2);
            Assert.IsTrue(built.ReleaseAfterDwell && built.MinimumDwellTime == 12.0, "the pass-through option");
            Assert.AreEqual("h", built.HeightLabel);
            Assert.AreEqual("length", built.LengthLabel);
            var ground = built.Lanes.First(l => l.LaneId.EndsWith("L0"));
            var top = built.Lanes.First(l => l.LaneId.EndsWith("L2"));
            Assert.AreEqual(1.38, ground.SlotHeight, 1e-5);
            Assert.AreEqual(0.0, top.SlotHeight);
            Assert.AreEqual(1.0, ground.SlotWidth, 1e-6);
            Assert.AreEqual(1.2, ground.SlotLength, 1e-6);

            var tall = new Item(0);
            tall.SetLabelValue("h", 1.8);
            Assert.IsFalse(built.Fits(ground, tall));
            Assert.IsTrue(built.Fits(top, tall));
        }
    }
}
