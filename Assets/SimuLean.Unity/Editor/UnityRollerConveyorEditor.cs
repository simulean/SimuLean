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
        /// simulates it) with the roller look as its child, which follows the lane's length. It conveys: an item
        /// takes the length ÷ the speed to reach the end — <see cref="DefaultSpeed"/> m/s, a usual roller conveyor, in the
        /// scene clock's time unit.</summary>
        /// <summary>The speed of a new conveyor (m/s).</summary>
        public const float DefaultSpeed = 0.3f;

        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Editor/Icons/RollerConveyor.png")]
        [SimuLeanVariantOf(typeof(UnityLengthLimitedQueue))]
        [MenuItem("GameObject/SimuLean/Queues/Conveyor", false, 20)]
        private static void CreateConveyor(MenuCommand cmd)
        {
            var go = new GameObject("Conveyor");
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            var lane = LibraryPresets.AddElement<UnityLengthLimitedQueue>(go);
            lane.laneLengths = new System.Collections.Generic.List<float> { 6f };
            var clock = UnityCompat.FindAny<UnitySimClock>();
            double unit = clock != null && clock.TimeUnitSeconds > 0 ? clock.TimeUnitSeconds : 1.0;
            lane.speed = (float)(DefaultSpeed * unit);

            var rollers = new GameObject("Rollers");
            rollers.transform.SetParent(go.transform, false);
            rollers.AddComponent<UnityRollerConveyor>().FitTo(lane.laneLengths[0], 0f);

            Undo.RegisterCreatedObjectUndo(go, "Create Conveyor");   // complete: one undo step, nothing dangling
            Selection.activeObject = go;
        }

        /// <summary>An accumulation lane: the same element without speed — items wait where they stop, up to
        /// the lane's length (trucks at a gate, blocks on a quay, pallets of several lengths) — with its outline painted on
        /// the floor (<see cref="UnityLaneMarking"/>, which follows the lane's length).</summary>
        [SimuLeanPresetIcon("Assets/SimuLean.Unity/Editor/Icons/AccumulationLane.png")]
        [SimuLeanVariantOf(typeof(UnityLengthLimitedQueue))]
        [MenuItem("GameObject/SimuLean/Queues/Accumulation Lane", false, 21)]
        private static void CreateAccumulationLane(MenuCommand cmd)
        {
            var go = new GameObject("Accumulation Lane");
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            var lane = LibraryPresets.AddElement<UnityLengthLimitedQueue>(go);
            lane.laneLengths = new System.Collections.Generic.List<float> { 8f };
            lane.speed = 0f;

            var marking = new GameObject("Marking");
            marking.transform.SetParent(go.transform, false);
            var paint = marking.AddComponent<UnityLaneMarking>();
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/SimuLean.Unity/Art/Floor/LaneMarking.mat");
            if (material != null) marking.GetComponent<MeshRenderer>().sharedMaterial = material;
            paint.FitTo(lane.laneLengths[0], 0f);

            Undo.RegisterCreatedObjectUndo(go, "Create Accumulation Lane");   // complete: one undo step
            Selection.activeObject = go;
        }
    }
}
