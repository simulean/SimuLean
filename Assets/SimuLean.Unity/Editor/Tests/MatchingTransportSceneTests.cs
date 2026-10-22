using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SimuLean.Results;
using SimuLean.Serialization;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// A yard (Queue, release Any Accepted, carried by a crane team) serving
    /// one Combiner with two assemblies by key — extracted and run headless gives the reference model
    /// <c>yard_kitting_capacity</c>'s result (Unity ↔ headless parity of the new settings).
    /// </summary>
    public class MatchingTransportSceneTests
    {
        [Test]
        public void YardServesTwoAssembliesByKeyWithACrane()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            try
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                T Make<T>(string n) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    return go.AddComponent<T>();
                }
                Dictionary<string, List<string>> Table(string name, params string[] blocks)
                    => new Dictionary<string, List<string>>
                    {
                        ["Time"] = blocks.Select((b, i) => name == "subs" ? i.ToString() : "0").ToList(),
                        ["Name"] = blocks.Select(_ => name).ToList(),
                        ["Q"] = blocks.Select(_ => "1").ToList(),
                        ["block"] = blocks.ToList(),
                    };

                var clock = Make<UnitySimClock>("Clock");
                clock.MaxTime = 200;
                var blocks = Make<UnitySource>("blocks");
                blocks.mode = SourceMode.Schedule;
                blocks.SetArrivalsTable(Table("blocks", "3", "1", "2", "4"));
                var subs = Make<UnitySource>("subs");
                subs.mode = SourceMode.Schedule;
                subs.SetArrivalsTable(Table("subs", "1", "2", "3", "4"));
                var yard = Make<UnityQueue>("yard");
                yard.capacity = 4;
                yard.releaseMode = ReleaseMode.AnyAccepted;
                var crane = Make<UnityResourceTeam>("crane");
                crane.members = new List<ResourceMemberSpec> { new ResourceMemberSpec { name = "Crane1" } };
                yard.transportBy.team = crane;
                yard.transportBy.travelTime = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2f };
                yard.transportBy.overrideLoadTimes = true;
                yard.transportBy.loadTime = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0.5f };
                yard.transportBy.unloadTime = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0.5f };
                var bays = Make<UnityCombiner>("bays");
                bays.requirements = new[] { 1 };
                bays.capacity = 2;
                bays.pullModeType = UnityCombiner.PullModeType.SingleLabel;
                bays.singleLabelName = "block";
                bays.singleLabelValue = "";
                bays.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 5f };
                bays.inputOrder = new List<SElement> { blocks, yard };
                var erected = Make<UnitySink>("erected");
                blocks.nextElements = new List<SElement> { bays };
                subs.nextElements = new List<SElement> { yard };
                yard.nextElements = new List<SElement> { bays };
                bays.nextElements = new List<SElement> { erected };

                var config = SceneExtractor.Extract(root).Config;
                var yardCfg = config.Elements.Find(e => e.Name == "yard");
                Assert.AreEqual("AnyAccepted", yardCfg.Parameters["releaseMode"], "the release mode goes to the config");
                Assert.AreEqual(2, config.Elements.Find(e => e.Name == "bays").Parameters["capacity"], "the assemblies too");

                var clockE = new SimClock(1);
                var model = new SimuLean.Headless.HeadlessModelFactory(clockE);
                model.BuildModel(config);
                var order = new List<double>();
                model.GetAllElements().Values.First(e => e.GetName() == "erected").ItemEntered += (el, it) => order.Add(it.GetLabelValue("block") ?? -1);
                model.StartModel();
                clockE.AdvanceClock(200);
                var results = RunResults.Capture(model);
                Assert.AreEqual("1,3,2,4", string.Join(",", order), "each assembly matched its own main item (as the reference model)");
                Assert.AreEqual(19, results.Find(ResultScope.Model, "model")[ResultMetrics.Makespan], 1e-9, "the reference model's makespan");
                Assert.AreEqual(4, results.Find(ResultScope.Resource, "Crane1")[ResultMetrics.Tasks], "the crane carried every sub-block");
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }
    }
}
