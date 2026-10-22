using UnityEditor;
using UnityEngine;
using UnityEditorInternal;

namespace SimuLean.Unity.Editor
{
    [CustomEditor(typeof(SElementLabels))]
    public class SElementLabelsEditor : UnityEditor.Editor
    {
        private ReorderableList labelList;
        private SerializedProperty labelPairsProperty;

        private void OnEnable()
        {
            labelPairsProperty = serializedObject.FindProperty("labelPairs");
            
            labelList = new ReorderableList(serializedObject, labelPairsProperty, true, true, true, true);

            // Draw header
            labelList.drawHeaderCallback = (Rect rect) =>
            {
                EditorGUI.LabelField(new Rect(rect.x, rect.y, rect.width * 0.4f, EditorGUIUtility.singleLineHeight), "Key");
                EditorGUI.LabelField(new Rect(rect.x + rect.width * 0.4f, rect.y, rect.width * 0.6f, EditorGUIUtility.singleLineHeight), "Value");
            };

            // Draw each element
            labelList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
            {
                var element = labelPairsProperty.GetArrayElementAtIndex(index);
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
            labelList.elementHeight = EditorGUIUtility.singleLineHeight + 4;

            // On add callback
            labelList.onAddCallback = (ReorderableList list) =>
            {
                var index = list.serializedProperty.arraySize;
                list.serializedProperty.arraySize++;
                list.index = index;

                var element = list.serializedProperty.GetArrayElementAtIndex(index);
                element.FindPropertyRelative("key").stringValue = "newLabel";
                element.FindPropertyRelative("value").doubleValue = 0.0;
            };
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            SElementLabels component = (SElementLabels)target;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Labels are key-value pairs that can be referenced in expressions.\n" +
                "Example: element.GetLabelValue('efficiency') * item.GetLabelValue('baseTime')",
                MessageType.Info
            );

            EditorGUILayout.Space();

            // Draw the list
            labelList.DoLayoutList();

            EditorGUILayout.Space();

            // Show label count
            EditorGUILayout.LabelField("Total Labels:", component.Count.ToString(), EditorStyles.boldLabel);

            EditorGUILayout.Space();

            // Utility buttons
            EditorGUILayout.BeginHorizontal();
            
            if (GUILayout.Button("Rebuild Dictionary"))
            {
                component.RebuildLabelsDictionary();
                EditorUtility.SetDirty(target);
            }

            if (GUILayout.Button("Clear All"))
            {
                if (EditorUtility.DisplayDialog("Clear All Labels", 
                    "Are you sure you want to remove all labels?", 
                    "Yes", "Cancel"))
                {
                    component.ClearLabels();
                    EditorUtility.SetDirty(target);
                }
            }

            EditorGUILayout.EndHorizontal();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
