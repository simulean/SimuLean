using System.Collections.Generic;
using SimuLean;

namespace SimuLean.Serialization
{
    internal sealed class UnitySourceHandler : ElementHandlerBase
    {
        public override string TypeName => "UnitySource";

        /// <summary>Parameterisation of this type's keys (numbers / bools are found by type; see ParameterSchema).</summary>
        public override SimuLean.Optimization.ParameterSchema Schema { get; } = new SimuLean.Optimization.ParameterSchema()
            .Choice("sourceMode", "Schedule", "InterArrival")
            .Describe(DemandFactorKey, "Demand factor", min: 0.01)
            .Structural("arrivalsTable", "assignLabels", "sheetName", "tableDatabase", "dataTable")
            .File("fileName")    // the table's file: a study varies it (Owner.fileName=A.csv|B.csv, follow-up #29)
            .File("tableSource"); // a table read at build time (SQL / block breakdown part): a study varies it too


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            string mode = config.GetParameter<string>("sourceMode", "Schedule");
            return mode == "InterArrival"
                ? BuildInterArrival(config, clock, vElement)
                : BuildSchedule(config, clock, vElement);
        }

        private static Element BuildSchedule(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var arrivalsTable = config.GetParameter<Dictionary<string, List<string>>>("arrivalsTable");
            string spec = config.GetParameter<string>("tableSource", null);
            if (SimuLean.Data.TableSources.IsSpec(spec))
            {
                // the table is read now, from the project database (SQL / a block breakdown part): every run reads the
                // same current data, and a study can vary the spec
                try { arrivalsTable = SimuLean.Data.TableSources.Read(spec, config.GetParameter<string>("tableDatabase", null)); }
                catch (System.Exception e) when (!(e is System.OutOfMemoryException))
                {
                    throw new System.FormatException($"Source '{config.Name}': its table '{spec}' could not be read ({e.Message}).");
                }
            }
            string tableName = config.GetParameter<string>("dataTable", null);
            string file = config.GetParameter<string>("fileName", null);
            // a file and a data table (a hand-written config): one would be ignored without a word — say which to keep
            // (a study's Owner.fileName factor replaces the data table itself, SourceTableParameter)
            if (!string.IsNullOrWhiteSpace(tableName) && !string.IsNullOrWhiteSpace(file) && (arrivalsTable == null || arrivalsTable.Count == 0))
                throw new System.FormatException($"Source '{config.Name}': both a data table ('{tableName}') and a table file ('{file}'); " +
                                                 $"keep one (to vary the data table's file in a study: data:{tableName}.file).");
            if ((arrivalsTable == null || arrivalsTable.Count == 0) && !string.IsNullOrWhiteSpace(tableName))
            {
                // a data table of the model: its snapshot, taken at the run's start; a study varies it as data:<table>
                if (!clock.Context.Data.TryGet(tableName, out var table))
                    throw new System.FormatException($"Source '{config.Name}': no data table '{tableName}' in the model " +
                                                     $"(tables: {(clock.Context.Data.Count == 0 ? "none" : string.Join(", ", clock.Context.Data.Names))}).");
                arrivalsTable = table.ToColumns();
            }
            if ((arrivalsTable == null || arrivalsTable.Count == 0) && !string.IsNullOrWhiteSpace(file))
            {
                // a config with the file only (written by hand): read it now — CSV / .data (Excel is read in Unity)
                try { arrivalsTable = SimuLean.Data.TableReader.Read(file); }
                catch (System.Exception e) when (e is System.IO.IOException || e is System.NotSupportedException || e is System.UnauthorizedAccessException)
                {
                    throw new System.FormatException($"Source '{config.Name}': its table file '{file}' could not be read ({e.Message}).");
                }
            }

            if (arrivalsTable == null || arrivalsTable.Count == 0)
                throw new System.FormatException($"Source '{config.Name}': schedule mode without an arrivals table (load the data in the inspector before extracting).");

            var source = new ScheduleSource(config.Name, clock, arrivalsTable, vElement: vElement);
            ApplyLabels(config, source.AssignedLabels);
            source.DemandFactor = DemandFactor(config);
            return source;
        }

        /// <summary>The demand factor (a study varies it, <c>Source.demandFactor=1|1.2</c>); must be &gt; 0.</summary>
        private static double DemandFactor(ElementConfig config)
        {
            double f = config.GetParameter<double>(DemandFactorKey, 1.0);
            if (!(f > 0) || double.IsInfinity(f))
                throw new System.FormatException($"Source '{config.Name}': demand factor {f} must be > 0.");
            return f;
        }

        public const string DemandFactorKey = "demandFactor";

        /// <summary>"label=value" records → the source's assigned labels.</summary>
        private static void ApplyLabels(ElementConfig config, System.Collections.Generic.Dictionary<string, string> target)
        {
            var records = config.GetParameter<List<string>>("assignLabels");
            if (records == null) return;
            foreach (string r in records)
            {
                int eq = r?.IndexOf('=') ?? -1;
                if (eq > 0) target[r.Substring(0, eq).Trim()] = r.Substring(eq + 1).Trim();
            }
        }

        private static Element BuildInterArrival(ElementConfig config, SimClock clock, IVElement vElement)
        {
            IRandomSampler tba = ElementBuildHelpers.BuildRandomProcess(config, prefix: "interArrival", clock: clock);

            var source = new InterArrivalSource(
                name:             config.Name,
                state:            clock,
                interArrivalTime: tba,
                vElement:         vElement);
            ApplyLabels(config, source.AssignedLabels);
            source.DemandFactor = DemandFactor(config);
            return source;
        }
    }
}
