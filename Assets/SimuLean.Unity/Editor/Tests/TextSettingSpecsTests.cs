using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using SimuLean.Optimization;
using SimuLean.Experimentation.Controller;
using SimuLean.Experimentation.Core;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Text settings (a resource use's priority expression, a team's request order written in place) in the scene tools:
    /// <c>simulean_params</c> lists them with their kind and current text, an Experimenter row takes each scenario's text,
    /// the Optimizer chooses among the texts its row gives, and a structural key (a team reference) stays refused.
    /// </summary>
    public class TextSettingSpecsTests : SceneTestFixture
    {
        private const string Root = "TextSettingModel";
        protected override string RootName => Root;

        private static JObject J(object answer) => JObject.FromObject(answer);

        // a line whose station is served by a one-member team, by a priority expression
        private void BuildLine()
        {
            SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps("T", Root));
            SimuLeanCommands.AddTeam("TOps", 1, "", 0, 0, 3, Root);
            SimuLeanCommands.Set("TSrv", "processBy.team", "TOps");
            SimuLeanCommands.Set("TSrv", "processBy.priority", "1 + 1");
        }

        private static SElement Station => GameObject.Find("TSrv").GetComponent<SElement>();
        private static UnityResourceTeam Team => GameObject.Find("TOps").GetComponent<UnityResourceTeam>();

        [Test]
        public void ParamsListsTextSettingsWithTheirKindAndCurrentText()
        {
            BuildLine();
            var station = J(SimuLeanCommands.Params("TSrv"));
            var prio = station["textFactors"]?["processBy.priority"];
            Assert.IsNotNull(prio, station.ToString());
            Assert.AreEqual("text", (string)prio["kind"]);
            Assert.AreEqual("1 + 1", (string)prio["now"]);
            Assert.IsNull(station["textFactors"]["processBy.team"], "a team reference is no text setting");
            var team = J(SimuLeanCommands.Params("TOps"));
            Assert.AreEqual("", (string)team["textFactors"]?["requestOrder"]?["now"], team.ToString());
            Assert.IsNotNull(J(SimuLeanCommands.Types("MultiServer"))["textFactors"], "simulean_types answers the section too");
        }

        [Test]
        public void AnExperimenterRowTakesEachScenariosText()
        {
            BuildLine();
            var exp = Test.Root.AddComponent<Experimenter>();
            var so = new SerializedObject(exp);
            so.FindProperty("modelRoot").objectReferenceValue = Test.Root;
            so.FindProperty("runTime").doubleValue = 100;
            so.FindProperty("randomSeed").intValue = 3;
            so.FindProperty("exportFormats").intValue = 0;
            so.ApplyModifiedPropertiesWithoutUndo();
            var prio = new ParameterSpec { name = "prio", target = ParameterTarget.Element, element = Station, key = "processBy.priority" };
            var order = new ParameterSpec { name = "order", target = ParameterTarget.Team, team = Team, key = "requestOrder" };
            Assert.IsTrue(prio.Info()?.IsText ?? false, "the catalog lists the priority as a text setting");
            Assert.IsTrue(order.Info()?.IsText ?? false, "and the team's request order");
            exp.Parameters.Add(prio);
            exp.Parameters.Add(order);
            exp.Metrics.Add(new MetricSpec { name = "out", scope = MetricScope.Element, element = GameObject.Find("TSink").GetComponent<SElement>(), statistic = "input" });
            exp.Scenarios.Add(new ExperimentScenario { scenarioName = "fifo", replications = 1, parameterValues = new List<string> { "1 + 1", "" } });
            exp.Scenarios.Add(new ExperimentScenario { scenarioName = "byPriority", replications = 1, parameterValues = new List<string> { "2 * 3", "|priority desc" } });
            Assert.IsNull(exp.Validate());

            var config = SceneExtractor.Extract(Test.Root).Config;
            exp.BuildStudy(SceneExtractor.Extract(Test.Root).Config).Apply(config, "byPriority");
            Assert.AreEqual("2 * 3", config.Elements.Find(e => e.Name == "TSrv").Get<string>("processBy.priority"));
            Assert.AreEqual("|priority desc", config.ResourceTeams.Find(t => t.Name == "TOps").RequestOrder);

            var runs = exp.RunHeadlessNow();
            Assert.AreEqual(2, runs.Count);
            Assert.IsTrue(runs.All(r => r.Error == null), string.Join(" | ", runs.Select(r => r.Error)));

            // a structural key (a team reference) is never a factor
            exp.Parameters.Add(new ParameterSpec { name = "who", target = ParameterTarget.Element, element = Station, key = "processBy.team" });
            exp.Scenarios[0].parameterValues.Add("TOps");
            exp.Scenarios[1].parameterValues.Add("TOps");
            StringAssert.Contains("has no parameter", exp.Validate());
        }

        [Test]
        public void TheOptimizerChoosesAmongTheTextsOfItsRow()
        {
            BuildLine();
            var config = SceneExtractor.Extract(Test.Root).Config;
            var spec = new ParameterSpec { name = "prio", target = ParameterTarget.Element, element = Station, key = "processBy.priority" };
            Assert.IsTrue(spec.IsText(config));
            var empty = Assert.Throws<System.InvalidOperationException>(() => spec.ToModelParameter(config));
            StringAssert.Contains("needs the texts", empty.Message);

            spec.texts = new List<string> { "1 + 1", "2 * 3" };
            var p = spec.ToModelParameter(config);
            Assert.IsInstanceOf<TextSettingParameter>(p);
            Assert.AreEqual(VariableKind.Choice, p.Variable.Kind);
            CollectionAssert.AreEqual(new[] { "1 + 1", "2 * 3" }, p.Variable.Options.ToArray());

            var objective = new SimulationObjective(config, new List<ModelParameter> { p },
                new List<ModelMetric> { new ModelMetric("out", MetricScope.Element, "TSink", "input") }, "out") { RunTime = 100 };
            var result = OptimizationEngine.Run(objective.BuildSpace(), objective, new RandomSearch { Samples = 4 }, new OptimizationSettings { Seed = 1 });
            Assert.IsNull(result.BestEvaluation.Error, result.BestEvaluation.Error);
            CollectionAssert.Contains(spec.texts, result.Best.Option(0), "the best is one of the texts given");
            Assert.AreEqual("1 + 1", config.Elements.Find(e => e.Name == "TSrv").Get<string>("processBy.priority"), "the scene's model is not changed");
        }
    }
}
