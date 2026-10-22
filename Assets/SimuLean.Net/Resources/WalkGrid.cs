using System;
using System.Collections.Generic;
using SimuLean.Transport;

namespace SimuLean.Resources
{
    /// <summary>A point of a path (world coordinates; y = height).</summary>
    public struct WayPoint
    {
        public double X, Y, Z;
        public WayPoint(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    /// <summary>
    /// The walkable area of a layout as a grid on the horizontal plane x / z: cells of
    /// <see cref="CellSize"/> from (<see cref="OriginX"/>, <see cref="OriginZ"/>), free or blocked. Engine data —
    /// Unity bakes it from colliders or the NavMesh, a layout optimiser can change it — and paths are found here
    /// (<see cref="FindPath"/>: A* on 8 neighbours without cutting corners, then line-of-sight smoothing), so they
    /// work headless and in parallel runs.
    /// </summary>
    public sealed class WalkGrid
    {
        private readonly bool[] walkable;

        public WalkGrid(double originX, double originZ, double cellSize, int width, int depth, double y = 0)
        {
            if (!(cellSize > 0)) throw new ArgumentException("cellSize must be > 0.");
            if (width < 1 || depth < 1) throw new ArgumentException("the grid needs at least one cell.");
            OriginX = originX; OriginZ = originZ; CellSize = cellSize; Width = width; Depth = depth; Y = y;
            walkable = new bool[width * depth];
            for (int i = 0; i < walkable.Length; i++) walkable[i] = true;
        }

        public double OriginX { get; }
        public double OriginZ { get; }
        public double CellSize { get; }
        /// <summary>Cells along x / along z.</summary>
        public int Width { get; }
        public int Depth { get; }
        /// <summary>Height of the floor (paths' intermediate points).</summary>
        public double Y { get; }

        public bool Inside(int ix, int iz) => ix >= 0 && iz >= 0 && ix < Width && iz < Depth;
        public bool IsWalkable(int ix, int iz) => Inside(ix, iz) && walkable[iz * Width + ix];
        public void SetWalkable(int ix, int iz, bool free) { if (Inside(ix, iz)) walkable[iz * Width + ix] = free; }

        public int Cell(double v, double origin) => (int)Math.Floor((v - origin) / CellSize);
        public double CenterX(int ix) => OriginX + (ix + 0.5) * CellSize;
        public double CenterZ(int iz) => OriginZ + (iz + 0.5) * CellSize;

        /// <summary>Rows of characters (row = z index, char = x index): '#' blocked, anything else free.</summary>
        public static WalkGrid FromRows(double originX, double originZ, double cellSize, IList<string> rows, double y = 0)
        {
            if (rows == null || rows.Count == 0) throw new ArgumentException("the grid needs rows.");
            int width = 0;
            foreach (var r in rows) width = Math.Max(width, r?.Length ?? 0);
            var g = new WalkGrid(originX, originZ, cellSize, width, rows.Count, y);
            for (int iz = 0; iz < rows.Count; iz++)
                for (int ix = 0; ix < width; ix++)
                    g.SetWalkable(ix, iz, rows[iz] != null && ix < rows[iz].Length && rows[iz][ix] != '#');
            return g;
        }

        public List<string> ToRows()
        {
            var rows = new List<string>(Depth);
            var chars = new char[Width];
            for (int iz = 0; iz < Depth; iz++)
            {
                for (int ix = 0; ix < Width; ix++) chars[ix] = IsWalkable(ix, iz) ? '.' : '#';
                rows.Add(new string(chars));
            }
            return rows;
        }

        /// <summary>The free cell nearest to (ix, iz) (itself if free); false if the grid has none.</summary>
        public bool NearestWalkable(int ix, int iz, out int fx, out int fz)
        {
            ix = Math.Max(0, Math.Min(Width - 1, ix)); iz = Math.Max(0, Math.Min(Depth - 1, iz));
            int maxR = Math.Max(Width, Depth);
            for (int r = 0; r <= maxR; r++)
            {
                double best = double.MaxValue; fx = fz = -1;
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r || !IsWalkable(ix + dx, iz + dz)) continue;
                        double d = dx * dx + dz * dz;
                        if (d < best) { best = d; fx = ix + dx; fz = iz + dz; }
                    }
                if (fx >= 0) return true;
            }
            fx = fz = -1;
            return false;
        }

