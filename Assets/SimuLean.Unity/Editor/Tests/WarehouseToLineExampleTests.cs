using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using SimuLean.Headless;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The user example of the warehouse / line-supply library the scene built by
    /// <see cref="WarehouseToLineExample"/> extracts to a model that runs like the reference model <c>warehouse_to_line</c>
    /// (the line never starves: every body but the last is assembled in the 8-hour shift), and so does the saved scene.
    /// </summary>
    public class WarehouseToLineExampleTests
    {
        static RunResults Run(SimulationConfig cfg, out HeadlessModelFactory factory)
        {
            cfg.MaxSimulationTime = 28800;
            var clock = new SimClock(cfg.Seed);
            factory = new HeadlessModelFactory(clock);
            factory.BuildModel(cfg);
            factory.StartModel();
            clock.AdvanceClock(cfg.MaxSimulationTime);
            return RunResults.Capture(factory, "warehouse_to_line");
        }

        static void CheckLikeTheReference(RunResults r)
        {
            Assert.IsFalse(r.Info.Deadlocked, "no deadlock");
            var cars = r.Of(ResultScope.Element).Single(o => o.Name == "Cars");
            Assert.AreEqual(479, cars[ResultMetrics.Input], "every body but the last assembled: the supply never stops the line");
            var train = r.Of(ResultScope.Element).Single(o => o.Name == "TuggerTrain");
            Assert.Greater(train[ResultMetrics.Tours], 100, "the tugger train runs");
            var trucks = r.Of(ResultScope.Element).Single(o => o.Name == "Trucks");
            Assert.GreaterOrEqual(trucks[ResultMetrics.TrucksReceived], 5, "trucks replenish the rack");
        }

        [Test]
        public void TheBuiltSceneRunsLikeTheReferenceModel()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            GameObject root = null;
            try
            {
                root = WarehouseToLineExample.Build(scene);
                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                Assert.IsEmpty(SimuLean.Optimization.ModelParameterCatalog.Undecided(cfg).ToList(), "every key decided");
                Assert.IsTrue(cfg.Connections.Any(c => c.ConnectionType == "PushToList"), "the rack publishes its stock");
                Assert.AreEqual(2, cfg.Connections.Count(c => c.ConnectionType == "PullFromList"), "the two pick faces pull their SKU");
                CheckLikeTheReference(Run(cfg, out _));
            }
            finally
            {
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else if (root != null) Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TheSavedSceneRunsHeadless()
        {
            using (var iso = new IsolatedScene(WarehouseToLineExample.ScenePath))
                CheckLikeTheReference(Run(SceneExtractor.Extract(iso.Root).Config, out _));
        }
    }
}
