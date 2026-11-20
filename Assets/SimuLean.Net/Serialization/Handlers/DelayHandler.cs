using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>Headless build of a <see cref="Delay"/>: every item waits its own delay, no
    /// capacity. Keys: <c>delay*</c> (the random process; an expression reads the item) and the input strategy.</summary>
    internal sealed class DelayHandler : ElementHandlerBase
    {
        public override string TypeName => "Delay";

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => new Delay(ElementBuildHelpers.BuildRandomProcess(config.SettingsAs<DelaySettings>().DelayStrategy, config.Name, "delayStrategy", clock),
                         config.Name, clock,
                         ElementBuildHelpers.BuildInputStrategy(config), vElement);
    }
}
