using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SimuLean.Data
{
    /// <summary>
    /// An in-memory result table (KPIs, event logs, per-run summaries) that is
    /// filled during a run without touching disk and written to the project SQLite
    /// database in one transaction by <see cref="WriteTo"/>. Keeping rows in memory
    /// makes it safe for parallel headless runs (each model owns its tables); only the
    /// final write goes through the shared, thread-safe <see cref="ProjectDatabase"/>.
    /// </summary>
    public sealed class ResultTable
    {
        private readonly List<object[]> rows = new List<object[]>();

        public string Name { get; }
        public IReadOnlyList<string> Columns { get; }
        public IReadOnlyList<object[]> Rows => rows;
        public int RowCount => rows.Count;

        public ResultTable(string name, params string[] columns)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Table name required.", nameof(name));
            if (columns == null || columns.Length == 0) throw new ArgumentException("At least one column required.", nameof(columns));
            Name = name;
            Columns = columns;
        }

        /// <summary>Appends a row (values in column order; int/long/double/bool/string/null).</summary>
        public void Add(params object[] values)
        {
            if (values == null || values.Length != Columns.Count)
                throw new ArgumentException($"Table '{Name}' expects {Columns.Count} values.");
            rows.Add(values);
        }

        public void Clear() => rows.Clear();

        /// <summary>
        /// Writes the table to <paramref name="db"/>: drops and recreates it (column types
        /// inferred from the first non-null value of each column), then inserts all rows
        /// in one transaction. <paramref name="tableName"/> overrides <see cref="Name"/>.
        /// </summary>
        public void WriteTo(ProjectDatabase db, string tableName = null, bool replace = true)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            string table = Quote(tableName ?? Name);

            var ddl = new StringBuilder();
            ddl.Append("CREATE TABLE IF NOT EXISTS ").Append(table).Append(" (");
            for (int c = 0; c < Columns.Count; c++)
            {
                if (c > 0) ddl.Append(", ");
                ddl.Append(Quote(Columns[c])).Append(' ').Append(SqlType(c));
            }
            ddl.Append(')');

            if (replace) db.Execute("DROP TABLE IF EXISTS " + table);
            db.Execute(ddl.ToString());
            if (!replace)
            {
                // appending to a table written before with other columns (an optimisation over other parameters): the
                // missing columns are added and every value goes under its column by name, not by position (code
                // "N columns but M values", or values under the first set's column names)
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in db.Query("PRAGMA table_info(" + table + ")")) existing.Add(Convert.ToString(row["name"], CultureInfo.InvariantCulture));
                for (int c = 0; c < Columns.Count; c++)
                    if (!existing.Contains(Columns[c]))
                        db.Execute("ALTER TABLE " + table + " ADD COLUMN " + Quote(Columns[c]) + " " + SqlType(c));
            }

            var insert = new StringBuilder("INSERT INTO ").Append(table).Append(" (");
            for (int c = 0; c < Columns.Count; c++) insert.Append(c == 0 ? "" : ", ").Append(Quote(Columns[c]));
            insert.Append(") VALUES (");
            for (int c = 0; c < Columns.Count; c++) insert.Append(c == 0 ? "?" : ", ?");
            insert.Append(')');
            db.ExecuteBatch(insert.ToString(), rows);
        }

        /// <summary>Writes the table as CSV text (invariant culture).</summary>
        public string ToCsv(char separator = ',')
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(separator.ToString(), Columns));
            foreach (var r in rows)
            {
                for (int c = 0; c < r.Length; c++)
                {
                    if (c > 0) sb.Append(separator);
                    sb.Append(Csv(r[c], separator));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private string SqlType(int column)
        {
            foreach (var r in rows)
            {
                object v = r[column];
                if (v == null) continue;
                if (v is int || v is long || v is bool) return "INTEGER";
                if (v is double || v is float) return "REAL";
                return "TEXT";
            }
            return "";
        }

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

        private static string Csv(object v, char sep)
        {
            if (v == null) return "";
            string s = v is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : v.ToString();
            return s.IndexOf(sep) >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0
                ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
