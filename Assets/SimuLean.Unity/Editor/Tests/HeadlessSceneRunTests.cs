using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SimuLean.Results;
using UnityEditor.SceneManagement;
using UnityEngine;
using SimuLean.Serialization;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// "Run Headless" on the clock (<see cref="HeadlessSceneRun"/>): source → server → sink extracted from
    /// a temporary scene, run on a worker thread, per-element results table.
    /// </summary>
    public class HeadlessSceneRunTests
    {
        [Test]
        public void SceneRunsHeadlessOnWorkerAndReportsElements()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n) where T : Component => test.Make<T>(n);

                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1f };
                var server = Make<UnityMultiServer>("Server");
                server.capacity = 1;
                server.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0.5f };
                var sink = Make<UnitySink>("Sink");
                src.nextElements = new List<SElement> { server };
                server.nextElements = new List<SElement> { sink };

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                cfg.MaxSimulationTime = 1000;
                cfg.UseFixedSeed = true;
                cfg.Seed = 7;

                var run = HeadlessSceneRun.Start(cfg, seriesInterval: 100);
                Assert.IsTrue(run.Completion.Wait(System.TimeSpan.FromSeconds(30)), "the run ends within 30 s");

                Assert.AreEqual(HeadlessSceneRun.RunState.Finished, run.State, run.Error?.ToString());
                Assert.AreEqual(1000, run.SimTime, 1e-9, "clock left at max time");
                Assert.AreEqual(1.0, run.Progress, 1e-9);
                var elements = run.Results.Of(ResultScope.Element).ToList();
                Assert.AreEqual(3, elements.Count, "one result per element");
                var sinkStats = run.Results.Find(ResultScope.Element, "Sink");
                // An arrival every 1 (t = 1..1000), service 0.5: all but possibly the last reach the sink.
                Assert.That(sinkStats[ResultMetrics.Input], Is.InRange(998, 1000), "sink input");
                var serverStats = run.Results.Find(ResultScope.Element, "Server");
                Assert.AreEqual(0.5, serverStats[ResultMetrics.StayAvg], 1e-9, "average stay = service time");
                Assert.IsTrue(serverStats.Metrics.Any(m => ResultMetrics.IsState(m.Key)), "state shares");
                Assert.AreEqual(1000, run.Results.Info.EndTime, 1e-9);
                Assert.AreEqual(7, run.Results.Info.Seed);
                var sinkIn = run.Results.Series.Single(x => x.Name == "Sink" && x.Metric == ResultMetrics.Input);
                Assert.AreEqual(10, sinkIn.Times.Count, "a point every 100 over the progress slices");
                Assert.AreEqual(sinkStats[ResultMetrics.Input], sinkIn.Values.Sum(), 1e-9, "the series add up to the run");
                Assert.AreSame(run.ResultSet, ResultsOutput.Last, "the run is the last results (Results tab)");
            }
        }
    }
}
