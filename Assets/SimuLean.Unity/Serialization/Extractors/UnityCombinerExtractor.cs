using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityCombiner</c>; the headless build side is
    /// <c>UnityCombinerHandler</c> in the engine.</summary>
    internal sealed class UnityCombinerExtractor : ElementExtractorBase
    {
        public override string TypeName => "UnityCombiner";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityCombiner combiner)) return;

            config.Parameters["requirements"]      = (int[])combiner.requirements.Clone();
            config.Parameters["batchMode"]         = combiner.batchMode;
            config.Parameters["capacity"]          = Mathf.Max(1, combiner.capacity);
            config.Parameters["updateRequirements"] = combiner.updateRequirements;

            if (combiner.updateLabels != null)
                config.Parameters["updateLabels"] = new List<string>(combiner.updateLabels);

            // Pull-mode strategy
            config.Parameters["pullModeType"] = combiner.pullModeType.ToString();
            switch (combiner.pullModeType)
            {
                case UnityCombiner.PullModeType.SingleLabel:
                    config.Parameters["singleLabelName"]  = combiner.singleLabelName;
                    config.Parameters["singleLabelValue"] = combiner.singleLabelValue;
                    break;
                case UnityCombiner.PullModeType.MultiLabel:
                    if (combiner.multiLabelPairs != null)
                        config.Parameters["multiLabelPairs"] = new List<string>(combiner.multiLabelPairs);
                    break;
            }

            if (combiner.delayStrategy != null)
                ExtractorHelpers.ExtractRandomProcessConfig(combiner.delayStrategy, config, prefix: "delay");


            // Inputs are ordinary nextElements connections; their order is exported by the extractor
            // as the "inputOrder" parameter (see UnityModelExtractor, Combiner.ResolveInputFor).
        }
    }
}
