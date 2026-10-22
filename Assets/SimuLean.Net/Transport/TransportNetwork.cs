using System;
using System.Collections.Generic;

namespace SimuLean.Transport
{
    /// <summary>
    /// The transport network: nodes + typed tracks, shared by all transport
    /// modes. Provides shortest-path routing (Dijkstra) and total distance
    /// between any two nodes, optionally restricted to a set of track types
    /// (e.g. vehicles route over TravelPath tracks only).
    ///
    /// Deterministic: same network + same query → same path (ties broken by
    /// node-id ordinal). Routes are cached per (from, to, mode) and the cache
    /// is invalidated on any topology edit; topology is expected to be static
    /// during a run.
    ///
    /// Engine-side and Unity-free: coordinates/lengths are resolved by the
    /// caller (Unity layer or config) before or while building the network.
    /// </summary>
    public class TransportNetwork
    {
        private readonly Dictionary<string, TransportNode> nodes
            = new Dictionary<string, TransportNode>();

        private readonly List<Track> tracks = new List<Track>();

        // adjacency: nodeId -> outgoing (neighborId, length, track)
        private readonly Dictionary<string, List<Adjacency>> adjacency
            = new Dictionary<string, List<Adjacency>>();

        // routes by (from, to, allowed track types): a tuple key, no string built per lookup (review2 #16)
        private readonly Dictionary<(string, string, string), RouteResult> routeCache
            = new Dictionary<(string, string, string), RouteResult>();

        private struct Adjacency
        {
            public string ToId;
            public double Length;
            public Track Track;
        }

        /// <summary>A resolved route: ordered nodes and total length in meters.</summary>
        public class RouteResult
        {
            public IReadOnlyList<TransportNode> Nodes { get; }
            public double Distance { get; }

            public RouteResult(IReadOnlyList<TransportNode> nodesOnPath, double distance)
            {
                Nodes = nodesOnPath;
                Distance = distance;
            }
        }

        public IReadOnlyList<Track> Tracks => tracks;
        public IEnumerable<TransportNode> Nodes => nodes.Values;
        public int NodeCount => nodes.Count;

        // ── building ─────────────────────────────────────────────────────────

        public TransportNode AddNode(string id, double x, double y, double z)
        {
            var node = new TransportNode(id, x, y, z);
            AddNode(node);
            return node;
        }

        public void AddNode(TransportNode node)
        {
            if (nodes.ContainsKey(node.Id))
                throw new ArgumentException($"Duplicate transport node id '{node.Id}'.");
            nodes[node.Id] = node;
            adjacency[node.Id] = new List<Adjacency>();
            routeCache.Clear();
        }

        public TransportNode GetNode(string id)
        {
            TransportNode n;
            return nodes.TryGetValue(id, out n) ? n : null;
        }

        /// <summary>
        /// Adds a track. <paramref name="length"/> &lt; 0 means "auto": use the
        /// euclidean distance between the two nodes.
        /// </summary>
        public Track AddTrack(string fromId, string toId,
                              double length = -1,
                              bool bidirectional = true,
                              TrackType type = TrackType.TravelPath,
                              TrackProfile profile = null)
        {
            TransportNode from = GetNode(fromId);
            TransportNode to = GetNode(toId);
            if (from == null) throw new ArgumentException($"Unknown node '{fromId}'.");
            if (to == null) throw new ArgumentException($"Unknown node '{toId}'.");

            double resolved = length >= 0 ? length : from.DistanceTo(to);
            var track = new Track(fromId, toId, resolved, bidirectional, type, profile);
            tracks.Add(track);

            adjacency[fromId].Add(new Adjacency { ToId = toId, Length = resolved, Track = track });
            if (bidirectional)
                adjacency[toId].Add(new Adjacency { ToId = fromId, Length = resolved, Track = track });

            routeCache.Clear();
            return track;
        }

        // ── routing ──────────────────────────────────────────────────────────

        /// <summary>
        /// Shortest route between two nodes over tracks whose type passes
        /// <paramref name="allowedTypes"/> (null = all types). Returns null if
        /// unreachable. Result is cached.
        /// </summary>
        public RouteResult GetRoute(string fromId, string toId, TrackType[] allowedTypes = null)
        {
            if (!nodes.ContainsKey(fromId))
                throw new ArgumentException($"Unknown node '{fromId}'.");
            if (!nodes.ContainsKey(toId))
                throw new ArgumentException($"Unknown node '{toId}'.");

            var cacheKey = (fromId, toId, ModeKeyCached(allowedTypes));
            RouteResult cached;
            if (routeCache.TryGetValue(cacheKey, out cached))
                return cached;

            RouteResult result = Dijkstra(fromId, toId, allowedTypes);
            routeCache[cacheKey] = result;   // negative results cached too
            return result;
        }

