// Standalone runtime tests for source (generator) statistics: items created inside a source
// leave through the link without entering, so content must stay 0 (current, peak, average).
// Run: dotnet run  (from StandaloneTests/sourcestatstests)
using System;
using System.Collections.Generic;
using SimuLean;
using SimuLean.Stochastics.Distributions;

static class Program
{
    static int passed, failed;

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    static void Near(double actual, double expected, string what, double tol = 1e-6)
        => Check(Math.Abs(actual - expected) <= tol, $"{what}: expected {expected}, got {actual}");

    static int Main()
    {
        Run("InterArrivalSource: content stays 0, output counted", TestInterArrival);
        Run("ReleaseSource: content stays 0, output counted", TestRelease);
        Run("Start resets source statistics", TestStartResets);
        Run("ScheduleSource: each item takes its own row's name and labels", TestScheduleRows);

        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    sealed class Collector : Element
    {
        public readonly List<Item> Got = new List<Item>();
        public Collector(SimClock c) : base("Collector", c) { }
        public override bool Receive(Item i) { Got.Add(i); return true; }
        public override bool CheckAvailability(Item i) => true;
        public override bool Unblock() => false;
        public override int GetQueueLength() => Got.Count;
        public override int GetFreeCapacity() => -1;
    }

    static void TestScheduleRows()
    {
        var clock = new SimClock();
        var table = new Dictionary<string, List<string>>
        {
            ["Time"]  = new List<string> { "1", "2", "3" },
            ["Name"]  = new List<string> { "A", "B", "C" },
            ["color"] = new List<string> { "10", "20", "30" },
        };
        var source = new ScheduleSource("Sched", clock, arrivalsTable: table);
        source.AssignedLabels["prio"] = "7";
        source.AssignedLabels["color"] = "99";   // the table column overrides it
        var sink = new Collector(clock);
        GeneralLink.CreateLink(source, new List<Element> { sink });
        source.Start(); sink.Start();
        clock.AdvanceClock(10);

        Check(sink.Got.Count == 3, $"3 items ({sink.Got.Count})");
        for (int i = 0; i < sink.Got.Count && i < 3; i++)
        {
            var it = sink.Got[i];
            Check(it.GetTypeName() == "ABC"[i].ToString(), $"item {i} name {it.GetTypeName()}");
            Near(it.GetLabelValue("color") ?? double.NaN, 10 * (i + 1), $"item {i} color");
            Near(it.GetLabelValue("prio") ?? double.NaN, 7, $"item {i} assigned prio");
            Near(it.GetCreationTime(), i + 1, $"item {i} creation time");
        }
    }

    static void CheckZeroContent(Element e, string who)
    {
        Near(e.GetStatsContentCurrent(), 0, who + " content current");
        Near(e.GetStatsContentMax(),     0, who + " content peak");
        Near(e.GetStatsContentAvgTW(),   0, who + " content average");
    }

    static void TestInterArrival()
    {
        var clock = new SimClock();
        var source = new InterArrivalSource("Src", clock, new ConstantRandomProcess(1.0));
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(source, new List<Element> { sink });
        source.Start(); sink.Start();
        clock.AdvanceClock(10.5);

        CheckZeroContent(source, "source");
        Near(source.GetStatsOutputCount(), 10, "source output");
        Near(source.GetStatsInputCount(), 0, "source input");
        // Non-generators keep tracking content.
        Near(sink.GetStatsInputCount(), 10, "sink input");
        Check(sink.GetStatsContentMax() > 0, "sink content peak > 0");
    }

    static void TestRelease()
    {
        var clock = new SimClock();
        var source = new ReleaseSource("Rel", clock);
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(source, new List<Element> { sink });
        source.Start(); sink.Start();
        for (int i = 1; i <= 5; i++)
        {
            var it = new Item(0);
            it.SetId("A", i, 0);
            source.Enqueue(it);
        }
        clock.AdvanceClock(5);

        CheckZeroContent(source, "release source");
        Near(source.GetStatsOutputCount(), 5, "release source output");
        Near(sink.GetStatsInputCount(), 5, "sink input");
    }

    static void TestStartResets()
    {
        var clock = new SimClock();
        var source = new InterArrivalSource("Src", clock, new ConstantRandomProcess(1.0));
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(source, new List<Element> { sink });
        source.Start(); sink.Start();
        clock.AdvanceClock(5.5);
        Check(source.GetStatsOutputCount() > 0, "output before restart");
        source.Start();
        Near(source.GetStatsOutputCount(), 0, "output after Start");
        CheckZeroContent(source, "source after Start");
    }
}
