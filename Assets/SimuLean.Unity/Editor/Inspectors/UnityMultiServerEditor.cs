using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>Multi-Server inspector (<see cref="SElementInspector"/>).</summary>
    [CustomEditor(typeof(UnityMultiServer))]
    public class UnityMultiServerEditor : SElementInspector
    {
        protected override void DrawParameters()
        {
            EditorGUILayout.PropertyField(Prop("capacity"), new GUIContent("Servers", "Items processed at the same time (parallel slots)."));
            EditorGUILayout.PropertyField(Prop("delayStrategy"), new GUIContent("Process time", "Time each item is processed (per slot)."));
        }

        protected override void DrawVisual()
        {
            EditorGUILayout.PropertyField(Prop("inItemPosition"),
                new GUIContent("Item position", "Centre of the slots (one per server). Empty = the top of the element's footprint."));
            EditorGUILayout.PropertyField(Prop("outItemPosition"),
                new GUIContent("Out position", "Optional: items move towards here while processed."));
            SimuLeanGUI.Field(Prop("separation"),
                new GUIContent("Slot spacing", "Distance between the servers' slots. 0 = automatic, from the size of the items."));
            EditorGUILayout.PropertyField(Prop("serverAnimator"),
                new GUIContent("Animator", "Optional: its WorkInProgress flag is on while a slot processes."));
        }

        protected override void CollectIssues(List<InspectorIssue> issues)
        {
            if (Prop("capacity").intValue < 1)
                issues.Add(new InspectorIssue(MessageType.Error, "Servers must be at least 1.", "Set to 1", () =>
                {
                    serializedObject.Update();
                    Prop("capacity").intValue = 1;
                    serializedObject.ApplyModifiedProperties();
                }));
        }
    }
}
