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
    public class AgentInterfaceCommandsTests : SceneTestFixture
    {
        private const string Root = "AgentModel";
        protected override string RootName => Root;

        [SetUp]
        public void SetUp()
        {
            ResultsOutput.StoreOverride = System.IO.Path.GetFullPath($"Temp/simulean_store_test_{Guid.NewGuid():N}.db");
        }

        [TearDown]
        public void TearDown()
        {
            ResultsOutput.StoreOverride = null;
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
        public void CommandArgumentDefaultsArePrintableText()
        {
            // The MCP server publishes each argument's default in the tool's schema; a control character there makes the
            // Unity CLI leave the whole command out of the tool list.
            var bad = typeof(SimuLeanCommands).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                .Where(m => m.GetCustomAttributes(typeof(global::Unity.Pipeline.Commands.CliCommandAttribute), false).Length > 0)
                .SelectMany(m => m.GetParameters().Where(p => p.HasDefaultValue && (p.DefaultValue as string ?? "").Any(char.IsControl))
                                                  .Select(p => m.DeclaringType.Name + "." + m.Name + "(" + p.Name + ")"))
                .ToList();
            Assert.IsEmpty(bad, "command arguments whose default holds a control character: " + string.Join(", ", bad));
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
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.RunHeadless(20, 1, 1, "CSrv.delayStrategy.uniformMin=0.1|0.2", "out=CSink.input", Root));
            StringAssert.Contains("belongs to another distribution", e.Message, "a factor on a key of an unused distribution says why");
        }

        [Test]
        public void AnExpressionIsCheckedAgainstTheSceneAsItIsNow()
        {
            // simulean_set read the inspector's cache of element names, refreshed by a hierarchy event that comes on
            // a later editor update — an element placed (or a scene opened) just before was "not in the scene"
            ExpressionField.Check("content('Anything') > 0", "reaction");   // fills the cache
            SimuLeanCommands.AddElement("UnityMultiServer", "Flex Cell X", 0, 0, 0, Root);
            var go = new GameObject("XShift");
            go.transform.SetParent(Test.Root.transform);
            go.AddComponent<UnityReaction>();
            var set = J(SimuLeanCommands.Set("XShift", "actions[0].value", "1 - get('Flex Cell X','open')"));
            Assert.IsFalse(Strings(set["warnings"]).Any(w => w.Contains("No element")), "the element just placed is known: " + set["warnings"]);
            set = J(SimuLeanCommands.Set("XShift", "actions[0].value", "1 - get('Flex Cel X','open')"));
            Assert.IsTrue(Strings(set["warnings"]).Any(w => w.Contains("No element 'Flex Cel X'") && w.Contains("Flex Cell X")),
                          "a misspelt name still warns, with the suggestion: " + set["warnings"]);
        }

        [Test]
        public void ALookActionAddedByIndexSetsTheColourItIsGiven()
        {
            // a colour set by command is the look's colour (no separate switch); an action added by index starts
            // from the action's defaults, not from a copy of the one before it
            var go = new GameObject("XLooks");
            go.transform.SetParent(Test.Root.transform);
            var r = go.AddComponent<UnityReaction>();
            SimuLeanCommands.Set("XLooks", "actions[0].kind", "SetItemLook");
            SimuLeanCommands.Set("XLooks", "actions[0].color", "#2E7D32");
            SimuLeanCommands.Set("XLooks", "actions[1].kind", "SetItemLook");
            SimuLeanCommands.Set("XLooks", "actions[1].scale", "1.5");
            var cfg = r.ToConfig(e => e.name);
            Assert.AreEqual("#2E7D32", cfg.Settings.Actions[0].Color, "the colour given is set");
            Assert.IsNull(cfg.Settings.Actions[1].Color, "the next action keeps the item's colour (not the copied green)");
            Assert.AreEqual(1.5, cfg.Settings.Actions[1].Scale, 1e-6);
            Assert.AreEqual(1, r.actions[1].count, "the action's defaults");
            Assert.AreEqual("STOPPED", r.actions[1].state);
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
            var answer = JObject.FromObject(SimuLean.Optimization.StudySpec.Answer(set, false, new System.Collections.Generic.List<string>()));
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
            // the horizon, time unit, warm-up, seeds actually used; each metric as mean ± half-width with n
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
            var one = J(SimuLeanCommands.RunHeadless(-1, 1, 1, "", "out=FSink.input", Root));
            Assert.Greater((double)one["settings"]["horizon"], 0, "without maxTime: the model's horizon, said");
            Assert.IsNull((double?)one["scenarios"][0]["metrics"]["out"]["halfWidth"], "one run: no interval");
        }

        [Test]
        public void TheWholeModelIsReadAndCheckedWithoutRunning()
        {
            // simulean_model (the config a run builds, by name) and simulean_validate (build, no run)
            Line("M");
            SimuLeanCommands.AddTeam("MCrew", 2, "", 0, 0, 3, Root);
            SimuLeanCommands.Set("MSrv", "processBy.team", "MCrew");
            var model = J(SimuLeanCommands.Model("", true, Root));
            var srv = model["elements"].First(e => (string)e["name"] == "MSrv");
            Assert.AreEqual("MultiServer", (string)srv["type"]);
            CollectionAssert.AreEqual(new[] { "MSink" }, Strings(srv["next"]), "next elements by name");
            Assert.AreEqual("0.5", (string)srv["parameters"]["delayStrategy.constantValue"], "parameters under their catalog keys");
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
            // studies and metrics find objects by name; two with one name made them take the first silently
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
            // the team was placed before 'travel' was parsed, and stayed after the error
            int teams = UnityCompat.FindAll<UnityResourceTeam>().Length;
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.AddTeam("TCrew", 2, "Netwrk", 0, 0, 0, Root));
            StringAssert.Contains("Network", e.Message, "the options and the closest one");
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.AddTeam("TCrew", 0, "", 0, 0, 0, Root), "no member: an error, not 1 silently");
            Assert.AreEqual(teams, UnityCompat.FindAll<UnityResourceTeam>().Length, "nothing left in the scene");
            var ok = J(SimuLeanCommands.AddTeam("TCrew", 2, "network", 0, 0, 0, Root));
            Assert.AreEqual("Network", (string)ok["travel"], "case-insensitive");
        }

        [Test]
        public void AddTeamGivesTheMembersTheirKindsModelNotABox()
        {
            // a team added by simulean_add_team had no member prefab, so its members were white boxes
            UnityResourceTeam Team(string name) => GameObject.Find(name).GetComponent<UnityResourceTeam>();
            void Expect(string team, string prefab, string why)
            {
                var t = Team(team);
                Assert.IsNotNull(t.memberPrefab, $"{team}: {why} — a member prefab, not a box");
                Assert.AreEqual(prefab, t.memberPrefab.name, $"{team}: {why}");
                // a model with meshes, or a person whose look (meshes) is applied per member (OperatorAppearance)
                Assert.IsTrue(t.memberPrefab.GetComponentInChildren<Renderer>(true) != null
                              || t.memberPrefab.GetComponentInChildren<OperatorAppearance>(true) != null, $"{team}: a model with meshes");
            }
            var cases = new[]
            {
                ("KAgv", "agv", "AGV"), ("KAgvs", "AGVs", "AGV"), ("KForklift", "forklift", "Forklift"),
                ("KTugger", "tugger train", "TowTractor"), ("KOps", "operators", "Operator"),
                ("KPallet", "pallet truck", "PalletTruckOperator"), ("KStacker", "stacker crane", "StackerCrane"),
                ("KDoctor", "doctor", "Doctor"),
            };
            foreach (var (name, kind, prefab) in cases)
            {
                var a = J(SimuLeanCommands.AddTeam(name, 2, "", 0, 0, 0, Root, kind));
                Assert.AreEqual(prefab, (string)a["model"], $"{kind}: the answer names the model");
                Expect(name, prefab, $"kind '{kind}'");
                var preset = LibraryPresets.KindOf(kind);
                Assert.AreEqual(preset.Speed, Team(name).speed, 1e-6, $"{kind}: the kind's realistic speed");
                Assert.AreEqual(SimuLean.Serialization.ResourceUnits.Physical, Team(name).units, $"{kind}: m/s");
            }
            // the kind keeps an asked travel
            SimuLeanCommands.AddTeam("KAgvNet", 1, "Network", 0, 0, 0, Root, "agv");
            Assert.AreEqual(TeamTravel.Network, Team("KAgvNet").travel);
            Expect("KAgvNet", "AGV", "AGV on the network");
            // a robot team is the preset's: its tool rest point
            SimuLeanCommands.AddTeam("KRobot", 1, "", 0, 0, 0, Root, "robot");
            Assert.IsTrue(Team("KRobot").MembersAreArms);
            Assert.IsNotNull(Team("KRobot").homeNode, "the robot's rest point, as the Library's preset");

            // no kind, a team named after one (a team named 'AGVs' used to get boxes): that kind
            var named = J(SimuLeanCommands.AddTeam("AGVs", 2, "", 0, 0, 0, Root));
            Assert.AreEqual("agv", (string)named["kind"], "the kind read from the team's name");
            Expect("AGVs", "AGV", "a team named AGVs");
            SimuLeanCommands.AddTeam("Forklifts", 1, "", 0, 0, 0, Root);
            Expect("Forklifts", "Forklift", "a team named Forklifts");

            // no kind: the look of the travel (vehicles on the network, people otherwise), the default speed kept
            SimuLeanCommands.AddTeam("KPlain", 1, "", 0, 0, 0, Root);
            Expect("KPlain", "Operator", "a team without a kind walks: an operator");
            Assert.AreEqual(1f, Team("KPlain").speed, "no kind: the team's default speed");
            SimuLeanCommands.AddTeam("KPlainNet", 1, "Network", 0, 0, 0, Root);
            Expect("KPlainNet", "AGV", "a network team without a kind: an AGV");
            SimuLeanCommands.AddTeam("KPlainAbs", 1, "Abstract", 0, 0, 0, Root);
            Expect("KPlainAbs", "Operator", "an abstract team is seen standing");

            // the same through simulean_apply and the Library's plain Resource Team
            SimuLeanCommands.Apply("[{\"op\":\"add_team\",\"name\":\"KApply\",\"members\":2,\"kind\":\"forklift\"}," +
                                   "{\"op\":\"add_team\",\"name\":\"KApplyNet\",\"travel\":\"Network\"}]");
            Expect("KApply", "Forklift", "simulean_apply's add_team with a kind");
            Expect("KApplyNet", "AGV", "simulean_apply's add_team on the network");
            SimuLeanCommands.AddElement("Resource Team", "KLib", 0, 0, 0, Root);
            Expect("KLib", "Operator", "the Library's plain Resource Team");

            // an unknown kind is refused before the scene changes, with the kinds
            int teams = UnityCompat.FindAll<UnityResourceTeam>().Length;
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.AddTeam("KBad", 1, "", 0, 0, 0, Root, "hovercraft"));
            StringAssert.Contains("forklift", e.Message, "the kinds listed");
            Assert.AreEqual(teams, UnityCompat.FindAll<UnityResourceTeam>().Length, "nothing left in the scene");

            // every kind of the table has its model
            foreach (var k in LibraryPresets.MemberKinds)
                Assert.IsNotNull(k.Load(), $"kind '{k.Name}': {k.Prefab} is missing");
        }

        [Test]
        public void DisconnectRemoveAndRenameUndoMistakes()
        {
            // the inverse commands, so an agent never needs the generic editor tools to correct its model
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
            StringAssert.Contains("takes no key", Assert.Throws<ArgumentException>(() => SimuLeanEditCommands.Remove("RSrv", "properties.S")).Message);
            Assert.IsNotNull(GameObject.Find("RSrv"), "a remove with a key (meant for one field) removes nothing");
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
        public void ReorderPutsTheNextElementsInANewOrder()
        {
            // routing by index reads the Next Elements' order
            Line("O");
            SimuLeanCommands.AddElement("UnitySink", "OSink2", 6, 0, 2, Root);
            SimuLeanCommands.Connect("OSrv", "OSink2");
            var srv = GameObject.Find("OSrv").GetComponent<SElement>();
            CollectionAssert.AreEqual(new[] { "OSink", "OSink2" }, srv.nextElements.Select(e => e.name).ToList());
            var r = J(SimuLeanEditCommands.Reorder("OSrv", "OSink2; OSink"));
            CollectionAssert.AreEqual(new[] { "OSink2", "OSink" }, Strings(r["next"]), "the answer lists the new order");
            CollectionAssert.AreEqual(new[] { "OSink2", "OSink" }, srv.nextElements.Select(e => e.name).ToList(), "the element's order");
            UnityEditor.Undo.PerformUndo();
            CollectionAssert.AreEqual(new[] { "OSink", "OSink2" }, srv.nextElements.Select(e => e.name).ToList(), "one undo restores it");
            StringAssert.Contains("every Next Element", Assert.Throws<ArgumentException>(() => SimuLeanEditCommands.Reorder("OSrv", "OSink")).Message);
            StringAssert.Contains("twice", Assert.Throws<ArgumentException>(() => SimuLeanEditCommands.Reorder("OSrv", "OSink;OSink")).Message);
            StringAssert.Contains("not a Next Element", Assert.Throws<ArgumentException>(() => SimuLeanEditCommands.Reorder("OSrv", "OBuf;OSink")).Message);
            var applied = J(SimuLeanCommands.Apply(@"[{""op"": ""reorder"", ""object"": ""OSrv"", ""next"": ""OSink2;OSink""}]"));
            Assert.AreEqual(1, (int)applied["applied"]);
            CollectionAssert.AreEqual(new[] { "OSink2", "OSink" }, srv.nextElements.Select(e => e.name).ToList(), "a simulean_apply operation too");
        }

        [Test]
        public void SetAndMetricsTakeJson()
        {
            // a value holding ; = | could not be written in the shorthand
            var f = SimuLean.Optimization.StudyInputs.FactorsOf("{\"Src.tableSource\": \"sql: SELECT a FROM t WHERE x = 1; \", \"Srv.capacity\": [1, 2.5], \"Q.flag\": true}");
            Assert.AreEqual("sql: SELECT a FROM t WHERE x = 1; ", f[0].levels.Single(), "a text level kept whole");
            CollectionAssert.AreEqual(new[] { "1", "2.5" }, f[1].levels, "numbers, invariant");
            CollectionAssert.AreEqual(new[] { "true" }, f[2].levels);
            var a = SimuLean.Optimization.StudyInputs.FactorsOf("[{\"path\": \"Srv.capacity\", \"values\": [3]}]");
            Assert.AreEqual("Srv.capacity", a[0].path);
            CollectionAssert.AreEqual(new[] { "3" }, a[0].levels);
            CollectionAssert.AreEqual(new[] { ("out", "Sink.input") }, SimuLean.Optimization.StudyInputs.MetricsOf("{\"out\": \"Sink.input\"}"));
            CollectionAssert.AreEqual(new[] { ("out", "Sink.input") }, SimuLean.Optimization.StudyInputs.MetricsOf("[{\"name\": \"out\", \"path\": \"Sink.input\"}]"));
            CollectionAssert.AreEqual(new[] { ("out", "Sink.input") }, SimuLean.Optimization.StudyInputs.MetricsOf("out=Sink.input"), "the shorthand still works");
            StringAssert.Contains("not valid JSON", Assert.Throws<ArgumentException>(() => SimuLean.Optimization.StudyInputs.FactorsOf("{\"a\": ")).Message);

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
            // a property (what a study varies as Element.properties.NAME) is set by name, without the generic tools, and a key
            // written with its quotes loses them
            Line("P");
            var first = J(SimuLeanCommands.Set("PSrv", "properties.beds", "2"));
            Assert.IsTrue((bool)first["created"] && (bool)first["componentAdded"], "the component and the property are created: " + first);
            var labels = GameObject.Find("PSrv").GetComponent<SElementProperties>();
            Assert.AreEqual(2.0, labels.GetValue("beds"));
            var again = J(SimuLeanCommands.Set("PSrv", "properties.\"beds\"", "3"));
            Assert.IsFalse((bool)again["created"], "the same property (quotes dropped), not a second one");
            Assert.AreEqual(1, labels.GetProperties().Count);
            Assert.AreEqual(3.0, labels.GetValue("beds"));
            var text = J(SimuLeanCommands.Set("PSrv", "properties.zone", "north"));
            Assert.IsTrue(Strings(text["warnings"]).Any(w => w.Contains("is a text")), "a text property is said: " + text["warnings"]);
            var applied = J(SimuLeanCommands.Apply("[{\"op\":\"set\",\"object\":\"PBuf\",\"key\":\"properties.slots\",\"value\":4}]"));
            Assert.AreEqual(0, ((JArray)applied["errors"]).Count, "simulean_apply: " + applied);
            Assert.AreEqual(4.0, GameObject.Find("PBuf").GetComponent<SElementProperties>().GetValue("slots"));
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set(Root, "properties.beds", "1"), "only elements have properties");
            var ok = J(SimuLeanCommands.Validate("PSrv.properties.beds=1|2", "out=PSink.input", Root));
            Assert.IsTrue((bool)ok["ok"], "a study varies the property: " + ok["errors"]);
        }
    
        [Test]
        public void EveryCommandArgumentHasOneConceptOfTheVocabulary()
        {
            var args = SimuLeanCommands.CommandArguments();
            Assert.Greater(args.Count, 100, "the commands are found by reflection");
            var uncovered = args.Where(a => SimuLean.Optimization.Vocabulary.ConceptOf(a.argument) == null)
                                .Select(a => a.command + "." + a.argument).Distinct().ToList();
            Assert.IsEmpty(uncovered, "give each new argument a concept in SimuLean.Optimization.Vocabulary.Concepts (or use a canonical name)");
            var retired = args.Where(a => SimuLean.Optimization.Vocabulary.Retired.ContainsKey(a.argument)).Select(a => a.command + "." + a.argument).ToList();
            Assert.IsEmpty(retired, "a retired name is back");
            var unused = SimuLean.Optimization.Vocabulary.Concepts.SelectMany(c => c.Arguments).Where(n => !args.Any(a => a.argument == n)).ToList();
            Assert.IsEmpty(unused, "a concept lists an argument no command has");

            var v = J(SimuLeanCommands.VocabularyOf("", "all"));
            var obj = v["arguments"].First(c => (string)c["concept"] == "object")["arguments"].First(a => (string)a["name"] == "object");
            CollectionAssert.Contains(obj["commands"].Select(c => (string)c).ToList(), "simulean_set");
            Assert.IsNotNull(v["paths"]); Assert.IsNotNull(v["statistics"]["element"]); Assert.IsNotNull(v["catalog"]["team"]);
        }

        [Test]
        public void TheVocabularyAnswersAnIndexAndItsSectionsTogetherAreTheWhole()
        {
            var index = J(SimuLeanCommands.VocabularyOf(""));
            Assert.LessOrEqual(SimuLeanCommands.AnswerSize(index), SimuLeanCommands.MaxAnswer, "the index fits");
            StringAssert.Contains("section=all", (string)index["ask"]);
            var names = index["sections"].Select(e => (string)e["section"]).ToList();
            CollectionAssert.AreEqual(SimuLean.Optimization.Vocabulary.Sections.Select(x => x.name).ToList(), names, "every section, in order");
            var all = J(SimuLeanCommands.VocabularyOf("", "all"));
            var merged = new JObject();
            foreach (string n in names)
            {
                var one = J(SimuLeanCommands.VocabularyOf("", n));
                // a section alone is read whole, or its parts are listed and each is
                var entry = index["sections"].First(e => (string)e["section"] == n);
                var section = new JObject { [n] = one[n] };
                if (entry["parts"] == null) Assert.LessOrEqual(SimuLeanCommands.AnswerSize(section), SimuLeanCommands.MaxAnswer, n + " fits");
                else foreach (string part in entry["parts"].Select(x => (string)x))
                    Assert.LessOrEqual(SimuLeanCommands.AnswerSize(new JObject { [n] = J(SimuLeanCommands.VocabularyOf("", n + "." + part))[n] }), SimuLeanCommands.MaxAnswer, n + "." + part + " fits");
                merged[n] = one[n];
            }
            all.Remove("errors"); all.Remove("warnings");
            Assert.IsTrue(JToken.DeepEquals(merged, all), "the sections together are the whole vocabulary");
            TestContext.WriteLine($"vocabulary: whole {SimuLeanCommands.AnswerSize(all)}, index {SimuLeanCommands.AnswerSize(index)}; " +
                                  string.Join(", ", index["sections"].Select(e => $"{e["section"]} {e["size"]}")));
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.VocabularyOf("", "nothing"));
        }

        // the arguments of a page's next: "simulean_results action=read resultSet=last detail=full top=5 offset=10 sort=-out"
        private static (string detail, int top, int offset, string sort) NextOf(string next)
        {
            int Int(string key) => int.Parse(System.Text.RegularExpressions.Regex.Match(next, @"\b" + key + @"=(\d+)").Groups[1].Value);
            var sort = System.Text.RegularExpressions.Regex.Match(next, @"\bsort=(\S+)");
            return (next.Contains("detail=full") ? "full" : "", Int("top"), Int("offset"), sort.Success ? sort.Groups[1].Value : "");
        }

        [Test]
        public void ALargeStudyAnswerIsATableAndWalkingItsPagesGivesTheWholeAnswer()
        {
            Line("G");
            SimuLeanCommands.Set("GSrv", "delayStrategy.strategyType", "Exponential");
            SimuLeanCommands.Set("GSrv", "delayStrategy.exponentialRate", "2");
            const string factors = "GSrv.delayStrategy.exponentialRate=1.5|2|2.5|3;GBuf.capacity=1|2|3", metrics = "out=GSink.input;wait=GBuf.stay_avg";
            int saved = SimuLeanCommands.MaxAnswer;
            try
            {
                foreach (string detail in new[] { "metrics", "full" })
                {
                    SimuLeanCommands.MaxAnswer = 10_000_000;
                    var whole = J(SimuLeanCommands.RunHeadless(30, 2, 1, factors, metrics, Root, detail));
                    Assert.IsNull(whole["page"], "within the limit: the answer whole");
                    var scenarios = (JArray)whole["scenarios"];
                    Assert.AreEqual(12, scenarios.Count);
                    Assert.AreEqual(detail == "full", scenarios[0]["perRun"] != null, "perRun with detail=full only");

                    // over the limit: a table, the settings and the warnings, and how to read the rest
                    SimuLeanCommands.MaxAnswer = SimuLeanCommands.AnswerSize(whole) / 2;
                    var cut = J(SimuLeanCommands.RunHeadless(30, 2, 1, factors, metrics, Root, detail));
                    Assert.IsNull(cut["scenarios"], "no scenario list");
                    Assert.AreEqual((int)cut["page"]["shown"], ((JArray)cut["table"]["rows"]).Count, "a row per scenario shown");
                    Assert.AreEqual(12, (int)cut["page"]["scenarios"], "the scenarios in all");
                    Assert.IsTrue(JToken.DeepEquals(whole["settings"], cut["settings"]), "the settings");
                    var page = cut["page"];
                    Assert.IsTrue((bool)page["truncated"]);
                    StringAssert.Contains("detail=full", (string)page["note"]);
                    StringAssert.Contains("simulean_results action=export", (string)page["note"]);
                    string next = (string)page["next"];
                    StringAssert.StartsWith("simulean_results action=read resultSet=last", next);
                    Assert.AreEqual(detail == "full", next.Contains("detail=full"), "the pages keep the detail: " + next);

                    // the pages, read from the last result set without running again, give every scenario as the whole answer
                    var walked = new System.Collections.Generic.List<JToken>();
                    for (int guard = 0; next != null && guard < 50; guard++)
                    {
                        var (d, top, offset, sort) = NextOf(next);
                        var p = J(SimuLeanCommands.Results("read", "last", detail: d, top: top, sort: sort, offset: offset));
                        Assert.LessOrEqual(SimuLeanCommands.AnswerSize(p), SimuLeanCommands.MaxAnswer + 2000, "a page fits");
                        walked.AddRange((JArray)p["scenarios"]);
                        next = (string)p["page"]["next"];
                    }
                    Assert.AreEqual(12, walked.Count, "every scenario once");
                    foreach (var sc in walked)
                        Assert.IsTrue(JToken.DeepEquals(sc, scenarios.First(x => (string)x["scenario"] == (string)sc["scenario"])),
                                      $"{detail}: {sc["scenario"]} as the whole answer gives it");
                }

                // a background run's answer is paged the same
                SimuLeanCommands.MaxAnswer = 10_000_000;
                SimuLeanCommands.RunStart(30, 2, 1, factors, metrics, Root, "metrics");
                Assert.IsTrue(SimuLeanCommands.WaitForBackgroundRun(60), "the background run ends within 60 s");
                var status = J(SimuLeanCommands.RunStatus(top: 5, sort: "-out", offset: 10).Result);
                Assert.AreEqual("completed", (string)status["status"]);
                var result = status["result"];
                Assert.AreEqual(2, ((JArray)result["scenarios"]).Count, "the last 2 of 12");
                Assert.AreEqual(12, (int)result["page"]["scenarios"]);
                Assert.AreEqual("out descending", (string)result["page"]["sort"]);
                Assert.IsNull(result["page"]["next"], "the last page has no next");
                Assert.Throws<ArgumentException>(() => SimuLeanCommands.Results("read", "last", sort: "nothing"), "an unknown sort metric");
            }
            finally { SimuLeanCommands.MaxAnswer = saved; }
        }
}
}
