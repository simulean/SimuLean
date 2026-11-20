using System.Collections.Generic;
using SimuLean.Unity;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityAgenda</c>; the headless build side is
    /// <c>AgendaHandler</c> in the engine.</summary>
    internal sealed class UnityAgendaExtractor : ElementExtractorBase
    {
        public override string TypeName => "Agenda";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityAgenda a)) return;
            config.Set("sessions", a.sessions ?? "");
            config.Set("slotMinutes", (double)a.slotMinutes);
            config.Set("leadDays", (double)a.leadDays);
            config.Set("reservedPerSession", a.reservedPerSession);
            config.Set("priorityLabel", a.priorityLabel ?? "");
            config.Set("overflow", a.overflow.ToString());
            if (!string.IsNullOrWhiteSpace(a.noShow)) config.Set("noShow", a.noShow.Trim());
            if (!string.IsNullOrWhiteSpace(a.cancel)) config.Set("cancel", a.cancel.Trim());
            config.Set("overbookPerSession", a.overbookPerSession);
            config.Set("arriveOnTime", a.arriveOnTime);
            var templates = new List<string>();
            if (a.templates != null) foreach (var t in a.templates) if (!string.IsNullOrWhiteSpace(t)) templates.Add(t.Trim());
            if (templates.Count > 0) config.Set("templates", templates);
            if (a.templateTable != null) config.Set("templateTable", a.templateTable.TableName);
            var holidays = new List<string>();
            if (a.holidays != null) foreach (var h in a.holidays) if (!string.IsNullOrWhiteSpace(h)) holidays.Add(h.Trim());
            if (holidays.Count > 0) config.Set("holidays", holidays);
        }
    }
}
