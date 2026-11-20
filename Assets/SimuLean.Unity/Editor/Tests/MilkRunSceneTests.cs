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
    /// The milk run in a scene: <see cref="UnityMilkRun"/> is extracted (settings, then the records naming
    /// other elements by their config ids), built by the engine handler and run headless; the train view's path math.
    /// </summary>
    public class MilkRunSceneTests
    {
        [Test]
        public void MilkRunSceneExtractsAndRunsHeadless()
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

                var team = Make<UnityResourceTeam>("Tuggers");
                team.AddMembers(1);
                team.members[0].loadTime = 1f;
                team.members[0].unloadTime = 2f;
                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 10f };
                var org = Make<UnityQueue>("OrgA"); org.capacity = 10;
                var line = Make<UnityQueue>("LineA"); line.capacity = 2;
                var use = Make<UnityMultiServer>("UseA");
                use.capacity = 1;
                use.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 12f };
                var empties = Make<UnityQueue>("EmptiesA"); empties.capacity = 10;
                var dock = Make<UnitySink>("Dock");
                src.nextElements = new List<SElement> { org };
                line.nextElements = new List<SElement> { use };
                use.nextElements = new List<SElement> { empties };

                var milk = Make<UnityMilkRun>("Milk");
                milk.transportBy = new TransportResourceUse { team = team };
                milk.capacity = 2;
                milk.stops.Add(new UnityMilkRun.StopSpec { name = "S1", emptiesOrigins = new List<SElement> { empties } });
                milk.flows.Add(new UnityMilkRun.FlowSpec { name = "fA", origin = org, stop = "S1", destination = line });
                milk.emptiesDestination = dock;
                milk.legs.Add(new UnityMilkRun.LegSpec { from = "Depot", to = "S1",
                    time = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 5f } });

                var x = SceneExtractor.Extract(root);
                SimulationConfig cfg = x.Config;
                var mc = cfg.Elements.Single(e => e.Type == "MilkRun");
                var flows = mc.Get<List<MilkRunFlowSettings>>("flows", null);
                Assert.AreEqual(1, flows.Count, "one flow");
                Assert.AreEqual(x.ElementIds[org], flows[0].Origin, "flow origin by config id");
                Assert.AreEqual(x.ElementIds[line], flows[0].Destination, "flow destination by config id");
                Assert.AreEqual(x.ElementIds[dock], mc.Get<string>("emptiesDestination", ""), "empties destination by id");
                Assert.AreEqual(cfg.ResourceTeams[0].Id, mc.Get<string>("transportBy.team", ""), "team = the Transport block");
                Assert.IsEmpty(SimuLean.Optimization.ModelParameterCatalog.Undecided(cfg), "every milk-run key is decided");

                cfg.MaxSimulationTime = 500;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(500);

                var mr = (MilkRun)elements[x.ElementIds[milk]];
                Assert.Greater(mr.Tours, 5, "tours run");
                Assert.AreEqual(mr.Delivered, elements[x.ElementIds[line]].GetStatsInputCount(), "deliveries enter the line through the link");
                Assert.Greater(mr.EmptiesReturned, 0, "empties come back");
                Assert.AreEqual(mr.EmptiesReturned, elements[x.ElementIds[dock]].GetStatsInputCount(), "empties enter the dock through the link");
            }
            finally
            {
                Object.DestroyImmediate(root);
                if (temporary) EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void TrailersFollowTheTractorsPath()
        {
            // the tractor went east 10 m, then north 5 m (newest last)
            var trail = new List<Vector3>();
            for (int i = 0; i <= 10; i++) trail.Add(new Vector3(i, 0, 0));
            for (int j = 1; j <= 5; j++) trail.Add(new Vector3(10, 0, j));
            Vector3 back = Vector3.back;
            Assert.That(Vector3.Distance(MilkRunTrainView.PointBehind(trail, 0f, back), new Vector3(10, 0, 5)), Is.LessThan(1e-4f), "at the tractor");
            Assert.That(Vector3.Distance(MilkRunTrainView.PointBehind(trail, 3f, back), new Vector3(10, 0, 2)), Is.LessThan(1e-4f), "on the last stretch");
            Assert.That(Vector3.Distance(MilkRunTrainView.PointBehind(trail, 7f, back), new Vector3(8, 0, 0)), Is.LessThan(1e-4f), "round the corner");
            Assert.That(Vector3.Distance(MilkRunTrainView.PointBehind(trail, 17f, back), new Vector3(-2, 0, 0)), Is.LessThan(1e-4f), "beyond the trail: straight on");
            Assert.That(Vector3.Distance(MilkRunTrainView.PointBehind(new List<Vector3>(), 2f, back), new Vector3(0, 0, -2)), Is.LessThan(1e-4f), "no trail yet");
        }
    }
}