        /// <summary>
        /// Shortest path between two free cells: A* on 8 neighbours (straight 1, diagonal √2; a diagonal step needs
        /// both orthogonal neighbours free — no corner cutting), then smoothed by line of sight. Cells from start to
        /// goal; null if unreachable.
        /// </summary>
        public List<(int x, int z)> FindPath(int sx, int sz, int gx, int gz)
        {
            if (!IsWalkable(sx, sz) || !IsWalkable(gx, gz)) return null;
            if (sx == gx && sz == gz) return new List<(int, int)> { (sx, sz) };
            int n = Width * Depth, start = sz * Width + sx, goal = gz * Width + gx;
            var g = new double[n];
            var from = new int[n];
            var closed = new bool[n];
            for (int i = 0; i < n; i++) { g[i] = double.PositiveInfinity; from[i] = -1; }
            g[start] = 0;
            var open = new MinHeap();
            open.Push(start, Heuristic(sx, sz, gx, gz));
            while (open.Count > 0)
            {
                int cur = open.Pop();
                if (closed[cur]) continue;
                if (cur == goal) break;
                closed[cur] = true;
                int cx = cur % Width, cz = cur / Width;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cx + dx, nz = cz + dz;
                        if (!IsWalkable(nx, nz)) continue;
                        if (dx != 0 && dz != 0 && (!IsWalkable(cx + dx, cz) || !IsWalkable(cx, cz + dz))) continue;
                        int ni = nz * Width + nx;
                        if (closed[ni]) continue;
                        double cost = g[cur] + (dx != 0 && dz != 0 ? Math.Sqrt(2) : 1);
                        if (cost < g[ni])
                        {
                            g[ni] = cost; from[ni] = cur;
                            open.Push(ni, cost + Heuristic(nx, nz, gx, gz));
                        }
                    }
            }
            if (from[goal] < 0) return null;
            var path = new List<(int, int)>();
            for (int c = goal; c != -1; c = from[c]) path.Add((c % Width, c / Width));
            path.Reverse();
            return Smooth(path);
        }

        // Octile distance: admissible for 8 neighbours.
        private static double Heuristic(int ax, int az, int bx, int bz)
        {
            int dx = Math.Abs(ax - bx), dz = Math.Abs(az - bz);
            return Math.Max(dx, dz) + (Math.Sqrt(2) - 1) * Math.Min(dx, dz);
        }

        // Keeps a cell only when the straight line from the last kept one to the next would cross a blocked cell.
        private List<(int x, int z)> Smooth(List<(int x, int z)> path)
        {
            if (path.Count <= 2) return path;
            var result = new List<(int, int)> { path[0] };
            int anchor = 0;
            for (int i = 2; i < path.Count; i++)
                if (!LineOfSight(path[anchor].x, path[anchor].z, path[i].x, path[i].z))
                {
                    result.Add(path[i - 1]);
                    anchor = i - 1;
                }
            result.Add(path[path.Count - 1]);
            return result;
        }

