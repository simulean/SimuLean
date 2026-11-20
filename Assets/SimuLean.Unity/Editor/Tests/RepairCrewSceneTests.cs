using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SimuLean.Downtime;
using SimuLean.Resources;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>Repair crews in a scene: a downtime's <c>Repair By</c> block and a resource team as a downtime's target
    /// are extracted as config keys (every key decided) and built by the engine (one build path).</summary>
    public class RepairCrewSceneTests
    {
        [Test]
        public void RepairByAndATeamTargetExtractAndBuild()
        {
            var root = new GameObject("Model") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                T Make<T>(string n) where T : Component
                {
                    var go = new GameObject(n);
                    go.transform.SetParent(root.transform);
                    return go.AddComponent<T>();
                }

                var mech = Make<UnityResourceTeam>("Mechanics");
                mech.travel = TeamTravel.Abstract;
                mech.AddMembers(1);
                var ops = Make<UnityResourceTeam>("Operators");
                ops.travel = TeamTravel.Abstract;
                ops.AddMembers(2);
                var src = Make<UnitySource>("Src");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2f };
                var press = Make<UnityMultiServer>("Press");
                press.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1.5f };
                press.processBy = new ProcessResourceUse { team = ops, preemption = PreemptMode.Resume };
                var sink = Make<UnitySink>("Sink");
                src.nextElements = new List<SElement> { press };
                press.nextElements = new List<SElement> { sink };

                Assert.IsFalse(new RepairResourceUse().preemptable, "a repair is not preemptable by default");
                Assert.AreEqual("1", new RepairResourceUse().priority, "a repair's priority is 1 by default");

                var fail = Make<UnityDowntime>("PressFail");
                fail.kind = UnityDowntime.DowntimeKind.MtbfMttr;
                fail.targets.Add(press);
                fail.timeToFailure = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 50f };
                fail.timeToRepair = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 5f };
                fail.repairBy = new RepairResourceUse { team = mech, priority = "5", preemption = PreemptMode.Resume };

                var opFail = Make<UnityDowntime>("OperatorFail");
                opFail.kind = UnityDowntime.DowntimeKind.MtbfMttr;
                opFail.teamTargets.Add(ops);
                opFail.timeToFailure = new RandomProcessConfig { strategyType = RandomProcessType.Exponential, exponentialRate = 0.02f };
                opFail.timeToRepair = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 4f };
                opFail.repairBy = new RepairResourceUse { team = mech, preemptable = true };

                var x = SceneExtractor.Extract(root);
                var config = x.Config;
                var dc = config.Downtimes.Single(d => d.Id == "PressFail");
                Assert.AreEqual(x.TeamIds[mech], dc.Get<string>("repairBy.team", ""), "the crew's team id");
                Assert.AreEqual("5", dc.Get<string>("repairBy.priority", ""));
                Assert.AreEqual("Resume", dc.Get<string>("repairBy.preemption", ""));
                Assert.IsFalse(dc.Get<bool>("repairBy.preemptable", true));
                var oc = config.Downtimes.Single(d => d.Id == "OperatorFail");
                Assert.That(oc.TargetIds, Is.EquivalentTo(new[] { x.TeamIds[ops] }), "the team is the target");
                Assert.IsTrue(oc.Get<bool>("repairBy.preemptable", false));
                Assert.IsEmpty(SimuLean.Optimization.ModelParameterCatalog.Undecided(config), "every key is decided");

                var clock = new SimClock(3);
                var model = new SimuLean.Headless.HeadlessModelFactory(clock);
                model.BuildModel(config);
                model.StartModel();
                clock.AdvanceClock(2000);
                var gen = model.DowntimesById["PressFail"].Single();
                Assert.IsNotNull(gen.Repair, "the press's downtime has its crew");
                Assert.Greater(gen.Repair.Repairs, 30, "repaired every 55");
                Assert.AreEqual(5.0, gen.Repair.RepairTimeAvg, 1e-9, "the repair lasts its time once the crew is there");
                var memberGens = model.DowntimesById["OperatorFail"];
                Assert.AreEqual(2, memberGens.Count, "one generator per member");
                Assert.That(memberGens.Select(g => g.Target).All(t => t is TaskExecutor), "the members are the targets");
                Assert.AreNotEqual(memberGens[0].Target, memberGens[1].Target);
                Assert.Greater(model.Find("Press").GetStateRatio(ElementState.BREAKDOWN), 0.0);
                var results = SimuLean.Results.RunResults.Capture(model, "repair");
                var o = results.Find(SimuLean.Results.ResultScope.Downtime, "PressFail");
                Assert.AreEqual(gen.Repair.Repairs, o[SimuLean.Results.ResultMetrics.Repairs], 1e-9, "repairs reported");

                fail.kind = UnityDowntime.DowntimeKind.Shift;
                Assert.IsFalse(SceneExtractor.Extract(root).Config.Downtimes.Single(d => d.Id == "PressFail")
                               .Has("repairBy.team"), "a shift exports no crew");
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
