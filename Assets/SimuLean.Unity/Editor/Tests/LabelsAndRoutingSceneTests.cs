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
            var go = new GameObject("Polygraphy");
            try
            {
                var st = go.AddComponent<UnityMultiServer>();
                st.entryLabels = new List<string> { "reps = reps + 1", " " };
                st.exitLabels = new List<string> { "t_out = now()" };
                st.outputStrategy.strategyType = OutputStrategyType.Probability;
                st.outputStrategy.weights = new List<float> { 0.9f, 0.1f };

                var config = SceneExtractor.ExtractElement(st);
                CollectionAssert.AreEqual(new[] { "reps = reps + 1", " " }, config.GetParameter<List<string>>(ElementConfigKeys.EntryLabels));
                CollectionAssert.AreEqual(new[] { "t_out = now()" }, config.GetParameter<List<string>>(ElementConfigKeys.ExitLabels));
                Assert.AreEqual("Probability", config.GetParameter<string>("outputStrategyType"));
                StringAssert.StartsWith("0.9", config.GetParameter<string>(ElementConfigKeys.OutputWeights));

                var model = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
                model.BuildModel(new SimulationConfig { Elements = { config } });
                var built = model.Find("Polygraphy");
                Assert.AreEqual(new[] { "reps" }, built.EntryLabels.Names);
                Assert.AreEqual(new[] { "t_out" }, built.ExitLabels.Names);
                Assert.IsInstanceOf<ProbabilityRoutingStrategy>(built.GetOutputStrategy());
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
                Assert.IsFalse(config.Parameters.ContainsKey(ElementConfigKeys.EntryLabels));
                Assert.IsFalse(config.Parameters.ContainsKey(ElementConfigKeys.ExitLabels));
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
