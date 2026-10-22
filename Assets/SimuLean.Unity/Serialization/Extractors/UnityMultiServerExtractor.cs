using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityMultiServer</c>; the headless build side is
    /// <c>UnityMultiServerHandler</c> in the engine.</summary>
    internal sealed class UnityMultiServerExtractor : ElementExtractorBase
    {
        public override string TypeName => "UnityMultiServer";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityMultiServer multiServer)) return;

            config.SetCapacity(multiServer.capacity);
            if (multiServer.releaseMode != SimuLean.ReleaseMode.FIFO)   // FIFO = the default: configs unchanged
            {
                config.Parameters["releaseMode"] = multiServer.releaseMode.ToString();
                config.Parameters["releaseScan"] = multiServer.releaseScan;
            }

            if (multiServer.delayStrategy != null)
                ExtractorHelpers.ExtractRandomProcessConfig(multiServer.delayStrategy, config, prefix: "delay");

        }
    }
}
