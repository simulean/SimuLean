using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SimuLean.Optimization;
using SimuLean.Results;
using SimuLean.Testing;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The out-of-editor runner from the editor the same study run in the editor (Unity's Mono) and
    /// in the runner (.NET, <see cref="ExternalRunner"/>) gives the same result set — every run's metrics, every object's
    /// statistics and the time series. Measured: the event sequences are the same (every statistic bit for bit);
    /// a value derived by floating-point arithmetic may differ in its last bit (one time-series point of operator_pool,
    /// 1 ulp), so a relative difference up to 1e-12 is accepted. Inconclusive when this machine has no runner yet (it is
    /// built on first use when the project has the runner's sources and a .NET 10 SDK); SimuLeanTestRunner builds it
    /// before a run that includes these tests, and reports a runner that cannot be built as a failure.
    /// </summary>
    public class ExternalRunnerTests
    {
        static IEnumerable<string> Models() => new[] { "basic_line", "operator_pool", "downtime_mtbf", "transport", "storage_stock_list_pull", "milk_run_shuttle_line" };

        static ExternalRunner.Executor Runner()
        {
            var executor = ExternalRunner.Prepare(force: true);
            if (executor.RunnerPath == null) Assert.Inconclusive("No runner on this machine yet: " + executor.Note);
            return executor;
        }

        [TestCaseSource(nameof(Models))]
        public void TheRunnerGivesTheEditorsResultsBitForBit(string name)
        {
            var model = ReferenceModels.All().First(m => m.Key == name).Value;
            var study = new Study(model) { Threads = 2, SeriesInterval = model.MaxSimulationTime / 20 }.WithReplications(3).WithSeed(11);
            study.Metric("end", "model.end_time");
            var batch = study.Batch();
            var inEditor = batch.Run();
            var executor = Runner();
            var outside = executor.Run(StudyBatch.Deserialize(StudyBatch.Serialize(batch)));
            Assert.AreEqual("runner", executor.RanIn, executor.Note);

            Assert.AreEqual(inEditor.Runs.Count, outside.Runs.Count, "runs");
            var differences = new List<string>();
            for (int i = 0; i < inEditor.Runs.Count; i++)
            {
                var a = inEditor.Runs[i];
                var b = outside.Runs[i];
                Assert.AreEqual(a.Id, b.Id, "the same runs in the same order");
                foreach (var kv in a.Metrics)
                    if (!b.Metrics.TryGetValue(kv.Key, out double v) || !Same(kv.Value, v)) differences.Add($"{a.Id} {kv.Key}: {kv.Value:R} / {v:R}");
                foreach (var o in a.Results.Objects)
                {
                    var p = b.Results.Find(o.Scope, o.Id);
                    if (p == null) { differences.Add($"{a.Id} {o.Scope}:{o.Id} missing"); continue; }
                    foreach (var st in o.Metrics)
                        if (!p.TryGet(st.Key, out double v) || !Same(st.Value, v)) differences.Add($"{a.Id} {o.Name}.{st.Key}: {st.Value:R} / {v:R}");
                }
                for (int s = 0; s < a.Results.Series.Count; s++)
                {
                    var sa = a.Results.Series[s];
                    var sb = b.Results.Series[s];
                    if (sa.Name != sb.Name || sa.Metric != sb.Metric || sa.Values.Count != sb.Values.Count) { differences.Add($"{a.Id} series {sa.Name}.{sa.Metric}"); continue; }
                    for (int k = 0; k < sa.Values.Count; k++)
                        if (!Same(sa.Times[k], sb.Times[k]) || !Same(sa.Values[k], sb.Values[k]))
                            differences.Add($"{a.Id} series {sa.Name}.{sa.Metric}[{k}]: {sa.Values[k]:R} / {sb.Values[k]:R}");
                }
            }
            Assert.IsEmpty(differences, $"{differences.Count} values differ between the editor and the runner:\n" + string.Join("\n", differences.Take(20)));
        }

        [Test]
        public void NonAsciiNamesComeBackFromTheRunnerIntact()
        {
            var model = ReferenceModels.All().First(m => m.Key == "basic_line").Value;
            const string name = "Café · → ×";
            model.Elements.Last().Name = name;   // connections go by id: a new name is enough
            var study = new Study(model) { Threads = 1 }.WithReplications(1).WithSeed(1);
            study.Metric("end", "model.end_time");
            var executor = Runner();
            var outside = executor.Run(StudyBatch.Deserialize(StudyBatch.Serialize(study.Batch())));
            Assert.AreEqual("runner", executor.RanIn, executor.Note);
            CollectionAssert.Contains(outside.Runs[0].Results.Objects.Select(o => o.Name).ToList(), name,
                                      "the runner's console code page must not garble the names on their way back");
        }

        // bit for bit (NaN = NaN), or a last-bit difference of floating-point arithmetic (Mono / CoreCLR)
        private static bool Same(double a, double b) => a.Equals(b) || System.Math.Abs(a - b) <= 1e-12 * System.Math.Max(System.Math.Abs(a), System.Math.Abs(b));

        [Test]
        public void AnOptimisationThroughTheRunnerMakesTheSameSearch()
        {
            SimulationObjective Objective() => new SimulationObjective(ReferenceModels.ResourceSharedTeam(),
                new ModelParameter[] { ConfigParameter.Of(ReferenceModels.ResourceSharedTeam(), ParameterScope.Team, "team", ModelParameterCatalog.TeamMembers, 1, 6) },
                new[] { new ModelMetric("done", MetricScope.Element, "sink", "input"), new ModelMetric("ops", MetricScope.Team, "team", "members") },
                "done - 100 * ops") { Replications = 2, RunTime = 800 };
            OptimizationSettings Settings() => new OptimizationSettings { MaxEvaluations = 16, Parallelism = 2, Seed = 3 };
            var classic = OptimizationEngine.Run(Objective().BuildSpace(), Objective(), new GeneticOptimizer { PopulationSize = 6, Generations = 3 }, Settings());
            var executor = Runner();
            var objective = Objective();
            var outside = OptimizationEngine.Run(objective.BuildSpace(), new BatchedObjective(objective, (b, t) => executor.Run(b, t)),
                                                new GeneticOptimizer { PopulationSize = 6, Generations = 3 }, Settings());
            Assert.AreEqual("runner", executor.RanIn, executor.Note);
            Assert.AreEqual(classic.Candidates.Count, outside.Candidates.Count, "evaluations");
            for (int i = 0; i < classic.Candidates.Count; i++)
            {
                var a = classic.Candidates[i];
                var b = outside.Candidates[i];
                CollectionAssert.AreEqual(a.Values, b.Values, $"candidate {i}: the same search");
                Assert.IsTrue(Same(a.Evaluation.Fitness, b.Evaluation.Fitness), $"candidate {i}: fitness {a.Evaluation.Fitness:R} / {b.Evaluation.Fitness:R}");
            }
        }

        [Test]
        public void TheClocksRunHeadlessGivesTheSameRunOutside()
        {
            var config = ReferenceModels.All().First(m => m.Key == "operator_pool").Value.Clone();
            config.UseFixedSeed = true;
            config.Seed = 21;
            var executor = Runner();
            var inEditor = HeadlessSceneRun.Start(config, 50, true, new ExternalRunner.Executor());
            var outside = HeadlessSceneRun.Start(config, 50, true, executor);
            Assert.IsTrue(System.Threading.Tasks.Task.WaitAll(new[] { inEditor.Completion, outside.Completion }, System.TimeSpan.FromSeconds(60)),
                          "both runs end within 60 s");
            Assert.AreEqual(HeadlessSceneRun.RunState.Finished, inEditor.State, inEditor.Error?.Message);
            Assert.AreEqual(HeadlessSceneRun.RunState.Finished, outside.State, outside.Error?.Message);
            Assert.AreEqual("editor", inEditor.RanIn);
            StringAssert.StartsWith("runner", outside.RanIn);
            foreach (var o in inEditor.Results.Objects)
            {
                var p = outside.Results.Find(o.Scope, o.Id);
                Assert.IsNotNull(p, o.Id);
                foreach (var st in o.Metrics)
                    Assert.IsTrue(p.TryGet(st.Key, out double v) && Same(st.Value, v), $"{o.Name}.{st.Key}: {st.Value:R} / {v:R}");
            }
            Assert.AreEqual(inEditor.Results.Series.Count, outside.Results.Series.Count, "series");
            Assert.AreEqual(inEditor.Results.Timeline.Count, outside.Results.Timeline.Count, "timeline");
            Assert.AreEqual(21, outside.Results.Info.Seed, "the model's seed");
        }

        [Test]
        public void TheRunnerAnswersWithTheEditorsEngine()
        {
            var executor = Runner();
            Assert.That(executor.RunnerPath, Does.Contain(ExternalRunner.Fingerprint(ResultsOutput.ProjectRoot)), "the runner of this engine version");
        }
    }
}
