using System;
using System.Collections.Generic;
using System.IO;

namespace SimuLean.Data
{
    /// <summary>
    /// Per-project SQLite database — SimuLean's structured data store
    /// (FlexSim-style "global tables", but real SQL). Elements can read model
    /// data (pick lists, task tables, parameters) from here instead of
    /// Inspector fields or spreadsheets.
    ///
    /// Thread-safe: a single connection guarded by a lock, so headless GA
    /// evaluations may read concurrently. Keep engine code free of Unity
    /// dependencies — callers supply the file path (the Unity layer resolves
    /// the default under the project root).
    /// </summary>
    public class ProjectDatabase : IDisposable
    {
        private IntPtr db;
        private readonly object sync = new object();

        /// <summary>Absolute path of the database file.</summary>
        public string Path { get; }

        /// <summary>Opens (or creates) the database file at <paramref name="path"/>.</summary>
        public ProjectDatabase(string path)
        {
            Path = path;

            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            int rc = SqliteInterop.Open(path, out db);
            if (rc != SqliteInterop.SQLITE_OK)
            {
                string msg = db != IntPtr.Zero ? SqliteInterop.ErrMsg(db) : "rc=" + rc;
                if (db != IntPtr.Zero) SqliteInterop.Close(db);
                db = IntPtr.Zero;
                throw new InvalidOperationException($"ProjectDatabase: cannot open '{path}': {msg}");
            }
        }

        /// <summary>
        /// Runs a SELECT and returns rows as column-name → value dictionaries.
        /// Use '?' placeholders with <paramref name="parameters"/> for values
        /// (never concatenate user data into SQL).
        /// </summary>
        public List<Dictionary<string, object>> Query(string sql, params object[] parameters)
        {
            lock (sync)
            {
                EnsureOpen();
                IntPtr stmt = PrepareAndBind(sql, parameters);
                try
                {
                    var rows = new List<Dictionary<string, object>>();
                    int colCount = -1;
                    string[] names = null;

                    int rc;
                    while ((rc = SqliteInterop.Step(stmt)) == SqliteInterop.SQLITE_ROW)
                    {
                        if (names == null)
                        {
                            colCount = SqliteInterop.ColumnCount(stmt);
                            names = new string[colCount];
                            for (int i = 0; i < colCount; i++)
                                names[i] = SqliteInterop.ColumnName(stmt, i);
                        }

                        var row = new Dictionary<string, object>(colCount);
                        for (int i = 0; i < colCount; i++)
                            row[names[i]] = ReadColumn(stmt, i);
                        rows.Add(row);
                    }

                    if (rc != SqliteInterop.SQLITE_DONE)
                        throw new InvalidOperationException(
                            $"ProjectDatabase.Query failed: {SqliteInterop.ErrMsg(db)}\nSQL: {sql}");

                    return rows;
                }
                finally
                {
                    SqliteInterop.Finalize(stmt);
                }
            }
        }

        /// <summary>
        /// Runs a SELECT and returns it as columns (name → cells as invariant text; NULL = ""), the columns named even
        /// when no row comes back (a data table's snapshot).
        /// </summary>
        public Dictionary<string, List<string>> QueryTable(string sql, params object[] parameters)
        {
            lock (sync)
            {
                EnsureOpen();
                IntPtr stmt = PrepareAndBind(sql, parameters);
                try
                {
                    int count = SqliteInterop.ColumnCount(stmt);
                    var names = new string[count];
                    var table = new Dictionary<string, List<string>>();
                    var cells = new List<string>[count];
                    for (int i = 0; i < count; i++)
                    {
                        string name = SqliteInterop.ColumnName(stmt, i);
                        if (table.ContainsKey(name)) throw new FormatException($"ProjectDatabase.QueryTable: column '{name}' appears twice.\nSQL: {sql}");
                        names[i] = name;
                        table[name] = cells[i] = new List<string>();
                    }
                    int rc;
                    while ((rc = SqliteInterop.Step(stmt)) == SqliteInterop.SQLITE_ROW)
                        for (int i = 0; i < count; i++)
                        {
                            object v = ReadColumn(stmt, i);
                            // doubles exact ("R": Unity's runtime writes 15 digits by default)
                            cells[i].Add(v == null ? "" : v is double dv ? dv.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                                         : v is IFormattable f ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) : v.ToString());
                        }
                    if (rc != SqliteInterop.SQLITE_DONE)
                        throw new InvalidOperationException($"ProjectDatabase.QueryTable failed: {SqliteInterop.ErrMsg(db)}\nSQL: {sql}");
                    return table;
                }
                finally
                {
                    SqliteInterop.Finalize(stmt);
                }
            }
        }

