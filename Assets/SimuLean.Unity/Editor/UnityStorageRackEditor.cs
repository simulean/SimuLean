using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Inspector for <see cref="UnityStorageRack"/>: shows the computed layout
    /// summary and the "Rebuild Storage Locations" button, and provides the
    /// storage element library under GameObject → SimuLean → Storage
    /// (pre-configured conventional / floor / drive-in / push-back elements).
    /// </summary>
    [CustomEditor(typeof(UnityStorageRack))]
    public class UnityStorageRackEditor : SimuLean.Unity.Editor.SElementInspector
    {
        protected override void DrawVisual() => DrawRemaining(true);

        protected override void DrawParameters()
        {
            DrawRemaining(false);
            var rack = (UnityStorageRack)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(rack.GetLayoutSummary() +
                "\nParameters only take effect after a rebuild. Do not scale the object — " +
                "set the pitches and rebuild instead.", MessageType.Info);

            if (GUILayout.Button("Rebuild Storage Locations"))
            {
                Undo.RegisterFullObjectHierarchyUndo(rack.gameObject, "Rebuild Storage Locations");
                rack.Rebuild();
                EditorUtility.SetDirty(rack);
                if (!Application.isPlaying)
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(rack.gameObject.scene);
            }
        }

        // ── storage element library ──────────────────────────────────────────

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/ASRS/Rack_Bay.fbx")]
        [MenuItem("GameObject/SimuLean/Storage/Conventional Rack", false, 10)]
        private static void CreateConventional(MenuCommand cmd)
        {
            UnityStorageRack rack = Create(cmd, "ConventionalRack", StorageRackStyle.ConventionalRack);
            rack.bays = 3; rack.slotsPerBay = 3; rack.levels = 4; rack.depth = 1;
            rack.bayWidth = 2.7f; rack.slotWidth = 0.9f; rack.levelHeight = 1.5f; rack.slotDepth = 1.2f;
            rack.Rebuild();
        }

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/LoadUnits/EuroPallet.fbx")]
        [MenuItem("GameObject/SimuLean/Storage/Floor Storage", false, 11)]
        private static void CreateFloor(MenuCommand cmd)
        {
            UnityStorageRack rack = Create(cmd, "FloorStorage", StorageRackStyle.FloorStorage);
            rack.bays = 4; rack.depth = 6; rack.stackHeight = 2;
            rack.bayWidth = 1.2f; rack.slotDepth = 1.2f; rack.stackPitch = 0.5f;
            rack.Rebuild();
        }

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/ASRS/Rack_Bay.fbx")]
        [MenuItem("GameObject/SimuLean/Storage/Drive-In Rack (DIN)", false, 12)]
        private static void CreateDriveIn(MenuCommand cmd)
        {
            UnityStorageRack rack = Create(cmd, "DriveInRack", StorageRackStyle.DriveInRack);
            rack.bays = 3; rack.slotsPerBay = 1; rack.levels = 3; rack.depth = 5;
            rack.bayWidth = 1.4f; rack.slotWidth = 1.35f; rack.levelHeight = 1.6f; rack.slotDepth = 1.35f;
            rack.Rebuild();
        }

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Art/Models/ASRS/Rack_Bay.fbx")]
        [MenuItem("GameObject/SimuLean/Storage/Push-Back Rack", false, 13)]
        private static void CreatePushBack(MenuCommand cmd)
        {
            UnityStorageRack rack = Create(cmd, "PushBackRack", StorageRackStyle.PushBackRack);
            rack.bays = 3; rack.slotsPerBay = 1; rack.levels = 3; rack.depth = 3;
            rack.bayWidth = 1.4f; rack.slotWidth = 1.35f; rack.levelHeight = 1.6f; rack.slotDepth = 1.35f;
            rack.Rebuild();
        }

        private static UnityStorageRack Create(MenuCommand cmd, string name, StorageRackStyle style)
        {
            var go = new GameObject(name);
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);

            UnityStorageRack rack = go.AddComponent<UnityStorageRack>();
            rack.style = style;
            Selection.activeObject = go;
            return rack;
        }
    }
}
