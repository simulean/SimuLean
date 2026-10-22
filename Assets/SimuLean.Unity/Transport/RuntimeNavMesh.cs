using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace SimuLean.Unity
{
    /// <summary>
    /// Bakes a NavMesh when the scene starts from the colliders inside its box (centred on this transform) — for
    /// teams moving with <i>Unity Physics</i> (<see cref="NavMeshMover"/>) in scenes without a baked NavMesh Surface
    /// (core navigation API, no package needed). Optional: a NavMesh Surface baked in the editor does the same.
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Transport, "Runtime NavMesh",
        Description = "Bakes a NavMesh on start from the colliders in an area, for resources moved by Unity physics")]
    [AddComponentMenu("SimuLean/Runtime NavMesh")]
    [DefaultExecutionOrder(-1000)]
    public class RuntimeNavMesh : MonoBehaviour
    {
        public Vector3 size = new Vector3(40f, 6f, 40f);
        [Tooltip("Colliders that count as floor or obstacles.")]
        public LayerMask layers = ~0;
        [Min(0.05f)] public float agentRadius = 0.35f;
        [Min(0.1f)] public float agentHeight = 1.8f;
        [Range(0f, 60f)] public float maxSlope = 30f;
        [Min(0f)] public float stepHeight = 0.3f;

        private NavMeshDataInstance instance;

        private void Awake()
        {
            var settings = NavMesh.GetSettingsByID(0);
            settings.agentRadius = agentRadius;
            settings.agentHeight = agentHeight;
            settings.agentSlope = maxSlope;
            settings.agentClimb = stepHeight;
            var bounds = new Bounds(Vector3.zero, size);
            var sources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(new Bounds(transform.position, size), layers, NavMeshCollectGeometry.PhysicsColliders,
                                          0, new List<NavMeshBuildMarkup>(), sources);
            var data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, transform.position, Quaternion.identity);
            if (data != null) instance = NavMesh.AddNavMeshData(data);
        }

        private void OnDestroy() { if (instance.valid) instance.Remove(); }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.5f);
            Gizmos.DrawWireCube(transform.position, size);
        }
    }
}
