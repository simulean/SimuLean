using System.Collections.Generic;
using SimuLean;

namespace SimuLean.Serialization
{
    internal sealed class CombinerHandler : ElementHandlerBase
    {
        public override string TypeName => "Combiner";

        // its inputs: 0 = the main one, k = component input k-1
        internal override Element InputPort(Element element, int input) => ((Combiner)element).GetInputPort(input);

        // its pull mode admits the components: the config's input strategy does not apply
        internal override bool AppliesInputStrategy => false;

        // its input order: ids of the upstream elements, [0] = main input, resolved lazily when the links are created
        // (Combiner.ResolveInputFor)
        internal override void PrepareWiring(Element element, ElementConfig config, IReadOnlyDictionary<string, Element> created)
        {
            var orderIds = config.SettingsAs<CombinerSettings>()?.InputOrder;
            if (orderIds == null || orderIds.Count == 0) return;
            ((Combiner)element).InputOrderProvider = () => orderIds.ConvertAll(id => created.TryGetValue(id, out Element e) ? e : null);
        }


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<CombinerSettings>();
            int[] requirements      = s.Requirements ?? new[] { 2 };
            bool batchMode          = s.BatchMode;
            int  capacity           = s.Capacity;   // assemblies in parallel
            bool updateRequirements = s.UpdateRequirements;
            var  updateLabels       = s.UpdateLabels;

            IRandomSampler delay   = ElementBuildHelpers.BuildRandomProcess(s.DelayStrategy, config.Name, "delayStrategy", clock);
            InputStrategy pullMode = ElementBuildHelpers.BuildPullModeStrategy(config, s);

            var combiner = new Combiner(
                requirements:       requirements,
                delayStrategy:      delay,
                name:               config.Name,
                simClock:           clock,
                batchMode:          batchMode,
                pullMode:           pullMode,
                updateRequirements: updateRequirements,
                updateLabels:       updateLabels,
                vElement:           vElement,
                capacity:           capacity);
            combiner.HeightLabel   = s.HeightLabel ?? SimuLean.Storage.StorageSystem.DefaultHeightLabel;
            combiner.PartsPerLayer = System.Math.Max(0, s.PartsPerLayer);

            return combiner;
        }
    }
}
