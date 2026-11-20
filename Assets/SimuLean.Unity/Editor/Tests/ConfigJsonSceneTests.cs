using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using SimuLean.Serialization;
using SimuLean.Testing;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The model exchange format of the out-of-editor runner (<see cref="ConfigJson"/>) on every example and test fixture scene:
    /// the model extracted from the scene, written to JSON and read back, gives the same JSON and the same seeded results
    /// (scenes put values in the parameter bags that the reference models do not — tables, lists, reactions, teams).
    /// </summary>
    public class ConfigJsonSceneTests
    {
        static IEnumerable<string> Scenes() =>
            AssetDatabase.FindAssets("t:Scene", IsolatedScene.SceneFolders).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p);

        [TestCaseSource(nameof(Scenes))]
        public void TheSceneModelRoundTripsThroughJson(string path)
        {
            using (var iso = new IsolatedScene(path))
            {
                SimulationConfig cfg;
                try { cfg = SceneExtractor.Extract(iso.Root).Config; }
                catch (SceneNotHeadlessException e)
                {
                    Assert.Pass("the scene runs only in Unity: " + e.Message);
                    return;
                }
                string json = ConfigJson.Serialize(cfg);
                SimulationConfig back = ConfigJson.Deserialize(json);
                Assert.AreEqual(json, ConfigJson.Serialize(back), "the JSON of the read model is the same");
                if (cfg.Elements.Count == 0) return;
                foreach (var c in new[] { cfg, back })
                {
                    c.UseFixedSeed = true;
                    c.Seed = 7;
                    c.MaxSimulationTime = System.Math.Min(c.MaxSimulationTime > 0 ? c.MaxSimulationTime : 1000, 1000);
                }
                string expected;
                try { expected = ReferenceModelRunner.ToJson(ReferenceModelRunner.Run(path, cfg)); }
                catch (SceneNotHeadlessException e)
                {
                    Assert.Pass("the scene runs only in Unity (" + e.Message + "); its JSON round-trips");
                    return;
                }
                Assert.AreEqual(expected, ReferenceModelRunner.ToJson(ReferenceModelRunner.Run(path, back)), "the same seeded results");
            }
        }
    }
}
