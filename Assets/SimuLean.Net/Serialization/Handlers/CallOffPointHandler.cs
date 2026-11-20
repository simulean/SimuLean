using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>Headless build of <c>UnityCallOffPoint</c> → <see cref="CallOffPoint"/> (codec and build in
    /// <see cref="FlowBuilders"/>). One port per family, named after it (connection <c>PortOutput</c>).</summary>
    internal sealed class CallOffPointHandler : ElementHandlerBase
    {
        public override string TypeName => "CallOffPoint";

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => FlowBuilders.BuildCallOffPoint(config, clock, vElement);
    }
}
