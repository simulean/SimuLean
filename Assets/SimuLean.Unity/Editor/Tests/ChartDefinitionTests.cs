using NUnit.Framework;
using SimuLean.Results;
using SimuLean.Results.Charts;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// A chart asset builds its chart from the shown results or from stored sets (compared when several), and an
    /// engine image becomes a Unity texture the right way up.
    /// </summary>
    public class ChartDefinitionTests
    {
        private static ResultSet Set(string name, double value)
        {
            var set = new ResultSet { Name = name, Model = "m" };
            set.MetricNames.Add("out");
            set.Scenarios.Add(new ResultScenario { Name = "A" });
            for (int r = 1; r <= 2; r++)
            {
                var run = new ResultRun { Scenario = "A", Replication = r };
                run.Metrics["out"] = value + r;
                set.Runs.Add(run);
            }
            return set;
        }

        [Test]
        public void AChartAssetFromShownResultsAndFromStoredSets()
        {
            var def = ScriptableObject.CreateInstance<ChartDefinition>();
            ResultsOutput.StoreOverride = System.IO.Path.GetFullPath($"Temp/simulean_chart_test_{System.Guid.NewGuid():N}.db");
            try
            {
                def.spec = new ChartQuery { Kind = ChartKind.Bar, Y = new ChartValue(ChartSource.Metric, "out") };
                var shown = def.Build(Set("one", 10), ResultsOutput.ProjectRoot);
                Assert.IsFalse(shown.IsEmpty, shown.Message);
                Assert.AreEqual(11.5, shown.Series[0].Values[0], 1e-9, "the mean of the shown runs");
                Assert.IsTrue(def.Build((ResultSet)null, ResultsOutput.ProjectRoot).IsEmpty, "no results: a message");

                var store = ResultsOutput.Store(ResultsOutput.ProjectRoot);
                int a = store.Save(Set("one", 10)), b = store.Save(Set("two", 20));
                def.source = ChartDataSource.StoredSets;
                Assert.IsTrue(def.Build((ResultSet)null, ResultsOutput.ProjectRoot).IsEmpty, "no set chosen: a message");
                def.storedSets.Add(a);
                def.storedSets.Add(b);
                var compared = def.Build((ResultSet)null, ResultsOutput.ProjectRoot);
                CollectionAssert.AreEqual(new[] { "one · A", "two · A" }, compared.Categories, "two stored sets side by side");
                Assert.AreEqual(21.5, compared.Series[0].Values[1], 1e-9);
            }
            finally
            {
                ResultsOutput.StoreOverride = null;
                Object.DestroyImmediate(def);
            }
        }

        [Test]
        public void AChartImageBecomesATextureTheRightWayUp()
        {
            var img = new ChartImage(4, 3, 0xFFFFFF);
            img.FillRect(0, 0, 4, 1, 0xFF0000);   // the top row red
            var tex = ChartTextures.ToTexture(img);
            try
            {
                Assert.AreEqual(new Color32(255, 0, 0, 255), (Color32)tex.GetPixel(0, 2), "Unity's top row (y = height − 1) is the image's first");
                Assert.AreEqual(new Color32(255, 255, 255, 255), (Color32)tex.GetPixel(0, 0));
            }
            finally { Object.DestroyImmediate(tex); }
        }
    }
}
