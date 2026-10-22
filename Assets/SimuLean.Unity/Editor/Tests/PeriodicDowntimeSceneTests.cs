using System.Linq;
using NUnit.Framework;
using SimuLean.Downtime;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>A periodic stop set on a <see cref="UnityDowntime"/>: extracted as a <c>Periodic</c>
    /// <see cref="DowntimeConfig"/> and built by the engine like the other kinds (one build path).</summary>
    public class PeriodicDowntimeSceneTests
    {
        [Test]
        public void APeriodicDowntimeExtractsAndRunsHeadless()
        {
            var root = new GameObject("Model") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var press = new GameObject("Press").AddComponent<UnityMultiServer>();
                press.transform.SetParent(root.transform);
                var pm = new GameObject("Maintenance").AddComponent<UnityDowntime>();
                pm.transform.SetParent(root.transform);
                pm.kind = UnityDowntime.DowntimeKind.Periodic;
                pm.targets.Add(press);
                pm.stateName = "SCHEDULED_DOWN";
                pm.periodicFirst = 10;
                pm.periodicPeriod = 100;
                pm.periodicDuration = 20;
                pm.useUntil = true;
                pm.periodicUntil = 350;
                pm.periodicCode = "PM";

                var config = SceneExtractor.Extract(root).Config;
                var dc = config.Downtimes.Single();
                Assert.AreEqual("Periodic", dc.Kind);
                Assert.AreEqual(100.0, dc.GetParameter<double>("period"), 1e-12);
                Assert.AreEqual(350.0, dc.GetParameter<double>("until"), 1e-12);
                Assert.IsEmpty(SimuLean.Optimization.ModelParameterCatalog.Undecided(config), "every key is decided");

                var clock = new SimClock(1);
                var model = new SimuLean.Headless.HeadlessModelFactory(clock);
                model.BuildModel(config);
                model.StartModel();
                clock.AdvanceClock(1000);
                var gen = (PeriodicDowntime)model.DowntimesById[dc.Id].Single();
                Assert.AreEqual(4, gen.StopCount, "stops at 10, 110, 210, 310");
                Assert.AreEqual(80.0, gen.TotalDowntime, 1e-9);
                Assert.AreEqual("PM", gen.Code);
                Assert.AreEqual(0.08, model.Find("Press").GetStateRatio(ElementState.SCHEDULED_DOWN), 1e-9);

                pm.useUntil = false;
                Assert.IsFalse(SceneExtractor.Extract(root).Config.Downtimes.Single().Parameters.ContainsKey("until"), "no end: no 'until'");
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
