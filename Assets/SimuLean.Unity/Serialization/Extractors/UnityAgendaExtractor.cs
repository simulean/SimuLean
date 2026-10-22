using System.Collections.Generic;
using SimuLean.Unity;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityAgenda</c>; the headless build side is
    /// <c>UnityAgendaHandler</c> in the engine.</summary>
    internal sealed class UnityAgendaExtractor : ElementExtractorBase
    {
        public override string TypeName => "UnityAgenda";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityAgenda a)) return;
            config.Parameters["sessions"] = a.sessions ?? "";
            config.Parameters["slotMinutes"] = (double)a.slotMinutes;
            config.Parameters["leadDays"] = (double)a.leadDays;
            config.Parameters["reservedPerSession"] = a.reservedPerSession;
            config.Parameters["priorityLabel"] = a.priorityLabel ?? "";
            config.Parameters["overflow"] = a.overflow.ToString();
            if (!string.IsNullOrWhiteSpace(a.noShow)) config.Parameters["noShow"] = a.noShow.Trim();
            if (!string.IsNullOrWhiteSpace(a.cancel)) config.Parameters["cancel"] = a.cancel.Trim();
            config.Parameters["overbookPerSession"] = a.overbookPerSession;
            config.Parameters["arriveOnTime"] = a.arriveOnTime;
            var templates = new List<string>();
            if (a.templates != null) foreach (var t in a.templates) if (!string.IsNullOrWhiteSpace(t)) templates.Add(t.Trim());
            if (templates.Count > 0) config.Parameters["templates"] = templates;
            if (a.templateTable != null) config.Parameters["templateTable"] = a.templateTable.TableName;
            var holidays = new List<string>();
            if (a.holidays != null) foreach (var h in a.holidays) if (!string.IsNullOrWhiteSpace(h)) holidays.Add(h.Trim());
            if (holidays.Count > 0) config.Parameters["holidays"] = holidays;
        }
    }
}
