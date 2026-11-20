using SimuLean.Unity;
using SimuLean;
using System.Collections.Generic;
using System.Globalization;
using System;
using UnityEngine;

namespace SimuLean.Serialization
{
    /// <summary>Extraction (Unity scene → <see cref="ElementConfig"/>) for <c>UnityAssemblyStation</c>; the headless build side is
    /// <c>AssemblyStationHandler</c> in the engine.</summary>
    internal sealed class UnityAssemblyStationExtractor : ElementExtractorBase
    {
        public override string TypeName => "AssemblyStation";

        public override void ExtractParameters(SElement element, ElementConfig config)
        {
            if (!(element is UnityAssemblyStation asy)) return;

            config.Set("workerCount", asy.workerCount);
            config.Set("stockCapacity", asy.stockCapacity);

            // the precedence network as typed records tasks[i] with its own requirements, the team only when it
            // is not the station's
            var tasks = new List<AssemblyTaskSettings>();
            if (asy.tasks != null)
                foreach (var t in asy.tasks)
                    if (t != null && !string.IsNullOrEmpty(t.id))
                        tasks.Add(TaskOf(t, asy.processBy != null ? asy.processBy.team : null));
            config.Set("tasks", tasks);
            // no station team, tasks with teams of their own: the Process block's settings for their uses
            if (asy.TasksUseOwnTeams) (asy.processBy ?? new ProcessResourceUse()).ExportTaskSettings(config);
            asy.setup?.Export(config);   // its setup (rule None: nothing); the crew's team in SceneExtractor
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

        private static AssemblyTaskSettings TaskOf(AssemblyTaskConfig t, UnityResourceTeam stationTeam)
        {
            var task = new AssemblyTaskSettings
            {
                Id = t.id,
                Priority = t.priority,
                Duration = EncodeDuration(t.duration ?? new RandomProcessConfig()),
            };
            foreach (string p in (t.predecessors ?? "").Split(','))
                if (!string.IsNullOrWhiteSpace(p)) (task.Predecessors = task.Predecessors ?? new List<string>()).Add(p.Trim());
            if (t.materials != null)
                foreach (var m in t.materials)
                    if (m != null && !string.IsNullOrEmpty(m.sku) && m.qty > 0)
                        (task.Materials = task.Materials ?? new List<MaterialSettings>()).Add(new MaterialSettings { Sku = m.sku, Qty = m.qty });
            // its own resource use (tasks[i].processBy): its requirements, and its request settings when it has its own
            var use = new TaskUseSettings();
            if (t.requirements != null)
                foreach (var r in t.requirements)
                    if (r != null) (use.Requirements = use.Requirements ?? new List<RequirementSettings>()).Add(r.ToSettings(stationTeam));
            var own = t.resourceSettings;
            if (own != null && own.own)
            {
                if (!string.IsNullOrWhiteSpace(own.priority)) use.Priority = own.priority.Trim();
                use.Preemption = (SimuLean.Resources.PreemptMode)System.Enum.Parse(typeof(SimuLean.Resources.PreemptMode), own.preemption.ToString());
                use.Preemptable = own.preemptable;
                use.Acquire = (SimuLean.Resources.AcquireOrder)System.Enum.Parse(typeof(SimuLean.Resources.AcquireOrder), own.acquire.ToString());
                use.MaxWait = string.IsNullOrWhiteSpace(own.maxWait) ? "0" : own.maxWait.Trim();
                use.OnTimeout = own.onTimeout.ToString();
            }
            if (use.Any) task.ProcessBy = use;
            return task;
        }

        internal static SamplerSettings EncodeDuration(RandomProcessConfig d) => d.ToSettings();
    }
}
