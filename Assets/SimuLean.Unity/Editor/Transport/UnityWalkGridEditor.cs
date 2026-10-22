using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>Inspector for <see cref="UnityWalkGrid"/>: bake button and the free / blocked count.</summary>
    [CustomEditor(typeof(UnityWalkGrid))]
    public class UnityWalkGridEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script", "rows");
            serializedObject.ApplyModifiedProperties();
            var g = (UnityWalkGrid)target;
            EditorGUILayout.Space();
            if (GUILayout.Button("Bake walkable area"))
            {
                Undo.RecordObject(g, "Bake walk grid");
                g.Bake();
                EditorUtility.SetDirty(g);
                SceneView.RepaintAll();
            }
            int blocked = 0, total = 0;
            if (g.rows != null) foreach (var r in g.rows) foreach (char c in r) { total++; if (c == '#') blocked++; }
            EditorGUILayout.HelpBox(total == 0 ? "Not baked yet (it bakes on its first use)."
                : $"{g.Width} × {g.Depth} cells, {blocked} blocked. Re-bake after moving obstacles.", MessageType.None);
        }
    }
}
