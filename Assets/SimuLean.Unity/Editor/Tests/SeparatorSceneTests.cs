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
    /// The Core Separator as a scene: the inverse of a Combiner in batch mode — the parts a Combiner puts on
    /// each main item come back out of the separator, the main items through its <c>empties</c> port; Split extracts no
    /// port. The view's piece spots (<see cref="UnitySeparator.PieceSpots"/>) stand side by side, centred, then in rows;
    /// the library model (UnpackingStation) gives the work point and the pieces' place.
    /// </summary>
    public class SeparatorSceneTests
    {
        [Test]
        public void ASceneSeparatorUndoesACombinerInBatchMode()
        {
            using (var test = new TestScene("Model"))
            {
                var root = test.Root;
                T Make<T>(string n) where T : Component => test.Make<T>(n);
                RandomProcessConfig Constant(float v) => new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = v };
                var mains = Make<UnitySource>("Mains");
                mains.mode = SourceMode.InterArrival;
                mains.interArrivalDistribution = Constant(5);
                var parts = Make<UnitySource>("Parts");
                parts.mode = SourceMode.InterArrival;
                parts.interArrivalDistribution = Constant(2);
                var comb = Make<UnityCombiner>("Combiner");
                comb.requirements = new[] { 2 };
                comb.batchMode = true;
                comb.delayStrategy = Constant(1);
                comb.inputOrder = new List<SElement> { mains, parts };
                var sep = Make<UnitySeparator>("Separator");
                sep.useProcessTime = true;
                sep.processTime = Constant(1);
                var partsOut = Make<UnitySink>("PartsOut");
                var mainsOut = Make<UnitySink>("MainsOut");
                mains.nextElements = new List<SElement> { comb };
                parts.nextElements = new List<SElement> { comb };
                comb.nextElements = new List<SElement> { sep };
                sep.nextElements = new List<SElement> { partsOut };
                sep.emptiesDestination = mainsOut;

                SimulationConfig cfg = SceneExtractor.Extract(root).Config;
                Assert.IsTrue(cfg.Connections.Any(c => c.ConnectionType == "PortOutput" && c.Port == Separator.PortEmpties));
                Assert.IsEmpty(SimuLean.Optimization.ModelParameterCatalog.Undecided(cfg).ToList(), "every key decided");
                cfg.MaxSimulationTime = 200;
                var clock = new SimClock(1);
                var factory = new HeadlessModelFactory(clock);
                var elements = factory.BuildModel(cfg);
                factory.StartModel();
                clock.AdvanceClock(200);
                double outMains = elements.Values.Single(e => e.Name == "MainsOut").GetStatsInputCount();
                double outParts = elements.Values.Single(e => e.Name == "PartsOut").GetStatsInputCount();
                Assert.Greater(outMains, 30, "the main items come back through the empties port");
                Assert.AreEqual(2 * outMains, outParts, "two parts per main item");

                sep.mode = SeparatorMode.Split;
                cfg = SceneExtractor.Extract(root).Config;
                Assert.IsFalse(cfg.Connections.Any(c => c.ConnectionType == "PortOutput"), "Split: no empties port");
            }
        }

        [Test]
        public void PieceSpotsStandSideBySideCentredThenInRows()
        {
            var sizes = new List<Vector3> { Vector3.one, Vector3.one, Vector3.one };
            var row = UnitySeparator.PieceSpots(new Vector2(4f, 1f), sizes);
            Assert.AreEqual(0f, row[1].x, 1e-4f, "centred");
            Assert.AreEqual(-row[0].x, row[2].x, 1e-4f);
            Assert.Greater(row[2].x - row[1].x, 1f - 1e-4f, "side by side, not overlapping");
            Assert.IsTrue(row.All(p => Mathf.Abs(p.z - row[0].z) < 1e-4f && Mathf.Abs(p.y) < 1e-4f), "one row on the area");
            var rows = UnitySeparator.PieceSpots(new Vector2(2.5f, 3f), sizes);
            Assert.Greater(Mathf.Abs(rows[2].z - rows[0].z), 1f - 1e-4f, "the third one in a second row: the area is 2.5 wide");
        }

        [Test]
        public void TheUnpackingStationModelGivesTheWorkPointAndThePiecesPlace()
        {
            var go = new GameObject("Sep");
            try
            {
                var sep = go.AddComponent<UnitySeparator>();
                LibraryPresets.AddModel(go, ("UnpackingStation", "UnpackingStation/UnpackingStation.fbx"));
                Assert.IsNotNull(sep.itemPosition, "the work point is bound to the model's WorkSocket");
                Assert.AreEqual("WorkSocket", sep.itemPosition.name);
                Assert.Less(sep.SlotWorld(0).x, 0f, "the container stands on the roller section, on the in side (-X)");
                Assert.Greater(sep.SlotWorld(0).y, 0.5f, "at working height");
                var pieces = sep.PiecesAt();
                Assert.IsNotNull(pieces, "the model's PiecesSocket");
                Assert.Greater(pieces.position.x, 0f, "the pieces wait on the worktop, on the out side (+X)");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
