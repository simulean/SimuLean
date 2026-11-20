using System.Collections.Generic;
using SimuLean;
using SimuLean.Unity;

namespace SimuLean.Serialization
{
    /// <summary>Shared extraction helpers of the line-supply wrappers (port connections).</summary>
    internal static class LineSupplyExtraction
    {
        public static void Port(List<ConnectionConfig> connections, string from, string to, string type, string port)
        {
            var c = new ConnectionConfig(from, to, type);
            c.Port = port;
            connections.Add(c);
        }
    }

    /// <summary>Extraction of <c>UnityCallOffPoint</c>; build: <c>CallOffPointHandler</c>. Each family's destination →
    /// a <c>PortOutput</c> connection from the family's port.</summary>
    internal sealed class UnityCallOffPointExtractor : ElementExtractorBase
    {
        public override string TypeName => "CallOffPoint";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (element is UnityCallOffPoint p) p.Fill(config.SettingsAs<CallOffPointSettings>());
        }

        public override void ExtractConnections(SElement element, string elementId, List<ConnectionConfig> connections,
                                                Dictionary<SElement, string> ids)
        {
            if (!(element is UnityCallOffPoint p) || p.families == null) return;
            var settings = new CallOffPointSettings();
            p.Fill(settings);
            for (int i = 0; i < p.families.Count && i < settings.Families.Count; i++)
            {
                var dest = p.families[i]?.destination;
                if (dest != null && ids.TryGetValue(dest, out string to))
                    LineSupplyExtraction.Port(connections, elementId, to, "PortOutput", settings.Families[i].Name);
            }
        }
    }

    /// <summary>Extraction of <c>UnityPacedLine</c>; build: <c>PacedLineHandler</c>. Each station's feeders →
    /// <c>PortInput</c> connections to port <c>&lt;station&gt;</c>; a call-off at the line → <c>PortOutput</c> from
    /// <c>&lt;station&gt;.callOff</c> to the family's destination.</summary>
    internal sealed class UnityPacedLineExtractor : ElementExtractorBase
    {
        public override string TypeName => "PacedLine";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (element is UnityPacedLine l) l.Fill(config.SettingsAs<PacedLineSettings>());
        }

        public override void ExtractConnections(SElement element, string elementId, List<ConnectionConfig> connections,
                                                Dictionary<SElement, string> ids)
        {
            if (!(element is UnityPacedLine l) || l.stations == null) return;
            foreach (var st in l.stations)
            {
                if (st == null || string.IsNullOrWhiteSpace(st.name)) continue;
                string station = st.name.Trim();
                if (st.feeders != null)
                    foreach (var f in st.feeders)
                        if (f != null && ids.TryGetValue(f, out string from))
                            LineSupplyExtraction.Port(connections, from, elementId, "PortInput", station);
                var dest = st.family?.destination;
                if (st.callOffAtLine && dest != null && ids.TryGetValue(dest, out string to))
                    LineSupplyExtraction.Port(connections, elementId, to, "PortOutput", station + PacedLine.CallOffSuffix);
            }
        }
    }
}
