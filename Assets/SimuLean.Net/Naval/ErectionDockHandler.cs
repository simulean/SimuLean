using SimuLean.Naval;

namespace SimuLean.Serialization
{
    /// <summary>Headless build of <c>UnityErectionDock</c>: <see cref="ErectionDock"/>.</summary>
    internal sealed class ErectionDockHandler : ElementHandlerBase
    {
        public override string TypeName => "ErectionDock";

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<ErectionDockSettings>();
            var dock = new ErectionDock(config.Name, clock,
                ElementBuildHelpers.BuildRandomProcess(s.DelayStrategy, config.Name, "delayStrategy", clock),
                s.Capacity, s.ErectionRule, vElement)
            {
                ExpectedBlocks = s.ExpectedBlocks,
            };
            // an empty label keeps the dock's own
            string Text(string v, string def) => string.IsNullOrWhiteSpace(v) ? def : v.Trim();
            dock.BlockLabel = Text(s.BlockLabel, dock.BlockLabel);
            dock.SequenceLabel = Text(s.SequenceLabel, dock.SequenceLabel);
            dock.PredecessorsLabel = Text(s.PredecessorsLabel, dock.PredecessorsLabel);
            dock.ShipName = Text(s.ShipName, dock.ShipName);
            return dock;
        }
    }
}
