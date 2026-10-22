using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SimuLean.Serialization;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Items laid out by their bounds whatever the pivot, a station's output look,
    /// per-member spawn points, and work points extracted and used headless.
    /// </summary>
    public class PlacementTests
    {
        private static GameObject Box(string name, Vector3 childOffset, Vector3 size)
        {
            var root = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.hideFlags = HideFlags.HideAndDontSave;
            cube.transform.SetParent(root.transform, false);
            cube.transform.localPosition = childOffset;
            cube.transform.localScale = size;
            return root;
        }

        [Test]
        public void ItemsRestOnTheirBottomWhateverThePivot()
        {
            var centred = Box("centred", Vector3.zero, new Vector3(1, 2, 1));
            var based = Box("based", new Vector3(0, 1, 0), new Vector3(1, 2, 1));   // a library model: pivot at the base
            try
            {
                Assert.AreEqual(1f, ItemVisuals.PivotHeight(centred), 1e-4, "centred pivot: half the height (the former layout)");
                Assert.AreEqual(0f, ItemVisuals.PivotHeight(based), 1e-4, "pivot at the base: no lift (it floated by 1 before)");
                Assert.AreEqual(2f, ItemVisuals.TopAbovePivot(based), 1e-4, "its top is its height above the pivot");
                // placed with the pivot PivotHeight above a point, the bottom is on the point
                foreach (var go in new[] { centred, based })
                {
                    go.transform.position = new Vector3(3, 5 + ItemVisuals.PivotHeight(go), 0);
                    var r = go.GetComponentInChildren<Renderer>();
                    Assert.AreEqual(5f, r.bounds.min.y, 1e-4, $"{go.name}: bottom on the point");
                }
            }
            finally { Object.DestroyImmediate(centred); Object.DestroyImmediate(based); }
        }

        [Test]
        public void StationOutputLookReplacesTheItemVisual()
        {
            var plate = Box("plate", Vector3.zero, new Vector3(1, 0.1f, 1));
            var panelPrefab = Box("panel", Vector3.zero, new Vector3(2, 0.2f, 1));
            GameObject look = null;
            try
            {
                plate.transform.position = new Vector3(1, 2, 3);
                var item = new Item(0) { vItem = plate };
                look = ItemVisuals.ReplaceLook(item, panelPrefab, "panel1");
                Assert.AreSame(look, item.vItem, "the new look is the item's visual");
                Assert.AreEqual(new Vector3(1, 2, 3), look.transform.position, "where the old one was");
                Assert.AreEqual("panel1", look.name);
            }
            finally
            {
                if (plate != null && (object)plate != look) Object.DestroyImmediate(plate);
                if (look != null) Object.DestroyImmediate(look);
                Object.DestroyImmediate(panelPrefab);
            }
        }

        [Test]
        public void MembersSpawnAtTheirOwnPointsAndWorkAtTheStationsWorkNodes()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            try
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                T Make<T>(string n, Vector3 at = default) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    go.transform.position = at;
                    return go.AddComponent<T>();
                }

                // two robots of one team, each in its own cell
                var cellA = Make<BoxCollider>("CellA", new Vector3(-5, 0, 0)).transform;
                var cellB = Make<BoxCollider>("CellB", new Vector3(5, 0, 0)).transform;
                var robots = Make<UnityResourceTeam>("Robots");
                robots.members = new List<ResourceMemberSpec>
                {
                    new ResourceMemberSpec { name = "R1", spawnPoint = cellA }, new ResourceMemberSpec { name = "R2", spawnPoint = cellB },
                    new ResourceMemberSpec { name = "R3" },
                };
                Assert.AreSame(cellA, robots.SpawnPointOf(0));
                Assert.AreSame(cellB, robots.SpawnPointOf(1));
                Assert.AreSame(robots.transform, robots.SpawnPointOf(2), "no spawn point: the team's transform (as before)");

                // a station whose welders stand beside the block, not on it
                var net = Make<UnityTransportNetwork>("Network");
                var home = Make<UnityTransportNode>("home", new Vector3(0, 0, -10));
                var st = Make<UnityTransportNode>("st", new Vector3(0, 0, 0));
                var w1 = Make<UnityTransportNode>("w1", new Vector3(-2, 0, 0));
                var w2 = Make<UnityTransportNode>("w2", new Vector3(2, 0, 0));
                net.nodes = new List<UnityTransportNode> { home, st, w1, w2 };
                net.edges = new List<UnityTransportNetwork.EdgeDefinition>
                {
                    new UnityTransportNetwork.EdgeDefinition { nodeA = home, nodeB = st }, new UnityTransportNetwork.EdgeDefinition { nodeA = st, nodeB = w1 },
                    new UnityTransportNetwork.EdgeDefinition { nodeA = st, nodeB = w2 },
                };
                var welders = Make<UnityResourceTeam>("Welders");
                welders.travel = TeamTravel.Network;
                welders.network = net;
                welders.homeNode = home;
                welders.members = new List<ResourceMemberSpec> { new ResourceMemberSpec { name = "Wa" }, new ResourceMemberSpec { name = "Wb" } };
                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.Schedule;
                src.SetArrivalsTable(new Dictionary<string, List<string>> { ["Time"] = new List<string> { "0" }, ["Name"] = new List<string> { "Src" }, ["Q"] = new List<string> { "1" } });
                var station = Make<UnityMultiServer>("Block");
                station.capacity = 1;
                station.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 5f };
                station.location = st;
                station.workNodes = new List<UnityTransportNode> { w1, w2 };
                station.processBy.team = welders;
                station.processBy.requirements = new List<ResourceRequirementSpec> { new ResourceRequirementSpec { count = 2 } };
                var sink = Make<UnitySink>("Sink");
                src.nextElements = new List<SElement> { station };
                station.nextElements = new List<SElement> { sink };

                var config = SceneExtractor.Extract(root).Config;
                var block = config.Elements.Find(e => e.Name == "Block");
                CollectionAssert.AreEqual(new[] { w1.NodeId, w2.NodeId }, (List<string>)block.Parameters["workNodes"], "work nodes go to the config");

                var clock = new SimClock(1);
                var model = new SimuLean.Headless.HeadlessModelFactory(clock);
                model.BuildModel(config);
                model.StartModel();
                clock.AdvanceClock(100);
                var at = model.Executors.Where(x => x.GetName() == "Wa" || x.GetName() == "Wb").Select(x => x.Location).OrderBy(x => x).ToList();
                CollectionAssert.AreEqual(new[] { w1.NodeId, w2.NodeId }.OrderBy(x => x).ToList(), at, "each welder stood at a work point, not on the block");
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }
    }
}
