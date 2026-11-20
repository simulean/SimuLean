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
    /// Building in bulk through the MCP — <c>simulean_apply</c> (a list of
    /// edits, all or nothing, one undo step), <c>simulean_repeat</c> (a group copied N times with its inner connections) and
    /// <c>simulean_invoke</c> (component actions). Each test builds its model in a scene of its own, closed at the end.
    /// </summary>
    public class AgentBulkCommandsTests : SceneTestFixture
    {
        private const string Root = "BulkModel";
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
        private static GameObject Go(string name) => GameObject.Find(name);
        private static SElement El(string name) => Go(name).GetComponent<SElement>();
        private static string[] Next(string name) => El(name).nextElements.Where(e => e != null).Select(e => e.name).ToArray();

        // Source (every 1) → Queue → Multi-Server (0.5) → Sink, as one list of operations
        internal static string LineOps(string p, string root = Root) => @"[
            {""op"": ""add"", ""type"": ""UnitySource"", ""name"": """ + p + @"Src"", ""parent"": """ + root + @"""},
            {""op"": ""add"", ""type"": ""UnityQueue"", ""name"": """ + p + @"Buf"", ""x"": 2, ""parent"": """ + root + @"""},
            {""op"": ""add"", ""type"": ""UnityMultiServer"", ""name"": """ + p + @"Srv"", ""x"": 4, ""parent"": """ + root + @"""},
            {""op"": ""add"", ""type"": ""UnitySink"", ""name"": """ + p + @"Sink"", ""x"": 6, ""parent"": """ + root + @"""},
            {""op"": ""set"", ""object"": """ + p + @"Src"", ""key"": ""mode"", ""value"": ""InterArrival""},
            {""op"": ""set"", ""object"": """ + p + @"Src"", ""key"": ""interArrivalDistribution.strategyType"", ""value"": ""Constant""},
            {""op"": ""set"", ""object"": """ + p + @"Src"", ""key"": ""interArrivalDistribution.constantValue"", ""value"": 1},
            {""op"": ""set"", ""object"": """ + p + @"Srv"", ""key"": ""delayStrategy.strategyType"", ""value"": ""Constant""},
            {""op"": ""set"", ""object"": """ + p + @"Srv"", ""key"": ""delayStrategy.constantValue"", ""value"": 0.5},
            {""op"": ""connect"", ""from"": """ + p + @"Src"", ""to"": """ + p + @"Buf""},
            {""op"": ""connect"", ""from"": """ + p + @"Buf"", ""to"": """ + p + @"Srv""},
            {""op"": ""connect"", ""from"": """ + p + @"Srv"", ""to"": """ + p + @"Sink""}
        ]";

        [Test]
        public void ApplyBuildsAModelInOneCallAndOneUndoStep()
        {
            var a = J(SimuLeanCommands.Apply(LineOps("A")));
            Assert.AreEqual(12, (int)a["applied"], a.ToString());
            Assert.AreEqual(3, (int)a["operations"]["connect"], "a summary: the operations per kind");
            CollectionAssert.Contains(a["created"].Select(x => (string)x).ToArray(), "ASink");
            CollectionAssert.AreEqual(new[] { "ABuf" }, Next("ASrc"));
            var run = J(SimuLeanCommands.RunHeadless(20, 1, 1, "", "out=ASink.input", Root, "metrics"));
            Assert.Greater((double)run["scenarios"][0]["metrics"]["out"]["mean"], 15, "the model built by the list runs");
            UnityEditor.Undo.PerformUndo();
            Assert.IsNull(Go("ASrc"), "one undo step removes the whole list");
            Assert.IsNull(Go("ASink"));
        }

        [Test]
        public void ApplyIsAllOrNothing()
        {
            string ops = @"[{""op"": ""add"", ""type"": ""UnityQueue"", ""name"": ""NQ"", ""parent"": """ + Root + @"""},
                            {""op"": ""add"", ""type"": ""UnitySink"", ""name"": ""NS"", ""parent"": """ + Root + @"""},
                            {""op"": ""connect"", ""from"": ""NQ"", ""to"": ""Nowhere""}]";
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Apply(ops));
            StringAssert.Contains("operation 3", e.Message, "the operation that failed");
            StringAssert.Contains("nothing was changed", e.Message);
            Assert.IsNull(Go("NQ"), "the operations before the error are undone");
            Assert.IsNull(Go("NS"));
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Apply(@"[{""op"": ""paint""}]"));
            StringAssert.Contains("add, set, connect", e.Message, "the operations there are");
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Apply(@"[{""op"": ""set"", ""object"": ""X""}]"));
            StringAssert.Contains("\"key\"", e.Message, "a missing field is named");
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Apply("{not json"));
        }

        [Test]
        public void AnApplyThatFailsLateUndoesOnlyItsOwnOperations()
        {
            // right after a successful apply, a second one failing on its 12th operation
            // left a connection of the first as an empty Next Elements entry (the property-diff undo restored it as none)
            SimuLeanCommands.Apply(LineOps("O"));
            string ops = @"[{""op"": ""add"", ""type"": ""UnityQueue"", ""name"": ""OExtra"", ""x"": 2, ""z"": 3, ""parent"": """ + Root + @"""},
                            {""op"": ""connect"", ""from"": ""OSrc"", ""to"": ""OExtra""}";
            for (int i = 0; i < 9; i++) ops += @", {""op"": ""set"", ""object"": ""OBuf"", ""key"": ""capacity"", ""value"": " + (i + 2) + "}";
            ops += @", {""op"": ""set"", ""object"": ""OBuf"", ""key"": ""noSuchField"", ""value"": 1}]";
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Apply(ops));
            StringAssert.Contains("operation 12", e.Message);
            Assert.IsNull(Go("OExtra"), "the failed apply's own element is undone");
            CollectionAssert.AreEqual(new[] { "OBuf" }, El("OSrc").nextElements.Select(x => x == null ? "<none>" : x.name).ToArray(),
                                      "the first apply's connection is kept as it was (not an empty entry)");
            CollectionAssert.AreEqual(new[] { "OSrv" }, Next("OBuf"));
            Assert.AreNotEqual(9, Go("OBuf").GetComponent<UnityQueue>().capacity, "the failed apply's sets are undone");
        }

        [Test]
        public void RepeatCopiesDecorationByTheNamePattern()
        {
            // decoration copies kept "(Clone)" names and the answer listed no copy
            var rack = new GameObject("DecoRack");
            rack.transform.SetParent(Test.Root.transform, false);
            new GameObject("Upright").transform.SetParent(rack.transform, false);
            UnityEditor.Undo.RegisterCreatedObjectUndo(rack, "test");
            var a = J(SimuLeanCommands.Repeat("DecoRack", 2, "0,0,3", "Rack_{i}"));
            var copies = a["copies"].Select(c => c.Select(x => (string)x).ToArray()).ToArray();
            CollectionAssert.AreEqual(new[] { "Rack_1" }, copies[0], a.ToString());
            CollectionAssert.AreEqual(new[] { "Rack_2" }, copies[1]);
            Assert.IsNotNull(Go("Rack_2"), "the copy carries the pattern's name");
            Assert.AreEqual("Upright", Go("Rack_2").transform.GetChild(0).name, "its parts keep their names");
            Assert.AreEqual(6f, Go("Rack_2").transform.position.z - rack.transform.position.z, 1e-4);
        }

        [Test]
        public void RepeatCopiesAGroupWithItsInnerConnections()
        {
            SimuLeanCommands.Apply(LineOps("R"));
            SimuLeanEditCommands.Disconnect("RSrc", "RBuf");
            SimuLeanEditCommands.Disconnect("RSrv", "RSink");
            // the group (queue → server) copied twice, each copy fed by the source and feeding the sink
            var a = J(SimuLeanCommands.Repeat("RBuf;RSrv", 2, "0,0,5", "", "RSrc>RBuf", "RSrv>RSink"));
            var copies = a["copies"].Select(c => c.Select(x => (string)x).OrderBy(x => x).ToArray()).ToArray();
            CollectionAssert.AreEqual(new[] { "RBuf_1", "RSrv_1" }, copies[0]);
            CollectionAssert.AreEqual(new[] { "RBuf_2", "RSrv_2" }, copies[1]);
            CollectionAssert.AreEqual(new[] { "RSrv_1" }, Next("RBuf_1"), "an inner connection points to the copy's own member");
            CollectionAssert.AreEqual(new[] { "RSrv_2" }, Next("RBuf_2"));
            CollectionAssert.AreEqual(new[] { "RSink" }, Next("RSrv_1"), "connectTo: each copy feeds the outside element");
            CollectionAssert.AreEquivalent(new[] { "RBuf_1", "RBuf_2" }, Next("RSrc"), "connectFrom: the outside element feeds each copy");
            CollectionAssert.IsEmpty(Next("RBuf").Except(new[] { "RSrv" }), "the original keeps its own connections");
            Assert.AreEqual(Go("RBuf").transform.position.z + 10, Go("RBuf_2").transform.position.z, 1e-4, "the offset per copy");
            var run = J(SimuLeanCommands.RunHeadless(20, 1, 1, "", "out=RSink.input", Root, "metrics"));
            Assert.Greater((double)run["scenarios"][0]["metrics"]["out"]["mean"], 15, "the items flow through the copies");
            UnityEditor.Undo.PerformUndo();
            Assert.IsNull(Go("RBuf_1"), "one undo step removes every copy");
            Assert.IsNull(Go("RSrv_2"));
        }

        [Test]
        public void RepeatChecksItsArguments()
        {
            SimuLeanCommands.Apply(LineOps("C"));
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Repeat("CBuf", 0), "at least one copy");
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Repeat("CBuf", 1, "", "", "CSrc>CSink"));
            StringAssert.Contains("not in the group", e.Message);
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Repeat("CBuf", 1, "", "{name}"));
            StringAssert.Contains("{i}", e.Message, "a pattern without the copy's number would repeat names");
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Repeat("CBuf", 1, "0,5"));
            StringAssert.Contains("x,y,z", e.Message);
            SimuLeanCommands.Repeat("CBuf", 1);
            Assert.IsNotNull(Go("CBuf_1"));
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Repeat("CBuf", 1), "a name already taken (CBuf_1) is refused");
        }

        [Test]
        public void CaptureRendersAPictureOfTheModelOrOfElements()
        {
            SimuLeanCommands.Apply(LineOps("P"));
            string file = System.IO.Path.GetFullPath($"Temp/simulean_capture_test_{Guid.NewGuid():N}.png");
            try
            {
                var all = J(SimuLeanCommands.Capture("", 40, 30, 0, 40, "", 320, 180, file));
                Assert.IsTrue(System.IO.File.Exists(file), all.ToString());
                var tex = new Texture2D(2, 2);
                Assert.IsTrue(tex.LoadImage(System.IO.File.ReadAllBytes(file)), "a PNG");
                Assert.AreEqual(320, tex.width);
                Assert.AreEqual(180, tex.height);
                Object.DestroyImmediate(tex);
                StringAssert.Contains("4 elements", (string)all["framed"]);

                var one = J(SimuLeanCommands.Capture("PSrv", 40, 30, 0, 40, "", 320, 180, file));
                Vector3 V(JToken p) => new Vector3((float)p[0], (float)p[1], (float)p[2]);
                Vector3 centre = Go("PSrv").transform.position;
                Assert.Less((V(one["camera"]["position"]) - centre).magnitude, (V(all["camera"]["position"]) - centre).magnitude,
                            "one element is framed closer than the whole model");
                var fixedDistance = J(SimuLeanCommands.Capture("PSrv", 90, 0, 10, 40, "", 64, 64, file));
                Assert.AreEqual(90f, (float)fixedDistance["camera"]["rotation"][0], 0.5f, "elevation 90: looking straight down");

                Assert.Throws<ArgumentException>(() => SimuLeanCommands.Capture("Nobody", 40, 30, 0, 40, "", 64, 64, file));
                Assert.Throws<ArgumentException>(() => SimuLeanCommands.Capture("", 40, 30, 0, 40, "", 8, 8, file), "a size too small");
            }
            finally { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); }
        }

        [Test]
        public void CaptureFramesTheModelWithItsNodesAndTeamsNotTheFloor()
        {
            // the floor widened the framing; nodes and team members were left out
            SimuLeanCommands.Apply(LineOps("G"));
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);   // 100 × 100 m, as a new scene's
            floor.transform.SetParent(Test.Root.transform, false);
            floor.transform.localScale = new Vector3(10f, 1f, 10f);
            floor.AddComponent<SimuLeanFloor>();
            SimuLeanCommands.AddElement("Transport Network", "GNet", 0, 0, 0, Root);
            SimuLeanCommands.AddElement("Transport Node", "GCorner", 14, 0, 9, "GNet");
            SimuLeanCommands.AddTeam("GCrew", 2, "", 10, 0, -6, Root, "operator");
            TeamMemberPreviews.EnsureAll();

            var content = ModelLayout.OfModel(Test.Scene);
            Assert.AreEqual(4, content.Elements);
            Assert.AreEqual(1, content.Nodes);
            Assert.AreEqual(1, content.Teams);
            Assert.AreEqual(2, content.Members, "the team's members (their previews) are framed");
            var all = content.Boxes[0];
            foreach (var b in content.Boxes) all.Encapsulate(b);
            foreach (var p in content.Points) all.Encapsulate(p);
            Assert.Less(all.size.x, 30f, "the floor is not framed");
            Assert.GreaterOrEqual(all.max.x, 14f - 1e-3f, "the node is framed");
            Assert.GreaterOrEqual(all.max.z, 9f - 1e-3f, "the node is framed");
            Assert.LessOrEqual(all.min.z, -6f + 0.5f, "the team's members are framed");

            string file = System.IO.Path.GetFullPath($"Temp/simulean_capture_test_{Guid.NewGuid():N}.png");
            try
            {
                var shot = J(SimuLeanCommands.Capture("", 40, 30, 0, 40, "", 320, 180, file));
                StringAssert.Contains("1 transport nodes", (string)shot["framed"]);
                StringAssert.Contains("1 teams (2 members shown)", (string)shot["framed"]);
                Assert.GreaterOrEqual((int)shot["drawn"]["connections"], 3, "the flow is drawn");
                Assert.GreaterOrEqual((int)shot["drawn"]["markers"], 1, "the node is drawn as a marker");
                var none = J(SimuLeanCommands.Capture("", 40, 30, 0, 40, "", 64, 64, file, "none"));
                Assert.AreEqual(0, (int)none["drawn"]["connections"]);
                Assert.Throws<ArgumentException>(() => SimuLeanCommands.Capture("", 40, 30, 0, 40, "", 64, 64, file, "flows"), "an unknown group");
            }
            finally { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); }
        }

        [Test]
        public void CaptureResourcesDrawsEveryUseOfATeam()
        {
            // connections=resources drew no setup crew (setup.setupBy.team); a repair crew was a downtime
            // connection and a requirement-only process use was not drawn
            SimuLeanCommands.Apply(LineOps("R"));
            SimuLeanCommands.AddTeam("RSetters", 1, "", 4, 0, -4, Root, "operator");
            SimuLeanCommands.AddTeam("RFixers", 1, "", 6, 0, -4, Root, "operator");
            SimuLeanCommands.AddTeam("ROps", 1, "", 2, 0, -4, Root, "operator");
            SimuLeanCommands.Set("RSrv", "setup.when", "LabelChange");
            SimuLeanCommands.Set("RSrv", "setup.setupBy.team", "RSetters");
            var srv = Go("RSrv").GetComponent<UnityMultiServer>();
            srv.processBy.requirements.Add(new ResourceRequirementSpec { team = Go("ROps").GetComponent<UnityResourceTeam>() });
            SimuLeanCommands.AddElement("Downtime", "RDown", 0, 0, 0, Root);
            var down = Go("RDown").GetComponent<UnityDowntime>();
            down.targets.Add(srv);
            down.repairBy.team = Go("RFixers").GetComponent<UnityResourceTeam>();

            var res = SceneConnections.All().Where(c => SceneConnections.GroupOf(c.Kind) == ConnectionGroup.Resources).ToList();
            Assert.IsTrue(res.Any(c => c.Kind == ConnectionKind.SetupCrew && c.From == srv && c.To.name == "RSetters"), "the setup crew");
            Assert.IsTrue(res.Any(c => c.Kind == ConnectionKind.ProcessTeam && c.From == srv && c.To.name == "ROps"), "a requirement-only process use");
            Assert.IsTrue(res.Any(c => c.Kind == ConnectionKind.RepairCrew && c.From == down && c.To.name == "RFixers"), "the repair crew");
            Assert.AreEqual(3, res.Count, string.Join(", ", res.Select(c => $"{c.Kind} {c.From.name}-{c.To.name}")));

            string file = System.IO.Path.GetFullPath($"Temp/simulean_capture_test_{Guid.NewGuid():N}.png");
            try
            {
                var shot = J(SimuLeanCommands.Capture("", 40, 30, 0, 40, "", 64, 64, file, "resources"));
                Assert.AreEqual(3, (int)shot["drawn"]["connections"], shot.ToString());
            }
            finally { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); }

            // the Connect tool removes each one from the field that holds it
            ConnectionModel.Remove(res.First(c => c.Kind == ConnectionKind.SetupCrew));
            Assert.IsNull(srv.setup.setupBy.team);
            ConnectionModel.Remove(SceneConnections.All().First(c => c.Kind == ConnectionKind.ProcessTeam && c.From == srv));
            Assert.IsNull(srv.processBy.requirements[0].team);
        }

        [Test]
        public void ATeamWithoutAPositionStandsBesideTheModelAndItsMembersCountInOverlaps()
        {
            // a team placed without a position stood at the origin on a Source; overlaps ignored team members
            SimuLeanCommands.Apply(LineOps("T"));
            var placed = J(SimuLeanCommands.AddTeam("TCrew", 2, "", 0, 0, 0, Root, "operator"));
            Assert.IsNotNull((string)placed["placedAt"], placed.ToString());
            Assert.Less((float)placed["at"][1], -1f, "in front of the model (−Z): " + placed);
            Assert.IsFalse(placed["overlaps"].Any(), placed.ToString());
            var on = J(SimuLeanCommands.AddTeam("TOn", 1, "", 6, 0, 0, Root, "operator"));
            Assert.IsTrue(on["overlaps"].Any(o => ((string)o).Contains("team member 'TOn_1' of 'TOn'") && ((string)o).Contains("'TSink'")), on.ToString());
            var check = J(SimuLeanCommands.Validate(root: Root));
            Assert.IsTrue(check["info"].Any(i => ((string)i).Contains("'TOn'") && ((string)i).Contains("overlap")), check.ToString());
            Assert.IsFalse(check["info"].Any(i => ((string)i).Contains("'TCrew'")), "two members of one team are not compared: " + check);
        }

        [Test]
        public void ToolsWithoutAPositionStandBesideTheModelApart()
        {
            // a List / Downtime / Reaction without a position stood at the origin, on the Source
            SimuLeanCommands.Apply(LineOps("P"));
            var applied = J(SimuLeanCommands.Apply(@"[
                {""op"": ""add"", ""type"": ""List"", ""name"": ""PList"", ""parent"": """ + Root + @"""},
                {""op"": ""add"", ""type"": ""Downtime"", ""name"": ""PDown"", ""parent"": """ + Root + @"""}]"));
            Assert.IsNotNull(applied["placed"]?["at"]?["PList"], applied.ToString());
            var reaction = J(SimuLeanCommands.AddElement("Reaction", "PReact", 0, 0, 0, Root));
            Assert.IsNotNull((string)reaction["placedAt"], reaction.ToString());
            float maxZ = new[] { "PSrc", "PBuf", "PSrv", "PSink" }.Max(n => ModelLayout.BoundsOf(Go(n))?.max.z ?? Go(n).transform.position.z);
            var spots = new[] { "PList", "PDown", "PReact" }.Select(n => Go(n).transform.position).ToList();
            Assert.IsTrue(spots.All(p => p.z > maxZ + 1f), "behind the model: " + string.Join(", ", spots));
            Assert.AreEqual(3, spots.Select(p => Mathf.RoundToInt(p.x * 10)).Distinct().Count(), "apart: " + string.Join(", ", spots));
            var given = J(SimuLeanCommands.AddElement("List", "PGiven", 2, 0, 5, Root));
            Assert.IsNull((string)given["placedAt"], "a position given is kept");
            Assert.AreEqual(new Vector3(2, 0, 5), Go("PGiven").transform.position);
            var node = J(SimuLeanCommands.AddElement("Transport Node", "PNode", 0, 0, 0, Root));
            Assert.IsNull((string)node["placedAt"], "a node's position means its place: kept");
        }

        [Test]
        public void ParamsFieldsFilterTheCatalogToo()
        {
            // fields=lanes still returned every catalog key
            SimuLeanCommands.Apply(LineOps("C"));
            var all = J(SimuLeanCommands.Params("CSrv"));
            var some = J(SimuLeanCommands.Params("CSrv", fields: "delayStrategy"));
            var keys = ((JObject)some["catalog"]).Properties().Select(x => x.Name).ToList();
            Assert.IsNotEmpty(keys, some.ToString());
            Assert.IsTrue(keys.All(k => k.StartsWith("delayStrategy.")), string.Join(", ", keys));
            Assert.Less(keys.Count, ((JObject)all["catalog"]).Count, "fewer than the whole catalog");
        }

        [Test]
        public void RackLanesAreSetByIdOrPatternAndListedWhenRebuilt()
        {
            // bay indexes are 0-based; many lanes set in one call
            SimuLeanCommands.AddElement("Pallet Rack", "LRack", 0, 0, 0, Root);
            SimuLeanCommands.AddElement("Transport Node", "LBay1", 2, 0, -3, Root);
            var rebuilt = J(SimuLeanCommands.Set("LRack", "bays", "2"));
            StringAssert.Contains("B0_S0_L0", (string)rebuilt["rebuilt"], rebuilt.ToString());
            StringAssert.Contains("0-based", (string)rebuilt["rebuilt"]);
            var r = Go("LRack").GetComponent<UnityStorageRack>();
            int perBay = r.slotsPerBay * r.levels;
            var set = J(SimuLeanCommands.Set("LRack", "lanes[B1_*].node", "LBay1"));
            Assert.AreEqual(perBay, (int)set["matched"], set.ToString());
            Assert.IsTrue(r.lanes.Where(l => l.laneId.StartsWith("B1_")).All(l => l.node != null && l.node.name == "LBay1"), "bay 1's lanes");
            Assert.IsTrue(r.lanes.Where(l => l.laneId.StartsWith("B0_")).All(l => l.node == null), "bay 0's untouched");
            var one = J(SimuLeanCommands.Set("LRack", "lanes[B0_S0_L0].capacity", "1"));
            Assert.AreEqual(1, (int)one["matched"], one.ToString());
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("LRack", "lanes[B9_*].node", "LBay1"), "no lane matches");
        }

        [Test]
        public void ATeamSaysWhereItsSpeedCameFrom()
        {
            // the speed applied without a kind was not said
            var plain = J(SimuLeanCommands.AddTeam("SCrew", 1, "", 0, 0, 0, Root));
            StringAssert.Contains("no kind", (string)plain["speedFrom"], plain.ToString());
            var named = J(SimuLeanCommands.AddTeam("Forklifts", 1, "", 0, 0, 0, Root));
            StringAssert.Contains("name", (string)named["speedFrom"], named.ToString());
            StringAssert.Contains("forklift", (string)named["speedFrom"]);
            var kind = J(SimuLeanCommands.AddTeam("SOps", 1, "", 0, 0, 0, Root, "operator"));
            StringAssert.StartsWith("kind 'operator'", (string)kind["speedFrom"], kind.ToString());
        }

        [Test]
        public void ALoadedTableSaysHowItsRowsWereRead()
        {
            // the answer gave rows and columns only
            SimuLeanCommands.Apply(LineOps("M"));
            string dir = System.IO.Path.GetFullPath("Temp/simulean_load_table_test_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string matrix = System.IO.Path.Combine(dir, "matrix.csv");
                System.IO.File.WriteAllText(matrix, "from,to,time\n,*,10\nA,B,5\n*,*,8\n");
                var m = J(SimuLeanCommands.LoadTable("MSrv", matrix));
                var read = m["read"].Select(x => (string)x).ToList();
                CollectionAssert.AreEqual(new[] { "(first setup) → any: 10", "A → B: 5", "any → any: 8" }, read, m.ToString());
                StringAssert.Contains("most specific", (string)m["lookup"]);
                string arrivals = System.IO.Path.Combine(dir, "arrivals.csv");
                System.IO.File.WriteAllText(arrivals, "Time,Q,family,name\n0,1,A,x\n5,2,B,y\n");
                var a = J(SimuLeanCommands.LoadTable("MSrc", arrivals));
                StringAssert.Contains("Time 0 … 5", (string)a["read"], a.ToString());
                StringAssert.Contains("labels family, name", (string)a["read"]);
                Assert.IsTrue(a["notes"].Any(n => ((string)n).Contains("'name'")), "a column that differs from Name by its case: " + a);
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        [Test]
        public void MoveTakesAnObjectToANewPlaceAndSaysWhenTravelDistancesChange()
        {
            SimuLeanCommands.Apply(LineOps("M"));
            // no Straight team: an element's place does not enter a run
            var m = J(SimuLeanEditCommands.Move("MBuf", 2, 8));
            Assert.AreEqual(2f, Go("MBuf").transform.position.x, 1e-4f);
            Assert.AreEqual(8f, Go("MBuf").transform.position.z, 1e-4f);
            CollectionAssert.AreEqual(new[] { 2f, 8f }, m["at"].Select(v => (float)v).ToArray());
            Assert.IsFalse(m["warnings"].Any(w => ((string)w).Contains("travel distances changed")), m.ToString());
            // with a Straight team, an element without a location node and the team itself are walked between
            SimuLeanCommands.AddTeam("MCrew", 1, "", 0, 0, 0, Root, "operator");
            var e = J(SimuLeanEditCommands.Move("MSrv", 4, 12));
            Assert.IsTrue(e["warnings"].Any(w => ((string)w).StartsWith("travel distances changed") && ((string)w).Contains("'MSrv'")), e.ToString());
            var t = J(SimuLeanEditCommands.Move("MCrew", -3, -6));
            Assert.IsTrue(t["warnings"].Any(w => ((string)w).Contains("Straight team 'MCrew'")), t.ToString());
            // onto another element: the overlap is named; one undo step puts it back
            var onto = J(SimuLeanEditCommands.Move("MSink", 2, 8));
            Assert.IsTrue(onto["overlaps"].Any(o => ((string)o).Contains("'MBuf'")), onto.ToString());
            UnityEditor.Undo.PerformUndo();
            Assert.AreEqual(6f, Go("MSink").transform.position.x, 1e-4f, "undone");
            Assert.AreEqual(-6f, Go("MCrew").transform.position.z, 1e-4f, "the move before is kept");
            // a transport node: its tracks' lengths
            SimuLeanCommands.Apply(@"[
                {""op"": ""add"", ""type"": ""Transport Network"", ""name"": ""MNet"", ""parent"": """ + Root + @"""},
                {""op"": ""add"", ""type"": ""Transport Node"", ""name"": ""MNa"", ""x"": 0, ""z"": -20, ""parent"": ""MNet""},
                {""op"": ""add"", ""type"": ""Transport Node"", ""name"": ""MNb"", ""x"": 10, ""z"": -20, ""parent"": ""MNet""},
                {""op"": ""connect"", ""from"": ""MNa"", ""to"": ""MNb""}]");
            var n = J(SimuLeanEditCommands.Move("MNb", 20, -20));
            Assert.IsTrue(n["warnings"].Any(w => ((string)w).Contains("MNa <-> MNb 10 → 20 m")), n.ToString());
            var bad = Assert.Throws<ArgumentException>(() => SimuLeanEditCommands.Move("Nothing here", 0, 0));
            StringAssert.Contains("Nothing here", bad.Message);
        }

        [Test]
        public void ApplyMovesAndPlacesATeamWithoutAPositionOnceTheListStands()
        {
            SimuLeanCommands.Apply(LineOps("Q"));
            // where a team added now would stand: the list then puts an element there
            var spot = ModelLayout.FreeSpotForTeam();
            string x = spot.x.ToString(System.Globalization.CultureInfo.InvariantCulture), z = spot.z.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var a = J(SimuLeanCommands.Apply(@"[
                {""op"": ""add_team"", ""name"": ""QFirst"", ""members"": 1, ""kind"": ""operator"", ""parent"": """ + Root + @"""},
                {""op"": ""add"", ""type"": ""UnityQueue"", ""name"": ""QLate"", ""x"": " + x + @", ""z"": " + z + @", ""parent"": """ + Root + @"""},
                {""op"": ""move"", ""object"": ""QBuf"", ""x"": 1, ""z"": 14}]"));
            Assert.AreEqual(14f, Go("QBuf").transform.position.z, 1e-4f, "moved: " + a);
            Assert.IsNotNull(a["teamsPlaced"]?["at"]?["QFirst"], a.ToString());
            Assert.Less(Go("QFirst").transform.position.z, spot.z - 1f, "placed in front of the element the list added: " + a);
            Assert.IsFalse(a["overlaps"].Any(o => ((string)o).Contains("QFirst")), a.ToString());
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Apply(@"[{""op"": ""move"", ""object"": ""QBuf"", ""x"": 1}]"));
            StringAssert.Contains("no \"z\"", e.Message);
        }

        [Test]
        public void TheModelShowsItsTransportNetwork()
        {
            // simulean_model showed no nodes, tracks or the teams on them
            SimuLeanCommands.Apply(@"[
                {""op"": ""add"", ""type"": ""Transport Network"", ""name"": ""NNet"", ""parent"": """ + Root + @"""},
                {""op"": ""add"", ""type"": ""Transport Node"", ""name"": ""NA"", ""x"": 0, ""z"": -4, ""parent"": ""NNet""},
                {""op"": ""add"", ""type"": ""Transport Node"", ""name"": ""NB"", ""x"": 10, ""z"": -4, ""parent"": ""NNet""},
                {""op"": ""connect"", ""from"": ""NA"", ""to"": ""NB""}]");
            var model = J(SimuLeanCommands.Model(root: Root, parameters: false));
            var tracks = model["network"]?["tracks"]?.Select(t => (string)t).ToList();
            Assert.IsNotNull(tracks, model.ToString());
            Assert.IsTrue(tracks.Any(t => t.StartsWith("NA – NB: 10") || t.StartsWith("NB – NA: 10")), string.Join(" | ", tracks));
            Assert.IsTrue(model["network"]["nodes"].Any(n => ((string)n).StartsWith("NA (0, -4)")), model["network"].ToString());
            var alone = J(SimuLeanCommands.Model("NNet", root: Root));
            Assert.IsNotNull(alone["network"], "object=<the network's name>: " + alone);
        }

        [Test]
        public void SettingAValueTurnsItsSwitchOn()
        {
            // transportBy.loadTime = 0 set without overrideLoadTimes was never used (nor exported)
            SimuLeanCommands.Apply(LineOps("W"));
            var set = J(SimuLeanCommands.Set("WSrv", "transportBy.loadTime.constantValue", "0"));
            StringAssert.Contains("transportBy.overrideLoadTimes", (string)set["alsoSet"], set.ToString());
            Assert.IsTrue(El("WSrv").transportBy.overrideLoadTimes);
            var again = J(SimuLeanCommands.Set("WSrv", "transportBy.unloadTime.constantValue", "0"));
            Assert.IsNull((string)again["alsoSet"], "already on: " + again);
        }

        [Test]
        public void FootprintsAreReportedAndOverlapsAreInformation()
        {
            // elements placed 2 m apart when a queue is 3 m wide; no command reported a footprint
            SimuLeanCommands.Apply(LineOps("O"));
            var placed = J(SimuLeanCommands.AddElement("UnityQueue", "OExtra", 0, 0, 0, Root));
            Assert.IsNotNull(placed["size"], placed.ToString());
            Assert.Greater((float)placed["size"][0], 0.5f, "a queue's footprint (its pad)");

            var applied = J(SimuLeanCommands.Apply(@"[{""op"": ""add"", ""type"": ""UnityQueue"", ""name"": ""OOver"", ""x"": 0.5, ""parent"": """ + Root + @"""}]"));
            Assert.IsNotNull(applied["sizes"]?["OOver"], applied.ToString());
            Assert.IsTrue(applied["overlaps"].Any(o => ((string)o).Contains("'OOver'") && ((string)o).Contains("'OExtra'")), applied.ToString());

            var model = J(SimuLeanCommands.Model(root: Root, parameters: false));
            var buf = model["elements"].First(e => (string)e["name"] == "OBuf");
            Assert.AreEqual(2, buf["size"].Count(), buf.ToString());
            Assert.AreEqual(2f, (float)buf["at"][0], 1e-3f, "where it stands [x, z]");

            var check = J(SimuLeanCommands.Validate(root: Root));
            Assert.IsTrue(check["info"].Any(i => ((string)i).Contains("'OExtra'") && ((string)i).Contains("overlap")), check.ToString());
            Assert.IsFalse(check["errors"].Any(e => ((string)e).Contains("overlap")), "an overlap is not an error");

            Go("OExtra").transform.position = new Vector3(0f, 0f, 20f);
            Go("OOver").transform.position = new Vector3(10f, 0f, 20f);
            check = J(SimuLeanCommands.Validate(root: Root));
            Assert.IsFalse(check["info"].Any(i => ((string)i).Contains("'OExtra'") || ((string)i).Contains("'OOver'")), check.ToString());
        }

        [Test]
        public void ADeadlockWarnedInEveryReplicationIsOneLineWithACount()
        {
            var answer = J(AgentReply.Of(() =>
            {
                Debug.LogWarning("[SimuLean] Deadlock at t = 4821.32: no event can move the 2 element(s) holding items — A: BLOCKED");
                Debug.LogWarning("[SimuLean] Deadlock at t = 5012.7: no event can move the 3 element(s) holding items — A: BLOCKED");
                Debug.LogWarning("[SimuLean] Deadlock at t = 18525: no event can move the 2 element(s) holding items — A: BLOCKED");
                Debug.LogWarning("another warning");
                return new { ok = true };
            }));
            var warnings = answer["warnings"].Select(w => (string)w).ToList();
            var deadlocks = warnings.Where(w => w.Contains("Deadlock at t")).ToList();
            Assert.AreEqual(1, deadlocks.Count, string.Join(" | ", warnings));
            StringAssert.Contains("t = 4821.32", deadlocks[0], "the first one is kept");
            StringAssert.Contains("2 more run(s)", deadlocks[0], "the others counted");
            CollectionAssert.Contains(warnings, "another warning", "other warnings untouched");
        }

        [Test]
        public void AnAnswerKeepsItsOwnWarningsNamedAndNotABackgroundJobs()
        {
            // a background run's warnings appeared in the answer of a command polled meanwhile
            var logger = new GameObject("WarnLogger");
            logger.transform.SetParent(Test.Root.transform, false);
            var job = new AgentReply.LogCapture(job: true);
            var release = new System.Threading.ManualResetEventSlim();
            System.Threading.Tasks.Task worker;
            using (job.Enter(detached: true))
                worker = System.Threading.Tasks.Task.Run(() => { release.Wait(); Debug.LogWarning("job warning"); });
            JObject answer;
            try
            {
                answer = J(AgentReply.Of(() =>
                {
                    release.Set();
                    worker.Wait();
                    Debug.LogWarning("own warning", logger);
                    return new { ok = true };
                }));
            }
            finally { job.Dispose(); }
            var warnings = answer["warnings"].Select(w => (string)w).ToList();
            CollectionAssert.DoesNotContain(warnings, "job warning", "the job's warning stays with the job");
            CollectionAssert.Contains(warnings, "[WarnLogger] own warning", "a warning names the object it was logged with");
            CollectionAssert.Contains(job.Warnings, "job warning");
            Assert.IsFalse(job.Warnings.Any(w => w.Contains("own warning")), "the command's warning is not the job's");
        }

        [Test]
        public void ApplyValidateOnlyReportsEveryBadOperationAndChangesNothing()
        {
            // one bad operation does not make the caller resend the whole list
            string ops = @"[{""op"": ""add"", ""type"": ""UnityQueue"", ""name"": ""VQ"", ""parent"": """ + Root + @"""},
                            {""op"": ""set"", ""object"": ""VQ"", ""key"": ""capacty"", ""value"": 3},
                            {""op"": ""set"", ""object"": ""VQ"", ""key"": ""capacity"", ""value"": 3},
                            {""op"": ""connect"", ""from"": ""VQ"", ""to"": ""Nowhere""}]";
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Apply(ops, true));
            StringAssert.Contains("2 of 4 operation(s) failed", e.Message);
            StringAssert.Contains("operation 2", e.Message);
            StringAssert.Contains("operation 4", e.Message);
            Assert.IsNull(Go("VQ"), "validateOnly changes nothing");
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Apply(ops));
            StringAssert.Contains("operation 2", e.Message, "a normal apply names every bad operation too");
            StringAssert.Contains("operation 4", e.Message);
            Assert.IsNull(Go("VQ"), "and changes nothing");
            var ok = J(SimuLeanCommands.Apply(LineOps("V"), true));
            Assert.IsTrue((bool)ok["valid"]);
            Assert.AreEqual(12, (int)ok["checked"]);
            Assert.IsNull(Go("VSrc"), "a valid list checked only: nothing placed");
        }

        [Test]
        public void ADistributionIsSetByItsTextAndListedWithEveryType()
        {
            SimuLeanCommands.Apply(LineOps("D"));
            SimuLeanCommands.Set("DSrv", "delayStrategy", "Triangular~0.6~1~1.6");
            var srv = Go("DSrv").GetComponent<UnityMultiServer>();
            Assert.AreEqual(RandomProcessType.Triangular, srv.delayStrategy.strategyType);
            Assert.AreEqual(0.6, srv.delayStrategy.triangularMin, 1e-12);
            Assert.AreEqual(1.6, srv.delayStrategy.triangularMax, 1e-12);
            SimuLeanCommands.Set("DSrv", "delayStrategy", "ExponentialMean~4");
            Assert.AreEqual(RandomProcessType.Exponential, srv.delayStrategy.strategyType, "a mean is stored as a rate");
            Assert.AreEqual(0.25, srv.delayStrategy.exponentialRate, 1e-12);
            SimuLeanCommands.Set("DSrv", "delayStrategy", "3");
            Assert.AreEqual(RandomProcessType.Constant, srv.delayStrategy.strategyType, "a number is a constant");
            Assert.AreEqual(3, srv.delayStrategy.constantValue, 1e-12);
            SimuLeanCommands.Apply(@"[{""op"": ""set"", ""object"": ""DSrc"", ""key"": ""interArrivalDistribution"", ""value"": ""Uniform~1~3""}]");
            Assert.AreEqual(RandomProcessType.Uniform, Go("DSrc").GetComponent<UnitySource>().interArrivalDistribution.strategyType, "by simulean_apply too");
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("DSrv", "delayStrategy", "Triangle~1~2"));
            StringAssert.Contains("triangularMin, triangularMode, triangularMax", e.Message, "the error names every type's keys");
            var p = J(SimuLeanCommands.Params("DSrv"));
            Assert.AreEqual("Constant~3", (string)p["fields"].First(f => (string)f["path"] == "delayStrategy")["value"]);
            Assert.AreEqual("weibullShape, weibullScale", (string)p["distributions"]["keys"]["Weibull"], "every distribution's keys");
        }

        [Test]
        public void TypesAndParamsAreCompactAndDescribeListItems()
        {
            // the shared groups are counted, not listed; a list's items are described
            var compact = J(SimuLeanCommands.Types("UnityMultiServer"));
            var full = J(SimuLeanCommands.Types("UnityMultiServer", "full"));
            var paths = compact["fields"].Select(f => (string)f["path"]).ToList();
            CollectionAssert.Contains(paths, "capacity");
            CollectionAssert.DoesNotContain(paths, "processBy.team", "a shared group at its defaults is left out");
            Assert.Greater((int)compact["shared"]["processBy"], 5, "and counted");
            Assert.Less(compact.ToString().Length, full.ToString().Length * 0.6, "compact is much smaller");
            var group = J(SimuLeanCommands.Types("UnityMultiServer", "compact", "processBy"));
            Assert.IsTrue(group["fields"].All(f => ((string)f["path"]).StartsWith("processBy")), "fields= lists a group");
            StringAssert.Contains("team: object(UnityResourceTeam)", (string)group["listItems"]["processBy.requirements[i]"]);
            var reaction = J(SimuLeanCommands.Types("UnityReaction"));
            Assert.IsNotNull(reaction["listItems"]?["actions[i]"], "a reaction's actions are described: " + reaction["listItems"]);
            SimuLeanCommands.Apply(LineOps("T"));
            SimuLeanCommands.Set("TSrv", "processBy.priority", "5");
            var p = J(SimuLeanCommands.Params("TSrv"));
            CollectionAssert.Contains(p["fields"].Select(f => (string)f["path"]).ToList(), "processBy.priority", "a changed shared field is listed");
        }

        [Test]
        public void SettingARuleFillsItsParameters()
        {
            SimuLeanCommands.Apply(LineOps("Rule"));
            SimuLeanCommands.Set("RuleBuf", "outputStrategy.strategyType", "Rule");
            var a = J(SimuLeanCommands.Set("RuleBuf", "outputStrategy.rule", "ShortestQueueRule"));
            CollectionAssert.AreEqual(new[] { "FreeWeight = 0" }, a["ruleParameters"].Select(x => (string)x).ToArray(), a.ToString());
            SimuLeanCommands.Set("RuleBuf", "outputStrategy.ruleParameters", "FreeWeight = 0.5");
            var q = Go("RuleBuf").GetComponent<UnityQueue>();
            Assert.AreEqual(1, q.outputStrategy.ruleParameters.Count);
            Assert.AreEqual("0.5", q.outputStrategy.ruleParameters[0].value);
            var w = J(SimuLeanCommands.Set("RuleBuf", "outputStrategy.ruleParameters", "FreeWieght = 1"));
            StringAssert.Contains("FreeWeight", w["warnings"].ToString(), "an unknown parameter: did you mean");
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("RuleBuf", "outputStrategy.ruleParameters", "FreeWeight"));
            StringAssert.Contains("Name = value", e.Message);
            var bad = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Set("RuleBuf", "nextElements[0]", "Nowhere"));
            StringAssert.DoesNotContain(".Array.data[", bad.Message, "user-facing paths in errors");
        }

        [Test]
        public void AnUnquotedLabelTextIsFlagged()
        {
            // 'type = A' reads the label A, which nothing sets
            SimuLeanCommands.Apply(LineOps("L"));
            var a = J(SimuLeanCommands.Set("LSrc", "createLabels", "type = A"));
            StringAssert.Contains("did you mean the text 'A'", a["warnings"].ToString());
            var b = J(SimuLeanCommands.Set("LSrc", "createLabels", "type = 'A'"));
            StringAssert.DoesNotContain("did you mean the text", b["warnings"].ToString());
            var c = J(SimuLeanCommands.Apply(@"[{""op"": ""set"", ""object"": ""LSrc"", ""key"": ""createLabels"", ""value"": ""type = B""}]"));
            StringAssert.Contains("did you mean the text 'B'", c["warnings"].ToString(), "simulean_apply checks after its last edit");
            var v = J(SimuLeanCommands.Validate(root: Root));
            StringAssert.Contains("did you mean the text 'B'", v["warnings"].ToString(), "and simulean_validate");
        }

        [Test]
        public void InvokeListsAndRunsTheActionsOfAnObject()
        {
            SimuLeanCommands.AddElement("Floor Zone", "Zone", 0, 0, 0, Root);
            var list = J(SimuLeanCommands.Invoke("Zone"));
            var actions = list["actions"].Select(x => (string)x["action"]).ToArray();
            CollectionAssert.Contains(actions, "Rebuild", "the actions marked for agents");
            Assert.IsNotEmpty((string)list["actions"].First(x => (string)x["action"] == "Rebuild")["description"]);
            var ran = J(SimuLeanCommands.Invoke("Zone", "rebuild"));
            Assert.IsTrue((bool)ran["ran"]);
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Invoke("Zone", "Explode"));
            StringAssert.Contains("Rebuild", e.Message, "the actions it has");
            var viaApply = J(SimuLeanCommands.Apply(@"[{""op"": ""invoke"", ""object"": ""Zone"", ""action"": ""Rebuild""}]"));
            Assert.AreEqual(1, (int)viaApply["applied"], "invoke inside a list");
        }
    }
}
