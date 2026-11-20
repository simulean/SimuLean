using System.Collections.Generic;
using SimuLean;

namespace SimuLean.Serialization
{
    internal sealed class SourceHandler : ElementHandlerBase
    {
        public override string TypeName => "Source";


        public override Element Build(ElementConfig config, SimClock clock, IVElement vElement)
        {
            string mode = config.SettingsAs<SourceSettings>().Mode;
            return mode == "InterArrival" ? BuildInterArrival(config, clock, vElement)
                 : mode == "RateTable" ? BuildRateTable(config, clock, vElement)
                 : BuildSchedule(config, clock, vElement);
        }

        // The table of a schedule / rate-table source: its rows as given, read from its table source, data table or file
        // (null when it has none).
        private static Dictionary<string, List<string>> TableOf(ElementConfig config, SimClock clock)
        {
            var s = config.SettingsAs<SourceSettings>();
            var arrivalsTable = s.ArrivalsTable;
            string spec = s.TableSource;
            if (SimuLean.Data.TableSources.IsSpec(spec))
            {
                // the table is read now, from the project database (SQL / a block breakdown part): every run reads the
                // same current data, and a study can vary the spec
                try { arrivalsTable = SimuLean.Data.TableSources.Read(spec, s.TableDatabase); }
                catch (System.Exception e) when (!(e is System.OutOfMemoryException))
                {
                    throw new System.FormatException($"Source '{config.Name}': its table '{spec}' could not be read ({e.Message}).", e);
                }
            }
            string tableName = s.DataTable;
            string file = s.FileName;
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
                    throw new System.FormatException($"Source '{config.Name}': its table file '{file}' could not be read ({e.Message}).", e);
                }
            }
            return arrivalsTable;
        }

        private static Element BuildSchedule(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var arrivalsTable = TableOf(config, clock);

            if (arrivalsTable == null || arrivalsTable.Count == 0)
                // both ways to give it one (an agent cannot use the inspector)
                throw new System.FormatException($"Source '{config.Name}': schedule mode without an arrivals table (load one with " +
                                                 $"simulean_load_table object={config.Name} file=<table.csv>, or in the Source's inspector).");

            var source = new ScheduleSource(config.Name, clock, arrivalsTable, vElement: vElement);
            source.CreateLabels = CreateLabels(config);
            source.ModelSize = ModelSize(config);
            source.DemandFactor = DemandFactor(config);
            source.ArrivalsOnStop = config.SettingsAs<SourceSettings>().ArrivalsOnStop;   // what the arrivals do while the source is stopped
            return source;
        }

        /// <summary>The demand factor (a study varies it, <c>Source.demandFactor=1|1.2</c>); must be &gt; 0.</summary>
        private static double DemandFactor(ElementConfig config)
        {
            double f = config.SettingsAs<SourceSettings>().DemandFactor;
            if (!(f > 0) || double.IsInfinity(f))
                throw new System.FormatException($"Source '{config.Name}': demand factor {f} must be > 0.");
            return f;
        }

        /// <summary>(width, height, length[, load floor]) of the items' model, or null when the config has none.</summary>
        private static double[] ModelSize(ElementConfig config) => ReplenishmentBuilders.DecodeModelSize(config, "");

        private static LabelAssignments CreateLabels(ElementConfig config)
            => LabelAssignments.Parse(config.SettingsAs<SourceSettings>().CreateLabels, config.Name, "createLabel");

        // A rate-table source: its periods inline (ratePeriods) or from its table (Start, Rate, [Name], labels)
        private static Element BuildRateTable(ElementConfig config, SimClock clock, IVElement vElement)
        {
            var s = config.SettingsAs<SourceSettings>();
            var periods = new List<RatePeriod>();
            var inline = s.RatePeriods ?? new List<RatePeriodSettings>();
            var table = TableOf(config, clock);
            bool hasTable = table != null && table.Count > 0;
            if (inline.Count > 0 && hasTable)
                throw new System.FormatException($"Source '{config.Name}': a rate table both inline (ratePeriods) and from a table; keep one.");
            if (inline.Count > 0)
            {
                for (int i = 0; i < inline.Count; i++)
                {
                    var p = inline[i] ?? new RatePeriodSettings();
                    periods.Add(new RatePeriod
                    {
                        Start = p.Start, Rate = p.Rate,
                        Labels = LabelAssignments.Parse(p.Labels, config.Name, "periodLabel"),
                    });
                }
            }
            else if (hasTable) periods.AddRange(RateTables.PeriodsOf(table, config.Name));
            else
                throw new System.FormatException($"Source '{config.Name}': mode RateTable without periods: set ratePeriods[i].start / " +
                                                 $".rate, or load a table with the columns {RateTables.StartColumn} and {RateTables.RateColumn} (simulean_load_table).");
            var unit = ElementBuildHelpers.SamplerOf(SamplerSettings.Exponential(1.0), clock, config.Name, "rateTable", "rateTable");
            var source = new RateTableSource(config.Name, clock, periods, unit, vElement);
            source.CreateLabels = CreateLabels(config);
            source.ModelSize = ModelSize(config);
            source.DemandFactor = DemandFactor(config);
            source.ArrivalsOnStop = s.ArrivalsOnStop;
            return source;
        }

        private static Element BuildInterArrival(ElementConfig config, SimClock clock, IVElement vElement)
        {
            IRandomSampler tba = ElementBuildHelpers.BuildRandomProcess(config.SettingsAs<SourceSettings>().InterArrivalDistribution,
                                                                        config.Name, "interArrivalDistribution", clock);

            var source = new InterArrivalSource(
                name:             config.Name,
                state:            clock,
                interArrivalTime: tba,
                vElement:         vElement);
            source.CreateLabels = CreateLabels(config);
            source.ModelSize = ModelSize(config);
            source.DemandFactor = DemandFactor(config);
            source.ArrivalsOnStop = config.SettingsAs<SourceSettings>().ArrivalsOnStop;
            return source;
        }
    }

    /// <summary>A rate-table source's table: its columns and how its rows become periods.</summary>
    public static class RateTables
    {
        /// <summary>The columns of a rate table: the period's start (model time) and its rate (arrivals per time unit);
        /// <c>Name</c> names the items; every other column is a label of the period's items.</summary>
        public const string StartColumn = "Start", RateColumn = "Rate";

        /// <summary>The periods of a rate table read from columns (<see cref="StartColumn"/>, <see cref="RateColumn"/>,
        /// optional <c>Name</c>, the other columns labels); a missing column or a value that is not a number fails naming
        /// the row.</summary>
        public static List<RatePeriod> PeriodsOf(Dictionary<string, List<string>> table, string owner)
        {
            List<string> Column(string name)
            {
                if (table.TryGetValue(name, out var c)) return c;
                throw new System.FormatException($"Source '{owner}': its rate table has no column '{name}' (columns: " +
                                                 $"{string.Join(", ", table.Keys)}; the names are case-sensitive)." + NameSuggestions.DidYouMean(name, table.Keys));
            }
            var starts = Column(StartColumn);
            var rates = Column(RateColumn);
            table.TryGetValue("Name", out var names);
            var periods = new List<RatePeriod>();
            for (int i = 0; i < starts.Count; i++)
            {
                double Number(List<string> col, string name)
                {
                    string v = i < col.Count ? col[i] : null;
                    if (v != null && double.TryParse(v.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                                                     System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
                    throw new System.FormatException($"Source '{owner}': rate table row {i + 1}: {name} '{v}' is not a number.");
                }
                var columns = new List<KeyValuePair<string, string>>();
                foreach (var kv in table)
                    if (kv.Key != StartColumn && kv.Key != RateColumn && kv.Key != "Name" && i < kv.Value.Count)
                        columns.Add(new KeyValuePair<string, string>(kv.Key, kv.Value[i]));
                periods.Add(new RatePeriod
                {
                    Start = Number(starts, StartColumn), Rate = Number(rates, RateColumn), Columns = columns,
                    ItemName = names != null && i < names.Count && !string.IsNullOrWhiteSpace(names[i]) ? names[i].Trim() : null,
                });
            }
            return periods;
        }

    }
}
