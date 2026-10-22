using System;
using System.Collections.Generic;
using System.Globalization;
using SimuLean;

namespace SimuLean.Serialization
{
    /// <summary>
    /// Extraction + headless build for <see cref="AssemblyStation"/>
    /// (<see cref="UnityAssemblyStation"/> wrapper). Task 0-D.
    ///
    /// The precedence network is serialized as a <c>List&lt;string&gt;</c> of
    /// pipe-delimited task records (deep-cloned by the GA's CloneConfig):
    /// <c>"id|pred1,pred2|sku1:qty1,sku2:qty2|priority|durationSpec"</c>.
    /// The duration is the LAST field and is re-joined on decode, so a duration
    /// spec may itself contain '|'. Each task's duration sampler is encoded with
    /// a compact <c>Type~p1~p2</c> codec and rebuilt through the shared
    /// <see cref="ElementBuildHelpers.BuildRandomProcess"/> (so it also gets a
    /// per-model random stream). Material feeds are emitted as <c>"StockInput"</c>
    /// connections into this station's <see cref="SkuStockPort"/>; the job input
    /// and assembled-job output are the standard <c>nextElements</c> connections.
    ///
    /// Limitation (documented): task ids and SKU strings must not contain the
    /// delimiters '|', ',' or ':'; an Expression duration must not contain
    /// '~' (any other characters, including '|', are preserved).
    /// </summary>
    internal sealed class UnityAssemblyStationHandler : ElementHandlerBase
    {
        public override string TypeName => "UnityAssemblyStation";

        /// <summary>Parameterisation of this type's keys (numbers / bools are found by type; see ParameterSchema).</summary>
        public override SimuLean.Optimization.ParameterSchema Schema { get; } = new SimuLean.Optimization.ParameterSchema()
            .Structural("assemblyTasks")
            .Describe("workerCount", "Workers", min: 1).Describe("stockCapacity", "Material buffer capacity", min: 1);

        // ── extraction ───────────────────────────────────────────────────────



        // ── build ────────────────────────────────────────────────────────────

        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            int workerCount   = config.GetParameter<int>("workerCount", 1);
            int stockCapacity = config.GetParameter<int>("stockCapacity", -1);

            var specs = new List<AssemblyTaskSpec>();
            var records = config.GetParameter<List<string>>("assemblyTasks", null);
            if (records != null)
                foreach (var rec in records)
                    if (!string.IsNullOrEmpty(rec))
                    {
                        var spec = DecodeTask(rec, clock, config.Name);
                        if (!string.IsNullOrEmpty(spec.Id))
                            specs.Add(spec);
                    }

            try
            {
                return new AssemblyStation(config.Name, clock, specs, workerCount, stockCapacity, null, vElement);
            }
            catch (ArgumentException e)
            {
                throw new FormatException($"Assembly station '{config.Name}': invalid task configuration: {e.Message}", e);
            }
        }

        // ── task record codec ──────────────────────────────────────────────────


        private static AssemblyTaskSpec DecodeTask(string record, SimClock clock, string owner)
        {
            string[] parts = record.Split('|');
            var spec = new AssemblyTaskSpec
            {
                Id = parts.Length > 0 ? parts[0].Trim() : ""
            };

            if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]))
                foreach (var p in parts[1].Split(','))
                    if (!string.IsNullOrWhiteSpace(p))
                        spec.Predecessors.Add(p.Trim());

            if (parts.Length > 2 && !string.IsNullOrEmpty(parts[2]))
                foreach (var m in parts[2].Split(','))
                {
                    int sep = m.LastIndexOf(':');
                    if (sep <= 0) continue;
                    string sku = m.Substring(0, sep);
                    if (int.TryParse(m.Substring(sep + 1), NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out int q) && q > 0)
                        spec.Materials[sku] = q;
                }

            if (parts.Length > 3 &&
                int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int prio))
                spec.Priority = prio;

            // Duration is the last field; re-join so a spec containing '|' survives.
            string dur = parts.Length > 4
                ? string.Join("|", parts, 4, parts.Length - 4)
                : "Constant~0";
            spec.Duration = DecodeDuration(dur, clock, owner, spec.Id);

            return spec;
        }

        // ── duration sampler codec ─────────────────────────────────────────────



        // One stream per task: key "<station>/task:<id>" (the Unity wrapper uses the same key).
        private static IRandomSampler DecodeDuration(string spec, SimClock clock, string owner, string taskId)
            => SamplerSpec.Decode(spec, clock, owner, "task:" + taskId);
    }
}
