using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SimuLean.Headless;
using SimuLean.Resources;
using SimuLean.Serialization;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Resource teams in a scene: a team used by a station for its process and
    /// to carry its items — extracted and run headless; and a sample scene migrated from the operator pool.
    /// </summary>
    public class ResourceSceneTests
    {
        [Test]
        public void TeamSceneExtractsAndRunsHeadless()
        {
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            try
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                T Make<T>(string n) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    return go.AddComponent<T>();
                }

                var team = Make<UnityResourceTeam>("Crew");
                team.AddMembers(2);
                team.members[0].loadTime = 0.1f;
                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2f };
                var queue = Make<UnityQueue>("Q");
                queue.capacity = 1000;
                var server = Make<UnityMultiServer>("Server");
                server.capacity = 1;
                server.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1f };
                var sink = Make<UnitySink>("Sink");
                src.nextElements = new List<SElement> { queue };
                queue.nextElements = new List<SElement> { server };
                server.nextElements = new List<SElement> { sink };
                server.processBy = new ProcessResourceUse { team = team, release = ProcessRelease.ItemLeaves };
                server.transportBy = new TransportResourceUse
                {
                    team = team,
                    travelTime = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0.5f },
                };
                queue.processBy = new ProcessResourceUse { team = team };   // a queue has no process: not exported

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;

                Assert.AreEqual(1, cfg.ResourceTeams.Count, "one team exported");
                var tc = cfg.ResourceTeams[0];
                Assert.AreEqual(2, tc.Members.Count);
                Assert.AreEqual(0.1, tc.Members[0].LoadTime, 1e-6, "member load time exported");
                var sc = cfg.Elements.Single(e => e.Type == nameof(UnityMultiServer));
                Assert.AreEqual(tc.Id, sc.GetParameter<string>(ResourceUseKeys.ProcessTeam, ""), "process team");
                Assert.AreEqual("ItemLeaves", sc.GetParameter<string>(ResourceUseKeys.ProcessRelease, ""));
                Assert.AreEqual(tc.Id, sc.GetParameter<string>(ResourceUseKeys.TransportTeam, ""), "transport team");
                Assert.IsNotEmpty(sc.GetParameter<string>(ResourceUseKeys.TransportTravel, ""), "travel time exported");
                var qc = cfg.Elements.Single(e => e.Type == nameof(UnityQueue));
                Assert.IsEmpty(qc.GetParameter<string>(ResourceUseKeys.ProcessTeam, ""), "no process team on a queue");

                cfg.MaxSimulationTime = 100;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(100);

                Element sinkEl = elements.Values.Single(e => e is Sink);
                Assert.That(sinkEl.GetStatsInputCount(), Is.InRange(45, 51), "items processed and carried to the sink");
                Assert.AreEqual(2, factory.Executors.Count);
                Assert.Greater(factory.Executors.Sum(x => x.TasksDone), 90, "the crew processed and carried");
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void VehicleFleetOnNetworkExtractsAndRunsHeadless()
        {
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            try
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                T Make<T>(string n, Vector3 at = default) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    go.transform.position = at;
                    return go.AddComponent<T>();
                }

                var net = Make<UnityTransportNetwork>("Network");
                var n1 = Make<UnityTransportNode>("n1", new Vector3(0, 0, 0));
                var n2 = Make<UnityTransportNode>("n2", new Vector3(10, 0, 0));
                net.nodes = new List<UnityTransportNode> { n1, n2 };
                net.edges = new List<UnityTransportNetwork.EdgeDefinition> { new UnityTransportNetwork.EdgeDefinition { nodeA = n1, nodeB = n2 } };

                var fleet = Make<UnityResourceTeam>("Fleet");
                fleet.AddMembers(1);
                fleet.members[0].loadTime = 1; fleet.members[0].unloadTime = 1;
                fleet.travel = TeamTravel.Network;
                fleet.homeNode = n1;
                fleet.speed = 1;
                fleet.traffic = true;
                net.segmentCapacity = 2;

                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 25f };
                var q1 = Make<UnityQueue>("Q1"); q1.capacity = 5; q1.location = n1;
                var q2 = Make<UnityQueue>("Q2"); q2.capacity = 5; q2.location = n2;
                var sink = Make<UnitySink>("Sink");
                src.nextElements = new List<SElement> { q1 };
                q1.nextElements = new List<SElement> { q2 };
                q2.nextElements = new List<SElement> { sink };
                q1.transportBy = new TransportResourceUse { team = fleet };   // default travel time ignored: the route decides

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;

                Assert.IsNotNull(cfg.Network, "network exported");
                Assert.AreEqual(2, cfg.Network.Nodes.Count);
                Assert.AreEqual(1, cfg.Network.Tracks.Count);
                Assert.AreEqual("Network", cfg.ResourceTeams[0].Travel);
                Assert.AreEqual("n1", cfg.ResourceTeams[0].HomeNode);
                Assert.IsTrue(cfg.ResourceTeams[0].Traffic, "traffic exported");
                Assert.AreEqual(2, cfg.Network.SegmentCapacity, "segment capacity exported");
                var q1c = cfg.Elements.Single(e => e.Name == "Q1");
                Assert.AreEqual("n1", q1c.GetParameter<string>(HeadlessModelFactory.LocationKey, ""), "location exported");

                cfg.MaxSimulationTime = 500;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(500);

                // An item every 25: the AGV goes n1 → (load 1) → n2 (10) → (unload 1) → back to n1 (10) = 22 < 25.
                Element sinkEl = elements.Values.Single(e => e is Sink);
                Assert.That(sinkEl.GetStatsInputCount(), Is.InRange(18, 21), "items carried by the AGV");
                var agv = factory.Executors.Single();
                Assert.AreEqual(ResourceMode.Spatial, agv.Mode);
                Assert.IsInstanceOf<TrafficNetworkTravel>(agv.TravelModel, "travels under traffic control");
                Assert.Greater(agv.GetTimeInState(ResourceStates.TRAVELLING_LOADED), 150, "travelled loaded ~10 per item");
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void WalkingTeamOnMatrixExtractsAndRunsHeadless()
        {
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            try
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                T Make<T>(string n, Vector3 at = default) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    go.transform.position = at;
                    return go.AddComponent<T>();
                }

                var home = Make<UnityTransportNode>("H", new Vector3(0, 0, 0));
                var spot = Make<UnityTransportNode>("S", new Vector3(3, 0, 4));   // 5 from H
                var matrix = Make<UnityTravelMatrix>("Floor");
                matrix.nodes = new List<UnityTransportNode> { home, spot };
                matrix.FillStraightLine();
                Assert.AreEqual(1, matrix.entries.Count);
                Assert.AreEqual(5f, matrix.entries[0].distance, 1e-4f);

                var team = Make<UnityResourceTeam>("Ops");
                team.AddMembers(1);
                team.travel = TeamTravel.Matrix;
                team.matrix = matrix;
                team.homeNode = home;
                team.speed = 1;

                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 50f };
                var server = Make<UnityMultiServer>("Server");
                server.capacity = 1;
                server.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2f };
                server.location = spot;
                server.processBy = new ProcessResourceUse { team = team };
                var sink = Make<UnitySink>("Sink");
                src.nextElements = new List<SElement> { server };
                server.nextElements = new List<SElement> { sink };

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                Assert.AreEqual(1, cfg.TravelMatrices.Count, "matrix exported");
                Assert.AreEqual("Matrix", cfg.ResourceTeams[0].Travel);
                Assert.AreEqual(cfg.TravelMatrices[0].Id, cfg.ResourceTeams[0].Matrix);

                cfg.MaxSimulationTime = 500;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(500);
                Element serverEl = elements.Values.Single(e => e is MultiServer);
                Assert.AreEqual(5.0, serverEl.GetTimeInState(ElementState.WAITING_FOR_RESOURCE), 1e-6, "waited once for the 5-unit walk");
                Assert.AreEqual(ResourceMode.Spatial, factory.Executors.Single().Mode);
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void GantryStackerOnAxesExtractsAndRunsHeadless()
        {
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            try
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                T Make<T>(string n) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    return go.AddComponent<T>();
                }

                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 20f };
                var stacker = Make<UnityStacker>("St");
                stacker.positionCount = 2;
                stacker.capacity = 3;
                stacker.useCycleTime = false;
                stacker.CreateCraneNodes();
                Assert.IsNotNull(stacker.pickNode);
                Assert.AreEqual(2, stacker.positionNodes.Count);

                var gantry = Make<UnityResourceTeam>("Gantry");
                gantry.AddMembers(1);
                gantry.travel = TeamTravel.Axes;
                gantry.homeNode = stacker.pickNode;
                gantry.axisSpeed = new Vector3(1, 1, 1);
                stacker.processBy = new ProcessResourceUse { team = gantry };
                var sink = Make<UnitySink>("Sink");
                src.nextElements = new List<SElement> { stacker };
                stacker.nextElements = new List<SElement> { sink };

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                Assert.IsNotNull(cfg.Network, "crane points exported as nodes");
                Assert.AreEqual(3, cfg.Network.Nodes.Count);
                Assert.AreEqual("Axes", cfg.ResourceTeams[0].Travel);
                var stc = cfg.Elements.Single(e => e.Type == nameof(UnityStacker));
                Assert.AreEqual(stacker.pickNode.NodeId, stc.GetParameter<string>(StackerBuilder.KeyPickLocation, ""));

                cfg.MaxSimulationTime = 400;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(400);
                var st = (Stacker)elements.Values.Single(e => e is Stacker);
                Assert.Greater(st.ItemsStacked, 15, "the gantry stacked the items");
                Assert.Greater(factory.Executors.Single().GetTimeInState(ResourceStates.TRAVELLING_LOADED), 0, "by travelling");
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void WalkGridBakedFromCollidersExtractsAndRunsHeadless()
        {
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            try
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                T Make<T>(string n, Vector3 at = default) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    go.transform.position = at;
                    return go.AddComponent<T>();
                }

                // A wall between home and the station.
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.transform.SetParent(root.transform);
                wall.transform.position = new Vector3(0, 1, 0);
                wall.transform.localScale = new Vector3(6, 2, 0.5f);
                var grid = Make<UnityWalkGrid>("Floor");
                grid.size = new Vector2(12, 12);
                grid.cellSize = 0.5f;
                grid.clearance = 0f;
                grid.Bake();
                Assert.AreEqual(24, grid.rows.Count);
                int blocked = 0; foreach (var r in grid.rows) foreach (char c in r) if (c == '#') blocked++;
                Assert.That(blocked, Is.InRange(12, 60), "the wall's cells are blocked");

                var home = Make<UnityTransportNode>("H", new Vector3(0, 0, -4));
                var spot = Make<UnityTransportNode>("S", new Vector3(0, 0, 4));
                var team = Make<UnityResourceTeam>("Ops");
                team.AddMembers(1);
                team.travel = TeamTravel.Grid;
                team.walkGrid = grid;
                team.homeNode = home;
                team.speed = 1;

                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 100f };
                var server = Make<UnityMultiServer>("Server");
                server.capacity = 1;
                server.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2f };
                server.location = spot;
                server.processBy = new ProcessResourceUse { team = team };
                var sink = Make<UnitySink>("Sink");
                src.nextElements = new List<SElement> { server };
                server.nextElements = new List<SElement> { sink };

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                Assert.AreEqual(1, cfg.WalkGrids.Count, "grid exported");
                Assert.AreEqual("Grid", cfg.ResourceTeams[0].Travel);
                Assert.AreEqual(cfg.WalkGrids[0].Id, cfg.ResourceTeams[0].Grid);

                cfg.MaxSimulationTime = 500;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(500);
                Element serverEl = elements.Values.Single(e => e is MultiServer);
                double walk = serverEl.GetTimeInState(ElementState.WAITING_FOR_RESOURCE);
                Assert.Greater(walk, 8.5, "walked around the wall (straight would be 8)");
                Assert.Less(walk, 16, "but not absurdly far");
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }

        [TestCase("Assets/Scenes/ResourceExamples/Resources_Operators.unity")]
        [TestCase("Assets/Scenes/ResourceExamples/Resources_OperatorsWalking.unity")]
        [TestCase("Assets/Scenes/ResourceExamples/Resources_OperatorsWalkingGrid.unity")]
        [TestCase("Assets/Scenes/ResourceExamples/Resources_AGVs.unity")]
        [TestCase("Assets/Scenes/ResourceExamples/Resources_RobotCell.unity")]
        [TestCase("Assets/Scenes/ResourceExamples/Resources_GantryStacker.unity")]
        [TestCase("Assets/Scenes/ResourceExamples/Shipyard_BlockAssembly.unity")]
        public void ExampleSceneRunsHeadless(string path)
        {
            using (var iso = new IsolatedScene(path))
            {
                SimulationConfig cfg = SceneExtractor.Extract(iso.Root).Config;

                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(3000);
                Assert.AreEqual(cfg.Elements.Count, elements.Count, "every extracted element is built");
                foreach (var s in elements.Values.OfType<Sink>())
                    Assert.Greater(s.GetStatsInputCount(), 0, $"{s.GetName()} received items headless");
                foreach (var ex in factory.Executors)
                    Assert.Greater(ex.TasksDone, 0, $"{ex.GetName()} worked");
            }
        }

        [Test]
        public void PhysicsTeamNeedsCalibrationForHeadless()
        {
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            TravelCalibrationAsset asset = null;
            try
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                T Make<T>(string n, Vector3 at = default) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    go.transform.position = at;
                    return go.AddComponent<T>();
                }
                var grid = Make<UnityWalkGrid>("Floor");
                grid.size = new Vector2(12, 12); grid.cellSize = 0.5f; grid.Bake();
                var home = Make<UnityTransportNode>("H", new Vector3(0, 0, -2.5f));
                var spot = Make<UnityTransportNode>("S", new Vector3(0, 0, 2.5f));
                var team = Make<UnityResourceTeam>("Ops");
                team.AddMembers(1);
                team.travel = TeamTravel.UnityPhysics; team.physicsEstimate = PhysicsEstimate.Grid;
                team.walkGrid = grid; team.homeNode = home; team.speed = 1;
                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 100f };
                var server = Make<UnityMultiServer>("Server");
                server.capacity = 1;
                server.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2f };
                server.location = spot;
                server.processBy = new ProcessResourceUse { team = team };
                var sink = Make<UnitySink>("Sink");
                src.nextElements = new List<SElement> { server };
                server.nextElements = new List<SElement> { sink };

                var ex = Assert.Throws<System.InvalidOperationException>(() => SceneExtractor.Extract(root));
                StringAssert.Contains("calibration", ex.Message, "headless refused without a calibration");

                // Calibrated: time = 1 + 1 · distance (moves measured as if with physics).
                asset = ScriptableObject.CreateInstance<TravelCalibrationAsset>();
                var moves = new List<MoveRecord>
                {
                    new MoveRecord { From = "A", To = "B", Start = 0, End = 3, Distance = 2 },
                    new MoveRecord { From = "A", To = "C", Start = 0, End = 5, Distance = 4 },
                    new MoveRecord { From = "B", To = "C", Start = 0, End = 7, Distance = 6 },
                };
                asset.Store(TravelCalibrationFit.Fit(moves), "test");
                team.calibration = asset;
                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                var tc = cfg.ResourceTeams[0];
                Assert.AreEqual("Calibrated", tc.Travel);
                Assert.AreEqual("Grid", tc.CalibrationBase);
                Assert.AreEqual(cfg.WalkGrids[0].Id, tc.Grid);

                cfg.MaxSimulationTime = 300;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(300);
                Element serverEl = elements.Values.Single(e => e is MultiServer);
                // first walk H → S: 1 + 1 · 5 = 6 (the grid path is straight)
                Assert.AreEqual(6.0, serverEl.GetTimeInState(ElementState.WAITING_FOR_RESOURCE), 0.3, "calibrated walking time");
            }
            finally
            {
                if (asset != null) Object.DestroyImmediate(asset);
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ElementPropertiesExtractAndResolveHeadless()
        {
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            try
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                T Make<T>(string n) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    return go.AddComponent<T>();
                }
                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 5f };
                var queue = Make<UnityQueue>("Buffer"); queue.capacity = 10;
                var sink = Make<UnitySink>("Exit");
                src.nextElements = new List<SElement> { queue };
                queue.nextElements = new List<SElement> { sink };
                var props = queue.gameObject.AddComponent<SElementLabels>();
                props.GetLabelPairs().Add(new LabelPair("zone", 3));
                props.GetLabelPairs().Add(new LabelPair("owner") { text = "Line 2" });
                props.GetLabelPairs().Add(new LabelPair("exit") { reference = sink });
                props.RebuildLabelsDictionary();

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                var qc = cfg.Elements.Single(e => e.Name == "Buffer");
                var recs = qc.GetParameter<List<string>>(PropertyRecords.Key, null);
                Assert.IsNotNull(recs, "properties exported");
                CollectionAssert.AreEquivalent(new[] { "d|zone|3", "s|owner|Line 2", "ref|exit|Exit" }, recs);

                var factory = new HeadlessModelFactory(new SimClock(1));
                var built = factory.BuildModel(cfg);
                var q = built[qc.Id];
                Assert.AreEqual(3.0, q.Properties.GetDouble("zone"));
                Assert.AreEqual("Line 2", q.Properties.GetText("owner"));
                Assert.AreSame(built.Values.Single(e => e is Sink), q.Properties.GetRef("exit"), "reference resolved headless");
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void MigratedCombinerSceneUsesItsTeam()
        {
            const string path = "Assets/Scenes/CombinerScene.unity";
            using (var iso = new IsolatedScene(path))
            {
                SimulationConfig cfg = SceneExtractor.Extract(iso.Root).Config;

                Assert.AreEqual(1, cfg.ResourceTeams.Count, "the former operator pool is a team");
                Assert.AreEqual(2, cfg.ResourceTeams[0].Members.Count, "with its two operators");
                var users = cfg.Elements.Where(e => e.GetParameter<string>(ResourceUseKeys.ProcessTeam, "") == cfg.ResourceTeams[0].Id).ToList();
                Assert.AreEqual(2, users.Count, "both stations that used the pool use the team");
                Assert.IsTrue(users.All(e => e.GetParameter<string>(ResourceUseKeys.ProcessRelease, "") == "ItemLeaves"), "pool rule kept");
            }
        }
    }
}
