using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityMultiServer</c>; the headless build side is
    /// <c>MultiServerHandler</c> in the engine.</summary>
    internal sealed class UnityMultiServerExtractor : ElementExtractorBase
    {
        public override string TypeName => "MultiServer";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityMultiServer multiServer)) return;

            config.Set("capacity", multiServer.capacity);
            // every option also at its default: a study can vary it from there
            config.Set("offerOrder", multiServer.offerOrder.ToString());
            config.Set("offerScan", multiServer.offerScan);

            if (multiServer.delayStrategy != null)
                ExtractorHelpers.ExtractRandomProcessConfig(multiServer.delayStrategy, config, "delayStrategy");
            multiServer.setup?.Export(config);   // its setup (also the rule None); the crew's team in SceneExtractor
            config.Set("reserveExit", multiServer.reserveExit);

        }
    }
}
