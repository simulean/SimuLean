using SimuLean.Naval;

namespace SimuLean.Serialization
{
    /// <summary>Headless build of <c>UnityErectionDock</c>: <see cref="ErectionDock"/>.</summary>
    internal sealed class UnityErectionDockHandler : ElementHandlerBase
    {
        public override string TypeName => "UnityErectionDock";

        public override SimuLean.Optimization.ParameterSchema Schema { get; } = new SimuLean.Optimization.ParameterSchema()
            .Describe("capacity", "Erection fronts", min: 1)
            .Choice<ErectionDock.ErectionRule>("erectionRule")
            .Describe("expectedBlocks", "Blocks per hull", min: 0)
            .Structural("blockLabel", "sequenceLabel", "predecessorsLabel", "shipName");

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var rule = System.Enum.TryParse(config.GetParameter<string>("erectionRule", "Sequence"), out ErectionDock.ErectionRule r)
                ? r : ErectionDock.ErectionRule.Sequence;
            var dock = new ErectionDock(config.Name, clock,
                ElementBuildHelpers.BuildRandomProcess(config, prefix: "delay", clock: clock),
                config.GetParameter<int>("capacity", 1), rule, vElement)
            {
                ExpectedBlocks = config.GetParameter<int>("expectedBlocks", 0),
            };
            string Text(string key, string def) { var v = config.GetParameter<string>(key, null); return string.IsNullOrWhiteSpace(v) ? def : v.Trim(); }
            dock.BlockLabel = Text("blockLabel", dock.BlockLabel);
            dock.SequenceLabel = Text("sequenceLabel", dock.SequenceLabel);
            dock.PredecessorsLabel = Text("predecessorsLabel", dock.PredecessorsLabel);
            dock.ShipName = Text("shipName", dock.ShipName);
            return dock;
        }
    }
}
