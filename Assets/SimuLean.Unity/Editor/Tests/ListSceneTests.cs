using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using SimuLean.Headless;
using SimuLean.Serialization;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Lists in a scene: source → queue (push to list) → list → server
    /// (pull from list) → sink. Built in a temporary additive scene, extracted, run headless.
    /// </summary>
    public class ListSceneTests
    {
        [Test]
        public void ListSceneExtractsAndRunsHeadless()
        {
            // The Test Runner runs in an untitled scene (no additive scene possible then): build there.
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

                var list = Make<UnitySimList>("Work");
                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1f };
                var queue = Make<UnityQueue>("Q");
                queue.capacity = 1000;
                var server = Make<UnityMultiServer>("Server");
                server.capacity = 1;
                server.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2f };
                var sink = Make<UnitySink>("Sink");

                src.nextElements = new List<SElement> { queue };
                queue.sendTo = SElement.SendMode.List;
                queue.pushToList = list;
                queue.nextElements = new List<SElement> { sink };   // ignored: the queue sends to the list
                server.receiveFrom = SElement.ReceiveMode.List;
                server.pullFromList = list;
                server.pullQuery = new Collections.ListQuerySpec { orderBy = "age", descending = true };
                server.nextElements = new List<SElement> { sink };

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;

                Assert.AreEqual(1, cfg.Lists.Count, "one list exported");
                Assert.IsTrue(cfg.Lists[0].Fields.Exists(f => f.name == "prio" && f.kind == Collections.ListFieldKind.Label), "default prio field exported");
                string listId = cfg.Lists[0].Id;
                Assert.IsTrue(cfg.Connections.Any(c => c.ConnectionType == "PushToList" && c.TargetId == listId), "queue pushes to the list");
                Assert.AreEqual(2, cfg.Connections.Count(c => c.ConnectionType == "General"), "src → Q and server → sink only (Q's Next Elements ignored)");
                var pull = cfg.Connections.Single(c => c.ConnectionType == "PullFromList");
                Assert.AreEqual(listId, pull.SourceId);
                Assert.AreEqual("|age desc", pull.GetParameter<string>("query"), "pull query exported");

                cfg.MaxSimulationTime = 100;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(100);

                Element sinkEl = elements.Values.Single(e => e is Sink);
                Element qEl = elements.Values.Single(e => e is ItemsQueue);
                // Arrivals every 1 (t = 1..100), service 2: about 50 served, the rest wait in the queue.
                Assert.That(sinkEl.GetStatsInputCount(), Is.InRange(48, 51), "served through the list");
                Assert.AreEqual(qEl.GetStatsInputCount() - qEl.GetStatsOutputCount(), qEl.GetQueueLength(), "queue balance");
                Assert.Greater(factory.Lists[listId].Pushed, 0, "items advertised in the list");
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }

        /// <summary>Combiner: main input from a connection, component input pulled from a list.</summary>
        [Test]
        public void CombinerListInputExtractsAndRunsHeadless()
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

                var parts = Make<UnitySimList>("Parts");
                var mainSrc = Make<UnitySource>("MainSrc");
                mainSrc.mode = SourceMode.InterArrival;
                mainSrc.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 4f };
                var partSrc = Make<UnitySource>("PartSrc");
                partSrc.mode = SourceMode.InterArrival;
                partSrc.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1f };
                partSrc.assignLabels = new List<LabelAssignment> { new LabelAssignment { label = "prio", value = "3" } };
                var partQ = Make<UnityQueue>("PartQ");
                partQ.capacity = 1000;
                var comb = Make<UnityCombiner>("Comb");
                comb.requirements = new[] { 2 };
                comb.batchMode = true;
                comb.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1f };
                comb.listInputs = new List<UnityCombiner.ListInput> { new UnityCombiner.ListInput { input = 1, list = parts } };
                var sink = Make<UnitySink>("Sink");

                mainSrc.nextElements = new List<SElement> { comb };
                partSrc.nextElements = new List<SElement> { partQ };
                partQ.sendTo = SElement.SendMode.List;
                partQ.pushToList = parts;
                comb.nextElements = new List<SElement> { sink };

                var slots = comb.ResolveInputSlots();
                Assert.AreEqual(2, slots.Count);
                Assert.AreEqual(mainSrc, slots[0], "main input from the connection");
                Assert.IsNull(slots[1], "component input fed by the list");
                Assert.AreEqual(2, comb.FedInputCount());

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                var pull = cfg.Connections.Single(c => c.ConnectionType == "PullFromList");
                Assert.AreEqual(1, pull.GetParameter<int>("port", -1), "port exported");
                var combCfg = cfg.Elements.Single(e => e.Type == "UnityCombiner");
                var order = combCfg.GetParameter<List<string>>("inputOrder");
                Assert.AreEqual(2, order.Count);
                Assert.AreEqual("", order[1], "list input placeholder");
                var srcCfg = cfg.Elements.Single(e => e.Name == "PartSrc");
                CollectionAssert.AreEqual(new[] { "prio=3" }, srcCfg.GetParameter<List<string>>("assignLabels"));

                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(100);
                Element sinkEl = elements.Values.Single(e => e is Sink);
                // A main every 4 (t = 4..100) with 2 parts each (a part every 1): ~24 assemblies.
                Assert.That(sinkEl.GetStatsInputCount(), Is.InRange(22, 25), "assemblies with parts pulled from the list");
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }
    }
}
