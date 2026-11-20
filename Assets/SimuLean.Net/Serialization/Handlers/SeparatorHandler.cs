using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>Headless build of <c>UnitySeparator</c> → <see cref="Separator"/> (codec and build in <see cref="FlowBuilders"/>).
    /// Port <c>empties</c> (connection <c>PortOutput</c>) is where the empty containers leave (Unpack).</summary>
    internal sealed class SeparatorHandler : ElementHandlerBase
    {
        public override string TypeName => "Separator";

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => FlowBuilders.BuildSeparator(config, clock, vElement);
    }
}
