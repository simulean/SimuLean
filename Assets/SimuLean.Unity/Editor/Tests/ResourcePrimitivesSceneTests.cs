using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SimuLean.Headless;
using SimuLean.Resources;
using SimuLean.Serialization;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The resource primitives in a scene: the fields of the Process block, a
    /// requirement's team and an assembly task's own requirements, a station's Reserve Exit and the Process
    /// block's Acquire Before Entry are extracted under their keys and built by the engine.
    /// </summary>
    public class ResourcePrimitivesSceneTests
    {
        // A scene model built by `build` under a root, extracted; the scene is closed (or the root destroyed) after.
        private static void WithModel(Action<Func<string, GameObject>> build, Action<SimulationConfig> check)
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                build(n => { var go = new GameObject(n); go.transform.SetParent(root.transform); return go; });
                check(SceneExtractor.Extract(root).Config);
            }
        }

        private static UnityResourceTeam Team(Func<string, GameObject> make, string name)
        {
            var team = make(name).AddComponent<UnityResourceTeam>();
            team.travel = TeamTravel.Abstract;
            team.AddMembers(1);
            return team;
        }

        /// <summary>A requirement of another team and the acquisition order are exported and built.</summary>
        [Test]
        public void RequirementTeamAndAcquireOrderExtractAndBuild()
        {
            UnityResourceTeam cranes = null, drivers = null;
            WithModel(make =>
            {
                cranes = Team(make, "Cranes"); drivers = Team(make, "Drivers");
                var src = make("Src").AddComponent<UnitySource>();
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 10f };
                var lift = make("Lift").AddComponent<UnityMultiServer>();
                lift.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2f };
                var sink = make("Sink").AddComponent<UnitySink>();
                src.nextElements = new List<SElement> { lift };
                lift.nextElements = new List<SElement> { sink };
                lift.processBy = new ProcessResourceUse
                {
                    team = cranes, acquire = AcquireOrder.InOrder,
                    requirements = new List<ResourceRequirementSpec>
                    {
                        new ResourceRequirementSpec { label = "crane" },
                        new ResourceRequirementSpec { label = "driver", team = drivers },
                    },
                };
            }, cfg =>
            {
                var ec = cfg.Elements.Single(e => e.Name == "Lift");
                Assert.AreEqual("InOrder", ec.Get<string>("processBy.acquire", ""), "acquire exported");
                var reqs = ec.Get<List<RequirementSettings>>("processBy.requirements", null);
                Assert.AreEqual(2, reqs.Count);
                Assert.IsNull(reqs[0].Team, "the use's own team is not repeated");
                Assert.AreEqual(SceneExtractor.TeamId(drivers), reqs[1].Team, "the driver's team by its model id");
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                factory.BuildModel(cfg);
                var use = ((IResourceUser)factory.Find("Lift")).Resources;
                Assert.AreEqual(AcquireOrder.InOrder, use.Order);
                Assert.AreSame(factory.Teams[SceneExtractor.TeamId(drivers)], use.Requirements[1].Team, "the requirement's team built");
                factory.StartModel();
                clock.AdvanceClock(100);
                Assert.Greater(factory.Find("Sink").GetStatsInputCount(), 5, "the lift runs with both teams");
            });
        }

        /// <summary>An assembly task's own requirements are exported as the task's <c>tasks[i].requirements</c> and built.</summary>
        [Test]
        public void AssemblyTaskRequirementsExtractAndBuild()
        {
            UnityResourceTeam cranes = null;
            WithModel(make =>
            {
                var ops = Team(make, "Fitters"); cranes = Team(make, "Cranes");
                var src = make("Src").AddComponent<UnitySource>();
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 20f };
                var asy = make("Asy").AddComponent<UnityAssemblyStation>();
                asy.tasks = new List<AssemblyTaskConfig>
                {
                    new AssemblyTaskConfig { id = "fit", duration = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 4f } },
                    new AssemblyTaskConfig
                    {
                        id = "lift", predecessors = "fit",
                        duration = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 3f },
                        requirements = new List<ResourceRequirementSpec> { new ResourceRequirementSpec { team = cranes }, new ResourceRequirementSpec() },
                    },
                };
                asy.processBy = new ProcessResourceUse { team = ops };
                var sink = make("Sink").AddComponent<UnitySink>();
                src.nextElements = new List<SElement> { asy };
                asy.nextElements = new List<SElement> { sink };
            }, cfg =>
            {
                var ec = cfg.Elements.Single(e => e.Name == "Asy");
                var tasks = ec.SettingsAs<AssemblyStationSettings>().Tasks;
                var liftCfg = tasks.Single(t => t.Id == "lift");
                Assert.AreEqual(2, liftCfg.ProcessBy.Requirements.Count, "two records of the lift, in the lift's own settings");
                Assert.IsTrue(tasks.Where(t => t.Id != "lift").All(t => t.ProcessBy == null), "the other tasks have none");
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                factory.BuildModel(cfg);
                var station = (AssemblyStation)factory.Find("Asy");
                var lift = station.Tasks.Single(t => t.Id == "lift").Resources;
                Assert.IsNotNull(lift, "the lift has its own use");
                Assert.AreSame(factory.Teams[SceneExtractor.TeamId(cranes)], lift.Requirements[0].Team);
                Assert.IsNull(station.Tasks.Single(t => t.Id == "fit").Resources, "the fit uses the station's");
            });
        }

        /// <summary>A station without a process team whose tasks name their own teams — the Process block's
        /// priority, preemption, preemptable, acquisition order and max wait are exported (no team key), offered by the
        /// catalog and carried by the built task uses.</summary>
        [Test]
        public void AssemblyTaskSettingsWithoutStationTeamExtractAndBuild()
        {
            UnityResourceTeam cranes = null, drivers = null;
            UnityAssemblyStation asy = null;
            WithModel(make =>
            {
                cranes = Team(make, "Cranes"); drivers = Team(make, "Drivers");
                var src = make("Src").AddComponent<UnitySource>();
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 20f };
                asy = make("Asy").AddComponent<UnityAssemblyStation>();
                asy.tasks = new List<AssemblyTaskConfig>
                {
                    new AssemblyTaskConfig
                    {
                        id = "lift", duration = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 3f },
                        requirements = new List<ResourceRequirementSpec>
                        {
                            new ResourceRequirementSpec { team = cranes, label = "crane" },
                            new ResourceRequirementSpec { team = drivers, label = "driver" },
                        },
                    },
                };
                asy.processBy = new ProcessResourceUse
                {
                    priority = "5", preemption = PreemptMode.Resume, preemptable = false, acquire = AcquireOrder.InOrder,
                    maxWait = "30", onTimeout = SimuLean.TimeoutAction.GiveUp,
                };
                var sink = make("Sink").AddComponent<UnitySink>();
                src.nextElements = new List<SElement> { asy };
                asy.nextElements = new List<SElement> { sink };
            }, cfg =>
            {
                Assert.IsTrue(asy.TasksUseOwnTeams);
                var ec = cfg.Elements.Single(e => e.Name == "Asy");
                Assert.IsFalse(ec.Has("processBy.team"), "no station team exported");
                Assert.AreEqual("5", ec.Get<string>("processBy.priority", ""));
                Assert.AreEqual("Resume", ec.Get<string>("processBy.preemption", ""));
                Assert.IsFalse(ec.Get<bool>("processBy.preemptable", true));
                Assert.AreEqual("InOrder", ec.Get<string>("processBy.acquire", ""));
                Assert.AreEqual("30", ec.Get<string>("processBy.maxWait", ""));
                Assert.AreEqual("GiveUp", ec.Get<string>("processBy.onTimeout", ""));
                // the catalog offers them (a study can vary them), in the scene run and in the editor's view of the element
                foreach (string key in new[] { "processBy.priority", "processBy.preemption", "processBy.preemptable",
                                               "processBy.acquire", "processBy.maxWait", "processBy.onTimeout" })
                {
                    Assert.DoesNotThrow(() => SimuLean.Optimization.ConfigParameter.Of(cfg, SimuLean.Optimization.ParameterScope.Element, "Asy", key), "Asy." + key);
                    Assert.IsTrue(SimuLean.Optimization.ModelParameterCatalog.ForElement(SceneExtractor.ExtractElement(asy)).Any(p => p.ConfigKey == key),
                                  "offered in the element's catalog: " + key);
                }
                Assert.IsEmpty(SimuLean.Optimization.ModelParameterCatalog.Undecided(cfg).ToList(), "every key decided");

                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                factory.BuildModel(cfg);
                var station = (AssemblyStation)factory.Find("Asy");
                Assert.IsNull(((IResourceUser)station).Resources, "the station itself has no use");
                var lift = station.Tasks.Single(t => t.Id == "lift").Resources;
                Assert.IsNotNull(lift, "the lift has its own use");
                Assert.AreSame(factory.Teams[SceneExtractor.TeamId(cranes)], lift.Requirements[0].Team);
                Assert.AreSame(factory.Teams[SceneExtractor.TeamId(drivers)], lift.Requirements[1].Team);
                Assert.AreEqual(5.0, lift.Priority(null), 1e-12, "priority");
                Assert.AreEqual(PreemptMode.Resume, lift.Preempt, "preemption");
                Assert.IsFalse(lift.Preemptable, "preemptable");
                Assert.AreEqual(AcquireOrder.InOrder, lift.Order, "acquisition order");
                Assert.IsNotNull(lift.MaxWait, "max wait");
                Assert.AreEqual("30", lift.MaxWait.Text);
                Assert.AreEqual(SimuLean.TimeoutAction.GiveUp, lift.MaxWait.OnTimeout);
                factory.StartModel();
                clock.AdvanceClock(200);
                Assert.Greater(factory.Find("Sink").GetStatsInputCount(), 5, "the station runs with the tasks' teams");
            });
        }
    
        /// <summary>A station's Reserve Exit and the Process block's Before The Part are exported under their keys
        /// (nothing when off) and built; the scene's model runs.</summary>
        [Test]
        public void ReserveExitAndAcquireBeforeEntryExtractAndBuild()
        {
            WithModel(make =>
            {
                var ops = Team(make, "Ops");
                var src = make("Src").AddComponent<UnitySource>();
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1f };
                var q = make("Q").AddComponent<UnityQueue>();
                var a = make("A").AddComponent<UnityMultiServer>();
                a.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2f };
                a.reserveExit = true;
                a.processBy = new ProcessResourceUse { team = ops, acquireBeforeEntry = true };
                var b = make("B").AddComponent<UnityMultiServer>();
                b.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 3f };
                var comb = make("Comb").AddComponent<UnityCombiner>();
                comb.reserveExit = true;
                var pick = make("Pick").AddComponent<UnityPickingStation>();
                pick.reserveExit = true;
                var sink = make("Sink").AddComponent<UnitySink>();
                src.nextElements = new List<SElement> { q };
                q.nextElements = new List<SElement> { a };
                a.nextElements = new List<SElement> { b };
                b.nextElements = new List<SElement> { sink };
            }, cfg =>
            {
                var a = cfg.Elements.Single(e => e.Name == "A");
                Assert.IsTrue(a.Get<bool>("reserveExit", false), "reserveExit exported");
                Assert.IsTrue(a.Get<bool>("processBy.acquireBeforeEntry", false), "acquireBeforeEntry exported");
                var bCfg = cfg.Elements.Single(e => e.Name == "B");   // off: exported at the default, a study can switch it
                Assert.IsFalse(bCfg.Get<bool>("reserveExit", true), "off: reserveExit = false exported");
                // every option at its default is in the catalog, so a study can vary it from there
                foreach (string key in new[] { "reserveExit", "offerOrder" })
                    Assert.DoesNotThrow(() => SimuLean.Optimization.ConfigParameter.Of(cfg, SimuLean.Optimization.ParameterScope.Element, "B", key), "B." + key);
                foreach (string key in new[] { "processBy.acquireBeforeEntry", "processBy.acquire", "processBy.onTimeout" })
                    Assert.DoesNotThrow(() => SimuLean.Optimization.ConfigParameter.Of(cfg, SimuLean.Optimization.ParameterScope.Element, "A", key), "A." + key);
                Assert.IsFalse(cfg.Elements.Single(e => e.Name == "Comb").Has("processBy.acquireBeforeEntry"),
                               "not offered on the Combiner (the Multi-Server only)");
                Assert.IsTrue(cfg.Elements.Single(e => e.Name == "Comb").Get<bool>("reserveExit", false), "the Combiner's");
                Assert.IsTrue(cfg.Elements.Single(e => e.Name == "Pick").Get<bool>("reserveExit", false), "the Picking Station's");
                cfg.Elements.RemoveAll(e => e.Name == "Comb" || e.Name == "Pick");   // not wired: only their keys are checked
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                factory.BuildModel(cfg);
                var built = factory.Find("A");
                Assert.IsTrue(built.ReserveExit, "ReserveExit built");
                Assert.IsTrue(((IResourceUser)built).Resources.AcquireBeforeEntry, "AcquireBeforeEntry built");
                factory.StartModel();
                clock.AdvanceClock(60);
                Assert.Greater(factory.Find("Sink").GetStatsInputCount(), 10, "the model runs");
            });
        }
    }
}
