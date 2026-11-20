using System;
using System.Collections.Generic;
using SimuLean.Downtime;
using SimuLean.Healthcare;

namespace SimuLean.Serialization
{
    /// <summary>Headless build of an <see cref="Agenda"/>. Keys: <c>sessions</c> (a weekly
    /// pattern), <c>slotMinutes</c> and <c>leadDays</c> (calendar minutes / days, converted with the model's time unit),
    /// <c>reservedPerSession</c>, <c>priorityLabel</c>, <c>overflow</c> (+ <c>overbookPerSession</c>), <c>noShow</c> and
    /// <c>cancel</c> (expressions), <c>holidays</c> (dates, on top of the model's), and the templates of later periods:
    /// <c>templates</c> (records <c>date | sessions | slot minutes</c>) and / or <c>templateTable</c> (a data table with the
    /// columns <c>From</c>, <c>Sessions</c>, <c>SlotMinutes</c>).</summary>
    internal sealed class AgendaHandler : ElementHandlerBase
    {
        public override string TypeName => "Agenda";

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<AgendaSettings>();
            var holidays = Holidays(config, s);
            var pattern = Pattern(config, s.Sessions ?? "", holidays);
            // calendar durations → model time units (the calendar is built before the elements)
            double secondsPerUnit = clock.Context.Calendar.SecondsPerUnit;
            double slotMinutes = s.SlotMinutes;
            if (!(slotMinutes > 0)) throw new FormatException($"Agenda '{config.Name}': the slot must be longer than 0 minutes.");
            var agenda = new Agenda(config.Name, clock, pattern, slotMinutes * 60.0 / secondsPerUnit, vElement)
            {
                LeadTime = Math.Max(0, s.LeadDays) * 86400.0 / secondsPerUnit,
                ReservedPerSession = Math.Max(0, s.ReservedPerSession),
                OverbookPerSession = Math.Max(0, s.OverbookPerSession),
                ArriveOnTime = s.ArriveOnTime,
                PriorityLabel = s.PriorityLabel ?? "priority",
                NoShow = s.NoShow,
                Cancel = s.Cancel,
                Overflow = s.Overflow,
            };

            // later templates: records, then a data table's rows
            var records = s.Templates;
            if (records != null)
                foreach (var r in records)
                {
                    if (string.IsNullOrWhiteSpace(r)) continue;
                    var parts = r.Split('|');
                    if (parts.Length != 3) throw new FormatException($"Agenda '{config.Name}': template '{r}' is not 'date | sessions | slot minutes'.");
                    AddTemplate(agenda, config, parts[0], parts[1], parts[2], holidays, secondsPerUnit);
                }
            string tableName = s.TemplateTable;
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                if (!clock.Context.Data.TryGet(tableName, out var table))
                    throw new FormatException($"Agenda '{config.Name}': no data table '{tableName}' for its templates.");
                table.Require("From"); table.Require("Sessions"); table.Require("SlotMinutes");
                for (int row = 0; row < table.RowCount; row++)
                    AddTemplate(agenda, config, table.Text(row, "From"), table.Text(row, "Sessions"), table.Text(row, "SlotMinutes"), holidays, secondsPerUnit);
            }
            return agenda;
        }

        private static void AddTemplate(Agenda agenda, ElementConfig config, string date, string sessions, string minutes,
                                        List<DateTime> holidays, double secondsPerUnit)
        {
            if (!SimCalendar.TryParseDate(date?.Trim(), out DateTime from))
                throw new FormatException($"Agenda '{config.Name}': template date '{date}' is not a date.");
            if (!double.TryParse(minutes?.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double m) || !(m > 0))
                throw new FormatException($"Agenda '{config.Name}': template slot '{minutes}' is not a number of minutes.");
            agenda.AddTemplate(from, Pattern(config, sessions, holidays), m * 60.0 / secondsPerUnit);
        }

        private static WeeklyShiftPattern Pattern(ElementConfig config, string text, List<DateTime> holidays)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException($"Agenda '{config.Name}': no sessions (e.g. Mon-Fri 08:30-12:30).");
            WeeklyShiftPattern pattern;
            try { pattern = WeeklyShiftPattern.Parse(text.Trim()); }
            catch (FormatException e) { throw new FormatException($"Agenda '{config.Name}': sessions '{text}': {e.Message}", e); }
            foreach (var d in holidays) pattern.AddHoliday(d);
            return pattern;
        }

        private static List<DateTime> Holidays(ElementConfig config, AgendaSettings s)
        {
            var list = new List<DateTime>();
            var holidays = s.Holidays;
            if (holidays != null)
                foreach (var h in holidays)
                {
                    if (string.IsNullOrWhiteSpace(h)) continue;
                    if (!SimCalendar.TryParseDate(h, out DateTime d)) throw new FormatException($"Agenda '{config.Name}': holiday '{h}' is not a date.");
                    list.Add(d);
                }
            return list;
        }
    }
}
