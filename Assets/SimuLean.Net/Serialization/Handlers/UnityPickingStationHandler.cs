using System;
using System.Collections.Generic;
using SimuLean;
using SimuLean.Data;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Extraction + headless build for <see cref="PickingStation"/>
    /// (<see cref="UnityPickingStation"/> wrapper). Task 0-D.
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
    internal sealed class UnityPickingStationHandler : ElementHandlerBase
    {
        public override string TypeName => "UnityPickingStation";

        /// <summary>Parameterisation of this type's keys (numbers / bools are found by type; see ParameterSchema).</summary>
        public override SimuLean.Optimization.ParameterSchema Schema { get; } = new SimuLean.Optimization.ParameterSchema()
            .Structural("skuColumn", "qtyColumn", "keyColumn", "containerKeyLabel", "labelPrefix",
                        "staticPickList", "useContainerLabels", "pickTable")
            .Describe("stockCapacity", "Stock buffer capacity", min: 1);

        // ── extraction ───────────────────────────────────────────────────────



        // ── build ────────────────────────────────────────────────────────────

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            IRandomSampler delay = ElementBuildHelpers.BuildRandomProcess(config, prefix: "delay", clock: clock);
            int stockCapacity    = config.GetParameter<int>("stockCapacity", -1);
            IPickListSource pick = BuildPickListSource(config, clock);

            return new PickingStation(
                name:           config.Name,
                simClock:       clock,
                delayStrategy:  delay,
                pickListSource: pick,
                stockCapacity:  stockCapacity,
                stockStrategy:  null,
                vElement:       vElement);
        }

        private static IPickListSource BuildPickListSource(ElementConfig config, SimClock clock)
        {
            var chain = new List<IPickListSource>();

            // a data table of the model: indexed once from the run's snapshot, never queried during the run
            string pickTable = config.GetParameter<string>("pickTable", "");
            if (!string.IsNullOrWhiteSpace(pickTable))
            {
                if (!clock.Context.Data.TryGet(pickTable, out var table))
                    throw new FormatException($"Picking Station '{config.Name}': no data table '{pickTable}' in the model " +
                                              $"(tables: {(clock.Context.Data.Count == 0 ? "none" : string.Join(", ", clock.Context.Data.Names))}).");
                string key = config.GetParameter<string>("keyColumn", "");
                chain.Add(new TablePickListSource(table, config.GetParameter<string>("skuColumn", "sku"), config.GetParameter<string>("qtyColumn", "qty"),
                                                  string.IsNullOrEmpty(key) ? null : key, config.GetParameter<string>("containerKeyLabel", "order_id")));
            }

            if (config.GetParameter<bool>("useContainerLabels", true))
                chain.Add(new LabelPickListSource(config.GetParameter<string>("labelPrefix", "pick_")));

            var staticDict = new Dictionary<string, int>();
            var staticList = config.GetParameter<List<string>>("staticPickList", null);
            if (staticList != null)
                foreach (var record in staticList)
                {
                    int sep = record.LastIndexOf(':');
                    if (sep <= 0) continue;
                    string sku = record.Substring(0, sep);
                    if (int.TryParse(record.Substring(sep + 1), out int qty) && qty > 0)
                        staticDict[sku] = qty;
                }
            chain.Add(new StaticPickListSource(staticDict));

            return new CompositePickListSource(chain.ToArray());
        }
    }
}
