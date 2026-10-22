using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Extraction / headless build of <see cref="UnityStacker"/> → <see cref="Stacker"/>. The parameter
    /// codec and the build live in the engine-pure <see cref="StackerBuilder"/>.
    /// </summary>
    internal sealed class UnityStackerHandler : ElementHandlerBase
    {
        public override string TypeName => "UnityStacker";

        /// <summary>Parameterisation of this type's keys (numbers / bools are found by type; see ParameterSchema).</summary>
        public override SimuLean.Optimization.ParameterSchema Schema { get; } = new SimuLean.Optimization.ParameterSchema()
            .Numbers(StackerBuilder.KeyPositionCapacities, integer: true)
            .Choice<StackSelectionRule>(StackerBuilder.KeySelectionRule)
            .Sampler(StackerBuilder.KeyCycleTime)
            .Structural(StackerBuilder.KeyHeightLabel, StackerBuilder.KeyPositionLabel, StackerBuilder.KeyKeyLabel, StackerBuilder.KeyStackType,
                        StackerBuilder.KeyPickLocation, StackerBuilder.KeyPositionLocations)
            .Describe(StackerBuilder.KeyPositions, "Positions", min: 1).Describe(StackerBuilder.KeyCapacity, "Items per stack", min: 1)
            .Describe(StackerBuilder.KeyPositionCapacities, "Items per stack, per position", min: 1)
            .Describe(StackerBuilder.KeyMaxHeight, "Max stack height (0 = none)", min: 0);


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => StackerBuilder.Build(config, clock, vElement);
    }
}
