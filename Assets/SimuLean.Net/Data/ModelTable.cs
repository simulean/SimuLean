using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimuLean.Data
{
    /// <summary>
    /// A named data table of the model: columns of text cells, read-only during the run — the
    /// snapshot of its source (inline rows, a file, an SQL query) taken at the run's start. Typed access by row and
    /// column, a key lookup, and <see cref="Require"/> so a consumer reports a missing column by table and name.
    /// </summary>
    public sealed class ModelTable
    {
        private readonly Dictionary<string, List<string>> columns;
        private readonly List<string> order;
        private readonly Dictionary<string, Dictionary<string, int>> indexes = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        public string Name { get; }
        public IReadOnlyList<string> Columns => order;
        public int RowCount { get; }

        private ModelTable(string name, Dictionary<string, List<string>> columns, List<string> order, int rows)
        {
            Name = name; this.columns = columns; this.order = order; RowCount = rows;
        }

        /// <summary>A table from columns (name → cells). Every column must have the same number of rows.</summary>
        public static ModelTable From(string name, IDictionary<string, List<string>> source)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new FormatException("Data table: a table needs a name.");
            var cols = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var order = new List<string>();
            int rows = -1;
            if (source != null)
                foreach (var kv in source)
                {
                    var cells = kv.Value ?? new List<string>();
                    if (rows >= 0 && cells.Count != rows)
                        throw new FormatException($"Data table '{name}': column '{kv.Key}' has {cells.Count} rows, '{order[0]}' has {rows}.");
                    rows = cells.Count;
                    cols[kv.Key] = new List<string>(cells);
                    order.Add(kv.Key);
                }
            return new ModelTable(name, cols, order, Math.Max(0, rows));
        }

        public bool HasColumn(string column) => column != null && columns.ContainsKey(column);

        /// <summary>Fails (naming the table, the missing columns and the ones there are) unless every column exists.</summary>
        public ModelTable Require(params string[] required)
        {
            var missing = new List<string>();
            foreach (string c in required) if (!HasColumn(c)) missing.Add(c);
            if (missing.Count > 0)
                throw new FormatException($"Data table '{Name}': no column {string.Join(", ", missing.ConvertAll(m => "'" + m + "'"))} " +
                                          $"(columns: {(order.Count == 0 ? "none" : string.Join(", ", order))}).");
            return this;
        }

        /// <summary>The cells of <paramref name="column"/>, in row order.</summary>
        public IReadOnlyList<string> Column(string column) => Cells(column);

        /// <summary>The text of a cell.</summary>
        public string Text(int row, string column)
        {
            var cells = Cells(column);
            if (row < 0 || row >= RowCount) throw new ArgumentOutOfRangeException(nameof(row), $"Data table '{Name}': row {row} (it has {RowCount}).");
            return cells[row];
        }

        /// <summary>The number in a cell (invariant culture; a comma decimal separator is accepted too).</summary>
        public double Number(int row, string column)
        {
            string text = Text(row, column);
            if (TryNumber(text, out double v)) return v;
            throw new FormatException($"Data table '{Name}': row {row + 1}, column '{column}': '{text}' is not a number.");
        }

        /// <summary>The first row whose <paramref name="keyColumn"/> equals <paramref name="key"/> (−1 if none). Indexed on first use.</summary>
        public int Find(string keyColumn, string key)
        {
            var cells = Cells(keyColumn);
            Dictionary<string, int> index;
            lock (indexes)
                if (!indexes.TryGetValue(keyColumn, out index))
                {
                    index = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int r = 0; r < cells.Count; r++) if (!index.ContainsKey(cells[r])) index[cells[r]] = r;
                    indexes[keyColumn] = index;
                }
            return key != null && index.TryGetValue(key, out int row) ? row : -1;
        }

        /// <summary>A copy of the columns (name → cells), e.g. to hand to a consumer that keeps its own table.</summary>
        public Dictionary<string, List<string>> ToColumns()
        {
            var copy = new Dictionary<string, List<string>>();
            foreach (string c in order) copy[c] = new List<string>(columns[c]);
            return copy;
        }

        public static bool TryNumber(string text, out double value)
        {
            text = (text ?? "").Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
            return text.IndexOf('.') < 0 && double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private List<string> Cells(string column)
        {
            if (column != null && columns.TryGetValue(column, out var cells)) return cells;
            throw new FormatException($"Data table '{Name}': no column '{column}' (columns: {(order.Count == 0 ? "none" : string.Join(", ", order))}).");
        }

        public override string ToString() => $"{Name} ({RowCount} rows: {string.Join(", ", order)})";
    }

    /// <summary>The model's data tables by name (<see cref="SimContext.Data"/>), built from the config at the run's start.</summary>
    public sealed class ModelData
    {
        private readonly Dictionary<string, ModelTable> tables = new Dictionary<string, ModelTable>(StringComparer.Ordinal);

        public IEnumerable<string> Names => tables.Keys;
        public int Count => tables.Count;

        public bool TryGet(string name, out ModelTable table) => tables.TryGetValue(name ?? "", out table);

        /// <summary>The table <paramref name="name"/>; fails naming the tables there are.</summary>
        public ModelTable this[string name]
        {
            get
            {
                if (TryGet(name, out var t)) return t;
                throw new KeyNotFoundException($"No data table '{name}' in the model (tables: {(tables.Count == 0 ? "none" : string.Join(", ", tables.Keys))}).");
            }
        }

        internal void Add(ModelTable table)
        {
            if (tables.ContainsKey(table.Name)) throw new FormatException($"Data table '{table.Name}' is defined twice.");
            tables[table.Name] = table;
        }
    }
}
