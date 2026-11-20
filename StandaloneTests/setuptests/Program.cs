// Standalone tests of station setups (changeovers): StationSetup on the Multi-Server, Combiner and single-job
// stations — when (None / EachItem / LabelChange, first item, per slot), how long (own stream), who (the machine alone, a crew
// of another team, the process's resources kept), downtime pausing a setup, statistics, the build path and the catalog;
// the setup matrix (from → to times from a data table, wildcards, distributions), setups on the Assembly Station,
// the Stacker and the Erection Dock, and a queue grouping items by the receiver's setup (with its anti-starvation limit).
// Run: dotnet run  (from StandaloneTests/setuptests)
using System;
using System.Collections.Generic;
using System.Linq;
using SimuLean;
using SimuLean.Headless;
using SimuLean.Optimization;
using SimuLean.Resources;
using SimuLean.Results;
using SimuLean.Serialization;
using SimuLean.Testing;

static class Program
{
    static int passed, failed;

    static void Check(bool cond, string what)
    {
        if (cond) passed++;
        else { failed++; Console.WriteLine("  FAIL: " + what); }
    }

    static void Near(double actual, double expected, string what, double tol = 1e-9)
        => Check(Math.Abs(actual - expected) <= tol, $"{what}: expected {expected}, got {actual}");

    static int Main()
    {
        Run("No setup by default: the process times are unchanged (null setup, rule None, config without keys)", TestNoSetup);
        Run("EachItem: a setup before every item's process; state SETUP, statistics", TestEachItem);
        Run("LabelChange: A,A,B,B,A gives 3 setups with setupFirst, 2 without; numeric labels", TestLabelChange);
        Run("LabelChange on a Multi-Server: the previous item is tracked per slot", TestPerSlot);
        Run("Setup crew of another team; no crew = the machine alone even with a process operator", TestSetupCrew);
        Run("Keep for process: the process's resources set up and stay (one request per job)", TestKeepForProcess);
        Run("A stop during a setup pauses it and shifts its end (ScheduleWork)", TestStopDuringSetup);
        Run("A stopped station's setup request waits for its resume", TestSetupRequestWaitsWhileDown);
        Run("Warm-up resets the setup statistics", TestWarmup);
        Run("Combiner and Picking Station set up too", TestCombinerAndPicking);
        Run("Build path: keys → StationSetup (rule, label, stream, crew Task = Setup, keep), errors", TestBuild);
        Run("Run results: setups / setup_time / setup_time_avg / state:SETUP only with a setup; reproducible; own stream", TestResults);
        Run("Preemption: a setup crew can be taken (Resume keeps the setup's remaining time)", TestPreemptSetupCrew);
        Run("Catalog: setup keys decided; time numbers are factors, rule a choice, first / keep bools", TestCatalog);
        Run("Reference model setup_changeover builds, runs and sets up", TestReferenceModel);
        Run("Setup matrix: lookup, wildcards, the first setup, errors of a pair given twice", TestMatrixLookup);
        Run("Setup matrix on a line: the times per pair, the setup time for pairs it does not list", TestMatrixLine);
        Run("Setup matrix from a data table: numbers and distributions, own streams per cell, build errors", TestMatrixBuild);
        Run("Assembly Station: a setup per job before its first task (LabelChange, crew of its own, keep ignored)", TestAssemblySetup);
        Run("Stacker: a changeover before an item's cycle, the item waiting at the pick point", TestStackerSetup);
        Run("Erection Dock: a front prepared before a block (EachItem / LabelChange per front, keep for the erection)", TestErectionSetup);
        Run("Grouping by setup: a queue offers first what the station is set up for; the limit lets the oldest go", TestGrouping);
        Run("Reference model setup_matrix_grouping: groups, sets up by the matrix, reproducible, catalog decided", TestMatrixGroupingModel);
        Run("SetupMatrix.Describe: each row as read — the first setup, any, a distribution, the rest counted", TestMatrixDescribe);

        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    sealed class At : IEvent { readonly Action a; public At(Action a) { this.a = a; } public void Execute() => a(); }
    static void Schedule(SimClock c, double t, Action a) => c.ScheduleEvent(new At(a), t - c.GetSimulationTime());

    /// <summary>Offers its items in order, as fast as the next element takes them.</summary>
    sealed class Feeder : Element
    {
        private readonly Queue<Item> items;
        public Feeder(SimClock c, string n, IEnumerable<Item> its) : base(n, c) { items = new Queue<Item>(its); }
        public override int GetQueueLength() => items.Count;
        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item i) => false;
        public override bool Receive(Item i) => false;
        public override bool Unblock()
        {
            bool any = false;
            while (items.Count > 0 && SendOut(items.Peek())) { items.Dequeue(); any = true; }
            return any;
        }
    }

    static List<Item> Items(params string[] labels)
    {
        var list = new List<Item>();
        foreach (string l in labels) { var it = new Item(0); if (l != null) it.SetLabelValue("type", l); list.Add(it); }
        return list;
    }

    /// <summary>Exit times of the sink's items, in order.</summary>
    sealed class Recorder
    {
        public readonly List<double> Times = new List<double>();
        public Recorder(Element sink) { sink.ItemEntered += (e, i) => Times.Add(e.GetSimClock().GetSimulationTime()); }
    }

    static (MultiServer ms, Sink sink, Feeder feed, Recorder rec) Line(SimClock clock, double process, int capacity, IEnumerable<Item> items,
                                                                       StationSetup setup = null)
    {
        var times = new IRandomSampler[capacity];
        for (int i = 0; i < capacity; i++) times[i] = new ConstantRandomProcess(process);
        var ms = new MultiServer(times, "M", clock) { Setup = setup };
        var sink = new Sink("Sink", clock);
        var feed = new Feeder(clock, "Feed", items);
        GeneralLink.CreateLink(feed, new List<Element> { ms });
        GeneralLink.CreateLink(ms, new List<Element> { sink });
        return (ms, sink, feed, new Recorder(sink));
    }

    static void Go(SimClock clock, double until, Feeder feed, params Element[] elements)
    {
        foreach (var e in elements) e.Start();
        feed.Start();
        Schedule(clock, 0, () => feed.Unblock());
        clock.AdvanceClock(until);
    }

    static StationSetup Each(double t) => new StationSetup { When = SetupWhen.EachItem, Time = new ConstantRandomProcess(t) };

    static string Seq(List<double> t) => string.Join(",", t.Select(x => x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));

    // ── tests ────────────────────────────────────────────────────────────

