// Run control: end conditions, finished versus deadlocked models, stalls and lack of progress, diagnosis at the end.
using System;
using System.Linq;
using System.Threading.Tasks;
using SimuLean;
using SimuLean.Resources;
using SimuLean.Serialization;

static partial class Program
{
    static void TestDeadlockUnderStop()
    {
        // src (every 1) → q (3) → srv (0.5) → sink; srv stopped (Immediate) at t = 2.2, resumed at `resumeAt` (0 = never).
        SimuLean.Results.RunInfo Run(double resumeAt)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, MaxSimulationTime = 100 };
            var src = new SimuLean.Serialization.ElementConfig("src", "Source", "src");
            src.Set("mode", "InterArrival");
            src.Set("interArrivalDistribution.strategyType", "Constant");
            src.Set("interArrivalDistribution.constantValue", 1.0);
            var q = new SimuLean.Serialization.ElementConfig("q", "Queue", "q");
            q.Set("capacity", 3);
            var srv = new SimuLean.Serialization.ElementConfig("srv", "MultiServer", "srv");
            srv.Set("capacity", 1);
            srv.Set("delayStrategy.strategyType", "Constant");
            srv.Set("delayStrategy.constantValue", 0.5);
            c.Elements.Add(src); c.Elements.Add(q); c.Elements.Add(srv);
            c.Elements.Add(new SimuLean.Serialization.ElementConfig("sink", "Sink", "sink"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "q", "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("q", "srv", "General"));
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("srv", "sink", "General"));
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            var m = model.Find("srv");
            StopToken stop = null;
            Schedule(clock, 2.2, () => stop = m.Stop(new StopRequest(ElementState.BREAKDOWN)));
            if (resumeAt > 0) Schedule(clock, resumeAt, () => m.Resume(stop));
            var oldSink = SimLog.WarningSink;
            SimLog.WarningSink = w => { };
            try { clock.AdvanceClock(100); } finally { SimLog.WarningSink = oldSink; }
            return SimuLean.Results.RunResults.Capture(model).Info;
        }
        var never = Run(0);
        Check(never.Deadlocked, "a stop nothing will end, items waiting behind it: deadlock");
        Check(never.Waiting.Exists(w => w.StartsWith("srv: BREAKDOWN")),
              $"the stopped element is named with its stop state: {string.Join(" | ", never.Waiting)}");
        var resumed = Run(50);   // the source is blocked meanwhile, but the resume is a pending event: no Idle, no stall
        Check(!resumed.Deadlocked && resumed.Waiting.Count == 0, $"a stop with a pending end is not a stall ({string.Join(" | ", resumed.Waiting)})");
    }

    static void TestEndCountAndRunningStop()
    {
        var cfg = SimuLean.Testing.ReferenceModels.EndAtSinkCount();   // stop when the sink has received 500
        cfg.WarmupTime = 100;
        var clock = new SimClock(cfg.Seed);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(cfg);
        model.StartModel();
        clock.AdvanceClock(cfg.MaxSimulationTime);
        var r = SimuLean.Results.RunResults.Capture(model);
        double observed = r.Find(SimuLean.Results.ResultScope.Element, "sink")[SimuLean.Results.ResultMetrics.Input];
        Check(r.Info.EndReason == "StopAtCount" && observed == 500,
              $"500 items received after the warm-up when the run stops ({observed}, {r.Info.EndReason})");

        // the shift model: one week from Monday 00:00, 40 h on shift → 128 h off, the last stop still running at the end
        var sc = SimuLean.Testing.ReferenceModels.ShiftModel();
        var c2 = new SimClock(sc.Seed);
        var m2 = new SimuLean.Headless.HeadlessModelFactory(c2);
        m2.BuildModel(sc);
        m2.StartModel();
        c2.AdvanceClock(sc.MaxSimulationTime);
        var shift = SimuLean.Results.RunResults.Capture(m2).Find(SimuLean.Results.ResultScope.Downtime, "shift");
        Near(shift[SimuLean.Results.ResultMetrics.DowntimeScheduled], 128 * 60, "downtime_scheduled includes the stop running at the end (128 h off shift)", 0.01);
        // the time actually off shift: each stop from the end of the job in hand at the shift's end (AfterCurrent)
        double offShift = m2.Find("srv").GetStateRatio(ElementState.OFF_SHIFT) * c2.GetSimulationTime();
        Near(shift[SimuLean.Results.ResultMetrics.DowntimeTotal], offShift, $"downtime_total = the time OFF_SHIFT ({offShift:F2})", 1e-6);
        Check(shift[SimuLean.Results.ResultMetrics.DowntimeTotal] < 128 * 60, "and less than scheduled: the jobs in hand finished first");
    }

    static void TestShiftNightNoStall()
    {
        var cfg = SimuLean.Testing.ReferenceModels.ShiftNightModel();
        var clock = new SimClock(cfg.Seed);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(cfg);
        model.StartModel();
        clock.AdvanceClock(cfg.MaxSimulationTime);
        var r = SimuLean.Results.RunResults.Capture(model);
        double done = r.Find(SimuLean.Results.ResultScope.Element, "sink")[SimuLean.Results.ResultMetrics.Input];
        var run = clock.Context.Run;
        Check(done == 20, $"all 20 jobs done over three shifts ({done})");
        Check(!run.Stalled && !run.Deadlocked && r.Info.EndReason == "Empty",
              $"no stall or deadlock reported at a shift end ({r.Info.EndReason}; {string.Join(" | ", run.Waiting)})");
        // 8 jobs a day from Monday 06:00: the 20th ends Wednesday 10:00 = 52 h after the start
        Near(clock.GetSimulationTime(), 52 * 60, "the run ends when the last job is done", 1e-6);
    }

    // ── stalls beside generators that always have a next event ───────────

    static (SimuLean.Results.RunResults results, SimClock clock, SimuLean.Headless.HeadlessModelFactory model) RunConfig(SimuLean.Serialization.SimulationConfig c)
    {
        var clock = new SimClock(c.Seed);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
        var oldSink = SimLog.WarningSink;
        SimLog.WarningSink = w => { };
        try { clock.AdvanceClock(c.MaxSimulationTime); } finally { SimLog.WarningSink = oldSink; }
        return (SimuLean.Results.RunResults.Capture(model), clock, model);
    }

    static void TestDeadlockBesideBackgroundGenerators()
    {
        // 6 items through srv into a dead-end buffer of 2: a deadlock once the last arrival is in (t = 5)
        var plain = RunConfig(SimuLean.Testing.ReferenceModels.DeadlockDeadEnd());
        double at = plain.clock.Context.Run.DeadlockTime;
        Check(plain.clock.Context.Run.Deadlocked && at < 10, $"the plain model deadlocks early ({at})");

        void Beside(string what, SimuLean.Serialization.DowntimeConfig dt)
        {
            foreach (bool whenEmpty in new[] { false, true })
            {
                var c = SimuLean.Testing.ReferenceModels.DeadlockDeadEnd();
                c.Downtimes.Add(dt);
                c.StopWhenEmpty = whenEmpty;
                var r = RunConfig(c);
                var run = r.clock.Context.Run;
                Check(run.Deadlocked, $"{what}: the deadlock is reported");
                Near(run.DeadlockTime, at, $"{what}: reported at the Idle point, as without the generator", 1e-9);
                if (whenEmpty)
                {
                    Check(r.results.Info.EndReason == "Deadlock", $"{what}, stop when empty: the run ends on the deadlock ({r.results.Info.EndReason})");
                    Near(r.clock.GetSimulationTime(), at, $"{what}, stop when empty: it ends there", 1e-9);
                }
                else Near(r.clock.GetSimulationTime(), c.MaxSimulationTime, $"{what}: without an end condition the run goes on to its horizon", 1e-9);
            }
        }
        // a calendar MTBF on the blocked station: its next failure is a background event, the repair is not
        var mtbf = new SimuLean.Serialization.DowntimeConfig("fail", "MtbfMttr", "srv");
        mtbf.Set("timeToFailure", "Constant~3");   // the first failure at 3, before the deadlock; repaired at 4; then every 4
        mtbf.Set("timeToRepair", "Constant~1");
        Beside("calendar MTBF", mtbf);
        var periodic = new SimuLean.Serialization.DowntimeConfig("pm", "Periodic", "srv");
        periodic.Set("periodicPeriod", 7.0);
        periodic.Set("periodicDuration", 1.0);
        periodic.Set("periodicFirst", 20.0);    // the first stop after the deadlock
        Beside("periodic downtime", periodic);
        var early = new SimuLean.Serialization.DowntimeConfig("pm", "Periodic", "srv");
        early.Set("periodicPeriod", 10.0);
        early.Set("periodicDuration", 0.5);
        early.Set("periodicFirst", 3.2);        // a stop running from 3.2 to 3.7, before the deadlock
        Beside("periodic downtime stopping before", early);
    }

    static void TestNoProgressEndCondition()
    {
        // the dead-end deadlock beside a periodic clock signal: the signal's ticks are foreground (a reaction may release
        // items), so the Idle check never runs and the deadlock is not reported
        SimuLean.Serialization.SimulationConfig WithTicks(double noProgressFor)
        {
            var c = SimuLean.Testing.ReferenceModels.DeadlockDeadEnd();
            var tick = new SimuLean.Serialization.ReactionConfig("tick");
            tick.Set("signal", "Every");
            tick.Set("period", 7.0);
            c.Reactions.Add(tick);
            c.StopWhenNoProgressFor = noProgressFor;
            return c;
        }
        var off = RunConfig(WithTicks(0));
        Check(!off.clock.Context.Run.Deadlocked && off.results.Info.EndReason == "Horizon",
              $"off: the ticks hide the deadlock up to the horizon ({off.results.Info.EndReason})");
        double last = SimuLean.Headless.RunMonitor.LastMovement(off.model.GetAllElements().Values);
        Check(last > 0 && last < 10, $"the last movement is early ({last})");

        var on = RunConfig(WithTicks(50));
        var run = on.clock.Context.Run;
        Check(on.results.Info.EndReason == "NoProgress", $"on: the run ends for lack of progress ({on.results.Info.EndReason})");
        Near(on.clock.GetSimulationTime(), last + 50, "on: it ends T after the last movement", 1e-9);
        Near(run.NoProgressTime, last + 50, "on: the time is recorded", 1e-9);
        Check(run.NoProgress && !run.Stalled && !run.Deadlocked, "on: flagged as no progress (not the Idle rule's stall / deadlock)");
        Check(run.Waiting.Exists(w => w.StartsWith("srv: BLOCKED")), $"on: the waiting elements are named ({string.Join(" | ", run.Waiting)})");
        Near(on.results.Find(SimuLean.Results.ResultScope.Model, "model")[SimuLean.Results.ResultMetrics.NoProgressTime], last + 50, "on: model.no_progress_time");
        Check(!off.results.Find(SimuLean.Results.ResultScope.Model, "model").TryGet(SimuLean.Results.ResultMetrics.NoProgressTime, out _),
              "off: no no_progress_time in the results");
        var d = SimuLean.Headless.ModelDiagnosis.Of(on.model, on.clock);
        Check((string)d["endReason"] == "NoProgress" && d["noProgress"] is double, "the diagnosis gives the end reason");

        // a model that keeps moving: the option changes nothing (same statistics, same end)
        var basicOff = SimuLean.Testing.ReferenceModels.BasicLine();
        var basicOn = SimuLean.Testing.ReferenceModels.BasicLine();
        basicOn.StopWhenNoProgressFor = 30;
        var a = RunConfig(basicOff); var b = RunConfig(basicOn);
        Check(b.results.Info.EndReason == "Horizon", $"a moving model is not ended ({b.results.Info.EndReason})");
        bool same = true;
        foreach (var o in a.results.Of(SimuLean.Results.ResultScope.Element))
        {
            var ob = b.results.Find(SimuLean.Results.ResultScope.Element, o.Id);
            foreach (var kv in o.Metrics) if (ob == null || !ob.TryGet(kv.Key, out double v) || v != kv.Value) same = false;
        }
        Check(same, "a moving model: identical statistics with the option on");

        // a night off shift is no lack of progress (T = 2 h, nights of 16 h): every job is done and the run ends empty
        var night = SimuLean.Testing.ReferenceModels.ShiftNightModel();
        night.StopWhenNoProgressFor = 120;
        var n = RunConfig(night);
        Check(n.results.Info.EndReason == "Empty", $"shift: the run ends empty, not at the first night ({n.results.Info.EndReason})");
        Near(n.clock.GetSimulationTime(), 52 * 60, "shift: at the last job", 1e-6);
        // with a periodic signal too (no Idle at all): the jobs are all done, nothing waits, the run goes to its horizon
        var nightTicks = SimuLean.Testing.ReferenceModels.ShiftNightModel();
        nightTicks.StopWhenNoProgressFor = 120;
        var t2 = new SimuLean.Serialization.ReactionConfig("tick");
        t2.Set("signal", "Every");
        t2.Set("period", 30.0);
        nightTicks.Reactions.Add(t2);
        var nt = RunConfig(nightTicks);
        Check(nt.results.Info.EndReason == "Horizon" && nt.results.Find(SimuLean.Results.ResultScope.Element, "sink")[SimuLean.Results.ResultMetrics.Input] == 20,
              $"shift + ticks: all 20 jobs done over the nights, no false end ({nt.results.Info.EndReason})");

        // invalid settings fail the build
        bool threw = false;
        try { RunConfig(WithTicks(-1)); } catch (ArgumentException) { threw = true; }
        Check(threw, "a negative time fails");
    }

    static void TestDeadlockThroughList()
    {
        var clock = new SimClock(1);
        var model = new SimuLean.Headless.HeadlessModelFactory(clock);
        model.BuildModel(SimuLean.Testing.ReferenceModels.YardListDeadlock());
        model.StartModel();
        clock.AdvanceClock(200);
        var run = clock.Context.Run;
        string all = string.Join(" | ", run.Waiting);
        Check(run.Deadlocked, "deadlocked: " + all);
        Check(run.Waiting.Exists(w => w.StartsWith("bayA: RECEIVING") && w.Contains("waits for components from list yardList") && !w.Contains("erected")),
              "bayA waits for its component list, not downstream: " + all);
        Check(run.Waiting.Exists(w => w.StartsWith("yard:") && w.Contains("waits for list yardList (pulled by bayA, bayB)")), "the yard waits for the list's pullers: " + all);
    }

    static void TestCombinerStarved()
    {
        string warned = null, info = null;
        var oldSink = SimLog.WarningSink;
        var oldInfo = SimLog.InfoSink;
        SimLog.WarningSink = w => warned = w;
        SimLog.InfoSink = m => { if (m.Contains("Finished")) info = m; };
        try
        {
            var cfg = SimuLean.Testing.ReferenceModels.CombinerStarved();
            var clock = new SimClock(cfg.Seed);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(cfg);
            model.StartModel();
            clock.AdvanceClock(cfg.MaxSimulationTime);
            var r = SimuLean.Results.RunResults.Capture(model);
            var m = r.Find(SimuLean.Results.ResultScope.Model, "model");
            Check(r.Find(SimuLean.Results.ResultScope.Element, "erected")[SimuLean.Results.ResultMetrics.Input] == 2
                  && model.Find("assembly").GetQueueLength() == 1 && model.Find("rack").GetQueueLength() == 1, $"both blocks out; a kit left in the Combiner and one in the rack (asm q={model.Find("assembly").GetQueueLength()} content={model.Find("assembly").GetStatsContentCurrent()} in={model.Find("assembly").GetStatsInputCount()} out={model.Find("assembly").GetStatsOutputCount()} state={model.Find("assembly").CurrentState.Name}; rack q={model.Find("rack").GetQueueLength()})");
            Check(m[SimuLean.Results.ResultMetrics.Deadlocked] == 0 && m[SimuLean.Results.ResultMetrics.Stalled] == 0 && warned == null,
                  "kits waiting for a block that will never come: not a deadlock, not a stall (no material)");
            Check(m.TryGet(SimuLean.Results.ResultMetrics.FinishTime, out double fin) && m[SimuLean.Results.ResultMetrics.Makespan] == fin,
                  $"finished when the last block is out ({fin})");
            Check(info != null && info.Contains("assembly: ") && info.Contains("waits for its main input prep") && !info.Contains("waits for erected"),
                  $"the Combiner holding kits only is reported waiting for its main input, upstream: {info}");

            // with a main item that will never be completed it is still a stall (material missing is the main's parts)
            var stall = SimuLean.Testing.ReferenceModels.StallCombiner();
            var clock2 = new SimClock(stall.Seed);
            var model2 = new SimuLean.Headless.HeadlessModelFactory(clock2);
            model2.BuildModel(stall);
            model2.StartModel();
            clock2.AdvanceClock(1000);
            Check(clock2.Context.Run.Stalled && clock2.Context.Run.Waiting.Count == 1 && clock2.Context.Run.Waiting[0].StartsWith("comb:")
                  && !clock2.Context.Run.Waiting[0].Contains("main input"), $"a main item waiting for parts stays a stall ({string.Join(" | ", clock2.Context.Run.Waiting)})");

        }
        finally { SimLog.WarningSink = oldSink; SimLog.InfoSink = oldInfo; }
    }

    static void TestFinishedWithSurplus()
    {
        SimuLean.Results.RunResults Run(SimuLean.Serialization.SimulationConfig cfg)
        {
            var clock = new SimClock(cfg.Seed);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(cfg);
            model.StartModel();
            clock.AdvanceClock(cfg.MaxSimulationTime);
            return SimuLean.Results.RunResults.Capture(model);
        }
        string warned = null;
        var oldSink = SimLog.WarningSink;
        SimLog.WarningSink = w => warned = w;
        try
        {
            var done = Run(SimuLean.Testing.ReferenceModels.FinishWithSurplus());
            var m = done.Find(SimuLean.Results.ResultScope.Model, "model");
            double hull = done.Find(SimuLean.Results.ResultScope.Element, "hull1")[SimuLean.Results.ResultMetrics.Input]
                          + done.Find(SimuLean.Results.ResultScope.Element, "hull2")[SimuLean.Results.ResultMetrics.Input];
            Check(hull == 4, $"the 4 blocks reach the hull ({hull})");
            Check(!done.Info.Deadlocked && m[SimuLean.Results.ResultMetrics.Deadlocked] == 0 && m[SimuLean.Results.ResultMetrics.Stalled] == 0 && warned == null,
                  "kits left behind a full rack by an unlimited source: not a deadlock, not a stall, not warned (#26)");
            Check(done.Info.EndReason == "Finished" && m[SimuLean.Results.ResultMetrics.FinishTime] == done.Info.EndTime,
                  $"\"when empty\" ends the finished run ({done.Info.EndReason} at {done.Info.EndTime})");
            Check(m.TryGet(SimuLean.Results.ResultMetrics.Makespan, out double span) && span == done.Info.EndTime && span < 100,
                  $"makespan = the last entry into the hull, without a sink ({span}) (#27)");

            var byHull = Run(SimuLean.Testing.ReferenceModels.EndAtStorage());
            Check(byHull.Info.EndReason == "StopAtCount" && byHull.Info.EndTime == span,
                  $"end condition on the hull queues (any elements): stops at the 4th block ({byHull.Info.EndTime}) (#27)");
        }
        finally { SimLog.WarningSink = oldSink; }
    }

    static void TestRunControl()
    {
        SimuLean.Results.RunResults Run(SimuLean.Serialization.SimulationConfig cfg, double until)
        {
            var clock = new SimClock(cfg.Seed);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(cfg);
            model.StartModel();
            clock.AdvanceClock(until);
            return SimuLean.Results.RunResults.Capture(model);
        }
        var stopped = Run(SimuLean.Testing.ReferenceModels.EndAtSinkCount(), 2000);
        double end = stopped.Info.EndTime;
        var sink = stopped.Find(SimuLean.Results.ResultScope.Element, "sink");
        Check(sink[SimuLean.Results.ResultMetrics.Input] == 500 && stopped.Info.EndReason == "StopAtCount" && end < 2000,
              $"stops at the 500th item (t = {end}, {stopped.Info.EndReason})");
        Check(stopped.Find(SimuLean.Results.ResultScope.Model, "model")[SimuLean.Results.ResultMetrics.Makespan] == end, "makespan = the stop time");
        // the same model run without the end condition up to that time gives the same statistics
        var plain = SimuLean.Testing.ReferenceModels.BasicLine();
        var upTo = Run(plain, end);
        bool same = true;
        foreach (var o in upTo.Objects)
        {
            if (o.Scope == SimuLean.Results.ResultScope.Model) continue;
            var other = stopped.Find(o.Scope, o.Id);
            foreach (var m in o.Metrics) same &= other != null && other.TryGet(m.Key, out double v) && Math.Abs(v - m.Value) < 1e-9;
        }
        Check(same, "every statistic equals a run to the stop time without the condition");

        var empty = Run(SimuLean.Testing.ReferenceModels.EndWhenEmpty(), 5000);
        Check(empty.Info.EndReason == "Empty" && empty.Info.EndTime < 400 && empty.Find(SimuLean.Results.ResultScope.Element, "sink")[SimuLean.Results.ResultMetrics.Input] == 300,
              $"model empty: stops when the 300 items are out (t = {empty.Info.EndTime})");

        string warned = null;
        var oldSink = SimLog.WarningSink;
        SimLog.WarningSink = w => warned = w;
        try
        {
            var flagged = Run(SimuLean.Testing.ReferenceModels.DeadlockDeadEnd(), 1000);
            Check(flagged.Info.Deadlocked && flagged.Info.EndReason == "Horizon" && flagged.Info.EndTime == 1000, "deadlock flagged; without an end condition the run goes on");
            Check(flagged.Info.Waiting.Count == 2 && flagged.Info.Waiting[1] == "buffer: full (2/2), no outputs" && flagged.Info.Waiting[0].StartsWith("srv: BLOCKED") && flagged.Info.Waiting[0].Contains("waits for buffer"),
                  $"the waiting elements and what they wait for: {string.Join(" | ", flagged.Info.Waiting)}");
            Check(warned != null && warned.Contains("Deadlock at t = 5"), $"warned once: {warned}");
            var stopCfg = SimuLean.Testing.ReferenceModels.DeadlockDeadEnd();
            stopCfg.StopWhenEmpty = true;
            var stopAtDeadlock = Run(stopCfg, 1000);
            Check(stopAtDeadlock.Info.EndReason == "Deadlock" && stopAtDeadlock.Info.EndTime == 5, $"\"when empty\" also stops at a deadlock (t = {stopAtDeadlock.Info.EndTime})");

            warned = null;
            var stalled = Run(SimuLean.Testing.ReferenceModels.StallCombiner(), 1000);
            var model = stalled.Find(SimuLean.Results.ResultScope.Model, "model");
            Check(!stalled.Info.Deadlocked && model[SimuLean.Results.ResultMetrics.Stalled] == 1 && model[SimuLean.Results.ResultMetrics.StallTime] == 20 && warned == null,
                  "a starved kit stalls (nothing blocked): flagged, not a deadlock, not warned");
            var stopStall = SimuLean.Testing.ReferenceModels.StallCombiner();
            stopStall.StopWhenEmpty = true;
            var stopAtStall = Run(stopStall, 1000);
            Check(stopAtStall.Info.EndReason == "Stalled" && stopAtStall.Info.EndTime == 20, $"\"when empty\" stops when it stalls (t = {stopAtStall.Info.EndTime})");
        }
        finally { SimLog.WarningSink = oldSink; }

        bool threw = false;
        try { var bad = SimuLean.Testing.ReferenceModels.BasicLine(); bad.StopAt.Add("nope"); bad.StopAtCount = 1; Run(bad, 10); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "an end condition on a missing element fails the build");
    }

    static void TestDeadlockNamesFullBufferAtRest()
    {
        // srv blocked behind a dead-end buffer of 2: the buffer waits for nothing, but it is what holds srv up
        var r = RunConfig(SimuLean.Testing.ReferenceModels.DeadlockDeadEnd());
        var run = r.clock.Context.Run;
        Check(run.Deadlocked, "the dead end deadlocks");
        Check(run.Waiting.Exists(w => w.StartsWith("srv: BLOCKED")), $"the blocked feeder is named ({string.Join(" | ", run.Waiting)})");
        Check(run.Waiting.Contains("buffer: full (2/2), no outputs"), $"the full buffer at rest is named ({string.Join(" | ", run.Waiting)})");
        Check(r.results.Info.Waiting.Contains("buffer: full (2/2), no outputs"), "the results carry it");
    }
}
