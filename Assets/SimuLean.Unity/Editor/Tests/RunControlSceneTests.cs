using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SimuLean.Results;
using SimuLean.Serialization;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Run control in the scene: the clock's end condition goes to the config and the scene's
    /// headless run stops there; an experiment in small — buffer capacity × welders (members with a label) ×
    /// release plan (a source table per file) — in one call (<see cref="SceneStudy"/>), with the model metrics.
    /// </summary>
    public class RunControlSceneTests
    {
        [Test]
        public void EndConditionAndStudyInOneCall()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            string dir = Path.Combine(Path.GetTempPath(), "simulean_runcontrol_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                T Make<T>(string n) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    return go.AddComponent<T>();
                }
                string Plan(string file, params double[] times)
                {
                    var lines = new List<string> { "Time,Name,Q" };
                    foreach (double t in times) lines.Add($"{t.ToString(System.Globalization.CultureInfo.InvariantCulture)},Plates,1");
                    string f = Path.Combine(dir, file); File.WriteAllLines(f, lines); return f;
                }
                string push = Plan("push.csv", 0, 0, 0, 0, 0, 0, 0, 0), pull = Plan("pull.csv", 0, 10, 20, 30, 40, 50, 60, 70);

                var clock = Make<UnitySimClock>("Clock");
                clock.MaxTime = 1000;
                clock.StopWhenEmpty = true;
                var plates = Make<UnitySource>("Plates");
                plates.mode = SourceMode.Schedule;
                plates.SetArrivalsTable(TableFiles.Read(push));
                var yard = Make<UnityQueue>("Yard");
                yard.capacity = 4;
                var weld = Make<UnityMultiServer>("Weld");
                weld.capacity = 8;
                weld.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 12f };
                var crew = Make<UnityResourceTeam>("Crew");
                crew.travel = TeamTravel.Abstract;   // no walking (a team now travels straight by default)
                crew.members = new List<ResourceMemberSpec>
                {
                    new ResourceMemberSpec { name = "fitter", properties = new List<string> { "skill = 'fitting'" } },
                    new ResourceMemberSpec { name = "welder", properties = new List<string> { "skill = 'welding'" } },
                };
                weld.processBy.team = crew;
                weld.processBy.requirements = new List<ResourceRequirementSpec> { new ResourceRequirementSpec { count = 1, query = new SimuLean.Collections.ListQuerySpec { conditions = { new SimuLean.Collections.ListCondition("skill", SimuLean.Collections.ListOp.Equal, "welding") } } } };
                var sink = Make<UnitySink>("Erected");
                plates.nextElements = new List<SElement> { yard };
                yard.nextElements = new List<SElement> { weld };
                weld.nextElements = new List<SElement> { sink };
                clock.StopAt.Add(sink);
                clock.StopAtCount = 8;

                // the end condition goes to the config, and the scene's headless run honours it
                var config = SceneExtractor.Extract(root).Config;
                Assert.AreEqual(8, config.StopAtCount);
                Assert.IsTrue(config.StopAt.Count == 1 && config.StopAt[0].StartsWith("UnitySink_Erected_"), "the end elements by their element ids");
                Assert.IsTrue(config.StopWhenEmpty);

                // an experiment in small, in one call
                var set = SceneStudy.Of(root)
                    .Factor("Yard.capacity", 1, 4)
                    .Factor("team:Crew.members[skill=welding]", 1, 2)
                    .Factor("Plates.table", push, pull)
                    .Metric("makespan", "model.makespan").Metric("erected", "Erected.input").Metric("welders", "team:Crew.members")
                    .WithReplications(2).Run();
                Assert.AreEqual(8, set.Scenarios.Count, "2 × 2 × 2 scenarios");
                Assert.AreEqual(16, set.Runs.Count, "× 2 replications");
                Assert.IsTrue(set.Runs.All(r => r.Metrics["erected"] == 8), "every run erects the 8 blocks and stops there");
                double Makespan(string welders, string plan) => set.Runs.Where(r => r.Scenario.Contains("members[skill=welding]=" + welders) && r.Scenario.EndsWith("=" + plan)).Average(r => r.Metrics["makespan"]);
                Assert.AreEqual(96, Makespan("1", "push"), 1e-9, "one welder: 8 × 12");
                Assert.AreEqual(48, Makespan("2", "push"), 1e-9, "two welders: half");
                Assert.AreEqual(82, Makespan("2", "pull"), 1e-9, "the pull plan releases the last plate at 70: 70 + 12");
                Assert.IsTrue(set.Runs.Where(r => r.Scenario.Contains("=2 ·")).All(r => r.Metrics["welders"] == 3), "2 welders + the fitter");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }
            /// <summary>The clock's "stop when no progress for" goes to the config and the built model honours it — a
        /// dead end beside a periodic clock signal (the signal keeps the run from ever being idle) ends T after the last
        /// movement with the end reason NoProgress.</summary>
        [Test]
        public void NoProgressEndConditionExtractsAndBuilds()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool temporary = !string.IsNullOrEmpty(active.path);
            var scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            var root = new GameObject("Model");
            string dir = Path.Combine(Path.GetTempPath(), "simulean_noprogress_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                T Make<T>(string n) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    return go.AddComponent<T>();
                }
                string plan = Path.Combine(dir, "plan.csv");
                File.WriteAllLines(plan, new[] { "Time,Name,Q", "0,Plates,1", "0,Plates,1", "0,Plates,1", "0,Plates,1", "0,Plates,1", "0,Plates,1" });
                var clock = Make<UnitySimClock>("Clock");
                clock.MaxTime = 1000;
                clock.StopWhenNoProgressFor = 50;
                var plates = Make<UnitySource>("Plates");
                plates.mode = SourceMode.Schedule;
                plates.SetArrivalsTable(TableFiles.Read(plan));
                var weld = Make<UnityMultiServer>("Weld");
                weld.capacity = 1;
                weld.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 12f };
                var yard = Make<UnityQueue>("Yard");   // a dead end of 2: nothing takes from it
                yard.capacity = 2;
                plates.nextElements = new List<SElement> { weld };
                weld.nextElements = new List<SElement> { yard };

                var config = SceneExtractor.Extract(root).Config;
                Assert.AreEqual(50, config.StopWhenNoProgressFor, 1e-12, "the clock's field goes to the config");
                Assert.AreEqual(config.StopWhenNoProgressFor, SimuLean.Serialization.ConfigJson.Deserialize(SimuLean.Serialization.ConfigJson.Serialize(config)).StopWhenNoProgressFor,
                                1e-12, "and through the model file");
                var tick = new ReactionConfig("tick");   // a periodic clock signal: its ticks are foreground events
                tick.Set("signal", "Every");
                tick.Set("period", 7.0);
                config.Reactions.Add(tick);

                var simClock = new SimClock(1);
                var model = new SimuLean.Headless.HeadlessModelFactory(simClock);
                model.BuildModel(config);
                model.StartModel();
                simClock.AdvanceClock(config.MaxSimulationTime);
                var r = RunResults.Capture(model);
                double last = SimuLean.Headless.RunMonitor.LastMovement(model.GetAllElements().Values);
                Assert.AreEqual("NoProgress", r.Info.EndReason, "the run ends for lack of progress");
                Assert.AreEqual(last + 50, simClock.GetSimulationTime(), 1e-9, "50 after the last movement");
                Assert.IsTrue(r.Info.Waiting.Any(w => w.StartsWith("Weld: BLOCKED")), string.Join(" | ", r.Info.Waiting));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
                if (temporary) EditorSceneManager.CloseScene(scene, true);
                else Object.DestroyImmediate(root);
            }
        }
}
}
