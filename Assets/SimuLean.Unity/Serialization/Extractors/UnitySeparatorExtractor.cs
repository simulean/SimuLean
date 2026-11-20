using System.Collections.Generic;
using SimuLean.Unity;

namespace SimuLean.Serialization
{
    /// <summary>Extraction of <c>UnitySeparator</c>; build: <c>SeparatorHandler</c> in the engine. The empties
    /// destination → a <c>PortOutput</c> connection from port <c>empties</c>.</summary>
    internal sealed class UnitySeparatorExtractor : ElementExtractorBase
    {
        public override string TypeName => "Separator";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (element is UnitySeparator s) s.Fill(config.SettingsAs<SeparatorSettings>());
        }

        public override void ExtractConnections(SElement element, string elementId, List<ConnectionConfig> connections,
                                                Dictionary<SElement, string> ids)
        {
            if (!(element is UnitySeparator s) || s.mode != SeparatorMode.Unpack || s.emptiesDestination == null ||
                !ids.TryGetValue(s.emptiesDestination, out string to)) return;
            var c = new ConnectionConfig(elementId, to, "PortOutput");
            c.Port = Separator.PortEmpties;
            connections.Add(c);
        }
    }
}
