using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The results of the example scenes that no other test checks by name each scene runs headless with a
    /// fixed seed (<see cref="SceneStudy"/>, as the SimuLean window does), without errors, deadlock or stall, every station
    /// processes items and every sink receives some; the numbers of the scene's card (seeded, or exact when every time is
    /// constant) are checked too. <c>Factory_3DModels</c> is a gallery: only the smoke check.
    /// </summary>
    public class ExampleResultTests
    {
        private const string Library = "Assets/Scenes/LibraryExamples/";

        // the element types that process items (a station); sources, queues, storage and transport are not checked
        private static readonly HashSet<string> Stations = new HashSet<string>
            { "MultiServer", "Combiner", "Separator", "PickingStation", "LineSideUnpacker", "ErectionDock" };

        /// <summary>Runs <paramref name="path"/> headless for <paramref name="horizon"/> (seeds <paramref name="seed"/>..),
        /// checks the smoke conditions on every run and returns the runs with the <paramref name="metrics"/> asked.</summary>
        private static List<ResultRun> RunChecked(string path, double horizon, int seed, int replications, params (string name, string metric)[] metrics)
        {
            using (var iso = new IsolatedScene(path))
            {
                var study = SceneStudy.Of(iso.Root).WithSeed(seed).WithReplications(replications).WithRunTime(horizon);
                SimulationConfig cfg = study.Model;
                var checks = new List<(string name, string metric, string what)>();
                int i = 0;
                foreach (var e in cfg.Elements)
                {
                    if (Stations.Contains(e.Type)) checks.Add(("station" + i++, e.Name + ".output", $"station {e.Name} processed items"));
                    else if (e.Type == "Sink") checks.Add(("sink" + i++, e.Name + ".input", $"sink {e.Name} received items"));
                }
                Assert.IsNotEmpty(checks, "the scene has stations and sinks");
                foreach (var c in checks) study.Metric(c.name, c.metric);
                study.Metric("deadlocked", "model.deadlocked").Metric("stalled", "model.stalled");
                foreach (var m in metrics) study.Metric(m.name, m.metric);

                var runs = study.Run().Runs;
                Assert.AreEqual(replications, runs.Count, "every replication ran");
                foreach (var r in runs)
                {
                    Assert.IsNull(r.Error, $"seed {r.Seed}: {r.Error}");
                    Assert.AreEqual(0, r.Metrics["deadlocked"], $"seed {r.Seed}: no deadlock");
                    Assert.AreEqual(0, r.Metrics["stalled"], $"seed {r.Seed}: no stall");
                    foreach (var c in checks)
                        Assert.Greater(r.Metrics[c.name], 0, $"seed {r.Seed}: {c.what}");
                }
                return runs;
            }
        }

        private static double Mean(List<ResultRun> runs, string metric) => runs.Average(r => r.Metrics[metric]);

        [Test]
        public void AssemblyKittingPacksATotePer20Seconds()
        {
            // constant times: 179 kits in 1 h (the totes, one every 20 s, are the bottleneck)
            var run = RunChecked(Library + "Assembly_Kitting.unity", 3600, 1, 1, ("kits", "Kits.input"), ("util", "KittingBench.utilization"))[0];
            Assert.AreEqual(179, run.Metrics["kits"], "kits (card)");
            Assert.AreEqual(0.597, run.Metrics["util"], 0.001, "bench utilisation (card)");
        }

        [Test]
        public void SetupChangeoverMatchesItsCard()
        {
            // card: seeds 1..3, 40 h, grouped by setup: 1186 out, 399 setups, wait 5.8 min
            var runs = RunChecked(Library + "Setup_Changeover.unity", 2400, 1, 3,
                                  ("out", "Exit.input"), ("setups", "Press.setups"), ("wait", "Buffer.stay_avg"));
            Assert.AreEqual(1186.33, Mean(runs, "out"), 0.01, "items out (card)");
            Assert.AreEqual(399, Mean(runs, "setups"), 0.01, "changeovers (card)");
            Assert.AreEqual(5.83, Mean(runs, "wait"), 0.01, "buffer wait, min (card)");
        }

        [Test]
        public void WorkshopResourcesMatchesItsCard()
        {
            // card: seed 1, 8 h: 288 normal and 62 urgent shipped, 47 preemptions, utilisation 0.81 / 0.24
            var run = RunChecked(Library + "Workshop_Resources.unity", 28800, 1, 1,
                                 ("normal", "Shipped.input"), ("urgent", "Shipped Urgent.input"), ("preemptions", "team:Operators.preemptions"),
                                 ("operators", "team:Operators.utilization"), ("mechanics", "team:Mechanics.utilization"))[0];
            Assert.AreEqual(288, run.Metrics["normal"], "normal orders shipped (card)");
            Assert.AreEqual(62, run.Metrics["urgent"], "urgent orders shipped (card)");
            Assert.AreEqual(47, run.Metrics["preemptions"], "preemptions (card)");
            Assert.AreEqual(0.81, run.Metrics["operators"], 0.005, "operators' utilisation (card)");
            Assert.AreEqual(0.24, run.Metrics["mechanics"], 0.005, "mechanic's utilisation (card)");
        }

        [Test]
        public void HospitalEmergencyDischargesItsPatients()
        {
            // constant times: 156 patients in, 140 discharged in 1000 s (the two boxes are the bottleneck)
            var run = RunChecked(Library + "Hospital_Emergency.unity", 1000, 1, 1, ("in", "Arrivals.output"), ("out", "Discharge.input"))[0];
            Assert.AreEqual(156, run.Metrics["in"], "patients in (card)");
            Assert.AreEqual(140, run.Metrics["out"], "patients discharged (card)");
        }

        [Test]
        public void ShipyardLibraryLaunchesItsShip()
        {
            // the sample hull: 7 sub-blocks (2 turned), 4 blocks erected in sequence, one ship launched at 83.78 s
            var run = RunChecked(Library + "Shipyard_Library.unity", 200, 1, 1,
                                 ("sub", "Sub-Assembly.output"), ("turned", "Turning.output"), ("erected", "Slipway.output"),
                                 ("launched", "Launch.input"), ("makespan", "model.makespan"))[0];
            Assert.AreEqual(7, run.Metrics["sub"], "sub-blocks (card)");
            Assert.AreEqual(2, run.Metrics["turned"], "sub-blocks turned (card)");
            Assert.AreEqual(4, run.Metrics["erected"], "blocks erected (card)");
            Assert.AreEqual(1, run.Metrics["launched"], "ship launched (card)");
            Assert.AreEqual(83.78, run.Metrics["makespan"], 0.01, "launch time (card)");
        }

        [Test]
        public void Factory3DModelsRuns()
        {
            // a gallery, not a study model: the smoke check only
            RunChecked(Library + "Factory_3DModels.unity", 7200, 1, 1);
        }
    }
}
