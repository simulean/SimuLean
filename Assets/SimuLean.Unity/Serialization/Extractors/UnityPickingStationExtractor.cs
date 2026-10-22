using SimuLean.Data;
using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using System;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityPickingStation</c>; the headless build side is
    /// <c>UnityPickingStationHandler</c> in the engine.</summary>
    internal sealed class UnityPickingStationExtractor : ElementExtractorBase
    {
        public override string TypeName => "UnityPickingStation";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityPickingStation ps)) return;

            config.Parameters["stockCapacity"] = ps.stockCapacity;

            if (ps.delayStrategy != null)
                ExtractorHelpers.ExtractRandomProcessConfig(ps.delayStrategy, config, prefix: "delay");

            // Pick-list source configuration (a data table first).
            if (ps.pickTable != null) config.Parameters["pickTable"] = ps.pickTable.TableName;
            config.Parameters["skuColumn"]          = ps.skuColumn ?? "";
            config.Parameters["qtyColumn"]          = ps.qtyColumn ?? "";
            config.Parameters["keyColumn"]          = ps.keyColumn ?? "";
            config.Parameters["containerKeyLabel"]  = ps.containerKeyLabel ?? "";
            config.Parameters["useContainerLabels"] = ps.useContainerLabels;
            config.Parameters["labelPrefix"]        = ps.labelPrefix ?? "";

            // Static fallback list encoded as "sku:qty" records (List<string> is
            // deep-cloned by the GA's CloneConfig).
            var staticList = new List<string>();
            if (ps.staticPickList != null)
                foreach (var row in ps.staticPickList)
                    if (row != null && !string.IsNullOrEmpty(row.sku) && row.qty > 0)
                        staticList.Add(row.sku + ":" + row.qty);
            config.Parameters["staticPickList"] = staticList;
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
