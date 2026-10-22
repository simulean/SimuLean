using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Extraction / headless build of <see cref="UnityLengthLimitedQueue"/> →
    /// <see cref="LengthLimitedQueue"/>. The parameter codec and the build live in the
    /// engine-pure <see cref="LengthLimitedQueueBuilder"/>.
    /// </summary>
    internal sealed class UnityLengthLimitedQueueHandler : ElementHandlerBase
    {
        public override string TypeName => "UnityLengthLimitedQueue";

        /// <summary>Parameterisation of this type's keys (numbers / bools are found by type; see ParameterSchema).</summary>
        public override SimuLean.Optimization.ParameterSchema Schema { get; } = new SimuLean.Optimization.ParameterSchema()
            .Numbers(LengthLimitedQueueBuilder.KeyLaneLengths).Structural(LengthLimitedQueueBuilder.KeyLengthLabel)
            .Describe(LengthLimitedQueueBuilder.KeyLaneLengths, "Lane lengths", min: 0).Describe(LengthLimitedQueueBuilder.KeyGap, "Gap", min: 0)
            .Describe(LengthLimitedQueueBuilder.KeyMaxItems, "Max items (0 = no limit)", min: 0);


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => LengthLimitedQueueBuilder.Build(config, clock, vElement);
    }
}
