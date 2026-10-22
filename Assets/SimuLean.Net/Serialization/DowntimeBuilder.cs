using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Downtime;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Builds <see cref="DowntimeGenerator"/>s from <see cref="DowntimeConfig"/>s
    /// (headless runs and the Unity wrapper share this path). Engine-pure.
    /// Also encodes intervals into the flat-list format.
    /// </summary>
    public static class DowntimeBuilder
    {
        /// <summary>Builds every generator of <paramref name="configs"/>; unknown targets are skipped with a log line.</summary>
        public static List<DowntimeGenerator> BuildAll(IEnumerable<DowntimeConfig> configs,
            IReadOnlyDictionary<string, Element> elementsById, SimClock clock)
        {
            var result = new List<DowntimeGenerator>();
            if (configs == null) return result;
            foreach (var cfg in configs) result.AddRange(Build(cfg, elementsById, clock));
            return result;
        }

        /// <summary>Builds one generator per resolvable target of <paramref name="cfg"/>.</summary>
        public static List<DowntimeGenerator> Build(DowntimeConfig cfg,
            IReadOnlyDictionary<string, Element> elementsById, SimClock clock)
        {
            var result = new List<DowntimeGenerator>();
            if (cfg == null || cfg.TargetIds == null) return result;

            foreach (string targetId in cfg.TargetIds)
            {
                if (targetId == null || !elementsById.TryGetValue(targetId, out Element target) || target == null)
                {
                    throw new FormatException($"Downtime '{cfg.Id}': target '{targetId}' is not an element of the model.");
                }

                DowntimeGenerator gen;
                switch (cfg.Kind)
                {
                    case "Timetable": gen = BuildTimetable(cfg, targetId, target, clock); break;
                    case "MtbfMttr":  gen = BuildMtbf(cfg, target, clock, $"{cfg.Id}:{target.GetName()}"); break;
                    case "Shift":     gen = BuildShift(cfg, target, clock); break;
                    case "Periodic":  gen = BuildPeriodic(cfg, target, clock); break;
                    default:
                        throw new FormatException($"Downtime '{cfg.Id}': unknown kind '{cfg.Kind}' (Timetable, MtbfMttr, Shift, Periodic).");
                }

                ApplyCommon(cfg, gen);
                gen.Name = $"{cfg.Id}:{target.GetName()}";
                result.Add(gen);
            }
            return result;
        }

        private static void ApplyCommon(DowntimeConfig cfg, DowntimeGenerator gen)
        {
            string state = cfg.GetParameter<string>("state");
            if (!string.IsNullOrEmpty(state)) gen.State = ElementState.Of(state);
            string mode = cfg.GetParameter<string>("mode");
            if (!string.IsNullOrEmpty(mode) && Enum.TryParse(mode, true, out StopMode m)) gen.Mode = m;
            if (cfg.Parameters.TryGetValue("blockInput", out object bi) && bi is bool bIn) gen.BlockInput = bIn;
            if (cfg.Parameters.TryGetValue("blockOutput", out object bo) && bo is bool bOut) gen.BlockOutput = bOut;
        }

        private static TimetableDowntime BuildTimetable(DowntimeConfig cfg, string targetId, Element target, SimClock clock)
        {
            var list = new List<DowntimeInterval>();
            var records = cfg.GetParameter<List<string>>("intervals") ?? new List<string>();
            ElementState defaultState = ElementState.Of(cfg.GetParameter<string>("state") ?? ElementState.STOPPED.Name);
            StopMode defaultMode = Enum.TryParse(cfg.GetParameter<string>("mode") ?? "", true, out StopMode dm) ? dm : StopMode.Immediate;
            SimCalendar cal = clock.Context.Calendar;

            foreach (string rec in records)
            {
                if (string.IsNullOrWhiteSpace(rec)) continue;
                // a record that cannot be read fails the build, naming it (it was dropped: the stop silently never
                // happened)
                if (!TryDecodeInterval(rec, cal, defaultState, defaultMode, out string recTarget, out DowntimeInterval iv))
                    throw new FormatException($"Downtime '{cfg.Id}': interval '{rec}' cannot be read (target|start|duration|state|mode|code|reason).");
                if (!string.IsNullOrEmpty(recTarget) && recTarget != targetId) continue;
                list.Add(iv);
            }
            list.AddRange(TableIntervals(cfg, targetId, clock, defaultState, defaultMode));

            var overlap = Enum.TryParse(cfg.GetParameter<string>("overlap") ?? "", true, out OverlapPolicy op) ? op : OverlapPolicy.Allow;
            return new TimetableDowntime(target, list, overlap, clock);
        }

        /// <summary>
        /// Keys of a timetable read from a data table: <c>dataTable</c> (the table's name), the column mapping
        /// <c>targetColumn</c> / <c>startColumn</c> / <c>endColumn</c> / <c>durationColumn</c> / <c>codeColumn</c> /
        /// <c>reasonColumn</c>, <c>durationScale</c>, <c>codeRules</c> (<c>code|state|mode|factor|ignore</c>) and
        /// <c>targetKeys</c> (<c>key|elementId</c>: the target column's values per target).
        /// </summary>
        public const string DataTableKey = "dataTable";

        // The intervals of the data table for one target, read from the model's snapshot (SimContext.Data) at build.
        private static IEnumerable<DowntimeInterval> TableIntervals(DowntimeConfig cfg, string targetId, SimClock clock,
                                                                    ElementState defaultState, StopMode defaultMode)
        {
            string tableName = cfg.GetParameter<string>(DataTableKey);
            if (string.IsNullOrWhiteSpace(tableName)) yield break;
            if (!clock.Context.Data.TryGet(tableName, out var table))
                throw new FormatException($"Downtime '{cfg.Id}': no data table '{tableName}' in the model " +
                                          $"(tables: {(clock.Context.Data.Count == 0 ? "none" : string.Join(", ", clock.Context.Data.Names))}).");
            string Col(string key, string fallback) { string v = cfg.GetParameter<string>(key, fallback); return string.IsNullOrEmpty(v) ? null : v; }
            var map = new DowntimeTableMapping
            {
                TargetColumn = Col("targetColumn", null), StartColumn = Col("startColumn", "start"), EndColumn = Col("endColumn", null),
                DurationColumn = Col("durationColumn", "duration"), CodeColumn = Col("codeColumn", null), ReasonColumn = Col("reasonColumn", null),
                DurationScale = cfg.GetParameter<double>("durationScale", 1.0), DefaultState = defaultState, DefaultMode = defaultMode,
            };
            foreach (string rule in cfg.GetParameter<List<string>>("codeRules") ?? new List<string>())
            {
                var p = (rule ?? "").Split('|');
                if (p.Length < 5 || string.IsNullOrEmpty(p[0])) continue;
                if (p[4] == "1" || p[4].Equals("true", StringComparison.OrdinalIgnoreCase)) { map.IgnoredCodes.Add(p[0]); continue; }
                if (!string.IsNullOrEmpty(p[1])) map.StateByCode[p[0]] = ElementState.Of(p[1]);
                if (Enum.TryParse(p[2], true, out StopMode m)) map.ModeByCode[p[0]] = m;
                if (double.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double f) && Math.Abs(f - 1) > 1e-12)
                    map.FactorByCode[p[0]] = f;
            }
            var required = new List<string> { map.StartColumn };
            if (map.TargetColumn != null) required.Add(map.TargetColumn);
            table.Require(required.ToArray());
            var rows = new List<IReadOnlyDictionary<string, object>>(table.RowCount);
            for (int r = 0; r < table.RowCount; r++)
            {
                var row = new Dictionary<string, object>();
                foreach (string c in table.Columns) row[c] = table.Text(r, c);
                rows.Add(row);
            }
            var byKey = DowntimeTable.Build(rows, map, clock.Context.Calendar, out int skipped);
            if (skipped > 0) SimLog.Warning($"[SimuLean] Downtime '{cfg.Id}': {skipped} row(s) of '{tableName}' skipped (no start or no positive duration).");
            if (map.TargetColumn == null)
            {
                foreach (var kv in byKey) foreach (var iv in kv.Value) yield return iv;
                yield break;
            }
            foreach (string pair in cfg.GetParameter<List<string>>("targetKeys") ?? new List<string>())
            {
                int bar = (pair ?? "").LastIndexOf('|');
                if (bar <= 0 || pair.Substring(bar + 1) != targetId) continue;
                if (byKey.TryGetValue(pair.Substring(0, bar), out var ivs)) foreach (var iv in ivs) yield return iv;
            }
        }

        // owner = the generator's name ("<config id>:<target>"): its streams are keyed by it (ttf / ttr / firstFailure).
        private static MtbfMttrDowntime BuildMtbf(DowntimeConfig cfg, Element target, SimClock clock, string owner)
        {
            var ttf = SamplerSpec.Decode(cfg.GetParameter<string>("ttf") ?? "ExponentialMean~3600", clock, owner, "ttf");
            var ttr = SamplerSpec.Decode(cfg.GetParameter<string>("ttr") ?? "ExponentialMean~300", clock, owner, "ttr");
            var gen = new MtbfMttrDowntime(target, ttf, ttr, clock);

            string first = cfg.GetParameter<string>("firstFailure");
            if (!string.IsNullOrEmpty(first)) gen.FirstFailure = SamplerSpec.Decode(first, clock, owner, "firstFailure");

            if (Enum.TryParse(cfg.GetParameter<string>("basis") ?? "", true, out DowntimeBasis basis)) gen.Basis = basis;

            var busy = cfg.GetParameter<List<string>>("busyStates");
            if (busy != null && busy.Count > 0)
            {
                gen.BusyStates.Clear();
                foreach (var s in busy) if (!string.IsNullOrEmpty(s)) gen.BusyStates.Add(ElementState.Of(s));
            }

            string code = cfg.GetParameter<string>("code");
            if (!string.IsNullOrEmpty(code)) gen.Code = code;
            return gen;
        }

        /// <summary>Keys of a periodic stop: <c>period</c>, <c>duration</c>, <c>first</c> (default 0), optional <c>until</c>
        /// (absent = no end), <c>allowOverlap</c> (bool, default false), <c>code</c>. Missing or invalid values fail the build.</summary>
        private static PeriodicDowntime BuildPeriodic(DowntimeConfig cfg, Element target, SimClock clock)
        {
            if (!cfg.Parameters.ContainsKey("period") || !cfg.Parameters.ContainsKey("duration"))
                throw new FormatException($"Downtime '{cfg.Id}': a periodic stop needs 'period' and 'duration'.");
            double period = cfg.GetParameter<double>("period");
            double duration = cfg.GetParameter<double>("duration");
            double first = cfg.GetParameter<double>("first", 0.0);
            double until = cfg.Parameters.TryGetValue("until", out object u) && u != null ? cfg.GetParameter<double>("until") : double.PositiveInfinity;
            bool overlap = cfg.GetParameter<bool>("allowOverlap", false);
            PeriodicDowntime gen;
            try { gen = new PeriodicDowntime(target, period, duration, first, until, overlap, clock); }
            catch (ArgumentException e) { throw new FormatException($"Downtime '{cfg.Id}': {e.Message}"); }
            string code = cfg.GetParameter<string>("code");
            if (!string.IsNullOrEmpty(code)) gen.Code = code;
            return gen;
        }

        private static ShiftDowntime BuildShift(DowntimeConfig cfg, Element target, SimClock clock)
        {
            var pattern = WeeklyShiftPattern.Parse(cfg.GetParameter<string>("pattern") ?? "");
            var holidays = cfg.GetParameter<List<string>>("holidays");
            if (holidays != null)
                foreach (var h in holidays)
                {
                    if (string.IsNullOrWhiteSpace(h)) continue;
                    if (!SimCalendar.TryParseDate(h, out DateTime d))
                        throw new FormatException($"Downtime '{cfg.Id}': holiday '{h}' is not a date.");
                    pattern.AddHoliday(d);
                }
            foreach (var d in clock.Context.Holidays) pattern.AddHoliday(d);   // the model's working calendar (I7)
            return new ShiftDowntime(target, pattern, clock);
        }

        // ── interval flat-list codec ──────────────────────────────────────────

        /// <summary>Encodes an interval as <c>"targetId|start|duration|state|mode|code|reason"</c>.</summary>
        public static string EncodeInterval(string targetId, DowntimeInterval iv)
            => string.Join("|",
                targetId ?? "",
                iv.Start.ToString("R", CultureInfo.InvariantCulture),
                iv.Duration.ToString("R", CultureInfo.InvariantCulture),
                iv.State.IsNone ? "" : iv.State.Name,
                iv.Mode?.ToString() ?? "",
                Clean(iv.Code),
                Clean(iv.Reason));

        /// <summary>Decodes <see cref="EncodeInterval"/> output; start may also be a date text.</summary>
        public static bool TryDecodeInterval(string record, SimCalendar calendar, ElementState defaultState,
                                             StopMode defaultMode, out string targetId, out DowntimeInterval iv)
        {
            iv = default; targetId = null;
            if (string.IsNullOrEmpty(record)) return false;
            string[] p = record.Split('|');
            if (p.Length < 3) return false;
            targetId = p[0];

            if (!SimuLean.Data.ModelTable.TryNumber(p[1], out double start)
                && !(calendar ?? SimCalendar.Default).TryParseSimTime(p[1], out start))
                return false;
            if (!SimuLean.Data.ModelTable.TryNumber(p[2], out double duration))
                return false;

            ElementState state = p.Length > 3 && p[3].Length > 0 ? ElementState.Of(p[3]) : defaultState;
            StopMode mode = p.Length > 4 && Enum.TryParse(p[4], true, out StopMode m) ? m : defaultMode;
            string code = p.Length > 5 && p[5].Length > 0 ? p[5] : null;
            string reason = p.Length > 6 ? string.Join("|", p, 6, p.Length - 6) : null;
            iv = new DowntimeInterval(start, duration, state, mode, code, string.IsNullOrEmpty(reason) ? null : reason);
            return true;
        }

        private static string Clean(string s) => s?.Replace("\r", " ").Replace("\n", " ") ?? "";
    }
}
