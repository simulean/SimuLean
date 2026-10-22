using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A straight run of tiling kit pieces along local +Z: [cap][segments][cap] — e.g. a
    /// crane runway (Runway_Segment + Runway_EndStop) or a roof/tie beam (Beam_Segment +
    /// Beam_End). Segments are stretched along Z by a few percent so any
    /// <see cref="length"/> fits; caps keep their size. Created from
    /// GameObject → SimuLean → Building with the right pieces already assigned.
    ///
    /// <para><b>Frame.</b> Origin at the start of the run, on the piece's own origin line
    /// (rail top for a runway, top flange for a beam); the run extends to +Z.</para>
    /// </summary>
    public class UnityModularStrip : ModularAssetBehaviour
    {
        [Header("Strip")]
        [Tooltip("Total length along local +Z including both caps (m).")]
        [Min(0.5f)] public float length = 12f;

        [Tooltip("Native length of one segment (m); the kits use 2 m.")]
        [Min(0.1f)] public float segmentLength = 2f;

        [Header("Pieces")]
        [Tooltip("Tiling piece (spans 0..segmentLength along +Z).")]
        public GameObject segment;
        [Tooltip("Optional cap at both ends (the far one turned 180°).")]
        public GameObject cap;
        [Tooltip("Optional different cap for the far end (not turned).")]
        public GameObject endCap;
        [Tooltip("Optional piece replacing the middle segment (e.g. a logo segment).")]
        public GameObject center;

        public override string GetSummary()
        {
            if (segment == null) return "Assign a segment piece.";
            return $"{length:0.##} m of {segment.name}" + (cap ? $" with {cap.name} caps" : "") +
                   (combine ? $"  |  combined in {chunkLength:0.#} m chunks" : "");
        }

        // A strip has no fixed kit: its pieces are chosen by the creation menu.
        protected override void ResolvePieces() { }

        protected override void Reset()
        {
            chunkLength = 24f;
            base.Reset();
        }

        [ContextMenu("Rebuild Strip")]
        public override void Rebuild()
        {
            var root = BeginBuild();
            if (segment == null) return;
            var all = ModularKit.Strip(root, Vector3.zero, length, segment, cap, endCap, center, segmentLength);
            if (combine) ModularKit.Combine(root, all, chunkLength, 2);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.6f, 0f, 0.8f);
            Gizmos.DrawLine(Vector3.zero, Vector3.forward * length);
        }
    }
}
