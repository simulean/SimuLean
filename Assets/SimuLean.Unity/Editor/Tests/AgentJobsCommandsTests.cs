using System;
using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SimuLean.Results;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The wait of the long-job status commands, optimisation and
    /// diagnosis through the MCP commands. Each test builds its model in a scene of its own, closed at the end.
    /// </summary>
    public class AgentJobsCommandsTests
    {
        private Scene active, scene;
        private bool temporary;
        private GameObject root;
        private int undoGroup;
        private const string Root = "JobsModel";

        [SetUp]
        public void SetUp()
        {
            active = SceneManager.GetActiveScene();
            temporary = !string.IsNullOrEmpty(active.path);
            scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            if (temporary) SceneManager.SetActiveScene(scene);
            root = new GameObject(Root);
            SceneManager.MoveGameObjectToScene(root, scene);
            ResultsOutput.StoreOverride = System.IO.Path.GetFullPath($"Temp/simulean_store_test_{Guid.NewGuid():N}.db");
            UnityEditor.Undo.IncrementCurrentGroup();
            undoGroup = UnityEditor.Undo.GetCurrentGroup();
        }

        [TearDown]
        public void TearDown()
        {
            ResultsOutput.StoreOverride = null;
            UnityEditor.Undo.RevertAllDownToGroup(undoGroup);
            if (temporary)
            {
                if (active.IsValid()) SceneManager.SetActiveScene(active);
                EditorSceneManager.CloseScene(scene, true);
            }
            else Object.DestroyImmediate(root);
        }

        private static JObject J(object answer) => (JObject)answer;

        /// <summary>Source (every 1) → Queue → Multi-Server (<paramref name="service"/>) → Sink.</summary>
        private static void Line(string p, double service)
        {
            SimuLeanCommands.AddElement("UnitySource", p + "Src", 0, 0, 0, Root);
            SimuLeanCommands.AddElement("UnityQueue", p + "Buf", 2, 0, 0, Root);
            SimuLeanCommands.AddElement("UnityMultiServer", p + "Srv", 4, 0, 0, Root);
            SimuLeanCommands.AddElement("UnitySink", p + "Sink", 6, 0, 0, Root);
            SimuLeanCommands.Set(p + "Src", "mode", "InterArrival");
            SimuLeanCommands.Set(p + "Src", "interArrivalDistribution.strategyType", "Constant");
            SimuLeanCommands.Set(p + "Src", "interArrivalDistribution.constantValue", "1");
            SimuLeanCommands.Set(p + "Srv", "delayStrategy.strategyType", "Constant");
            SimuLeanCommands.Set(p + "Srv", "delayStrategy.constantValue", service.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SimuLeanCommands.Connect(p + "Src", p + "Buf");
            SimuLeanCommands.Connect(p + "Buf", p + "Srv");
            SimuLeanCommands.Connect(p + "Srv", p + "Sink");
        }

        [Test]
        public void AStatusWithoutAJobOrWithoutWaitAnswersAtOnce()
        {
            var t = SimuLeanCommands.OptimizeStatus(false, 0);
            Assert.IsTrue(t.IsCompleted, "wait 0: the answer at once");
            var done = JobWait.Until(() => new JObject { ["status"] = "completed" }, 30);
            Assert.IsTrue(done.IsCompleted, "a job that is not going on: no wait whatever the wait asked");
            Assert.Throws<ArgumentException>(() => JobWait.Until(() => new JObject(), -1));
        }

        [UnityTest]
        public IEnumerator AWaitingStatusAnswersWhenTheJobEnds()
        {
            int ticks = 0;
            var task = JobWait.Until(() => new JObject { ["status"] = ++ticks < 4 ? "running" : "completed" }, 30);
            Assert.IsFalse(task.IsCompleted, "the job is going on: the call waits");
            double start = UnityEditor.EditorApplication.timeSinceStartup;
            while (!task.IsCompleted && UnityEditor.EditorApplication.timeSinceStartup - start < 10) yield return null;
            Assert.IsTrue(task.IsCompleted, "answered once the job ended, long before the wait was over");
            Assert.AreEqual("completed", (string)((JObject)task.Result)["status"]);
        }

        [UnityTest]
        public IEnumerator AnOptimisationRunsFromTheCommandsAlone()
        {
            Line("O", 1.5);   // one server is a bottleneck: capacity 2 or 3 serves every arrival
            var start = J(SimuLeanCommands.OptimizeStart("OSrv.capacity=1..3", "out=OSink.input", "out", "", "random",
                                                         "samples=6;batchSize=3", -1, -1, 2, 30, 1, 1, Root));
            Assert.AreEqual("running", (string)start["status"], start.ToString());
            Assert.AreEqual("Random search", (string)start["algorithm"]["Algorithm"]);
            Assert.AreEqual(2, (int)start["replications"]);
            JObject status = null;
            double t0 = UnityEditor.EditorApplication.timeSinceStartup;
            while (UnityEditor.EditorApplication.timeSinceStartup - t0 < 60)
            {
                status = J(SimuLeanCommands.OptimizeStatus(false, 0).Result);
                if ((string)status["status"] != "running") break;
                yield return null;
            }
            Assert.AreEqual("completed", (string)status["status"], status.ToString());
            var best = status["best"];
            Assert.IsNotNull(best, status.ToString());
            Assert.Greater(int.Parse((string)best["values"]["OSrv.capacity"]), 1, "the bottleneck removed");
            Assert.AreEqual(2, (int)best["metrics"]["out"]["n"], "the best re-run for every replication");
            Assert.IsNotNull((double?)best["metrics"]["out"]["halfWidth"]);
        }

        [Test]
        public void AnOptimisationIsCheckedBeforeItStarts()
        {
            Line("V", 1);
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.OptimizeStart("VSrv.capacity=1..3", "out=VSnk.input", "", "", "", "",
                                                                                         -1, -1, 0, 10, 0, 1, Root));
            StringAssert.Contains("VSnk", e.Message, "the unknown element of a metric, before anything runs");
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.OptimizeStart("VSrv.capacity=1..3", "out=VSink.input", "", "", "annealing",
                                                                                     "", -1, -1, 0, 10, 0, 1, Root));
            StringAssert.Contains("Genetic", e.Message, "the algorithms there are");
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.OptimizeStart("VSrv.capacity=1..3", "out=VSink.input", "", "", "genetic",
                                                                                     "population=4", -1, -1, 0, 10, 0, 1, Root));
            StringAssert.Contains("populationSize", e.Message, "the settings there are");
        }

        [Test]
        public void SetSwitchesASerializeReferenceFieldByType()
        {
            SimuLeanCommands.AddElement("Optimizer", "Opt", 0, 0, 0, Root);
            SimuLeanCommands.Set("Opt", "algorithm", "RandomSearch");
            var opt = GameObject.Find("Opt").GetComponent<SimuLean.Optimization.Evaluator.Optimizer>();
            Assert.IsInstanceOf<SimuLean.Optimization.RandomSearchSettings>(opt.Algorithm);
            SimuLeanCommands.Set("Opt", "algorithm.samples", "12");
            Assert.AreEqual(12, ((SimuLean.Optimization.RandomSearchSettings)opt.Algorithm).samples);
            SimuLeanCommands.Set("Opt", "algorithm", "genetic");
            Assert.IsInstanceOf<SimuLean.Optimization.GeneticAlgorithmSettings>(opt.Algorithm);
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("Opt", "algorithm", "Annealing"));
        }

        private static JObject Run(string set, string metrics, string scenarios = "", string where = "", string derived = "",
                                   double confidence = 0, int replications = 1)
            => J(SimuLeanCommands.RunHeadless(20, replications, 1, set, metrics, Root, "metrics", scenarios, where, derived, confidence));

        [Test]
        public void WhereKeepsOnlyTheCombinationsThatMeetIt()
        {
            Line("W", 1);
            var a = Run("WSrv.capacity=1|2|3;WBuf.capacity=1|2|3", "out=WSink.input", where: "WSrv_capacity + WBuf_capacity <= 3");
            Assert.AreEqual(3, a["scenarios"].Count(), "(1,1), (1,2), (2,1) of the 9: " + a);
            Assert.IsTrue(a["scenarios"].All(s => (double)s["parameters"]["WSrv.capacity"] + (double)s["parameters"]["WBuf.capacity"] <= 3));
            var e = Assert.Throws<ArgumentException>(() => Run("WSrv.capacity=1|2", "out=WSink.input", where: "capacity <= 1"));
            StringAssert.Contains("WSrv_capacity", e.Message, "an unknown name: the factor names given");
        }

        [Test]
        public void ExplicitScenariosRunWithTheirOwnReplications()
        {
            Line("X", 1);
            var a = Run("", "out=XSink.input",
                        scenarios: "[{\"name\": \"low\", \"XSrv.capacity\": 1}, {\"name\": \"high\", \"XSrv.capacity\": 3, \"replications\": 2}]");
            var names = a["scenarios"].Select(s => (string)s["scenario"]).ToArray();
            CollectionAssert.AreEqual(new[] { "low", "high" }, names);
            Assert.AreEqual(2, (int)a["scenarios"][1]["runs"]["ok"], "high's own replications");
            Assert.Throws<ArgumentException>(() => Run("", "out=XSink.input", scenarios: "[{\"name\": \"a\", \"XSrv.capacity\": 1}, {\"name\": \"b\"}]"),
                                             "a scenario without a value for a factor");
        }

        [Test]
        public void DerivedMetricsAndTheConfidenceLevel()
        {
            Line("Y", 0.5);
            SimuLeanCommands.Set("YSrv", "delayStrategy.strategyType", "Exponential");
            SimuLeanCommands.Set("YSrv", "delayStrategy.exponentialRate", "2");
            var a = Run("", "out=YSink.input", derived: "half=out/2", confidence: 0.9, replications: 3);
            var m = a["scenarios"][0]["metrics"];
            Assert.AreEqual((double)m["out"]["mean"] / 2, (double)m["half"]["mean"], 1e-9);
            Assert.AreEqual(3, (int)m["half"]["n"]);
            Assert.AreEqual(0.9, (double)a["settings"]["confidence"], 1e-12);
            var e = Assert.Throws<ArgumentException>(() => Run("", "out=YSink.input", derived: "x=foo*2"));
            StringAssert.Contains("foo", e.Message);
        }

        [Test]
        public void AWildcardSplitsAMetricByLabelValue()
        {
            Line("Z", 0.5);
            SimuLeanCommands.Set("ZSrc", "assignLabels[0].value", "7");
            SimuLeanCommands.Set("ZSink", "tallies", "tis = age");
            SimuLeanCommands.Set("ZSink", "tallySplit", "prio");
            var a = Run("", "w=ZSink.tally:tis[*]:count");
            var m = a["scenarios"][0]["metrics"];
            Assert.IsNotNull(m["w"], "the unsplit total: " + m);
            Assert.IsNotNull(m["w[7]"], "one metric per label value: " + m);
            Assert.AreEqual((double)m["w"]["mean"], (double)m["w[7]"]["mean"], 1e-9, "one value: the split is the total");
        }

        [Test]
        public void AnExperimenterOfTheSceneRunsInOneCall()
        {
            Line("E", 0.5);
            SimuLeanCommands.AddElement("Experimenter", "Exp", 0, 0, 0, Root);
            var a = J(SimuLeanCommands.RunHeadless(20, 1, 1, "", "out=ESink.input", Root, "metrics", experimenter: "Exp"));
            Assert.AreEqual(1, a["scenarios"].Count(), a.ToString());
            Assert.Greater((double)a["scenarios"][0]["metrics"]["out"]["mean"], 0);
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.RunHeadless(20, 1, 1, "ESrv.capacity=2", "out=ESink.input", Root,
                                                                                       "metrics", experimenter: "Exp"));
            StringAssert.Contains("experimenter", e.Message);
        }

        [Test]
        public void ADiagnosisTellsWhatEachElementHoldsAndWaitsFor()
        {
            Line("D", 2);   // arrivals every 1, service 2: the queue grows
            var d = J(SimuLeanCommands.Diagnose(10.5, 1, "", false, Root));
            Assert.AreEqual(10.5, (double)d["time"], 1e-9);
            Assert.IsFalse((bool)d["stalled"]);
            var buf = d["elements"].FirstOrDefault(x => (string)x["name"] == "DBuf");
            Assert.IsNotNull(buf, d.ToString());
            Assert.Greater((int)buf["items"], 2, "the queue holds the items the server could not take");
            StringAssert.Contains("DSrv", (string)buf["waitsFor"]);
            Assert.IsNull(d["elements"].FirstOrDefault(x => (string)x["name"] == "DSink"), "idle, empty elements are left out");
            var every = J(SimuLeanCommands.Diagnose(10.5, 1, "", true, Root));
            Assert.Greater(every["elements"].Count(), d["elements"].Count(), "all = every element");
            var faster = J(SimuLeanCommands.Diagnose(10.5, 1, "DSrv.capacity=3", false, Root));
            var fasterBuf = faster["elements"].FirstOrDefault(x => (string)x["name"] == "DBuf");
            Assert.IsTrue(fasterBuf == null || (int)fasterBuf["items"] < (int)buf["items"], "set: a what-if");
        }
    }
}
