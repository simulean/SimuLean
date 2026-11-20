using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Downtime;
using SimuLean.Serialization;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Scene component that applies one downtime generator (MTBF/MTTR, timetable, shift
    /// calendar or periodic stop) to a list of elements — one generator instance per target.
    /// Not an <see cref="SElement"/>: the scene extraction exports it as a <see cref="DowntimeConfig"/>, the model
    /// builds and starts its generators (scene and headless alike) and binds them to it (<see cref="Bind"/>);
    /// a timetable's data table is exported by name with its column mapping and read from the run's snapshot.
    /// </summary>
    [AddComponentMenu("SimuLean/Downtime")]
    [SimuLean.Unity.SimuLeanTool(SimuLean.Unity.SimuLeanToolCategories.Downtime, "Downtime", Description = "Failures (MTBF/MTTR), shift calendars, timetables or periodic stops applied to elements")]
    public class UnityDowntime : MonoBehaviour
    {
        public enum DowntimeKind { MtbfMttr, Timetable, Shift, Periodic }
        public enum OutputBlocking { ModeDefault, Block, Allow }

        /// <summary>The downtime's stop mode: the kind's default (a Shift AfterCurrent, every other kind Immediate) or one
        /// chosen. The values of Immediate and AfterCurrent are those of <see cref="StopMode"/>, so the scenes that saved
        /// one keep it.</summary>
        public enum DowntimeMode { KindDefault = 2, Immediate = 0, AfterCurrent = 1 }

        [Serializable]
        public class TimetableRow
        {
            [Tooltip("Optional: name/key of the target this row applies to (empty = all targets).")]
            public string target = "";
            [Tooltip("Start: simulation time (number) or a date, e.g. 2026-01-05 06:00.")]
            public string start = "0";
            [Tooltip("Duration in simulation units.")]
            public double duration = 3600;
            [Tooltip("State name (empty = the component's state).")]
            public string state = "";
            [Tooltip("Stop code (reports; code rules apply to it).")]
            public string code = "";
            [Tooltip("Free text shown with the stop.")]
            public string reason = "";
        }

        [Serializable]
        public class CodeRule
        {
            [Tooltip("Rows with this code use the rule.")]
            public string code = "";
            [Tooltip("State for rows with this code (empty = default state).")]
            public string state = "";
            [Tooltip("Immediate: the work in progress is paused; After Current: the current job finishes first.")]
            public StopMode mode = StopMode.Immediate;
            [Tooltip("Duration multiplier for rows with this code.")]
            public double factor = 1.0;
            [Tooltip("Drop rows with this code.")]
            public bool ignore = false;
        }

        [Tooltip("Identifier used in the exported config (empty = GameObject name).")]
        public string id = "";

        [Tooltip("Off: the downtime stops nothing (its statistics read 0). A study switches it: downtime:Id.enabled.")]
        public bool downtimeEnabled = true;

        [Tooltip("MTBF / MTTR: random failures and repairs. Timetable: stops at given times. Shift: working windows of a week calendar. " +
                 "Periodic: a stop of fixed length every period (planned maintenance, breaks).")]
        public DowntimeKind kind = DowntimeKind.MtbfMttr;

        [Tooltip("Elements stopped by this generator (one independent generator per element).")]
        public List<SElement> targets = new List<SElement>();

        [Tooltip("Keys matched against the timetable target column/field, aligned with Targets. Empty = element GameObject name.")]
        public List<string> targetKeys = new List<string>();

        [Tooltip("Resource teams stopped by this generator: each member gets its own independent generator (its own random " +
                 "numbers). A member's Immediate stop interrupts only work whose use has a Preemption mode (the team's On Stop " +
                 "decides: hand it over or wait for the same member).")]
        public List<UnityResourceTeam> teamTargets = new List<UnityResourceTeam>();

        [Tooltip("Repair by: the team whose members repair each stop (empty = none: a stop lasts its time from its start). " +
                 "The element waits (WAITING_FOR_REPAIR) until the crew has come to it; the repair time (time to repair, the " +
                 "row's or the periodic duration) starts then. Not for shift calendars.")]
        public RepairResourceUse repairBy = new RepairResourceUse();

        [Header("Stop behaviour")]
        [Tooltip("State accounted while stopped: BREAKDOWN, STOPPED, SCHEDULED_DOWN, OFF_SHIFT or any custom name. Empty = the " +
                 "kind's: MTBF / MTTR BREAKDOWN, Timetable STOPPED, Shift OFF_SHIFT, Periodic SCHEDULED_DOWN.")]
        public string stateName = "";
        [Tooltip("Kind Default: a Shift After Current, the other kinds Immediate. Immediate: the work in progress is paused. " +
                 "After Current: the current job finishes first, then the stop starts and lasts its whole time (a shift's ends at " +
                 "the next window).")]
        [SimuLeanHelp("After Current delays a stop, it never cuts it: the stop starts when the job in hand ends and lasts its " +
                      "whole time, so a Timetable row's return moves later by the overrun, and a fixed-hour calendar written as rows " +
                      "drifts day after day (125-min jobs: back at 06:20, 06:40, 07:00…). For stops at fixed clock times use a Shift: " +
                      "its return stays the calendar's.")]
        public DowntimeMode mode = DowntimeMode.KindDefault;
        [Tooltip("The stopped element refuses items.")]
        public bool blockInput = true;
        [Tooltip("The stopped element keeps its finished items (Mode Default: blocked when Immediate).")]
        public OutputBlocking blockOutput = OutputBlocking.ModeDefault;

        [Header("MTBF / MTTR")]
        [Tooltip("Time between the end of a repair and the next failure.")]
        public RandomProcessConfig timeToFailure = new RandomProcessConfig();
        [Tooltip("Duration of each repair.")]
        public RandomProcessConfig timeToRepair = new RandomProcessConfig();
        [Tooltip("Use a different distribution for the first failure (e.g. a machine that starts half worn).")]
        public bool useFirstFailure = false;
        [Tooltip("Time to the first failure.")]
        public RandomProcessConfig firstFailure = new RandomProcessConfig();
        [Tooltip("Calendar Time: the time to failure always runs. Busy Time: only while the element is in a busy state.")]
        [SimuLeanHelp("CalendarTime: each time to failure is drawn when the repair ends and counts from then (a wait for the repair " +
                      "crew is part of the downtime); BusyTime: it counts only while the target is in busyStates (PROCESSING).")]
        public DowntimeBasis basis = DowntimeBasis.CalendarTime;
        [Tooltip("States counted as busy for the BusyTime basis.")]
        public List<string> busyStates = new List<string> { "PROCESSING" };
        [Tooltip("Stop code of the failures (reports).")]
        public string failureCode = "MTBF";

        [Header("Shift calendar (needs a Start Date/Time on UnitySimClock)")]
        [TextArea(2, 5)]
        [Tooltip("Working windows, e.g. \"Mon-Fri 06:00-14:00,14:00-22:00; Sat 06:00-14:00\". Outside them the targets are stopped.")]
        public string shiftPattern = "Mon-Fri 06:00-22:00";
        [Tooltip("Non-working dates (yyyy-MM-dd).")]
        public List<string> holidays = new List<string>();

        [Header("Periodic stop")]
        [Tooltip("Time of the first stop (simulation units).")]
        public double periodicFirst = 0;
        [Tooltip("Time between the starts of two stops (simulation units, > 0).")]
        public double periodicPeriod = 3600;
        [Tooltip("Length of each stop (simulation units, > 0; at most the period unless overlap is allowed).")]
        public double periodicDuration = 300;
        [Tooltip("Stop repeating: only stops that start before Until happen.")]
        public bool useUntil = false;
        [Tooltip("No stop starts at or after this time (simulation units; after the first stop).")]
        public double periodicUntil = 86400;
        [Tooltip("Allow stops longer than the period (they overlap and stack on the element).")]
        public bool allowOverlap = false;
        [Tooltip("Stop code of the periodic stops (reports).")]
        public string periodicCode = "PERIODIC";

        [Header("Timetable")]
        [Tooltip("Overlapping rows: Allow (stops stack), Serialize (one after the other) or Merge (one stop).")]
        [SimuLeanHelp("Rows that overlap — also rows that wait for the same job under After Current: Allow (the default) lets them " +
                      "take effect together, so the shorter is absorbed in the longer; Serialize queues each behind the previous for " +
                      "its whole length; Merge makes them one stop.")]
        public OverlapPolicy overlap = OverlapPolicy.Allow;
        [Tooltip("The stops: start, duration, state, code, reason (optionally per target).")]
        public List<TimetableRow> rows = new List<TimetableRow>();

        [Tooltip("A data table with the stops (in addition to the rows above): one row per stop, its columns mapped " +
                 "below. Read when each run starts (a study varies it as data:<table>).")]
        public UnityDataTable dataTable;

        // The column mapping of the data table (one row per stop).
        [Tooltip("Column with the target key (matched against Target Keys / element names).")]
        public string targetColumn = "target";
        [Tooltip("Column with the start (simulation time or a date).")]
        public string startColumn = "start";
        [Tooltip("Column with the end (optional; else the duration column).")]
        public string endColumn = "";
        [Tooltip("Column with the duration.")]
        public string durationColumn = "duration";
        [Tooltip("Column with the stop code (optional).")]
        public string codeColumn = "code";
        [Tooltip("Column with the reason text (optional).")]
        public string reasonColumn = "reason";
        [Tooltip("Duration column unit → simulation units (e.g. 60 for minutes → seconds).")]
        public double durationScale = 1.0;
        [Tooltip("Per code: state, mode, duration factor, or drop the rows.")]
        public List<CodeRule> codeRules = new List<CodeRule>();

        [NonSerialized] private readonly List<DowntimeGenerator> generators = new List<DowntimeGenerator>();

        /// <summary>Generators of the current run (one per target).</summary>
        public IReadOnlyList<DowntimeGenerator> Generators => generators;

        public int TotalStops { get { int n = 0; foreach (var g in generators) n += g.StopCount; return n; } }
        public double TotalDowntime { get { double t = 0; foreach (var g in generators) t += g.TotalDowntime; return t; } }

        /// <summary>Repairs done by the crews of this run's generators (-1 when they have no repair crew).</summary>
        public int TotalRepairs
        {
            get { int n = -1; foreach (var g in generators) if (g.Repair != null) n = Math.Max(n, 0) + g.Repair.Repairs; return n; }
        }

        /// <summary>Mean wait for the crew over the generators' arrived crews (0 when none).</summary>
        public double RepairWaitAvg
        {
            get
            {
                double sum = 0; int n = 0;
                foreach (var g in generators)
                    if (g.Repair != null) { sum += g.Repair.WaitAvg * g.Repair.CrewsArrived; n += g.Repair.CrewsArrived; }
                return n > 0 ? sum / n : 0;
            }
        }

        public string ConfigId => string.IsNullOrEmpty(id) ? gameObject.name : id;

        /// <summary>The state the stops account: <see cref="stateName"/>, or the kind's default when empty.</summary>
        public string EffectiveStateName => string.IsNullOrEmpty(stateName) ? DowntimeBuilder.DefaultStateName(kind.ToString()) : stateName;

        /// <summary>The stop mode: <see cref="mode"/>, or the kind's default (<see cref="DowntimeMode.KindDefault"/>).</summary>
        public StopMode EffectiveMode => mode == DowntimeMode.KindDefault ? DowntimeBuilder.DefaultMode(kind.ToString()) : (StopMode)(int)mode;

        // ── lifecycle (driven by UnitySimClock) ───────────────────────────────

        /// <summary>Binds the generators the model factory built from this component (one per target) for this run.</summary>
        public void Bind(IReadOnlyList<DowntimeGenerator> built)
        {
            generators.Clear();
            if (built != null) generators.AddRange(built);
        }

        // ── config export (the scene extraction) ───────

        /// <summary>
        /// Describes this component as a <see cref="DowntimeConfig"/>, naming targets with
        /// <paramref name="idOf"/> (targets it returns null for are skipped) and teams — team targets, the repair crew —
        /// with <paramref name="teamIdOf"/> (null = no teams).
        /// </summary>
        public DowntimeConfig ToConfig(Func<SElement, string> idOf, SimCalendar calendar, Func<UnityResourceTeam, string> teamIdOf)
        {
            var cfg = new DowntimeConfig { Id = ConfigId, Kind = kind.ToString() };
            var keyToId = new Dictionary<string, string>(StringComparer.Ordinal);

            for (int i = 0; i < targets.Count; i++)
            {
                SElement se = targets[i];
                if (se == null) continue;
                string tid = idOf(se);
                if (tid == null) continue;
                cfg.TargetIds.Add(tid);
                string key = i < targetKeys.Count && !string.IsNullOrEmpty(targetKeys[i]) ? targetKeys[i] : se.gameObject.name;
                keyToId[key] = tid;
            }
            if (teamIdOf != null)
                foreach (var team in teamTargets)
                {
                    string tid = team != null ? teamIdOf(team) : null;
                    if (tid == null || cfg.TargetIds.Contains(tid)) continue;
                    cfg.TargetIds.Add(tid);
                    if (!keyToId.ContainsKey(team.gameObject.name)) keyToId[team.gameObject.name] = tid;
                }
            if (teamIdOf != null && kind != DowntimeKind.Shift && repairBy != null && repairBy.IsUsed)
            {
                string crew = repairBy.team != null ? teamIdOf(repairBy.team) : "";   // "" = the requirements name the teams
                if (crew != null) repairBy.Export(cfg, crew);
            }

            if (!downtimeEnabled) cfg.Set(SimuLean.Serialization.DowntimeKeys.Enabled, false);
            // the kind's defaults written out (empty state, Kind Default mode): the model view shows them and a study can
            // vary the mode of any downtime
            cfg.Set(SimuLean.Serialization.DowntimeKeys.State, EffectiveStateName);
            cfg.Set(SimuLean.Serialization.DowntimeKeys.Mode, EffectiveMode.ToString());
            cfg.Set(SimuLean.Serialization.DowntimeKeys.BlockInput, blockInput);
            if (blockOutput != OutputBlocking.ModeDefault) cfg.Set(SimuLean.Serialization.DowntimeKeys.BlockOutput, blockOutput == OutputBlocking.Block);

            switch (kind)
            {
                case DowntimeKind.MtbfMttr:
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.TimeToFailure, timeToFailure.ToSettings());
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.TimeToRepair, timeToRepair.ToSettings());
                    if (useFirstFailure) cfg.Set(SimuLean.Serialization.DowntimeKeys.FirstFailure, firstFailure.ToSettings());
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.Basis, basis.ToString());
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.BusyStates, new List<string>(busyStates));
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.FailureCode, failureCode);
                    break;

                case DowntimeKind.Shift:
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.ShiftPattern, shiftPattern);
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.Holidays, new List<string>(holidays));
                    break;

                case DowntimeKind.Periodic:
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.PeriodicFirst, periodicFirst);
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.PeriodicPeriod, periodicPeriod);
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.PeriodicDuration, periodicDuration);
                    if (useUntil) cfg.Set(SimuLean.Serialization.DowntimeKeys.PeriodicUntil, periodicUntil);
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.AllowOverlap, allowOverlap);
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.PeriodicCode, periodicCode);
                    break;

                case DowntimeKind.Timetable:
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.Overlap, overlap.ToString());
                    cfg.Set(SimuLean.Serialization.DowntimeKeys.Rows, BuildIntervalRecords(keyToId, calendar));
                    if (dataTable != null) ExportTableMapping(cfg, keyToId);
                    break;
            }
            return cfg;
        }

        private List<SimuLean.Serialization.DowntimeIntervalSettings> BuildIntervalRecords(Dictionary<string, string> keyToId, SimCalendar calendar)
        {
            var records = new List<SimuLean.Serialization.DowntimeIntervalSettings>();

            foreach (var r in rows)
            {
                if (r == null) continue;
                string tid = "";
                // a target the model does not know (a team member's name) goes as written: the build refuses it, naming it
                // (it was skipped, so the stop silently never happened)
                if (!string.IsNullOrEmpty(r.target) && !keyToId.TryGetValue(r.target, out tid)) tid = r.target;
                double start;
                if (!double.TryParse(r.start, NumberStyles.Float, CultureInfo.InvariantCulture, out start)
                    && !calendar.TryParseSimTime(r.start, out start))
                {
                    Debug.LogWarning($"[UnityDowntime] {name}: invalid start '{r.start}' — row skipped.");
                    continue;
                }
                // A row has no mode of its own, and its state only when the row gives one: left absent, the build takes the
                // downtime's (stateName, mode), so a study that varies them reaches every row.
                records.Add(SimuLean.Serialization.DowntimeIntervalSettings.Of(tid, start, r.duration,
                    string.IsNullOrEmpty(r.state) ? null : ElementState.Of(r.state).Name, null, r.code, r.reason));
            }

            return records;
        }

        /// <summary>The data table and its column mapping: the engine builds the stops from the run's snapshot.</summary>
        private void ExportTableMapping(DowntimeConfig cfg, Dictionary<string, string> keyToId)
        {
            cfg.Set(DowntimeBuilder.DataTableKey, dataTable.TableName);
            cfg.Set(SimuLean.Serialization.DowntimeKeys.TargetColumn, targetColumn ?? "");
            cfg.Set(SimuLean.Serialization.DowntimeKeys.StartColumn, startColumn ?? "");
            cfg.Set(SimuLean.Serialization.DowntimeKeys.EndColumn, endColumn ?? "");
            cfg.Set(SimuLean.Serialization.DowntimeKeys.DurationColumn, durationColumn ?? "");
            cfg.Set(SimuLean.Serialization.DowntimeKeys.CodeColumn, codeColumn ?? "");
            cfg.Set(SimuLean.Serialization.DowntimeKeys.ReasonColumn, reasonColumn ?? "");
            cfg.Set(SimuLean.Serialization.DowntimeKeys.DurationScale, durationScale);
            var rules = new List<SimuLean.Serialization.DowntimeCodeRuleSettings>();
            foreach (var r in codeRules)
                if (r != null && !string.IsNullOrEmpty(r.code))
                    rules.Add(new SimuLean.Serialization.DowntimeCodeRuleSettings
                    {
                        Code = r.code, State = string.IsNullOrEmpty(r.state) ? null : r.state, Mode = r.mode, Factor = r.factor, Ignore = r.ignore,
                    });
            cfg.Set(SimuLean.Serialization.DowntimeKeys.CodeRules, rules);
            var keys = new List<SimuLean.Serialization.DowntimeTargetKeySettings>();
            foreach (var kv in keyToId) keys.Add(SimuLean.Serialization.DowntimeTargetKeySettings.Of(kv.Key, kv.Value));
            cfg.Set(SimuLean.Serialization.DowntimeKeys.TargetKeys, keys);
        }
    }
}
