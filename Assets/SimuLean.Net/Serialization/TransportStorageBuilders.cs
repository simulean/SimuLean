using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean.Storage;
using SimuLean.Transport;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Flat-list encoding of transport/storage configuration data inside
    /// <see cref="ElementConfig.Parameters"/> (unified proposal §8).
    ///
    /// Everything is a <c>List&lt;string&gt;</c> of pipe-delimited records:
    ///   node : "id|x|y|z"
    ///   track: "fromId|toId|length|bidirectional|trackType"   (length &lt; 0 = auto/euclidean)
    ///   lane : "laneId|x|y|z|capacity|accessDiscipline|skuConstraint|nodeId" (nodeId may be empty)
    ///
    /// Rationale: no tuple-keyed dictionaries (JSON-hostile), and the GA's
    /// in-memory CloneConfig already deep-clones List&lt;string&gt; values, so
    /// parallel evaluations never share mutable state. All numbers use
    /// InvariantCulture round-trip format. Ids containing '|' are sanitized
    /// to '_' at encode time.
    /// </summary>
    public static class TransportStorageConfigCodec
    {
        public const char Sep = '|';

        // ── parameter keys ───────────────────────────────────────────────────
        public const string KeyNetworkNodes  = "networkNodes";
        public const string KeyNetworkTracks = "networkTracks";
        public const string KeyLanes         = "lanes";

        // ── primitives ───────────────────────────────────────────────────────

        public static string SanitizeId(string id)
            => string.IsNullOrEmpty(id) ? id : id.Replace(Sep, '_');

        public static string FormatDouble(double value)
            => value.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>A number of a network / lane record: invariant, a decimal comma accepted ("2,5" = 2.5, not 25 as a
        /// thousands separator); anything else fails, naming it.</summary>
        public static double ParseDouble(string text)
        {
            if (SimuLean.Data.ModelTable.TryNumber(text, out double v)) return v;
            throw new FormatException($"Transport / storage record: '{text}' is not a number.");
        }

        /// <summary>Tolerant numeric read: Parameters values may arrive as
        /// int/float/double/string depending on the extraction path.</summary>
        public static double GetDouble(ElementConfig config, string key, double defaultValue)
        {
            object raw;
            if (config.Parameters == null || !config.Parameters.TryGetValue(key, out raw) || raw == null)
                return defaultValue;
            return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        }

        public static int GetInt(ElementConfig config, string key, int defaultValue)
        {
            object raw;
            if (config.Parameters == null || !config.Parameters.TryGetValue(key, out raw) || raw == null)
                return defaultValue;
            return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }

        // ── nodes ────────────────────────────────────────────────────────────

        public static string FormatNode(string id, double x, double y, double z)
            => SanitizeId(id) + Sep + FormatDouble(x) + Sep + FormatDouble(y) + Sep + FormatDouble(z);

        // ── tracks ───────────────────────────────────────────────────────────

        public static string FormatTrack(string fromId, string toId, double length,
                                         bool bidirectional, TrackType type)
            => SanitizeId(fromId) + Sep + SanitizeId(toId) + Sep + FormatDouble(length)
               + Sep + (bidirectional ? "1" : "0") + Sep + type.ToString();

        // ── lanes ────────────────────────────────────────────────────────────

        public static string FormatLane(string laneId, double x, double y, double z, int capacity,
                                        AccessDiscipline access, SkuConstraint sku, string nodeId)
            => SanitizeId(laneId) + Sep + FormatDouble(x) + Sep + FormatDouble(y) + Sep + FormatDouble(z)
               + Sep + capacity.ToString(CultureInfo.InvariantCulture)
               + Sep + access.ToString() + Sep + sku.ToString()
               + Sep + (nodeId == null ? "" : SanitizeId(nodeId));

        // ── decoding ─────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds a <see cref="TransportNetwork"/> from the flat lists stored
        /// in <paramref name="config"/>. Returns null (with a console message)
        /// when the config carries no nodes.
        /// </summary>
        public static TransportNetwork BuildNetwork(NetworkConfig config)
        {
            var nodeRecords  = config?.Nodes;
            var trackRecords = config?.Tracks;
            if (nodeRecords == null || nodeRecords.Count == 0) return null;   // the model has no network

            var network = new TransportNetwork();

            foreach (string record in nodeRecords)
            {
                string[] f = record.Split(Sep);
                if (f.Length != 4)
                {
                    throw new FormatException($"Network node record '{record}': expected id|x|y|z.");
                }
                network.AddNode(f[0], ParseDouble(f[1]), ParseDouble(f[2]), ParseDouble(f[3]));
            }

            if (trackRecords != null)
            {
                foreach (string record in trackRecords)
                {
                    string[] f = record.Split(Sep);
                    if (f.Length != 5)
                    {
                        throw new FormatException($"Network track record '{record}': expected from|to|length|bidirectional|type.");
                    }
                    TrackType type;
                    if (!Enum.TryParse(f[4], out type))
                        type = TrackType.TravelPath;
                    network.AddTrack(f[0], f[1], ParseDouble(f[2]), f[3] == "1", type);
                }
            }

            return network;
        }

        /// <summary>Parses one lane record into a live <see cref="StorageLane"/>, or null on a malformed record.</summary>
        public static StorageLane ParseLane(string record)
        {
            string[] f = record.Split(Sep);
            if (f.Length != 8)
            {
                throw new FormatException($"Storage lane record '{record}': expected 8 fields.");
            }

            AccessDiscipline access;
            if (!Enum.TryParse(f[5], out access))
                access = AccessDiscipline.Direct;
            SkuConstraint sku;
            if (!Enum.TryParse(f[6], out sku))
                sku = SkuConstraint.Mixed;

            var coordinate = new Coordinate(ParseDouble(f[1]), ParseDouble(f[2]), ParseDouble(f[3]));
            string nodeId = string.IsNullOrEmpty(f[7]) ? null : f[7];
            int capacity = int.Parse(f[4], CultureInfo.InvariantCulture);

            return new StorageLane(f[0], coordinate, capacity, access, sku, nodeId);
        }
    }

    /// <summary>
    /// Engine-pure build logic for the transport/storage element handlers
    /// (ElementConfig → live headless element). Kept free of Unity types so it
    /// is exercised verbatim by the standalone serialization test suite; the
    /// IElementHandler classes are thin wrappers that add scene extraction.
    ///
    /// The transport network is model-level (<see cref="SimulationConfig.Network"/>); the
    /// resources that move a storage's items are its Transport use (<see cref="ResourceUseKeys"/>).
    /// </summary>
    public static class TransportStorageBuilders
    {
        // ── StorageSystem ────────────────────────────────────────────────────

        public static Element BuildStorageSystem(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var system = new StorageSystem(config.Name, clock, vElement);

            var laneRecords = config.GetParameter<List<string>>(TransportStorageConfigCodec.KeyLanes);
            if (laneRecords != null)
            {
                foreach (string record in laneRecords)
                {
                    StorageLane lane = TransportStorageConfigCodec.ParseLane(record);
                    if (lane != null)
                        system.AddLane(lane);
                }
            }

            system.SetLocationPolicy(BuildPolicy(config, "locationPolicy"));
            system.SetRelocationPolicy(BuildPolicy(config, "relocationPolicy"));   // null = same as location

            system.SetReferencePoint(
                TransportStorageConfigCodec.GetDouble(config, "refX", 0.0),
                TransportStorageConfigCodec.GetDouble(config, "refY", 0.0),
                TransportStorageConfigCodec.GetDouble(config, "refZ", 0.0));

            return system;
        }

        /// <summary>
        /// Builds a slotting policy from "<paramref name="prefix"/>Type"
        /// (FirstAvailable | ClosestSpot | Random | DedicatedBySku) plus the
        /// optional "<paramref name="prefix"/>_skuMap" list of "sku=laneId"
        /// pairs. Missing/empty/unknown type → null (caller-appropriate default).
        /// </summary>
        public static LocationPolicyBase BuildPolicy(ElementConfig config, string prefix)
        {
            string type = config.GetParameter<string>(prefix + "Type", null);
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
                    var pairs = config.GetParameter<List<string>>(prefix + "_skuMap");
                    if (pairs != null)
                    {
                        foreach (string pair in pairs)
                        {
                            if (string.IsNullOrEmpty(pair)) continue;
                            int eq = pair.IndexOf('=');
                            if (eq <= 0 || eq == pair.Length - 1) continue;
                            map[pair.Substring(0, eq).Trim()] = pair.Substring(eq + 1).Trim();
                        }
                    }
                    return new DedicatedBySkuPolicy(map);
                }
                default:
                    throw new FormatException($"Unknown storage policy '{type}' (key '{prefix}Type').");
            }
        }
    }
}
