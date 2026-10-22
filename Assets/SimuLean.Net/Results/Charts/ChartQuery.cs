using System;
using System.Collections.Generic;

namespace SimuLean.Results.Charts
{
    /// <summary>The kinds of chart.</summary>
    public enum ChartKind
    {
        /// <summary>Mean per category with its confidence interval across replications.</summary>
        Bar,
        /// <summary>Distribution per category across replications: min, quartiles, median, max.</summary>
        Box,
        /// <summary>Counts of the values in bins.</summary>
        Histogram,
        /// <summary>One point per run: X against Y (e.g. a parameter against a metric).</summary>
        Scatter,
        /// <summary>A recorded time series: one line per run and object.</summary>
        Line,
        /// <summary>Share of time per state, stacked per object.</summary>
        States,
        /// <summary>Stacked columns (e.g. per period of a live run), absolute or each column to 100 % (<see cref="ChartData.Normalise"/>).
        /// Drawn from data built in code (the element inspector, dashboards), not from a query.</summary>
        Stacked,
        /// <summary>A Gantt of one run: a row per object, its state intervals over time in the states' colours (needs the
        /// run's timeline: Record timeline).</summary>
        Gantt,
        /// <summary>A job Gantt of one run: a row per item, where it was over time (a colour per element; needs Record timeline).</summary>
        JobGantt,
        /// <summary>The first series as a filled area, the others as lines, one column per category (e.g. content: mean and max
        /// per time bucket). Drawn from data built in code, not from a query.</summary>
        Area,
    }

    /// <summary>What a value is read from.</summary>
    public enum ChartSource
    {
        /// <summary>A named metric of the runs (the <c>metrics</c> table).</summary>
        Metric,
        /// <summary>A statistic of objects (the <c>results</c> table): scope, objects, statistic name.</summary>
        Statistic,
        /// <summary>A scenario parameter, read as a number (the <c>scenarios</c> table).</summary>
        Parameter,
    }

    /// <summary>What the categories (bars, boxes, stacks) of a chart are.</summary>
    public enum ChartGroup { Scenario, Object, Run }

    /// <summary>One quantity of a chart.</summary>
    [Serializable]
    public sealed class ChartValue
    {
        public ChartSource Source = ChartSource.Statistic;
        /// <summary>Metric, statistic (a <see cref="ResultMetrics"/> name) or parameter name.</summary>
        public string Name = "";
        /// <summary>Statistic only: the object kind (<c>element</c>, <c>resource</c>, … — a <see cref="ResultScope"/> in lower case).</summary>
        public string Scope = "element";
        /// <summary>Statistic only: the objects, as names or <c>*</c> patterns separated by <c>;</c> (empty = all).</summary>
        public string Objects = "";

        public ChartValue() { }
        public ChartValue(ChartSource source, string name, string scope = "element", string objects = "")
        {
            Source = source; Name = name ?? ""; Scope = scope ?? "element"; Objects = objects ?? "";
        }

        public string Label => Source == ChartSource.Statistic && !string.IsNullOrEmpty(Objects) && !Objects.Contains("*") && !Objects.Contains(";")
            ? $"{Objects} {Name}" : Name;
    }

