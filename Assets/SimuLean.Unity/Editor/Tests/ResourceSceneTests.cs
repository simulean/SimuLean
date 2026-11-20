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
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n) where T : Component => test.Make<T>(n);

                var team = Make<UnityResourceTeam>("Crew");
                team.travel = TeamTravel.Abstract;   // the element's travel time (a team now travels straight by default)
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
                var sc = cfg.Elements.Single(e => e.Type == "MultiServer");
                Assert.AreEqual(tc.Id, sc.Get<string>("processBy.team", ""), "process team");
                Assert.AreEqual("ItemLeaves", sc.Get<string>("processBy.release", ""));
                Assert.AreEqual(tc.Id, sc.Get<string>("transportBy.team", ""), "transport team");
                Assert.IsNotNull(sc.Get<SimuLean.Serialization.SamplerSettings>("transportBy.travelTime"), "travel time exported");
                var qc = cfg.Elements.Single(e => e.Type == "Queue");
                Assert.IsEmpty(qc.Get<string>("processBy.team", ""), "no process team on a queue");

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
        }

        /// <summary>The preemption fields of the Process / Transport blocks and the team's On Stop are extracted and
        /// built; a new use's priority is 1.</summary>
        [Test]
        public void PreemptionFieldsExtractAndBuild()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n) where T : Component => test.Make<T>(n);

                var team = Make<UnityResourceTeam>("Crew");
                team.travel = TeamTravel.Abstract;
                team.AddMembers(1);
                team.jobOnMemberStop = ResourceStopPolicy.WaitForSame;
                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2f };
                var routine = Make<UnityMultiServer>("Routine");
                routine.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1.5f };
                var urgent = Make<UnityMultiServer>("Urgent");
                urgent.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0.5f };
                var sink = Make<UnitySink>("Sink");
                src.nextElements = new List<SElement> { routine, urgent };
                routine.nextElements = new List<SElement> { sink };
                urgent.nextElements = new List<SElement> { sink };
                routine.processBy = new ProcessResourceUse { team = team, preemptable = false };
                urgent.processBy = new ProcessResourceUse { team = team, priority = "5", preemption = PreemptMode.Restart };
                urgent.transportBy = new TransportResourceUse { team = team, preemption = PreemptMode.Resume };
                Assert.AreEqual("1", new ProcessResourceUse().priority, "a new use's priority is 1");

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                Assert.AreEqual(ResourceStopPolicy.WaitForSame, cfg.ResourceTeams[0].JobOnMemberStop, "team On Stop exported");
                var uc = cfg.Elements.Single(e => e.Name == "Urgent");
                var rc = cfg.Elements.Single(e => e.Name == "Routine");
                Assert.AreEqual("Restart", uc.Get<string>("processBy.preemption", ""));
                Assert.AreEqual("Resume", uc.Get<string>("transportBy.preemption", ""));
                Assert.AreEqual("None", rc.Get<string>("processBy.preemption", ""));
                Assert.IsFalse(rc.Get<bool>("processBy.preemptable", true), "preemptable exported");

                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                factory.BuildModel(cfg);
                var urgentRes = ((IResourceUser)factory.Find("Urgent")).Resources;
                var routineRes = ((IResourceUser)factory.Find("Routine")).Resources;
                Assert.AreEqual(PreemptMode.Restart, urgentRes.Preempt, "process mode built");
                Assert.IsFalse(routineRes.Preemptable, "preemptable built");
                Assert.AreEqual(ResourceStopPolicy.WaitForSame, factory.Executors[0].OnStop, "team On Stop built");
                Assert.IsTrue(urgentRes.Dispatcher.Preemptive, "the team is preemptive");
            }
        }

        /// <summary>Preemption everywhere: the Process block's preemption fields of an assembly station (one
        /// request per task, the station's mode) and of an erection dock (with its own time, HoldFor) are extracted and built
        /// onto the engine elements, and the scene runs headless with a station that takes their operators.</summary>
        [Test]
        public void PreemptionFieldsOfAssemblyAndDockExtractAndBuild()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n) where T : Component => test.Make<T>(n);
                RandomProcessConfig Const(float v) => new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = v };

                var team = Make<UnityResourceTeam>("Crew");
                team.travel = TeamTravel.Abstract;
                team.AddMembers(2);
                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = Const(6f);
                var asm = Make<UnityAssemblyStation>("Asm");
                asm.workerCount = 2;
                asm.tasks = new List<AssemblyTaskConfig>
                {
                    new AssemblyTaskConfig { id = "a", duration = Const(3f) },
                    new AssemblyTaskConfig { id = "b", duration = Const(2f) },
                };
                asm.processBy = new ProcessResourceUse { team = team, preemption = PreemptMode.Restart, preemptable = true };
                var urgentSrc = Make<UnitySource>("UrgentSrc");
                urgentSrc.mode = SourceMode.InterArrival;
                urgentSrc.interArrivalDistribution = Const(7f);
                var urgent = Make<UnityMultiServer>("Urgent");
                urgent.delayStrategy = Const(1f);
                urgent.processBy = new ProcessResourceUse { team = team, priority = "5", preemption = PreemptMode.Resume };
                var dock = Make<SimuLean.Unity.Naval.UnityErectionDock>("Dock");
                dock.processBy = new ProcessResourceUse { team = team, preemptable = false, holdForOwnTime = true, holdFor = Const(2f) };
                var sink = Make<UnitySink>("Sink");
                src.nextElements = new List<SElement> { asm };
                asm.nextElements = new List<SElement> { sink };
                urgentSrc.nextElements = new List<SElement> { urgent };
                urgent.nextElements = new List<SElement> { sink };

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                var ac = cfg.Elements.Single(e => e.Name == "Asm");
                var dc = cfg.Elements.Single(e => e.Name == "Dock");
                Assert.AreEqual("Restart", ac.Get<string>("processBy.preemption", ""), "assembly mode exported");
                Assert.IsTrue(ac.Get<bool>("processBy.preemptable", false), "assembly preemptable exported");
                Assert.AreEqual("None", dc.Get<string>("processBy.preemption", ""), "dock mode exported");
                Assert.IsFalse(dc.Get<bool>("processBy.preemptable", true), "dock preemptable exported");
                Assert.IsTrue(dc.Has("processBy.holdFor"), "dock own time exported");

                cfg.MaxSimulationTime = 200;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                factory.BuildModel(cfg);
                var asmRes = ((IResourceUser)factory.Find("Asm")).Resources;
                var dockRes = ((IResourceUser)factory.Find("Dock")).Resources;
                Assert.IsInstanceOf<AssemblyStation>(factory.Find("Asm"));
                Assert.IsInstanceOf<SimuLean.Naval.ErectionDock>(factory.Find("Dock"));
                Assert.AreEqual(PreemptMode.Restart, asmRes.Preempt, "assembly mode built");
                Assert.IsTrue(asmRes.Preemptable, "assembly preemptable built");
                Assert.IsFalse(dockRes.Preemptable, "dock preemptable built");
                Assert.IsNotNull(dockRes.HoldFor, "dock own time built");
                factory.StartModel();
                clock.AdvanceClock(200);
                Assert.Greater(asmRes.Interruptions, 0, "the urgent station took operators from assembly tasks");
                Assert.Greater(((Element)factory.Find("Sink")).GetStatsInputCount(), 0, "the scene's model runs headless");
            }
        }

        [Test]
        public void AStraightTeamGoesInStraightLinesToPointsInFrontOfTheElements()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n, Vector3 at = default) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    go.transform.position = at;
                    return go.AddComponent<T>();
                }

                var team = Make<UnityResourceTeam>("Ops", new Vector3(0f, 0f, -10f));
                Assert.AreEqual(TeamTravel.Straight, team.travel, "a new team travels in straight lines");
                team.AddMembers(1);
                team.speed = 2f;
                var src = Make<UnitySource>("Src", new Vector3(-5f, 0f, 0f));
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 100f };
                var server = Make<UnityMultiServer>("Server");
                server.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1f };
                var sink = Make<UnitySink>("Sink", new Vector3(5f, 0f, 0f));
                src.nextElements = new List<SElement> { server };
                server.nextElements = new List<SElement> { sink };
                server.processBy = new ProcessResourceUse { team = team };

                var x = SceneExtractor.Extract(root);
                var cfg = x.Config;
                var tc = cfg.ResourceTeams.Single();
                Assert.AreEqual(SimuLean.Serialization.TravelKind.Straight, tc.Travel);
                Assert.AreEqual(ExtractionRules.AutoHomeId(tc.Id), tc.HomeNode, "no home node: the team's own position");
                string serverId = x.ElementIds[server];
                var sc = cfg.Elements.Single(e => e.Id == serverId);
                Assert.AreEqual(SceneExtractor.AutoPointPrefix + serverId, sc.Get<string>("location", ""),
                                "an element without a location gets one");
                Vector3 front = UnityResourceTeam.AutoPoint(server);
                Assert.Less(front.z, -0.5f, "in front of the element (−Z, the model frame), where a person works");
                Assert.AreEqual(0f, front.y, 1e-6f, "on its floor");

                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                factory.BuildModel(cfg);
                double arrived = double.NaN, done = double.NaN;
                factory.Find("Server").ItemEntered += (e, item) => { if (double.IsNaN(arrived)) arrived = clock.GetSimulationTime(); };
                factory.Find("Sink").ItemEntered += (e, item) => { if (double.IsNaN(done)) done = clock.GetSimulationTime(); };
                factory.StartModel();
                clock.AdvanceClock(150);
                var op = factory.Executors.Single();
                Assert.GreaterOrEqual(op.TasksDone, 1, "the operator processed the first item");
                Assert.AreEqual(SceneExtractor.AutoPointPrefix + serverId, op.Location, "and stays at the station's point");
                double walk = Vector3.Distance(team.transform.position, front) / 2.0;
                Assert.AreEqual(walk + 1.0, done - arrived, 1e-4, "the item leaves after the walk (straight distance ÷ speed) and the process");
            }
        }

        [Test]
        public void AStorageMovedByAStraightTeamGetsAPointPerLane()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                var crane = new GameObject("Crane");
                crane.transform.SetParent(root.transform);
                var team = crane.AddComponent<UnityResourceTeam>();
                team.AddMembers(1);
                var storeGo = new GameObject("Store");
                storeGo.transform.SetParent(root.transform);
                var store = storeGo.AddComponent<UnityStorageSystem>();
                foreach (var (n, y) in new[] { ("L1", 0f), ("L2", 3f) })
                {
                    var lane = new GameObject(n);
                    lane.transform.SetParent(storeGo.transform);
                    lane.transform.localPosition = new Vector3(2f, y, 0f);
                }
                store.transportBy = new TransportResourceUse { team = team };
                Assert.IsTrue(store.AutoLaneNodes);

                var cfg = SceneExtractor.Extract(root).Config;
                var nodes = cfg.Network.Nodes.Where(r => r.Id.StartsWith("lane:")).ToList();
                Assert.AreEqual(2, nodes.Count, "one point per lane");
                foreach (var lane in store.ResolveLanes())
                {
                    Assert.AreEqual(store.AutoLaneNodeId(lane.LaneId), lane.NodeId);
                    Assert.IsTrue(nodes.Any(r => r.Id == lane.NodeId), $"{lane.LaneId}: its point is in the model");
                }

                team.travel = TeamTravel.Abstract;
                Assert.IsFalse(store.AutoLaneNodes, "an abstract team: instant moves, as before");
            }
        }

        [Test]
        public void VehicleFleetOnNetworkExtractsAndRunsHeadless()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
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
                Assert.AreEqual(SimuLean.Serialization.TravelKind.Network, cfg.ResourceTeams[0].Travel);
                Assert.AreEqual("n1", cfg.ResourceTeams[0].HomeNode);
                Assert.IsTrue(cfg.ResourceTeams[0].Traffic, "traffic exported");
                Assert.AreEqual(2, cfg.Network.SegmentCapacity, "segment capacity exported");
                var q1c = cfg.Elements.Single(e => e.Name == "Q1");
                Assert.AreEqual("n1", q1c.Get<string>("location", ""), "location exported");

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
                Assert.Greater(agv.GetTimeInState(ResourceStates.TRAVELING_LOADED), 150, "travelled loaded ~10 per item");
            }
        }

        [Test]
        public void WalkingTeamOnMatrixExtractsAndRunsHeadless()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
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
                Assert.AreEqual(SimuLean.Serialization.TravelKind.Matrix, cfg.ResourceTeams[0].Travel);
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
        }

        [Test]
        public void GantryStackerOnAxesExtractsAndRunsHeadless()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n) where T : Component => test.Make<T>(n);

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
                Assert.AreEqual(SimuLean.Serialization.TravelKind.Axes, cfg.ResourceTeams[0].Travel);
                var stc = cfg.Elements.Single(e => e.Type == "Stacker");
                Assert.AreEqual(stacker.pickNode.NodeId, stc.Get<string>("pickNode", ""));

                cfg.MaxSimulationTime = 400;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(400);
                var st = (Stacker)elements.Values.Single(e => e is Stacker);
                Assert.Greater(st.ItemsStacked, 15, "the gantry stacked the items");
                Assert.Greater(factory.Executors.Single().GetTimeInState(ResourceStates.TRAVELING_LOADED), 0, "by travelling");
            }
        }

        [Test]
        public void WalkGridBakedFromCollidersExtractsAndRunsHeadless()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
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
                Assert.AreEqual(SimuLean.Serialization.TravelKind.Grid, cfg.ResourceTeams[0].Travel);
                Assert.AreEqual(cfg.WalkGrids[0].Id, cfg.ResourceTeams[0].WalkGrid);

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
        }

        [TestCase("Assets/SimuLean.Unity/Editor/Tests/Scenes/Resources_Operators.unity")]
        [TestCase("Assets/SimuLean.Unity/Editor/Tests/Scenes/Resources_OperatorsWalking.unity")]
        [TestCase("Assets/SimuLean.Unity/Editor/Tests/Scenes/Resources_OperatorsWalkingGrid.unity")]
        [TestCase("Assets/SimuLean.Unity/Editor/Tests/Scenes/Resources_AGVs.unity")]
        [TestCase("Assets/SimuLean.Unity/Editor/Tests/Scenes/Resources_RobotCell.unity")]
        [TestCase("Assets/SimuLean.Unity/Editor/Tests/Scenes/Resources_GantryStacker.unity")]
        [TestCase("Assets/SimuLean.Unity/Editor/Tests/Scenes/Shipyard_BlockAssembly.unity")]
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
                    Assert.Greater(s.GetStatsInputCount(), 0, $"{s.Name} received items headless");
                foreach (var ex in factory.Executors)
                    Assert.Greater(ex.TasksDone, 0, $"{ex.Name} worked");
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

                var ex = Assert.Throws<SceneNotHeadlessException>(() => SceneExtractor.Extract(root));
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
                Assert.AreEqual(SimuLean.Serialization.TravelKind.Calibrated, tc.Travel);
                Assert.AreEqual(SimuLean.Serialization.TravelKind.Grid, tc.CalibrationBase);
                Assert.AreEqual(cfg.WalkGrids[0].Id, tc.WalkGrid);

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
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n) where T : Component => test.Make<T>(n);
                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 5f };
                var queue = Make<UnityQueue>("Buffer"); queue.capacity = 10;
                var sink = Make<UnitySink>("Exit");
                src.nextElements = new List<SElement> { queue };
                queue.nextElements = new List<SElement> { sink };
                var props = queue.gameObject.AddComponent<SElementProperties>();
                props.GetProperties().Add(new ElementProperty("zone", 3));
                props.GetProperties().Add(new ElementProperty("owner") { text = "Line 2" });
                props.GetProperties().Add(new ElementProperty("exit") { reference = sink });
                props.Rebuild();

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                var qc = cfg.Elements.Single(e => e.Name == "Buffer");
                var recs = qc.Get<List<PropertySettings>>(PropertyRecords.Key, null);
                Assert.IsNotNull(recs, "properties exported");
                CollectionAssert.AreEquivalent(new[] { "Number zone 3", "Text owner Line 2", "Reference exit Exit" },
                                               recs.Select(r => r.Kind + " " + r.Name + " " + (r.Kind == PropertyValueKind.Number ? r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : r.Text)));

                var factory = new HeadlessModelFactory(new SimClock(1));
                var built = factory.BuildModel(cfg);
                var q = built[qc.Id];
                Assert.AreEqual(3.0, q.Properties.GetDouble("zone"));
                Assert.AreEqual("Line 2", q.Properties.GetText("owner"));
                Assert.AreSame(built.Values.Single(e => e is Sink), q.Properties.GetRef("exit"), "reference resolved headless");
            }
        }
    }
}
