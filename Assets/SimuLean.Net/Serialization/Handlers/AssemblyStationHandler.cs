using System;
using System.Collections.Generic;
using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Headless build of an <see cref="AssemblyStation"/> (<c>UnityAssemblyStation</c> wrapper). Its settings
    /// (<see cref="AssemblyStationSettings"/>) hold the precedence network as typed records — <c>tasks[i]</c> with its id,
    /// predecessors, materials, priority, duration (a sampler spec <c>Type~p1~p2</c>, its own stream
    /// <c>&lt;station&gt;/task:&lt;id&gt;</c>) and own requirements (the <c>id|preds|sku:qty|…</c> and
    /// <c>taskId=count#query#label#team</c> record strings are gone). Material feeds are <c>"StockInput"</c> connections into
    /// its <see cref="SkuStockPort"/>; the job input and the assembled-job output are the standard connections.
    /// </summary>
    internal sealed class AssemblyStationHandler : ElementHandlerBase
    {
        public override string TypeName => "AssemblyStation";

        // a material feed delivers to its SKU stock
        internal override SkuStockPort StockPort(Element element) => ((AssemblyStation)element).GetStockPort();

        // its tasks with resources of their own
        internal override void ConfigureProcessUse(Element element, ElementConfig config,
            IReadOnlyDictionary<string, SimuLean.Resources.ResourceDispatcher> teams, SimuLean.Resources.StationResources use)
            => ResourceBuild.AssemblyTaskUses(config, teams, (AssemblyStation)element, use);

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<AssemblyStationSettings>();
            var specs = new List<AssemblyTaskSpec>();
            foreach (var t in s.Tasks ?? new List<AssemblyTaskSettings>())
            {
                if (t == null || string.IsNullOrWhiteSpace(t.Id)) continue;
                var spec = new AssemblyTaskSpec { Id = t.Id.Trim(), Priority = t.Priority };
                foreach (string p in t.Predecessors ?? new List<string>())
                    if (!string.IsNullOrWhiteSpace(p)) spec.Predecessors.Add(p.Trim());
                foreach (var m in t.Materials ?? new List<MaterialSettings>())
                    if (m != null && !string.IsNullOrEmpty(m.Sku) && m.Qty > 0) spec.Materials[m.Sku] = m.Qty;
                // one stream per task: "<station>/task:<id>"
                spec.Duration = ElementBuildHelpers.SamplerOf(t.Duration ?? SamplerSettings.Constant(0), clock, config.Name, "task:" + spec.Id,
                                                              "tasks[" + spec.Id + "].duration");
                specs.Add(spec);
            }
            try
            {
                return new AssemblyStation(config.Name, clock, specs, s.WorkerCount, s.StockCapacity, null, vElement);
            }
            catch (ArgumentException e)
            {
                throw new FormatException($"Assembly station '{config.Name}': invalid task configuration: {e.Message}", e);
            }
        }
    }
}
