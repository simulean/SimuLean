using System;
using System.Collections.Generic;
using SimuLean.Resources;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A distance matrix between locations (transport nodes) for teams with <b>Matrix</b> travel — operators walking,
    /// forklifts on a known layout. Entries can be typed or filled from the nodes'
    /// straight-line distances or from routes on the network; a missing pair uses the reverse one when symmetric.
    /// Exported as a <c>TravelMatrixConfig</c>; later baked from NavMesh.
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Transport, "Travel Matrix",
        Description = "Distances between locations for resources that travel by matrix (operators walking, forklifts)")]
    [AddComponentMenu("SimuLean/Travel Matrix")]
    public class UnityTravelMatrix : MonoBehaviour
    {
        [Serializable]
        public class Entry
        {
            public UnityTransportNode from;
            public UnityTransportNode to;
            [Min(0)] public float distance;
        }

        [Tooltip("A missing pair uses the reverse one.")]
        public bool symmetric = true;

        [Tooltip("Locations the fill buttons connect (all pairs).")]
        public List<UnityTransportNode> nodes = new List<UnityTransportNode>();

        public List<Entry> entries = new List<Entry>();

        /// <summary>The matrix as engine data.</summary>
        public DistanceMatrix Build()
        {
            var m = new DistanceMatrix(symmetric);
            foreach (var e in entries)
                if (e?.from != null && e.to != null) m.Set(e.from.NodeId, e.to.NodeId, e.distance);
            return m;
        }

        public SimuLean.Serialization.TravelMatrixConfig ToConfig(string id)
        {
            var mc = new SimuLean.Serialization.TravelMatrixConfig { Id = id, Symmetric = symmetric };
            foreach (var e in entries)
                if (e?.from != null && e.to != null)
                    mc.Entries.Add(SimuLean.Serialization.ResourceBuild.MatrixEntry(e.from.NodeId, e.to.NodeId, e.distance));
            return mc;
        }

        /// <summary>Fills every pair of <see cref="nodes"/> with the straight-line distance (editor helper).</summary>
        [SimuLeanAction("Fill every pair with the straight-line distance between the points")]
        public void FillStraightLine() => Fill((a, b) => Vector3.Distance(a.transform.position, b.transform.position));

        /// <summary>Fills every pair of <see cref="nodes"/> with the shortest route on <paramref name="network"/>
        /// (unreachable pairs are left out).</summary>
        public void FillFromNetwork(UnityTransportNetwork network)
        {
            var net = network.GetOrBuildNetwork();
            network.InvalidateBuild();   // edit time: do not keep a stale build
            Fill((a, b) => (float)net.GetDistance(a.NodeId, b.NodeId));
        }

        /// <summary>Fills every pair of <see cref="nodes"/> with the walking distance on <paramref name="grid"/>
        /// (the engine's A*, around obstacles; unreachable pairs are left out) — a fixed layout's grid as a matrix.</summary>
        public void FillFromWalkGrid(UnityWalkGrid grid)
        {
            var points = new SimuLean.Transport.TransportNetwork();
            foreach (var n in nodes)
                if (n != null && points.GetNode(n.NodeId) == null)
                    points.AddNode(n.NodeId, n.transform.position.x, n.transform.position.y, n.transform.position.z);
            var travel = new GridTravel(grid.Build(), points, 1);
            Fill((a, b) => (float)travel.Distance(a.NodeId, b.NodeId));
        }

        private void Fill(Func<UnityTransportNode, UnityTransportNode, float> distance)
        {
            entries.Clear();
            for (int i = 0; i < nodes.Count; i++)
                for (int j = symmetric ? i + 1 : 0; j < nodes.Count; j++)
                {
                    if (i == j || nodes[i] == null || nodes[j] == null) continue;
                    float d = distance(nodes[i], nodes[j]);
                    if (float.IsInfinity(d) || float.IsNaN(d)) continue;
                    entries.Add(new Entry { from = nodes[i], to = nodes[j], distance = d });
                }
        }
    }
}
