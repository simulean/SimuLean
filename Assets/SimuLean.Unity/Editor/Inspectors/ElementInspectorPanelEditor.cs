using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// Element Inspector Panel inspector: the inspectable elements (kept filled automatically) with
    /// buttons to clear the list or list every element again.
    /// </summary>
    [CustomEditor(typeof(ElementInspectorPanel))]
    public class ElementInspectorPanelEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var panel = (ElementInspectorPanel)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("simClock"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("cameraController"));
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("elements"), new GUIContent("Inspectable Elements"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("autoCollectElements"), new GUIContent("Auto Collect"));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add All Scene Elements"))
            {
                Undo.RecordObject(panel, "Add all scene elements");
                panel.AddAllSceneElements();
                EditorUtility.SetDirty(panel);
            }
            if (GUILayout.Button("Clear"))
            {
                Undo.RecordObject(panel, "Clear inspectable elements");
                panel.ClearElements();
                EditorUtility.SetDirty(panel);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("contributors"), true);
            DrawPropertiesExcluding(serializedObject, "m_Script", "simClock", "cameraController", "elements",
                                    "autoCollectElements", "contributors");
            serializedObject.ApplyModifiedProperties();
        }
    }

    /// <summary>
    /// Keeps the panels' element lists up to date while the scene is edited (auto collection). The list is not
    /// marked dirty: the panel gathers the elements again at run start (<c>Awake</c>), and the edit that added an
    /// element has already dirtied the scene. Opening a scene therefore never leaves it modified. A modified scene
    /// would make the next test run stop on Unity's "Scene(s) Have Been Modified" dialog, which blocks MCP clients.
    /// </summary>
    [InitializeOnLoad]
    internal static class ElementInspectorPanelSync
    {
        static ElementInspectorPanelSync()
        {
            EditorApplication.hierarchyChanged += Sync;
        }

        private static void Sync()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            foreach (var panel in UnityCompat.FindAll<ElementInspectorPanel>(true))
                if (panel != null && panel.autoCollectElements) panel.GatherSceneElements();
        }
    }
}
