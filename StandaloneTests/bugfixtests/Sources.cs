// Sources: a restarted source, the demand factor, the backlog order of a schedule source.
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Serialization;

static partial class Program
{
    static void TestScheduleBacklogOrder()
    {
        // A1, A2 at 0 and B1 at 1 for a station that takes 1 each: A2 waits in the backlog; at t = 1 the station frees and
        // B1 arrives at the same time — A2 must go first.
        var clock = new SimClock(1);
        var table = new Dictionary<string, List<string>>
        {
            ["Time"] = new List<string> { "0", "1", "2" }, ["Name"] = new List<string> { "A", "B", "C" }, ["Q"] = new List<string> { "2", "1", "1" },
        };
        var src = new ScheduleSource("S", clock, table);
        var m = new MultiServer(new[] { Const(1) }, "M", clock);
        var outp = new Collector(clock);
        GeneralLink.CreateLink(src, L(m)); GeneralLink.CreateLink(m, L(outp));
        foreach (var e in new Element[] { src, m, outp }) e.Start();
        clock.AdvanceClock(10);
        string order = string.Join(",", outp.Got.Select(i => i.Name + i.GetId()));
        Check(order == "A1,A2,B3,C4", "served in arrival order: " + order);

        // A receiver that opens without telling the source (a notification still pending): A1 waits in the backlog, the
        // receiver opens at 1.5, B2 arrives at 2 — the backlog is offered first.
        var c2 = new SimClock(1);
        var t2 = new Dictionary<string, List<string>>
        {
            ["Time"] = new List<string> { "0", "2" }, ["Name"] = new List<string> { "A", "B" }, ["Q"] = new List<string> { "1", "1" },
        };
        var s2 = new ScheduleSource("S", c2, t2);
        var gate = new Opening(c2);
        GeneralLink.CreateLink(s2, L(gate));
        foreach (var e in new Element[] { s2, gate }) e.Start();
        Schedule(c2, 1.5, () => gate.Open = true);
        c2.AdvanceClock(10);
        string order2 = string.Join(",", gate.Got.Select(i => i.Name + i.GetId()));
        Check(order2 == "A1,B2", "the backlog goes before a new arrival: " + order2);
    }

    /// <summary>Takes items only once opened, and does not tell its origins when it opens.</summary>
    sealed class Opening : Element
    {
        public readonly List<Item> Got = new List<Item>();
        public bool Open;
        public Opening(SimClock c) : base("Opening", c) { }
        public override bool Receive(Item i) { Got.Add(i); return true; }
        public override bool CheckAvailability(Item i) => Open;
        public override bool Unblock() => false;
        public override int GetQueueLength() => Got.Count;
        public override int GetFreeCapacity() => Open ? -1 : 0;
    }

    // 100 referrals of one (a row each, every 1) — factor 1.2 gives 120, 0.5 gives 50, at the rows' times;
    // an inter-arrival source every 1 with factor 2 gives about 200 in 100; a factor of 0 fails.
    static void TestDemandFactor()
    {
        int Count(string mode, double? factor)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", mode);
            if (mode == "Schedule")
            {
                var times = Enumerable.Range(0, 100).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
                src.Set("arrivalsTable", new Dictionary<string, List<string>>
                {
                    ["Time"] = times, ["Name"] = times.Select(_ => "p").ToList(), ["Q"] = times.Select(_ => "1").ToList(),
                });
            }
            else
            {
                src.Set("interArrivalDistribution.strategyType", "Constant");
                src.Set("interArrivalDistribution.constantValue", 1.0);
            }
            if (factor.HasValue) src.Set("demandFactor", factor.Value);
            c.Elements.Add(src);
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "sink", "General"));
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            clock.AdvanceClock(99.999);
            return (int)model.Find("sink").GetStatsInputCount();
        }
        Check(Count("Schedule", null) == 100, "no factor: the table as it is");
        Check(Count("Schedule", 1.2) == 120, $"1.2: 120 ({Count("Schedule", 1.2)})");
        Check(Count("Schedule", 0.5) == 50, $"0.5: 50 ({Count("Schedule", 0.5)})");
        int fast = Count("InterArrival", 2.0), plain = Count("InterArrival", null);
        Check(Math.Abs(fast - 2 * plain) <= 1, $"twice the rate: {fast} vs {plain}");
        bool failed = false;
        try { Count("Schedule", 0.0); } catch (FormatException) { failed = true; }
        Check(failed, "a factor of 0 fails the build");
    }

    static void TestSourceRestartForgetsHeldItem()
    {
        var clock = new SimClock(1);
        var src = new InterArrivalSource("Src", clock, Const(10));
        var outp = new Collector(clock) { Open = false };
        GeneralLink.CreateLink(src, L(outp));
        src.Start(); outp.Start();
        clock.AdvanceClock(15);              // the item of t = 10 is held: the output is closed
        src.Start();                         // restart: a new run on the same clock
        outp.OpenNow();                      // room downstream: nothing of the old run may come out
        Check(outp.Got.Count == 0, $"no item of the previous run is sent ({outp.Got.Count})");
    }
}
