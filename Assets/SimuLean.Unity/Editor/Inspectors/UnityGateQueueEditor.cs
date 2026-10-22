using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>Gate Queue inspector (<see cref="SElementInspector"/>).</summary>
    [CustomEditor(typeof(UnityGateQueue))]
    public class UnityGateQueueEditor : SElementInspector
    {
        private static int releaseQuantity = 1;

        protected override void DrawParameters()
        {
            EditorGUILayout.PropertyField(Prop("capacity"), new GUIContent("Capacity", "Items it can hold until the gate releases them."));
        }

        protected override void DrawVisual()
        {
            EditorGUILayout.PropertyField(Prop("storagePosition"), new GUIContent("Item position", "Where the first item stands."));
            SimuLeanGUI.SubHeader("Spacing (x · y · z)");
            SimuLeanGUI.Field(Prop("xSeparation"), new GUIContent("X", "Horizontal spacing between items."));
            SimuLeanGUI.Field(Prop("ySeparation"), new GUIContent("Y", "Vertical spacing between items."));
            SimuLeanGUI.Field(Prop("zSeparation"), new GUIContent("Z", "Depth spacing between items."));
            SimuLeanGUI.SubHeader("Layout");
            EditorGUILayout.PropertyField(Prop("yLevels"), new GUIContent("Y levels", "Vertical levels before the next layer."));
            EditorGUILayout.PropertyField(Prop("zLevels"), new GUIContent("Z levels", "Depth levels before the next column."));
        }

        protected override void DrawPlayExtra()
        {
            var gate = (UnityGateQueue)target;
            if (gate.GetElement() is GateQueue gq)
                SimuLeanGUI.Row("Pending release", gq.GetPendingItems().ToString());
            EditorGUILayout.BeginHorizontal();
            releaseQuantity = Mathf.Max(1, EditorGUILayout.IntField("Release", releaseQuantity));
            if (GUILayout.Button("Release", GUILayout.Width(70))) gate.Release(releaseQuantity);
            EditorGUILayout.EndHorizontal();
        }
    }
}
