using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The logic viewer on example scenes: <c>simulean_model section=logic</c> answers every decision site
    /// of the extracted model (the engine's <see cref="SimuLean.Logic.ModelLogic"/>), the <i>Model Logic</i> window builds the
    /// same report, and neither changes the scene.
    /// </summary>
    public class ModelLogicTests
    {
        private const string Workshop = "Assets/Scenes/LibraryExamples/Workshop_Advanced.unity";
        private const string Reactions = "Assets/Scenes/ReactionsDemo.unity";

        private static JObject Logic(IsolatedScene iso)
            => (JObject)SimuLeanCommands.Model("", true, iso.Root.name, "logic");

        [Test]
        public void TheLogicSectionListsTheDecisionSitesOfAnExampleScene()
        {
            using (var iso = new IsolatedScene(Workshop))
            {
                bool dirty = iso.Scene.isDirty;
                var answer = Logic(iso);
                Assert.AreEqual(0, ((JArray)answer["errors"]).Count, "errors: " + answer["errors"]);
                var elements = (JArray)answer["elements"];
                Assert.IsNotNull(elements, "elements with decisions");
                Assert.Greater(elements.Count, 3);
                var teams = (JArray)answer["teams"];
                Assert.IsNotNull(teams, "the teams");
                foreach (var t in teams)
                    Assert.IsTrue(t["decisions"].Any(d => (string)d["site"] == "dispatch"
                                                          && (((string)d["say"]).StartsWith("Dispatch requests") || ((string)d["say"]).StartsWith("Rule "))),
                                  $"team {t["name"]}: its request order");
                Assert.IsTrue(teams.Any(t => t["usedBy"] != null), "the uses of the teams");
                var all = answer.Descendants().OfType<JProperty>().ToList();
                Assert.IsFalse(all.Any(p => p.Name == "problem"), "every expression and query parses");
                Assert.IsNull(answer["clock"], "the logic section only, not the model's structure");

                // one object; an unknown name suggests
                string name = (string)elements[0]["name"];
                var one = (JObject)SimuLeanCommands.Model(name, true, iso.Root.name, "logic");
                Assert.AreEqual(1, ((JArray)one["elements"]).Count, "one element");
                Assert.Throws<System.ArgumentException>(() => SimuLeanCommands.Model("", true, iso.Root.name, "logik"), "an unknown section");
                Assert.AreEqual(dirty, iso.Scene.isDirty, "read-only: the scene is not modified");
            }
        }

        [Test]
        public void ReactionsShowTheirSignalAndWhatTheirActionsWrite()
        {
            using (var iso = new IsolatedScene(Reactions))
            {
                var answer = Logic(iso);
                var reactions = (JArray)answer["reactions"];
                Assert.IsNotNull(reactions, "the reactions: " + answer);
                foreach (var r in reactions)
                {
                    var decisions = (JArray)r["decisions"];
                    Assert.AreEqual(1, decisions.Count, $"reaction {r["name"]}: one decision");
                    var d = decisions[0];
                    StringAssert.StartsWith("When ", (string)d["say"], $"reaction {r["name"]}: its signal first");
                    Assert.AreEqual("signal", ((string)d["settings"][0]).Split(' ')[0], $"reaction {r["name"]}: its raw settings");
                    if (d["settings"].Any(x => ((string)x).StartsWith("actions[")))
                        Assert.IsTrue(d["writes"] != null || (bool?)d["viewOnly"] == true, $"reaction {r["name"]}: what it writes");
                }
            }
        }

        [Test]
        public void TheWindowBuildsTheSameReport()
        {
            using (var iso = new IsolatedScene(Workshop))
            {
                var window = ScriptableObject.CreateInstance<ModelLogicWindow>();
                try
                {
                    window.Refresh(iso.Root);
                    Assert.IsNull(window.Error, window.Error);
                    Assert.IsNotNull(window.Report);
                    var expected = Logic(iso);
                    var elements = window.Report.OfKind("element").Where(o => o.Decisions.Count > 0 || o.WokenBy.Count > 0 || o.Notes.Count > 0).Select(o => o.Name);
                    CollectionAssert.IsSubsetOf(((JArray)expected["elements"]).Select(e => (string)e["name"]).ToList(), elements.ToList(),
                                                "the window shows what the command answers");
                }
                finally { Object.DestroyImmediate(window); }
            }
        }

        [Test]
        public void TheWindowsDefaultViewOfTheChoiceDemoShowsOnlyRealDecisions()
        {
            using (var iso = new IsolatedScene("Assets/Scenes/ChoiceLogicDemo.unity"))
            {
                var window = ScriptableObject.CreateInstance<ModelLogicWindow>();
                try
                {
                    window.Refresh(iso.Root);
                    Assert.IsNull(window.Error, window.Error);
                    var lines = window.VisibleLines();
                    string all = string.Join("\n", lines);
                    foreach (string noise in new[] { "parameterKey", "routing_mode", "maxWait", "onTimeout", "KeepWaiting", "(default)" })
                        Assert.IsFalse(all.Contains(noise), $"no '{noise}' in the default view:\n{all}");
                    Assert.IsTrue(lines.Any(l => l.Contains("[routing] Route best score: (out.family == item.family ? 10 : 0) - out.content")), all);
                    Assert.IsTrue(lines.Any(l => l.Contains("[admission] Admit when element.open == 1 · woken by")), all);
                    Assert.IsTrue(lines.Any(l => l.Contains("[order] Release order prio desc, age")), all);
                    Assert.IsTrue(lines.Any(l => l.Contains("[routing] Rule ShortestQueueRule")), all);
                    Assert.IsTrue(lines.Any(l => l.StartsWith("No decisions of their own") && l.Contains("Cell A")), all);
                    window.ShowAll = true;
                    Assert.IsTrue(window.VisibleLines().Any(l => l.Contains("(default)")), "All shows the defaults too");
                }
                finally { Object.DestroyImmediate(window); }
            }
        }
    }
}