        /// <summary>True when the segment between two cell centres crosses only free cells (sampled finely, and
        /// both cells beside every diagonal crossing free: no squeezing between two blocked corners).</summary>
        public bool LineOfSight(int ax, int az, int bx, int bz)
        {
            double x0 = ax + 0.5, z0 = az + 0.5, x1 = bx + 0.5, z1 = bz + 0.5;
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0)) * 4) + 1;
            int px = ax, pz = az;
            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps;
                int cx = (int)Math.Floor(x0 + (x1 - x0) * t), cz = (int)Math.Floor(z0 + (z1 - z0) * t);
                if (!IsWalkable(cx, cz)) return false;
                if (cx != px && cz != pz && (!IsWalkable(cx, pz) || !IsWalkable(px, cz))) return false;
                px = cx; pz = cz;
            }
            return true;
        }

        private sealed class MinHeap
        {
            private readonly List<(int item, double key)> h = new List<(int, double)>();
            public int Count => h.Count;

            public void Push(int item, double key)
            {
                h.Add((item, key));
                int i = h.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (h[p].key <= h[i].key) break;
                    var t = h[p]; h[p] = h[i]; h[i] = t; i = p;
                }
            }

            public int Pop()
            {
                int top = h[0].item;
                h[0] = h[h.Count - 1];
                h.RemoveAt(h.Count - 1);
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < h.Count && h[l].key < h[m].key) m = l;
                    if (r < h.Count && h[r].key < h[m].key) m = r;
                    if (m == i) break;
                    var t = h[m]; h[m] = h[i]; h[i] = t; i = m;
                }
                return top;
            }
        }
    }

    /// <summary>
    /// Travel over a <see cref="WalkGrid"/> (operators, forklifts moving freely around obstacles): the locations'
    /// coordinates (points of a <see cref="TransportNetwork"/>) are placed on the nearest free cell, the path is found
    /// by the grid's A*, its length ÷ speed is the time, and the leg carries the path's points for views. Paths are
    /// cached per pair of locations for the run.
    /// </summary>
    public sealed class GridTravel : ITravelModel
    {
        private readonly Dictionary<string, (double length, WayPoint[] path)> cache = new Dictionary<string, (double, WayPoint[])>();

        public GridTravel(WalkGrid grid, TransportNetwork points, double speed)
        {
            Grid = grid ?? throw new ArgumentNullException(nameof(grid));
            Points = points ?? throw new ArgumentNullException(nameof(points));
            if (!(speed > 0)) throw new ArgumentException("speed must be > 0.", nameof(speed));
            Speed = speed;
        }

        public WalkGrid Grid { get; }
        public TransportNetwork Points { get; }
        public double Speed { get; }

        public double Distance(string from, string to) => from == to ? 0 : Path(from, to).length;

        public TravelPlan Plan(TaskExecutor executor, string from, string to)
        {
            var p = Path(from, to);
            if (double.IsPositiveInfinity(p.length))
                throw new InvalidOperationException($"'{executor?.GetName()}' cannot walk from '{from}' to '{to}': no free path on the walk grid.");
            return new TravelPlan { Route = new[] { from, to }, Distance = p.length, Duration = p.length / Speed, Path = p.path };
        }

        private (double length, WayPoint[] path) Path(string from, string to)
        {
            string key = from + "\u0001" + to;
            if (cache.TryGetValue(key, out var hit)) return hit;
            var result = (double.PositiveInfinity, (WayPoint[])null);
            var a = Points.GetNode(from); var b = Points.GetNode(to);
            if (a != null && b != null
                && Grid.NearestWalkable(Grid.Cell(a.X, Grid.OriginX), Grid.Cell(a.Z, Grid.OriginZ), out int ax, out int az)
                && Grid.NearestWalkable(Grid.Cell(b.X, Grid.OriginX), Grid.Cell(b.Z, Grid.OriginZ), out int bx, out int bz))
            {
                var cells = Grid.FindPath(ax, az, bx, bz);
                if (cells != null)
                {
                    var pts = new List<WayPoint> { new WayPoint(a.X, a.Y, a.Z) };
                    for (int i = 1; i < cells.Count - 1; i++)
                        pts.Add(new WayPoint(Grid.CenterX(cells[i].x), Grid.Y, Grid.CenterZ(cells[i].z)));
                    pts.Add(new WayPoint(b.X, b.Y, b.Z));
                    double len = 0;
                    for (int i = 1; i < pts.Count; i++)
                    {
                        double dx = pts[i].X - pts[i - 1].X, dz = pts[i].Z - pts[i - 1].Z;
                        len += Math.Sqrt(dx * dx + dz * dz);
                    }
                    result = (len, pts.ToArray());
                }
            }
            cache[key] = result;
            return result;
        }
    }
}
