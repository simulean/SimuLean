using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The Library's packs: core is always shown, the sector libraries can be hidden; every entry knows its
    /// pack (by module, menu folder or attribute), the tools go under their own switch, a scene's packs stay shown, and
    /// the agents see the pack in <c>simulean_types</c>.
    /// </summary>
    public class LibraryPacksTests
    {
        private static SimuLeanWindow.Entry Entry(string name) =>
            SimuLeanWindow.CollectEntries().FirstOrDefault(e => e.Name == name) ?? throw new AssertionException($"No Library entry '{name}'.");

        [Test]
        public void EveryEntryHasItsPack()
        {
            Assert.AreEqual(SimuLeanPacks.Healthcare, Entry("Agenda").Pack, "an element of a sector module");
            Assert.AreEqual(SimuLeanPacks.Shipbuilding, Entry("Erection Dock").Pack);
            Assert.AreEqual(SimuLeanPacks.Core, Entry("Delay").Pack, "a general element");
            Assert.AreEqual(SimuLeanPacks.Shipbuilding, Entry("Block Breakdown").Pack, "a tool marked with its pack");
            Assert.AreEqual(SimuLeanPacks.Core, Entry("Data Table").Pack);
            Assert.AreEqual(SimuLeanPacks.Manufacturing, Entry("CNC Machine").Pack, "a preset by its menu folder");
            Assert.AreEqual(SimuLeanPacks.Healthcare, Entry("Bed Porters").Pack, "a preset marked with its pack");
            Assert.AreEqual(SimuLeanPacks.Core, Entry("Operators").Pack);
            var entries = SimuLeanWindow.CollectEntries();
            Assert.IsTrue(entries.All(e => e.Pack == SimuLeanPacks.Core || SimuLeanPacks.Optional.Contains(e.Pack)), "known packs only");
            foreach (string p in new[] { SimuLeanPacks.Manufacturing, SimuLeanPacks.Healthcare, SimuLeanPacks.Shipbuilding })
                Assert.IsTrue(entries.Any(e => e.Pack == p), $"the {p} pack has entries");
        }

        [Test]
        public void ToolsAndElementsAreSeparated()
        {
            Assert.IsTrue(Entry("Reaction").IsTool);
            Assert.IsTrue(Entry("List").IsTool);
            Assert.IsTrue(Entry("Data Table").IsTool);
            Assert.IsFalse(Entry("Delay").IsTool, "elements");
            Assert.IsFalse(Entry("Resource Team").IsTool, "resources sit with the elements");
            Assert.IsFalse(Entry("Operators").IsTool, "presets too");
        }

        [Test]
        public void ANewProjectShowsTheGeneralPacksAndAnOlderSettingsFileToo()
        {
            CollectionAssert.AreEquivalent(SimuLeanPacks.DefaultShown, new SimuLeanProjectSettings().shownPacks);
            string root = Path.GetFullPath($"Temp/simulean_packs_test_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
                File.WriteAllText(Path.Combine(root, SimuLeanProjectSettings.RelativePath), "{\"autoSetUpNewScenes\": true}");
                CollectionAssert.AreEquivalent(SimuLeanPacks.DefaultShown, SimuLeanProjectSettings.Load(root).shownPacks, "a file without the field");
                var s = SimuLeanProjectSettings.Load(root);
                s.shownPacks.Add(SimuLeanPacks.Healthcare);
                s.shownPacks.Remove(SimuLeanPacks.Manufacturing);
                s.Save(root);
                CollectionAssert.AreEquivalent(new[] { SimuLeanPacks.Warehousing, SimuLeanPacks.Healthcare }, SimuLeanProjectSettings.Load(root).shownPacks);
            }
            finally { Directory.Delete(root, true); }
        }

        [Test]
        public void APackTheSceneUsesIsFound()
        {
            var go = new GameObject("__packs_agenda");
            try
            {
                go.AddComponent<UnityAgenda>();
                Assert.IsTrue(SimuLeanWindow.ScenePacks(SimuLeanWindow.CollectEntries()).Contains(SimuLeanPacks.Healthcare));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void TheElementsAreInFewSectionsAndTheVisualsApart()
        {
            Assert.AreEqual("Arrivals & exits", Entry("Source").Section);
            Assert.AreEqual("Arrivals & exits", Entry("Sink").Section);
            Assert.AreEqual("Queues & conveyors", Entry("Queue").Section);
            Assert.AreEqual("Queues & conveyors", Entry("Conveyor").Section, "the conveyor preset sits with the queues");
            Assert.AreEqual("Stations", Entry("Delay").Section);
            Assert.AreEqual("Stations", Entry("Combiner").Section);
            Assert.AreEqual("Storage", Entry("Storage Rack").Section);
            Assert.AreEqual("Resources & paths", Entry("Operators").Section, "a team preset");
            Assert.AreEqual("Resources & paths", Entry("Resource Team").Section, "the empty team");
            Assert.AreEqual("Resources & paths", Entry("Transport Network").Section, "the paths sit with the resources");
            Assert.AreEqual("Resources & paths", Entry("Hall Crane").Section, "a crane is a team");
            Assert.AreEqual(SimuLeanPacks.Healthcare, Entry("Agenda").Section, "a pack's entries under the pack");
            Assert.IsTrue(Entry("Wall").Visual, "the building kit is visual only");
            Assert.IsTrue(Entry("Safety Fence").Visual, "marked visual");
            Assert.IsTrue(Entry("Hospital Bed").Visual, "a pack's decor folder");
            Assert.IsFalse(Entry("Conveyor").Visual, "the conveyor simulates");
            var entries = SimuLeanWindow.CollectEntries();
            Assert.IsTrue(entries.Where(e => !e.IsTool && e.Pack == SimuLeanPacks.Core && !e.Visual)
                                 .All(e => SimuLeanWindow.CoreSections.Contains(e.Section)), "every core element in a core section");
            Assert.IsFalse(new SimuLeanProjectSettings().showVisuals, "visuals hidden by default");
        }

        [Test]
        public void TheShipyardTeamsAreInTheShipbuildingPack()
        {
            foreach (string team in new[] { "Goliath Crane", "Level-Luffing Crane", "SPMT Transporters", "Welding Gantry", "Welding Robots" })
                Assert.AreEqual(SimuLeanPacks.Shipbuilding, Entry(team).Pack, team);
            Assert.AreEqual(SimuLeanPacks.Core, Entry("Hall Crane").Pack, "a hall crane is general");
        }

        [Test]
        public void TheConveyorIsALaneWithRollersThatFollowIt()
        {
            // the preset's menu method itself (Place would also create the hierarchy's organiser groups)
            Entry("Conveyor").MenuMethod.Invoke(null, new object[] { new UnityEditor.MenuCommand(null) });
            var go = UnityEditor.Selection.activeGameObject;
            try
            {
                var lane = go.GetComponent<UnityLengthLimitedQueue>();
                Assert.IsNotNull(lane, "the conveyor simulates: a length-limited lane");
                var rollers = go.GetComponentInChildren<UnityRollerConveyor>();
                Assert.IsNotNull(rollers, "with the roller look");
                Assert.AreEqual(lane.laneLengths[0], rollers.length, 1e-4f);
                Assert.AreEqual(-lane.laneLengths[0], rollers.transform.localPosition.z, 1e-4f, "from the exit backwards");
                rollers.FitTo(9f, 0f);
                Assert.AreEqual(9f, rollers.length, 1e-4f);
                Assert.AreEqual(-9f, rollers.transform.localPosition.z, 1e-4f);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void TheDelayHasAnIcon()
        {
            var icon = Entry("Delay").IconPath;
            Assert.IsNotEmpty(icon);
            Assert.IsNotNull(UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(icon), icon);
        }

        [Test]
        public void TheAgentsSeeThePack()
        {
            var types = JObject.FromObject(SimuLeanCommands.Types(""))["types"];
            Assert.AreEqual(SimuLeanPacks.Healthcare, (string)types.First(t => (string)t["name"] == "Agenda")["pack"]);
            Assert.AreEqual(SimuLeanPacks.Core, (string)types.First(t => (string)t["name"] == "Delay")["pack"]);
        }
    }
}
