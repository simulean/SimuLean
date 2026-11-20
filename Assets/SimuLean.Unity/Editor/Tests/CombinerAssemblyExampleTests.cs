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
    /// The example of the Combiner's assembly view: the scene built by <see cref="CombinerAssemblyExample"/> has an
    /// entry per component input and extracts to a model in which the totes are the bottleneck (one every 20 s: a kit per
    /// tote in the hour, less the one in process), and so does the saved scene.
    /// </summary>
    public class CombinerAssemblyExampleTests
    {
        static void CheckRuns(SimulationConfig cfg)
        {
            cfg.MaxSimulationTime = 3600;
            var clock = new SimClock(cfg.Seed);
            var factory = new HeadlessModelFactory(clock);
            factory.BuildModel(cfg);
            factory.StartModel();
            clock.AdvanceClock(cfg.MaxSimulationTime);
            var r = RunResults.Capture(factory, "kitting");
            Assert.IsFalse(r.Info.Deadlocked, "no deadlock");
            var kits = r.Of(ResultScope.Element).Single(o => o.Name == "Kits");
            Assert.AreEqual(179, kits[ResultMetrics.Input], 1, "a kit per tote: the totes are the bottleneck");
        }

        [Test]
        public void TheBuiltSceneHasAnEntryPerComponentAndRuns()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            GameObject root = null;
            try
            {
                root = CombinerAssemblyExample.Build(scene);
                var c = root.GetComponentInChildren<UnityCombiner>();
                Assert.AreEqual(2, c.componentEntries.Count(e => e != null), "an entry per component input");
                Assert.IsTrue(c.animateAssembly);
                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                Assert.IsEmpty(SimuLean.Optimization.ModelParameterCatalog.Undecided(cfg).ToList(), "every key decided");
                CheckRuns(cfg);
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
            using (var iso = new IsolatedScene(CombinerAssemblyExample.ScenePath))
                CheckRuns(SceneExtractor.Extract(iso.Root).Config);
        }
    }
}
