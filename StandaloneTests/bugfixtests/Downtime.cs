// Downtime and calendar: stops and resumes, timetables, shifts, holidays, warm-up, and statistics under a stop.
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Downtime;
using SimuLean.Serialization;

static partial class Program
{
    /// <summary>Bare element to drive ScheduleWork / Stop directly.</summary>
    sealed class Bare : Element
    {
        public Bare(SimClock c) : base("Bare", c) { }
        public override bool Receive(Item i) => false;
        public override bool CheckAvailability(Item i) => false;
        public override bool Unblock() => false;
        public override int GetQueueLength() => 0;
        public override int GetFreeCapacity() => 0;
    }

    sealed class Nop : IEvent { public void Execute() { } }

    static void TestUtilizationUnderStop()
    {
        // Two slots always busy (a source with no gap), an Immediate stop from 20 to 50 of a 100 run: busy 70 % (less the first arrivals, 0.01 apart).
        {
            var clock = new SimClock(1);
            var src = new InterArrivalSource("Src", clock, Const(0.01));
            var m = new MultiServer(new IRandomSampler[] { Const(5), Const(5) }, "M", clock);
            var sink = new Sink("Sink", clock);
            GeneralLink.CreateLink(src, L(m)); GeneralLink.CreateLink(m, L(sink));
            foreach (var e in new Element[] { src, m, sink }) e.Start();
            StopToken stop = null;
            bool pausedSlotProcessing = false;
            Schedule(clock, 20, () => stop = m.Stop(new StopRequest(ElementState.STOPPED)));
            Schedule(clock, 30, () => pausedSlotProcessing = m.Slots.All(sl => sl.GetState() == ElementState.PROCESSING));
            Schedule(clock, 50, () => m.Resume(stop));
            clock.AdvanceClock(100);
            Near(m.GetUtilization(), 0.7, "Multi-Server utilisation without the stopped 30 %", 1e-3);
            Near(m.GetUtilization(), m.GetStateRatio(ElementState.PROCESSING), "the slots' PROCESSING share = the element's (all slots busy)", 1e-3);
            Near(m.GetSlotStateRatio(0, ElementState.STOPPED), 0.3, "a slot accounts the stop state", 1e-3);
            Check(pausedSlotProcessing, "the paused slots keep their own state (PROCESSING) for the station's logic");
        }
        // A Combiner with two assemblies, its parts always there.
        {
            var clock = new SimClock(1);
            var main = new InterArrivalSource("Main", clock, Const(0.01));
            var part = new InterArrivalSource("Part", clock, Const(0.01));
            var comb = new Combiner(new[] { 1 }, Const(5), "Comb", clock, capacity: 2);
            var sink = new Sink("Sink", clock);
            GeneralLink.CreateLink(main, L(comb)); GeneralLink.CreateLink(part, L(comb.GetComponentInput(0)));
            GeneralLink.CreateLink(comb, L(sink));
            foreach (var e in new Element[] { main, part, comb, sink }) e.Start();
            StopToken stop = null;
            Schedule(clock, 20, () => stop = comb.Stop(new StopRequest(ElementState.STOPPED)));
            Schedule(clock, 50, () => comb.Resume(stop));
            clock.AdvanceClock(100);
            Near(comb.GetUtilization(), 0.7, "Combiner (2 assemblies) utilisation without the stopped 30 %", 1e-3);
        }
    }

    // ── #8 ───────────────────────────────────────────────────────────────────

