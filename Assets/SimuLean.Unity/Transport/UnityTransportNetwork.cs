using System;
using System.Collections.Generic;
using UnityEngine;
using SimuLean.Transport;

namespace SimuLean.Unity
{
    /// <summary>
    /// Scene-authored transport network. Holds node references and typed
    /// edges; builds the core (Unity-free) <see cref="TransportNetwork"/> on
    /// demand, reading world positions once at build time.
    ///
    /// Visualization: edges are drawn as gizmo lines with direction arrows,
    /// color-coded per track type; <see cref="showNetwork"/> hides the whole
    /// overlay when the network is not being edited.
    /// </summary>
    [SimuLean.Unity.SimuLeanTool(SimuLean.Unity.SimuLeanToolCategories.Transport, "Transport Network", Description = "Nodes and tracks used by transport systems")]
    public class UnityTransportNetwork : MonoBehaviour
    {
        [Serializable]
        public class EdgeDefinition
        {
            [Tooltip("One end of the track.")]
            public UnityTransportNode nodeA;
            [Tooltip("The other end of the track.")]
            public UnityTransportNode nodeB;
            [Tooltip("Travelled both ways (else only from A to B).")]
            public bool bidirectional = true;
            [Tooltip("Kind of track (travel path, conveyor…).")]
            public TrackType trackType = TrackType.TravelPath;

            [Tooltip("Length in meters. < 0 = auto (euclidean distance between the nodes).")]
            public float manualLength = -1f;
        }

        [Tooltip("Nodes of the network. Empty = auto-collect UnityTransportNode components in children.")]
        public List<UnityTransportNode> nodes = new List<UnityTransportNode>();

        [Tooltip("The tracks between the nodes.")]
        public List<EdgeDefinition> edges = new List<EdgeDefinition>();

        [Header("Visualization")]
        [Tooltip("Show/hide the whole network overlay (nodes are drawn by their own component).")]
        public bool showNetwork = true;

        [Header("Traffic")]
        [Tooltip("Vehicles a track holds at once, for teams with Traffic (either direction: they cannot pass).")]
        [Min(1)] public int segmentCapacity = 1;

        /// <summary>The traffic control of the current run (built by the model with this network's segment capacity; null
        /// outside a run or without teams under traffic control).</summary>
        public SimuLean.Resources.TrafficControl Traffic { get; set; }

        [Tooltip("Arrow head size in meters.")]
        public float arrowSize = 0.4f;

        [Header("Editing defaults (used by the scene editor)")]
        [Tooltip("Track type assigned to newly drawn edges.")]
        public TrackType newEdgeType = TrackType.TravelPath;

        [Tooltip("Whether newly drawn edges are bidirectional.")]
        public bool newEdgeBidirectional = true;

        [Tooltip("Layer mask for placing nodes with Shift+Click (raycast target). 0 = fall back to the network's Y plane.")]
        public LayerMask groundMask = ~0;

        [Header("Auto-generation")]
        [Tooltip("Anchor transforms (e.g. your SElements) the MST generator connects.")]
        public List<Transform> anchorPoints = new List<Transform>();

        private TransportNetwork builtNetwork;

        // ── build ────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds (once per play session) the core network from the scene
        /// definition. World positions are read here and never again.
        /// </summary>
        public TransportNetwork GetOrBuildNetwork()
        {
            if (builtNetwork != null)
                return builtNetwork;

            var network = new TransportNetwork();

            foreach (UnityTransportNode n in EffectiveNodes())
            {
                Vector3 p = n.transform.position;
                network.AddNode(n.NodeId, p.x, p.y, p.z);
            }

            foreach (EdgeDefinition e in edges)
            {
                if (e.nodeA == null || e.nodeB == null)
                {
                    Debug.LogWarning($"[{name}] Edge with missing node reference skipped.");
                    continue;
                }
                network.AddTrack(e.nodeA.NodeId, e.nodeB.NodeId,
                                 e.manualLength >= 0 ? e.manualLength : -1,
                                 e.bidirectional, e.trackType);
            }

            builtNetwork = network;
            return builtNetwork;
        }

        /// <summary>Discards the built network (next GetOrBuildNetwork rebuilds).</summary>
        public void InvalidateBuild() => builtNetwork = null;

        private List<UnityTransportNode> EffectiveNodes()
        {
            if (nodes != null && nodes.Count > 0)
                return nodes;
            return new List<UnityTransportNode>(GetComponentsInChildren<UnityTransportNode>());
        }

