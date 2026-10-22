using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SimuLean.Serialization;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// A small model built only through the SimuLean pipeline commands (place, set, connect, team)
    /// and run headless through them gives the same result as the scene extracted and built directly; overrides by catalog
    /// key make scenarios; the model's elements are found by display name.
    /// </summary>
    public class PipelineCommandsTests
    {
        // every command answers a JSON object (its fields plus errors / warnings)
        private static JToken Get(object o, string member) => ((JObject)o)[member];

        /// <summary>A metric's mean in a scenario of a run answer.</summary>
        internal static double MetricMean(JToken scenario, string metric)
        {
            var m = scenario["metrics"][metric];
            return m is JObject o ? (double)o["mean"] : (double)m;
        }

        [Test]
        public void AModelBuiltAndRunThroughTheCommands()
        {
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            if (temporary) SceneManager.SetActiveScene(scene);
            var root = new GameObject("S3Model");
            SceneManager.MoveGameObjectToScene(root, scene);
            // the studies are kept in a results store of their own, never in the project database
            ResultsOutput.StoreOverride = System.IO.Path.GetFullPath($"Temp/simulean_store_test_{System.Guid.NewGuid():N}.db");
            try
            {
                // self-describing
                var types = (JArray)Get(SimuLeanCommands.Types(), "types");
                Assert.IsTrue(types.Any(t => (string)t["component"] == "UnityMultiServer"), "the types list the elements");
                var queueInfo = SimuLeanCommands.Types("UnityQueue");
                Assert.IsNotNull(Get(queueInfo, "fields"));

                SimuLeanCommands.AddElement("UnitySource", "S3Src", 0, 0, 0, "S3Model");
                SimuLeanCommands.AddElement("UnityQueue", "S3Buffer", 2, 0, 0, "S3Model");
                SimuLeanCommands.AddElement("UnityMultiServer", "S3Station", 4, 0, 0, "S3Model");
                SimuLeanCommands.AddElement("UnitySink", "S3Sink", 6, 0, 0, "S3Model");
                SimuLeanCommands.Set("S3Src", "mode", "InterArrival");
                SimuLeanCommands.Set("S3Src", "interArrivalDistribution.strategyType", "Constant");
                SimuLeanCommands.Set("S3Src", "interArrivalDistribution.constantValue", "1");
                SimuLeanCommands.Set("S3Buffer", "capacity", "100");
                SimuLeanCommands.Set("S3Station", "capacity", "1");
                SimuLeanCommands.Set("S3Station", "delayStrategy.strategyType", "Constant");
                SimuLeanCommands.Set("S3Station", "delayStrategy.constantValue", "2");
                SimuLeanCommands.Connect("S3Src", "S3Buffer");
                SimuLeanCommands.Connect("S3Buffer", "S3Station");
                SimuLeanCommands.Connect("S3Station", "S3Sink");
                var team = SimuLeanCommands.AddTeam("S3Crew", 2, "", 0, 0, 3, "S3Model");
                SimuLeanCommands.Set("S3Station", "processBy.team", "S3Crew");
                CollectionAssert.AreEqual(new[] { "S3Crew_1", "S3Crew_2" }, Get(team, "members").Select(m => (string)m).ToList(),
                                          "members named after the team");
                const string doctor = "Assets/SimuLean.Unity/Prefabs/ResourceMembers/Doctor.prefab";
                SimuLeanCommands.Set("S3Crew", "memberPrefab", doctor);
                Assert.AreSame(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(doctor),
                               GameObject.Find("S3Crew").GetComponent<UnityResourceTeam>().memberPrefab, "an object field takes an asset path");
                SimuLeanCommands.AddElement("Pathway Arrows", "S3Arrows", 0, 0, 0, "S3Model");
                Assert.IsNotNull(GameObject.Find("S3Arrows").GetComponent<UnityPathwayArrows>(), "a tool of another tab (Visualization) is placed too");
                Assert.IsNotNull(Get(SimuLeanCommands.Types("Element Label"), "fields"), "and described");
                SimuLeanCommands.AddElement("Dashboard", "S3Dash", 0, 0, 0, "S3Model");
                SimuLeanCommands.Set("S3Dash", "chartSize", "340, 210");
                Assert.AreEqual(new Vector2(340, 210), GameObject.Find("S3Dash").GetComponent<UnityDashboard>().chartSize, "a Vector2 by text");

                var p = SimuLeanCommands.Params("S3Station");
                Assert.AreEqual("1", (string)Get(p, "catalog")["capacity"], "the catalog keys of the element");

                // the same model extracted and built directly; elements by display name
                var config = SceneExtractor.Extract(root).Config;
                var clock = new SimClock(1);
                var model = new SimuLean.Headless.HeadlessModelFactory(clock);
                model.BuildModel(config);
                var sink = model.Find("S3Sink");
                Assert.IsNotNull(sink, "found by display name (the id is Type_Name_instanceId)");
                int delivered = 0;
                sink.ItemEntered += (el, it) => delivered++;
                model.StartModel();
                clock.AdvanceClock(20);

                var runs = SimuLeanCommands.RunHeadless(20, 1, 1, "S3Station.capacity=1|2", "out=S3Sink.input", "S3Model", "full");
                var scenarios = (JArray)Get(runs, "scenarios");
                Assert.AreEqual(2, scenarios.Count, "one scenario per level");
                Assert.AreEqual(0, ((JArray)Get(runs, "errors")).Count, "no error");
                double Out(JToken s) => MetricMean(s, "out");
                Assert.AreEqual(delivered, Out(scenarios[0]), 1e-9, "capacity 1: the direct build's result");
                Assert.Greater(Out(scenarios[1]), Out(scenarios[0]), "capacity 2 delivers more");
                Assert.IsTrue(((JArray)scenarios[0]["resources"]).Any(), "the team's members are reported");
                Assert.AreEqual(1, (int)scenarios[0]["runs"]["ok"], "the runs that completed");

                // with metrics, the default detail is the metrics and the model block only (follow-up #30)
                var small = Get(SimuLeanCommands.RunHeadless(20, 1, 1, "", "out=S3Sink.input", "S3Model"), "scenarios")[0];
                Assert.IsNull(small["elements"], "detail=metrics: no element KPIs");
                Assert.IsNotNull(small["model"], "the model block stays");
                Assert.AreEqual(delivered, MetricMean(small, "out"), 1e-9);

                // the same study in the background, polled until done, gives the same answer
                SimuLeanCommands.RunStart(20, 1, 1, "", "out=S3Sink.input", "S3Model");
                object status = null;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (watch.Elapsed.TotalSeconds < 60)
                {
                    status = SimuLeanCommands.RunStatus().Result;   // wait 0: answered at once
                    if ((string)Get(status, "status") != "running") break;
                    System.Threading.Thread.Sleep(20);
                }
                Assert.AreEqual("completed", (string)Get(status, "status"));
                var bg = Get(status, "result")["scenarios"][0];
                Assert.AreEqual(delivered, MetricMean(bg, "out"), 1e-9, "the background answer");

                // a study of the MCP is published (the Results tab shows it) and never stored (G1b stored it)
                var store = ResultsOutput.Store(ResultsOutput.ProjectRoot);
                Assert.AreEqual(0, store.List().Count, "the studies are not stored");
                Assert.IsNotNull(ResultsOutput.Last, "the last study is published");
                Assert.AreEqual(scene.name, ResultsOutput.Last.Model, "the scene is the model");
                Assert.AreEqual(0, ResultsOutput.Last.StoreId, "not stored: no id");
                int id = ResultsOutput.Save(ResultsOutput.Last, ResultsOutput.ProjectRoot);
                var reloaded = store.Load(id);
                Assert.AreEqual(ResultsOutput.Last.Runs.Count, reloaded.Runs.Count, "Save keeps it on request");

                // the fields of the active distribution only
                var paths = Get(p, "fields").Select(f => (string)f["path"]).ToList();
                CollectionAssert.Contains(paths, "delayStrategy.constantValue");
                CollectionAssert.DoesNotContain(paths, "delayStrategy.uniformMin", "a Constant delay hides the other distributions' fields");

                // simulean_types reports what the Library places (its prefab), and simulean_set warns about a
                // field of an inactive distribution
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/SimuLean.Unity/Prefabs/Elements/Multiserver.prefab");
                if (prefab != null)
                {
                    string placed = new UnityEditor.SerializedObject(prefab.GetComponent<UnityMultiServer>()).FindProperty("delayStrategy.strategyType").enumDisplayNames[
                        new UnityEditor.SerializedObject(prefab.GetComponent<UnityMultiServer>()).FindProperty("delayStrategy.strategyType").enumValueIndex];
                    var described = Get(SimuLeanCommands.Types("UnityMultiServer"), "fields").First(f => (string)f["path"] == "delayStrategy.strategyType");
                    Assert.AreEqual(placed.Replace(" ", ""), (string)described["value"], "the prefab's distribution");
                }
                Assert.AreEqual(1, ((JArray)Get(SimuLeanCommands.Set("S3Station", "delayStrategy.uniformMin", "1"), "warnings")).Count, "a Uniform field while Constant is active: warned");
                Assert.AreEqual(0, ((JArray)Get(SimuLeanCommands.Set("S3Station", "delayStrategy.constantValue", "2"), "warnings")).Count, "the active distribution's field: no warning");
            }
            finally
            {
                ResultsOutput.StoreOverride = null;
                if (temporary)
                {
                    if (active.IsValid()) SceneManager.SetActiveScene(active);
                    EditorSceneManager.CloseScene(scene, true);
                }
                else Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ItemsWalkOnANetworkBuiltThroughTheCommands()
        {
            // Nodes, a track (connect between two nodes), locations and walkBy, all by the commands; the
            // extracted model walks 12 m at 1.5 m/s (time unit 1 s) from the source to the sink: 8 s in the item's age
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            if (temporary) SceneManager.SetActiveScene(scene);
            var root = new GameObject("WalkModel");
            SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                SimuLeanCommands.AddElement("Transport Network", "WalkNet", 0, 0, 0, "WalkModel");
                SimuLeanCommands.AddElement("Transport Node", "WalkA", 0, 0, 0, "WalkNet");
                SimuLeanCommands.AddElement("Transport Node", "WalkB", 12, 0, 0, "WalkNet");
                var track = SimuLeanCommands.Connect("WalkA", "WalkB");
                Assert.AreEqual(12f, (float)Get(track, "length"), 1e-4f, "a track between two nodes");
                SimuLeanCommands.AddElement("UnitySource", "WalkSrc", 0, 0, 0, "WalkModel");
                SimuLeanCommands.AddElement("UnitySink", "WalkSink", 12, 0, 0, "WalkModel");
                SimuLeanCommands.Set("WalkSrc", "mode", "InterArrival");
                SimuLeanCommands.Set("WalkSrc", "interArrivalDistribution.strategyType", "Constant");
                SimuLeanCommands.Set("WalkSrc", "interArrivalDistribution.constantValue", "20");
                SimuLeanCommands.Set("WalkSrc", "location", "WalkA");
                SimuLeanCommands.Set("WalkSink", "location", "WalkB");
                SimuLeanCommands.Set("WalkSrc", "walkBy.speed", "1.5");
                SimuLeanCommands.Set("WalkSink", "tallies", "walk = age");
                SimuLeanCommands.Connect("WalkSrc", "WalkSink");

                var config = SceneExtractor.Extract(root).Config;
                var src = config.Elements.First(e => e.Name == "WalkSrc");
                Assert.AreEqual(1.5, src.GetParameter<double>(ResourceUseKeys.WalkSpeed, 0), 1e-6, "walkSpeed extracted");
                var clock = new SimClock(1);
                var model = new SimuLean.Headless.HeadlessModelFactory(clock);
                model.BuildModel(config);
                Assert.AreEqual(1, model.WalkLinks.Count, "the source's items walk");
                model.StartModel();
                clock.AdvanceClock(100);
                var sink = model.Find("WalkSink");
                Assert.GreaterOrEqual(sink.GetStatsInputCount(), 4.0);
                Assert.AreEqual(8.0, sink.StatsCollector.Tallies.Get("walk").Mean, 1e-6, "12 m at 1.5 m/s on foot");
            }
            finally
            {
                if (temporary)
                {
                    if (active.IsValid()) SceneManager.SetActiveScene(active);
                    EditorSceneManager.CloseScene(scene, true);
                }
                else Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ATableThatCannotBeLoadedLeavesTheSceneAsItWas()
        {
            // A missing file left the source switched to Schedule mode with a new, empty data table
            var active = SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            if (temporary) SceneManager.SetActiveScene(scene);
            var root = new GameObject("S40Model");
            SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                SimuLeanCommands.AddElement("UnitySource", "S40Src", 0, 0, 0, "S40Model");
                SimuLeanCommands.Set("S40Src", "mode", "InterArrival");
                int tablesBefore = UnityCompat.FindAll<UnityDataTable>().Length;
                Assert.Throws<System.ArgumentException>(() => SimuLeanCommands.LoadTable("S40Src", "no_such_file_" + System.Guid.NewGuid().ToString("N") + ".csv"));
                var src = GameObject.Find("S40Src").GetComponent<UnitySource>();
                Assert.AreEqual(SourceMode.InterArrival, src.mode, "the source keeps its mode");
                Assert.IsNull(src.dataTable, "no table linked");
                Assert.AreEqual(tablesBefore, UnityCompat.FindAll<UnityDataTable>().Length, "no table object left behind");
            }
            finally
            {
                if (temporary)
                {
                    if (active.IsValid()) SceneManager.SetActiveScene(active);
                    EditorSceneManager.CloseScene(scene, true);
                }
                else Object.DestroyImmediate(root);
            }
        }
    }
}
