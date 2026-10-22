using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// One straight wall of an industrial workshop, assembled from the IndustrialBuilding
    /// kit on a 2 m × 1 m grid: a concrete plinth row, <see cref="rows"/> sandwich-panel
    /// rows (any of them a ribbon-window or dark-fascia row), a coping on top and,
    /// optionally, the corner trims that close the wall's END.
    ///
    /// <para><b>Frame.</b> Origin at the bottom of the INTERIOR face of the cladding, local +X
    /// along the wall, +Y up, the exterior faces −Z (interior on +Z). Build a closed
    /// building by running the walls counter-clockwise seen from above (each wall's end
    /// meets the next wall's start) — rotate the next wall by −90° about Y at the corner.</para>
    ///
    /// <para><b>Length.</b> Any length: the cell count is round(length / 2 m) and the cells
    /// are stretched by a few percent to fit. Heights are always whole 1 m rows.</para>
    ///
    /// <para><b>Openings.</b> A sectional door takes 2 cells × (plinth + 3 rows), a personnel
    /// door 1 cell × (plinth + 2 rows). Door instances stay live (not combined) and get a
    /// <see cref="UnityIndustrialDoor"/> so the simulation can open them.</para>
    /// </summary>
    public class UnityModularWall : ModularAssetBehaviour
    {
        public const float Cell = 2f, RowHeight = 1f, PlinthHeight = 1f;
        public const float Crest = -0.115f;          // outer face of the cladding ribs
        public const float CopingHeight = 0.18f;

        public enum OpeningType { SectionalDoor, PersonnelDoor }

        [Serializable]
        public class Opening
        {
            public OpeningType type = OpeningType.SectionalDoor;
            [Tooltip("First grid cell (0 = the wall start) the opening occupies.")]
            [Min(0)] public int cell;
            [Tooltip("Mount a SimuLean sign centred over the opening.")]
            public bool sign;
        }

        [Serializable]
        public class Pieces
        {
            public GameObject Wall_Panel, Wall_Panel_Dark, Wall_Window, Wall_Plinth, Wall_Top, Wall_Corner;
            public GameObject Corner_Plinth, Corner_Top, Door_Sectional, Door_Personnel, Sign_SimuLean;
        }

        [Header("Wall size")]
        [Tooltip("Length along local +X (m). Rounded to whole 2 m cells, which stretch slightly to fit.")]
        [Min(2f)] public float length = 24f;

        [Tooltip("Panel rows (1 m each) above the 1 m plinth. Wall height = 1 + rows + 0.18 coping.")]
        [Range(1, 24)] public int rows = 7;

        [Tooltip("Row indices (0 = first row above the plinth) built as ribbon windows.")]
        public List<int> windowRows = new List<int> { 5 };

        [Tooltip("Row indices built with the dark (fascia) cladding.")]
        public List<int> darkRows = new List<int> { 6 };

        [Tooltip("Close the wall END with the corner trim, plinth and coping corners.")]
        public bool cornerAtEnd = true;

        [Header("Openings")]
        public List<Opening> openings = new List<Opening>();

        [Header("Kit pieces (auto-filled from Art/Models/IndustrialBuilding)")]
        public Pieces pieces = new Pieces();

        public int Cells => Mathf.Max(1, Mathf.RoundToInt(length / Cell));
        public float Height => PlinthHeight + rows * RowHeight + CopingHeight;

        public override string GetSummary()
        {
            int n = Cells;
            return $"{length:0.##} m × {Height:0.##} m  |  {n} cells @ {length / n:0.###} m  |  " +
                   $"{openings.Count} opening(s)" + (combine ? $"  |  combined in {chunkLength:0.#} m chunks" : "");
        }

        protected override void ResolvePieces() => ModularKit.ResolvePieces(pieces, ModularKit.BuildingKit);

        [ContextMenu("Rebuild Wall")]
        public override void Rebuild()
        {
            var root = BeginBuild();
            var p = pieces;
            int n = Cells;
            float kx = length / (n * Cell);
            var statics = new List<GameObject>();
            var covered = new HashSet<(int, int)>();

            foreach (var o in openings)
            {
                bool sectional = o.type == OpeningType.SectionalDoor;
                int span = sectional ? 2 : 1, nr = sectional ? 3 : 2;
                if (o.cell + span > n)
                {
                    Debug.LogWarning($"[SimuLean] {name}: opening at cell {o.cell} does not fit in {n} cells.", this);
                    continue;
                }
                bool clash = false;
                for (int j = o.cell; j < o.cell + span; j++)
                    for (int r = -1; r < nr; r++)
                        clash |= !covered.Add((j, r));
                if (clash)
                    Debug.LogWarning($"[SimuLean] {name}: opening at cell {o.cell} overlaps another one.", this);

                var door = ModularKit.Place(sectional ? p.Door_Sectional : p.Door_Personnel, root,
                                            new Vector3(o.cell * Cell * kx, 0f, 0f), Quaternion.identity,
                                            new Vector3(kx, 1f, 1f));
                if (door != null)
                {
                    var d = door.AddComponent<UnityIndustrialDoor>();
                    d.kind = sectional ? UnityIndustrialDoor.DoorKind.Sectional : UnityIndustrialDoor.DoorKind.Personnel;
                    d.travelTime = sectional ? 4f : 1.5f;
                }
                if (o.sign)
                    statics.Add(ModularKit.Place(p.Sign_SimuLean, root,
                        new Vector3((o.cell + span * 0.5f) * Cell * kx, PlinthHeight + nr * RowHeight + 0.05f, Crest)));
            }

            float top = PlinthHeight + rows * RowHeight;
            var scale = new Vector3(kx, 1f, 1f);
            for (int j = 0; j < n; j++)
            {
                float x = j * Cell * kx;
                if (!covered.Contains((j, -1)))
                    statics.Add(ModularKit.Place(p.Wall_Plinth, root, new Vector3(x, 0f, 0f), Quaternion.identity, scale));
                for (int r = 0; r < rows; r++)
                {
                    if (covered.Contains((j, r))) continue;
                    var prefab = windowRows.Contains(r) ? p.Wall_Window : darkRows.Contains(r) ? p.Wall_Panel_Dark : p.Wall_Panel;
                    statics.Add(ModularKit.Place(prefab, root, new Vector3(x, PlinthHeight + r * RowHeight, 0f),
                                                 Quaternion.identity, scale));
                }
                statics.Add(ModularKit.Place(p.Wall_Top, root, new Vector3(x, top, 0f), Quaternion.identity, scale));
            }
            if (cornerAtEnd)
            {
                statics.Add(ModularKit.Place(p.Corner_Plinth, root, new Vector3(length, 0f, 0f)));
                for (int r = 0; r < rows; r++)
                    statics.Add(ModularKit.Place(p.Wall_Corner, root, new Vector3(length, PlinthHeight + r * RowHeight, 0f)));
                statics.Add(ModularKit.Place(p.Corner_Top, root, new Vector3(length, top, 0f)));
            }
            statics.RemoveAll(g => g == null);
            if (combine) ModularKit.Combine(root, statics, chunkLength, 0);
        }

        /// <summary>The live doors of this wall, in opening order.</summary>
        public UnityIndustrialDoor[] Doors => GetComponentsInChildren<UnityIndustrialDoor>();

        void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.6f, 0f, 0.8f);
            Gizmos.DrawWireCube(new Vector3(length * 0.5f, Height * 0.5f, -0.06f), new Vector3(length, Height, 0.12f));
            Gizmos.DrawLine(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f));   // interior side
        }
    }
}
