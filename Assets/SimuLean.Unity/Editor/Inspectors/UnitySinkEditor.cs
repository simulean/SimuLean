using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>Sink inspector (<see cref="SElementInspector"/>).</summary>
    [CustomEditor(typeof(UnitySink))]
    public class UnitySinkEditor : SElementInspector
    {
        protected override bool HasVisual => false;

        protected override void DrawParameters()
        {
            EditorGUILayout.PropertyField(Prop("expectedItems"),
                new GUIContent("Expected items", "Items expected by the end of the run (0 = none): the progress shown in Play."));
            EditorGUILayout.PropertyField(Prop("keepItems"), new GUIContent("Keep items",
                "Keep the received items (they stay in the scene and in the model) instead of only counting them."));
            if (Prop("keepItems").boolValue)
                EditorGUILayout.PropertyField(Prop("positionLabels"), new GUIContent("Position labels",
                    "Labels of a kept item's position from the sink (x along, y up, z across, m); none: a row along +X."));
        }

        protected override void DrawPlayExtra()
        {
            var sink = (UnitySink)target;
            if (sink.expectedItems <= 0) return;
            float progress = Mathf.Clamp01((float)sink.Input / sink.expectedItems);
            EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 18), progress, $"{sink.Input} / {sink.expectedItems} items");
        }
    }
}
