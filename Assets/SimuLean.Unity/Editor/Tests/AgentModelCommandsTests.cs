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
    /// Command checks: a resource use whose requirements name their teams, the Source's rate
    /// table (inline and loaded from a file), the schedule's arrivals counted from Q, an element property as a metric, the
    /// calendar start in the settings, the clock kept for one object, member previews at the home node, tools placed off the
    /// transport nodes, an empty object slot shown as (none). Each test builds in a scene of its own, closed at the end.
    /// </summary>
    public class AgentModelCommandsTests : SceneTestFixture
    {
        private string dir;
        private const string Root = "ModelCommandsModel";
        protected override string RootName => Root;

        [SetUp]
        public void SetUp()
        {
            // the model's clock (the scene's settings: stop when empty, the calendar start)
            new GameObject("ModelClock").transform.SetParent(Test.Root.transform);
            GameObject.Find("ModelClock").AddComponent<UnitySimClock>();
            ResultsOutput.StoreOverride = System.IO.Path.GetFullPath($"Temp/simulean_store_test_{Guid.NewGuid():N}.db");
            dir = System.IO.Path.GetFullPath("Temp/simulean_b03_test_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void TearDown()
        {
            ResultsOutput.StoreOverride = null;
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        }

        private static JObject J(object answer) => (JObject)answer;
        private static T C<T>(string name) where T : Component => GameObject.Find(name).GetComponent<T>();

        // Src → Q → Srv → Out (a clinic: one doctor, 1 time unit each)
        private static void Line(string p) => SimuLeanCommands.Apply(@"[
            {""op"": ""add"", ""type"": ""UnitySource"", ""name"": """ + p + @"Src"", ""parent"": """ + Root + @"""},
            {""op"": ""add"", ""type"": ""UnityQueue"", ""name"": """ + p + @"Q"", ""x"": 3, ""parent"": """ + Root + @"""},
            {""op"": ""add"", ""type"": ""UnityMultiServer"", ""name"": """ + p + @"Srv"", ""x"": 6, ""parent"": """ + Root + @"""},
            {""op"": ""add"", ""type"": ""UnitySink"", ""name"": """ + p + @"Out"", ""x"": 9, ""parent"": """ + Root + @"""},
            {""op"": ""set"", ""object"": """ + p + @"Srv"", ""key"": ""delayStrategy"", ""value"": ""Constant~1""},
            {""op"": ""connect"", ""from"": """ + p + @"Src"", ""to"": """ + p + @"Q""},
            {""op"": ""connect"", ""from"": """ + p + @"Q"", ""to"": """ + p + @"Srv""},
            {""op"": ""connect"", ""from"": """ + p + @"Srv"", ""to"": """ + p + @"Out""}]");

        [Test]
        public void AUseWhoseRequirementsNameTheirTeamsIsActive()
        {
            Line("R");
            SimuLeanCommands.Apply(@"[
                {""op"": ""add_team"", ""name"": ""ROps"", ""members"": 1, ""travel"": ""Abstract"", ""parent"": """ + Root + @"""},
                {""op"": ""add_team"", ""name"": ""RTools"", ""members"": 1, ""travel"": ""Abstract"", ""parent"": """ + Root + @"""},
                {""op"": ""set"", ""object"": ""RSrc"", ""key"": ""mode"", ""value"": ""InterArrival""},
                {""op"": ""set"", ""object"": ""RSrc"", ""key"": ""interArrivalDistribution"", ""value"": ""Constant~5""},
                {""op"": ""set"", ""object"": ""RSrv"", ""key"": ""processBy.requirements[0].team"", ""value"": ""ROps""},
                {""op"": ""set"", ""object"": ""RSrv"", ""key"": ""processBy.requirements[1].count"", ""value"": 1}]");
            Assert.IsTrue(C<UnityMultiServer>("RSrv").processBy.IsUsed, "a requirement's team makes the use active");
            var bad = J(SimuLeanCommands.Validate(root: Root));
            Assert.IsFalse((bool)bad["ok"], bad.ToString());
            StringAssert.Contains("processBy.requirements[1] names no team", bad["errors"].ToString());
            SimuLeanCommands.Set("RSrv", "processBy.requirements[1].team", "RTools");
            var run = J(SimuLeanCommands.RunHeadless(horizon: 100, metrics: "ops=team:ROps.utilization;tools=team:RTools.utilization", root: Root));
            Assert.Greater((double)run["scenarios"][0]["metrics"]["ops"]["mean"], 0.1, run.ToString());
            Assert.Greater((double)run["scenarios"][0]["metrics"]["tools"]["mean"], 0.1, run.ToString());
        }

        [Test]
        public void ARateTableSourceInlineAndFromAFile()
        {
            Line("T");
            SimuLeanCommands.Apply(@"[
                {""op"": ""set"", ""object"": ""TSrc"", ""key"": ""mode"", ""value"": ""RateTable""},
                {""op"": ""set"", ""object"": ""TSrc"", ""key"": ""ratePeriods[0].rate"", ""value"": 0.5},
                {""op"": ""set"", ""object"": ""TSrc"", ""key"": ""ratePeriods[1].start"", ""value"": 100},
                {""op"": ""set"", ""object"": ""TSrc"", ""key"": ""ratePeriods[1].rate"", ""value"": 0},
                {""op"": ""set"", ""object"": ""ModelClock"", ""key"": ""stopWhenEmpty"", ""value"": true}]");
            var x = SceneExtractor.Extract(Test.Root);
            var ec = x.Config.Elements.First(e => e.Name == "TSrc");
            Assert.AreEqual("RateTable", ec.Get<string>("mode"));
            Assert.AreEqual(2, ec.SettingsAs<SimuLean.Serialization.SourceSettings>().RatePeriods.Count, "the periods inline");
            var run = J(SimuLeanCommands.RunHeadless(horizon: 100000, metrics: "n=TOut.input;span=model.makespan", root: Root));
            var s = run["scenarios"][0];
            double n = (double)s["metrics"]["n"]["mean"];
            Assert.That(n, Is.InRange(25, 80), "≈ 50 arrivals in [0, 100): " + run);
            Assert.Less((double)s["metrics"]["span"]["mean"], 1000, "the door closes: the run ends at the last exit, not the horizon");

            string rates = System.IO.Path.Combine(dir, "rates.csv");
            System.IO.File.WriteAllText(rates, "Start,Rate,kind\n0,0.5,A\n100,0\n");
            var loaded = J(SimuLeanCommands.LoadTable("TSrc", rates));
            Assert.AreEqual("RateTable", (string)loaded["mode"], loaded.ToString());
            Assert.AreEqual(0, C<UnitySource>("TSrc").ratePeriods.Count, "the table replaces the inline periods");
            Assert.IsTrue(loaded["notes"].Any(t => ((string)t).Contains("the source stops")), loaded.ToString());

            string sched = System.IO.Path.Combine(dir, "sched.csv");
            System.IO.File.WriteAllText(sched, "Time,Name,Q\n0,P,2\n10,P,2\n20,P,1\n");
            SimuLeanCommands.Set("TSrc", "mode", "Schedule");
            var a = J(SimuLeanCommands.LoadTable("TSrc", sched));
            Assert.AreEqual(5, (int)a["arrivals"], "the arrivals counted from Q: " + a);
            StringAssert.Contains("3 rows, 5 arrivals", (string)a["read"]);
        }

        [Test]
        public void APropertyIsAMetricTheStartIsEchoedAndOneObjectKeepsTheClock()
        {
            Line("P");
            SimuLeanCommands.Apply(@"[
                {""op"": ""set"", ""object"": ""PSrc"", ""key"": ""mode"", ""value"": ""InterArrival""},
                {""op"": ""set"", ""object"": ""PSrv"", ""key"": ""properties.late"", ""value"": 3},
                {""op"": ""set"", ""object"": ""ModelClock"", ""key"": ""startDateTime"", ""value"": ""2026-11-16 08:00""}]");
            var v = J(SimuLeanCommands.Validate(metrics: "l=PSrv.property:lat", root: Root));
            StringAssert.Contains("Did you mean 'late'", v["errors"].ToString(), v.ToString());
            StringAssert.Contains("2026-11-16", (string)v["settings"]["start"], "the calendar start in the settings");
            var run = J(SimuLeanCommands.RunHeadless(horizon: 10, metrics: "l=PSrv.property:late", root: Root));
            Assert.AreEqual(3.0, (double)run["scenarios"][0]["metrics"]["l"]["mean"], run.ToString());
            var one = J(SimuLeanCommands.Model("PSrv", root: Root));
            Assert.IsNotNull(one["clock"]?["timeUnit"], "one object's answer keeps the clock: " + one);
        }

        [Test]
        public void ANetworkTeamIsDrawnAtItsHomeNodeAndToolsAvoidNodes()
        {
            Line("N");
            SimuLeanCommands.Apply(@"[
                {""op"": ""add"", ""type"": ""Transport Network"", ""name"": ""NNet"", ""parent"": """ + Root + @"""},
                {""op"": ""add"", ""type"": ""Transport Node"", ""name"": ""NDock"", ""x"": -6, ""z"": -8, ""parent"": ""NNet""},
                {""op"": ""add_team"", ""name"": ""NLifts"", ""members"": 1, ""kind"": ""forklift"", ""travel"": ""Network"", ""parent"": """ + Root + @"""},
                {""op"": ""set"", ""object"": ""NLifts"", ""key"": ""homeNode"", ""value"": ""NDock""}]");
            TeamMemberPreviews.EnsureAll();
            var team = C<UnityResourceTeam>("NLifts");
            var preview = team.transform.Find(TeamMemberPreviews.Child);
            Assert.IsNotNull(preview);
            Assert.Less(Vector3.Distance(preview.GetChild(0).position, new Vector3(-6, 0, -8)), 0.01f, "the member at its home node");

            // a node where the next tool would go: the list goes beside it
            var spot = ModelLayout.FreeSpotForTool();
            SimuLeanCommands.AddElement("Transport Node", "NBlock", spot.x, 0, spot.z, "NNet");
            var placed = J(SimuLeanCommands.AddElement("List", "NOrders", 0, 0, 0, Root));
            var at = C<Transform>("NOrders").position;
            Assert.Greater(new Vector2(at.x - spot.x, at.z - spot.z).magnitude, 1f, "not on the node: " + placed);
        }

        [Test]
        public void AnEmptyObjectSlotReadsNone()
        {
            Line("C");
            var comb = SimuLeanCommands.AddElement("UnityCombiner", "CComb", 12, 0, 0, Root);
            var c = C<UnityCombiner>("CComb");
            c.inputOrder = new System.Collections.Generic.List<SElement> { null, null };
            var p = J(SimuLeanCommands.Params("CComb", fields: "inputOrder"));
            var f = p["fields"].First(x => (string)x["path"] == "inputOrder");
            Assert.AreEqual("[(none), (none)]", (string)f["value"], p.ToString());
            StringAssert.Contains("input 0 (the MAIN item)", p["help"]["inputOrder"].ToString());
        }
    }
}
