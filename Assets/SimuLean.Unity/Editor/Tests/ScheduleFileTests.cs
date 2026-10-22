using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SimuLean.Serialization;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// A Schedule Source linked to a file Data Table reads the
    /// file when the run starts (<c>readFileAtRun</c>), or warns that the file is newer than the copy loaded in the
    /// inspector — checked on the extraction every run starts from.
    /// </summary>
    public class ScheduleFileTests
    {
        private static void WriteTable(string path, int rows, System.DateTime written)
        {
            File.WriteAllLines(path, new[] { "Time,Name,Q" }.Concat(Enumerable.Range(0, rows).Select(i => $"{i},Src,1")));
            File.SetLastWriteTimeUtc(path, written);
        }

        private static int Delivered(SimulationConfig config)
        {
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(config);
            int n = 0;
            model.GetAllElements().Values.First(e => e.GetName() == "Sink").ItemEntered += (el, it) => n++;
            model.StartModel();
            clock.AdvanceClock(100);
            return n;
        }

        private static string Database => ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot);

        /// <summary>A Schedule Source → Sink model whose arrivals are the file data table "Arrivals".</summary>
        private static UnityDataTable BuildModel(GameObject root, string path)
        {
            var tableGo = new GameObject("Arrivals");
            tableGo.transform.SetParent(root.transform);
            var table = tableGo.AddComponent<UnityDataTable>();
            table.source = DataSourceKind.File;
            table.fileName = path;
            var srcGo = new GameObject("Src");
            srcGo.transform.SetParent(root.transform);
            var src = srcGo.AddComponent<UnitySource>();
            src.mode = SourceMode.Schedule;
            src.dataTable = table;
            var sinkGo = new GameObject("Sink");
            sinkGo.transform.SetParent(root.transform);
            var sink = sinkGo.AddComponent<UnitySink>();
            src.nextElements = new List<SElement> { sink };
            return table;
        }

        /// <summary>After a run (the component bound to its engine source), a run started again uses the
        /// table reloaded in the inspector, not the previous run's.</summary>
        [Test]
        public void ARestartUsesTheReloadedTable()
        {
            string path = Path.Combine(Application.temporaryCachePath, "cr34_schedule.csv");
            var root = new GameObject("Model") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                WriteTable(path, 2, new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));
                var table = BuildModel(root, path);
                table.Load(Database);
                SceneModelBuilder.Build(new SimClock(1), new List<SElement>(), root);   // a run: the component is bound

                WriteTable(path, 4, new System.DateTime(2026, 2, 1, 0, 0, 0, System.DateTimeKind.Utc));
                table.Load(Database);                                                  // reloaded in the inspector
                Assert.AreEqual(4, Delivered(SceneExtractor.Extract(root).Config), "the next run uses the reloaded table");
            }
            finally
            {
                Object.DestroyImmediate(root);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void TheRunReadsTheFileOrWarnsThatTheCopyIsStale()
        {
            string path = Path.Combine(Application.temporaryCachePath, "s6_schedule.csv");
            var root = new GameObject("Model") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                WriteTable(path, 2, new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));
                var table = BuildModel(root, path);

                table.Load(Database);
                Assert.AreEqual(2, table.Rows["Time"].Count, "the copy loaded in the inspector");
                Assert.AreEqual(2, Delivered(SceneExtractor.Extract(root).Config), "file unchanged: no warning, the copy");

                // the file changes after the load
                WriteTable(path, 5, new System.DateTime(2026, 2, 1, 0, 0, 0, System.DateTimeKind.Utc));
                LogAssert.Expect(LogType.Warning, new Regex("changed after it was loaded"));
                Assert.AreEqual(2, Delivered(SceneExtractor.Extract(root).Config), "off: the loaded copy, with a warning");

                table.readFileAtRun = true;
                Assert.AreEqual(5, Delivered(SceneExtractor.Extract(root).Config), "on: the file as it is now");
                Assert.AreEqual(2, table.Rows["Time"].Count, "the scene's copy is not changed by a run");

                // a study of the scene varies the table's file (data:<table>.file), one file per level
                string other = Path.Combine(Application.temporaryCachePath, "s6_schedule_b.csv");
                WriteTable(other, 3, new System.DateTime(2026, 3, 1, 0, 0, 0, System.DateTimeKind.Utc));
                try
                {
                    var config = SceneExtractor.Extract(root).Config;
                    Assert.AreEqual("Arrivals", config.Elements.Find(e => e.Name == "Src").Parameters["dataTable"], "the source names its table");
                    Assert.AreEqual(path, config.Tables.Find(t => t.Name == "Arrivals").File, "the file goes to the config");
                    var set = SceneStudy.Of(root).Factor("data:Arrivals.file", path, other).Metric("out", "Sink.input").WithRunTime(100).Run();
                    Assert.AreEqual(5, set.Runs[0].Metrics["out"], "level 1: the 5-row file");
                    Assert.AreEqual(3, set.Runs[1].Metrics["out"], "level 2: the 3-row file");
                }
                finally { File.Delete(other); }

                // read at run with the file gone: the extraction fails naming the table (a data table has no copy fallback)
                File.Delete(path);
                var ex = Assert.Throws<System.FormatException>(() => SceneExtractor.Extract(root));
                StringAssert.Contains("'Arrivals'", ex.Message);
            }
            finally
            {
                Object.DestroyImmediate(root);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
