using SimuLean;

namespace SimuLean.Serialization
{
    internal sealed class UnityGateQueueHandler : ElementHandlerBase
    {
        public override string TypeName => "UnityGateQueue";

        /// <summary>Parameterisation of this type's keys (numbers / bools are found by type; see ParameterSchema).</summary>
        public override SimuLean.Optimization.ParameterSchema Schema { get; } = new SimuLean.Optimization.ParameterSchema()
            .Describe("capacity", "Capacity", min: 1);


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            int capacity = config.GetParameter<int>("capacity", 100);

            var gq = new GateQueue(
                capacity: capacity,
                myName:   config.Name,
                sClock:   clock,
                vElement: vElement);

            return gq;
        }
    }
}
