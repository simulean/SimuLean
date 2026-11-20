using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Extraction / headless build of <see cref="LengthLimitedQueue"/> →
    /// <see cref="LengthLimitedQueue"/>. The parameter codec and the build live in the
    /// engine-pure <see cref="LengthLimitedQueueBuilder"/>.
    /// </summary>
    internal sealed class LengthLimitedQueueHandler : ElementHandlerBase
    {
        public override string TypeName => "LengthLimitedQueue";


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => LengthLimitedQueueBuilder.Build(config, clock, vElement);
    }
}
