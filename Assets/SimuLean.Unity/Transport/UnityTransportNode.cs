using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Scene marker for one transport-network node. Its world position feeds
    /// the core <see cref="SimuLean.Transport.TransportNode"/> coordinates at
    /// build time — the engine never reads this Transform afterwards.
    /// </summary>
    [SimuLean.Unity.SimuLeanTool(SimuLean.Unity.SimuLeanToolCategories.Transport, "Transport Node", Description = "A node of a transport network", Order = 1)]
    public class UnityTransportNode : MonoBehaviour
    {
        [Tooltip("Unique node id within the network. Empty = use the GameObject name.")]
        public string nodeId = "";

        public string NodeId => string.IsNullOrEmpty(nodeId) ? gameObject.name : nodeId;

        [Tooltip("Gizmo sphere radius in scene view.")]
        public float gizmoRadius = 0.25f;

        private void OnDrawGizmos()
        {
            // Individual node gizmo (network-level drawing adds edges/arrows).
            Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.9f);
            Gizmos.DrawSphere(transform.position, gizmoRadius);
        }
    }
}
