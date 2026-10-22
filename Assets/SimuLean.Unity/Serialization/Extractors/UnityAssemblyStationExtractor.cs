using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using System.Globalization;
using System;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityAssemblyStation</c>; the headless build side is
    /// <c>UnityAssemblyStationHandler</c> in the engine.</summary>
    internal sealed class UnityAssemblyStationExtractor : ElementExtractorBase
    {
        public override string TypeName => "UnityAssemblyStation";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityAssemblyStation asy)) return;

            config.Parameters["workerCount"]   = asy.workerCount;
            config.Parameters["stockCapacity"] = asy.stockCapacity;

            var records = new List<string>();
            if (asy.tasks != null)
                foreach (var t in asy.tasks)
                    if (t != null && !string.IsNullOrEmpty(t.id))
                        records.Add(EncodeTask(t));
            config.Parameters["assemblyTasks"] = records;
        }

        public override void ExtractConnections(SElement element, string elementId,
                                                List<ConnectionConfig> connections,
                                                Dictionary<SElement, string> ids)
        {
            if (!(element is UnityAssemblyStation asy) || asy.materialSources == null) return;

            foreach (var src in asy.materialSources)
                if (src != null && ids.ContainsKey(src))
                    connections.Add(new ConnectionConfig(ids[src], elementId, "StockInput"));
        }

        private static string EncodeTask(AssemblyTaskConfig t)
        {
            var materials = new List<string>();
            if (t.materials != null)
                foreach (var m in t.materials)
                    if (m != null && !string.IsNullOrEmpty(m.sku) && m.qty > 0)
                        materials.Add(m.sku + ":" + m.qty);

            return string.Join("|", new[]
            {
                t.id ?? "",
                t.predecessors ?? "",
                string.Join(",", materials),
                t.priority.ToString(CultureInfo.InvariantCulture),
                EncodeDuration(t.duration ?? new RandomProcessConfig())
            });
        }

        internal static string EncodeDuration(RandomProcessConfig d) => d.ToSamplerSpec();
    }
}
