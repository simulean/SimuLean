using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimuLean.Data;
using SimuLean.Serialization;
using UnityEditor;
using UnityEngine;

namespace SimuLean.Unity.Editor
{
    /// <summary>
    /// The Data tab: the project database as the hub of the model's data — its path (project
    /// settings), its tables (preview, use one in the scene as a data table), CSV / Excel import into a table, an SQL
    /// console, and the scene's data tables (load / refresh, select).
    /// </summary>
    public partial class SimuLeanWindow
    {
        private string dataImportFile = "", dataImportSheet = "", dataImportTable = "";
        private bool dataImportReplace = true;
        private string dataSql = "SELECT name FROM sqlite_master WHERE type = 'table'";
        private string dataMessage;
        private MessageType dataMessageType;
        private Dictionary<string, List<string>> dataPreview;
        private string dataPreviewTitle;
        private Vector2 dataPreviewScroll;
        private List<string> dataTables;

        private static string ProjectDatabasePath => ResultsOutput.DatabasePath(ResultsOutput.ProjectRoot);

        private void DrawData()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            GroupHeader("Project database", "Data", "project setting");
            {
                var s = SimuLeanProjectSettings.Load();
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.BeginHorizontal();
                s.databasePath = EditorGUILayout.TextField(new GUIContent("Database", "The project's SQLite database: relative to the project, or absolute. Data tables, timetables, pick lists and SQLite exports use it."), s.databasePath);
                if (GUILayout.Button("…", GUILayout.Width(24)))
                {
                    string picked = EditorUtility.SaveFilePanel("Project database", Path.GetDirectoryName(ProjectDatabasePath), Path.GetFileName(ProjectDatabasePath), "db");
                    if (!string.IsNullOrEmpty(picked)) { s.databasePath = Relative(picked); GUI.FocusControl(null); }
                }
                EditorGUILayout.EndHorizontal();
                if (EditorGUI.EndChangeCheck()) { s.Save(); dataTables = null; }
                bool exists = File.Exists(ProjectDatabasePath);
                EditorGUILayout.LabelField(" ", exists ? ProjectDatabasePath : "Not created yet: an import creates it.", EditorStyles.miniLabel);
            }

            GroupHeader("Tables in the database", "Data", dataTables != null ? dataTables.Count.ToString() : null);
            if (dataTables == null) dataTables = ListDatabaseTables();
            if (dataTables.Count == 0) EditorGUILayout.LabelField("No table.", EditorStyles.miniLabel);
            foreach (string table in dataTables)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(table);
                if (GUILayout.Button("Preview", EditorStyles.miniButton, GUILayout.Width(60))) RunPreview($"SELECT * FROM \"{table}\" LIMIT 200", table);
                if (GUILayout.Button(new GUIContent("Use in scene", "Adds a Data Table to the scene that reads this table (SQL) at every run start."), EditorStyles.miniButton, GUILayout.Width(86)))
                    AddSceneTable(table, DataSourceKind.Sql, $"SELECT * FROM \"{table}\"", null);
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("Refresh", EditorStyles.miniButton, GUILayout.Width(70))) dataTables = null;

            GroupHeader("Import a file into the database", "Data");
            EditorGUILayout.BeginHorizontal();
            dataImportFile = EditorGUILayout.TextField(new GUIContent("File", "CSV, .data or Excel."), dataImportFile);
            if (GUILayout.Button("…", GUILayout.Width(24)))
            {
                string picked = EditorUtility.OpenFilePanelWithFilters("Import a table", ResultsOutput.ProjectRoot, new[] { "Tables", "csv,data,xlsx,xls", "All", "*" });
                if (!string.IsNullOrEmpty(picked))
                {
                    dataImportFile = Relative(picked);
                    if (string.IsNullOrEmpty(dataImportTable)) dataImportTable = TableNameOf(picked);
                    GUI.FocusControl(null);
                }
            }
            EditorGUILayout.EndHorizontal();
            dataImportSheet = EditorGUILayout.TextField(new GUIContent("Sheet", "Excel: the sheet (empty = the first)."), dataImportSheet);
            dataImportTable = EditorGUILayout.TextField(new GUIContent("Table", "The table's name in the database (letters, digits, '_')."), dataImportTable);
            dataImportReplace = EditorGUILayout.Toggle(new GUIContent("Replace", "Replace the table if it exists; off = append the rows."), dataImportReplace);
            if (GUILayout.Button("Import")) Import();

            GroupHeader("SQL", "Data");
            dataSql = EditorGUILayout.TextArea(dataSql, GUILayout.MinHeight(40));
            if (GUILayout.Button("Run")) RunPreview(dataSql, "Query");

