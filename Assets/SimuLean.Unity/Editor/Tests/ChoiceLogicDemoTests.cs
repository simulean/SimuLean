using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using SimuLean.Headless;
using SimuLean.Serialization;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The example scene <c>ChoiceLogicDemo</c>: it runs headless (every station used, both reactions fire), its logic
    /// section shows each site of the choice logic (BestScore, offerSort, a choice rule, the admission wake-up, Set Item Look).
    /// </summary>
    public class ChoiceLogicDemoTests
    {
        private const string Demo = "Assets/Scenes/ChoiceLogicDemo.unity";

        [Test]
        public void TheDemoRunsHeadlessAndUsesEveryStation()
        {
            using (var iso = new IsolatedScene(Demo))
            {
                SimulationConfig cfg = SceneExtractor.Extract(iso.Root).Config;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                Dictionary<string, Element> elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(14400);
                foreach (string name in new[] { "Cell A", "Cell B", "Flex Cell", "Packer 1", "Packer 2", "Shipped" })
                    Assert.Greater(factory.Find(name).GetStatsInputCount(), 0, $"{name} received items");
                Assert.AreEqual(2, factory.ReactionsById.Count, "both reactions built");
                foreach (var r in factory.ReactionsById.Values)
                    Assert.Greater(r.Fired, 0, $"reaction {r.Name} fired");
            }
        }

        [Test]
        public void TheModelLogicWindowShowsTheRunTimeCountersOfARun()
        {
            using (var iso = new IsolatedScene(Demo))
            {
                SimulationConfig cfg = SceneExtractor.Extract(iso.Root).Config;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(14400);
                var report = SimuLean.Logic.ModelLogic.Live(SimuLean.Logic.ModelLogic.Of(cfg), factory);
                var routing = report.Find("Router").Decisions.First(d => d.Site == "routing");
                StringAssert.StartsWith("routed Cell A ", routing.LiveText);
                StringAssert.Contains("last → ", routing.LiveText);
                var flex = report.Find("Flex Cell").Decisions.First(d => d.Site == "admission");
                Assert.Greater((long)flex.Live["wakeUps"], 0, flex.LiveText);
                // entries are the items that entered (one each); checks the condition's evaluations
                Assert.AreEqual((long)factory.Find("Flex Cell").GetStatsInputCount(), (long)flex.Live["entered"], flex.LiveText);
                Assert.GreaterOrEqual((long)flex.Live["checks"], (long)flex.Live["entered"], flex.LiveText);
                StringAssert.Contains(" for item ", routing.LiveText, "the last choice names its item and labels");
                Assert.Greater((long)report.Find("Pack Queue").Decisions.First(d => d.Site == "order").Live["released"], 0);
                Assert.Greater((long)report.Find("Pack Queue").Decisions.First(d => d.Site == "routing").Live["ruleCalls"], 0);
                var window = ScriptableObject.CreateInstance<ModelLogicWindow>();
                try
                {
                    window.ShowLive(factory);
                    var lines = window.VisibleLines();
                    string all = string.Join("\n", lines);
                    Assert.IsTrue(lines.Any(l => l.StartsWith("    live: routed Cell A ")), all);
                    Assert.IsTrue(lines.Any(l => l.StartsWith("    live: fired ")), "the reaction's fires: " + all);
                }
                finally { Object.DestroyImmediate(window); }
            }
        }

        [Test]
        public void TheLogicSectionShowsEachDecisionSite()
        {
            using (var iso = new IsolatedScene(Demo))
            {
                var answer = (JObject)SimuLeanCommands.Model("", true, iso.Root.name, "logic");
                Assert.AreEqual(0, ((JArray)answer["errors"]).Count, "errors: " + answer["errors"]);
                JToken Decision(string owner, string site)
                    => answer["elements"].First(e => (string)e["name"] == owner)["decisions"].FirstOrDefault(d => (string)d["site"] == site);
                StringAssert.StartsWith("Route best score: (out.family == item.family ? 10 : 0) - out.content", (string)Decision("Router", "routing")["say"]);
                Assert.IsTrue(Decision("Router", "routing")["settings"].Any(x => ((string)x).StartsWith("outputStrategy.scoreExpression = ")), "the raw setting");
                Assert.AreEqual("Release order prio desc, age", (string)Decision("Pack Queue", "order")["say"]);
                Assert.AreEqual("ShortestQueueRule", (string)Decision("Pack Queue", "routing")["rule"]);
                StringAssert.Contains("woken by Flex Cell Shift", (string)Decision("Flex Cell", "admission")["say"]);
                Assert.IsFalse(answer.ToString().Contains("parameterKey"), "a setting its strategy does not use is not listed");
                Assert.IsNull(answer["elements"].FirstOrDefault(e => (string)e["name"] == "Cell A"), "Cell A has no decision of its own");
                Assert.IsTrue(answer["wakeUps"].Any(w => (string)w["element"] == "Flex Cell" && (string)w["property"] == "open"), "the wake-up");
                Assert.IsTrue(answer["reactions"].SelectMany(r => r["decisions"]).Any(d => (bool?)d["viewOnly"] == true), "Set Item Look");
            }
        }
    }
}
