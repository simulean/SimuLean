using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using SimuLean.Headless;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Line supply in a scene: source → call-off point → vehicle buffer → paced line → sink; the orders are
    /// kitted by a server and fed to the line station; a second branch builds wagons (stacker) that an unpacker empties.
    /// Built in a temporary scene, extracted (port connections), run headless.
    /// </summary>
    public class LineSupplySceneTests
    {
        [Test]
        public void CallOffBufferLineSceneExtractsAndRunsHeadless()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n) where T : Component => test.Make<T>(n);

                var src = Make<UnitySource>("Cars");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2.5f };
                var cp = Make<UnityCallOffPoint>("CallOff");
                var buffer = Make<UnityQueue>("Buffer");
                buffer.capacity = 100;
                var line = Make<UnityPacedLine>("Line");
                line.positions = 4;
                line.takt = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2f };
                var kit = Make<UnityMultiServer>("Kit");
                kit.capacity = 1;
                kit.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1f };
                var sink = Make<UnitySink>("Sink");

                cp.families = new List<CallOffFamilyDef>
                {
                    new CallOffFamilyDef { name = "Door", referenceTables = new List<string> { "R1:0.5|R2:0.5" }, destination = kit },
                };
                line.stations = new List<LineStationDef>
                {
                    new LineStationDef { name = "Door", position = 3, matchLabel = "vehicle", portCapacity = 2, feeders = new List<SElement> { kit } },
                };
                src.nextElements = new List<SElement> { cp };
                cp.nextElements = new List<SElement> { buffer };
                buffer.nextElements = new List<SElement> { line };
                line.nextElements = new List<SElement> { sink };

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                Assert.IsTrue(cfg.Connections.Any(c => c.ConnectionType == "PortOutput" && c.Port == "Door"), "orders → kit");
                Assert.IsTrue(cfg.Connections.Any(c => c.ConnectionType == "PortInput" && c.Port == "Door"), "kit → station port");
                Assert.IsEmpty(ModelParameterCatalogUndecided(cfg), "every key decided");

                cfg.MaxSimulationTime = 200;
                var clock = new SimClock(3);
                var factory = new HeadlessModelFactory(clock);
                factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(200);
                var r = RunResults.Capture(factory, "scene");
                var lineObj = r.Of(ResultScope.Element).Single(o => o.Type == nameof(PacedLine));
                Assert.Greater(lineObj[ResultMetrics.Fitted], 50, "doors fitted");
                Assert.AreEqual(0, lineObj[ResultMetrics.Mismatches], "in sequence");
                Assert.IsNotNull(r.Of(ResultScope.Element).FirstOrDefault(o => o.Type == nameof(LineStationPort)), "the station port is reported");
            }
        }

        [Test]
        public void LineSideUnpackerSceneExtractsEmptiesConnection()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n) where T : Component => test.Make<T>(n);
                var src = Make<UnitySource>("Parts");
                src.mode = SourceMode.InterArrival;
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 1f };
                var stacker = Make<UnityStacker>("Wagons");
                stacker.positionCount = 1; stacker.capacity = 4; stacker.stackType = "Wagon";
                var unp = Make<UnityLineSideUnpacker>("LineSide");
                unp.capacity = 2;
                unp.usePieceTime = true;
                unp.pieceTime = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 0.5f };
                var sink = Make<UnitySink>("Sink");
                var empties = Make<UnitySink>("Empties");
                src.nextElements = new List<SElement> { stacker };
                stacker.nextElements = new List<SElement> { unp };
                unp.nextElements = new List<SElement> { sink };
                unp.emptiesDestination = empties;

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                Assert.IsTrue(cfg.Connections.Any(c => c.ConnectionType == "PortOutput" && c.Port == Separator.PortEmpties));
                cfg.MaxSimulationTime = 100;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(100);
                var u = (LineSideUnpacker)elements.Values.Single(e => e is LineSideUnpacker);
                Assert.Greater(u.Released, 80);
                Assert.Greater(elements.Values.Single(e => e.Name == "Empties").GetStatsInputCount(), 20, "empty wagons collected");
            }
        }

        private static List<string> ModelParameterCatalogUndecided(SimulationConfig cfg)
            => new List<string>(SimuLean.Optimization.ModelParameterCatalog.Undecided(cfg));
    }
}
