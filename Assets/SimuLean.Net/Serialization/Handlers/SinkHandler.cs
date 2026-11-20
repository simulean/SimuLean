using SimuLean;

namespace SimuLean.Serialization
{
    internal sealed class SinkHandler : ElementHandlerBase
    {
        public override string TypeName => "Sink";


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            return new Sink(config.Name, clock, vElement) { KeepItems = config.SettingsAs<SinkSettings>().KeepItems };
        }
    }
}
