// Standalone runtime tests for source (generator) statistics: items created inside a source
// leave through the link without entering, so content must stay 0 (current, peak, average).
// Run: dotnet run  (from StandaloneTests/sourcestatstests)
using System;
using System.Collections.Generic;
using System.Linq;
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
        Run("Creation labels are 'name = expression' records: quoted text, numbers, pick, own stream, errors", TestCreateLabels);
        Run("Resource member labels: 'name = expression' evaluated once at build; an unquoted text fails", TestMemberLabels);
        Run("A stopped source: Hold, Pause or Lose its arrivals (time-between-arrivals and schedule)", TestStopPolicies);
        Run("Rate table: the cumulative rate inverted exactly across periods; rate 0 = none; the last rate holds", TestRateTableInversion);
        Run("Rate table: counts per period match the rates; nothing after a closing 0; seeded; labels; demand factor", TestRateTableRuns);
        Run("Rate table: a clinic whose door closes ends at its makespan with stop-when-empty; build errors", TestRateTableClinic);

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

    sealed class TimedCollector : Element
    {
        public readonly List<double> Times = new List<double>();
        private readonly SimClock clock;
        public TimedCollector(SimClock c) : base("Collector", c) { clock = c; }
        public override bool Receive(Item i) { Times.Add(clock.GetSimulationTime()); return true; }
        public override bool CheckAvailability(Item i) => true;
        public override bool Unblock() => false;
        public override int GetQueueLength() => Times.Count;
        public override int GetFreeCapacity() => -1;
    }

    // a source stopped from 25 to 65 (time-between-arrivals 10) or 25 to 45 (schedule 10, 20, 30, 40, 50, 80), run to 99
    static (List<double> times, int lost, bool lostReported) StopRun(bool schedule, SourceStopPolicy policy)
    {
        var clock = new SimClock(1);
        Element source;
        if (schedule)
        {
            var table = new Dictionary<string, List<string>> { ["Time"] = new List<string> { "10", "20", "30", "40", "50", "80" } };
            source = new ScheduleSource("Sched", clock, arrivalsTable: table) { ArrivalsOnStop = policy };
        }
        else source = new InterArrivalSource("Src", clock, new ConstantRandomProcess(10.0)) { ArrivalsOnStop = policy };
        var sink = new TimedCollector(clock);
        GeneralLink.CreateLink(source, new List<Element> { sink });
        source.Start(); sink.Start();
        clock.Schedule(() =>
        {
            var token = source.Stop(new StopRequest(ElementState.STOPPED));
            clock.Schedule(() => source.Resume(token), schedule ? 20 : 40);
        }, 25);
        clock.AdvanceClock(99);
        bool reported = false;
        ((IElementMetrics)source).ReportMetrics((name, value) => reported |= name == SimuLean.Results.ResultMetrics.LostArrivals);
        int lost = schedule ? ((ScheduleSource)source).LostArrivals : ((InterArrivalSource)source).LostArrivals;
        return (sink.Times, lost, reported);
    }

    static void TestStopPolicies()
    {
        string T(List<double> t) => string.Join(",", t);
        // time between arrivals
        var hold = StopRun(false, SourceStopPolicy.Hold);
        Check(T(hold.times) == "10,20,65,75,85,95" && !hold.lostReported,
              $"Hold: the arrival due at 30 waits and leaves at the end of the stop, the next one 10 later ({T(hold.times)})");
        var pause = StopRun(false, SourceStopPolicy.Pause);
        Check(T(pause.times) == "10,20,70,80,90", $"Pause: the 5 left at the stop come after it, nothing waits ({T(pause.times)})");
        var lose = StopRun(false, SourceStopPolicy.Lose);
        Check(T(lose.times) == "10,20,70,80,90" && lose.lost == 4 && lose.lostReported,
              $"Lose: the arrivals at 30..60 are lost (4) and the clock goes on ({T(lose.times)}, lost {lose.lost})");
        // schedule
        var sHold = StopRun(true, SourceStopPolicy.Hold);
        Check(T(sHold.times) == "10,20,45,45,50,80", $"Schedule, Hold: the rows of 30 and 40 pile up and leave at 45 ({T(sHold.times)})");
        var sPause = StopRun(true, SourceStopPolicy.Pause);
        Check(T(sPause.times) == "10,20,50,60,70", $"Schedule, Pause: the rest of the timetable is 20 later (80 -> 100) ({T(sPause.times)})");
        var sLose = StopRun(true, SourceStopPolicy.Lose);
        Check(T(sLose.times) == "10,20,50,80" && sLose.lost == 2, $"Schedule, Lose: the rows of 30 and 40 are lost ({T(sLose.times)}, lost {sLose.lost})");

        // built from a config: the key, its default and a bad value
        var cfg = new SimuLean.Serialization.SimulationConfig();
        var e = new SimuLean.Serialization.ElementConfig { Id = "src", Name = "Src", Type = "Source" };
        e.Set("mode", "InterArrival");
        e.Set("interArrivalDistribution.strategyType", "Constant");
        e.Set("arrivalsOnStop", "lose");
        cfg.Elements.Add(e);
        var built = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
        built.BuildModel(cfg);
        Check(((InterArrivalSource)built.Find("Src")).ArrivalsOnStop == SourceStopPolicy.Lose, "arrivalsOnStop read from the config (any case)");
        e.Set("arrivalsOnStop", "Hold");
        var offered = SimuLean.Optimization.ModelParameterCatalog.ForElement(e).FirstOrDefault(p => p.Key == "arrivalsOnStop");
        Check(offered != null && offered.Kind == SimuLean.Optimization.VariableKind.Choice && offered.Options.Count == 3,
              "arrivalsOnStop is a study factor (a choice of Hold, Pause, Lose)");
        var heldRun = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
        heldRun.BuildModel(cfg);
        var results = SimuLean.Results.RunResults.Capture(heldRun);
        double lostRead = SimuLean.Optimization.MetricCatalog.Read(
            new SimuLean.Optimization.ModelMetric("lost", SimuLean.Optimization.MetricScope.Element, "Src", SimuLean.Results.ResultMetrics.LostArrivals), results);
        Check(lostRead == 0, "a study reads lost_arrivals as 0 on a source that holds its arrivals (arrivalsOnStop may vary between scenarios)");
        Exception error = null;
        try { e.Set("arrivalsOnStop", "Drop"); new SimuLean.Headless.HeadlessModelFactory(new SimClock(1)).BuildModel(cfg); } catch (Exception ex) { error = ex; }
        Check(error != null && error.ToString().Contains("Hold, Pause or Lose"), "an unknown arrivalsOnStop fails the build naming the options: " + error?.Message);
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
        source.CreateLabels = LabelAssignments.Parse(new[] { "prio = 7", "color = 99" }, "Sched", "createLabel");   // the table column overrides color
        var sink = new Collector(clock);
        GeneralLink.CreateLink(source, new List<Element> { sink });
        source.Start(); sink.Start();
        clock.AdvanceClock(10);

        Check(sink.Got.Count == 3, $"3 items ({sink.Got.Count})");
        for (int i = 0; i < sink.Got.Count && i < 3; i++)
        {
            var it = sink.Got[i];
            Check(it.Name == "ABC"[i].ToString(), $"item {i} name {it.Name}");
            Near(it.GetLabelValue("color") ?? double.NaN, 10 * (i + 1), $"item {i} color");
            Near(it.GetLabelValue("prio") ?? double.NaN, 7, $"item {i} assigned prio");
            Near(it.GetCreationTime(), i + 1, $"item {i} creation time");
        }
    }

    // a source built by its handler (the one build path), run for `until`, its items collected
    static List<Item> Created(int seed, double until, params string[] records)
    {
        var cfg = new SimuLean.Serialization.ElementConfig("src", "Source", "Src");
        cfg.Set("mode", "InterArrival");
        cfg.Set("interArrivalDistribution.strategyType", "Exponential");
        cfg.Set("createLabels", new List<string>(records));
        var clock = new SimClock(seed);
        SimuLean.Serialization.ElementHandlerRegistry.CreateDefault().TryGetHandler("Source", out var handler);
        var src = (Element)handler.Build(cfg, clock, null);
        var sink = new Collector(clock);
        GeneralLink.CreateLink(src, new List<Element> { sink });
        src.Start(); sink.Start();
        clock.AdvanceClock(until);
        return sink.Got;
    }

    static void TestCreateLabels()
    {
        var a = Created(7, 50, "sku = '007'", "n = 007", "kind = pick(0.5, 'A', 0.5, 'B')", "u = rand()", "", "due = now() + 480", "serial = id");
        Check(a.Count > 10, $"items made ({a.Count})");
        Check(a.TrueForAll(i => i.GetLabelText("sku") == "007" && i.GetLabelValue("sku") == null), "a quoted constant stays text ('007')");
        Check(a.TrueForAll(i => i.GetLabelValue("n") == 7), "an unquoted number is a number (007 → 7)");
        Check(a.TrueForAll(i => i.GetLabelText("kind") == "A" || i.GetLabelText("kind") == "B")
              && a.Exists(i => i.GetLabelText("kind") == "A") && a.Exists(i => i.GetLabelText("kind") == "B"), "pick draws texts");
        Check(a.TrueForAll(i => Math.Abs((i.GetLabelValue("due") ?? 0) - (i.GetCreationTime() + 480)) < 1e-9), "now() is the creation time");
        Check(a.TrueForAll(i => i.GetLabelValue("serial") == i.GetId()), "a creation label reads the item's id");
        var b = Created(7, 50, "sku = '007'", "n = 007", "kind = pick(0.5, 'A', 0.5, 'B')", "u = rand()", "", "due = now() + 480", "serial = id");
        Check(a.Count == b.Count && a.Select((i, k) => i.GetLabelValue("u") == b[k].GetLabelValue("u") && i.GetLabelText("kind") == b[k].GetLabelText("kind")).All(x => x),
              "reproducible under a seed");
        // own stream: random labels leave the arrivals as they were (constants draw nothing either)
        var plain = Created(7, 50);
        var constant = Created(7, 50, "prio = 5");
        Check(plain.Count == a.Count && plain.Select((i, k) => i.GetCreationTime() == a[k].GetCreationTime() && i.GetCreationTime() == constant[k].GetCreationTime()).All(x => x),
              "the labels draw from their own streams: the arrival times are unchanged");
        var c = Created(8, 50, "u = rand()");
        Check(c.Count == 0 || a.Count == 0 || c[0].GetLabelValue("u") != a[0].GetLabelValue("u"), "another seed, other draws");
        // bad records fail the build naming the source
        foreach (var bad in new[] { "prio", "= 5", "prio == 5", "sku = 'A" })
        {
            string msg = null;
            try { Created(1, 1, bad); } catch (FormatException ex) { msg = ex.Message; }
            Check(msg != null && msg.Contains("'Src'"), $"'{bad}' fails naming the source ({msg ?? "no error"})");
        }
    }

    static void TestMemberLabels()
    {
        var c = new SimuLean.Serialization.SimulationConfig();
        var t = new SimuLean.Serialization.ResourceTeamConfig { Id = "crew", Name = "crew" };
        t.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "W1", Properties = new List<string> { "skill = 'welding'", "level = 3", "rate = level * 2", "code = '007'" } });
        c.ResourceTeams.Add(t);
        var factory = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1), enableLogging: false);
        factory.BuildModel(c);
        var w = factory.Executors.First(m => m.Name == "W1");
        Check(w.GetPropertyText("skill") == "welding" && w.GetProperty("level") == 3 && w.GetProperty("rate") == 6 && w.GetPropertyText("code") == "007",
              "quoted text, numbers, a record reading the one before it");
        t.Members[0].Properties = new List<string> { "skill = welding" };
        string msg = null;
        try { new SimuLean.Headless.HeadlessModelFactory(new SimClock(1), enableLogging: false).BuildModel(c); } catch (Exception ex) { msg = ex.Message; }
        Check(msg != null && msg.Contains("W1") && msg.Contains("quote"), $"an unquoted text fails naming the member ({msg ?? "no error"})");
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

    // ── Rate table ─────────────────────────────────────────

    sealed class One : SimuLean.IRandomSampler { public double NextValue() => 1; }

    static void TestRateTableInversion()
    {
        var clock = new SimClock(1);
        var periods = new List<RatePeriod>
        {
            new RatePeriod { Start = 5, Rate = 2 }, new RatePeriod { Start = 10, Rate = 0 }, new RatePeriod { Start = 20, Rate = 1 },
        };
        var src = new RateTableSource("r", clock, periods, new One());
        Near(src.RateAt(0), 0, "rate before the first period");
        Near(src.RateAt(7), 2, "rate in the first period");
        Near(src.RateAt(15), 0, "rate in the closed period");
        Near(src.RateAt(1000), 1, "the last rate holds");
        Near(src.Mass(0, 30), 2 * 5 + 0 + 10, "the cumulative rate over three periods");
        Near(src.TimeAfter(0, 1), 5.5, "starts at the first period");
        Near(src.TimeAfter(9, 4), 22, "crosses the closed period: 2 in [9, 10), the rest from 20 at rate 1");
        Near(src.TimeAfter(src.TimeAfter(0, 3), 3), src.TimeAfter(0, 6), "inversion is additive");
        src.DemandFactor = 2;
        Near(src.TimeAfter(5, 1), 5.25, "the demand factor multiplies the rate");
        var closing = new RateTableSource("c", clock, new List<RatePeriod> { new RatePeriod { Start = 0, Rate = 1 }, new RatePeriod { Start = 3, Rate = 0 } }, new One());
        Check(double.IsPositiveInfinity(closing.TimeAfter(2.5, 1)), "rate 0 to the end: no next arrival (+inf)");
    }

    static SimuLean.Serialization.SimulationConfig RateModel(Action<SimuLean.Serialization.ElementConfig> set, double serviceTime = 0, double horizon = 1000)
    {
        var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 7, MaxSimulationTime = horizon };
        var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
        src.Set("mode", "RateTable");
        set(src);
        c.Elements.Add(src);
        var q = new SimuLean.Serialization.ElementConfig("q", "Queue", "q"); q.Set("capacity", 10000); c.Elements.Add(q);
        var s = new SimuLean.Serialization.ElementConfig("doc", "MultiServer", "doc");
        s.Set("capacity", 1); s.Set("delayStrategy", SimuLean.Serialization.SamplerSettings.Constant(serviceTime)); c.Elements.Add(s);
        c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink"));
        c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "q", "General"));
        c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("q", "doc", "General"));
        c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("doc", "sink", "General"));
        return c;
    }

    sealed class Times : IVElement
    {
        public readonly List<double> At = new List<double>();
        public readonly List<string> Kinds = new List<string>();
        public SimClock Clock;
        public void ReportState(string msg) { }
        public object GenerateItem(int type) { At.Add(Clock.GetSimulationTime()); return null; }
        public void LoadItem(Item item) { }
        public void UnloadItem(Item item) { }
    }

    static (SimuLean.Results.RunResults r, SimuLean.Headless.HeadlessModelFactory model) RunRate(SimuLean.Serialization.SimulationConfig c, double until)
    {
        var clock = new SimClock(c.Seed);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
        clock.AdvanceClock(until);
        return (SimuLean.Results.RunResults.Capture(model), model);
    }

    static void TestRateTableRuns()
    {
        var periods = new List<SimuLean.Serialization.RatePeriodSettings>
        {
            SimuLean.Serialization.RatePeriodSettings.Of(0, 1.0, "shift = 'day'"),
            SimuLean.Serialization.RatePeriodSettings.Of(2000, 0.2, "shift = 'night'"),
            SimuLean.Serialization.RatePeriodSettings.Of(4000, 0.0),
        };
        var c = RateModel(e => e.Set("ratePeriods", periods), horizon: 10000);
        var (r, model) = RunRate(c, 10000);
        var sink = (Sink)model.Find("sink");
        var src = (RateTableSource)model.Find("src");
        int n = src.GetNumberItems();
        double expected = 2000 * 1.0 + 2000 * 0.2;
        Check(Math.Abs(n - expected) < 4 * Math.Sqrt(expected), $"arrivals {n} ≈ {expected} (Poisson, 4 sd)");
        Check(sink.GetStatsInputCount() == n, "every arrival reached the sink");
        Check(sink.StatsCollector.LastEntryTime < 4000, $"nothing after the rate drops to 0 for good (last entry {sink.StatsCollector.LastEntryTime})");
        Check(r.Find(SimuLean.Results.ResultScope.Element, "src")[SimuLean.Results.ResultMetrics.Output] == n, "the results count the source's output");

        // counts per period, from the arrival times (a view records each creation)
        var view = new Times();
        var clock = new SimClock(7); view.Clock = clock;
        var m = new SimuLean.Headless.HeadlessModelFactory(clock, false, null, ec => ec.Id == "src" ? view : null);
        m.BuildModel(RateModel(e => e.Set("ratePeriods", periods), horizon: 10000));
        m.StartModel(); clock.AdvanceClock(10000);
        int day = view.At.Count(t => t < 2000), night = view.At.Count(t => t >= 2000 && t < 4000);
        Check(Math.Abs(day - 2000) < 4 * Math.Sqrt(2000) && Math.Abs(night - 400) < 4 * Math.Sqrt(400), $"per period: day {day} ≈ 2000, night {night} ≈ 400");
        Check(view.At.Count == n, "the same seed gives the same arrivals (scene view and headless agree)");

        // labels: the period's records, and a table's columns
        var labelled = RateModel(e => e.Set("ratePeriods", periods), horizon: 10000);
        var clock2 = new SimClock(7);
        var m2 = new SimuLean.Headless.HeadlessModelFactory(clock2);
        m2.BuildModel(labelled);
        var shifts = new List<string>();
        ((Sink)m2.Find("sink")).ItemEntered += (el, it) => shifts.Add(it.GetLabelText("shift"));
        m2.StartModel(); clock2.AdvanceClock(10000);
        Check(shifts.Count == n && shifts.Take(day).All(s => s == "day") && shifts.Skip(day).All(s => s == "night"), "each item gets its period's labels");

        var table = new Dictionary<string, List<string>>
        {
            ["Start"] = new List<string> { "0", "50" }, ["Rate"] = new List<string> { "0.5", "0" },
            ["Name"] = new List<string> { "walkin", "" }, ["kind"] = new List<string> { "A", "B" },
        };
        var (rt, mt) = RunRate(RateModel(e => e.Set("arrivalsTable", table), horizon: 1000), 1000);
        var tsrc = (RateTableSource)mt.Find("src");
        Check(tsrc.Periods.Count == 2 && tsrc.Periods[0].ItemName == "walkin" && tsrc.Periods[0].Columns.Any(kv => kv.Key == "kind" && kv.Value == "A"),
              "a table's Start / Rate / Name and its other columns as labels");
        Check(((Sink)mt.Find("sink")).StatsCollector.LastEntryTime < 50, "a table's closing 0");

        // the demand factor doubles the rate; a different seed changes the draws
        var (r2, m3) = RunRate(RateModel(e => { e.Set("ratePeriods", periods); e.Set("demandFactor", 2.0); }, horizon: 10000), 10000);
        int n2 = ((RateTableSource)m3.Find("src")).GetNumberItems();
        Check(Math.Abs(n2 - 2 * expected) < 4 * Math.Sqrt(2 * expected), $"demand factor 2: {n2} ≈ {2 * expected}");
    }

    static void TestRateTableClinic()
    {
        // a clinic: walk-ins at 0.2 per minute for 4 hours, then the door closes; one doctor, 4 minutes each
        var c = RateModel(e => e.Set("ratePeriods", new List<SimuLean.Serialization.RatePeriodSettings>
        {
            SimuLean.Serialization.RatePeriodSettings.Of(0, 0.2), SimuLean.Serialization.RatePeriodSettings.Of(240, 0),
        }), serviceTime: 4, horizon: 100000);
        c.StopWhenEmpty = true;
        var (r, model) = RunRate(c, 100000);
        double lastOut = ((Sink)model.Find("sink")).StatsCollector.LastEntryTime;
        Check(r.Info.EndReason == "Empty", $"the run ends because nothing is left to do ({r.Info.EndReason})");
        Check(r.Info.EndTime < 1000 && Math.Abs(r.Info.EndTime - lastOut) < 1e-9, $"it ends at the last exit (t = {r.Info.EndTime}, last out {lastOut})");
        var mk = r.Find(SimuLean.Results.ResultScope.Model, "model");
        Check(Math.Abs(mk[SimuLean.Results.ResultMetrics.Makespan] - lastOut) < 1e-9, "makespan = the last exit");

        string Err(Action<SimuLean.Serialization.ElementConfig> set)
        {
            try { RunRate(RateModel(set), 10); return null; } catch (FormatException e) { return e.Message; }
        }
        string e1 = Err(e => e.Set("ratePeriods", new List<SimuLean.Serialization.RatePeriodSettings>
            { SimuLean.Serialization.RatePeriodSettings.Of(10, 1), SimuLean.Serialization.RatePeriodSettings.Of(5, 1) }));
        Check(e1 != null && e1.Contains("must increase"), $"decreasing starts fail ({e1})");
        string e2 = Err(e => e.Set("ratePeriods", new List<SimuLean.Serialization.RatePeriodSettings> { SimuLean.Serialization.RatePeriodSettings.Of(0, -1) }));
        Check(e2 != null && e2.Contains("rate -1"), $"a negative rate fails ({e2})");
        string e3 = Err(e => e.Set("arrivalsTable", new Dictionary<string, List<string>> { ["start"] = new List<string> { "0" }, ["Rate"] = new List<string> { "1" } }));
        Check(e3 != null && e3.Contains("no column 'Start'") && e3.Contains("start"), $"a missing column fails with a did-you-mean ({e3})");
        string e4 = Err(e => { });
        Check(e4 != null && e4.Contains("ratePeriods"), $"no periods fails saying what to set ({e4})");
    }
}