    static void TestAssemblyUnblockDuringStop()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 1, 1);
        var spec = new AssemblyTaskSpec { Id = "t1", Duration = Const(1) };
        var asy = new AssemblyStation("Asy", clock, new List<AssemblyTaskSpec> { spec });
        var outp = new Collector(clock) { Open = false };
        GeneralLink.CreateLink(feeder, L(asy)); GeneralLink.CreateLink(asy, L(outp));
        foreach (var e in new Element[] { feeder, asy, outp }) e.Start();
        feeder.Begin();
        clock.AdvanceClock(2);   // job done at t=1, output closed -> blocked
        asy.Stop(new StopRequest(ElementState.STOPPED, StopMode.AfterCurrent));   // output not blocked
        Schedule(clock, 3, () => outp.OpenNow());
        clock.AdvanceClock(5);
        Check(outp.Got.Count == 1, $"job released while the stop is shown (delivered {outp.Got.Count})");
    }

    // ── #9 ───────────────────────────────────────────────────────────────────

    static void TestDowntimeTableDates()
    {
        var cal = new SimCalendar(new DateTime(2026, 1, 1));
        var rows = new List<IReadOnlyDictionary<string, object>>
        {
            new Dictionary<string, object> { ["start"] = new DateTime(2026, 3, 5, 8, 0, 0), ["duration"] = 60.0 },
        };
        var result = DowntimeTable.Build(rows, new DowntimeTableMapping(), cal, out int skipped);
        double expected = (new DateTime(2026, 3, 5, 8, 0, 0) - new DateTime(2026, 1, 1)).TotalSeconds;
        Check(skipped == 0 && result.ContainsKey("") && result[""].Count == 1, "row read");
        if (result.ContainsKey("") && result[""].Count == 1)
            Near(result[""][0].Start, expected, "5 March stays 5 March");
    }

    // ── #10 ──────────────────────────────────────────────────────────────────

    static void TestMutatedStopRequest()
    {
        var clock = new SimClock(1);
        var e = new Bare(clock);
        e.Start();
        var req = new StopRequest(ElementState.STOPPED);        // Immediate, blocks input + output
        StopToken token = e.Stop(req);
        req.BlockInput = false;                                 // generator reuses / edits its request
        req.Mode = StopMode.AfterCurrent;
        e.Resume(token);
        Check(!e.IsDown && !e.IsInputBlocked && !e.IsOutputBlocked && !e.IsWorkPaused,
              $"counters balanced (in {e.IsInputBlocked}, out {e.IsOutputBlocked}, paused {e.IsWorkPaused})");
    }

    // ── #11 ──────────────────────────────────────────────────────────────────

    static void TestResumePullsAll()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 3, 0);
        var m1 = new MultiServer(new[] { Const(1), Const(1), Const(1) }, "M1", clock);
        var m2 = new MultiServer(new[] { Const(100), Const(100), Const(100) }, "M2", clock);
        var outp = new Collector(clock);
        GeneralLink.CreateLink(feeder, L(m1)); GeneralLink.CreateLink(m1, L(m2)); GeneralLink.CreateLink(m2, L(outp));
        foreach (var e in new Element[] { feeder, m1, m2, outp }) e.Start();
        StopToken stop = m2.Stop(new StopRequest(ElementState.STOPPED));
        feeder.Begin();
        clock.AdvanceClock(5);      // M1 finished all three at t=1, all blocked by the stopped M2
        Check(m2.GetQueueLength() == 0, "M2 empty while stopped");
        Schedule(clock, 10, () => m2.Resume(stop));
        clock.AdvanceClock(11);
        Check(m2.GetQueueLength() == 3, $"all three slots refilled on resume (M2 holds {m2.GetQueueLength()})");
    }

    // ── #12 ──────────────────────────────────────────────────────────────────

    static void TestCancelWorkShowsStop()
    {
        var clock = new SimClock(1);
        var e = new Bare(clock);
        e.Start();
        WorkHandle work = e.ScheduleWork(new Nop(), 10);
        clock.AdvanceClock(1);
        e.Stop(new StopRequest(ElementState.STOPPED, StopMode.AfterCurrent));
        Check(e.CurrentState != ElementState.STOPPED, "AfterCurrent stop hidden while work is pending");
        clock.AdvanceClock(2);
        work.Cancel();
        Check(e.CurrentState == ElementState.STOPPED, $"stop shown once the work is cancelled ({e.CurrentState.Name})");
    }

    // ── #13 / #14 ────────────────────────────────────────────────────────────

    static void TestTimetableMode()
    {
        var clock = new SimClock(1);
        var e = new Bare(clock);
        e.Start();
        var gen = new TimetableDowntime(e, new[] { new DowntimeInterval(5, 10, ElementState.STOPPED) })
        { Mode = StopMode.AfterCurrent };
        gen.Start();
        clock.AdvanceClock(6);
        Check(e.ActiveStops.Count == 1 && e.ActiveStops[0].Request.Mode == StopMode.AfterCurrent,
              "interval without a mode uses the generator's AfterCurrent");
    }

    static void TestTimetableCancelPending()
    {
        var clock = new SimClock(1);
        var e = new Bare(clock);
        e.Start();
        var gen = new TimetableDowntime(e, new[] { new DowntimeInterval(5, 10, ElementState.STOPPED) });
        gen.Start();
        clock.AdvanceClock(7);       // stop active, its end pending at t=15
        Check(e.IsDown, "stop active");
        gen.CancelPending();
        Check(clock.PendingEventCount == 0, $"no generator events left ({clock.PendingEventCount} pending)");
    }

