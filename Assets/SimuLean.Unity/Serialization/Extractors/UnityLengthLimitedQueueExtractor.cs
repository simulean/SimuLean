using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityLengthLimitedQueue</c>; the headless build side is
    /// <c>UnityLengthLimitedQueueHandler</c> in the engine.</summary>
    internal sealed class UnityLengthLimitedQueueExtractor : ElementExtractorBase
    {
        public override string TypeName => "UnityLengthLimitedQueue";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityLengthLimitedQueue lane)) return;

            LengthLimitedQueueBuilder.Encode(config, lane.GetLaneLengths(), lane.gap, lane.defaultItemLength,
                                             lane.lengthLabel, lane.maxItems, lane.acceptOversizeOnEmptyLane);
        }
    }
}
