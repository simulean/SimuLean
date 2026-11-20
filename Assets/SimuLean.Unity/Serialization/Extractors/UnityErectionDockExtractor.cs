using SimuLean.Unity.Naval;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityErectionDock</c>; the headless
    /// build side is <c>ErectionDockHandler</c> in the engine.</summary>
    internal sealed class UnityErectionDockExtractor : ElementExtractorBase
    {
        public override string TypeName => "ErectionDock";

        public override void ExtractParameters(SimuLean.Unity.SElement element, ElementConfig config)
        {
            if (!(element is UnityErectionDock dock)) return;
            config.Set("capacity", dock.capacity);
            config.Set("erectionRule", dock.erectionRule.ToString());
            config.Set("expectedBlocks", dock.expectedBlocks);
            config.Set("blockLabel", dock.blockLabel ?? "");
            config.Set("sequenceLabel", dock.sequenceLabel ?? "");
            config.Set("predecessorsLabel", dock.predecessorsLabel ?? "");
            config.Set("shipName", dock.shipName ?? "");
            if (dock.delayStrategy != null)
                ExtractorHelpers.ExtractRandomProcessConfig(dock.delayStrategy, config, "delayStrategy");
            dock.setup?.Export(config);   // its setup (rule None: nothing); the crew's team in SceneExtractor
        }
    }
}
