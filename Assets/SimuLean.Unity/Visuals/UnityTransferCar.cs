using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Unity
{
    /// <summary>
    /// A procedural <b>transfer car</b> (rail shuttle with parallel roller decks; geometry <see cref="TransferCarMesh"/>),
    /// sized by the loads it carries: <see cref="capacity"/> → lanes = min(C, <see cref="maxLanes"/>) decks side by side
    /// along the track, each ceil(C / lanes) positions deep. Purely visual (no engine, serialization or headless part):
    /// use it as the member prefab of a resource team whose milk run shows its loads <i>On Board</i> — the sockets
    /// <c>Slot_&lt;lane&gt;_&lt;depth&gt;</c> (their +Z along the lane, i.e. across the track) are where
    /// <see cref="MilkRunTrainView"/> puts them, depth first, then lane. Local axes: +Z along the track, +X across it (the
    /// lanes' run direction: loads transfer sideways onto / off the car), origin on the floor at the car's centre; a
    /// <c>Beacon</c> node marks the beacon. Never scale it: set the parameters and <see cref="Rebuild"/> (also at run time,
    /// e.g. <see cref="SetCapacity"/> when a scenario changes C). Everything generated lives under the child
    /// <c>Generated</c>; one LODGroup (LOD1: lower-detail chassis, the decks' striped beds).
    /// </summary>
    [SelectionBase]
    public class UnityTransferCar : MonoBehaviour
    {
        [Tooltip("Loads carried at once: lanes = min(C, Max Lanes), positions per lane = ceil(C / lanes).")]
        [Min(1)] public int capacity = 3;
        [Range(1, 6)] public int maxLanes = 3;
        [Tooltip("Lane, pitch, deck height, rollers, wheels (lanes and depth are set from the capacity).")]
        public TransferCarSpec spec = new TransferCarSpec();

        [Header("Materials (empty = generated defaults)")]
        public Material paintMaterial, steelMaterial, safetyMaterial, darkMaterial, lightMaterial;
        public Material deckFrameMaterial, rollerMaterial;

        public const string GeneratedName = "Generated";

        /// <summary>The spec of the current capacity (lanes and depth filled in).</summary>
        public TransferCarSpec Spec
        {
            get
            {
                var s = spec.Clone();
                var c = TransferCarSpec.ForCapacity(capacity, maxLanes);
                s.lanes = c.lanes;
                s.depth = c.depth;
                return s;
            }
        }

        /// <summary>The load sockets in loading order (depth first, then lane), after a <see cref="Rebuild"/>.</summary>
        public IReadOnlyList<Transform> Slots => slots;
        private readonly List<Transform> slots = new List<Transform>();

        /// <summary>Changes the capacity and rebuilds when it differs (or nothing was built yet).</summary>
        public void SetCapacity(int c)
        {
            c = Mathf.Max(1, c);
            if (c == capacity && transform.Find(GeneratedName) != null) return;
            capacity = c;
            Rebuild();
        }

        [ContextMenu("Rebuild Transfer Car")]
        public void Rebuild()
        {
            var old = transform.Find(GeneratedName);
            if (old != null) { if (Application.isPlaying) { old.name = "(old)"; old.gameObject.SetActive(false); Destroy(old.gameObject); } else DestroyImmediate(old.gameObject); }
            slots.Clear();
            var s = Spec;
            var root = new GameObject(GeneratedName).transform;
            root.SetParent(transform, false);
            var defaults = TransferCarMesh.DefaultMaterials();
            var mats = new[]
            {
                paintMaterial ? paintMaterial : defaults[0], steelMaterial ? steelMaterial : defaults[1], safetyMaterial ? safetyMaterial : defaults[2],
                darkMaterial ? darkMaterial : defaults[3], lightMaterial ? lightMaterial : defaults[4],
            };
            var lod0 = new List<Renderer>();
            var lod1 = new List<Renderer>();
            for (int lod = 0; lod < 2; lod++)
            {
                var go = new GameObject("Chassis_LOD" + lod, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(root, false);
                go.GetComponent<MeshFilter>().sharedMesh = TransferCarMesh.BuildChassis(s, lod);
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterials = mats;
                (lod == 0 ? lod0 : lod1).Add(r);
            }
            var deck = TransferCarMesh.LaneDeck(s);
            Mesh deck0 = RollerConveyorMesh.Build(deck, 0, null, "TransferCar_Deck_LOD0"), deck1 = RollerConveyorMesh.Build(deck, 1, null, "TransferCar_Deck_LOD1");
            var frameMat = deckFrameMaterial ? deckFrameMaterial : mats[1];
            var rollerMat = rollerMaterial ? rollerMaterial : mats[1];
            for (int l = 0; l < s.lanes; l++)
            {
                float z = (l - (s.lanes - 1) * 0.5f) * s.LaneSpacing;
                for (int lod = 0; lod < 2; lod++)
                {
                    var go = new GameObject($"Lane_{l}_LOD{lod}", typeof(MeshFilter), typeof(MeshRenderer));
                    go.transform.SetParent(root, false);
                    go.transform.localPosition = new Vector3(0f, 0f, z);
                    go.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);   // deck run (+Z) → across the track
                    go.GetComponent<MeshFilter>().sharedMesh = lod == 0 ? deck0 : deck1;
                    var r = go.GetComponent<MeshRenderer>();
                    r.sharedMaterials = new[] { frameMat, rollerMat };
                    (lod == 0 ? lod0 : lod1).Add(r);
                }
            }
            for (int d = 0; d < s.depth; d++)
                for (int l = 0; l < s.lanes; l++)
                {
                    var slot = new GameObject($"Slot_{l}_{d}").transform;
                    slot.SetParent(root, false);
                    slot.localPosition = s.SlotPosition(l, d);
                    slot.localRotation = Quaternion.Euler(0f, 90f, 0f);   // +Z along the lane
                    slots.Add(slot);
                }
            var beacon = new GameObject("Beacon").transform;
            beacon.SetParent(root, false);
            beacon.localPosition = TransferCarMesh.BeaconPosition(s);
            var group = root.gameObject.AddComponent<LODGroup>();
            group.fadeMode = LODFadeMode.None;
            group.SetLODs(new[] { new LOD(0.12f, lod0.ToArray()), new LOD(0f, lod1.ToArray()) });
            group.RecalculateBounds();
        }

        private void Reset() => Rebuild();
    }
}
