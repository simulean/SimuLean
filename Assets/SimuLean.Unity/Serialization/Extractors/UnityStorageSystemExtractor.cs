using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityStorageSystem</c>; the headless build side is
    /// <c>UnityStorageSystemHandler</c> in the engine.</summary>
    internal sealed class UnityStorageSystemExtractor : ElementExtractorBase
    {
        public override string TypeName => "UnityStorageSystem";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityStorageSystem uss)) return;

            var laneRecords = new List<string>();
            foreach (UnityStorageSystem.ResolvedLane lane in uss.ResolveLanes())
            {
                laneRecords.Add(TransportStorageConfigCodec.FormatLane(
                    lane.LaneId, lane.Position.x, lane.Position.y, lane.Position.z,
                    lane.Capacity, lane.Access, lane.Sku, lane.NodeId));
            }
            config.Parameters[TransportStorageConfigCodec.KeyLanes] = laneRecords;

            config.Parameters["locationPolicyType"] = uss.locationPolicy.ToString();
            if (uss.locationPolicy == StoragePolicyKind.DedicatedBySku)
                config.Parameters["locationPolicy_skuMap"] = uss.GetDedicatedSkuPairs();

            StoragePolicyKind relocationKind;
            if (UnityStorageSystem.TryMapRelocation(uss.relocationPolicy, out relocationKind))
            {
                config.Parameters["relocationPolicyType"] = relocationKind.ToString();
                if (relocationKind == StoragePolicyKind.DedicatedBySku)
                    config.Parameters["relocationPolicy_skuMap"] = uss.GetDedicatedSkuPairs();
            }

            Transform reference = uss.referencePoint != null ? uss.referencePoint : uss.transform;
            config.Parameters["refX"] = (double)reference.position.x;
            config.Parameters["refY"] = (double)reference.position.y;
            config.Parameters["refZ"] = (double)reference.position.z;
        }

    }
}
