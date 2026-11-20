using SimuLean.Unity;
using SimuLean;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnitySource</c>; the headless build side is
    /// <c>SourceHandler</c> in the engine.</summary>
    internal sealed class UnitySourceExtractor : ElementExtractorBase
    {
        public override string TypeName => "Source";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnitySource source)) return;

            config.Set("mode", source.mode.ToString());

            if (source.ReadsTableSource)
            {
                // read by the handler when the model is built (headless, scene, study): the current data, a catalog key
                config.Set("tableSource", source.tableSource.Trim());
                config.Set("tableDatabase", source.TableDatabasePath());
            }
            else if (source.ReadsDataTable)
            {
                // a data table of the model: the handler takes its snapshot by name
                config.Set("dataTable", source.dataTable.TableName);
            }
            else if (source.mode == SourceMode.RateTable)
            {
                // the periods inline; else the rows set from code
                var periods = new List<RatePeriodSettings>();
                foreach (var p in source.ratePeriods ?? new List<RatePeriodSpec>())
                {
                    if (p == null) continue;
                    var periodLabels = new List<string>();
                    if (p.labels != null) foreach (var l in p.labels) if (!string.IsNullOrWhiteSpace(l)) periodLabels.Add(l.Trim());
                    periods.Add(new RatePeriodSettings { Start = p.start, Rate = p.rate, Labels = periodLabels.Count > 0 ? periodLabels : null });
                }
                var table = periods.Count == 0 ? source.TableForRun(out _) : null;
                if (periods.Count > 0) config.Set("ratePeriods", periods);
                else if (table != null && table.Count > 0) config.Set("arrivalsTable", table);
                else Debug.LogError($"[SourceHandler] '{source.name}': RateTable mode without periods: set Rate Periods " +
                                   "(start, rate) or link a Data Table with the columns Start and Rate.");
            }
            else if (source.mode == SourceMode.Schedule)
            {
                // no data table: the rows set from code (SetArrivalsTable)
                var arrivalsTable = source.TableForRun(out string warning);
                if (warning != null) Debug.LogWarning("[SourceHandler] " + warning);
                if (arrivalsTable != null && arrivalsTable.Count > 0)
                {
                    config.Set("arrivalsTable", arrivalsTable);
                    Debug.Log($"[SourceHandler] '{source.name}': Schedule mode, " +
                              $"{source.GetItemCount()} items loaded.");
                }
                else
                {
                    Debug.LogError($"[SourceHandler] '{source.name}': Schedule mode but no arrivals table: " +
                                   "link a Data Table (Parameters ▸ Data table) or set a table source.");
                }
            }
            else // InterArrival mode
            {
                if (source.interArrivalDistribution != null)
                    ExtractorHelpers.ExtractRandomProcessConfig(
                        source.interArrivalDistribution, config, "interArrivalDistribution");
                else
                {
                    config.Set("interArrivalDistribution" + "." + "strategyType", "Constant");   // scene: null = immediate
                    config.Set("interArrivalDistribution" + ".constantValue", 0.0);
                }
            }


            var labels = new List<string>();   // name = expression records (LabelAssignments)
            if (source.createLabels != null)
                foreach (var l in source.createLabels)
                    if (!string.IsNullOrWhiteSpace(l)) labels.Add(l.Trim());
            config.Set("createLabels", labels);
            config.Set("demandFactor", (double)source.demandFactor);   // always: a catalog parameter
            config.Set("arrivalsOnStop", source.arrivalsOnStop.ToString());   // always: a Choice of the catalog

            // the items' size from their 3D model, applied by the engine where the labels set none
            if (source.sizeFromModel)   // the keys of SourceHandler.Model*Key
                ReplenishmentBuilders.EncodeModelSize(config, "", ItemVisuals.ModelSize(source.itemPrefab));
        }
    }
}
