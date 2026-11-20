using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Inspector for <see cref="UnityStorageRack"/>: the layout (Parameters) and the rack structure, accessories,
    /// generated visuals and item view (Visual) in groups that depend on the style, the layout / triangle summary and
    /// the "Rebuild Storage Locations" button; and the storage element library under GameObject → SimuLean → Storage
    /// (pre-configured pallet rack, carton flow rack, floor storage, drive-in and push-back elements — listed by the
    /// SimuLean window's Presets from these menu items).
    /// </summary>
    [CustomEditor(typeof(UnityStorageRack))]
    public class UnityStorageRackEditor : SimuLean.Unity.Editor.SElementInspector
    {
        private static readonly string[] Layout =
        {
            "style", "bays", "slotsPerBay", "levels", "depth", "stackHeight",
            "bayWidth", "slotWidth", "levelHeight", "slotDepth", "groundLevel", "firstLevelHeight", "skuMix",
        };
        private static readonly string[] Structure = { "frameDepth", "uprightWidth", "uprightDepth", "beamHeight", "uprightExtension", "bracingPattern" };
        private static readonly string[] Accessories = { "deckType", "rowSpacerLength", "uprightProtectors", "endOfAisleGuards", "locationLabels" };
        private static readonly string[] Carton = { "flowSlopeDegrees", "overstockDeck" };
        private static readonly string[] Build = { "proceduralVisuals", "baysPerChunk", "generateLods", "slotPrefab", "slotVisualThickness" };
        private static readonly string[] Materials =
        {
            "frameMaterial", "beamMaterial", "steelMaterial", "safetyMaterial", "labelMaterial", "deckMaterial", "trackMaterial", "floorMarkingMaterial",
        };
        private static readonly string[] ItemView = { "restItemsOnSurface", "itemVerticalOffset", "stackPitch" };

        private static string[] _all;
        private static string[] All
        {
            get
            {
                if (_all != null) return _all;
                var l = new System.Collections.Generic.List<string>();
                foreach (var g in new[] { Layout, Structure, Accessories, Carton, Build, Materials, ItemView }) l.AddRange(g);
                return _all = l.ToArray();
            }
        }

        private UnityStorageRack Rack => (UnityStorageRack)target;

        protected override void DrawParameters()
        {
            StorageRackStyle style = Rack.style;
            bool floor = style == StorageRackStyle.FloorStorage, carton = style == StorageRackStyle.CartonFlowRack;

            Field("style");
            Field("bays");
            if (!floor)
            {
                Field("slotsPerBay", carton ? "Lanes Per Bay" : null);
                Field("levels");
            }
            Field("depth", floor ? "Rows" : carton ? "Boxes Per Lane" : null);
            if (floor) Field("stackHeight");
            Field("bayWidth", floor ? "Cell Width" : null);
            if (!floor)
            {
                Field("slotWidth", carton ? "Lane Width" : null);
                Field("levelHeight");
            }
            Field("slotDepth", floor ? "Cell Depth" : carton ? "Box Pitch" : null);
            if (!floor && !carton) Field("groundLevel");
            if (carton || (!floor && !Rack.groundLevel)) Field("firstLevelHeight", carton ? "First Shelf Height" : null);
            Field("skuMix");

            DrawRemaining(false, All);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(Rack.GetLayoutSummary() +
                "\nParameters only take effect after a rebuild. Do not scale the object — set the pitches and rebuild instead.",
                MessageType.Info);
            RebuildButton();
        }

        protected override void DrawVisual()
        {
            StorageRackStyle style = Rack.style;
            bool floor = style == StorageRackStyle.FloorStorage, carton = style == StorageRackStyle.CartonFlowRack;
            bool procedural = Rack.slotPrefab == null && Rack.proceduralVisuals;

            if (!floor && procedural)
            {
                SimuLean.Unity.Editor.SimuLeanGUI.SubHeader("Rack structure");
                if (!carton) Field("frameDepth");
                Field("uprightWidth");
                Field("uprightDepth");
                if (!carton) Field("beamHeight");
                Field("uprightExtension");
                Field("bracingPattern");

                SimuLean.Unity.Editor.SimuLeanGUI.SubHeader("Accessories");
                if (carton)
                {
                    Field("flowSlopeDegrees");
                    Field("overstockDeck");
                    Field("locationLabels", "Lane Labels");
                }
                else
                {
                    if (style == StorageRackStyle.ConventionalRack) Field("deckType");
                    Field("rowSpacerLength");
                    Field("uprightProtectors");
                    Field("endOfAisleGuards");
                    if (style == StorageRackStyle.ConventionalRack) Field("locationLabels");
                }
            }
            else if (carton) Field("flowSlopeDegrees");

            SimuLean.Unity.Editor.SimuLeanGUI.SubHeader("Generated visuals");
            Field("slotPrefab");
            if (Rack.slotPrefab == null)
            {
                Field("proceduralVisuals");
                if (procedural)
                {
                    if (!floor)
                    {
                        Field("baysPerChunk");
                        Field("generateLods");
                    }
                    var mats = serializedObject.FindProperty("frameMaterial");
                    if (mats != null && SimuLean.Unity.Editor.SimuLeanGUI.Section("UnityStorageRack.Materials", "Material overrides", false))
                    {
                        if (floor) Field("floorMarkingMaterial");
                        else
                            foreach (string m in Materials)
                                if (m != "floorMarkingMaterial" && (carton || m != "trackMaterial")) Field(m);
                    }
                }
                else Field("slotVisualThickness");
            }

            SimuLean.Unity.Editor.SimuLeanGUI.SubHeader("Item location view");
            Field("restItemsOnSurface");
            Field("itemVerticalOffset");
            if (floor) Field("stackPitch");

            DrawRemaining(true, All);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(Rack.GetVisualSummary(), MessageType.None);
            RebuildButton();
        }

        private void Field(string name, string label = null)
        {
            SerializedProperty p = serializedObject.FindProperty(name);
            if (p == null) return;
            SimuLean.Unity.Editor.SimuLeanGUI.Field(p, label != null ? new GUIContent(label, p.tooltip) : null);
        }

        private void RebuildButton()
        {
            if (!GUILayout.Button("Rebuild Storage Locations")) return;
            serializedObject.ApplyModifiedProperties();
            UnityStorageRack rack = Rack;
            Undo.RegisterFullObjectHierarchyUndo(rack.gameObject, "Rebuild Storage Locations");
            rack.Rebuild();
            EditorUtility.SetDirty(rack);
            if (!Application.isPlaying)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(rack.gameObject.scene);
            serializedObject.Update();
        }

        // ── storage element library ──────────────────────────────────────────

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/ASRS/Rack_Bay.fbx")]
        [SimuLeanVariantOf(typeof(UnityStorageRack))]
        [MenuItem("GameObject/SimuLean/Storage/Pallet Rack", false, 10)]
        private static void CreatePalletRack(MenuCommand cmd)
        {
            UnityStorageRack rack = Create(cmd, "PalletRack", StorageRackStyle.ConventionalRack);
            rack.bays = 3; rack.slotsPerBay = 3; rack.levels = 4; rack.depth = 1;
            rack.bayWidth = 2.8f; rack.slotWidth = 0.9f; rack.levelHeight = 1.5f; rack.slotDepth = 1.2f;
            rack.groundLevel = true; rack.deckType = RackDeckType.None;
            rack.Rebuild();
        }

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/ASRS/Rack_Bay.fbx")]
        [SimuLeanVariantOf(typeof(UnityStorageRack))]
        [MenuItem("GameObject/SimuLean/Storage/Carton Flow Rack", false, 11)]
        private static void CreateCartonFlowRack(MenuCommand cmd)
        {
            UnityStorageRack rack = Create(cmd, "CartonFlowRack", StorageRackStyle.CartonFlowRack);
            rack.bays = 2; rack.slotsPerBay = 4; rack.levels = 4; rack.depth = 4;
            rack.bayWidth = 1.88f; rack.slotWidth = 0.45f; rack.levelHeight = 0.45f; rack.slotDepth = 0.6f;
            rack.firstLevelHeight = 0.35f; rack.flowSlopeDegrees = 6f; rack.overstockDeck = true;
            rack.uprightWidth = 0.08f; rack.uprightDepth = 0.06f; rack.uprightExtension = 0.1f;
            rack.uprightProtectors = false; rack.endOfAisleGuards = false;
            rack.Rebuild();
        }

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/WarehouseLoadUnits/EuroPallet.fbx")]
        [SimuLeanVariantOf(typeof(UnityStorageRack))]
        [MenuItem("GameObject/SimuLean/Storage/Floor Storage", false, 12)]
        private static void CreateFloor(MenuCommand cmd)
        {
            UnityStorageRack rack = Create(cmd, "FloorStorage", StorageRackStyle.FloorStorage);
            rack.bays = 4; rack.depth = 6; rack.stackHeight = 2;
            rack.bayWidth = 1.2f; rack.slotDepth = 1.2f; rack.stackPitch = 0.5f;
            rack.Rebuild();
        }

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/ASRS/Rack_Bay.fbx")]
        [SimuLeanVariantOf(typeof(UnityStorageRack))]
        [MenuItem("GameObject/SimuLean/Storage/Drive-In Rack (DIN)", false, 13)]
        private static void CreateDriveIn(MenuCommand cmd)
        {
            UnityStorageRack rack = Create(cmd, "DriveInRack", StorageRackStyle.DriveInRack);
            rack.bays = 3; rack.slotsPerBay = 1; rack.levels = 3; rack.depth = 5;
            rack.bayWidth = 1.45f; rack.slotWidth = 1.35f; rack.levelHeight = 1.6f; rack.slotDepth = 1.35f;
            rack.Rebuild();
        }

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/ASRS/Rack_Bay.fbx")]
        [SimuLeanVariantOf(typeof(UnityStorageRack))]
        [MenuItem("GameObject/SimuLean/Storage/Push-Back Rack", false, 14)]
        private static void CreatePushBack(MenuCommand cmd)
        {
            UnityStorageRack rack = Create(cmd, "PushBackRack", StorageRackStyle.PushBackRack);
            rack.bays = 3; rack.slotsPerBay = 1; rack.levels = 3; rack.depth = 3;
            rack.bayWidth = 1.45f; rack.slotWidth = 1.35f; rack.levelHeight = 1.6f; rack.slotDepth = 1.35f;
            rack.Rebuild();
        }

        private static UnityStorageRack Create(MenuCommand cmd, string name, StorageRackStyle style)
        {
            var go = new GameObject(name);
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            UnityStorageRack rack = go.AddComponent<UnityStorageRack>();
            rack.style = style;
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);   // complete: one undo step, nothing dangling
            Selection.activeObject = go;
            return rack;
        }
    }
}
