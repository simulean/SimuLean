using System.Linq;
using SimuLean.Naval;
using SimuLean.Unity.Naval;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor.Naval
{
    /// <summary>
    /// Inspector of <see cref="UnityBlockBreakdown"/>: import an Excel / CSV file into the breakdown table of the project
    /// database, check it (errors / warnings, summary), and point the linked sources at its parts.
    /// </summary>
    [CustomEditor(typeof(UnityBlockBreakdown))]
    public class UnityBlockBreakdownEditor : UnityEditor.Editor
    {
        private string report;
        private MessageType reportType = MessageType.None;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var bd = (UnityBlockBreakdown)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Database: " + bd.DatabasePath(), EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Import Excel / CSV…")) Import(bd);
                if (GUILayout.Button("Check")) Check(bd);
                if (GUILayout.Button("Apply to sources")) Apply(bd);
            }
            if (!string.IsNullOrEmpty(report)) EditorGUILayout.HelpBox(report, reportType);
        }

        private void Import(UnityBlockBreakdown bd)
        {
            string path = EditorUtility.OpenFilePanelWithFilters("Block breakdown", Application.dataPath,
                new[] { "Tables", "xlsx,xls,csv,data", "All files", "*" });
            if (string.IsNullOrEmpty(path)) return;
            if (bd.table.TrimStart().StartsWith("select", System.StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("Import", "The breakdown is a query: import into a table name instead.", "OK");
                return;
            }
            try
            {
                var breakdown = BlockBreakdown.FromTable(TableFiles.Read(path, ""));
                var db = SimuLean.Data.ProjectDatabaseRegistry.GetOrOpen(bd.DatabasePath());
                breakdown.Save(db, bd.table);
                Show(breakdown, $"Imported {System.IO.Path.GetFileName(path)} into '{bd.table}'.");
            }
            catch (System.Exception e) { report = "Import failed: " + e.Message; reportType = MessageType.Error; }
        }

        private void Check(UnityBlockBreakdown bd)
        {
            try { Show(bd.Load(), "Breakdown '" + bd.table + "'."); }
            catch (System.Exception e) { report = e.Message; reportType = MessageType.Error; }
        }

        private void Show(BlockBreakdown breakdown, string head)
        {
            var issues = breakdown.Validate();
            int errors = issues.Count(i => i.IsError);
            report = head + "\n" + UnityBlockBreakdown.Summary(breakdown) +
                     (issues.Count == 0 ? "\nNo problems." : "\n" + string.Join("\n", issues.Take(12).Select(i => "• " + i))) +
                     (issues.Count > 12 ? $"\n… {issues.Count - 12} more" : "");
            reportType = errors > 0 ? MessageType.Error : issues.Count > 0 ? MessageType.Warning : MessageType.Info;
        }

        private void Apply(UnityBlockBreakdown bd)
        {
            var sources = bd.Links().Where(l => l.source != null).Select(l => l.source).ToArray();
            if (sources.Length == 0) { report = "Link the sources first (Sources it feeds)."; reportType = MessageType.Warning; return; }
            Undo.RecordObjects(sources, "Apply block breakdown");
            int n = bd.ApplyToSources();
            foreach (var s in sources)
            {
                EditorUtility.SetDirty(s);
                s.LoadDataInEditor();                      // a preview copy of what a run reads
            }
            report = $"{n} source(s) read the breakdown's parts: " +
                     string.Join(", ", bd.Links().Where(l => l.source != null).Select(l => $"{l.source.name} ← {l.part}"));
            reportType = MessageType.Info;
        }
    }
}
