using SimuLean;

namespace SimuLean.Serialization
{
    internal sealed class UnityMultiServerHandler : ElementHandlerBase
    {
        public override string TypeName => "UnityMultiServer";

        /// <summary>Parameterisation of this type's keys (numbers / bools are found by type; see ParameterSchema).</summary>
        public override SimuLean.Optimization.ParameterSchema Schema { get; } = new SimuLean.Optimization.ParameterSchema()
            .Describe("capacity", "Servers", min: 1)
            .Choice<ReleaseMode>(ElementBuildHelpers.ReleaseModeKey).Describe(ElementBuildHelpers.ReleaseScanKey, "Release scan", min: 0);


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            int capacity = config.GetParameter<int>("capacity", 1);

            // Build one independent delay-process instance per server slot
            IRandomSampler[] cycleTime = new IRandomSampler[capacity];
            for (int i = 0; i < capacity; i++)
                cycleTime[i] = ElementBuildHelpers.BuildRandomProcess(config, prefix: "delay", clock: clock);

            InputStrategy inputStrat = ElementBuildHelpers.BuildInputStrategy(config);

            var ms = new MultiServer(
                randomTimes:   cycleTime,
                name:          config.Name,
                sClock:        clock,
                inputStrategy: inputStrat,
                vElement:      vElement);

            (ms.Release, ms.ReleaseScan) = ElementBuildHelpers.BuildRelease(config);
            return ms;
        }
    }
}
