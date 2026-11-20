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
  (typical for shift ends; the stop takes effect — its state shown, `StopToken.EffectiveStart` — once the element has
  no timed work left; it may still hold items). **AfterCurrent delays a stop, it does not cut it:** a timetable row, a
  periodic stop, a failure's repair and a reaction's stop for a time last their whole length from that moment — a row
  30 + 20 on a machine busy 0..100 stops it 100..120. A Shift is the exception: its stop ends with the calendar's next
  window, so the return stays on time and the stop is shorter by the job that overran the shift end. A stop due while
  another of the same downtime waits for the job: with the overlap policy *Allow* both take effect together (they
  overlap); *Serialize* / *Merge* (and a periodic stop without overlap) queue it behind, for its whole length.
  Both are by design: **a Timetable's return moves with the overrun.** A fixed-hour calendar written as AfterCurrent rows
  drifts day after day — with 125-min jobs a Mon-Fri 06:00-14:00 calendar as rows comes back at 06:20, 06:40, 07:00,
  07:20 (the week's working time is the same, only later); for stops at fixed clock times use a **Shift**, whose return
  stays the calendar's. **Rows that wait for the same job overlap** under *Allow* (a 12:00 + 30 lunch and a 12:45 + 15
  break behind a job ending 12:51 stop it 12:51..13:21: the break is absorbed); to have them one after the other
  (12:51..13:21, then 13:21..13:36) use *Serialize*.
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

Every generator stops with state BREAKDOWN and mode Immediate unless it says otherwise (Periodic: SCHEDULED_DOWN;
Shift: OFF_SHIFT + AfterCurrent); a timetable built from a config stops its rows STOPPED. A config without `stateName` /
`mode` takes these kind defaults (`DowntimeBuilder.DefaultStateName` / `DefaultMode`).

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
2. Stop behaviour: state name (empty = the kind's: MTBF/MTTR BREAKDOWN, Timetable STOPPED, Shift OFF_SHIFT, Periodic
   SCHEDULED_DOWN), mode (*Kind Default* = a Shift AfterCurrent, the others Immediate), input / output blocking.
