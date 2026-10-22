using System;
using System.Collections.Generic;
using System.IO;
using SimuLean.Serialization;

namespace SimuLean.Data
{
    /// <summary>
    /// Reads data tables from their sources and builds the model's <see cref="ModelData"/>. The
    /// snapshot rule: a table is read once, at the run's start — when the scene is extracted (Unity, with its Excel
    /// reader) or, for a config whose table has no snapshot, when the model is built — and never during the run.
    /// </summary>
    public static class DataResolver
    {
        /// <summary>
        /// Reads <paramref name="table"/>'s source: a file through <paramref name="fileReader"/> (path, sheet → columns;
        /// default <see cref="TableReader"/>: CSV / <c>.data</c>), an SQL query on <paramref name="databasePath"/>, or the
        /// inline rows as they are. Errors name the table and the source.
        /// </summary>
        public static Dictionary<string, List<string>> Read(DataTableConfig table, string databasePath,
                                                            Func<string, string, Dictionary<string, List<string>>> fileReader = null)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            string name = table.Name ?? "?";
            switch (table.Source)
            {
                case DataSourceKind.Inline:
                    return table.Columns ?? new Dictionary<string, List<string>>();
                case DataSourceKind.File:
                    if (string.IsNullOrWhiteSpace(table.File)) throw new FormatException($"Data table '{name}': no file.");
                    try
                    {
                        return fileReader != null ? fileReader(table.File, table.Sheet) : TableReader.Read(table.File);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException || e is FormatException)
                    {
                        throw new FormatException($"Data table '{name}': the file '{table.File}' could not be read ({e.Message}).", e);
                    }
                case DataSourceKind.Sql:
                    if (string.IsNullOrWhiteSpace(table.Sql)) throw new FormatException($"Data table '{name}': no SQL query.");
                    if (string.IsNullOrWhiteSpace(databasePath)) throw new FormatException($"Data table '{name}': an SQL table needs the project database (no database path).");
                    try
                    {
                        return ProjectDatabaseRegistry.GetOrOpen(databasePath).QueryTable(table.Sql);
                    }
                    catch (Exception e) when (e is InvalidOperationException || e is IOException || e is DllNotFoundException || e is FormatException)
                    {
                        throw new FormatException($"Data table '{name}': the query failed ({e.Message}).", e);
                    }
                default:
                    throw new FormatException($"Data table '{name}': unknown source '{table.Source}'.");
            }
        }

        /// <summary>
        /// The model's tables from <paramref name="config"/>: each table's snapshot, read from its source when it has none
        /// (<see cref="Read"/>). Every table is checked (a name, unique, columns of equal length).
        /// </summary>
        public static ModelData Build(SimulationConfig config, Func<string, string, Dictionary<string, List<string>>> fileReader = null)
        {
            var data = new ModelData();
            if (config?.Tables == null) return data;
            foreach (var t in config.Tables)
            {
                if (t == null) continue;
                var columns = t.Columns ?? Read(t, config.DatabasePath, fileReader);
                data.Add(ModelTable.From(t.Name, columns));
            }
            return data;
        }
    }
}
