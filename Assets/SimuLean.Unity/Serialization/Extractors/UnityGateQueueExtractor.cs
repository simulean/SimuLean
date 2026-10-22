using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityGateQueue</c>; the headless build side is
    /// <c>UnityGateQueueHandler</c> in the engine.</summary>
    internal sealed class UnityGateQueueExtractor : ElementExtractorBase
    {
        public override string TypeName => "UnityGateQueue";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityGateQueue gateQueue)) return;

            config.SetCapacity(gateQueue.capacity);
        }
    }
}