        // ── editor tools ─────────────────────────────────────────────────────

        [ContextMenu("Collect Child Nodes")]
        public void CollectChildNodes()
        {
            nodes = new List<UnityTransportNode>(GetComponentsInChildren<UnityTransportNode>());
            Debug.Log($"[{name}] Collected {nodes.Count} transport nodes.");
        }

        /// <summary>
        /// Explicit editor tool (NOT a runtime default): places every model element (<see cref="SElement"/>) that
        /// has no <c>location</c> on the closest network node. Results are ordinary editable references.
        /// </summary>
        [ContextMenu("Place Elements On Closest Node")]
        public void PlaceElementsOnClosestNode()
        {
            List<UnityTransportNode> all = EffectiveNodes();
            if (all.Count == 0)
            {
                Debug.LogWarning($"[{name}] No nodes to place elements on.");
                return;
            }

            int assigned = 0;
            foreach (SElement el in UnityCompat.FindAll<SElement>())
            {
                if (el.location != null)
                    continue;   // never override an explicit assignment

                UnityTransportNode closest = null;
                float best = float.PositiveInfinity;
                foreach (UnityTransportNode n in all)
                {
                    float d = (n.transform.position - el.transform.position).sqrMagnitude;
                    if (d < best) { best = d; closest = n; }
                }
#if UNITY_EDITOR
                UnityEditor.Undo.RecordObject(el, "Place element on node");
#endif
                el.location = closest;
                assigned++;
                Debug.Log($"[{name}] {el.name} → node '{closest.NodeId}' ({Mathf.Sqrt(best):F1} m). Review and adjust if needed.");
            }
            Debug.Log($"[{name}] Place-on-closest done: {assigned} element(s) placed.");
        }

        /// <summary>The network as model data (nodes and tracks) for headless runs.</summary>
        public SimuLean.Serialization.NetworkConfig ToConfig()
        {
            var cfg = new SimuLean.Serialization.NetworkConfig { SegmentCapacity = segmentCapacity };
            foreach (UnityTransportNode n in EffectiveNodes())
            {
                Vector3 p = n.transform.position;
                cfg.Nodes.Add(SimuLean.Serialization.TransportStorageConfigCodec.FormatNode(n.NodeId, p.x, p.y, p.z));
            }
            foreach (EdgeDefinition e in edges)
                if (e.nodeA != null && e.nodeB != null)
                    cfg.Tracks.Add(SimuLean.Serialization.TransportStorageConfigCodec.FormatTrack(
                        e.nodeA.NodeId, e.nodeB.NodeId, e.manualLength >= 0 ? e.manualLength : -1, e.bidirectional, e.trackType));
            return cfg;
        }

        // ── gizmos ───────────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            if (!showNetwork || edges == null)
                return;

            foreach (EdgeDefinition e in edges)
            {
                if (e.nodeA == null || e.nodeB == null)
                    continue;

                Vector3 a = e.nodeA.transform.position;
                Vector3 b = e.nodeB.transform.position;

                Gizmos.color = ColorFor(e.trackType);
                Gizmos.DrawLine(a, b);

                // direction arrows: one per direction at 1/3 and 2/3 of the edge
                DrawArrow(a, b, 0.55f);
                if (e.bidirectional)
                    DrawArrow(b, a, 0.55f);
            }
        }

        private static Color ColorFor(TrackType type)
        {
            switch (type)
            {
                case TrackType.Conveyor: return new Color(1f, 0.6f, 0.1f);   // orange
                case TrackType.Crane: return new Color(0.8f, 0.2f, 0.9f);    // purple
                default: return new Color(0.2f, 0.9f, 0.3f);                 // green
            }
        }

        private void DrawArrow(Vector3 from, Vector3 to, float positionAlong)
        {
            Vector3 dir = (to - from).normalized;
            if (dir == Vector3.zero)
                return;

            Vector3 tip = Vector3.Lerp(from, to, positionAlong);
            Vector3 right = Quaternion.LookRotation(dir) * Quaternion.Euler(0, 155, 0) * Vector3.forward;
            Vector3 left = Quaternion.LookRotation(dir) * Quaternion.Euler(0, -155, 0) * Vector3.forward;
            Gizmos.DrawLine(tip, tip + right * arrowSize);
            Gizmos.DrawLine(tip, tip + left * arrowSize);
        }
    }
}
