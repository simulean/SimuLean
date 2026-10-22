using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SimuLean.Serialization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The scene's data tables (inline, file) are extracted as snapshots and reach the built model by name
    /// (Unity ↔ headless parity: the same config builds the same tables).
    /// </summary>
    public class DataTableSceneTests
    {
        [Test]
        public void TablesOfTheSceneReachTheModelByName()
        {
            string csv = Path.Combine(Application.temporaryCachePath, "d1_plan.csv");
            var root = new GameObject("Model") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                File.WriteAllLines(csv, new[] { "block,start", "B1,0", "B2,12.5", "B3,30" });
                var inline = new GameObject("Speeds").AddComponent<UnityDataTable>();
                inline.transform.SetParent(root.transform);
                inline.Rows = new Dictionary<string, List<string>> { ["station"] = new List<string> { "a", "b" }, ["speed"] = new List<string> { "1.5", "2" } };
                var file = new GameObject("PlanTable").AddComponent<UnityDataTable>();
                file.transform.SetParent(root.transform);
                file.tableName = "plan";
                file.source = DataSourceKind.File;
                file.fileName = csv;
                file.readFileAtRun = true;

                var config = SceneExtractor.Extract(root).Config;
                Assert.AreEqual(2, config.Tables.Count);
                Assert.IsNotNull(config.Tables.Find(t => t.Name == "plan").Columns, "the file's snapshot is taken at extraction");

                File.Delete(csv);   // the run needs no file: it has the snapshot
                var clock = new SimClock(1);
                new SimuLean.Headless.HeadlessModelFactory(clock).BuildModel(config);
                Assert.AreEqual(2.0, clock.Context.Data["Speeds"].Number(1, "speed"), 1e-12, "inline, named after the GameObject");
                Assert.AreEqual(3, clock.Context.Data["plan"].RowCount);
                Assert.AreEqual(12.5, clock.Context.Data["plan"].Number(clock.Context.Data["plan"].Find("block", "B2"), "start"), 1e-12);
            }
            finally
            {
                Object.DestroyImmediate(root);
                if (File.Exists(csv)) File.Delete(csv);
            }
        }

        [Test]
        public void AnImportedTableIsReadBySqlAtTheRunsStart()
        {
            string db = Path.Combine(Application.temporaryCachePath, "d2_" + System.Guid.NewGuid().ToString("N") + ".db");
            var go = new GameObject("Orders") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                // A file imported into the project database (here a temporary one), read by an SQL data table
                var database = SimuLean.Data.ProjectDatabaseRegistry.GetOrOpen(db);
                database.ImportTable("orders", new Dictionary<string, List<string>>
                {
                    ["id"] = new List<string> { "A", "B" }, ["qty"] = new List<string> { "2", "4.5" },
                });
                CollectionAssert.Contains(database.ListTables(), "orders");
                var t = go.AddComponent<UnityDataTable>();
                t.source = DataSourceKind.Sql;
                t.sql = "SELECT id, qty FROM orders WHERE qty > 3";
                var snap = t.Snapshot(db, out string warning);
                Assert.IsNull(warning);
                Assert.AreEqual("Orders", snap.Name);
                CollectionAssert.AreEqual(new[] { "B" }, snap.Columns["id"], "the query runs at the snapshot");
                Assert.AreEqual("4.5", snap.Columns["qty"][0]);
            }
            finally
            {
                Object.DestroyImmediate(go);
                SimuLean.Data.ProjectDatabaseRegistry.CloseAll();
                if (File.Exists(db)) File.Delete(db);
            }
        }
    }
}
