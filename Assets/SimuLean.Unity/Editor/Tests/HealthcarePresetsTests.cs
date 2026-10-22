using System.Linq;
using NUnit.Framework;
using SimuLean.Serialization;
using SimuLean.Headless;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The healthcare library's presets (<see cref="Healthcare.HealthcarePresets"/>): placed through the SimuLean window,
    /// each is its element with the healthcare look; the composites are connected models that run headless.
    /// </summary>
    public class HealthcarePresetsTests
    {
        private Scene scene;
        private bool temporary;

        [SetUp]
        public void SetUp()
        {
            var active = SceneManager.GetActiveScene();
            temporary = !string.IsNullOrEmpty(active.path);
            scene = temporary ? EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive) : active;
            if (temporary) SceneManager.SetActiveScene(scene);
        }

        [TearDown]
        public void TearDown()
        {
            if (temporary) EditorSceneManager.CloseScene(scene, true);
            else foreach (var go in scene.GetRootGameObjects()) Object.DestroyImmediate(go);
        }

        private static GameObject Place(string name)
        {
            var entry = SimuLeanWindow.CollectEntries().FirstOrDefault(e => e.Name == name);
            Assert.IsNotNull(entry, $"'{name}' is not in the library");
            Assert.AreEqual("Presets", entry.Group, name);
            StringAssert.StartsWith("Healthcare", entry.Subgroup, name);
            var go = SimuLeanWindow.Place(entry, Vector3.zero);
            Assert.IsNotNull(go, $"'{name}' created nothing");
            return go;
        }

        [Test]
        public void EveryPresetPlacesItsElementWithItsLook()
        {
            var agenda = Place("Clinic Agenda");
            Assert.IsNotNull(agenda.GetComponent<UnityAgenda>());
            Assert.AreEqual(StationModel.Look.Virtual, agenda.GetComponent<StationModel>().look);
            Assert.IsTrue(agenda.GetComponent<UnityElementLabel>().hideItems, "patients at home drawn as a count");

            var list = Place("Waiting List (at home)");
            Assert.IsNotNull(list.GetComponent<UnityQueue>());
            Assert.IsTrue(list.GetComponent<UnityElementLabel>().hideItems);

            var line = Place("Waiting Line").GetComponent<UnityQueue>();
            Assert.AreEqual(QueueLayoutMode.Line, line.layoutMode);
            Assert.IsFalse(line.showFootprint);

            var home = Place("Home Test");
            Assert.IsNotNull(home.GetComponent<UnityDelay>());
            Assert.AreEqual(StationModel.Look.Virtual, home.GetComponent<StationModel>().look);

            Assert.AreEqual(StationModel.Look.Virtual, Place("Report (not face to face)").GetComponent<StationModel>().look);
            Assert.IsNotNull(Place("Treatment at Home").GetComponent<UnityDelay>());
            CollectionAssert.AreEqual(new[] { "time_in_system = age" }, Place("Discharge").GetComponent<UnitySink>().tallies);
            Assert.AreEqual(3, Place("Equipment Pool").GetComponent<UnityResourceTeam>().members.Count);
        }

        [Test]
        public void PatientsComeInAndLeaveThroughHospitalDoors()
        {
            foreach (var name in new[] { "Referrals", "Discharge" })
            {
                var go = Place(name);
                var sm = go.GetComponent<StationModel>();
                Assert.AreEqual("HospitalDoor", sm.Chosen.name, name);
                Assert.IsNotNull(go.GetComponent<SlidingDoorView>(), $"{name}: the doors open as patients pass");
                Assert.IsNotNull(go.GetComponentsInChildren<Transform>().FirstOrDefault(t => t.name == "Door_L"), name);
            }
            var src = Place("Referrals").GetComponent<UnitySource>();
            Assert.AreEqual("ItemSocket", src.itemPosition != null ? src.itemPosition.name : null, "patients appear inside the door");
        }

        [Test]
        public void TheWaitingRoomHasASeatPerPlace()
        {
            var q = Place("Waiting Room").GetComponent<UnityQueue>();
            Assert.AreEqual(q.capacity, q.Seats().Count);
            q.capacity = 9;
            UnityQueueEditor.FitChairs(q);
            Assert.AreEqual(9, q.Seats().Count, "the chairs follow the capacity");
        }

        [Test]
        public void DecorationIsPlacedWithoutElements()
        {
            var zone = Place("Floor Zone");
            var fz = zone.GetComponent<UnityFloorZone>();
            Assert.IsNotNull(fz.Visual, "the zone is drawn");
            Assert.AreNotEqual((HideFlags)0, fz.Visual.gameObject.hideFlags & HideFlags.DontSave, "and never saved");
            Assert.IsNotNull(fz.Visual.Find("Caption"), "with its name");
            foreach (var name in new[] { "Hospital Bed", "Stretcher", "Wheelchair", "Side Table" })
            {
                var go = Place(name);
                Assert.IsNull(go.GetComponentInChildren<SElement>(), $"{name}: decoration, not an element");
                Assert.IsNotNull(go.GetComponentInChildren<Renderer>(), $"{name}: visible");
            }
        }

        [Test]
        public void TheHospitalWingKitBuildsWallsAndRooms()
        {
            foreach (var name in new[] { "Partition Wall", "Glass Partition", "Wall with Door" })
            {
                var wall = Place(name);
                Assert.IsNotNull(wall.GetComponent<UnityModularStrip>().segment, name);
                Assert.IsNotNull(wall.GetComponentInChildren<Renderer>(), $"{name}: built");
            }
            var room = Place("Room Walls");
            Assert.AreEqual(4, room.GetComponentsInChildren<UnityModularStrip>().Length, "four walls");
            var front = room.GetComponentsInChildren<UnityModularStrip>().First(w => w.name == "Front");
            Assert.AreEqual("HWall_Door", front.center != null ? front.center.name : null, "a door in the front wall");
        }

        [Test]
        public void ThePlaceModelCommandPlacesALibraryModel()
        {
            var bed = SimuLeanCommands.PlaceModel("HospitalBed", "Bed 1", 2f, 0f, 3f, 90f);
            var go = GameObject.Find("Decor/Bed 1");
            Assert.IsNotNull(go, $"placed under the Decor group ({bed})");
            Assert.AreEqual(new Vector3(2f, 0f, 3f), go.transform.position);
            Assert.IsNotNull(SimuLeanCommands.ModelAsset("WaitingChairs/Seat_Table"), "a file of a kit");
            Assert.IsNull(SimuLeanCommands.ModelAsset("WaitingChairs"), "a kit of several models needs the file");
            Assert.Throws<System.ArgumentException>(() => SimuLeanCommands.PlaceModel("NoSuchModel"));
        }

        [Test]
        public void RoomsHaveSignsAndCompositesTheirPathway()
        {
            var room = Place("Consultation Room");
            Assert.AreEqual("{name}", room.GetComponent<UnityElementLabel>().text, "the room's name over it");
            var clinic = Place("Outpatient Clinic");
            var arrows = clinic.GetComponentInChildren<UnityPathwayArrows>();
            Assert.IsNotNull(arrows);
            Assert.AreEqual(4, arrows.Connections().Count, "referrals → agenda → waiting room → consultation → discharge");
            Assert.Greater(arrows.ChevronCount, 0);
            Assert.IsNull(clinic.GetComponent<SElement>(), "the composite stays a plain group");
            var door = clinic.GetComponentInChildren<UnitySource>().transform;
            Assert.AreEqual(1f, Vector3.Dot(door.forward, clinic.transform.right), 1e-4f, "the entrance opens towards the pathway");
        }

        [Test]
        public void TheHealthcareDashboardChartsAClinicRun()
        {
            var dash = Place("Healthcare Dashboard").GetComponent<UnityDashboard>();
            Assert.AreEqual(4, dash.charts.Count);
            var clinic = Place("Outpatient Clinic");
            var run = Run(clinic, 24 * 14);
            var set = new SimuLean.Results.ResultSet { Name = "clinic", Model = "m" };
            set.Scenarios.Add(new SimuLean.Results.ResultScenario { Name = "A" });
            set.Runs.Add(new SimuLean.Results.ResultRun { Scenario = "A", Replication = 1, Results = SimuLean.Results.RunResults.Capture(run.model, "clinic") });
            foreach (var chart in dash.charts)
            {
                var data = chart.Build(set, ResultsOutput.ProjectRoot);
                Assert.IsFalse(data.IsEmpty, $"{chart.spec.Title}: {data.Message}");
            }
        }

        [Test]
        public void TheOutpatientClinicRunsHeadless()
        {
            var root = Place("Outpatient Clinic");
            var result = Run(root, 24 * 14);
            var done = result.model.Find("Discharge");
            Assert.Greater(done.GetStatsInputCount(), 0, "patients booked, seen and discharged");
            var agenda = result.model.Find("Agenda") as SimuLean.Healthcare.Agenda;
            Assert.IsNotNull(agenda);
            Assert.Greater(agenda.LeadTimes.Count, 0);
        }

        [Test]
        public void TriageAndDiagnosticTestPresets()
        {
            var triage = Place("Triage").GetComponent<UnityMultiServer>();
            StringAssert.StartsWith("triage = pick(", triage.entryLabels[0]);
            var test = Place("Diagnostic Test (hospital)").GetComponent<UnityMultiServer>();
            Assert.IsNotNull(test.processBy.team, "its devices");
            Assert.AreEqual(2, test.processBy.team.members.Count);
        }

        [Test]
        public void TheFollowUpLoopRepeatsReviews()
        {
            var root = Place("Follow-up Loop");
            var result = Run(root, 24 * 365 * 2);
            Assert.Greater(result.model.Find("Discharge").GetStatsInputCount(), 0, "patients discharged after their reviews");
            var review = result.model.Find("Review");
            Assert.Greater(review.GetStatsInputCount(), result.model.Find("Discharge").GetStatsInputCount(), "more reviews than discharges: the loop");
        }

        [Test]
        public void TheHomeDiagnosticTestLendsItsDevices()
        {
            var root = Place("Home Diagnostic Test");
            var result = Run(root, 24 * 30);
            Assert.Greater(result.model.Find("Report").GetStatsInputCount(), 0, "valid tests reach the report");
            Assert.LessOrEqual(result.model.Find("Home Test").GetStatsContentMax(), 3, "never more patients at home than devices");
        }

        // the scene's model with the clock in hours from a Monday, inter-arrival every few hours
        private static (HeadlessModelFactory model, SimClock clock) Run(GameObject root, double hours)
        {
            foreach (var src in root.GetComponentsInChildren<UnitySource>())
                src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = 3 };
            var config = SceneExtractor.Extract(root).Config;
            config.StartDateTime = "2025-01-06 00:00";
            config.TimeUnitSeconds = 3600;
            config.MaxSimulationTime = hours;
            config.UseFixedSeed = true; config.Seed = 1;
            var clock = new SimClock(1);
            var model = new HeadlessModelFactory(clock);
            model.BuildModel(config);
            model.StartModel();
            clock.AdvanceClock(hours);
            return (model, clock);
        }
    }
}
