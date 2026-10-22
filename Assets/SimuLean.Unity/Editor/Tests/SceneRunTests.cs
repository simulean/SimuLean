using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using SimuLean.Headless;
using SimuLean.Serialization;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// One build path: a run in the scene builds its model like a headless run (scene extraction → engine
    /// factory) with the components as views, binds the components to the engine objects, and gives exactly the results
    /// of the headless run with the same seed. Every run builds a fresh model (no previous model left running).
    /// </summary>
    public class SceneRunTests
    {
        [Test]
        public void SceneRunIsTheHeadlessModelWithTheComponentsAsViews()
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

                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Exponential, exponentialRate = 1.0 };
                var queue = Make<UnityQueue>("Q");
                queue.capacity = 5;
                var server = Make<UnityMultiServer>("Server");
                server.capacity = 2;
                server.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Uniform, uniformMin = 0.5, uniformMax = 2.5 };
                var sink = Make<UnitySink>("Sink");
                src.nextElements = new List<SElement> { queue };
                queue.nextElements = new List<SElement> { server };
                server.nextElements = new List<SElement> { sink };

                // Scene run
                var sceneClock = new SimClock(11);
                var bound = new List<SElement>();
                HeadlessModelFactory model = SceneModelBuilder.Build(sceneClock, bound, root);
                model.StartModel();
                sceneClock.AdvanceClock(400);

                Assert.AreEqual(4, bound.Count, "every element component is bound");
                Assert.IsInstanceOf<MultiServer>(server.GetElement());
                Assert.AreSame(server, server.GetElement().vElement, "the component is the view of its engine element");
                Assert.AreSame(queue, queue.GetElement().vElement);

                // Headless run of the same scene, same seed
                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                var clock = new SimClock(11);
                var factory = new HeadlessModelFactory(clock);
                Dictionary<string, Element> built = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(400);

                Element headlessSink = null, headlessServer = null;
                foreach (var e in built.Values)
                {
                    if (e.GetName() == "Sink") headlessSink = e;
                    if (e.GetName() == "Server") headlessServer = e;
                }
                Assert.Greater(sink.GetElement().GetStatsInputCount(), 200, "the scene run moved items");
                Assert.AreEqual(headlessSink.GetStatsInputCount(), sink.GetElement().GetStatsInputCount(), "same throughput");
                Assert.AreEqual(headlessServer.GetStatsStayTimeMean(), server.GetElement().GetStatsStayTimeMean(), 1e-12, "same stay times");
                Assert.AreEqual(((MultiServer)headlessServer).GetUtilization(), ((MultiServer)server.GetElement()).GetUtilization(), 1e-12);

                // A second run builds a new model on a new clock; the first one is not touched any more.
                Element firstSink = sink.GetElement();
                double firstCount = firstSink.GetStatsInputCount();
                var second = new SimClock(11);
                SceneModelBuilder.Build(second, bound, root).StartModel();
                second.AdvanceClock(50);
                Assert.AreNotSame(firstSink, sink.GetElement(), "rebound to the new model");
                Assert.AreEqual(firstCount, firstSink.GetStatsInputCount(), "the previous model does not run on");
                Assert.AreEqual(4, second.Context.Elements.Count, "the new model's context holds only its own elements");
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }
    }
}
