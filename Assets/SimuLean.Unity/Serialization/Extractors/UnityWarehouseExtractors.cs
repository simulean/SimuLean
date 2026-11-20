using System.Collections.Generic;
using SimuLean.Unity;

namespace SimuLean.Serialization
{
    /// <summary>Extraction of <c>UnityPickFace</c>; build: <c>PickFaceHandler</c> (<see cref="ReplenishmentBuilders"/>).
    /// The empties destination → a <c>PortOutput</c> connection from port <c>empties</c>.</summary>
    internal sealed class UnityPickFaceExtractor : ElementExtractorBase
    {
        public override string TypeName => "PickFace";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (element is UnityPickFace f) f.Fill(config);
        }

        public override void ExtractConnections(SElement element, string elementId, List<ConnectionConfig> connections,
                                                Dictionary<SElement, string> ids)
        {
            if (!(element is UnityPickFace f) || f.emptiesDestination == null || !ids.TryGetValue(f.emptiesDestination, out string to)) return;
            var c = new ConnectionConfig(elementId, to, "PortOutput");
            c.Port = "empties";
            connections.Add(c);
        }
    }

    /// <summary>Extraction of <c>UnityBaseStockReplenisher</c>; build: <c>BaseStockReplenisherHandler</c>. The SKU table
    /// file is read here (the config carries the rows, so headless runs need no file).</summary>
    internal sealed class UnityBaseStockReplenisherExtractor : ElementExtractorBase
    {
        public override string TypeName => "BaseStockReplenisher";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (element is UnityBaseStockReplenisher r) r.Fill(config);
        }
    }
}
