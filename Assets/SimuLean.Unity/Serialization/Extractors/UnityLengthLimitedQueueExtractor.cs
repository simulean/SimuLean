using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityLengthLimitedQueue</c>; the headless build side is
    /// <c>LengthLimitedQueueHandler</c> in the engine.</summary>
    internal sealed class UnityLengthLimitedQueueExtractor : ElementExtractorBase
    {
        public override string TypeName => "LengthLimitedQueue";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityLengthLimitedQueue lane)) return;

            var s = config.SettingsAs<LengthLimitedQueueSettings>();
            s.LaneLengths = lane.GetLaneLengths();
            s.Gap = lane.gap;
            s.DefaultItemLength = lane.defaultItemLength;
            s.LengthLabel = lane.lengthLabel ?? "";
            s.MaxItems = lane.maxItems;
            s.AcceptOversizeOnEmptyLane = lane.acceptOversizeOnEmptyLane;
            s.Speed = lane.speed;
        }
    }
}
