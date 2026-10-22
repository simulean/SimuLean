using SimuLean.Unity.Naval;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityErectionDock</c>; the headless
    /// build side is <c>UnityErectionDockHandler</c> in the engine.</summary>
    internal sealed class UnityErectionDockExtractor : ElementExtractorBase
    {
        public override string TypeName => "UnityErectionDock";

        public override void ExtractParameters(SimuLean.Unity.SElement element, ElementConfig config)
        {
            if (!(element is UnityErectionDock dock)) return;
            config.SetCapacity(dock.capacity);
            config.Parameters["erectionRule"] = dock.erectionRule.ToString();
            config.Parameters["expectedBlocks"] = dock.expectedBlocks;
            config.Parameters["blockLabel"] = dock.blockLabel ?? "";
            config.Parameters["sequenceLabel"] = dock.sequenceLabel ?? "";
            config.Parameters["predecessorsLabel"] = dock.predecessorsLabel ?? "";
            config.Parameters["shipName"] = dock.shipName ?? "";
            if (dock.delayStrategy != null)
                ExtractorHelpers.ExtractRandomProcessConfig(dock.delayStrategy, config, prefix: "delay");
        }
    }
}
