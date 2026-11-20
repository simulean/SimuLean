using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Extraction / headless build of <see cref="Stacker"/> → <see cref="Stacker"/>. The parameter
    /// codec and the build live in the engine-pure <see cref="StackerBuilder"/>.
    /// </summary>
    internal sealed class StackerHandler : ElementHandlerBase
    {
        public override string TypeName => "Stacker";


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => StackerBuilder.Build(config, clock, vElement);
    }
}
