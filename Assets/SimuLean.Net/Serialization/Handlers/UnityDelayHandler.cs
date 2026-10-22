using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>Headless build of a <see cref="Delay"/>: every item waits its own delay, no
    /// capacity. Keys: <c>delay*</c> (the random process; an expression reads the item) and the input strategy.</summary>
    internal sealed class UnityDelayHandler : ElementHandlerBase
    {
        public override string TypeName => "UnityDelay";

        public override SimuLean.Optimization.ParameterSchema Schema { get; } = new SimuLean.Optimization.ParameterSchema();

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => new Delay(ElementBuildHelpers.BuildRandomProcess(config, prefix: "delay", clock: clock), config.Name, clock,
                         ElementBuildHelpers.BuildInputStrategy(config), vElement);
    }
}