        /// <summary>
        /// Total shortest-path distance in meters, or
        /// <see cref="double.PositiveInfinity"/> if unreachable.
        /// </summary>
        public double GetDistance(string fromId, string toId, TrackType[] allowedTypes = null)
        {
            RouteResult r = GetRoute(fromId, toId, allowedTypes);
            return r != null ? r.Distance : double.PositiveInfinity;
        }

        /// <summary>The node closest (euclidean) to a coordinate, or null on an empty network.
        /// Deterministic: ties broken by node-id ordinal.</summary>
        public TransportNode GetClosestNode(double x, double y, double z)
        {
            TransportNode best = null;
            double bestDist = double.PositiveInfinity;
            // iterate in deterministic id order
            foreach (string id in SortedNodeIds())
            {
                TransportNode n = nodes[id];
                double dx = n.X - x, dy = n.Y - y, dz = n.Z - z;
                double d = dx * dx + dy * dy + dz * dz;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = n;
                }
            }
            return best;
        }

        // ── internals ────────────────────────────────────────────────────────

        // the key of the last track-type array asked for (callers pass the same arrays and never change them)
        private TrackType[] lastAllowed;
        private string lastKey;

        private string ModeKeyCached(TrackType[] allowedTypes)
        {
            if (allowedTypes != null && ReferenceEquals(allowedTypes, lastAllowed)) return lastKey;
            string k = ModeKey(allowedTypes);
            lastAllowed = allowedTypes; lastKey = k;
            return k;
        }
        private static string ModeKey(TrackType[] allowedTypes)
        {
            if (allowedTypes == null || allowedTypes.Length == 0)
                return "*";
            var copy = (TrackType[])allowedTypes.Clone();
            Array.Sort(copy);
            return string.Join(",", copy);
        }

        private IEnumerable<string> SortedNodeIds()
        {
            var ids = new List<string>(nodes.Keys);
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        private static bool TypeAllowed(TrackType type, TrackType[] allowedTypes)
        {
            if (allowedTypes == null || allowedTypes.Length == 0)
                return true;
            for (int i = 0; i < allowedTypes.Length; i++)
                if (allowedTypes[i] == type)
                    return true;
            return false;
        }

        private RouteResult Dijkstra(string fromId, string toId, TrackType[] allowedTypes)
        {
            // Networks are editor-authored (tens to low hundreds of nodes);
            // a simple O(V^2) scan with deterministic tie-breaks is plenty and
            // avoids priority-queue nondeterminism.
            var sortedIds = SortedNodeIds();   // once per search, not once per settled node (review2 #16)
            var dist = new Dictionary<string, double>();
            var prev = new Dictionary<string, string>();
            var visited = new HashSet<string>();

            foreach (string id in nodes.Keys)
                dist[id] = double.PositiveInfinity;
            dist[fromId] = 0;

            while (true)
            {
                // pick unvisited node with min dist; tie → lowest id ordinal
                string current = null;
                double best = double.PositiveInfinity;
                foreach (string id in sortedIds)
                {
                    if (visited.Contains(id))
                        continue;
                    double d = dist[id];
                    if (d < best)
                    {
                        best = d;
                        current = id;
                    }
                }

                if (current == null || double.IsPositiveInfinity(best))
                    return null;                         // unreachable
                if (current == toId)
                    break;

                visited.Add(current);

                foreach (Adjacency adj in adjacency[current])
                {
                    if (visited.Contains(adj.ToId))
                        continue;
                    if (!TypeAllowed(adj.Track.Type, allowedTypes))
                        continue;

                    double candidate = dist[current] + adj.Length;
                    double existing = dist[adj.ToId];
                    // strict improvement, or equal-cost tie broken by predecessor id
                    if (candidate < existing ||
                        (candidate == existing && prev.ContainsKey(adj.ToId) &&
                         string.CompareOrdinal(current, prev[adj.ToId]) < 0))
                    {
                        dist[adj.ToId] = candidate;
                        prev[adj.ToId] = current;
                    }
                }
            }

            // reconstruct path
            var path = new List<TransportNode>();
            string walk = toId;
            while (true)
            {
                path.Add(nodes[walk]);
                if (walk == fromId)
                    break;
                walk = prev[walk];
            }
            path.Reverse();
            return new RouteResult(path, dist[toId]);
        }
    }
}
