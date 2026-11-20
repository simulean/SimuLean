using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityStorageSystem</c>; the headless build side is
    /// <c>StorageSystemHandler</c> in the engine.</summary>
    internal sealed class UnityStorageSystemExtractor : ElementExtractorBase
    {
        public override string TypeName => "StorageSystem";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityStorageSystem uss)) return;

            var s = config.SettingsAs<StorageSystemSettings>();
            // lanes left from an older layout (a field changed in the inspector without Rebuild)
            if (uss is UnityStorageRack rack && rack.LayoutMismatch() is string stale)
                Debug.LogWarning($"[SimuLean] Storage rack '{rack.name}': {stale} — press Rebuild Storage Locations " +
                                 $"(simulean_invoke object={rack.name} action=Rebuild), or set a layout field again with simulean_set.");
            s.Lanes = new List<StorageLaneSettings>();
            foreach (UnityStorageSystem.ResolvedLane lane in uss.ResolveLanes())
                s.Lanes.Add(StorageLaneSettings.Of(lane.LaneId, lane.Position.x, lane.Position.y, lane.Position.z,
                                                   lane.Capacity, lane.Access, lane.Sku, lane.NodeId,
                                                   lane.SlotSize.x, lane.SlotSize.y, lane.SlotSize.z));
            s.PhysicalDimensions = uss.physicalDimensions;
            s.LengthLabel = uss.lengthLabel ?? "";
            s.WidthLabel = uss.widthLabel ?? "";
            s.HeightLabel = uss.heightLabel ?? "";
            s.PutAwayByTransport = uss.putAwayByTransport;
            s.InboundCapacity = Mathf.Max(1, uss.inboundCapacity);
            s.ReleaseAfterDwell = uss.releaseAfterDwell;
            s.MinimumDwellTime = Mathf.Max(0f, uss.minimumDwellTime);

            s.LocationPolicy = uss.locationPolicy.ToString();
            if (uss.locationPolicy == StoragePolicyKind.DedicatedBySku)
                s.DedicatedSkuMappings = uss.GetDedicatedSkuPairs();

            StoragePolicyKind relocationKind;
            if (UnityStorageSystem.TryMapRelocation(uss.relocationPolicy, out relocationKind))
            {
                s.RelocationPolicy = relocationKind.ToString();
                if (relocationKind == StoragePolicyKind.DedicatedBySku)
                    s.DedicatedSkuMappings = uss.GetDedicatedSkuPairs();
            }

            Transform reference = uss.referencePoint != null ? uss.referencePoint : uss.transform;
            s.RefX = reference.position.x;
            s.RefY = reference.position.y;
            s.RefZ = reference.position.z;
        }

    }
}
