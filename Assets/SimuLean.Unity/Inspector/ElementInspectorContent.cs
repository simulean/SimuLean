using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Results.Charts;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>One row of an inspector table (label, value, optional note / colour swatch); <see cref="Header"/> rows start a group.</summary>
    public sealed class InspectorRow
    {
        public string Label, Value, Note;
        public Color? Swatch;
        public bool Header;

        public InspectorRow() { }
        public InspectorRow(string label, string value, string note = null, Color? swatch = null)
        { Label = label; Value = value; Note = note; Swatch = swatch; }

        /// <summary>A group header row.</summary>
        public static InspectorRow H(string title) => new InspectorRow { Label = title, Header = true };
    }

    /// <summary>Everything the inspector shows for one element at one instant (rebuilt at a few Hz, drawn every frame).</summary>
    public sealed class InspectorView
    {
        public string Title, Subtitle, StateText;
        public Color StateColor = Color.gray;
        public readonly List<InspectorRow> Kpis = new List<InspectorRow>();
        public readonly List<InspectorRow> Detail = new List<InspectorRow>();
        public readonly List<ChartData> Charts = new List<ChartData>();
    }

    /// <summary>
    /// Extensibility hook of <see cref="ElementInspectorPanel"/>: a model adds its own rows / charts for the
    /// elements it knows (e.g. custom counters of a model-specific element). Contributors are found (1) in the
    /// panel's <c>contributors</c> list, (2) registered in code with <see cref="ElementInspectorPanel.RegisterContributor"/>,
    /// (3) as components implementing this interface on the selected object or its parents.
    /// <para>Contributors must be <b>read-only</b>: never change the model, schedule events or move items (the
    /// inspector must not change simulation results). They run on the main thread at the panel refresh rate.</para>
    /// </summary>
    public interface IElementInspectorContributor
    {
        /// <summary>True if this contributor has something to add for <paramref name="element"/>.</summary>
        bool Supports(Element element);

        /// <summary>Adds KPI rows (tab 1), after the generic ones. Use <see cref="InspectorRow.H"/> to start a group.</summary>
        void AddKpis(Element element, ElementInspectorContext context, List<InspectorRow> rows);

        /// <summary>Adds static-parameter rows (tab 3).</summary>
        void AddDetail(Element element, ElementInspectorContext context, List<InspectorRow> rows);

        /// <summary>Adds charts (tab 2; the engine's <see cref="ChartData"/>: <see cref="ChartKind.Stacked"/>,
        /// <see cref="ChartKind.Area"/> or <see cref="ChartKind.Bar"/> drawn as a list); called at most about once per second.</summary>
        void AddCharts(Element element, ElementInspectorContext context, List<ChartData> charts);
    }

    /// <summary>Convenience base class for scene contributors (override only what you need).</summary>
    public abstract class ElementInspectorContributor : MonoBehaviour, IElementInspectorContributor
    {
        public abstract bool Supports(Element element);
        public virtual void AddKpis(Element element, ElementInspectorContext context, List<InspectorRow> rows) { }
        public virtual void AddDetail(Element element, ElementInspectorContext context, List<InspectorRow> rows) { }
        public virtual void AddCharts(Element element, ElementInspectorContext context, List<ChartData> charts) { }
    }

    /// <summary>
    /// What the report and the contributors get: the current time, the model calendar, the live history and
    /// formatting helpers that respect the calendar (with the default calendar, times are plain simulation units).
    /// </summary>
    public sealed class ElementInspectorContext
    {
        public SimClock Clock;
        public ElementLiveHistory History;
        public GameObject Owner;
        /// <summary>Simulation time of this refresh.</summary>
        public double Now;
        /// <summary>Label of one simulation time unit when the calendar is the default one (e.g. "s", "min", "tu").</summary>
        public string TimeUnitLabel = "s";
        /// <summary>Length (sim units) of one column of the state-split chart; set by the panel.</summary>
        public double Period;
        /// <summary>Label of a period ("day" with a calendar).</summary>
        public string PeriodLabel = "period";

        public SimCalendar Calendar => Clock != null ? Clock.Context.Calendar : SimCalendar.Default;

        /// <summary>True when the model has a real calendar (start date set on the clock or config).</summary>
        public bool HasCalendar => !Calendar.IsDefault;

        public static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        public static string N(double v, string fmt = "N0")
            => double.IsNaN(v) || double.IsInfinity(v) ? "–" : v.ToString(fmt, Culture);

        public static string Pct(double part, double total) => total > 0 ? N(100 * part / total, "N1") + " %" : "–";

        /// <summary>A simulation-time duration: h / min / d with a calendar, plain units otherwise.</summary>
        public string Duration(double simDuration)
        {
            if (double.IsNaN(simDuration)) return "–";
            if (!HasCalendar) return N(simDuration, simDuration < 100 ? "N2" : "N0") + " " + TimeUnitLabel;
            double s = Calendar.ToSeconds(simDuration);
            if (s < 120) return N(s, "N1") + " s";
            if (s < 5400) return N(s / 60, "N1") + " min";
            if (s < 2 * 86400) return N(s / 3600, "N1") + " h";
            return N(s / 86400, "N1") + " d";
        }

        /// <summary>A simulation time instant: calendar date or plain units.</summary>
        public string Instant(double simTime, string dateFormat = "yyyy-MM-dd HH:mm")
            => HasCalendar ? Calendar.Format(simTime, dateFormat) : N(simTime, "N0") + " " + TimeUnitLabel;

        /// <summary>A rate given a count over <see cref="Now"/>: per hour with a calendar, per time unit otherwise.</summary>
        public string Rate(double count)
        {
            if (Now <= 0) return "–";
            if (!HasCalendar) return N(count / Now, "N3") + " / " + TimeUnitLabel;
            double hours = Calendar.ToSeconds(Now) / 3600.0;
            return hours > 0 ? N(count / hours, "N1") + " / h" : "–";
        }
    }

    /// <summary>State colours shared by the inspector (known states fixed, others from a stable hash).</summary>
    public static class InspectorPalette
    {
        public static readonly Color Busy = new Color(0.30f, 0.75f, 0.35f);
        public static readonly Color Setup = new Color(0.95f, 0.80f, 0.25f);
        public static readonly Color Blocked = new Color(0.97f, 0.56f, 0.18f);
        public static readonly Color Down = new Color(0.88f, 0.28f, 0.24f);
        public static readonly Color Scheduled = new Color(0.38f, 0.50f, 0.90f);
        public static readonly Color Idle = new Color(0.55f, 0.57f, 0.61f);
        public static readonly Color Travel = new Color(0.30f, 0.72f, 0.82f);
        public static readonly Color Content = new Color(0.30f, 0.55f, 0.90f);
        public static readonly Color Max = new Color(0.97f, 0.62f, 0.25f);

        /// <summary>Preferred display order of the core states (others follow in log order) — <see cref="ChartPalette.StateOrder"/>.</summary>
        public static string[] StateOrder => ChartPalette.StateOrder;

        /// <summary>A state's colour (<see cref="ChartPalette.StateColor"/>: one palette for the inspector and every chart).</summary>
        public static Color StateColor(string name) => ToColor(ChartPalette.StateColor(name));

        /// <summary>0xRRGGBB → colour, and back.</summary>
        public static Color ToColor(int rgb) { var (r, g, b) = ChartPalette.ToFloats(rgb); return new Color(r, g, b); }
        public static int Rgb(Color c) => ChartPalette.Rgb(c.r, c.g, c.b);

        /// <summary>Human-readable state name ("WAITING_FOR_RESOURCE" → "Waiting for operator").</summary>
        public static string StateText(string name)
        {
            if (string.IsNullOrEmpty(name)) return "–";
            string s = name.Replace('_', ' ').ToLowerInvariant();
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
