using System;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Headless build of a <see cref="SequencingStation"/> from its typed settings (<see cref="SequencingStationSettings"/>);
    /// the handler delegates here, so the build path is testable without Unity. Each time sampler has its own
    /// stream keyed by the element name and the setting (<c>prepareTime</c>…).
    /// Wiring (factory): carriers by ordinary connections; orders by <c>PortInput</c> (port <c>orders</c>); pieces by
    /// <c>PortInput</c> or <c>PullFromList</c> (port <c>pieces</c>); served orders by <c>PortOutput</c> (port <c>orders</c>).
    /// </summary>
    public static class SequencingStationBuilder
    {
        public const string TypeName = "SequencingStation";

        public static SequencingStation Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<SequencingStationSettings>();
            var st = new SequencingStation(config.Name, clock, vElement)
            {
                CarrierCapacity = Math.Max(1, s.CarrierCapacity),
                CapacityLabel = s.CapacityLabel ?? "",
                LotSize = s.LotSize,
                OldestOrderMaxWait = s.OldestOrderMaxWait,
                WaitForFullCarrier = s.WaitForFullCarrier,
                ReferenceLabel = s.ReferenceLabel ?? "",
                PieceField = s.PieceField ?? "",
                OrderTimeLabel = s.OrderTimeLabel ?? "",
                VehicleLabel = s.VehicleLabel ?? "",
                CopyOrderLabels = s.CopyOrderLabels,
                OperatorScope = s.OperatorScope,
            };
            if (!string.IsNullOrWhiteSpace(s.Applies)) st.Applies = Expressions.Expression.ParseCondition(s.Applies, Expressions.ExpressionContext.Sequencing, st.Name, "applies");
            st.OrderPort.Capacity = s.OrderCapacity;
            st.PiecePort.Capacity = s.PieceStock;
            st.PrepareTime = Sampler(s.PrepareTime, config.Name, "prepareTime", clock);
            st.PickTime = Sampler(s.PickTime, config.Name, "pickTime", clock);
            st.ExtraTime = Sampler(s.ExtraTime, config.Name, "extraTime", clock);
            st.PlaceTime = Sampler(s.PlaceTime, config.Name, "placeTime", clock);
            st.GapTime = Sampler(s.GapTime, config.Name, "gapTime", clock);
            st.CloseTime = Sampler(s.CloseTime, config.Name, "closeTime", clock);
            ElementBuildHelpers.ApplyOutputStrategy(st, config);
            return st;
        }

        private static IRandomSampler Sampler(SamplerSettings s, string owner, string purpose, SimClock clock)
            => ElementBuildHelpers.SamplerOf(s, clock, owner, purpose, purpose);
    }

    /// <summary>Headless build of <c>UnitySequencingStation</c> → <see cref="SequencingStation"/> (build: <see cref="SequencingStationBuilder"/>).</summary>
    internal sealed class SequencingStationHandler : ElementHandlerBase
    {
        public override string TypeName => SequencingStationBuilder.TypeName;

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
            => SequencingStationBuilder.Build(config, clock, vElement);
    }
}
