using System.Collections.Generic;
using SimuLean.Unity;

namespace SimuLean.Serialization
{
    /// <summary>Extraction of <c>UnityLineSideUnpacker</c>; build: <c>LineSideUnpackerHandler</c> in the engine. The
    /// empties destination → a <c>PortOutput</c> connection from port <c>empties</c>.</summary>
    internal sealed class UnityLineSideUnpackerExtractor : ElementExtractorBase
    {
        public override string TypeName => "LineSideUnpacker";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (element is UnityLineSideUnpacker u) u.Fill(config.SettingsAs<LineSideUnpackerSettings>());
        }

        public override void ExtractConnections(SElement element, string elementId, List<ConnectionConfig> connections,
                                                Dictionary<SElement, string> ids)
        {
            if (!(element is UnityLineSideUnpacker u) || u.emptiesDestination == null ||
                !ids.TryGetValue(u.emptiesDestination, out string to)) return;
            var c = new ConnectionConfig(elementId, to, "PortOutput");
            c.Port = Separator.PortEmpties;
            connections.Add(c);
        }
    }
}
