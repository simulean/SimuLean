using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnitySink</c>; the headless build side is
    /// <c>UnitySinkHandler</c> in the engine.</summary>
    internal sealed class UnitySinkExtractor : ElementExtractorBase
    {
        public override string TypeName => "UnitySink";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnitySink sink)) return;
            if (sink.keepItems) config.Parameters["keepItems"] = true;   // default off: configs unchanged

        }
    }
}
