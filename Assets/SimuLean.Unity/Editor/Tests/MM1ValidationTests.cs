using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The M/M/1 validation scene (<c>Assets/Scenes/Validation/MM1.unity</c>, <c>docs/guide/validation.md</c>): λ = 0.8,
    /// μ = 1 per minute, so Wq = 4, Lq = 3.2, W = 5 and the utilisation 0.8. Run headless from the scene, the means of a
    /// few replications are within a few percent of the theory.
    /// </summary>
    public class MM1ValidationTests
    {
        private const string Path = "Assets/Scenes/Validation/MM1.unity";

        [Test]
        public void TheMM1SceneMatchesQueueingTheory()
        {
            var opened = EditorSceneManager.OpenScene(Path, OpenSceneMode.Additive);
            var active = SceneManager.GetActiveScene();
            try
            {
                // the root of the opened scene itself (the test runner's scene has a model root of the same name)
                var model = System.Array.Find(opened.GetRootGameObjects(), g => g.name == "SimuLean Model");
                Assert.IsNotNull(model, "the scene's model root");
                var study = SceneStudy.Of(model).WithReplications(5).WithSeed(1);
                study.Metric("Wq", "Queue.stay_avg").Metric("Lq", "Queue.content_avg")
                     .Metric("W", "Exit.tally:time_in_system:mean").Metric("rho", "Server.utilization");
                var runs = study.Run().Runs;
                Assert.IsTrue(runs.TrueForAll(r => r.Error == null), runs.Find(r => r.Error != null)?.Error);
                var m = new JObject();
                foreach (string k in new[] { "Wq", "Lq", "W", "rho" })
                    m[k] = new JObject { ["mean"] = runs.ConvertAll(r => r.Metrics[k]).Average() };
                Assert.AreEqual(4.0, (double)m["Wq"]["mean"], 0.4, "Wq");
                Assert.AreEqual(3.2, (double)m["Lq"]["mean"], 0.35, "Lq");
                Assert.AreEqual(5.0, (double)m["W"]["mean"], 0.4, "W");
                Assert.AreEqual(0.8, (double)m["rho"]["mean"], 0.02, "utilisation");
            }
            finally
            {
                if (active.IsValid()) SceneManager.SetActiveScene(active);
                EditorSceneManager.CloseScene(opened, true);
            }
        }
    }
}
