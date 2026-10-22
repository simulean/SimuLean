using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SimuLean.Headless;
using SimuLean.Optimization;
using SimuLean.Experimentation.Controller;
using SimuLean.Experimentation.Core;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The Experimenter runs scenarios × replications of the scene headless with the Optimizer's parameter /
    /// metric specs; each run is the scene's model with the scenario's values and the replication's seed (base + r − 1,
    /// the same in every scenario).
    /// </summary>
    public class ExperimenterSceneTests
    {
        /// <summary>Removing a parameter removes its value in every scenario (the values after it
        /// moved to the next parameter); a new scenario never repeats a name.</summary>
        [Test]
        public void RemovingAParameterKeepsTheOtherValuesAndNewScenarioNamesAreUnique()
        {
            var go = new GameObject("Exp");
            try
            {
                var exp = go.AddComponent<Experimenter>();
                foreach (string n in new[] { "servers", "capacity", "speed" }) exp.Parameters.Add(new ParameterSpec { name = n });
                exp.Scenarios.Add(new ExperimentScenario { scenarioName = "Scenario 1", parameterValues = new List<string> { "1", "10", "100" } });
                exp.Scenarios.Add(new ExperimentScenario { scenarioName = "Scenario 3", parameterValues = new List<string> { "2", "20", "200" } });
                var so = new SerializedObject(exp);
                SimuLean.Experimentation.Editor.ExperimenterEditor.RemoveParameter(so.FindProperty("parameters"), so.FindProperty("scenarios"), 0);
                Assert.AreEqual("Scenario 4", SimuLean.Experimentation.Editor.ExperimenterEditor.NewScenarioName(so.FindProperty("scenarios")),
                                "not 'Scenario 3' again");
                so.ApplyModifiedPropertiesWithoutUndo();
                Assert.AreEqual(new[] { "capacity", "speed" }, exp.Parameters.ConvertAll(p => p.name).ToArray());
                CollectionAssert.AreEqual(new[] { "10", "100" }, exp.Scenarios[0].parameterValues, "capacity keeps 10, speed keeps 100");
                CollectionAssert.AreEqual(new[] { "20", "200" }, exp.Scenarios[1].parameterValues);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ScenariosAreTheSceneModelWithTheirValuesAndSeeds()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var scene = !string.IsNullOrEmpty(active.path) ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
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
                var server = Make<UnityMultiServer>("Server");
                server.capacity = 1;
                server.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Uniform, uniformMin = 1.0, uniformMax = 3.0 };
                var sink = Make<UnitySink>("Sink");
                src.nextElements = new List<SElement> { server };
                server.nextElements = new List<SElement> { sink };

                var exp = root.AddComponent<Experimenter>();
                var so = new SerializedObject(exp);
                so.FindProperty("modelRoot").objectReferenceValue = root;
                so.FindProperty("runTime").doubleValue = 300;
                so.FindProperty("randomSeed").intValue = 7;
                so.FindProperty("exportFormats").intValue = 0;
                so.ApplyModifiedPropertiesWithoutUndo();
                exp.Parameters.Add(new ParameterSpec { name = "servers", target = ParameterTarget.Element, element = server, key = "capacity" });
                exp.Metrics.Add(new MetricSpec { name = "throughput", scope = MetricScope.Element, element = sink, statistic = "input" });
                exp.Scenarios.Add(new ExperimentScenario { scenarioName = "one", replications = 2, parameterValues = new List<string> { "1" } });
                exp.Scenarios.Add(new ExperimentScenario { scenarioName = "three", replications = 2, parameterValues = new List<string> { "3" } });
                Assert.IsNull(exp.Validate());

                var results = exp.RunHeadlessNow();
                Assert.AreEqual(4, results.Count);
                Assert.AreEqual("one", results[0].Scenario); Assert.AreEqual(2, results[1].Replication);
                Assert.AreEqual("three", results[3].Scenario);
                Assert.AreNotEqual(results[0].Metrics["throughput"], results[1].Metrics["throughput"], "replications differ");
                Assert.Greater(results[2].Metrics["throughput"], results[0].Metrics["throughput"], "three servers serve more");
                Assert.IsNotEmpty(results[3].Results?.Objects);

                // run "three", replication 2 = the scene with 3 servers, seed 7 + 1
                server.capacity = 3;
                var clock = new SimClock(8);
                var model = new HeadlessModelFactory(clock);
                model.BuildModel(SceneExtractor.Extract(root).Config);
                model.StartModel();
                clock.AdvanceClock(300);
                double expected = 0;
                foreach (var e in model.GetAllElements().Values) if (e.GetName() == "Sink") expected = e.GetStatsInputCount();
                Assert.AreEqual(expected, results[3].Metrics["throughput"], "a run is the scene's model with the scenario's values and seed");

                exp.Scenarios[1].parameterValues.Add("2");
                StringAssert.Contains("2 values for 1 parameters", exp.Validate());
                exp.Scenarios[1].parameterValues.RemoveAt(1);

                // any kind of parameter: an option (the service-time distribution) by its name
                exp.Parameters.Add(new ParameterSpec { name = "dist", target = ParameterTarget.Element, element = server, key = "delayStrategyType" });
                exp.Scenarios[0].parameterValues.Add("Uniform");
                exp.Scenarios[1].parameterValues.Add("Constant");
                Assert.IsNull(exp.Validate());
                var mixed = exp.RunHeadlessNow();
                var set = exp.BuildResultSet();
                Assert.AreEqual("Constant", set.Scenarios[1].Parameters[1].Value, "the scenario's values go with the results");
                Assert.AreEqual(4, set.Runs.Count);
                Assert.AreEqual(4 * 1, set.MetricsTable().RowCount, "one metric per run");
                Assert.Greater(set.ResultsTable().RowCount, 0, "every statistic of every run");
                Assert.AreEqual(results[0].Metrics["throughput"], mixed[0].Metrics["throughput"], "the same scenario (Uniform) gives the same run");
                exp.Scenarios[1].parameterValues[1] = "Nope";
                StringAssert.Contains("is not one of", exp.Validate());
                exp.Scenarios[1].parameterValues[1] = "Constant";
                exp.Scenarios[1].parameterValues[0] = "many";
                StringAssert.Contains("is not a number", exp.Validate());
            }
            finally
            {
                Object.DestroyImmediate(root);
                if (scene != active) EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
