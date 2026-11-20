using System.Collections.Generic;
using SimuLean.Unity;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnitySequencingStation</c>; the build side is
    /// <c>SequencingStationHandler</c> (codec <see cref="SequencingStationBuilder"/>). Ports: orders and pieces by
    /// <c>PortInput</c>, pieces from a list by <c>PullFromList</c> (port <c>pieces</c>), served orders by <c>PortOutput</c>.</summary>
    internal sealed class UnitySequencingStationExtractor : ElementExtractorBase
    {
        public override string TypeName => SequencingStationBuilder.TypeName;

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (element is UnitySequencingStation s) s.Fill(config.SettingsAs<SequencingStationSettings>());
        }

        public override void ExtractConnections(SElement element, string elementId, List<ConnectionConfig> connections,
                                                Dictionary<SElement, string> ids)
        {
            if (!(element is UnitySequencingStation s)) return;
            void In(IEnumerable<SElement> sources, string port)
            {
                if (sources == null) return;
                foreach (var src in sources)
                    if (src != null && ids.TryGetValue(src, out string srcId))
                    {
                        var c = new ConnectionConfig(srcId, elementId, "PortInput");
                        c.Port = port;
                        connections.Add(c);
                    }
            }
            In(s.orderSources, SequencingStation.PortOrders);
            In(s.pieceSources, SequencingStation.PortPieces);
            if (s.pieceList != null && s.pieceList.list != null)
            {
                // Same id as the scene extractor gives the list.
                var pull = new ConnectionConfig("list_" + s.pieceList.list.ListName, elementId, "PullFromList");
                pull.Query = s.pieceList.query != null ? s.pieceList.query.Encode() : "";
                pull.Port = SequencingStation.PortPieces;
                connections.Add(pull);
            }
            if (s.servedOrdersTo != null && ids.TryGetValue(s.servedOrdersTo, out string to))
            {
                var c = new ConnectionConfig(elementId, to, "PortOutput");
                c.Port = SequencingStation.PortOrders;
                connections.Add(c);
            }
        }
    }
}
