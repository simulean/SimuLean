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
    /// Max-wait timers in a scene: the Process / Setup / Repair blocks' Max Wait and On Timeout (and a process
    /// Reroute's Timeout Target), a Queue's Max wait / On timeout / Timeout target and an element's list Max Wait are
    /// extracted under their keys (nothing when empty) and built by the engine; the scene's model runs and times out.
    /// </summary>
    public class MaxWaitSceneTests
    {
        private static void WithModel(Action<Func<string, GameObject>> build, Action<SimulationConfig> check)
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                build(n => { var go = new GameObject(n); go.transform.SetParent(root.transform); return go; });
                check(SceneExtractor.Extract(root).Config);
            }
        }

        private static RandomProcessConfig Constant(float v) => new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = v };

        private static HeadlessModelFactory Run(SimulationConfig cfg, double until)
        {
            var clock = new SimClock(1);
            var factory = new HeadlessModelFactory(clock);
            factory.BuildModel(cfg);
            factory.StartModel();
            clock.AdvanceClock(until);
            return factory;
        }

        /// <summary>A Multi-Server whose job gives up waiting for the busy operator: its item goes to the timeout target.</summary>
        [Test]
        public void ProcessMaxWaitAndRerouteExtractAndBuild()
        {
            WithModel(make =>
            {
                var team = make("Ops").AddComponent<UnityResourceTeam>();
                team.travel = TeamTravel.Abstract;
                team.AddMembers(1);
                var srcH = make("SrcH").AddComponent<UnitySource>();
                srcH.mode = SourceMode.InterArrival; srcH.interArrivalDistribution = Constant(1f);
                var h = make("H").AddComponent<UnityMultiServer>(); h.delayStrategy = Constant(100f);
                var src = make("Src").AddComponent<UnitySource>();
                src.mode = SourceMode.InterArrival; src.interArrivalDistribution = Constant(10f);
                var s = make("S").AddComponent<UnityMultiServer>(); s.delayStrategy = Constant(2f);
                var sink = make("Sink").AddComponent<UnitySink>();
                var lost = make("Lost").AddComponent<UnitySink>();
                srcH.nextElements = new List<SElement> { h }; h.nextElements = new List<SElement> { sink };
                src.nextElements = new List<SElement> { s }; s.nextElements = new List<SElement> { sink };
                h.processBy = new ProcessResourceUse { team = team };
                s.processBy = new ProcessResourceUse { team = team, maxWait = "3", onTimeout = SimuLean.TimeoutAction.Reroute, timeoutTarget = lost };
            }, cfg =>
            {
                var ec = cfg.Elements.Single(e => e.Name == "S");
                Assert.AreEqual("3", ec.Get<string>("processBy.maxWait", ""));
                Assert.AreEqual("Reroute", ec.Get<string>("processBy.onTimeout", ""));
                Assert.AreEqual(cfg.Elements.Single(e => e.Name == "Lost").Id, ec.Get<string>("processBy.timeoutTarget", ""),
                                "the target by its model id");
                var h = cfg.Elements.Single(e => e.Name == "H");   // no limit: exported at its default, a study can vary it
                Assert.AreEqual("0", h.Get<string>("processBy.maxWait", ""), "no limit: 0");
                Assert.AreEqual("KeepWaiting", h.Get<string>("processBy.onTimeout", ""), "the default action");
                var m = Run(cfg, 58);
                Assert.AreEqual(5, m.Find("S").Timeouts, "items at 10…50 give up after 3");
                Assert.AreEqual(5, m.Find("Lost").GetStatsInputCount(), "and leave by the target");
            });
        }

        /// <summary>A Queue's reneging, a list puller's max wait and a setup crew's limit.</summary>
        [Test]
        public void QueuePullAndSetupMaxWaitExtractAndBuild()
        {
            WithModel(make =>
            {
                var setters = make("Setters").AddComponent<UnityResourceTeam>();
                setters.travel = TeamTravel.Abstract;
                setters.AddMembers(1);
                var src = make("Src").AddComponent<UnitySource>();
                src.mode = SourceMode.InterArrival; src.interArrivalDistribution = Constant(1f);
                var q = make("Q").AddComponent<UnityQueue>();
                var s = make("S").AddComponent<UnityMultiServer>(); s.delayStrategy = Constant(3f);
                var sink = make("Sink").AddComponent<UnitySink>();
                var lost = make("Lost").AddComponent<UnitySink>();
                q.maxWait = "5.5"; q.onTimeout = SimuLean.TimeoutAction.Reroute; q.timeoutTarget = lost;
                s.setup = new StationSetupConfig { when = SetupWhen.EachItem, time = Constant(0.5f) };
                s.setup.setupBy = new SetupResourceUse { team = setters, maxWait = "2", onTimeout = SimuLean.TimeoutAction.GiveUp };
                src.nextElements = new List<SElement> { q }; q.nextElements = new List<SElement> { s }; s.nextElements = new List<SElement> { sink };

                var list = make("Parts").AddComponent<UnitySimList>(); list.listName = "Parts";
                var src2 = make("Src2").AddComponent<UnitySource>();
                src2.mode = SourceMode.InterArrival; src2.interArrivalDistribution = Constant(10f);
                src2.sendTo = SElement.SendMode.List; src2.pushToList = list;
                var p = make("P").AddComponent<UnityMultiServer>(); p.delayStrategy = Constant(2f);
                p.receiveFrom = SElement.ReceiveMode.List; p.pullFromList = list; p.pullMaxWait = "4";
                p.nextElements = new List<SElement> { sink };
            }, cfg =>
            {
                var q = cfg.Elements.Single(e => e.Name == "Q");
                Assert.AreEqual("5.5", q.Get<string>("maxWait", ""));
                Assert.AreEqual("Reroute", q.Get<string>("onTimeout", ""));
                Assert.AreEqual(cfg.Elements.Single(e => e.Name == "Lost").Id, q.Get<string>("timeoutTarget", ""));
                var s = cfg.Elements.Single(e => e.Name == "S");
                Assert.AreEqual("2", s.Get<string>("setup.setupBy.maxWait", ""));
                Assert.AreEqual("GiveUp", s.Get<string>("setup.setupBy.onTimeout", ""));
                Assert.AreEqual("4", cfg.Elements.Single(e => e.Name == "P").Get<string>("pullMaxWait", ""));
                Assert.IsFalse(cfg.Elements.Single(e => e.Name == "Sink").Has("pullMaxWait"));
                var m = Run(cfg, 40);
                Assert.Greater(m.Find("Q").Timeouts, 0, "items renege");
                Assert.AreEqual(m.Find("Q").Timeouts, (int)m.Find("Lost").GetStatsInputCount(), "to the target");
                Assert.IsNotNull(((MultiServer)m.Find("S")).Setup.Resources.MaxWait, "the setup crew's limit built");
                Assert.Greater(m.Find("P").Timeouts, 0, "the puller times out between list entries");
                Assert.IsTrue(m.Find("P").LimitedWaits.Contains(WaitKind.ListPull));
            });
        }
    }
}
