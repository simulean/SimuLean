using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// Painted floor markings of block (floor) storage: one rectangle outline per ground cell, cells laid out as
    /// <see cref="UnityStorageRack"/> floor storage does (cell (b, r) centred at ((b + 0.5) · cellWidth, 0,
    /// (r + 0.5) · cellDepth)). Each cell is marked on its own, inset from the cell edge, so neighbouring lanes show
    /// two parallel lines with a gap. One sub-mesh (floor paint), a few millimetres above the floor.
    /// </summary>
    public static class FloorMarkingMesh
    {
        public static Mesh Build(int cellsX, int cellsZ, float cellWidth, float cellDepth,
                                 float lineWidth = 0.06f, float inset = 0.04f, Mesh target = null, string name = null)
        {
            var g = new StorageMeshBuilder(1);
            const float y = 0.003f;
            float lw = Mathf.Clamp(lineWidth, 0.01f, 0.3f);
            for (int b = 0; b < Mathf.Max(1, cellsX); b++)
                for (int r = 0; r < Mathf.Max(1, cellsZ); r++)
                {
                    float x0 = b * cellWidth + inset, x1 = (b + 1) * cellWidth - inset;
                    float z0 = r * cellDepth + inset, z1 = (r + 1) * cellDepth - inset;
                    if (x1 - x0 < 2f * lw || z1 - z0 < 2f * lw) continue;
                    Rect(g, x0, z0, x1, z0 + lw, y);            // front line
                    Rect(g, x0, z1 - lw, x1, z1, y);            // back line
                    Rect(g, x0, z0 + lw, x0 + lw, z1 - lw, y);  // left line
                    Rect(g, x1 - lw, z0 + lw, x1, z1 - lw, y);  // right line
                }
            return g.ToMesh(target, name);
        }

        private static void Rect(StorageMeshBuilder g, float x0, float z0, float x1, float z1, float y)
            => g.Quad(0, new Vector3(x0, y, z0), new Vector3(x1, y, z0), new Vector3(x1, y, z1), new Vector3(x0, y, z1), Vector3.up,
                      new Vector2(x0, z0), new Vector2(x1, z0), new Vector2(x1, z1), new Vector2(x0, z1));
    }
}
