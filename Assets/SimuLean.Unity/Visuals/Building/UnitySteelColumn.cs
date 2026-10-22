using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// HEB300 steel column from the IndustrialBuilding kit: base plate with anchors,
    /// shaft (1 m segments stretched to fit), cap plate, and optionally a crane bracket
    /// (corbel) for an <see cref="UnityOverheadCrane"/> runway and a yellow/black
    /// impact guard.
    ///
    /// <para><b>Frame.</b> Origin on the column axis at floor level, +Y up, the web along Z,
    /// the flanges facing the wall; the bracket points to local +Z and carries the runway
    /// beam axis at z = +0.50 with the rail top at <see cref="railHeight"/>. For a crane
    /// of span S between two column rows, put the column axes S + 1.0 m apart.</para>
    /// </summary>
    public class UnitySteelColumn : ModularAssetBehaviour
    {
        public const float BaseHeight = 0.35f, TopHeight = 0.125f;
        public const float BracketBelowRail = 0.455f;   // rail top to runway beam underside
        public const float BracketReach = 0.50f;        // column axis to runway axis

        [Serializable]
        public class Pieces
        {
            public GameObject Column_Base, Column_Shaft, Column_Top, Column_Bracket, Column_Guard;
        }

        [Header("Column")]
        [Tooltip("Floor to the top of the cap plate (m).")]
        [Min(1f)] public float height = 7.6f;

        [Tooltip("Add the crane bracket (corbel) on local +Z.")]
        public bool bracket = true;

        [Tooltip("Crane rail top height (m); the bracket sits 0.455 m below it.")]
        [Min(1f)] public float railHeight = 6f;

        [Tooltip("Add the yellow/black impact guard at the base.")]
        public bool guard = true;

        [Header("Kit pieces (auto-filled from Art/Models/IndustrialBuilding)")]
        public Pieces pieces = new Pieces();

        public override string GetSummary()
        {
            int n = ShaftSegments(out float k);
            return $"{height:0.##} m  |  {n} shaft segment(s) @ {k:0.###} m" +
                   (bracket ? $"  |  bracket for rail at {railHeight:0.##} m" : "") + (guard ? "  |  guard" : "");
        }

        int ShaftSegments(out float k)
        {
            float run = Mathf.Max(0.1f, height - TopHeight - BaseHeight);
            int n = Mathf.Max(1, Mathf.RoundToInt(run));
            k = run / n;
            return n;
        }

        protected override void ResolvePieces() => ModularKit.ResolvePieces(pieces, ModularKit.BuildingKit);

        [ContextMenu("Rebuild Column")]
        public override void Rebuild()
        {
            var root = BeginBuild();
            var p = pieces;
            var all = new List<GameObject> { ModularKit.Place(p.Column_Base, root, Vector3.zero) };
            int n = ShaftSegments(out float k);
            for (int j = 0; j < n; j++)
                all.Add(ModularKit.Place(p.Column_Shaft, root, new Vector3(0f, BaseHeight + j * k, 0f),
                                         Quaternion.identity, new Vector3(1f, k, 1f)));
            all.Add(ModularKit.Place(p.Column_Top, root, new Vector3(0f, height - TopHeight, 0f)));
            if (bracket)
            {
                float y = railHeight - BracketBelowRail;
                if (y > BaseHeight && y < height - TopHeight)
                    all.Add(ModularKit.Place(p.Column_Bracket, root, new Vector3(0f, y, 0f)));
                else
                    Debug.LogWarning($"[SimuLean] {name}: rail height {railHeight} m puts the bracket outside the column.", this);
            }
            if (guard) all.Add(ModularKit.Place(p.Column_Guard, root, Vector3.zero));
            all.RemoveAll(g => g == null);
            if (combine) ModularKit.Combine(root, all, 1000f, 1);   // one chunk: a column is small
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.2f, 0.5f, 1f, 0.8f);
            Gizmos.DrawWireCube(new Vector3(0f, height * 0.5f, 0f), new Vector3(0.3f, height, 0.3f));
            if (bracket)
                Gizmos.DrawWireSphere(new Vector3(0f, railHeight, BracketReach), 0.08f);   // rail top
        }
    }
}
