using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Storage;
using SimuLean.Transport;

namespace SimuLean.Serialization
{
    /// <summary>Builds the model's transport network from its config (<see cref="NetworkConfig"/>: typed nodes and tracks).</summary>
    public static class TransportStorageConfigCodec
    {
        /// <summary>
        /// Rebuilds a <see cref="TransportNetwork"/> from <paramref name="config"/>; null when it has no nodes.
        /// </summary>
        public static TransportNetwork BuildNetwork(NetworkConfig config)
        {
            if (config?.Nodes == null || config.Nodes.Count == 0) return null;   // the model has no network
            var network = new TransportNetwork();
            foreach (var n in config.Nodes)
            {
                if (n == null || string.IsNullOrEmpty(n.Id)) throw new FormatException("Network: a node without an id.");
                network.AddNode(n.Id, n.X, n.Y, n.Z);
            }
            foreach (var t in config.Tracks ?? new List<NetworkTrackConfig>())
                if (t != null) network.AddTrack(t.From, t.To, t.Length, t.Bidirectional, t.Type);
            return network;
        }
    }

    /// <summary>
    /// Engine-pure build logic for the transport/storage element handlers
    /// (ElementConfig → live headless element). Kept free of Unity types so it
    /// is exercised verbatim by the standalone serialization test suite; the
    /// IElementHandler classes are thin wrappers that add scene extraction.
    ///
    /// The transport network is model-level (<see cref="SimulationConfig.Network"/>); the
    /// resources that move a storage's items are its Transport use (<c>transportBy</c>).
    /// </summary>
    public static class TransportStorageBuilders
    {
        // ── StorageSystem ────────────────────────────────────────────────────

        /// <summary>A storage from its typed settings (<see cref="StorageSystemSettings"/>): lanes, slotting,
        /// physical dimensions, put-away by transport and pass-through release.</summary>
        public static Element BuildStorageSystem(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<StorageSystemSettings>();
            var system = new StorageSystem(config.Name, clock, vElement);

            foreach (var l in s.Lanes ?? new List<StorageLaneSettings>())
            {
                if (l == null) continue;
                system.AddLane(new StorageLane(l.Id, new Coordinate(l.X, l.Y, l.Z), l.Capacity, l.Access, l.Sku,
                                               string.IsNullOrEmpty(l.Node) ? null : l.Node)
                {
                    SlotWidth = l.SlotWidth, SlotHeight = l.SlotHeight, SlotLength = l.SlotLength,
                });
            }

            system.SetLocationPolicy(BuildPolicy(s, s.LocationPolicy, "locationPolicy"));
            system.SetRelocationPolicy(BuildPolicy(s, s.RelocationPolicy, "relocationPolicy"));   // null = same as location

            system.SetReferencePoint(s.RefX, s.RefY, s.RefZ);

            system.PhysicalDimensions = s.PhysicalDimensions;
            system.LengthLabel = s.LengthLabel;
            system.WidthLabel = s.WidthLabel;
            system.HeightLabel = s.HeightLabel;
            system.PutAwayByTransport = s.PutAwayByTransport;
            system.InboundCapacity = Math.Max(1, s.InboundCapacity);
            system.ReleaseAfterDwell = s.ReleaseAfterDwell;
            system.MinimumDwellTime = Math.Max(0, s.MinimumDwellTime);

            return system;
        }

        /// <summary>
        /// A slotting policy by name (FirstAvailable | ClosestSpot | Random | DedicatedBySku, with the
        /// <see cref="StorageSystemSettings.DedicatedSkuMappings"/>). Absent / empty → null
        /// (caller-appropriate default).
        /// </summary>
        public static LocationPolicyBase BuildPolicy(StorageSystemSettings s, string type, string key)
        {
            if (string.IsNullOrEmpty(type))
                return null;

            switch (type)
            {
                case "FirstAvailable":
                    return new FirstAvailablePolicy();
                case "ClosestSpot":
                    return new ClosestSpotPolicy();
                case "Random":
                    return new RandomPolicy();
                case "DedicatedBySku":
                {
                    var map = new Dictionary<string, string>();
                    foreach (var m in s.DedicatedSkuMappings ?? new List<SkuLaneSettings>())
                    {
                        if (m == null || string.IsNullOrWhiteSpace(m.Sku) || string.IsNullOrWhiteSpace(m.Lane)) continue;
                        map[m.Sku.Trim()] = m.Lane.Trim();
                    }
                    return new DedicatedBySkuPolicy(map);
                }
                default:
                    throw new FormatException($"Unknown storage policy '{type}' (key '{key}').");
            }
        }
    }
}