    /// <summary>
    /// A chart's definition, independent of Unity: the kind, the value(s) to plot, the categories, filters on
    /// scenarios — <see cref="ChartBuilder"/> turns it and a result set's long tables into <see cref="ChartData"/>, which
    /// <see cref="ChartRenderer"/> draws. Unity keeps it in a <c>ChartDefinition</c> asset.
    /// </summary>
    [Serializable]
    public sealed class ChartQuery
    {
        public string Title = "";
        public ChartKind Kind = ChartKind.Bar;
        /// <summary>The value plotted (Y; the binned value of a histogram; the series statistic of a line chart).</summary>
        public ChartValue Y = new ChartValue();
        /// <summary>Scatter only: the X value.</summary>
        public ChartValue X = new ChartValue(ChartSource.Parameter, "");
        /// <summary>Bar / box: the categories; the other dimension (objects or scenarios), when several, makes the series.</summary>
        public ChartGroup GroupBy = ChartGroup.Scenario;
        /// <summary>Scenarios kept, as names or <c>*</c> patterns separated by <c>;</c> (empty = all).</summary>
        public string Scenarios = "";
        /// <summary>Histogram: the number of bins (0 = Sturges' rule).</summary>
        public int Bins;
        /// <summary>Confidence level of the bar intervals.</summary>
        public double Confidence = 0.95;
        /// <summary>Bar: write the mean (± the half-width) above each bar.</summary>
        public bool ValueLabels;
        /// <summary>Gantt: the replication shown (of the first scenario the filter keeps; 0 = the first one).</summary>
        public int Replication;
        /// <summary>Gantt: the time window (both 0 = the whole run).</summary>
        public double From, To;
    }

    /// <summary>Quartiles of a box.</summary>
    public struct BoxStats
    {
        public int N;
        public double Min, Q1, Median, Q3, Max;
    }

    /// <summary>One series of a chart: a colour in the legend.</summary>
    public sealed class ChartSeries
    {
        public string Name = "";
        /// <summary>A text shown with the series where the view lists them (e.g. the inspector's bars: "42 % · 3 h").</summary>
        public string Note;
        /// <summary>A fixed colour (0xRRGGBB, e.g. a state's) or −1 = the palette by series index.</summary>
        public int Color = -1;
        /// <summary>Bar / States: one per category (NaN = none). Bar intervals in <see cref="Low"/> / <see cref="High"/>.</summary>
        public double[] Values = new double[0];
        public double[] Low = new double[0];
        public double[] High = new double[0];
        /// <summary>Box: one per category (N = 0: none).</summary>
        public BoxStats[] Boxes = new BoxStats[0];
        /// <summary>Scatter / Line: the points; Histogram: (bin start, count).</summary>
        public List<(double x, double y)> Points = new List<(double, double)>();
    }

    /// <summary>A Gantt bar: <see cref="Row"/> (a category) in <see cref="Series"/> (a state) from <see cref="Start"/> to <see cref="End"/>.</summary>
    public struct GanttSegment
    {
        public int Row, Series;
        public double Start, End;
        public GanttSegment(int row, int series, double start, double end) { Row = row; Series = series; Start = start; End = end; }
    }

    /// <summary>What <see cref="ChartRenderer"/> draws: categories and series, the axis titles, any message.</summary>
    public sealed class ChartData
    {
        public ChartKind Kind;
        public string Title = "";
        /// <summary>A line under the title (what the values are: "mean ± 95 % CI over 4 replications").</summary>
        public string Subtitle;
        public string XLabel = "";
        public string YLabel = "";
        public List<string> Categories = new List<string>();
        public List<ChartSeries> Series = new List<ChartSeries>();
        /// <summary>Histogram: the width of a bin.</summary>
        public double BinWidth;
        /// <summary>Stacked: each column to 100 %.</summary>
        public bool Normalise;
        /// <summary>The value unit shown with the axis maximum (e.g. <c>%</c>, <c>items</c>).</summary>
        public string Unit = "";
        /// <summary>Columns too many to label one by one (time buckets): the labels under the first, middle and last column.</summary>
        public string XStart, XCenter, XEnd;
        /// <summary>Bar: the mean written above each bar (and ± the half-width).</summary>
        public bool ValueLabels;
        /// <summary>Gantt: the bars (rows = <see cref="Categories"/>, series = states) and the time range shown.</summary>
        public List<GanttSegment> Segments = new List<GanttSegment>();
        public double TimeFrom, TimeTo;
        /// <summary>Why the chart is empty (no data, a name not found…); null when there is something to draw.</summary>
        public string Message;

        public bool IsEmpty => Message != null;
    }
}
