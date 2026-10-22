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
    public class AgentBulkCommandsTests
    {
        private Scene active, scene;
        private bool temporary;
        private GameObject root;
        private int undoGroup;
        private const string Root = "BulkModel";

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
        private static GameObject Go(string name) => GameObject.Find(name);
        private static SElement El(string name) => Go(name).GetComponent<SElement>();
        private static string[] Next(string name) => El(name).nextElements.Where(e => e != null).Select(e => e.name).ToArray();

        // Source (every 1) → Queue → Multi-Server (0.5) → Sink, as one list of operations
        internal static string LineOps(string p, string root = Root) => @"[
            {""op"": ""add"", ""type"": ""UnitySource"", ""name"": """ + p + @"Src"", ""parent"": """ + root + @"""},
            {""op"": ""add"", ""type"": ""UnityQueue"", ""name"": """ + p + @"Buf"", ""x"": 2, ""parent"": """ + root + @"""},
            {""op"": ""add"", ""type"": ""UnityMultiServer"", ""name"": """ + p + @"Srv"", ""x"": 4, ""parent"": """ + root + @"""},
            {""op"": ""add"", ""type"": ""UnitySink"", ""name"": """ + p + @"Sink"", ""x"": 6, ""parent"": """ + root + @"""},
            {""op"": ""set"", ""element"": """ + p + @"Src"", ""key"": ""mode"", ""value"": ""InterArrival""},
            {""op"": ""set"", ""element"": """ + p + @"Src"", ""key"": ""interArrivalDistribution.strategyType"", ""value"": ""Constant""},
            {""op"": ""set"", ""element"": """ + p + @"Src"", ""key"": ""interArrivalDistribution.constantValue"", ""value"": 1},
            {""op"": ""set"", ""element"": """ + p + @"Srv"", ""key"": ""delayStrategy.strategyType"", ""value"": ""Constant""},
            {""op"": ""set"", ""element"": """ + p + @"Srv"", ""key"": ""delayStrategy.constantValue"", ""value"": 0.5},
            {""op"": ""connect"", ""from"": """ + p + @"Src"", ""to"": """ + p + @"Buf""},
            {""op"": ""connect"", ""from"": """ + p + @"Buf"", ""to"": """ + p + @"Srv""},
            {""op"": ""connect"", ""from"": """ + p + @"Srv"", ""to"": """ + p + @"Sink""}
        ]";

        [Test]
        public void ApplyBuildsAModelInOneCallAndOneUndoStep()
        {
            var a = J(SimuLeanCommands.Apply(LineOps("A")));
            Assert.AreEqual(12, (int)a["applied"], a.ToString());
            Assert.AreEqual(12, a["results"].Count(), "one short result per operation");
            Assert.AreEqual("connect", (string)a["results"][11]["op"]);
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
            e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Apply(@"[{""op"": ""set"", ""element"": ""X""}]"));
            StringAssert.Contains("\"key\"", e.Message, "a missing field is named");
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Apply("{not json"));
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
