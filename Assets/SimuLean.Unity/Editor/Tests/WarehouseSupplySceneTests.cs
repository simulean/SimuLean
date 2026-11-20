using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using SimuLean.Collections;
using SimuLean.Headless;
using SimuLean.Results;
using SimuLean.Serialization;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Warehouse supply in a scene: a base-stock replenisher stores unit loads in a storage that publishes its
    /// stock to a list; a pick face pulls loads of its SKU from the list (kanban of one) and hands single pieces to a
    /// station; emptied loads go to a sink. Built in a temporary scene, extracted, run headless — the scene twin of the
    /// <c>pick_face_kanban</c> reference model (same hand-computed results).
    /// </summary>
    public class WarehouseSupplySceneTests
    {
        [Test]
        public void PickFaceFromPublishedStockExtractsAndRunsHeadless()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n) where T : Component => test.Make<T>(n);

                var stock = Make<UnitySimList>("Stock");
                var repl = Make<UnityBaseStockReplenisher>("Trucks");
                repl.skus = new List<ReplenishmentSkuRow> { new ReplenishmentSkuRow { sku = "P", piecesPerLoad = 3, initialLoads = 3 } };
                repl.truckloadSize = 3;
                repl.useLeadTime = true;
                repl.leadTime = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 20f };
                var wh = Make<UnityStorageSystem>("Warehouse");
                wh.lanes = new List<StorageLaneDefinition> { new StorageLaneDefinition { laneId = "L", kind = StorageLaneKind.Selective, capacity = 10 } };
                wh.sendTo = SElement.SendMode.List;
                wh.pushToList = stock;
                var face = Make<UnityPickFace>("FaceP");
                face.sku = "P";
                face.piecesPerLoad = 3;
                face.receiveFrom = SElement.ReceiveMode.List;
                face.pullFromList = stock;
                face.pullQuery = new ListQuerySpec { conditions = new List<ListCondition> { new ListCondition("sku", ListOp.Equal, "P") } };
                var station = Make<UnityMultiServer>("Station");
                station.capacity = 1;
                station.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 2f };
                var sink = Make<UnitySink>("Sink");
                var empties = Make<UnitySink>("Empties");
                repl.nextElements = new List<SElement> { wh };
                face.nextElements = new List<SElement> { station };
                face.emptiesDestination = empties;
                station.nextElements = new List<SElement> { sink };

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                Assert.IsTrue(cfg.Elements.Any(e => e.Type == "PickFace"), "pick face extracted");
                Assert.IsTrue(cfg.Elements.Any(e => e.Type == "BaseStockReplenisher"), "replenisher extracted");
                Assert.IsTrue(cfg.Connections.Any(c => c.ConnectionType == "PushToList"), "the storage publishes its stock");
                Assert.IsTrue(cfg.Connections.Any(c => c.ConnectionType == "PullFromList" && (c.Query ?? "").StartsWith("sku == P")), "the face pulls its SKU");
                Assert.IsTrue(cfg.Connections.Any(c => c.ConnectionType == "PortOutput" && c.Port == "empties"), "empties port");
                Assert.IsEmpty(SimuLean.Optimization.ModelParameterCatalog.Undecided(cfg).ToList(), "every key decided");

                cfg.MaxSimulationTime = 50;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(50);
                Assert.AreEqual(18, elements.Values.Single(e => e.Name == "Sink").GetStatsInputCount(), "pieces done (2 … 18, 32 … 48)");
                Assert.AreEqual(6, elements.Values.Single(e => e.Name == "Empties").GetStatsInputCount(), "empty loads");
                var r = RunResults.Capture(factory, "scene");
                var trucks = r.Of(ResultScope.Element).Single(o => o.Type == nameof(BaseStockReplenisher));
                Assert.AreEqual(2, trucks[ResultMetrics.TrucksOrdered]);
                Assert.AreEqual(1, trucks[ResultMetrics.TrucksReceived]);
                var faceObj = r.Of(ResultScope.Element).Single(o => o.Type == nameof(PickFace));
                Assert.AreEqual(18, faceObj[ResultMetrics.Pieces]);
                Assert.AreEqual(2, faceObj[ResultMetrics.StockOuts]);
            }
        }

        [Test]
        public void ReplenisherReadsItsSkuTableFromAFileOutsideTheProject()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "simulean_sku_table_test.csv");
            System.IO.File.WriteAllText(path, "sku,piecesPerLoad,initialLoads\nA,12,2\nB,6,0\n");
            var go = new GameObject("Trucks");
            try
            {
                var repl = go.AddComponent<UnityBaseStockReplenisher>();
                repl.skuTableFile = path;
                var rows = repl.ResolveSkus();
                Assert.AreEqual(2, rows.Count);
                Assert.AreEqual("A", rows[0].Sku);
                Assert.AreEqual(12, rows[0].PiecesPerLoad);
                Assert.AreEqual(2, rows[0].InitialLoads);
                Assert.AreEqual(0, rows[1].InitialLoads);
            }
            finally
            {
                Object.DestroyImmediate(go);
                System.IO.File.Delete(path);
            }
        }
    }
}
