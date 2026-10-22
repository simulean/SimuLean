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

        [Tooltip("MTBF / MTTR: random failures and repairs. Timetable: stops at given times. Shift: working windows of a week calendar. " +
                 "Periodic: a stop of fixed length every period (planned maintenance, breaks).")]
        public DowntimeKind kind = DowntimeKind.MtbfMttr;

        [Tooltip("Elements stopped by this generator (one independent generator per element).")]
        public List<SElement> targets = new List<SElement>();

        [Tooltip("Keys matched against the timetable target column/field, aligned with Targets. Empty = element GameObject name.")]
        public List<string> targetKeys = new List<string>();

        [Header("Stop behaviour")]
        [Tooltip("State accounted while stopped: BREAKDOWN, STOPPED, SCHEDULED_DOWN, OFF_SHIFT or any custom name.")]
        public string stateName = "BREAKDOWN";
        [Tooltip("Immediate: the work in progress is paused. After Current: the current job finishes first.")]
        public StopMode mode = StopMode.Immediate;
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

        public string ConfigId => string.IsNullOrEmpty(id) ? gameObject.name : id;

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
        /// <paramref name="idOf"/> (targets it returns null for are skipped).
        /// </summary>
        public DowntimeConfig ToConfig(Func<SElement, string> idOf, SimCalendar calendar)
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

            cfg.Parameters["state"] = string.IsNullOrEmpty(stateName) ? ElementState.STOPPED.Name : stateName;
            cfg.Parameters["mode"] = mode.ToString();
            cfg.Parameters["blockInput"] = blockInput;
            if (blockOutput != OutputBlocking.ModeDefault) cfg.Parameters["blockOutput"] = blockOutput == OutputBlocking.Block;

            switch (kind)
            {
                case DowntimeKind.MtbfMttr:
                    cfg.Parameters["ttf"] = timeToFailure.ToSamplerSpec();
                    cfg.Parameters["ttr"] = timeToRepair.ToSamplerSpec();
                    if (useFirstFailure) cfg.Parameters["firstFailure"] = firstFailure.ToSamplerSpec();
                    cfg.Parameters["basis"] = basis.ToString();
                    cfg.Parameters["busyStates"] = new List<string>(busyStates);
                    cfg.Parameters["code"] = failureCode;
                    break;

                case DowntimeKind.Shift:
                    cfg.Parameters["pattern"] = shiftPattern;
                    cfg.Parameters["holidays"] = new List<string>(holidays);
                    break;

                case DowntimeKind.Periodic:
                    cfg.Parameters["first"] = periodicFirst;
                    cfg.Parameters["period"] = periodicPeriod;
                    cfg.Parameters["duration"] = periodicDuration;
                    if (useUntil) cfg.Parameters["until"] = periodicUntil;
                    cfg.Parameters["allowOverlap"] = allowOverlap;
                    cfg.Parameters["code"] = periodicCode;
                    break;

                case DowntimeKind.Timetable:
                    cfg.Parameters["overlap"] = overlap.ToString();
                    cfg.Parameters["intervals"] = BuildIntervalRecords(keyToId, calendar);
                    if (dataTable != null) ExportTableMapping(cfg, keyToId);
                    break;
            }
            return cfg;
        }

        private List<string> BuildIntervalRecords(Dictionary<string, string> keyToId, SimCalendar calendar)
        {
            var records = new List<string>();
            ElementState defaultState = ElementState.Of(string.IsNullOrEmpty(stateName) ? "STOPPED" : stateName);

            foreach (var r in rows)
            {
                if (r == null) continue;
                string tid = "";
                if (!string.IsNullOrEmpty(r.target) && !keyToId.TryGetValue(r.target, out tid)) continue;
                double start;
                if (!double.TryParse(r.start, NumberStyles.Float, CultureInfo.InvariantCulture, out start)
                    && !calendar.TryParseSimTime(r.start, out start))
                {
                    Debug.LogWarning($"[UnityDowntime] {name}: invalid start '{r.start}' — row skipped.");
                    continue;
                }
                var iv = new DowntimeInterval(start, r.duration,
                    string.IsNullOrEmpty(r.state) ? defaultState : ElementState.Of(r.state), mode, r.code, r.reason);
                records.Add(DowntimeBuilder.EncodeInterval(tid, iv));
            }

            return records;
        }

        /// <summary>The data table and its column mapping: the engine builds the stops from the run's snapshot.</summary>
        private void ExportTableMapping(DowntimeConfig cfg, Dictionary<string, string> keyToId)
        {
            cfg.Parameters[DowntimeBuilder.DataTableKey] = dataTable.TableName;
            cfg.Parameters["targetColumn"] = targetColumn ?? "";
            cfg.Parameters["startColumn"] = startColumn ?? "";
            cfg.Parameters["endColumn"] = endColumn ?? "";
            cfg.Parameters["durationColumn"] = durationColumn ?? "";
            cfg.Parameters["codeColumn"] = codeColumn ?? "";
            cfg.Parameters["reasonColumn"] = reasonColumn ?? "";
            cfg.Parameters["durationScale"] = durationScale;
            var rules = new List<string>();
            foreach (var r in codeRules)
                if (r != null && !string.IsNullOrEmpty(r.code))
                    rules.Add($"{r.code}|{r.state}|{r.mode}|{r.factor.ToString("R", CultureInfo.InvariantCulture)}|{(r.ignore ? 1 : 0)}");
            cfg.Parameters["codeRules"] = rules;
            var keys = new List<string>();
            foreach (var kv in keyToId) keys.Add(kv.Key + "|" + kv.Value);
            cfg.Parameters["targetKeys"] = keys;
        }
    }
}
