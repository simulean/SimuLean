using SimuLean;

namespace SimuLean.Serialization
{
    internal sealed class UnitySinkHandler : ElementHandlerBase
    {
        public override string TypeName => "UnitySink";


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            return new Sink(config.Name, clock, vElement) { KeepItems = config.GetParameter<bool>("keepItems", false) };
        }
    }
}
