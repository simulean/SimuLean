using System.Collections.Generic;
using SimuLean.Collections;
using SimuLean.Data;
using SimuLean.Naval;
using SimuLean.Unity.Naval;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor.Naval
{
    /// <summary>
    /// The naval library's ready-made objects (GameObject → SimuLean → Shipbuilding, the window's Presets), built on the
    /// existing elements: the shop stations of a block process (plate cutting, panel line, sub-assembly,
    /// sub-block welding, turning, block assembly, outfitting, blasting and painting), the yards, the erection docks and
    /// the launch, each visible in the scene when placed. The <i>Panel Line</i> and the <i>Block Process (IJPR)</i> are
    /// composite presets: several elements placed already connected under one parent. The block workspace stands in for
    /// the station models still to come (N6).
    /// </summary>
    public static class NavalPresets
    {
        private const string Menu = "GameObject/SimuLean/Shipbuilding/";
        private const string Workspace = "BlockWorkspace/BlockWorkspace.fbx";
        private const string PaintBooth = "PaintBooth/PaintBooth.fbx";
        private const string Workstation = "WorkstationProcessor/WorkstationProcessor.fbx";
        private const string Positioner = "WeldPositioner/WeldPositioner.fbx";
        private const string RackBay = "ASRS/Rack_Bay.fbx";
        private const string SinkModel = "SinkStation/SinkStation.fbx";
        private const string Icon = LibraryPresets.Models + Workspace;
        private const string ShipItems = "Assets/SimuLean.Unity/Prefabs/Items/Shipbuilding/";

        /// <summary>The project database table the IJPR composite reads (the generic sample hull when it is missing).</summary>
        public const string SampleHullTable = "sample_hull";

        private static readonly (string, string) WorkspaceModel = ("Block workspace", Workspace);

        // ------------------------------------------------------------------ steel preparation

        [SimuLeanPresetIcon(LibraryPresets.Models + RackBay)]
        [MenuItem(Menu + "Steel Stockyard", false, 1)]
        private static void SteelStockyard(MenuCommand cmd)
        {
            var go = new GameObject("Steel Stockyard");
            GameObjectUtility.SetParentAndAlign(go, cmd?.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Steel Stockyard");
            var yard = go.AddComponent<UnityStorageRack>();
            if (go.GetComponent<SElementConnectionGizmo>() == null) go.AddComponent<SElementConnectionGizmo>();
            yard.style = StorageRackStyle.FloorStorage;
            yard.bays = 4; yard.slotsPerBay = 2; yard.stackHeight = 6;
            yard.bayWidth = 3.2f; yard.slotWidth = 3f; yard.slotDepth = 12.5f; yard.stackPitch = 0.1f;
            yard.Rebuild();
            Selection.activeObject = go;
        }

        [SimuLeanPresetIcon(LibraryPresets.Models + RackBay)]
        [MenuItem(Menu + "Profile Rack", false, 2)]
        private static void ProfileRack(MenuCommand cmd) =>
            LibraryPresets.Station<UnityQueue>(cmd, "Profile Rack", q => { q.capacity = 40; q.releaseMode = ReleaseMode.AnyAccepted; },
                                               ("Profile rack", RackBay));

        [SimuLeanPresetIcon(LibraryPresets.Models + Workstation)]
        [MenuItem(Menu + "Plate Cutting", false, 3)]
        private static void PlateCutting(MenuCommand cmd) =>
            LibraryPresets.Station<UnityMultiServer>(cmd, "Plate Cutting", s => Time(s.delayStrategy, "has(t_cut) ? t_cut : 2"),
                                                     ("Cutting table", Workstation));

        [SimuLeanPresetIcon(LibraryPresets.Models + Positioner)]
        [MenuItem(Menu + "Panel Line", false, 4)]
        private static void PanelLine(MenuCommand cmd)
        {
            var line = Group(cmd, "Panel Line");
            SElement previous = null;
            string[] stations = { "Edge Preparation", "Butt Welding", "Stiffener Fitting", "Fillet Welding" };
            for (int i = 0; i < stations.Length; i++)
            {
                var s = Place<UnityMultiServer>(line, stations[i], new Vector3(i * 8f, 0, 0),
                                                m => Time(m.delayStrategy, "has(t_panel) ? t_panel : 3"),
                                                i % 2 == 0 ? ("Panel station", Workstation) : ("Welding gantry", Positioner));
                Connect(previous, s);
                previous = s;
            }
            Selection.activeObject = line;
        }

        // ------------------------------------------------------------------ sub-blocks and blocks

        [SimuLeanPresetIcon(Icon)]
        [MenuItem(Menu + "Sub-Assembly Cell", false, 10)]
        private static void SubAssemblyCell(MenuCommand cmd) =>
            LibraryPresets.Station<UnityMultiServer>(cmd, "Sub-Assembly Cell", SubAssembly, WorkspaceModel);

        [SimuLeanPresetIcon(LibraryPresets.Models + Positioner)]
        [MenuItem(Menu + "Sub-Block Welding", false, 11)]
        private static void SubBlockWelding(MenuCommand cmd) =>
            LibraryPresets.Station<UnityCombiner>(cmd, "Sub-Block Welding", c =>
            {
                c.requirements = new[] { 1 };                       // the panel (main) + its profile kit
                Time(c.delayStrategy, "has(t_sbw) ? t_sbw : 6");
                RouteOnTurn(c);
                c.processBy.requirements.Add(Skill(1, "welder"));
            }, WorkspaceModel, ("Weld positioner", Positioner));

        [SimuLeanPresetIcon(LibraryPresets.Models + "ShipBlock/ShipBlockCurved.fbx")]
        [MenuItem(Menu + "Turning Station", false, 12)]
        private static void TurningStation(MenuCommand cmd) =>
            Turning(LibraryPresets.Station<UnityMultiServer>(cmd, "Turning Station", s => Time(s.delayStrategy, "2"), WorkspaceModel));

        [SimuLeanPresetIcon(Icon)]
        [MenuItem(Menu + "Sub-Block Yard", false, 13)]
        private static void SubBlockYard(MenuCommand cmd) =>
            LibraryPresets.Station<UnityQueue>(cmd, "Sub-Block Yard", q => Yard(q, 8), WorkspaceModel);

        [SimuLeanPresetIcon(Icon)]
        [MenuItem(Menu + "Block Assembly Bay", false, 14)]
        private static void BlockAssemblyBay(MenuCommand cmd) =>
            LibraryPresets.Station<UnityCombiner>(cmd, "Block Assembly Bay", c => BlockBays(c, 3), WorkspaceModel);

        [SimuLeanPresetIcon(Icon)]
        [MenuItem(Menu + "Outfitting Station", false, 15)]
        private static void OutfittingStation(MenuCommand cmd) =>
            LibraryPresets.Station<UnityMultiServer>(cmd, "Outfitting Station", s => { s.capacity = 2; Time(s.delayStrategy, "has(t_out) ? t_out : 8"); },
                                                     WorkspaceModel);

        [SimuLeanPresetIcon(LibraryPresets.Models + PaintBooth)]
        [MenuItem(Menu + "Blasting & Painting Hall", false, 16)]
        private static void PaintingHall(MenuCommand cmd) =>
            LibraryPresets.Station<UnityMultiServer>(cmd, "Blasting & Painting Hall", Painting, ("Paint hall", PaintBooth));

        [SimuLeanPresetIcon(Icon)]
        [MenuItem(Menu + "Block Yard", false, 17)]
        private static void BlockYard(MenuCommand cmd) =>
            LibraryPresets.Station<UnityQueue>(cmd, "Block Yard", q => Yard(q, 6), WorkspaceModel);

        // ------------------------------------------------------------------ erection and launch

        [SimuLeanPresetIcon(Icon)]
        [MenuItem(Menu + "Slipway", false, 20)]
        private static void Slipway(MenuCommand cmd) =>
            LibraryPresets.Station<UnityErectionDock>(cmd, "Slipway", d => { d.erectionRule = ErectionDock.ErectionRule.Sequence; d.capacity = 1; },
                                                     WorkspaceModel);

        [SimuLeanPresetIcon(Icon)]
        [MenuItem(Menu + "Building Dock", false, 21)]
        private static void BuildingDock(MenuCommand cmd) =>
            LibraryPresets.Station<UnityErectionDock>(cmd, "Building Dock", d => { d.erectionRule = ErectionDock.ErectionRule.Precedence; d.capacity = 2; },
                                                     WorkspaceModel);

        [SimuLeanPresetIcon(LibraryPresets.Models + SinkModel)]
        [MenuItem(Menu + "Launch", false, 22)]
        private static void Launch(MenuCommand cmd) =>
            LibraryPresets.Station<UnitySink>(cmd, "Launch", s => s.keepItems = true, ("Launch", SinkModel));

        // ------------------------------------------------------------------ teams (resources of the yard, N4)

        private const string Teams = Menu + "Teams/";

        [SimuLeanPresetIcon(LibraryPresets.Members + "GoliathCrane.prefab")]
        [MenuItem(Teams + "Goliath Crane", false, 60)]
        private static void GoliathCrane(MenuCommand cmd) => LibraryPresets.Team(cmd, "Goliath Crane", "GoliathCrane.prefab");

        [SimuLeanPresetIcon(LibraryPresets.Models + "OverheadCrane/Trolley.fbx")]
        [MenuItem("GameObject/SimuLean/Teams/Hall Crane", false, 50)]   // a general crane: core resources, not the naval pack
        private static void HallCrane(MenuCommand cmd) => LibraryPresets.Team(cmd, "Hall Crane", "HallCrane.prefab");

        [SimuLeanPresetIcon(LibraryPresets.Members + "LevelLuffingCrane.prefab")]
        [MenuItem(Teams + "Level-Luffing Crane", false, 62)]
        private static void LuffingCrane(MenuCommand cmd) => LibraryPresets.Team(cmd, "Level-Luffing Crane", "LevelLuffingCrane.prefab");

        [SimuLeanPresetIcon(LibraryPresets.Members + "SPMT.prefab")]
        [MenuItem(Teams + "SPMT Transporters", false, 63)]
        private static void Spmt(MenuCommand cmd) => LibraryPresets.Team(cmd, "SPMT Transporters", "SPMT.prefab");

        [SimuLeanPresetIcon(LibraryPresets.Members + "WeldingGantry.prefab")]
        [MenuItem(Teams + "Welding Gantry", false, 64)]
        private static void WeldingGantry(MenuCommand cmd) => LibraryPresets.Team(cmd, "Welding Gantry", "WeldingGantry.prefab");

        [SimuLeanPresetIcon(LibraryPresets.Members + "IndustrialRobot_Welder.prefab")]
        [MenuItem(Teams + "Welding Robots", false, 65)]
        private static void WeldingRobots(MenuCommand cmd) => LibraryPresets.Team(cmd, "Welding Robots", "IndustrialRobot_Welder.prefab");

        // ------------------------------------------------------------------ the IJPR block process (composite)

        /// <summary>
        /// The whole block process of the naval library (the flow of <c>ReferenceModels.NavalBlockProcess</c>): the
        /// breakdown tool feeding the kits and block orders, sub-assembly → turning / sub-block yard → block bays →
        /// outfitting → painting → block yard → slipway → launch, and the teams (fitters, welders, painters, crane). It
        /// reads the project database's <see cref="SampleHullTable"/>, which is filled with the generic sample hull when
        /// missing; point the breakdown at your own table to model a ship.
        /// </summary>
        [SimuLeanPresetIcon(LibraryPresets.Models + "ShipBlock/ShipBlockFlat.fbx")]
        [MenuItem(Menu + "Block Process (IJPR)", false, 40)]
        private static void BlockProcess(MenuCommand cmd) => BlockProcess(cmd, null);

        /// <summary>The composite with its breakdown on <paramref name="databasePath"/> (null = the project database).</summary>
        public static GameObject BlockProcess(MenuCommand cmd, string databasePath)
        {
            var root = Group(cmd, "Block Process");
            float x = 0;
            Vector3 Next(float step = 22f) { var p = new Vector3(x, 0, 0); x += step; return p; }

            var kits = Place<UnitySource>(root, "Sub-Block Kits", new Vector3(-14, 0, 0), s => s.itemPrefab = Item("PlateKit"),
                                          ("Source", "SourceStation/SourceStation.fbx"));
            var orders = Place<UnitySource>(root, "Block Orders", new Vector3(44, 0, 16), s => s.itemPrefab = Item("BlockOrder"),
                                            ("Source", "SourceStation/SourceStation.fbx"));
            var sba = Place<UnityMultiServer>(root, "Sub-Assembly", Next(), m => { SubAssembly(m); m.capacity = 2; }, WorkspaceModel);
            var turning = Place<UnityMultiServer>(root, "Turning", new Vector3(x - 11, 0, -24), m => Time(m.delayStrategy, "2"), WorkspaceModel);
            Turning(turning.gameObject);
            var subYard = Place<UnityQueue>(root, "Sub-Block Yard", Next(), q => Yard(q, 8), WorkspaceModel);
            var bays = Place<UnityCombiner>(root, "Block Assembly Bays", Next(), c => BlockBays(c, 2), WorkspaceModel);
            var outfitting = Place<UnityMultiServer>(root, "Outfitting", Next(), m => { m.capacity = 2; Time(m.delayStrategy, "8"); }, WorkspaceModel);
            var painting = Place<UnityMultiServer>(root, "Painting", Next(), m => { Painting(m); Time(m.delayStrategy, "10"); }, ("Paint hall", PaintBooth));
            var blockYard = Place<UnityQueue>(root, "Block Yard", Next(), q => Yard(q, 6), WorkspaceModel);
            var slipway = Place<UnityErectionDock>(root, "Slipway", Next(26f), d =>
            {
                d.erectionRule = ErectionDock.ErectionRule.Sequence;
                d.capacity = 1;
                Time(d.delayStrategy, "4 + 0.02 * weight");
            }, WorkspaceModel);
            var launch = Place<UnitySink>(root, "Launch", Next(), s => s.keepItems = true, ("Launch", SinkModel));

            Connect(kits, sba);
            Connect(sba, turning);                                      // output 0: the turned sub-blocks
            Connect(sba, subYard);                                      // output 1
            sba.outputStrategy.strategyType = OutputStrategyType.Expression;
            sba.outputStrategy.routingExpression = "item.turn == 1 ? 0 : 1";
            Connect(turning, subYard);
            Connect(orders, bays);
            Connect(subYard, bays);
            bays.inputOrder = new List<SElement> { orders, subYard };   // main = the block order
            Connect(bays, outfitting);
            Connect(outfitting, painting);
            Connect(painting, blockYard);
            Connect(blockYard, slipway);
            Connect(slipway, launch);

            var fitters = Team(root, "Fitters", "Fitter.prefab", 2, "fitter");
            var welders = Team(root, "Welders", "Welder.prefab", 4, "welder");
            var painters = Team(root, "Painters", "Painter.prefab", 1, "painter");
            var crane = Team(root, "Slipway Crane", "LevelLuffingCrane.prefab", 1, null);
            // each crew starts beside the station it works at (the team's transform is its members' spawn point)
            fitters.transform.localPosition = sba.transform.localPosition + new Vector3(0, 0, -12);
            welders.transform.localPosition = bays.transform.localPosition + new Vector3(0, 0, -12);
            painters.transform.localPosition = painting.transform.localPosition + new Vector3(0, 0, -6);
            crane.transform.localPosition = slipway.transform.localPosition + new Vector3(0, 0, 14);
            sba.processBy.team = fitters;
            bays.processBy.team = welders;
            bays.processBy.requirements.Clear();
            bays.processBy.requirements.Add(Skill(2, "welder"));
            painting.processBy.team = painters;
            slipway.processBy.team = crane;
            slipway.processBy.holdFor = Constant(1);

            var breakdown = new GameObject("Block Breakdown");
            Undo.RegisterCreatedObjectUndo(breakdown, "Create Block Breakdown");
            breakdown.transform.SetParent(root.transform, false);
            var bd = breakdown.AddComponent<UnityBlockBreakdown>();
            bd.table = SampleHullTable;
            bd.databasePath = databasePath ?? "";
            bd.blockInterval = 12;
            bd.subBlockSource = kits;
            bd.blockOrderSource = orders;
            EnsureSampleHull(bd.DatabasePath());
            bd.ApplyToSources();
            slipway.expectedBlocks = SafeBlockCount(bd);

            Selection.activeObject = root;
            return root;
        }

        /// <summary>Writes the generic sample hull to the project database's <see cref="SampleHullTable"/> when it is not
        /// there yet (an existing table is never overwritten).</summary>
        public static bool EnsureSampleHull(string databasePath)
        {
            try
            {
                var db = ProjectDatabaseRegistry.GetOrOpen(databasePath);
                if (db.TableExists(SampleHullTable)) return false;
                SimuLean.Testing.ReferenceModels.SampleHull().Save(db, SampleHullTable);
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[SimuLean] Sample hull not written to {databasePath}: {ex.Message}");
                return false;
            }
        }

        // ------------------------------------------------------------------ behaviour shared by the presets

        /// <summary>The library's ship items (Prefabs/Items/Shipbuilding): PlateKit, SteelPlate, ProfileBundle, SubBlock,
        /// Block, BlockOrder.</summary>
        private static GameObject Item(string name) => AssetDatabase.LoadAssetAtPath<GameObject>(ShipItems + name + ".prefab");

        // the item rolls over while it is turned
        private static void Turning(GameObject station)
        {
            if (station.GetComponent<BlockTurnView>() == null) station.AddComponent<BlockTurnView>();
        }

        private static void SubAssembly(UnityMultiServer s)
        {
            s.outputItemPrefab = Item("SubBlock");                      // the kit leaves as a sub-block (its labels size it)
            s.capacity = 3;
            Time(s.delayStrategy, "has(t_sba) ? t_sba : 6");
            s.processBy.requirements.Add(Skill(1, "fitter"));
        }

        private static void Painting(UnityMultiServer s)
        {
            s.capacity = 1;
            Time(s.delayStrategy, "has(t_paint) ? t_paint : 10");
            s.processBy.holdFor = Constant(6);                          // painters for the painting part, not the curing
        }

        private static void BlockBays(UnityCombiner c, int bays)
        {
            c.visualMode = UnityCombiner.VisualMode.NewItem;            // the order and its sub-blocks become the block
            c.combinedItemPrefab = Item("Block");
            c.capacity = bays;
            c.requirements = new[] { 1 };
            c.pullModeType = UnityCombiner.PullModeType.SingleLabel;    // a block's own sub-blocks
            c.singleLabelName = "block";
            c.singleLabelValue = "";
            c.updateRequirements = true;                                // as many as the order's nsub
            c.updateLabels = new[] { "nsub" };
            Time(c.delayStrategy, "has(t_ba) ? t_ba : 16");
        }

        private static void Yard(UnityQueue q, int places)
        {
            q.capacity = places;
            q.releaseMode = ReleaseMode.AnyAccepted;                    // whichever block the next station takes
        }

        private static void RouteOnTurn(SElement e)
        {
            e.outputStrategy.strategyType = OutputStrategyType.Expression;
            e.outputStrategy.routingExpression = "item.turn == 1 ? 0 : 1";
        }

        private static void Time(RandomProcessConfig r, string expression)
        {
            r.strategyType = RandomProcessType.Expression;
            r.expression = expression;
        }

        private static RandomProcessConfig Constant(float value) =>
            new RandomProcessConfig { strategyType = RandomProcessType.Constant, constantValue = value };

        private static ResourceRequirementSpec Skill(int count, string skill)
        {
            var r = new ResourceRequirementSpec { count = count, label = skill };
            r.query.conditions.Add(new ListCondition("skill", ListOp.Equal, skill));
            return r;
        }

        private static int SafeBlockCount(UnityBlockBreakdown bd)
        {
            try { return bd.Load().BlockCount; }
            catch (System.Exception) { return 0; }
        }

        private static GameObject Group(MenuCommand cmd, string name)
        {
            var go = new GameObject(name);
            GameObjectUtility.SetParentAndAlign(go, cmd?.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            return go;
        }

        private static T Place<T>(GameObject parent, string name, Vector3 at, System.Action<T> setup, params (string, string)[] models)
            where T : SElement
        {
            var go = LibraryPresets.Station(new MenuCommand(parent), name, setup, models);
            go.transform.localPosition = at;
            return go.GetComponent<T>();
        }

        private static void Connect(SElement from, SElement to)
        {
            if (from == null || to == null) return;
            if (from.nextElements == null) from.nextElements = new List<SElement>();
            from.nextElements.Add(to);
        }

        private static UnityResourceTeam Team(GameObject parent, string name, string memberPrefab, int members, string skill)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(parent.transform, false);
            var team = go.AddComponent<UnityResourceTeam>();
            team.teamName = name;
            team.memberPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(LibraryPresets.Members + memberPrefab);
            team.AddMembers(members);
            if (skill != null)
                foreach (var m in team.members) m.labels.Add(new LabelAssignment { label = "skill", value = skill });
            return team;
        }
    }
}
