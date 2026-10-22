using NUnit.Framework;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>The Delay element: the scene component extracts its delay and the engine builds a Delay.</summary>
    public class DelaySceneTests
    {
        [Test]
        public void ADelayExtractsItsDelayAndBuildsHeadless()
        {
            var go = new GameObject("Night at home");
            try
            {
                var d = go.AddComponent<UnityDelay>();
                d.delayStrategy.strategyType = RandomProcessType.Constant;
                d.delayStrategy.constantValue = 7.0;
                var config = SceneExtractor.ExtractElement(d);
                Assert.AreEqual("UnityDelay", config.Type);
                Assert.AreEqual(7.0, config.GetParameter<double>("constantValue"), 1e-12);

                var clock = new SimClock(1);
                var model = new SimuLean.Headless.HeadlessModelFactory(clock);
                model.BuildModel(new SimulationConfig { Elements = { config } });
                var built = model.Find("Night at home");
                Assert.IsInstanceOf<Delay>(built);
                Assert.AreEqual(-1, built.GetFreeCapacity(), "no capacity");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ItIsInTheProcessorsLibrary()
        {
            var attr = (SimuLeanElementAttribute)System.Attribute.GetCustomAttribute(typeof(UnityDelay), typeof(SimuLeanElementAttribute));
            Assert.IsNotNull(attr);
            Assert.AreEqual(SimuLeanModules.Processors, attr.Module);
        }
    }
}
