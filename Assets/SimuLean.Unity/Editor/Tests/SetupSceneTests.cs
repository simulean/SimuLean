using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SimuLean.Headless;
using SimuLean.Resources;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Station setups in a scene: the Setup section of a Multi-Server, a Combiner and a Picking Station is
    /// extracted as the setup keys (nothing with the rule None), the engine builds it from them (one build path) and the
    /// headless run sets up — by the crew's team, the station SETUP, the results' setup statistics.
    /// </summary>
    public class SetupSceneTests
    {
        [Test]
        public void SetupExtractsBuildsAndRunsHeadless()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n) where T : Component => test.Make<T>(n);

                var setters = Make<UnityResourceTeam>("Setters");
                setters.travel = TeamTravel.Abstract;
                setters.AddMembers(1);
                var srcA = Make<UnitySource>("SrcA");
                srcA.mode = SourceMode.InterArrival;
                srcA.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 4f };
                srcA.createLabels = new List<string> { "type = 'A'" };
                var srcB = Make<UnitySource>("SrcB");
                srcB.mode = SourceMode.InterArrival;
                srcB.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 6f };
                srcB.createLabels = new List<string> { "type = 'B'" };
                var queue = Make<UnityQueue>("Queue");
                var press = Make<UnityMultiServer>("Press");
                press.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1f };
                press.setup = new StationSetupConfig
                {
                    when = SetupWhen.LabelChange, label = "type", setupFirst = true,
                    time = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0.5f },
                    setupBy = new SetupResourceUse { team = setters, priority = "2" },
                };
                var plain = Make<UnityMultiServer>("Plain");   // no setup: the rule None exported (a study may switch it)
                plain.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0.2f };
                var sink = Make<UnitySink>("Sink");
                srcA.nextElements = new List<SElement> { queue };
                srcB.nextElements = new List<SElement> { queue };
                queue.nextElements = new List<SElement> { press };
                press.nextElements = new List<SElement> { plain };
                plain.nextElements = new List<SElement> { sink };

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                var pc = cfg.Elements.Single(e => e.Name == "Press");
                Assert.AreEqual("LabelChange", pc.Get<string>("setup.when", ""), "rule exported");
                Assert.AreEqual("type", pc.Get<string>("setup.label", ""), "label exported");
                Assert.IsTrue(pc.Get<bool>("setup.setupFirst", false), "first item exported");
                Assert.AreEqual("Constant~0.5", pc.Get<SimuLean.Serialization.SamplerSettings>("setup.time")?.ToString(), "time exported as a distribution");
                Assert.AreEqual(cfg.ResourceTeams[0].Id, pc.Get<string>("setup.setupBy.team", ""), "the crew's team by its model id");
                Assert.AreEqual("2", pc.Get<string>("setup.setupBy.priority", ""), "crew priority exported");
                var plainCfg = cfg.Elements.Single(e => e.Name == "Plain");
                Assert.AreEqual("None", plainCfg.Get<string>("setup.when", ""), "rule None exported");
                Assert.IsTrue(SceneExtractor.ExtractElement(press).Has("setup.setupBy.team"), "the editor's view has the crew too");
                Assert.AreEqual(0, SimuLean.Optimization.ModelParameterCatalog.Undecided(cfg).Count, "every setup key decided");

                RunResults Run(out HeadlessModelFactory model)
                {
                    var clock = new SimClock(1);
                    model = new HeadlessModelFactory(clock);
                    model.BuildModel(cfg);
                    model.StartModel();
                    clock.AdvanceClock(120);
                    return RunResults.Capture(model);
                }
                var r = Run(out var factory);
                var setup = ((ISetupStation)factory.Find("Press")).Setup;
                Assert.IsNotNull(setup, "the setup is built");
                Assert.AreEqual(TaskType.Setup, setup.Resources.Task, "its crew sets up (task type Setup)");
                Assert.IsNull(((ISetupStation)factory.Find("Plain")).Setup, "no setup on the plain server");
                var po = r.Find(ResultScope.Element, factory.Find("Press").Name) ?? r.Of(ResultScope.Element).First(o => o.Name == "Press");
                Assert.IsTrue(po.TryGet(ResultMetrics.Setups, out double n) && n > 2, $"the press set up on type changes ({n})");
                Assert.IsTrue(po.TryGet(ResultMetrics.SetupTime, out double t) && Mathf.Approximately((float)t, (float)(0.5 * n)), "0.5 per setup");
                Assert.IsTrue(po.TryGet(ResultMetrics.State("SETUP"), out double share) && share > 0, "state:SETUP");
                var setter = factory.Executors[0];
                Assert.Greater(setter.GetTimeInState(ElementState.SETUP), 0, "the setter shows SETUP");
                Assert.AreEqual(0, setter.GetTimeInState(ElementState.PROCESSING), "the setter does not process");
                var again = Run(out _);
                var po2 = again.Of(ResultScope.Element).First(o => o.Name == "Press");
                po2.TryGet(ResultMetrics.Setups, out double n2);
                Assert.AreEqual(n, n2, "deterministic");
            }
        }

        [Test]
        public void CombinerAndPickingStationExportTheirSetup()
        {
            var go = new GameObject("Comb");
            var go2 = new GameObject("Pick");
            try
            {
                var comb = go.AddComponent<UnityCombiner>();
                comb.setup = new StationSetupConfig { when = SetupWhen.EachItem };
                var ec = SceneExtractor.ExtractElement(comb);
                Assert.AreEqual("EachItem", ec.Get<string>("setup.when", ""), "Combiner: rule");
                Assert.AreEqual("Constant~1", ec.Get<SimuLean.Serialization.SamplerSettings>("setup.time")?.ToString(), "Combiner: default time 1");
                Assert.AreEqual("type", ec.Get<string>("setup.label", ""), "the label also with EachItem (a study may switch the rule)");
                var pick = go2.AddComponent<UnityPickingStation>();
                Assert.AreEqual("None", SceneExtractor.ExtractElement(pick).Get<string>("setup.when", ""), "a new station has no setup");
                pick.setup.when = SetupWhen.LabelChange;
                Assert.AreEqual("LabelChange", SceneExtractor.ExtractElement(pick).Get<string>("setup.when", ""), "Picking Station: rule");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(go2);
            }
        }

        /// <summary>A setup matrix (a Data Table of the scene) and a queue grouping by the station's setup are
        /// extracted (the table with the model, its name as <c>setup.matrix</c>; <c>groupBySetup</c> / <c>groupLimit</c>), built
        /// by the engine and run headless: fewer setups with the grouping, the times from the matrix.</summary>
        [Test]
        public void SetupMatrixAndGroupingExtractBuildAndRun()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n) where T : Component => test.Make<T>(n);

                var sources = new List<UnitySource>();
                foreach (string t in new[] { "A", "B", "C" })
                {
                    var s = Make<UnitySource>("Src" + t);
                    s.mode = SourceMode.InterArrival;
                    s.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Exponential, exponentialRate = 1f / 4.5f };
                    s.createLabels = new List<string> { $"type = '{t}'" };
                    sources.Add(s);
                }
                var table = Make<UnityDataTable>("Changeovers");
                table.Rows = new Dictionary<string, List<string>>
                {
                    ["from"] = new List<string> { "", "A", "*" },
                    ["to"] = new List<string> { "*", "B", "*" },
                    ["time"] = new List<string> { "3", "Uniform~1~2", "1.5" },
                };
                var queue = Make<UnityQueue>("Queue");
                queue.groupBySetup = true;
                queue.groupLimit = 4;
                var cell = Make<UnityMultiServer>("Cell");
                cell.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Exponential, exponentialRate = 1f };
                cell.setup = new StationSetupConfig { when = SetupWhen.LabelChange, label = "type", matrix = table };
                var sink = Make<UnitySink>("Sink");
                foreach (var s in sources) s.nextElements = new List<SElement> { queue };
                queue.nextElements = new List<SElement> { cell };
                cell.nextElements = new List<SElement> { sink };

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                var cc = cfg.Elements.Single(e => e.Name == "Cell");
                Assert.AreEqual("Changeovers", cc.Get<string>("setup.matrix", ""), "the matrix by its table's name");
                Assert.IsTrue(cfg.Tables.Any(t => t.Name == "Changeovers"), "the table goes with the model");
                var qc = cfg.Elements.Single(e => e.Name == "Queue");
                Assert.IsTrue(qc.Get<bool>("groupBySetup", false), "groupBySetup exported");
                Assert.AreEqual(4, qc.Get<int>("groupLimit", 0), "groupLimit exported");
                Assert.AreEqual(0, SimuLean.Optimization.ModelParameterCatalog.Undecided(cfg).Count, "every key decided");

                double Setups(SimulationConfig c, out StationSetup setup, out ItemsQueue engineQueue)
                {
                    var clock = new SimClock(7);
                    var model = new HeadlessModelFactory(clock);
                    model.BuildModel(c);
                    model.StartModel();
                    clock.AdvanceClock(1000);
                    setup = ((ISetupStation)model.Find("Cell")).Setup;
                    engineQueue = (ItemsQueue)model.Find("Queue");
                    var o = RunResults.Capture(model).Of(ResultScope.Element).First(x => x.Name == "Cell");
                    Assert.IsTrue(o.TryGet(ResultMetrics.Setups, out double n), "setups reported");
                    return n;
                }
                double grouped = Setups(cfg, out var built, out var q);
                Assert.IsNotNull(built.Matrix, "the matrix is built");
                Assert.AreEqual(3, built.Matrix.Count, "three pairs");
                Assert.IsTrue(q.GroupBySetup && q.GroupLimit == 4, "the engine queue groups");
                Assert.Greater(grouped, 0, "the cell sets up");
                Assert.Greater(built.MeanTime, 1, "setup times from the matrix (1–3)");
                queue.groupBySetup = false;
                double fifo = Setups(SceneExtractor.Extract(root).Config, out _, out _);
                Assert.Greater(fifo, grouped, $"without grouping more setups ({fifo} > {grouped})");
            }
        }

        [Test]
        public void AssemblyStackerAndErectionDockExportTheirSetup()
        {
            var objects = new List<GameObject>();
            try
            {
                SElement Add<T>(string n) where T : SElement { var go = new GameObject(n); objects.Add(go); return go.AddComponent<T>(); }
                foreach (var station in new[] { Add<UnityAssemblyStation>("Asy"), Add<UnityStacker>("St"), Add<SimuLean.Unity.Naval.UnityErectionDock>("Dock") })
                {
                    Assert.IsTrue(station is ISetupUser, station.GetType().Name + " has a Setup section");
                    Assert.AreEqual("None", SceneExtractor.ExtractElement(station).Get<string>("setup.when", ""), "no setup by default");
                    ((ISetupUser)station).Setup.when = SetupWhen.LabelChange;
                    var ec = SceneExtractor.ExtractElement(station);
                    Assert.AreEqual("LabelChange", ec.Get<string>("setup.when", ""), station.GetType().Name + ": rule");
                    Assert.AreEqual("Constant~1", ec.Get<SimuLean.Serialization.SamplerSettings>("setup.time")?.ToString(), station.GetType().Name + ": time");
                }
            }
            finally
            {
                foreach (var go in objects) Object.DestroyImmediate(go);
            }
        }
    }
}
