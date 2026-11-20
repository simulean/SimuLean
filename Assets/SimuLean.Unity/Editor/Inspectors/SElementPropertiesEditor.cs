using UnityEditor;
using UnityEngine;
using UnityEditorInternal;

namespace SimuLean.Unity.Editor
{
    [CustomEditor(typeof(SElementProperties))]
    public class SElementPropertiesEditor : UnityEditor.Editor
    {
        private ReorderableList list;
        private SerializedProperty propertiesProperty;

        private void OnEnable()
        {
            propertiesProperty = serializedObject.FindProperty("properties");
            
            list = new ReorderableList(serializedObject, propertiesProperty, true, true, true, true);

            // Draw header
            list.drawHeaderCallback = (Rect rect) =>
            {
                EditorGUI.LabelField(new Rect(rect.x, rect.y, rect.width * 0.4f, EditorGUIUtility.singleLineHeight), "Name");
                EditorGUI.LabelField(new Rect(rect.x + rect.width * 0.4f, rect.y, rect.width * 0.6f, EditorGUIUtility.singleLineHeight), "Value");
            };

            // Draw each element
            list.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
            {
                var element = propertiesProperty.GetArrayElementAtIndex(index);
                var keyProperty = element.FindPropertyRelative("key");
                var valueProperty = element.FindPropertyRelative("value");

                rect.y += 2;

                // Key field (40% width)
                EditorGUI.PropertyField(
                    new Rect(rect.x, rect.y, rect.width * 0.4f - 5, EditorGUIUtility.singleLineHeight),
                    keyProperty,
                    GUIContent.none
                );

                // Value field (60% width)
                EditorGUI.PropertyField(
                    new Rect(rect.x + rect.width * 0.4f, rect.y, rect.width * 0.6f, EditorGUIUtility.singleLineHeight),
                    valueProperty,
                    GUIContent.none
                );
            };

            // Element height
            list.elementHeight = EditorGUIUtility.singleLineHeight + 4;

            // On add callback
            list.onAddCallback = (ReorderableList list) =>
            {
                var index = list.serializedProperty.arraySize;
                list.serializedProperty.arraySize++;
                list.index = index;

                var element = list.serializedProperty.GetArrayElementAtIndex(index);
                element.FindPropertyRelative("key").stringValue = "newProperty";
                element.FindPropertyRelative("value").doubleValue = 0.0;
            };
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            SElementProperties component = (SElementProperties)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "The element's properties: values of the element (not of its items) that expressions read with " +
                "get('Element', 'name'); a study varies their numbers (Element.properties.name).",
                MessageType.Info
            );

            EditorGUILayout.Space();

            // Draw the list
            list.DoLayoutList();

            EditorGUILayout.Space();

            // Show label count
            EditorGUILayout.LabelField("Properties:", component.Count.ToString(), EditorStyles.boldLabel);

            EditorGUILayout.Space();

            // Utility buttons
            EditorGUILayout.BeginHorizontal();
            
            if (GUILayout.Button("Rebuild Dictionary"))
            {
                component.Rebuild();
                EditorUtility.SetDirty(target);
            }

            if (GUILayout.Button("Clear All"))
            {
                if (EditorUtility.DisplayDialog("Clear All Properties", 
                    "Are you sure you want to remove all properties?", 
                    "Yes", "Cancel"))
                {
                    component.Clear();
                    EditorUtility.SetDirty(target);
                }
            }

            EditorGUILayout.EndHorizontal();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
