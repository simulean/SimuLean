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
    /// The agent interface's rule and listing commands: a rule parameter
    /// list that changes nothing is a warning naming the other list, list items printed readably by
    /// <c>simulean_params</c> and the compact <c>simulean_types</c> list with its pack filter. The save
    /// command's guard is in <see cref="SimuLeanModelsTests"/>. Each test builds in a scene of its own.
    /// </summary>
    public class AgentRuleAndListingCommandsTests : SceneTestFixture
    {
        private const string Root = "RuleListingModel";
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
        private static string Warnings(JObject a) => string.Join(" | ", (a["warnings"] as JArray ?? new JArray()).Select(w => (string)w));

        [Test]
        public void ARuleParameterListThatChangesNothingWarns()
        {
            SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps("P", Root));
            SimuLeanCommands.Set("PBuf", "outputStrategy.strategyType", "Rule");
            SimuLeanCommands.Set("PBuf", "outputStrategy.rule", "ShortestQueueRule");
            // the release list while the Queue has no release rule: the name is the routing rule's
            var a = J(SimuLeanCommands.Set("PBuf", "ruleParameters", "FreeWeight = 0.5"));
            StringAssert.Contains("offerRule is empty", Warnings(a));
            StringAssert.Contains("outputStrategy.ruleParameters", Warnings(a), "names the other list");
            // validate says it too (the extractor drops the list)
            var v = J(SimuLeanCommands.Validate(root: Root));
            StringAssert.Contains("offerRule is empty", Warnings(v), v.ToString());

            // a release rule set: the stray name is not one of its parameters, and the other list is named
            var b = J(SimuLeanCommands.Set("PBuf", "offerRule", "EarliestDueDateRule"));
            StringAssert.Contains("has no parameter 'FreeWeight'", Warnings(b));
            StringAssert.Contains("did you mean outputStrategy.ruleParameters", Warnings(b));

            // the right list: no warning
            SimuLeanCommands.Set("PBuf", "ruleParameters", "");
            var c = J(SimuLeanCommands.Set("PBuf", "outputStrategy.ruleParameters", "FreeWeight = 0.5"));
            Assert.IsFalse(Warnings(c).Contains("ruleParameters"), Warnings(c));

            // simulean_apply checks once at its end: parameters set before their rule are fine
            SimuLeanCommands.Set("PBuf", "offerRule", "");
            var d = J(SimuLeanCommands.Apply("[{\"op\":\"set\",\"object\":\"PBuf\",\"key\":\"ruleParameters\",\"value\":\"DueLabel = due2\"}," +
                                             "{\"op\":\"set\",\"object\":\"PBuf\",\"key\":\"offerRule\",\"value\":\"EarliestDueDateRule\"}]"));
            Assert.IsFalse(Warnings(d).Contains("ruleParameters"), Warnings(d));
            var e = J(SimuLeanCommands.Apply("[{\"op\":\"set\",\"object\":\"PBuf\",\"key\":\"ruleParameters\",\"value\":\"FreeWeigth = 1\"}]"));
            StringAssert.Contains("has no parameter 'FreeWeigth'", Warnings(e));
        }

        [Test]
        public void ParamsPrintsListItemsReadably()
        {
            SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps("L", Root));
            SimuLeanCommands.AddTeam("NamedOps", 2, "", 0, 0, 3, Root);
            SimuLeanCommands.Set("NamedOps", "members[1].name", "Anna");
            var team = J(SimuLeanCommands.Params("NamedOps"));
            string members = (string)team["fields"].First(f => (string)f["path"] == "members")["value"];
            StringAssert.Contains("NamedOps_1", members, "the generated name of an unnamed member");
            StringAssert.Contains("Anna", members);
            Assert.IsFalse(members.Contains("ResourceMemberSpec"), members);

            SimuLeanCommands.Set("LBuf", "offerRule", "EarliestDueDateRule");
            var queue = J(SimuLeanCommands.Params("LBuf"));
            Assert.AreEqual("[DueLabel = due]", (string)queue["fields"].First(f => (string)f["path"] == "ruleParameters")["value"]);

            var go = new GameObject("LookReaction");
            go.transform.SetParent(Test.Root.transform);
            go.AddComponent<UnityReaction>();
            SimuLeanCommands.Set("LookReaction", "actions[0].kind", "SetItemLook");
            SimuLeanCommands.Set("LookReaction", "actions[0].color", "#E53935");
            string actions = (string)J(SimuLeanCommands.Params("LookReaction"))["fields"].First(f => (string)f["path"] == "actions")["value"];
            StringAssert.Contains("kind = SetItemLook", actions);
            StringAssert.Contains("color = #E53935", actions);
        }

        [Test]
        public void TypesListsTheCorePackAndOneLinePerPack()
        {
            var compact = J(SimuLeanCommands.Types());
            var names = compact["types"].Select(t => (string)t["name"]).ToList();
            CollectionAssert.Contains(names, "Queue");
            CollectionAssert.Contains(names, "Storage Rack");
            Assert.IsTrue(compact["types"].All(t => (string)t["pack"] == SimuLeanPacks.Core), "only Core by default");
            CollectionAssert.DoesNotContain(names, "Wall", "no decoration by default");
            StringAssert.Contains("Pick Face", (string)compact["packs"][SimuLeanPacks.Warehousing], "a pack's line names its entries");
            Assert.AreEqual("Storage Rack", (string)compact["types"].First(t => (string)t["name"] == "Pallet Rack")["variantOf"],
                            "a preset names the type it places");

            var all = J(SimuLeanCommands.Types(pack: "all"))["types"];
            Assert.Greater(all.Count(), names.Count);
            var warehousing = J(SimuLeanCommands.Types(pack: "warehousing"))["types"];
            Assert.IsTrue(warehousing.All(t => (string)t["pack"] == SimuLeanPacks.Warehousing) && warehousing.Any(t => (string)t["name"] == "Pick Face"));
            var presets = J(SimuLeanCommands.Types(pack: "Presets"))["types"];
            Assert.IsTrue(presets.All(t => (string)t["group"] == "Presets"), "a group");
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Types(pack: "Healthcar"));
            StringAssert.Contains("Healthcare", e.Message);
        }
    }
}
