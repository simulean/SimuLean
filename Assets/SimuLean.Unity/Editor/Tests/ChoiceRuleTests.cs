using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>Choice rules the example rules of SimuLean.UserLogic are found in the editor, a scene that uses
    /// one extracts it with its parameters and runs headless as the declarative form it mirrors, the model card lists it in its
    /// scripts, and the simulean_rule_* commands answer.</summary>
    public class ChoiceRuleTests
    {
        [Test]
        public void TheExampleRulesAreFoundInTheUserLogicAssembly()
        {
            var t = SimuLean.Rules.ChoiceRules.Find("ShortestQueueRule");
            Assert.IsNotNull(t, "ShortestQueueRule is compiled and found");
            Assert.AreEqual("SimuLean.UserLogic", t.Assembly.GetName().Name, "rules live in their own assembly");
            Assert.IsFalse(t.Assembly.GetReferencedAssemblies().Any(a => a.Name.StartsWith("UnityEngine")), "the rules' assembly has no Unity reference");
            foreach (var r in SimuLean.Rules.ChoiceRules.All.Where(x => x.Assembly == t.Assembly))
                CollectionAssert.IsEmpty(SimuLean.Rules.ChoiceRules.ContractProblems(r), "the contract scan: " + r.Name);
            CollectionAssert.Contains(SimuLean.Rules.ChoiceRules.Names(SimuLean.Rules.RuleSite.Release), "EarliestDueDateRule");
        }

        [Test]
        public void ASceneQueueWithARoutingAndAReleaseRuleRunsHeadlessAsTheDeclarativeForms()
        {
            var root = new GameObject("RulesModel");
            try
            {
                var q = new GameObject("Q").AddComponent<UnityQueue>();
                q.transform.SetParent(root.transform);
                q.offerRule = "EarliestDueDateRule";
                q.ruleParameters = new List<RuleParameterValue> { new RuleParameterValue { name = "DueLabel", value = "prio" } };
                q.outputStrategy.strategyType = OutputStrategyType.Rule;
                q.outputStrategy.rule = "ShortestQueueRule";
                q.outputStrategy.ruleParameters = new List<RuleParameterValue> { new RuleParameterValue { name = "FreeWeight", value = "0.5" } };
                var config = SceneExtractor.ExtractElement(q);
                Assert.AreEqual("Rule", config.Get<string>("outputStrategy.strategyType"));
                Assert.AreEqual("ShortestQueueRule", config.Get<string>("outputStrategy.rule"));
                Assert.AreEqual("EarliestDueDateRule", config.Get<string>("offerRule"));
                var records = config.Get<List<PropertySettings>>("rule");
                Assert.AreEqual(0.5, records.First(r => r.Name == "FreeWeight").Value);
                Assert.AreEqual("prio", records.First(r => r.Name == "DueLabel").Text);

                var model = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
                model.BuildModel(new SimulationConfig { Elements = { config } });
                var built = (ItemsQueue)model.Find("Q");
                Assert.IsInstanceOf<SimuLean.Rules.RuleRoutingStrategy>(built.GetOutputStrategy());
                Assert.IsInstanceOf<SimuLean.Rules.RuleItemOrder>(built.OfferSort);
                var rule = (SimuLean.UserLogic.Examples.ShortestQueueRule)((SimuLean.Rules.RuleRoutingStrategy)built.GetOutputStrategy()).Rule;
                Assert.AreEqual(0.5, rule.FreeWeight, "the scene's parameter value reaches the rule");

                // the catalog: rule.FreeWeight, a number a study varies
                var keys = SimuLean.Optimization.ModelParameterCatalog.ForElement(config).Select(p => p.Key).ToList();
                CollectionAssert.Contains(keys, "rule.FreeWeight");
                CollectionAssert.Contains(keys, "outputStrategy.rule");

                // the model card's scripts both rules with their Why, the decisions by rung; no validate warning
                // (both rules have a Why, neither is at the initial values that equal its primitive)
                var report = SimuLean.Logic.ModelLogic.Of(new SimulationConfig { Elements = { config } });
                var scripts = report.Scripts();
                var rules = ((System.Collections.IList)scripts["rules"]).Cast<Dictionary<string, object>>().ToList();
                CollectionAssert.AreEquivalent(new[] { "ShortestQueueRule", "EarliestDueDateRule" }, rules.Select(r => (string)r["rule"]).ToList());
                Assert.IsTrue(rules.All(r => r["why"] is string w && w.Length > 0 && r["sourceHash"] is string h && h.Length == 8));
                Assert.AreEqual(2, (int)scripts["scripted"], (string)scripts["summary"]);
                CollectionAssert.IsEmpty(report.RuleWarnings());
                var other = (Dictionary<string, object>)SimuLeanCommands.CardScripts("Assets/NotOpen.unity");
                StringAssert.Contains("open the model", (string)other["note"]);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void RuleShowAndCheckAnswerAndRuleNewRefusesARuleWithoutWhy()
        {
            var show = (JObject)(SimuLeanCommands.RuleShow("HighestPriorityRule"));
            Assert.AreEqual("dispatch", (string)show["site"], show.ToString());
            Assert.AreEqual("rule.AgeWeight", (string)show["parameters"][0]["key"]);
            Assert.IsTrue((bool)show["ok"]);
            StringAssert.EndsWith("HighestPriorityRule.cs", (string)show["file"]);
            Assert.AreEqual(8, ((string)show["sourceHash"]).Length);

            var all = (JObject)(SimuLeanCommands.RuleShow(""));
            Assert.IsTrue(all["rules"].Any(r => (string)r["name"] == "ShortestQueueRule" && (string)r["site"] == "routing"));

            var missing = Assert.Catch(() => SimuLeanCommands.RuleCheck("ShortestQueueRul"));
            StringAssert.Contains("ShortestQueueRule", missing.Message, "did you mean");

            var noWhy = Assert.Catch(() => SimuLeanCommands.RuleNew("routing", "MyRouting", ""));
            StringAssert.Contains("why", noWhy.Message);
            var badKind = Assert.Catch(() => SimuLeanCommands.RuleNew("routes", "MyRouting", "BestScore cannot read the order book of the stores"));
            StringAssert.Contains("routing, release or dispatch", badKind.Message);

            string source = SimuLeanCommands.RuleTemplate("release", "MyRelease", "offerSort cannot look ahead at the next station's setup");
            StringAssert.Contains("public sealed class MyRelease : IReleaseRule", source);
            StringAssert.Contains("Why = \"offerSort cannot look ahead", source);
            StringAssert.Contains("[RuleParameter(", source);
        }
    }
}
