using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnitySource</c>; the headless build side is
    /// <c>UnitySourceHandler</c> in the engine.</summary>
    internal sealed class UnitySourceExtractor : ElementExtractorBase
    {
        public override string TypeName => "UnitySource";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnitySource source)) return;

            config.Parameters["sourceMode"]  = source.mode.ToString();

            if (source.mode == SourceMode.Schedule && source.ReadsTableSource)
            {
                // read by the handler when the model is built (headless, scene, study): the current data, a catalog key
                config.Parameters["tableSource"] = source.tableSource.Trim();
                config.Parameters["tableDatabase"] = source.TableDatabasePath();
            }
            else if (source.ReadsDataTable)
            {
                // a data table of the model: the handler takes its snapshot by name
                config.Parameters["dataTable"] = source.dataTable.TableName;
            }
            else if (source.mode == SourceMode.Schedule)
            {
                // no data table: the rows set from code (SetArrivalsTable)
                var arrivalsTable = source.TableForRun(out string warning);
                if (warning != null) Debug.LogWarning("[UnitySourceHandler] " + warning);
                if (arrivalsTable != null && arrivalsTable.Count > 0)
                {
                    config.SetArrivalsTable(arrivalsTable);
                    Debug.Log($"[UnitySourceHandler] '{source.name}': Schedule mode, " +
                              $"{source.GetItemCount()} items loaded.");
                }
                else
                {
                    Debug.LogError($"[UnitySourceHandler] '{source.name}': Schedule mode but no arrivals table: " +
                                   "link a Data Table (Parameters ▸ Data table) or set a table source.");
                }
            }
            else // InterArrival mode
            {
                if (source.interArrivalDistribution != null)
                    ExtractorHelpers.ExtractRandomProcessConfig(
                        source.interArrivalDistribution, config, prefix: "interArrival");
                else
                {
                    config.Parameters["interArrivalStrategyType"] = "Constant";   // scene: null = immediate
                    config.Parameters["constantValue"] = 0.0;
                }
            }


            var labels = new List<string>();
            if (source.assignLabels != null)
                foreach (var l in source.assignLabels)
                    if (l != null && !string.IsNullOrEmpty(l.label)) labels.Add(l.label + "=" + (l.value ?? ""));
            config.Parameters["assignLabels"] = labels;
            config.Parameters["demandFactor"] = (double)source.demandFactor;   // always: a catalog parameter (I8)
        }
    }
}
