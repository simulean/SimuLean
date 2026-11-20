using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityStacker</c>; the headless build side is
    /// <c>StackerHandler</c> in the engine.</summary>
    internal sealed class UnityStackerExtractor : ElementExtractorBase
    {
        public override string TypeName => "Stacker";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityStacker s)) return;

            s.Fill(config.SettingsAs<StackerSettings>());
            s.setup?.Export(config);   // its setup (rule None: nothing); the crew's team in SceneExtractor
        }
    }
}
