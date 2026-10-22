using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// The library's ready-made objects (<see cref="LibraryPresets"/>): placed through the SimuLean window's own path,
    /// each station is an element with its model visible in edit mode (a <see cref="StationModel"/> child bound to the
    /// item position) and a <see cref="MachineStateView"/>; each team gets its member prefab. Also the prefabs they use
    /// (people, porters, items) and the views' rules.
    /// </summary>
    public class LibraryPresetsTests
    {
        private const string Members = LibraryPresets.Members;
        private const string Items = LibraryPresets.Items;

        private static readonly string[] Stations =
        {
            "CNC Machine", "Workstation", "Paint Booth", "Inspection Station", "Weld Positioner", "Assembly Bench",
            "Palletizer", "Reception Desk", "Consultation Room",
        };

        private static readonly string[] TeamsPresets =
        {
            "Operators", "Bed Porters", "Stretcher Porters", "Wheelchair Porters", "AGV Fleet",
            "Forklifts", "Pallet Trucks", "Robots (Gripper)", "Robots (Vacuum)", "Robots (Welding)",
        };

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
            var go = SimuLeanWindow.Place(entry, Vector3.zero);
            Assert.IsNotNull(go, $"'{name}' created nothing");
            return go;
        }

        [Test]
        public void StationsAreElementsWithTheirModelVisibleBeforeTheRun()
        {
            foreach (var name in Stations)
            {
                var go = Place(name);
                Assert.IsNotNull(go.GetComponent<SElement>(), $"{name}: no element");
                var sm = go.GetComponent<StationModel>();
                Assert.IsNotNull(sm, $"{name}: no station model");
                Assert.IsNotNull(sm.Instance, $"{name}: the model is not in the scene");
                Assert.Greater(sm.Instance.GetComponentsInChildren<Renderer>().Length, 0, $"{name}: the model has no renderers");
                Assert.IsNotNull(go.GetComponent<MachineStateView>(), $"{name}: no state view");
                var socket = sm.Socket();
                if (socket != null)
                {
                    var bound = new[] { "inItemPosition", "itemPosition" }
                        .Select(f => go.GetComponent<SElement>().GetType().GetField(f))
                        .Where(f => f != null).Select(f => f.GetValue(go.GetComponent<SElement>()) as Transform);
                    Assert.IsTrue(bound.Contains(socket), $"{name}: the item position is not bound to {socket.name}");
                }
            }
        }

        [Test]
        public void EveryPresetHasAnIcon()
        {
            var presets = SimuLeanWindow.CollectEntries().Where(e => e.Group == "Presets").ToList();
            Assert.Greater(presets.Count, 20);
            foreach (var e in presets)
            {
                Assert.IsFalse(string.IsNullOrEmpty(e.IconPath), $"preset '{e.Name}' has no [SimuLeanPresetIcon]");
                var icon = AssetDatabase.LoadMainAssetAtPath(e.IconPath);
                Assert.IsNotNull(icon, $"preset '{e.Name}': icon {e.IconPath} not found");
                // a model with nothing to draw (e.g. a prefab whose look is built when placed) gives an empty tile
                if (icon is GameObject g)
                    Assert.Greater(g.GetComponentsInChildren<Renderer>(true).Length, 0, $"preset '{e.Name}': icon {e.IconPath} has nothing to draw");
            }
        }

        [Test]
        public void PlacingPresetsNeverRenamesOtherObjects()
        {
            var bystander = new GameObject("Directional Light");
            Selection.activeGameObject = bystander;
            var a = Place("Consultation Room");
            var b = Place("Consultation Room");
            var t = Place("Operators");
            Assert.AreEqual("Directional Light", bystander.name);
            Assert.AreNotSame(a, b);
            Assert.IsNotNull(a.GetComponent<UnityMultiServer>());
            Assert.IsNotNull(b.GetComponent<UnityMultiServer>());
            Assert.IsNotNull(t.GetComponent<UnityResourceTeam>());
            foreach (var root in scene.GetRootGameObjects())
                Assert.IsNull(root.GetComponent<SElement>(), $"{root.name}: an element ended up as a root, outside its group");
        }

        [Test]
        public void ChoosingAnotherModelRebindsTheItemPosition()
        {
            var go = Place("CNC Machine");
            var ms = go.GetComponent<UnityMultiServer>();
            var sm = go.GetComponent<StationModel>();
            Assert.AreEqual("WorkSocket", ms.inItemPosition.name);
            Assert.AreEqual(2, sm.options.Count, "the CNC preset offers the CNC and the workstation models");
            sm.selected = 1;
            sm.Rebuild();
            Assert.IsNull(ms.inItemPosition, "the workstation model has no work socket: back to the footprint");
            Assert.AreEqual(1, go.transform.Cast<Transform>().Count(t => t.name == StationModel.ModelChild));
            sm.selected = 0;
            sm.Rebuild();
            Assert.AreEqual("WorkSocket", ms.inItemPosition.name);
        }

        [Test]
        public void TheWaitingRoomSeatsTheQueue()
        {
            var go = Place("Waiting Room");
            var q = go.GetComponent<UnityQueue>();
            Assert.AreEqual(QueueLayoutMode.Seats, q.layoutMode);
            Assert.AreEqual(q.capacity, q.Seats().Count, "one seat per place");
        }

        [Test]
        public void ReferralsBringPatients()
        {
            var go = Place("Referrals");
            var src = go.GetComponent<UnitySource>();
            Assert.IsNotNull(src.itemPrefab);
            Assert.AreEqual("Patient", src.itemPrefab.name);
        }

        [Test]
        public void TeamsGetTheirMembers()
        {
            foreach (var name in TeamsPresets)
            {
                var go = Place(name);
                var team = go.GetComponent<UnityResourceTeam>();
                Assert.IsNotNull(team, name);
                Assert.IsNotNull(team.memberPrefab, $"{name}: no member prefab");
                Assert.GreaterOrEqual(team.members.Count, 1, $"{name}: the window adds the first member");
            }
        }

        [Test]
        public void PortersPushTheirCarrierAndThePatientRidesOnIt()
        {
            foreach (var (prefab, radius) in new[] { ("BedPorter", 0.10f), ("StretcherPorter", 0.10f), ("WheelchairPorter", 0.30f) })
            {
                var p = AssetDatabase.LoadAssetAtPath<GameObject>(Members + prefab + ".prefab");
                Assert.IsNotNull(p, prefab);
                var anim = p.GetComponent<UnityVehicleAnimator>();
                Assert.IsNotNull(anim, prefab);
                Assert.AreEqual(radius, anim.wheelRadius, 1e-4f, prefab);
                var look = p.GetComponentInChildren<OperatorAppearance>(true);
                Assert.IsNotNull(look, prefab);
                Assert.AreNotSame(p, look.gameObject, $"{prefab}: the look sits on the porter, not on the root (it would hide the carrier)");
                var load = p.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "LoadSocket");
                Assert.IsNotNull(load, $"{prefab}: no LoadSocket");
                Assert.AreEqual("PatientSocket", load.parent.name, prefab);
            }
        }

        [Test]
        public void PeopleAndContainersArePrefabs()
        {
            foreach (var m in new[] { "Doctor", "Nurse", "PalletTruckOperator", "IndustrialRobot_Welder" })
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(Members + m + ".prefab"), m);
            var patient = AssetDatabase.LoadAssetAtPath<GameObject>(Items + "Patient.prefab");
            Assert.AreEqual(OperatorAppearance.Gender.Mixed, patient.GetComponent<OperatorAppearance>().gender);
            foreach (var c in new[] { "EuroPallet", "Tote", "RollCage" })
            {
                var item = AssetDatabase.LoadAssetAtPath<GameObject>(Items + c + ".prefab");
                Assert.IsNotNull(item, c);
                Assert.IsNotNull(ItemVisuals.LoadSocket(item), $"{c}: a container marks where its load rests");
            }
            var welder = AssetDatabase.LoadAssetAtPath<GameObject>(Members + "IndustrialRobot_Welder.prefab");
            Assert.AreEqual("WeldingTorch", welder.GetComponent<RobotArmRig>().tool.name);
        }

        [Test]
        public void PeopleAreTheOperatorWithAnotherLook()
        {
            var names = SimuLeanWindow.CollectEntries().Select(e => e.Name).ToList();
            foreach (var role in new[] { "Doctors", "Nurses", "Fitters", "Welders", "Painters", "Outfitters" })
                CollectionAssert.DoesNotContain(names, role, "a role is a look of the operator, not a library element");
            var op = AssetDatabase.LoadAssetAtPath<GameObject>(Members + "Operator.prefab");
            foreach (var role in new[] { "Doctor", "Nurse", "Fitter", "Welder", "Painter", "Outfitter" })
            {
                var p = AssetDatabase.LoadAssetAtPath<GameObject>(Members + role + ".prefab");
                Assert.AreSame(op, PrefabUtility.GetCorrespondingObjectFromSource(p), $"{role}: a variant of the operator");
                Assert.AreEqual(role, p.GetComponent<OperatorAppearance>().look);
            }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(op, scene);
            var look = go.GetComponent<OperatorAppearance>();
            Assert.IsNotNull(look.catalog);
            look.SetLook("Welder");
            Assert.That(go.GetComponentsInChildren<Transform>().Any(t => t.name == "WelderHat_LOD0"), "the welder's head gear");
            Assert.That(go.GetComponentsInChildren<Transform>().Any(t => t.name == "Arm_L"), "the joints the views drive");
            Assert.That(go.GetComponentsInChildren<Transform>(true).All(t => t == go.transform || (t.gameObject.hideFlags & HideFlags.DontSave) != 0),
                        "the look's models are not saved with the scene");
        }

        [Test]
        public void TheLampFollowsTheElementState()
        {
            Assert.AreEqual(MachineStateView.Lamp.Green, MachineStateView.LampFor("PROCESSING"));
            Assert.AreEqual(MachineStateView.Lamp.Amber, MachineStateView.LampFor("IDLE"));
            Assert.AreEqual(MachineStateView.Lamp.Amber, MachineStateView.LampFor("BLOCKED"));
            Assert.AreEqual(MachineStateView.Lamp.Amber, MachineStateView.LampFor("WAITING_FOR_RESOURCE"));
            Assert.AreEqual(MachineStateView.Lamp.Red, MachineStateView.LampFor("BREAKDOWN"));
            Assert.AreEqual(MachineStateView.Lamp.Red, MachineStateView.LampFor("OFF_SHIFT"));
            Assert.AreEqual(MachineStateView.Lamp.None, MachineStateView.LampFor(null));
        }
    }
}
