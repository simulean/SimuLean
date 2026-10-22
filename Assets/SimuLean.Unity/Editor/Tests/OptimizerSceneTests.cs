using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SimuLean.Optimization;
using SimuLean.Optimization.Evaluator;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The scene's parameters / metrics (<see cref="ParameterSpec"/>, <see cref="MetricSpec"/>) become a
    /// <see cref="SimulationObjective"/> over the extracted model, which any algorithm optimises; the Optimizer
    /// component validates its set-up.
    /// </summary>
    public class OptimizerSceneTests
    {
        /// <summary>A saved key the current options do not offer is kept (the inspector replaced it with
        /// the first option); a new parameter takes the first option.</summary>
        [Test]
        public void ASavedKeyNotOfferedNowIsKept()
        {
            var options = new List<ParameterInfo> { new ParameterInfo { Key = "capacity" }, new ParameterInfo { Key = "normalMean" } };
            Assert.AreEqual(1, SimuLean.Optimization.Editor.SpecGUI.SelectedOption("normalMean", options));
            Assert.AreEqual(-1, SimuLean.Optimization.Editor.SpecGUI.SelectedOption("uniformMin", options), "kept, shown as not offered");
            Assert.AreEqual(0, SimuLean.Optimization.Editor.SpecGUI.SelectedOption("", options), "a new parameter: the first option");
        }

        /// <summary>When every candidate failed (a metric typo), the result set keeps the history and the
        /// candidates' errors instead of failing on the re-run of a "best" that never ran.</summary>
        [Test]
        public void AFailedBestStillGivesTheResultSet()
        {
            var config = SimuLean.Testing.ReferenceModels.MultiServer3();
            var objective = new SimulationObjective(config,
                new ModelParameter[] { ConfigParameter.Of(config, ParameterScope.Element, "ms", "capacity", 1, 3) },
                new[] { new ModelMetric("out", MetricScope.Element, "no_such_element", "input") }, "out") { RunTime = 100 };
            var r = OptimizationEngine.Run(objective.BuildSpace(), objective, new RandomSearch { Samples = 3 }, new OptimizationSettings { Seed = 1 });
            Assert.IsNotNull(r.BestEvaluation?.Error, "every candidate failed");
            SimuLean.Results.ResultSet set = null;
            Assert.DoesNotThrow(() => set = Optimizer.BuildResultSet(r, objective, "out", null, System.DateTime.Now));
            Assert.AreEqual(0, set.Runs.Count, "no re-run of a failed best");
            Assert.IsTrue(set.Extra.Exists(t => t.Name == "history"), "the history is kept");
        }

        [Test]
        public void SceneSpecsBecomeAnOptimisationProblem()
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

                var servers = new ParameterSpec { name = "servers", target = ParameterTarget.Element, element = server, key = "capacity", min = 1, max = 4 };
                Assert.IsNotNull(servers.Info(), "the catalog offers the server count");
                Assert.AreEqual(VariableKind.Integer, servers.Info().Kind);
                var throughput = new MetricSpec { name = "throughput", scope = MetricScope.Element, element = sink, statistic = "input" };

                var config = SceneExtractor.Extract(root).Config;
                var objective = new SimulationObjective(config, new List<ModelParameter> { servers.ToModelParameter(config) },
                    new List<ModelMetric> { throughput.ToModelMetric() }, "throughput - 60 * servers") { RunTime = 500, BaseSeed = 3 };
                var space = objective.BuildSpace();

                // exhaustive reference
                double bestExhaustive = double.MinValue; double bestServers = 0;
                for (int k = 1; k <= 4; k++)
                {
                    var c = new Candidate(space); c.SetNumber(0, k);
                    double f = objective.Evaluate(c).Fitness;
                    if (f > bestExhaustive) { bestExhaustive = f; bestServers = k; }
                }
                var result = OptimizationEngine.Run(space, objective, new RandomSearch { Samples = 40, BatchSize = 8 },
                    new OptimizationSettings { Seed = 5, Parallelism = 2 });
                Assert.AreEqual(bestServers, result.Best.Number(0), "the optimiser finds the exhaustive optimum");
                Assert.AreEqual(bestExhaustive, result.BestEvaluation.Fitness, 1e-9);
                Assert.AreEqual(1, config.Elements.Find(e => e.Name == "Server").GetParameter<int>("capacity", 0), "the scene's model is not changed");

                // the component validates its set-up
                var opt = root.AddComponent<Optimizer>();
                var so = new SerializedObject(opt);
                so.FindProperty("modelRoot").objectReferenceValue = root;
                so.ApplyModifiedPropertiesWithoutUndo();
                Assert.IsNotNull(opt.Validate(), "no parameter yet");
                opt.Parameters.Add(servers);
                opt.Metrics.Add(throughput);
                Assert.IsNull(opt.Validate());
                opt.Parameters.Add(new ParameterSpec { target = ParameterTarget.Element, element = sink, key = "capacity" });
                StringAssert.Contains("has no parameter", opt.Validate());
            }
            finally
            {
                Object.DestroyImmediate(root);
                if (scene != active) EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
