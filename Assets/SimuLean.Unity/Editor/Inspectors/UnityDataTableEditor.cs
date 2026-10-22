using System.Collections.Generic;
using System.Linq;
using SimuLean.Serialization;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>Data table inspector: the source's fields, <i>Load</i>, rows typed as CSV (inline) and a preview.</summary>
    [CustomEditor(typeof(UnityDataTable))]
    public class UnityDataTableEditor : UnityEditor.Editor
    {
        private string csv;
        private string error;
        private Vector2 scroll;

        public override void OnInspectorGUI()
        {
            var t = (UnityDataTable)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("tableName"), new GUIContent("Name", "The table's name in the model (empty = the GameObject's)."));
            EditorGUILayout.LabelField(" ", $"Read in the model as table \"{t.TableName}\"", EditorStyles.miniLabel);
            var sourceProp = serializedObject.FindProperty("source");
            EditorGUILayout.PropertyField(sourceProp, new GUIContent("Source"));
            var source = (DataSourceKind)sourceProp.enumValueIndex;
            switch (source)
            {
                case DataSourceKind.File:
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("fileName"), new GUIContent("File", "CSV, .data or Excel; relative to the project or Assets."));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("sheetName"), new GUIContent("Sheet", "Excel: the sheet (empty = the first)."));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("readFileAtRun"), new GUIContent("Read file at run", "Read the file when each run starts; off = the loaded copy."));
                    break;
                case DataSourceKind.Sql:
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("sql"), new GUIContent("SQL", "A SELECT on the project database, run when each run starts."));
                    EditorGUILayout.LabelField(" ", "Database: " + ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot), EditorStyles.miniLabel);
                    break;
            }
            serializedObject.ApplyModifiedProperties();

            if (source != DataSourceKind.Inline && GUILayout.Button(source == DataSourceKind.File ? "Load the file" : "Run the query (preview)"))
            {
                error = null;
                try
                {
                    Undo.RecordObject(t, "Load data table");
                    t.Load(ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot));
                    EditorUtility.SetDirty(t);
                }
                catch (System.Exception e) { error = e.Message; }
            }

            if (source == DataSourceKind.Inline)
            {
                EditorGUILayout.LabelField("Rows (CSV: a header line, then one line per row)", EditorStyles.boldLabel);
                if (csv == null) csv = ToCsv(t.Rows);
                csv = EditorGUILayout.TextArea(csv, GUILayout.MinHeight(80));
                if (GUILayout.Button("Apply rows"))
                {
                    error = null;
                    try
                    {
                        var rows = SimuLean.Data.TableReader.Columns(SimuLean.Data.TableReader.ParseCsv(csv ?? ""));
                        SimuLean.Data.ModelTable.From(t.TableName, rows);   // checks the shape
                        Undo.RecordObject(t, "Edit data table rows");
                        t.Rows = rows;
                        EditorUtility.SetDirty(t);
                    }
                    catch (System.Exception e) { error = e.Message; }
                }
            }
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            Preview(t.Rows);
        }

        private void Preview(Dictionary<string, List<string>> rows)
        {
            if (rows.Count == 0) { EditorGUILayout.HelpBox("No rows yet.", MessageType.Info); return; }
            var cols = rows.Keys.ToList();
            int n = rows.Values.First().Count;
            EditorGUILayout.LabelField($"{n} rows · {cols.Count} columns", EditorStyles.miniBoldLabel);
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(200));
            EditorGUILayout.BeginHorizontal();
            foreach (var c in cols) GUILayout.Label(c, EditorStyles.boldLabel, GUILayout.Width(90));
            EditorGUILayout.EndHorizontal();
            for (int r = 0; r < Mathf.Min(n, 20); r++)
            {
                EditorGUILayout.BeginHorizontal();
                foreach (var c in cols) GUILayout.Label(rows[c][r], GUILayout.Width(90));
                EditorGUILayout.EndHorizontal();
            }
            if (n > 20) EditorGUILayout.LabelField($"… {n - 20} more", EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();
        }

        private static string ToCsv(Dictionary<string, List<string>> rows)
        {
            if (rows.Count == 0) return "column1,column2\n";
            var cols = rows.Keys.ToList();
            string Q(string v) => v.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
            var lines = new List<string> { string.Join(",", cols.Select(Q)) };
            int n = rows[cols[0]].Count;
            for (int r = 0; r < n; r++) lines.Add(string.Join(",", cols.Select(c => Q(rows[c][r]))));
            return string.Join("\n", lines) + "\n";
        }
    }
}
