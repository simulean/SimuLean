using System.Collections.Generic;
using SimuLean.Resources;
using UnityEngine;
using UnityEngine.AI;

namespace SimuLean.Unity
{
    /// <summary>
    /// The walkable area of the layout for teams with <b>Grid</b> travel — operators, forklifts moving freely around
    /// obstacles. A rectangle centred on this transform (floor at its height), divided in
    /// cells; <see cref="Bake"/> marks each cell free or blocked from the scene's colliders or its NavMesh, and the
    /// result is plain data (<see cref="rows"/>) that the engine searches with its own A* — scene and headless runs
    /// use the same grid, and a layout optimiser can change it without Unity. Re-bake after moving obstacles.
    /// </summary>
    [SimuLeanTool(SimuLeanToolCategories.Transport, "Walk Grid",
        Description = "Walkable area (baked from colliders or the NavMesh) where resources move freely, paths by A*")]
    [AddComponentMenu("SimuLean/Walk Grid")]
    public class UnityWalkGrid : MonoBehaviour
    {
        public enum BakeSource { Colliders, NavMesh }

        [Tooltip("Size of the area along x and z (world units), centred on this transform.")]
        public Vector2 size = new Vector2(20f, 20f);
        [Min(0.05f)] public float cellSize = 0.5f;

        [Tooltip("Colliders: a cell is blocked when a collider of the mask is inside it (above the floor). NavMesh: a cell is free when the NavMesh covers its centre.")]
        public BakeSource source = BakeSource.Colliders;
        [Tooltip("Colliders: layers that block (the floor is ignored: only what rises above it counts).")]
        public LayerMask obstacleMask = ~0;
        [Tooltip("Colliders: height checked above the floor.")]
        [Min(0.01f)] public float obstacleHeight = 1.8f;
        [Tooltip("Extra margin around obstacles (e.g. half the width of whoever walks).")]
        [Min(0f)] public float clearance = 0.2f;

        [Tooltip("Baked cells: one string per row along z, one char per cell along x ('#' blocked, '.' free).")]
        public List<string> rows = new List<string>();

        public bool showGrid = true;

        public int Width => Mathf.Max(1, Mathf.CeilToInt(size.x / cellSize));
        public int Depth => Mathf.Max(1, Mathf.CeilToInt(size.y / cellSize));
        public Vector3 Origin => transform.position - new Vector3(Width * cellSize, 0f, Depth * cellSize) * 0.5f;

        /// <summary>Marks every cell free or blocked (editor helper; also usable at run time before the model starts).</summary>
        [SimuLeanAction("Bake the walkable cells from the scene")]
        public void Bake()
        {
            if (source == BakeSource.Colliders) Physics.SyncTransforms();   // colliders just created / moved in the editor
            rows = new List<string>(Depth);
            Vector3 o = Origin;
            var chars = new char[Width];
            for (int iz = 0; iz < Depth; iz++)
            {
                for (int ix = 0; ix < Width; ix++)
                {
                    var c = new Vector3(o.x + (ix + 0.5f) * cellSize, o.y, o.z + (iz + 0.5f) * cellSize);
                    chars[ix] = Free(c) ? '.' : '#';
                }
                rows.Add(new string(chars));
            }
        }

        private bool Free(Vector3 cellCentre)
        {
            if (source == BakeSource.NavMesh)
                return NavMesh.SamplePosition(cellCentre, out NavMeshHit hit, cellSize * 0.5f + clearance, NavMesh.AllAreas)
                       && new Vector2(hit.position.x - cellCentre.x, hit.position.z - cellCentre.z).magnitude <= cellSize * 0.5f;
            const float lift = 0.05f;   // above the floor's own collider
            var centre = cellCentre + Vector3.up * (lift + obstacleHeight * 0.5f);
            var half = new Vector3(cellSize * 0.5f + clearance, obstacleHeight * 0.5f, cellSize * 0.5f + clearance);
            return !Physics.CheckBox(centre, half, Quaternion.identity, obstacleMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>The baked grid as engine data (bakes first if it never was).</summary>
        public WalkGrid Build()
        {
            if (rows == null || rows.Count == 0) Bake();
            Vector3 o = Origin;
            return WalkGrid.FromRows(o.x, o.z, cellSize, rows, o.y);
        }

        public SimuLean.Serialization.WalkGridConfig ToConfig(string id)
        {
            if (rows == null || rows.Count == 0) Bake();
            Vector3 o = Origin;
            return new SimuLean.Serialization.WalkGridConfig
            {
                Id = id, OriginX = o.x, OriginZ = o.z, Y = o.y, CellSize = cellSize, Rows = new List<string>(rows),
            };
        }

        private void OnDrawGizmosSelected()
        {
            if (!showGrid) return;
            Vector3 o = Origin;
            Gizmos.color = new Color(0.3f, 0.9f, 0.4f, 0.6f);
            Gizmos.DrawWireCube(transform.position, new Vector3(Width * cellSize, 0.01f, Depth * cellSize));
            if (rows == null) return;
            Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.45f);
            for (int iz = 0; iz < rows.Count; iz++)
                for (int ix = 0; ix < rows[iz].Length; ix++)
                    if (rows[iz][ix] == '#')
                        Gizmos.DrawCube(new Vector3(o.x + (ix + 0.5f) * cellSize, o.y + 0.02f, o.z + (iz + 0.5f) * cellSize),
                                        new Vector3(cellSize * 0.95f, 0.02f, cellSize * 0.95f));
        }
    }
}
