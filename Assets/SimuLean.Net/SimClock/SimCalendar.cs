using System;
using System.Globalization;

namespace SimuLean
{
    /// <summary>
    /// Maps simulation time (a unit-less <c>double</c> owned by <see cref="SimClock"/>)
    /// to calendar dates and back. One calendar per model, owned by
    /// <see cref="SimContext.Calendar"/>.
    ///
    /// <para>The engine keeps simulating in plain numbers; the calendar is only a
    /// view: <c>date = Start + simTime · SecondsPerUnit</c>. It is used to convert
    /// date-stamped input data (order books, downtime traces, shift patterns) to sim
    /// time at load time, and to format sim time for reports.</para>
    ///
    /// <para>Immutable and thread-safe. Dates are treated as local "wall clock" time
    /// with no time zone or daylight-saving adjustment (a plant calendar).</para>
    /// </summary>
    public sealed class SimCalendar
    {
        /// <summary>Calendar used when a model does not configure one: 2000-01-01 00:00, seconds.</summary>
        public static readonly SimCalendar Default = new SimCalendar(new DateTime(2000, 1, 1), 1.0, isDefault: true);

        /// <summary>Date and time that corresponds to simulation time 0.</summary>
        public DateTime Start { get; }

        /// <summary>Length of one simulation time unit in seconds (1 = the sim clock counts seconds).</summary>
        public double SecondsPerUnit { get; }

        /// <summary>True when no start date was configured (<see cref="Default"/>, <see cref="Undated"/>): dates count from
        /// 2000-01-01, and views show model time rather than calendar dates. The time unit still applies.</summary>
        public bool IsDefault { get; }

        /// <summary>Date formats accepted by <see cref="TryParseDate"/> besides ISO-8601.</summary>
        public static readonly string[] DateFormats =
        {
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm",
            "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy H:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy H:mm", "dd/MM/yyyy",
            "d/M/yyyy HH:mm:ss", "d/M/yyyy H:mm:ss", "d/M/yyyy HH:mm", "d/M/yyyy H:mm", "d/M/yyyy",
        };

        public SimCalendar(DateTime start, double secondsPerUnit = 1.0) : this(start, secondsPerUnit, false) { }

        /// <summary>A calendar without a start date but with a time unit (e.g. 3600 = the model counts hours): dates from
        /// 2000-01-01 at that unit (the unit was dropped when no start date was set).</summary>
        public static SimCalendar Undated(double secondsPerUnit)
            => secondsPerUnit > 0 && secondsPerUnit != 1.0 ? new SimCalendar(Default.Start, secondsPerUnit, isDefault: true) : Default;

        private SimCalendar(DateTime start, double secondsPerUnit, bool isDefault)
        {
            if (secondsPerUnit <= 0) throw new ArgumentOutOfRangeException(nameof(secondsPerUnit));
            Start = DateTime.SpecifyKind(start, DateTimeKind.Unspecified);
            SecondsPerUnit = secondsPerUnit;
            IsDefault = isDefault;
        }

        /// <summary>Creates a calendar from a date string (see <see cref="TryParseDate"/>); throws if invalid.</summary>
        public static SimCalendar Parse(string start, double secondsPerUnit = 1.0)
        {
            if (!TryParseDate(start, out DateTime dt))
                throw new FormatException($"Invalid calendar start date '{start}'.");
            return new SimCalendar(dt, secondsPerUnit);
        }

        // ── conversions ───────────────────────────────────────────────────────

        /// <summary>Calendar date at simulation time <paramref name="simTime"/>.</summary>
        public DateTime ToDateTime(double simTime) => Start.AddSeconds(simTime * SecondsPerUnit);

        /// <summary>Simulation time of <paramref name="date"/> (negative before <see cref="Start"/>).</summary>
        public double ToSimTime(DateTime date) => (date - Start).TotalSeconds / SecondsPerUnit;

        /// <summary>Converts a duration in seconds to simulation units.</summary>
        public double FromSeconds(double seconds) => seconds / SecondsPerUnit;

        /// <summary>Converts a duration in simulation units to seconds.</summary>
        public double ToSeconds(double simDuration) => simDuration * SecondsPerUnit;

        /// <summary>Converts a <see cref="TimeSpan"/> to simulation units.</summary>
        public double FromTimeSpan(TimeSpan span) => span.TotalSeconds / SecondsPerUnit;

        /// <summary>Parses a date string and converts it to simulation time.</summary>
        public bool TryParseSimTime(string text, out double simTime)
        {
            if (TryParseDate(text, out DateTime dt)) { simTime = ToSimTime(dt); return true; }
            simTime = 0; return false;
        }

        /// <summary>Formats simulation time as a date string (default <c>yyyy-MM-dd HH:mm:ss</c>).</summary>
        public string Format(double simTime, string format = "yyyy-MM-dd HH:mm:ss")
            => ToDateTime(simTime).ToString(format, CultureInfo.InvariantCulture);

        // ── calendar helpers (used by shift patterns) ─────────────────────────

        /// <summary>Day of week at simulation time <paramref name="simTime"/>.</summary>
        public DayOfWeek DayOfWeekAt(double simTime) => ToDateTime(simTime).DayOfWeek;

        /// <summary>Simulation time of the midnight (00:00) that starts the day containing <paramref name="simTime"/>.</summary>
        public double StartOfDay(double simTime) => ToSimTime(ToDateTime(simTime).Date);

        /// <summary>
        /// Parses ISO-8601 or one of <see cref="DateFormats"/> (invariant culture,
        /// day-first for slashed dates). Returns false for null/empty/unparseable text.
        /// </summary>
        public static bool TryParseDate(string text, out DateTime date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture,
                                       DateTimeStyles.AllowWhiteSpaces, out date))
                return true;
            return DateTime.TryParse(text, CultureInfo.InvariantCulture,
                                     DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out date);
        }

        public override string ToString()
            => $"SimCalendar(start={Start:yyyy-MM-dd HH:mm:ss}, unit={SecondsPerUnit}s{(IsDefault ? ", default" : "")})";
    }
}
