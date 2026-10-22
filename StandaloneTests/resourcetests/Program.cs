// Standalone tests of the resource core: Resource / TaskExecutor /
// TaskSequence — steps, states and statistics, holding, downtime, labels, travel modes, warm-up.
// Run: dotnet run  (from StandaloneTests/resourcetests)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimuLean;
using SimuLean.Collections;
using SimuLean.Resources;
using SimuLean.Storage;
using SimuLean.Transport;

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
        Run("Task steps run in order; states, utilisation, usage per requester", TestSteps);
        Run("Hold keeps the executor until Continue", TestHold);
        Run("Immediate stop pauses the task; AfterCurrent finishes it first", TestDowntime);
        Run("Availability: busy / stopped executors refuse tasks; BecameAvailable", TestAvailability);
        Run("Travel: instant when abstract, timed and located when spatial; load / unload", TestTravel);
        Run("Labels, durations read when the step starts, warm-up resets counters", TestLabelsAndWarmup);
        Run("Dispatcher: FIFO if available (requests in arrival order, longest idle resource)", TestDispatchFifo);
        Run("Dispatcher: request priority rule", TestDispatchPriority);
        Run("Dispatcher: skill query and resource order rule", TestDispatchSkillAndOrder);
        Run("Dispatcher: all-or-nothing for several resources, no head-of-line blocking", TestDispatchAllOrNothing);
        Run("Dispatcher: stopped resources wait; unused resources go back; zero-time tasks; cancel; stats", TestDispatchMisc);
        Run("Stations: shared team (ItemLeaves): operators' processing / held time match the stations", TestSharedTeamStats);
        Run("Stations: EndOfProcess releases at the end of the process; request priority from the item", TestStationRelease);
        Run("Stations: Combiner, PickingStation, AssemblyStation, Stacker with a team of one", TestAllStationsWithTeam);
        Run("Transport: an operator loads, carries and delivers; link statistics; states", TestTransportBasic);
        Run("Transport: room on the way is counted; a full destination keeps the operator HELD", TestTransportRoom);
        Run("Travel: network route and times (CurrentLeg), open-ended move ended by Arrive", TestNetworkTravel);
        Run("Dispatcher: nearest resource (distance to the request's location)", TestNearestResource);
        Run("Transport: vehicles travel empty to the origin's location, then to the destination's; unplaced elements fail", TestSpatialTransport);
        Run("Storage: retrievals and relocations moved by its transport team", TestStorageMoves);
        Run("Travel: distance matrix (symmetric, missing pair fails); a stop mid-way freezes and shifts the leg", TestMatrixAndStops);
        Run("Stations: a spatial operator walks to the station before the process starts", TestOperatorWalks);
        Run("Stations: walkers that arrive at once (zero-length walk) start the job once", TestZeroLengthWalkStartsOnce);
        Run("Stops: an AfterCurrent stop shows on a resource only when its current task ends (held in a process)", TestAfterCurrentStopWaitsForTask);
        Run("Transport: one request per item, also when the hand-over is deferred (zero load time)", TestOneTransportRequestPerItem);
        Run("Transport: a carrier does not take an item out of a stopped origin", TestCarrierWaitsForStoppedOrigin);
        Run("Transport: a queue, a length-limited lane and a delay as origins get a carrier per held item", TestQueueOriginUsesEveryCarrier);
        Run("Storage: a retrieved item the output refuses waits in the storage; carried out by a transport team", TestStorageOutboundWaits);
        Run("Storage: overlapping retrievals in transport mode, reserved destinations, a lane being emptied takes no put-away", TestStorageConcurrentRetrievals);
        Run("Axes: trapezoidal axis times, simultaneous vs sequential axes", TestAxisTravel);
        Run("AS/RS: a storage moved by a crane on axes", TestAsrs);
        Run("Stacker: cycles done by a gantry crane (pick → position)", TestGantryStacker);
        Run("Walk grid: A* around walls, no corner cutting, smoothing, unreachable, blocked locations", TestWalkGrid);
        Run("Grid travel: path length / speed, path points on the leg, cache", TestGridTravel);
        Run("Move log: loaded / empty, durations without stops; external (physics) moves ended by Arrive", TestMoveLogExternal);
        Run("Calibration: least-squares fit, pair statistics, calibrated travel (pair mean vs general model), config", TestCalibration);
        Run("Traffic: follow (wait for the track), head-on deadlock resolved, capacity 2, segment times on the leg", TestTraffic);
        Run("Traffic: a segment's time is its length on the route's track types", TestTrafficSegmentOnRouteTypes);
        Run("Work points: an operator's point stays taken while it works there for another job (HoldFor)", TestWorkPointKeptForNextJob);
        Run("Walking items: route and time on the network, room counted on the way, waiting at the door, same place at once", TestItemsWalk);
        Run("Probability routing on walking and carried items: weights over every destination, choices forgotten (review2 #1, #2)", TestProbabilityRoutingOnLinks);
        Run("A walk to the same place (handed over at once) respects a stopped destination, also waiting at the door (review2 #5)", TestWalkToStoppedSamePlace);
        Run("Walking items from the config: walkSpeed in m/s with the calendar's unit, a grid, statistics and labels", TestItemsWalkConfig);
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        Console.WriteLine("- " + name);
        try { test(); }
        catch (Exception ex) { failed++; Console.WriteLine("  EXCEPTION: " + ex); }
    }

    sealed class At : IEvent { readonly Action a; public At(Action a) { this.a = a; } public void Execute() => a(); }
    static void Schedule(SimClock c, double t, Action a) => c.ScheduleEvent(new At(a), t - c.GetSimulationTime());

    sealed class Station : Element
    {
        public Station(SimClock c, string n) : base(n, c) { }
        public override int GetQueueLength() => 0;
        public override int GetFreeCapacity() => 0;
        public override bool CheckAvailability(Item i) => false;
        public override bool Receive(Item i) => false;
        public override bool Unblock() => false;
    }

    static void TestSteps()
    {
        var clock = new SimClock(1);
        var op = new TaskExecutor("Op1", clock);
        var st = new Station(clock, "Lathe");
        op.Start();
        var log = new List<string>();
        var task = new TaskSequence(TaskType.Process, st)
            .Setup(1).Callback(() => log.Add("setup@" + clock.GetSimulationTime().ToString(CultureInfo.InvariantCulture)))
            .Process(2).Callback(() => log.Add("done@" + clock.GetSimulationTime().ToString(CultureInfo.InvariantCulture)))
            .Delay(0.5);
        bool completed = false;
        task.Completed += t => completed = true;
        op.Execute(task);
        Check(op.CurrentTask == task && !op.IsAvailable, "busy while running");
        clock.AdvanceClock(10);
        Check(string.Join(" ", log) == "setup@1 done@3", "steps in order: " + string.Join(" ", log));
        Check(completed && op.IsAvailable && op.TasksDone == 1, "completed and free again");
        Near(task.StartTime, 0, "start time"); Near(task.EndTime, 3.5, "end time");
        Near(op.GetTimeInState(ElementState.SETUP), 1, "setup time");
        Near(op.GetTimeInState(ElementState.PROCESSING), 2, "process time");
        Near(op.GetTimeInState(ResourceStates.WAITING), 0.5, "waiting time");
        Near(op.Utilization, 3.5 / 10, "utilisation = busy states / time");
        Near(op.UsageByRequester["Lathe"], 3.5, "usage per requester");
        Check(clock.Context.Elements.Contains(op), "a resource is a model element (reports, inspector)");
    }

    static void TestHold()
    {
        var clock = new SimClock(1);
        var op = new TaskExecutor("Op1", clock);
        op.Start();
        var task = new TaskSequence(TaskType.Process).Process(1).Hold().Callback(() => { });
        op.Execute(task);
        Schedule(clock, 3, () => Check(op.Continue(), "Continue releases the hold"));
        clock.AdvanceClock(2.5);
        Check(!op.IsAvailable && op.CurrentTask == task, "held after processing");
        clock.AdvanceClock(10);
        Check(op.IsAvailable, "free after Continue");
        Near(op.GetTimeInState(ResourceStates.HELD), 2.0, "time held (1 → 3)");
        var idle = new TaskExecutor("Op2", clock);
        idle.Start();
        Check(!idle.Continue(), "Continue without a held task is refused");
    }

    static void TestDowntime()
    {
        // Immediate: the remaining work waits for the end of the stop.
        var clock = new SimClock(1);
        var op = new TaskExecutor("Op1", clock);
        op.Start();
        var task = new TaskSequence(TaskType.Process).Process(4);
        op.Execute(task);
        StopToken stop = null;
        Schedule(clock, 1, () => stop = op.Stop(new StopRequest(ElementState.BREAKDOWN)));
        Schedule(clock, 3, () => op.Resume(stop));
        clock.AdvanceClock(20);
        Near(task.EndTime, 6, "4 of work + 2 of breakdown");
        Near(op.GetTimeInState(ElementState.BREAKDOWN), 2, "breakdown accounted");

        // AfterCurrent (a break): the task ends on time, then the resource is off until the end of the stop.
        clock = new SimClock(1);
        op = new TaskExecutor("Op1", clock);
        op.Start();
        task = new TaskSequence(TaskType.Process).Process(4);
        op.Execute(task);
        Schedule(clock, 1, () => stop = op.Stop(new StopRequest(ElementState.OFF_SHIFT, StopMode.AfterCurrent)));
        Schedule(clock, 2, () => Check(!op.IsAvailable, "not available while finishing before a break"));
        Schedule(clock, 6, () => op.Resume(stop));
        int becameAvailable = 0;
        op.BecameAvailable += _ => becameAvailable++;
        clock.AdvanceClock(20);
        Near(task.EndTime, 4, "the task finishes on time");
        Check(becameAvailable == 1 && op.IsAvailable, $"available once, after the break ({becameAvailable})");
        Near(op.GetTimeInState(ElementState.OFF_SHIFT), 2, "off shift from the end of the task to the resume");
    }

    static void TestAvailability()
    {
        var clock = new SimClock(1);
        var op = new TaskExecutor("Op1", clock);
        op.Start();
        op.Execute(new TaskSequence(TaskType.Other).Process(1));
        bool threw = false;
        try { op.Execute(new TaskSequence(TaskType.Other).Process(1)); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "a busy executor refuses a second task");
        clock.AdvanceClock(5);
        var stop = op.Stop(new StopRequest(ElementState.STOPPED));
        threw = false;
        try { op.Execute(new TaskSequence(TaskType.Other).Process(1)); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "a stopped executor refuses tasks");
        int n = 0;
        op.BecameAvailable += _ => n++;
        op.Resume(stop);
        Check(n == 1 && op.IsAvailable, "BecameAvailable when the stop ends");
        // A Completed handler may hand the next task over at once.
        var next = new TaskSequence(TaskType.Other).Process(1);
        var first = new TaskSequence(TaskType.Other).Process(1);
        first.Completed += _ => op.Execute(next);
        op.Execute(first);
        clock.AdvanceClock(20);
        Check(next.IsDone && op.TasksDone == 3, $"chained task ran ({op.TasksDone})");
    }

    static void TestTravel()
    {
        var clock = new SimClock(1);
        var abs = new TaskExecutor("Abstract", clock);
        abs.Start();
        var item = new Item(0);
        var log = new List<string>();
        var t1 = new TaskSequence(TaskType.Transport)
            .Travel("A").Load(item, () => 0, i => log.Add("loaded@" + clock.GetSimulationTime().ToString(CultureInfo.InvariantCulture)))
            .Travel("B").Unload(item, () => 0, i => log.Add("unloaded@" + clock.GetSimulationTime().ToString(CultureInfo.InvariantCulture)));
        abs.Execute(t1);
        Check(t1.IsDone && abs.Location == null, "abstract: travel, load, unload instantly; no location");
        Check(string.Join(" ", log) == "loaded@0 unloaded@0", string.Join(" ", log));

        var agv = new TaskExecutor("AGV", clock, ResourceMode.Spatial) { HomeNode = "Home", LoadTime = 0.5, UnloadTime = 0.5 };
        agv.TravelModel = new FunctionTravel((from, to) => 2);
        agv.Start();
        Check(agv.Location == "Home", "spatial starts at home");
        log.Clear();
        var t2 = new TaskSequence(TaskType.Transport)
            .Travel("A").Load(item, onLoaded: i => log.Add("loaded@" + clock.GetSimulationTime().ToString(CultureInfo.InvariantCulture)))
            .Travel("B").Unload(item, onUnloaded: i => log.Add("unloaded@" + clock.GetSimulationTime().ToString(CultureInfo.InvariantCulture)));
        agv.Execute(t2);
        Schedule(clock, 3, () => Check(agv.Carried.Contains(item), "carries the item while travelling loaded"));
        clock.AdvanceClock(20);
        Check(string.Join(" ", log) == "loaded@2.5 unloaded@5", string.Join(" ", log));
        Check(agv.Location == "B" && agv.Carried.Count == 0, "at B, empty");
        Near(agv.GetTimeInState(ResourceStates.TRAVELLING_EMPTY), 2, "travelling empty");
        Near(agv.GetTimeInState(ResourceStates.TRAVELLING_LOADED), 2, "travelling loaded");
        Near(agv.GetTimeInState(ResourceStates.LOADING) + agv.GetTimeInState(ResourceStates.UNLOADING), 1, "loading + unloading");

        var lost = new TaskExecutor("NoModel", clock, ResourceMode.Spatial) { HomeNode = "Home" };
        lost.Start();
        bool threw = false;
        try { lost.Execute(new TaskSequence(TaskType.Transport).Travel("X")); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "a spatial executor without a travel model fails loudly");
    }

    // ── dispatcher ───────────────────────────────────────────────────────

    static (SimClock clock, ResourceDispatcher d, TaskExecutor[] ops) Team(params string[] names)
    {
        var clock = new SimClock(1);
        var d = new ResourceDispatcher("Team", clock);
        var ops = names.Select(n => new TaskExecutor(n, clock)).ToArray();
        foreach (var o in ops) { o.Start(); d.Add(o); }
        d.Start();
        return (clock, d, ops);
    }

    // A request whose resources each process for `time`; logs "<station>:<resource>@t".
    static ResourceRequest Work(Element station, double time, List<string> log, params ResourceRequirement[] reqs)
        => new ResourceRequest(station, TaskType.Process, (r, got) =>
        {
            foreach (var ex in got)
            {
                log.Add($"{station.GetName()}:{ex.GetName()}@{ex.GetSimClock().GetSimulationTime().ToString(CultureInfo.InvariantCulture)}");
                ex.Execute(new TaskSequence(TaskType.Process, station).Process(time));
            }
        }, reqs);

    static void TestDispatchFifo()
    {
        var (clock, d, ops) = Team("Op1", "Op2");
        var log = new List<string>();
        foreach (var s in new[] { "S1", "S2", "S3", "S4" }) d.Request(Work(new Station(clock, s), 2, log));
        clock.AdvanceClock(20);
        // Op2 freed after Op1 at t=2 (same time, Op1's event first): S3 → Op1, S4 → Op2.
        Check(string.Join(" ", log) == "S1:Op1@0 S2:Op2@0 S3:Op1@2 S4:Op2@2", string.Join(" ", log));
        Check(d.Served == 4 && d.Requests.Count == 0 && d.Resources.Count == 2, "all served, both free again");
        Near(d.Requests.WaitTime.Average, 1.0, "requests waited 0, 0, 2, 2 for resources");
    }

    static void TestDispatchPriority()
    {
        var (clock, d, ops) = Team("Op1");
        d.RequestOrder = ListQuery.All.OrderBy("priority", descending: true);
        var log = new List<string>();
        d.Request(Work(new Station(clock, "First"), 1, log));                 // takes Op1 until t=1
        var prios = new[] { ("A", 1.0), ("B", 5.0), ("C", 3.0) };
        foreach (var (n, p) in prios) { var r = Work(new Station(clock, n), 1, log); r.Priority = p; d.Request(r); }
        clock.AdvanceClock(20);
        Check(string.Join(" ", log) == "First:Op1@0 B:Op1@1 C:Op1@2 A:Op1@3", string.Join(" ", log));
    }

    static void TestDispatchSkillAndOrder()
    {
        var (clock, d, ops) = Team("Welder", "Painter", "Painter2");
        ops[0].SetLabelValue("skill", "welding");
        ops[1].SetLabelValue("skill", "painting");
        ops[2].SetLabelValue("skill", "painting");
        var log = new List<string>();
        var painting = new ResourceRequirement(1, ListQuery.All.Where("skill", ListOp.Equal, "painting"), "painting");
        d.Request(Work(new Station(clock, "Paint1"), 4, log, painting));           // Painter (first free painter)
        clock.AdvanceClock(5);
        // Least used painter first: Painter worked 4, Painter2 0.
        d.ResourceOrder = ListQuery.All.OrderBy("utilization");
        d.Request(Work(new Station(clock, "Paint2"), 1, log, painting));
        clock.AdvanceClock(20);
        Check(string.Join(" ", log) == "Paint1:Painter@0 Paint2:Painter2@5", string.Join(" ", log));
        Check(d.Requests.Ordered().Count == 0 && ops[0].TasksDone == 0, "the welder was never taken");
        // Request fields for rules.
        var r = Work(new Station(clock, "X"), 1, log, painting);
        r.Priority = 7;
        Check(r.GetListNumber("priority") == 7 && r.GetListText("station") == "X" && r.GetListText("skill") == "painting"
              && r.GetListText("taskType") == "Process" && r.GetListNumber("count") == 1, "request fields");
        Check(ops[1].GetListText("skill") == "painting" && ops[1].GetListNumber("tasksDone") == 1 && ops[1].GetListText("name") == "Painter", "resource fields");
    }

    static void TestDispatchAllOrNothing()
    {
        var (clock, d, ops) = Team("Op1", "Op2");
        var log = new List<string>();
        d.Request(Work(new Station(clock, "Busy"), 3, log));                       // Op1 until t=3
        d.Request(Work(new Station(clock, "Two"), 1, log, new ResourceRequirement(2)));   // needs both: waits
        Schedule(clock, 1, () => d.Request(Work(new Station(clock, "One"), 1, log)));     // served by Op2 at once
        clock.AdvanceClock(20);
        // Op2 free since t=2, Op1 since t=3: longest idle first.
        Check(string.Join(" ", log) == "Busy:Op1@0 One:Op2@1 Two:Op2@3 Two:Op1@3", string.Join(" ", log));
        Check(ops.All(o => o.IsAvailable), "nothing held partially");
    }

    static void TestDispatchMisc()
    {
        // Stopped resource: the request waits for the end of the stop.
        var (clock, d, ops) = Team("Op1");
        var stop = ops[0].Stop(new StopRequest(ElementState.OFF_SHIFT));
        var log = new List<string>();
        d.Request(Work(new Station(clock, "S"), 1, log));
        Schedule(clock, 2, () => ops[0].Resume(stop));
        clock.AdvanceClock(10);
        Check(string.Join(" ", log) == "S:Op1@2", "served when the stop ended: " + string.Join(" ", log));

        // A requester that gives its resource no task: the resource goes back to the list.
        (clock, d, ops) = Team("Op1");
        log.Clear();
        d.Request(new ResourceRequest(new Station(clock, "Lazy"), TaskType.Other, (r, got) => log.Add("lazy")));
        d.Request(Work(new Station(clock, "Next"), 1, log));
        clock.AdvanceClock(10);
        Check(string.Join(" ", log) == "lazy Next:Op1@0", "unused resource reused: " + string.Join(" ", log));

        // Zero-time tasks chain without recursion; cancel withdraws a pending request.
        (clock, d, ops) = Team("Op1");
        log.Clear();
        for (int i = 0; i < 500; i++) d.Request(Work(new Station(clock, "Z" + i), 0, log));
        Check(log.Count == 500 && d.Served == 500, $"500 instantaneous tasks ({log.Count})");
        d.Request(Work(new Station(clock, "Long"), 5, log));
        var cancelled = Work(new Station(clock, "Cancelled"), 1, log);
        d.Request(cancelled);
        Check(d.Cancel(cancelled) && !d.Cancel(cancelled) && cancelled.State == RequestState.Cancelled, "cancel once");
        clock.AdvanceClock(10);
        Check(!log.Any(l => l.StartsWith("Cancelled")), "a cancelled request is never served");
    }

    // ── stations ─────────────────────────────────────────────────────────

    // Runs a headless model (its resource teams built by the factory); returns every element's flow counts and
    // time per state, and the built members.
    static Dictionary<string, double> RunModel(SimuLean.Serialization.SimulationConfig cfg, out List<TaskExecutor> ops)
    {
        var clock = new SimClock(cfg.Seed);
        var factory = new SimuLean.Headless.HeadlessModelFactory(clock);
        var el = factory.BuildModel(cfg);
        factory.StartModel();
        clock.AdvanceClock(cfg.MaxSimulationTime);
        ops = factory.Executors.ToList();
        var r = new Dictionary<string, double>();
        foreach (var kv in el)
        {
            r[kv.Key + ".in"] = kv.Value.GetStatsInputCount();
            r[kv.Key + ".out"] = kv.Value.GetStatsOutputCount();
            foreach (var s in kv.Value.GetStateSnapshot()) r[kv.Key + "." + s.Key] = s.Value;
        }
        return r;
    }

    // resource_shared_team reproduces the former operator pool exactly (its baseline values were kept through
    // the migration); here: the operators' own statistics, which the pool never had.
    static void TestSharedTeamStats()
    {
        var r = RunModel(SimuLean.Testing.ReferenceModels.ResourceSharedTeam(), out var ops);
        Check(ops.Count == 2, "team of two built by the factory");
        double busy = ops.Sum(o => o.GetTimeInState(ElementState.PROCESSING));
        double held = ops.Sum(o => o.GetTimeInState(ResourceStates.HELD));
        double stationProc = new[] { "a", "b", "c" }.Sum(id => r.TryGetValue(id + ".PROCESSING", out var v) ? v : 0);
        Near(busy, stationProc, "operators' processing time = the stations' processing time", 1e-6);
        Check(held > 0 && ops.All(o => o.TasksDone > 0), $"operators held while stations were blocked ({held:F1})");
        // Usage counts finished tasks; the ones still running at the end are in the busy / held time only.
        double usage = ops.Sum(o => o.UsageByRequester.Values.Sum());
        Check(usage <= busy + held + 1e-6 && usage > busy + held - 10, $"usage per station ≈ processing + held ({usage:F1} vs {busy + held:F1})");
    }

    // Each other station kind with a team of one (ItemLeaves): the operator works and the station waits for it.
    static void TestAllStationsWithTeam()
    {
        var cases = new (string name, Func<SimuLean.Serialization.SimulationConfig> model, string id)[]
        {
            ("combiner", SimuLean.Testing.ReferenceModels.CombinerModel, "comb"),
            ("picking", SimuLean.Testing.ReferenceModels.PickingModel, "pick"),
            ("assembly", SimuLean.Testing.ReferenceModels.AssemblyModel, "asy"),
            ("stacker", SimuLean.Testing.ReferenceModels.StackerModel, "st"),
        };
        foreach (var (name, model, id) in cases)
        {
            var cfg = model();
            var team = new SimuLean.Serialization.ResourceTeamConfig { Id = "ops", Name = "Ops" };
            team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "Op" });
            cfg.ResourceTeams.Add(team);
            var e = cfg.Elements.Find(x => x.Id == id);
            e.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessTeam] = "ops";
            e.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessRelease] = "ItemLeaves";
            var r = RunModel(cfg, out var ops);
            Check(ops.Count == 1 && ops[0].TasksDone > 0, $"{name}: the operator worked ({ops[0].TasksDone} tasks)");
            Check(r.ContainsKey(id + ".WAITING_FOR_RESOURCE"), $"{name}: the station did wait for its operator");
            Check(r[id + ".out"] > 0, $"{name}: items flow ({r[id + ".out"]})");
        }

        // A release that is not EndOfProcess / ItemLeaves fails the build (it was ignored: EndOfProcess)
        var bad = cases[0].model();
        var crew = new SimuLean.Serialization.ResourceTeamConfig { Id = "ops", Name = "Ops" };
        crew.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "Op" });
        bad.ResourceTeams.Add(crew);
        var be = bad.Elements.Find(x => x.Id == cases[0].id);
        be.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessTeam] = "ops";
        be.Parameters[SimuLean.Serialization.ResourceUseKeys.ProcessRelease] = "ItemLeave";
        bool failed = false;
        try { new SimuLean.Headless.HeadlessModelFactory(new SimClock(1), enableLogging: false).BuildModel(bad); }
        catch (FormatException ex) { failed = ex.Message.Contains("ItemLeave"); }
        catch (Exception ex) { failed = ex.InnerException is FormatException fe && fe.Message.Contains("ItemLeave"); }
        Check(failed, "an unknown process release fails the build, naming it");
    }

    // ── transport ────────────────────────────────────────────────────────

    static void TestTransportBasic()
    {
        var clock = new SimClock(1);
        var d = new ResourceDispatcher("Team", clock);
        var op = new TaskExecutor("Op", clock);
        d.Add(op);
        var m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock);
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
        var link = ResourceTransportLink.Create(m, new[] { sink }, new StationResources(d));
        link.TravelTime = (item, dest) => 2;
        link.LoadTime = item => 0.5;              // the unload time comes from the resource
        op.UnloadTime = 0.5;
        foreach (var e in new Element[] { m, sink, op }) e.Start();
        d.Start();
        m.Receive(new Item(0));
        Schedule(clock, 1.2, () => Check(m.GetQueueLength() == 1 && link.InTransit == 1, "processed item waits in M while the operator loads"));
        Schedule(clock, 2, () => Check(m.GetStatsOutputCount() == 1 && sink.GetStatsInputCount() == 0, "left M at the end of the load (1.5), not yet delivered"));
        clock.AdvanceClock(10);
        Check(sink.GetStatsInputCount() == 1 && link.InTransit == 0, "delivered");
        Near(op.GetTimeInState(ResourceStates.LOADING), 0.5, "loading");
        Near(op.GetTimeInState(ResourceStates.TRAVELLING_LOADED), 2, "travelling loaded");
        Near(op.GetTimeInState(ResourceStates.UNLOADING), 0.5, "unloading");
        Near(m.GetTimeInState(ElementState.BLOCKED), 0.5, "M blocked while waiting for the load (1 → 1.5)");
        Near(op.UsageByRequester["M"], 3, "the transport counts for the origin station");
        Check(op.IsAvailable && op.TasksDone == 1, "released at the delivery");
    }

    static void TestTransportRoom()
    {
        // Three finished items, a destination queue of 1 in front of a slow server: the room promised to an item
        // on its way is not offered twice, and later items wait in M (blocked) — no operator parks a second item.
        var clock = new SimClock(1);
        var d = new ResourceDispatcher("Team", clock);
        var ops = new[] { new TaskExecutor("Op1", clock), new TaskExecutor("Op2", clock), new TaskExecutor("Op3", clock) };
        foreach (var o in ops) d.Add(o);
        var m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1), new ConstantRandomProcess(1), new ConstantRandomProcess(1) }, "M", clock);
        var q = new ItemsQueue(1, "Q", clock);
        var s = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(10) }, "S", clock);
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
        var link = ResourceTransportLink.Create(m, new[] { q }, new StationResources(d));
        link.TravelTime = (item, dest) => 1;
        GeneralLink.CreateLink(q, new List<Element> { s });
        GeneralLink.CreateLink(s, new List<Element> { sink });
        foreach (var e in new Element[] { m, q, s, sink }.Concat(ops)) e.Start();
        d.Start();
        for (int i = 0; i < 3; i++) m.Receive(new Item(0));
        Schedule(clock, 1.5, () => Check(link.InTransit == 1 && ops.Count(o => !o.IsAvailable) == 1, "one move for one free place"));
        clock.AdvanceClock(40);
        Check(sink.GetStatsInputCount() == 3, $"all three through ({sink.GetStatsInputCount()})");
        Check(ops.All(o => o.GetTimeInState(ResourceStates.HELD) == 0), "nobody waited at a full destination (room was checked first)");

        // Another upstream fills the destination while the operator travels: it waits HELD until there is room.
        clock = new SimClock(1);
        d = new ResourceDispatcher("Team", clock);
        var op = new TaskExecutor("Op", clock);
        d.Add(op);
        m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock);
        q = new ItemsQueue(1, "Q", clock);
        s = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(10) }, "S", clock);
        sink = new Sink("Sink", clock);
        var other = new ReleaseSource("Other", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
        link = ResourceTransportLink.Create(m, new[] { q }, new StationResources(d));
        link.TravelTime = (item, dest) => 2;
        GeneralLink.CreateLink(other, new List<Element> { q });
        GeneralLink.CreateLink(q, new List<Element> { s });
        GeneralLink.CreateLink(s, new List<Element> { sink });
        foreach (var e in new Element[] { m, q, s, sink, other, op }) e.Start();
        d.Start();
        other.Enqueue(new Item(0));                                // S busy 0 → 10, Q empty
        m.Receive(new Item(0));                                    // done at 1, carried 1 → 3
        Schedule(clock, 2, () => other.Enqueue(new Item(0)));      // fills Q at 2
        clock.AdvanceClock(40);
        Near(op.GetTimeInState(ResourceStates.HELD), 7, "held at the full destination from 3 until S takes from Q at 10");
        Check(sink.GetStatsInputCount() == 3 && op.IsAvailable, "everything delivered, operator free");
    }

    // A — B — C on a line, 10 m per track, two-way.
    static TransportNetwork Line(params string[] ids)
    {
        var net = new TransportNetwork();
        for (int i = 0; i < ids.Length; i++)
        {
            net.AddNode(ids[i], 10 * i, 0, 0);
            if (i > 0) net.AddTrack(ids[i - 1], ids[i], 10, true, TrackType.TravelPath);
        }
        return net;
    }

    sealed class OpenEnded : ITravelModel
    {
        public double Distance(string from, string to) => 1;
        public TravelPlan Plan(TaskExecutor e, string from, string to) => new TravelPlan { Duration = double.NaN };
    }

    static void TestNetworkTravel()
    {
        var clock = new SimClock(1);
        var agv = new TaskExecutor("AGV", clock, ResourceMode.Spatial) { HomeNode = "A", TravelModel = new NetworkTravel(Line("A", "B", "C"), 2) };
        agv.Start();
        TravelLeg seen = null;
        agv.TravelStarted += (e, leg) => seen = leg;
        agv.Execute(new TaskSequence(TaskType.Transport).Travel("C"));
        Check(seen != null && string.Join(",", seen.Route) == "A,B,C", "route A,B,C");
        Near(seen.EndTime - seen.StartTime, 10, "20 m at 2 m/s");
        Check(agv.CurrentLeg == seen && agv.CurrentState == ResourceStates.TRAVELLING_EMPTY, "travelling empty, leg exposed");
        clock.AdvanceClock(20);
        Check(agv.Location == "C" && agv.CurrentLeg == null && agv.IsAvailable, "at C, no leg");
        Near(agv.DistanceTo("A"), 20, "distance back to A");

        var walker = new TaskExecutor("W", clock, ResourceMode.Spatial) { HomeNode = "A", TravelModel = new OpenEnded() };
        walker.Start();
        bool done = false;
        var t = new TaskSequence(TaskType.Transport).Travel("B").Callback(() => done = true);
        walker.Execute(t);
        clock.AdvanceClock(30);
        Check(!done && walker.CurrentLeg != null && walker.CurrentLeg.IsOpenEnded, "open-ended: waits for the arrival");
        Schedule(clock, 37, () => Check(walker.Arrive(), "Arrive ends the move"));
        clock.AdvanceClock(40);
        Check(done && walker.Location == "B" && !walker.Arrive(), "arrived at B at 37, next step ran; nothing more to end");
        Near(walker.GetTimeInState(ResourceStates.TRAVELLING_EMPTY), 17, "travelled 20 → 37");
    }

    static void TestNearestResource()
    {
        var clock = new SimClock(1);
        var net = Line("A", "B", "C", "D");
        var d = new ResourceDispatcher("Fleet", clock) { ResourceOrder = ListQuery.All.OrderBy("distance") };
        var far = new TaskExecutor("Far", clock, ResourceMode.Spatial) { HomeNode = "A", TravelModel = new NetworkTravel(net, 1) };
        var near = new TaskExecutor("Near", clock, ResourceMode.Spatial) { HomeNode = "C", TravelModel = new NetworkTravel(net, 1) };
        foreach (var e in new[] { far, near }) { e.Start(); d.Add(e); }
        d.Start();
        TaskExecutor got = null;
        var r = new ResourceRequest(null, TaskType.Transport, (q, g) => got = g[0]) { Location = "D" };
        d.Request(r);
        Check(got == near, $"the nearest one to D ({got?.GetName()})");
        Near(near.GetListNumber("distance"), 10, "distance field measured to the request");

        // Without a rule: FIFO (longest idle), whatever the distance.
        var d2 = new ResourceDispatcher("Fleet2", clock);
        var a = new TaskExecutor("First", clock, ResourceMode.Spatial) { HomeNode = "A", TravelModel = new NetworkTravel(net, 1) };
        var b = new TaskExecutor("Second", clock, ResourceMode.Spatial) { HomeNode = "D", TravelModel = new NetworkTravel(net, 1) };
        foreach (var e in new[] { a, b }) { e.Start(); d2.Add(e); }
        d2.Start();
        got = null;
        d2.Request(new ResourceRequest(null, TaskType.Transport, (q, g) => got = g[0]) { Location = "D" });
        Check(got == a, "no rule: longest idle first");
    }

    static void TestSpatialTransport()
    {
        var clock = new SimClock(1);
        var net = Line("A", "B", "C");
        var d = new ResourceDispatcher("Fleet", clock);
        var agv = new TaskExecutor("AGV", clock, ResourceMode.Spatial) { HomeNode = "C", TravelModel = new NetworkTravel(net, 1), LoadTime = 1, UnloadTime = 1 };
        d.Add(agv);
        var m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock) { Location = "A" };
        var q = new ItemsQueue(10, "Q", clock) { Location = "B" };
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
        var link = ResourceTransportLink.Create(m, new[] { q }, new StationResources(d));
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(q, new List<Element> { sink });
        foreach (var e in new Element[] { m, q, sink, agv }) e.Start();
        d.Start();
        m.Receive(new Item(0));
        // done at 1; AGV C → A (20) until 21, load until 22, A → B (10) until 32, unload until 33.
        Schedule(clock, 32.5, () => Check(sink.GetStatsInputCount() == 0 && agv.CurrentState == ResourceStates.UNLOADING, "unloading at B"));
        clock.AdvanceClock(40);
        Check(sink.GetStatsInputCount() == 1 && agv.Location == "B", "delivered at 33, AGV stays at B");
        Near(agv.GetTimeInState(ResourceStates.TRAVELLING_EMPTY), 20, "empty C → A");
        Near(agv.GetTimeInState(ResourceStates.TRAVELLING_LOADED), 10, "loaded A → B");

        // A spatial carrier between unplaced elements fails loudly.
        clock = new SimClock(1);
        d = new ResourceDispatcher("Fleet", clock);
        agv = new TaskExecutor("AGV", clock, ResourceMode.Spatial) { HomeNode = "A", TravelModel = new NetworkTravel(net, 1) };
        d.Add(agv);
        m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock);
        q = new ItemsQueue(10, "Q", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
        ResourceTransportLink.Create(m, new[] { q }, new StationResources(d));
        GeneralLink.CreateLink(q, new List<Element> { new Sink("Sink", clock) });
        foreach (var e in new Element[] { m, q, agv }) e.Start();
        d.Start();
        m.Receive(new Item(0));
        bool threw = false;
        try { clock.AdvanceClock(5); } catch (InvalidOperationException ex) { threw = ex.Message.Contains("location"); }
        Check(threw, "no location → clear error");
    }

    static void TestStorageMoves()
    {
        var clock = new SimClock(1);
        var net = Line("D", "L1", "L2");
        var d = new ResourceDispatcher("Cranes", clock);
        var crane = new TaskExecutor("Crane", clock, ResourceMode.Spatial) { HomeNode = "D", TravelModel = new NetworkTravel(net, 1), LoadTime = 1, UnloadTime = 1 };
        d.Add(crane);
        var storage = new StorageSystem("Store", clock) { Location = "D", Transport = new StationResources(d) };
        storage.AddLane(new StorageLane("lane1", new Coordinate(10, 0, 0), 2, AccessDiscipline.Lifo, SkuConstraint.Mixed, "L1"));
        storage.AddLane(new StorageLane("lane2", new Coordinate(20, 0, 0), 2, AccessDiscipline.Lifo, SkuConstraint.Mixed, "L2"));
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(storage, new List<Element> { sink });
        foreach (var e in new Element[] { storage, sink, crane }) e.Start();
        d.Start();
        var first = new Item(0); var second = new Item(0);
        storage.Receive(first); storage.Receive(second);           // both in lane1 (first at the back)
        double at = double.NaN;
        Check(storage.RequestRetrieval(ListQuery.All.Where(e => e.Item == first), i => at = clock.GetSimulationTime()), "retrieval accepted");
        clock.AdvanceClock(200);
        // blocker: D → L1 (10) 10, load 11, L1 → L2 (10) 21, unload 22; target: L2 → L1 32, load 33, L1 → D 43, unload 44.
        Near(at, 44, "relocation then retrieval, carried by the crane");
        Check(storage.TotalRelocations == 1 && sink.GetStatsInputCount() == 1 && crane.Location == "D", "one relocation, item out, crane at D");
    }

    static void TestMatrixAndStops()
    {
        var clock = new SimClock(1);
        var m = new DistanceMatrix().Set("H", "S1", 5).Set("S1", "S2", 6);
        var walker = new TaskExecutor("W", clock, ResourceMode.Spatial) { HomeNode = "H", TravelModel = new MatrixTravel(m, 0.5) };
        walker.Start();
        Near(walker.DistanceTo("S1"), 5, "H → S1");
        Near(m.Distance("S2", "S1"), 6, "symmetric: reverse pair");
        Check(double.IsPositiveInfinity(m.Distance("H", "S2")), "missing pair: unreachable");
        walker.Execute(new TaskSequence(TaskType.Transport).Travel("S1").Travel("S2"));
        var leg = walker.CurrentLeg;
        Check(leg != null && string.Join(",", leg.Route) == "H,S1", "straight route H → S1");
        Near(leg.EndTime, 10, "5 at 0.5 per time unit");
        StopToken stop = null;
        Schedule(clock, 4, () => stop = walker.Stop(new StopRequest(ElementState.STOPPED, StopMode.Immediate)));
        Schedule(clock, 7, () => { Near(leg.Fraction(clock.GetSimulationTime()), 0.4, "frozen at 40 % while stopped"); walker.Resume(stop); });
        clock.AdvanceClock(8);
        Near(leg.StartTime, 3, "start shifted by the 3-unit stop");
        Near(leg.EndTime, 13, "end shifted by the stop");
        Near(leg.Fraction(8), 0.5, "continues from where it stopped");
        clock.AdvanceClock(40);
        Check(walker.Location == "S2" && walker.IsAvailable, "S1 at 13, then S2 at 25");
        var lost = new TaskExecutor("L", clock, ResourceMode.Spatial) { HomeNode = "H", TravelModel = new MatrixTravel(m, 1) };
        lost.Start();
        bool threw = false;
        try { lost.Execute(new TaskSequence(TaskType.Transport).Travel("S2")); } catch (InvalidOperationException ex) { threw = ex.Message.Contains("matrix"); }
        Check(threw, "H → S2 is not in the matrix: fails loudly");
    }

    static void TestOperatorWalks()
    {
        var clock = new SimClock(1);
        var m = new DistanceMatrix().Set("H", "S", 4);
        var d = new ResourceDispatcher("Ops", clock);
        var op = new TaskExecutor("Op", clock, ResourceMode.Spatial) { HomeNode = "H", TravelModel = new MatrixTravel(m, 1) };
        d.Add(op);
        var s = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(2) }, "S", clock) { Location = "S" };
        s.Resources = new StationResources(d);
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { s });
        GeneralLink.CreateLink(s, new List<Element> { sink });
        foreach (var e in new Element[] { s, sink, op }) e.Start();
        d.Start();
        s.Receive(new Item(0));
        Schedule(clock, 3, () => Check(op.CurrentState == ResourceStates.TRAVELLING_EMPTY && s.CurrentState == ElementState.WAITING_FOR_RESOURCE, "walking; the station waits"));
        clock.AdvanceClock(20);
        Check(sink.GetStatsInputCount() == 1 && op.Location == "S", "processed after the walk, operator stays at S");
        Near(s.GetTimeInState(ElementState.WAITING_FOR_RESOURCE), 4, "waited for the 4-unit walk");
        Near(op.GetTimeInState(ElementState.PROCESSING), 2, "then processed 2");
        s.Receive(new Item(0));
        clock.AdvanceClock(30);
        Near(s.GetTimeInState(ElementState.WAITING_FOR_RESOURCE), 4, "already there: no walk the second time");
    }

    static void TestZeroLengthWalkStartsOnce()
    {
        // two operators whose walk to the station takes no time: the travel ends inside its own start, and the
        // process must still start once, after both have their task
        var clock = new SimClock(1);
        var m = new DistanceMatrix().Set("H", "S", 0);
        var d = new ResourceDispatcher("Ops", clock);
        var ops = new[]
        {
            new TaskExecutor("Op1", clock, ResourceMode.Spatial) { HomeNode = "H", TravelModel = new MatrixTravel(m, 1) },
            new TaskExecutor("Op2", clock, ResourceMode.Spatial) { HomeNode = "H", TravelModel = new MatrixTravel(m, 1) },
        };
        foreach (var op in ops) d.Add(op);
        var s = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(2) }, "S", clock) { Location = "S" };
        s.Resources = new StationResources(d, new ResourceRequirement(2));
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { s });
        GeneralLink.CreateLink(s, new List<Element> { sink });
        foreach (var e in new Element[] { s, sink, ops[0], ops[1] }) e.Start();
        d.Start();
        s.Receive(new Item(0));
        clock.AdvanceClock(20);
        Check(sink.GetStatsInputCount() == 1, $"one item out ({sink.GetStatsInputCount()})");
        Near(s.GetTimeInState(ElementState.PROCESSING), 2, "processed once, for 2");
        Check(ops.All(o => o.TasksDone == 1 && o.IsAvailable), $"each operator did one task and is free ({ops[0].TasksDone}, {ops[1].TasksDone})");
    }

    static void TestAfterCurrentStopWaitsForTask()
    {
        // the operator is held by a 10-unit process from t=0; a shift end (AfterCurrent) at t=2 must show at t=10,
        // when the process (its task) ends, not at once
        var clock = new SimClock(1);
        var d = new ResourceDispatcher("Ops", clock);
        var op = new TaskExecutor("Op", clock);
        d.Add(op);
        var s = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(10) }, "S", clock);
        s.Resources = new StationResources(d);
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { s });
        GeneralLink.CreateLink(s, new List<Element> { sink });
        foreach (var e in new Element[] { s, sink, op }) e.Start();
        d.Start();
        s.Receive(new Item(0));
        StopToken stop = null;
        Schedule(clock, 2, () => stop = op.Stop(new StopRequest(ElementState.STOPPED, StopMode.AfterCurrent)));
        Schedule(clock, 5, () => Check(op.CurrentState == ElementState.PROCESSING, $"still processing at t=5 ({op.CurrentState})"));
        Schedule(clock, 20, () => op.Resume(stop));
        clock.AdvanceClock(25);
        Near(op.GetTimeInState(ElementState.PROCESSING), 10, "processing for the whole 10-unit process");
        Near(op.GetTimeInState(ElementState.STOPPED), 10, "stopped from the end of the task (10) to the resume (20)");
        Check(sink.GetStatsInputCount() == 1, "the job finished");
    }

    static void TestOneTransportRequestPerItem()
    {
        // free abstract carriers, no load time: the item is loaded inside the server's own send and handed over just
        // after; meanwhile the server offers it again - it must not get a second carrier
        var clock = new SimClock(1);
        var d = new ResourceDispatcher("Movers", clock);
        var movers = new[] { new TaskExecutor("Mv1", clock), new TaskExecutor("Mv2", clock) };
        foreach (var mv in movers) d.Add(mv);
        var m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1), new ConstantRandomProcess(1) }, "M", clock);
        var q = new ItemsQueue(10, "Q", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
        var link = ResourceTransportLink.Create(m, new List<Element> { q }, new StationResources(d));
        link.TravelTime = (it, dest) => 2;
        foreach (var e in new Element[] { m, q, movers[0], movers[1] }) e.Start();
        d.Start();
        link.Reset();
        m.Receive(new Item(0)); m.Receive(new Item(0));
        clock.AdvanceClock(10);
        Check(q.GetStatsInputCount() == 2, $"delivered once each ({q.GetStatsInputCount()})");
        Check(d.Served == 2, $"one transport request per item ({d.Served})");
        Check(movers.Sum(x => x.TasksDone) == 2, $"one carrier worked ({movers[0].TasksDone} + {movers[1].TasksDone})");
        Check(link.InTransit == 0, $"nothing left on its way ({link.InTransit})");
    }

    static void TestQueueOriginUsesEveryCarrier()
    {
        // Five items wait in the origin, three free carriers, 10 per move: three travel at once (the origin offered only
        // its head, and the next only after a delivery: one carrier at a time). Order kept; one request per item.
        foreach (string kind in new[] { "queue", "lane", "delay" })
        {
            var clock = new SimClock(1);
            var d = new ResourceDispatcher("Trucks", clock);
            var trucks = new[] { new TaskExecutor("T1", clock), new TaskExecutor("T2", clock), new TaskExecutor("T3", clock) };
            foreach (var t in trucks) d.Add(t);
            Element origin = kind == "queue" ? new ItemsQueue(20, "Q", clock)
                           : kind == "lane" ? new LengthLimitedQueue("L", clock, 50)
                           : (Element)new Delay(new ConstantRandomProcess(0), "D", clock);
            var sink = new Sink("Sink", clock);
            var link = ResourceTransportLink.Create(origin, new[] { sink }, new StationResources(d));
            link.TravelTime = (it, dest) => 10;
            foreach (var e in new Element[] { origin, sink }.Concat(trucks)) e.Start();
            d.Start();
            link.Reset();
            var items = Enumerable.Range(0, 5).Select(i => new Item(0)).ToList();
            foreach (var it in items) origin.Receive(it);
            Schedule(clock, 11, () => Check(sink.GetStatsInputCount() == 3, $"{kind}: three carriers travelled at once ({sink.GetStatsInputCount()} delivered at t=11)"));
            clock.AdvanceClock(100);
            Check(sink.GetStatsInputCount() == 5 && origin.GetQueueLength() == 0, $"{kind}: all delivered ({sink.GetStatsInputCount()})");
            Check(d.Served == 5, $"{kind}: one transport request per item ({d.Served})");
            Check(trucks.All(t => t.TasksDone >= 1), $"{kind}: every truck worked ({string.Join(", ", trucks.Select(t => t.TasksDone))})");
            Near(sink.StatsCollector.LastEntryTime, 20, $"{kind}: the last two delivered by the second round");
        }

        // A destination with room for two: only two carriers are requested, the rest wait in the origin (FIFO).
        {
            var clock = new SimClock(1);
            var d = new ResourceDispatcher("Trucks", clock);
            var trucks = new[] { new TaskExecutor("T1", clock), new TaskExecutor("T2", clock), new TaskExecutor("T3", clock) };
            foreach (var t in trucks) d.Add(t);
            var q = new ItemsQueue(20, "Q", clock);
            var dest = new ItemsQueue(2, "Dest", clock);
            var link = ResourceTransportLink.Create(q, new[] { dest }, new StationResources(d));
            link.TravelTime = (it, to) => 10;
            foreach (var e in new Element[] { q, dest }.Concat(trucks)) e.Start();
            d.Start();
            link.Reset();
            var items = Enumerable.Range(0, 5).Select(i => new Item(0)).ToList();
            foreach (var it in items) q.Receive(it);
            clock.AdvanceClock(50);
            Check(dest.GetQueueLength() == 2 && q.GetQueueLength() == 3 && link.InTransit == 0, $"room for two: two carried, three wait (dest {dest.GetQueueLength()}, origin {q.GetQueueLength()})");
            Check(dest.GetItems().SequenceEqual(items.Take(2)), "the first two, in order");
            Check(d.Served == 2, $"two requests ({d.Served})");
        }
    }

    static void TestCarrierWaitsForStoppedOrigin()
    {
        // the carrier starts loading at t=1 (load 5); the server is stopped (output blocked) from 3 to 10: the item
        // leaves the server when the stop ends, not at 6
        var clock = new SimClock(1);
        var d = new ResourceDispatcher("Movers", clock);
        var mv = new TaskExecutor("Mv", clock) { LoadTime = 5 };
        d.Add(mv);
        var m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock);
        var q = new ItemsQueue(10, "Q", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
        var link = ResourceTransportLink.Create(m, new List<Element> { q }, new StationResources(d));
        foreach (var e in new Element[] { m, q, mv }) e.Start();
        d.Start();
        link.Reset();
        m.Receive(new Item(0));
        StopToken stop = null;
        Schedule(clock, 3, () => stop = m.Stop(new StopRequest(ElementState.STOPPED, StopMode.Immediate)));
        Schedule(clock, 8, () => Check(m.GetQueueLength() == 1 && q.GetStatsInputCount() == 0, $"still in the stopped server at t=8 (server {m.GetQueueLength()}, queue {q.GetStatsInputCount()})"));
        Schedule(clock, 10, () => m.Resume(stop));
        clock.AdvanceClock(20);
        Check(q.GetStatsInputCount() == 1 && m.GetQueueLength() == 0, $"delivered after the stop (queue {q.GetStatsInputCount()})");
        Check(mv.IsAvailable, "carrier free again");
    }

    static void TestStorageOutboundWaits()
    {
        // three instant retrievals into a 1-place queue in front of a slow server: the third is refused at first and
        // must wait in the storage (counted there), not vanish
        var clock = new SimClock(1);
        var storage = new StorageSystem("Store", clock);
        storage.AddLane(new StorageLane("lane1", new Coordinate(0, 0, 0), 5, AccessDiscipline.Direct, SkuConstraint.Mixed));
        var q = new ItemsQueue(1, "Q", clock);
        var m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(10) }, "M", clock);
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { storage });
        GeneralLink.CreateLink(storage, new List<Element> { q });
        GeneralLink.CreateLink(q, new List<Element> { m });
        GeneralLink.CreateLink(m, new List<Element> { sink });
        foreach (var e in new Element[] { storage, q, m, sink }) e.Start();
        for (int i = 0; i < 3; i++) storage.Receive(new Item(0));
        int retrieved = 0;
        for (int i = 0; i < 3; i++) storage.RequestRetrieval(null, it => retrieved++);
        Check(storage.GetQueueLength() == 1 && retrieved == 2, $"the refused one waits in the storage ({storage.GetQueueLength()} there, {retrieved} out)");
        clock.AdvanceClock(50);
        Check(sink.GetStatsInputCount() == 3 && retrieved == 3 && storage.GetQueueLength() == 0, $"all three out in the end ({sink.GetStatsInputCount()})");

        // carried out by a transport team: no failed hand-over
        clock = new SimClock(1);
        var d = new ResourceDispatcher("Movers", clock);
        var mv = new TaskExecutor("Mv", clock) { LoadTime = 1 };
        d.Add(mv);
        storage = new StorageSystem("Store", clock);
        storage.AddLane(new StorageLane("lane1", new Coordinate(0, 0, 0), 5, AccessDiscipline.Direct, SkuConstraint.Mixed));
        sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { storage });
        var link = ResourceTransportLink.Create(storage, new List<Element> { sink }, new StationResources(d));
        link.TravelTime = (it, dest) => 3;
        foreach (var e in new Element[] { storage, sink, mv }) e.Start();
        d.Start(); link.Reset();
        storage.Receive(new Item(0)); storage.Receive(new Item(0));
        storage.RequestRetrieval(null, null); storage.RequestRetrieval(null, null);
        clock.AdvanceClock(50);
        Check(sink.GetStatsInputCount() == 2 && storage.GetQueueLength() == 0, $"both carried out ({sink.GetStatsInputCount()})");
    }

    static void TestStorageConcurrentRetrievals()
    {
        var clock = new SimClock(1);
        var net = Line("D", "L1", "L2");
        var d = new ResourceDispatcher("Cranes", clock);
        var crane = new TaskExecutor("Crane", clock, ResourceMode.Spatial) { HomeNode = "D", TravelModel = new NetworkTravel(net, 1), LoadTime = 1, UnloadTime = 1 };
        d.Add(crane);
        var storage = new StorageSystem("Store", clock) { Location = "D", Transport = new StationResources(d) };
        storage.AddLane(new StorageLane("lane1", new Coordinate(10, 0, 0), 3, AccessDiscipline.Direct, SkuConstraint.Mixed, "L1"));
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(storage, new List<Element> { sink });
        foreach (var e in new Element[] { storage, sink, crane }) e.Start();
        d.Start();
        var a = new Item(0); var b = new Item(0);
        storage.Receive(a); storage.Receive(b);
        var got = new List<Item>();
        // two "oldest" requests back to back: the second must not pick the item the first is already fetching
        Check(storage.RequestRetrieval(null, got.Add) && storage.RequestRetrieval(null, got.Add), "both requests accepted");
        clock.AdvanceClock(200);
        Check(got.Count == 2 && got[0] == a && got[1] == b, $"a then b, each once ({got.Count})");
        Check(storage.TotalRetrievals == 2 && sink.GetStatsInputCount() == 2, "two retrievals, two items out");

        // LIFO lane1 [y (face), x]; retrieving x relocates y to lane2 (1 place). While the crane works, a put-away
        // must neither take lane2's place (reserved for y) nor go into lane1 in front of x.
        clock = new SimClock(1);
        d = new ResourceDispatcher("Cranes", clock);
        crane = new TaskExecutor("Crane", clock, ResourceMode.Spatial) { HomeNode = "D", TravelModel = new NetworkTravel(net, 1), LoadTime = 1, UnloadTime = 1 };
        d.Add(crane);
        storage = new StorageSystem("Store", clock) { Location = "D", Transport = new StationResources(d) };
        storage.AddLane(new StorageLane("lane1", new Coordinate(10, 0, 0), 2, AccessDiscipline.Lifo, SkuConstraint.Mixed, "L1"));
        storage.AddLane(new StorageLane("lane2", new Coordinate(20, 0, 0), 1, AccessDiscipline.Lifo, SkuConstraint.Mixed, "L2"));
        sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(storage, new List<Element> { sink });
        foreach (var e in new Element[] { storage, sink, crane }) e.Start();
        d.Start();
        var x = new Item(0); var y = new Item(0);
        storage.Receive(x); storage.Receive(y);
        Item out1 = null;
        Check(storage.RequestRetrieval(ListQuery.All.Where(e => e.Item == x), it => out1 = it), "retrieval of x accepted");
        bool accepted = true;
        Schedule(clock, 15, () => accepted = storage.Receive(new Item(0)));   // y is on its way to lane2
        clock.AdvanceClock(200);
        Check(!accepted, "the put-away during the retrieval is refused (lane2 reserved, lane1 being emptied)");
        Check(out1 == x && storage.TotalRelocations == 1, "x retrieved after y was relocated");
        Check(storage.GetLane("lane2").Count == 1 && storage.GetLane("lane2").Contents[0].Item == y, "y in lane2");
    }

    static TransportNetwork Points(params (string id, double x, double y)[] pts)
    {
        var net = new TransportNetwork();
        foreach (var p in pts) net.AddNode(p.id, p.x, p.y, 0);
        return net;
    }

    static void TestAxisTravel()
    {
        Near(AxisTravel.AxisTime(10, 2, 1), 7, "trapezoid: 10/2 + 2/1");
        Near(AxisTravel.AxisTime(2, 2, 1), 2 * Math.Sqrt(2), "triangle: never reaches top speed");
        Near(AxisTravel.AxisTime(-6, 3, 0), 2, "no acceleration: d / v, either direction");
        Check(double.IsPositiveInfinity(AxisTravel.AxisTime(1, 0, 0)), "an axis without speed cannot move");
        var pts = Points(("A", 0, 0), ("B", 6, 2));
        var together = new AxisTravel(pts, new[] { 2.0, 1.0, 0.0 });
        var oneByOne = new AxisTravel(pts, new[] { 2.0, 1.0, 0.0 }, sequential: true);
        Near(together.Distance("A", "B"), 3, "together: the slowest axis (x 3, y 2)");
        Near(oneByOne.Distance("A", "B"), 5, "one after the other: 3 + 2");
        var clock = new SimClock(1);
        var crane = new TaskExecutor("Crane", clock, ResourceMode.Spatial) { HomeNode = "A", TravelModel = together };
        crane.Start();
        crane.Execute(new TaskSequence(TaskType.Transport).Travel("B"));
        Near(crane.CurrentLeg.EndTime, 3, "planned 3");
        Near(crane.CurrentLeg.Distance, Math.Sqrt(40), "leg distance = straight line");
    }

    static void TestAsrs()
    {
        var clock = new SimClock(1);
        var pts = Points(("IO", 0, 0), ("R1", 0, 2), ("R2", 6, 0));
        var d = new ResourceDispatcher("Cranes", clock);
        var crane = new TaskExecutor("SRM", clock, ResourceMode.Spatial) { HomeNode = "IO", TravelModel = new AxisTravel(pts, new[] { 2.0, 1.0, 0.0 }), LoadTime = 1, UnloadTime = 1 };
        d.Add(crane);
        var storage = new StorageSystem("ASRS", clock) { Location = "IO", Transport = new StationResources(d) };
        storage.AddLane(new StorageLane("lane1", new Coordinate(0, 2, 0), 2, AccessDiscipline.Lifo, SkuConstraint.Mixed, "R1"));
        storage.AddLane(new StorageLane("lane2", new Coordinate(6, 0, 0), 2, AccessDiscipline.Lifo, SkuConstraint.Mixed, "R2"));
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(storage, new List<Element> { sink });
        foreach (var e in new Element[] { storage, sink, crane }) e.Start();
        d.Start();
        var first = new Item(0); var second = new Item(0);
        storage.Receive(first); storage.Receive(second);
        double at = double.NaN;
        storage.RequestRetrieval(ListQuery.All.Where(e => e.Item == first), i => at = clock.GetSimulationTime());
        clock.AdvanceClock(100);
        // blocker: IO → R1 2, load 3, R1 → R2 max(3, 2) = 3 → 6, unload 7; target: R2 → R1 10, load 11, R1 → IO 13, unload 14.
        Near(at, 14, "relocation + retrieval with axis times");
        Check(crane.Location == "IO" && storage.TotalRelocations == 1, "crane back at the I/O point");
    }

    static void TestGantryStacker()
    {
        var clock = new SimClock(1);
        var pts = Points(("K", 0, 0), ("P0", 4, 0), ("P1", 8, 0));
        var d = new ResourceDispatcher("Gantry", clock);
        var crane = new TaskExecutor("Gantry", clock, ResourceMode.Spatial) { HomeNode = "K", TravelModel = new AxisTravel(pts, new[] { 2.0, 2.0, 1.0 }), LoadTime = 1, UnloadTime = 1 };
        d.Add(crane);
        var st = new Stacker("St", clock, 2, 10) { PickLocation = "K", Resources = new StationResources(d) };
        st.Positions[0].Location = "P0"; st.Positions[1].Location = "P1";
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { st });
        GeneralLink.CreateLink(st, new List<Element> { sink });
        foreach (var e in new Element[] { st, sink, crane }) e.Start();
        d.Start();
        st.Receive(new Item(0));
        // crane at K: load 0 → 1 (pick point free), K → P0 2 → 3, unload → 4: placed.
        Schedule(clock, 2, () => Check(st.PickItem == null && st.CycleInProgress && crane.CurrentState == ResourceStates.TRAVELLING_LOADED, "pick point free, carrying"));
        Schedule(clock, 3.5, () => Check(st.ItemsStacked == 0, "not placed yet"));
        clock.AdvanceClock(4.5);
        Check(st.ItemsStacked == 1 && crane.Location == "P0" && crane.IsAvailable, "placed at 4 at P0; crane free");
        st.Receive(new Item(0));
        clock.AdvanceClock(20);
        // second: P0 → K 2, load 1, K → P0 2, unload 1 → 4.5 + 6 = 10.5
        Check(st.ItemsStacked == 2, "second placed");
        Near(crane.GetTimeInState(ResourceStates.TRAVELLING_EMPTY), 2, "travelled back empty once");
        Near(crane.GetTimeInState(ResourceStates.TRAVELLING_LOADED), 4, "two loaded moves of 2");
    }

    static void TestWalkGrid()
    {
        var open = WalkGrid.FromRows(0, 0, 1, new[] { ".....", ".....", "....." });
        Check(string.Join("/", open.ToRows()) == "...../...../.....", "rows round trip");
        var straight = open.FindPath(0, 0, 4, 0);
        Check(straight.Count == 2, $"open grid: smoothed to a straight segment ({straight.Count} points)");

        //   z=3  .....
        //   z=2  ..#..
        //   z=1  ..#..
        //   z=0  ..#..        a wall at x = 2, open at the top
        var wall = WalkGrid.FromRows(0, 0, 1, new[] { "..#..", "..#..", "..#..", "....." });
        Check(!wall.LineOfSight(0, 0, 4, 0), "the wall blocks the straight line");
        var around = wall.FindPath(0, 0, 4, 0);
        Check(around != null && around.Exists(c => c.z == 3), "goes around through the top row");
        for (int i = 1; i < around.Count; i++)
            Check(wall.LineOfSight(around[i - 1].x, around[i - 1].z, around[i].x, around[i].z), $"segment {i} is free");

        var closed = WalkGrid.FromRows(0, 0, 1, new[] { "..#..", "..#..", "..#.." });
        Check(closed.FindPath(0, 0, 4, 0) == null, "a full wall: unreachable");

        var corner = WalkGrid.FromRows(0, 0, 1, new[] { ".#", "#." });
        Check(corner.FindPath(0, 0, 1, 1) == null, "no squeezing diagonally between two blocked corners");

        Check(wall.NearestWalkable(2, 1, out int fx, out int fz) && wall.IsWalkable(fx, fz) && Math.Abs(fx - 2) == 1, "a blocked cell maps to a free neighbour");
    }

    static void TestGridTravel()
    {
        var grid = WalkGrid.FromRows(0, 0, 1, new[] { "..#..", "..#..", "..#..", "....." });
        var pts = new TransportNetwork();
        pts.AddNode("A", 0.5, 0, 0.5); pts.AddNode("B", 4.5, 0, 0.5); pts.AddNode("C", 4.5, 0, 3.5);
        var gt = new GridTravel(grid, pts, 2);
        double dAB = gt.Distance("A", "B");
        Check(dAB > 4 + 1e-9 && dAB < 4 + 2 * 3 + 1e-9, $"around the wall: longer than straight, shorter than the box ({dAB:F2})");
        Near(gt.Distance("A", "B"), dAB, "cached, same value");
        Near(gt.Distance("B", "C"), 3, "straight when free");

        var clock = new SimClock(1);
        var walker = new TaskExecutor("W", clock, ResourceMode.Spatial) { HomeNode = "A", TravelModel = gt };
        walker.Start();
        walker.Execute(new TaskSequence(TaskType.Transport).Travel("B"));
        var leg = walker.CurrentLeg;
        Check(leg.Path != null && leg.Path.Count >= 3, "the leg carries the path points");
        Near(leg.Path[0].X, 0.5, "starts at A"); Near(leg.Path[leg.Path.Count - 1].X, 4.5, "ends at B");
        Near(leg.EndTime, dAB / 2, "time = length / speed");
        clock.AdvanceClock(100);
        Check(walker.Location == "B", "arrived");

        var shut = new GridTravel(WalkGrid.FromRows(0, 0, 1, new[] { "..#..", "..#.." }), pts, 1);
        Check(double.IsPositiveInfinity(shut.Distance("A", "B")), "no path: infinite distance");
        bool threw = false;
        try { shut.Plan(walker, "A", "B"); } catch (InvalidOperationException ex) { threw = ex.Message.Contains("walk grid"); }
        Check(threw, "planning it fails loudly");
    }

    static void TestMoveLogExternal()
    {
        var clock = new SimClock(1);
        var m = new DistanceMatrix().Set("H", "A", 4).Set("A", "B", 6);
        var walker = new TaskExecutor("W", clock, ResourceMode.Spatial) { HomeNode = "H", TravelModel = new ExternalTravel(new MatrixTravel(m, 1)), RecordMoves = true };
        walker.Start();
        var item = new Item(0);
        walker.Execute(new TaskSequence(TaskType.Transport).Travel("A").Load(item, () => 0).Travel("B").Unload(item, () => 0));
        Check(walker.CurrentLeg != null && walker.CurrentLeg.IsOpenEnded && walker.CurrentLeg.Distance == 4, "open-ended move, estimate distance 4");
        Near(walker.DistanceTo("A"), 4, "distance from the estimate");
        Schedule(clock, 5, () => walker.Arrive());                                   // physics says: arrived at 5
        StopToken stop = null;
        Schedule(clock, 7, () => stop = walker.Stop(new StopRequest(ElementState.STOPPED, StopMode.Immediate)));
        Schedule(clock, 9, () => walker.Resume(stop));                               // 2 stopped mid-way
        Schedule(clock, 14, () => walker.Arrive());
        clock.AdvanceClock(20);
        Check(walker.Moves.Count == 2 && walker.Location == "B", $"two moves logged ({walker.Moves.Count})");
        Check(!walker.Moves[0].Loaded && walker.Moves[1].Loaded, "first empty, second loaded");
        Near(walker.Moves[0].Duration, 5, "first move 0 → 5");
        Near(walker.Moves[1].Duration, 7, "second 5 → 14 minus the 2 stopped");
        Near(walker.Moves[1].Distance, 6, "with its estimate distance");
    }

    static void TestCalibration()
    {
        // time = 2 + 0.5 d + 1.5 loaded, exact
        var moves = new List<MoveRecord>();
        double[] ds = { 4, 6, 10, 4, 6, 10, 8 };
        for (int i = 0; i < ds.Length; i++)
        {
            bool loaded = i % 2 == 0;
            moves.Add(new MoveRecord { From = "P" + i % 3, To = "Q", Loaded = loaded, Start = 0, End = 2 + 0.5 * ds[i] + (loaded ? 1.5 : 0), Distance = ds[i] });
        }
        var fit = TravelCalibrationFit.Fit(moves);
        Near(fit.A, 2, "A", 1e-9); Near(fit.B, 0.5, "B", 1e-9); Near(fit.C, 1.5, "C", 1e-9);
        Near(fit.R2, 1, "R² = 1 for exact data", 1e-9);
        Check(fit.Samples == 7 && fit.Pairs.Count > 0, "samples and pairs");

        // pair mean vs general model
        var m = new DistanceMatrix().Set("A", "B", 10).Set("A", "C", 20);
        var cal = new CalibratedTravel(new MatrixTravel(m, 1), 1, 0.5, 0, minSamples: 3)
            .AddPair(new CalibratedTravel.PairStat { From = "A", To = "B", Loaded = false, Mean = 9, Sd = 1, N = 5 })
            .AddPair(new CalibratedTravel.PairStat { From = "A", To = "C", Loaded = false, Mean = 50, Sd = 1, N = 2 });
        Near(cal.Time("A", "B", false, out bool p1), 9, "seen 5 times: its mean"); Check(p1, "from the pair");
        Near(cal.Time("A", "C", false, out bool p2), 11, "seen twice only: general 1 + 0.5 · 20"); Check(!p2, "from the general model");
        Near(cal.Time("A", "B", true, out _), 6, "loaded never seen: general 1 + 0.5 · 10");

        // from config, as exported from a scene
        var cfg = new SimuLean.Serialization.SimulationConfig { Seed = 1, MaxSimulationTime = 10 };
        var mx = new SimuLean.Serialization.TravelMatrixConfig { Id = "floor" };
        mx.Entries.Add("H|S|8");
        cfg.TravelMatrices.Add(mx);
        var team = new SimuLean.Serialization.ResourceTeamConfig
        {
            Id = "ops", Name = "ops", Travel = "Calibrated", CalibrationBase = "Matrix", Matrix = "floor", Speed = 1, HomeNode = "H",
            CalibrationFit = "1|0.5|0",
        };
        team.CalibrationPairs.Add(new CalibratedTravel.PairStat { From = "H", To = "S", Loaded = false, Mean = 3, Sd = 0.2, N = 4 }.Encode());
        team.Members.Add(new SimuLean.Serialization.ResourceMemberConfig { Name = "Op" });
        cfg.ResourceTeams.Add(team);
        var factory = new SimuLean.Headless.HeadlessModelFactory(new SimClock(1));
        factory.BuildModel(cfg);
        var op = factory.Executors.Single();
        Check(op.TravelModel is CalibratedTravel, "calibrated travel built from config");
        Near(op.TravelModel.Plan(op, "H", "S").Duration, 3, "the pair mean");
        Near(op.TravelModel.Plan(op, "S", "H").Duration, 5, "the reverse pair (matrix symmetric) by the general model: 1 + 0.5 · 8");
    }

    static (TaskExecutor v, double arrived) Vehicle(SimClock clock, TrafficNetworkTravel model, string name, string home, string to, double at, List<(string, double)> log)
    {
        var v = new TaskExecutor(name, clock, ResourceMode.Spatial) { HomeNode = home, TravelModel = model };
        v.Start();
        Schedule(clock, at, () => v.Execute(new TaskSequence(TaskType.Transport).Travel(to).Callback(() => log.Add((name, clock.GetSimulationTime())))));
        return (v, double.NaN);
    }

    // Items that walk by themselves (WalkLink): M at A sends to Q (capacity 1) at B, 10 m away, at 1 m per
    // unit; Q feeds a slow server S at B (same place: at once).
    static void TestItemsWalk()
    {
        var clock = new SimClock(1);
        var net = Line("A", "B", "C");
        var m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1), new ConstantRandomProcess(1), new ConstantRandomProcess(1) }, "M", clock) { Location = "A" };
        var q = new ItemsQueue(1, "Q", clock) { Location = "B" };
        var s = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(30) }, "S", clock) { Location = "B" };
        var sink = new Sink("Sink", clock) { Location = "C" };
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
        var walk = WalkLink.Create(m, new[] { q }, new NetworkTravel(net, 1));
        var toS = WalkLink.Create(q, new[] { s }, new NetworkTravel(net, 1));
        var toSink = WalkLink.Create(s, new[] { sink }, new NetworkTravel(net, 1));
        foreach (var e in new Element[] { m, q, s, sink }) e.Start();
        m.Receive(new Item(0));
        Schedule(clock, 6, () =>
        {
            Check(walk.Walks.Count == 1 && m.GetStatsOutputCount() == 1 && q.GetStatsInputCount() == 0, "it left M at 1 and walks");
            var leg = walk.Walks.Count == 1 ? walk.Walks[0].Leg : null;
            Check(leg != null && leg.Route.SequenceEqual(new[] { "A", "B" }) && leg.Distance == 10, "route A → B, 10 m");
            Near(leg?.Fraction(6) ?? -1, 0.5, "half way at 6 (1 → 11)");
        });
        Schedule(clock, 11.5, () => Check(walk.Walks.Count == 0 && s.GetStatsInputCount() == 1 && toS.Walks.Count == 0,
                                          "entered Q at 11 and S at once (same place)"));
        clock.AdvanceClock(60);
        Check(sink.GetStatsInputCount() == 1, "walked on to the sink (S at B → C)");

        // Room counted on the way: three items done at 1, Q holds one, S is busy — one walks, the others wait in M.
        clock = new SimClock(1);
        m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1), new ConstantRandomProcess(1), new ConstantRandomProcess(1) }, "M", clock) { Location = "A" };
        q = new ItemsQueue(1, "Q", clock) { Location = "B" };
        s = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(100) }, "S", clock) { Location = "B" };
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
        var other = new ReleaseSource("Other", clock);
        walk = WalkLink.Create(m, new[] { q }, new NetworkTravel(net, 1));
        GeneralLink.CreateLink(q, new List<Element> { s });
        GeneralLink.CreateLink(other, new List<Element> { s });
        var end = new Sink("End", clock);
        GeneralLink.CreateLink(s, new List<Element> { end });
        foreach (var e in new Element[] { m, q, s, other, end }) e.Start();
        other.Enqueue(new Item(0));                                   // S busy 0 → 100
        for (int i = 0; i < 3; i++) m.Receive(new Item(0));
        Schedule(clock, 5, () => Check(walk.Walks.Count == 1 && m.GetQueueLength() == 2, "one walks to the one free place, two wait in M"));
        Schedule(clock, 50, () => Check(q.GetQueueLength() == 1 && m.GetQueueLength() == 2 && walk.Walks.Count == 0, "Q full, the others still in M"));
        clock.AdvanceClock(102);
        Check(walk.Walks.Count == 1, "S took one at 100: the next one walks");

        // Waiting at the door: another input fills Q while the item walks; it enters when Q has room.
        clock = new SimClock(1);
        m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock) { Location = "A" };
        q = new ItemsQueue(1, "Q", clock) { Location = "B" };
        s = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(20) }, "S", clock) { Location = "B" };
        sink = new Sink("Sink", clock);
        other = new ReleaseSource("Other", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
        walk = WalkLink.Create(m, new[] { q }, new NetworkTravel(net, 1));
        GeneralLink.CreateLink(other, new List<Element> { q });
        GeneralLink.CreateLink(q, new List<Element> { s });
        GeneralLink.CreateLink(s, new List<Element> { sink });
        foreach (var e in new Element[] { m, q, s, sink, other }) e.Start();
        other.Enqueue(new Item(0));                                   // S busy 0 → 20
        m.Receive(new Item(0));                                       // walks 1 → 11
        Schedule(clock, 5, () => other.Enqueue(new Item(0)));         // fills Q at 5
        Schedule(clock, 15, () => Check(walk.Walks.Count == 1 && walk.Walks[0].Waiting, "at Q's door at 11, waiting"));
        clock.AdvanceClock(100);
        Check(walk.Walks.Count == 0 && sink.GetStatsInputCount() == 3, $"entered when Q had room; all three through ({sink.GetStatsInputCount()})");

        // A routing rule names a full destination: the item waits (never goes to the other one); waiting at the door, it
        // sets off to the named one and waits there.
        foreach (bool atDoor in new[] { false, true })
        {
            clock = new SimClock(1);
            m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock) { Location = "A" };
            var q1 = new ItemsQueue(1, "Q1", clock) { Location = "B" };
            var q2 = new ItemsQueue(1, "Q2", clock) { Location = "B" };
            other = new ReleaseSource("Other", clock);
            GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
            GeneralLink.CreateLink(other, new List<Element> { q1 });
            m.SetOutputStrategy(new ExpressionRoutingStrategy("route"));
            walk = WalkLink.Create(m, new[] { q1, q2 }, new NetworkTravel(net, 1));
            walk.WaitAtDoor = atDoor;
            foreach (var e in new Element[] { m, q1, q2, other }) e.Start();
            other.Enqueue(new Item(0));                               // Q1 full from 0 (nothing takes from it)
            var it = new Item(0);
            it.SetLabelValue("route", 0);                             // its rule: Q1
            m.Receive(it);
            clock.AdvanceClock(30);
            Check(q2.GetQueueLength() == 0, $"{(atDoor ? "at the door" : "room")}: not sent to Q2 because Q1 is full");
            Check(atDoor ? walk.Walks.Count == 1 && walk.Walks[0].Waiting && walk.Walks[0].To == q1 : m.GetQueueLength() == 1,
                  atDoor ? "it walked to Q1 and waits at its door" : "it waits in M");
        }
    }

    // Weights 1 | 0 with the first destination full — through a walk or a carrier the item
    // must wait for its (weighted) destination, never go to the weight-0 one; every choice is forgotten once the item left.
    static void TestProbabilityRoutingOnLinks()
    {
        foreach (string kind in new[] { "walk", "carried" })
        {
            var clock = new SimClock(1);
            var net = Line("A", "B", "C");
            var m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock) { Location = "A" };
            var full = new ItemsQueue(1, "Full", clock) { Location = "B" };
            var free = new ItemsQueue(100, "Free", clock) { Location = "B" };
            var other = new ReleaseSource("Other", clock);
            GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
            GeneralLink.CreateLink(other, new List<Element> { full });
            var routing = new ProbabilityRoutingStrategy(new[] { 1.0, 0.0 });
            m.SetOutputStrategy(routing);
            var d = new ResourceDispatcher("Team", clock);
            var op = new TaskExecutor("Op", clock);
            d.Add(op);
            if (kind == "walk") WalkLink.Create(m, new Element[] { full, free }, new NetworkTravel(net, 1));
            else ResourceTransportLink.Create(m, new Element[] { full, free }, new StationResources(d)).TravelTime = (item, dest) => 1;
            foreach (var e in new Element[] { m, full, free, other, op }) e.Start();
            d.Start();
            other.Enqueue(new Item(0));                                  // "Full" holds one, nothing takes from it
            for (int i = 0; i < 3; i++) m.Receive(new Item(0));
            clock.AdvanceClock(50);
            Check(free.GetStatsInputCount() == 0, $"{kind}: nothing sent to the weight-0 destination ({free.GetStatsInputCount()})");

            // choices forgotten: weights 1 | 1, both with room — every item leaves, nothing remembered
            clock = new SimClock(1);
            m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock) { Location = "A" };
            var a = new ItemsQueue(1000, "A1", clock) { Location = "B" };
            var b = new ItemsQueue(1000, "B1", clock) { Location = "B" };
            GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
            routing = new ProbabilityRoutingStrategy(new[] { 1.0, 1.0 });
            m.SetOutputStrategy(routing);
            d = new ResourceDispatcher("Team", clock);
            for (int k = 0; k < 4; k++) d.Add(new TaskExecutor("Op" + k, clock));
            if (kind == "walk") WalkLink.Create(m, new Element[] { a, b }, new NetworkTravel(net, 1));
            else ResourceTransportLink.Create(m, new Element[] { a, b }, new StationResources(d)).TravelTime = (item, dest) => 1;
            foreach (var e in new Element[] { m, a, b }) e.Start();
            foreach (var r in d.Members) r.Start();
            d.Start();
            var feed = new ReleaseSource("Feed", clock);
            GeneralLink.CreateLink(feed, new List<Element> { m });
            feed.Start();
            for (int i = 0; i < 20; i++) feed.Enqueue(new Item(0));
            clock.AdvanceClock(500);
            Check(m.GetStatsInputCount() > 0 && a.GetStatsInputCount() + b.GetStatsInputCount() == m.GetStatsInputCount() && routing.Remembered == 0,
                  $"{kind}: every item arrived and no choice is kept ({m.GetStatsInputCount()} items, {routing.Remembered} kept)");
        }
    }

    static void TestWalkToStoppedSamePlace()
    {
        foreach (bool atDoor in new[] { false, true })
        {
            var clock = new SimClock(1);
            var net = Line("A", "B", "C");
            var m = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "M", clock) { Location = "B" };
            var q = new ItemsQueue(10, "Q", clock) { Location = "B" };       // the same place: no walk, an immediate hand-over
            GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { m });
            var walk = WalkLink.Create(m, new[] { q }, new NetworkTravel(net, 1));
            walk.WaitAtDoor = atDoor;
            foreach (var e in new Element[] { m, q }) e.Start();
            var token = q.Stop(new StopRequest { BlockInput = true });
            m.Receive(new Item(0));
            clock.AdvanceClock(10);
            Check(q.GetStatsInputCount() == 0, $"{(atDoor ? "at the door" : "room")}: the stopped queue took nothing ({q.GetStatsInputCount()})");
            q.Resume(token);
            clock.AdvanceClock(20);
            Check(q.GetStatsInputCount() == 1, $"{(atDoor ? "at the door" : "room")}: taken once the stop ended ({q.GetStatsInputCount()})");
        }
    }

    // The same walk built from a config: a network in metres, a time unit of 60 s and walkSpeed 1 m/s → 60 m take 1 unit.
    static void TestItemsWalkConfig()
    {
        SimuLean.Serialization.SimulationConfig Config(string travel)
        {
            var c = new SimuLean.Serialization.SimulationConfig { UseFixedSeed = true, Seed = 1, TimeUnitSeconds = 60 };
            c.Network = new SimuLean.Serialization.NetworkConfig();
            c.Network.Nodes.Add(SimuLean.Serialization.TransportStorageConfigCodec.FormatNode("A", 0, 0, 0));
            c.Network.Nodes.Add(SimuLean.Serialization.TransportStorageConfigCodec.FormatNode("B", 60, 0, 0));
            c.Network.Tracks.Add(SimuLean.Serialization.TransportStorageConfigCodec.FormatTrack("A", "B", 60, true, TrackType.TravelPath));
            var src = new SimuLean.Serialization.ElementConfig("src", "UnitySource", "src");
            src.Parameters["sourceMode"] = "InterArrival";
            src.Parameters["interArrivalStrategyType"] = "Constant";
            src.Parameters["constantValue"] = 10.0;
            src.Parameters["location"] = "A";
            src.Parameters["exitLabels"] = new List<string> { "left = now()" };
            if (travel != null) { src.Parameters["walkSpeed"] = 1.0; src.Parameters["walkTravel"] = travel; }
            var sink = new SimuLean.Serialization.ElementConfig("done", "UnitySink", "done");
            sink.Parameters["location"] = "B";
            sink.Parameters["tallies"] = new List<string> { "walk = now() - left" };
            c.Elements.Add(src); c.Elements.Add(sink);
            c.Connections.Add(new SimuLean.Serialization.ConnectionConfig("src", "done", "General"));
            return c;
        }
        double WalkMean(SimuLean.Serialization.SimulationConfig c)
        {
            var clock = new SimClock(1);
            var model = new SimuLean.Headless.HeadlessModelFactory(clock);
            model.BuildModel(c);
            model.StartModel();
            clock.AdvanceClock(100);
            var done = model.Find("done");
            Check(done.GetStatsInputCount() >= 9, $"items through ({done.GetStatsInputCount()})");
            return done.StatsCollector.Tallies.Get("walk").Mean;
        }
        Near(WalkMean(Config("Network")), 1.0, "60 m at 1 m/s with 60 s per unit: 1 unit on foot", 1e-9);
        Near(WalkMean(Config(null)), 0.0, "without walkSpeed the hand-over is immediate (as before)", 1e-9);
        var bad = Config("Teleport");
        bool failed = false;
        try { WalkMean(bad); } catch (ArgumentException) { failed = true; }
        Check(failed, "an unknown walk travel fails the build");
    }

    static void TestWorkPointKeptForNextJob()
    {
        // Op1 serves job 1 at w1 (t 1-3, HoldFor 2), then job 2 at w1 (t 10-12). Job 1's process ends at 11 and releases
        // its work points: w1 must stay Op1's (it is still working there), so Op2, called at 11.5, goes to w2.
        var clock = new SimClock(1);
        var m = new DistanceMatrix().Set("H", "w1", 1).Set("H", "w2", 1).Set("w1", "w2", 1).Set("H", "S", 1).Set("S", "w1", 1).Set("S", "w2", 1);
        var d = new ResourceDispatcher("Ops", clock);
        var op1 = new TaskExecutor("Op1", clock, ResourceMode.Spatial) { HomeNode = "H", TravelModel = new MatrixTravel(m, 1) };
        var op2 = new TaskExecutor("Op2", clock, ResourceMode.Spatial) { HomeNode = "H", TravelModel = new MatrixTravel(m, 1) };
        d.Add(op1); d.Add(op2);
        var s = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(10), new ConstantRandomProcess(10), new ConstantRandomProcess(10) }, "S", clock) { Location = "S" };
        s.WorkLocations.Add("w1"); s.WorkLocations.Add("w2");
        s.Resources = new StationResources(d) { HoldFor = new ConstantRandomProcess(2) };
        var sink = new Sink("Sink", clock);
        GeneralLink.CreateLink(new Station(clock, "Up"), new List<Element> { s });
        GeneralLink.CreateLink(s, new List<Element> { sink });
        foreach (var e in new Element[] { s, sink, op1, op2 }) e.Start();
        d.Start();
        var away = op2.Stop(new StopRequest(ElementState.STOPPED, StopMode.Immediate));   // Op2 joins at 11.5
        s.Receive(new Item(0));
        Schedule(clock, 10, () => s.Receive(new Item(0)));
        Schedule(clock, 11.5, () => { op2.Resume(away); s.Receive(new Item(0)); });
        clock.AdvanceClock(14);
        Check(op1.Location == "w1" && op2.Location == "w2", $"Op1 at w1, Op2 at the free point w2 (Op1 {op1.Location}, Op2 {op2.Location})");
    }

    static void TestTrafficSegmentOnRouteTypes()
    {
        // a 2-long conveyor between the same nodes is not the vehicle's road: A → B takes the 10-long road
        var clock = new SimClock(1);
        var net = Line("A", "B");
        net.AddTrack("A", "B", 2, true, TrackType.Conveyor);
        var model = new TrafficNetworkTravel(new NetworkTravel(net, 1), new TrafficControl());
        var log = new List<(string, double)>();
        Vehicle(clock, model, "V", "A", "B", 0, log);
        clock.AdvanceClock(50);
        Check(log.Count == 1 && Math.Abs(log[0].Item2 - 10) < 1e-9, $"arrives at 10 ({(log.Count > 0 ? log[0].Item2 : double.NaN)})");
    }

    static void TestTraffic()
    {
        // follow: one vehicle per track, the second waits for the first to clear A–B
        var clock = new SimClock(1);
        var net = Line("A", "B", "C");
        var traffic = new TrafficControl();
        var model = new TrafficNetworkTravel(new NetworkTravel(net, 1), traffic);
        var log = new List<(string, double)>();
        var v1 = Vehicle(clock, model, "V1", "A", "C", 0, log).v;
        var v2 = Vehicle(clock, model, "V2", "A", "C", 1, log).v;
        Schedule(clock, 5, () =>
        {
            Check(v1.CurrentLeg.SegmentIndex == 0 && Math.Abs(v1.CurrentLeg.SegmentEndTime - 10) < 1e-9, "V1 on A–B until 10");
            Check(v2.CurrentState == ResourceStates.TRAFFIC_WAIT && traffic.Occupancy("A", "B") == 1, "V2 waits at A (TRAFFIC_WAIT)");
        });
        clock.AdvanceClock(100);
        Check(log.Count == 2 && log[0].Item1 == "V1" && Math.Abs(log[0].Item2 - 20) < 1e-9, "V1 arrives at 20");
        Check(log[1].Item1 == "V2" && Math.Abs(log[1].Item2 - 30) < 1e-9, $"V2 follows and arrives at 30 ({log[1].Item2})");
        Near(v2.GetTimeInState(ResourceStates.TRAFFIC_WAIT), 9, "V2 waited 1 → 10");
        Check(traffic.Waits == 1 && traffic.DeadlocksResolved == 0, "one wait, no deadlock");
        Check(traffic.ActiveMoves == 0, $"finished moves are not kept ({traffic.ActiveMoves})");
        Near(traffic.TotalWait, 9, "total wait");
        Check(traffic.Occupancy("A", "B") == 0 && traffic.Occupancy("B", "C") == 0, "tracks free at the end");

        // head-on: A → C and C → A at once meet at B: deadlock, resolved by letting one through
        clock = new SimClock(1);
        traffic = new TrafficControl();
        model = new TrafficNetworkTravel(new NetworkTravel(Line("A", "B", "C"), 1), traffic);
        log = new List<(string, double)>();
        Vehicle(clock, model, "East", "A", "C", 0, log);
        Vehicle(clock, model, "West", "C", "A", 0, log);
        clock.AdvanceClock(100);
        Check(log.Count == 2 && log.TrueForAll(x => Math.Abs(x.Item2 - 20) < 1e-9), $"both arrive at 20 ({string.Join(", ", log)})");
        Check(traffic.DeadlocksResolved == 1, $"the deadlock was detected and resolved ({traffic.DeadlocksResolved})");

        // two vehicles per track: nobody waits
        clock = new SimClock(1);
        traffic = new TrafficControl(2);
        model = new TrafficNetworkTravel(new NetworkTravel(Line("A", "B", "C"), 1), traffic);
        log = new List<(string, double)>();
        Vehicle(clock, model, "V1", "A", "C", 0, log);
        Vehicle(clock, model, "V2", "A", "C", 1, log);
        clock.AdvanceClock(100);
        Check(log.Count == 2 && Math.Abs(log[1].Item2 - 21) < 1e-9 && traffic.Waits == 0, "capacity 2: no waiting, V2 at 21");
    }

    static void TestStationRelease()
    {
        // One operator, two single-slot stations feeding a station that is closed: with EndOfProcess the
        // operator is free as soon as the process ends, even though the station is blocked.
        var clock = new SimClock(1);
        var d = new ResourceDispatcher("Team", clock);
        var op = new TaskExecutor("Op", clock);
        d.Add(op);
        var s1 = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "S1", clock);
        var s2 = new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, "S2", clock);
        var res = new StationResources(d) { Release = ProcessRelease.EndOfProcess };
        s1.Resources = res; s2.Resources = res;
        var gate = new Station(clock, "Closed");   // never accepts: both stations block after their process
        GeneralLink.CreateLink(s1, new List<Element> { gate });
        GeneralLink.CreateLink(s2, new List<Element> { gate });
        GeneralLink.CreateLink(new Station(clock, "Up1"), new List<Element> { s1 });   // stations have an input
        GeneralLink.CreateLink(new Station(clock, "Up2"), new List<Element> { s2 });
        foreach (var e in new Element[] { s1, s2, gate, op }) e.Start();
        d.Start();
        s1.Receive(new Item(0));
        s2.Receive(new Item(0));
        clock.AdvanceClock(10);
        Check(s1.GetStateRatio(ElementState.BLOCKED) > 0 && s2.GetStateRatio(ElementState.BLOCKED) > 0, "both blocked");
        Near(op.GetTimeInState(ElementState.PROCESSING), 2, "the operator processed both jobs (0–1, 1–2)");
        Check(op.IsAvailable && op.GetTimeInState(ResourceStates.HELD) == 0, "never held: released at the end of each process");

        // Request priority from the item: with one operator busy, the higher-priority job goes first.
        clock = new SimClock(1);
        d = new ResourceDispatcher("Team", clock);
        op = new TaskExecutor("Op", clock);
        d.Add(op);
        d.RequestOrder = SimuLean.Collections.ListQuery.All.OrderBy("priority", true);
        res = new StationResources(d) { Priority = i => i.GetLabelValue("prio") ?? 0 };
        var stations = new[] { "Busy", "Low", "High" }.Select(n => new MultiServer(new IRandomSampler[] { new ConstantRandomProcess(1) }, n, clock)).ToArray();
        var sink = new Sink("Sink", clock);
        foreach (var s in stations)
        {
            s.Resources = res;
            GeneralLink.CreateLink(s, new List<Element> { sink });
            GeneralLink.CreateLink(new Station(clock, "Up" + s.GetName()), new List<Element> { s });
        }
        foreach (var e in stations.Cast<Element>().Concat(new Element[] { sink, op })) e.Start();
        d.Start();
        stations[0].Receive(new Item(0));
        var low = new Item(0); low.SetLabelValue("prio", 1); stations[1].Receive(low);
        var high = new Item(0); high.SetLabelValue("prio", 9); stations[2].Receive(high);
        clock.AdvanceClock(2.5);   // Busy 0–1, then High 1–2 (priority), Low 2–3
        Check(stations[2].GetStatsOutputCount() == 1 && stations[1].GetStatsOutputCount() == 0, "the high-priority job got the operator first");
    }

    static void TestLabelsAndWarmup()
    {
        var clock = new SimClock(1);
        var op = new TaskExecutor("Op1", clock);
        op.SetLabelValue("skill", "welding");
        op.SetLabelValue("level", "3");
        op.Start();
        Check(op.GetLabelText("skill") == "welding" && op.GetLabelValue("level") == 3 && op.GetLabelValue("skill") == null, "labels");
        double seen = double.NaN;
        var task = new TaskSequence(TaskType.Process).Delay(2).Process(() => { seen = clock.GetSimulationTime(); return 1; });
        op.Execute(task);
        clock.AdvanceClock(5);
        Near(seen, 2, "duration evaluated when its step starts");
        Element.ScheduleWarmup(clock, new Element[] { op }, 6);
        op.Execute(new TaskSequence(TaskType.Process, name: "spans the warm-up").Process(2));   // 5 → 7
        clock.AdvanceClock(10);
        Check(op.TasksDone == 1, $"tasks counted from the warm-up on ({op.TasksDone})");
        Near(op.GetTimeInState(ElementState.PROCESSING), 1, "time per state counted from the warm-up (6 → 7)");
    }
}
