using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityQueue</c>; the headless build side is
    /// <c>UnityQueueHandler</c> in the engine.</summary>
    internal sealed class UnityQueueExtractor : ElementExtractorBase
    {
        public override string TypeName => "UnityQueue";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityQueue queue)) return;

            config.SetCapacity(queue.capacity);
            if (queue.releaseMode != SimuLean.ReleaseMode.FIFO)   // FIFO = the default: configs unchanged
            {
                config.Parameters["releaseMode"] = queue.releaseMode.ToString();
                config.Parameters["releaseScan"] = queue.releaseScan;
            }
        }
    }
}
