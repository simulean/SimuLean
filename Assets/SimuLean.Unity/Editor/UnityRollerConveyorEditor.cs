using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Inspector for <see cref="UnityRollerConveyor"/>: shows the computed
    /// length/roller summary and a "Rebuild Conveyor" button, and provides the
    /// GameObject → SimuLean → Transport → Roller Conveyor menu entry.
    /// Mirrors <see cref="UnityStorageRackEditor"/> (parametric, never scale).
    /// </summary>
    [CustomEditor(typeof(UnityRollerConveyor))]
    public class UnityRollerConveyorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var conv = (UnityRollerConveyor)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(conv.GetSummary() +
                "\nParameters only take effect after a rebuild. Do not scale the object — " +
                "set the length/pitch and rebuild instead (scaling would deform the rollers).",
                MessageType.Info);

            if (GUILayout.Button("Rebuild Conveyor"))
            {
                Undo.RegisterFullObjectHierarchyUndo(conv.gameObject, "Rebuild Conveyor");
                conv.Rebuild();
                EditorUtility.SetDirty(conv);
                if (!Application.isPlaying)
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(conv.gameObject.scene);
            }
        }

        /// <summary>A conveyor: an accumulating lane (<see cref="UnityLengthLimitedQueue"/>, the element that
        /// simulates it) with the roller look as its child, which follows the lane's length.</summary>
        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Editor/Icons/RollerConveyor.png")]
        [MenuItem("GameObject/SimuLean/Queues/Conveyor", false, 20)]
        private static void CreateConveyor(MenuCommand cmd)
        {
            var go = new GameObject("Conveyor");
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Conveyor");
            var lane = LibraryPresets.AddElement<UnityLengthLimitedQueue>(go);
            lane.laneLengths = new System.Collections.Generic.List<float> { 6f };

            var rollers = new GameObject("Rollers");
            rollers.transform.SetParent(go.transform, false);
            rollers.AddComponent<UnityRollerConveyor>().FitTo(lane.laneLengths[0], 0f);

            Selection.activeObject = go;
        }
    }
}
