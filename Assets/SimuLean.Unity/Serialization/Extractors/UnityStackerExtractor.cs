using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityStacker</c>; the headless build side is
    /// <c>UnityStackerHandler</c> in the engine.</summary>
    internal sealed class UnityStackerExtractor : ElementExtractorBase
    {
        public override string TypeName => "UnityStacker";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityStacker s)) return;

            StackerBuilder.Encode(config, s.GetSettings());
        }
    }
}
