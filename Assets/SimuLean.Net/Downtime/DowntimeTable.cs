using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Data;

namespace SimuLean.Downtime
{
    /// <summary>
    /// Column mapping and rules to turn table rows (project database, CSV, any
    /// name→value rows) into <see cref="DowntimeInterval"/>s.
    /// Start/end columns may hold simulation times (numbers) or date text, converted
    /// through the model <see cref="SimCalendar"/>.
    /// </summary>
    public sealed class DowntimeTableMapping
    {
        /// <summary>Column naming the target (element name, line code…). Null = all rows belong to one target.</summary>
        public string TargetColumn;
        public string StartColumn = "start";
        /// <summary>End column (used when <see cref="DurationColumn"/> is null or empty in a row).</summary>
        public string EndColumn;
        public string DurationColumn = "duration";
        public string CodeColumn;
        public string ReasonColumn;

        /// <summary>Multiplier from the duration column's unit to simulation units (e.g. 60 for minutes → s).</summary>
        public double DurationScale = 1.0;

        /// <summary>State per code; codes not listed use <see cref="DefaultState"/>.</summary>
        public Dictionary<string, ElementState> StateByCode = new Dictionary<string, ElementState>(StringComparer.OrdinalIgnoreCase);
        public ElementState DefaultState = ElementState.STOPPED;

        /// <summary>Mode per code; codes not listed use <see cref="DefaultMode"/>.</summary>
        public Dictionary<string, StopMode> ModeByCode = new Dictionary<string, StopMode>(StringComparer.OrdinalIgnoreCase);
        public StopMode DefaultMode = StopMode.Immediate;

        /// <summary>Duration multiplier per code (sensitivity factors, calibration); default 1.</summary>
        public Dictionary<string, double> FactorByCode = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Codes whose rows are dropped.</summary>
        public HashSet<string> IgnoredCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Builds downtime intervals from table rows.</summary>
    public static class DowntimeTable
    {
        /// <summary>
        /// Converts <paramref name="rows"/> to intervals grouped by the target column value
        /// (key "" when <see cref="DowntimeTableMapping.TargetColumn"/> is null). Rows with an
        /// unparseable start or a non-positive duration are skipped (counted in <paramref name="skipped"/>).
        /// Intervals keep table order; <see cref="TimetableDowntime"/> sorts them.
        /// </summary>
        public static Dictionary<string, List<DowntimeInterval>> Build(
            IEnumerable<IReadOnlyDictionary<string, object>> rows, DowntimeTableMapping map,
            SimCalendar calendar, out int skipped)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            calendar = calendar ?? SimCalendar.Default;
            skipped = 0;
            var result = new Dictionary<string, List<DowntimeInterval>>(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                string code = Text(row, map.CodeColumn);
                if (code != null && map.IgnoredCodes.Contains(code)) { skipped++; continue; }

                if (!TryTime(row, map.StartColumn, calendar, out double start)) { skipped++; continue; }

                double duration;
                if (!string.IsNullOrEmpty(map.DurationColumn) && TryNumber(row, map.DurationColumn, out double d))
                    duration = d * map.DurationScale;
                else if (TryTime(row, map.EndColumn, calendar, out double end))
                    duration = end - start;
                else { skipped++; continue; }

                if (code != null && map.FactorByCode.TryGetValue(code, out double factor)) duration *= factor;
                if (duration <= 0) { skipped++; continue; }

                ElementState state = code != null && map.StateByCode.TryGetValue(code, out ElementState st) ? st : map.DefaultState;
                StopMode mode = code != null && map.ModeByCode.TryGetValue(code, out StopMode m) ? m : map.DefaultMode;

                string key = map.TargetColumn == null ? "" : (Text(row, map.TargetColumn) ?? "");
                if (!result.TryGetValue(key, out var list)) result[key] = list = new List<DowntimeInterval>();
                list.Add(new DowntimeInterval(start, duration, state, mode, code, Text(row, map.ReasonColumn)));
            }
            return result;
        }

        /// <summary>Convenience: runs <paramref name="sql"/> on <paramref name="db"/> and builds the intervals.</summary>
        public static Dictionary<string, List<DowntimeInterval>> Build(ProjectDatabase db, string sql,
            DowntimeTableMapping map, SimCalendar calendar, out int skipped, params object[] parameters)
        {
            var rows = new List<IReadOnlyDictionary<string, object>>();
            foreach (var r in db.Query(sql, parameters)) rows.Add(r);
            return Build(rows, map, calendar, out skipped);
        }

        private static string Text(IReadOnlyDictionary<string, object> row, string column)
        {
            if (string.IsNullOrEmpty(column) || !row.TryGetValue(column, out object v) || v == null) return null;
            if (v is double dv) return dv.ToString(CultureInfo.InvariantCulture);
            if (v is float fv) return fv.ToString(CultureInfo.InvariantCulture);
            string s = Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static bool TryNumber(IReadOnlyDictionary<string, object> row, string column, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(column) || !row.TryGetValue(column, out object v) || v == null) return false;
            switch (v)
            {
                case double d: value = d; return true;
                case float f:  value = f; return true;
                case long l:   value = l; return true;
                case int i:    value = i; return true;
                default:
                    string s = Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim().Replace(',', '.');
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            }
        }

        private static bool TryTime(IReadOnlyDictionary<string, object> row, string column, SimCalendar cal, out double t)
        {
            // Typed dates (Excel / SQLite readers) are converted directly: going through text
            // would render them as invariant MM/dd/yyyy, which the dd/MM-first parser swaps.
            if (!string.IsNullOrEmpty(column) && row.TryGetValue(column, out object v))
            {
                if (v is DateTime dt)        { t = cal.ToSimTime(dt); return true; }
                if (v is DateTimeOffset dto) { t = cal.ToSimTime(dto.DateTime); return true; }
            }
            if (TryNumber(row, column, out t)) return true;
            string s = Text(row, column);
            return s != null && cal.TryParseSimTime(s, out t);
        }
    }
}
