using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimuLean.Downtime
{
    /// <summary>One working window of a weekly pattern. <see cref="End"/> ≤ <see cref="Start"/> means it ends the next day.</summary>
    public struct ShiftWindow
    {
        public DayOfWeek Day;
        public TimeSpan Start;
        public TimeSpan End;

        public ShiftWindow(DayOfWeek day, TimeSpan start, TimeSpan end) { Day = day; Start = start; End = end; }
    }

    /// <summary>
    /// Weekly working-time pattern (plus non-working dates) on the model calendar.
    /// Text form, parsed by <see cref="Parse"/>:
    /// <c>"Mon-Fri 06:00-14:00,14:00-22:00; Sat 06:00-14:00"</c> (days: Mon Tue Wed Thu Fri Sat Sun,
    /// ranges with '-', lists with ','; <c>22:00-06:00</c> crosses midnight; <c>00:00-24:00</c> = whole day).
    /// </summary>
    public sealed class WeeklyShiftPattern
    {
        private readonly List<ShiftWindow> windows = new List<ShiftWindow>();
        private readonly HashSet<DateTime> holidays = new HashSet<DateTime>();

        public IReadOnlyList<ShiftWindow> Windows => windows;

        /// <summary>Dates (00:00) on which windows that start that day are skipped.</summary>
        public ICollection<DateTime> Holidays => holidays;

        public WeeklyShiftPattern Add(DayOfWeek day, TimeSpan start, TimeSpan end)
        {
            windows.Add(new ShiftWindow(day, start, end));
            return this;
        }

        public WeeklyShiftPattern AddHoliday(DateTime date)
        {
            holidays.Add(date.Date);
            return this;
        }

        /// <summary>True if <paramref name="t"/> falls inside a working window.</summary>
        public bool IsWorking(DateTime t)
        {
            foreach (var (s, e) in MergedWindows(t.AddDays(-1), t.AddDays(1)))
                if (t >= s && t < e) return true;
            return false;
        }

        /// <summary>
        /// Next time after <paramref name="t"/> at which the working status changes
        /// (end of the current window, or start of the next one). Looks ahead 15 days;
        /// returns <see cref="DateTime.MaxValue"/> if the status never changes.
        /// </summary>
        public DateTime NextChange(DateTime t)
        {
            foreach (var (s, e) in MergedWindows(t.AddDays(-1), t.AddDays(15)))
            {
                if (t >= s && t < e) return e;   // inside: the window's end
                if (s > t) return s;             // outside: the next start
            }
            return DateTime.MaxValue;
        }

        /// <summary>Working windows overlapping [from, to), sorted and merged.</summary>
        public List<(DateTime start, DateTime end)> MergedWindows(DateTime from, DateTime to)
        {
            var raw = new List<(DateTime, DateTime)>();
            for (DateTime d = from.Date.AddDays(-1); d <= to.Date; d = d.AddDays(1))
            {
                if (holidays.Contains(d)) continue;
                foreach (var w in windows)
                {
                    if (w.Day != d.DayOfWeek) continue;
                    DateTime s = d + w.Start;
                    DateTime e = w.End > w.Start ? d + w.End : d.AddDays(1) + w.End;
                    if (e > from && s < to) raw.Add((s, e));
                }
            }
            raw.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            var merged = new List<(DateTime, DateTime)>(raw.Count);
            foreach (var (s, e) in raw)
            {
                if (merged.Count > 0 && s <= merged[merged.Count - 1].Item2)
                {
                    var last = merged[merged.Count - 1];
                    if (e > last.Item2) merged[merged.Count - 1] = (last.Item1, e);
                }
                else merged.Add((s, e));
            }
            return merged;
        }

        // ── text form ─────────────────────────────────────────────────────────

        private static readonly string[] DayNames = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

        /// <summary>Parses the text form described on the class. Throws <see cref="FormatException"/> on errors.</summary>
        public static WeeklyShiftPattern Parse(string text)
        {
            var pattern = new WeeklyShiftPattern();
            if (string.IsNullOrWhiteSpace(text)) return pattern;

            foreach (string rawGroup in text.Split(';'))
            {
                string group = rawGroup.Trim();
                if (group.Length == 0) continue;
                int space = group.IndexOf(' ');
                if (space < 0) throw new FormatException($"Shift group '{group}' needs '<days> <ranges>'.");

                List<DayOfWeek> days = ParseDays(group.Substring(0, space).Trim());
                foreach (string range in group.Substring(space + 1).Split(','))
                {
                    string r = range.Trim();
                    if (r.Length == 0) continue;
                    int dash = r.IndexOf('-');
                    if (dash < 0) throw new FormatException($"Time range '{r}' needs 'HH:mm-HH:mm'.");
                    TimeSpan s = ParseTime(r.Substring(0, dash)), e = ParseTime(r.Substring(dash + 1));
                    foreach (var d in days) pattern.Add(d, s, e);
                }
            }
            return pattern;
        }

        private static List<DayOfWeek> ParseDays(string spec)
        {
            var result = new List<DayOfWeek>();
            foreach (string part in spec.Split(','))
            {
                string p = part.Trim().ToLowerInvariant();
                int dash = p.IndexOf('-');
                if (dash > 0)
                {
                    int a = DayIndex(p.Substring(0, dash)), b = DayIndex(p.Substring(dash + 1));
                    for (int i = a; ; i = (i + 1) % 7) { result.Add((DayOfWeek)i); if (i == b) break; }
                }
                else result.Add((DayOfWeek)DayIndex(p));
            }
            return result;
        }

        private static int DayIndex(string s)
        {
            s = s.Trim();
            if (s.Length >= 3) s = s.Substring(0, 3);
            int i = Array.IndexOf(DayNames, s);
            if (i < 0) throw new FormatException($"Unknown day '{s}'.");
            return i;
        }

        private static TimeSpan ParseTime(string s)
        {
            s = s.Trim();
            if (s == "24:00") return TimeSpan.FromHours(24);
            if (TimeSpan.TryParseExact(s, new[] { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" },
                                       CultureInfo.InvariantCulture, out TimeSpan t))
                return t;
            throw new FormatException($"Invalid time '{s}'.");
        }
    }
}