            GroupHeader("Data tables of the scene", "Data");
            var sceneTables = InScenes<UnityDataTable>().ToList();
            if (sceneTables.Count == 0) EditorGUILayout.LabelField("None: add one from the Library (Data) or with Use in scene.", EditorStyles.miniLabel);
            foreach (var t in sceneTables)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"{t.TableName}  ·  {t.source}  ·  {RowCount(t)} rows");
                if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(52))) Select(t);
                using (new EditorGUI.DisabledScope(t.source == DataSourceKind.Inline))
                    if (GUILayout.Button("Load", EditorStyles.miniButton, GUILayout.Width(44))) LoadSceneTable(t);
                EditorGUILayout.EndHorizontal();
            }
            if (sceneTables.Any(t => t.source != DataSourceKind.Inline) && GUILayout.Button("Load all"))
                foreach (var t in sceneTables.Where(x => x.source != DataSourceKind.Inline)) LoadSceneTable(t);

            if (!string.IsNullOrEmpty(dataMessage)) EditorGUILayout.HelpBox(dataMessage, dataMessageType);
            DrawDataPreviewTable();
            EditorGUILayout.EndScrollView();
        }

        private static List<string> ListDatabaseTables()
        {
            if (!File.Exists(ProjectDatabasePath)) return new List<string>();
            try { return ProjectDatabaseRegistry.GetOrOpen(ProjectDatabasePath).ListTables(); }
            catch (System.Exception) { return new List<string>(); }
        }

        private void Import()
        {
            try
            {
                string file = File.Exists(dataImportFile) ? dataImportFile : Path.Combine(ResultsOutput.ProjectRoot, dataImportFile);
                var columns = TableFiles.Read(file, string.IsNullOrEmpty(dataImportSheet) ? null : dataImportSheet);
                Directory.CreateDirectory(Path.GetDirectoryName(ProjectDatabasePath));
                int n = ProjectDatabaseRegistry.GetOrOpen(ProjectDatabasePath).ImportTable(dataImportTable, columns, dataImportReplace);
                Message($"Imported {n} rows into '{dataImportTable}'.", MessageType.Info);
                dataTables = null;
                RunPreview($"SELECT * FROM \"{dataImportTable}\" LIMIT 200", dataImportTable);
            }
            catch (System.Exception e) { Message("Import failed: " + e.Message, MessageType.Error); }
        }

        private void RunPreview(string sql, string title)
        {
            try
            {
                dataPreview = ProjectDatabaseRegistry.GetOrOpen(ProjectDatabasePath).QueryTable(sql);
                dataPreviewTitle = title;
                dataMessage = null;
            }
            catch (System.Exception e) { dataPreview = null; Message(e.Message, MessageType.Error); }
        }

        private void LoadSceneTable(UnityDataTable t)
        {
            try
            {
                Undo.RecordObject(t, "Load data table");
                t.Load(ProjectDatabasePath);
                EditorUtility.SetDirty(t);
                Message($"'{t.TableName}': {RowCount(t)} rows loaded.", MessageType.Info);
            }
            catch (System.Exception e) { Message(e.Message, MessageType.Error); }
        }

        /// <summary>Adds a Data Table to the open scene (in its hierarchy group), reading <paramref name="sql"/> or a file.</summary>
        private static void AddSceneTable(string name, DataSourceKind source, string sql, string file)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Add data table");
            var t = Undo.AddComponent<UnityDataTable>(go);
            t.tableName = name;
            t.source = source;
            if (sql != null) t.sql = sql;
            if (file != null) t.fileName = file;
            if (ModelHierarchy.AutoOrganize) ModelHierarchy.Place(go);
            Select(t);
        }

        private void DrawDataPreviewTable()
        {
            if (dataPreview == null) return;
            var cols = dataPreview.Keys.ToList();
            int n = cols.Count > 0 ? dataPreview[cols[0]].Count : 0;
            GroupHeader(dataPreviewTitle ?? "Preview", "Data", $"{n} rows · {cols.Count} columns");
            dataPreviewScroll = EditorGUILayout.BeginScrollView(dataPreviewScroll, GUILayout.MinHeight(120), GUILayout.MaxHeight(320));
            EditorGUILayout.BeginHorizontal();
            foreach (var c in cols) GUILayout.Label(c, EditorStyles.boldLabel, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();
            for (int r = 0; r < n; r++)
            {
                EditorGUILayout.BeginHorizontal();
                foreach (var c in cols) GUILayout.Label(dataPreview[c][r], GUILayout.Width(100));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        private void Message(string text, MessageType type) { dataMessage = text; dataMessageType = type; }

        private static int RowCount(UnityDataTable t)
        {
            var rows = t.Rows;
            return rows.Count == 0 ? 0 : rows.Values.First().Count;
        }

        private static string Relative(string path)
        {
            string full = Path.GetFullPath(path), root = Path.GetFullPath(ResultsOutput.ProjectRoot) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, System.StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length).Replace('\\', '/') : full;
        }

        private static string TableNameOf(string file)
        {
            var chars = Path.GetFileNameWithoutExtension(file).Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
            string name = new string(chars);
            return name.Length > 0 && char.IsDigit(name[0]) ? "t_" + name : name;
        }
    }
}