        /// <summary>Runs an INSERT and returns the new row's id, read under the same lock (another thread's insert cannot
        /// come between them).</summary>
        public long Insert(string sql, params object[] parameters)
        {
            lock (sync)
            {
                Execute(sql, parameters);
                return Convert.ToInt64(Query("SELECT last_insert_rowid() AS id")[0]["id"], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Runs a non-query statement (CREATE/INSERT/UPDATE/DELETE).</summary>
        public void Execute(string sql, params object[] parameters)
        {
            lock (sync)
            {
                EnsureOpen();
                IntPtr stmt = PrepareAndBind(sql, parameters);
                try
                {
                    int rc = SqliteInterop.Step(stmt);
                    if (rc != SqliteInterop.SQLITE_DONE && rc != SqliteInterop.SQLITE_ROW)
                        throw new InvalidOperationException(
                            $"ProjectDatabase.Execute failed: {SqliteInterop.ErrMsg(db)}\nSQL: {sql}");
                }
                finally
                {
                    SqliteInterop.Finalize(stmt);
                }
            }
        }

        /// <summary>
        /// Runs one prepared statement for every parameter row inside a single
        /// transaction (fast bulk insert/update). Returns the number of rows executed.
        /// On error the transaction is rolled back and the exception rethrown.
        /// </summary>
        public int ExecuteBatch(string sql, IEnumerable<object[]> rows)
        {
            lock (sync)
            {
                EnsureOpen();
                ExecRaw("BEGIN TRANSACTION");
                IntPtr stmt = IntPtr.Zero;
                int count = 0;
                try
                {
                    stmt = PrepareAndBind(sql, null);
                    foreach (object[] row in rows)
                    {
                        SqliteInterop.Reset(stmt);
                        SqliteInterop.ClearBindings(stmt);
                        Bind(stmt, row);
                        int rc = SqliteInterop.Step(stmt);
                        if (rc != SqliteInterop.SQLITE_DONE && rc != SqliteInterop.SQLITE_ROW)
                            throw new InvalidOperationException(
                                $"ProjectDatabase.ExecuteBatch failed at row {count}: {SqliteInterop.ErrMsg(db)} | SQL: {sql}");
                        count++;
                    }
                    SqliteInterop.Finalize(stmt);
                    stmt = IntPtr.Zero;
                    ExecRaw("COMMIT");
                    return count;
                }
                catch
                {
                    if (stmt != IntPtr.Zero) SqliteInterop.Finalize(stmt);
                    try { ExecRaw("ROLLBACK"); } catch { /* keep the original error */ }
                    throw;
                }
            }
        }

        private void ExecRaw(string sql)
        {
            IntPtr stmt = PrepareAndBind(sql, null);
            try { SqliteInterop.Step(stmt); }
            finally { SqliteInterop.Finalize(stmt); }
        }

        /// <summary>True when a table with the given name exists.</summary>
        public bool TableExists(string tableName)
        {
            var rows = Query(
                "SELECT name FROM sqlite_master WHERE type='table' AND name=?", tableName);
            return rows.Count > 0;
        }

        /// <summary>The names of the user tables, sorted (the SimuLean window's Data tab).</summary>
        public List<string> ListTables()
        {
            var names = new List<string>();
            foreach (var row in Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"))
                names.Add(Convert.ToString(row["name"], System.Globalization.CultureInfo.InvariantCulture));
            return names;
        }

        /// <summary>
        /// Writes <paramref name="columns"/> (name → cells, e.g. a CSV / Excel file) as the table <paramref name="name"/>
        /// (import into the project database): a column is REAL when every non-empty cell is a number
        /// (invariant), else TEXT; empty cells are NULL. <paramref name="replace"/> drops an existing table first, else
        /// the rows are appended (its columns must match). One transaction; returns the rows written.
        /// </summary>
        public int ImportTable(string name, IDictionary<string, List<string>> columns, bool replace = true)
        {
            if (!SafeIdentifier(name)) throw new ArgumentException($"Import: '{name}' is not a valid table name (letters, digits, '_', not starting with a digit).");
            if (columns == null || columns.Count == 0) throw new ArgumentException($"Import into '{name}': no columns.");
            var names = new List<string>(columns.Keys);
            foreach (string c in names)
                if (!SafeIdentifier(c)) throw new ArgumentException($"Import into '{name}': '{c}' is not a valid column name (letters, digits, '_').");
            int rows = -1;
            foreach (var kv in columns)
            {
                if (rows >= 0 && kv.Value.Count != rows) throw new ArgumentException($"Import into '{name}': column '{kv.Key}' has {kv.Value.Count} rows, others {rows}.");
                rows = kv.Value.Count;
            }
            var numeric = new bool[names.Count];
            for (int c = 0; c < names.Count; c++)
            {
                numeric[c] = true;
                foreach (string cell in columns[names[c]])
                    if (!string.IsNullOrWhiteSpace(cell) && !ModelTable.TryNumber(cell, out _)) { numeric[c] = false; break; }
            }
            var data = new List<object[]>(rows);
            for (int r = 0; r < rows; r++)
            {
                var row = new object[names.Count];
                for (int c = 0; c < names.Count; c++)
                {
                    string cell = columns[names[c]][r];
                    if (string.IsNullOrWhiteSpace(cell)) row[c] = null;
                    else if (numeric[c]) { ModelTable.TryNumber(cell, out double v); row[c] = v; }
                    else row[c] = cell;
                }
                data.Add(row);
            }
            if (replace || !TableExists(name))
            {
                if (replace) Execute($"DROP TABLE IF EXISTS \"{name}\"");
                var defs = new List<string>();
                for (int c = 0; c < names.Count; c++) defs.Add($"\"{names[c]}\" {(numeric[c] ? "REAL" : "TEXT")}");
                Execute($"CREATE TABLE \"{name}\" ({string.Join(", ", defs)})");
            }
            string cols = string.Join(", ", names.ConvertAll(c => "\"" + c + "\""));
            string marks = string.Join(", ", names.ConvertAll(_ => "?"));
            return ExecuteBatch($"INSERT INTO \"{name}\" ({cols}) VALUES ({marks})", data);
        }

        private static bool SafeIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s) || char.IsDigit(s[0])) return false;
            foreach (char ch in s) if (!(char.IsLetterOrDigit(ch) || ch == '_')) return false;
            return true;
        }

        // ── internals ────────────────────────────────────────────────────────

        private void EnsureOpen()
        {
            if (db == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(ProjectDatabase));
        }

        private IntPtr PrepareAndBind(string sql, object[] parameters)
        {
            int rc = SqliteInterop.Prepare(db, sql, out IntPtr stmt);
            if (rc != SqliteInterop.SQLITE_OK)
                throw new InvalidOperationException(
                    $"ProjectDatabase: prepare failed: {SqliteInterop.ErrMsg(db)}\nSQL: {sql}");

            Bind(stmt, parameters);
            return stmt;
        }

        private static void Bind(IntPtr stmt, object[] parameters)
        {
            if (parameters == null) return;
            for (int i = 0; i < parameters.Length; i++)
            {
                object p = parameters[i];
                int index = i + 1;   // SQLite bind indices are 1-based
                if      (p == null)          SqliteInterop.BindText(stmt, index, null);
                else if (p is int pi)        SqliteInterop.BindInt64(stmt, index, pi);
                else if (p is long pl)       SqliteInterop.BindInt64(stmt, index, pl);
                else if (p is double pd)     SqliteInterop.BindDouble(stmt, index, pd);
                else if (p is float pf)      SqliteInterop.BindDouble(stmt, index, pf);
                else if (p is bool pb)       SqliteInterop.BindInt64(stmt, index, pb ? 1 : 0);
                else                         SqliteInterop.BindText(stmt, index, System.Convert.ToString(p, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        private static object ReadColumn(IntPtr stmt, int i)
        {
            switch (SqliteInterop.ColumnType(stmt, i))
            {
                case SqliteInterop.SQLITE_INTEGER: return SqliteInterop.ColumnInt64(stmt, i);
                case SqliteInterop.SQLITE_FLOAT:   return SqliteInterop.ColumnDouble(stmt, i);
                case SqliteInterop.SQLITE_NULL:    return null;
                default:                           return SqliteInterop.ColumnText(stmt, i);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (db != IntPtr.Zero)
                {
                    SqliteInterop.Close(db);
                    db = IntPtr.Zero;
                }
            }
        }
    }
}
