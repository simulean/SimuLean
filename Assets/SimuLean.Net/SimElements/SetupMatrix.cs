using System;
using System.Collections.Generic;
using SimuLean.Data;

namespace SimuLean
{
    /// <summary>
    /// Sequence-dependent setup times (<see cref="StationSetup.Matrix"/>): a time per <i>from → to</i> pair of
    /// the setup label's values, read from a model data table with the columns <c>from</c>, <c>to</c> and <c>time</c>. A
    /// <c>time</c> cell is a number or a sampler spec (<c>Type~p1~p2</c>, as <c>setup.time</c>); each cell that is not a
    /// number draws from its own stream (owner = the station, purpose <c>setup.matrix[from&gt;to]</c>). <c>*</c> in
    /// <c>from</c> or <c>to</c> is any value; an empty <c>from</c> is the slot's first setup (no previous value) — or a
    /// previous item without the label —, an empty <c>to</c> an item without the label. Lookup, most specific first:
    /// (from, to), (from, *), (*, to), (*, *); no row = the setup's <see cref="StationSetup.Time"/>.
    /// </summary>
    public sealed class SetupMatrix
    {
        /// <summary>The wildcard of the <c>from</c> / <c>to</c> columns.</summary>
        public const string Any = "*";
        /// <summary>The table's columns.</summary>
        public const string FromColumn = "from", ToColumn = "to", TimeColumn = "time";

        private readonly Dictionary<(string from, string to), IRandomSampler> cells =
            new Dictionary<(string, string), IRandomSampler>();

        /// <summary>The table it was read from (reports).</summary>
        public string Name { get; }

        public SetupMatrix(string name = null) { Name = name ?? ""; }

        /// <summary>The pairs it lists.</summary>
        public int Count => cells.Count;

        /// <summary>Sets the time of a pair (<c>*</c> = any value; "" = no value). A pair given twice fails.</summary>
        public void Set(string from, string to, IRandomSampler time)
        {
            var key = (Norm(from), Norm(to));
            if (cells.ContainsKey(key))
                throw new FormatException($"Setup matrix '{Name}': the pair '{key.Item1}' → '{key.Item2}' is given twice.");
            cells[key] = time ?? throw new ArgumentNullException(nameof(time));
        }

        /// <summary>The sampler of a setup from <paramref name="from"/> (null = no value) to <paramref name="to"/> (null = an
        /// item without the label); <paramref name="hasFrom"/> false = the slot's first setup. Null when no row applies.</summary>
        public IRandomSampler Find(string from, bool hasFrom, string to)
        {
            string f = hasFrom ? Norm(from) : "", t = Norm(to);
            if (cells.TryGetValue((f, t), out var s)) return s;
            if (cells.TryGetValue((f, Any), out s)) return s;
            if (cells.TryGetValue((Any, t), out s)) return s;
            if (cells.TryGetValue((Any, Any), out s)) return s;
            return null;
        }

        private static string Norm(string v) => (v ?? "").Trim();

        /// <summary>How a matrix is looked up, for a reader of its rows (<see cref="Describe"/>).</summary>
        public const string LookupText = "most specific row first: (from, to), (from, any), (any, to), (any, any); no row = the setup's own time (setup.time)";

        /// <summary>Rows of a matrix shown at most (<see cref="Describe"/>).</summary>
        public const int RowsShown = 12;

        /// <summary>
        /// The rows of a matrix table as they are read, one line
        /// each — <c>A → B: 30</c>, an empty <c>from</c> as <c>(first setup)</c>, an empty <c>to</c> as <c>(no label)</c>,
        /// <c>*</c> as <c>any</c>, the time as written (a number or a distribution) — the first <see cref="RowsShown"/>, then a
        /// count of the rest. The columns are those of <see cref="From"/>; a missing cell reads as empty.
        /// </summary>
        public static List<string> Describe(IList<string> from, IList<string> to, IList<string> time)
        {
            int n = Math.Max(from?.Count ?? 0, Math.Max(to?.Count ?? 0, time?.Count ?? 0));
            string Cell(IList<string> col, int r) => col != null && r < col.Count ? Norm(col[r]) : "";
            string Side(string v, bool isFrom) => v == Any ? "any" : v.Length == 0 ? (isFrom ? "(first setup)" : "(no label)") : v;
            var lines = new List<string>();
            for (int r = 0; r < Math.Min(n, RowsShown); r++)
            {
                string t = Cell(time, r);
                lines.Add($"{Side(Cell(from, r), true)} → {Side(Cell(to, r), false)}: {(t.Length == 0 ? "(no time: an error at build)" : t)}");
            }
            if (n > RowsShown) lines.Add($"… {n - RowsShown} more rows");
            return lines;
        }

        /// <summary>The purpose of the random stream of a pair's sampler (owner = the station).</summary>
        public static string StreamPurpose(string from, string to) => $"setup.matrix[{Norm(from)}>{Norm(to)}]";

        /// <summary>
        /// Reads the matrix from <paramref name="table"/> (columns <c>from</c>, <c>to</c>, <c>time</c>): a number is a
        /// constant time, anything else a sampler spec that <paramref name="decodeTime"/> decodes (cell, stream purpose of the
        /// pair: <see cref="StreamPurpose"/>; the builder decodes it with its own stream of <paramref name="owner"/>'s model).
        /// An empty or invalid time cell, a negative number or a pair given twice fails, naming the table and the row.
        /// </summary>
        public static SetupMatrix From(ModelTable table, string owner, Func<string, string, IRandomSampler> decodeTime)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            table.Require(FromColumn, ToColumn, TimeColumn);
            var m = new SetupMatrix(table.Name);
            for (int r = 0; r < table.RowCount; r++)
            {
                string from = Norm(table.Text(r, FromColumn)), to = Norm(table.Text(r, ToColumn)), cell = Norm(table.Text(r, TimeColumn));
                IRandomSampler time;
                if (cell.Length == 0)
                    throw new FormatException($"Setup matrix '{table.Name}': row {r + 1} ('{from}' → '{to}') has no time.");
                if (ModelTable.TryNumber(cell, out double v))
                {
                    if (v < 0) throw new FormatException($"Setup matrix '{table.Name}': row {r + 1}: the time {cell} is negative.");
                    time = new ConstantRandomProcess(v);
                }
                else
                {
                    try { time = decodeTime(cell, StreamPurpose(from, to)); }
                    catch (FormatException e)
                    {
                        throw new FormatException($"Setup matrix '{table.Name}': row {r + 1} ('{from}' → '{to}'): {e.Message}", e);
                    }
                }
                m.Set(from, to, time);
            }
            return m;
        }
    }
}
