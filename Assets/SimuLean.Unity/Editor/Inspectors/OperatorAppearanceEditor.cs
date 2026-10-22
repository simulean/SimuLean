using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>The person's look as a list of the catalog's looks (<see cref="PeopleLooks"/>), the rest as usual.</summary>
    [CustomEditor(typeof(OperatorAppearance))]
    [CanEditMultipleObjects]
    public class OperatorAppearanceEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var catalog = serializedObject.FindProperty("catalog");
            var look = serializedObject.FindProperty("look");
            EditorGUILayout.PropertyField(catalog);
            var names = (catalog.objectReferenceValue as PeopleLooks)?.Names().ToArray();
            if (names != null && names.Length > 0 && !look.hasMultipleDifferentValues)
            {
                int i = System.Array.FindIndex(names, n => string.Equals(n, look.stringValue, System.StringComparison.OrdinalIgnoreCase));
                int j = EditorGUILayout.Popup(new GUIContent("Look", look.tooltip), Mathf.Max(0, i), names);
                if (j != i) look.stringValue = names[j];
            }
            else EditorGUILayout.PropertyField(look);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("gender"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("helmet"));
            if (catalog.objectReferenceValue == null) EditorGUILayout.PropertyField(serializedObject.FindProperty("femaleModel"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
