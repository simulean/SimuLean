using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Naval library, the yard's resources: the member prefabs (SPMT, Goliath crane, welding gantry, hall crane) carry
    /// the view a team drives them with; the crane rigs (<see cref="ICraneRig"/>) put the head where the engine says,
    /// fit around the points they work at, and the vehicle view finds every wheel of the SPMT with its own radius.
    /// </summary>
    public class ShipyardResourceTests
    {
        private const string Members = LibraryPresets.Members;

        private static void InPreview(string prefab, System.Action<GameObject> body)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(Members + prefab);
            Assert.IsNotNull(asset, prefab);
            var scene = EditorSceneManager.NewPreviewScene();
            try { body((GameObject)PrefabUtility.InstantiatePrefab(asset, scene)); }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void TheSpmtsWheelsAreFoundWithTheirRadius()
        {
            InPreview("SPMT.prefab", go =>
            {
                Assert.IsNotNull(go.GetComponent<UnityVehicleAnimator>());
                var wheels = UnityVehicleAnimator.FindWheels(go.transform, 0.1f);
                Assert.AreEqual(28, wheels.Count, "7 axle lines x 4 bogies");
                foreach (var (w, r) in wheels) Assert.AreEqual(0.36f, r, 0.03f, w.name);
                Assert.IsNotNull(go.GetComponentsInChildren<Transform>().FirstOrDefault(t => t.name == "Lift"), "the deck lifts");
            });
        }

        [Test]
        public void TheForkliftAndWheelchairWheelsKeepTheirSize()
        {
            InPreview("Forklift.prefab", go =>
            {
                var wheels = UnityVehicleAnimator.FindWheels(go.transform, 0.1f);
                Assert.AreEqual(4, wheels.Count);
                Assert.AreEqual(0.2285f, wheels.First(w => w.wheel.name == "Wheel_FL").radius, 0.02f);
            });
            InPreview("WheelchairPorter.prefab", go =>
            {
                var wheels = UnityVehicleAnimator.FindWheels(go.transform, 0.1f);
                Assert.That(wheels.Any(w => Mathf.Abs(w.radius - 0.30f) < 0.03f), "the big wheels at 0.30 m");
            });
        }

        [Test]
        public void TheTravelingGantriesPutTheirHeadWhereAsked([Values("GoliathCrane.prefab", "WeldingGantry.prefab")] string prefab)
        {
            InPreview(prefab, go =>
            {
                var rig = go.GetComponent<TravelingGantryRig>();
                Assert.IsNotNull(rig, prefab);
                ICraneRig crane = rig;
                Assert.IsTrue(crane.HasHead);
                rig.Fit(rig.Span * 1.5f, rig.Length * 1.5f);
                var target = new Vector2(rig.Span * 0.2f, rig.Length * 0.1f);
                float cup = crane.RestCup * 0.5f;
                crane.SetHead(target, cup);
                Vector3 socket = go.GetComponentsInChildren<Transform>().First(t => t.name == "HookSocket").position;
                Assert.AreEqual(target.x, socket.x, 0.05f, "trolley along the girder");
                Assert.AreEqual(target.y, socket.z, 0.05f, "bridge along the runway");
                Assert.AreEqual(cup, socket.y, 0.05f, "head lowered");
                var legW = go.GetComponentsInChildren<Transform>().First(t => t.name == "Leg_W");
                var legE = go.GetComponentsInChildren<Transform>().First(t => t.name == "Leg_E");
                Assert.AreEqual(rig.Span, Mathf.Abs(legE.localPosition.x - legW.localPosition.x), 1e-3f, "legs at the new span");
            });
        }

        [Test]
        public void TheHallCraneIsACraneRigFittedAroundItsPoints()
        {
            InPreview("HallCrane.prefab", go =>
            {
                var crane = go.GetComponent<UnityOverheadCrane>();
                Assert.IsNotNull(crane);
                ICraneRig rig = crane;
                rig.FitAround(new[] { new Vector3(-5, 0, -20), new Vector3(5, 0, 20) }, 1f);
                Assert.IsTrue(rig.HasHead, "built by the fit");
                Assert.GreaterOrEqual(crane.span, 10f);
                Assert.GreaterOrEqual(crane.BridgeLimit, 20f, "the bridge reaches both ends");
                rig.SetHead(rig.PlanOf(new Vector3(3, 0, 12)), 2f);
                Assert.AreEqual(3f, rig.CupWorld.x, 0.05f);
                Assert.AreEqual(12f, rig.CupWorld.z, 0.05f);
                Assert.AreEqual(2f, rig.CupWorld.y, 0.05f);
            });
        }

        [Test]
        public void ACraneTeamsPreviewStandsAsInTheRun([Values(true, false)] bool lengthwise)
        {
            // the run fits the crane around the team's points (its home and the stations it serves): the preview does too
            var home = lengthwise ? new Vector3(2, 0, -20) : new Vector3(-20, 0, 2);
            var station = lengthwise ? new Vector3(-4, 0, 26) : new Vector3(26, 0, -4);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Members + "GoliathCrane.prefab");
            using (var t = new TestScene("CranePreview", activate: true, revertUndo: true))
            {
                var team = t.Make<UnityResourceTeam>("Goliath");
                team.travel = TeamTravel.Axes;
                team.memberPrefab = prefab;
                team.members.Add(new ResourceMemberSpec());
                team.homeNode = t.Make<UnityTransportNode>("Home");
                team.homeNode.transform.position = home;
                var stop = t.Make<UnityTransportNode>("Station");
                stop.transform.position = station;
                var server = t.Make<UnityMultiServer>("Assembly");
                server.location = stop;
                server.processBy.team = team;

                TeamMemberPreviews.EnsureAll();
                var holder = team.transform.Find(TeamMemberPreviews.Child);
                Assert.IsNotNull(holder, "the team has its preview");
                var shown = holder.GetChild(0).GetComponent<TravelingGantryRig>();
                Assert.IsNotNull(shown, "the member is the Goliath crane");

                // the run's member: the prefab fitted around the same points (UnityResourceTeam binding)
                InPreview("GoliathCrane.prefab", go =>
                {
                    var run = (TravelingGantryRig)UnityResourceTeam.CraneRigOf(go);
                    run.FitAround(team.ReachPoints(), 1f);
                    Assert.AreEqual(lengthwise ? 0f : 90f, shown.transform.eulerAngles.y, 0.1f, "the runway along the points' longer side");
                    Assert.AreEqual(run.transform.eulerAngles.y, shown.transform.eulerAngles.y, 0.1f, "turned as in the run");
                    Assert.Less(Vector3.Distance(run.transform.position, shown.transform.position), 0.01f, "centred as in the run");
                    Assert.AreEqual(run.Span, shown.Span, 0.01f, "the span as in the run");
                    Assert.AreEqual(run.Length, shown.Length, 0.01f, "the runway length as in the run");
                    Assert.Greater(shown.Length, 46f, "the runway covers both points");
                });

                // a served element moved: the preview follows
                stop.transform.position = station + (lengthwise ? new Vector3(0, 0, 10) : new Vector3(10, 0, 0));
                TeamMemberPreviews.EnsureAll();
                var refit = team.transform.Find(TeamMemberPreviews.Child).GetChild(0).GetComponent<TravelingGantryRig>();
                Assert.Greater(refit.Length, 56f, "refitted to the moved station");
            }
        }

        [Test]
        public void TheShipyardWorkersHaveBothLooksTheirHeadGearAndTheJoints([Values("Fitter", "Welder", "Painter", "Outfitter")] string role)
        {
            InPreview(role + ".prefab", go =>
            {
                var look = go.GetComponent<OperatorAppearance>();
                Assert.IsNotNull(look, role);
                Assert.IsNotNull(look.femaleModel, $"{role}: the female look");
                Assert.IsNotNull(go.GetComponent<UnityVehicleAnimator>(), $"{role}: walks");
                foreach (var g in new[] { OperatorAppearance.Gender.Male, OperatorAppearance.Gender.Female })
                {
                    look.gender = g;
                    look.Apply();
                    var active = go.GetComponentsInChildren<Transform>().Select(t => t.name).ToList();
                    foreach (var j in new[] { "Torso", "Leg_L", "Arm_R", "CarrySocket", "Helmet" })
                        CollectionAssert.Contains(active, j, $"{role} {g}: {j}");
                    Assert.That(active.Any(n => n.Contains("Hat_LOD0")), $"{role} {g}: the head gear");
                }
            });
        }

        [Test]
        public void TheShipbuildingTeamsGetTheirMembers()
        {
            bool organize = ModelHierarchy.AutoOrganize;
            ModelHierarchy.AutoOrganize = false;   // no model groups left in the open scene
            try
            {
                foreach (var name in new[] { "Goliath Crane", "Hall Crane", "Level-Luffing Crane", "SPMT Transporters", "Welding Gantry", "Welding Robots" })
                {
                    var entry = SimuLeanWindow.CollectEntries().First(e => e.Name == name && e.MenuPath != null);   // Hall Crane: the core teams
                    var go = SimuLeanWindow.Place(entry, Vector3.zero);
                    try
                    {
                        var team = go.GetComponent<UnityResourceTeam>();
                        Assert.IsNotNull(team, name);
                        Assert.IsNotNull(team.memberPrefab, $"{name}: member prefab");
                        Assert.IsNotNull(AssetDatabase.LoadMainAssetAtPath(entry.IconPath), $"{name}: icon");
                    }
                    finally { Object.DestroyImmediate(go); }
                }
            }
            finally { ModelHierarchy.AutoOrganize = organize; }
        }
    }
}
