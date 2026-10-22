using SimuLean;

namespace SimuLean.Serialization
{
    internal sealed class UnityQueueHandler : ElementHandlerBase
    {
        public override string TypeName => "UnityQueue";

        /// <summary>Parameterisation of this type's keys (numbers / bools are found by type; see ParameterSchema).</summary>
        public override SimuLean.Optimization.ParameterSchema Schema { get; } = new SimuLean.Optimization.ParameterSchema()
            .Describe("capacity", "Capacity", min: 1)
            .Choice<ReleaseMode>(ElementBuildHelpers.ReleaseModeKey).Describe(ElementBuildHelpers.ReleaseScanKey, "Release scan", min: 0);


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            int capacity = config.GetParameter<int>("capacity", 100);

            InputStrategy inputStrat = ElementBuildHelpers.BuildInputStrategy(config);

            var queue = new ItemsQueue(
                capacity:      capacity,
                myName:        config.Name,
                sClock:        clock,
                inputStrategy: inputStrat,
                vElement:      vElement);

            (queue.Release, queue.ReleaseScan) = ElementBuildHelpers.BuildRelease(config);
            return queue;
        }
    }
}