3. MTBF/MTTR: the distributions (same editor as processing times); Shift: pattern text + holidays;
   Timetable: inline rows and/or a SQL query on the project database with a column mapping and
   per-code rules (`targetKeys` match the table's target column; empty = GameObject name);
   Periodic: first, period, duration, optional until, allow overlap, code (changing the kind clears a state left at
   the former kind's default, so it follows the new kind).

4. *Repair By* (not for shifts): the team whose members repair each stop, its requirements, priority, preemption and
   preemptable (off by default) — the same block as an element's Process resources. *Team Targets*: resource teams whose
   members break down (one generator per member).

`UnitySimClock` starts every `UnityDowntime` after the elements; `UnityModelExtractor` exports them
as `DowntimeConfig`s (database rows resolved at extraction), so the same stops apply in headless
runs and GA evaluations.

## 5. Headless config

```csharp
var dt = new DowntimeConfig("m1-failures", "MtbfMttr", "Machine1");
dt.Set(DowntimeKeys.TimeToFailure, "Exponential~0.001");   // a distribution: SamplerSettings, or its text Type~p1~p2 (ExponentialMean~m too)
dt.Set(DowntimeKeys.TimeToRepair, "Uniform~60~300");
dt.Set(DowntimeKeys.State, "BREAKDOWN");
config.Downtimes.Add(dt);
```

The keys are the downtime component's field paths (`DowntimeKeys`, 2026-11-05: `stateName`, `mode`, `timeToFailure`,
`timeToRepair`, `firstFailure`, `basis`, `busyStates`, `failureCode`). Other kinds: `Timetable` (`rows` = a `List<DowntimeIntervalSettings>`:
`target`, `start` (a model time or a date), `duration`, `state`, `mode`, `code`, `reason`; `overlap`), `Shift` (`shiftPattern`, `holidays`), `Periodic`
(`periodicPeriod`, `periodicDuration`, `periodicFirst`, optional `periodicUntil`, `allowOverlap`, `periodicCode`):

```csharp
var pm = new DowntimeConfig("press-pm", "Periodic", "Press");
pm.Set(DowntimeKeys.PeriodicFirst, 480.0);      // the first stop at 8 h (minutes model)
pm.Set(DowntimeKeys.PeriodicPeriod, 480.0);     // then every 8 h
pm.Set(DowntimeKeys.PeriodicDuration, 30.0);    // for 30 min
pm.Set(DowntimeKeys.Mode, "AfterCurrent");
config.Downtimes.Add(pm);
```

The settings are typed (`DowntimeSettings`): every key is a `DowntimeKeys` constant (`Serialization/DowntimeBuilder.cs`)
or a repair crew's `repairBy.*` path; any other key — a misspelt `ttf`, the old `first` / `period` — fails when it is written,
naming it and the closest valid paths. An unknown `mode`, `basis` or `overlap` fails the same way.

## 6. Repair crews

A downtime (MTBF / MTTR, timetable or periodic) may be repaired by members of a resource team (`repairBy.team`,
`repairBy.requirements`, `repairBy.priority`, `repairBy.preemption`, `repairBy.preemptable`). On a stop the element shows
**WAITING_FOR_REPAIR** while the crew is requested and travels to it (to its work point / location, like a station's
operator); when the whole crew is there the repair time starts (the time to repair, or the interval's / periodic stop's
length) and the element shows the downtime's state (BREAKDOWN by default); at its end the crew is free and the stop
ends. A stop thus lasts the wait for the crew + its travel + the repair; the MTBF calendar basis counts the next failure
from the end of the repair. With a priority and a preemption mode the repair takes the crew from work of lower
priority; a repair is not preemptable unless `repairBy.preemptable` is set (then it waits for its crew again and goes on).
A downtime's target may also be a resource team: each member gets its own generator (its own random numbers). Results
(only with a crew): `repairs` (completed), `repair_waits` (crews arrived), `repair_wait_avg` (over the crews arrived),
`repair_time_avg` (over the repairs completed); the crew's usage by requester shows the repaired
elements. Without a crew a downtime behaves as before.

```csharp
var dt = new DowntimeConfig("m1-failures", "MtbfMttr", "Machine1");
dt.Set(DowntimeKeys.TimeToFailure, "ExponentialMean~3600");
dt.Set(DowntimeKeys.TimeToRepair, "ExponentialMean~600");
dt.Set("repairBy.team", "mechanics");       // a team id
dt.Set("repairBy.priority", "5");
dt.Set("repairBy.preemption", "Resume");
config.Downtimes.Add(dt);
```

## 7. Limitations

Queues / warehouses (no timed work) are not paused — they refuse items in and out while stopped. A source chooses
(*On Stop*): **Hold** (the default: the arrivals wait and leave when the stop ends), **Pause** (no arrivals, the
timetable shifts by the stop) or **Lose** (they come and are dropped, counted in `lost_arrivals`).
Vehicle and operator legs **are** paused by an Immediate stop of the resource and go on with the remaining time.
Downtime parameters (periods, durations, time-to-failure / repair distributions, modes) are study and
optimiser parameters like any other (`ParameterSchema.Downtime`), and their results are study and optimiser metrics
(`downtime:Id.stops`, `downtime_total` — the time the targets actually stood, each stop from when it took effect, equal
to their `state:<stateName>` time —, `downtime_scheduled` — the stop time as scheduled, each stop's planned length;
equal for Immediate stops —, with a crew `repairs`, `repair_waits`, `repair_wait_avg`, `repair_time_avg`; a
downtime on several targets has one generator each, `downtime:Id@Machine1.repairs`; the bare id reads all of them
together: counts summed, the average wait over every crew arrived and the average repair time over every repair completed). A calendar-time failure or a periodic stop
still to come does not hide a stall: the run monitor judges beside it. See `docs/architecture.md` §3.12 and §8.
