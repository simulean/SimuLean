using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>Headless build of <c>UnityPacedLine</c> → <see cref="PacedLine"/> (codec and build in <see cref="FlowBuilders"/>).
    /// Ports: <c>&lt;station&gt;</c> (component input, connection <c>PortInput</c>) and <c>&lt;station&gt;.callOff</c>
    /// (connection <c>PortOutput</c>).</summary>
    internal sealed class PacedLineHandler : ElementHandlerBase
    {
        public override string TypeName => "PacedLine";

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => FlowBuilders.BuildPacedLine(config, clock, vElement);
    }
}
