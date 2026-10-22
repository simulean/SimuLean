using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Naval library N4, the yard's resources: the member prefabs (SPMT, Goliath crane, welding gantry, hall crane) carry
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
        public void TheTravellingGantriesPutTheirHeadWhereAsked([Values("GoliathCrane.prefab", "WeldingGantry.prefab")] string prefab)
        {
            InPreview(prefab, go =>
            {
                var rig = go.GetComponent<TravellingGantryRig>();
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
