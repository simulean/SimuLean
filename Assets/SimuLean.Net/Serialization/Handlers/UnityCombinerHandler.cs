using System.Collections.Generic;
using SimuLean;

namespace SimuLean.Serialization
{
    internal sealed class UnityCombinerHandler : ElementHandlerBase
    {
        public override string TypeName => "UnityCombiner";

        /// <summary>Parameterisation of this type's keys (numbers / bools are found by type; see ParameterSchema).</summary>
        public override SimuLean.Optimization.ParameterSchema Schema { get; } = new SimuLean.Optimization.ParameterSchema()
            .Choice("pullModeType", "Default", "SingleLabel", "MultiLabel", "Custom")
            .Structural("singleLabelName", "singleLabelValue", "multiLabelPairs", "updateLabels")
            .Describe("requirements", "Components required", min: 1).Describe("batchMode", "Components travel with the item")
            .Describe("updateRequirements", "Requirements from the main item's labels")
            .Describe("capacity", "Assemblies in parallel", min: 1);


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            int[] requirements      = config.GetParameter<int[]>("requirements",      new int[] { 1 });
            bool batchMode          = config.GetParameter<bool>("batchMode",          false);
            int  capacity           = config.GetParameter<int>("capacity",            1);   // assemblies in parallel (S1)
            bool updateRequirements = config.GetParameter<bool>("updateRequirements", false);
            var  updateLabels       = config.GetParameter<List<string>>("updateLabels");

            IRandomSampler delay   = ElementBuildHelpers.BuildRandomProcess(config, prefix: "delay", clock: clock);
            InputStrategy pullMode = ElementBuildHelpers.BuildPullModeStrategy(config);

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

            return combiner;
        }
    }
}
