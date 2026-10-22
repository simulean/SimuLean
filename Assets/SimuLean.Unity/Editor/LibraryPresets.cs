using System;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The SimuLean Library's ready-made objects (GameObject → SimuLean → …, the window's Presets): each is an element
    /// with its default behaviour and a 3D model, as an object in FlexSim's library — a CNC machine is a MultiServer, a
    /// palletizer a Combiner, a team of operators a Resource Team whose members look like operators (the healthcare
    /// presets are in <c>Editor/Healthcare/HealthcarePresets</c>, the naval ones in <c>Editor/Naval/NavalPresets</c>). Stations get a <see cref="StationModel"/> (the model shown, selectable) and a
    /// <see cref="MachineStateView"/> (stack light / status panel, moving parts); they are visible in the scene as
    /// soon as they are placed. Teams get their member prefab (<c>Prefabs/ResourceMembers</c>); the window adds the
    /// first member. Every menu method selects the object it creates, as its last step (the window's <c>Place</c> takes
    /// the selection as the created object).
    /// </summary>
    public static class LibraryPresets
    {
        public const string Models = "Assets/SimuLean.Unity/Art/Models/";
        public const string Members = "Assets/SimuLean.Unity/Prefabs/ResourceMembers/";
        public const string Items = "Assets/SimuLean.Unity/Prefabs/Items/";
        private const string Manufacturing = "GameObject/SimuLean/Manufacturing/";
        private const string Teams = "GameObject/SimuLean/Teams/";

        private static readonly (string, string) Cnc = ("CNC machine", "CncMachine/CncMachine.fbx");
        private static readonly (string, string) Workstation = ("Workstation", "WorkstationProcessor/WorkstationProcessor.fbx");
        private static readonly (string, string) Bench = ("Assembly bench", "AssemblyBench/AssemblyBench.fbx");
        private static readonly (string, string) AssemblyTable = ("Assembly table", "AssemblyWorkstation/AssemblyWorkstation.fbx");

        // ------------------------------------------------------------------ manufacturing stations

        [SimuLeanPresetIcon(Models + "CncMachine/CncMachine.fbx")]
        [MenuItem(Manufacturing + "CNC Machine", false, 20)]
        private static void CncMachine(MenuCommand cmd) => Station<UnityMultiServer>(cmd, "CNC Machine", null, Cnc, Workstation);

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Prefabs/Elements/Multiserver.prefab")]
        [MenuItem(Manufacturing + "Workstation", false, 21)]
        private static void WorkstationStation(MenuCommand cmd) => Station<UnityMultiServer>(cmd, "Workstation", null, Workstation, Cnc);

        [SimuLeanPresetIcon(Models + "PaintBooth/PaintBooth.fbx")]
        [MenuItem(Manufacturing + "Paint Booth", false, 22)]
        private static void PaintBooth(MenuCommand cmd) =>
            Station<UnityMultiServer>(cmd, "Paint Booth", null, ("Paint booth", "PaintBooth/PaintBooth.fbx"));

        [SimuLeanPresetIcon(Models + "InspectionStation/InspectionStation.fbx")]
        [MenuItem(Manufacturing + "Inspection Station", false, 23)]
        private static void Inspection(MenuCommand cmd) =>
            Station<UnityMultiServer>(cmd, "Inspection Station", null, ("Vision inspection", "InspectionStation/InspectionStation.fbx"));

        [SimuLeanPresetIcon(Models + "WeldPositioner/WeldPositioner.fbx")]
        [MenuItem(Manufacturing + "Weld Positioner", false, 24)]
        private static void WeldPositioner(MenuCommand cmd) =>
            Station<UnityMultiServer>(cmd, "Weld Positioner", null, ("Weld positioner", "WeldPositioner/WeldPositioner.fbx"));

        [SimuLeanPresetIcon(Models + "AssemblyBench/AssemblyBench.fbx")]
        [MenuItem(Manufacturing + "Assembly Bench", false, 25)]
        private static void AssemblyBench(MenuCommand cmd) => Station<UnityCombiner>(cmd, "Assembly Bench", null, Bench, AssemblyTable);

        [SimuLeanPresetIcon(Models + "Palletizer/Palletizer.fbx")]
        [MenuItem(Manufacturing + "Palletizer", false, 26)]
        private static void Palletizer(MenuCommand cmd) =>
            Station<UnityCombiner>(cmd, "Palletizer", c => c.requirements = new[] { 12 }, ("Column palletizer", "Palletizer/Palletizer.fbx"));

        [SimuLeanPresetIcon(Models + "SafetyFence/Fence_Panel.fbx")]
        [SimuLeanVisual]
        [MenuItem(Manufacturing + "Safety Fence", false, 40)]
        private static void SafetyFence(MenuCommand cmd)
        {
            var go = NewObject(cmd, "Safety Fence");
            var strip = go.AddComponent<UnityModularStrip>();
            strip.segmentLength = 1f;
            strip.length = 6f;
            strip.segment = Load("SafetyFence/Fence_Panel.fbx");
            strip.cap = Load("SafetyFence/Fence_End.fbx");
            strip.Rebuild();
            Select(go);
        }

        // ------------------------------------------------------------------ resource teams

        [SimuLeanPresetIcon(Models + "Operator/Operator.fbx")]   // the prefab has no meshes until a look is applied
        [MenuItem(Teams + "Operators", false, 20)]
        private static void Operators(MenuCommand cmd) => Team(cmd, "Operators", "Operator.prefab");

        // people are one element: a doctor, a nurse, a fitter… is the operator with another look (OperatorAppearance.look;
        // the Doctor, Nurse, Fitter… prefabs are variants of the operator, chosen as a team's member prefab)

        [SimuLeanPresetIcon(Members + "BedPorter.prefab"), SimuLeanPresetPack(SimuLeanPacks.Healthcare)]
        [MenuItem(Teams + "Bed Porters", false, 23)]
        private static void BedPorters(MenuCommand cmd) => Team(cmd, "Bed Porters", "BedPorter.prefab");

        [SimuLeanPresetIcon(Members + "StretcherPorter.prefab"), SimuLeanPresetPack(SimuLeanPacks.Healthcare)]
        [MenuItem(Teams + "Stretcher Porters", false, 24)]
        private static void StretcherPorters(MenuCommand cmd) => Team(cmd, "Stretcher Porters", "StretcherPorter.prefab");

        [SimuLeanPresetIcon(Members + "WheelchairPorter.prefab"), SimuLeanPresetPack(SimuLeanPacks.Healthcare)]
        [MenuItem(Teams + "Wheelchair Porters", false, 25)]
        private static void WheelchairPorters(MenuCommand cmd) => Team(cmd, "Wheelchair Porters", "WheelchairPorter.prefab");

        [SimuLeanPresetIcon(Members + "AGV.prefab")]
        [MenuItem(Teams + "AGV Fleet", false, 30)]
        private static void Agvs(MenuCommand cmd) => Team(cmd, "AGV Fleet", "AGV.prefab");

        [SimuLeanPresetIcon(Members + "Forklift.prefab")]
        [MenuItem(Teams + "Forklifts", false, 31)]
        private static void Forklifts(MenuCommand cmd) => Team(cmd, "Forklifts", "Forklift.prefab");

        [SimuLeanPresetIcon(Members + "PalletTruckOperator.prefab")]
        [MenuItem(Teams + "Pallet Trucks", false, 32)]
        private static void PalletTrucks(MenuCommand cmd) => Team(cmd, "Pallet Trucks", "PalletTruckOperator.prefab");

        [SimuLeanPresetIcon(Members + "IndustrialRobot_Gripper.prefab")]
        [MenuItem(Teams + "Robots (Gripper)", false, 40)]
        private static void RobotsGripper(MenuCommand cmd) => Team(cmd, "Robots (Gripper)", "IndustrialRobot_Gripper.prefab");

        [SimuLeanPresetIcon(Members + "IndustrialRobot_Vacuum.prefab")]
        [MenuItem(Teams + "Robots (Vacuum)", false, 41)]
        private static void RobotsVacuum(MenuCommand cmd) => Team(cmd, "Robots (Vacuum)", "IndustrialRobot_Vacuum.prefab");

        [SimuLeanPresetIcon(Members + "IndustrialRobot_Welder.prefab")]
        [MenuItem(Teams + "Robots (Welding)", false, 42)]
        private static void RobotsWelding(MenuCommand cmd) => Team(cmd, "Robots (Welding)", "IndustrialRobot_Welder.prefab");

        // ------------------------------------------------------------------ helpers

        /// <summary>An element with its model options (the first one shown) and the state view.</summary>
        public static GameObject Station<T>(MenuCommand cmd, string name, Action<T> setup, params (string label, string path)[] models)
            where T : SElement
        {
            var go = NewObject(cmd, name);
            var element = AddElement<T>(go);
            setup?.Invoke(element);
            AddModel(go, models);
            go.AddComponent<MachineStateView>();
            Select(go);
            return go;
        }

        private static GameObject NewObject(MenuCommand cmd, string name)
        {
            var go = new GameObject(name);
            GameObjectUtility.SetParentAndAlign(go, cmd?.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            return go;
        }

        internal static T AddElement<T>(GameObject go) where T : SElement
        {
            var element = go.AddComponent<T>();
            if (go.GetComponent<SElementConnectionGizmo>() == null) go.AddComponent<SElementConnectionGizmo>();
            return element;
        }

        private static void AddModel(GameObject go, params (string label, string path)[] models)
        {
            var sm = go.AddComponent<StationModel>();
            foreach (var (label, path) in models)
            {
                var model = Load(path);
                if (model != null) sm.options.Add(new StationModel.Option { name = label, model = model });
                else Debug.LogWarning($"[SimuLean] Library model {Models + path} not found.");
            }
            sm.selected = 0;
            sm.Rebuild();
        }

        /// <summary>A resource team whose members look like <paramref name="memberPrefab"/> (in <see cref="Members"/>).</summary>
        public static GameObject Team(MenuCommand cmd, string name, string memberPrefab)
        {
            var go = NewObject(cmd, name);
            var team = go.AddComponent<UnityResourceTeam>();   // its name is the GameObject's (one name; review2 #41)
            team.memberPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Members + memberPrefab);
            if (team.memberPrefab == null) Debug.LogWarning($"[SimuLean] Member prefab {Members + memberPrefab} not found.");
            Select(go);
            return go;
        }

        // last: adding an element can move the selection (its hierarchy group), and the window's Place takes the
        // selected object as the one created
        private static void Select(GameObject go) => Selection.activeObject = go;

        private static GameObject Load(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(Models + path);
    }
}
