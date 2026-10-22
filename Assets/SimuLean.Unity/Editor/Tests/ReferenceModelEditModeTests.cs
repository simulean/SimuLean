using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SimuLean.Headless;
using SimuLean.Serialization;
using SimuLean.Testing;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// EditMode layer of the regression net: what needs Unity.
    /// (1) the reference models, built by the Unity-compiled engine, match the same baselines as the
    /// dotnet runner (StandaloneTests/referencemodels/baselines); (2) the sample scenes extract and run
    /// headless. Batch mode:
    /// <c>Unity.exe -batchmode -runTests -projectPath . -testPlatform EditMode -testResults results.xml</c>
    /// </summary>
    public class ReferenceModelEditModeTests
    {
        static string BaselineDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "StandaloneTests", "referencemodels", "baselines"));

        static IEnumerable<string> ModelNames() => ReferenceModels.All().Select(m => m.Key);

        [TestCaseSource(nameof(ModelNames))]
        public void ReferenceModelMatchesBaseline(string model)
        {
            string file = Path.Combine(BaselineDir, model + ".json");
            Assert.That(File.Exists(file), $"no baseline {file} (run the dotnet runner with --update and commit it)");
            SimulationConfig cfg = ReferenceModels.All().First(m => m.Key == model).Value;
            var diffs = ReferenceModelRunner.Compare(File.ReadAllText(file), ReferenceModelRunner.Run(model, cfg));
            Assert.IsEmpty(diffs, string.Join("\n", diffs));
        }

        [TestCase("Assets/Scenes/CombinerScene.unity")]
        [TestCase("Assets/Scenes/AssemblyLine.unity")]
        [TestCase("Assets/Scenes/ListsDemo.unity")]
        public void SampleSceneRunsHeadless(string path)
        {
            using (var iso = new IsolatedScene(path))
            {
                SimulationConfig cfg = SceneExtractor.Extract(iso.Root).Config;

                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                Dictionary<string, Element> elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(2000);

                Assert.AreEqual(cfg.Elements.Count, elements.Count, "every extracted element is built");
                var combiners = elements.Values.OfType<Combiner>().ToList();
                Assert.IsNotEmpty(combiners, "scene has a combiner");
                foreach (var c in combiners)
                    Assert.Greater(c.GetStatsOutputCount(), 0, $"{c.GetName()} produced units headless");
                foreach (var s in elements.Values.OfType<Sink>())
                    Assert.Greater(s.GetStatsInputCount(), 0, $"{s.GetName()} received items headless");
                foreach (var l in factory.Lists)
                    Assert.Greater(l.Value.Pulled, 0, $"list {l.Key} was pulled from");
            }
        }
    }
}
