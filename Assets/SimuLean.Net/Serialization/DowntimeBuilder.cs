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
            IReadOnlyDictionary<string, Element> elementsById, SimClock clock,
            IReadOnlyDictionary<string, SimuLean.Resources.ResourceDispatcher> teams = null)
        {
            var result = new List<DowntimeGenerator>();
            if (configs == null) return result;
            foreach (var cfg in configs) result.AddRange(Build(cfg, elementsById, clock, teams));
            return result;
        }

        /// <summary>Builds one generator per resolvable target of <paramref name="cfg"/>. A target is an element or a
        /// resource team each member gets its own generator, its random streams keyed by the member's name); with a
        /// <see cref=""repairBy.team""/> each generator gets its repair crew (<paramref name="teams"/>).</summary>
        public static List<DowntimeGenerator> Build(DowntimeConfig cfg,
            IReadOnlyDictionary<string, Element> elementsById, SimClock clock,
            IReadOnlyDictionary<string, SimuLean.Resources.ResourceDispatcher> teams = null)
        {
            var result = new List<DowntimeGenerator>();
            if (cfg == null || cfg.TargetIds == null) return result;
            var s = cfg.Settings ?? new DowntimeSettings();   // an unknown key failed when it was written
            if (cfg.Kind == "Shift" && s.RepairBy != null && ResourceBuild.IsActive(s.RepairBy.Team, s.RepairBy.Requirements))
                throw new FormatException($"Downtime '{cfg.Id}': a shift calendar has no repair crew (repair by is for failures, timetables and periodic stops).");

            foreach (string targetId in cfg.TargetIds)
            {
                if (targetId != null && elementsById.TryGetValue(targetId, out Element element) && element != null)
                    result.Add(BuildOne(cfg, targetId, element, clock, teams));
                else if (targetId != null && teams != null && teams.TryGetValue(targetId, out var team) && team != null)
                    foreach (var member in team.Members) result.Add(BuildOne(cfg, targetId, member, clock, teams));
                else
                    throw new FormatException($"Downtime '{cfg.Id}': target '{targetId}' is neither an element nor a resource team of the model.");
            }
            return result;
        }

        private static DowntimeGenerator BuildOne(DowntimeConfig cfg, string targetId, Element target, SimClock clock,
                                                  IReadOnlyDictionary<string, SimuLean.Resources.ResourceDispatcher> teams)
        {
            DowntimeGenerator gen;
            switch (cfg.Kind)
            {
                case "Timetable": gen = BuildTimetable(cfg, targetId, target, clock); break;
                case "MtbfMttr":  gen = BuildMtbf(cfg, target, clock, $"{cfg.Id}:{target.Name}"); break;
                case "Shift":     gen = BuildShift(cfg, target, clock); break;
                case "Periodic":  gen = BuildPeriodic(cfg, target, clock); break;
                default:
                    throw new FormatException($"Downtime '{cfg.Id}': unknown kind '{cfg.Kind}' (Timetable, MtbfMttr, Shift, Periodic).");
            }

            ApplyCommon(cfg, gen);
            gen.Name = $"{cfg.Id}:{target.Name}";
            var crew = ResourceBuild.RepairUse(cfg, teams, target);
            if (crew != null) gen.Repair = new DowntimeRepair(crew, crew.Preemptable);
            return gen;
        }

        /// <summary>The state a downtime of <paramref name="kind"/> stops with when its <c>stateName</c> is not given:
        /// MtbfMttr BREAKDOWN, Periodic SCHEDULED_DOWN, Timetable STOPPED (its rows'), Shift OFF_SHIFT.</summary>
        public static string DefaultStateName(string kind)
            => kind == "Periodic" ? ElementState.SCHEDULED_DOWN.Name : kind == "Timetable" ? ElementState.STOPPED.Name
             : kind == "Shift" ? ElementState.OFF_SHIFT.Name : ElementState.BREAKDOWN.Name;

        /// <summary>The mode a downtime of <paramref name="kind"/> stops with when its <c>mode</c> is not given: a Shift
        /// AfterCurrent (the job in hand finishes at the shift's end), every other kind Immediate.</summary>
        public static StopMode DefaultMode(string kind) => kind == "Shift" ? StopMode.AfterCurrent : StopMode.Immediate;

        private static void ApplyCommon(DowntimeConfig cfg, DowntimeGenerator gen)
        {
            var s = cfg.Settings ?? new DowntimeSettings();
            if (!string.IsNullOrEmpty(s.StateName)) gen.State = ElementState.Of(s.StateName);
            if (s.Mode != null) gen.Mode = s.Mode.Value;
            if (s.BlockInput != null) gen.BlockInput = s.BlockInput.Value;
            if (s.BlockOutput != null) gen.BlockOutput = s.BlockOutput.Value;
        }

        private static TimetableDowntime BuildTimetable(DowntimeConfig cfg, string targetId, Element target, SimClock clock)
        {
            var s = cfg.Settings ?? new DowntimeSettings();
            var list = new List<DowntimeInterval>();
            ElementState defaultState = ElementState.Of(string.IsNullOrEmpty(s.StateName) ? ElementState.STOPPED.Name : s.StateName);
            StopMode defaultMode = s.Mode ?? StopMode.Immediate;
            SimCalendar cal = clock.Context.Calendar;

            int i = 0;
            foreach (var rec in s.Rows ?? new List<DowntimeIntervalSettings>())
            {
                i++;
                if (rec == null) continue;
                // an interval that cannot be read fails the build, naming it (it was dropped: the stop silently never
                // happened)
                if (!TryReadInterval(rec, cal, defaultState, defaultMode, out DowntimeInterval iv))
                    throw new FormatException($"Downtime '{cfg.Id}': interval {i} (rows[{i - 1}]) cannot be read: its start '{rec.Start}' is not a time or a date.");
                // a row's target must be one of the downtime's (an element or a team; not a team's member): any other was
                // skipped on every target, so the stop silently never happened
                if (!string.IsNullOrEmpty(rec.Target) && !cfg.TargetIds.Contains(rec.Target))
                    throw new FormatException($"Downtime '{cfg.Id}': rows[{i - 1}] targets '{rec.Target}', which is not one of its targets " +
                                              $"({string.Join(", ", cfg.TargetIds)}). A row's target is an element or a team of 'targets' / " +
                                              "'teamTargets' (a team's members are not named one by one); a row without a target applies to every target.");
                if (!string.IsNullOrEmpty(rec.Target) && rec.Target != targetId) continue;
                list.Add(iv);
            }
            list.AddRange(TableIntervals(cfg, targetId, clock, defaultState, defaultMode));

            return new TimetableDowntime(target, list, s.Overlap ?? OverlapPolicy.Allow, clock);
        }

        /// <summary>
        /// Keys of a timetable read from a data table: <c>dataTable</c> (the table's name), the column mapping
        /// <c>targetColumn</c> / <c>startColumn</c> / <c>endColumn</c> / <c>durationColumn</c> / <c>codeColumn</c> /
        /// <c>reasonColumn</c>, <c>durationScale</c>, <c>codeRules</c> (<c>code|state|mode|factor|ignore</c>) and
        /// <c>targetKeys</c> (<c>key|elementId</c>: the target column's values per target).
        /// </summary>
        public const string DataTableKey = DowntimeKeys.DataTable;

        // The intervals of the data table for one target, read from the model's snapshot (SimContext.Data) at build.
        private static IEnumerable<DowntimeInterval> TableIntervals(DowntimeConfig cfg, string targetId, SimClock clock,
                                                                    ElementState defaultState, StopMode defaultMode)
        {
            var s = cfg.Settings ?? new DowntimeSettings();
            string tableName = s.DataTable;
            if (string.IsNullOrWhiteSpace(tableName)) yield break;
            if (!clock.Context.Data.TryGet(tableName, out var table))
                throw new FormatException($"Downtime '{cfg.Id}': no data table '{tableName}' in the model " +
                                          $"(tables: {(clock.Context.Data.Count == 0 ? "none" : string.Join(", ", clock.Context.Data.Names))}).");
            string Col(string v, string fallback) { v = v ?? fallback; return string.IsNullOrEmpty(v) ? null : v; }
            var map = new DowntimeTableMapping
            {
                TargetColumn = Col(s.TargetColumn, null), StartColumn = Col(s.StartColumn, "start"), EndColumn = Col(s.EndColumn, null),
                DurationColumn = Col(s.DurationColumn, "duration"), CodeColumn = Col(s.CodeColumn, null), ReasonColumn = Col(s.ReasonColumn, null),
                DurationScale = s.DurationScale, DefaultState = defaultState, DefaultMode = defaultMode,
            };
            foreach (var rule in s.CodeRules ?? new List<DowntimeCodeRuleSettings>())
            {
                if (rule == null || string.IsNullOrEmpty(rule.Code)) continue;
                if (rule.Ignore) { map.IgnoredCodes.Add(rule.Code); continue; }
                if (!string.IsNullOrEmpty(rule.State)) map.StateByCode[rule.Code] = ElementState.Of(rule.State);
                if (rule.Mode != null) map.ModeByCode[rule.Code] = rule.Mode.Value;
                if (Math.Abs(rule.Factor - 1) > 1e-12) map.FactorByCode[rule.Code] = rule.Factor;
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
            foreach (var tk in s.TargetKeys ?? new List<DowntimeTargetKeySettings>())
            {
                if (tk == null || string.IsNullOrEmpty(tk.Key) || tk.Target != targetId) continue;
                if (byKey.TryGetValue(tk.Key, out var ivs)) foreach (var iv in ivs) yield return iv;
            }
        }

        // owner = the generator's name ("<config id>:<target>"): its streams are keyed by it (ttf / ttr / firstFailure).
        private static MtbfMttrDowntime BuildMtbf(DowntimeConfig cfg, Element target, SimClock clock, string owner)
        {
            var s = cfg.Settings ?? new DowntimeSettings();
            var ttf = ElementBuildHelpers.SamplerOf(s.TimeToFailure ?? SamplerSettings.ExponentialOfMean(3600), clock, owner, "ttf", DowntimeKeys.TimeToFailure);
            var ttr = ElementBuildHelpers.SamplerOf(s.TimeToRepair ?? SamplerSettings.ExponentialOfMean(300), clock, owner, "ttr", DowntimeKeys.TimeToRepair);
            var gen = new MtbfMttrDowntime(target, ttf, ttr, clock);

            if (s.FirstFailure != null) gen.FirstFailure = ElementBuildHelpers.SamplerOf(s.FirstFailure, clock, owner, "firstFailure", DowntimeKeys.FirstFailure);

            if (s.Basis != null) gen.Basis = s.Basis.Value;

            var busy = s.BusyStates;
            if (busy != null && busy.Count > 0)
            {
                gen.BusyStates.Clear();
                foreach (var b in busy) if (!string.IsNullOrEmpty(b)) gen.BusyStates.Add(ElementState.Of(b));
            }

            if (!string.IsNullOrEmpty(s.FailureCode)) gen.Code = s.FailureCode;
            return gen;
        }

        /// <summary>Keys of a periodic stop: <c>periodicPeriod</c>, <c>periodicDuration</c>, <c>periodicFirst</c> (default 0),
        /// optional <c>periodicUntil</c> (absent = no end), <c>allowOverlap</c> (bool, default false), <c>periodicCode</c>.
        /// Missing or invalid values fail the build.</summary>
        private static PeriodicDowntime BuildPeriodic(DowntimeConfig cfg, Element target, SimClock clock)
        {
            var s = cfg.Settings ?? new DowntimeSettings();
            if (s.PeriodicPeriod == null || s.PeriodicDuration == null)
                throw new FormatException($"Downtime '{cfg.Id}': a periodic stop needs '{DowntimeKeys.PeriodicPeriod}' and '{DowntimeKeys.PeriodicDuration}'.");
            double period = s.PeriodicPeriod.Value;
            double duration = s.PeriodicDuration.Value;
            double first = s.PeriodicFirst ?? 0.0;
            double until = s.PeriodicUntil ?? double.PositiveInfinity;
            bool overlap = s.AllowOverlap;
            PeriodicDowntime gen;
            try { gen = new PeriodicDowntime(target, period, duration, first, until, overlap, clock); }
            catch (ArgumentException e) { throw new FormatException($"Downtime '{cfg.Id}': {e.Message}", e); }
            if (!string.IsNullOrEmpty(s.PeriodicCode)) gen.Code = s.PeriodicCode;
            return gen;
        }

        private static ShiftDowntime BuildShift(DowntimeConfig cfg, Element target, SimClock clock)
        {
            var pattern = WeeklyShiftPattern.Parse(cfg.Settings?.ShiftPattern ?? "");
            var holidays = cfg.Settings?.Holidays;
            if (holidays != null)
                foreach (var h in holidays)
                {
                    if (string.IsNullOrWhiteSpace(h)) continue;
                    if (!SimCalendar.TryParseDate(h, out DateTime d))
                        throw new FormatException($"Downtime '{cfg.Id}': holiday '{h}' is not a date.");
                    pattern.AddHoliday(d);
                }
            foreach (var d in clock.Context.Holidays) pattern.AddHoliday(d);   // the model's working calendar
            return new ShiftDowntime(target, pattern, clock);
        }

        // ── interval flat-list codec ──────────────────────────────────────────

        /// <summary>An interval as a typed record of <paramref name="targetId"/> (empty = every target).</summary>
        public static DowntimeIntervalSettings IntervalOf(string targetId, DowntimeInterval iv)
            => DowntimeIntervalSettings.Of(targetId, iv.Start, iv.Duration, iv.State.IsNone ? null : iv.State.Name, iv.Mode,
                                           Clean(iv.Code), Clean(iv.Reason));

        /// <summary>Reads an interval record: its start a model time (a decimal comma accepted) or a date of the calendar; the
        /// state and mode absent = the downtime's. False when the start cannot be read.</summary>
        public static bool TryReadInterval(DowntimeIntervalSettings r, SimCalendar calendar, ElementState defaultState,
                                           StopMode defaultMode, out DowntimeInterval iv)
        {
            iv = default;
            string st = (r?.Start ?? "").Trim();
            if (!SimuLean.Data.ModelTable.TryNumber(st, out double start)
                && !(calendar ?? SimCalendar.Default).TryParseSimTime(st, out start))
                return false;
            ElementState state = string.IsNullOrEmpty(r.State) ? defaultState : ElementState.Of(r.State);
            iv = new DowntimeInterval(start, r.Duration, state, r.Mode ?? defaultMode, string.IsNullOrEmpty(r.Code) ? null : r.Code,
                                      string.IsNullOrEmpty(r.Reason) ? null : r.Reason);
            return true;
        }

        private static string Clean(string s) => s?.Replace("\r", " ").Replace("\n", " ") ?? "";
    }

    /// <summary>
    /// The paths of a downtime's settings (<see cref="DowntimeSettings"/>; a key is the field path). Common: <c>stateName</c>, <c>mode</c>, <c>blockInput</c>, <c>blockOutput</c>; MTBF / MTTR:
    /// <c>timeToFailure</c>, <c>timeToRepair</c>, <c>firstFailure</c> (sampler specs), <c>basis</c>, <c>busyStates</c>,
    /// <c>failureCode</c>; shift: <c>shiftPattern</c>, <c>holidays</c>; periodic: <c>periodicFirst</c>, <c>periodicPeriod</c>,
    /// <c>periodicDuration</c>, <c>periodicUntil</c>, <c>allowOverlap</c>, <c>periodicCode</c>; timetable: <c>overlap</c>,
    /// <c>rows</c> (the intervals as <c>target|start|duration|state|mode|code|reason</c> records), <c>dataTable</c> and its
    /// column mapping; the repair crew: <c>repairBy.*</c> (<see cref="RepairKeys"/>).
    /// </summary>
    public static class DowntimeKeys
    {
        public const string Enabled = "enabled", State = "stateName", Mode = "mode", BlockInput = "blockInput", BlockOutput = "blockOutput";
        public const string TimeToFailure = "timeToFailure", TimeToRepair = "timeToRepair", FirstFailure = "firstFailure",
                            Basis = "basis", BusyStates = "busyStates", FailureCode = "failureCode";
        public const string ShiftPattern = "shiftPattern", Holidays = "holidays";
        public const string PeriodicFirst = "periodicFirst", PeriodicPeriod = "periodicPeriod", PeriodicDuration = "periodicDuration",
                            PeriodicUntil = "periodicUntil", AllowOverlap = "allowOverlap", PeriodicCode = "periodicCode";
        public const string Overlap = "overlap", Rows = "rows", DataTable = "dataTable";
        /// <summary>A timetable read from a data table: the column mapping, the duration scale, the code rules
        /// (<c>code|state|mode|factor|ignore</c>) and the target column's values per target (<c>key|elementId</c>).</summary>
        public const string TargetColumn = "targetColumn", StartColumn = "startColumn", EndColumn = "endColumn",
                            DurationColumn = "durationColumn", CodeColumn = "codeColumn", ReasonColumn = "reasonColumn",
                            DurationScale = "durationScale", CodeRules = "codeRules", TargetKeys = "targetKeys";
    }
}