#if !OLDAPI
    // ── #17 ──────────────────────────────────────────────────────────────────

    static void TestWarmup()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 10, 1);
        var q = new ItemsQueue(100, "Q", clock);
        var m = new MultiServer(new[] { Const(2) }, "M", clock);   // slower than arrivals: queue builds up
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(feeder, L(q)); GeneralLink.CreateLink(q, L(m)); GeneralLink.CreateLink(m, L(sink));
        var all = new Element[] { feeder, q, m, sink };
        foreach (var e in all) e.Start();
        Element.ScheduleWarmup(clock, all, 5.5);
        feeder.Begin();
        clock.AdvanceClock(5.5);
        double contentAtWarmup = q.GetStatsContentCurrent();
        clock.AdvanceClock(30);
        // Arrivals every 1 (t = 0..9), service 2: departures at t = 2, 4, …, 20.
        Near(q.GetStatsInputCount(), 4, "queue input counted after warm-up only (t = 6..9)");
        Near(sink.GetStatsInputCount(), 8, "sink departures after warm-up only (t = 6..20)");
        Near(sink.GetStatsInputCount() + 2, 10, "model untouched: the 2 pre-warm-up departures happened, all 10 left");
        Check(contentAtWarmup > 0 && q.GetStatsContentMax() >= contentAtWarmup,
              $"queue content continues from its level at warm-up ({contentAtWarmup})");
        // M busy from 5.5 to 20, idle 20..30: ratio over the 24.5 observed units.
        Near(m.GetStateRatio(ElementState.PROCESSING), 14.5 / 24.5, "M processing ratio since the warm-up", 1e-6);
    }
