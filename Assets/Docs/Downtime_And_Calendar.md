# Downtime, shifts and the simulation calendar

Guide to the interruption subsystem and the calendar added on 2026-09-29. Design and
internals: `docs/architecture.md` §3.1 (calendar), §3.12 (downtime), §3.5 (serialization).

## 1. Calendar: simulation time ↔ dates

The engine still simulates plain numbers. A `SimCalendar` on the model context maps them to
dates: `date = Start + simTime × SecondsPerUnit`.

- **Unity:** `UnitySimClock` → *Start Date/Time* (e.g. `2026-01-05 06:00`, ISO or
  `dd/MM/yyyy HH:mm`) and *Seconds per Time Unit* (1 = the clock counts seconds). Empty start =
  default calendar (seconds; `IsDefault` = true). The inspector shows the current
  date while playing and the end date.
- **Headless:** `SimulationConfig.StartDateTime` / `TimeUnitSeconds`; `HeadlessModelFactory`
  sets `clock.Context.Calendar` before building anything.
- **Code:** `clock.Context.Calendar = new SimCalendar(new DateTime(2026, 1, 5, 6, 0, 0), 60);`
  then `calendar.ToSimTime(date)`, `ToDateTime(t)`, `Format(t)`, `TryParseSimTime("05/01/2026 14:00", out t)`,
  `clock.GetDateTime()`.

Set the calendar **before** converting date-stamped data (order books, stop traces, shifts).

## 2. Stopping any element

```csharp
var token = machine.Stop(new StopRequest(ElementState.BREAKDOWN) { Code = "E-01", Reason = "Jam" });
// ... later
machine.Resume(token);
```

- `StopMode.Immediate` (default) pauses the element's timed work now and resumes it later with the
  remaining time; `StopMode.AfterCurrent` lets work in progress finish and refuses new items
  (typical for shift ends; the stop state is shown once the element is empty).
- `BlockInput` (default true) and `BlockOutput` (default: true for Immediate, false for
  AfterCurrent) are enforced by `GeneralLink` and the output strategies — every element honours
  them without extra code.
- Stops may overlap; the element runs again when all are resumed. Events: `Stopped`, `Resumed`,
  `StateChanged`; counters `StopCount`, `ActiveStops`, `IsDown`.
- States: `STOPPED`, `BREAKDOWN`, `SCHEDULED_DOWN`, `OFF_SHIFT` (plus `SETUP`) are predefined; any
  custom name works via `ElementState.Of("MY_STATE")`. Time in each state is in the usual state
  statistics.

**Writing a new element?** Schedule its processing / setup / travel delays with
`ScheduleWork(evt, delay)` instead of `simClock.ScheduleEvent(evt, delay)`; otherwise stops cannot
pause them. Keep `ScheduleEvent` for absolute-time events (arrivals, timetables).

## 3. Generators

All in `SimuLean.Downtime`, engine-pure; build them after the model and call `Start()` after the
elements' own `Start()` (`HeadlessModelFactory.StartModel()` and `UnitySimClock` do this).

| Generator | Use | Key settings |
|---|---|---|
| `MtbfMttrDowntime(target, ttf, ttr)` | random failures | any `IRandomSampler` for time-to-failure / repair, optional `FirstFailure`, `Basis` = `CalendarTime` (from the end of the last repair) or `BusyTime` (only while in `BusyStates`), `Code` |
| `TimetableDowntime(target, intervals, overlap)` | historical stop traces, planned maintenance | `DowntimeInterval(start, duration, state, mode, code, reason)`; `OverlapPolicy` Allow / Serialize / Merge; intervals already over at start are skipped, a running one is clipped |
| `PeriodicDowntime(target, period, duration, first, until, allowOverlap)` | repeating planned stops (preventive maintenance, a break every shift, cleaning cycles) | stops at `first`, `first + period`, … for `duration`; optional `until` (no stop starts at or after it); default SCHEDULED_DOWN + Immediate, `Code` "PERIODIC"; `duration > period` only with `allowOverlap` (stops stack); invalid values fail the build |
| `ShiftDowntime(target, WeeklyShiftPattern.Parse("Mon-Fri 06:00-14:00,14:00-22:00; Sat 06:00-14:00"))` | working calendars | overnight windows (`22:00-06:00`), `AddHoliday(date)`; default AfterCurrent + OFF_SHIFT, on the model calendar; a window's end applies a hair early (`CloseTolerance`), so slots that fill the window exactly (12 × 20 min in 4 h) give 12 jobs, not 13 |

`DowntimeTable.Build(rows | db + sql, mapping)` turns table rows into intervals grouped by target:
`DowntimeTableMapping` names the target / start / end-or-duration / code / reason columns (start as a
number or a date), `DurationScale`, per-code `StateByCode`, `ModeByCode`, `FactorByCode`,
`IgnoredCodes`.

## 4. In a Unity scene

Add *SimuLean → Downtime* (`UnityDowntime`) to any GameObject:

1. `kind` = MtbfMttr / Timetable / Shift / Periodic; `targets` = the scene elements (one independent generator
   per target).
2. Stop behaviour: state name, mode, input / output blocking.
3. MTBF/MTTR: the distributions (same editor as processing times); Shift: pattern text + holidays;
   Timetable: inline rows and/or a SQL query on the project database with a column mapping and
   per-code rules (`targetKeys` match the table's target column; empty = GameObject name);
   Periodic: first, period, duration, optional until, allow overlap, code (choosing Periodic turns a
   BREAKDOWN state into SCHEDULED_DOWN).

`UnitySimClock` starts every `UnityDowntime` after the elements; `UnityModelExtractor` exports them
as `DowntimeConfig`s (database rows resolved at extraction), so the same stops apply in headless
runs and GA evaluations.

## 5. Headless config

```csharp
var dt = new DowntimeConfig("m1-failures", "MtbfMttr", "Machine1");
dt.Parameters["ttf"] = "Exponential~0.001";   // SamplerSpec: Type~p1~p2 (ExponentialMean~m also accepted)
dt.Parameters["ttr"] = "Uniform~60~300";
dt.Parameters["state"] = "BREAKDOWN";
config.Downtimes.Add(dt);
```

Other kinds: `Timetable` (`intervals` = `List<string>` of `"targetId|start|duration|state|mode|code|reason"`,
`overlap`), `Shift` (`pattern`, `holidays`), `Periodic` (`period`, `duration`, `first`, optional `until`,
`allowOverlap`, `code`):

```csharp
var pm = new DowntimeConfig("press-pm", "Periodic", "Press");
pm.Parameters["first"] = 480.0;      // the first stop at 8 h (minutes model)
pm.Parameters["period"] = 480.0;     // then every 8 h
pm.Parameters["duration"] = 30.0;    // for 30 min
pm.Parameters["mode"] = "AfterCurrent";
config.Downtimes.Add(pm);
``` See the `DowntimeConfig` summary in
`Serialization/SimulationConfig.cs`.

## 6. Limitations

Vehicle legs, source arrivals and queues / warehouses (no timed work) are not paused — they refuse
items in and out while stopped. Repair crews (resource teams) are not modelled. Downtime parameters
are not yet GA-optimizable. See `docs/architecture.md` §8 #15.
