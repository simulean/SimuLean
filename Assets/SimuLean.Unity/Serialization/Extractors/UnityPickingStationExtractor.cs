using SimuLean.Data;
using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using System;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityPickingStation</c>; the headless build side is
    /// <c>PickingStationHandler</c> in the engine.</summary>
    internal sealed class UnityPickingStationExtractor : ElementExtractorBase
    {
        public override string TypeName => "PickingStation";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityPickingStation ps)) return;

            config.Set("stockCapacity", ps.stockCapacity);
            config.Set("returnLeftovers", ps.returnLeftovers);

            if (ps.delayStrategy != null)
                ExtractorHelpers.ExtractRandomProcessConfig(ps.delayStrategy, config, "delayStrategy");
            ps.setup?.Export(config);   // its setup (also the rule None); the crew's team in SceneExtractor
            config.Set("reserveExit", ps.reserveExit);

            // Pick-list source configuration (a data table first).
            if (ps.pickTable != null) config.Set("pickTable", ps.pickTable.TableName);
            config.Set("skuColumn", ps.skuColumn ?? "");
            config.Set("qtyColumn", ps.qtyColumn ?? "");
            config.Set("keyColumn", ps.keyColumn ?? "");
            config.Set("containerKeyLabel", ps.containerKeyLabel ?? "");
            config.Set("useContainerLabels", ps.useContainerLabels);
            config.Set("labelPrefix", ps.labelPrefix ?? "");

            // the static fallback list: typed records staticPickList[i] (sku, qty)
            var staticList = new List<PickListEntrySettings>();
            if (ps.staticPickList != null)
                foreach (var row in ps.staticPickList)
                    if (row != null && !string.IsNullOrEmpty(row.sku) && row.qty > 0)
                        staticList.Add(PickListEntrySettings.Of(row.sku, row.qty));
            config.SettingsAs<PickingStationSettings>().StaticPickList = staticList;
        }

        public override void ExtractConnections(SElement element, string elementId,
                                                List<ConnectionConfig> connections,
                                                Dictionary<SElement, string> ids)
        {
            if (!(element is UnityPickingStation ps)) return;

            if (ps.stockSources != null)
                foreach (var src in ps.stockSources)
                    if (src != null && ids.ContainsKey(src))
                        connections.Add(new ConnectionConfig(ids[src], elementId, "StockInput"));

            if (ps.returnsDestination != null && ids.ContainsKey(ps.returnsDestination))
                connections.Add(new ConnectionConfig(elementId, ids[ps.returnsDestination], "ReturnsOutput"));
        }
    }
}
