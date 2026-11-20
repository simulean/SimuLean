using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SimuLean.Results;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Command fixes: type names matched loosely and several at once, a compact <c>simulean_apply</c> answer, the time unit of
    /// a new model and the run settings in <c>simulean_validate</c>, a rack's lanes following its layout, a milk run's empties
    /// origin that empties carriers refused and a pull query set as one text. Each test builds in a scene of its own.
    /// </summary>
    public class AgentEditCommandsTests : SceneTestFixture
    {
        private const string Root = "EditModel";
        protected override string RootName => Root;

        [SetUp]
        public void SetUp()
        {
            ResultsOutput.StoreOverride = Path.GetFullPath($"Temp/simulean_store_test_{Guid.NewGuid():N}.db");
        }

        [TearDown]
        public void TearDown()
        {
            ResultsOutput.StoreOverride = null;
        }

        private static JObject J(object answer) => JObject.FromObject(answer);

        [Test]
        public void TypeNamesMatchInAnyFormAndSeveralAtOnce()
        {
            foreach (string name in new[] { "MultiServer", "multi server", "multi-server", "UnityMultiServer", "Multi-Server" })
                Assert.AreEqual("Multi-Server", (string)J(SimuLeanCommands.Types(name))["name"], name);
            Assert.AreEqual("Storage Rack", (string)J(SimuLeanCommands.Types("storagerack"))["name"]);
            var several = J(SimuLeanCommands.Types("Queue, Sink,MultiServer"));
            CollectionAssert.AreEqual(new[] { "Queue", "Sink", "Multi-Server" }, several["types"].Select(t => (string)t["name"]).ToArray());
            var oneBad = J(SimuLeanCommands.Types("Queue,Nothingness"));
            Assert.IsNotNull(oneBad["types"][1]["error"], "a bad name in a list answers its error beside the others");
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Types("Qeueu"));
            StringAssert.Contains("Queue", e.Message, "a did-you-mean");
            Assert.Less(e.Message.Length, 400, "not the whole Library: " + e.Message);
        }

        [Test]
        public void SeveralTypesWithAFieldFilterListWhatEachHasAndALargeAnswerIsCut()
        {
            var filtered = J(SimuLeanCommands.Types("Sink,MultiServer", "compact", "delayStrategy"));
            var sink = filtered["types"][0];
            Assert.IsNull(sink["error"], "a type without the field is no error: " + sink);
            Assert.AreEqual(0, ((JArray)sink["fields"]).Count, "it lists no field: " + sink);
            StringAssert.Contains("no field under 'delayStrategy'", (string)sink["note"]);
            Assert.Greater(((JArray)filtered["types"][1]["fields"]).Count, 0, "the type that has it lists it");

            int saved = SimuLeanCommands.MaxAnswer;
            try
            {
                SimuLeanCommands.MaxAnswer = 1000;
                var cut = J(SimuLeanCommands.Types("Queue,Sink,MultiServer", "full"));
                var types = (JArray)cut["types"];
                Assert.IsNotNull(types[0]["fields"], "the first type in full");
                Assert.IsNull(types[2]["fields"], "a later one by name and description: " + types[2]);
                Assert.AreEqual("Multi-Server", (string)types[2]["name"]);
                StringAssert.Contains("ask them one at a time or with fields=", (string)cut["note"]);
                StringAssert.Contains("Multi-Server", (string)cut["note"]);
            }
            finally { SimuLeanCommands.MaxAnswer = saved; }
            Assert.IsNull(J(SimuLeanCommands.Types("Queue,Sink"))["note"], "a small answer is whole");
        }

        [Test]
        public void ALongTypeListingIsCutSayingHowToReadTheRestAndEveryTypeAnswerFits()
        {
            var whole = J(SimuLeanCommands.Types("", pack: "all"));
            int entries = ((JArray)whole["types"]).Count;
            Assert.IsNull(whole["note"], "pack=all fits: " + SimuLeanCommands.AnswerSize(whole));
            int saved = SimuLeanCommands.MaxAnswer;
            try
            {
                SimuLeanCommands.MaxAnswer = 6000;
                var cut = J(SimuLeanCommands.Types("", pack: "all"));
                var types = (JArray)cut["types"];
                Assert.AreEqual(entries, types.Count, "every entry is listed, the later ones by name, pack and group");
                Assert.IsNotNull(types[0]["description"], "the first ones whole");
                Assert.IsNull(types[entries - 1]["description"], "the last by name: " + types[entries - 1]);
                Assert.IsNotNull(types[entries - 1]["pack"]);
                StringAssert.Contains("pack=<pack>", (string)cut["note"]);
                StringAssert.Contains($"of {entries} entries", (string)cut["note"]);
            }
            finally { SimuLeanCommands.MaxAnswer = saved; }

            // what an agent's tool reads whole: the default list, pack=all and each type alone with detail=full
            Assert.LessOrEqual(SimuLeanCommands.AnswerSize(J(SimuLeanCommands.Types())), SimuLeanCommands.MaxAnswer, "the default list");
            Assert.LessOrEqual(SimuLeanCommands.AnswerSize(whole), SimuLeanCommands.MaxAnswer, "pack=all");
            var sizes = whole["types"].Select(t => (string)t["name"]).Distinct()
                                      .Select(n => (name: n, size: SimuLeanCommands.AnswerSize(J(SimuLeanCommands.Types(n, "full"))))).ToList();
            var large = sizes.Where(x => x.size > SimuLeanCommands.MaxAnswer).ToList();
            Assert.IsEmpty(large, "a type answer over the limit: " + string.Join(", ", large.Select(x => $"{x.name} {x.size}")));
            TestContext.WriteLine($"types: list {SimuLeanCommands.AnswerSize(J(SimuLeanCommands.Types()))}, pack=all {SimuLeanCommands.AnswerSize(whole)}, the largest type with detail=full " +
                                  string.Join(", ", sizes.OrderByDescending(x => x.size).Take(3).Select(x => $"{x.name} {x.size}")));
        }

        [Test]
        public void ARunAnswerGivesTheModelsWarningsOncePerCause()
        {
            // a Combiner without batchMode detaches what it packs: the Separator gets empty carriers and warns (once per run)
            RandomProcessConfig Constant(float v) => new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = v };
            var mains = Test.Make<UnitySource>("MwMains");
            mains.mode = SourceMode.InterArrival;
            mains.interArrivalDistribution = Constant(5);
            var parts = Test.Make<UnitySource>("MwParts");
            parts.mode = SourceMode.InterArrival;
            parts.interArrivalDistribution = Constant(2);
            var comb = Test.Make<UnityCombiner>("MwPack");
            comb.requirements = new[] { 2 };
            comb.batchMode = false;
            comb.delayStrategy = Constant(1);
            comb.inputOrder = new System.Collections.Generic.List<SElement> { mains, parts };
            var sep = Test.Make<UnitySeparator>("MwUnpack");
            sep.useProcessTime = true;
            sep.processTime = Constant(1);
            var partsOut = Test.Make<UnitySink>("MwPartsOut");
            var mainsOut = Test.Make<UnitySink>("MwMainsOut");
            mains.nextElements = new System.Collections.Generic.List<SElement> { comb };
            parts.nextElements = new System.Collections.Generic.List<SElement> { comb };
            comb.nextElements = new System.Collections.Generic.List<SElement> { sep };
            sep.nextElements = new System.Collections.Generic.List<SElement> { partsOut };
            sep.emptiesDestination = mainsOut;
            var run = J(SimuLeanCommands.RunHeadless(100, 3, 1, "", "pieces=MwPartsOut.input", Root, "metrics"));
            var empty = run["warnings"].Select(w => (string)w).Where(w => w.Contains("with nothing in it")).ToList();
            Assert.AreEqual(1, empty.Count, "one line for the three runs: " + run["warnings"]);
            StringAssert.Contains("batchMode", empty[0]);
            Assert.AreEqual(0, (double)run["scenarios"][0]["metrics"]["pieces"]["mean"], "the pieces never reach the line");
        }

        [Test]
        public void DeleteRemovesOnlyWhatTheCommandsWroteInThisSession()
        {
            SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps("Dl", Root));
            SimuLeanCommands.RunHeadless(20, 1, 1, "", "out=DlSink.input", Root, "metrics");
            // an export the command wrote: to the trash
            var ex = J(SimuLeanCommands.Results(action: "export", formats: "json"));
            string file = (string)ex["exported"][0];
            Assert.IsTrue(File.Exists(file), ex.ToString());
            // the trash replaced: a test run sends nothing to the machine's recycle bin
            string bin = Path.GetFullPath($"Temp/simulean_trash_test_{Guid.NewGuid():N}");
            var trash = SimuLeanCommands.MoveToTrash;
            JObject del;
            try
            {
                SimuLeanCommands.MoveToTrash = f => { Directory.CreateDirectory(bin); File.Move(f, Path.Combine(bin, Path.GetFileName(f))); return "the test bin"; };
                del = J(SimuLeanCommands.Results(action: "delete", path: file));
            }
            finally { SimuLeanCommands.MoveToTrash = trash; }
            Assert.IsFalse(File.Exists(file), "deleted: " + del);
            Assert.AreEqual("the test bin", (string)del["to"], del.ToString());
            Assert.IsTrue(File.Exists(Path.Combine(bin, Path.GetFileName(file))), "moved by the replaced trash");
            Directory.Delete(bin, true);
            // a file the commands did not write, under the same folder, and a file elsewhere: refused, kept
            string mine = Path.Combine(ResultsOutput.Folder(ResultsOutput.ProjectRoot), $"user_{Guid.NewGuid():N}.txt");
            File.WriteAllText(mine, "the user's");
            try
            {
                var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Results(action: "delete", path: mine));
                StringAssert.Contains("not written by these commands", e.Message);
                Assert.IsTrue(File.Exists(mine), "kept");
                e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Results(action: "delete", path: "Assets/SimuLean.Unity/Elements/UnityPickFace.cs"));
                StringAssert.Contains("not inside the results folder or Recordings", e.Message);
            }
            finally { File.Delete(mine); }
            // a set the command stored: removed from the store; one stored otherwise: refused
            int saved = (int)J(SimuLeanCommands.Results(action: "save", name: "DlSaved"))["saved"];
            var gone = J(SimuLeanCommands.Results(action: "delete", resultSet: saved.ToString()));
            StringAssert.Contains("DlSaved", (string)gone["deleted"]);
            Assert.IsFalse(ResultsOutput.Store(ResultsOutput.ProjectRoot).List().Any(s => s.Id == saved), "removed from the store");
            var other = SimuLeanCommands.ResultSetOf("last");
            other.StoreId = 0;
            int theirs = ResultsOutput.Save(other, ResultsOutput.ProjectRoot);
            var refused = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Results(action: "delete", resultSet: theirs.ToString()));
            StringAssert.Contains("not stored by simulean_results action=save", refused.Message);
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Results(action: "delete"), "nothing named");
        }

        [Test]
        public void TypesGivesTheInspectorTooltipsOnlyWhenAsked()
        {
            var compact = J(SimuLeanCommands.Types("Pick Face"));
            Assert.IsNull(compact["tips"], "no tooltips in a compact answer: " + compact["help"]);
            StringAssert.Contains("discarded at once", (string)compact["help"]["emptiesDestination"], "a Pick Face's notes are help");
            StringAssert.Contains("starts empty", (string)compact["help"]["startFull"]);
            StringAssert.Contains("reorderQuantity", (string)compact["help"]["reorderPoint"]);
            var asked = J(SimuLeanCommands.Types("Pick Face", "compact", "sku,emptiesDestination"));
            StringAssert.Contains("SKU", (string)asked["tips"]["sku"], "fields= gives the tooltips: " + asked);
            Assert.IsNull(asked["tips"]["emptiesDestination"], "a field with a help note has no tip");
            var full = J(SimuLeanCommands.Types("Pick Face", "full"));
            StringAssert.Contains("open", (string)full["tips"]["positions"], "detail=full gives them too");
        }

        [Test]
        public void TextbookNamesOfOtherToolsPointToTheSimuLeanEntryOrField()
        {
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Types("SingleServer"));
            StringAssert.Contains("Multi-Server", e.Message, "a server is a Multi-Server");
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Types("Router"));
            StringAssert.Contains("outputStrategy", e.Message, "routing is an element's output strategy");
            SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps("H", Root));
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("HSrv", "servers", "2"));
            StringAssert.Contains("'capacity'", e.Message, "servers = capacity");
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("HSrv", "x", "3"));
            StringAssert.Contains("not a field", e.Message, "the place is not a field, and the answer says how to move it");
        }

        [Test]
        public void ACombinersInputOrderSetBeforeItsConnectionsIsKept()
        {
            SimuLeanCommands.Apply(@"[
                {""op"": ""add"", ""type"": ""Source"", ""name"": ""IoTotes"", ""parent"": """ + Root + @"""},
                {""op"": ""add"", ""type"": ""Source"", ""name"": ""IoFace"", ""z"": 3, ""parent"": """ + Root + @"""},
                {""op"": ""add"", ""type"": ""Combiner"", ""name"": ""IoPack"", ""x"": 4, ""parent"": """ + Root + @"""},
                {""op"": ""set"", ""object"": ""IoPack"", ""key"": ""inputOrder"", ""value"": ""IoTotes,IoFace""},
                {""op"": ""connect"", ""from"": ""IoFace"", ""to"": ""IoPack""},
                {""op"": ""connect"", ""from"": ""IoTotes"", ""to"": ""IoPack""}]");
            var pack = GameObject.Find("IoPack").GetComponent<UnityCombiner>();
            CollectionAssert.AreEqual(new[] { "IoTotes", "IoFace" }, pack.ResolveInputOrder().Select(e => e.name).ToArray(),
                                      "the list set first decides, not the order of the connections");
            SimuLeanCommands.Apply(@"[
                {""op"": ""add"", ""type"": ""Source"", ""name"": ""IoA"", ""z"": 6, ""parent"": """ + Root + @"""},
                {""op"": ""add"", ""type"": ""Source"", ""name"": ""IoB"", ""z"": 9, ""parent"": """ + Root + @"""},
                {""op"": ""add"", ""type"": ""Combiner"", ""name"": ""IoPack2"", ""x"": 4, ""z"": 6, ""parent"": """ + Root + @"""},
                {""op"": ""connect"", ""from"": ""IoB"", ""to"": ""IoPack2""},
                {""op"": ""connect"", ""from"": ""IoA"", ""to"": ""IoPack2""}]");
            CollectionAssert.AreEqual(new[] { "IoB", "IoA" },
                                      GameObject.Find("IoPack2").GetComponent<UnityCombiner>().ResolveInputOrder().Select(e => e.name).ToArray(),
                                      "without a list, the first one connected is the main input");
        }

        [Test]
        public void ApplyListsTheTeamsItCreatedWithTheirSpeed()
        {
            var a = J(SimuLeanCommands.Apply(@"[{""op"": ""add_team"", ""name"": ""CrTeam"", ""members"": 2, ""kind"": ""operator""}]"));
            CollectionAssert.Contains(a["created"].Select(x => (string)x).ToArray(), "CrTeam", "a team is created too: " + a);
            var full = J(SimuLeanCommands.Apply(@"[{""op"": ""add_team"", ""name"": ""CrTeam2"", ""members"": 1, ""kind"": ""operator""}]", false, "full"));
            StringAssert.Contains("operator", (string)full["results"][0]["speedFrom"], "the kind that set the speed: " + full);
        }

        [Test]
        public void ApplySaysWhenANameIsCreatedLaterInTheList()
        {
            SimuLeanCommands.Apply(@"[{""op"": ""add"", ""type"": ""Multi-Server"", ""name"": ""FrSrv"", ""parent"": """ + Root + @"""}]");
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Apply(@"[
                {""op"": ""set"", ""object"": ""FrSrv"", ""key"": ""processBy.team"", ""value"": ""FrTeam""},
                {""op"": ""add_team"", ""name"": ""FrTeam"", ""members"": 1}]"));
            StringAssert.Contains("created later in this list (operation 2)", e.Message);
        }

        [Test]
        public void SetHintsAtTheListFieldsAndRuleParametersAndEchoesADistributionAsText()
        {
            SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps("Sh", Root));
            var list = new GameObject("ShStock").AddComponent<UnitySimList>();
            list.transform.SetParent(GameObject.Find(Root).transform);
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("ShSrv", "sendTo", "ShStock"));
            StringAssert.Contains("'ShStock' is the list: set sendTo = List and pushToList = ShStock", e.Message);
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("ShSrv", "receiveFrom", "ShStock"));
            StringAssert.Contains("pullFromList = ShStock", e.Message);
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("ShSrv", "rule.FreeWeight", "0.5"));
            StringAssert.Contains("outputStrategy.ruleParameters", e.Message);
            var set = J(SimuLeanCommands.Set("ShSrv", "delayStrategy", "Triangular~3~4~6"));
            Assert.AreEqual("Triangular~3~4~6", (string)set["value"], "the distribution's text, not its type: " + set);
        }

        [Test]
        public void SettingAConveyorsLaneLengthRefitsItsRollers()
        {
            var entry = SimuLeanWindow.CollectEntries().First(x => x.Name == "Conveyor");
            // under the test scene's root, so the undo revert at the end removes it whole
            var go = SimuLeanWindow.Place(entry, Vector3.zero, Test.Root.transform);
            go.name = "CvRollers";
            var rollers = go.GetComponentInChildren<UnityRollerConveyor>();
            Assert.IsNotNull(rollers);
            SimuLeanCommands.Set("CvRollers", "laneLengths", "12");
            Assert.AreEqual(12f, rollers.length, 1e-4f, "the rollers follow the lane's length");
            Assert.AreEqual(-12f, rollers.transform.localPosition.x, 1e-4f, "from the exit backwards");
        }

        [Test]
        public void ParamsShowATeamReferenceByItsName()
        {
            SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps("Pn", Root));
            SimuLeanCommands.Apply(@"[{""op"": ""add_team"", ""name"": ""PnTeam"", ""members"": 1, ""parent"": """ + Root + @"""}]");
            SimuLeanCommands.Set("PnSrv", "processBy.team", "PnTeam");
            var p = J(SimuLeanCommands.Params("PnSrv", fields: "processBy"));
            Assert.AreEqual("PnTeam", (string)p["catalog"]["processBy.team"], "the team by its name, not its internal id: " + p["catalog"]);
        }

        [Test]
        public void SetWithAnEmptyValueClearsTheFieldAndWithoutOneIsRefused()
        {
            SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps("Cv", Root));
            SimuLeanCommands.Apply(@"[{""op"": ""add_team"", ""name"": ""CvTeam"", ""members"": 1, ""parent"": """ + Root + @"""}]");
            var src = GameObject.Find("CvSrc").GetComponent<UnitySource>();
            var srv = GameObject.Find("CvSrv").GetComponent<UnityMultiServer>();
            SimuLeanCommands.Set("CvSrc", "createLabels", "prio = 1, kind = 'A'");
            var sink = GameObject.Find("CvSink").GetComponent<UnitySink>();
            Assert.IsNotEmpty(sink.positionLabels);
            SimuLeanCommands.Set("CvSrv", "processBy.team", "CvTeam");
            Assert.IsNotNull(srv.processBy.team);
            SimuLeanCommands.Set("CvSrc", "createLabels", "");
            CollectionAssert.IsEmpty(src.createLabels, "\"\" empties a list");
            SimuLeanCommands.Set("CvSink", "positionLabels", null);
            Assert.AreEqual("", sink.positionLabels, "null empties a text");
            SimuLeanCommands.Set("CvSrv", "processBy.team", "");
            Assert.IsNull(srv.processBy.team, "\"\" clears an object reference");
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("CvSink", "positionLabels", SimuLeanCommands.NoValue));
            StringAssert.Contains("value is required", e.Message, "a missing value is refused, not taken as empty");
        }

        [Test]
        public void AMilkRunFlowsLoadTimeTurnsItsOverrideOn()
        {
            SimuLeanCommands.Apply(@"[{""op"": ""add"", ""type"": ""Milk Run"", ""name"": ""LtTrain"", ""parent"": """ + Root + @"""}]");
            var answer = J(SimuLeanCommands.Set("LtTrain", "flows[0].loadTime", "10"));
            var train = GameObject.Find("LtTrain").GetComponent<UnityMilkRun>();
            Assert.IsTrue(train.flows[0].overrideTimes, "the flow's time is used: " + answer);
            StringAssert.Contains("overrideTimes", (string)answer["alsoSet"]);
        }

        [Test]
        public void ApplyAnswersASummaryUnlessFull()
        {
            var a = J(SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps("S", Root)));
            Assert.AreEqual(12, (int)a["applied"]);
            Assert.IsNull(a["results"], "no line per operation: " + a);
            Assert.AreEqual(4, (int)a["operations"]["add"]);
            Assert.AreEqual(5, (int)a["operations"]["set"]);
            CollectionAssert.AreEquivalent(new[] { "SSrc", "SBuf", "SSrv", "SSink" }, a["created"].Select(x => (string)x).ToArray());
            var full = J(SimuLeanCommands.Apply(@"[{""op"": ""set"", ""object"": ""SBuf"", ""key"": ""capacity"", ""value"": 3}]", false, "full"));
            Assert.AreEqual(1, full["results"].Count(), "detail=full: one result per operation");
            var check = J(SimuLeanCommands.Apply(@"[{""op"": ""set"", ""object"": ""SBuf"", ""key"": ""capacity"", ""value"": 4}]", true));
            Assert.IsTrue((bool)check["valid"]);
            Assert.AreEqual(1, (int)check["operations"]["set"]);
        }

        [Test]
        public void ATeamsSizeIsSetByANumber()
        {
            SimuLeanCommands.Apply(@"[{""op"": ""add_team"", ""name"": ""SizeTeam"", ""members"": 2, ""kind"": ""operator"", ""parent"": """ + Root + @"""}]");
            var team = GameObject.Find("SizeTeam").GetComponent<UnityResourceTeam>();
            team.members[1].properties.Add("skill = 'weld'");
            SimuLeanCommands.Set("SizeTeam", "members", "3");
            CollectionAssert.AreEqual(new[] { "SizeTeam_1", "SizeTeam_2", "SizeTeam_3" }, team.members.Select(m => m.name).ToArray(),
                                      "the new member continues the numbered names");
            CollectionAssert.AreEqual(new[] { "skill = 'weld'" }, team.members[2].properties, "and copies the last member's properties");
            SimuLeanCommands.Apply(@"[{""op"": ""set"", ""object"": ""SizeTeam"", ""key"": ""members"", ""value"": 1}]");
            Assert.AreEqual(1, team.members.Count, "a smaller number keeps the first members");
            Assert.AreEqual("SizeTeam_1", team.members[0].name);
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("SizeTeam", "members", "0"));
        }

        [Test]
        public void AStudyVariesTheStopModeOfATeamsTimetable()
        {
            SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps("D", Root));
            SimuLeanCommands.Apply(@"[
                {""op"": ""add_team"", ""name"": ""DTeam"", ""members"": 1, ""travel"": ""Abstract"", ""parent"": """ + Root + @"""},
                {""op"": ""set"", ""object"": ""DSrv"", ""key"": ""delayStrategy.constantValue"", ""value"": 4},
                {""op"": ""set"", ""object"": ""DSrv"", ""key"": ""processBy.team"", ""value"": ""DTeam""},
                {""op"": ""set"", ""object"": ""DSrv"", ""key"": ""processBy.preemption"", ""value"": ""Resume""},
                {""op"": ""add"", ""type"": ""Downtime"", ""name"": ""DBreak"", ""parent"": """ + Root + @"""},
                {""op"": ""set"", ""object"": ""DBreak"", ""key"": ""kind"", ""value"": ""Timetable""},
                {""op"": ""set"", ""object"": ""DBreak"", ""key"": ""teamTargets"", ""value"": ""DTeam""},
                {""op"": ""set"", ""object"": ""DBreak"", ""key"": ""rows[0].start"", ""value"": ""6""},
                {""op"": ""set"", ""object"": ""DBreak"", ""key"": ""rows[0].duration"", ""value"": 3},
                {""op"": ""set"", ""object"": ""DBreak"", ""key"": ""mode"", ""value"": ""Immediate""}]");
            // the rows of the timetable carry no mode of their own, so the study's value reaches them
            var run = J(SimuLeanCommands.RunHeadless(20, 1, 1, "downtime:DBreak.mode=Immediate|AfterCurrent", "intr=DSrv.interruptions", Root, "metrics"));
            var sc = run["scenarios"];
            Assert.AreEqual(2, sc.Count(), run.ToString());
            Assert.Greater((double)sc[0]["metrics"]["intr"]["mean"], 0, "Immediate interrupts the job in progress: " + run);
            Assert.AreEqual(0, (double)sc[1]["metrics"]["intr"]["mean"], "AfterCurrent lets it finish: " + run);
        }

        [Test]
        public void ValidateEchoesTheRunSettings()
        {
            SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps("V", Root));
            var v = J(SimuLeanCommands.Validate(root: Root));
            Assert.IsTrue((bool)v["ok"], v.ToString());
            Assert.IsNotNull(v["settings"]?["timeUnit"], "the time unit: " + v);
            Assert.IsNotNull(v["settings"]["horizon"]);
            Assert.IsNotNull(v["settings"]["warmup"]);
            Assert.IsNotNull(v["info"], "the info list");
        }

        [Test]
        public void TheSettingsEchoTheReplicationsThatRun()
        {
            SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps("V", Root));
            var v = J(SimuLeanCommands.Validate(root: Root, replications: 5, seed: 3));
            Assert.AreEqual(5, (int)v["settings"]["replications"], "validate takes the run's replications: " + v);
            Assert.AreEqual("3..7", (string)v["settings"]["seeds"]);

            // scenarios with their own replications run those, so the echo says them
            var same = J(SimuLeanCommands.Validate(root: Root, scenarios: @"[{""name"": ""a"", ""replications"": 3}, {""name"": ""b"", ""replications"": 3}]"));
            Assert.AreEqual(3, (int)same["settings"]["replications"], same.ToString());
            Assert.AreEqual("1..3", (string)same["settings"]["seeds"]);
            Assert.IsNull(same["settings"]["replicationsPerScenario"], "one count when they agree");

            var run = J(SimuLeanCommands.RunHeadless(20, 1, 1, "", "out=VSink.input", Root, "metrics",
                                                     @"[{""name"": ""a"", ""replications"": 2}, {""name"": ""b"", ""replications"": 4}]"));
            Assert.AreEqual(4, (int)run["settings"]["replications"], run.ToString());
            Assert.AreEqual(2, (int)run["settings"]["replicationsPerScenario"]["a"]);
            Assert.AreEqual(4, (int)run["settings"]["replicationsPerScenario"]["b"]);
        }

        [Test]
        public void ARacksLanesFollowItsLayout()
        {
            SimuLeanCommands.AddElement("Pallet Rack", "EditRack", 0, 0, 0, Root);
            var rack = GameObject.Find("EditRack").GetComponent<UnityStorageRack>();
            Assert.AreEqual(rack.bays * rack.slotsPerBay * rack.levels, rack.lanes.Count);
            var set = J(SimuLeanCommands.Set("EditRack", "levels", "2"));
            Assert.AreEqual(rack.bays * rack.slotsPerBay * 2, rack.lanes.Count, "simulean_set rebuilds the lanes");
            Assert.IsNotNull((string)set["rebuilt"]);
            Assert.IsTrue(rack.lanes.All(l => l.laneId.EndsWith("_L0") || l.laneId.EndsWith("_L1")), "levels counts from L0");
            var a = J(SimuLeanCommands.Apply(@"[{""op"": ""set"", ""object"": ""EditRack"", ""key"": ""bays"", ""value"": 2},
                                               {""op"": ""set"", ""object"": ""EditRack"", ""key"": ""slotsPerBay"", ""value"": 2}]"));
            Assert.AreEqual(8, rack.lanes.Count, "simulean_apply rebuilds once at its end");
            Assert.AreEqual(1, a["rebuilt"].Count());
            Assert.IsNull(rack.LayoutMismatch());
            rack.levels = 3;   // as the inspector without Rebuild
            StringAssert.Contains("Rebuild", J(SimuLeanCommands.Validate(root: Root))["warnings"].ToString(), "stale lanes are a warning");
        }

        [Test]
        public void AnEmptiesOriginThatEmptiesCarriersIsAnError()
        {
            SimuLeanCommands.Apply(@"[{""op"": ""add"", ""type"": ""Line-Side Unpacker"", ""name"": ""UnpackLine"", ""parent"": """ + Root + @"""},
                {""op"": ""add"", ""type"": ""Sink"", ""name"": ""EmptiesSink"", ""x"": 4, ""parent"": """ + Root + @"""},
                {""op"": ""add"", ""type"": ""Milk Run"", ""name"": ""MilkTrain"", ""x"": 8, ""parent"": """ + Root + @"""},
                {""op"": ""add_team"", ""name"": ""TrainTuggers"", ""members"": 1, ""kind"": ""tugger"", ""parent"": """ + Root + @"""},
                {""op"": ""set"", ""object"": ""MilkTrain"", ""key"": ""transportBy.team"", ""value"": ""TrainTuggers""},
                {""op"": ""set"", ""object"": ""MilkTrain"", ""key"": ""emptiesDestination"", ""value"": ""EmptiesSink""},
                {""op"": ""set"", ""object"": ""MilkTrain"", ""key"": ""stops[0].name"", ""value"": ""Line""},
                {""op"": ""set"", ""object"": ""MilkTrain"", ""key"": ""stops[0].emptiesOrigins"", ""value"": ""UnpackLine""}]");
            var v = J(SimuLeanCommands.Validate(root: Root));
            Assert.IsFalse((bool)v["ok"]);
            StringAssert.Contains("empties origin", v["errors"].ToString());
            StringAssert.Contains("emptiesDestination", v["errors"].ToString(), "the pattern");
            var help = J(SimuLeanCommands.Types("Milk Run"))["help"];
            StringAssert.Contains("Queue", (string)help["stops[i].emptiesOrigins"], "the field says so");
        }

        [Test]
        public void APullQueryIsSetAsOneText()
        {
            SimuLeanCommands.AddElement("Pick Face", "QueryFace", 0, 0, 0, Root);
            SimuLeanCommands.Set("QueryFace", "pullQuery", "sku == 'A'; qty > 0 | due desc");
            var q = GameObject.Find("QueryFace").GetComponent<SElement>().pullQuery;
            Assert.AreEqual(2, q.conditions.Count);
            Assert.AreEqual("sku", q.conditions[0].field);
            Assert.AreEqual("A", q.conditions[0].value);
            Assert.AreEqual(SimuLean.Collections.ListOp.Greater, q.conditions[1].op);
            Assert.AreEqual("due", q.orderBy);
            Assert.IsTrue(q.descending);
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("QueryFace", "pullQuery", "sku == 'A' | a | b"));
        }
    }

    /// <summary>The time unit of a new model: no test scene (a closed new model needs the open scene saved).</summary>
    public class NewModelTimeUnitTests
    {
        private static JObject J(object answer) => JObject.FromObject(answer);

        [Test]
        public void TheTimeUnitOfANewModelIsChosen()
        {
            Assert.AreEqual(1, SimuLeanCommands.TimeUnitSeconds(""));
            Assert.AreEqual(60, SimuLeanCommands.TimeUnitSeconds("min"));
            Assert.AreEqual(3600, SimuLeanCommands.TimeUnitSeconds("h"));
            Assert.AreEqual(86400, SimuLeanCommands.TimeUnitSeconds("days"));
            Assert.AreEqual(0.5, SimuLeanCommands.TimeUnitSeconds("0.5"));
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.TimeUnitSeconds("fortnight"));
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Models("open", "x", timeUnit: "min"), "only for action=new");

            // the clock of the new scene gets them (the command's step between the set-up and the save)
            var host = new GameObject("ClockHost");
            try
            {
                var clock = host.AddComponent<UnitySimClock>();
                Assert.AreEqual(480, SimuLeanCommands.SetClock(clock, 60, 480));
                Assert.AreEqual(60, clock.TimeUnitSeconds);
                Assert.AreEqual(480, clock.MaxTime);
                Assert.AreEqual(480, SimuLeanCommands.SetClock(clock, 3600, -1), "no horizon: the clock's stays");
                Assert.AreEqual(3600, clock.TimeUnitSeconds);
                // the seed (Fixed mode) and the warm-up of a new model; -1 leaves them
                SimuLeanCommands.SetClockRun(clock, 7, 60);
                Assert.AreEqual(SeedMode.Fixed, clock.SeedMode);
                Assert.AreEqual(7, clock.Seed);
                Assert.AreEqual(60, clock.WarmupTime);
                SimuLeanCommands.SetClockRun(clock, -1, -1);
                Assert.AreEqual(7, clock.Seed);
                Assert.AreEqual(60, clock.WarmupTime);
                Assert.Throws<ArgumentException>(() => SimuLeanCommands.Models("open", "x", seed: 3), "seed only for action=new");
            }
            finally { Object.DestroyImmediate(host); }
        }
    }
}
