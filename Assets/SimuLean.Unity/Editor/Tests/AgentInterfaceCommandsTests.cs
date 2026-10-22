using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SimuLean.Results;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The agent interface of the SimuLean MCP commands: errors and warnings in
    /// every answer, failed runs reported, studies checked before they run, connections checked as Next Elements are.
    /// Each test builds its model in a scene of its own, closed at the end.
    /// </summary>
    public class AgentInterfaceCommandsTests
    {
        private Scene active, scene;
        private bool temporary;
        private GameObject root;
        private int undoGroup;
        private const string Root = "AgentModel";

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
            // every command records undo steps; they are reverted at the end with no redo — a removed object's record would
            // otherwise recreate it in the scene open in the editor at the user's next Undo
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
        private static string[] Strings(JToken t) => t == null ? new string[0] : t.Select(x => (string)x).ToArray();

        /// <summary>Source → Queue → Multi-Server → Sink, constant times (1 and 0.5).</summary>
        private static void Line(string p)
        {
            SimuLeanCommands.AddElement("UnitySource", p + "Src", 0, 0, 0, Root);
            SimuLeanCommands.AddElement("UnityQueue", p + "Buf", 2, 0, 0, Root);
            SimuLeanCommands.AddElement("UnityMultiServer", p + "Srv", 4, 0, 0, Root);
            SimuLeanCommands.AddElement("UnitySink", p + "Sink", 6, 0, 0, Root);
            SimuLeanCommands.Set(p + "Src", "mode", "InterArrival");
            SimuLeanCommands.Set(p + "Src", "interArrivalDistribution.strategyType", "Constant");
            SimuLeanCommands.Set(p + "Src", "interArrivalDistribution.constantValue", "1");
            SimuLeanCommands.Set(p + "Srv", "delayStrategy.strategyType", "Constant");
            SimuLeanCommands.Set(p + "Srv", "delayStrategy.constantValue", "0.5");
            SimuLeanCommands.Connect(p + "Src", p + "Buf");
            SimuLeanCommands.Connect(p + "Buf", p + "Srv");
            SimuLeanCommands.Connect(p + "Srv", p + "Sink");
        }

        [Test]
        public void EveryAnswerCarriesErrorsAndWarnings()
        {
            var placed = J(SimuLeanCommands.AddElement("UnityQueue", "AQueue", 0, 0, 0, Root));
            Assert.IsNotNull(placed["errors"], "errors[] in a placing answer");
            Assert.IsNotNull(placed["warnings"], "warnings[] in a placing answer");
            Assert.IsNotNull(J(SimuLeanCommands.Params("AQueue"))["warnings"], "and in a describing answer");
            Assert.IsNotNull(J(SimuLeanCommands.Types())["types"], "the list of types is a field of the answer");
        }

        [Test]
        public void WarningsLoggedDuringARunReachTheAnswer()
        {
            Line("W");
            // a tilted element: the extractor's warning (once only in the console) is in the answer
            GameObject.Find("WBuf").transform.rotation = Quaternion.Euler(90, 0, 0);
            var answer = J(SimuLeanCommands.RunHeadless(20, 1, 1, "", "out=WSink.input", Root));
            Assert.IsTrue(Strings(answer["warnings"]).Any(w => w.Contains("tilted")), "the extractor's warning: " + answer["warnings"]);
            Assert.AreEqual(0, ((JArray)answer["errors"]).Count, "no error");
        }

        [Test]
        public void AStudyIsCheckedBeforeItRuns()
        {
            Line("C");
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.RunHeadless(20, 1, 1, "", "out=CSnk.input", Root));
            StringAssert.Contains("Did you mean 'CSink'", e.Message, "an unknown target names the closest element");
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.RunHeadless(20, 1, 1, "", "busy=CSrv.state:Bussy", Root));
            StringAssert.Contains("no state 'Bussy'", e.Message, "a misspelt state is refused (a run read 0)");
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.RunHeadless(20, 1, 1, "CSrv.capacity=1|-1", "out=CSink.input", Root));
            StringAssert.Contains("capacity=-1", e.Message, "a level the model cannot be built with is named");
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.RunHeadless(20, 1, 1, "CSrv.uniformMin=0.1|0.2", "out=CSink.input", Root));
            StringAssert.Contains("belongs to another distribution", e.Message, "a factor on a key of an unused distribution says why");
        }

        [Test]
        public void FailedRunsAreReportedNotAveragedAsNull()
        {
            var set = new ResultSet { Name = "t" };
            set.MetricNames.Add("out");
            set.Scenarios.Add(new ResultScenario { Name = "A" });
            var good = new ResultRun { Scenario = "A", Replication = 1, Seed = 1 };
            good.Metrics["out"] = 10;
            set.Runs.Add(good);
            set.Runs.Add(new ResultRun { Scenario = "A", Replication = 2, Seed = 2, Error = "InvalidOperationException: boom" });
            var answer = JObject.FromObject(SimuLeanCommands.Answer(set, false, new System.Collections.Generic.List<string>()));
            var sc = answer["scenarios"][0];
            Assert.AreEqual(1, (int)sc["runs"]["ok"]);
            Assert.AreEqual(1, (int)sc["runs"]["failed"]);
            StringAssert.Contains("boom", (string)sc["firstError"]);
            Assert.IsTrue(Strings(answer["errors"]).Any(x => x.Contains("1 of 2 runs failed") && x.Contains("boom")), "errors: " + answer["errors"]);
            Assert.AreEqual(10, PipelineCommandsTests.MetricMean(sc, "out"), 1e-9, "the mean of the runs that completed");
        }

        [Test]
        public void RunAnswersGiveTheEffectiveSettingsAndIntervals()
        {
            // The horizon, time unit, warm-up, seeds actually used; each metric as mean ± half-width with n
            Line("F");
            SimuLeanCommands.Set("FSrv", "delayStrategy.strategyType", "Exponential");
            SimuLeanCommands.Set("FSrv", "delayStrategy.exponentialRate", "2");
            var answer = J(SimuLeanCommands.RunHeadless(30, 3, 5, "", "out=FSink.input", Root));
            var settings = answer["settings"];
            Assert.AreEqual(30.0, (double)settings["horizon"], 1e-9, "the horizon run");
            Assert.AreEqual("s", (string)settings["timeUnit"], "the model's time unit");
            Assert.AreEqual(3, (int)settings["replications"]);
            Assert.AreEqual("5..7", (string)settings["seeds"], "seeds seed, seed+1, …");
            Assert.AreEqual(0.95, (double)settings["confidence"], 1e-9);
            var outMetric = answer["scenarios"][0]["metrics"]["out"];
            Assert.AreEqual(3, (int)outMetric["n"], "three runs gave a number");
            Assert.IsNotNull((double?)outMetric["halfWidth"], "a half-width with replications");
            var one = J(SimuLeanCommands.RunHeadless(0, 1, 1, "", "out=FSink.input", Root));
            Assert.Greater((double)one["settings"]["horizon"], 0, "without maxTime: the model's horizon, said");
            Assert.IsNull((double?)one["scenarios"][0]["metrics"]["out"]["halfWidth"], "one run: no interval");
        }

        [Test]
        public void TheWholeModelIsReadAndCheckedWithoutRunning()
        {
            // Simulean_model (the config a run builds, by name) and simulean_validate (build, no run)
            Line("M");
            SimuLeanCommands.AddTeam("MCrew", 2, "", 0, 0, 3, Root);
            SimuLeanCommands.Set("MSrv", "processBy.team", "MCrew");
            var model = J(SimuLeanCommands.Model("", true, Root));
            var srv = model["elements"].First(e => (string)e["name"] == "MSrv");
            Assert.AreEqual("MultiServer", (string)srv["type"]);
            CollectionAssert.AreEqual(new[] { "MSink" }, Strings(srv["next"]), "next elements by name");
            Assert.AreEqual("0.5", (string)srv["parameters"]["constantValue"], "parameters under their catalog keys");
            CollectionAssert.AreEqual(new[] { "MCrew_1", "MCrew_2" }, Strings(model["teams"][0]["members"]), "the team");
            Assert.IsNotNull(model["clock"]["timeUnit"], "the clock");
            var one = J(SimuLeanCommands.Model("MBuf", false, Root));
            Assert.AreEqual(1, ((JArray)one["elements"]).Count, "one element");
            Assert.IsNull(one["elements"][0]["parameters"], "structure only");

            var ok = J(SimuLeanCommands.Validate("MSrv.capacity=1|2", "out=MSink.input", Root));
            Assert.IsTrue((bool)ok["ok"], "a valid model and study: " + ok["errors"]);
            Assert.AreEqual(2, (int)ok["scenarios"]);
            var bad = J(SimuLeanCommands.Validate("", "out=MSnk.input", Root));
            Assert.IsFalse((bool)bad["ok"]);
            Assert.IsTrue(Strings(bad["errors"]).Any(x => x.Contains("Did you mean 'MSink'")), "errors: " + bad["errors"]);
        }

        [Test]
        public void NamesAreUniqueAtCreation()
        {
            // Studies and metrics find objects by name; two with one name made them take the first silently
            SimuLeanCommands.AddElement("UnityQueue", "UBuf", 0, 0, 0, Root);
            int servers = UnityCompat.FindAll<UnityMultiServer>().Length;
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.AddElement("UnityMultiServer", "UBuf", 2, 0, 0, Root));
            StringAssert.Contains("'UBuf (2)'", e.Message, "a free name is proposed");
            Assert.AreEqual(servers, UnityCompat.FindAll<UnityMultiServer>().Length, "nothing placed");
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.AddTeam("UBuf", 1, "", 0, 0, 0, Root), "a team cannot take an element's name");
            // without a name, the Library's is made unique in the whole scene (not only among siblings)
            var first = (string)J(SimuLeanCommands.AddElement("UnitySink", "", 0, 0, 0, Root))["name"];
            var second = (string)J(SimuLeanCommands.AddElement("UnitySink", "", 0, 0, 2, "UBuf"))["name"];
            Assert.AreNotEqual(first, second, "two Library sinks under different parents get different names");
        }

        [Test]
        public void ParseErrorsNameTheKeyTheTypeAndAnExample()
        {
            // .NET's "Input string was not in a correct format" named neither the key nor the expected value
            SimuLeanCommands.AddElement("UnityMultiServer", "PSrv", 0, 0, 0, Root);
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("PSrv", "capacity", "4.0"));
            StringAssert.Contains("'capacity'", e.Message);
            StringAssert.Contains("whole number", e.Message);
            StringAssert.Contains("e.g.", e.Message);
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("PSrv", "delayStrategy.constantValue", "2,5"));
            StringAssert.Contains("'.' as the decimal point", e.Message);
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("PSrv", "capacty", "2"));
            StringAssert.Contains("Did you mean 'capacity'", e.Message, "an unknown field suggests");
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("PSvr", "capacity", "2"));
            StringAssert.Contains("Did you mean 'PSrv'", e.Message, "an unknown object suggests");
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.AddElement("Multi Servr", "", 0, 0, 0, Root));
            StringAssert.Contains("Did you mean", e.Message, "an unknown Library entry suggests");
        }

        [Test]
        public void AddTeamChecksEverythingBeforeChangingTheScene()
        {
            // The team was placed before 'travel' was parsed, and stayed after the error
            int teams = UnityCompat.FindAll<UnityResourceTeam>().Length;
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.AddTeam("TCrew", 2, "Netwrk", 0, 0, 0, Root));
            StringAssert.Contains("Network", e.Message, "the options and the closest one");
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.AddTeam("TCrew", 0, "", 0, 0, 0, Root), "no member: an error, not 1 silently");
            Assert.AreEqual(teams, UnityCompat.FindAll<UnityResourceTeam>().Length, "nothing left in the scene");
            var ok = J(SimuLeanCommands.AddTeam("TCrew", 2, "network", 0, 0, 0, Root));
            Assert.AreEqual("Network", (string)ok["travel"], "case-insensitive");
        }

        [Test]
        public void DisconnectRemoveAndRenameUndoMistakes()
        {
            // The inverse commands, so an agent never needs the generic editor tools to correct its model
            Line("R");
            SimuLeanCommands.AddTeam("RCrew", 1, "", 0, 0, 3, Root);
            SimuLeanCommands.Set("RSrv", "processBy.team", "RCrew");

            var d = J(SimuLeanEditCommands.Disconnect("RSrc", "RBuf"));
            Assert.AreEqual(0, ((JArray)d["next"]).Count, "RSrc sends nowhere");
            StringAssert.Contains("does not send to", Assert.Throws<ArgumentException>(() => SimuLeanEditCommands.Disconnect("RSrc", "RBuf")).Message);
            SimuLeanCommands.Connect("RSrc", "RBuf");

            var removed = J(SimuLeanEditCommands.Remove("RCrew"));
            CollectionAssert.Contains(Strings(removed["referencesCleared"]), "RSrv.processBy.team", "the station's team reference cleared");
            Assert.IsNull(GameObject.Find("RSrv").GetComponent<UnityMultiServer>().processBy.team, "no dangling team");
            removed = J(SimuLeanEditCommands.Remove("RSrv"));
            CollectionAssert.Contains(Strings(removed["referencesCleared"]), "RBuf.nextElements[0]", "the queue's connection removed");
            Assert.AreEqual(0, GameObject.Find("RBuf").GetComponent<SElement>().nextElements.Count, "no empty Next Elements entry left");
            UnityEditor.Undo.PerformUndo();
            Assert.IsNotNull(GameObject.Find("RSrv"), "one undo brings the station back");
            Assert.AreEqual("RSrv", GameObject.Find("RBuf").GetComponent<SElement>().nextElements[0].name, "and its connection");
            StringAssert.Contains("not a SimuLean object", Assert.Throws<ArgumentException>(() => SimuLeanEditCommands.Remove(Root)).Message);

            SimuLeanCommands.Set("RSrc", "exitLabels", "dest = 'RSink'");
            var renamed = J(SimuLeanEditCommands.Rename("RSink", "RExit"));
            Assert.IsNotNull(GameObject.Find("RExit"));
            Assert.IsTrue(Strings(renamed["warnings"]).Any(w => w.Contains("RSrc.exitLabels[0]")), "a text naming the old name is warned: " + renamed["warnings"]);
            Assert.Throws<ArgumentException>(() => SimuLeanEditCommands.Rename("RExit", "RBuf"), "the new name must be free");
        }

        [Test]
        public void SetAndMetricsTakeJson()
        {
            // A value holding ; = | could not be written in the shorthand
            var f = SimuLeanCommands.FactorsOf("{\"Src.tableSource\": \"sql: SELECT a FROM t WHERE x = 1; \", \"Srv.capacity\": [1, 2.5], \"Q.flag\": true}");
            Assert.AreEqual("sql: SELECT a FROM t WHERE x = 1; ", f[0].levels.Single(), "a text level kept whole");
            CollectionAssert.AreEqual(new[] { "1", "2.5" }, f[1].levels, "numbers, invariant");
            CollectionAssert.AreEqual(new[] { "true" }, f[2].levels);
            var a = SimuLeanCommands.FactorsOf("[{\"path\": \"Srv.capacity\", \"values\": [3]}]");
            Assert.AreEqual("Srv.capacity", a[0].path);
            CollectionAssert.AreEqual(new[] { "3" }, a[0].levels);
            CollectionAssert.AreEqual(new[] { ("out", "Sink.input") }, SimuLeanCommands.MetricsOf("{\"out\": \"Sink.input\"}"));
            CollectionAssert.AreEqual(new[] { ("out", "Sink.input") }, SimuLeanCommands.MetricsOf("[{\"name\": \"out\", \"path\": \"Sink.input\"}]"));
            CollectionAssert.AreEqual(new[] { ("out", "Sink.input") }, SimuLeanCommands.MetricsOf("out=Sink.input"), "the shorthand still works");
            StringAssert.Contains("not valid JSON", Assert.Throws<ArgumentException>(() => SimuLeanCommands.FactorsOf("{\"a\": ")).Message);

            Line("J");
            var shorthand = J(SimuLeanCommands.RunHeadless(20, 1, 1, "JSrv.capacity=1|2", "out=JSink.input", Root));
            var json = J(SimuLeanCommands.RunHeadless(20, 1, 1, "{\"JSrv.capacity\": [1, 2]}", "{\"out\": \"JSink.input\"}", Root));
            for (int i = 0; i < 2; i++)
                Assert.AreEqual(PipelineCommandsTests.MetricMean(shorthand["scenarios"][i], "out"), PipelineCommandsTests.MetricMean(json["scenarios"][i], "out"),
                                "the same study either way");
        }

        [Test]
        public void ConnectChecksWhatNextElementsAllows()
        {
            SimuLeanCommands.AddElement("UnitySource", "KMain", 0, 0, 0, Root);
            SimuLeanCommands.AddElement("UnitySource", "KPart", 0, 0, 2, Root);
            SimuLeanCommands.AddElement("UnityCombiner", "KComb", 3, 0, 0, Root);
            SimuLeanCommands.AddElement("UnitySink", "KSink", 6, 0, 0, Root);
            // a Combiner is fed through its upstreams' Next Elements: connect no longer refuses it
            SimuLeanCommands.Connect("KMain", "KComb");
            SimuLeanCommands.Connect("KPart", "KComb");
            CollectionAssert.Contains(GameObject.Find("KPart").GetComponent<SElement>().nextElements.Select(n => n.name).ToList(), "KComb");
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Connect("KSink", "KComb"), "a sink has no output");
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Connect("KComb", "KMain"), "a source takes no input");
            var again = J(SimuLeanCommands.Connect("KMain", "KComb"));
            Assert.IsTrue(Strings(again["warnings"]).Any(w => w.Contains("already")), "a repeated connection is said");

            // an element that pushes to a list: connecting makes it send to its Next Elements again (the Connect tool's rule)
            SimuLeanCommands.AddElement("UnitySimList", "KList", 0, 0, 5, Root);
            SimuLeanCommands.AddElement("UnityQueue", "KQueue", 0, 0, 7, Root);
            SimuLeanCommands.Set("KQueue", "pushToList", "KList");
            SimuLeanCommands.Set("KQueue", "sendTo", "List");
            var nextWarnings = Strings(J(SimuLeanCommands.Set("KQueue", "nextElements", "KSink"))["warnings"]);
            Assert.IsTrue(nextWarnings.Any(w => w.Contains("sends its items to a list")), "Next Elements set while pushing to a list: warned");
            var switched = J(SimuLeanCommands.Connect("KQueue", "KSink"));
            Assert.IsTrue(Strings(switched["warnings"]).Any(w => w.Contains("now sends to its Next Elements")), "said: " + switched["warnings"]);
            Assert.AreEqual(SElement.SendMode.Elements, GameObject.Find("KQueue").GetComponent<SElement>().sendTo);
        }

        [Test]
        public void ElementPropertiesAreSetByName()
        {
            // CASE2 v2 run: a property (what a study varies as Element.properties.NAME) needed the generic tools, and a key
            // went in with its quotes
            Line("P");
            var first = J(SimuLeanCommands.Set("PSrv", "properties.beds", "2"));
            Assert.IsTrue((bool)first["created"] && (bool)first["componentAdded"], "the component and the property are created: " + first);
            var labels = GameObject.Find("PSrv").GetComponent<SElementLabels>();
            Assert.AreEqual(2.0, labels.GetLabelValue("beds"));
            var again = J(SimuLeanCommands.Set("PSrv", "properties.\"beds\"", "3"));
            Assert.IsFalse((bool)again["created"], "the same property (quotes dropped), not a second one");
            Assert.AreEqual(1, labels.GetLabelPairs().Count);
            Assert.AreEqual(3.0, labels.GetLabelValue("beds"));
            var text = J(SimuLeanCommands.Set("PSrv", "properties.zone", "north"));
            Assert.IsTrue(Strings(text["warnings"]).Any(w => w.Contains("is a text")), "a text property is said: " + text["warnings"]);
            var applied = J(SimuLeanCommands.Apply("[{\"op\":\"set\",\"element\":\"PBuf\",\"key\":\"properties.slots\",\"value\":4}]"));
            Assert.AreEqual(0, ((JArray)applied["errors"]).Count, "simulean_apply: " + applied);
            Assert.AreEqual(4.0, GameObject.Find("PBuf").GetComponent<SElementLabels>().GetLabelValue("slots"));
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set(Root, "properties.beds", "1"), "only elements have properties");
            var ok = J(SimuLeanCommands.Validate("PSrv.properties.beds=1|2", "out=PSink.input", Root));
            Assert.IsTrue((bool)ok["ok"], "a study varies the property: " + ok["errors"]);
        }
    }
}
