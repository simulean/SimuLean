using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>Inspector for <see cref="UnityTravelMatrix"/>: fill buttons (straight line, network routes).</summary>
    [CustomEditor(typeof(UnityTravelMatrix))]
    public class UnityTravelMatrixEditor : UnityEditor.Editor
    {
        private UnityTransportNetwork network;
        private UnityWalkGrid walkGrid;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var m = (UnityTravelMatrix)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Fill all pairs of the nodes", EditorStyles.boldLabel);
            if (GUILayout.Button("Straight-line distances"))
            {
                Undo.RecordObject(m, "Fill travel matrix");
                m.FillStraightLine();
                EditorUtility.SetDirty(m);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (network == null) network = UnityCompat.FindAny<UnityTransportNetwork>();
                network = (UnityTransportNetwork)EditorGUILayout.ObjectField(network, typeof(UnityTransportNetwork), true);
                using (new EditorGUI.DisabledScope(network == null))
                    if (GUILayout.Button("Network routes", GUILayout.Width(120)))
                    {
                        Undo.RecordObject(m, "Fill travel matrix");
                        m.FillFromNetwork(network);
                        EditorUtility.SetDirty(m);
                    }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (walkGrid == null) walkGrid = UnityCompat.FindAny<UnityWalkGrid>();
                walkGrid = (UnityWalkGrid)EditorGUILayout.ObjectField(walkGrid, typeof(UnityWalkGrid), true);
                using (new EditorGUI.DisabledScope(walkGrid == null))
                    if (GUILayout.Button("Walk grid (A*)", GUILayout.Width(120)))
                    {
                        Undo.RecordObject(m, "Fill travel matrix");
                        m.FillFromWalkGrid(walkGrid);
                        EditorUtility.SetDirty(m);
                    }
            }
            EditorGUILayout.HelpBox($"{m.entries.Count} distances. Nodes need a position only (no tracks).", MessageType.None);
        }
    }
}