#endif

    // ── #20 ──────────────────────────────────────────────────────────────────

    static void TestProgressShift()
    {
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 1, 1);
        var m = new MultiServer(new[] { Const(10) }, "M", clock);
        var outp = new Collector(clock);
        GeneralLink.CreateLink(feeder, L(m)); GeneralLink.CreateLink(m, L(outp));
        foreach (var e in new Element[] { feeder, m, outp }) e.Start();
        StopToken stop = null;
        Schedule(clock, 1, () => stop = m.Stop(new StopRequest(ElementState.STOPPED) { BlockInput = false }));
        Schedule(clock, 3, () => feeder.Begin());          // job loaded during the pause
        Schedule(clock, 5, () => m.Resume(stop));
        clock.AdvanceClock(5.5);
        Check(m.workInProgress.Count == 1, "job in progress");
        if (m.workInProgress.Count == 1)
            Near(m.workInProgress[0].loadTime, 5, "load time shifted by the 2 paused units it was loaded (not the whole 4-unit pause)");
        clock.AdvanceClock(20);
        Check(outp.Got.Count == 1, "job finishes");
    }

    static void TestSinkStopResume()
    {
        var clock = new SimClock(1);
        var sink = new Sink("K", clock);
        sink.Start();
        var token = sink.Stop(new StopRequest(ElementState.OFF_SHIFT));
        clock.AdvanceClock(5);
        Check(sink.Resume(token), "resume succeeds");
        Check(sink.CurrentState != ElementState.OFF_SHIFT, "the sink is no longer off shift");
    }

    // A 4 h session (Mon-Fri 08:30-12:30) with exactly 20-minute jobs (1/3 h) and a full queue every morning: twelve
    // jobs a day, as the slots fit, over 200 days. The thirteenth used to start at the closing time on some days — the
    // twelfth ended there up to floating-point drift, which grows with the absolute time — before the AfterCurrent
    // stop took effect (13 a day from day ~100).
    static void TestShiftSessionSlots()
    {
        const int days = 200;
        int MaxPerDay(double slot)
        {
            var c = new SimuLean.Serialization.SimulationConfig
            {
                UseFixedSeed = true, Seed = 1, MaxSimulationTime = days * 24, StartDateTime = "2025-01-06 00:00", TimeUnitSeconds = 3600,
            };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "Schedule");
            var times = Enumerable.Range(0, days).Select(d => (d * 24 + 6).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
            src.Set("arrivalsTable", new Dictionary<string, List<string>>
            {
                ["Time"] = times, ["Name"] = times.Select(_ => "p").ToList(), ["Q"] = times.Select(_ => "14").ToList(),
            });
            var q = new SimuLean.Serialization.ElementConfig("q", "Queue", "q");
            q.Set("capacity", 100000);
            var st = new SimuLean.Serialization.ElementConfig("st", "MultiServer", "st");
            st.Set("capacity", 1); st.Set("delayStrategy.strategyType", "Constant"); st.Set("delayStrategy.constantValue", slot);
            c.Elements.Add(src); c.Elements.Add(q); c.Elements.Add(st); c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink"));
            foreach (var (f, t) in new[] { ("src", "q"), ("q", "st"), ("st", "sink") })
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(f, t, "General"));
            var shift = new SimuLean.Serialization.DowntimeConfig("agenda", "Shift", "st");
            shift.Set("shiftPattern", "Mon-Fri 08:30-12:30");
            c.Downtimes.Add(shift);
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            var sink = model.Find("sink");
            model.StartModel();
            int max = 0;
            for (int d = 0; d < days; d++)
            {
                clock.AdvanceClock(d * 24 + 7);
                double before = sink.GetStatsInputCount();
                clock.AdvanceClock(d * 24 + 20);
                max = Math.Max(max, (int)(sink.GetStatsInputCount() - before));
            }
            return max;
        }
        int exact = MaxPerDay(1.0 / 3.0), typed = MaxPerDay((double)0.3333333f), longer = MaxPerDay(0.34), shorter = MaxPerDay(0.33);
        Check(exact == 12, $"exact 20-minute slots: at most 12 patients a session over {days} days (got {exact})");
        Check(typed == 12, $"20 minutes typed in a float field (0.3333333, 1.4 ms short over 12 slots): 12 (got {typed})");
        Check(longer == 12, $"slots of 0.34 h: the twelfth starts 12:14, before the close (got {longer})");
        Check(shorter == 13, $"slots of 0.33 h: a thirteenth really starts before 12:30 (12:26) (got {shorter})");
    }

    // ── #52 ──────────────────────────────────────────────────────────────────

    // A run with a warm-up at W counts, at T, exactly what a run without warm-up counts between W and T (same seed):
    // requests served by a team, tasks and usage of its members, stops and downtime of the generators.
    static void TestWarmupResetsEverything()
    {
        foreach (string model in new[] { "resource_shared_team", "downtime_mtbf" })
        {
            SimulationConfig Config() => SimuLean.Testing.ReferenceModels.All().First(m => m.Key == model).Value;
            double end = Config().MaxSimulationTime, w = end / 3;

            // without warm-up: the figures at W and at the end
            var c1 = Config(); c1.WarmupTime = 0;
            var clock1 = new SimClock(c1.UseFixedSeed ? (int?)c1.Seed : 1);
            var f1 = new SimuLean.Headless.HeadlessModelFactory(clock1, enableLogging: false);
            f1.BuildModel(c1); f1.StartModel();
            clock1.AdvanceClock(w);
            var atW = Figures(f1);
            clock1.AdvanceClock(end);
            var atEnd = Figures(f1);

            // with the warm-up at W
            var c2 = Config(); c2.WarmupTime = w;
            var clock2 = new SimClock(c2.UseFixedSeed ? (int?)c2.Seed : 1);
            var f2 = new SimuLean.Headless.HeadlessModelFactory(clock2, enableLogging: false);
            f2.BuildModel(c2); f2.StartModel();
            clock2.AdvanceClock(end);
            var warm = Figures(f2);

            Check(atEnd.Count > 0, $"{model}: something to compare");
            foreach (var kv in atEnd)
            {
                // a task / stop running at W is counted from W on: usage and downtime are not plain differences
                if (kv.Key.EndsWith(".usage") || kv.Key.EndsWith(".downtime"))
                    Check(warm[kv.Key] <= kv.Value - atW[kv.Key] + 1e-9 && warm[kv.Key] >= 0 && warm[kv.Key] <= kv.Value,
                          $"{model} {kv.Key}: {warm[kv.Key]} after the warm-up, {kv.Value - atW[kv.Key]} ended after it, {kv.Value} in the whole run");
                else
                    Check(Math.Abs(warm[kv.Key] - (kv.Value - atW[kv.Key])) < 1e-9,
                          $"{model} {kv.Key}: {warm[kv.Key]} with the warm-up, want {kv.Value - atW[kv.Key]} ({kv.Value} - {atW[kv.Key]})");
            }
        }
    }

    static Dictionary<string, double> Figures(SimuLean.Headless.HeadlessModelFactory f)
    {
        var d = new Dictionary<string, double>();
        foreach (var t in f.Teams) d["team " + t.Key + ".served"] = t.Value.Served;
        foreach (var ex in f.Executors)
        {
            d["member " + ex.Name + ".tasks"] = ex.TasksDone;
            d["member " + ex.Name + ".usage"] = ex.UsageByRequester.Values.Sum();
        }
        int i = 0;
        foreach (var g in f.DowntimeGenerators)
        {
            d["generator " + i + ".stops"] = g.StopCount;
            d["generator " + i + ".downtime"] = g.TotalDowntime;
            d["element " + g.Target.Name + ".stops"] = g.Target.StopCount;
            i++;
        }
        return d;
    }

    // ── #55 ──────────────────────────────────────────────────────────────────

    static void TestResumeReleasesAllBlocked()
    {
        // three finished items wait in a 3-slot Multi-Server; it is stopped; meanwhile the receiver opens (its
        // notification skips the stopped server); at the end of the stop all three must leave, not one
        var clock = new SimClock(1);
        var feeder = new Feeder(clock, 3, 0);
        var m = new MultiServer(new[] { Const(1), Const(1), Const(1) }, "M", clock);
        var outp = new Collector(clock) { Open = false };
        GeneralLink.CreateLink(feeder, L(m)); GeneralLink.CreateLink(m, L(outp));
        foreach (var e in new Element[] { feeder, m, outp }) e.Start();
        feeder.Begin();
        StopToken stop = null;
        Schedule(clock, 2, () => stop = m.Stop(new StopRequest(ElementState.STOPPED, StopMode.Immediate)));
        Schedule(clock, 3, () => outp.OpenNow());
        Schedule(clock, 4, () => m.Resume(stop));
        clock.AdvanceClock(6);
        Check(outp.Got.Count == 3 && m.GetQueueLength() == 0, $"all three out after the stop ({outp.Got.Count} out, {m.GetQueueLength()} left)");
    }

    // Two agendas with their own shifts (Mon-Fri 08:00-12:00, slots of 1 h) and a full queue; the model's
    // holiday on Wednesday 2025-01-08 closes both — 4 patients fewer each in the week; a bad date fails the build.
    static void TestModelHolidays()
    {
        (int a, int b) Week(params string[] holidays)
        {
            var c = new SimuLean.Serialization.SimulationConfig
            {
                UseFixedSeed = true, Seed = 1, MaxSimulationTime = 7 * 24, StartDateTime = "2025-01-06 00:00", TimeUnitSeconds = 3600,
            };
            c.Holidays.AddRange(holidays);
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "Schedule");
            src.Set("arrivalsTable", new Dictionary<string, List<string>>
            {
                ["Time"] = new List<string> { "0" }, ["Name"] = new List<string> { "p" }, ["Q"] = new List<string> { "200" },
            });
            var q = new SimuLean.Serialization.ElementConfig("q", "Queue", "q");
            q.Set("capacity", 100000);
            q.Set("outputStrategy.strategyType", "RoundRobin");
            c.Elements.Add(src); c.Elements.Add(q); c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "q", "General"));
            foreach (var n in new[] { "a", "b" })
            {
                var st = new SimuLean.Serialization.ElementConfig(n, "MultiServer", n);
                st.Set("capacity", 1); st.Set("delayStrategy.strategyType", "Constant"); st.Set("delayStrategy.constantValue", 1.0);
                c.Elements.Add(st);
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("q", n, "General"));
                c.Connections.Add(new SimuLean.Serialization.ConnectionConfig(n, "sink", "General"));
                var shift = new SimuLean.Serialization.DowntimeConfig("agenda_" + n, "Shift", n);
                shift.Set("shiftPattern", "Mon-Fri 08:00-12:00");
                c.Downtimes.Add(shift);
            }
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            clock.AdvanceClock(7 * 24);
            return ((int)model.Find("a").GetStatsOutputCount(), (int)model.Find("b").GetStatsOutputCount());
        }
        var plain = Week();
        var holiday = Week("2025-01-08");
        Check(plain.a == 20 && plain.b == 20, $"four slots a day, five days, each agenda ({plain.a}, {plain.b})");
        Check(holiday.a == 16 && holiday.b == 16, $"the model's holiday closes both agendas ({holiday.a}, {holiday.b})");
        bool failed = false;
        try { Week("not a date"); } catch (FormatException) { failed = true; }
        Check(failed, "a holiday that is not a date fails the build");
    }

    // ── AfterCurrent: the stop is delayed by the work in hand, never cut ──────

    // A bare element with jobs in hand (ending at the given times) and an AfterCurrent timetable; the times the element
    // goes STOPPED and back, read every 0.5.
    static (Bare e, TimetableDowntime gen, SimClock clock, List<double> stops, List<double> resumes) AfterCurrentRun(
        double horizon, double[] jobsEndAt, OverlapPolicy overlap, params DowntimeInterval[] rows)
    {
        var clock = new SimClock(1);
        var e = new Bare(clock);
        e.Start();
        foreach (double end in jobsEndAt) e.ScheduleWork(new Nop(), end);
        var gen = new TimetableDowntime(e, rows, overlap) { Mode = StopMode.AfterCurrent };
        var stops = new List<double>(); var resumes = new List<double>();
        e.StateChanged += (el, from, to) =>
        {
            if (to == ElementState.STOPPED) stops.Add(clock.GetSimulationTime());
            else if (from == ElementState.STOPPED) resumes.Add(clock.GetSimulationTime());
        };
        gen.Start();
        clock.AdvanceClock(horizon);
        return (e, gen, clock, stops, resumes);
    }

    static void TestAfterCurrentDelaysTheStop()
    {
        var row = new DowntimeInterval(30, 20, ElementState.STOPPED);

        // the job in hand ends before the scheduled end: stopped 40..60 (not 40..50)
        var a = AfterCurrentRun(100, new[] { 40.0 }, OverlapPolicy.Allow, row);
        Check(a.stops.SequenceEqual(new[] { 40.0 }) && a.resumes.SequenceEqual(new[] { 60.0 }),
              $"job 0..40, row 30+20: stopped 40..60 (stops {string.Join(",", a.stops)}; back {string.Join(",", a.resumes)})");
        Near(a.gen.TotalDowntime, 20, "downtime_total: the 20 stood");
        Near(a.gen.ScheduledDowntime, 20, "downtime_scheduled: the 20 planned");
        Near(a.e.GetStateRatio(ElementState.STOPPED) * 100, a.gen.TotalDowntime, "downtime_total = the time STOPPED");

        // the job in hand ends after the scheduled end: stopped 100..120 (it never stopped before: the stop was cut)
        var b = AfterCurrentRun(60, new[] { 100.0 }, OverlapPolicy.Allow, row);
        Check(b.stops.Count == 0 && b.e.ActiveStops.Count == 1 && !b.e.ActiveStops[0].IsEffective,
              "job 0..100: at 60 the stop waits for the job (not effective)");
        Near(b.gen.TotalDowntime, 0, "nothing stood yet");
        Near(b.gen.ScheduledDowntime, 20, "but 30..50 was scheduled");
        b.clock.AdvanceClock(150);
        Check(b.stops.SequenceEqual(new[] { 100.0 }) && b.resumes.SequenceEqual(new[] { 120.0 }),
              $"job 0..100, row 30+20: stopped 100..120 (stops {string.Join(",", b.stops)}; back {string.Join(",", b.resumes)})");
        Near(b.gen.TotalDowntime, 20, "downtime_total 20");
        Near(b.gen.ScheduledDowntime, 20, "downtime_scheduled 20");
        Near(b.e.ActiveStops.Count, 0, "no stop left");

        // several jobs in hand (capacity > 1): the element stops as a whole, when the last of them ends
        var c = AfterCurrentRun(150, new[] { 40.0, 70.0 }, OverlapPolicy.Allow, row);
        Check(c.stops.SequenceEqual(new[] { 70.0 }) && c.resumes.SequenceEqual(new[] { 90.0 }),
              $"jobs ending 40 and 70: stopped 70..90 (stops {string.Join(",", c.stops)}; back {string.Join(",", c.resumes)})");

        // no work in hand: as before, the stop keeps its schedule
        var d = AfterCurrentRun(100, new double[0], OverlapPolicy.Allow, row);
        Check(d.stops.SequenceEqual(new[] { 30.0 }) && d.resumes.SequenceEqual(new[] { 50.0 }), "idle: stopped 30..50");
    }

    static void TestAfterCurrentStopDueWhileDelayed()
    {
        var first = new DowntimeInterval(30, 20, ElementState.STOPPED);
        var second = new DowntimeInterval(60, 20, ElementState.STOPPED);

        // Allow: a stop due while another waits for the same job takes effect with it — they overlap (100..120), none lost
        var a = AfterCurrentRun(200, new[] { 100.0 }, OverlapPolicy.Allow, first, second);
        Check(a.gen.StopCount == 2 && a.stops.SequenceEqual(new[] { 100.0 }) && a.resumes.SequenceEqual(new[] { 120.0 }),
              $"Allow: both rows stop 100..120 together ({a.gen.StopCount} stops; {string.Join(",", a.stops)} / {string.Join(",", a.resumes)})");
        Near(a.gen.ScheduledDowntime, 40, "both scheduled (each counts)");
        Near(a.gen.TotalDowntime, 20, "downtime_total: the 20 stood, overlapping stops once");
        Near(a.e.GetTimeInState(ElementState.STOPPED), 20, "= the time STOPPED");

        // Serialize (and Merge): the second waits for the delayed first one to end, then lasts its whole length
        foreach (var policy in new[] { OverlapPolicy.Serialize, OverlapPolicy.Merge })
        {
            var b = AfterCurrentRun(200, new[] { 100.0 }, policy, first, second);
            Check(b.gen.StopCount == 2 && b.stops.SequenceEqual(new[] { 100.0 }) && b.resumes.SequenceEqual(new[] { 140.0 }),
                  $"{policy}: stopped 100..120 then 120..140 ({b.gen.StopCount} stops; {string.Join(",", b.stops)} / {string.Join(",", b.resumes)})");
            Near(b.gen.TotalDowntime, 40, $"{policy}: 40 stood");
            Near(b.gen.ScheduledDowntime, 40, $"{policy}: 40 scheduled");
        }

        // a periodic stop without overlap queues the same way: due 30 and 80, the job ends at 100 → 100..120, 120..140
        {
            var clock = new SimClock(1);
            var e = new Bare(clock);
            e.Start();
            e.ScheduleWork(new Nop(), 100);
            var gen = new PeriodicDowntime(e, 50, 20, 30, until: 100, clock: clock) { Mode = StopMode.AfterCurrent };
            var ends = new List<double>();
            gen.StopEnded += (g, t) => ends.Add(clock.GetSimulationTime());
            gen.Start();
            clock.AdvanceClock(200);
            Check(gen.StopCount == 2 && ends.SequenceEqual(new[] { 120.0, 140.0 }),
                  $"periodic, no overlap: ends 120 and 140 ({string.Join(",", ends)})");
            Near(gen.TotalDowntime, 40, "periodic: 40 stood");
        }
    }

    static void TestAfterCurrentFailure()
    {
        // an AfterCurrent failure (MTBF): the repair time counts from the end of the job in hand; the next time to
        // failure from the end of the repair
        var clock = new SimClock(1);
        var e = new Bare(clock);
        e.Start();
        e.ScheduleWork(new Nop(), 100);
        var gen = new MtbfMttrDowntime(e, Const(30), Const(20), clock) { Mode = StopMode.AfterCurrent, State = ElementState.STOPPED };
        var starts = new List<double>(); var ends = new List<double>();
        gen.StopEnded += (g, t) => { starts.Add(t.EffectiveStart); ends.Add(clock.GetSimulationTime()); };
        gen.Start();
        clock.AdvanceClock(175);
        Check(starts.SequenceEqual(new[] { 100.0, 150.0 }) && ends.SequenceEqual(new[] { 120.0, 170.0 }),
              $"failure due 30 stops 100..120, the next 150..170 (effective {string.Join(",", starts)}; ends {string.Join(",", ends)})");
        Near(gen.TotalDowntime, 40, "MTBF: 40 stood");
        Near(gen.ScheduledDowntime, 40, "MTBF: 40 scheduled (two repairs of 20)");
    }

    // The kind defaults the Unity component exports for an empty state / Kind Default mode are what the engine builds
    // when a downtime gives neither.
    static void TestDowntimeKindDefaults()
    {
        foreach (string kind in new[] { "MtbfMttr", "Timetable", "Shift", "Periodic" })
        {
            var clock = new SimClock(1);
            var e = new Bare(clock);
            var cfg = new DowntimeConfig("d", kind, "m");
            if (kind == "Shift") cfg.Set("shiftPattern", "Mon-Fri 06:00-14:00");
            if (kind == "Periodic") { cfg.Set("periodicPeriod", 100.0); cfg.Set("periodicDuration", 10.0); }
            if (kind == "Timetable") cfg.Set("rows", new List<DowntimeIntervalSettings> { DowntimeIntervalSettings.Of("", 5, 10) });
            var gen = DowntimeBuilder.Build(cfg, new Dictionary<string, Element> { ["m"] = e }, clock).Single();
            ElementState state = gen is TimetableDowntime tt ? tt.Intervals[0].State : gen.State;
            Check(state.Name == DowntimeBuilder.DefaultStateName(kind), $"{kind}: state {state.Name} = DefaultStateName {DowntimeBuilder.DefaultStateName(kind)}");
            Check(gen.Mode == DowntimeBuilder.DefaultMode(kind), $"{kind}: mode {gen.Mode} = DefaultMode {DowntimeBuilder.DefaultMode(kind)}");
        }
    }
}
