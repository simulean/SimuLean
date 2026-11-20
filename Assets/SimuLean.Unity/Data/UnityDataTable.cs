using System.Collections.Generic;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A named data table of the model: rows typed here, a file (CSV / <c>.data</c> / Excel) or
    /// an SQL query on the project database. Every run starts from a snapshot taken when the scene is extracted
    /// (<see cref="Snapshot"/>; never read during the run): SQL is queried then, a file is read then with
    /// <see cref="readFileAtRun"/>, else its copy loaded in the inspector is used (with a warning when the file's content changed since). The engine reads it by name: <c>clock.Context.Data["name"]</c>.
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Data, "Data Table",
                  Description = "A named table of model data: typed rows, a CSV / Excel file or an SQL query on the project database",
                  Order = 0)]
    [DisallowMultipleComponent]
    public class UnityDataTable : MonoBehaviour
    {
        [Tooltip("The table's name in the model (empty = the GameObject's name).")]
        public string tableName = "";

        [Tooltip("Where the rows come from: typed here (Inline), a file, or an SQL query on the project database.")]
        public DataSourceKind source = DataSourceKind.Inline;

        [Tooltip("File source: CSV, .data or Excel; absolute, or relative to the project / Assets.")]
        public string fileName = "";
        [Tooltip("File source, Excel: the sheet (empty = the first).")]
        public string sheetName = "";
        [Tooltip("File source: read the file when each run starts; off = the copy loaded in the inspector (a warning tells " +
                 "when the file's content changed since).")]
        public bool readFileAtRun = false;

        [Tooltip("SQL source: a SELECT on the project database (Project Settings ▸ SimuLean), run when each run starts.")]
        [TextArea(2, 6)] public string sql = "SELECT * FROM my_table";

        // The rows: typed here (Inline) or the loaded copy of the file / query.
        [SerializeField] private SerializableDataDict data;
        // The file's content when it was loaded (FileFingerprint; empty = unknown). Content, not the write time: a git
        // checkout gives every file a new time, which made every fresh clone warn.
        [SerializeField, HideInInspector] private string loadedFileHash = "";

        /// <summary>The table's name in the model.</summary>
        public string TableName => string.IsNullOrWhiteSpace(tableName) ? gameObject.name : tableName.Trim();

        /// <summary>The rows held here (typed, or the loaded copy); empty when none.</summary>
        public Dictionary<string, List<string>> Rows
        {
            get => data != null && data.columnNames != null && data.columnNames.Count > 0 ? data.ToDict() : new Dictionary<string, List<string>>();
            set => data = SerializableDataDict.FromDict(value ?? new Dictionary<string, List<string>>());
        }

        /// <summary>The file on disk (as given, under the project, or under <c>Assets/</c>), null when there is none.</summary>
        public string ResolveFile()
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            foreach (string candidate in new[] { fileName, System.IO.Path.Combine(ResultsOutput.ProjectRoot, fileName), System.IO.Path.Combine(Application.dataPath, fileName) })
                if (System.IO.File.Exists(candidate)) return System.IO.Path.GetFullPath(candidate);
            return null;
        }

        /// <summary>Reads the source now and keeps the rows here (the inspector's <i>Load</i>): the file, or the query on
        /// <paramref name="databasePath"/>. Inline tables are left as they are.</summary>
        public void Load(string databasePath)
        {
            if (source == DataSourceKind.Inline) return;
            Rows = Read(databasePath);
            string path = source == DataSourceKind.File ? ResolveFile() : null;
            loadedFileHash = path != null ? FileFingerprint(path) : "";
        }

        /// <summary>The file's content as a SHA-256 hex string, carriage returns left out (a checkout may turn LF into
        /// CRLF without changing the table).</summary>
        public static string FileFingerprint(string path)
        {
            byte[] bytes = System.IO.File.ReadAllBytes(path);
            var text = new List<byte>(bytes.Length);
            foreach (byte b in bytes) if (b != (byte)'\r') text.Add(b);
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return System.BitConverter.ToString(sha.ComputeHash(text.ToArray())).Replace("-", "");
        }

        /// <summary>
        /// The table as the run takes it (extraction): its config with the snapshot. SQL is queried now; a file is read
        /// now with <see cref="readFileAtRun"/>, else the loaded copy is used — <paramref name="warning"/> tells when the
        /// file's content differs from it. Errors name the table.
        /// </summary>
        public DataTableConfig Snapshot(string databasePath, out string warning)
        {
            warning = null;
            var config = new DataTableConfig { Name = TableName, Source = source, File = fileName, Sheet = sheetName, Sql = sql };
            switch (source)
            {
                case DataSourceKind.Sql:
                    config.Columns = Read(databasePath);
                    break;
                case DataSourceKind.File when readFileAtRun:
                    config.Columns = Read(databasePath);
                    break;
                case DataSourceKind.File:
                    string path = ResolveFile();
                    if (path != null && !string.IsNullOrEmpty(loadedFileHash) && FileFingerprint(path) != loadedFileHash)
                        warning = $"Data table '{TableName}': the file '{fileName}' changed after it was loaded — the run uses the " +
                                  "loaded copy. Load it again, or tick 'Read file at run'.";
                    var copy = Rows;
                    config.Columns = copy.Count > 0 ? copy : Read(databasePath);   // never loaded: read it now
                    break;
                default:
                    config.Columns = Rows;
                    break;
            }
            return config;
        }

        private Dictionary<string, List<string>> Read(string databasePath)
        {
            var probe = new DataTableConfig { Name = TableName, Source = source, File = ResolveFile() ?? fileName, Sheet = sheetName, Sql = sql };
            return SimuLean.Data.DataResolver.Read(probe, databasePath, (file, sheet) => TableFiles.Read(file, sheet));
        }
    }
}