    static void TestNoSetup()
    {
        foreach (var setup in new[] { null, new StationSetup { When = SetupWhen.None, Time = new ConstantRandomProcess(5) } })
        {
            var clock = new SimClock(1);
            var (ms, sink, feed, rec) = Line(clock, 2, 1, Items("A", "B", "A"), setup);
            Go(clock, 50, feed, ms, sink);
            Check(Seq(rec.Times) == "2,4,6", $"rule {(setup == null ? "none" : "None")}: items leave at 2, 4, 6 ({Seq(rec.Times)})");
            Near(ms.GetTimeInState(ElementState.SETUP), 0, "no time in SETUP");
        }
        var c = ReferenceModels.BasicLine();
        var clock2 = new SimClock(c.Seed);
        var model = new HeadlessModelFactory(clock2);
        model.BuildModel(c);
        Check(((ISetupStation)model.Find("srv")).Setup == null, "a config without setup keys builds no setup");
    }

    static void TestEachItem()
    {
        var clock = new SimClock(1);
        var (ms, sink, feed, rec) = Line(clock, 2, 1, Items("A", "A", "A"), Each(1));
        bool sawSetup = false;
        Schedule(clock, 0.5, () => sawSetup = ms.CurrentState == ElementState.SETUP && ms.Slots[0].InSetup);
        Go(clock, 50, feed, ms, sink);
        Check(sawSetup, "the station is SETUP during the setup (its slot too)");
        Check(Seq(rec.Times) == "3,6,9", $"each cycle = setup 1 + process 2 ({Seq(rec.Times)})");
        Check(ms.Setup.Count == 3, $"3 setups ({ms.Setup.Count})");
        Near(ms.Setup.TotalTime, 3, "setup_time");
        Near(ms.Setup.MeanTime, 1, "setup_time_avg");
        Near(ms.GetTimeInState(ElementState.SETUP), 3, "time in SETUP");
        Near(ms.GetTimeInState(ElementState.PROCESSING), 6, "time PROCESSING (setups are not processing)");
        Near(ms.GetUtilization(), 6.0 / 50, "utilisation = PROCESSING share only");
        Near(ms.GetMeanServiceTime(), 2, "the mean service time is the process's");
    }

    static void TestLabelChange()
    {
        foreach (bool first in new[] { true, false })
        {
            var clock = new SimClock(1);
            var setup = new StationSetup { When = SetupWhen.LabelChange, Label = "type", First = first, Time = new ConstantRandomProcess(1) };
            var (ms, sink, feed, rec) = Line(clock, 2, 1, Items("A", "A", "B", "B", "A"), setup);
            Go(clock, 50, feed, ms, sink);
            string expected = first ? "3,5,8,10,13" : "2,4,7,9,12";
            Check(ms.Setup.Count == (first ? 3 : 2), $"setupFirst {first}: {(first ? 3 : 2)} setups ({ms.Setup.Count})");
            Check(Seq(rec.Times) == expected, $"setupFirst {first}: exits {expected} ({Seq(rec.Times)})");
        }
        // numeric labels: 1, 1, 2 (compared as invariant text); an item without the label is a change too
        {
            var clock = new SimClock(1);
            var items = new List<Item>();
            foreach (double v in new[] { 1.0, 1.0, 2.0 }) { var it = new Item(0); it.SetLabelValue("ref", v); items.Add(it); }
            items.Add(new Item(0));   // no 'ref'
            var setup = new StationSetup { When = SetupWhen.LabelChange, Label = "ref", First = false, Time = new ConstantRandomProcess(1) };
            var (ms, sink, feed, rec) = Line(clock, 2, 1, items, setup);
            Go(clock, 50, feed, ms, sink);
            Check(ms.Setup.Count == 2, $"numeric: 1 → 2 and 2 → (none) are changes ({ms.Setup.Count})");
        }
    }

    static void TestPerSlot()
    {
        // two slots, A and B arrive together: each slot sets up for its first item (setup.setupFirst); then A goes to slot 0
        // (the first idle one) and B to slot 1 again — no change at either slot.
        var clock = new SimClock(1);
        var setup = new StationSetup { When = SetupWhen.LabelChange, Label = "type", First = true, Time = new ConstantRandomProcess(1) };
        var (ms, sink, feed, rec) = Line(clock, 2, 2, Items("A", "B", "A", "B"), setup);
        Go(clock, 50, feed, ms, sink);
        Check(ms.Setup.Count == 2, $"one setup per slot for its first item, none after ({ms.Setup.Count})");
        Check(Seq(rec.Times) == "3,3,5,5", $"exits ({Seq(rec.Times)})");
        Near(ms.Slots[0].GetTimeInState(ElementState.SETUP), 1, "slot 0 set up once");
    }

    static void TestSetupCrew()
    {
        var clock = new SimClock(1);
        var ops = new ResourceDispatcher("Ops", clock); var op = new TaskExecutor("Op", clock); ops.Add(op);
        var setters = new ResourceDispatcher("Setters", clock); var setter = new TaskExecutor("Setter", clock); setters.Add(setter);
        var setup = Each(1);
        setup.Resources = new StationResources(setters) { Task = TaskType.Setup };
        var (ms, sink, feed, rec) = Line(clock, 2, 1, Items("A", "A"), setup);
        ms.Resources = new StationResources(ops);
        bool opIdleDuringSetup = false, setterSetsUp = false;
        Schedule(clock, 0.5, () => { opIdleDuringSetup = op.IsAvailable; setterSetsUp = setter.CurrentState == ElementState.SETUP; });
        foreach (var e in new Element[] { ms, sink, op, setter }) e.Start();
        feed.Start(); ops.Start(); setters.Start();
        Schedule(clock, 0, () => feed.Unblock());
        clock.AdvanceClock(50);
        Check(opIdleDuringSetup && setterSetsUp, "during the setup the setter is SETUP and the process operator is free");
        Check(Seq(rec.Times) == "3,6", $"setup by the setter, then the process by the operator ({Seq(rec.Times)})");
        Near(setter.GetTimeInState(ElementState.SETUP), 2, "the setter's time in SETUP");
        Near(op.GetTimeInState(ElementState.PROCESSING), 4, "the operator's time PROCESSING");
        Near(op.GetTimeInState(ElementState.SETUP), 0, "the operator never sets up");
        Check(setter.TasksDone == 2 && op.TasksDone == 2, "one setup task and one process task per job");

        // a setup crew's requests are of type Setup (a team's request order on taskType can tell them apart)
        var clock3 = new SimClock(1);
        var setters3 = new ResourceDispatcher("Setters", clock3); var setter3 = new TaskExecutor("Setter", clock3); setters3.Add(setter3);
        var crew = new StationResources(setters3) { Task = TaskType.Setup };
        var busy = new StationResources(setters3);
        var host = new SimuLean.MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "Host", clock3);
        foreach (var e in new Element[] { host, setter3 }) e.Start();
        setters3.Start();
        busy.Acquire(host, new Item(0), () => { });          // the only setter busy
        crew.Acquire(host, new Item(1), () => { });          // the setup crew's request waits
        var pending = setters3.Requests.Entries.Select(x => x.Value as ResourceRequest).Where(r => r != null).ToList();
        Check(pending.Count == 1 && pending[0].TaskType == TaskType.Setup, $"the pending setup request is of type Setup ({string.Join(",", pending.Select(r => r.TaskType))})");

