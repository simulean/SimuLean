using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SimuLean.Results;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Result sets (list, read, save, export, chart) and the project
    /// database and distribution fitting through the MCP. Each test builds its model in a scene of its own, closed at the
    /// end; the files, stored sets and tables it makes are removed.
    /// </summary>
    public class AgentDataCommandsTests : SceneTestFixture
    {
        private const string Root = "DataModel";
        protected override string RootName => Root;

        [SetUp]
        public void SetUp()
        {
            ResultsOutput.StoreOverride = Path.GetFullPath($"Temp/simulean_store_test_{Guid.NewGuid():N}.db");
        }

        [TearDown]
        public void TearDown()
        {
            ResultsOutput.StoreOverride = null;
        }

        private static JObject J(object answer) => (JObject)answer;

        private static void Study(string p)
        {
            SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps(p, Root));
            SimuLeanCommands.RunHeadless(20, 2, 1, p + "Srv.capacity=1|2", "out=" + p + "Sink.input", Root, "metrics");
        }

        [Test]
        public void TheLastStudyIsListedReadAndSaved()
        {
            Study("L");
            var list = J(SimuLeanCommands.Results("list"));
            Assert.IsNotNull(list["last"], list.ToString());
            Assert.AreEqual(2, (int)list["last"]["scenarios"]);
            var read = J(SimuLeanCommands.Results("read"));
            Assert.AreEqual(2, read["scenarios"].Count());
            Assert.IsNotNull(read["scenarios"][0]["metrics"]["out"]["mean"]);

            var saved = J(SimuLeanCommands.Results("save", name: "study-test"));
            int id = (int)saved["saved"];
            var stored = J(SimuLeanCommands.Results("list"))["stored"];
            Assert.IsTrue(stored.Any(s => (int)s["id"] == id && (string)s["name"] == "study-test"), stored.ToString());
            var byId = J(SimuLeanCommands.Results("read", id.ToString()));
            Assert.AreEqual("study-test", (string)byId["name"]);
            var byName = J(SimuLeanCommands.Results("read", "study-test"));
            Assert.AreEqual(2, byName["scenarios"].Count());
            var e = Assert.Throws<ArgumentException>(() => SimuLeanCommands.Results("read", "no such set"));
            StringAssert.Contains("study-test", e.Message, "the stored sets are named");
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Results("paint"));
        }

        [Test]
        public void TheLastStudyIsExportedAndCharted()
        {
            Study("E");
            string chart = Path.GetFullPath($"Temp/simulean_chart_test_{Guid.NewGuid():N}.svg");
            var exported = J(SimuLeanCommands.Results("export", formats: "json"))["exported"].Select(x => (string)x).ToList();
            try
            {
                Assert.IsNotEmpty(exported);
                Assert.IsTrue(exported.All(p => File.Exists(p) || Directory.Exists(p)), string.Join(", ", exported));

                // the axes are written as the study writes its metrics and factors (one grammar)
                var bar = J(SimuLeanCommands.Results("chart", kind: "bar", yAxis: "out", path: chart));
                Assert.IsTrue(File.Exists(chart));
                StringAssert.Contains("<svg", File.ReadAllText(chart));
                Assert.AreEqual(2, (int)bar["categories"], "one bar per scenario");
                SimuLeanCommands.Results("chart", kind: "bar", yAxis: "ESrv.utilization", path: chart);
                SimuLeanCommands.Results("chart", kind: "bar", yAxis: "*.utilization", groupBy: "object", path: chart);
                SimuLeanCommands.Results("chart", kind: "scatter", yAxis: "out", xAxis: "ESrv.capacity", path: chart);

                Assert.Throws<ArgumentException>(() => SimuLeanCommands.Results("chart", kind: "pie", yAxis: "out", path: chart));
                Assert.Throws<ArgumentException>(() => SimuLeanCommands.Results("chart", kind: "bar", yAxis: "metric:out", path: chart), "the old prefixes are gone");
                Assert.Throws<ArgumentException>(() => SimuLeanCommands.Results("chart", kind: "bar", yAxis: "nothing", path: chart), "neither a metric, a factor nor a path");
                Assert.Throws<ArgumentException>(() => SimuLeanCommands.Results("chart", kind: "bar", yAxis: "ESrv.nothing", path: chart), "no data");
                Assert.Throws<ArgumentException>(() => SimuLeanCommands.Results("export", formats: "pdf"));
            }
            finally
            {
                if (File.Exists(chart)) File.Delete(chart);
                foreach (string p in exported)
                    if (File.Exists(p)) File.Delete(p); else if (Directory.Exists(p)) Directory.Delete(p, true);
            }
        }

        [Test]
        public void FitRanksDistributionsAndWritesOneIntoAField()
        {
            SimuLeanCommands.Apply(AgentBulkCommandsTests.LineOps("F", Root));
            // an exponential sample of mean 2 (its quantiles, so no random source in the test)
            string values = string.Join(",", Enumerable.Range(0, 300).Select(k => (-Math.Log(1 - (k + 0.5) / 300) * 2.0)
                                                                                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            var fit = J(SimuLeanCommands.Fit(values, top: 3, into: "FSrv.delayStrategy"));
            Assert.AreEqual(300, (int)fit["n"]);
            Assert.AreEqual(3, fit["fits"].Count());
            Assert.IsNotEmpty((string)fit["fits"][0]["spec"]);
            var exponential = fit["fits"].First(f => (string)f["family"] == "Exponential");
            Assert.AreEqual(2.0, (double)exponential["mean"], 0.1, "each fit gives its mean: " + exponential);
            Assert.AreEqual((double)exponential["mean"], (double)exponential["std"], 1e-9, "and its standard deviation (an exponential's is its mean)");
            var srv = GameObject.Find("FSrv").GetComponent<UnityMultiServer>();
            Assert.AreEqual((string)fit["fits"][0]["family"], srv.delayStrategy.strategyType.ToString(), "the best fit written into the field");
            Assert.AreEqual((string)fit["written"]["family"], (string)fit["fits"][0]["family"]);

            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Fit("1,2,3"), "too few values");
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Fit("1,2,x,4,5,6"), "not a number");
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Fit(values, sql: "SELECT 1"), "one data source");
            Assert.Throws<ArgumentException>(() => SimuLeanCommands.Fit(values, into: "FSrv.capacity"), "not a distribution field");
        }

        [Test]
        public void TheProjectDatabaseIsListedQueriedAndFed()
        {
            string table = "__cs2_test_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string csv = Path.GetFullPath($"Temp/{table}.csv");
            File.WriteAllText(csv, "t,q\n1.5,2\n2.5,3\n4.0,1\n");
            try
            {
                var imported = J(SimuLeanCommands.Query(import: csv, table: table));
                Assert.AreEqual(3, (int)imported["rows"]);
                var tables = J(SimuLeanCommands.Query())["tables"];
                Assert.IsTrue(tables.Any(t => (string)t["table"] == table && (long)t["rows"] == 3), tables.ToString());
                var rows = J(SimuLeanCommands.Query($"SELECT t FROM \"{table}\" ORDER BY t", maxRows: 2));
                Assert.AreEqual(2, rows["rows"].Count());
                Assert.IsTrue((bool)rows["truncated"]);
                Assert.AreEqual(3, (int)rows["count"]);

                var fit = J(SimuLeanCommands.Fit(sql: $"SELECT t FROM \"{table}\" UNION ALL SELECT t * 2 FROM \"{table}\""));
                Assert.AreEqual(6, (int)fit["n"], "the data of a query");
                Assert.Throws<ArgumentException>(() => SimuLeanCommands.Query(import: "Temp/no_such_file.csv"));
            }
            finally
            {
                SimuLeanCommands.Query($"DROP TABLE IF EXISTS \"{table}\"");
                if (File.Exists(csv)) File.Delete(csv);
            }
        }
    }
}
