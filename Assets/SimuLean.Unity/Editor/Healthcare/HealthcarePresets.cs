using System.Collections.Generic;
using SimuLean.Resources;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor.Healthcare
{
    /// <summary>
    /// The healthcare library's ready-made objects (GameObject → SimuLean → Healthcare, the window's
    /// Presets), built on the existing elements: each preset is an element with its
    /// default behaviour and its look — a waiting list at home is a Queue drawn as a count, a home test a Delay with the
    /// virtual look, a consultation room a Multi-Server in the consultation box — plus the <see cref="UnityAgenda"/>. The
    /// <i>Outpatient Clinic</i> and the <i>Home Diagnostic Test</i> are composite presets: several elements placed already
    /// connected under one parent. Times are in model time units (set the clock's unit, e.g. hours).
    /// </summary>
    public static class HealthcarePresets
    {
        private const string Menu = "GameObject/SimuLean/Healthcare/";
        private const string Models = LibraryPresets.Models;
        private const string Consultation = "ConsultationBox/ConsultationBox.fbx";
        private const string Desk = "ReceptionDesk/ReceptionDesk.fbx";
        private const string Door = "HospitalDoor/HospitalDoor.fbx";   // entrances and exits: the hospital door

        // ------------------------------------------------------------------ arrivals and booking

        [SimuLeanPresetIcon(LibraryPresets.Models + "HospitalPeople/Patient.fbx")]
        [MenuItem(Menu + "Referrals", false, 1)]
        private static void ReferralsMenu(MenuCommand cmd) => Referrals(cmd, "Referrals");

        [SimuLeanPresetIcon(Models + Desk)]
        [MenuItem(Menu + "Clinic Agenda", false, 2)]
        private static void AgendaMenu(MenuCommand cmd) => Agenda(cmd, "Agenda");

        [SimuLeanPresetIcon(LibraryPresets.Models + "HospitalPeople/Patient.fbx")]
        [MenuItem(Menu + "Waiting List (at home)", false, 3)]
        private static void WaitingListMenu(MenuCommand cmd) => WaitingList(cmd, "Waiting List");

        // ------------------------------------------------------------------ waiting areas

        [SimuLeanPresetIcon(Models + "WaitingChairs/Seat_Row.fbx")]
        [MenuItem(Menu + "Waiting Room", false, 10)]
        private static void WaitingRoomMenu(MenuCommand cmd) => WaitingRoom(cmd, "Waiting Room", 6);

        [SimuLeanPresetIcon(LibraryPresets.Models + "HospitalPeople/Patient.fbx")]
        [MenuItem(Menu + "Waiting Line", false, 11)]
        private static void WaitingLineMenu(MenuCommand cmd) => WaitingLine(cmd, "Waiting Line");

        // ------------------------------------------------------------------ stations

        [SimuLeanPresetIcon(Models + Desk)]
        [MenuItem(Menu + "Reception Desk", false, 20)]
        private static void Reception(MenuCommand cmd) =>
            Sign(LibraryPresets.Station<UnityMultiServer>(cmd, "Reception Desk", s => s.capacity = 2, ("Reception desk", Desk)));

        [SimuLeanPresetIcon(Models + Consultation)]
        [MenuItem(Menu + "Consultation Room", false, 21)]
        private static void ConsultationMenu(MenuCommand cmd) => ConsultationRoom(cmd, "Consultation Room");

        [SimuLeanPresetIcon(Models + Consultation)]
        [MenuItem(Menu + "Home Test", false, 22)]
        private static void HomeTestMenu(MenuCommand cmd) => Virtual<UnityDelay>(cmd, "Home Test", "Home test (night at home)",
            d => Constant(d.delayStrategy, 24));

        [SimuLeanPresetIcon(Models + Consultation)]
        [MenuItem(Menu + "Report (not face to face)", false, 23)]
        private static void ReportMenu(MenuCommand cmd) => Virtual<UnityMultiServer>(cmd, "Report", "Report — not face to face",
            s => { s.capacity = 1; Constant(s.delayStrategy, 0.5f); });

        [SimuLeanPresetIcon(Models + Consultation)]
        [MenuItem(Menu + "Treatment at Home", false, 24)]
        private static void TreatmentMenu(MenuCommand cmd) => Virtual<UnityDelay>(cmd, "Treatment at Home", "Treatment at home",
            d => Constant(d.delayStrategy, 2160));

        [SimuLeanPresetIcon(Models + Consultation)]
        [MenuItem(Menu + "Triage", false, 25)]
        private static void TriageMenu(MenuCommand cmd) => Triage(cmd, "Triage");

        [SimuLeanPresetIcon(Models + Consultation)]
        [MenuItem(Menu + "Diagnostic Test (hospital)", false, 26)]
        private static void DiagnosticTestMenu(MenuCommand cmd) => DiagnosticTest(cmd, "Diagnostic Test");

        [SimuLeanPresetIcon(Models + Door)]
        [MenuItem(Menu + "Discharge", false, 30)]
        private static void DischargeMenu(MenuCommand cmd) => Discharge(cmd, "Discharge");

        // ------------------------------------------------------------------ equipment
        // (staff: the Operators team with the Doctor / Nurse look — a role is a look of the operator, not a preset)

        [SimuLeanPresetIcon(Models + Consultation)]
        [MenuItem(Menu + "Equipment Pool", false, 42)]
        private static void EquipmentMenu(MenuCommand cmd) => EquipmentPool(cmd, "Equipment Pool", 3);

        // ------------------------------------------------------------------ dashboard

        [SimuLeanPresetIcon(Models + Desk)]
        [MenuItem(Menu + "Healthcare Dashboard", false, 50)]
        private static void DashboardMenu(MenuCommand cmd) => Dashboard(cmd);

        /// <summary>The library's healthcare charts (created once in <see cref="ChartFolder"/>): patients waiting per list,
        /// time in system (P90) at the discharges, agenda slots booked, lead time to the appointment. They find the
        /// elements by name (the presets' names: *Waiting*, *Agenda*, *Line*, *List*, *Discharge*).</summary>
        public const string ChartFolder = "Assets/SimuLean.Unity/Charts/Healthcare";

        public static List<ChartDefinition> HealthcareCharts()
        {
            var specs = new (string file, string title, string stat, string objects)[]
            {
                ("PatientsWaiting", "Patients waiting", "content", "*Waiting*;*Agenda*;*Line*;*List*"),
                ("TimeInSystemP90", "Time in system — P90", "tally:time_in_system:p90", "*Discharge*"),
                ("AgendaSlotsBooked", "Agenda slots booked", "slots_booked", "*Agenda*"),
                ("LeadTime", "Lead time to the appointment", "lead_time_avg", "*Agenda*"),
            };
            if (!AssetDatabase.IsValidFolder(ChartFolder)) AssetDatabase.CreateFolder("Assets/SimuLean.Unity/Charts", "Healthcare");
            var list = new List<ChartDefinition>();
            foreach (var (file, title, stat, objects) in specs)
            {
                string path = $"{ChartFolder}/{file}.asset";
                var chart = AssetDatabase.LoadAssetAtPath<ChartDefinition>(path);
                if (chart == null)
                {
                    chart = ScriptableObject.CreateInstance<ChartDefinition>();
                    chart.spec = new SimuLean.Results.Charts.ChartQuery
                    {
                        Title = title, Kind = SimuLean.Results.Charts.ChartKind.Bar,
                        Y = new SimuLean.Results.Charts.ChartValue(SimuLean.Results.Charts.ChartSource.Statistic, stat, "element", objects),
                        GroupBy = SimuLean.Results.Charts.ChartGroup.Object, ValueLabels = true,
                    };
                    AssetDatabase.CreateAsset(chart, path);
                }
                list.Add(chart);
            }
            AssetDatabase.SaveAssets();
            return list;
        }

        /// <summary>A live dashboard with the healthcare charts (top right).</summary>
        public static GameObject Dashboard(MenuCommand cmd)
        {
            var go = Group(cmd, "Healthcare Dashboard");
            var d = go.AddComponent<UnityDashboard>();
            d.charts = HealthcareCharts();
            d.corner = UnityDashboard.Corner.TopRight;
            d.columns = 2;
            d.refreshSeconds = 2f;
            Selection.activeObject = go;
            return go;
        }

        // ------------------------------------------------------------------ decoration (no element: the hospital around the model)

        [SimuLeanPresetIcon(Models + "WaitingChairs/Seat_Table.fbx")]
        [MenuItem(Menu + "Decor/Floor Zone", false, 80)]
        private static void FloorZoneMenu(MenuCommand cmd) => FloorZone(cmd, "Waiting area", new Vector2(6f, 4f));

        [SimuLeanPresetIcon(Models + "HospitalBed/HospitalBed.fbx")]
        [MenuItem(Menu + "Decor/Hospital Bed", false, 81)]
        private static void BedMenu(MenuCommand cmd) => Decor(cmd, "Hospital Bed", "HospitalBed/HospitalBed.fbx");

        [SimuLeanPresetIcon(Models + "Stretcher/Stretcher.fbx")]
        [MenuItem(Menu + "Decor/Stretcher", false, 82)]
        private static void StretcherMenu(MenuCommand cmd) => Decor(cmd, "Stretcher", "Stretcher/Stretcher.fbx");

        [SimuLeanPresetIcon(Models + "Wheelchair/Wheelchair.fbx")]
        [MenuItem(Menu + "Decor/Wheelchair", false, 83)]
        private static void WheelchairMenu(MenuCommand cmd) => Decor(cmd, "Wheelchair", "Wheelchair/Wheelchair.fbx");

        [SimuLeanPresetIcon(Models + "WaitingChairs/Seat_Table.fbx")]
        [MenuItem(Menu + "Decor/Side Table", false, 84)]
        private static void TableMenu(MenuCommand cmd) => Decor(cmd, "Side Table", "WaitingChairs/Seat_Table.fbx");

        [SimuLeanPresetIcon(Models + "HospitalWall/HWall_Panel.fbx")]
        [MenuItem(Menu + "Decor/Partition Wall", false, 85)]
        private static void PartitionMenu(MenuCommand cmd) => Wall(cmd, "Partition Wall", 6f, Panel, null);

        [SimuLeanPresetIcon(Models + "HospitalWall/HWall_Glass.fbx")]
        [MenuItem(Menu + "Decor/Glass Partition", false, 86)]
        private static void GlassMenu(MenuCommand cmd) => Wall(cmd, "Glass Partition", 6f, GlassPanel, null);

        [SimuLeanPresetIcon(Models + "HospitalWall/HWall_Door.fbx")]
        [MenuItem(Menu + "Decor/Wall with Door", false, 87)]
        private static void DoorWallMenu(MenuCommand cmd) => Wall(cmd, "Wall with Door", 5f, Panel, DoorPanel);

        [SimuLeanPresetIcon(Models + "HospitalWall/HWall_Door.fbx")]
        [MenuItem(Menu + "Decor/Room Walls", false, 88)]
        private static void RoomMenu(MenuCommand cmd) => RoomWalls(cmd, "Room", 6f, 4f);

        private const string Panel = "HospitalWall/HWall_Panel.fbx", GlassPanel = "HospitalWall/HWall_Glass.fbx",
                             DoorPanel = "HospitalWall/HWall_Door.fbx", WallEnd = "HospitalWall/HWall_End.fbx";

        /// <summary>A run of the hospital wing kit (cut-away partitions, 1 m pieces) along its local +Z, with an optional
        /// centre piece (a door).</summary>
        public static GameObject Wall(MenuCommand cmd, string name, float length, string segment, string center)
        {
            var go = Group(cmd, name);
            var strip = go.AddComponent<UnityModularStrip>();
            strip.segmentLength = 1f;
            strip.length = length;
            strip.segment = Load(segment);
            strip.cap = Load(WallEnd);
            if (center != null) strip.center = Load(center);
            strip.Rebuild();
            Selection.activeObject = go;
            return go;
        }

        /// <summary>The walls of a room, <paramref name="width"/> × <paramref name="depth"/> m centred on the object, the door
        /// in the middle of the front wall (−Z); a station or a waiting area goes inside.</summary>
        public static GameObject RoomWalls(MenuCommand cmd, string name, float width, float depth)
        {
            var root = Group(cmd, name);
            var sub = new MenuCommand(root);
            void Side(string n, Vector3 at, float yaw, float len, string center)
            {
                var w = Wall(sub, n, len, Panel, center);
                w.transform.localPosition = at;
                w.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                w.GetComponent<UnityModularStrip>().Rebuild();
            }
            Side("Front", new Vector3(-width / 2, 0f, -depth / 2), 90f, width, DoorPanel);
            Side("Back", new Vector3(-width / 2, 0f, depth / 2), 90f, width, null);
            Side("Left", new Vector3(-width / 2, 0f, -depth / 2), 0f, depth, null);
            Side("Right", new Vector3(width / 2, 0f, -depth / 2), 0f, depth, null);
            Selection.activeObject = root;
            return root;
        }

        /// <summary>A coloured, labelled area on the floor (<see cref="UnityFloorZone"/>).</summary>
        public static GameObject FloorZone(MenuCommand cmd, string label, Vector2 size)
        {
            var go = Group(cmd, label);
            var zone = go.AddComponent<UnityFloorZone>();
            zone.label = label;
            zone.size = size;
            zone.Rebuild();
            Selection.activeObject = go;
            return go;
        }

        /// <summary>A library model placed as decoration (nothing in the model), keeping its own import rotation / scale.</summary>
        public static GameObject Decor(MenuCommand cmd, string name, string model)
        {
            var asset = Load(model);
            if (asset == null) { Debug.LogWarning($"[SimuLean] Library model {Models + model} not found."); return null; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            GameObjectUtility.SetParentAndAlign(go, cmd?.context as GameObject);
            go.transform.localRotation = asset.transform.localRotation;
            go.transform.localScale = asset.transform.localScale;
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            Selection.activeObject = go;
            return go;
        }

        // ------------------------------------------------------------------ composites

        [SimuLeanPresetIcon(Models + Consultation)]
        [MenuItem(Menu + "Outpatient Clinic", false, 60)]
        private static void OutpatientClinicMenu(MenuCommand cmd) => OutpatientClinic(cmd);

        /// <summary>Referrals → agenda (patients wait at home) → waiting room → consultation room (a doctor) → discharge.</summary>
        public static GameObject OutpatientClinic(MenuCommand cmd)
        {
            var root = Group(cmd, "Outpatient Clinic");
            var sub = new MenuCommand(root);
            var referrals = At(Referrals(sub, "Referrals"), 0f);
            var agenda = At(Agenda(sub, "Agenda"), 5f);
            var room = At(WaitingRoom(sub, "Waiting Room", 6), 10f);
            var visit = At(ConsultationRoom(sub, "Consultation Room"), 16f);
            var doctors = LibraryPresets.Team(sub, "Doctors", "Doctor.prefab").GetComponent<UnityResourceTeam>();
            doctors.AddMembers(1);
            visit.GetComponent<UnityMultiServer>().processBy = new ProcessResourceUse { team = doctors };
            var done = At(Discharge(sub, "Discharge"), 22f);
            Connect(referrals, agenda); Connect(agenda, room); Connect(room, visit); Connect(visit, done);
            Pathway(root);
            Selection.activeObject = root;
            return root;
        }

        [SimuLeanPresetIcon(Models + Consultation)]
        [MenuItem(Menu + "Home Diagnostic Test", false, 61)]
        private static void HomeDiagnosticTestMenu(MenuCommand cmd) => HomeDiagnosticTest(cmd);

        /// <summary>
        /// Device desk (a device of the equipment pool goes home with the patient: released at the device return) → a night
        /// at home → device return; an invalid test (10 %, drawn on entry to the desk) goes back to the desk's line, a valid
        /// one to the report (not face to face).
        /// </summary>
        public static GameObject HomeDiagnosticTest(MenuCommand cmd)
        {
            var root = Group(cmd, "Home Diagnostic Test");
            var sub = new MenuCommand(root);
            var referrals = At(Referrals(sub, "Referrals"), -5f);
            var line = At(WaitingLine(sub, "Device Desk Line"), 0f);
            var desk = At(LibraryPresets.Station<UnityMultiServer>(sub, "Device Desk", s => { s.capacity = 1; Constant(s.delayStrategy, 0.25f); },
                                                               ("Reception desk", Desk)), 5f);
            var home = At(Virtual<UnityDelay>(sub, "Home Test", "Home test (night at home)", d => Constant(d.delayStrategy, 24)), 10f);
            var back = At(LibraryPresets.Station<UnityMultiServer>(sub, "Device Return", s => { s.capacity = 1; Constant(s.delayStrategy, 0.1f); },
                                                               ("Reception desk", Desk)), 15f);
            var report = At(Virtual<UnityMultiServer>(sub, "Report", "Report — not face to face", s => Constant(s.delayStrategy, 0.5f)), 20f);
            var pool = EquipmentPool(sub, "Polygraphs", 3).GetComponent<UnityResourceTeam>();

            var deskEl = desk.GetComponent<UnityMultiServer>();
            deskEl.entryLabels = new List<string> { "tests = tests + 1", "valid = rand() < 0.9" };
            deskEl.processBy = new ProcessResourceUse { team = pool, release = ProcessRelease.AtElement, releaseAt = back.GetComponent<SElement>() };
            var backEl = back.GetComponent<UnityMultiServer>();
            backEl.outputStrategy.strategyType = OutputStrategyType.Expression;
            backEl.outputStrategy.routingExpression = "item.valid ? 0 : 1";
            Connect(referrals, line); Connect(line, desk); Connect(desk, home); Connect(home, back);
            Connect(back, report); Connect(back, line);   // 0 = valid → report, 1 = repeat
            Pathway(root);                                 // last: the arrows follow every connection (review2 #33)
            Selection.activeObject = root;
            return root;
        }

        [SimuLeanPresetIcon(Models + Consultation)]
        [MenuItem(Menu + "Follow-up Loop", false, 62)]
        private static void FollowUpLoopMenu(MenuCommand cmd) => FollowUpLoop(cmd);

        /// <summary>
        /// Treatment at home → review visit (a doctor); at each review 60 % continue the treatment (back home), the others
        /// are discharged — `reviews` counts the visits, `continue` is drawn on entry to the review (route on it).
        /// </summary>
        public static GameObject FollowUpLoop(MenuCommand cmd)
        {
            var root = Group(cmd, "Follow-up Loop");
            var sub = new MenuCommand(root);
            var referrals = At(Referrals(sub, "Referrals"), 0f);
            var treatment = At(Virtual<UnityDelay>(sub, "Treatment at Home", "Treatment at home", d => Constant(d.delayStrategy, 2160)), 5f);
            var line = At(WaitingLine(sub, "Review Line"), 10f);
            var review = At(ConsultationRoom(sub, "Review"), 15f);
            var done = At(Discharge(sub, "Discharge"), 21f);
            var r = review.GetComponent<UnityMultiServer>();
            Constant(r.delayStrategy, 0.33f);
            r.entryLabels = new List<string> { "reviews = reviews + 1", "continue = rand() < 0.6" };
            r.outputStrategy.strategyType = OutputStrategyType.Expression;
            r.outputStrategy.routingExpression = "item.continue ? 1 : 0";
            Connect(referrals, treatment); Connect(treatment, line); Connect(line, review);
            Connect(review, done); Connect(review, treatment);   // 0 = discharge, 1 = another round of treatment
            Pathway(root);
            Selection.activeObject = root;
            return root;
        }

        // ------------------------------------------------------------------ builders (public: composites and tests)

        /// <summary>A triage station: a nurse assigns the triage level on entry (1 = most urgent; 10 / 30 / 60 %) — route
        /// on <c>item.triage</c> or order a list by it.</summary>
        public static GameObject Triage(MenuCommand cmd, string name) =>
            Sign(LibraryPresets.Station<UnityMultiServer>(cmd, name, s =>
            {
                s.capacity = 1;
                Constant(s.delayStrategy, 0.1f);
                s.entryLabels = new List<string> { "triage = pick(0.1, 1, 0.3, 2, 0.6, 3)" };
            }, ("Consultation box", Consultation)));

        /// <summary>A diagnostic test in the hospital: the room and its devices (an equipment pool of 2, the station's process
        /// team, free at the end of the test).</summary>
        public static GameObject DiagnosticTest(MenuCommand cmd, string name)
        {
            var go = LibraryPresets.Station<UnityMultiServer>(cmd, name, s => { s.capacity = 2; Constant(s.delayStrategy, 0.5f); },
                                                               ("Consultation box", Consultation));
            var devices = EquipmentPool(new MenuCommand(go), name + " Devices", 2).GetComponent<UnityResourceTeam>();
            go.GetComponent<UnityMultiServer>().processBy = new ProcessResourceUse { team = devices };
            Sign(go);
            Selection.activeObject = go;
            return go;
        }


        public static GameObject Referrals(MenuCommand cmd, string name)
        {
            var go = Element<UnitySource>(cmd, name, out var src);
            var patient = AssetDatabase.LoadAssetAtPath<GameObject>(LibraryPresets.Items + "Patient.prefab");
            if (patient != null) src.itemPrefab = patient;
            src.separation = 0.7f;
            src.mode = SourceMode.InterArrival;          // referrals keep arriving (a schedule table is one click away)
            src.interArrivalDistribution = new RandomProcessConfig { strategyType = RandomProcessType.Exponential, exponentialRate = 1 };
            AddModel(go, ("Hospital door", Door));
            go.AddComponent<SlidingDoorView>();
            Selection.activeObject = go;
            return go;
        }

        public static GameObject Agenda(MenuCommand cmd, string name)
        {
            var go = Element<UnityAgenda>(cmd, name, out _);
            Look(go, StationModel.Look.Virtual, "Agenda — patients at home until their appointment");
            Label(go, "{n} booked (at home)", hide: true);
            Selection.activeObject = go;
            return go;
        }

        public static GameObject WaitingList(MenuCommand cmd, string name)
        {
            var go = Element<UnityQueue>(cmd, name, out var q);
            q.capacity = 100000;
            Look(go, StationModel.Look.Virtual, "At home");
            Label(go, "{n} waiting at home", hide: true);
            Selection.activeObject = go;
            return go;
        }

        public static GameObject WaitingRoom(MenuCommand cmd, string name, int seats)
        {
            const float seatPitch = 0.6f, endCap = 0.06f;
            var go = Element<UnityQueue>(cmd, name, out var q);
            q.capacity = seats;
            q.layoutMode = QueueLayoutMode.Seats;
            var row = new GameObject("Chairs");
            row.transform.SetParent(go.transform, false);
            // the kit runs along its +Z with the seats facing its +X: turned so they face the queue's +Z, centred
            float length = seats * seatPitch + 2 * endCap;
            row.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
            row.transform.localPosition = new Vector3(length * 0.5f, 0f, 0f);
            var strip = row.AddComponent<UnityModularStrip>();
            strip.combine = false;                       // keeps each seat's Seat node
            strip.segmentLength = seatPitch;
            strip.length = length;
            strip.segment = Load("WaitingChairs/Seat_Row.fbx");
            strip.cap = Load("WaitingChairs/Seat_End.fbx");
            strip.Rebuild();
            Selection.activeObject = go;
            return go;
        }

        public static GameObject WaitingLine(MenuCommand cmd, string name)
        {
            var go = Element<UnityQueue>(cmd, name, out var q);
            q.capacity = 100000;
            q.layoutMode = QueueLayoutMode.Line;
            q.lineLength = 4f;
            q.showFootprint = false;
            q.ApplyFootprintVisibility();
            Selection.activeObject = go;
            return go;
        }

        public static GameObject ConsultationRoom(MenuCommand cmd, string name) =>
            Sign(LibraryPresets.Station<UnityMultiServer>(cmd, name, s => s.capacity = 1, ("Consultation box", Consultation)));

        public static GameObject Discharge(MenuCommand cmd, string name)
        {
            var go = Element<UnitySink>(cmd, name, out var sink);
            sink.tallies = new List<string> { "time_in_system = age" };
            AddModel(go, ("Hospital door", Door));
            go.AddComponent<SlidingDoorView>();
            Selection.activeObject = go;
            return go;
        }

        /// <summary>Devices lent to patients (a team without travel; members named after it). Use it as a station's process
        /// team with the release <i>At Element</i> (the device return).</summary>
        public static GameObject EquipmentPool(MenuCommand cmd, string name, int devices)
        {
            var go = new GameObject(name);
            GameObjectUtility.SetParentAndAlign(go, cmd?.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            var team = go.AddComponent<UnityResourceTeam>();
            team.teamName = name;
            team.showMembers = false;
            team.AddMembers(devices);
            Selection.activeObject = go;
            return go;
        }

        /// <summary>An element with the virtual look (a step done elsewhere or not face to face) and a caption.</summary>
        public static GameObject Virtual<T>(MenuCommand cmd, string name, string caption, System.Action<T> setup) where T : SElement
        {
            var go = Element<T>(cmd, name, out var element);
            setup?.Invoke(element);
            Look(go, StationModel.Look.Virtual, caption);
            Selection.activeObject = go;
            return go;
        }

        // ------------------------------------------------------------------ helpers

        private static GameObject Element<T>(MenuCommand cmd, string name, out T element) where T : SElement
        {
            var go = new GameObject(name);
            GameObjectUtility.SetParentAndAlign(go, cmd?.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            element = go.AddComponent<T>();
            if (go.GetComponent<SElementConnectionGizmo>() == null) go.AddComponent<SElementConnectionGizmo>();
            return go;
        }

        private static void Look(GameObject go, StationModel.Look look, string caption)
        {
            var sm = go.GetComponent<StationModel>();
            if (sm == null) sm = go.AddComponent<StationModel>();
            sm.look = look;
            sm.virtualCaption = caption;
            sm.ApplyLook();
        }

        /// <summary>The room's sign: its name over it, facing the camera (a door sign; rename the GameObject to change it).</summary>
        public static GameObject Sign(GameObject go)
        {
            var label = go.AddComponent<UnityElementLabel>();
            label.text = "{name}";
            label.height = 2.75f;
            label.characterSize = 0.045f;
            Selection.activeObject = go;
            return go;
        }

        private static void Label(GameObject go, string text, bool hide)
        {
            var label = go.AddComponent<UnityElementLabel>();
            label.text = text;
            label.hideItems = hide;
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

        private static GameObject Group(MenuCommand cmd, string name)
        {
            var go = new GameObject(name);
            GameObjectUtility.SetParentAndAlign(go, cmd?.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            return go;
        }

        private static GameObject At(GameObject go, float x)
        {
            go.transform.localPosition = new Vector3(x, 0f, 0f);
            // a composite runs along +X: the entrance's inside (+Z) faces the flow, the exit's faces back to it
            if (go.GetComponent<UnitySource>() != null) go.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            else if (go.GetComponent<UnitySink>() != null) go.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
            return go;
        }

        /// <summary>Floor arrows along the composite's connections (a child, so the composite stays a plain group).</summary>
        private static void Pathway(GameObject root)
        {
            var go = new GameObject("Pathway Arrows");
            go.transform.SetParent(root.transform, false);
            go.AddComponent<UnityPathwayArrows>().root = root.transform;
        }

        private static void Connect(GameObject from, GameObject to)
        {
            var a = from.GetComponent<SElement>();
            var b = to.GetComponent<SElement>();
            if (a == null || b == null) return;
            if (a.nextElements == null) a.nextElements = new List<SElement>();
            a.nextElements.Add(b);
        }

        private static void Constant(RandomProcessConfig r, float value)
        {
            r.strategyType = RandomProcessType.Constant;
            r.constantValue = value;
        }

        private static GameObject Load(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(Models + path);
    }
}
