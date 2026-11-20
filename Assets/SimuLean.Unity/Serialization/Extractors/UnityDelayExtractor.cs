using SimuLean.Unity;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityDelay</c>; the headless build side is
    /// <c>DelayHandler</c> in the engine.</summary>
    internal sealed class UnityDelayExtractor : ElementExtractorBase
    {
        public override string TypeName => "Delay";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityDelay delay)) return;
            if (delay.delayStrategy != null)
                ExtractorHelpers.ExtractRandomProcessConfig(delay.delayStrategy, config, "delayStrategy");
        }
    }
}
