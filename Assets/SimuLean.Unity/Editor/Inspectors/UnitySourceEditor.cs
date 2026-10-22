using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>Source inspector (<see cref="SElementInspector"/>): inter-arrival or schedule table.</summary>
    [CustomEditor(typeof(UnitySource))]
    public class UnitySourceEditor : SElementInspector
    {
        private bool showDataPreview;
        private Vector2 previewScroll;

        protected override bool DrawsReceive => false;

        private SourceMode Mode => (SourceMode)Prop("mode").enumValueIndex;

        protected override void DrawParameters()
        {
            EditorGUILayout.PropertyField(Prop("mode"), new GUIContent("Mode",
                "InterArrival: items arrive with a time between arrivals.\nSchedule: items arrive as listed in a table."));
            if (Mode == SourceMode.InterArrival)
                EditorGUILayout.PropertyField(Prop("interArrivalDistribution"),
                    new GUIContent("Time between arrivals", "Constant 0 = items as fast as the next element takes them."));
            else DrawSchedule();
            EditorGUILayout.PropertyField(Prop("demandFactor"), new GUIContent("Demand factor",
                "1 = as given; 1.2 = 20 % more arrivals. A study parameter (demand scenarios)."));
            EditorGUILayout.PropertyField(Prop("assignLabels"),
                new GUIContent("Item labels", "Labels every created item gets (e.g. prio = 5)."), true);
        }

        protected override void DrawVisual()
        {
            EditorGUILayout.PropertyField(Prop("itemPrefab"), new GUIContent("Item prefab", "The visual of each created item."));
            EditorGUILayout.PropertyField(Prop("itemPosition"), new GUIContent("Item position", "Where items appear."));
            SimuLeanGUI.Field(Prop("separation"), new GUIContent("Item spacing", "Spacing of the items waiting to leave."));
        }

        protected override void CollectIssues(List<InspectorIssue> issues)
        {
            var source = (UnitySource)target;
            if (source.ReadsTableSource)
            {
                if (!source.IsDataLoaded())
                    issues.Add(new InspectorIssue(MessageType.Info, "The table is read from its table source when a run starts; load a preview copy to see it.",
                        "Load data", source.LoadDataInEditor));
            }
            else if (Mode == SourceMode.Schedule && !source.ReadsDataTable)
                issues.Add(new InspectorIssue(MessageType.Warning, "Schedule mode: choose the data table with the arrivals (Parameters).",
                    "New data table", () => NewDataTable(source)));
            if (Prop("itemPrefab").objectReferenceValue == null)
                issues.Add(new InspectorIssue(MessageType.Info, "No item prefab: the items are not shown in the scene (Visual)."));
        }

        private void DrawSchedule()
        {
            var source = (UnitySource)target;
            EditorGUILayout.PropertyField(Prop("tableSource"), new GUIContent("Table source",
                "Read when a run starts: 'sql:SELECT …' (project database) or a block breakdown part 'naval:blocks:block_breakdown'. Empty = the data table."));
            if (!string.IsNullOrWhiteSpace(source.tableSource))
                EditorGUILayout.PropertyField(Prop("tableDatabase"), new GUIContent("Database", "Empty = the project database (the Data tab)"));
            if (!source.ReadsTableSource)
            {
                // The arrivals are a data table of the model
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(Prop("dataTable"), new GUIContent("Data table",
                    "The model's data table with the arrivals: one row per arrival (Time, Name, Q and label columns). Read when each run starts."));
                if (source.dataTable == null && GUILayout.Button("New", EditorStyles.miniButton, GUILayout.Width(40))) NewDataTable(source);
                EditorGUILayout.EndHorizontal();
                if (source.dataTable != null)
                {
                    var rows = source.dataTable.Rows;
                    EditorGUILayout.LabelField(" ", $"\"{source.dataTable.TableName}\" · {source.dataTable.source} · " +
                                                    $"{(rows.Count == 0 ? 0 : rows.Values.First().Count)} rows", EditorStyles.miniLabel);
                }
                return;
            }
            // the table source: a preview copy of what a run will read
            var r = EditorGUI.PrefixLabel(EditorGUILayout.GetControlRect(), new GUIContent("Data"));
            if (source.IsDataLoaded())
            {
                var cols = source.GetColumnNames();
                float w = (r.width - 4) / 3;
                GUI.Label(new Rect(r.x, r.y, w, r.height), $"{source.GetItemCount()} rows · {cols.Count} columns", EditorStyles.miniLabel);
                if (GUI.Button(new Rect(r.x + w + 2, r.y, w, r.height), "Reload")) source.LoadDataInEditor();
                if (GUI.Button(new Rect(r.x + 2 * w + 4, r.y, w, r.height), "Clear")
                    && EditorUtility.DisplayDialog("Clear data", "Clear the loaded table?", "Clear", "Cancel")) source.ClearData();
                showDataPreview = EditorGUILayout.Foldout(showDataPreview, "Preview", true);
                if (showDataPreview) DrawDataPreview(source);
            }
            else if (GUI.Button(r, "Load a preview of the table source")) source.LoadDataInEditor();
        }

        /// <summary>A new inline data table named after the source, placed in the Data group and linked.</summary>
        private static void NewDataTable(UnitySource source)
        {
            var go = new GameObject(source.name + " table");
            Undo.RegisterCreatedObjectUndo(go, "New data table");
            var t = Undo.AddComponent<UnityDataTable>(go);
            t.tableName = source.name;
            t.Rows = new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string> { "0" }, ["Name"] = new List<string> { source.name }, ["Q"] = new List<string> { "1" },
            };
            if (ModelHierarchy.AutoOrganize) ModelHierarchy.Place(go);
            Undo.RecordObject(source, "New data table");
            source.dataTable = t;
            EditorUtility.SetDirty(source);
            Selection.activeObject = go;
        }

        private void DrawDataPreview(UnitySource source)
        {
            var data = source.GetArrivalsTable();
            if (data == null || data.Count == 0) return;
            var cols = data.Keys.ToList();
            int rows = data.Values.First().Count, shown = Mathf.Min(10, rows);
            previewScroll = EditorGUILayout.BeginScrollView(previewScroll, GUILayout.MaxHeight(220));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("#", EditorStyles.miniBoldLabel, GUILayout.Width(30));
            foreach (var c in cols) EditorGUILayout.LabelField(c, EditorStyles.miniBoldLabel, GUILayout.Width(90));
            EditorGUILayout.EndHorizontal();
            for (int i = 0; i < shown; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(i.ToString(), EditorStyles.miniLabel, GUILayout.Width(30));
                foreach (var c in cols)
                {
                    string v = data[c][i];
                    EditorGUILayout.LabelField(v.Length > 14 ? v.Substring(0, 12) + "…" : v, EditorStyles.miniLabel, GUILayout.Width(90));
                }
                EditorGUILayout.EndHorizontal();
            }
            if (rows > shown) EditorGUILayout.LabelField($"… {rows - shown} more rows", EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();
        }
    }
}
