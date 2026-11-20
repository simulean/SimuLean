using System.Collections.Generic;
using NUnit.Framework;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>Labels on entry / exit and probability routing go from the scene to the engine.</summary>
    public class LabelsAndRoutingSceneTests
    {
        [Test]
        public void AStationsLabelsAndWeightsAreExtractedAndBuilt()
        {
            var go = new GameObject("HomeTest");
            try
            {
                var st = go.AddComponent<UnityMultiServer>();
                st.entryLabels = new List<string> { "reps = (reps ?? 0) + 1", " " };
                st.exitLabels = new List<string> { "t_out = now()" };
                st.outputStrategy.strategyType = OutputStrategyType.Probability;
                st.outputStrategy.weights = new List<float> { 0.9f, 0.1f };

                var config = SceneExtractor.ExtractElement(st);
                CollectionAssert.AreEqual(new[] { "reps = (reps ?? 0) + 1", " " }, config.Get<List<string>>("entryLabels"));
                CollectionAssert.AreEqual(new[] { "t_out = now()" }, config.Get<List<string>>("exitLabels"));
                Assert.AreEqual("Probability", config.Get<string>("outputStrategy.strategyType"));
                var weights = config.Get<double[]>("outputStrategy.weights");
                Assert.AreEqual(0.9, weights[0], 0.0, "0.9f is exported as 0.9");
                Assert.AreEqual(0.1, weights[1], 0.0);

                var model = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
                model.BuildModel(new SimulationConfig { Elements = { config } });
                var built = model.Find("HomeTest");
                Assert.AreEqual(new[] { "reps" }, built.EntryLabels.Names);
                Assert.AreEqual(new[] { "t_out" }, built.ExitLabels.Names);
                Assert.IsInstanceOf<ProbabilityRoutingStrategy>(built.GetOutputStrategy());
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ASourcesCreationLabelsAreRecordsExtractedAndBuilt()
        {
            var go = new GameObject("Orders");
            try
            {
                var src = go.AddComponent<UnitySource>();
                src.mode = SourceMode.InterArrival;
                src.createLabels = new List<string> { "sku = '007'", " ", "n = 007" };
                var config = SceneExtractor.ExtractElement(src);
                CollectionAssert.AreEqual(new[] { "sku = '007'", "n = 007" }, config.Get<List<string>>("createLabels"));

                var model = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
                model.BuildModel(new SimulationConfig { Elements = { config } });
                var built = (InterArrivalSource)model.Find("Orders");
                Assert.AreEqual(new[] { "sku", "n" }, built.CreateLabels.Names);

                src.createLabels = new List<string> { "sku = 'A-07" };   // a bad record (unclosed text) fails the build naming the source
                var bad = SceneExtractor.ExtractElement(src);
                var ex = Assert.Catch(() => new SimuLean.Headless.HeadlessModelFactory(new SimClock(1), enableLogging: false)
                    .BuildModel(new SimulationConfig { Elements = { bad } }));
                StringAssert.Contains("Orders", ex.Message);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void WithoutLabelsNothingIsExtracted()
        {
            var go = new GameObject("Plain");
            try
            {
                var config = SceneExtractor.ExtractElement(go.AddComponent<UnityQueue>());
                Assert.IsFalse(config.Has("entryLabels"));
                Assert.IsFalse(config.Has("exitLabels"));
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
