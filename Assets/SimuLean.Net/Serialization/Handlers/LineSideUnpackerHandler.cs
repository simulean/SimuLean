using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>Headless build of <c>UnityLineSideUnpacker</c> → <see cref="LineSideUnpacker"/> (Manufacturing pack; codec
    /// and build in <see cref="FlowBuilders"/>). Port <c>empties</c> (connection <c>PortOutput</c>) is where the empty
    /// carriers leave.</summary>
    internal sealed class LineSideUnpackerHandler : ElementHandlerBase
    {
        public override string TypeName => "LineSideUnpacker";

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => FlowBuilders.BuildLineSideUnpacker(config, clock, vElement);
    }
}
