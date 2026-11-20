using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityCombiner</c>; the headless build side is
    /// <c>CombinerHandler</c> in the engine.</summary>
    internal sealed class UnityCombinerExtractor : ElementExtractorBase
    {
        public override string TypeName => "Combiner";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityCombiner combiner)) return;

            config.Set("requirements", (int[])combiner.requirements.Clone());
            config.Set("batchMode", combiner.batchMode);
            config.Set("capacity", Mathf.Max(1, combiner.capacity));
            config.Set("updateRequirements", combiner.updateRequirements);
            config.Set("heightLabel", combiner.heightLabel ?? "");
            config.Set("partsPerLayer", Mathf.Max(0, combiner.partsPerLayer));

            if (combiner.updateLabels != null)
                config.Set("updateLabels", new List<string>(combiner.updateLabels));

            // Pull-mode strategy
            config.Set("pullModeType", combiner.pullModeType.ToString());
            switch (combiner.pullModeType)
            {
                case UnityCombiner.PullModeType.SingleLabel:
                    config.Set("singleLabelName", combiner.singleLabelName);
                    config.Set("singleLabelValue", combiner.singleLabelValue);
                    break;
                case UnityCombiner.PullModeType.MultiLabel:
                    if (combiner.multiLabelPairs != null)
                        config.Set("multiLabelPairs", ExtractorHelpers.LabelValues(combiner.multiLabelPairs));
                    break;
            }

            if (combiner.delayStrategy != null)
                ExtractorHelpers.ExtractRandomProcessConfig(combiner.delayStrategy, config, "delayStrategy");
            combiner.setup?.Export(config);   // its setup (also the rule None); the crew's team in SceneExtractor
            config.Set("reserveExit", combiner.reserveExit);   // (also at its default)


            // Inputs are ordinary nextElements connections; their order is exported by the extractor
            // as the "inputOrder" parameter (see UnityModelExtractor, Combiner.ResolveInputFor).
        }
    }
}
