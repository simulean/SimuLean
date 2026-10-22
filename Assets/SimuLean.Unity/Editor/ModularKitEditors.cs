using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Inspector for every <see cref="ModularAssetBehaviour"/> (wall, column, strip, crane):
    /// summary, a Rebuild button and a reminder never to scale; plus the
    /// GameObject → SimuLean → Building menu (single pieces and a complete workshop).
    /// </summary>
    [CustomEditor(typeof(ModularAssetBehaviour), true)]
    public class ModularAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var asset = (ModularAssetBehaviour)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(asset.GetSummary() +
                "\nSize parameters take effect after a rebuild. Do not scale the object — set the sizes " +
                "and rebuild (only the tiling pieces stretch; doors, trucks and trolley keep their shape).",
                MessageType.Info);

            if (GUILayout.Button("Rebuild"))
                foreach (var t in targets)
                    Rebuild((ModularAssetBehaviour)t);

            if (asset is UnityOverheadCrane crane && Application.isPlaying)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Test motion", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Random target"))
                        crane.MoveTo(Random.Range(-crane.BridgeLimit, crane.BridgeLimit),
                                     Random.Range(-crane.TrolleyLimit, crane.TrolleyLimit),
                                     Random.Range(0.5f, crane.MaxDrop));
                    if (GUILayout.Button("Park"))
                        crane.MoveTo(-crane.BridgeLimit, 0f, 1f);
                }
            }
        }

        static void Rebuild(ModularAssetBehaviour asset)
        {
            Undo.RegisterFullObjectHierarchyUndo(asset.gameObject, "Rebuild " + asset.name);
            asset.Rebuild();
            EditorUtility.SetDirty(asset);
            if (!Application.isPlaying)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(asset.gameObject.scene);
        }

        // ------------------------------------------------------------------ menu

        const string Menu = "GameObject/SimuLean/Building/";

        static T Create<T>(MenuCommand cmd, string name) where T : ModularAssetBehaviour
        {
            var go = new GameObject(name);
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            var c = go.AddComponent<T>();        // Reset() resolves the kit and builds
            Selection.activeObject = go;
            return c;
        }

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/IndustrialBuilding/Wall_Window.fbx")]
        [MenuItem(Menu + "Wall", false, 30)]
        static void CreateWall(MenuCommand cmd) => Create<UnityModularWall>(cmd, "Wall");

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/IndustrialBuilding/Column_Shaft.fbx")]
        [MenuItem(Menu + "Steel Column", false, 31)]
        static void CreateColumn(MenuCommand cmd) => Create<UnitySteelColumn>(cmd, "SteelColumn");

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/IndustrialBuilding/Beam_Segment.fbx")]
        [MenuItem(Menu + "Steel Beam (IPE)", false, 32)]
        static void CreateBeam(MenuCommand cmd)
        {
            var s = Create<UnityModularStrip>(cmd, "SteelBeam");
            SetStrip(s, ModularKit.BuildingKit, "Beam_Segment", "Beam_End", null);
        }

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/OverheadCrane/Runway_Segment.fbx")]
        [MenuItem(Menu + "Crane Runway", false, 33)]
        static void CreateRunway(MenuCommand cmd)
        {
            var s = Create<UnityModularStrip>(cmd, "CraneRunway");
            SetStrip(s, ModularKit.CraneKit, "Runway_Segment", "Runway_EndStop", null);
        }

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/OverheadCrane/Trolley.fbx")]
        [MenuItem(Menu + "Overhead Crane", false, 34)]
        static void CreateCrane(MenuCommand cmd) => Create<UnityOverheadCrane>(cmd, "OverheadCrane");

        static void SetStrip(UnityModularStrip s, string kit, string segment, string cap, string center)
        {
            s.segment = ModularKit.Load(kit, segment);
            s.cap = cap != null ? ModularKit.Load(kit, cap) : null;
            s.center = center != null ? ModularKit.Load(kit, center) : null;
            s.Rebuild();
        }

        /// <summary>
        /// A complete 24 × 18 m workshop (8 m bays, 7.6 m columns) with a 16.36 m span
        /// crane on the column brackets — the same layout as Tools/Blender workshop_preview.
        /// </summary>
        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/IndustrialBuilding/Door_Sectional.fbx")]
        [MenuItem(Menu + "Workshop (24 x 18 m with crane)", false, 50)]
        static void CreateWorkshop(MenuCommand cmd)
        {
            const float W = 24f, D = 18f, colH = 7.6f, rail = 6f, colOff = 0.32f;
            var root = new GameObject("Workshop");
            GameObjectUtility.SetParentAndAlign(root, cmd.context as GameObject);
            Undo.RegisterCreatedObjectUndo(root, "Create Workshop");
            float x0 = -W / 2f, z0 = -D / 2f;

            // walls counter-clockwise (interior on each wall's +Z side)
            Wall(root, "Wall_South", new Vector3(x0, 0f, z0), 0f, W,
                 (UnityModularWall.OpeningType.SectionalDoor, 1, true), (UnityModularWall.OpeningType.PersonnelDoor, 6, false));
            Wall(root, "Wall_East", new Vector3(-x0, 0f, z0), -90f, D);
            Wall(root, "Wall_North", new Vector3(-x0, 0f, -z0), 180f, W,
                 (UnityModularWall.OpeningType.SectionalDoor, 4, false));
            Wall(root, "Wall_West", new Vector3(x0, 0f, -z0), 90f, D);

            // portal frames every 8 m: two columns with crane brackets + an IPE beam on top
            float[] xs = { x0 + colOff, -4f, 4f, -x0 - colOff };
            for (int i = 0; i < xs.Length; i++)
            {
                Column(root, $"Column_S{i}", new Vector3(xs[i], 0f, z0 + colOff), 0f, colH, rail, true);
                Column(root, $"Column_N{i}", new Vector3(xs[i], 0f, -z0 - colOff), 180f, colH, rail, true);
                var beam = Child<UnityModularStrip>(root, $"Beam_{i}", new Vector3(xs[i], colH + 0.40f, z0 + colOff - 0.15f), 0f);
                beam.length = D - 2f * colOff + 0.30f;
                SetStrip(beam, ModularKit.BuildingKit, "Beam_Segment", "Beam_End", null);
            }
            // gable (wind) columns
            foreach (int sx in new[] { 1, -1 })
                foreach (float z in new[] { -3f, 3f })
                    Column(root, $"Column_Gable{(sx > 0 ? "E" : "W")}{(z > 0 ? "N" : "S")}",
                           new Vector3(sx * (-x0 - colOff), 0f, z), sx > 0 ? -90f : 90f, colH, rail, false);

            // crane: runways along the building length (world X) on the brackets
            var crane = Child<UnityOverheadCrane>(root, "OverheadCrane", Vector3.zero, 90f);
            crane.span = D - 2f * (colOff + UnitySteelColumn.BracketReach);
            crane.runwayLength = W - 2f * colOff + 0.30f;
            crane.railHeight = rail;
            crane.bridgePosition = -2.5f;
            crane.trolleyPosition = -3f;
            crane.hookDrop = 3.4f;
            crane.Rebuild();

            Selection.activeObject = root;
        }

        static T Child<T>(GameObject root, string name, Vector3 pos, float yaw) where T : ModularAssetBehaviour
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            return go.AddComponent<T>();         // builds once with defaults; callers rebuild after configuring
        }

        static void Wall(GameObject root, string name, Vector3 pos, float yaw, float length,
                         params (UnityModularWall.OpeningType type, int cell, bool sign)[] openings)
        {
            var w = Child<UnityModularWall>(root, name, pos, yaw);
            w.length = length;
            foreach (var o in openings)
                w.openings.Add(new UnityModularWall.Opening { type = o.type, cell = o.cell, sign = o.sign });
            w.Rebuild();
        }

        static void Column(GameObject root, string name, Vector3 pos, float yaw, float h, float rail, bool crane)
        {
            var c = Child<UnitySteelColumn>(root, name, pos, yaw);
            c.height = h;
            c.railHeight = rail;
            c.bracket = crane;
            c.guard = crane;
            c.Rebuild();
        }
    }
}
