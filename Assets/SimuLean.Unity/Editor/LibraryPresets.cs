using System;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// The SimuLean Library's ready-made objects (GameObject → SimuLean → …, the window's Presets): each is an element
    /// with its default behaviour and a 3D model, as an object of a simulation library — a CNC machine is a MultiServer, a
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
        private const string Warehousing = "GameObject/SimuLean/Warehousing/";

        private static readonly (string, string) Cnc = ("CNC machine", "CncMachine/CncMachine.fbx");
        private static readonly (string, string) Workstation = ("Workstation", "WorkstationProcessor/WorkstationProcessor.fbx");
        private static readonly (string, string) Bench = ("Assembly bench", "AssemblyBench/AssemblyBench.fbx");
        private static readonly (string, string) AssemblyTable = ("Assembly table", "AssemblyWorkstation/AssemblyWorkstation.fbx");

        // ------------------------------------------------------------------ manufacturing stations

        [SimuLeanPresetIcon(Models + "CncMachine/CncMachine.fbx")]
        [SimuLeanVariantOf(typeof(UnityMultiServer))]
        [MenuItem(Manufacturing + "CNC Machine", false, 20)]
        private static void CncMachine(MenuCommand cmd) => Station<UnityMultiServer>(cmd, "CNC Machine", Takes(300), Cnc, Workstation);

        [SimuLeanPresetIcon(Models + "PaintBooth/PaintBooth.fbx")]
        [SimuLeanVariantOf(typeof(UnityMultiServer))]
        [MenuItem(Manufacturing + "Paint Booth", false, 22)]
        private static void PaintBooth(MenuCommand cmd) =>
            Station<UnityMultiServer>(cmd, "Paint Booth", Takes(900), ("Paint booth", "PaintBooth/PaintBooth.fbx"));

        [SimuLeanPresetIcon(Models + "InspectionStation/InspectionStation.fbx")]
        [SimuLeanVariantOf(typeof(UnityMultiServer))]
        [MenuItem(Manufacturing + "Inspection Station", false, 23)]
        private static void Inspection(MenuCommand cmd) =>
            Station<UnityMultiServer>(cmd, "Inspection Station", Takes(30), ("Vision inspection", "InspectionStation/InspectionStation.fbx"));

        [SimuLeanPresetIcon(Models + "WeldPositioner/WeldPositioner.fbx")]
        [SimuLeanVariantOf(typeof(UnityMultiServer))]
        [MenuItem(Manufacturing + "Weld Positioner", false, 24)]
        private static void WeldPositioner(MenuCommand cmd) =>
            Station<UnityMultiServer>(cmd, "Weld Positioner", Takes(240), ("Weld positioner", "WeldPositioner/WeldPositioner.fbx"));

        [SimuLeanPresetIcon(Models + "AssemblyBench/AssemblyBench.fbx")]
        [SimuLeanVariantOf(typeof(UnityCombiner))]
        [MenuItem(Manufacturing + "Assembly Bench", false, 25)]
        private static void AssemblyBench(MenuCommand cmd) => Station<UnityCombiner>(cmd, "Assembly Bench", Assembles(120), Bench, AssemblyTable);

        [SimuLeanPresetIcon(Models + "Palletizer/Palletizer.fbx")]
        [SimuLeanVariantOf(typeof(UnityCombiner))]
        [MenuItem(Manufacturing + "Palletizer", false, 26)]
        // 12 cartons 400 × 300 × 250 on a Euro pallet: 2 layers of 3 × 2, the load growing on the pallet (On Load)
        private static void Palletizer(MenuCommand cmd) =>
            Station<UnityCombiner>(cmd, "Palletizer", Packer(12, 60), ("Column palletizer", "Palletizer/Palletizer.fbx"));

        // 4 small cartons (SmallCarton, 255 × 170 × 120) into a tote 600 × 400: one 2 × 2 layer inside it
        [SimuLeanPresetIcon(Models + "WarehouseLoadUnits/SmallLoadCarrier_600x400x280.fbx")]
        [SimuLeanVariantOf(typeof(UnityCombiner))]
        [MenuItem(Manufacturing + "Tote Packer", false, 27)]
        private static void TotePacker(MenuCommand cmd) =>
            Station<UnityCombiner>(cmd, "Tote Packer", Packer(4, 30), AssemblyTable, Bench);

        // a packing combiner: n components of one input, each laid straight in its place in the load, in `seconds`
        private static Action<UnityCombiner> Packer(int n, double seconds) => c =>
        {
            c.requirements = new[] { n };
            c.componentLayout = UnityCombiner.ComponentLayout.OnLoad;
            c.delayStrategy = Constant(seconds);
        };

        // Sensible defaults of a variant: a typical time for what it is, in seconds of the scene clock's unit
        // (a CNC machining a part takes minutes, an inspection seconds). Constant, as the element's default; change the
        // distribution in the inspector.
        private static Action<UnityMultiServer> Takes(double seconds) => s => s.delayStrategy = Constant(seconds);
        private static Action<UnityCombiner> Assembles(double seconds) => c => c.delayStrategy = Constant(seconds);

        private static RandomProcessConfig Constant(double seconds)
        {
            var clock = UnityCompat.FindAny<UnitySimClock>();
            double unit = clock != null && clock.TimeUnitSeconds > 0 ? clock.TimeUnitSeconds : 1.0;
            return new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = seconds / unit };
        }

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

        // ------------------------------------------------------------------ warehousing

        // a milk run drawn as a tugger train: the tow tractor (for an abstract team; a spatial team shows its member
        // prefab) with the carriers on board coupled behind it as trailers
        [SimuLeanPresetIcon(Models + "TuggerTrain/TowTractor.fbx")]
        [SimuLeanVariantOf(typeof(UnityMilkRun))]
        [MenuItem(Warehousing + "Tugger Train", false, 20)]
        private static void TuggerTrain(MenuCommand cmd)
        {
            var go = NewObject(cmd, "Tugger Train");
            var milkRun = go.AddComponent<UnityMilkRun>();
            milkRun.carrierView = MilkRunCarrierView.Trailers;
            milkRun.tractorPrefab = Load("TuggerTrain/TowTractor.fbx");
            milkRun.capacity = 4;
            Select(go);
        }

        // ------------------------------------------------------------------ resource teams

        [SimuLeanPresetIcon(Models + "Operator/Operator.fbx")]   // the prefab has no meshes until a look is applied
        [SimuLeanVariantOf(typeof(UnityResourceTeam), "People")]
        [MenuItem(Teams + "Operators", false, 20)]
        private static void Operators(MenuCommand cmd) => Team(cmd, "Operators", "operator");

        // people are one element: a doctor, a nurse, a fitter… is the operator with another look (OperatorAppearance.look;
        // the Doctor, Nurse, Fitter… prefabs are variants of the operator, chosen as a team's member prefab)

        [SimuLeanPresetIcon(Members + "BedPorter.prefab"), SimuLeanPresetPack(SimuLeanPacks.Healthcare)]
        [SimuLeanVariantOf(typeof(UnityResourceTeam), "People")]
        [MenuItem(Teams + "Bed Porters", false, 23)]
        private static void BedPorters(MenuCommand cmd) => Team(cmd, "Bed Porters", "bed porter");

        [SimuLeanPresetIcon(Members + "StretcherPorter.prefab"), SimuLeanPresetPack(SimuLeanPacks.Healthcare)]
        [SimuLeanVariantOf(typeof(UnityResourceTeam), "People")]
        [MenuItem(Teams + "Stretcher Porters", false, 24)]
        private static void StretcherPorters(MenuCommand cmd) => Team(cmd, "Stretcher Porters", "stretcher porter");

        [SimuLeanPresetIcon(Members + "WheelchairPorter.prefab"), SimuLeanPresetPack(SimuLeanPacks.Healthcare)]
        [SimuLeanVariantOf(typeof(UnityResourceTeam), "People")]
        [MenuItem(Teams + "Wheelchair Porters", false, 25)]
        private static void WheelchairPorters(MenuCommand cmd) => Team(cmd, "Wheelchair Porters", "wheelchair porter");

        /// <summary>A token pool: a team used as a semaphore — N abstract members (no
        /// travel, no look of their own) that the stations of a zone require, held In Order before their own crew, so at
        /// most N jobs are in the zone at once. Seen in <c>simulean_diagnose</c>, with utilisation and max wait; never a
        /// counter property kept by reactions (racy).</summary>
        [SimuLeanPresetIcon(Models + "SafetyFence/Fence_Door.fbx")]   // a zone's door: N may be inside
        [MenuItem(Teams + "Token Pool", false, 50)]
        private static void TokenPool(MenuCommand cmd)
        {
            var go = NewObject(cmd, "Token Pool");
            var team = go.AddComponent<UnityResourceTeam>();
            team.travel = TeamTravel.Abstract;
            team.AddMembers(3);
            Select(go);
        }

        [SimuLeanPresetIcon(Members + "AGV.prefab")]
        [MenuItem(Teams + "AGV Fleet", false, 30)]
        private static void Agvs(MenuCommand cmd) => Team(cmd, "AGV Fleet", "agv");

        [SimuLeanPresetIcon(Members + "Forklift.prefab")]
        [SimuLeanVariantOf(typeof(UnityResourceTeam), "Vehicles")]
        [MenuItem(Teams + "Forklifts", false, 21)]
        private static void Forklifts(MenuCommand cmd) => Team(cmd, "Forklifts", "forklift");

        [SimuLeanPresetIcon(Members + "PalletTruckOperator.prefab")]
        [SimuLeanVariantOf(typeof(UnityResourceTeam), "Vehicles")]
        [MenuItem(Teams + "Pallet Trucks", false, 27)]
        private static void PalletTrucks(MenuCommand cmd) => Team(cmd, "Pallet Trucks", "pallet truck");

        // an AS/RS: the crane runs along the aisle it is placed on (its +Z) and serves the lanes of the storage whose
        // transport it is (StackerCraneView; lanes without a node get one at their position)
        [SimuLeanPresetIcon(Models + "ASRS/StackerCrane.fbx")]
        [SimuLeanVariantOf(typeof(UnityResourceTeam), "Cranes")]
        [MenuItem(Teams + "Stacker Cranes", false, 33)]
        private static void StackerCranes(MenuCommand cmd) => Team(cmd, "Stacker Cranes", "stacker crane");

        [SimuLeanPresetIcon(Members + "IndustrialRobot_Gripper.prefab")]
        [SimuLeanVariantOf(typeof(UnityResourceTeam), "Robots")]
        [MenuItem(Teams + "Robots (Gripper)", false, 40)]
        private static void RobotsGripper(MenuCommand cmd) => Team(cmd, "Robots (Gripper)", "robot");

        [SimuLeanPresetIcon(Members + "IndustrialRobot_Vacuum.prefab")]
        [SimuLeanVariantOf(typeof(UnityResourceTeam), "Robots")]
        [MenuItem(Teams + "Robots (Vacuum)", false, 41)]
        private static void RobotsVacuum(MenuCommand cmd) => Team(cmd, "Robots (Vacuum)", "vacuum robot");

        [SimuLeanPresetIcon(Members + "IndustrialRobot_Welder.prefab")]
        [SimuLeanVariantOf(typeof(UnityResourceTeam), "Robots")]
        [MenuItem(Teams + "Robots (Welding)", false, 42)]
        private static void RobotsWelding(MenuCommand cmd) => Team(cmd, "Robots (Welding)", "welding robot");

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
            return go;   // registered for Undo when it is complete (Select): undo then removes it whole, nothing dangling
        }

        internal static T AddElement<T>(GameObject go) where T : SElement
        {
            var element = go.AddComponent<T>();
            if (go.GetComponent<SElementConnectionGizmo>() == null) go.AddComponent<SElementConnectionGizmo>();
            return element;
        }

        internal static void AddModel(GameObject go, params (string label, string path)[] models)
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

        // Realistic speeds (m/s): a person walking, a person pushing a load (bed, wheelchair, pallet truck), a robot's tool.
        private const float Walk = 1.2f, Push = 1.0f, Arm = 1.5f;

        // ------------------------------------------------------------------ team member kinds

        /// <summary>
        /// A kind of team member: its look (the member prefab), its realistic speed (m/s, Physical units) and
        /// the names an agent may give it. <see cref="MemberKinds"/> is the one table of member looks: the team presets,
        /// <c>simulean_add_team kind=…</c> and a team placed without a kind (<see cref="DefaultKind"/>, by its travel) all
        /// read it, so a team placed from the Library or by an agent is never shown as a box.
        /// </summary>
        public sealed class MemberKind
        {
            public string Name;
            /// <summary>Asset path of the member prefab (or a library model).</summary>
            public string Prefab;
            public float Speed;
            /// <summary>A 6-axis arm: the team gets a tool rest point and a (switched-off) safety fence.</summary>
            public bool Arm;
            public string[] Aliases;
            public GameObject Load() => AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);
        }

        private static MemberKind K(string name, string prefab, float speed, params string[] aliases)
            => new MemberKind { Name = name, Prefab = prefab.Contains("/") ? Models + prefab : Members + prefab, Speed = speed, Aliases = aliases };

        private static MemberKind Robot(string name, string prefab, params string[] aliases)
        {
            var k = K(name, prefab, Arm, aliases);
            k.Arm = true;
            return k;
        }

        /// <summary>The kinds of team member, by name (a kind is also found by an alias or a plural).</summary>
        public static readonly MemberKind[] MemberKinds =
        {
            K("operator", "Operator.prefab", Walk, "human", "person", "people", "worker", "staff", "walking"),
            K("doctor", "Doctor.prefab", Walk),
            K("nurse", "Nurse.prefab", Walk),
            K("fitter", "Fitter.prefab", Walk),
            K("welder", "Welder.prefab", Walk),
            K("painter", "Painter.prefab", Walk),
            K("outfitter", "Outfitter.prefab", Walk),
            K("bed porter", "BedPorter.prefab", Push, "porter"),
            K("stretcher porter", "StretcherPorter.prefab", Push),
            K("wheelchair porter", "WheelchairPorter.prefab", Push),
            K("agv", "AGV.prefab", 1.0f, "agv fleet", "amr", "vehicle", "automated guided vehicle"),
            K("forklift", "Forklift.prefab", 2.5f, "fork lift", "lift truck"),
            K("pallet truck", "PalletTruckOperator.prefab", Push, "pallet jack"),
            K("tugger", "TuggerTrain/TowTractor.fbx", 2.0f, "tugger train", "tow tractor", "tractor"),
            K("stacker crane", "StackerCrane.prefab", 2.0f, "asrs"),
            K("hall crane", "HallCrane.prefab", 1.0f, "crane", "overhead crane", "bridge crane"),
            K("goliath crane", "GoliathCrane.prefab", 0.6f, "gantry crane"),
            K("level luffing crane", "LevelLuffingCrane.prefab", 1.0f, "luffing crane"),
            K("spmt", "SPMT.prefab", 0.8f, "spmt transporter", "transporter"),
            K("welding gantry", "WeldingGantry.prefab", 0.5f),
            Robot("robot", "IndustrialRobot_Gripper.prefab", "gripper robot", "robot (gripper)", "arm"),
            Robot("vacuum robot", "IndustrialRobot_Vacuum.prefab", "robot (vacuum)"),
            Robot("welding robot", "IndustrialRobot_Welder.prefab", "robot (welding)"),
        };

        // roles of people with no member kind of their own: the did-you-mean of KindOf points them to 'operator'
        private static readonly string[] PersonRoles = { "setter", "mechanic", "technician", "maintainer", "maintenance", "repair",
                                                         "inspector", "engineer", "electrician", "machinist", "driver", "picker", "packer", "assembler" };

        private static string NormKind(string t) => t.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');

        /// <summary>The kind named <paramref name="kind"/> (its name, an alias or a plural; any case, '-' / '_' as spaces);
        /// null for an empty text; an <see cref="ArgumentException"/> listing the kinds for an unknown one.</summary>
        public static MemberKind KindOf(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind)) return null;
            var found = TryKindOf(kind);
            if (found != null) return found;
            var names = Array.ConvertAll(MemberKinds, m => m.Name);
            var options = new System.Collections.Generic.List<string>(names);
            foreach (var m in MemberKinds) options.AddRange(m.Aliases);
            string hint = SimuLean.Serialization.NameSuggestions.DidYouMean(kind, options);
            // a person's role that is no kind of its own (setters, mechanics) walks as an operator
            if (hint.Length == 0 && Array.Exists(PersonRoles, r => NormKind(kind).StartsWith(r, StringComparison.Ordinal)))
                hint = " A person who walks to the job (setter, mechanic, technician…) is kind 'operator' (1.2 m/s).";
            throw new ArgumentException($"kind: '{kind}' is not a team member kind ({string.Join(", ", names)}).{hint}");
        }

        /// <summary>The kind named <paramref name="text"/> as <see cref="KindOf"/>, or null (no error) — e.g. a team named
        /// after its kind ('AGVs', 'Forklifts', 'Operators').</summary>
        public static MemberKind TryKindOf(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string k = NormKind(text);
            foreach (string candidate in new[] { k, k.EndsWith("s") ? k.Substring(0, k.Length - 1) : null })
            {
                if (candidate == null) continue;
                foreach (var m in MemberKinds)
                    if (NormKind(m.Name) == candidate || Array.Exists(m.Aliases, a => NormKind(a) == candidate)) return m;
            }
            return null;
        }

        /// <summary>
        /// The look of a team placed without a kind, by its travel (resources are seen in 3D by default):
        /// vehicles on the transport network (Network) are AGVs, a team moving by axes is a hall crane, any other team
        /// (Straight, Grid, Matrix, Unity physics, Abstract — people walking or standing) is an operator.
        /// </summary>
        public static MemberKind DefaultKind(TeamTravel travel)
            => travel == TeamTravel.Network ? KindOf("agv") : travel == TeamTravel.Axes ? KindOf("hall crane") : KindOf("operator");

        /// <summary>The look of <paramref name="kind"/> on <paramref name="team"/>: its member prefab only (the travel, speed
        /// and units stay the team's). Warns when the prefab is missing.</summary>
        public static void ApplyLook(UnityResourceTeam team, MemberKind kind)
        {
            if (kind == null) return;
            team.memberPrefab = kind.Load();
            if (team.memberPrefab == null) Debug.LogWarning($"[SimuLean] Member prefab {kind.Prefab} not found.");
        }

        /// <summary>The whole kind on <paramref name="team"/>: its look, its realistic speed in m/s (Physical units) and, for
        /// an arm, the tool rest point and the safety fence (undoable parts; the team is registered for undo already).</summary>
        public static void ApplyKind(UnityResourceTeam team, MemberKind kind)
        {
            if (kind == null) return;
            ApplyLook(team, kind);
            team.speed = kind.Speed;
            team.units = SimuLean.Serialization.ResourceUnits.Physical;
            if (kind.Arm) AddArmCell(team);
        }

        /// <summary>
        /// A resource team of <paramref name="kind"/> (<see cref="MemberKinds"/>): its members look like the kind's prefab and
        /// travel in straight lines between the elements they serve at the kind's speed (m/s, Physical units, as
        /// mobile resources). A robot team gets its tool rest point and fence.
        /// </summary>
        public static GameObject Team(MenuCommand cmd, string name, string kind)
        {
            var k = KindOf(kind) ?? throw new ArgumentException("A team preset needs a member kind.");
            var go = NewObject(cmd, name);
            var team = go.AddComponent<UnityResourceTeam>();   // its name is the GameObject's (one name)
            team.travel = TeamTravel.Straight;
            ApplyLook(team, k);
            team.speed = k.Speed;
            team.units = SimuLean.Serialization.ResourceUnits.Physical;
            Select(go);
            if (k.Arm) AddArmCell(team);
            Selection.activeObject = go;
            return go;
        }

        /// <summary>A robot team: the arm stands at the team's position, its tool rests at the <c>Home</c> point above and in
        /// front of it and goes to each station's work socket; a safety fence round it can be switched on
        /// (<see cref="RobotCellFence"/>, off by default).</summary>
        private static void AddArmCell(UnityResourceTeam team)
        {
            var go = team.gameObject;
            if (team.homeNode == null)
            {
                var home = new GameObject("Home");
                home.transform.SetParent(go.transform, false);
                home.transform.localPosition = new Vector3(0f, 1.4f, 1.0f);
                home.AddComponent<UnityTransportNode>();
                Undo.RegisterCreatedObjectUndo(home, "Create " + go.name);   // the team is registered already
                team.homeNode = home.GetComponent<UnityTransportNode>();
            }
            if (go.GetComponent<RobotCellFence>() == null) Undo.AddComponent<RobotCellFence>(go);
        }

        // last: adding an element can move the selection (its hierarchy group), and the window's Place takes the
        // selected object as the one created
        // The preset is complete: its creation (components, model, children) is one undo step, then it is selected.
        // Registered before its parts are added, or undoing it would leave them dangling.
        private static void Select(GameObject go)
        {
            Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);
            Selection.activeObject = go;
        }

        private static GameObject Load(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(Models + path);
    }
}
