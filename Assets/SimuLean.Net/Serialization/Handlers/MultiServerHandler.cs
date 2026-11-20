using SimuLean;

namespace SimuLean.Serialization
{
    internal sealed class MultiServerHandler : ElementHandlerBase
    {
        public override string TypeName => "MultiServer";

        // the Multi-Server only: a Combiner gathers its components and a Picking Station its stock between the grant and
        // the work (its timer would run on meanwhile), and only a Multi-Server's job sends its item elsewhere
        internal override bool JobsAcquireBeforeEntry => true;
        internal override bool JobsReroute => true;


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<MultiServerSettings>();
            int capacity = s.Capacity;

            // Build one independent delay-process instance per server slot
            IRandomSampler[] cycleTime = new IRandomSampler[capacity];
            for (int i = 0; i < capacity; i++)
                cycleTime[i] = ElementBuildHelpers.BuildRandomProcess(s.DelayStrategy, config.Name, "delayStrategy", clock);

            InputStrategy inputStrat = ElementBuildHelpers.BuildInputStrategy(config);

            var ms = new MultiServer(
                randomTimes:   cycleTime,
                name:          config.Name,
                sClock:        clock,
                inputStrategy: inputStrat,
                vElement:      vElement);

            ms.OfferOrder = s.OfferOrder;
            ms.OfferScan = System.Math.Max(0, s.OfferScan);
            return ms;
        }
    }
}