        // no crew: the machine sets up alone, even though its process needs an operator
        var clock2 = new SimClock(1);
        var ops2 = new ResourceDispatcher("Ops", clock2); var op2 = new TaskExecutor("Op", clock2); ops2.Add(op2);
        var (ms2, sink2, feed2, rec2) = Line(clock2, 2, 1, Items("A", "A"), Each(1));
        ms2.Resources = new StationResources(ops2);
        foreach (var e in new Element[] { ms2, sink2, op2 }) e.Start();
        feed2.Start(); ops2.Start();
        Schedule(clock2, 0, () => feed2.Unblock());
        clock2.AdvanceClock(50);
        Check(Seq(rec2.Times) == "3,6", $"machine alone: same cycle ({Seq(rec2.Times)})");
        Near(op2.GetTimeInState(ElementState.SETUP), 0, "machine alone: the operator does not set up");
        Near(op2.GetTimeInState(ElementState.PROCESSING), 4, "machine alone: the operator only processes");
    }

    static void TestKeepForProcess()
    {
        foreach (bool keep in new[] { true, false })
        {
            var clock = new SimClock(1);
            var ops = new ResourceDispatcher("Ops", clock); var op = new TaskExecutor("Op", clock); ops.Add(op);
            var setup = Each(1);
            if (keep) setup.KeepForProcess = true;
            else setup.Resources = new StationResources(ops) { Task = TaskType.Setup };   // same team, two requests
            var (ms, sink, feed, rec) = Line(clock, 2, 1, Items("A", "A"), setup);
            ms.Resources = new StationResources(ops);
            foreach (var e in new Element[] { ms, sink, op }) e.Start();
            feed.Start(); ops.Start();
            Schedule(clock, 0, () => feed.Unblock());
            clock.AdvanceClock(50);
            string k = keep ? "keep" : "no keep";
            Check(Seq(rec.Times) == "3,6", $"{k}: cycle setup 1 + process 2 ({Seq(rec.Times)})");
            Near(op.GetTimeInState(ElementState.SETUP), 2, $"{k}: the operator set up");
            Near(op.GetTimeInState(ElementState.PROCESSING), 4, $"{k}: and processed");
            Check(op.TasksDone == (keep ? 2 : 4), $"{k}: {(keep ? 2 : 4)} tasks ({op.TasksDone})");
            Check(ops.Served == (keep ? 2 : 4), $"{k}: {(keep ? "one request" : "two requests")} per job ({ops.Served})");
        }
    }

    static void TestStopDuringSetup()
    {
        var clock = new SimClock(1);
        var (ms, sink, feed, rec) = Line(clock, 2, 1, Items("A"), Each(4));
        StopToken stop = null;
        Schedule(clock, 1, () => stop = ms.Stop(new StopRequest(ElementState.BREAKDOWN)));
        Schedule(clock, 3, () => ms.Resume(stop));
        Go(clock, 50, feed, ms, sink);
        Check(Seq(rec.Times) == "8", $"setup 0 → 6 (paused 1 → 3), process 6 → 8 ({Seq(rec.Times)})");
        Near(ms.GetTimeInState(ElementState.SETUP), 4, "SETUP 4 (the stop is BREAKDOWN)");
        Near(ms.GetTimeInState(ElementState.BREAKDOWN), 2, "BREAKDOWN 2");
        Near(ms.Setup.TotalTime, 4, "setup_time = the sampled duration");
    }

    static void TestSetupRequestWaitsWhileDown()
    {
        var clock = new SimClock(1);
        var setters = new ResourceDispatcher("Setters", clock); var setter = new TaskExecutor("Setter", clock); setters.Add(setter);
        var setup = Each(1);
        setup.Resources = new StationResources(setters) { Task = TaskType.Setup };
        var (ms, sink, feed, rec) = Line(clock, 2, 1, Items("A"), setup);
        StopToken stop = null;
        foreach (var e in new Element[] { ms, sink, setter }) e.Start();
        feed.Start(); setters.Start();
        // stopped (output blocked only, so it still receives) when the item arrives: its setup request waits
        stop = ms.Stop(new StopRequest(ElementState.STOPPED) { BlockInput = false });
        Schedule(clock, 0, () => feed.Unblock());
        Schedule(clock, 5, () => ms.Resume(stop));
        clock.AdvanceClock(50);
        Check(Seq(rec.Times) == "8", $"setup requested at 0, served at the resume 5: 5 → 6 → 8 ({Seq(rec.Times)})");
    }

    static void TestWarmup()
    {
        var clock = new SimClock(1);
        var (ms, sink, feed, rec) = Line(clock, 2, 1, Items("A", "A", "A"), Each(1));
        Element.ScheduleWarmup(clock, new IWarmupStatistics[] { ms, sink }, 3.5);
        Go(clock, 50, feed, ms, sink);
        Check(ms.Setup.Count == 2, $"setups after the warm-up at 3.5: 4, 7 ({ms.Setup.Count})");
        Near(ms.Setup.TotalTime, 2, "setup_time after the warm-up");
    }

    static void TestCombinerAndPicking()
    {
        var clock = new SimClock(1);
        var comb = new Combiner(new[] { 1 }, new ConstantRandomProcess(2), "C", clock)
        {
            Setup = new StationSetup { When = SetupWhen.LabelChange, Label = "type", Time = new ConstantRandomProcess(1) },
        };
        var mains = new Feeder(clock, "Mains", Items("A", "A", "B"));
        var parts = new Feeder(clock, "Parts", Items(null, null, null));
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(mains, new List<Element> { comb });
        GeneralLink.CreateLink(parts, new List<Element> { comb });
        GeneralLink.CreateLink(comb, new List<Element> { sink });
        var rec = new Recorder(sink);
        foreach (var e in new Element[] { comb, sink, mains, parts }) e.Start();
        Schedule(clock, 0, () => { mains.Unblock(); parts.Unblock(); });
        clock.AdvanceClock(50);
        Check(comb.Setup.Count == 2, $"Combiner: setups for A (first) and B ({comb.Setup.Count})");
        Check(Seq(rec.Times) == "3,5,8", $"Combiner: exits ({Seq(rec.Times)})");
        Near(comb.GetTimeInState(ElementState.SETUP), 2, "Combiner: time in SETUP");

        // a Picking Station (a single-job station) built from its config
        var c = ReferenceModels.PickingModel();
        var pick = c.Elements.Find(e => e.Type == "PickingStation");
        pick.Set("setup.when", "EachItem");
        pick.Set("setup.time", "Constant~0.5");
        var clock2 = new SimClock(c.Seed);
        var model = new HeadlessModelFactory(clock2);
        model.BuildModel(c);
        model.StartModel();
        clock2.AdvanceClock(200);
        var station = (PickingStation)model.Find(pick.Name);
        Check(station.Setup != null && station.Setup.Count > 0, $"Picking Station: sets up ({station.Setup?.Count})");
        Near(station.Setup.TotalTime, 0.5 * station.Setup.Count, "Picking Station: 0.5 per setup");
        Check(station.GetTimeInState(ElementState.SETUP) > 0, "Picking Station: time in SETUP");
    }

    static SimulationConfig SetupLine(string when, string time = "Constant~1", Action<ElementConfig> more = null)
    {
        var c = ReferenceModels.BasicLine();
        var srv = c.Elements.Find(e => e.Id == "srv");
        if (when != null) srv.Set("setup.when", when);
        srv.Set("setup.time", time);
        more?.Invoke(srv);
        return c;
    }

    static StationSetup BuildSetup(SimulationConfig c, out HeadlessModelFactory model)
    {
        var clock = new SimClock(c.Seed);
        model = new HeadlessModelFactory(clock);
        model.BuildModel(c);
        return ((ISetupStation)model.Find("srv")).Setup;
    }

    static bool Fails(Action a) { try { a(); return false; } catch (Exception) { return true; } }

    static void TestBuild()
    {
        var s = BuildSetup(SetupLine("LabelChange", "Uniform~1~3", e => { e.Set("setup.label", "ref"); e.Set("setup.setupFirst", false); }), out _);
        Check(s != null && s.When == SetupWhen.LabelChange && s.Label == "ref" && !s.First && s.Time is SimuLean.Stochastics.Distributions.UniformDistribution,
              "rule, label, first and the time's distribution");
        Check(s.Resources == null && !s.KeepForProcess, "no crew: the machine alone");
        Check(BuildSetup(SetupLine("None"), out _) == null, "rule None builds no setup");

        // a crew of its own: a use of the team, of type Setup
        var withCrew = SetupLine("EachItem", more: e =>
        {
            e.Set("setup.setupBy.team", "setters");
            e.Set("setup.setupBy.priority", "4");
            e.Set("setup.setupBy.preemption", "Resume");
            e.Set("setup.setupBy.preemptable", false);
        });
        withCrew.ResourceTeams.Add(Team("setters", "Setter"));
        s = BuildSetup(withCrew, out _);
        Check(s.Resources != null && s.Resources.Task == TaskType.Setup && s.Resources.Dispatcher.Name == "setters"
              && s.Resources.Priority(null) == 4 && s.Resources.Preempt == PreemptMode.Resume && !s.Resources.Preemptable,
              "crew: team, task type Setup, priority, preemption, preemptable");

        // keep: only with the process's own team
        var keep = SetupLine("EachItem", more: e =>
        {
            e.Set("setup.setupBy.team", "ops"); e.Set("setup.keepForProcess", true);
            e.Set("processBy.team", "ops");
        });
        keep.ResourceTeams.Add(Team("ops", "Op"));
        s = BuildSetup(keep, out _);
        Check(s.KeepForProcess && s.Resources == null, "keep with the process team: the process's resources do it (no crew of its own)");
        var keepOther = SetupLine("EachItem", more: e =>
        {
            e.Set("setup.setupBy.team", "setters"); e.Set("setup.keepForProcess", true);
            e.Set("processBy.team", "ops");
        });
        keepOther.ResourceTeams.Add(Team("ops", "Op")); keepOther.ResourceTeams.Add(Team("setters", "Setter"));
        s = BuildSetup(keepOther, out _);
        Check(!s.KeepForProcess && s.Resources != null, "keep with another team: ignored (warning), the crew is its own");

        Check(Fails(() => BuildSetup(SetupLine("Sometimes"), out _)), "an unknown rule fails the build");
        Check(Fails(() => BuildSetup(SetupLine("LabelChange", more: e => e.Set("setup.label", " ")), out _)), "LabelChange without a label fails");
        var onQueue = ReferenceModels.BasicLine();
        Check(Fails(() => onQueue.Elements.Find(e => e.Id == "q").Set("setup.when", "EachItem")),
              "a setup on a type that does not set up fails when written (its settings have no setup group)");
    }

    static ResourceTeamConfig Team(string id, params string[] members)
    {
        var t = new ResourceTeamConfig { Id = id, Name = id };
        foreach (var m in members) t.Members.Add(new ResourceMemberConfig { Name = m, Properties = new List<string>() });
        return t;
    }

    static RunResults RunConfig(SimulationConfig c, double horizon, out HeadlessModelFactory model)
    {
        var clock = new SimClock(c.Seed);
        model = new HeadlessModelFactory(clock);
        model.BuildModel(c);
        model.StartModel();
        clock.AdvanceClock(horizon);
        return RunResults.Capture(model, "setup");
    }

    static void TestResults()
    {
        var plain = RunConfig(ReferenceModels.BasicLine(), 500, out _);
        var srvPlain = plain.Find(ResultScope.Element, "srv");
        Check(!srvPlain.TryGet(ResultMetrics.Setups, out _) && !srvPlain.TryGet(ResultMetrics.State("SETUP"), out _),
              "without a setup: no setup statistics (the results keep their shape)");

        var r = RunConfig(SetupLine("EachItem", "Uniform~0.1~0.3"), 500, out var model);
        var srv = r.Find(ResultScope.Element, "srv");
        var setup = ((ISetupStation)model.Find("srv")).Setup;
        Check(srv.TryGet(ResultMetrics.Setups, out double n) && n == setup.Count && n > 0, $"setups ({n})");
        Check(srv.TryGet(ResultMetrics.SetupTime, out double t) && Math.Abs(t - setup.TotalTime) < 1e-9, "setup_time");
        Check(srv.TryGet(ResultMetrics.SetupTimeAvg, out double avg) && avg > 0.1 && avg < 0.3, $"setup_time_avg in (0.1, 0.3): {avg}");
        Check(srv.TryGet(ResultMetrics.State("SETUP"), out double share) && share > 0, "state:SETUP");

        // reproducible under the seed
        var again = RunConfig(SetupLine("EachItem", "Uniform~0.1~0.3"), 500, out _);
        var srv2 = again.Find(ResultScope.Element, "srv");
        srv2.TryGet(ResultMetrics.SetupTime, out double t2); srv2.TryGet(ResultMetrics.Output, out double out2); srv.TryGet(ResultMetrics.Output, out double out1);
        Check(t2 == t && out2 == out1, "the same seed gives the same run");

        // own stream: with arrivals far apart (no queue), the process draws are the same with and without the setup
        SimulationConfig Spaced(bool withSetup)
        {
            var c = ReferenceModels.BasicLine();
            var src = c.Elements.Find(e => e.Id == "src");
            src.Set("interArrivalDistribution.strategyType", "Constant"); src.Set("interArrivalDistribution.constantValue", 100.0);
            var s = c.Elements.Find(e => e.Id == "srv");
            s.Set("delayStrategy.strategyType", "Uniform"); s.Set("delayStrategy.uniformMin", 1.0); s.Set("delayStrategy.uniformMax", 5.0);
            if (withSetup) { s.Set("setup.when", "EachItem"); s.Set("setup.time", "Uniform~1~5"); }
            return c;
        }
        RunConfig(Spaced(false), 5000, out var m0);
        RunConfig(Spaced(true), 5000, out var m1);
        var a = (MultiServer)m0.Find("srv"); var b = (MultiServer)m1.Find("srv");
        Check(a.GetStatsOutputCount() == b.GetStatsOutputCount() && a.GetStatsOutputCount() > 10, "the same number of jobs");
        Near(b.GetMeanServiceTime(), a.GetMeanServiceTime(), "the same process draws (the setup has its own stream)", 1e-12);
    }

    static void TestPreemptSetupCrew()
    {
        // one setter: a low-priority setup (4 long) at Low is taken at 1 by a high-priority process request at High
        // (Resume); Low's setup waits, paused, and goes on with its remaining 3 when the setter is back at 3.
        var clock = new SimClock(1);
        var setters = new ResourceDispatcher("Setters", clock); var setter = new TaskExecutor("Setter", clock); setters.Add(setter);
        var setup = Each(4);
        setup.Resources = new StationResources(setters) { Task = TaskType.Setup, Priority = _ => 1 };
        var (low, lowSink, feed, rec) = Line(clock, 2, 1, Items("A"), setup);
        var high = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(2) }, "High", clock);
        high.Resources = new StationResources(setters) { Priority = _ => 5, Preempt = PreemptMode.Resume };
        var highSink = new Sink("HighSink", clock);
        GeneralLink.CreateLink(high, new List<Element> { highSink });
        foreach (var e in new Element[] { low, lowSink, high, highSink, setter }) e.Start();
        feed.Start(); setters.Start();
        Schedule(clock, 0, () => feed.Unblock());
        Schedule(clock, 1, () => high.Receive(new Item(0)));
        bool waiting = false;
        Schedule(clock, 2, () => waiting = low.CurrentState == ElementState.WAITING_FOR_RESOURCE);
        clock.AdvanceClock(50);
        Check(waiting, "Low waits for its setter while its setup is paused");
        Check(Seq(rec.Times) == "8", $"Low: setup 0 → 1 and 3 → 6, process 6 → 8 ({Seq(rec.Times)})");
        Near(low.GetTimeInState(ElementState.SETUP), 4, "Low: SETUP 4");
        Check(setter.Preemptions == 1, "one preemption of the setter");
    }

    static void TestCatalog()
    {
        var c = SetupLine("LabelChange", "Uniform~1~3", e =>
        {
            e.Set("setup.label", "ref"); e.Set("setup.setupFirst", true); e.Set("setup.keepForProcess", false);
            e.Set("setup.setupBy.team", "setters"); e.Set("setup.setupBy.priority", "2"); e.Set("setup.setupBy.preemption", "None");
            e.Set("setup.setupBy.preemptable", true);
            e.Set("setup.setupBy.requirements", new List<RequirementSettings> { RequirementSettings.Of(1) });
        });
        c.ResourceTeams.Add(Team("setters", "Setter"));
        var undecided = ModelParameterCatalog.Undecided(c);
        Check(undecided.Count == 0, "every setup key decided" + (undecided.Count > 0 ? ": " + string.Join(", ", undecided) : ""));
        var offered = ModelParameterCatalog.ForElement(c.Elements.Find(e => e.Id == "srv"));
        ParameterInfo Find(string key) => offered.FirstOrDefault(p => p.Key == key);
        Check(Find("setup.time" + ".uniformMin") != null && Find("setup.time" + ".uniformMax") != null, "the setup time's numbers are factors");
        Check(Find("setup.when")?.Kind == VariableKind.Choice, "the rule is a choice");
        Check(Find("setup.setupFirst")?.Kind == VariableKind.Binary && Find("setup.keepForProcess")?.Kind == VariableKind.Binary, "first / keep are bools");
        Check(Find("setup.setupBy.priority")?.Kind == VariableKind.Real, "a constant setup priority is a number");
        Check(Find("setup.label") == null && Find("setup.setupBy.team") == null, "label and team are structural");
    }

    static void TestReferenceModel()
    {
        var kv = ReferenceModels.All().FirstOrDefault(x => x.Key == "setup_changeover");
        Check(kv.Value != null, "setup_changeover is a reference model");
        if (kv.Value == null) return;
        var r = RunConfig(kv.Value, kv.Value.MaxSimulationTime, out _);
        foreach (string id in new[] { "press", "paint" })
        {
            var o = r.Find(ResultScope.Element, id);
            Check(o != null && o.TryGet(ResultMetrics.Setups, out double n) && n > 0, $"{id} sets up");
        }
    }

    // ── setup matrix, more stations, grouping ─────────────────

    static IRandomSampler C(double v) => new ConstantRandomProcess(v);

    static void TestMatrixLookup()
    {
        var m = new SetupMatrix("m");
        m.Set("", "*", C(0.5));
        m.Set("A", "B", C(3));
        m.Set("A", "*", C(4));
        m.Set("*", "A", C(2));
        m.Set("*", "*", C(1));
        double T(string from, bool hasFrom, string to) => m.Find(from, hasFrom, to)?.NextValue() ?? double.NaN;
        Near(T(null, false, "B"), 0.5, "the first setup: from empty");
        Near(T("A", true, "B"), 3, "an exact pair");
        Near(T("A", true, "C"), 4, "(from, *) before (*, to)");
        Near(T("B", true, "A"), 2, "(*, to)");
        Near(T("B", true, "C"), 1, "(*, *)");
        Near(T(" A ", true, "B"), 3, "values are trimmed");
        var none = new SetupMatrix("n");
        none.Set("A", "B", C(3));
        Check(none.Find("B", true, "A") == null && none.Find(null, false, "A") == null, "no row applies: null (the setup time)");
        none.Set("*", "B", C(7));
        Near(none.Find(null, false, "B").NextValue(), 7, "the first setup matches a wildcard from too");
        Check(Fails(() => m.Set("A", " B", C(1))), "a pair given twice fails");
    }

    static StationSetup MatrixSetup(SetupMatrix m, double fallback)
        => new StationSetup { When = SetupWhen.LabelChange, Label = "type", First = true, Time = C(fallback), Matrix = m };

    static void TestMatrixLine()
    {
        foreach (bool any in new[] { true, false })
        {
            var m = new SetupMatrix("m");
            m.Set("", "*", C(0.5)); m.Set("A", "B", C(3)); m.Set("*", "A", C(2));
            if (any) m.Set("*", "*", C(1));
            var clock = new SimClock(1);
            var (ms, sink, feed, rec) = Line(clock, 2, 1, Items("A", "B", "A", "C"), MatrixSetup(m, 4));
            Go(clock, 50, feed, ms, sink);
            string expected = any ? "2.5,7.5,11.5,14.5" : "2.5,7.5,11.5,17.5";
            Check(Seq(rec.Times) == expected, $"{(any ? "with" : "without")} (*, *): first 0.5, A→B 3, B→A 2, A→C {(any ? "1" : "the setup time 4")} ({Seq(rec.Times)})");
            Near(ms.Setup.TotalTime, any ? 6.5 : 9.5, "setup_time sums the pairs' times");
        }
        // EachItem with a matrix: a pair of one value (a cleaning between two batches of the same type) has a time too
        {
            var m = new SetupMatrix("m");
            m.Set("A", "A", C(0.25)); m.Set("*", "*", C(1));
            var clock = new SimClock(1);
            var setup = new StationSetup { When = SetupWhen.EachItem, Label = "type", Matrix = m };
            var (ms, sink, feed, rec) = Line(clock, 2, 1, Items("A", "A"), setup);
            Go(clock, 50, feed, ms, sink);
            Check(Seq(rec.Times) == "3,5.25", $"EachItem: first (*, *) 1, then A→A 0.25 ({Seq(rec.Times)})");
        }
    }

    static DataTableConfig Table(string name, params (string from, string to, string time)[] rows)
        => new DataTableConfig
        {
            Name = name,
            Columns = new Dictionary<string, List<string>>
            {
                ["from"] = rows.Select(r => r.from).ToList(), ["to"] = rows.Select(r => r.to).ToList(), ["time"] = rows.Select(r => r.time).ToList(),
            },
        };

    static SimulationConfig TypedLine(Action<ElementConfig> more)
    {
        var c = SetupLine("LabelChange", "Uniform~0.1~0.3", e => { e.Set("setup.label", "type"); more?.Invoke(e); });
        c.Elements.Find(e => e.Id == "src").Set("createLabels", new List<string> { "type = pick(0.5, 'A', 0.5, 'B')" });
        return c;
    }

    static SimulationConfig MatrixLine(DataTableConfig table)
    {
        var c = TypedLine(e => e.Set("setup.matrix", table?.Name ?? "missing"));
        if (table != null) c.Tables.Add(table);
        return c;
    }

    static void TestMatrixBuild()
    {
        var t = Table("changeovers", ("", "*", "2"), ("A", "B", "Uniform~1~2"), ("B", "A", "Triangular~3~4~5"));
        var s = BuildSetup(MatrixLine(t), out _);
        Check(s.Matrix != null && s.Matrix.Count == 3 && s.Matrix.Name == "changeovers", "the matrix is read from the data table");
        Check(s.Matrix.Find(null, false, "A") is ConstantRandomProcess, "a number is a constant time");
        Check(s.Matrix.Find("A", true, "B") is SimuLean.Stochastics.Distributions.UniformDistribution, "a spec is its distribution");

        RunConfig(MatrixLine(t), 2000, out var model);
        var setup = ((ISetupStation)model.Find("srv")).Setup;
        Check(setup.Count > 20, $"setups ({setup.Count})");
        double avg = setup.MeanTime;
        Check(avg > 1 && avg < 5, $"the mean setup time lies between the pairs' (1–5): {avg}");
        RunConfig(MatrixLine(t), 2000, out var model2);
        var s2 = ((ISetupStation)model2.Find("srv")).Setup;
        Check(s2.Count == setup.Count && s2.TotalTime == setup.TotalTime, "the same seed gives the same setups");

        // own streams: a matrix whose pairs never occur leaves the run as with the setup time alone (the purpose 'setup'
        // of the setup time unchanged), and a constant cell draws nothing
        RunConfig(TypedLine(null), 2000, out var m0);
        RunConfig(MatrixLine(Table("unused", ("X", "Y", "Uniform~5~6"), ("Y", "X", "7"))), 2000, out var m1);
        var a = ((ISetupStation)m0.Find("srv")).Setup; var b = ((ISetupStation)m1.Find("srv")).Setup;
        Check(a.Count == b.Count && a.Count > 0 && Math.Abs(a.TotalTime - b.TotalTime) < 1e-12, "a matrix with no matching pair changes nothing");

        Check(Fails(() => BuildSetup(MatrixLine(null), out _)), "a missing table fails the build");
        var noTime = new DataTableConfig { Name = "bad", Columns = new Dictionary<string, List<string>> { ["from"] = new List<string> { "A" }, ["to"] = new List<string> { "B" } } };
        Check(Fails(() => BuildSetup(MatrixLine(noTime), out _)), "a table without the time column fails");
        Check(Fails(() => BuildSetup(MatrixLine(Table("dup", ("A", "B", "1"), ("A", "B", "2"))), out _)), "a pair given twice fails");
        Check(Fails(() => BuildSetup(MatrixLine(Table("spec", ("A", "B", "Sometimes~1"))), out _)), "an unknown distribution fails");
        Check(Fails(() => BuildSetup(MatrixLine(Table("neg", ("A", "B", "-1"))), out _)), "a negative time fails");
        Check(Fails(() => BuildSetup(MatrixLine(Table("empty", ("A", "B", " "))), out _)), "an empty time fails");
        try { BuildSetup(MatrixLine(Table("dup2", ("A", "B", "1"), ("A", "B", "2"))), out _); Check(false, "dup2 fails"); }
        catch (Exception e) { Check(e.Message.Contains("dup2"), "the error names the table: " + e.Message); }
    }

    static void TestAssemblySetup()
    {
        var clock = new SimClock(1);
        var spec = new AssemblyTaskSpec { Id = "t", Duration = C(2) };
        var asy = new AssemblyStation("Asy", clock, new List<AssemblyTaskSpec> { spec })
        {
            Setup = new StationSetup { When = SetupWhen.LabelChange, Label = "type", Time = C(1) },
        };
        var sink = new Sink("Sink", clock);
        var feed = new Feeder(clock, "Feed", Items("A", "A", "B"));
        GeneralLink.CreateLink(feed, new List<Element> { asy });
        GeneralLink.CreateLink(asy, new List<Element> { sink });
        var rec = new Recorder(sink);
        bool sawSetup = false;
        Schedule(clock, 0.5, () => sawSetup = asy.CurrentState == ElementState.SETUP && asy.InSetup && asy.RunningTaskCount == 0);
        Go(clock, 50, feed, asy, sink);
        Check(sawSetup, "SETUP before the first task, no task running");
        Check(Seq(rec.Times) == "3,5,8", $"A: setup + task, A: task, B: setup + task ({Seq(rec.Times)})");
        Check(asy.Setup.Count == 2, $"2 setups ({asy.Setup.Count})");
        Near(asy.GetTimeInState(ElementState.SETUP), 2, "time in SETUP");

        // built from its config: a crew of its own; keep for process ignored (its process resources are per task)
        var c = ReferenceModels.AssemblyModel();
        var e = c.Elements.Find(x => x.Id == "asy");
        e.Set("setup.when", "EachItem"); e.Set("setup.time", "Constant~0.5");
        e.Set("setup.setupBy.team", "ops"); e.Set("setup.keepForProcess", true);
        e.Set("processBy.team", "ops"); e.Set("processBy.release", "EndOfProcess");
        c.ResourceTeams.Add(Team("ops", "Op1", "Op2"));
        var r = RunConfig(c, 2000, out var model);
        var station = (AssemblyStation)model.Find("asy");
        Check(!station.Setup.KeepForProcess && station.Setup.Resources != null && station.Setup.Resources.Task == TaskType.Setup,
              "keep for process ignored: the crew is its own (Setup tasks)");
        var o = r.Find(ResultScope.Element, "asy");
        Check(o.TryGet(ResultMetrics.Setups, out double n) && n > 100, $"sets up once per job ({n})");
        Check(o.TryGet(ResultMetrics.Output, out double outN) && Math.Abs(n - outN) <= 1, $"one setup per job ({n} vs {outN})");
        Near(station.Setup.TotalTime, 0.5 * station.Setup.Count, "0.5 per setup");
    }

    static void TestStackerSetup()
    {
        var clock = new SimClock(1);
        var st = new Stacker("St", clock, 1, 2)
        {
            CycleTime = C(0.5),
            Setup = new StationSetup { When = SetupWhen.LabelChange, Label = "type", Time = C(1) },
        };
        var sink = new Sink("Sink", clock);
        var feed = new Feeder(clock, "Feed", Items("A", "A", "B", "B"));
        GeneralLink.CreateLink(feed, new List<Element> { st });
        GeneralLink.CreateLink(st, new List<Element> { sink });
        bool waitsAtPick = false;
        Schedule(clock, 0.5, () => waitsAtPick = st.CurrentState == ElementState.SETUP && st.InSetup && !st.CycleInProgress && st.GetFreeCapacity() == 0);
        Go(clock, 50, feed, st, sink);
        Check(waitsAtPick, "SETUP with the item at the pick point (no cycle yet)");
        Check(st.Setup.Count == 2, $"setups for A (first) and B ({st.Setup.Count})");
        Check(st.ItemsStacked == 4 && sink.GetNumberItems() == 2, $"4 items stacked, 2 stacks out ({st.ItemsStacked}, {sink.GetNumberItems()})");
        Near(st.GetTimeInState(ElementState.SETUP), 2, "time in SETUP");

        // built from its config (one build path)
        var c = ReferenceModels.StackerModel();
        var e = c.Elements.Find(x => x.Id == "st");
        e.Set("setup.when", "EachItem"); e.Set("setup.time", "Constant~0.1");
        var r = RunConfig(c, 200, out var model);
        var stacker = (Stacker)model.Find("st");
        Check(stacker.Setup != null && stacker.Setup.Count > 50 && stacker.Setup.Count >= stacker.ItemsStacked,
              $"a setup before every cycle ({stacker.Setup?.Count} for {stacker.ItemsStacked})");
        Check(r.Find(ResultScope.Element, "st").TryGet(ResultMetrics.SetupTime, out double t) && t > 0, "setup_time reported");
    }

    static void TestErectionSetup()
    {
        // precedence, two fronts, all blocks of one weight: LabelChange on weight sets a front up for its first block only
        var c = ReferenceModels.ErectionPrecedence();
        var dock = c.Elements.Find(x => x.Id == "dock");
        dock.Set("setup.when", "LabelChange"); dock.Set("setup.label", "weight"); dock.Set("setup.time", "Constant~1");
        var r = RunConfig(c, 500, out var model);
        var dk = (SimuLean.Naval.ErectionDock)model.Find("dock");
        Check(dk.ErectedCount == 6, "the hull is erected");
        // the crew of three covers one erection of two at a time, so every block is erected on the first front
        Check(dk.Setup.Count == 1 && dk.Fronts[1].GetTimeInState(ElementState.PROCESSING) == 0,
              $"one weight, one front used: only the front's first block sets up ({dk.Setup.Count})");
        Check(r.Find(ResultScope.Element, "dock").TryGet(ResultMetrics.State("SETUP"), out double share) && share > 0, "state:SETUP");

        // EachItem with the erection crew kept from the setup to the erection: one request per block
        var k = ReferenceModels.ErectionPrecedence();
        var kd = k.Elements.Find(x => x.Id == "dock");
        kd.Set("setup.when", "EachItem"); kd.Set("setup.time", "Constant~1"); kd.Set("setup.setupBy.team", "crew"); kd.Set("setup.keepForProcess", true);
        var rk = RunConfig(k, 500, out var mk);
        var dk2 = (SimuLean.Naval.ErectionDock)mk.Find("dock");
        Check(dk2.Setup.KeepForProcess && dk2.Setup.Count == 6, $"keep: 6 setups by the erection crew ({dk2.Setup.Count})");
        Check(rk.Find(ResultScope.Team, "crew").TryGet(ResultMetrics.Served, out double served) && served == 6, $"one request per block ({served})");
        Check(dk2.LastErectionTime > 46, $"the setups lengthen the erection ({dk2.LastErectionTime})");
    }

    static (ItemsQueue q, MultiServer ms, List<string> order) GroupLine(bool group, int limit, params string[] types)
    {
        var clock = new SimClock(1);
        var q = new ItemsQueue(100, "Q", clock) { GroupBySetup = group, GroupLimit = limit };
        var ms = new MultiServer(new IRandomSampler[] { C(1) }, "M", clock)
        {
            Setup = new StationSetup { When = SetupWhen.LabelChange, Label = "type", Time = C(1) },
        };
        var sink = new Sink("Sink", clock);
        var feed = new Feeder(clock, "Feed", Items(types));
        GeneralLink.CreateLink(feed, new List<Element> { q });
        GeneralLink.CreateLink(q, new List<Element> { ms });
        GeneralLink.CreateLink(ms, new List<Element> { sink });
        var order = new List<string>();
        sink.ItemEntered += (e, i) => order.Add(i.GetLabelText("type"));
        foreach (var e in new Element[] { q, ms, sink }) e.Start();
        feed.Start();
        Schedule(clock, 0, () => feed.Unblock());
        clock.AdvanceClock(100);
        return (q, ms, order);
    }

    static void TestGrouping()
    {
        var types = new[] { "A", "B", "A", "B", "A", "B" };
        var (_, plain, plainOrder) = GroupLine(false, 0, types);
        Check(plain.Setup.Count == 6 && string.Join("", plainOrder) == "ABABAB", $"no grouping: FIFO, 6 setups ({plain.Setup.Count}, {string.Join("", plainOrder)})");

        var (_, ms, order) = GroupLine(true, 0, types);
        Check(string.Join("", order) == "AAABBB" && ms.Setup.Count == 2, $"grouping without a limit: AAABBB, 2 setups ({string.Join("", order)}, {ms.Setup.Count})");

        // limit 1: A2 overtakes the oldest (B1) once, then B1 goes; B3 is then the oldest and matches; B5 overtakes A4
        // once, then A4 — A0 A2 B1 B3 B5 A4
        var (_, ms1, order1) = GroupLine(true, 1, types);
        Check(string.Join("", order1) == "AABBBA" && ms1.Setup.Count == 3, $"limit 1: AABBBA, 3 setups ({string.Join("", order1)}, {ms1.Setup.Count})");

        // anti-starvation: one B among many A — with a limit of 3 it leaves after the 3 A's that overtook it
        var many = new[] { "A", "B", "A", "A", "A", "A", "A", "A", "A" };
        var (_, _, orderMany) = GroupLine(true, 3, many);
        Check(string.Join("", orderMany) == "AAAABAAAA", $"limit 3: the B waits for 3 A's only ({string.Join("", orderMany)})");
        var (_, _, orderAll) = GroupLine(true, 0, many);
        Check(string.Join("", orderAll) == "AAAAAAAAB", $"no limit: the B goes last ({string.Join("", orderAll)})");

        // a receiver with EachItem is never 'set up for' an item: the queue releases unchanged
        var clock2 = new SimClock(1);
        var ms2 = new MultiServer(new IRandomSampler[] { C(1) }, "M2", clock2) { Setup = Each(1) };
        ms2.Start();
        Check(!ms2.IsSetUpFor(Items("A")[0]), "EachItem: never set up for the next item");
    }

    static void TestMatrixGroupingModel()
    {
        var kv = ReferenceModels.All().FirstOrDefault(x => x.Key == "setup_matrix_grouping");
        Check(kv.Value != null, "setup_matrix_grouping is a reference model");
        if (kv.Value == null) return;
        var r = RunConfig(kv.Value, kv.Value.MaxSimulationTime, out var model);
        var cell = r.Find(ResultScope.Element, "cell");
        Check(cell.TryGet(ResultMetrics.Setups, out double n) && n > 0, $"the cell sets up ({n})");
        cell.TryGet(ResultMetrics.Output, out double outN);
        Check(n < outN / 2, $"grouping: far fewer setups than items ({n} for {outN})");
        var setup = ((ISetupStation)model.Find("cell")).Setup;
        Check(setup.Matrix != null && setup.Matrix.Count == 5, "the matrix of 5 pairs");
        var again = RunConfig(ReferenceModels.SetupMatrixGrouping(), 2000, out _);
        again.Find(ResultScope.Element, "cell").TryGet(ResultMetrics.SetupTime, out double t2);
        cell.TryGet(ResultMetrics.SetupTime, out double t1);
        Check(t1 == t2, "reproducible under its seed");

        // without grouping the same model needs more setups
        var nog = ReferenceModels.SetupMatrixGrouping();
        nog.Elements.Find(e => e.Id == "q").Set("groupBySetup", false);
        var rn = RunConfig(nog, 2000, out _);
        rn.Find(ResultScope.Element, "cell").TryGet(ResultMetrics.Setups, out double nNo);
        Check(nNo > n, $"without grouping: more setups ({nNo} > {n})");

        var undecided = ModelParameterCatalog.Undecided(kv.Value);
        Check(undecided.Count == 0, "every key decided" + (undecided.Count > 0 ? ": " + string.Join(", ", undecided) : ""));
        var offered = ModelParameterCatalog.ForElement(kv.Value.Elements.Find(e => e.Id == "q"));
        Check(offered.Any(p => p.Key == "groupBySetup" && p.Kind == VariableKind.Binary), "groupBySetup is a bool factor");
        Check(offered.Any(p => p.Key == "groupLimit"), "groupLimit is a factor");
        var cellKeys = ModelParameterCatalog.ForElement(kv.Value.Elements.Find(e => e.Id == "cell"));
        Check(!cellKeys.Any(p => p.Key == "setup.matrix"), "setup.matrix is structural (the table is varied as data:<table>)");
    }

    static void TestMatrixDescribe()
    {
        var lines = SimuLean.SetupMatrix.Describe(new[] { "", "A", "*", "B" }, new[] { "*", "B", "*", "" }, new[] { "10", "5", "8", "Triangular~4~6~9" });
        Check(lines.Count == 4 && lines[0] == "(first setup) → any: 10" && lines[1] == "A → B: 5" && lines[2] == "any → any: 8"
              && lines[3] == "B → (no label): Triangular~4~6~9", "rows as read: " + string.Join(" | ", lines));
        var many = Enumerable.Range(0, 20).Select(i => "F" + i).ToList();
        var capped = SimuLean.SetupMatrix.Describe(many, many, many.Select(_ => "1").ToList());
        Check(capped.Count == SimuLean.SetupMatrix.RowsShown + 1 && capped[capped.Count - 1] == "… 8 more rows", "the rest counted: " + capped[capped.Count - 1]);
    }
}
