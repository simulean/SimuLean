using System;
using System.Collections.Generic;
using SimuLean;
using SimuLean.Data;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Extraction + headless build for <see cref="PickingStation"/>
    /// (<see cref="PickingStation"/> wrapper).
    ///
    /// Wiring model (mirrors the wrapper's <c>ConnectSim</c>):
    ///  - Container/order input and picked-container output are the standard
    ///    <c>nextElements</c> "General" connections handled centrally.
    ///  - Stock feeds are emitted as <c>"StockInput"</c> connections
    ///    (source → this station's <see cref="SkuStockPort"/>).
    ///  - The returns destination is emitted as a <c>"ReturnsOutput"</c>
    ///    connection (this station's <see cref="ReturnsPort"/> → destination).
    ///
    /// The pick-list source chain (data table → container labels → static) is rebuilt
    /// from serialized flags; a container a source has no list for falls back to the next.
    /// </summary>
    internal sealed class PickingStationHandler : ElementHandlerBase
    {
        public override string TypeName => "PickingStation";

        // a material feed delivers to its SKU stock; its returns leave by their own port
        internal override SkuStockPort StockPort(Element element) => ((PickingStation)element).GetStockPort();
        internal override Element ReturnsPort(Element element) => ((PickingStation)element).GetReturnsPort();

        // ── extraction ───────────────────────────────────────────────────────



        // ── build ────────────────────────────────────────────────────────────

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<PickingStationSettings>();
            IRandomSampler delay = ElementBuildHelpers.BuildRandomProcess(s.DelayStrategy, config.Name, "delayStrategy", clock);
            int stockCapacity    = s.StockCapacity;
            IPickListSource pick = BuildPickListSource(config, s, clock);

            return new PickingStation(
                name:           config.Name,
                simClock:       clock,
                delayStrategy:  delay,
                pickListSource: pick,
                stockCapacity:  stockCapacity,
                stockStrategy:  null,
                vElement:       vElement)
            {
                ReturnLeftovers = s.ReturnLeftovers,
            };
        }

        private static IPickListSource BuildPickListSource(ElementConfig config, PickingStationSettings s, SimClock clock)
        {
            var chain = new List<IPickListSource>();

            // a data table of the model: indexed once from the run's snapshot, never queried during the run
            string pickTable = s.PickTable ?? "";
            if (!string.IsNullOrWhiteSpace(pickTable))
            {
                if (!clock.Context.Data.TryGet(pickTable, out var table))
                    throw new FormatException($"Picking Station '{config.Name}': no data table '{pickTable}' in the model " +
                                              $"(tables: {(clock.Context.Data.Count == 0 ? "none" : string.Join(", ", clock.Context.Data.Names))}).");
                string key = s.KeyColumn ?? "";
                chain.Add(new TablePickListSource(table, s.SkuColumn ?? "sku", s.QtyColumn ?? "qty",
                                                  string.IsNullOrEmpty(key) ? null : key, s.ContainerKeyLabel ?? "order_id"));
            }

            if (s.UseContainerLabels)
                chain.Add(new LabelPickListSource(s.LabelPrefix ?? "pick_"));

            var staticDict = new Dictionary<string, int>();
            foreach (var e in s.StaticPickList ?? new List<PickListEntrySettings>())
                if (e != null && !string.IsNullOrEmpty(e.Sku) && e.Qty > 0)
                    staticDict[e.Sku] = e.Qty;
            chain.Add(new StaticPickListSource(staticDict));

            return new CompositePickListSource(chain.ToArray());
        }
    }
}
