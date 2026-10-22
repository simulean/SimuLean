using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SimuLean.Data;
using SimuLean.Naval;
using SimuLean.Serialization;
using SimuLean.Unity.Naval;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Naval library N1 in the scene: a <see cref="UnityBlockBreakdown"/> points its sources at the breakdown's parts in the
    /// project database; a run reads the current table (not the preview copy) and the extractor hands the spec to the
    /// engine, so the scene, headless runs and studies read the same data. The engine side is covered by the standalone
    /// suite <c>navaltests</c>.
    /// </summary>
    public class BlockBreakdownTests
    {
        private string dbPath;
        private readonly List<Object> created = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            dbPath = Path.Combine(Path.GetTempPath(), "simulean_naval_editmode_" + System.Guid.NewGuid().ToString("N") + ".db");
            var table = new Dictionary<string, List<string>>
            {
                ["block"] = new List<string> { "1", "1", "2" },
                ["sub"] = new List<string> { "1", "2", "1" },
                ["weight"] = new List<string> { "40", "38", "55" },
                ["erect_after"] = new List<string> { "", "", "1" },
            };
            BlockBreakdown.FromTable(table).Save(ProjectDatabaseRegistry.GetOrOpen(dbPath));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
            ProjectDatabaseRegistry.CloseAll();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }

        private T Make<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            created.Add(go);
            return go.AddComponent<T>();
        }

        [Test]
        public void TheBreakdownFeedsItsSourcesFromTheDatabase()
        {
            var bd = Make<UnityBlockBreakdown>("Breakdown");
            bd.databasePath = dbPath;
            bd.blockInterval = 10;
            bd.subBlockSource = Make<UnitySource>("Kits");
            bd.blockOrderSource = Make<UnitySource>("Orders");
            bd.erectionSource = Make<UnitySource>("Plan");
            Assert.AreEqual("2 blocks · 3 sub-blocks · 133 t steel · 0 turned · erection chain 2 block(s)", UnityBlockBreakdown.Summary(bd.Load()));
            Assert.AreEqual(3, bd.ApplyToSources());

            Assert.AreEqual("naval:blocks;interval=10:block_breakdown", bd.blockOrderSource.tableSource);
            Assert.IsTrue(bd.blockOrderSource.ReadsTableSource);
            var orders = bd.blockOrderSource.TableForRun(out string warning);
            Assert.IsNull(warning);
            CollectionAssert.AreEqual(new[] { "2", "1" }, orders["nsub"]);
            CollectionAssert.AreEqual(new[] { "0", "10" }, orders["Time"]);
            Assert.AreEqual(3, bd.subBlockSource.TableForRun(out _)["Name"].Count);

            // a run reads the table as it is now: one more block in the database
            var db = ProjectDatabaseRegistry.GetOrOpen(dbPath);
            db.Execute("INSERT INTO block_breakdown (block, sub, weight, erect_after) VALUES ('3', '1', '20', '2')");
            Assert.AreEqual(3, bd.blockOrderSource.TableForRun(out _)["Name"].Count);

            // the extractor hands the spec to the engine, which reads it when the model is built
            var config = SceneExtractor.ExtractElement(bd.blockOrderSource);
            Assert.AreEqual("naval:blocks;interval=10:block_breakdown", config.GetParameter<string>("tableSource"));
            Assert.AreEqual(dbPath, config.GetParameter<string>("tableDatabase"));
            Assert.IsNull(config.GetParameter<Dictionary<string, List<string>>>("arrivalsTable"), "no copy in the config: the handler reads the table");
        }

        [Test]
        public void AScenesSlipwayErectsTheBreakdownsBlocksAndLaunchesTheShip()
        {
            // the breakdown's block orders → yard (any accepted) → slipway (sequence, the hull = 3 blocks) → launch (kept)
            var root = new GameObject("NavalModel");
            created.Add(root);
            T Make<T>(string n) where T : Component
            {
                var go = new GameObject(n);
                go.transform.SetParent(root.transform);
                return go.AddComponent<T>();
            }
            var db = ProjectDatabaseRegistry.GetOrOpen(dbPath);
            db.Execute("INSERT INTO block_breakdown (block, sub, weight, erect_after) VALUES ('3', '1', '20', '2')");
            var bd = Make<UnityBlockBreakdown>("Breakdown");
            bd.databasePath = dbPath;
            bd.blockInterval = 2;
            var blocks = Make<UnitySource>("Blocks");
            bd.blockOrderSource = blocks;
            bd.ApplyToSources();
            var yard = Make<UnityQueue>("Yard");
            yard.capacity = 10;
            yard.releaseMode = SimuLean.ReleaseMode.AnyAccepted;
            var slipway = Make<UnityErectionDock>("Slipway");
            slipway.erectionRule = ErectionDock.ErectionRule.Sequence;
            slipway.expectedBlocks = 3;
            slipway.delayStrategy = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 5f };
            var launch = Make<UnitySink>("Launch");
            launch.keepItems = true;
            blocks.nextElements = new List<SElement> { yard };
            yard.nextElements = new List<SElement> { slipway };
            slipway.nextElements = new List<SElement> { launch };

            var cfg = SceneExtractor.Extract(root).Config;
            Assert.AreEqual("Sequence", cfg.Elements.Find(e => e.Type == "UnityErectionDock").GetParameter<string>("erectionRule"));
            Assert.IsTrue(cfg.Elements.Find(e => e.Type == "UnitySink").GetParameter<bool>("keepItems"));
            cfg.MaxSimulationTime = 100;
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(cfg);
            model.StartModel();
            clock.AdvanceClock(100);
            var dock = (ErectionDock)model.Find("Slipway");
            var sink = (Sink)model.Find("Launch");
            Assert.AreEqual(1, dock.ShipsLaunched);
            Assert.AreEqual(1, sink.KeptCount);
            Assert.AreEqual(3, sink.Kept[0].GetSubItems().Count);
            Assert.AreEqual(15, dock.LastErectionTime, 1e-9, "blocks at 0, 2, 4, erected 0-5, 5-10, 10-15");
        }

        [Test]
        public void TheShipbuildingPresetsAreVisibleWhenPlaced()
        {
            var stations = new (string name, System.Type type)[]
            {
                ("Profile Rack", typeof(UnityQueue)), ("Plate Cutting", typeof(UnityMultiServer)),
                ("Sub-Assembly Cell", typeof(UnityMultiServer)), ("Sub-Block Welding", typeof(UnityCombiner)),
                ("Turning Station", typeof(UnityMultiServer)), ("Sub-Block Yard", typeof(UnityQueue)),
                ("Block Assembly Bay", typeof(UnityCombiner)), ("Outfitting Station", typeof(UnityMultiServer)),
                ("Blasting & Painting Hall", typeof(UnityMultiServer)), ("Block Yard", typeof(UnityQueue)),
                ("Slipway", typeof(UnityErectionDock)), ("Building Dock", typeof(UnityErectionDock)), ("Launch", typeof(UnitySink)),
            };
            foreach (var (name, type) in stations)
            {
                var entry = SimuLeanWindow.CollectEntries().First(e => e.Name == name);
                var go = SimuLeanWindow.Place(entry, Vector3.zero);
                created.Add(go);
                Assert.IsNotNull(go.GetComponent(type), name);
                Assert.IsNotNull(go.GetComponent<StationModel>()?.Instance, $"{name}: no model in the scene");
                Assert.IsNotNull(AssetDatabase.LoadMainAssetAtPath(entry.IconPath), $"{name}: icon");
            }
            var stock = SimuLeanWindow.Place(SimuLeanWindow.CollectEntries().First(e => e.Name == "Steel Stockyard"), Vector3.zero);
            created.Add(stock);
            Assert.AreEqual(StorageRackStyle.FloorStorage, stock.GetComponent<UnityStorageRack>().style);

            var line = SimuLeanWindow.Place(SimuLeanWindow.CollectEntries().First(e => e.Name == "Panel Line"), Vector3.zero);
            created.Add(line);
            var panel = line.GetComponentsInChildren<UnityMultiServer>();
            Assert.AreEqual(4, panel.Length, "the panel line's 4 stations");
            for (int i = 0; i < 3; i++) CollectionAssert.AreEqual(new[] { panel[i + 1] }, panel[i].nextElements, "connected in line");

            var dockEntry = SimuLeanWindow.CollectEntries().First(e => e.Type == typeof(UnityErectionDock));
            Assert.AreEqual(SimuLeanModules.Shipbuilding, dockEntry.Subgroup);
            Assert.IsNotNull(AssetDatabase.LoadMainAssetAtPath(dockEntry.IconPath), "the element's icon");
        }

        [Test]
        public void TheBlockProcessCompositeBuildsTheShipFromTheSampleHull()
        {
            var root = SimuLean.Unity.Editor.Naval.NavalPresets.BlockProcess(null, dbPath);
            created.Add(root);
            Assert.IsTrue(ProjectDatabaseRegistry.GetOrOpen(dbPath).TableExists(SimuLean.Unity.Editor.Naval.NavalPresets.SampleHullTable));
            Assert.IsFalse(SimuLean.Unity.Editor.Naval.NavalPresets.EnsureSampleHull(dbPath), "an existing table is kept");
            Assert.AreEqual(4, root.GetComponentInChildren<UnityErectionDock>().expectedBlocks);

            var cfg = SceneExtractor.Extract(root).Config;
            cfg.MaxSimulationTime = 2000;
            var clock = new SimClock(7);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(cfg);
            model.StartModel();
            clock.AdvanceClock(2000);
            var dock = (ErectionDock)model.Find("Slipway");
            var launch = (Sink)model.Find("Launch");
            Assert.AreEqual(1, dock.ShipsLaunched, "the ship is launched");
            Assert.AreEqual(4, launch.Kept[0].GetSubItems().Count, "with its 4 blocks");
            Assert.AreEqual(0, model.Find("Sub-Block Yard").GetQueueLength());
        }

        [Test]
        public void AnOpenedSceneThatReadsTheSampleHullGetsItWhenItIsMissing()
        {
            // a fresh clone has no project database: opening a shipbuilding example writes the sample hull, once
            var sample = Make<UnityBlockBreakdown>("Sample Breakdown");
            sample.table = SimuLean.Unity.Editor.Naval.NavalPresets.SampleHullTable;
            sample.databasePath = dbPath;
            var own = Make<UnityBlockBreakdown>("Own Breakdown");
            own.table = "my_hull";
            own.databasePath = dbPath;
            var scene = sample.gameObject.scene;

            var db = ProjectDatabaseRegistry.GetOrOpen(dbPath);
            Assert.IsFalse(db.TableExists(SimuLean.Unity.Editor.Naval.NavalPresets.SampleHullTable));
            Assert.AreEqual(1, SimuLean.Unity.Editor.Naval.SampleHullOnOpen.EnsureFor(scene), "the missing sample hull is written");
            Assert.IsTrue(db.TableExists(SimuLean.Unity.Editor.Naval.NavalPresets.SampleHullTable));
            Assert.Greater(sample.Load().BlockCount, 0, "the breakdown reads it");
            Assert.IsFalse(db.TableExists("my_hull"), "a table of the user's is never invented");
            Assert.AreEqual(0, SimuLean.Unity.Editor.Naval.SampleHullOnOpen.EnsureFor(scene), "an existing table is kept");
        }

        [Test]
        public void TheBreakdownToolIsInTheLibraryDataGroup()
        {
            var tool = SimuLeanWindow.CollectEntries().FirstOrDefault(e => e.Type == typeof(UnityBlockBreakdown));
            Assert.IsNotNull(tool);
            Assert.AreEqual("Data", tool.Group);
        }
    }
}
