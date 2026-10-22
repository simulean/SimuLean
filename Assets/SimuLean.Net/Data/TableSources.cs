using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SimuLean.Data
{
    /// <summary>
    /// Tables a source reads when its model is built (headless, in a scene run, in a Study), instead of a copy kept in the
    /// scene — so every run reads the same, current data (SQL as the source of data). A
    /// <b>table spec</b> is one of:
    /// <list type="bullet">
    /// <item><c>sql:&lt;SELECT …&gt;</c> — a query of the project database (columns as returned: <c>Name</c>,
    /// <c>Time</c>, <c>Q</c> and labels for a Schedule source);</item>
    /// <item><c>naval:&lt;part&gt;[;interval=h][;start=h][;lead=h]:&lt;table, SELECT … or file&gt;</c> — a part
    /// (<c>subblocks</c>, <c>blocks</c>, <c>erection</c>) generated from a block breakdown
    /// (<see cref="SimuLean.Naval.BlockBreakdown"/>), released every <c>interval</c> in erection order from
    /// <c>start</c>, the sub-block kits <c>lead</c> before their block.</item>
    /// </list>
    /// The spec is a catalog key of the source (<c>tableSource</c>), so a Study varies it like a file (ship variants as
    /// scenarios); the database path is resolved by the caller (the Unity layer: the project database by default).
    /// </summary>
    public static class TableSources
    {
        /// <summary>True when <paramref name="spec"/> names a table this class reads.</summary>
        public static bool IsSpec(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return false;
            string s = spec.TrimStart();
            return s.StartsWith("sql:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("naval:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Reads the table of <paramref name="spec"/> (column name → values).</summary>
        public static Dictionary<string, List<string>> Read(string spec, string databasePath)
        {
            if (!IsSpec(spec)) throw new FormatException($"Table spec '{spec}': expected 'sql:<query>' or 'naval:<part>:<table>'.");
            string s = spec.Trim();
            if (s.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException($"Table spec '{spec}': no project database given.");
                var rows = ProjectDatabaseRegistry.GetOrOpen(databasePath).Query(s.Substring(4).Trim());
                return Columns(rows);
            }
            // naval:<part>[;opts]:<source>
            string rest = s.Substring("naval:".Length);
            int colon = rest.IndexOf(':');
            if (colon < 0) throw new FormatException($"Table spec '{spec}': expected 'naval:<part>:<table or query>'.");
            string head = rest.Substring(0, colon), source = rest.Substring(colon + 1).Trim();
            var parts = head.Split(';');
            var part = SimuLean.Naval.BlockBreakdown.ParsePart(parts[0]);
            var options = new SimuLean.Naval.BlockBreakdown.ReleaseOptions();
            foreach (var opt in parts.Skip(1))
            {
                int eq = opt.IndexOf('=');
                if (eq <= 0) throw new FormatException($"Table spec '{spec}': option '{opt}' is not name=value.");
                string key = opt.Substring(0, eq).Trim().ToLowerInvariant();
                if (!double.TryParse(opt.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    throw new FormatException($"Table spec '{spec}': option '{opt}' needs a number.");
                switch (key)
                {
                    case "interval": options.BlockInterval = v; break;
                    case "start": options.Start = v; break;
                    case "lead": options.SubBlockLead = v; break;
                    default: throw new FormatException($"Table spec '{spec}': unknown option '{key}' (interval, start, lead).");
                }
            }
            return SimuLean.Naval.BlockBreakdown.Load(source, databasePath).Generate(part, options);
        }

        /// <summary>Spec of a breakdown part (the inverse of <see cref="Read"/>'s naval form).</summary>
        public static string NavalSpec(SimuLean.Naval.BlockBreakdown.Part part, string source, double interval = 0, double start = 0, double lead = 0)
        {
            string name = part == SimuLean.Naval.BlockBreakdown.Part.SubBlocks ? "subblocks"
                        : part == SimuLean.Naval.BlockBreakdown.Part.Blocks ? "blocks" : "erection";
            string opts = "";
            if (interval != 0) opts += ";interval=" + interval.ToString("R", CultureInfo.InvariantCulture);
            if (start != 0) opts += ";start=" + start.ToString("R", CultureInfo.InvariantCulture);
            if (lead != 0) opts += ";lead=" + lead.ToString("R", CultureInfo.InvariantCulture);
            return $"naval:{name}{opts}:{source}";
        }

        private static Dictionary<string, List<string>> Columns(List<Dictionary<string, object>> rows)
        {
            var table = new Dictionary<string, List<string>>();
            if (rows.Count == 0) return table;
            foreach (var k in rows[0].Keys) table[k] = new List<string>(rows.Count);
            foreach (var r in rows)
                foreach (var k in table.Keys)
                    table[k].Add(r.TryGetValue(k, out var v) ? Text(v) : "");
            return table;
        }

        private static string Text(object v) => v switch
        {
            null => "",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString(),
        };
    }
}
