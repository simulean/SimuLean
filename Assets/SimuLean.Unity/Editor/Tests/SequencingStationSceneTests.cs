using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using SimuLean.Headless;
using SimuLean.Serialization;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Sequencing station in a scene: wagons + call-off orders + pieces pulled from a list by reference,
    /// served orders to a sink; extracted (ports and list pull exported) and run headless. Plus the view's slot lookup.
    /// </summary>
    public class SequencingStationSceneTests
    {
        [Test]
        public void SequencingSceneExtractsAndRunsHeadless()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n) where T : Component => test.Make<T>(n);
                RandomProcessConfig Const(float v) => new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = v };

                var wagons = Make<UnitySource>("Wagons");
                wagons.mode = SourceMode.InterArrival;
                wagons.interArrivalDistribution = Const(1f);
                var empties = Make<UnityQueue>("Empties");
                empties.capacity = 1000;
                var orders = Make<UnitySource>("Orders");
                orders.mode = SourceMode.InterArrival;
                orders.interArrivalDistribution = Const(10f);
                orders.createLabels = new List<string> { "reference = 'A'" };
                var pieces = Make<UnitySource>("Pieces");
                pieces.mode = SourceMode.InterArrival;
                pieces.interArrivalDistribution = Const(5f);
                pieces.createLabels = new List<string> { "sku = 'A'" };
                var stock = Make<UnityQueue>("Stock");
                stock.capacity = 1000;
                var list = Make<UnitySimList>("StockList");
                var seq = Make<UnitySequencingStation>("Seq");
                seq.carrierCapacity = 3;
                seq.pickTime = Const(2f);
                var full = Make<UnitySink>("Full");
                var served = Make<UnitySink>("Served");

                wagons.nextElements = new List<SElement> { empties };
                empties.nextElements = new List<SElement> { seq };
                pieces.nextElements = new List<SElement> { stock };
                stock.sendTo = SElement.SendMode.List;
                stock.pushToList = list;
                seq.orderSources = new List<SElement> { orders };
                seq.pieceList = new SequencingPieceList { list = list };
                seq.servedOrdersTo = served;
                seq.nextElements = new List<SElement> { full };

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                var sc = cfg.Elements.Single(e => e.Type == "SequencingStation");
                Assert.AreEqual(3, sc.Get<int>("carrierCapacity", 0));
                Assert.AreEqual("Constant~2", sc.Get<SimuLean.Serialization.SamplerSettings>("pickTime")?.ToString());
                Assert.IsFalse(sc.Has("prepareTime"), "zero time = no sampler");
                Assert.IsTrue(cfg.Connections.Any(c => c.ConnectionType == "PortInput" && c.TargetId == sc.Id
                                                       && c.Port == SequencingStation.PortOrders), "orders port");
                Assert.IsTrue(cfg.Connections.Any(c => c.ConnectionType == "PortOutput" && c.SourceId == sc.Id), "served orders");
                var pull = cfg.Connections.Single(c => c.ConnectionType == "PullFromList");
                Assert.AreEqual(cfg.Lists[0].Id, pull.SourceId, "pull from the scene list");
                Assert.AreEqual(SequencingStation.PortPieces, pull.Port);

                cfg.MaxSimulationTime = 200;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(200);

                var st = (SequencingStation)elements[sc.Id];
                // Orders every 10 from t = 10, lots of 3: carriers start at 30, 60, 90… and take 3 × 2.
                Assert.That(st.CarriersCompleted, Is.InRange(5, 7), "carriers filled");
                Assert.AreEqual(st.PiecesPlaced, 3 * st.CarriersCompleted + (st.Carrier != null ? st.Carrier.subItems?.Count ?? 0 : 0));
                Element servedEl = elements.Values.Single(e => e.Name == "Served");
                Assert.AreEqual(st.PiecesPlaced, (int)servedEl.GetStatsInputCount(), "every served order reached the sink");
            }
        }

        [Test]
        public void SlotsAreFoundByNameOrLaidOutOnAGrid()
        {
            var stGo = new GameObject("Seq");
            var carrier = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var st = stGo.AddComponent<UnitySequencingStation>();
                var body = new GameObject("Body").transform;
                body.SetParent(carrier.transform, false);
                var slot = new GameObject("Slot_1").transform;
                slot.SetParent(body, false);
                Assert.AreSame(slot, UnitySequencingStation.FindSlot(carrier.transform, 1), "Slot_i found at any depth");
                Assert.IsNull(UnitySequencingStation.FindSlot(carrier.transform, 2));

                st.gridColumns = 3;
                st.slotSpacing = new Vector2(0.5f, 0.4f);
                Vector3 s0 = st.GridSlotLocal(carrier.transform, 0, 9), s4 = st.GridSlotLocal(carrier.transform, 4, 9);
                Assert.AreEqual(-0.5f, s0.x, 1e-4f); Assert.AreEqual(-0.4f, s0.z, 1e-4f);
                Assert.AreEqual(0f, s4.x, 1e-4f); Assert.AreEqual(0f, s4.z, 1e-4f);
                Assert.AreEqual(0.5f, s0.y, 1e-4f, "on top of the unit cube");
            }
            finally
            {
                Object.DestroyImmediate(stGo);
                Object.DestroyImmediate(carrier);
            }
        }
    }
}
